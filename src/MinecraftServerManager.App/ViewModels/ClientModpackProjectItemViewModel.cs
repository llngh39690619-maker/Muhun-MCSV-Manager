using System.Windows;
using System.Text.RegularExpressions;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.GameClient;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.ViewModels;

public sealed class ClientModpackProjectItemViewModel : ObservableObject
{
    private string? _iconImagePath;
    private string? _previewImagePath;
    private readonly string _author;
    private readonly string _description;
    private string _fullDescription;
    private readonly FtbClientCatalogVersion? _ftbFallbackVersion;
    private readonly string _metricLocalizationKey;

    public ClientModpackProjectItemViewModel(ModrinthClientModpackProject project)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        SourceId = "modrinth";
        SourceLabel = "MODRINTH";
        ProjectId = project.ProjectId;
        Title = project.Title;
        _description = project.Description;
        _fullDescription = string.IsNullOrWhiteSpace(project.FullDescription)
            ? project.Description
            : project.FullDescription;
        _author = project.Author;
        Downloads = project.Downloads;
        _metricLocalizationKey = "client.vm.catalog.downloads";
        UpdatedAt = project.DateModified;
        GameVersions = project.GameVersions;
        Categories = project.Categories;
        IconUri = project.IconUri;
        PreviewImageUri = project.FeaturedImageUri;
        SubscribeToCultureChanges();
    }

    public ClientModpackProjectItemViewModel(FtbClientCatalogProject project)
    {
        FtbProject = project ?? throw new ArgumentNullException(nameof(project));
        SourceId = "ftb";
        SourceLabel = "FTB";
        ProjectId = project.ProjectId;
        Title = project.Title;
        _description = project.Description;
        _fullDescription = project.Description;
        _ftbFallbackVersion = string.IsNullOrWhiteSpace(project.Description)
            ? project.StableVersions.FirstOrDefault()
            : null;
        _author = "Feed The Beast";
        Downloads = project.Installs;
        _metricLocalizationKey = "client.vm.catalog.installs";
        UpdatedAt = project.UpdatedAt;
        GameVersions = project.StableVersions
            .Select(static version => version.GameVersion)
            .Where(static version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Categories = [];
        IconUri = project.IconUri;
        PreviewImageUri = project.PreviewImageUri;
        SubscribeToCultureChanges();
    }

    public ClientModpackProjectItemViewModel(OnlineModpackSearchResult project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Provider != OnlineModpackProvider.CurseForge)
        {
            throw new ArgumentException(
                "The provider-neutral client catalogue constructor accepts CurseForge projects only.",
                nameof(project));
        }

        CurseForgeProject = project;
        SourceId = "curseforge";
        SourceLabel = "CURSEFORGE";
        ProjectId = project.ProjectId;
        Title = project.Name;
        _description = project.Summary;
        _fullDescription = project.Summary;
        _author = project.Authors;
        Downloads = Math.Max(0, project.DownloadCount ?? 0);
        _metricLocalizationKey = "client.vm.catalog.downloads";
        UpdatedAt = project.UpdatedAtUtc ?? DateTimeOffset.MinValue;
        GameVersions = [];
        Categories = [];
        IconUri = project.IconUri;
        PreviewImageUri = project.PreviewImageUri;
        SubscribeToCultureChanges();
    }

    public ModrinthClientModpackProject? Project { get; }

    public FtbClientCatalogProject? FtbProject { get; }

    public OnlineModpackSearchResult? CurseForgeProject { get; }

    public string SourceId { get; }

    public string SourceLabel { get; }

    public string ProjectId { get; }

    public string Title { get; }

    public string Description
    {
        get
        {
            if (_ftbFallbackVersion is null)
            {
                return _description;
            }

            var gameVersion = string.IsNullOrWhiteSpace(_ftbFallbackVersion.GameVersion)
                ? L("client.vm.catalog.ftb.unknownGameVersion")
                : _ftbFallbackVersion.GameVersion;
            var loader = string.IsNullOrWhiteSpace(_ftbFallbackVersion.LoaderName)
                ? L("client.vm.loader.unknown")
                : string.IsNullOrWhiteSpace(_ftbFallbackVersion.LoaderVersion)
                    ? _ftbFallbackVersion.LoaderName
                    : $"{_ftbFallbackVersion.LoaderName} {_ftbFallbackVersion.LoaderVersion}";
            return L("client.vm.catalog.ftb.fallbackDescription", gameVersion, loader);
        }
    }

    public string FullDescription => string.IsNullOrWhiteSpace(_fullDescription)
        ? Description
        : _fullDescription;

    public string AuthorText => L("client.vm.catalog.author", _author);

    public long Downloads { get; }

    public DateTimeOffset UpdatedAt { get; }

    public IReadOnlyList<string> GameVersions { get; }

    public IReadOnlyList<string> Categories { get; }

    public Uri? IconUri { get; }

    public Uri? PreviewImageUri { get; }

    public Uri? ProjectPageUri => CurseForgeProject?.ProjectPageUri;

    public bool RequiresCurseForgeManualDownload =>
        CurseForgeProject?.AllowsThirdPartyDistribution is false;

    public string DownloadText => Downloads switch
    {
        >= 1_000_000 => L(_metricLocalizationKey, $"{Downloads / 1_000_000d:0.##}M"),
        >= 1_000 => L(_metricLocalizationKey, $"{Downloads / 1_000d:0.#}K"),
        _ => L(_metricLocalizationKey, Downloads.ToString("N0", LocalizationService.Current.Culture)),
    };

    public string UpdatedText => UpdatedAt <= DateTimeOffset.MinValue
        ? L("client.vm.catalog.updatedUnavailable")
        : L("client.vm.catalog.updated", UpdatedAt.ToLocalTime());

    public string GameVersionText => GameVersions
        .Select(value => (Value: value, Parsed: ParseStableVersion(value)))
        .OrderByDescending(item => item.Parsed)
        .ThenByDescending(item => item.Value, StringComparer.Ordinal)
        .Select(item => item.Value)
        .FirstOrDefault() ?? (FtbProject is null
            ? L("client.vm.catalog.multiVersion")
            : L("client.vm.catalog.ftb.unknownGameVersion"));

    public bool HasGameVersionText => GameVersions.Any(static version =>
        !string.IsNullOrWhiteSpace(version));

    public string CategoryText => Categories.Count == 0
        ? L("client.vm.catalog.type")
        : string.Join(" · ", Categories.Take(3));

    public string? IconImagePath
    {
        get => _iconImagePath;
        private set
        {
            if (SetProperty(ref _iconImagePath, value))
            {
                OnPropertyChanged(nameof(CardImagePath));
            }
        }
    }

    public string? PreviewImagePath
    {
        get => _previewImagePath;
        private set
        {
            if (SetProperty(ref _previewImagePath, value))
            {
                OnPropertyChanged(nameof(CardImagePath));
            }
        }
    }

    public string? CardImagePath => PreviewImagePath ?? IconImagePath;

    public void SetCachedArtwork(string? iconImagePath, string? previewImagePath)
    {
        IconImagePath = iconImagePath;
        PreviewImagePath = previewImagePath;
    }

    internal void ApplyDetails(ModrinthClientModpackProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!string.Equals(ProjectId, project.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Catalog project details do not match the selected project.");
        }

        var fullDescription = string.IsNullOrWhiteSpace(project.FullDescription)
            ? project.Description
            : project.FullDescription;
        SetProperty(ref _fullDescription, fullDescription, nameof(FullDescription));
    }

    private static Version ParseStableVersion(string value)
        => Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0);

    private void SubscribeToCultureChanges() =>
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AuthorText));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(FullDescription));
        OnPropertyChanged(nameof(GameVersionText));
        OnPropertyChanged(nameof(CategoryText));
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);
}

