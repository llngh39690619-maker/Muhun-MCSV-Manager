using System.Collections.ObjectModel;
using System.Security;
using System.Windows.Media;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.ViewModels;

public enum OnlineModpackOperationState
{
    Idle,
    Searching,
    LoadingVersions,
    Installing,
    Cancelling
}

public enum OnlineModpackBrowseMode
{
    Featured,
    Search
}

public sealed record OnlineModpackFilterChoice(string Key, string DisplayName);

public sealed record OnlineModpackSortChoice(OnlineModpackSort Sort, string DisplayName);

public enum OnlineModpackArtworkState
{
    Loading,
    Ready,
    Unavailable
}

/// <summary>
/// Presentation-only wrapper for one provider result. Remote media URIs are intentionally not
/// exposed as an Image source; <see cref="ArtworkPath"/> is populated only by the bounded local
/// artwork cache.
/// </summary>
public sealed class OnlineModpackCatalogCardViewModel : ObservableObject
{
    private string? _artworkPath;
    private ImageSource? _artwork;
    private OnlineModpackArtworkState _artworkState = OnlineModpackArtworkState.Loading;
    private Stretch _artworkStretch = Stretch.UniformToFill;

    public OnlineModpackCatalogCardViewModel(OnlineModpackSearchResult project)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
    }

    public OnlineModpackSearchResult Project { get; }

    public string Name => Project.Name;

    public string Summary => Project.Summary;

    public string Authors => Project.Authors;

    public string SourceDisplay => Project.SourceDisplay;

    public string Initial => string.IsNullOrWhiteSpace(Project.Name)
        ? "?"
        : Project.Name.Trim()[..1].ToUpperInvariant();

    public string DownloadCountDisplay => Project.DownloadCount is { } count
        ? LocalizationService.Current.Get("online.downloadCount", count switch
        {
            >= 1_000_000 => $"{count / 1_000_000d:0.##}M",
            >= 1_000 => $"{count / 1_000d:0.#}K",
            _ => $"{count:N0}"
        })
        : LocalizationService.Current.Get("online.downloadCountUnavailable");

    public string UpdatedDisplay => Project.UpdatedAtUtc is { } updated
        ? LocalizationService.Current.Get(
            "online.updatedDate",
            updated.ToLocalTime().ToString("yyyy-MM-dd"))
        : LocalizationService.Current.Get("online.updatedDateUnavailable");

    public string? ArtworkPath
    {
        get => _artworkPath;
        internal set => SetProperty(ref _artworkPath, value);
    }

    public ImageSource? Artwork
    {
        get => _artwork;
        internal set
        {
            if (SetProperty(ref _artwork, value))
            {
                OnPropertyChanged(nameof(HasArtwork));
            }
        }
    }

    public bool HasArtwork => Artwork is not null;

    public OnlineModpackArtworkState ArtworkState
    {
        get => _artworkState;
        private set
        {
            if (SetProperty(ref _artworkState, value))
            {
                OnPropertyChanged(nameof(ArtworkStatusText));
            }
        }
    }

    public string ArtworkStatusText => ArtworkState switch
    {
        OnlineModpackArtworkState.Loading => LocalizationService.Current.Get("online.artwork.loading"),
        OnlineModpackArtworkState.Unavailable => LocalizationService.Current.Get("online.artwork.fallback"),
        _ => string.Empty
    };

    public Stretch ArtworkStretch
    {
        get => _artworkStretch;
        private set => SetProperty(ref _artworkStretch, value);
    }

    internal void SetArtwork(string localPath, ImageSource artwork, bool isIconFallback)
    {
        ArtworkPath = localPath;
        ArtworkStretch = isIconFallback ? Stretch.Uniform : Stretch.UniformToFill;
        Artwork = artwork;
        ArtworkState = OnlineModpackArtworkState.Ready;
    }

    internal void MarkArtworkUnavailable()
    {
        if (ArtworkState != OnlineModpackArtworkState.Ready)
        {
            ArtworkState = OnlineModpackArtworkState.Unavailable;
        }
    }
}

