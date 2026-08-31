using System.Windows;
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

    public ModrinthClientModpackProject? Project { get; }

    public FtbClientCatalogProject? FtbProject { get; }

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
    public ClientCatalogVersionItemViewModel(ModrinthClientModpackVersion version)
    {
        ModrinthVersion = version ?? throw new ArgumentNullException(nameof(version));
        GameVersions = version.GameVersions;
        SubscribeToCultureChanges();
    }

    public ClientCatalogVersionItemViewModel(FtbClientCatalogVersion version)
    {
        FtbVersion = version ?? throw new ArgumentNullException(nameof(version));
        GameVersions = string.IsNullOrWhiteSpace(version.GameVersion)
            ? []
            : [version.GameVersion];
        SubscribeToCultureChanges();
    }

    public ModrinthClientModpackVersion? ModrinthVersion { get; }

    public FtbClientCatalogVersion? FtbVersion { get; }

    public string Name
    {
        get
        {
            if (ModrinthVersion is not null)
            {
                return ModrinthVersion.Name;
            }

            var version = FtbVersion!;
            var loader = string.IsNullOrWhiteSpace(version.LoaderName)
                ? LocalizationService.Current.Get("client.vm.loader.unknown")
                : string.IsNullOrWhiteSpace(version.LoaderVersion)
                    ? version.LoaderName
                    : $"{version.LoaderName} {version.LoaderVersion}";
            var gameVersion = string.IsNullOrWhiteSpace(version.GameVersion)
                ? LocalizationService.Current.Get("client.vm.catalog.ftb.unknownGameVersion")
                : version.GameVersion;
            return $"{version.Name} · Minecraft {gameVersion} · {loader}";
        }
    }

    public IReadOnlyList<string> GameVersions { get; }

    private void SubscribeToCultureChanges() =>
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);

    private void OnCultureChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(Name));
}

public sealed record ClientCatalogLoaderChoice(MinecraftClientLoader? Loader, string Name);

public sealed record ClientCatalogSortChoice(ModrinthClientModpackSort Sort, string Name);

public sealed record ClientCatalogCategoryChoice(string? Category, string Name);

public sealed record ClientCatalogGameVersionChoice(string? Version, string Name);