public sealed class ClientCatalogVersionItemViewModel : ObservableObject
{
    public ClientCatalogVersionItemViewModel(
        ModrinthClientModpackVersion version,
        string? projectTitle = null)
    {
        ModrinthVersion = version ?? throw new ArgumentNullException(nameof(version));
        ProjectTitle = projectTitle;
        GameVersions = version.GameVersions;
        SubscribeToCultureChanges();
    }

    public ClientCatalogVersionItemViewModel(
        FtbClientCatalogVersion version,
        string? projectTitle = null)
    {
        FtbVersion = version ?? throw new ArgumentNullException(nameof(version));
        ProjectTitle = projectTitle;
        GameVersions = string.IsNullOrWhiteSpace(version.GameVersion)
            ? []
            : [version.GameVersion];
        SubscribeToCultureChanges();
    }

    public ClientCatalogVersionItemViewModel(
        OnlineModpackVersion version,
        string? projectTitle = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Provider != OnlineModpackProvider.CurseForge)
        {
            throw new ArgumentException(
                "The provider-neutral client version constructor accepts CurseForge versions only.",
                nameof(version));
        }

        CurseForgeVersion = version;
        ProjectTitle = projectTitle;
        GameVersions = string.IsNullOrWhiteSpace(version.MinecraftVersion)
            ? []
            : [version.MinecraftVersion];
        SubscribeToCultureChanges();
    }

    public ModrinthClientModpackVersion? ModrinthVersion { get; }

    public FtbClientCatalogVersion? FtbVersion { get; }

    public OnlineModpackVersion? CurseForgeVersion { get; }

    public string? ProjectTitle { get; }

    public string PackVersionDisplay
    {
        get
        {
            if (ModrinthVersion is not null)
            {
                return ClientCatalogVersionDisplayFormatter.FormatPackVersion(
                    ProjectTitle,
                    versionName: ModrinthVersion.Name,
                    versionNumber: ModrinthVersion.VersionNumber,
                    gameVersion: PreferredGameVersion);
            }

            if (CurseForgeVersion is { } curseForgeVersion)
            {
                return ClientCatalogVersionDisplayFormatter.FormatPackVersion(
                    ProjectTitle,
                    curseForgeVersion.VersionName,
                    versionNumber: null,
                    PreferredGameVersion);
            }

            var version = FtbVersion!;
            return ClientCatalogVersionDisplayFormatter.FormatPackVersion(
                ProjectTitle,
                version.Name,
                versionNumber: null,
                PreferredGameVersion);
        }
    }

    public string GameVersionDisplay => $"MC {(
        string.IsNullOrWhiteSpace(PreferredGameVersion)
            ? LocalizationService.Current.Get("client.vm.catalog.ftb.unknownGameVersion")
            : PreferredGameVersion)}";

    public string LoaderDisplay => ClientCatalogVersionDisplayFormatter.FormatLoader(RawLoader)
        ?? LocalizationService.Current.Get("client.vm.loader.unknown");

    public string Name => string.Join(
        " · ",
        new[] { PackVersionDisplay, GameVersionDisplay, LoaderDisplay }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));

    public IReadOnlyList<string> GameVersions { get; }

    private string? PreferredGameVersion => GameVersions.FirstOrDefault(static value =>
        !string.IsNullOrWhiteSpace(value));

    private string? RawLoader
    {
        get
        {
            if (ModrinthVersion is { } modrinthVersion)
            {
                return modrinthVersion.Loaders.FirstOrDefault(static value =>
                           !string.IsNullOrWhiteSpace(value))
                       ?? ClientCatalogVersionDisplayFormatter.InferLoader(
                           $"{modrinthVersion.Name} {modrinthVersion.VersionNumber}");
            }

            if (CurseForgeVersion is { } curseForgeVersion)
            {
                return string.IsNullOrWhiteSpace(curseForgeVersion.Loader)
                    ? ClientCatalogVersionDisplayFormatter.InferLoader(curseForgeVersion.VersionName)
                    : curseForgeVersion.Loader;
            }

            return string.IsNullOrWhiteSpace(FtbVersion!.LoaderName)
                ? ClientCatalogVersionDisplayFormatter.InferLoader(FtbVersion.Name)
                : FtbVersion.LoaderName;
        }
    }

    private void SubscribeToCultureChanges() =>
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(PackVersionDisplay));
        OnPropertyChanged(nameof(GameVersionDisplay));
        OnPropertyChanged(nameof(LoaderDisplay));
        OnPropertyChanged(nameof(Name));
    }
}

