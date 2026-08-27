using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Providers;

public interface IFtbExecutableSignatureVerifier
{
    Task VerifyAsync(string executablePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicitly fails closed until a platform Authenticode verifier is supplied by the composition
/// root. Tests and other hosts can inject their own verifier without weakening the downloader.
/// </summary>
public sealed class FtbFailClosedExecutableSignatureVerifier : IFtbExecutableSignatureVerifier
{
    public Task VerifyAsync(string executablePath, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "尚未設定 FTB Installer 的 Authenticode 驗證器，基於安全理由拒絕執行下載檔。");
}

/// <summary>
/// Downloads the latest official Windows x64 installer and its checksum from the FTBTeam GitHub
/// release. Release metadata, both assets, declared sizes, SHA-256 digests and final redirect hosts
/// are all checked before the executable is promoted to the caller's destination.
/// </summary>
public sealed partial class FtbInstallerDownloader
{
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/FTBTeam/FTB-Server-Installer/releases/latest");
    private const string InstallerAssetName = "ftb-server-windows-amd64.exe";
    private const string ChecksumAssetName = InstallerAssetName + ".sha256";
    private const long MaximumReleaseMetadataBytes = 2L * 1024 * 1024;
    private const long MaximumInstallerBytes = 128L * 1024 * 1024;
    private const long MaximumChecksumBytes = 4L * 1024;

    private readonly HttpClient _httpClient;
    private readonly IFtbExecutableSignatureVerifier _signatureVerifier;
    private readonly string _userAgent;

    public FtbInstallerDownloader(
        HttpClient httpClient,
        string userAgent,
        IFtbExecutableSignatureVerifier signatureVerifier)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        ArgumentNullException.ThrowIfNull(signatureVerifier);
        if (userAgent.Contains('\r') || userAgent.Contains('\n'))
        {
            throw new ArgumentException("User-Agent 不得包含換行字元。", nameof(userAgent));
        }

        _httpClient = httpClient;
        _userAgent = userAgent;
        _signatureVerifier = signatureVerifier;
    }

    public async Task<FtbInstallerArtifact> DownloadLatestWindowsX64Async(
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("FTB Installer 目的路徑沒有父目錄。");
        Directory.CreateDirectory(parent);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"FTB Installer 目的路徑已存在，為避免覆寫已取消：{destination}");
        }

