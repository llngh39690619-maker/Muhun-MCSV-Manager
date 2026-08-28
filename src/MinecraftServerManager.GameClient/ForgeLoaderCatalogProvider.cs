using System.Text.Json;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>Discovers only Forge versions explicitly promoted as recommended.</summary>
public sealed class ForgeLoaderCatalogProvider : IMinecraftLoaderCatalogProvider
{
    public static readonly Uri PromotionsUri =
        new("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json");

    private const int MaximumPromotions = 8_192;
    private const long MaximumCatalogBytes = 4L * 1024 * 1024;
    private static readonly IReadOnlySet<string> AllowedHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "files.minecraftforge.net" };
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    private readonly OfficialCatalogHttpReader _reader;

    public ForgeLoaderCatalogProvider(HttpClient httpClient, TimeSpan? requestTimeout = null)
    {
        _reader = new OfficialCatalogHttpReader(httpClient, requestTimeout);
    }

    public MinecraftClientLoader Loader => MinecraftClientLoader.Forge;

    public async Task<IReadOnlyList<MinecraftLoaderCatalogEntry>> GetVersionsAsync(
        MinecraftReleaseCatalogSnapshot stableMinecraftReleases,
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        if (!OfficialCatalogValidation.IsStableMinecraftRelease(stableMinecraftReleases, gameVersion))
        {
            return [];
        }

        var bytes = await _reader.GetAsync(
                PromotionsUri,
                AllowedHosts,
                MaximumCatalogBytes,
                cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, JsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("promos", out var promotions) ||
            promotions.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Forge promotions schema is invalid.");
        }

        var recommendationKey = $"{gameVersion}-recommended";
        string? recommended = null;
        var count = 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var promotion in promotions.EnumerateObject())
        {
            count++;
            if (count > MaximumPromotions)
            {
                throw new InvalidDataException("Forge promotions catalog contains too many entries.");
            }

            if (!keys.Add(promotion.Name))
            {
                throw new InvalidDataException($"Forge promotions contains duplicate key '{promotion.Name}'.");
            }

            if (!string.Equals(promotion.Name, recommendationKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (promotion.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Forge recommended promotion has an invalid value.");
            }

            recommended = promotion.Value.GetString();
        }

        if (recommended is null)
        {
            return [];
        }

        OfficialCatalogValidation.ValidateVersionToken(recommended, "Forge recommended version");
        if (!OfficialCatalogValidation.IsStrictStableNumericVersion(recommended, 2, 4))
        {
            throw new InvalidDataException("Forge recommended promotion is not a stable numeric release.");
        }

        var artifactUri = CreateInstallerArtifactUri(gameVersion, recommended);
        return
        [
            new MinecraftLoaderCatalogEntry(
                Loader,
                gameVersion,
                recommended,
                MinecraftLoaderReleaseChannel.Recommended,
                MinecraftClientLoaderInstallKind.Managed,
                PromotionsUri,
                artifactUri,
                "Forge 官方 recommended 版本；安裝前仍須驗證 Maven checksum sidecar。")
        ];
    }

    internal static Uri CreateInstallerArtifactUri(string gameVersion, string loaderVersion)
    {
        OfficialCatalogValidation.ValidateVersionToken(
            gameVersion,
            "Minecraft version",
            maximumLength: 64);
        OfficialCatalogValidation.ValidateVersionToken(
            loaderVersion,
            "Forge version",
            maximumLength: 128);
        if (!OfficialCatalogValidation.IsStrictStableNumericVersion(gameVersion, 2, 3) ||
            !OfficialCatalogValidation.IsStrictStableNumericVersion(loaderVersion, 2, 4))
        {
            throw new InvalidDataException(
                "Forge installation requires stable numeric Minecraft and loader versions.");
        }

        var coordinate = $"{gameVersion}-{loaderVersion}";
        return new Uri(
            $"https://maven.minecraftforge.net/net/minecraftforge/forge/{coordinate}/forge-{coordinate}-installer.jar");
    }
}