internal static partial class ClientCatalogVersionDisplayFormatter
{
    internal static string FormatPackVersion(
        string? projectTitle,
        string? versionName,
        string? versionNumber,
        string? gameVersion)
    {
        var title = NormalizeWhitespace(projectTitle);
        var displayVersion = RemovePackageExtension(NormalizeWhitespace(versionName));
        var fallbackVersion = RemovePackageExtension(NormalizeWhitespace(versionNumber));

        displayVersion = RemovePhrase(displayVersion, title);
        displayVersion = RemoveGameVersion(displayVersion, gameVersion);
        displayVersion = KnownLoaderRegex().Replace(displayVersion, " ");
        displayVersion = ReleaseLabelRegex().Replace(displayVersion, " ");
        displayVersion = DanglingConnectorRegex().Replace(displayVersion, " ");
        displayVersion = NormalizeSeparators(displayVersion);

        if (string.IsNullOrWhiteSpace(displayVersion))
        {
            displayVersion = fallbackVersion;
        }

        if (!string.IsNullOrWhiteSpace(fallbackVersion)
            && !ContainsPhrase(displayVersion, fallbackVersion))
        {
            displayVersion = NormalizeSeparators($"{displayVersion} {fallbackVersion}");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return string.IsNullOrWhiteSpace(displayVersion)
                ? NormalizeWhitespace(versionName)
                : displayVersion;
        }

        return string.IsNullOrWhiteSpace(displayVersion)
            ? title
            : $"{title} {displayVersion}";
    }

