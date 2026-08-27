using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Providers;

public interface IModrinthOfficialLoaderArtifactProvider
{
    Task<ModrinthLoaderArtifact> DownloadVanillaServerAsync(
        string minecraftVersion,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task VerifyVanillaServerAsync(
        string minecraftVersion,
        string serverJarPath,
        CancellationToken cancellationToken = default);

    Task<ModrinthLoaderArtifact> DownloadLatestStableFabricInstallerAsync(
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ModrinthLoaderArtifact> DownloadForgeInstallerAsync(
        string minecraftVersion,
        string loaderVersion,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ModrinthLoaderArtifact> DownloadNeoForgeInstallerAsync(
        string loaderVersion,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves and downloads only first-party Mojang/Fabric/Forge/NeoForge artifacts. Every response
/// is bounded, final redirect hosts are checked, and files are atomically promoted only after an
/// official SHA-1 or SHA-256 and declared size have been verified.
/// </summary>
public sealed partial class ModrinthOfficialLoaderArtifactProvider
    : IModrinthOfficialLoaderArtifactProvider
{
    private static readonly Uri MojangVersionManifest = new(
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    private static readonly Uri FabricInstallerVersions = new(
        "https://meta.fabricmc.net/v2/versions/installer");

    private const long MaximumJsonBytes = 16L * 1024 * 1024;
    private const long MaximumChecksumBytes = 4L * 1024;
    private const long MaximumInstallerBytes = 256L * 1024 * 1024;
    private const long MaximumServerJarBytes = 1024L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly string _userAgent;

    public ModrinthOfficialLoaderArtifactProvider(HttpClient httpClient, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        if (userAgent.Contains('\r') || userAgent.Contains('\n'))
        {
            throw new ArgumentException("User-Agent 不得包含換行字元。", nameof(userAgent));
        }

        _httpClient = httpClient;
        _userAgent = userAgent.Trim();
    }

    public async Task<ModrinthLoaderArtifact> DownloadVanillaServerAsync(
        string minecraftVersion,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var descriptor = await ResolveMojangServerAsync(minecraftVersion, cancellationToken)
            .ConfigureAwait(false);
        var file = await DownloadVerifiedFileAsync(
                descriptor.Source,
                destinationPath,
                HashAlgorithmName.SHA1,
                descriptor.Sha1,
                descriptor.Size,
                MaximumServerJarBytes,
                IsOfficialMojangDataUri,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        return new ModrinthLoaderArtifact(
            ModrinthLoaderArtifactKind.MinecraftServer,
            file,
            descriptor.Source,
            descriptor.Size,
            "SHA-1",
            descriptor.Sha1.ToLowerInvariant());
    }

    public async Task VerifyVanillaServerAsync(
        string minecraftVersion,
        string serverJarPath,
        CancellationToken cancellationToken = default)
    {
        var descriptor = await ResolveMojangServerAsync(minecraftVersion, cancellationToken)
            .ConfigureAwait(false);
        await VerifyLocalFileAsync(
                serverJarPath,
                HashAlgorithmName.SHA1,
                descriptor.Sha1,
                descriptor.Size,
                MaximumServerJarBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ModrinthLoaderArtifact> DownloadLatestStableFabricInstallerAsync(
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = await GetBoundedBytesAsync(
                FabricInstallerVersions,
                MaximumJsonBytes,
                "application/json",
                IsOfficialFabricMetaUri,
                cancellationToken)
            .ConfigureAwait(false);
        using var document = ParseJson(bytes, "Fabric Installer Meta");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Fabric Installer Meta 回應必須是陣列。");
        }

        JsonElement? selected = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("stable", out var stable)
                && stable.ValueKind == JsonValueKind.True)
            {
                selected = item;
                break;
            }
        }

        var installer = selected
            ?? throw new InvalidDataException("Fabric Installer Meta 沒有 stable installer。");
        var version = ReadRequiredString(installer, "version", "Fabric Installer");
        ValidateMavenToken(version, "Fabric Installer version");
        var expectedMaven = $"net.fabricmc:fabric-installer:{version}";
        if (!ReadRequiredString(installer, "maven", "Fabric Installer")
            .Equals(expectedMaven, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Fabric Installer Maven coordinate 與官方格式不符。");
        }

        var expectedUri = new Uri(
            $"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{version}/"
            + $"fabric-installer-{version}.jar");
        var source = ReadRequiredUri(installer, "url", "Fabric Installer");
        if (!source.AbsoluteUri.Equals(expectedUri.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Fabric Installer URL 與官方 Maven coordinate 不符。");
        }

        return await DownloadMavenInstallerAsync(
                ModrinthLoaderArtifactKind.FabricInstaller,
                source,
                destinationPath,
                IsOfficialFabricMavenUri,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<ModrinthLoaderArtifact> DownloadForgeInstallerAsync(
        string minecraftVersion,
        string loaderVersion,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateMavenToken(minecraftVersion, "Minecraft version");
        ValidateMavenToken(loaderVersion, "Forge version");
        var coordinate = loaderVersion.StartsWith(minecraftVersion + "-", StringComparison.Ordinal)
            ? loaderVersion
            : $"{minecraftVersion}-{loaderVersion}";
        var source = new Uri(
            $"https://maven.minecraftforge.net/net/minecraftforge/forge/{coordinate}/"
            + $"forge-{coordinate}-installer.jar");
        return DownloadMavenInstallerAsync(
            ModrinthLoaderArtifactKind.ForgeInstaller,
            source,
            destinationPath,
            IsOfficialForgeMavenUri,
            progress,
            cancellationToken);
    }

    public Task<ModrinthLoaderArtifact> DownloadNeoForgeInstallerAsync(
        string loaderVersion,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateMavenToken(loaderVersion, "NeoForge version");
        var source = new Uri(
            $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/"
            + $"neoforge-{loaderVersion}-installer.jar");
        return DownloadMavenInstallerAsync(
            ModrinthLoaderArtifactKind.NeoForgeInstaller,
            source,
            destinationPath,
            IsOfficialNeoForgeMavenUri,
            progress,
            cancellationToken);
    }

    private async Task<ModrinthLoaderArtifact> DownloadMavenInstallerAsync(
        ModrinthLoaderArtifactKind kind,
        Uri source,
        string destinationPath,
        Func<Uri, bool> uriPolicy,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        EnsureUriAllowed(source, uriPolicy, "Loader Installer");
        var checksum = await ResolveMavenChecksumAsync(
                source,
                uriPolicy,
                cancellationToken)
            .ConfigureAwait(false);
        var exactArtifactPolicy = CreateExactUriPolicy(source, uriPolicy);
        var file = await DownloadVerifiedFileAsync(
                source,
                destinationPath,
                checksum.Algorithm,
                checksum.Hash,
                expectedSize: null,
                MaximumInstallerBytes,
                exactArtifactPolicy,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        var size = new FileInfo(file).Length;
        return new ModrinthLoaderArtifact(
            kind,
            file,
            source,
            size,
            checksum.DisplayName,
            checksum.Hash.ToLowerInvariant());
    }

    private async Task<MavenChecksum> ResolveMavenChecksumAsync(
        Uri artifactUri,
        Func<Uri, bool> uriPolicy,
        CancellationToken cancellationToken)
    {
        var sha256Uri = new Uri(artifactUri.AbsoluteUri + ".sha256");
        try
        {
            var bytes = await GetBoundedBytesAsync(
                    sha256Uri,
                    MaximumChecksumBytes,
                    "text/plain",
                    CreateExactUriPolicy(sha256Uri, uriPolicy),
                    cancellationToken)
                .ConfigureAwait(false);
            return new MavenChecksum(
                HashAlgorithmName.SHA256,
                "SHA-256",
                ParseStrictHash(bytes, 64, "Maven .sha256"));
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Some historical first-party Forge releases predate published SHA-256 sidecars.
            // Fall back only when that exact sidecar is definitively absent; malformed, redirected,
            // oversized, forbidden, or transient SHA-256 responses remain hard failures.
        }

        var sha1Uri = new Uri(artifactUri.AbsoluteUri + ".sha1");
        var sha1Bytes = await GetBoundedBytesAsync(
                sha1Uri,
                MaximumChecksumBytes,
                "text/plain",
                CreateExactUriPolicy(sha1Uri, uriPolicy),
                cancellationToken)
            .ConfigureAwait(false);
        return new MavenChecksum(
            HashAlgorithmName.SHA1,
            "SHA-1",
            ParseStrictHash(sha1Bytes, 40, "Maven .sha1"));
    }

    private static Func<Uri, bool> CreateExactUriPolicy(
        Uri expected,
        Func<Uri, bool> basePolicy)
        => candidate => basePolicy(candidate)
            && candidate.AbsoluteUri.Equals(expected.AbsoluteUri, StringComparison.Ordinal);

    private async Task<MojangServerDescriptor> ResolveMojangServerAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        ValidateVersionArgument(minecraftVersion, "Minecraft version");
        var manifestBytes = await GetBoundedBytesAsync(
                MojangVersionManifest,
                MaximumJsonBytes,
                "application/json",
                IsOfficialMojangMetaUri,
                cancellationToken)
            .ConfigureAwait(false);
        using var manifest = ParseJson(manifestBytes, "Mojang version manifest");
        if (!manifest.RootElement.TryGetProperty("versions", out var versions)
            || versions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Mojang version manifest 缺少 versions 陣列。");
        }

        JsonElement? selected = null;
        foreach (var item in versions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || !minecraftVersion.Equals(id.GetString(), StringComparison.Ordinal))
            {
                continue;
            }

            if (selected is not null)
            {
                throw new InvalidDataException($"Mojang manifest 含有重複版本：{minecraftVersion}");
            }

            selected = item;
        }

        var versionEntry = selected
            ?? throw new InvalidDataException($"Mojang manifest 找不到 Minecraft {minecraftVersion}。");
        var metadataUri = ReadRequiredUri(versionEntry, "url", "Mojang version");
        EnsureUriAllowed(metadataUri, IsOfficialMojangMetaUri, "Mojang version metadata");
        var metadataSha1 = ReadRequiredHash(versionEntry, "sha1", 40, "Mojang version metadata");
        var metadataBytes = await GetBoundedBytesAsync(
                metadataUri,
                MaximumJsonBytes,
                "application/json",
                IsOfficialMojangMetaUri,
                cancellationToken)
            .ConfigureAwait(false);
        VerifyBytesHash(metadataBytes, HashAlgorithmName.SHA1, metadataSha1, "Mojang version metadata");

        using var metadata = ParseJson(metadataBytes, "Mojang version metadata");
        if (!ReadRequiredString(metadata.RootElement, "id", "Mojang version metadata")
            .Equals(minecraftVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Mojang version metadata ID 與請求版本不符。");
        }

        if (!metadata.RootElement.TryGetProperty("downloads", out var downloads)
            || downloads.ValueKind != JsonValueKind.Object
            || !downloads.TryGetProperty("server", out var server)
            || server.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Minecraft {minecraftVersion} 沒有官方 dedicated server 下載。");
        }

        var source = ReadRequiredUri(server, "url", "Mojang server download");
        EnsureUriAllowed(source, IsOfficialMojangDataUri, "Mojang server download");
        var sha1 = ReadRequiredHash(server, "sha1", 40, "Mojang server download");
        var size = ReadRequiredSize(server, "size", MaximumServerJarBytes, "Mojang server download");
        return new MojangServerDescriptor(source, size, sha1);
    }

    private async Task<string> DownloadVerifiedFileAsync(
        Uri source,
        string destinationPath,
        HashAlgorithmName hashAlgorithm,
        string expectedHash,
        long? expectedSize,
        long maximumSize,
        Func<Uri, bool> uriPolicy,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        EnsureUriAllowed(source, uriPolicy, "下載來源");
        var destination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("下載目的路徑沒有父目錄。");
        var parentInfo = new DirectoryInfo(parent);
        if (!parentInfo.Exists)
        {
            throw new DirectoryNotFoundException($"下載目的父目錄不存在：{parent}");
        }

        RejectReparse(parentInfo, "下載目的父目錄");
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"下載目的已存在，不會覆寫：{destination}");
        }

        var partial = destination + $".{Guid.NewGuid():N}.partial";
        try
        {
            using var response = await SendAsync(source, "application/octet-stream", cancellationToken)
                .ConfigureAwait(false);
            EnsureFinalUri(response, uriPolicy, "下載檔案");
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            var declared = response.Content.Headers.ContentLength;
            if (expectedSize is { } knownSize)
            {
                if (declared != knownSize)
                {
                    throw new InvalidDataException(
                        $"下載 Content-Length 不符，預期 {knownSize}，實際 {declared?.ToString() ?? "missing"}。");
                }
            }
            else if (declared is null or < 1 || declared > maximumSize)
            {
                throw new InvalidDataException("官方 Maven 檔案必須提供安全的 Content-Length。");
            }

            var targetSize = expectedSize ?? declared!.Value;
            if (targetSize < 1 || targetSize > maximumSize)
            {
                throw new InvalidDataException("下載檔案大小超過安全上限。");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                partial,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(hashAlgorithm);
            var buffer = new byte[128 * 1024];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > targetSize || total > maximumSize)
                {
                    throw new InvalidDataException("下載檔案超過官方宣告大小。");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                progress?.Report(Math.Clamp((double)total / targetSize, 0d, 1d));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (total != targetSize)
            {
                throw new InvalidDataException(
                    $"下載檔案大小不符，預期 {targetSize}，實際 {total}。");
            }

            VerifyHash(hash.GetHashAndReset(), expectedHash, hashAlgorithm.Name ?? "hash", "下載檔案");
            await output.DisposeAsync().ConfigureAwait(false);
            File.Move(partial, destination, overwrite: false);
            progress?.Report(1d);
            return destination;
        }
        catch
        {
            TryDeleteFile(partial);
            throw;
        }
    }

    private static async Task VerifyLocalFileAsync(
        string filePath,
        HashAlgorithmName algorithm,
        string expectedHash,
        long expectedSize,
        long maximumSize,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length != expectedSize || info.Length < 1 || info.Length > maximumSize)
        {
            throw new InvalidDataException("本機 Minecraft server.jar 大小與 Mojang metadata 不符。");
        }

        RejectReparse(info, "Minecraft server.jar");
        await using var input = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(algorithm);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        VerifyHash(hash.GetHashAndReset(), expectedHash, algorithm.Name ?? "hash", "Minecraft server.jar");
    }

    private async Task<byte[]> GetBoundedBytesAsync(
        Uri source,
        long maximumBytes,
        string accept,
        Func<Uri, bool> uriPolicy,
        CancellationToken cancellationToken)
    {
        EnsureUriAllowed(source, uriPolicy, "API 來源");
        using var response = await SendAsync(source, accept, cancellationToken).ConfigureAwait(false);
        EnsureFinalUri(response, uriPolicy, "API 回應");
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } declared && declared > maximumBytes)
        {
            throw new InvalidDataException("API 回應超過安全大小上限。");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("API 回應超過安全大小上限。");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri source,
        string accept,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = await ReadErrorTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"官方 Loader 來源回應 HTTP {(int)response.StatusCode} "
            + $"{response.ReasonPhrase}. {details}",
            inner: null,
            response.StatusCode);
    }

    private static async Task<string> ReadErrorTextAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        const int maximum = 64 * 1024;
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maximum];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static JsonDocument ParseJson(byte[] bytes, string context)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{context} 回傳無效 JSON。", exception);
        }
    }

    private static string ReadRequiredString(JsonElement element, string property, string context)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{context} 缺少文字欄位 {property}。");
        }

        return value.GetString()!;
    }

    private static Uri ReadRequiredUri(JsonElement element, string property, string context)
    {
        var text = ReadRequiredString(element, property, context);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException($"{context} 的 {property} 不是有效 URL。");
        }

        return uri;
    }

