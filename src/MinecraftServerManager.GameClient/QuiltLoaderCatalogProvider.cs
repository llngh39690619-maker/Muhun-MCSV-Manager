using System.Text.Json;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>Discovers non-prerelease Quilt Loader versions through Quilt Meta v3.</summary>
public sealed class QuiltLoaderCatalogProvider : IMinecraftLoaderCatalogProvider
{
    private const int MaximumEntries = 4_096;
    private const long MaximumCatalogBytes = 4L * 1024 * 1024;
    private static readonly IReadOnlySet<string> AllowedHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "meta.quiltmc.org" };
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    private readonly OfficialCatalogHttpReader _reader;

    public QuiltLoaderCatalogProvider(HttpClient httpClient, TimeSpan? requestTimeout = null)
    {
        _reader = new OfficialCatalogHttpReader(httpClient, requestTimeout);
    }

    public MinecraftClientLoader Loader => MinecraftClientLoader.Quilt;

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
            $"https://meta.quiltmc.org/v3/versions/loader/{encodedGameVersion}");
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
            throw new InvalidDataException("Quilt loader catalog schema or entry count is invalid.");
        }

        var entries = new List<MinecraftLoaderCatalogEntry>();
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("loader", out var loader) ||
                loader.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Quilt loader catalog entry schema is invalid.");
            }

            var version = RequireString(loader, "version", 128);
            OfficialCatalogValidation.ValidateVersionToken(version, "Quilt loader version");
            var maven = RequireString(loader, "maven", 256);
            if (!maven.Equals($"org.quiltmc:quilt-loader:{version}", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Quilt loader Maven coordinate is inconsistent.");
            }

            // Quilt Meta has no loader-level stable boolean. Quilt's documented stable choice is
            // the latest non-beta build, so fail closed to plain numeric release identifiers.
            if (!OfficialCatalogValidation.IsStrictStableNumericVersion(version, 2, 4))
            {
                continue;
            }

            if (!versions.Add(version))
            {
                throw new InvalidDataException($"Quilt loader catalog contains duplicate version '{version}'.");
            }

            var profileUri = new Uri(
                $"https://meta.quiltmc.org/v3/versions/loader/{encodedGameVersion}/{Uri.EscapeDataString(version)}/profile/json");
            entries.Add(new MinecraftLoaderCatalogEntry(
                Loader,
                gameVersion,
                version,
                MinecraftLoaderReleaseChannel.Stable,
                MinecraftClientLoaderInstallKind.Managed,
                catalogUri,
                profileUri,
                "Quilt 官方非預發布版 launcher profile。"));
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
            throw new InvalidDataException($"Quilt loader property '{propertyName}' is invalid.");
        }

        return property.GetString()!;
    }
}