    internal static string? InferLoader(string? value)
    {
        var match = KnownLoaderRegex().Match(value ?? string.Empty);
        return match.Success ? match.Value : null;
    }

    internal static string? FormatLoader(string? value)
    {
        var loader = InferLoader(value);
        if (string.IsNullOrWhiteSpace(loader))
        {
            return null;
        }

        var collapsed = Regex.Replace(loader, "[-_\\s]", string.Empty);
        return collapsed.ToLowerInvariant() switch
        {
            "neoforge" => "NeoForge",
            "forge" => "Forge",
            "fabric" => "Fabric",
            "quilt" => "Quilt",
            _ => null,
        };
    }

    private static string RemovePackageExtension(string value) =>
        PackageExtensionRegex().Replace(value, string.Empty).Trim();

    private static string RemoveGameVersion(string value, string? gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return value;
        }

        var escapedVersion = Regex.Escape(gameVersion.Trim());
        return Regex.Replace(
            value,
            $@"(?<![A-Za-z0-9])(?:(?:Minecraft|MC)\s*)?{escapedVersion}(?![A-Za-z0-9])",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string RemovePhrase(string value, string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return value;
        }

        return Regex.Replace(
            value,
            Regex.Escape(phrase),
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool ContainsPhrase(string value, string phrase) =>
        value.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWhitespace(string? value) =>
        WhitespaceRegex().Replace(value?.Trim() ?? string.Empty, " ");

    private static string NormalizeSeparators(string value)
    {
        var normalized = SeparatorRegex().Replace(value, " ");
        normalized = WhitespaceRegex().Replace(normalized, " ");
        return normalized.Trim(' ', '-', '–', '—', '|', '·', '_', '.', ',');
    }

    [GeneratedRegex(@"\.(?:zip|mrpack|jar)(?=$|[^A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackageExtensionRegex();

    [GeneratedRegex(@"(?<![A-Za-z])(?:neo[-_\s]?forge|forge|fabric|quilt)(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnownLoaderRegex();

    [GeneratedRegex(@"(?<![A-Za-z])release(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseLabelRegex();

    [GeneratedRegex(@"\b(?:for|on)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DanglingConnectorRegex();

    [GeneratedRegex(@"\s*[-–—|·]+\s*")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

public sealed record ClientCatalogLoaderChoice(MinecraftClientLoader? Loader, string Name);

public sealed record ClientCatalogSortChoice(ModrinthClientModpackSort Sort, string Name);

public sealed record ClientCatalogCategoryChoice(string? Category, string Name);

public sealed record ClientCatalogGameVersionChoice(string? Version, string Name);