    private static string ReadRequiredHash(
        JsonElement element,
        string property,
        int length,
        string context)
    {
        var hash = ReadRequiredString(element, property, context);
        if (hash.Length != length || !HexRegex().IsMatch(hash))
        {
            throw new InvalidDataException($"{context} 的 {property} 無效。");
        }

        return hash;
    }

    private static long ReadRequiredSize(
        JsonElement element,
        string property,
        long maximum,
        string context)
    {
        if (!element.TryGetProperty(property, out var value)
            || !value.TryGetInt64(out var size)
            || size < 1
            || size > maximum)
        {
            throw new InvalidDataException($"{context} 的 {property} 無效。");
        }

        return size;
    }

    private static string ParseStrictHash(byte[] bytes, int length, string context)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes).Trim();
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"{context} 不是有效 UTF-8。", exception);
        }

        if (text.Length != length || !HexRegex().IsMatch(text))
        {
            throw new InvalidDataException($"{context} 必須只包含 {length} 位十六進位 hash。");
        }

        return text;
    }

    private static void VerifyBytesHash(
        byte[] bytes,
        HashAlgorithmName algorithm,
        string expectedHash,
        string context)
    {
        var actual = algorithm == HashAlgorithmName.SHA1
            ? SHA1.HashData(bytes)
            : SHA256.HashData(bytes);
        VerifyHash(actual, expectedHash, algorithm.Name ?? "hash", context);
    }

    private static void VerifyHash(
        byte[] actual,
        string expectedHash,
        string algorithm,
        string context)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHash);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{context} 的 {algorithm} 格式無效。", exception);
        }

        if (expected.Length != actual.Length
            || !CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new InvalidDataException($"{context} {algorithm} 驗證失敗。");
        }
    }

    private static void EnsureFinalUri(
        HttpResponseMessage response,
        Func<Uri, bool> uriPolicy,
        string context)
    {
        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException($"{context} 缺少最終來源 URI。");
        EnsureUriAllowed(finalUri, uriPolicy, context);
    }

    private static void EnsureUriAllowed(Uri uri, Func<Uri, bool> policy, string context)
    {
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !policy(uri))
        {
            throw new InvalidDataException($"{context} 指向未核准的來源：{uri}");
        }
    }

    private static bool IsOfficialMojangMetaUri(Uri uri)
        => uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsOfficialMojangDataUri(Uri uri)
        => (uri.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("launcher.mojang.com", StringComparison.OrdinalIgnoreCase))
            && uri.AbsolutePath.StartsWith("/v1/objects/", StringComparison.Ordinal)
            && uri.AbsolutePath.EndsWith("/server.jar", StringComparison.Ordinal);

    private static bool IsOfficialFabricMetaUri(Uri uri)
        => uri.Host.Equals("meta.fabricmc.net", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/v2/versions/installer", StringComparison.Ordinal);

    private static bool IsOfficialFabricMavenUri(Uri uri)
        => uri.Host.Equals("maven.fabricmc.net", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith(
                "/net/fabricmc/fabric-installer/",
                StringComparison.Ordinal);

    private static bool IsOfficialForgeMavenUri(Uri uri)
        => uri.Host.Equals("maven.minecraftforge.net", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith(
                "/net/minecraftforge/forge/",
                StringComparison.Ordinal);

    private static bool IsOfficialNeoForgeMavenUri(Uri uri)
        => uri.Host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith(
                "/releases/net/neoforged/neoforge/",
                StringComparison.Ordinal);

    private static void ValidateMavenToken(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!MavenTokenRegex().IsMatch(value))
        {
            throw new ArgumentException($"{name} 不是安全的 Maven 版本值。", name);
        }
    }

    internal static void ValidateVersionArgument(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128
            || value[0] == '-'
            || value.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException($"{name} 含有不安全字元。", name);
        }
    }

    private static void RejectReparse(FileSystemInfo info, string context)
    {
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{context} 不得是 reparse point。");
        }
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
            // Preserve the original download/security failure.
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HexRegex();

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z._+\\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex MavenTokenRegex();

    private sealed record MojangServerDescriptor(Uri Source, long Size, string Sha1);

    private sealed record MavenChecksum(
        HashAlgorithmName Algorithm,
        string DisplayName,
        string Hash);
}