public sealed class OnlineModpackViewModel : ObservableObject, IDisposable
{
    private const int MaximumArtworkAttemptsPerCard = 3;
    private readonly IOnlineModpackWorkflow _workflow;
    private readonly IOnlineModpackArtworkCache? _artworkCache;
    private readonly IOnlineModpackArtworkDecoder _artworkDecoder;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _artworkCancellation;
    private long _operationGeneration;
    private OnlineModpackProviderChoice _selectedProvider;
    private OnlineModpackSearchResult? _selectedResult;
    private OnlineModpackVersion? _selectedVersion;
    private OnlineModpackOperationState _operationState;
    private OnlineModpackBrowseMode _browseMode = OnlineModpackBrowseMode.Featured;
    private string _searchQuery = string.Empty;
    private string _serverName = string.Empty;
    private string _stageText = L("online.status.initial");
    private string _progressDetailText = string.Empty;
    private string _resultsHeading = L("online.heading.featured", "FTB");
    private string _errorMessage = string.Empty;
    private double _progressPercentage;
    private bool _isProgressIndeterminate;
    private bool _isInstallOperation;
    private bool _disposed;
    private OnlineModpackSortChoice _selectedSort;
    private int _selectedResultLimit = 20;
    private OnlineModpackFilterChoice _selectedGameVersion;
    private OnlineModpackFilterChoice _selectedLoader;
    private OnlineModpackFilterChoice? _selectedCategory;

    public OnlineModpackViewModel(
        IOnlineModpackWorkflow workflow,
        IOnlineModpackArtworkDecoder? artworkDecoder = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        _workflow = workflow;
        _artworkCache = workflow.ArtworkCache;
        _artworkDecoder = artworkDecoder ?? new OnlineModpackArtworkDecoder();
        Providers =
        [
            new(OnlineModpackProvider.Ftb, "FTB", false),
            new(OnlineModpackProvider.Modrinth, "Modrinth", false),
            // CurseForge requires an official API credential, but the credential is supplied only
            // to the active dialog operation and is never retained by this view model.
            new(OnlineModpackProvider.CurseForge, "CurseForge", true)
        ];
        _selectedProvider = Providers[0];
        SortChoices =
        [
            new(OnlineModpackSort.Relevance, L("online.sort.relevance")),
            new(OnlineModpackSort.Downloads, L("online.sort.downloads")),
            new(OnlineModpackSort.RecentlyUpdated, L("online.sort.updated")),
            new(OnlineModpackSort.Newest, L("online.sort.newest"))
        ];
        ResultLimitChoices = [20, 40, 60, 100];
        _selectedSort = SortChoices[0];
        GameVersionChoices =
        [
            new(string.Empty, L("online.filter.allVersions")),
            new("26.2", "26.2"),
            new("26.1", "26.1"),
            new("1.21.11", "1.21.11"),
            new("1.21.1", "1.21.1"),
            new("1.20.1", "1.20.1"),
            new("1.19.2", "1.19.2"),
            new("1.18.2", "1.18.2"),
            new("1.16.5", "1.16.5"),
            new("1.12.2", "1.12.2")
        ];
        _selectedGameVersion = GameVersionChoices[0];
        LoaderChoices = [];
        CategoryChoices = [];
        _selectedLoader = new(string.Empty, L("online.filter.allLoaders"));
        ConfigureProviderFilters();
    }

    public event EventHandler? Installed;

    /// <summary>
    /// Raised after a provider, sort, game-version, loader, or category choice changes and the
    /// previous browse operation has been invalidated. The dialog owns the short debounce because
    /// it is also the only component allowed to access the operation-scoped CurseForge credential.
    /// </summary>
    public event EventHandler? BrowseCriteriaChanged;

    public IReadOnlyList<OnlineModpackProviderChoice> Providers { get; }

    public IReadOnlyList<OnlineModpackSortChoice> SortChoices { get; }

    public IReadOnlyList<int> ResultLimitChoices { get; }

    public IReadOnlyList<OnlineModpackFilterChoice> GameVersionChoices { get; }

    public ObservableCollection<OnlineModpackFilterChoice> LoaderChoices { get; }

    public ObservableCollection<OnlineModpackFilterChoice> CategoryChoices { get; }

    public ObservableCollection<OnlineModpackSearchResult> Results { get; } = [];

    public ObservableCollection<OnlineModpackCatalogCardViewModel> CatalogItems { get; } = [];

    public ObservableCollection<OnlineModpackVersion> Versions { get; } = [];