        var partialPath = destination + $".{Guid.NewGuid():N}.partial";
        try
        {
            var release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            var checksumBytes = await DownloadSmallAssetAsync(
                    release.Checksum,
                    MaximumChecksumBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            VerifyAssetDigest(release.Checksum, checksumBytes);

            var expectedHash = ParseChecksum(checksumBytes);
            var releaseHash = ParseSha256Digest(release.Installer.Digest, InstallerAssetName);
            if (!expectedHash.Equals(releaseHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("FTB Installer 的 release digest 與 .sha256 不一致。");
            }

            await DownloadInstallerAsync(
                    release.Installer,
                    expectedHash,
                    partialPath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            await _signatureVerifier.VerifyAsync(partialPath, cancellationToken).ConfigureAwait(false);

            File.Move(partialPath, destination, overwrite: false);
            return new FtbInstallerArtifact(
                release.Tag,
                destination,
                release.Installer.Size,
                expectedHash.ToLowerInvariant());
        }
        catch
        {
            TryDeleteFile(partialPath);
            throw;
        }
    }

    private async Task<ReleaseInfo> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
                LatestReleaseUri,
                "application/vnd.github+json",
                cancellationToken)
            .ConfigureAwait(false);
        EnsureFinalUri(response, uri => uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase));
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedBytesAsync(
                response.Content,
                MaximumReleaseMetadataBytes,
                cancellationToken)
            .ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("GitHub latest release 回傳了無效 JSON。", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (ReadBoolean(root, "draft") || ReadBoolean(root, "prerelease"))
            {
                throw new InvalidDataException("GitHub latest release 不得是 draft 或 prerelease。");
            }

            var tag = ReadRequiredString(root, "tag_name", "GitHub latest release");
            if (!root.TryGetProperty("assets", out var assetsElement)
                || assetsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("GitHub latest release 缺少 assets 陣列。");
            }

            ReleaseAsset? installer = null;
            ReleaseAsset? checksum = null;
            foreach (var element in assetsElement.EnumerateArray())
            {
                var name = ReadRequiredString(element, "name", "GitHub release asset");
                if (name.Equals(InstallerAssetName, StringComparison.Ordinal))
                {
                    if (installer is not null)
                    {
                        throw new InvalidDataException("GitHub release 含有重複的 FTB Installer asset。");
                    }

                    installer = ParseAsset(element, MaximumInstallerBytes);
                }
                else if (name.Equals(ChecksumAssetName, StringComparison.Ordinal))
                {
                    if (checksum is not null)
                    {
                        throw new InvalidDataException("GitHub release 含有重複的 FTB checksum asset。");
                    }

                    checksum = ParseAsset(element, MaximumChecksumBytes);
                }
            }

            return new ReleaseInfo(
                tag,
                installer ?? throw new InvalidDataException("GitHub release 缺少 Windows x64 FTB Installer。"),
                checksum ?? throw new InvalidDataException("GitHub release 缺少 FTB Installer .sha256。"));
        }
    }

    private async Task<byte[]> DownloadSmallAssetAsync(
        ReleaseAsset asset,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(asset.DownloadUri, "application/octet-stream", cancellationToken)
            .ConfigureAwait(false);
        EnsureOfficialAssetResponse(response);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureDeclaredSize(response.Content.Headers, asset.Size);
        var bytes = await ReadBoundedBytesAsync(response.Content, maximumBytes, cancellationToken)
            .ConfigureAwait(false);
        if (bytes.LongLength != asset.Size)
        {
            throw new InvalidDataException(
                $"FTB checksum 大小不符，預期 {asset.Size} bytes，實際 {bytes.LongLength} bytes。");
        }

        return bytes;
    }

    private async Task DownloadInstallerAsync(
        ReleaseAsset asset,
        string expectedHash,
        string partialPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(asset.DownloadUri, "application/octet-stream", cancellationToken)
            .ConfigureAwait(false);
        EnsureOfficialAssetResponse(response);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureDeclaredSize(response.Content.Headers, asset.Size);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            total += count;
            if (total > asset.Size)
            {
                throw new InvalidDataException("FTB Installer 實際大小超過 release metadata 宣告值。");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            progress?.Report(Math.Clamp((double)total / asset.Size, 0d, 1d));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        if (total != asset.Size)
        {
            throw new InvalidDataException(
                $"FTB Installer 大小不符，預期 {asset.Size} bytes，實際 {total} bytes。");
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        var expectedBytes = Convert.FromHexString(expectedHash);
        var actualBytes = Convert.FromHexString(actualHash);
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            throw new InvalidDataException("FTB Installer SHA-256 驗證失敗。");
        }

        progress?.Report(1d);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri source,
        string accept,
        CancellationToken cancellationToken)
    {
        EnsureOfficialSourceUri(source);
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ReleaseAsset ParseAsset(JsonElement element, long maximumSize)
    {
        var name = ReadRequiredString(element, "name", "GitHub release asset");
        if (!element.TryGetProperty("size", out var sizeElement)
            || !sizeElement.TryGetInt64(out var size)
            || size < 1
            || size > maximumSize)
        {
            throw new InvalidDataException($"GitHub release asset '{name}' 大小無效。");
        }

        var urlText = ReadRequiredString(element, "browser_download_url", "GitHub release asset");
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException($"GitHub release asset '{name}' URL 無效。");
        }

        EnsureOfficialSourceUri(uri);
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith(
                "/FTBTeam/FTB-Server-Installer/releases/download/",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"GitHub release asset '{name}' 不屬於官方 FTBTeam repo。");
        }

        return new ReleaseAsset(
            name,
            size,
            uri,
            ReadRequiredString(element, "digest", "GitHub release asset"));
    }

    private static void VerifyAssetDigest(ReleaseAsset asset, byte[] bytes)
    {
        var expected = ParseSha256Digest(asset.Digest, asset.Name);
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"FTB release asset '{asset.Name}' digest 驗證失敗。");
        }
    }

    private static string ParseChecksum(byte[] bytes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes)
                .Trim();
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("FTB .sha256 不是有效 UTF-8。", exception);
        }

        if (!Sha256Regex().IsMatch(text))
        {
            throw new InvalidDataException("FTB .sha256 必須只包含 64 位十六進位 SHA-256。");
        }

        return text;
    }

    private static string ParseSha256Digest(string digest, string assetName)
    {
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"GitHub release asset '{assetName}' 缺少 SHA-256 digest。");
        }

        var value = digest[prefix.Length..];
        if (!Sha256Regex().IsMatch(value))
        {
            throw new InvalidDataException($"GitHub release asset '{assetName}' SHA-256 digest 無效。");
        }

        return value;
    }

    private static void EnsureOfficialSourceUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"FTB Installer 來源必須使用 HTTPS：{uri}");
        }

        if (!uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            && !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"FTB Installer 來源不是官方 GitHub host：{uri}");
        }
    }

    private static void EnsureOfficialAssetResponse(HttpResponseMessage response)
        => EnsureFinalUri(response, uri =>
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static void EnsureFinalUri(HttpResponseMessage response, Func<Uri, bool> predicate)
    {
        var uri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("下載回應缺少最終來源 URI。");
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !predicate(uri))
        {
            throw new InvalidDataException($"FTB Installer 重新導向到未核准來源：{uri}");
        }
    }

    private static void EnsureDeclaredSize(HttpContentHeaders headers, long expectedSize)
    {
        if (headers.ContentLength is not { } declared || declared != expectedSize)
        {
            throw new InvalidDataException(
                $"FTB release asset Content-Length 不符，預期 {expectedSize}，實際 {headers.ContentLength?.ToString() ?? "missing"}。");
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = await ReadBoundedBytesAsync(response.Content, 64 * 1024, cancellationToken)
            .ConfigureAwait(false);
        throw new HttpRequestException(
            $"下載 FTB 官方檔案失敗：HTTP {(int)response.StatusCode} {response.ReasonPhrase}. "
            + Encoding.UTF8.GetString(details));
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
        {
            throw new InvalidDataException("下載回應超過允許大小。");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return output.ToArray();
            }

            total += count;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("下載回應超過允許大小。");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ReadBoolean(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string ReadRequiredString(JsonElement element, string property, string context)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{context}缺少文字欄位 {property}。");
        }

        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidDataException($"{context}的 {property} 不得為空。");
        }

        return result;
    }

    private static void TryDeleteFile(string path)
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
            // Preserve the original security/download failure.
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    private sealed record ReleaseInfo(string Tag, ReleaseAsset Installer, ReleaseAsset Checksum);

    private sealed record ReleaseAsset(string Name, long Size, Uri DownloadUri, string Digest);
}
