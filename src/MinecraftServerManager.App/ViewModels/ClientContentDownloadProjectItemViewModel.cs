using System.Windows;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.ViewModels;

public sealed class ClientContentDownloadProjectItemViewModel : ObservableObject
{
    private ModrinthClientContentProject _project;
    private readonly string _targetGameVersion;

    public ClientContentDownloadProjectItemViewModel(
        ModrinthClientContentProject project,
        string compatibilityText,
        string targetGameVersion)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        CompatibilityText = compatibilityText ?? throw new ArgumentNullException(nameof(compatibilityText));
        _targetGameVersion = targetGameVersion ?? throw new ArgumentNullException(nameof(targetGameVersion));
        SubscribeToCultureChanges();
    }

    public ModrinthClientContentProject Project => _project;

    public string ProjectId => Project.ProjectId;

    public string Title => Project.Title;

    public string Summary => Project.Description;

    public string FullDescription => string.IsNullOrWhiteSpace(Project.FullDescription)
        ? Summary
        : Project.FullDescription;

    public string DetailsText => string.IsNullOrWhiteSpace(FullDescription)
        ? Summary
        : FullDescription;

    public string Author => Project.Author;

    public string AuthorText => Author;

    public string LocalizedAuthorText => L("client.vm.catalog.author", Author);

    public Uri? IconUri => Project.IconUri;

    public Uri? IconImagePath => IconUri;

    public long Downloads => Project.Downloads;

    public string DownloadText => Downloads switch
    {
        >= 1_000_000 => L("client.vm.catalog.downloads", $"{Downloads / 1_000_000d:0.##}M"),
        >= 1_000 => L("client.vm.catalog.downloads", $"{Downloads / 1_000d:0.#}K"),
        _ => L("client.vm.catalog.downloads", Downloads.ToString("N0", LocalizationService.Current.Culture)),
    };

    public DateTimeOffset DateModified => Project.DateModified;

    public string UpdatedText => DateModified <= DateTimeOffset.MinValue
        ? L("client.vm.catalog.updatedUnavailable")
        : L("client.vm.catalog.updated", DateModified.ToLocalTime());

    public IReadOnlyList<string> GameVersions => Project.GameVersions;

    public IReadOnlyList<string> Loaders => Project.Loaders;

    public string GameVersionText => string.IsNullOrWhiteSpace(_targetGameVersion)
        ? GameVersions.FirstOrDefault() ?? L("client.vm.catalog.multiVersion")
        : _targetGameVersion;

    public string CompatibilityDetailText
    {
        get
        {
            if (Project.Kind is MinecraftClientContentKind.ResourcePack or MinecraftClientContentKind.ShaderPack)
            {
                return $"MC {GameVersionText}";
            }

            var loaderText = string.Join(" / ", Loaders.Take(3));
            return string.IsNullOrWhiteSpace(loaderText)
                ? $"MC {GameVersionText}"
                : $"MC {GameVersionText} · {loaderText}";
        }
    }

    public string CompatibilityText { get; }

    public Uri ProjectPageUri => Project.ProjectPageUri;

    public void ApplyDetails(ModrinthClientContentProject details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (!string.Equals(ProjectId, details.ProjectId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Project details do not match the selected project.", nameof(details));
        }

        var preservedAuthor = string.IsNullOrWhiteSpace(details.Author)
            ? Project.Author
            : details.Author;
        _project = details with { Author = preservedAuthor };
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(FullDescription));
        OnPropertyChanged(nameof(DetailsText));
        OnPropertyChanged(nameof(Author));
        OnPropertyChanged(nameof(AuthorText));
        OnPropertyChanged(nameof(LocalizedAuthorText));
        OnPropertyChanged(nameof(IconUri));
        OnPropertyChanged(nameof(IconImagePath));
        OnPropertyChanged(nameof(Downloads));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(DateModified));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(GameVersions));
        OnPropertyChanged(nameof(Loaders));
        OnPropertyChanged(nameof(CompatibilityDetailText));
        OnPropertyChanged(nameof(ProjectPageUri));
    }

    private void SubscribeToCultureChanges() =>
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LocalizedAuthorText));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(GameVersionText));
        OnPropertyChanged(nameof(CompatibilityDetailText));
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);
}