    public OnlineModpackProviderChoice SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Providers.Any(candidate => ReferenceEquals(candidate, value)))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    L("online.validation.provider"));
            }

            if (IsInstalling && !ReferenceEquals(_selectedProvider, value))
            {
                return;
            }

            if (!SetProperty(ref _selectedProvider, value))
            {
                return;
            }

            CancelAndInvalidateOperation();
            Results.Clear();
            CatalogItems.Clear();
            Versions.Clear();
            SelectedResult = null;
            SelectedVersion = null;
            ErrorMessage = string.Empty;
            StageText = BrowseMode == OnlineModpackBrowseMode.Search
                ? L("online.status.filtersChanged")
                : L("online.status.preparingFeatured", value.DisplayName);
            ResultsHeading = BrowseMode == OnlineModpackBrowseMode.Search
                ? L("online.heading.searchResults", value.DisplayName)
                : L("online.heading.featured", value.DisplayName);
            ConfigureProviderFilters();
            OnPropertyChanged(nameof(IsSelectedProviderAvailable));
            OnPropertyChanged(nameof(IsCurseForgeSelected));
            OnPropertyChanged(nameof(ProviderAvailabilityText));
            NotifyActionStateChanged();
            BrowseCriteriaChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsSelectedProviderAvailable => true;

    public bool IsCurseForgeSelected
        => SelectedProvider.Provider == OnlineModpackProvider.CurseForge;

    public string ProviderAvailabilityText => SelectedProvider.Provider switch
    {
        OnlineModpackProvider.Ftb => L("online.provider.ftbHint"),
        OnlineModpackProvider.CurseForge => L("online.provider.curseForgeTransientHint"),
        _ => L("online.provider.queryHint")
    };

    public bool HasCategories => CategoryChoices.Count > 1;

    public OnlineModpackSortChoice SelectedSort
    {
        get => _selectedSort;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SortChoices.Contains(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (SetProperty(ref _selectedSort, value))
            {
                MarkFiltersChanged();
            }
        }
    }

    public int SelectedResultLimit
    {
        get => _selectedResultLimit;
        set
        {
            if (!ResultLimitChoices.Contains(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (SetProperty(ref _selectedResultLimit, value))
            {
                OnPropertyChanged(nameof(ResultLimitText));
                MarkFiltersChanged();
            }
        }
    }

    public string ResultLimitText => L("online.maxResults", SelectedResultLimit);

    public OnlineModpackFilterChoice SelectedGameVersion
    {
        get => _selectedGameVersion;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!GameVersionChoices.Contains(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (SetProperty(ref _selectedGameVersion, value))
            {
                MarkFiltersChanged();
            }
        }
    }

    public OnlineModpackFilterChoice SelectedLoader
    {
        get => _selectedLoader;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!LoaderChoices.Contains(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (SetProperty(ref _selectedLoader, value))
            {
                MarkFiltersChanged();
            }
        }
    }

    public OnlineModpackFilterChoice? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is not null && !CategoryChoices.Contains(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (SetProperty(ref _selectedCategory, value))
            {
                MarkFiltersChanged();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value ?? string.Empty))
            {
                NotifyActionStateChanged();
            }
        }
    }

    public OnlineModpackSearchResult? SelectedResult
    {
        get => _selectedResult;
        private set
        {
            if (SetProperty(ref _selectedResult, value))
            {
                NotifyActionStateChanged();
            }
        }
    }

    public OnlineModpackVersion? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                OnPropertyChanged(nameof(SelectedVersionAvailability));
                NotifyActionStateChanged();
            }
        }
    }

    public string SelectedVersionAvailability => SelectedVersion is { HasOfficialServerPack: false }
        ? L("online.validation.noServerPack")
        : string.Empty;

    public string ServerName
    {
        get => _serverName;
        set
        {
            if (SetProperty(ref _serverName, value ?? string.Empty))
            {
                NotifyActionStateChanged();
            }
        }
    }

    public OnlineModpackOperationState OperationState
    {
        get => _operationState;
        private set
        {
            if (SetProperty(ref _operationState, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsInputEnabled));
                OnPropertyChanged(nameof(CancelButtonText));
                OnPropertyChanged(nameof(ShowProgressPercentage));
                NotifyActionStateChanged();
            }
        }
    }

    public bool IsBusy => OperationState != OnlineModpackOperationState.Idle;

    public OnlineModpackBrowseMode BrowseMode
    {
        get => _browseMode;
        private set => SetProperty(ref _browseMode, value);
    }

    public bool IsInstalling => _isInstallOperation;

    public bool CanChangeProvider => !IsInstalling;

    public bool IsInputEnabled => !IsBusy;

    public bool CanSearch => !IsBusy
        && IsSelectedProviderAvailable
        && !string.IsNullOrWhiteSpace(SearchQuery);

    public bool CanLoadFeatured => !IsBusy && IsSelectedProviderAvailable;

    public bool CanInstall => !IsBusy
        && SelectedResult is not null
        && SelectedVersion is { HasOfficialServerPack: true }
        && !string.IsNullOrWhiteSpace(ServerName);

    public string CancelButtonText => IsBusy ? L("online.cancelOperation") : L("common.close");

    public string StageText
    {
        get => _stageText;
        private set => SetProperty(ref _stageText, value);
    }

    public string ProgressDetailText
    {
        get => _progressDetailText;
        private set => SetProperty(ref _progressDetailText, value);
    }

    public string ResultsHeading
    {
        get => _resultsHeading;
        private set => SetProperty(ref _resultsHeading, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public double ProgressPercentage
    {
        get => _progressPercentage;
        private set => SetProperty(ref _progressPercentage, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set
        {
            if (SetProperty(ref _isProgressIndeterminate, value))
            {
                OnPropertyChanged(nameof(ShowProgressPercentage));
            }
        }
    }

    public bool ShowProgressPercentage => IsBusy && !IsProgressIndeterminate;

    public ServerInstance? InstalledServer { get; private set; }

    public async Task LoadFeaturedAsync(SecureString? transientApiKey)
    {
        ThrowIfDisposed();
        if (IsBusy)
        {
            return;
        }

        BrowseMode = OnlineModpackBrowseMode.Featured;
        var provider = SelectedProvider;
        CancelArtworkHydration();
        Results.Clear();
        CatalogItems.Clear();
        Versions.Clear();
        SelectedResult = null;
        SelectedVersion = null;
        ErrorMessage = string.Empty;
        ResultsHeading = L("online.heading.featured", provider.DisplayName);

        var operation = BeginOperation(
            OnlineModpackOperationState.Searching,
            L("online.status.loadingFeatured", provider.DisplayName),
            isInstall: false,
            indeterminate: true);

        try
        {
            var matches = await _workflow.BrowseAsync(
                BuildCurrentBrowseRequest(query: string.Empty),
                transientApiKey,
                operation.Token);
            if (!IsCurrent(operation.Generation))
            {
                return;
            }

            PublishResults(matches, provider.Provider);

            StageText = Results.Count == 0
                ? L("online.status.noFeatured", provider.DisplayName)
                : L("online.status.loadedFeatured", Results.Count, provider.DisplayName);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            SetCancelledMessage(operation.Generation, L("online.status.featuredCancelled"));
        }
        catch (Exception exception)
        {
            SetOperationError(operation.Generation, L("online.error.featuredFailed"), exception);
        }
        finally
        {
            CompleteOperation(operation);
        }
    }

    public async Task SearchAsync(SecureString? transientApiKey)
    {
        ThrowIfDisposed();
        if (IsBusy)
        {
            return;
        }

        var query = SearchQuery.Trim();
        if (query.Length == 0)
        {
            ErrorMessage = L("online.validation.searchQuery");
            return;
        }

        BrowseMode = OnlineModpackBrowseMode.Search;
        CancelArtworkHydration();
        Results.Clear();
        CatalogItems.Clear();
        Versions.Clear();
        SelectedResult = null;
        SelectedVersion = null;
        ErrorMessage = string.Empty;
        ResultsHeading = L("online.heading.searchResults", SelectedProvider.DisplayName);

        var operation = BeginOperation(
            OnlineModpackOperationState.Searching,
            L("online.status.searching", SelectedProvider.DisplayName),
            isInstall: false,
            indeterminate: true);

        try
        {
            var matches = await _workflow.BrowseAsync(
                BuildCurrentBrowseRequest(query),
                transientApiKey,
                operation.Token);

            if (!IsCurrent(operation.Generation))
            {
                return;
            }

            PublishResults(matches, SelectedProvider.Provider);

            StageText = Results.Count == 0
                ? L("online.status.noResults")
                : L("online.status.resultsFound", Results.Count);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            SetCancelledMessage(operation.Generation, L("online.status.searchCancelled"));
        }
        catch (Exception exception)
        {
            SetOperationError(operation.Generation, L("online.error.searchFailed"), exception);
        }
        finally
        {
            CompleteOperation(operation);
        }
    }

    /// <summary>
    /// Refreshes the catalog using the last explicitly selected Featured/Search mode. Search text is
    /// intentionally not used to infer the mode, so choosing Featured does not clear a typed query.
    /// </summary>
    public Task RefreshCurrentCatalogAsync(SecureString? transientApiKey)
    {
        ThrowIfDisposed();
        return BrowseMode == OnlineModpackBrowseMode.Search
            ? SearchAsync(transientApiKey)
            : LoadFeaturedAsync(transientApiKey);
    }

    public async Task SelectResultAsync(
        OnlineModpackSearchResult? result,
        SecureString? transientApiKey)
    {
        ThrowIfDisposed();
        if (result is null || IsBusy || !Results.Contains(result))
        {
            return;
        }

        SelectedResult = result;
        SelectedVersion = null;
        Versions.Clear();
        ServerName = result.Name;
        ErrorMessage = string.Empty;

        var operation = BeginOperation(
            OnlineModpackOperationState.LoadingVersions,
            L("online.status.loadingVersions", result.Name),
            isInstall: false,
            indeterminate: true);

        try
        {
            var versions = await _workflow.GetVersionsAsync(result, transientApiKey, operation.Token);
            if (!IsCurrent(operation.Generation))
            {
                return;
            }

            foreach (var version in versions.Where(item =>
                         item.Provider == result.Provider
                         && item.ProjectId == result.ProjectId
                         && MatchesSelectedVersionFilters(item)))
            {
                Versions.Add(version);
            }

            SelectedVersion = Versions.FirstOrDefault(item => item.HasOfficialServerPack)
                ?? Versions.FirstOrDefault();
            StageText = Versions.Count == 0
                ? L("online.status.noVersions")
                : L("online.status.loadedVersions", Versions.Count);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            SetCancelledMessage(operation.Generation, L("online.status.versionsCancelled"));
        }
        catch (Exception exception)
        {
            SetOperationError(operation.Generation, L("online.error.versionsFailed"), exception);
        }
        finally
        {
            CompleteOperation(operation);
        }
    }

    public async Task InstallAsync(SecureString? transientApiKey)
    {
        ThrowIfDisposed();
        if (!TryBuildInstallRequest(out var request))
        {
            return;
        }

        ErrorMessage = string.Empty;
        InstalledServer = null;
        var operation = BeginOperation(
            OnlineModpackOperationState.Installing,
            L("online.stage.preparing"),
            isInstall: true,
            indeterminate: true);
        var progress = new Progress<OnlineModpackInstallProgress>(value =>
            ApplyInstallProgress(operation.Generation, value));

        try
        {
            var installed = await _workflow.InstallAsync(
                request,
                transientApiKey,
                progress,
                operation.Token);
            ArgumentNullException.ThrowIfNull(installed);

            if (!IsCurrent(operation.Generation))
            {
                return;
            }

            InstalledServer = installed;
            ProgressPercentage = 100;
            IsProgressIndeterminate = false;
            ProgressDetailText = string.Empty;
            StageText = L("online.status.installed", installed.Name);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            SetCancelledMessage(operation.Generation, L("online.status.installCancelled"));
        }
        catch (Exception exception)
        {
            SetOperationError(operation.Generation, L("online.error.installFailed"), exception);
        }
        finally
        {
            CompleteOperation(operation);
        }

        if (InstalledServer is not null && IsCurrent(operation.Generation))
        {
            Installed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal bool TryBuildInstallRequest(out OnlineModpackInstallRequest request)
    {
        ThrowIfDisposed();
        request = null!;
        if (IsBusy)
        {
            return false;
        }

        if (SelectedResult is null || SelectedVersion is null)
        {
            ErrorMessage = L("online.validation.selectPackVersion");
            return false;
        }

        if (SelectedVersion.Provider != SelectedResult.Provider
            || !SelectedVersion.ProjectId.Equals(SelectedResult.ProjectId, StringComparison.Ordinal))
        {
            ErrorMessage = L("online.validation.versionMismatch");
            return false;
        }

        if (!SelectedVersion.HasOfficialServerPack)
        {
            ErrorMessage = L("online.validation.noServerPack");
            return false;
        }

        var serverName = ServerName.Trim();
        if (serverName.Length == 0)
        {
            ErrorMessage = L("core.validation.serverName");
            return false;
        }

        ErrorMessage = string.Empty;
        request = new OnlineModpackInstallRequest(SelectedResult, SelectedVersion, serverName);
        return true;
    }

    internal void SetBackgroundSubmissionError(string message)
    {
        ThrowIfDisposed();
        ErrorMessage = string.IsNullOrWhiteSpace(message)
            ? L("jobs.error.addModpack")
            : message.Trim();
    }

    internal void SetTransientCredentialRequired()
    {
        ThrowIfDisposed();
        if (!IsCurseForgeSelected)
        {
            return;
        }

        ErrorMessage = string.Empty;
        ProgressDetailText = string.Empty;
        StageText = L("online.status.curseForgeKeyRequired");
    }

    public void CancelCurrentOperation()
    {
        if (!IsBusy || _operationCancellation is null)
        {
            return;
        }

        OperationState = OnlineModpackOperationState.Cancelling;
        StageText = _isInstallOperation
            ? L("online.status.cancellingInstall")
            : L("online.status.cancellingOperation");
        _operationCancellation.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelAndInvalidateOperation();
        CancelArtworkHydration();
    }

    private OperationContext BeginOperation(
        OnlineModpackOperationState state,
        string stageText,
        bool isInstall,
        bool indeterminate)
    {
        var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        var generation = ++_operationGeneration;
        _isInstallOperation = isInstall;
        OnPropertyChanged(nameof(IsInstalling));
        OnPropertyChanged(nameof(CanChangeProvider));
        ProgressPercentage = 0;
        IsProgressIndeterminate = indeterminate;
        ProgressDetailText = string.Empty;
        StageText = stageText;
        OperationState = state;
        return new OperationContext(generation, cancellation);
    }

    private void CompleteOperation(OperationContext operation)
    {
        operation.Cancellation.Dispose();
        if (!IsCurrent(operation.Generation))
        {
            return;
        }

        _operationCancellation = null;
        _isInstallOperation = false;
        OnPropertyChanged(nameof(IsInstalling));
        OnPropertyChanged(nameof(CanChangeProvider));
        OperationState = OnlineModpackOperationState.Idle;
    }

    private void CancelAndInvalidateOperation()
    {
        ++_operationGeneration;
        CancelArtworkHydration();
        var cancellation = _operationCancellation;
        _operationCancellation = null;
        cancellation?.Cancel();
        _isInstallOperation = false;
        OnPropertyChanged(nameof(IsInstalling));
        OnPropertyChanged(nameof(CanChangeProvider));
        OperationState = OnlineModpackOperationState.Idle;
    }

    private bool IsCurrent(long generation) => generation == _operationGeneration && !_disposed;

    private void ApplyInstallProgress(long generation, OnlineModpackInstallProgress progress)
    {
        if (!IsCurrent(generation))
        {
            return;
        }

        StageText = string.IsNullOrWhiteSpace(progress.Message)
            ? GetStageDisplay(progress.Stage)
            : progress.Message;
        ProgressDetailText = progress.Detail?.Trim() ?? string.Empty;
        IsProgressIndeterminate = progress.Percentage is null;
        if (progress.Percentage is { } percentage)
        {
            ProgressPercentage = Math.Clamp(percentage, 0, 100);
        }
    }

    private void SetCancelledMessage(long generation, string message)
    {
        if (IsCurrent(generation))
        {
            ErrorMessage = string.Empty;
            ProgressDetailText = string.Empty;
            StageText = message;
        }
    }

    private void SetOperationError(long generation, string prefix, Exception exception)
    {
        if (!IsCurrent(generation))
        {
            return;
        }

        ErrorMessage = L("common.errorWithDetail", prefix, GetSafeErrorMessage(exception));
        ProgressDetailText = string.Empty;
        StageText = L("common.operationFailed", prefix);
    }

    private static string GetSafeErrorMessage(Exception exception)
    {
        var message = exception.Message?.Trim();
        return string.IsNullOrWhiteSpace(message) ? L("common.unexpectedError") : message;
    }

    private static string GetStageDisplay(OnlineModpackInstallStage stage) => stage switch
    {
        OnlineModpackInstallStage.Preparing => L("online.stage.preparing"),
        OnlineModpackInstallStage.ResolvingMetadata => L("online.stage.metadata"),
        OnlineModpackInstallStage.Downloading => L("online.stage.downloading"),
        OnlineModpackInstallStage.Verifying => L("online.stage.verifying"),
        OnlineModpackInstallStage.Extracting => L("online.stage.extracting"),
        OnlineModpackInstallStage.InstallingLoader => L("online.stage.loader"),
        OnlineModpackInstallStage.DetectingServer => L("online.stage.detectingServer"),
        OnlineModpackInstallStage.Finalizing => L("online.stage.finalizing"),
        _ => L("online.stage.installing")
    };

    private void NotifyActionStateChanged()
    {
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(CanLoadFeatured));
        OnPropertyChanged(nameof(CanInstall));
    }

    internal OnlineModpackBrowseRequest BuildCurrentBrowseRequest(string? query = null)
    {
        ThrowIfDisposed();
        var request = new OnlineModpackBrowseRequest(
            SelectedProvider.Provider,
            Query: (query ?? SearchQuery).Trim(),
            Sort: SelectedSort.Sort,
            GameVersion: NullIfEmpty(SelectedGameVersion.Key),
            Loader: NullIfEmpty(SelectedLoader.Key),
            SourceCategory: NullIfEmpty(SelectedCategory?.Key),
            Offset: 0,
            Limit: SelectedResultLimit);
        request.Validate();
        return request;
    }

    private void PublishResults(
        IEnumerable<OnlineModpackSearchResult> matches,
        OnlineModpackProvider expectedProvider)
    {
        CancelArtworkHydration();
        _artworkCancellation = new CancellationTokenSource();
        foreach (var match in matches.Where(item => item.Provider == expectedProvider))
        {
            Results.Add(match);
            var card = new OnlineModpackCatalogCardViewModel(match);
            CatalogItems.Add(card);
            if (_artworkCache is not null)
            {
                _ = HydrateArtworkAsync(
                    card,
                    _artworkCancellation.Token);
            }
            else
            {
                card.MarkArtworkUnavailable();
            }
        }
    }

    private async Task HydrateArtworkAsync(
        OnlineModpackCatalogCardViewModel card,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var attempt in BuildArtworkAttempts(card.Project))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var localPath = await _artworkCache!.GetOrCacheAsync(
                    card.Project.Provider,
                    attempt.Uri,
                    cancellationToken);
                var artwork = await _artworkDecoder.DecodePreviewAsync(localPath, cancellationToken);
                if (localPath is null || artwork is null)
                {
                    continue;
                }

                if (!_disposed
                    && !cancellationToken.IsCancellationRequested
                    && CatalogItems.Contains(card))
                {
                    card.SetArtwork(localPath, artwork, attempt.IsIconFallback);
                }

                return;
            }

            if (!_disposed
                && !cancellationToken.IsCancellationRequested
                && CatalogItems.Contains(card))
            {
                card.MarkArtworkUnavailable();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or NotSupportedException
                                           or ObjectDisposedException)
        {
            if (!_disposed
                && !cancellationToken.IsCancellationRequested
                && CatalogItems.Contains(card))
            {
                card.MarkArtworkUnavailable();
            }
        }
    }

    private static IReadOnlyList<ArtworkAttempt> BuildArtworkAttempts(OnlineModpackSearchResult project)
    {
        var attempts = new List<ArtworkAttempt>(MaximumArtworkAttemptsPerCard);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var uri in project.PreviewImageUriCandidates.Take(2))
        {
            Add(uri);
        }

        Add(project.IconUri);
        foreach (var uri in project.PreviewImageUriCandidates.Concat(project.IconUriCandidates))
        {
            Add(uri);
        }

        return attempts;

        void Add(Uri? uri)
        {
            if (uri is null
                || attempts.Count >= MaximumArtworkAttemptsPerCard
                || !seen.Add(uri.AbsoluteUri))
            {
                return;
            }

            attempts.Add(new ArtworkAttempt(uri, Uri.Equals(uri, project.IconUri)));
        }
    }

    private void CancelArtworkHydration()
    {
        var cancellation = Interlocked.Exchange(ref _artworkCancellation, null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void ConfigureProviderFilters()
    {
        LoaderChoices.Clear();
        LoaderChoices.Add(new(string.Empty, L("online.filter.allLoaders")));
        foreach (var loader in SelectedProvider.Provider switch
                 {
                     OnlineModpackProvider.Ftb => new[]
                     {
                         new OnlineModpackFilterChoice("forge", "Forge"),
                         new OnlineModpackFilterChoice("neoforge", "NeoForge"),
                         new OnlineModpackFilterChoice("fabric", "Fabric")
                     },
                     OnlineModpackProvider.Modrinth => new[]
                     {
                         new OnlineModpackFilterChoice("fabric", "Fabric"),
                         new OnlineModpackFilterChoice("forge", "Forge"),
                         new OnlineModpackFilterChoice("neoforge", "NeoForge"),
                         new OnlineModpackFilterChoice("quilt", "Quilt")
                     },
                     OnlineModpackProvider.CurseForge => new[]
                     {
                         new OnlineModpackFilterChoice("fabric", "Fabric"),
                         new OnlineModpackFilterChoice("forge", "Forge"),
                         new OnlineModpackFilterChoice("neoforge", "NeoForge"),
                         new OnlineModpackFilterChoice("quilt", "Quilt")
                     },
                     _ => []
                 })
        {
            LoaderChoices.Add(loader);
        }

        CategoryChoices.Clear();
        CategoryChoices.Add(new(string.Empty, L("online.filter.allCategories")));
        if (SelectedProvider.Provider == OnlineModpackProvider.Modrinth)
        {
            foreach (var category in new[]
                     {
                         new OnlineModpackFilterChoice("adventure", L("online.category.adventure")),
                         new OnlineModpackFilterChoice("challenging", L("online.category.challenging")),
                         new OnlineModpackFilterChoice("combat", L("online.category.combat")),
                         new OnlineModpackFilterChoice("kitchen-sink", L("online.category.kitchenSink")),
                         new OnlineModpackFilterChoice("lightweight", L("online.category.lightweight")),
                         new OnlineModpackFilterChoice("magic", L("online.category.magic")),
                         new OnlineModpackFilterChoice("multiplayer", L("online.category.multiplayer")),
                         new OnlineModpackFilterChoice("optimization", L("online.category.optimization")),
                         new OnlineModpackFilterChoice("quests", L("online.category.quests")),
                         new OnlineModpackFilterChoice("technology", L("online.category.technology"))
                     })
            {
                CategoryChoices.Add(category);
            }
        }

        _selectedLoader = LoaderChoices[0];
        _selectedCategory = CategoryChoices[0];
        OnPropertyChanged(nameof(SelectedLoader));
        OnPropertyChanged(nameof(SelectedCategory));
        OnPropertyChanged(nameof(HasCategories));
    }

    private void MarkFiltersChanged()
    {
        if (_disposed)
        {
            return;
        }

        CancelAndInvalidateOperation();
        Results.Clear();
        CatalogItems.Clear();
        Versions.Clear();
        SelectedResult = null;
        SelectedVersion = null;
        ErrorMessage = string.Empty;
        StageText = IsSelectedProviderAvailable
            ? L("online.status.filtersChanged")
            : ProviderAvailabilityText;
        NotifyActionStateChanged();
        BrowseCriteriaChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);

    private bool MatchesSelectedVersionFilters(OnlineModpackVersion version)
    {
        var gameVersion = NullIfEmpty(SelectedGameVersion.Key);
        if (gameVersion is not null
            && !version.MinecraftVersion.Equals(gameVersion, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var loader = NullIfEmpty(SelectedLoader.Key);
        if (loader is null)
        {
            return true;
        }

        var normalizedExpected = NormalizeLoader(loader);
        return NormalizeLoader(version.Loader).Contains(
            normalizedExpected,
            StringComparison.Ordinal);
    }

    private static string NormalizeLoader(string value)
        => new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()) switch
        {
            "neoforge" => "neoforge",
            var normalized => normalized
        };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct OperationContext(
        long Generation,
        CancellationTokenSource Cancellation)
    {
        public CancellationToken Token => Cancellation.Token;
    }

    private readonly record struct ArtworkAttempt(Uri Uri, bool IsIconFallback);
}
