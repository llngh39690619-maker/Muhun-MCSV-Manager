using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Services;

public sealed record CloudflaredBootstrapProgress(
    string Message,
    double? Percentage = null);

public sealed record CloudflaredBootstrapResult(
    string ExecutablePath,
    string Version,
    long Size,
    string Sha256);

public interface ICloudflaredBootstrapService : IDisposable
{
    Task<CloudflaredBootstrapResult> InstallLatestAsync(
        IProgress<CloudflaredBootstrapProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Installs the official Windows amd64 cloudflared release into an application-owned tools
/// directory. Release metadata and every redirect hop are allowlisted; the downloaded executable
/// is never run and cannot replace the previous verified file until its exact size and SHA-256
/// digest both match GitHub's release-asset record.
/// </summary>
public sealed class CloudflaredBootstrapService : ICloudflaredBootstrapService
{
    public const string WindowsAmd64AssetName = "cloudflared-windows-amd64.exe";
    public const long MaximumExecutableBytes = 256L * 1024 * 1024;

    internal static readonly Uri LatestReleaseApiUri = new(
        "https://api.github.com/repos/cloudflare/cloudflared/releases/latest");

    private const long MaximumMetadataBytes = 1024 * 1024;
    private const string DefaultUserAgent = "MuhunMCSVManager-cloudflared-bootstrap/1.0";
    private const string GitHubApiVersion = "2022-11-28";

    private static readonly HashSet<string> GitHubAssetHosts = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com",
    };

    private readonly string _applicationRoot;
    private readonly HttpClient _metadataClient;
    private readonly HttpClient _artifactClient;
    private readonly bool _ownsClients;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;

    public CloudflaredBootstrapService(
        string applicationRoot,
        string userAgent = DefaultUserAgent)
        : this(
            applicationRoot,
            CreateClient(),
            CreateClient(),
            userAgent,
            ownsClients: true)
    {
    }

    internal CloudflaredBootstrapService(
        string applicationRoot,
        HttpClient metadataClient,
        HttpClient artifactClient,
        string userAgent = DefaultUserAgent,
        bool ownsClients = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
        ArgumentNullException.ThrowIfNull(metadataClient);
        ArgumentNullException.ThrowIfNull(artifactClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        if (ReferenceEquals(metadataClient, artifactClient))
        {
            throw new ArgumentException(
                "GitHub metadata and executable downloads require separate HTTP clients.",
                nameof(artifactClient));
        }

        _applicationRoot = Path.GetFullPath(applicationRoot);
        _metadataClient = metadataClient;
        _artifactClient = artifactClient;
        _ownsClients = ownsClients;
        ConfigureClient(_metadataClient, userAgent, "application/vnd.github+json");
        ConfigureClient(_artifactClient, userAgent, "application/octet-stream");
    }

    public async Task<CloudflaredBootstrapResult> InstallLatestAsync(
        IProgress<CloudflaredBootstrapProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CloudflaredBootstrapProgress("正在查詢 Cloudflare 官方最新版…"));

            var metadataBytes = await FirstPartyArtifactHttp.GetBoundedBytesAsync(
                    _metadataClient,
                    LatestReleaseApiUri,
                    MaximumMetadataBytes,
                    IsExactLatestReleaseUri,
                    "cloudflared 最新版本資料",
                    cancellationToken)
                .ConfigureAwait(false);
            var release = ParseRelease(metadataBytes);
            var asset = SelectAndValidateAsset(release);

            var (_, stagingDirectory, destination) = PrepareManagedDirectories();
            var verifiedDownload = SafePath.CombineUnderRoot(
                stagingDirectory,
                $"cloudflared.{Guid.NewGuid():N}.verified");
            try
            {
                progress?.Report(new CloudflaredBootstrapProgress(
                    $"正在下載並驗證 cloudflared {release.TagName}…",
                    0));
                var forwardingProgress = new InlineProgress<double>(value =>
                    progress?.Report(new CloudflaredBootstrapProgress(
                        $"正在下載並驗證 cloudflared… {value:P0}",
                        Math.Clamp(value * 100, 0, 100))));
                await FirstPartyArtifactHttp.DownloadVerifiedSha256Async(
                        _artifactClient,
                        asset.DownloadUri,
                        verifiedDownload,
                        asset.Sha256,
                        asset.Size,
                        uri => UriEquals(uri, asset.DownloadUri) || IsGitHubAssetHostUri(uri),
                        (from, to) =>
                            (UriEquals(from, asset.DownloadUri) || IsGitHubAssetHostUri(from))
                            && IsGitHubAssetHostUri(to),
                        WindowsAmd64AssetName,
                        "cloudflared Windows amd64",
                        forwardingProgress,
                        cancellationToken)
                    .ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(destination))
                {
                    throw new IOException(
                        $"cloudflared 安裝目的路徑被資料夾占用：'{destination}'。");
                }

                if (File.Exists(destination))
                {
                    SafePath.EnsureNoReparsePointsUnderRoot(_applicationRoot, destination);
                }

                // Same-volume overwrite: the old executable remains intact until the completely
                // verified staging file is atomically renamed over it.
                File.Move(verifiedDownload, destination, overwrite: true);
                SafePath.EnsureNoReparsePointsUnderRoot(_applicationRoot, destination);
                progress?.Report(new CloudflaredBootstrapProgress(
                    $"cloudflared {release.TagName} 已安全安裝並通過 SHA-256 驗證。",
                    100));
                return new CloudflaredBootstrapResult(
                    destination,
                    release.TagName,
                    asset.Size,
                    asset.Sha256);
            }
            finally
            {
                TryDelete(verifiedDownload);
                CleanupOperationPartials(stagingDirectory, verifiedDownload);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClients)
        {
            _metadataClient.Dispose();
            _artifactClient.Dispose();
        }

        _operationGate.Dispose();
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip
                                     | DecompressionMethods.Deflate
                                     | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    private static void ConfigureClient(HttpClient client, string userAgent, string accept)
    {
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        client.DefaultRequestHeaders.Remove("X-GitHub-Api-Version");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", GitHubApiVersion);
    }

    private static GitHubRelease ParseRelease(byte[] metadata)
    {
        try
        {
            var release = JsonSerializer.Deserialize<GitHubRelease>(metadata)
                ?? throw new InvalidDataException("GitHub 最新版本回應是空的。");
            var tag = release.TagName?.Trim();
            if (string.IsNullOrWhiteSpace(tag)
                || tag.Length > 100
                || tag.Any(char.IsControl))
            {
                throw new InvalidDataException("GitHub 最新版本缺少有效的 tag_name。");
            }

            return release with { TagName = tag };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("GitHub 最新版本回應不是有效 JSON。", exception);
        }
    }

    private static ValidatedAsset SelectAndValidateAsset(GitHubRelease release)
    {
        var matches = (release.Assets ?? [])
            .Where(asset => string.Equals(
                asset.Name,
                WindowsAmd64AssetName,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                "GitHub latest release 必須且只能包含一個 cloudflared Windows amd64 執行檔。");
        }

        var asset = matches[0];
        if (!string.Equals(asset.State, "uploaded", StringComparison.Ordinal))
        {
            throw new InvalidDataException("cloudflared release asset 尚未完成上傳。");
        }

        if (asset.Id <= 0 || asset.Size is < 1 or > MaximumExecutableBytes)
        {
            throw new InvalidDataException("cloudflared release asset 大小或識別碼不在安全範圍內。");
        }

        var digest = asset.Digest?.Trim();
        const string prefix = "sha256:";
        if (digest is null
            || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || digest.Length != prefix.Length + 64)
        {
            throw new InvalidDataException(
                "GitHub release asset 未提供必要的 sha256 digest；已拒絕下載。");
        }

        var sha256 = digest[prefix.Length..];
        if (sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("GitHub release asset 的 sha256 digest 格式無效。");
        }

        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var apiUri)
            || !IsExactAssetApiUri(apiUri, asset.Id))
        {
            throw new InvalidDataException(
                "cloudflared release asset API URL 不是官方 cloudflare/cloudflared 來源。");
        }

        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var browserUri)
            || !IsExactBrowserDownloadUri(browserUri, release.TagName))
        {
            throw new InvalidDataException(
                "cloudflared release asset 的瀏覽器下載 URL 不是官方 GitHub 來源。");
        }

        return new ValidatedAsset(browserUri, asset.Size, sha256.ToLowerInvariant());
    }

    private (string ManagedRoot, string StagingDirectory, string Destination)
        PrepareManagedDirectories()
    {
        if (!Directory.Exists(_applicationRoot))
        {
            throw new DirectoryNotFoundException(
                $"MCSV 應用程式資料目錄不存在：'{_applicationRoot}'。");
        }

        SafePath.EnsureNoReparsePointsUnderRoot(_applicationRoot, _applicationRoot);
        var toolsRoot = EnsureOwnedDirectory(_applicationRoot, "tools");
        var managedRoot = EnsureOwnedDirectory(toolsRoot, "cloudflared");
        var staging = EnsureOwnedDirectory(managedRoot, ".staging");
        var destination = SafePath.CombineUnderRoot(managedRoot, "cloudflared.exe");
        return (managedRoot, staging, destination);

        string EnsureOwnedDirectory(string parent, string name)
        {
            var path = SafePath.CombineUnderRoot(parent, name);
            if (File.Exists(path) && !Directory.Exists(path))
            {
                throw new IOException($"MCSV 工具資料夾路徑被檔案占用：'{path}'。");
            }

            Directory.CreateDirectory(path);
            SafePath.EnsureNoReparsePointsUnderRoot(_applicationRoot, path);
            return path;
        }
    }

    private static bool IsExactLatestReleaseUri(Uri uri)
        => UriEquals(uri, LatestReleaseApiUri);

    private static bool IsExactAssetApiUri(Uri uri, long assetId)
        => IsSecureHost(uri, "api.github.com")
           && string.Equals(
               uri.AbsolutePath,
               $"/repos/cloudflare/cloudflared/releases/assets/{assetId}",
               StringComparison.Ordinal)
           && string.IsNullOrEmpty(uri.Query)
           && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsExactBrowserDownloadUri(Uri uri, string tagName)
        => IsSecureHost(uri, "github.com")
           && string.Equals(
               uri.AbsolutePath,
               $"/cloudflare/cloudflared/releases/download/{Uri.EscapeDataString(tagName)}/{WindowsAmd64AssetName}",
               StringComparison.Ordinal)
           && string.IsNullOrEmpty(uri.Query)
           && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsGitHubAssetHostUri(Uri uri)
        => IsSecureUri(uri) && GitHubAssetHosts.Contains(uri.IdnHost);

    private static bool IsSecureHost(Uri uri, string host)
        => IsSecureUri(uri)
           && string.Equals(uri.IdnHost, host, StringComparison.OrdinalIgnoreCase);

    private static bool IsSecureUri(Uri uri)
        => uri.IsAbsoluteUri
           && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
           && string.IsNullOrEmpty(uri.UserInfo)
           && (uri.IsDefaultPort || uri.Port == 443);

    private static bool UriEquals(Uri left, Uri right)
        => left.AbsoluteUri.Equals(right.AbsoluteUri, StringComparison.Ordinal);

    private static void CleanupOperationPartials(string stagingDirectory, string verifiedDownload)
    {
        try
        {
            var prefix = Path.GetFileName(verifiedDownload) + ".";
            foreach (var path in Directory.EnumerateFiles(
                         stagingDirectory,
                         prefix + "*.partial",
                         SearchOption.TopDirectoryOnly))
            {
                TryDelete(path);
            }
        }
        catch
        {
            // Best effort only; preserve the original operation result.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup must never mask verification or cancellation failures.
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] GitHubAsset[]? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);

    private sealed record ValidatedAsset(Uri DownloadUri, long Size, string Sha256);
}
