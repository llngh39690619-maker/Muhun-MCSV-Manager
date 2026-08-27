using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Providers;

public sealed record AddonUpdateInfo(
    string LocalPath,
    string FileName,
    string Sha512,
    bool IsRecognized,
    string? ProjectId,
    string? CurrentVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    Uri? DownloadUri,
    string? DownloadFileName,
    string? DownloadSha512,
    long? DownloadSize,
    string Message);

/// <summary>
/// Performs read-only plugin/mod recognition and compatible update lookup through Modrinth.
/// It never replaces local files; callers can present the verified metadata for approval.
/// </summary>
public sealed class ModrinthUpdateProvider
{
    private static readonly Uri BaseUri = new("https://api.modrinth.com/v2/");
    private readonly HttpClient _httpClient;

    public ModrinthUpdateProvider(HttpClient httpClient, string userAgent)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= BaseUri;
        _httpClient.Timeout = TimeSpan.FromMinutes(2);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<AddonUpdateInfo>> CheckUpdatesAsync(
        ServerInstance instance,
        IProgress<(int Completed, int Total)>? hashProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var files = ResolveAddonDirectories(instance)
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.jar", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var localArtifacts = new List<LocalArtifact>(files.Length);
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                files[index],
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA512.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            localArtifacts.Add(new LocalArtifact(files[index], hash));
            hashProgress?.Report((index + 1, files.Length));
        }

        var results = new List<AddonUpdateInfo>(localArtifacts.Count);
        foreach (var batch in localArtifacts.Chunk(100))
        {
            var hashes = batch.Select(artifact => artifact.Sha512).ToArray();
            using var recognizedDocument = await PostJsonAsync(
                "version_files",
                new Dictionary<string, object?>
                {
                    ["hashes"] = hashes,
                    ["algorithm"] = "sha512"
                },
                cancellationToken).ConfigureAwait(false);

            var updateBody = new Dictionary<string, object?>
            {
                ["hashes"] = hashes,
                ["algorithm"] = "sha512",
                ["version_types"] = new[] { "release" }
            };
            var loaders = ResolveLoaders(instance.CoreType);
            if (loaders.Length > 0) updateBody["loaders"] = loaders;
            if (!string.IsNullOrWhiteSpace(instance.MinecraftVersion))
            {
                updateBody["game_versions"] = new[] { instance.MinecraftVersion };
            }

            using var updatesDocument = await PostJsonAsync("version_files/update", updateBody, cancellationToken)
                .ConfigureAwait(false);

            foreach (var artifact in batch)
            {
                if (!recognizedDocument.RootElement.TryGetProperty(artifact.Sha512, out var current))
                {
                    results.Add(new AddonUpdateInfo(
                        artifact.Path,
                        Path.GetFileName(artifact.Path),
                        artifact.Sha512,
                        false,
                        null,
                        null,
                        null,
                        false,
                        null,
                        null,
                        null,
                        null,
                        "Modrinth 無法透過檔案雜湊辨識"));
                    continue;
                }

                var currentVersion = ReadString(current, "version_number");
                var projectId = ReadString(current, "project_id");
                if (!updatesDocument.RootElement.TryGetProperty(artifact.Sha512, out var latest))
                {
                    results.Add(new AddonUpdateInfo(
                        artifact.Path,
                        Path.GetFileName(artifact.Path),
                        artifact.Sha512,
                        true,
                        projectId,
                        currentVersion,
                        currentVersion,
                        false,
                        null,
                        null,
                        null,
                        null,
                        "沒有找到符合目前 Loader 與 Minecraft 版本的 Release 更新"));
                    continue;
                }

                var latestVersion = ReadString(latest, "version_number");
                var download = ReadPrimaryFile(latest);
                var hasUpdate = download?.Sha512 is { Length: > 0 } candidateHash
                    && !candidateHash.Equals(artifact.Sha512, StringComparison.OrdinalIgnoreCase);
                results.Add(new AddonUpdateInfo(
                    artifact.Path,
                    Path.GetFileName(artifact.Path),
                    artifact.Sha512,
                    true,
                    projectId,
                    currentVersion,
                    latestVersion,
                    hasUpdate,
                    download?.Url,
                    download?.FileName,
                    download?.Sha512,
                    download?.Size,
                    hasUpdate ? "有相容的 Release 更新" : "已是相容的最新檔案"));
            }
        }

        return results;
    }

    private async Task<JsonDocument> PostJsonAsync(
        string relativeUri,
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(relativeUri, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Modrinth API 錯誤：HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {details}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<string> ResolveAddonDirectories(ServerInstance instance)
    {
        if (instance.CoreType is CoreType.Fabric or CoreType.Forge or CoreType.NeoForge)
        {
            yield return Path.Combine(instance.DirectoryPath, "mods");
            yield break;
        }

        if (instance.CoreType is CoreType.Mohist or CoreType.Arclight or CoreType.CatServer)
        {
            yield return Path.Combine(instance.DirectoryPath, "plugins");
            yield return Path.Combine(instance.DirectoryPath, "mods");
            yield break;
        }

        if (instance.CoreType is CoreType.Vanilla)
        {
            yield break;
        }

        yield return Path.Combine(instance.DirectoryPath, "plugins");
        if (instance.CoreType is CoreType.Unknown or CoreType.CustomJar)
        {
            yield return Path.Combine(instance.DirectoryPath, "mods");
        }
    }

    private static string[] ResolveLoaders(CoreType coreType) => coreType switch
    {
        CoreType.Paper => ["paper", "spigot", "bukkit"],
        CoreType.Purpur => ["purpur", "paper", "spigot", "bukkit"],
        CoreType.Folia => ["folia", "paper"],
        CoreType.Spigot => ["spigot", "bukkit"],
        CoreType.CraftBukkit => ["bukkit"],
        CoreType.Fabric => ["fabric"],
        CoreType.Forge => ["forge"],
        CoreType.NeoForge => ["neoforge"],
        CoreType.Velocity => ["velocity"],
        CoreType.Mohist => ["forge", "spigot", "bukkit"],
        CoreType.Arclight => ["neoforge", "forge", "fabric", "spigot", "bukkit"],
        CoreType.CatServer => ["forge", "spigot", "bukkit"],
        CoreType.Akarin => ["paper", "spigot", "bukkit"],
        _ => []
    };

    private static DownloadArtifact? ReadPrimaryFile(JsonElement version)
    {
        if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? fallback = null;
        foreach (var file in files.EnumerateArray())
        {
            fallback ??= file;
            if (file.TryGetProperty("primary", out var primary) && primary.GetBoolean())
            {
                return ParseDownload(file);
            }
        }

        return fallback is { } candidate ? ParseDownload(candidate) : null;
    }

    private static DownloadArtifact? ParseDownload(JsonElement file)
    {
        var url = ReadString(file, "url");
        var fileName = ReadString(file, "filename");
        if (url is null || fileName is null) return null;
        var sha512 = file.TryGetProperty("hashes", out var hashes) ? ReadString(hashes, "sha512") : null;
        var size = file.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
            ? parsedSize
            : (long?)null;
        return new DownloadArtifact(new Uri(url), fileName, sha512, size);
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record LocalArtifact(string Path, string Sha512);
    private sealed record DownloadArtifact(Uri Url, string FileName, string? Sha512, long? Size);
}
