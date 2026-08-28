using System.Text.Json;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>Discovers stable Fabric Loader versions through Fabric Meta v2.</summary>
public sealed class FabricLoaderCatalogProvider : IMinecraftLoaderCatalogProvider
{
    private const int MaximumEntries = 4_096;
    private const long MaximumCatalogBytes = 4L * 1024 * 1024;
    private static readonly IReadOnlySet<string> AllowedHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "meta.fabricmc.net" };
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    private readonly OfficialCatalogHttpReader _reader;

    public FabricLoaderCatalogProvider(HttpClient httpClient, TimeSpan? requestTimeout = null)
    {
        _reader = new OfficialCatalogHttpReader(httpClient, requestTimeout);
    }

    public MinecraftClientLoader Loader => MinecraftClientLoader.Fabric;

    public async Task<IReadOnlyList<MinecraftLoaderCatalogEntry>> GetVersionsAsync(
        MinecraftReleaseCatalogSnapshot stableMinecraftReleases,
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        if (!OfficialCatalogValidation.IsStableMinecraftRelease(stableMinecraftReleases, gameVersion))
        {
            return [];
        }

        var encodedGameVersion = Uri.EscapeDataString(gameVersion);
        var catalogUri = new Uri(
            $"https://meta.fabricmc.net/v2/versions/loader/{encodedGameVersion}");
        var bytes = await _reader.GetAsync(
                catalogUri,
                AllowedHosts,
                MaximumCatalogBytes,
                cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(bytes, JsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() > MaximumEntries)
        {
            throw new InvalidDataException("Fabric loader catalog schema or entry count is invalid.");
        }

        var entries = new List<MinecraftLoaderCatalogEntry>();
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("loader", out var loader) ||
                loader.ValueKind != JsonValueKind.Object ||
                !loader.TryGetProperty("stable", out var stable) ||
                stable.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !item.TryGetProperty("intermediary", out var intermediary) ||
                intermediary.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Fabric loader catalog entry schema is invalid.");
            }

            var version = RequireString(loader, "version", 128);
            OfficialCatalogValidation.ValidateVersionToken(version, "Fabric loader version");
            var maven = RequireString(loader, "maven", 256);
            if (!maven.Equals($"net.fabricmc:fabric-loader:{version}", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Fabric loader Maven coordinate is inconsistent.");
            }

            var intermediaryVersion = RequireString(intermediary, "version", 64);
            if (!string.Equals(intermediaryVersion, gameVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Fabric intermediary version is inconsistent with the request.");
            }

            if (stable.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            if (!versions.Add(version))
            {
                throw new InvalidDataException($"Fabric loader catalog contains duplicate version '{version}'.");
            }

            var profileUri = new Uri(
                $"https://meta.fabricmc.net/v2/versions/loader/{encodedGameVersion}/{Uri.EscapeDataString(version)}/profile/json");
            entries.Add(new MinecraftLoaderCatalogEntry(
                Loader,
                gameVersion,
                version,
                MinecraftLoaderReleaseChannel.Stable,
                MinecraftClientLoaderInstallKind.Managed,
                catalogUri,
                profileUri,
                "Fabric 官方穩定版 launcher profile。"));
        }

        entries.Sort(static (left, right) =>
            LoaderVersionComparer.Instance.Compare(right.Version, left.Version));
        return entries;
    }

    private static string RequireString(JsonElement element, string propertyName, int maximumLength)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()) ||
            property.GetString()!.Length > maximumLength)
        {
            throw new InvalidDataException($"Fabric loader property '{propertyName}' is invalid.");
        }

        return property.GetString()!;
    }
}