public sealed record ClientContentDownloadLoaderChoice(
    MinecraftClientLoader? Loader,
    string DisplayName);

public sealed record ClientContentDownloadCategoryChoice(
    string? Category,
    string DisplayName);

public sealed record ClientContentDownloadSortChoice(
    ModrinthClientContentSort Sort,
    string DisplayName);

public sealed class ClientContentDownloadVersionItemViewModel
{
    private readonly string _targetGameVersion;
    private readonly string? _preferredLoader;

    public ClientContentDownloadVersionItemViewModel(
        ModrinthClientContentVersion version,
        string? projectTitle = null,
        MinecraftClientContentKind kind = MinecraftClientContentKind.Mod,
        string? targetGameVersion = null,
        string? preferredLoader = null)
    {
        Version = version ?? throw new ArgumentNullException(nameof(version));
        ProjectTitle = projectTitle;
        Kind = kind;
        _targetGameVersion = targetGameVersion?.Trim() ?? string.Empty;
        _preferredLoader = preferredLoader;
    }

    public ModrinthClientContentVersion Version { get; }

    public string? ProjectTitle { get; }

    public MinecraftClientContentKind Kind { get; }

    public string VersionId => Version.VersionId;

    public string Name => Version.Name;

    public string VersionNumber => Version.VersionNumber;

    public string ContentVersionDisplay
    {
        get
        {
            var fromName = ClientCatalogVersionDisplayFormatter.FormatPackVersion(
                ProjectTitle,
                Name,
                versionNumber: null,
                SelectedGameVersion);
            if (string.IsNullOrWhiteSpace(ProjectTitle) ||
                !string.Equals(fromName, ProjectTitle.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return fromName;
            }

            return ClientCatalogVersionDisplayFormatter.FormatPackVersion(
                ProjectTitle,
                VersionNumber,
                versionNumber: null,
                SelectedGameVersion);
        }
    }

    public string GameVersionDisplay => $"MC {SelectedGameVersion}";

    public string LoaderDisplay
    {
        get
        {
            if (Kind is not MinecraftClientContentKind.Mod)
            {
                return string.Empty;
            }

            return ClientCatalogVersionDisplayFormatter.FormatLoader(_preferredLoader)
                   ?? Version.Loaders
                       .Select(ClientCatalogVersionDisplayFormatter.FormatLoader)
                       .FirstOrDefault(static loader => !string.IsNullOrWhiteSpace(loader))
                   ?? LocalizationService.Current.Get("client.vm.loader.unknown");
        }
    }

    public string DisplayName => string.Join(
        " · ",
        new[] { ContentVersionDisplay, GameVersionDisplay, LoaderDisplay }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));

    private string SelectedGameVersion => !string.IsNullOrWhiteSpace(_targetGameVersion)
        ? _targetGameVersion
        : Version.GameVersions.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
          ?? LocalizationService.Current.Get("client.vm.catalog.ftb.unknownGameVersion");

    public string CompatibilityText
    {
        get
        {
            var versions = string.Join(" / ", Version.GameVersions.Take(3));
            var loaders = string.Join(" / ", Version.Loaders.Take(4));
            return string.IsNullOrWhiteSpace(loaders)
                ? versions
                : $"{versions} · {loaders}";
        }
    }

    public DateTimeOffset DatePublished => Version.DatePublished;
}

public sealed record ClientContentDownloadDependencyItemViewModel(
    string ProjectId,
    string DisplayName,
    string VersionNumber)
{
    public string DisplayText => string.IsNullOrWhiteSpace(VersionNumber)
        ? DisplayName
        : $"{DisplayName} · {VersionNumber}";
}

public sealed record ClientContentDownloadFallbackItemViewModel(
    string DisplayName,
    string Message,
    Uri OpenUri);
