using System.Collections.ObjectModel;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.ViewModels;

public enum CoreServerCreationOperationState
{
    Idle,
    LoadingCores,
    LoadingVersions,
    Creating,
    Cancelling
}

public sealed class CoreServerCreationViewModel : ObservableObject, IDisposable
{
    private readonly ICoreServerCreationWorkflow _workflow;
    private readonly IIncrementalCoreServerCatalogWorkflow? _incrementalCatalogWorkflow;
    private readonly List<CoreServerVersion> _allVersions = [];
    private readonly Dictionary<string, IncrementalVersionState> _incrementalVersionStates =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _catalogRefreshCancellation;
    private Task? _catalogRefreshTask;
    private long _operationGeneration;
    private long _catalogGeneration;
    private CoreServerProduct? _selectedCore;
    private CoreServerVersion? _selectedVersion;
    private CoreServerCreationOperationState _operationState;
    private string _versionSearchQuery = string.Empty;
    private string _serverName = string.Empty;
    private string _stageText = L("core.status.preparingCatalog");
    private string _detailText = string.Empty;
    private string _errorMessage = string.Empty;
    private string _versionStateText = L("core.status.chooseCore");
    private double _progressPercentage;
    private bool _isProgressIndeterminate;
    private bool _isDetailIndeterminate;
    private bool _catalogLoaded;
    private bool _versionsLoaded;
    private bool _isCreateOperation;
    private bool _serverNameWasManuallyEdited;
    private bool _isApplyingAutomaticServerName;
    private bool _minecraftEulaAccepted;
    private bool _incrementalInitializationStarted;
    private bool _isCatalogRefreshing;
    private bool _disposed;

    public CoreServerCreationViewModel(ICoreServerCreationWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        _workflow = workflow;
        _incrementalCatalogWorkflow = workflow as IIncrementalCoreServerCatalogWorkflow;
    }

    public event EventHandler? Created;

    public ObservableCollection<CoreServerProduct> Cores { get; } = [];

    /// <summary>The version list after applying <see cref="VersionSearchQuery"/>.</summary>
    public ObservableCollection<CoreServerVersion> Versions { get; } = [];

    public CoreServerProduct? SelectedCore
    {
        get => _selectedCore;
        private set
        {
            if (SetProperty(ref _selectedCore, value))
            {
                OnPropertyChanged(nameof(HasSelectedCore));
                OnPropertyChanged(nameof(RequiresMinecraftEula));
                NotifyActionStateChanged();
            }
        }
    }

    public bool HasSelectedCore => SelectedCore is not null;

    public bool RequiresMinecraftEula
        => SelectedCore is { Software: not CoreServerSoftware.Velocity };

    public bool MinecraftEulaAccepted
    {
        get => _minecraftEulaAccepted;
        set
        {
            if (SetProperty(ref _minecraftEulaAccepted, value))
            {
                NotifyActionStateChanged();
            }
        }
    }

