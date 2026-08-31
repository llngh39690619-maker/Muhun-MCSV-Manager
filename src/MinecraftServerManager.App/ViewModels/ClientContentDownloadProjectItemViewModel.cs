using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.ViewModels;

public sealed class ClientContentDownloadProjectItemViewModel : ObservableObject
{
    private ModrinthClientContentProject _project;

    public ClientContentDownloadProjectItemViewModel(
        ModrinthClientContentProject project,
        string compatibilityText)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        CompatibilityText = compatibilityText ?? throw new ArgumentNullException(nameof(compatibilityText));
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

    public Uri? IconUri => Project.IconUri;

    public Uri? IconImagePath => IconUri;

    public long Downloads => Project.Downloads;

    public DateTimeOffset DateModified => Project.DateModified;

    public IReadOnlyList<string> GameVersions => Project.GameVersions;

    public IReadOnlyList<string> Loaders => Project.Loaders;

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
        OnPropertyChanged(nameof(IconUri));
        OnPropertyChanged(nameof(IconImagePath));
        OnPropertyChanged(nameof(Downloads));
        OnPropertyChanged(nameof(DateModified));
        OnPropertyChanged(nameof(GameVersions));
        OnPropertyChanged(nameof(Loaders));
        OnPropertyChanged(nameof(ProjectPageUri));
    }
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

public sealed class ClientContentDownloadVersionItemViewModel(
    ModrinthClientContentVersion version)
{
    public ModrinthClientContentVersion Version { get; } =
        version ?? throw new ArgumentNullException(nameof(version));

    public string VersionId => Version.VersionId;

    public string Name => Version.Name;

    public string VersionNumber => Version.VersionNumber;

    public string DisplayName => string.Equals(Name, VersionNumber, StringComparison.Ordinal)
        ? Name
        : $"{Name} · {VersionNumber}";

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
