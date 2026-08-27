namespace MinecraftServerManager.Contracts.Plugins;

public static class ProductFirstPartyProviderIdentities
{
    public const string PublisherId = "muhun.firstparty";
    public const string CatalogProviderId = "muhun.catalog";
}

public static class ProductModpackCatalogSources
{
    public const string Modrinth = "modrinth";
    public const string Ftb = "ftb";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Modrinth,
        Ftb,
    };
}

/// <summary>Bounded request understood by the signed Muhun catalogue provider.</summary>
public sealed record ProductProviderModpackSearchRequest(
    string Source,
    string Query = "",
    string? GameVersion = null,
    string? Loader = null,
    int Offset = 0,
    int Limit = 20,
    string Sort = "relevance",
    string? Category = null);

public sealed record ProductProviderModpackProject(
    string Source,
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string Author,
    Uri? IconUri,
    Uri? PreviewImageUri,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<string> Categories,
    long Downloads,
    DateTimeOffset? UpdatedAtUtc);

public sealed record ProductProviderModpackSearchPage(
    IReadOnlyList<ProductProviderModpackProject> Projects,
    int Offset,
    int Limit,
    int TotalHits);