    public CoreServerVersion? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                ApplyAutomaticServerName();
                NotifyActionStateChanged();
            }
        }
    }

    public string VersionSearchQuery
    {
        get => _versionSearchQuery;
        set
        {
            if (!SetProperty(ref _versionSearchQuery, value ?? string.Empty))
            {
                return;
            }

            ApplyVersionFilter();
        }
    }

    public string ServerName
    {
        get => _serverName;
        set
        {
            if (SetProperty(ref _serverName, value ?? string.Empty))
            {
                if (!_isApplyingAutomaticServerName)
                {
                    _serverNameWasManuallyEdited = true;
                }

                NotifyActionStateChanged();
            }
        }
    }

    public CoreServerCreationOperationState OperationState
    {
        get => _operationState;
        private set
        {
            if (!SetProperty(ref _operationState, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsInputEnabled));
            OnPropertyChanged(nameof(CancelButtonText));
            OnPropertyChanged(nameof(ShowProgressPercentage));
            OnPropertyChanged(nameof(ShowDetailProgress));
            NotifyActionStateChanged();
        }
    }

    public bool IsBusy => OperationState != CoreServerCreationOperationState.Idle;

    public bool IsCatalogRefreshing
    {
        get => _isCatalogRefreshing;
        private set => SetProperty(ref _isCatalogRefreshing, value);
    }

    public bool IsCreating => _isCreateOperation;

    public bool IsInputEnabled => !IsBusy;

    public bool CanCreate => !IsBusy
        && SelectedCore is not null
        && SelectedVersion is not null
        && !string.IsNullOrWhiteSpace(ServerName)
        && (!RequiresMinecraftEula || MinecraftEulaAccepted);

    public string CancelButtonText => IsBusy ? L("core.cancelOperation") : L("common.close");

    public string StageText
    {
        get => _stageText;
        private set => SetProperty(ref _stageText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set
        {
            if (SetProperty(ref _detailText, value))
            {
                OnPropertyChanged(nameof(ShowDetailProgress));
            }
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string VersionStateText
    {
        get => _versionStateText;
        private set => SetProperty(ref _versionStateText, value);
    }

    public string CoreCatalogStateText => _catalogLoaded && Cores.Count == 0
                ? L("core.error.noCores")
        : string.Empty;

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

    public bool IsDetailIndeterminate
    {
        get => _isDetailIndeterminate;
        private set => SetProperty(ref _isDetailIndeterminate, value);
    }

    public bool ShowDetailProgress => IsBusy && !string.IsNullOrWhiteSpace(DetailText);

    public ServerInstance? CreatedServer { get; private set; }

    internal Task CatalogRefreshCompletion => _catalogRefreshTask ?? Task.CompletedTask;

    public async Task InitializeAsync()
    {
        ThrowIfDisposed();
        if (_incrementalCatalogWorkflow is not null)
        {
            await InitializeIncrementalCatalogAsync();
            return;
        }

        if (IsBusy || _catalogLoaded)
        {
            return;
        }

        Cores.Clear();
        ResetSelectedCore();
        ErrorMessage = string.Empty;
        _catalogLoaded = false;
        OnPropertyChanged(nameof(CoreCatalogStateText));
        var operation = BeginOperation(
            CoreServerCreationOperationState.LoadingCores,
                L("core.progress.readingCores"),
            isCreate: false,
            indeterminate: true);

        try
        {
            var cores = await _workflow.GetAvailableCoresAsync(operation.Token);
            if (!IsCurrent(operation.Generation))
            {
                return;
            }

            foreach (var core in cores
                         .Where(IsUsableCore)
                         .DistinctBy(item => item.CoreId, StringComparer.OrdinalIgnoreCase))
            {
                Cores.Add(core);
            }

            _catalogLoaded = true;
            OnPropertyChanged(nameof(CoreCatalogStateText));
            StageText = Cores.Count == 0
                ? L("core.status.noCores")
                : L("core.status.loadedCores", Cores.Count);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            SetCancelledMessage(operation.Generation, L("core.status.coresCancelled"));
        }
        catch (Exception exception)
        {
            SetOperationError(operation.Generation, L("core.error.readCores"), exception);
        }
        finally
        {
            CompleteOperation(operation);
        }
    }

    public async Task SelectCoreAsync(CoreServerProduct? core)
    {
        ThrowIfDisposed();
        if (core is null || IsBusy || !Cores.Contains(core))
        {
            return;
        }

        if (_incrementalCatalogWorkflow is not null)
        {
            SelectIncrementalCore(core);
            return;
        }

        SelectedCore = core;
        SelectedVersion = null;
        _allVersions.Clear();
        Versions.Clear();
        _versionsLoaded = false;
        VersionStateText = L("core.progress.readVersions", core.DisplayName);
        ApplyAutomaticServerName(core.DisplayName);
        ErrorMessage = string.Empty;
        var operation = BeginOperation(
            CoreServerCreationOperationState.LoadingVersions,
                L("core.progress.readVersions", core.DisplayName),
            isCreate: false,
            indeterminate: true);

        try
        {
            var versions = await _workflow.GetVersionsAsync(core, operation.Token);
            if (!IsCurrent(operation.Generation))
            {
                return;
            }

            _allVersions.AddRange(versions
                .Where(item => IsUsableVersion(item, core))
                .DistinctBy(item => item.VersionId, StringComparer.OrdinalIgnoreCase));
            _versionsLoaded = true;
            ApplyVersionFilter();
            StageText = _allVersions.Count == 0
                ? L("core.status.noVersionsFor", core.DisplayName)
                : L("core.status.loadedVersions", core.DisplayName, _allVersions.Count);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            SetCancelledMessage(operation.Generation, L("core.status.versionsCancelled"));
            if (IsCurrent(operation.Generation))
            {
                VersionStateText = L("core.status.versionsCancelledRetry");
            }
        }
        catch (Exception exception)
        {
            SetOperationError(operation.Generation, L("core.error.readVersions"), exception);
            VersionStateText = L("core.status.versionsFailedNoFake");
        }
        finally
        {
            CompleteOperation(operation);
        }
    }

    public async Task CreateAsync()
    {
        ThrowIfDisposed();
        if (!TryBuildCreationRequest(out var request))
        {
            return;
        }

        ErrorMessage = string.Empty;
        CreatedServer = null;
        CancelCatalogRefresh();
        var operation = BeginOperation(
            CoreServerCreationOperationState.Creating,
            L("core.progress.preparingCreate"),
            isCreate: true,
            indeterminate: true);
        var progress = new Progress<CoreServerCreationProgress>(value =>
            ApplyCreationProgress(operation.Generation, value));

        try
        {
            var created = await _workflow.CreateAsync(
                request,
                progress,
                operation.Token);
            ArgumentNullException.ThrowIfNull(created);

            if (!IsCurrent(operation.Generation))
            {
                return;
            }

            CreatedServer = created;
            ProgressPercentage = 100;
            IsProgressIndeterminate = false;
            ClearDetailProgress();
            StageText = L("core.status.created", created.Name);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            SetCancelledMessage(operation.Generation, L("core.status.createCancelled"));
        }
        catch (Exception exception)
        {
            SetOperationError(operation.Generation, L("core.error.createFailed"), exception);
        }
        finally
        {
            CompleteOperation(operation);
        }

        if (CreatedServer is not null && IsCurrent(operation.Generation))
        {
            Created?.Invoke(this, EventArgs.Empty);
        }
    }

    internal bool TryBuildCreationRequest(out CoreServerCreationRequest request)
    {
        ThrowIfDisposed();
        request = null!;
        if (IsBusy)
        {
            return false;
        }

        if (SelectedCore is null || SelectedVersion is null)
        {
            ErrorMessage = L("core.validation.selectCoreVersion");
            return false;
        }

        if (!_allVersions.Contains(SelectedVersion)
            || !string.Equals(SelectedVersion.CoreId, SelectedCore.CoreId, StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = L("core.validation.versionMismatch");
            return false;
        }

        var serverName = ServerName.Trim();
        if (serverName.Length == 0)
        {
            ErrorMessage = L("core.validation.serverName");
            return false;
        }

        ErrorMessage = string.Empty;
        if (RequiresMinecraftEula && !MinecraftEulaAccepted)
        {
            ErrorMessage = L("core.validation.eulaRequired");
            return false;
        }

        request = new CoreServerCreationRequest(
            SelectedCore,
            SelectedVersion,
            serverName,
            MinecraftEulaAccepted);
        return true;
    }

    internal void SetBackgroundSubmissionError(string message)
    {
        ThrowIfDisposed();
        ErrorMessage = string.IsNullOrWhiteSpace(message)
            ? L("jobs.error.addCore")
            : message.Trim();
    }

    public void CancelCurrentOperation()
    {
        if (!IsBusy || _operationCancellation is null)
        {
            return;
        }

        OperationState = CoreServerCreationOperationState.Cancelling;
        StageText = _isCreateOperation
            ? L("core.progress.cancellingCreate")
            : L("core.progress.cancellingRead");
        ClearDetailProgress();
        _operationCancellation.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelCatalogRefresh();
        CancelAndInvalidateOperation();
    }

    private OperationContext BeginOperation(
        CoreServerCreationOperationState state,
        string stageText,
        bool isCreate,
        bool indeterminate)
    {
        var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        var generation = ++_operationGeneration;
        _isCreateOperation = isCreate;
        OnPropertyChanged(nameof(IsCreating));
        ProgressPercentage = 0;
        IsProgressIndeterminate = indeterminate;
        ClearDetailProgress();
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
        _isCreateOperation = false;
        OnPropertyChanged(nameof(IsCreating));
        OperationState = CoreServerCreationOperationState.Idle;
    }

    private void CancelAndInvalidateOperation()
    {
        ++_operationGeneration;
        var cancellation = _operationCancellation;
        _operationCancellation = null;
        cancellation?.Cancel();
        ClearDetailProgress();
        _isCreateOperation = false;
        OnPropertyChanged(nameof(IsCreating));
        OperationState = CoreServerCreationOperationState.Idle;
    }

    private void ResetSelectedCore()
    {
        SelectedCore = null;
        SelectedVersion = null;
        _allVersions.Clear();
        Versions.Clear();
        _versionsLoaded = false;
        VersionStateText = L("core.status.chooseCore");
    }

    private void ApplyVersionFilter()
    {
        var query = VersionSearchQuery.Trim();
        var filtered = query.Length == 0
            ? _allVersions
            : _allVersions.Where(item => MatchesVersion(item, query)).ToList();

        var previous = SelectedVersion;
        Versions.Clear();
        foreach (var version in filtered)
        {
            Versions.Add(version);
        }

        SelectedVersion = previous is not null && Versions.Contains(previous)
            ? previous
            : Versions.FirstOrDefault(item => item.IsRecommended) ?? Versions.FirstOrDefault();
        VersionStateText = GetVersionStateText(query);
    }

    private void ApplyAutomaticServerName(string? provisionalName = null)
    {
        if (_serverNameWasManuallyEdited)
        {
            return;
        }

        var value = SelectedCore is { } core && SelectedVersion is { } version
            ? $"{core.DisplayName}-{version.MinecraftVersion}"
            : provisionalName;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _isApplyingAutomaticServerName = true;
        try
        {
            ServerName = value;
        }
        finally
        {
            _isApplyingAutomaticServerName = false;
        }
    }

    private string GetVersionStateText(string query)
    {
        if (SelectedCore is null)
        {
            return L("core.status.chooseCore");
        }

        if (_incrementalCatalogWorkflow is not null)
        {
            if (!_incrementalVersionStates.TryGetValue(SelectedCore.CoreId, out var state))
            {
                return IsCatalogRefreshing
                    ? L("core.status.backgroundReadingVersions", SelectedCore.DisplayName)
                    : L("core.status.noVersionData", SelectedCore.DisplayName);
            }

            if (_allVersions.Count > 0 && Versions.Count == 0)
            {
                return L("core.status.noVersionMatch", query);
            }

            if (state.RefreshFailed)
            {
                return _allVersions.Count > 0
                    ? L("core.status.updateFailedCache", SelectedCore.DisplayName)
                    : L("core.status.updateFailedNoCache", SelectedCore.DisplayName);
            }

            if (_allVersions.Count == 0)
            {
                return state.RefreshPending
                    ? L("core.status.backgroundUpdatingVersions", SelectedCore.DisplayName)
                    : L("core.status.noVersionsNoFake", SelectedCore.DisplayName);
            }

            if (state.IsCached && state.RefreshPending)
            {
                return L("core.status.showingTrustedCache");
            }

            return string.Empty;
        }

        if (!_versionsLoaded)
        {
            return L("core.status.waitingVersions", SelectedCore.DisplayName);
        }

        if (_allVersions.Count == 0)
        {
            return L("core.status.noVersionsNoFake", SelectedCore.DisplayName);
        }

        return Versions.Count == 0
            ? L("core.status.noVersionMatch", query)
            : string.Empty;
    }

    private void ApplyCreationProgress(long generation, CoreServerCreationProgress progress)
    {
        if (!IsCurrent(generation) || !_isCreateOperation)
        {
            return;
        }

        StageText = string.IsNullOrWhiteSpace(progress.Message)
            ? GetStageDisplay(progress.Stage)
            : progress.Message;
        IsProgressIndeterminate = progress.Percentage is null;
        if (progress.Percentage is { } percentage)
        {
            ProgressPercentage = Math.Clamp(percentage, 0, 100);
        }

        DetailText = progress.Detail?.Trim() ?? string.Empty;
        IsDetailIndeterminate = DetailText.Length > 0 && progress.IsDetailIndeterminate;
    }

    private void SetCancelledMessage(long generation, string message)
    {
        if (!IsCurrent(generation))
        {
            return;
        }

        ErrorMessage = string.Empty;
        ClearDetailProgress();
        StageText = message;
    }

    private void SetOperationError(long generation, string prefix, Exception exception)
    {
        if (!IsCurrent(generation))
        {
            return;
        }

        ErrorMessage = $"{prefix}：{GetSafeErrorMessage(exception)}";
        ClearDetailProgress();
        StageText = $"{prefix}。";
    }

    private void ClearDetailProgress()
    {
        DetailText = string.Empty;
        IsDetailIndeterminate = false;
    }

    private static bool IsUsableCore(CoreServerProduct core)
        => Enum.IsDefined(core.Software)
           && !string.IsNullOrWhiteSpace(core.CoreId)
           && !string.IsNullOrWhiteSpace(core.DisplayName);

    private static bool IsUsableVersion(CoreServerVersion version, CoreServerProduct core)
        => string.Equals(version.CoreId, core.CoreId, StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(version.VersionId)
           && !string.IsNullOrWhiteSpace(version.DisplayName)
           && !string.IsNullOrWhiteSpace(version.MinecraftVersion);

    private static bool MatchesVersion(CoreServerVersion version, string query)
        => version.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
           || version.MinecraftVersion.Contains(query, StringComparison.OrdinalIgnoreCase)
           || (version.Build?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
           || version.VersionId.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string GetSafeErrorMessage(Exception exception)
    {
        var message = exception.Message?.Trim();
        return string.IsNullOrWhiteSpace(message) ? L("common.unexpectedError") : message;
    }

    private static string GetStageDisplay(CoreServerCreationStage stage) => stage switch
    {
        CoreServerCreationStage.Preparing => L("core.stage.preparing"),
        CoreServerCreationStage.ResolvingVersion => L("core.stage.resolvingVersion"),
        CoreServerCreationStage.PreparingDirectory => L("core.stage.preparingDirectory"),
        CoreServerCreationStage.Downloading => L("core.stage.downloading"),
        CoreServerCreationStage.Verifying => L("core.stage.verifying"),
        CoreServerCreationStage.Installing => L("core.stage.installing"),
        CoreServerCreationStage.DetectingServer => L("core.stage.detectingServer"),
        CoreServerCreationStage.Finalizing => L("core.stage.finalizing"),
        _ => L("core.stage.creating")
    };

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);

    private bool IsCurrent(long generation) => generation == _operationGeneration && !_disposed;

    private async Task InitializeIncrementalCatalogAsync()
    {
        if (_incrementalInitializationStarted || _catalogLoaded)
        {
            return;
        }

        _incrementalInitializationStarted = true;
        Cores.Clear();
        ResetSelectedCore();
        _incrementalVersionStates.Clear();
        ErrorMessage = string.Empty;
        StageText = L("core.status.readingLocalCatalog");
        var generation = ++_catalogGeneration;
        var cancellation = new CancellationTokenSource();
        _catalogRefreshCancellation = cancellation;
        try
        {
            var bootstrap = await _incrementalCatalogWorkflow!
                .GetCatalogBootstrapAsync(cancellation.Token);
            if (!IsCatalogCurrent(generation))
            {
                return;
            }

            foreach (var core in bootstrap.Cores
                         .Where(IsUsableCore)
                         .DistinctBy(item => item.CoreId, StringComparer.OrdinalIgnoreCase))
            {
                Cores.Add(core);
                if (bootstrap.CachedVersions.TryGetValue(core.CoreId, out var cached))
                {
                    _incrementalVersionStates[core.CoreId] = new IncrementalVersionState(
                        cached
                            .Where(version => IsUsableVersion(version, core))
                            .DistinctBy(
                                static version => version.VersionId,
                                StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        IsCached: true,
                        RefreshPending: true,
                        RefreshFailed: false);
                }
            }

            _catalogLoaded = true;
            OnPropertyChanged(nameof(CoreCatalogStateText));
            StageText = bootstrap.StatusText;
            IsCatalogRefreshing = true;
            _catalogRefreshTask = ConsumeIncrementalCatalogAsync(
                generation,
                cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (IsCatalogCurrent(generation))
            {
                StageText = L("core.status.backgroundCancelled");
            }
        }
        catch (Exception exception)
        {
            if (IsCatalogCurrent(generation))
            {
                _catalogLoaded = true;
                OnPropertyChanged(nameof(CoreCatalogStateText));
                ErrorMessage = L("core.error.localCatalogFailed", GetSafeErrorMessage(exception));
                StageText = L("core.status.catalogUnavailable");
            }
        }
    }

    private async Task ConsumeIncrementalCatalogAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in _incrementalCatalogWorkflow!
                               .RefreshCatalogAsync(cancellationToken))
            {
                if (!IsCatalogCurrent(generation))
                {
                    return;
                }

                StageText = update.StatusText;
                if (update.IsFinal)
                {
                    foreach (var coreId in _incrementalVersionStates.Keys.ToArray())
                    {
                        var state = _incrementalVersionStates[coreId];
                        if (state.RefreshPending)
                        {
                            _incrementalVersionStates[coreId] = state with
                            {
                                RefreshPending = false
                            };
                        }
                    }

                    if (SelectedCore is not null)
                    {
                        ApplyIncrementalVersionState(SelectedCore);
                    }

                    continue;
                }

                if (update.Core is null)
                {
                    continue;
                }

                var core = Cores.FirstOrDefault(item => item.CoreId.Equals(
                    update.Core.CoreId,
                    StringComparison.OrdinalIgnoreCase));
                if (core is null)
                {
                    continue;
                }

                var versions = update.Versions
                    .Where(version => IsUsableVersion(version, core))
                    .DistinctBy(
                        static version => version.VersionId,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (update.Succeeded)
                {
                    _incrementalVersionStates[core.CoreId] = new IncrementalVersionState(
                        versions,
                        IsCached: false,
                        RefreshPending: false,
                        RefreshFailed: false);
                }
                else if (_incrementalVersionStates.TryGetValue(core.CoreId, out var previous))
                {
                    _incrementalVersionStates[core.CoreId] = previous with
                    {
                        RefreshPending = false,
                        RefreshFailed = true
                    };
                }
                else
                {
                    _incrementalVersionStates[core.CoreId] = new IncrementalVersionState(
                        [],
                        IsCached: false,
                        RefreshPending: false,
                        RefreshFailed: true);
                }

                if (SelectedCore?.CoreId.Equals(
                        core.CoreId,
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    ApplyIncrementalVersionState(core);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCatalogCurrent(generation))
            {
                StageText = L("core.status.backgroundUpdateCancelled");
            }
        }
        catch (Exception exception)
        {
            if (IsCatalogCurrent(generation))
            {
                StageText = L("core.status.backgroundUpdateIncomplete");
                ErrorMessage = L("core.error.backgroundUpdateFailed", GetSafeErrorMessage(exception));
            }
        }
        finally
        {
            if (IsCatalogCurrent(generation))
            {
                IsCatalogRefreshing = false;
                Interlocked.Exchange(ref _catalogRefreshCancellation, null)?.Dispose();
                if (SelectedCore is not null)
                {
                    VersionStateText = GetVersionStateText(VersionSearchQuery.Trim());
                }
            }
        }
    }

    private void SelectIncrementalCore(CoreServerProduct core)
    {
        SelectedCore = core;
        SelectedVersion = null;
        _allVersions.Clear();
        Versions.Clear();
        _versionsLoaded = false;
        ApplyAutomaticServerName(core.DisplayName);
        ErrorMessage = string.Empty;
        ApplyIncrementalVersionState(core);
    }

    private void ApplyIncrementalVersionState(CoreServerProduct core)
    {
        _allVersions.Clear();
        if (_incrementalVersionStates.TryGetValue(core.CoreId, out var state))
        {
            _allVersions.AddRange(state.Versions);
            _versionsLoaded = true;
        }
        else
        {
            _versionsLoaded = false;
        }

        ApplyVersionFilter();
    }

    private void CancelCatalogRefresh()
    {
        ++_catalogGeneration;
        var cancellation = Interlocked.Exchange(ref _catalogRefreshCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        IsCatalogRefreshing = false;
    }

    private bool IsCatalogCurrent(long generation)
        => generation == _catalogGeneration && !_disposed;

    private void NotifyActionStateChanged() => OnPropertyChanged(nameof(CanCreate));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct OperationContext(
        long Generation,
        CancellationTokenSource Cancellation)
    {
        public CancellationToken Token => Cancellation.Token;
    }

    private sealed record IncrementalVersionState(
        IReadOnlyList<CoreServerVersion> Versions,
        bool IsCached,
        bool RefreshPending,
        bool RefreshFailed);
}
