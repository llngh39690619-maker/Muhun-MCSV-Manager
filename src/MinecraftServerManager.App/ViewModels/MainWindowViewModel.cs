using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.Services.Localization;
using MinecraftServerManager.Client;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly string ProductDisplayVersion =
        typeof(MainWindowViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+', 2)[0]
        ?? "1.0";

    internal const string ConsoleWorkspaceTabKey = "Console";
    internal const string DiagnosticWorkspaceTabKey = "Diagnostics";
    internal const string PlayersWorkspaceTabKey = "Players";
    internal const string AddonsWorkspaceTabKey = "Addons";
    internal const string JavaRuntimeWorkspaceTabKey = "JavaRuntime";
    private const string ProviderUserAgent = "MuhunMCSVManager/1.0 (Windows; manager)";
    private const DispatcherPriority StateDispatcherPriority = DispatcherPriority.Send;
    private const DispatcherPriority PresenceDispatcherPriority = DispatcherPriority.Background;
    internal const int MaximumPendingConsoleLinesPerInstance = 4_096;
    private const int MaximumTrackedOnlinePlayers = 4_096;
    private const int ConsoleDrainBatchSize = MaximumPendingConsoleLinesPerInstance;
    internal static readonly TimeSpan ConsoleUiRefreshInterval = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan PresenceUiRefreshInterval = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan ProductServiceUpdateReconnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex SaveCompletionPattern = new(
        @"(?:Saved the game|Saved the world|Saved all player data|Saved all chunks)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private readonly ApplicationPaths _paths;
    private readonly IJsonSettingsStore<ManagerSettings> _settingsStore;
    private readonly AppearanceThemeService _appearanceThemeService;
    private readonly IServerRemovalConfirmationService _serverRemovalConfirmationService;
    private readonly IServerDeletionConfirmationService _serverDeletionConfirmationService;
    private readonly ServerDirectoryDeletionService _serverDirectoryDeletionService;
    private readonly IOnlineModpackWorkflow _onlineModpackWorkflow;
    private readonly IOnlineModpackDialogService _onlineModpackDialogService;
    private readonly ICurseForgeUpdateCredentialPrompt _curseForgeUpdateCredentialPrompt;
    private readonly IModpackUpdateSelectionService _modpackUpdateSelectionService;
    private readonly ICoreServerCreationDialogService _coreServerCreationDialogService;
    private readonly IBackgroundOnlineModpackDialogService? _backgroundOnlineModpackDialogService;
    private readonly IBackgroundCoreServerCreationDialogService? _backgroundCoreServerCreationDialogService;
    private readonly BackgroundServerJobCoordinator _backgroundJobs;
    private readonly ICoreServerCreationWorkflow? _ownedCoreServerCreationWorkflow;
    private readonly ExistingServerImportCoordinator _existingServerImportCoordinator;
    private readonly JarCoreDetector _coreDetector = new();
    private readonly ServerPackDetector _serverPackDetector = new();
    private readonly JavaVersionRecommendationService _javaRecommendations = new();
    private readonly ServerProcessManager _processManager;
    private readonly ProductServiceDesktopController? _productServiceController;
    private readonly IBundledProductServiceUpdateLauncher? _productServiceUpdateLauncher;
    private readonly BackupService _backupService = new();
    private readonly BackupRestoreService _backupRestoreService = new();
    private readonly ModpackUpdateBackupPlanner _modpackUpdateBackupPlanner = new();
    private readonly ModpackUpdateTransactionService _modpackUpdateTransactionService = new();
    private readonly MinecraftStatusProbe _minecraftStatusProbe = new();
    private readonly ServerWatchdogState _watchdogState = new();
    private readonly WindowsSystemMemoryProbe _systemMemoryProbe = new();
    private readonly MemoryRecommendationService _memoryRecommendationService;
    private readonly JvmMemoryLaunchOverlayService _jvmMemoryLaunchOverlayService = new();
    private readonly CrashRestartLimiter _crashRestartLimiter = new();
    private readonly CrashDiagnosticService _crashDiagnosticService = new();
    private readonly CancellationTokenSource _sessionServicesCancellation = new();
    private readonly CancellationTokenSource _applicationShutdownCancellation = new();
    private readonly ServerPropertiesPortService _serverPropertiesPortService = new();
    private readonly MinecraftEulaAcceptanceService _minecraftEulaAcceptanceService = new();
    private readonly Func<PortOccupancySnapshot> _capturePortOccupancy;
    private readonly SemaphoreSlim _portAssignmentGate = new(1, 1);
    // Every operation that exposes a Server directory to the manager must hold this gate from
    // its final path/identity check through port assignment, persistence, and AddInstance. The
    // workflows may build in parallel, but registration is deliberately atomic with manual
    // folder/JAR imports so two records can never claim the same directory or port.
    private readonly SemaphoreSlim _serverRegistryGate = new(1, 1);
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private readonly SemaphoreSlim _normalWindowSizePersistenceGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, int> _pendingLaunchPorts = new();
    private readonly ConcurrentDictionary<Guid, Guid> _pendingLaunchPortSessions = new();
    private readonly ConcurrentDictionary<Guid, ServerPropertiesDocumentFormatToken> _serverPropertiesFormats = new();
    private readonly ConcurrentDictionary<Guid, string> _serviceServerPropertiesRevisions = new();
    private readonly object _serviceServerPropertiesStateSync = new();
    private readonly Dictionary<Guid, long> _serviceServerPropertiesReloadGenerations = [];
    private readonly Dictionary<Guid, long> _serviceServerPropertiesReloadsInFlight = [];
    private readonly HashSet<Guid> _serviceServerPropertiesSavesInFlight = [];
    private readonly ConcurrentDictionary<Guid, CoreType> _playerPresenceCoreTypes = new();
    private readonly ConcurrentDictionary<Guid, ServerInstance> _instanceModels = new();
    private readonly ConcurrentDictionary<(Guid InstanceId, Guid SessionId), DateTimeOffset> _sessionStartedAt = new();
    private readonly ConcurrentDictionary<(Guid InstanceId, Guid SessionId), int> _sessionLaunchPorts = new();
    private readonly ConcurrentDictionary<(Guid InstanceId, Guid SessionId), byte> _sessionsThatReachedRunning = new();
    private readonly ConcurrentDictionary<(Guid InstanceId, Guid SessionId), Task> _watchdogTasks = new();
    private readonly ConcurrentDictionary<(Guid InstanceId, Guid SessionId), Task> _recoveryPointTasks = new();
    private readonly ConcurrentDictionary<(Guid InstanceId, Guid SessionId), Task> _crashReportTasks = new();
    private readonly ConcurrentDictionary<Guid, CrashSessionPlan> _crashPlans = new();
    private readonly ConcurrentDictionary<Guid, string> _lastHealthyRecoveryPoints = new();
    private readonly ConcurrentDictionary<(Guid InstanceId, Guid SessionId), TaskCompletionSource> _pendingSaveFlushes = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _backupGates = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _lifecycleGates = new();
    private readonly ConcurrentDictionary<Guid, byte> _lifecycleTransitions = new();
    private readonly ConcurrentDictionary<Guid, byte> _modpackUpdates = new();
    private readonly ConcurrentDictionary<Guid, Task> _modpackUpdateTasks = new();
    private readonly ConcurrentDictionary<Guid, PendingModpackHealthValidation> _pendingModpackHealthValidations = new();
    private readonly ConcurrentDictionary<Guid, Task> _modpackHealthFinalizationTasks = new();
    private readonly ConcurrentDictionary<(Guid InstanceId, Guid SessionId), Task> _modpackHealthProbeTasks = new();
    private readonly ConcurrentDictionary<Guid, byte> _modpackAutoRestartBlocks = new();
    private readonly ConcurrentDictionary<Guid, byte> _modpackRecoveryFailures = new();
    private readonly HashSet<ServerInstanceViewModel> _bulkSelectionSubscriptions = [];
    private readonly object _batchLifecycleOperationSync = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _automaticMemoryRecommendationCancellations = new();
    private readonly ConcurrentDictionary<Guid, Task> _automaticMemoryRecommendationTasks = new();
    private readonly object _addonScanSync = new();
    private readonly ConcurrentDictionary<(Guid InstanceId, Guid SessionId), byte> _watchdogRecoveryInProgress = new();
    private readonly ConcurrentDictionary<Guid, Guid> _latestConsoleSessions = new();
    private readonly ConcurrentDictionary<Guid, ServerResourceSample> _pendingResourceSamples = new();
    private readonly ConcurrentDictionary<Guid, byte> _scheduledResourceSampleDrains = new();
    private readonly ConcurrentDictionary<Guid, BoundedDropOldestQueue<PendingConsoleLine>> _pendingConsoleLines = new();
    private readonly ConcurrentDictionary<Guid, byte> _scheduledConsoleDrains = new();
    private readonly PlayerPresenceDispatchBuffer _playerPresenceBuffer =
        new(MaximumTrackedOnlinePlayers);
    private readonly ConcurrentDictionary<Guid, byte> _scheduledPresenceDrains = new();
    private readonly ConcurrentDictionary<Guid, long> _manualStopEpochs = new();
    private readonly HttpClient _javaHttpClient = new();
    private readonly HttpClient _modrinthHttpClient = new();
    private readonly AdoptiumRuntimeProvider _javaProvider;
    private readonly ModrinthUpdateProvider _modrinthProvider;
    private ManagerSettings _settings = new();
    private ServerInstanceViewModel? _selectedServer;
    private ServerInstanceViewModel? _secondaryServer;
    private bool _isBulkSelectionMode;
    private bool _isBatchLifecycleOperationRunning;
    private bool _isServerListMutationRunning;
    private CancellationTokenSource? _batchLifecycleOperationCancellation;
    private Task _batchLifecycleOperationTask = Task.CompletedTask;
    private bool _isSplitConsoleVisible;
    private string _selectedWorkspaceTabKey = ConsoleWorkspaceTabKey;
    private string? _statusMessageKey = "main.vm.status.ready";
    private object?[] _statusMessageArguments = [];
    private string _statusMessage = LocalizationService.Current.Get("main.vm.status.ready");
    private int _selectedJavaMajor = 21;
    private double? _previewWindowWidth;
    private double? _previewWindowHeight;
    private bool _isDisposed;
    private Task _lastDiagnosticOutputPreferenceSave = Task.CompletedTask;
    private Task _lastPlayerRegistryReload = Task.CompletedTask;
    private Task _lastAutomaticMemoryRecommendation = Task.CompletedTask;
    private Task _lastAddonScan = Task.CompletedTask;
    private BackgroundJobsWindow? _backgroundJobsWindow;
    private ClientContentDownloadCenterWindow? _contentDownloadCenterWindow;
    private RemoteAccessDialog? _remoteAccessDialog;
    private RemoteWebConsoleDialog? _remoteWebConsoleDialog;
    private ProductServiceRemoteAccessDialog? _productServiceRemoteAccessDialog;
    private RemoteAccessCoordinator? _remoteAccessCoordinator;
    private readonly object _remoteAccessLifecycleSync = new();
    private readonly object _remoteAccessShutdownSync = new();
    private Task? _remoteAccessShutdownTask;
    private readonly RemoteAccessSessionState _remoteAccessSessionState = new();
    private readonly object _remoteAccessRecoverySync = new();
    private RemoteControlSettings _remoteAccessRecoverySettings = new();
    private bool _remoteAccessRecoveryConfigurationComplete;
    private CancellationTokenSource? _remoteAccessRecoveryCancellation;
    private Task _remoteAccessRecoveryTask = Task.CompletedTask;
    private readonly object _shutdownSync = new();
    private Task? _shutdownTask;
    private Task? _disposeTask;
    private Task _productServicePollingTask = Task.CompletedTask;
    private Task _legacyServiceMigrationTask = Task.CompletedTask;
    private ProductServiceConnectionState _productServiceConnectionState =
        ProductServiceConnectionState.Unavailable;
    private string _productServiceConnectionCode = "service.not_initialized";
    private ProductApiVersion? _productServiceNegotiatedApiVersion;
    private bool _isProductServiceUpdateRunning;
    private IReadOnlyList<ServerInstance>? _readOnlyLegacyInstances;
    private readonly Dictionary<Guid, Guid> _playerPresenceSessions = [];
    private readonly ConcurrentDictionary<Guid, byte> _loadedPlayerRegistries = new();
    private readonly HashSet<Guid> _dirtyProductServiceRegistrations = [];
    private bool _applyingProductServiceProjection;
    private readonly object _playerRegistryReloadSync = new();
    private CancellationTokenSource? _playerRegistryReloadCancellation;
    private long _playerRegistryReloadVersion;
    private long _serverPropertiesReloadVersion;
    private CancellationTokenSource? _addonScanCancellation;
    private long _addonScanVersion;
    private bool _isClientWorkspace;

    public MainWindowViewModel(ApplicationPaths paths)
        : this(
            paths,
            new ServerRemovalConfirmationService(),
            new OnlineModpackWorkflow(paths),
            onlineModpackDialogService: null)
    {
    }

    internal static MainWindowViewModel CreateServiceOwned(
        ApplicationPaths paths,
        IProductServiceClient? client = null,
        IServerDeletionConfirmationService? deletionConfirmationService = null,
        IBundledProductServiceUpdateLauncher? productServiceUpdateLauncher = null)
        => new(
            paths,
            new ServerRemovalConfirmationService(),
            new OnlineModpackWorkflow(paths),
            onlineModpackDialogService: null,
            serverDeletionConfirmationService: deletionConfirmationService,
            productServiceClient: client ?? new ProductServiceClient(),
            productServiceUpdateLauncher: productServiceUpdateLauncher);

    internal MainWindowViewModel(
        ApplicationPaths paths,
        IServerRemovalConfirmationService serverRemovalConfirmationService)
        : this(
            paths,
            serverRemovalConfirmationService,
            new OnlineModpackWorkflow(paths),
            onlineModpackDialogService: null)
    {
    }

    internal MainWindowViewModel(
        ApplicationPaths paths,
        Func<PortOccupancySnapshot> capturePortOccupancy)
        : this(
            paths,
            new ServerRemovalConfirmationService(),
            new OnlineModpackWorkflow(paths),
            onlineModpackDialogService: null,
            capturePortOccupancy: capturePortOccupancy)
    {
    }

    internal MainWindowViewModel(
        ApplicationPaths paths,
        IServerRemovalConfirmationService serverRemovalConfirmationService,
        IServerDeletionConfirmationService serverDeletionConfirmationService)
        : this(
            paths,
            serverRemovalConfirmationService,
            new OnlineModpackWorkflow(paths),
            onlineModpackDialogService: null,
            serverDeletionConfirmationService: serverDeletionConfirmationService)
    {
    }

    internal MainWindowViewModel(
        ApplicationPaths paths,
        IServerRemovalConfirmationService serverRemovalConfirmationService,
        IOnlineModpackWorkflow onlineModpackWorkflow,
        IOnlineModpackDialogService? onlineModpackDialogService,
        IExistingServerImportChoiceService? existingServerImportChoiceService = null,
        ICoreServerCreationDialogService? coreServerCreationDialogService = null,
        IServerDeletionConfirmationService? serverDeletionConfirmationService = null,
        Func<PortOccupancySnapshot>? capturePortOccupancy = null,
        IProductServiceClient? productServiceClient = null,
        string? productServiceImportsRoot = null,
        ICurseForgeUpdateCredentialPrompt? curseForgeUpdateCredentialPrompt = null,
        IModpackUpdateSelectionService? modpackUpdateSelectionService = null,
        IJsonSettingsStore<ManagerSettings>? settingsStore = null,
        IBundledProductServiceUpdateLauncher? productServiceUpdateLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(serverRemovalConfirmationService);
        ArgumentNullException.ThrowIfNull(onlineModpackWorkflow);

        _paths = paths;
        ClientWorkspace = new ClientWorkspaceViewModel(
            _paths,
            () => _settings.NewClientDefaults ??= new NewMinecraftClientDefaultsSettings());
        ClientWorkspace.ContentDownloadCenterRequested += OnContentDownloadCenterRequested;
        _settingsStore = settingsStore ?? new JsonSettingsStore<ManagerSettings>(_paths.SettingsFile);
        _appearanceThemeService = new AppearanceThemeService(_paths);
        _serverRemovalConfirmationService = serverRemovalConfirmationService;
        _serverDeletionConfirmationService = serverDeletionConfirmationService
            ?? new ServerDeletionConfirmationService();
        _serverDirectoryDeletionService = new ServerDirectoryDeletionService(paths);
        _capturePortOccupancy = capturePortOccupancy ?? SystemPortOccupancy.Capture;
        _onlineModpackWorkflow = onlineModpackWorkflow;
        _curseForgeUpdateCredentialPrompt = curseForgeUpdateCredentialPrompt
            ?? new CurseForgeUpdateCredentialPrompt();
        _modpackUpdateSelectionService = modpackUpdateSelectionService
            ?? new ModpackUpdateSelectionService();
        _backgroundJobs = new BackgroundServerJobCoordinator(
            CommitBackgroundServerAsync,
            serverName => Servers.Any(server => server.Name.Equals(
                serverName,
                StringComparison.CurrentCultureIgnoreCase)),
            serverName => SafePath.CreateUniqueDirectoryPath(_paths.Servers, serverName));
        _backgroundJobs.PropertyChanged += OnBackgroundJobsPropertyChanged;
        _onlineModpackDialogService = onlineModpackDialogService
            ?? new OnlineModpackDialogService(_onlineModpackWorkflow);
        if (onlineModpackDialogService is null)
        {
            _backgroundOnlineModpackDialogService =
                new BackgroundOnlineModpackDialogService(_onlineModpackWorkflow, _backgroundJobs);
        }
        if (coreServerCreationDialogService is null)
        {
            _ownedCoreServerCreationWorkflow = new CoreServerCreationWorkflow(paths);
            _coreServerCreationDialogService = new CoreServerCreationDialogService(
                _ownedCoreServerCreationWorkflow);
            _backgroundCoreServerCreationDialogService =
                new BackgroundCoreServerCreationDialogService(
                    _ownedCoreServerCreationWorkflow,
                    _backgroundJobs);
        }
        else
        {
            _coreServerCreationDialogService = coreServerCreationDialogService;
        }
        _existingServerImportCoordinator = new ExistingServerImportCoordinator(
            existingServerImportChoiceService ?? new ExistingServerImportChoiceService(),
            ImportServerFolderAsync,
            ImportServerAsync);
        _javaProvider = new AdoptiumRuntimeProvider(_javaHttpClient, ProviderUserAgent);
        _modrinthProvider = new ModrinthUpdateProvider(_modrinthHttpClient, ProviderUserAgent);
        _memoryRecommendationService = new MemoryRecommendationService(_systemMemoryProbe);
        _productServiceController = productServiceClient is null
            ? null
            : new ProductServiceDesktopController(productServiceClient, productServiceImportsRoot);
        _productServiceUpdateLauncher = productServiceClient is null
            ? null
            : productServiceUpdateLauncher
              ?? new BundledProductServiceUpdateLauncher(ProductDisplayVersion);
        _processManager = new ServerProcessManager(new ServerProcessManagerOptions
        {
            MaximumRetainedConsoleLines = 5_000,
            ResourceSamplingInterval = TimeSpan.FromSeconds(2),
            GracefulStopTimeout = TimeSpan.FromSeconds(30),
            ShouldAutoRestartAsync = ShouldAutomaticallyRestartAsync,
            GetAutoRestartDelayAsync = GetAutomaticRestartDelayAsync,
            PrepareAutoRestartAsync = PrepareAutomaticRestartAsync,
            PrepareStartWithContextAsync = PrepareServerStartAsync,
            PreparedStartAborted = instanceId => ReleasePendingLaunchPort(instanceId)
        });

        _processManager.ConsoleLineReceived += OnConsoleLineReceived;
        _processManager.StateChanged += OnServerStateChanged;
        _processManager.ResourceSampled += OnResourceSampled;

        ShowServerWorkspaceCommand = new RelayCommand(() => IsClientWorkspace = false);
        ShowClientWorkspaceCommand = new RelayCommand(() =>
        {
            IsClientWorkspace = true;
            if (ClientWorkspace.InitializeCommand.CanExecute(null))
            {
                ClientWorkspace.InitializeCommand.Execute(null);
            }
        });

        ImportExistingServerCommand = new AsyncRelayCommand(
            () => GuardAsync(ImportExistingServerAsync, "main.vm.operation.importExisting"),
            CanCreateOrImportServer);
        ImportServerCommand = new AsyncRelayCommand(
            () => GuardAsync(ImportServerAsync, "main.vm.operation.importCore"),
            CanCreateOrImportServer);
        ImportServerFolderCommand = new AsyncRelayCommand(
            () => GuardAsync(ImportServerFolderAsync, "main.vm.operation.importFolder"),
            CanCreateOrImportServer);
        CreateCoreServerCommand = new AsyncRelayCommand(
            () => GuardAsync(CreateCoreServerAsync, "main.vm.operation.createCore"),
            CanCreateOrImportServer);
        InstallOnlineModpackCommand = new AsyncRelayCommand(
            () => GuardAsync(InstallOnlineModpackAsync, "main.vm.operation.installModpack"),
            CanCreateOrImportServer);
        StartSelectedCommand = new AsyncRelayCommand(
            () => GuardAsync(StartSelectedAsync, "main.vm.operation.startServer"),
            CanStartSelectedServer);
        StopSelectedCommand = new AsyncRelayCommand(
            () => GuardAsync(StopSelectedAsync, "main.vm.operation.stopServer"),
            CanStopSelectedServer);
        ToggleBulkSelectionModeCommand = new RelayCommand(
            ToggleBulkSelectionMode,
            () => !IsBatchLifecycleOperationRunning && !_isServerListMutationRunning);
        StartCheckedServersCommand = new AsyncRelayCommand(
            () => GuardAsync(StartCheckedServersAsync, "main.vm.operation.startSelected"),
            CanRunCheckedServerBatch);
        StopCheckedServersCommand = new AsyncRelayCommand(
            () => GuardAsync(StopCheckedServersAsync, "main.vm.operation.stopSelected"),
            CanRunCheckedServerBatch);
        Servers.CollectionChanged += OnServersCollectionChanged;
        RestartSelectedCommand = new AsyncRelayCommand(
            () => GuardAsync(RestartSelectedAsync, "main.vm.operation.restartServer"),
            () => SelectedServer is not null
                  && (!IsProductServiceRuntime || IsProductServiceConnected));
        OpenBackgroundJobsCommand = new RelayCommand(OpenBackgroundJobsWindow);
        UpdateProductServiceCommand = new AsyncRelayCommand(
            UpdateProductServiceAsync,
            CanUpdateProductService);
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(_paths.Root));
        OpenSettingsCommand = new RelayCommand(OpenGeneralSettings);
        OpenAppearanceSettingsCommand = OpenSettingsCommand;
        OpenServerAppearanceCommand = new RelayCommand(
            parameter => OpenServerAppearance(parameter as ServerInstanceViewModel),
            parameter => parameter is ServerInstanceViewModel server
                         && Servers.Contains(server)
                         && (server.CanAccessLocalFiles || server.IsServiceManaged));
        OpenRemoteManagementCommand = new RelayCommand(
            OpenRemoteAccess,
            () => _productServiceController is not null
                ? IsProductServiceConnected
                : _remoteAccessCoordinator is not null);
        // Keep the legacy diagnostic/smoke entry point as an alias while the visible toolbar now
        // exposes one cohesive Remote management surface.
        OpenRemoteAccessCommand = OpenRemoteManagementCommand;
        OpenRemoteWebConsoleCommand = new RelayCommand(
            OpenRemoteWebConsole,
            () => _productServiceController is not null
                ? IsProductServiceConnected
                : _remoteAccessCoordinator is not null);
        OpenSelectedFolderCommand = new AsyncRelayCommand(
            () => GuardAsync(OpenSelectedFolderAsync, "main.vm.operation.openFolder"),
            CanOpenSelectedFolder);
        RemoveServerCommand = new AsyncRelayCommand(
            parameter => GuardAsync(
                () => RemoveServerAsync(parameter as ServerInstanceViewModel),
                "main.vm.operation.removeServer"),
            parameter => parameter is ServerInstanceViewModel server
                         && Servers.Contains(server)
                         && (!IsProductServiceRuntime || IsProductServiceConnected)
                         && !IsBatchLifecycleOperationRunning
                         && !_isServerListMutationRunning);
        DeleteServerCommand = new AsyncRelayCommand(
            parameter => GuardAsync(
                () => DeleteServerPermanentlyAsync(parameter as ServerInstanceViewModel),
                "main.vm.operation.deleteServer"),
            parameter => parameter is ServerInstanceViewModel server
                          && Servers.Contains(server)
                          && ((!server.IsServiceManaged && !IsProductServiceRuntime)
                              || (server.IsServiceManaged
                                  && SupportsProductServiceFileAdministration))
                          && !IsBatchLifecycleOperationRunning
                          && !_isServerListMutationRunning);
        SaveSelectedSettingsCommand = new AsyncRelayCommand(
            () => GuardAsync(SaveSelectedSettingsAsync, "main.vm.operation.saveSettings"),
            () => SelectedServer is not null
                  && (SelectedServer.CanAccessLocalFiles
                      || (SelectedServer.IsServiceManaged && IsProductServiceConnected)));
        SaveServerAppearanceCommand = new AsyncRelayCommand(
            parameter => GuardAsync(
                () => SaveServerAppearanceAsync(parameter as ServerInstanceViewModel),
                "main.vm.operation.saveAppearance"),
            parameter => parameter is ServerInstanceViewModel server
                         && Servers.Contains(server)
                         && (server.CanAccessLocalFiles || server.IsServiceManaged));
        ChooseBackgroundCommand = new AsyncRelayCommand(() => GuardAsync(ChooseBackgroundAsync, "main.vm.operation.chooseBackground"));
        ChooseIconCommand = new AsyncRelayCommand(() => GuardAsync(ChooseIconAsync, "main.vm.operation.chooseIcon"));
        ReloadPropertiesCommand = new AsyncRelayCommand(
            () => GuardAsync(ReloadPropertiesAsync, "main.vm.operation.reloadProperties"),
            () => CanReloadSelectedServerProperties);
        SavePropertiesCommand = new AsyncRelayCommand(
            () => GuardAsync(SavePropertiesAsync, "main.vm.operation.saveProperties"),
            () => CanSaveSelectedServerProperties);
        DownloadJavaCommand = new AsyncRelayCommand(
            () => GuardAsync(DownloadSelectedJavaAsync, "main.vm.operation.downloadJava"),
            () => !IsProductServiceRuntime);
        RefreshJavaCommand = new AsyncRelayCommand(() => GuardAsync(RefreshJavaAsync, "main.vm.operation.scanJava"));
        CreateBackupCommand = new AsyncRelayCommand(
            () => GuardAsync(CreateSelectedBackupAsync, "main.vm.operation.createBackup"),
            CanUseSelectedBackupCommands);
        RestoreBackupCommand = new AsyncRelayCommand(
            parameter => GuardAsync(
                () => RestoreSelectedBackupAsync(parameter as BackupItemViewModel),
                "main.vm.operation.restoreBackup"),
            parameter => parameter is BackupItemViewModel && CanUseSelectedBackupCommands());
        OpenBackupFolderCommand = new RelayCommand(OpenSelectedBackupFolder);
        OpenCrashReportsFolderCommand = new RelayCommand(OpenSelectedCrashReportsFolder);
        OpenRecoveryPointsFolderCommand = new RelayCommand(
            OpenSelectedRecoveryPointsFolder,
            () => CanManageLocalRecoveryPoints);
        RefreshBackupsCommand = new AsyncRelayCommand(
            () => GuardAsync(RefreshSelectedBackupsAsync, "main.vm.operation.refreshBackups"),
            CanUseSelectedBackupCommands);
        RestoreRecoveryPointCommand = new AsyncRelayCommand(
            () => GuardAsync(RestoreRecoveryPointAsync, "main.vm.operation.restoreRecoveryPoint"),
            () => CanManageLocalRecoveryPoints);
        UpdateSelectedModpackCommand = new AsyncRelayCommand(
            () => GuardAsync(RunTrackedSelectedModpackUpdateAsync, "main.vm.operation.updateModpack"),
            () => SelectedServer?.CanIterativelyUpdateModpack == true
                  && (SelectedServer.CanAccessLocalFiles
                      || (SelectedServer.IsServiceManaged && IsProductServiceConnected))
                  && !_modpackUpdates.ContainsKey(SelectedServer.Id));
        OpenModpackUpdateBackupsCommand = new RelayCommand(OpenModpackUpdateBackupsFolder);
        CheckAddonUpdatesCommand = new AsyncRelayCommand(
            () => GuardAsync(CheckAddonUpdatesAsync, "main.vm.operation.checkAddonUpdates"),
            () => CanBrowseSelectedServerFiles);
        OpenAddonFolderCommand = new AsyncRelayCommand(
            () => GuardAsync(OpenSelectedAddonFolderAsync, "main.vm.operation.openFolder"),
            () => CanBrowseSelectedServerFiles);
        RefreshPlayersCommand = new AsyncRelayCommand(
            () => GuardAsync(RefreshPlayersAsync, "main.vm.operation.refreshPlayers"),
            () => CanRefreshSelectedPlayers);
        KickPlayerCommand = new AsyncRelayCommand(() => GuardAsync(() => SendPlayerCommandAsync("kick", "main.players.kick"), "main.vm.operation.kickPlayer"), CanManageSelectedPlayers);
        BanPlayerCommand = new AsyncRelayCommand(() => GuardAsync(() => SendPlayerCommandAsync("ban", "main.players.ban"), "main.vm.operation.banPlayer"), CanManageSelectedPlayers);
        PardonPlayerCommand = new AsyncRelayCommand(() => GuardAsync(() => SendPlayerCommandAsync("pardon", "main.players.pardon"), "main.vm.operation.pardonPlayer"), CanManageSelectedPlayers);
        OpPlayerCommand = new AsyncRelayCommand(() => GuardAsync(() => SendPlayerCommandAsync("op", "main.players.op"), "main.vm.operation.opPlayer"), CanManageSelectedPlayers);
        DeopPlayerCommand = new AsyncRelayCommand(() => GuardAsync(() => SendPlayerCommandAsync("deop", "main.players.deop"), "main.vm.operation.deopPlayer"), CanManageSelectedPlayers);
        WhitelistAddCommand = new AsyncRelayCommand(() => GuardAsync(() => SendPlayerCommandAsync("whitelist add", "main.players.whitelistAdd"), "main.vm.operation.whitelistAdd"), CanManageSelectedPlayers);
        WhitelistRemoveCommand = new AsyncRelayCommand(() => GuardAsync(() => SendPlayerCommandAsync("whitelist remove", "main.players.whitelistRemove"), "main.vm.operation.whitelistRemove"), CanManageSelectedPlayers);
        WhitelistOnCommand = new AsyncRelayCommand(() => GuardAsync(() => SendAdministrativeCommandAsync("whitelist on", "main.vm.status.whitelistEnabled"), "main.vm.operation.whitelistOn"), CanManageSelectedPlayers);
        WhitelistOffCommand = new AsyncRelayCommand(() => GuardAsync(() => SendAdministrativeCommandAsync("whitelist off", "main.vm.status.whitelistDisabled"), "main.vm.operation.whitelistOff"), CanManageSelectedPlayers);
        ClearBackgroundCommand = new AsyncRelayCommand(() => GuardAsync(ClearBackgroundAsync, "main.vm.operation.clearBackground"));
        ClearIconCommand = new AsyncRelayCommand(() => GuardAsync(ClearIconAsync, "main.vm.operation.clearIcon"));
        LocalizationService.Current.CultureChanged += OnLocalizationCultureChanged;
    }

    public ObservableCollection<ServerInstanceViewModel> Servers { get; } = [];

    public ClientWorkspaceViewModel ClientWorkspace { get; }

    public bool IsClientWorkspace
    {
        get => _isClientWorkspace;
        private set
        {
            if (SetProperty(ref _isClientWorkspace, value))
            {
                OnPropertyChanged(nameof(IsServerWorkspace));
            }
        }
    }

    public bool IsServerWorkspace => !IsClientWorkspace;

    internal async Task ShowClientWorkspaceForDiagnosticsAsync()
    {
        IsClientWorkspace = true;
        await ClientWorkspace.InitializeForDiagnosticsAsync();
    }

    internal async Task ShowClientCatalogForDiagnosticsAsync()
    {
        await ShowClientWorkspaceForDiagnosticsAsync();
        await ClientWorkspace.ShowCatalogForDiagnosticsAsync();
    }
    public ObservableCollection<JavaRuntimeItemViewModel> InstalledJavaRuntimes { get; } = [];
    public IReadOnlyList<int> JavaMajorChoices { get; } = JavaVersionRecommendationService.SupportedMajorVersions;

    public AsyncRelayCommand ImportExistingServerCommand { get; }
    public RelayCommand ShowServerWorkspaceCommand { get; }
    public RelayCommand ShowClientWorkspaceCommand { get; }
    public AsyncRelayCommand ImportServerCommand { get; }
    public AsyncRelayCommand ImportServerFolderCommand { get; }
    public AsyncRelayCommand CreateCoreServerCommand { get; }
    public AsyncRelayCommand InstallOnlineModpackCommand { get; }
    public AsyncRelayCommand StartSelectedCommand { get; }
    public AsyncRelayCommand StopSelectedCommand { get; }
    public RelayCommand ToggleBulkSelectionModeCommand { get; }
    public AsyncRelayCommand StartCheckedServersCommand { get; }
    public AsyncRelayCommand StopCheckedServersCommand { get; }
    public AsyncRelayCommand RestartSelectedCommand { get; }
    public RelayCommand OpenBackgroundJobsCommand { get; }
    public AsyncRelayCommand UpdateProductServiceCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenAppearanceSettingsCommand { get; }
    public RelayCommand OpenServerAppearanceCommand { get; }
    public RelayCommand OpenRemoteManagementCommand { get; }
    public RelayCommand OpenRemoteAccessCommand { get; }
    public RelayCommand OpenRemoteWebConsoleCommand { get; }
    public AsyncRelayCommand OpenSelectedFolderCommand { get; }
    public AsyncRelayCommand RemoveServerCommand { get; }
    public AsyncRelayCommand DeleteServerCommand { get; }
    public AsyncRelayCommand SaveSelectedSettingsCommand { get; }
    public AsyncRelayCommand SaveServerAppearanceCommand { get; }
    public AsyncRelayCommand ChooseBackgroundCommand { get; }
    public AsyncRelayCommand ChooseIconCommand { get; }
    public AsyncRelayCommand ReloadPropertiesCommand { get; }
    public AsyncRelayCommand SavePropertiesCommand { get; }
    public AsyncRelayCommand DownloadJavaCommand { get; }
    public AsyncRelayCommand RefreshJavaCommand { get; }
    public AsyncRelayCommand CreateBackupCommand { get; }
    public AsyncRelayCommand RestoreBackupCommand { get; }
    public RelayCommand OpenBackupFolderCommand { get; }
    public RelayCommand OpenCrashReportsFolderCommand { get; }
    public RelayCommand OpenRecoveryPointsFolderCommand { get; }
    public AsyncRelayCommand RefreshBackupsCommand { get; }
    public AsyncRelayCommand RestoreRecoveryPointCommand { get; }
    public AsyncRelayCommand UpdateSelectedModpackCommand { get; }
    public RelayCommand OpenModpackUpdateBackupsCommand { get; }
    public AsyncRelayCommand CheckAddonUpdatesCommand { get; }
    public AsyncRelayCommand OpenAddonFolderCommand { get; }
    public AsyncRelayCommand RefreshPlayersCommand { get; }
    public AsyncRelayCommand KickPlayerCommand { get; }
    public AsyncRelayCommand BanPlayerCommand { get; }
    public AsyncRelayCommand PardonPlayerCommand { get; }
    public AsyncRelayCommand OpPlayerCommand { get; }
    public AsyncRelayCommand DeopPlayerCommand { get; }
    public AsyncRelayCommand WhitelistAddCommand { get; }
    public AsyncRelayCommand WhitelistRemoveCommand { get; }
    public AsyncRelayCommand WhitelistOnCommand { get; }
    public AsyncRelayCommand WhitelistOffCommand { get; }
    public AsyncRelayCommand ClearBackgroundCommand { get; }
    public AsyncRelayCommand ClearIconCommand { get; }

    public ServerInstanceViewModel? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (ReferenceEquals(_selectedServer, value)) return;
            if (_selectedServer is not null)
            {
                _selectedServer.PropertyChanged -= OnSelectedServerPropertyChanged;
            }
            if (!SetProperty(ref _selectedServer, value)) return;
            if (value is not null)
            {
                value.PropertyChanged += OnSelectedServerPropertyChanged;
            }
            CancelPlayerRegistryReload();
            CancelAddonScan();
            OnPropertyChanged(nameof(HasSelectedServer));
            OnPropertyChanged(nameof(CanEditSelectedLocalConfiguration));
            OnPropertyChanged(nameof(CanReloadSelectedServerProperties));
            OnPropertyChanged(nameof(CanEditSelectedServerProperties));
            OnPropertyChanged(nameof(CanSaveSelectedServerProperties));
            OnPropertyChanged(nameof(IsSelectedServerPropertiesOperationRunning));
            OnPropertyChanged(nameof(CanBrowseSelectedServerFiles));
            OnPropertyChanged(nameof(CanManageLocalRecoveryPoints));
            OnPropertyChanged(nameof(CanRefreshSelectedPlayers));
            OnPropertyChanged(nameof(BackgroundImagePath));
            OnPropertyChanged(nameof(IsSplitDiagnosticOutputVisible));
            NotifySelectedLifecycleCommandsCanExecuteChanged();
            NotifySelectedServiceDependentCommandsCanExecuteChanged();
            OpenSelectedFolderCommand.NotifyCanExecuteChanged();
            RestartSelectedCommand.NotifyCanExecuteChanged();
            SaveSelectedSettingsCommand.NotifyCanExecuteChanged();
            ReloadPropertiesCommand.NotifyCanExecuteChanged();
            SavePropertiesCommand.NotifyCanExecuteChanged();
            OpenRecoveryPointsFolderCommand.NotifyCanExecuteChanged();
            RestoreRecoveryPointCommand.NotifyCanExecuteChanged();
            UpdateSelectedModpackCommand.NotifyCanExecuteChanged();
            CheckAddonUpdatesCommand.NotifyCanExecuteChanged();
            OpenAddonFolderCommand.NotifyCanExecuteChanged();
            RefreshPlayersCommand.NotifyCanExecuteChanged();
            if (value?.SeparateDiagnosticOutput != true
                && SelectedWorkspaceTabKey == DiagnosticWorkspaceTabKey)
            {
                SelectedWorkspaceTabKey = ConsoleWorkspaceTabKey;
            }
            if (value is not null)
            {
                SelectedJavaMajor = value.Model.JavaMajorVersion ?? 21;
                if (value.CanAccessLocalFiles)
                {
                    value.RefreshBackups();
                    _ = ReloadPropertiesQuietlyAsync(value);
                    if (SelectedWorkspaceTabKey == PlayersWorkspaceTabKey)
                    {
                        QueuePlayerRegistryLoadIfNeeded(value);
                    }
                }
                else if (value.IsServiceManaged && IsProductServiceConnected)
                {
                    if (SupportsProductServicePropertiesEditor)
                    {
                        _ = GuardAsync(
                            () => ReloadPropertiesQuietlyAsync(value),
                            "main.vm.operation.reloadProperties");
                    }
                    _ = GuardAsync(RefreshSelectedBackupsAsync, "main.vm.operation.refreshBackups");
                    if (SelectedWorkspaceTabKey == PlayersWorkspaceTabKey)
                    {
                        QueuePlayerRegistryLoadIfNeeded(value);
                    }
                }
                if (SelectedWorkspaceTabKey == AddonsWorkspaceTabKey
                    && value.IsServiceManaged
                    && SupportsProductServiceFileAdministration)
                {
                    QueueServerAdministrationSnapshot(value);
                }
                else if (SelectedWorkspaceTabKey == JavaRuntimeWorkspaceTabKey
                         && value.IsServiceManaged
                         && SupportsProductServiceFileAdministration)
                {
                    QueueServerAdministrationSnapshot(value);
                }
                SecondaryServer ??= Servers.FirstOrDefault(server => server.Id != value.Id) ?? value;
            }
        }
    }

    public ServerInstanceViewModel? SecondaryServer
    {
        get => _secondaryServer;
        set
        {
            if (!SetProperty(ref _secondaryServer, value)) return;
            OnPropertyChanged(nameof(IsSplitDiagnosticOutputVisible));
        }
    }

    public bool HasSelectedServer => SelectedServer is not null;
    public bool CanEditSelectedLocalConfiguration =>
        SelectedServer?.CanAccessLocalFiles == true;
    public bool IsSelectedServerPropertiesOperationRunning =>
        SelectedServer is { IsServiceManaged: true } server &&
        IsServiceServerPropertiesOperationRunning(server.Id);
    public bool CanReloadSelectedServerProperties => SelectedServer is { } server &&
        !IsSelectedServerPropertiesOperationRunning &&
        (server.CanAccessLocalFiles ||
         (server.IsServiceManaged && SupportsProductServicePropertiesEditor));
    public bool CanEditSelectedServerProperties => SelectedServer is { } server &&
        !IsSelectedServerPropertiesOperationRunning &&
        (server.CanAccessLocalFiles ||
         (server.IsServiceManaged &&
          SupportsProductServicePropertiesEditor &&
          _serviceServerPropertiesRevisions.ContainsKey(server.Id)));
    public bool CanSaveSelectedServerProperties =>
        CanEditSelectedServerProperties &&
        (SelectedServer is not { IsServiceManaged: true } serviceServer ||
         serviceServer.State is ServerState.Stopped or ServerState.Crashed or ServerState.Faulted);
    public bool CanBrowseSelectedServerFiles => SelectedServer is { } server
        && (server.CanAccessLocalFiles
            || (server.IsServiceManaged && SupportsProductServiceFileAdministration));
    public bool CanManageLocalRecoveryPoints =>
        !IsProductServiceRuntime && SelectedServer?.CanAccessLocalFiles == true;
    public bool CanRefreshSelectedPlayers => SelectedServer is { } server
        && (server.CanAccessLocalFiles
            || (server.IsServiceManaged && IsProductServiceConnected));
    public bool IsProductServiceRuntime => _productServiceController is not null;
    public bool IsProductServiceConnected =>
        _productServiceConnectionState == ProductServiceConnectionState.Connected;
    public bool ShowProductServiceUpdateAction =>
        _productServiceUpdateLauncher is not null &&
        _productServiceConnectionState == ProductServiceConnectionState.Incompatible;
    public bool IsProductServiceUpdateRunning
    {
        get => _isProductServiceUpdateRunning;
        private set
        {
            if (!SetProperty(ref _isProductServiceUpdateRunning, value)) return;
            UpdateProductServiceCommand.NotifyCanExecuteChanged();
        }
    }
    public ProductApiVersion? ProductServiceNegotiatedApiVersion =>
        _productServiceNegotiatedApiVersion;
    public bool SupportsProductServiceFileAdministration =>
        IsProductServiceConnected &&
        _productServiceNegotiatedApiVersion is { } version &&
        version.CompareTo(new ProductApiVersion(1, 5)) >= 0;
    public bool SupportsProductServicePropertiesEditor =>
        IsProductServiceConnected &&
        _productServiceNegotiatedApiVersion is { } version &&
        version.CompareTo(ProductApiProtocol.ServerPropertiesEditorVersion) >= 0;
    public bool KeepsRunningServersOnGuiExit => IsProductServiceRuntime;
    public string ProductServiceConnectionText => FormatProductServiceConnection(
        _productServiceConnectionState,
        _productServiceConnectionCode);
    public bool IsBulkSelectionMode
    {
        get => _isBulkSelectionMode;
        set
        {
            if (!SetProperty(ref _isBulkSelectionMode, value)) return;
            if (!value)
            {
                ClearBulkSelection();
            }
            NotifyCheckedServerCommandsCanExecuteChanged();
        }
    }

    public bool IsBatchLifecycleOperationRunning
    {
        get => _isBatchLifecycleOperationRunning;
        private set
        {
            if (!SetProperty(ref _isBatchLifecycleOperationRunning, value)) return;
            ToggleBulkSelectionModeCommand.NotifyCanExecuteChanged();
            NotifyCheckedServerCommandsCanExecuteChanged();
            RemoveServerCommand.NotifyCanExecuteChanged();
            DeleteServerCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasRunningServers => Servers.Any(server => server.State is ServerState.Starting or ServerState.Running or ServerState.Stopping);
    public bool HasActiveBackgroundJobs => _backgroundJobs.HasActiveJobs;
    public ReadOnlyObservableCollection<BackgroundServerJobViewModel> BackgroundJobItems => _backgroundJobs.Jobs;
    public string BackgroundJobSummary => _backgroundJobs.SummaryText;
    public string BackgroundJobActivity => _backgroundJobs.HasJobs
        ? $"{_backgroundJobs.SummaryText}｜{_backgroundJobs.LatestActivityText}"
        : L("main.vm.jobs.none");
    public double BackgroundJobProgress => _backgroundJobs.AggregateProgress;
    public bool IsBackgroundJobProgressIndeterminate => _backgroundJobs.IsAggregateProgressIndeterminate;
    public string BackgroundSchedulingProfile => _backgroundJobs.SchedulingProfileText;
    public RelayCommand ClearFinishedBackgroundJobsCommand => _backgroundJobs.ClearFinishedCommand;
    public RelayCommand CancelAllBackgroundJobsCommand => _backgroundJobs.CancelAllCommand;

    public bool IsSplitConsoleVisible
    {
        get => _isSplitConsoleVisible;
        set
        {
            if (!SetProperty(ref _isSplitConsoleVisible, value)) return;
            OnPropertyChanged(nameof(IsSplitDiagnosticOutputVisible));
        }
    }

    public bool IsSplitDiagnosticOutputVisible =>
        IsSplitConsoleVisible
        && SelectedServer?.SeparateDiagnosticOutput == true
        && SecondaryServer?.SeparateDiagnosticOutput == true;

    public string SelectedWorkspaceTabKey
    {
        get => _selectedWorkspaceTabKey;
        set
        {
            if (!SetProperty(ref _selectedWorkspaceTabKey, value)) return;
            if (value != AddonsWorkspaceTabKey)
            {
                CancelAddonScan();
            }
            if (value == PlayersWorkspaceTabKey && SelectedServer is { } server)
            {
                QueuePlayerRegistryLoadIfNeeded(server);
            }
            else if (value == JavaRuntimeWorkspaceTabKey)
            {
                CancelPlayerRegistryReload();
                if (SelectedServer is { IsServiceManaged: true } javaServer
                    && SupportsProductServiceFileAdministration)
                {
                    QueueServerAdministrationSnapshot(javaServer);
                }
                else
                {
                    _ = GuardAsync(RefreshJavaAsync, "main.vm.operation.scanJava");
                }
            }
            else if (value == AddonsWorkspaceTabKey && SelectedServer is { } addonServer)
            {
                CancelPlayerRegistryReload();
                if (addonServer.IsServiceManaged && SupportsProductServiceFileAdministration)
                {
                    QueueServerAdministrationSnapshot(addonServer);
                }
            }
            else
            {
                CancelPlayerRegistryReload();
            }
        }
    }

    public int SelectedJavaMajor
    {
        get => _selectedJavaMajor;
        set => SetProperty(ref _selectedJavaMajor, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            _statusMessageKey = null;
            _statusMessageArguments = [];
            SetProperty(ref _statusMessage, value);
        }
    }

    public string? BackgroundImagePath => SelectedServer?.BackgroundImagePath;
    public double WindowWidth => Math.Clamp(
        _previewWindowWidth
        ?? _settings.UserInterface?.WindowWidth
        ?? ManagerUiSettings.DefaultWindowWidth,
        ManagerUiSettings.MinimumPersistedWindowWidth,
        ManagerUiSettings.MaximumPersistedWindowWidth);
    public double WindowHeight => Math.Clamp(
        _previewWindowHeight
        ?? _settings.UserInterface?.WindowHeight
        ?? ManagerUiSettings.DefaultWindowHeight,
        ManagerUiSettings.MinimumPersistedWindowHeight,
        ManagerUiSettings.MaximumPersistedWindowHeight);
    public string ServerCountText => L("main.vm.serverCount", Servers.Count);
    public string RunningSummary => L(
        "main.vm.runningSummary",
        Servers.Count(server => server.State == ServerState.Running),
        Servers.Count);
    public string VersionText => $"X MCSV {ProductDisplayVersion} · .NET 10 · Windows x64";
    internal Task LastDiagnosticOutputPreferenceSave => _lastDiagnosticOutputPreferenceSave;
    internal Task LastPlayerRegistryReload => _lastPlayerRegistryReload;
    internal Task LastAutomaticMemoryRecommendation => _lastAutomaticMemoryRecommendation;
    internal Task LastAddonScan => _lastAddonScan;

    internal async Task PersistNormalWindowSizeAsync(double width, double height)
    {
        var normalizedWidth = Math.Round(Math.Clamp(
            width,
            ManagerUiSettings.MinimumPersistedWindowWidth,
            ManagerUiSettings.MaximumPersistedWindowWidth));
        var normalizedHeight = Math.Round(Math.Clamp(
            height,
            ManagerUiSettings.MinimumPersistedWindowHeight,
            ManagerUiSettings.MaximumPersistedWindowHeight));
        await _normalWindowSizePersistenceGate.WaitAsync();
        try
        {
            // Lock order is always normal-window gate -> settings-save gate. No other settings
            // writer acquires the normal-window gate, so this cannot invert with a regular save.
            await _settingsSaveGate.WaitAsync();
            try
            {
                if (_previewWindowWidth is not null || _previewWindowHeight is not null)
                {
                    return;
                }

                // Keep mutation, detached snapshot creation, file write and rollback in this one
                // critical section. A delayed resize can therefore never serialize an unrelated
                // settings transaction before that transaction has committed or rolled back.
                var userInterface = _settings.UserInterface ??= new ManagerUiSettings();
                if (Math.Abs(userInterface.WindowWidth - normalizedWidth) < 0.5
                    && Math.Abs(userInterface.WindowHeight - normalizedHeight) < 0.5)
                {
                    return;
                }

                var previousWidth = userInterface.WindowWidth;
                var previousHeight = userInterface.WindowHeight;
                userInterface.WindowWidth = normalizedWidth;
                userInterface.WindowHeight = normalizedHeight;
                try
                {
                    await SaveSettingsLockedAsync();
                }
                catch
                {
                    if (ReferenceEquals(_settings.UserInterface, userInterface))
                    {
                        userInterface.WindowWidth = previousWidth;
                        userInterface.WindowHeight = previousHeight;
                    }
                    throw;
                }
            }
            finally
            {
                _settingsSaveGate.Release();
            }
        }
        finally
        {
            _normalWindowSizePersistenceGate.Release();
        }
    }

    internal Task PersistGeneralSettingsValuesAsync(
        ManagerUiSettings userInterface,
        NewServerDefaultsSettings defaults,
        ApplicationAppearanceSettings appearance)
        => PersistGeneralSettingsValuesAsync(
            userInterface,
            defaults,
            (_settings.NewClientDefaults ?? new NewMinecraftClientDefaultsSettings()).Copy(),
            appearance);

    internal async Task PersistGeneralSettingsValuesAsync(
        ManagerUiSettings userInterface,
        NewServerDefaultsSettings defaults,
        NewMinecraftClientDefaultsSettings clientDefaults,
        ApplicationAppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(userInterface);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(clientDefaults);
        ArgumentNullException.ThrowIfNull(appearance);
        var nextUserInterface = userInterface.Copy();
        var nextDefaults = defaults.Copy();
        var nextClientDefaults = clientDefaults.Copy();
        var nextAppearance = appearance.Copy();

        await _settingsSaveGate.WaitAsync();
        try
        {
            var previousAppearance = _settings.Appearance;
            var previousUserInterface = _settings.UserInterface;
            var previousDefaults = _settings.NewServerDefaults;
            var previousClientDefaults = _settings.NewClientDefaults;
            _settings.Appearance = nextAppearance;
            _settings.UserInterface = nextUserInterface;
            _settings.NewServerDefaults = nextDefaults;
            _settings.NewClientDefaults = nextClientDefaults;
            _settings.SchemaVersion = Math.Max(
                _settings.SchemaVersion,
                ManagerSettings.CurrentSchemaVersion);
            try
            {
                await SaveSettingsLockedAsync();
            }
            catch
            {
                // Roll back before releasing the gate. A queued resize writer must never observe
                // and persist a settings-dialog transaction that did not commit.
                _settings.Appearance = previousAppearance;
                _settings.UserInterface = previousUserInterface;
                _settings.NewServerDefaults = previousDefaults;
                _settings.NewClientDefaults = previousClientDefaults;
                throw;
            }
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    public async Task InitializeAsync(bool allowInteractiveAutoImport = true)
    {
        _paths.EnsureCreated();
        _settings = await _settingsStore.LoadAsync() ?? new ManagerSettings();
        _settings.Appearance = _appearanceThemeService.Repair(_settings.Appearance);
        _settings.ServiceServerAppearances ??= [];
        var serviceAppearanceSettingsChanged = RepairServiceAppearancePreferences();
        var remoteAutoStartMigrated = ApplyRemoteAutoStartMigration(_settings);
        _settings.UserInterface ??= new ManagerUiSettings();
        _settings.NewServerDefaults ??= new NewServerDefaultsSettings();
        _settings.NewClientDefaults ??= new NewMinecraftClientDefaultsSettings();
        RepairManagerUiSettings(_settings.UserInterface);
        RepairNewServerDefaults(_settings.NewServerDefaults);
        RepairNewMinecraftClientDefaults(_settings.NewClientDefaults);
        foreach (var model in _settings.Instances)
        {
            RepairPortablePaths(model);
            model.BackgroundImageOpacity = double.IsFinite(model.BackgroundImageOpacity)
                ? Math.Clamp(model.BackgroundImageOpacity, 0, 1)
                : 0.25;
            if (_productServiceController is not null)
            {
                serviceAppearanceSettingsChanged |= CaptureInitialServiceAppearancePreference(model);
            }
        }
        if (_productServiceController is not null)
        {
            // Capture the repaired Preview 9 snapshot. It remains untouched until each
            // Service-owned copy transaction has committed and has its own durable receipt.
            _readOnlyLegacyInstances = CloneServerInstances(_settings.Instances);
        }
        string? remoteMigrationWarning = null;
        if (remoteAutoStartMigrated || serviceAppearanceSettingsChanged)
        {
            try
            {
                await _settingsStore.SaveAsync(
                    PrepareSettingsForPersistence(),
                    _applicationShutdownCancellation.Token);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Remote access is auxiliary. Keep the repaired in-memory policy and allow the
                // manager to finish loading even if this one migration write cannot complete.
                remoteMigrationWarning = L("main.vm.remote.migrationWriteFailed", error.Message);
            }
        }
        var modpackRecoverySummary = _productServiceController is null
            ? await RecoverPendingModpackUpdatesAsync(_applicationShutdownCancellation.Token)
            : null;
        // Application.Current becomes globally visible before App.xaml has finished loading its
        // ResourceDictionary. A headless/background consumer of this public ViewModel must never
        // write into that partially initialized WPF dictionary (or into any dispatcher-owned UI
        // resources from the wrong thread). Production startup reaches this code on the App
        // dispatcher; non-UI callers intentionally keep the repaired settings without applying
        // them to process-global visual resources.
        var application = Application.Current;
        if (application is not null && application.Dispatcher.CheckAccess())
        {
            _appearanceThemeService.Apply(application.Resources, _settings.Appearance);
            ApplyFontResources(application.Resources, _settings.UserInterface.FontSize);
        }

        OnPropertyChanged(nameof(WindowWidth));
        OnPropertyChanged(nameof(WindowHeight));

        string? productServiceStatus = null;
        if (_productServiceController is null)
        {
            foreach (var model in _settings.Instances.OrderBy(
                         instance => instance.Name,
                         StringComparer.CurrentCultureIgnoreCase))
            {
                _playerPresenceCoreTypes[model.Id] = model.CoreType;
                _instanceModels[model.Id] = model;
                Servers.Add(CreateServerViewModel(model));
            }
        }
        else
        {
            var serviceSnapshot = await _productServiceController.RefreshFocusedAsync(
                SelectedServer?.Id,
                _applicationShutdownCancellation.Token);
            ApplyProductServiceSnapshot(serviceSnapshot);
            var serviceIds = serviceSnapshot.Servers.Select(server => server.Summary.Id).ToHashSet();
            var pendingLegacyCount = (_readOnlyLegacyInstances ?? [])
                .Count(server => !serviceIds.Contains(server.Id));
            productServiceStatus = pendingLegacyCount == 0
                ? ProductServiceConnectionText
                : string.Join(
                    "；",
                    ProductServiceConnectionText,
                    LocalizationService.Current.Get("service.migration.pending", pendingLegacyCount));
            _productServicePollingTask = RunProductServicePollingAsync();
            _legacyServiceMigrationTask = Task.Run(
                () => MigrateLegacyServersAsync(_sessionServicesCancellation.Token),
                CancellationToken.None);
        }

        SelectedServer = Servers.FirstOrDefault();
        OnPropertyChanged(nameof(ServerCountText));
        OnPropertyChanged(nameof(RunningSummary));
        var remoteAccessStatus = await InitializeRemoteAccessAsync(application);
        if (_productServiceController is null
            && Servers.Count == 0
            && allowInteractiveAutoImport
            && await TryAutoDiscoverCurrentFolderAsync())
        {
            return;
        }

        await RefreshJavaAsync();

        var loadedStatus = Servers.Count > 0
            ? L("main.vm.status.loadedServers", Servers.Count)
            : _productServiceController is not null && IsProductServiceConnected
                ? LocalizationService.Current.Get("service.registry.empty")
                : _productServiceController is null
                    ? L("main.vm.status.noServersHint")
                    : null;
        StatusMessage = string.Join(
            "；",
            new[]
            {
                loadedStatus,
                productServiceStatus,
                modpackRecoverySummary,
                remoteAccessStatus,
                remoteMigrationWarning
            }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private async Task RunProductServicePollingAsync()
    {
        if (_productServiceController is null)
        {
            return;
        }

        var cancellationToken = _sessionServicesCancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                var snapshot = await _productServiceController.RefreshFocusedAsync(
                        SelectedServer?.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                {
                    continue;
                }

                await dispatcher.InvokeAsync(
                    () => ApplyProductServiceSnapshot(snapshot),
                    DispatcherPriority.Background,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal GUI shutdown. The Service and its Java processes remain running.
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
            // A late dispatcher teardown can race the idempotent disposal path.
        }
    }

    private bool CanUpdateProductService()
        => ShowProductServiceUpdateAction && !IsProductServiceUpdateRunning;

    internal async Task UpdateProductServiceAsync()
    {
        if (_productServiceUpdateLauncher is null ||
            _productServiceController is null ||
            !CanUpdateProductService())
        {
            return;
        }

        IsProductServiceUpdateRunning = true;
        SetStatus("main.vm.service.updateStarting");
        try
        {
            var result = await _productServiceUpdateLauncher
                .UpdateAsync(_applicationShutdownCancellation.Token);
            if (!result.Succeeded)
            {
                SetStatus(result.Outcome switch
                {
                    BundledProductServiceUpdateOutcome.Cancelled =>
                        "main.vm.service.updateCancelled",
                    BundledProductServiceUpdateOutcome.ReleaseLayoutUnavailable =>
                        "main.vm.service.updateReleaseUnavailable",
                    BundledProductServiceUpdateOutcome.PublisherVerificationFailed =>
                        "main.vm.service.updatePublisherRejected",
                    _ => "main.vm.service.updateFailed",
                });
                return;
            }

            SetStatus("main.vm.service.updateReconnecting");
            var deadline = DateTimeOffset.UtcNow.Add(ProductServiceUpdateReconnectTimeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                _applicationShutdownCancellation.Token.ThrowIfCancellationRequested();
                var snapshot = await _productServiceController.RefreshFocusedAsync(
                    SelectedServer?.Id,
                    _applicationShutdownCancellation.Token);
                ApplyProductServiceSnapshot(snapshot);
                if (snapshot.Connection.IsConnected &&
                    _productServiceNegotiatedApiVersion is { } apiVersion &&
                    apiVersion.CompareTo(ProductApiProtocol.MinecraftEulaConsentVersion) >= 0)
                {
                    SetStatus("main.vm.service.updateCompleted");
                    return;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(500),
                    _applicationShutdownCancellation.Token);
            }

            SetStatus("main.vm.service.updateReconnectFailed");
        }
        catch (OperationCanceledException) when (_applicationShutdownCancellation.IsCancellationRequested)
        {
            // Application shutdown does not cancel or weaken the already elevated updater. The
            // Service remains the sole owner of Java processes and the next GUI launch reprobes it.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _ = error;
            SetStatus("main.vm.service.updateFailed");
        }
        finally
        {
            IsProductServiceUpdateRunning = false;
        }
    }

    private void ApplyProductServiceSnapshot(ProductServiceDesktopSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var previousState = _productServiceConnectionState;
        var previousCode = _productServiceConnectionCode;
        var previousNegotiatedApiVersion = _productServiceNegotiatedApiVersion;
        _productServiceConnectionState = snapshot.Connection.State;
        _productServiceConnectionCode = snapshot.Connection.Code;
        _productServiceNegotiatedApiVersion = NegotiateProductServiceApiVersion(snapshot.Connection);
        if (previousState != _productServiceConnectionState ||
            previousNegotiatedApiVersion != _productServiceNegotiatedApiVersion)
        {
            foreach (var serverId in _serviceServerPropertiesRevisions.Keys)
            {
                _serviceServerPropertiesRevisions.TryRemove(serverId, out _);
            }
        }
        if (previousNegotiatedApiVersion != _productServiceNegotiatedApiVersion)
        {
            OnPropertyChanged(nameof(ProductServiceNegotiatedApiVersion));
            OnPropertyChanged(nameof(SupportsProductServiceFileAdministration));
            OnPropertyChanged(nameof(SupportsProductServicePropertiesEditor));
            OnPropertyChanged(nameof(CanBrowseSelectedServerFiles));
            OnPropertyChanged(nameof(CanReloadSelectedServerProperties));
            OnPropertyChanged(nameof(CanEditSelectedServerProperties));
            OnPropertyChanged(nameof(CanSaveSelectedServerProperties));
            OpenSelectedFolderCommand.NotifyCanExecuteChanged();
            CheckAddonUpdatesCommand.NotifyCanExecuteChanged();
            OpenAddonFolderCommand.NotifyCanExecuteChanged();
            DeleteServerCommand.NotifyCanExecuteChanged();
            ReloadPropertiesCommand.NotifyCanExecuteChanged();
            SavePropertiesCommand.NotifyCanExecuteChanged();
        }
        if (previousState != _productServiceConnectionState ||
            !string.Equals(previousCode, _productServiceConnectionCode, StringComparison.Ordinal))
        {
            foreach (var server in Servers.Where(static server => server.IsServiceManaged))
            {
                server.IsControlChannelAvailable = IsProductServiceConnected;
            }
            OnPropertyChanged(nameof(IsProductServiceConnected));
            OnPropertyChanged(nameof(ShowProductServiceUpdateAction));
            OnPropertyChanged(nameof(SupportsProductServiceFileAdministration));
            OnPropertyChanged(nameof(SupportsProductServicePropertiesEditor));
            OnPropertyChanged(nameof(CanBrowseSelectedServerFiles));
            OnPropertyChanged(nameof(CanReloadSelectedServerProperties));
            OnPropertyChanged(nameof(CanEditSelectedServerProperties));
            OnPropertyChanged(nameof(CanSaveSelectedServerProperties));
            OnPropertyChanged(nameof(ProductServiceConnectionText));
            OnPropertyChanged(nameof(CanRefreshSelectedPlayers));
            NotifySelectedLifecycleCommandsCanExecuteChanged();
            RestartSelectedCommand.NotifyCanExecuteChanged();
            NotifyCheckedServerCommandsCanExecuteChanged();
            NotifyCreateOrImportCommandsCanExecuteChanged();
            UpdateSelectedModpackCommand.NotifyCanExecuteChanged();
            OpenRemoteManagementCommand.NotifyCanExecuteChanged();
            OpenRemoteWebConsoleCommand.NotifyCanExecuteChanged();
            RemoveServerCommand.NotifyCanExecuteChanged();
            DeleteServerCommand.NotifyCanExecuteChanged();
            OpenSelectedFolderCommand.NotifyCanExecuteChanged();
            CheckAddonUpdatesCommand.NotifyCanExecuteChanged();
            OpenAddonFolderCommand.NotifyCanExecuteChanged();
            SaveSelectedSettingsCommand.NotifyCanExecuteChanged();
            ReloadPropertiesCommand.NotifyCanExecuteChanged();
            SavePropertiesCommand.NotifyCanExecuteChanged();
            RefreshPlayersCommand.NotifyCanExecuteChanged();
            NotifySelectedServiceDependentCommandsCanExecuteChanged();
            UpdateProductServiceCommand.NotifyCanExecuteChanged();
            if (!IsProductServiceUpdateRunning)
            {
                StatusMessage = ProductServiceConnectionText;
            }
            if (snapshot.Connection.IsConnected
                && SelectedWorkspaceTabKey == PlayersWorkspaceTabKey
                && SelectedServer is { IsServiceManaged: true } selected)
            {
                QueuePlayerRegistryLoadIfNeeded(selected);
            }
            if (snapshot.Connection.IsConnected
                && SelectedWorkspaceTabKey == AddonsWorkspaceTabKey
                && SelectedServer is { IsServiceManaged: true } addonServer
                && SupportsProductServiceFileAdministration)
            {
                QueueServerAdministrationSnapshot(addonServer);
            }
            if (snapshot.Connection.IsConnected
                && SelectedWorkspaceTabKey == JavaRuntimeWorkspaceTabKey
                && SelectedServer is { IsServiceManaged: true } javaServer
                && SupportsProductServiceFileAdministration)
            {
                QueueServerAdministrationSnapshot(javaServer);
            }
        }

        if (!snapshot.Connection.IsConnected)
        {
            return;
        }

        var serviceIds = snapshot.Servers.Select(server => server.Summary.Id).ToHashSet();
        foreach (var stale in Servers.Where(server => !serviceIds.Contains(server.Id)).ToArray())
        {
            Servers.Remove(stale);
            _serviceServerPropertiesRevisions.TryRemove(stale.Id, out _);
            RemoveServiceServerPropertiesState(stale.Id);
            _dirtyProductServiceRegistrations.Remove(stale.Id);
            _instanceModels.TryRemove(stale.Id, out _);
            _playerPresenceCoreTypes.TryRemove(stale.Id, out _);
        }

        var localMetadata = _settings.Instances
            .GroupBy(server => server.Id)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var projection in snapshot.Servers)
        {
            var server = Servers.FirstOrDefault(item => item.Id == projection.Summary.Id);
            if (server is null)
            {
                var hasLocalMetadata = localMetadata.TryGetValue(projection.Summary.Id, out var model);
                model ??= CreateServiceProjectionModel(projection.Registration);
                UpdateServiceProjectionMetadata(model, projection.Registration);
                ApplyServiceAppearancePreference(model);
                _instanceModels[model.Id] = model;
                _playerPresenceCoreTypes[model.Id] = model.CoreType;
                server = CreateServerViewModel(
                    model,
                    isServiceManaged: true,
                    hasLocalMetadata: hasLocalMetadata);
                Servers.Add(server);
            }
            else if (!_dirtyProductServiceRegistrations.Contains(server.Id))
            {
                _applyingProductServiceProjection = true;
                try
                {
                    UpdateServiceProjectionMetadata(server.Model, projection.Registration);
                    server.NotifyServiceRegistrationChanged();
                }
                finally
                {
                    _applyingProductServiceProjection = false;
                }
            }

            ApplyProductServiceStatus(server, projection.Status);
            ApplyProductServicePresence(server, projection.Status, projection.Console.Entries);
            foreach (var entry in projection.Console.Entries)
            {
                if (SaveCompletionPattern.IsMatch(entry.Text)
                    && _pendingSaveFlushes.TryGetValue(
                        (projection.Summary.Id, entry.SessionId),
                        out var saveCompletion))
                {
                    saveCompletion.TrySetResult();
                }
            }
            var consoleLines = projection.Console.Entries.Select(MapProductConsoleLine).ToArray();
            if (projection.ReplaceConsole)
            {
                server.ReplaceConsoleBatch(consoleLines);
            }
            else if (consoleLines.Length > 0)
            {
                server.AppendConsoleBatch(consoleLines);
            }
        }

        if (SelectedServer is null || !Servers.Contains(SelectedServer))
        {
            SelectedServer = Servers.FirstOrDefault();
        }
        if (SecondaryServer is null || !Servers.Contains(SecondaryServer))
        {
            SecondaryServer = Servers.FirstOrDefault(server => server != SelectedServer)
                              ?? SelectedServer;
        }

        OnPropertyChanged(nameof(ServerCountText));
        OnPropertyChanged(nameof(RunningSummary));
        OnPropertyChanged(nameof(HasRunningServers));
    }

    private static ProductApiVersion? NegotiateProductServiceApiVersion(
        ProductServiceConnectionResult connection)
    {
        if (!connection.IsConnected || connection.Handshake is null)
        {
            return null;
        }

        var protocol = connection.Handshake.Protocol;
        if (!protocol.Ready)
        {
            return null;
        }

        var negotiation = ProductApiProtocol.Negotiate(
            protocol.MinimumApiVersion,
            protocol.ApiVersion);
        return negotiation.IsCompatible
            ? negotiation.SelectedVersion
            : null;
    }

    private ServerInstance CreateServiceProjectionModel(ProductServerRegistration registration)
        => new()
        {
            Id = registration.Id,
            DirectoryPath = Path.Combine(
                _paths.Root,
                ".service-projection",
                registration.Id.ToString("N")),
            SeparateDiagnosticOutput = true,
        };

    private static void UpdateServiceProjectionMetadata(
        ServerInstance model,
        ProductServerRegistration registration)
    {
        model.Id = registration.Id;
        model.Name = registration.Name;
        model.JavaExecutablePath = registration.JavaRuntimePath;
        model.JavaMajorVersion = TryInferJavaMajorVersion(registration.JavaRuntimePath);
        model.LaunchKind = (ServerLaunchKind)registration.LaunchKind;
        model.ServerJarPath = registration.ServerJarPath;
        model.JavaArgumentFilePaths = registration.JavaArgumentFilePaths.ToList();
        model.CoreType = ParseProductCoreType(registration.CoreType);
        model.MinecraftVersion = registration.MinecraftVersion;
        model.MinimumMemoryMb = registration.MinimumMemoryMb;
        model.MaximumMemoryMb = registration.MaximumMemoryMb;
        model.MemoryAllocationMode = MemoryAllocationMode.Manual;
        model.JvmArguments = registration.JvmArguments.ToList();
        model.ServerArguments = registration.ServerArguments.ToList();
        model.StopCommand = registration.StopCommand;
        model.Port = registration.Port;
        model.AutoRestart = registration.AutoRestart;
        model.EnableHangWatchdog = false;
        model.EnableAutomaticRecoveryPoints = false;
        model.ModpackProviderId = registration.ModpackProviderId;
        model.ModpackSource = (ModpackSourceKind)registration.ModpackSource;
        model.ModpackProjectId = registration.ModpackProjectId;
        model.ModpackVersionId = registration.ModpackVersionId;
        model.ModpackVersionName = registration.ModpackVersionName;
        model.IsInstallerArtifact = registration.IsInstallerArtifact;
    }

    private static int? TryInferJavaMajorVersion(string? runtimePath)
    {
        if (string.IsNullOrWhiteSpace(runtimePath)) return null;

        var match = Regex.Match(
            runtimePath,
            @"(?:^|[\\/])temurin-(?:jre-|jdk-)?(?<major>\d{1,3})(?:-|[\\/]|$)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100));
        return match.Success
               && int.TryParse(match.Groups["major"].Value, out var major)
               && JavaVersionRecommendationService.SupportedMajorVersions.Contains(major)
            ? major
            : null;
    }

    private static CoreType ParseProductCoreType(string? value)
        => Enum.TryParse<CoreType>(value, ignoreCase: true, out var coreType) && Enum.IsDefined(coreType)
            ? coreType
            : CoreType.Unknown;

    private static void ApplyProductServiceStatus(
        ServerInstanceViewModel server,
        ProductServerStatus status)
    {
        server.SetState((ServerState)status.Server.State);
        if (status.Server.State is ProductServerState.Starting
            or ProductServerState.Running
            or ProductServerState.Stopping)
        {
            server.MarkPortAsActive(status.Server.Port);
        }
        if (status.Resource is { } resource && status.Server.State == ProductServerState.Running)
        {
            server.UpdateMetrics(resource.CpuPercent, resource.WorkingSetBytes, resource.Uptime);
        }
    }

    private void ApplyProductServicePresence(
        ServerInstanceViewModel server,
        ProductServerStatus status,
        IReadOnlyList<ProductConsoleEntry> entries)
    {
        if (status.SessionId is not { } sessionId ||
            status.Server.State is not (ProductServerState.Starting or ProductServerState.Running))
        {
            _playerPresenceSessions.Remove(server.Id);
            server.UpdateOnlinePlayers([]);
            return;
        }

        if (!_playerPresenceSessions.TryGetValue(server.Id, out var previousSession)
            || previousSession != sessionId)
        {
            _playerPresenceSessions[server.Id] = sessionId;
            server.UpdateOnlinePlayers([]);
        }

        var coreType = server.Model.CoreType;
        foreach (var entry in entries)
        {
            if (entry.SessionId != sessionId ||
                entry.Stream is not (ProductConsoleStream.StandardOutput
                    or ProductConsoleStream.StandardError) ||
                !PlayerPresenceEventParser.TryParse(entry.Text, coreType, out var change))
            {
                continue;
            }

            server.UpdatePlayerPresence(change.PlayerName, change.IsOnline);
        }
    }

    private static ConsoleLine MapProductConsoleLine(ProductConsoleEntry entry)
        => new(
            entry.Timestamp,
            entry.Text,
            (ConsoleStream)entry.Stream)
        {
            SessionId = entry.SessionId,
            Severity = (ConsoleLineSeverity)entry.Severity,
            DiagnosticId = entry.DiagnosticId,
            IsDiagnosticContinuation = entry.IsDiagnosticContinuation,
        };

    private static string FormatProductServiceConnection(
        ProductServiceConnectionState state,
        string code)
        => ProductServiceStatusLocalizer.Format(state, code);

    private void EnsureProductServiceConnected()
    {
        if (_productServiceController is null)
        {
            return;
        }

        if (!IsProductServiceConnected)
        {
            throw new InvalidOperationException(ProductServiceConnectionText);
        }
    }

    private async Task<T> ExecuteProductServiceOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProductServiceClientException error)
        {
            throw new InvalidOperationException(
                FormatProductServiceOperationError(error.Code),
                error);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                L("main.vm.service.operationFailed"),
                error);
        }
    }

    private static string FormatProductServiceOperationError(string? code)
        => code switch
        {
            "service.timeout" or "service.connection_failed" =>
                L("main.vm.service.noResponse"),
            "service.access_denied" =>
                L("main.vm.service.accessDenied"),
            "service.not_ready" =>
                L("main.vm.service.notReady"),
            "protocol.schema_unsupported" or "protocol.version_incompatible" =>
                L("main.vm.service.incompatible"),
            "server.not_found" =>
                L("main.vm.service.serverNotFound"),
            "server.properties_changed" =>
                L("main.vm.service.propertiesChanged"),
            _ =>
                L("main.vm.service.rejected"),
        };

    private Task DispatchProductServiceStatusAsync(
        ProductServerStatus status,
        CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            var server = Servers.FirstOrDefault(item => item.Id == status.Server.Id);
            if (server is not null)
            {
                ApplyProductServiceStatus(server, status);
                OnPropertyChanged(nameof(RunningSummary));
                OnPropertyChanged(nameof(HasRunningServers));
            }
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(
                () =>
                {
                    var server = Servers.FirstOrDefault(item => item.Id == status.Server.Id);
                    if (server is null) return;
                    ApplyProductServiceStatus(server, status);
                    OnPropertyChanged(nameof(RunningSummary));
                    OnPropertyChanged(nameof(HasRunningServers));
                },
                DispatcherPriority.Send,
                cancellationToken)
            .Task;
    }

    internal static bool ApplyRemoteAutoStartMigration(ManagerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var remoteSettingsWereMissing = settings.RemoteControl is null;
        settings.RemoteControl ??= new RemoteControlSettings();
        var changed = remoteSettingsWereMissing
                      || settings.SchemaVersion < ManagerSettings.CurrentSchemaVersion
                      || !settings.RemoteControl.Enabled;
        settings.SchemaVersion = Math.Max(
            settings.SchemaVersion,
            ManagerSettings.CurrentSchemaVersion);
        settings.RemoteControl.Enabled = true;
        return changed;
    }

    public Task ShutdownAsync()
    {
        lock (_shutdownSync)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            if (_isDisposed)
            {
                return Task.CompletedTask;
            }

            return _shutdownTask = RunShutdownAttemptAsync();
        }
    }

    private async Task RunShutdownAttemptAsync()
    {
        // Ensure the task is stored before a synchronously failing stage can clear it for retry.
        await Task.Yield();
        try
        {
            await ShutdownCoreAsync();
        }
        catch
        {
            lock (_shutdownSync)
            {
                _shutdownTask = null;
            }

            throw;
        }
    }

    private async Task ShutdownCoreAsync()
    {
        if (_isDisposed) return;
        var remoteCleanup = EnsureRemoteAccessStoppedForApplicationExitAsync();
        await CancelAndWaitForBatchLifecycleOperationAsync();
        await WaitForModpackUpdatesToFinishAsync();
        await remoteCleanup;
        await _backgroundJobs.CancelAndWaitAsync();
        await StopAllServersCoordinatedAsync(CancellationToken.None);
        await WaitForPendingModpackHealthActionsAsync();
        await SaveSettingsAsync();
        await DisposeAsync();
    }

    public ValueTask DisposeAsync()
    {
        lock (_shutdownSync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            if (_isDisposed)
            {
                return ValueTask.CompletedTask;
            }

            return new ValueTask(_disposeTask = RunDisposeAttemptAsync());
        }
    }

    private async Task RunDisposeAttemptAsync()
    {
        // Match ShutdownAsync: successful cleanup is cached, while a failed attempt may be
        // retried after the caller fixes the transient resource that blocked disposal.
        await Task.Yield();
        try
        {
            await DisposeCoreAsync();
            lock (_shutdownSync)
            {
                _isDisposed = true;
            }
        }
        catch
        {
            lock (_shutdownSync)
            {
                _disposeTask = null;
            }

            throw;
        }
    }

    private async Task DisposeCoreAsync()
    {
        ClientWorkspace.ContentDownloadCenterRequested -= OnContentDownloadCenterRequested;
        if (_contentDownloadCenterWindow is { IsLoaded: true } contentWindow)
        {
            contentWindow.Close();
            _contentDownloadCenterWindow = null;
        }
        await ClientWorkspace.DisposeAsync();
        LocalizationService.Current.CultureChanged -= OnLocalizationCultureChanged;
        Servers.CollectionChanged -= OnServersCollectionChanged;
        foreach (var server in _bulkSelectionSubscriptions)
        {
            server.PropertyChanged -= OnBulkSelectionServerPropertyChanged;
        }
        _bulkSelectionSubscriptions.Clear();
        if (_selectedServer is not null)
        {
            _selectedServer.PropertyChanged -= OnSelectedServerPropertyChanged;
        }
        var remoteCleanup = EnsureRemoteAccessStoppedForApplicationExitAsync();
        await CancelAndWaitForBatchLifecycleOperationAsync();
        await WaitForModpackUpdatesToFinishAsync();
        await remoteCleanup;
        RemoteAccessCoordinator? remoteCoordinator;
        lock (_remoteAccessLifecycleSync)
        {
            remoteCoordinator = _remoteAccessCoordinator;
        }
        if (remoteCoordinator is not null)
        {
            remoteCoordinator.StateChanged -= OnRemoteAccessStateChanged;
            try
            {
                await remoteCoordinator.DisposeAsync();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                SetStatus("main.vm.remote.cleanupFailed", error.Message);
            }
            finally
            {
                lock (_remoteAccessLifecycleSync)
                {
                    if (ReferenceEquals(_remoteAccessCoordinator, remoteCoordinator))
                    {
                        _remoteAccessCoordinator = null;
                    }
                }
            }
        }
        _sessionServicesCancellation.Cancel();
        try
        {
            await Task.WhenAll(_productServicePollingTask, _legacyServiceMigrationTask);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _ = error;
            // Polling is observational only. A failed final read must not prevent GUI shutdown
            // or affect the Service-owned Java processes.
        }
        CancelPlayerRegistryReload();
        await _lastPlayerRegistryReload;
        _backgroundJobs.PropertyChanged -= OnBackgroundJobsPropertyChanged;
        await _backgroundJobs.DisposeAsync();
        _processManager.ConsoleLineReceived -= OnConsoleLineReceived;
        _processManager.StateChanged -= OnServerStateChanged;
        _processManager.ResourceSampled -= OnResourceSampled;
        foreach (var validation in _pendingModpackHealthValidations.Values)
        {
            validation.CancelSessionProbe();
        }
        await _processManager.DisposeAsync();
        await WaitForPendingModpackHealthActionsAsync();
        foreach (var validation in _pendingModpackHealthValidations.Values)
        {
            validation.Dispose();
        }
        _pendingModpackHealthValidations.Clear();
        if (_productServiceController is not null)
        {
            await _productServiceController.DisposeAsync();
        }
        var backgroundTasks = _watchdogTasks.Values
            .Concat(_recoveryPointTasks.Values)
            .Concat(_crashReportTasks.Values)
            .Concat(_automaticMemoryRecommendationTasks.Values)
            .Distinct()
            .ToArray();
        if (backgroundTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(backgroundTasks);
            }
            catch (OperationCanceledException) when (_sessionServicesCancellation.IsCancellationRequested)
            {
                // Expected during manager shutdown.
            }
        }
        if (_onlineModpackWorkflow is IAsyncDisposable asyncDisposableWorkflow)
        {
            await asyncDisposableWorkflow.DisposeAsync();
        }
        else if (_onlineModpackWorkflow is IDisposable disposableWorkflow)
        {
            disposableWorkflow.Dispose();
        }
        if (_ownedCoreServerCreationWorkflow is IAsyncDisposable asyncDisposableCoreWorkflow)
        {
            await asyncDisposableCoreWorkflow.DisposeAsync();
        }
        else if (_ownedCoreServerCreationWorkflow is IDisposable disposableCoreWorkflow)
        {
            disposableCoreWorkflow.Dispose();
        }
        // Keep settings persistence alive until every retryable, user-supplied workflow has
        // disposed. If one fails, a second Shutdown attempt must still be able to save settings.
        if (_settingsStore is IDisposable disposableSettingsStore)
        {
            disposableSettingsStore.Dispose();
        }
        _javaHttpClient.Dispose();
        _modrinthHttpClient.Dispose();
        _portAssignmentGate.Dispose();
        _serverRegistryGate.Dispose();
        _settingsSaveGate.Dispose();
        _normalWindowSizePersistenceGate.Dispose();
        foreach (var gate in _backupGates.Values)
        {
            gate.Dispose();
        }
        foreach (var gate in _lifecycleGates.Values)
        {
            gate.Dispose();
        }
        _applicationShutdownCancellation.Dispose();
        _sessionServicesCancellation.Dispose();
    }

    private void OnLocalizationCultureChanged(object? sender, EventArgs eventArgs)
    {
        if (_isDisposed) return;
        // SetCulture is a synchronous setting mutation. Raising the scalar notification on the
        // same thread guarantees the service status cannot remain in the previous language even
        // in headless hosts where an Application dispatcher exists but is not being pumped.
        OnPropertyChanged(nameof(ProductServiceConnectionText));
        OnPropertyChanged(nameof(BackgroundJobActivity));
        OnPropertyChanged(nameof(ServerCountText));
        OnPropertyChanged(nameof(RunningSummary));
        if (_statusMessageKey is { } key)
        {
            SetProperty(
                ref _statusMessage,
                L(key, _statusMessageArguments),
                nameof(StatusMessage));
        }
    }

    private ServerInstanceViewModel CreateServerViewModel(
        ServerInstance model,
        bool isServiceManaged = false,
        bool hasLocalMetadata = true)
    {
        var viewModel = new ServerInstanceViewModel(
            model,
            SendCommandAsync,
            OnDiagnosticOutputPreferenceChanged,
            configuredOneDriveRoots: null,
            memoryModeRequested: OnServerMemoryModeRequested,
            isServiceManaged: isServiceManaged,
            hasLocalMetadata: hasLocalMetadata);
        viewModel.IsControlChannelAvailable = !isServiceManaged || IsProductServiceConnected;
        var memory = _systemMemoryProbe.GetSnapshot();
        viewModel.SetSystemMemoryDisplay(memory.AvailablePhysicalBytes, memory.TotalPhysicalBytes);
        var availableMb = (int)Math.Clamp(memory.AvailablePhysicalBytes / 1024L / 1024L, 2048, 131072);
        viewModel.SetMemorySliderMaximum(Math.Max(2048, availableMb * 4 / 5));
        return viewModel;
    }

    private void OnServerMemoryModeRequested(
        ServerInstanceViewModel server,
        MemoryAllocationMode mode)
    {
        if (!Servers.Contains(server)) return;
        if (_automaticMemoryRecommendationCancellations.TryGetValue(server.Id, out var previous))
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A completed background scan can dispose its source between lookup and cancel.
            }
        }

        if (mode != MemoryAllocationMode.Automatic)
        {
            server.CancelAutomaticMemoryRecommendation();
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _sessionServicesCancellation.Token);
        _automaticMemoryRecommendationCancellations[server.Id] = cancellation;
        server.BeginAutomaticMemoryRecommendation();
        var task = RecalculateAutomaticMemoryAsync(server, cancellation);
        _automaticMemoryRecommendationTasks[server.Id] = task;
        _lastAutomaticMemoryRecommendation = task;
    }

    private async Task RecalculateAutomaticMemoryAsync(
        ServerInstanceViewModel server,
        CancellationTokenSource owner)
    {
        try
        {
            var recommendation = await _memoryRecommendationService.RecommendAsync(
                    server.DirectoryPath,
                    owner.Token)
                .ConfigureAwait(false);
            await DispatchMemoryRecommendationAsync(() =>
            {
                if (IsCurrentAutomaticMemoryRequest(server, owner))
                {
                    server.ApplyAutomaticMemoryRecommendation(recommendation);
                    SetStatus("main.vm.memory.recalculated", server.Name);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
            // A newer mode/scan or application shutdown owns the visible state.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await DispatchMemoryRecommendationAsync(() =>
            {
                if (IsCurrentAutomaticMemoryRequest(server, owner))
                {
                    server.FailAutomaticMemoryRecommendation(error.Message);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            if (_automaticMemoryRecommendationCancellations.TryGetValue(
                    server.Id,
                    out var current)
                && ReferenceEquals(current, owner))
            {
                _automaticMemoryRecommendationCancellations.TryRemove(server.Id, out _);
            }

            owner.Dispose();
        }
    }

    private bool IsCurrentAutomaticMemoryRequest(
        ServerInstanceViewModel server,
        CancellationTokenSource owner)
        => !owner.IsCancellationRequested
           && server.MemoryAllocationMode == MemoryAllocationMode.Automatic
           && _automaticMemoryRecommendationCancellations.TryGetValue(server.Id, out var current)
           && ReferenceEquals(current, owner);

    private static async Task DispatchMemoryRecommendationAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await dispatcher.InvokeAsync(action, DispatcherPriority.DataBind);
    }

    internal async Task ImportExistingServerAsync()
    {
        var owner = GetAccessibleMainWindow();
        var choice = await _existingServerImportCoordinator.ChooseAndImportAsync(owner);
        if (choice is null)
        {
            SetStatus("main.vm.status.importExistingCancelled");
        }
    }

    private async Task MigrateLegacyServersAsync(CancellationToken cancellationToken)
    {
        if (_productServiceController is null)
        {
            return;
        }

        var legacy = CloneServerInstances(_readOnlyLegacyInstances ?? []);
        if (legacy.Count == 0)
        {
            return;
        }

        var failures = new List<string>();
        var migrated = 0;
        try
        {
            var initial = await _productServiceController.RefreshFocusedAsync(null, cancellationToken)
                .ConfigureAwait(false);
            if (!initial.Connection.IsConnected)
            {
                return;
            }

            var registered = initial.Servers.Select(value => value.Summary.Id).ToHashSet();
            foreach (var model in legacy.Where(value => !registered.Contains(value.Id)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    RepairPortablePaths(model);
                    EnsureConcreteJavaForServiceImport(model);
                    _ = await _productServiceController.ImportAsync(
                            model,
                            $"preview9:{model.Id:N}",
                            cancellationToken)
                        .ConfigureAwait(false);
                    registered.Add(model.Id);
                    migrated++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    failures.Add($"{model.Name}: {error.Message}");
                }
            }

            var refreshed = await _productServiceController.RefreshFocusedAsync(null, cancellationToken)
                .ConfigureAwait(false);
            await ApplyProductServiceSnapshotOnUiAsync(refreshed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (migrated > 0 || failures.Count > 0)
        {
            await SetStatusMessageOnUiAsync(
                    failures.Count == 0
                        ? "main.vm.service.migrationCompleted"
                        : "main.vm.service.migrationPartial",
                    failures.Count == 0
                        ? [migrated]
                        : [migrated, failures.Count, failures[0]],
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ImportIntoProductServiceAsync(
        ServerInstance model,
        string migrationKey,
        bool applyNewServerDefaults,
        bool persistLocalMetadata,
        CancellationToken cancellationToken)
    {
        var controller = _productServiceController
            ?? throw new InvalidOperationException(L("main.vm.error.serviceImportUnavailable"));
        if (!IsProductServiceConnected)
        {
            throw new InvalidOperationException(ProductServiceConnectionText);
        }

        RepairPortablePaths(model);
        EnsureConcreteJavaForServiceImport(model);
        if (applyNewServerDefaults)
        {
            ApplyNewServerDefaults(model);
        }

        _ = await controller.ImportAsync(model, migrationKey, cancellationToken).ConfigureAwait(false);
        if (persistLocalMetadata)
        {
            await PersistServiceMetadataAsync(model, cancellationToken).ConfigureAwait(false);
        }

        var snapshot = await controller.RefreshFocusedAsync(model.Id, cancellationToken)
            .ConfigureAwait(false);
        await ApplyProductServiceSnapshotOnUiAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureConcreteJavaForServiceImport(ServerInstance model)
    {
        if (!string.IsNullOrWhiteSpace(model.JavaExecutablePath) &&
            File.Exists(model.JavaExecutablePath))
        {
            return;
        }

        var major = model.JavaMajorVersion
                    ?? _javaRecommendations.GetRecommendation(
                        model.MinecraftVersion,
                        model.CoreType).MajorVersion;
        model.JavaMajorVersion = major;
        model.JavaExecutablePath = model.LaunchKind == ServerLaunchKind.JavaArgumentFiles
            ? FindBundledJavaExecutable(model.DirectoryPath, major) ?? FindManagedJavaExecutable(major)
            : FindManagedJavaExecutable(major);
        if (string.IsNullOrWhiteSpace(model.JavaExecutablePath) ||
            !File.Exists(model.JavaExecutablePath))
        {
            throw new FileNotFoundException(
                L("main.vm.service.migrationJavaMissing", major));
        }
    }

    private async Task PersistServiceMetadataAsync(
        ServerInstance model,
        CancellationToken cancellationToken)
    {
        await _settingsSaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _readOnlyLegacyInstances ??= [];
            CaptureInitialServiceAppearancePreference(model);
            var snapshot = CloneServerInstances([model])[0];
            var index = _readOnlyLegacyInstances.ToList().FindIndex(value => value.Id == model.Id);
            var updated = _readOnlyLegacyInstances.ToList();
            if (index >= 0)
            {
                updated[index] = snapshot;
            }
            else
            {
                updated.Add(snapshot);
            }

            _readOnlyLegacyInstances = updated;
            _settings.SchemaVersion = Math.Max(
                _settings.SchemaVersion,
                ManagerSettings.CurrentSchemaVersion);
            _settings.Instances = CloneServerInstances(updated);
            await _settingsStore.SaveAsync(
                    PrepareSettingsForPersistence(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private async Task ApplyProductServiceSnapshotOnUiAsync(
        ProductServiceDesktopSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyProductServiceSnapshot(snapshot);
            return;
        }

        await dispatcher.InvokeAsync(
            () => ApplyProductServiceSnapshot(snapshot),
            DispatcherPriority.DataBind,
            cancellationToken);
    }

    private async Task SetStatusMessageOnUiAsync(
        string key,
        object?[] arguments,
        CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SetStatus(key, arguments);
            return;
        }

        await dispatcher.InvokeAsync(
            () => SetStatus(key, arguments),
            DispatcherPriority.Background,
            cancellationToken);
    }

    private static Window? GetAccessibleMainWindow()
    {
        var application = Application.Current;
        return application is not null && application.Dispatcher.CheckAccess()
            ? application.MainWindow
            : null;
    }

    private async Task ImportServerAsync()
    {
        var picker = new OpenFileDialog
        {
            Title = L("main.vm.filePicker.serverJarTitle"),
            Filter = L("main.vm.filePicker.jarFilter"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (PrimaryDisplayWindowPlacement.ShowDialogOnProductDisplay(picker) != true) return;

        SetStatus("main.vm.status.analyzingJar");
        var detection = await _coreDetector.DetectAsync(picker.FileName);
        if (!detection.IsValidJar)
        {
            throw new InvalidDataException(detection.Error ?? L("main.vm.error.invalidJar"));
        }

        var recommendation = _javaRecommendations.GetRecommendation(detection.MinecraftVersion, detection.CoreType);
        var dialog = new ImportServerDialog(detection, recommendation) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        var serverName = dialog.ServerName.Trim();
        var stagingDirectory = SafePath.CombineUnderRoot(
            _paths.Servers,
            $".importing-jar-{Guid.NewGuid():N}");
        string? ownedStagingDirectory = stagingDirectory;
        string? ownedFinalDirectory = null;
        SafePathObjectIdentityLease? ownedFinalIdentityLease = null;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            SafePath.EnsureNoReparsePointsUnderRoot(_paths.Servers, stagingDirectory);
            var isInstaller = detection.CoreType is CoreType.Forge or CoreType.NeoForge
                && Path.GetFileName(picker.FileName).Contains("installer", StringComparison.OrdinalIgnoreCase);
            var artifactName = isInstaller ? Path.GetFileName(picker.FileName) : "server.jar";
            var stagingArtifactPath = SafePath.CombineUnderRoot(stagingDirectory, artifactName);
            File.Copy(picker.FileName, stagingArtifactPath, overwrite: false);

            var model = new ServerInstance
            {
                Name = serverName,
                DirectoryPath = stagingDirectory,
                ServerJarPath = stagingArtifactPath,
                CoreType = detection.IsRecognized ? detection.CoreType : CoreType.CustomJar,
                MinecraftVersion = string.IsNullOrWhiteSpace(dialog.MinecraftVersion) ? null : dialog.MinecraftVersion.Trim(),
                JavaMajorVersion = dialog.SelectedJavaMajor,
                JavaExecutablePath = FindManagedJavaExecutable(dialog.SelectedJavaMajor),
                IsInstallerArtifact = isInstaller
            };

            await _serverRegistryGate.WaitAsync();
            try
            {
                // Copy into a GUID-owned staging tree first. The final Directory.Move is the
                // ownership boundary: if a background workflow wins the same name, this move
                // fails without ever deleting or writing into the winner's directory.
                var finalDirectory = ServerDirectoryPromotion.PromoteToUniqueDirectory(
                    _paths.Servers,
                    stagingDirectory,
                    serverName);
                ownedStagingDirectory = null;
                ownedFinalDirectory = finalDirectory;
                if (OperatingSystem.IsWindows())
                {
                    ownedFinalIdentityLease = SafePath.CaptureExistingObjectIdentityLease(finalDirectory);
                }
                model.DirectoryPath = finalDirectory;
                model.ServerJarPath = SafePath.CombineUnderRoot(finalDirectory, artifactName);

                if (IsProductServiceRuntime)
                {
                    await ImportIntoProductServiceAsync(
                        model,
                        $"created-jar:{model.Id:N}",
                        applyNewServerDefaults: true,
                        persistLocalMetadata: true,
                        CancellationToken.None);
                }
                else
                {
                    await PersistNewInstanceSnapshotAsync(model, CancellationToken.None);
                    AddInstance(model);
                }
                ownedFinalDirectory = null;
                ownedFinalIdentityLease?.Dispose();
                ownedFinalIdentityLease = null;
            }
            catch (Exception primaryException)
            {
                // Keep the registry gate while deleting a final tree that this import promoted.
                // Otherwise another import could register that path after this failure and have
                // its newly managed Server deleted by our outer cleanup.
                var failedFinalDirectory = ownedFinalDirectory;
                ownedFinalDirectory = null;
                if (failedFinalDirectory is not null)
                {
                    try
                    {
                        if (ownedFinalIdentityLease is { } identityLease)
                        {
                            await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                                _paths.Servers,
                                failedFinalDirectory,
                                identityLease.Identity,
                                cancellationToken: CancellationToken.None);
                        }
                        else
                        {
                            await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                                _paths.Servers,
                                failedFinalDirectory,
                                CancellationToken.None);
                        }
                    }
                    catch (Exception cleanupException)
                    {
                        throw new AggregateException(
                            L("main.vm.error.importCleanupFailed"),
                            primaryException,
                            cleanupException);
                    }
                    finally
                    {
                        ownedFinalIdentityLease?.Dispose();
                        ownedFinalIdentityLease = null;
                    }
                }

                throw;
            }
            finally
            {
                ownedFinalIdentityLease?.Dispose();
                ownedFinalIdentityLease = null;
                _serverRegistryGate.Release();
            }

            SelectedServer = Servers.First(server => server.Id == model.Id);
            if (IsProductServiceRuntime)
            {
                await TryDeleteCompletedGeneratedSourceAsync(model.DirectoryPath);
            }
            SelectedServer.AppendConsole(SystemConsoleLineFactory.Create(
                model.Id,
                isInstaller
                    ? L("main.vm.console.installerImported")
                    : L("main.vm.console.coreImported", model.CoreType, detection.ConfidencePercent),
                ConsoleLineSeverity.Information));
            SetStatus("main.vm.status.created", model.Name);
        }
        catch
        {
            if (ownedStagingDirectory is not null)
            {
                await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                    _paths.Servers,
                    ownedStagingDirectory,
                    CancellationToken.None);
            }

            throw;
        }
    }

    private async Task ImportServerFolderAsync()
    {
        var picker = new OpenFolderDialog
        {
            Title = L("main.vm.filePicker.serverFolderTitle"),
            Multiselect = false
        };
        if (PrimaryDisplayWindowPlacement.ShowDialogOnProductDisplay(picker) != true) return;

        SetStatus("main.vm.status.analyzingFolder");
        var detection = await _serverPackDetector.DetectAsync(picker.FolderName);
        if (!detection.IsRecognized || !detection.IsRunnable)
        {
            var details = string.IsNullOrWhiteSpace(detection.Error)
                ? string.Join(Environment.NewLine, detection.Warnings)
                : detection.Error;
            throw new InvalidDataException(string.IsNullOrWhiteSpace(details)
                ? L("main.vm.error.noSafeLaunch")
                : details);
        }

        await PromptAndAddServerPackAsync(detection, autoDetected: false);
    }

    private async Task<bool> TryAutoDiscoverCurrentFolderAsync()
    {
        var detection = await _serverPackDetector.DetectAsync(_paths.Root);
        if (!detection.IsRecognized || !detection.IsRunnable)
        {
            return false;
        }

        await PromptAndAddServerPackAsync(detection, autoDetected: true);
        return true;
    }

    private async Task<bool> PromptAndAddServerPackAsync(
        ServerPackDetectionResult detection,
        bool autoDetected)
    {
        if (Servers.Any(server => PathsEqual(server.DirectoryPath, detection.DirectoryPath)))
        {
            SetStatus("main.vm.status.folderAlreadyManaged");
            return false;
        }

        var dialog = new ImportServerFolderDialog(detection, autoDetected)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true)
        {
            if (autoDetected)
            {
                SetStatus("main.vm.status.autoDetectedFolder", detection.SuggestedName);
            }
            else
            {
                SetStatus("main.vm.status.importFolderCancelled");
            }
            return false;
        }

        var requiredJava = detection.JavaMajorVersion
            ?? _javaRecommendations.GetRecommendation(detection.MinecraftVersion, detection.CoreType).MajorVersion;
        var bundledJava = detection.JavaExecutablePath;
        var selectedJava = !string.IsNullOrWhiteSpace(bundledJava) && File.Exists(bundledJava)
            ? bundledJava
            : FindManagedJavaExecutable(requiredJava);
        var model = new ServerInstance
        {
            Name = dialog.ServerName.Trim(),
            DirectoryPath = Path.GetFullPath(detection.DirectoryPath),
            ServerJarPath = string.Empty,
            CoreType = detection.CoreType,
            MinecraftVersion = detection.MinecraftVersion,
            JavaMajorVersion = requiredJava,
            JavaExecutablePath = selectedJava,
            MinimumMemoryMb = detection.MinimumMemoryMb ?? 1024,
            MaximumMemoryMb = detection.MaximumMemoryMb ?? 4096,
            ServerArguments = [.. detection.ServerArguments],
            LaunchKind = ServerLaunchKind.JavaArgumentFiles,
            JavaArgumentFilePaths = [.. detection.JavaArgumentFilePaths],
            SourceLaunchScriptPath = detection.SourceLaunchScriptPath
        };

        if (IsProductServiceRuntime)
        {
            var existingPort = await _serverPropertiesPortService.ReadServerPortAsync(
                Path.Combine(model.DirectoryPath, "server.properties"));
            model.Port = existingPort ?? ServerPortAllocator.DefaultPreferredPort;
            await _serverRegistryGate.WaitAsync();
            try
            {
                await ImportIntoProductServiceAsync(
                    model,
                    $"manual-folder:{model.Id:N}",
                    applyNewServerDefaults: true,
                    persistLocalMetadata: true,
                    CancellationToken.None);
            }
            finally
            {
                _serverRegistryGate.Release();
            }

            SelectedServer = Servers.First(server => server.Id == model.Id);
            SelectedServer.AppendConsole(SystemConsoleLineFactory.Create(
                model.Id,
                L("main.vm.console.serviceFolderImported", detection.PackName ?? model.Name),
                ConsoleLineSeverity.Information));
            SetStatus("main.vm.status.serviceFolderImported", model.Name);
            return true;
        }

        await _serverRegistryGate.WaitAsync();
        try
        {
            // Detection and the confirmation dialog intentionally happen outside the gate. The
            // path must be checked again here because a background job can finish while the user
            // is reading the dialog. Keep the gate through persistence and AddInstance so there
            // is no await window in which a second record can claim the same directory.
            if (Servers.Any(server => server.Id == model.Id
                                      || PathsEqual(server.DirectoryPath, model.DirectoryPath)))
            {
                SetStatus("main.vm.status.folderAlreadyManaged");
                return false;
            }

            var existingPort = await _serverPropertiesPortService.ReadServerPortAsync(
                Path.Combine(model.DirectoryPath, "server.properties"));
            model.Port = existingPort ?? ServerPortAllocator.DefaultPreferredPort;
            await PersistNewInstanceSnapshotAsync(model, CancellationToken.None);
            AddInstance(model);
            SelectedServer = Servers.First(server => server.Id == model.Id);
        }
        finally
        {
            _serverRegistryGate.Release();
        }

        SelectedServer.AppendConsole(SystemConsoleLineFactory.Create(
            model.Id,
            L(
                "main.vm.console.folderImportedInPlace",
                detection.PackName ?? model.Name,
                model.CoreType,
                model.MinecraftVersion,
                Path.GetFileName(model.SourceLaunchScriptPath),
                model.Port),
            ConsoleLineSeverity.Information));
        SetStatus("main.vm.status.folderAdded", model.Name);
        return true;
    }

    internal async Task CreateCoreServerAsync()
    {
        SetStatus("main.vm.status.chooseCore");
        var application = Application.Current;
        var owner = application is not null && application.Dispatcher.CheckAccess()
            ? application.MainWindow
            : null;
        if (_backgroundCoreServerCreationDialogService is not null)
        {
            var submitted = _backgroundCoreServerCreationDialogService.ShowCreateDialog(owner);
            SetStatus(submitted
                ? "main.vm.status.coreQueued"
                : "main.vm.status.coreCancelled");
            return;
        }

        var created = _coreServerCreationDialogService.ShowCreateDialog(owner);
        if (created is null)
        {
            SetStatus("main.vm.status.coreCancelled");
            return;
        }

        if (!Directory.Exists(created.DirectoryPath))
        {
            throw new DirectoryNotFoundException(
                L("main.vm.error.coreFolderMissing", created.DirectoryPath));
        }

        await _serverRegistryGate.WaitAsync();
        try
        {
            if (Servers.Any(server => server.Id == created.Id
                                      || PathsEqual(server.DirectoryPath, created.DirectoryPath)))
            {
                throw new InvalidOperationException(
                    L("main.vm.error.createdServerAlreadyManaged"));
            }

            if (IsProductServiceRuntime)
            {
                await ImportIntoProductServiceAsync(
                    created,
                    $"created-core:{created.Id:N}",
                    applyNewServerDefaults: true,
                    persistLocalMetadata: true,
                    CancellationToken.None);
            }
            else
            {
                await PersistNewInstanceSnapshotAsync(created, CancellationToken.None);
                AddInstance(created);
            }
            SelectedServer = Servers.First(server => server.Id == created.Id);
        }
        finally
        {
            _serverRegistryGate.Release();
        }

        if (IsProductServiceRuntime)
        {
            await TryDeleteCompletedGeneratedSourceAsync(created.DirectoryPath);
        }

        SelectedServer.AppendConsole(SystemConsoleLineFactory.Create(
            created.Id,
            L(
                "main.vm.console.coreCreated",
                created.CoreType,
                created.MinecraftVersion,
                ServerPortAllocator.DefaultPreferredPort),
            ConsoleLineSeverity.Information));
        SetStatus("main.vm.status.coreCreated", created.Name);
    }

    internal async Task InstallOnlineModpackAsync()
    {
        SetStatus("main.vm.status.chooseModpack");
        var application = Application.Current;
        var owner = application is not null && application.Dispatcher.CheckAccess()
            ? application.MainWindow
            : null;
        if (_backgroundOnlineModpackDialogService is not null)
        {
            var submitted = _backgroundOnlineModpackDialogService.ShowInstallDialog(owner);
            SetStatus(submitted
                ? "main.vm.status.modpackQueued"
                : "main.vm.status.modpackCancelled");
            return;
        }

        var installed = _onlineModpackDialogService.ShowInstallDialog(owner);
        if (installed is null)
        {
            SetStatus("main.vm.status.modpackCancelled");
            return;
        }

        if (!Directory.Exists(installed.DirectoryPath))
        {
            throw new DirectoryNotFoundException(
                L("main.vm.error.modpackFolderMissing", installed.DirectoryPath));
        }

        await _serverRegistryGate.WaitAsync();
        try
        {
            if (Servers.Any(server => server.Id == installed.Id
                                      || PathsEqual(server.DirectoryPath, installed.DirectoryPath)))
            {
                throw new InvalidOperationException(L("main.vm.error.installedServerAlreadyManaged"));
            }

            if (IsProductServiceRuntime)
            {
                await ImportIntoProductServiceAsync(
                    installed,
                    $"installed-modpack:{installed.Id:N}",
                    applyNewServerDefaults: true,
                    persistLocalMetadata: true,
                    CancellationToken.None);
            }
            else
            {
                await PersistNewInstanceSnapshotAsync(installed, CancellationToken.None);
                AddInstance(installed);
            }
            SelectedServer = Servers.First(server => server.Id == installed.Id);
        }
        finally
        {
            _serverRegistryGate.Release();
        }

        if (IsProductServiceRuntime)
        {
            await TryDeleteCompletedGeneratedSourceAsync(installed.DirectoryPath);
        }

        SelectedServer.AppendConsole(SystemConsoleLineFactory.Create(
            installed.Id,
            L("main.vm.console.modpackInstalled", ServerPortAllocator.DefaultPreferredPort),
            ConsoleLineSeverity.Information));
        SetStatus("main.vm.status.modpackInstalled", installed.Name);
    }

    private void AddInstance(ServerInstance model)
    {
        model.SeparateDiagnosticOutput ??= true;
        if (!_settings.Instances.Any(instance => instance.Id == model.Id))
        {
            _settings.Instances.Add(model);
        }
        _playerPresenceCoreTypes[model.Id] = model.CoreType;
        _instanceModels[model.Id] = model;
        Servers.Add(CreateServerViewModel(model));
        OnPropertyChanged(nameof(ServerCountText));
        OnPropertyChanged(nameof(RunningSummary));
    }

    private async Task CommitBackgroundServerAsync(
        ServerInstance model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            var operation = dispatcher.InvokeAsync(
                () => CommitBackgroundServerOnUiAsync(model, cancellationToken),
                DispatcherPriority.Send);
            await (await operation).ConfigureAwait(false);
            return;
        }

        await CommitBackgroundServerOnUiAsync(model, cancellationToken);
    }

    private async Task CommitBackgroundServerOnUiAsync(
        ServerInstance model,
        CancellationToken cancellationToken)
    {
        await _serverRegistryGate.WaitAsync(cancellationToken);
        string? cleanupDirectory = null;
        SafePathObjectIdentityLease? cleanupIdentityLease = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var managedDirectory = SafePath.EnsureWithinRoot(
                _paths.Servers,
                model.DirectoryPath,
                allowRoot: false);
            if (!Directory.Exists(managedDirectory))
            {
                throw new DirectoryNotFoundException(
                    L("main.vm.error.backgroundFolderMissing", managedDirectory));
            }

            var alreadyManaged = Servers.FirstOrDefault(server => server.Id == model.Id
                || PathsEqual(server.DirectoryPath, managedDirectory));
            if (alreadyManaged is not null)
            {
                // A user can import the just-finished directory during the narrow handoff window.
                // Treat that as an idempotent commit instead of leaving a verified orphan.
                SetStatus("main.vm.status.backgroundAlreadyManaged", alreadyManaged.Name);
                return;
            }

            if (IsProductServiceRuntime)
            {
                model.DirectoryPath = managedDirectory;
                await ImportIntoProductServiceAsync(
                    model,
                    $"background-create:{model.Id:N}",
                    applyNewServerDefaults: true,
                    persistLocalMetadata: true,
                    cancellationToken);
                var serviceAdded = Servers.First(server => server.Id == model.Id);
                serviceAdded.AppendConsole(SystemConsoleLineFactory.Create(
                    model.Id,
                    L("main.vm.console.backgroundCommittedToService"),
                    ConsoleLineSeverity.Information));
                SetStatus("main.vm.status.backgroundServiceCompleted", model.Name);
                await TryDeleteCompletedGeneratedSourceAsync(managedDirectory);
                return;
            }

            // Display names are not filesystem identities. Active background submissions with
            // the same canonical name are rejected by the coordinator, but a later manual import
            // with that display name must not strand this already-promoted unique directory.
            model.DirectoryPath = managedDirectory;
            cleanupDirectory = managedDirectory;
            if (OperatingSystem.IsWindows())
            {
                cleanupIdentityLease = SafePath.CaptureExistingObjectIdentityLease(managedDirectory);
            }

            // Persist the complete next snapshot before exposing the row in the UI. This keeps
            // concurrent background completions serialized and avoids a visible row that would
            // disappear after restart if the atomic settings write fails.
            await PersistNewInstanceSnapshotAsync(model, cancellationToken);
            AddInstance(model);
            cleanupDirectory = null;
            cleanupIdentityLease?.Dispose();
            cleanupIdentityLease = null;
            var added = Servers.First(server => server.Id == model.Id);
            added.AppendConsole(SystemConsoleLineFactory.Create(
                model.Id,
                L("main.vm.console.backgroundCompleted", ServerPortAllocator.DefaultPreferredPort),
                ConsoleLineSeverity.Information));
            SetStatus("main.vm.status.backgroundCompleted", model.Name);
        }
        catch
        {
            if (cleanupDirectory is not null)
            {
                if (cleanupIdentityLease is { } identityLease)
                {
                    await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                        _paths.Servers,
                        cleanupDirectory,
                        identityLease.Identity,
                        cancellationToken: CancellationToken.None);
                }
                else
                {
                    await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                        _paths.Servers,
                        cleanupDirectory,
                        CancellationToken.None);
                }
            }

            throw;
        }
        finally
        {
            cleanupIdentityLease?.Dispose();
            _serverRegistryGate.Release();
        }
    }

    private async Task TryDeleteCompletedGeneratedSourceAsync(string path)
    {
        try
        {
            var fullPath = SafePath.EnsureWithinRoot(_paths.Servers, path, allowRoot: false);
            if (!Directory.Exists(fullPath))
            {
                return;
            }

            await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                _paths.Servers,
                fullPath,
                CancellationToken.None);
        }
        catch (Exception error) when (error is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException
                                      or ArgumentException)
        {
            // Service commit already succeeded. A stale generated Preview tree is harmless and
            // must not turn a valid Service registration into a failed background job.
        }
    }

    private async Task PersistNewInstanceSnapshotAsync(
        ServerInstance model,
        CancellationToken cancellationToken)
    {
        ApplyNewServerDefaults(model);
        await _settingsSaveGate.WaitAsync(cancellationToken);
        try
        {
            var previousInstances = _settings.Instances;
            _settings.SchemaVersion = Math.Max(
                _settings.SchemaVersion,
                ManagerSettings.CurrentSchemaVersion);
            _settings.Instances = Servers.Select(server => server.Model)
                .Append(model)
                .ToList();
            try
            {
                await _settingsStore.SaveAsync(
                    PrepareSettingsForPersistence(),
                    cancellationToken);
            }
            catch
            {
                _settings.Instances = previousInstances;
                throw;
            }
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private void OnBackgroundJobsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BackgroundServerJobCoordinator.HasActiveJobs)
            or nameof(BackgroundServerJobCoordinator.SummaryText)
            or nameof(BackgroundServerJobCoordinator.LatestActivityText)
            or nameof(BackgroundServerJobCoordinator.AggregateProgress)
            or nameof(BackgroundServerJobCoordinator.IsAggregateProgressIndeterminate))
        {
            OnPropertyChanged(nameof(HasActiveBackgroundJobs));
            OnPropertyChanged(nameof(BackgroundJobSummary));
            OnPropertyChanged(nameof(BackgroundJobActivity));
            OnPropertyChanged(nameof(BackgroundJobProgress));
            OnPropertyChanged(nameof(IsBackgroundJobProgressIndeterminate));
        }
    }

    private void OpenBackgroundJobsWindow()
    {
        var owner = GetAccessibleMainWindow();
        if (_backgroundJobsWindow is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            PrimaryDisplayWindowPlacement.ActivateWhenInteractive(existing);
            return;
        }

        var window = new BackgroundJobsWindow(this);
        if (owner is not null)
        {
            window.Owner = owner;
        }

        window.Closed += (_, _) => _backgroundJobsWindow = null;
        _backgroundJobsWindow = window;
        window.Show();
    }

    private void OnContentDownloadCenterRequested(object? sender, EventArgs e)
    {
        var owner = GetAccessibleMainWindow();
        if (_contentDownloadCenterWindow is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            PrimaryDisplayWindowPlacement.ActivateWhenInteractive(existing);
            return;
        }

        var window = new ClientContentDownloadCenterWindow
        {
            DataContext = ClientWorkspace,
        };
        if (owner is not null)
        {
            window.Owner = owner;
        }

        window.Closed += (_, _) => _contentDownloadCenterWindow = null;
        _contentDownloadCenterWindow = window;
        window.Show();
    }

    private async Task StartSelectedAsync()
    {
        var server = SelectedServer;
        if (server is null) return;
        await StartServerAsync(server, allowInteractiveJavaDownload: true, CancellationToken.None);
    }

    private void ToggleBulkSelectionMode()
        => IsBulkSelectionMode = !IsBulkSelectionMode;

    private bool CanRunCheckedServerBatch()
        => IsBulkSelectionMode
           && !IsBatchLifecycleOperationRunning
           && !_isServerListMutationRunning
           && (!IsProductServiceRuntime || IsProductServiceConnected)
           && Servers.Any(static server => server.IsBulkSelected);

    private bool CanUseSelectedBackupCommands()
        => SelectedServer is not null &&
           (!IsProductServiceRuntime || IsProductServiceConnected);

    private bool CanManageSelectedPlayers()
        => SelectedServer?.CanManagePlayers == true &&
           (!IsProductServiceRuntime || IsProductServiceConnected);

    private ServerInstanceViewModel[] SnapshotCheckedServers()
        => Servers.Where(static server => server.IsBulkSelected).ToArray();

    private void NotifyCheckedServerCommandsCanExecuteChanged()
    {
        StartCheckedServersCommand.NotifyCanExecuteChanged();
        StopCheckedServersCommand.NotifyCanExecuteChanged();
    }

    private void ClearBulkSelection()
    {
        foreach (var server in Servers)
        {
            server.IsBulkSelected = false;
        }
    }

    private void OnServersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var observedServer in _bulkSelectionSubscriptions)
            {
                observedServer.IsBulkSelected = false;
                observedServer.PropertyChanged -= OnBulkSelectionServerPropertyChanged;
            }
            _bulkSelectionSubscriptions.Clear();
        }
        else if (eventArgs.Action != NotifyCollectionChangedAction.Move)
        {
            if (eventArgs.OldItems is not null)
            {
                foreach (var server in eventArgs.OldItems.OfType<ServerInstanceViewModel>())
                {
                    server.IsBulkSelected = false;
                    server.PropertyChanged -= OnBulkSelectionServerPropertyChanged;
                    _bulkSelectionSubscriptions.Remove(server);
                }
            }

            if (eventArgs.NewItems is not null)
            {
                foreach (var server in eventArgs.NewItems.OfType<ServerInstanceViewModel>())
                {
                    if (!IsBulkSelectionMode)
                    {
                        server.IsBulkSelected = false;
                    }
                    if (_bulkSelectionSubscriptions.Add(server))
                    {
                        server.PropertyChanged += OnBulkSelectionServerPropertyChanged;
                    }
                }
            }
        }

        NotifyCheckedServerCommandsCanExecuteChanged();
    }

    private void OnBulkSelectionServerPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(ServerInstanceViewModel.IsBulkSelected)) return;
        if (!IsBulkSelectionMode && sender is ServerInstanceViewModel { IsBulkSelected: true } server)
        {
            server.IsBulkSelected = false;
            return;
        }
        NotifyCheckedServerCommandsCanExecuteChanged();
    }

    private Task StartCheckedServersAsync()
        => RunTrackedCheckedServerBatchAsync(StartCheckedServersCoreAsync);

    private Task StopCheckedServersAsync()
        => RunTrackedCheckedServerBatchAsync(StopCheckedServersCoreAsync);

    private Task RunTrackedCheckedServerBatchAsync(
        Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_batchLifecycleOperationSync)
        {
            if (_isServerListMutationRunning)
            {
                return Task.FromException(new InvalidOperationException(
                    L("main.vm.error.listMutationBlocksBatch")));
            }
            if (!_batchLifecycleOperationTask.IsCompleted)
            {
                return _batchLifecycleOperationTask;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _applicationShutdownCancellation.Token);
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _batchLifecycleOperationCancellation = cancellation;
            _batchLifecycleOperationTask = completion.Task;
            _ = CompleteTrackedCheckedServerBatchAsync(
                operation,
                cancellation,
                completion);
            return completion.Task;
        }
    }

    private async Task CompleteTrackedCheckedServerBatchAsync(
        Func<CancellationToken, Task> operation,
        CancellationTokenSource cancellation,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        var wasCanceled = false;
        IsBatchLifecycleOperationRunning = true;
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            wasCanceled = true;
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            IsBatchLifecycleOperationRunning = false;
            lock (_batchLifecycleOperationSync)
            {
                if (ReferenceEquals(_batchLifecycleOperationCancellation, cancellation))
                {
                    _batchLifecycleOperationCancellation = null;
                }
            }

            if (wasCanceled)
            {
                completion.TrySetCanceled(cancellation.Token);
            }
            else if (failure is not null)
            {
                completion.TrySetException(failure);
            }
            else
            {
                completion.TrySetResult();
            }
            cancellation.Dispose();
        }
    }

    private async Task CancelAndWaitForBatchLifecycleOperationAsync()
    {
        Task operationTask;
        lock (_batchLifecycleOperationSync)
        {
            _batchLifecycleOperationCancellation?.Cancel();
            operationTask = _batchLifecycleOperationTask;
        }

        try
        {
            await operationTask;
        }
        catch (OperationCanceledException)
        {
            // Expected when shutdown interrupts a sequential bulk start/stop operation.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A batch failure must not prevent the authoritative shutdown-time stop-all pass.
            SetStatus("main.vm.batch.waitFailed", error.Message);
        }
    }

    private async Task StartCheckedServersCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkedServers = SnapshotCheckedServers();
        if (checkedServers.Length == 0) return;

        var succeeded = 0;
        var skipped = 0;
        var failures = new List<string>();
        // Start sequentially so several large modpacks do not cold-start at the same instant.
        foreach (var server in checkedServers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Servers.Contains(server)
                || server.State is not (ServerState.Stopped or ServerState.Crashed or ServerState.Faulted)
                || _modpackUpdates.ContainsKey(server.Id))
            {
                skipped++;
                continue;
            }

            try
            {
                await StartServerAsync(
                    server,
                    allowInteractiveJavaDownload: true,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // StateChanged is projected onto the UI dispatcher asynchronously. The
                // process-manager snapshot is authoritative here and avoids reporting a
                // successful start as skipped while the row still displays Stopped.
                if ((_productServiceController is not null
                     && server.State is ServerState.Starting or ServerState.Running)
                    || (_productServiceController is null
                        && _processManager.TryGetSnapshot(server.Id, out var processSnapshot)
                        && processSnapshot.State is ServerState.Starting or ServerState.Running))
                {
                    succeeded++;
                }
                else
                {
                    // An interactive prerequisite dialog can be cancelled without throwing.
                    skipped++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failures.Add($"{server.Name}：{error.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        SetCheckedServerBatchStatus(
            "main.vm.operation.startSelected",
            succeeded,
            skipped,
            failures);
    }

    private async Task StopCheckedServersCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkedServers = SnapshotCheckedServers();
        if (checkedServers.Length == 0) return;

        var eligibleServers = checkedServers
            .Where(server => Servers.Contains(server)
                             && server.State is ServerState.Starting or ServerState.Running)
            .ToArray();
        var skipped = checkedServers.Length - eligibleServers.Length;

        // Materialize every stop task before awaiting any one of them. A slow server cannot
        // prevent the other checked servers from receiving their shutdown request promptly.
        var stopTasks = eligibleServers
            .Select(server => StopCheckedServerAsync(server, cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(stopTasks);
        cancellationToken.ThrowIfCancellationRequested();
        var failures = results
            .Where(static result => result.Error is not null)
            .Select(static result => $"{result.ServerName}：{result.Error!.Message}")
            .ToList();
        var succeeded = results.Count(static result => result.WasStopped);
        skipped += results.Count(static result => !result.WasStopped && result.Error is null);

        SetCheckedServerBatchStatus(
            "main.vm.operation.stopSelected",
            succeeded,
            skipped,
            failures);
    }

    private async Task<CheckedServerStopResult> StopCheckedServerAsync(
        ServerInstanceViewModel server,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvalidateAutomaticRestartIntent(server.Id);
            var wasStopped = await StopServerCoordinatedAsync(server.Id, cancellationToken);
            return new CheckedServerStopResult(server.Name, wasStopped, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new CheckedServerStopResult(server.Name, WasStopped: false, Error: error);
        }
    }

    private void SetCheckedServerBatchStatus(
        string operationKey,
        int succeeded,
        int skipped,
        IReadOnlyList<string> failures)
    {
        var operation = L(operationKey);
        if (failures.Count == 0)
        {
            SetStatus("main.vm.batch.summary", operation, succeeded, skipped, failures.Count);
        }
        else
        {
            SetStatus(
                "main.vm.batch.summaryWithFailure",
                operation,
                succeeded,
                skipped,
                failures.Count,
                failures[0]);
        }
    }

    private sealed record CheckedServerStopResult(
        string ServerName,
        bool WasStopped,
        Exception? Error);

    private bool CanOpenSelectedFolder()
        => SelectedServer is { } server &&
           (server.CanAccessLocalFiles ||
            (server.IsServiceManaged && SupportsProductServiceFileAdministration));

    private bool CanStartSelectedServer()
        => SelectedServer is { } server
           && server.State is ServerState.Stopped or ServerState.Crashed or ServerState.Faulted
           && !_modpackUpdates.ContainsKey(server.Id)
           && !_modpackRecoveryFailures.ContainsKey(server.Id)
           && (!IsProductServiceRuntime || IsProductServiceConnected);

    private bool CanCreateOrImportServer()
        => !IsProductServiceRuntime || IsProductServiceConnected;

    private void NotifyCreateOrImportCommandsCanExecuteChanged()
    {
        ImportExistingServerCommand.NotifyCanExecuteChanged();
        ImportServerCommand.NotifyCanExecuteChanged();
        ImportServerFolderCommand.NotifyCanExecuteChanged();
        CreateCoreServerCommand.NotifyCanExecuteChanged();
        InstallOnlineModpackCommand.NotifyCanExecuteChanged();
    }

    private bool CanStopSelectedServer()
        => SelectedServer?.State is ServerState.Starting or ServerState.Running
           && (!IsProductServiceRuntime || IsProductServiceConnected);

    private void OnSelectedServerPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ServerInstanceViewModel.State))
        {
            NotifySelectedLifecycleCommandsCanExecuteChanged();
            UpdateSelectedModpackCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSaveSelectedServerProperties));
            SavePropertiesCommand.NotifyCanExecuteChanged();
        }
        else if (eventArgs.PropertyName == nameof(ServerInstanceViewModel.CanIterativelyUpdateModpack))
        {
            UpdateSelectedModpackCommand.NotifyCanExecuteChanged();
        }

        if (eventArgs.PropertyName is nameof(ServerInstanceViewModel.IsServiceManaged)
            or nameof(ServerInstanceViewModel.CanAccessLocalFiles))
        {
            OnPropertyChanged(nameof(CanEditSelectedLocalConfiguration));
            OnPropertyChanged(nameof(CanReloadSelectedServerProperties));
            OnPropertyChanged(nameof(CanEditSelectedServerProperties));
            OnPropertyChanged(nameof(CanSaveSelectedServerProperties));
            OnPropertyChanged(nameof(CanBrowseSelectedServerFiles));
            OnPropertyChanged(nameof(CanManageLocalRecoveryPoints));
            OpenSelectedFolderCommand.NotifyCanExecuteChanged();
            CheckAddonUpdatesCommand.NotifyCanExecuteChanged();
            OpenAddonFolderCommand.NotifyCanExecuteChanged();
            OpenRecoveryPointsFolderCommand.NotifyCanExecuteChanged();
            RestoreRecoveryPointCommand.NotifyCanExecuteChanged();
            ReloadPropertiesCommand.NotifyCanExecuteChanged();
            SavePropertiesCommand.NotifyCanExecuteChanged();
        }

        if (!_applyingProductServiceProjection &&
            sender is ServerInstanceViewModel { IsServiceManaged: true } server &&
            eventArgs.PropertyName is
                nameof(ServerInstanceViewModel.Name) or
                nameof(ServerInstanceViewModel.Port) or
                nameof(ServerInstanceViewModel.MinimumMemoryMb) or
                nameof(ServerInstanceViewModel.MaximumMemoryMb) or
                nameof(ServerInstanceViewModel.AutoRestart))
        {
            _dirtyProductServiceRegistrations.Add(server.Id);
        }
    }

    private void NotifySelectedLifecycleCommandsCanExecuteChanged()
    {
        StartSelectedCommand.NotifyCanExecuteChanged();
        StopSelectedCommand.NotifyCanExecuteChanged();
    }

    private void NotifySelectedServiceDependentCommandsCanExecuteChanged()
    {
        CreateBackupCommand.NotifyCanExecuteChanged();
        RestoreBackupCommand.NotifyCanExecuteChanged();
        RefreshBackupsCommand.NotifyCanExecuteChanged();
        KickPlayerCommand.NotifyCanExecuteChanged();
        BanPlayerCommand.NotifyCanExecuteChanged();
        PardonPlayerCommand.NotifyCanExecuteChanged();
        OpPlayerCommand.NotifyCanExecuteChanged();
        DeopPlayerCommand.NotifyCanExecuteChanged();
        WhitelistAddCommand.NotifyCanExecuteChanged();
        WhitelistRemoveCommand.NotifyCanExecuteChanged();
        WhitelistOnCommand.NotifyCanExecuteChanged();
        WhitelistOffCommand.NotifyCanExecuteChanged();
    }

    private async Task StartServerAsync(
        ServerInstanceViewModel server,
        bool allowInteractiveJavaDownload,
        CancellationToken cancellationToken,
        bool userConfirmedMinecraftEula = false)
    {
        if (_modpackRecoveryFailures.ContainsKey(server.Id))
        {
            throw new InvalidOperationException(
                L("main.vm.error.modpackRecoveryIncomplete", server.Name));
        }

        // A prior failed validation deliberately suppresses ProcessManager's automatic restart.
        // Reaching this method means a user explicitly requested a new launch, so that one-shot
        // suppression can now be cleared without weakening crash-loop protection.
        _modpackAutoRestartBlocks.TryRemove(server.Id, out _);
        if (_modpackUpdates.ContainsKey(server.Id))
        {
            throw new InvalidOperationException(
                L("main.vm.error.modpackUpdateBlocksStart", server.Name));
        }

        if (_productServiceController is not null)
        {
            EnsureProductServiceConnected();
            SetStatus("main.vm.status.serviceStartRequest", server.Name);
            var result = await ExecuteProductServiceLifecycleWithEulaConfirmationAsync(
                server,
                restart: false,
                allowInteractiveEulaConfirmation: allowInteractiveJavaDownload,
                cancellationToken);
            if (result is null)
            {
                return;
            }
            ApplyProductServiceStatus(server, result.Status);
            OnPropertyChanged(nameof(RunningSummary));
            OnPropertyChanged(nameof(HasRunningServers));
            SetStatus(
                result.Changed
                    ? "main.vm.status.serviceStartAccepted"
                    : "main.vm.status.serviceStartUnchanged",
                server.Name);
            return;
        }

        if (server.State is ServerState.Starting or ServerState.Running or ServerState.Stopping)
        {
            throw new InvalidOperationException(L("main.vm.error.alreadyStarting", server.Name, server.StateText));
        }

        if (server.Model.IsInstallerArtifact)
        {
            throw new InvalidOperationException(L("main.vm.error.installerCannotStart"));
        }

        if ((server.Model.MemoryAllocationMode == MemoryAllocationMode.Manual
             || server.Model is { MemoryAllocationMode: MemoryAllocationMode.Legacy, LaunchKind: ServerLaunchKind.ExecutableJar })
            && (server.MaximumMemoryMb < server.MinimumMemoryMb || server.MinimumMemoryMb < 256))
        {
            throw new InvalidOperationException(L("main.vm.error.memoryInvalid256"));
        }

        ValidateReliabilitySettings(server);

        var requiredJava = server.Model.JavaMajorVersion ?? 21;
        if (string.IsNullOrWhiteSpace(server.Model.JavaExecutablePath) || !File.Exists(server.Model.JavaExecutablePath))
        {
            server.Model.JavaExecutablePath = FindManagedJavaExecutable(requiredJava);
        }

        if (string.IsNullOrWhiteSpace(server.Model.JavaExecutablePath) || !File.Exists(server.Model.JavaExecutablePath))
        {
            if (!allowInteractiveJavaDownload)
            {
                throw new InvalidOperationException(
                    L("main.vm.error.javaRequiredDesktop", requiredJava));
            }

            var answer = DarkMessageBox.Show(
                Application.Current.MainWindow,
                L("main.vm.confirm.downloadJava", requiredJava),
                L("main.vm.confirm.downloadJavaTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            SelectedJavaMajor = requiredJava;
            var installedJavaPath = await DownloadJavaForMajorAsync(requiredJava);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Servers.Contains(server))
            {
                throw new InvalidOperationException(
                    L("main.vm.error.serverRemovedDuringJavaInstall"));
            }
            server.JavaExecutablePath = installedJavaPath;
            await SaveSettingsAsync();
        }

        SetStatus("main.vm.status.lockingAndCheckingPort", server.Name);
        try
        {
            await StartProcessCoordinatedAsync(
                server.Model,
                cancellationToken,
                userConfirmedMinecraftEula);
        }
        catch (MinecraftEulaAcceptanceRequiredException) when (
            allowInteractiveJavaDownload && !userConfirmedMinecraftEula)
        {
            if (!ConfirmMinecraftEulaAcceptance(server))
            {
                return;
            }

            await StartProcessCoordinatedAsync(
                server.Model,
                cancellationToken,
                userConfirmedMinecraftEula: true);
        }
    }

    private async Task StopSelectedAsync()
    {
        var server = SelectedServer;
        if (server is null) return;
        InvalidateAutomaticRestartIntent(server.Id);
        SetStatus("main.vm.status.stoppingSafely", server.Name);
        await StopServerCoordinatedAsync(server.Id, CancellationToken.None);
    }

    private async Task<ProductServerMutationResult?> ExecuteProductServiceLifecycleWithEulaConfirmationAsync(
        ServerInstanceViewModel server,
        bool restart,
        bool allowInteractiveEulaConfirmation,
        CancellationToken cancellationToken)
    {
        var controller = _productServiceController
            ?? throw new InvalidOperationException(L("main.vm.service.operationFailed"));
        try
        {
            return await ExecuteProductServiceOperationAsync(
                token => restart
                    ? controller.RestartAsync(server.Id, token)
                    : controller.StartAsync(server.Id, token),
                cancellationToken);
        }
        catch (InvalidOperationException error) when (
            allowInteractiveEulaConfirmation && IsEulaConfirmationRequired(error))
        {
            if (!ConfirmMinecraftEulaAcceptance(server))
            {
                return null;
            }

            return await ExecuteProductServiceOperationAsync(
                token => restart
                    ? controller.RestartAsync(
                        server.Id,
                        acceptMinecraftEula: true,
                        token)
                    : controller.StartAsync(
                        server.Id,
                        acceptMinecraftEula: true,
                        token),
                cancellationToken);
        }
    }

    private static bool IsEulaConfirmationRequired(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is ProductServiceClientException
                {
                    Code: "server.eula_acceptance_required"
                })
            {
                return true;
            }
        }

        return false;
    }

    private bool ConfirmMinecraftEulaAcceptance(ServerInstanceViewModel server)
    {
        var accepted = MinecraftEulaConfirmationDialog.Show(
            Application.Current?.MainWindow,
            server.Name);
        if (!accepted)
        {
            SetStatus("main.vm.status.serverState", server.Name, server.StateText);
        }

        return accepted;
    }

    private async Task<bool?> ResolveRestartEulaAuthorizationAsync(
        ServerInstanceViewModel server,
        bool allowInteractiveConfirmation,
        CancellationToken cancellationToken)
    {
        if (!MinecraftEulaAcceptanceService.IsRequired(server.Model.CoreType)
            || await _minecraftEulaAcceptanceService.IsAcceptedAsync(
                    server.DirectoryPath,
                    cancellationToken))
        {
            return false;
        }

        if (!allowInteractiveConfirmation)
        {
            throw new MinecraftEulaAcceptanceRequiredException();
        }

        return ConfirmMinecraftEulaAcceptance(server) ? true : null;
    }

    private Task PrepareServerStartAsync(
        ServerInstance launchSnapshot,
        ServerStartContext startContext,
        CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException(L("main.vm.error.startDispatcherUnavailable"));
        return dispatcher.InvokeAsync(
                () => PrepareServerStartOnUiAsync(
                    launchSnapshot,
                    startContext,
                    cancellationToken),
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken)
            .Task
            .Unwrap();
    }

    private Task<bool> ShouldAutomaticallyRestartAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return Task.FromResult(false);
        }

        return dispatcher.InvokeAsync(
                () =>
                {
                    var enabled = Servers.FirstOrDefault(server => server.Id == instanceId)?.AutoRestart == true;
                    return enabled
                        && !_modpackUpdates.ContainsKey(instanceId)
                        && !_modpackAutoRestartBlocks.ContainsKey(instanceId)
                        && (!_pendingModpackHealthValidations.TryGetValue(instanceId, out var validation)
                            || validation.IsHealthy)
                        && (!_crashPlans.TryGetValue(instanceId, out var plan)
                            || plan.Decision.ShouldRestart);
                },
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken)
            .Task;
    }

    private Task<TimeSpan> GetAutomaticRestartDelayAsync(
        Guid instanceId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _crashPlans.TryGetValue(instanceId, out var plan) && plan.SessionId == sessionId
                ? plan.Decision.Delay
                : TimeSpan.FromSeconds(5));
    }

    private async Task PrepareAutomaticRestartAsync(
        ServerInstance restartSnapshot,
        CancellationToken cancellationToken)
    {
        if (!_crashPlans.TryGetValue(restartSnapshot.Id, out var plan)) return;
        if (_crashReportTasks.TryGetValue((restartSnapshot.Id, plan.SessionId), out var reportTask))
        {
            await reportTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await WaitForBackupIdleAsync(restartSnapshot.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task PrepareServerStartOnUiAsync(
        ServerInstance launchSnapshot,
        ServerStartContext startContext,
        CancellationToken cancellationToken)
    {
        var server = Servers.FirstOrDefault(item => item.Id == launchSnapshot.Id)
            ?? throw new InvalidOperationException(L("main.vm.error.startInstanceNotFound"));
        if (server.Model.CoreType is not (CoreType.Velocity or CoreType.Waterfall or CoreType.BungeeCord))
        {
            await EnsureEulaAcceptedUnderLockAsync(
                launchSnapshot.DirectoryPath,
                startContext.UserConfirmedMinecraftEula,
                cancellationToken);
        }
        await ApplyEffectiveMemoryForLaunchAsync(server, launchSnapshot, cancellationToken);
        try
        {
            var portAssignment = await AssignAvailablePortAsync(
                server.DirectoryPath,
                server.Id,
                requestedPort: ServerPortAllocator.DefaultPreferredPort,
                reserveForLaunch: true,
                launchConfiguration: launchSnapshot,
                cancellationToken: cancellationToken);
            launchSnapshot.Port = portAssignment.Port;
            server.Port = portAssignment.Port;
            if (server.Model.CoreType == CoreType.Velocity)
            {
                server.Model.ServerArguments ??= [];
                VelocityPortArgumentEditor.SetPort(server.Model.ServerArguments, portAssignment.Port);
            }
            if (portAssignment.FileUpdated)
            {
                await ReloadPropertiesQuietlyAsync(server);
            }

            await SaveSettingsAsync();
            server.AppendConsole(SystemConsoleLineFactory.Create(
                server.Id,
                portAssignment.WasConflict
                    ? L("main.vm.console.portConflict", portAssignment.PreviousPort, portAssignment.Port)
                    : L("main.vm.console.portAssigned", portAssignment.Port),
                portAssignment.WasConflict
                    ? ConsoleLineSeverity.Warning
                    : ConsoleLineSeverity.Information));
            SetStatus("main.vm.status.startingOnPort", server.Name, portAssignment.Port);
        }
        catch
        {
            // The reservation is created before the port is persisted. If any later preparation
            // step fails, no process state transition exists that could release it for us.
            ReleasePendingLaunchPort(server.Id);
            throw;
        }
    }

    private async Task ApplyEffectiveMemoryForLaunchAsync(
        ServerInstanceViewModel server,
        ServerInstance launchSnapshot,
        CancellationToken cancellationToken)
    {
        var configuredMode = launchSnapshot.MemoryAllocationMode;
        if (configuredMode == MemoryAllocationMode.Legacy)
        {
            // Exact compatibility: old JAR instances already used the saved numbers, while old
            // Forge/NeoForge argument-file instances must keep their installer-owned file intact.
            return;
        }

        var effectiveMode = configuredMode;
        var minimumMb = launchSnapshot.MinimumMemoryMb;
        var maximumMb = launchSnapshot.MaximumMemoryMb;
        string explanation;
        if (configuredMode == MemoryAllocationMode.UseManagerDefault)
        {
            var defaults = _settings.NewServerDefaults;
            effectiveMode = MemoryAllocationMode.Manual;
            minimumMb = defaults.MinimumMemoryMb;
            maximumMb = defaults.MaximumMemoryMb;
            explanation = L("main.vm.memory.managerDefaultExplanation");
        }
        else
        {
            explanation = effectiveMode == MemoryAllocationMode.Manual
                ? L("main.vm.memory.manualExplanation")
                : string.Empty;
        }

        if (effectiveMode == MemoryAllocationMode.Automatic)
        {
            var recommendation = await _memoryRecommendationService.RecommendAsync(
                launchSnapshot.DirectoryPath,
                cancellationToken);
            minimumMb = recommendation.MinimumMemoryMb;
            maximumMb = recommendation.MaximumMemoryMb;
            explanation = recommendation.Explanation;
            server.SetMemorySliderMaximum(recommendation.SafeAllocationCeilingMb);
        }

        if (minimumMb < 512 || maximumMb < minimumMb)
        {
            throw new InvalidOperationException(
                L("main.vm.error.effectiveMemoryInvalid"));
        }

        launchSnapshot.MinimumMemoryMb = minimumMb;
        launchSnapshot.MaximumMemoryMb = maximumMb;
        server.ApplyEffectiveMemoryDisplay(minimumMb, maximumMb);
        if (launchSnapshot.LaunchKind == ServerLaunchKind.JavaArgumentFiles)
        {
            await _jvmMemoryLaunchOverlayService.ApplyAsync(
                launchSnapshot,
                minimumMb,
                maximumMb,
                cancellationToken);
        }

        server.AppendConsole(SystemConsoleLineFactory.Create(
            server.Id,
            L("main.vm.console.launchMemory", minimumMb, maximumMb, explanation),
            ConsoleLineSeverity.Information));
    }

    private async Task RestartSelectedAsync()
    {
        var server = SelectedServer;
        if (server is null) return;
        if (_productServiceController is not null)
        {
            EnsureProductServiceConnected();
            SetStatus("main.vm.status.serviceRestartRequest", server.Name);
            var result = await ExecuteProductServiceLifecycleWithEulaConfirmationAsync(
                server,
                restart: true,
                allowInteractiveEulaConfirmation: true,
                CancellationToken.None);
            if (result is null)
            {
                return;
            }
            ApplyProductServiceStatus(server, result.Status);
            OnPropertyChanged(nameof(RunningSummary));
            OnPropertyChanged(nameof(HasRunningServers));
            SetStatus("main.vm.status.serviceRestartAccepted", server.Name);
            return;
        }
        var restartEulaAuthorization = await ResolveRestartEulaAuthorizationAsync(
            server,
            allowInteractiveConfirmation: true,
            CancellationToken.None);
        if (restartEulaAuthorization is null)
        {
            return;
        }
        InvalidateAutomaticRestartIntent(server.Id);
        await StopServerCoordinatedAsync(server.Id, CancellationToken.None);
        await StartServerAsync(
            server,
            allowInteractiveJavaDownload: true,
            CancellationToken.None,
            restartEulaAuthorization.Value);
    }

    private async Task StopAllServersCoordinatedAsync(CancellationToken cancellationToken)
    {
        if (_productServiceController is not null)
        {
            // Closing the GUI relinquishes only its IPC session. The Windows Service deliberately
            // keeps every Java process alive until the user issues an explicit stop operation.
            return;
        }

        var instanceIds = Servers.Select(server => server.Id).ToArray();
        foreach (var instanceId in instanceIds)
        {
            InvalidateAutomaticRestartIntent(instanceId);
        }

        // Materialize every task before awaiting so one failed/stubborn process cannot prevent a
        // safe-stop attempt from being delivered to the other independent Server instances.
        await Task.WhenAll(instanceIds.Select(instanceId =>
            StopServerCoordinatedAsync(instanceId, cancellationToken)));
    }

    private async Task<bool> StopServerCoordinatedAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var lifecycleGate = await EnterLifecycleTransitionAsync(instanceId, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await WaitForBackupIdleAsync(instanceId, cancellationToken).ConfigureAwait(false);
            if (_productServiceController is not null)
            {
                EnsureProductServiceConnected();
                var result = await ExecuteProductServiceOperationAsync(
                        token => _productServiceController.StopAsync(instanceId, token),
                        cancellationToken)
                    .ConfigureAwait(false);
                await DispatchProductServiceStatusAsync(result.Status, cancellationToken)
                    .ConfigureAwait(false);
                return result.Changed;
            }
            return await _processManager.StopAsync(instanceId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleTransitions.TryRemove(instanceId, out _);
            lifecycleGate.Release();
        }
    }

    private async Task<Guid> StartProcessCoordinatedAsync(
        ServerInstance instance,
        CancellationToken cancellationToken,
        bool userConfirmedMinecraftEula = false)
    {
        var lifecycleGate = await EnterLifecycleTransitionAsync(instance.Id, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await WaitForBackupIdleAsync(instance.Id, cancellationToken).ConfigureAwait(false);
            return await _processManager.StartAsync(
                    instance,
                    new ServerStartContext(userConfirmedMinecraftEula),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // A successful launch keeps its reservation until the Running state atomically
            // promotes it to ActivePort. Only a failed launch may release it here.
            ReleasePendingLaunchPort(instance.Id);
            throw;
        }
        finally
        {
            _lifecycleTransitions.TryRemove(instance.Id, out _);
            lifecycleGate.Release();
        }
    }

    private async Task<bool> TryStartProcessCoordinatedAsync(
        ServerInstance instance,
        Func<bool> canStart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canStart);
        var lifecycleGate = await EnterLifecycleTransitionAsync(instance.Id, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await WaitForBackupIdleAsync(instance.Id, cancellationToken).ConfigureAwait(false);
            if (!canStart())
            {
                ReleasePendingLaunchPort(instance.Id);
                return false;
            }
            await _processManager.StartAsync(instance, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            ReleasePendingLaunchPort(instance.Id);
            throw;
        }
        finally
        {
            _lifecycleTransitions.TryRemove(instance.Id, out _);
            lifecycleGate.Release();
        }
    }

    private async Task WaitForBackupIdleAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var gate = _backupGates.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        gate.Release();
    }

    private async Task<ServerStopResult> StopServerDetailedCoordinatedAsync(
        Guid instanceId,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken)
    {
        var lifecycleGate = await EnterLifecycleTransitionAsync(instanceId, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await WaitForBackupIdleAsync(instanceId, cancellationToken).ConfigureAwait(false);
            return await _processManager.StopDetailedAsync(
                    instanceId,
                    gracefulTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleTransitions.TryRemove(instanceId, out _);
            lifecycleGate.Release();
        }
    }

    private async Task<SemaphoreSlim> EnterLifecycleTransitionAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var gate = _lifecycleGates.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _lifecycleTransitions[instanceId] = 0;
        return gate;
    }

    private async Task SendCommandAsync(Guid instanceId, string command)
        => await SendCommandOwnedAsync(instanceId, command, CancellationToken.None);

    private async Task SendCommandOwnedAsync(
        Guid instanceId,
        string command,
        CancellationToken cancellationToken)
    {
        if (_productServiceController is not null)
        {
            EnsureProductServiceConnected();
            var status = await ExecuteProductServiceOperationAsync(
                token => _productServiceController.SendCommandAsync(instanceId, command, token),
                cancellationToken);
            await DispatchProductServiceStatusAsync(status, cancellationToken);
            return;
        }
        await _processManager.SendCommandAsync(instanceId, command, cancellationToken);
    }

    /// <summary>
    /// Remote-control entry points deliberately use an immutable instance ID and never mutate
    /// <see cref="SelectedServer"/>. The WPF adapter must invoke these methods on the application
    /// dispatcher so desktop and mobile clients cannot race through global selection state.
    /// </summary>
    internal Task StartServerForRemoteAsync(Guid instanceId, CancellationToken cancellationToken)
        => StartServerAsync(
            FindServerForRemote(instanceId),
            allowInteractiveJavaDownload: false,
            cancellationToken);

    internal async Task StopServerForRemoteAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var server = FindServerForRemote(instanceId);
        InvalidateAutomaticRestartIntent(instanceId);
        SetStatus("main.vm.status.remoteStopping", server.Name);
        if (!await StopServerCoordinatedAsync(instanceId, cancellationToken))
        {
            throw new InvalidOperationException(L("main.vm.error.noStoppableProcess", server.Name));
        }
    }

    internal async Task RestartServerForRemoteAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var server = FindServerForRemote(instanceId);
        if (_productServiceController is not null)
        {
            EnsureProductServiceConnected();
            var result = await ExecuteProductServiceOperationAsync(
                token => _productServiceController.RestartAsync(instanceId, token),
                cancellationToken);
            ApplyProductServiceStatus(server, result.Status);
            return;
        }
        var restartEulaAuthorization = await ResolveRestartEulaAuthorizationAsync(
            server,
            allowInteractiveConfirmation: false,
            cancellationToken);
        InvalidateAutomaticRestartIntent(instanceId);
        await StopServerCoordinatedAsync(instanceId, cancellationToken);
        await StartServerAsync(
            server,
            allowInteractiveJavaDownload: false,
            cancellationToken,
            restartEulaAuthorization.GetValueOrDefault());
    }

    internal async Task SendCommandForRemoteAsync(
        Guid instanceId,
        string command,
        CancellationToken cancellationToken)
    {
        var server = FindServerForRemote(instanceId);
        if (!server.CanSendCommand)
        {
            throw new InvalidOperationException(L("main.vm.error.commandRequiresRunning"));
        }

        var normalized = command?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 512
            || normalized.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new InvalidOperationException(L("main.vm.error.remoteCommandInvalid"));
        }

        if (_productServiceController is not null)
        {
            EnsureProductServiceConnected();
            var status = await ExecuteProductServiceOperationAsync(
                token => _productServiceController.SendCommandAsync(instanceId, normalized, token),
                cancellationToken);
            ApplyProductServiceStatus(server, status);
        }
        else
        {
            await _processManager.SendCommandAsync(instanceId, normalized, cancellationToken);
        }
        SetStatus("main.vm.status.remoteCommandSent", server.Name);
    }

    internal async Task ExecutePlayerActionForRemoteAsync(
        Guid instanceId,
        string action,
        string? playerName,
        string? reason,
        CancellationToken cancellationToken)
    {
        var server = FindServerForRemote(instanceId);
        EnsureServerAcceptsAdministrativeCommands(server);
        var normalizedAction = action?.Trim().ToLowerInvariant() ?? string.Empty;
        string command;
        if (normalizedAction is "whitelist-on" or "whitelist-off")
        {
            command = normalizedAction == "whitelist-on" ? "whitelist on" : "whitelist off";
        }
        else
        {
            if (!IsValidMinecraftPlayerName(playerName))
            {
                throw new InvalidOperationException(
                    L("main.vm.error.playerNameInvalid"));
            }

            var verb = normalizedAction switch
            {
                "kick" => "kick",
                "ban" => "ban",
                "pardon" => "pardon",
                "op" => "op",
                "deop" => "deop",
                "whitelist-add" => "whitelist add",
                "whitelist-remove" => "whitelist remove",
                _ => throw new InvalidOperationException(L("main.vm.error.playerActionUnsupported"))
            };
            var normalizedReason = reason?.Trim();
            if (normalizedReason is { Length: > 160 }
                || normalizedReason?.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                throw new InvalidOperationException(L("main.vm.error.playerReasonInvalid"));
            }

            if (!string.IsNullOrEmpty(normalizedReason)
                && normalizedAction is not "kick" and not "ban")
            {
                throw new InvalidOperationException(L("main.vm.error.playerReasonNotAllowed"));
            }

            command = $"{verb} {playerName!.Trim()}";
            if (!string.IsNullOrEmpty(normalizedReason))
            {
                command += $" {normalizedReason}";
            }
        }

        await SendCommandOwnedAsync(instanceId, command, cancellationToken);
        SetStatus("main.vm.status.remotePlayerActionSent", server.Name);
        if (server.CanAccessLocalFiles || server.IsServiceManaged)
        {
            await Task.Delay(400, cancellationToken);
            _ = await ReloadPlayersQuietlyAsync(server);
        }
    }

    private ServerInstanceViewModel FindServerForRemote(Guid instanceId)
        => Servers.FirstOrDefault(server => server.Id == instanceId)
           ?? throw new KeyNotFoundException(L("main.vm.error.serverNotFound"));

    private async Task RefreshPlayersAsync()
    {
        var server = SelectedServer;
        if (server is null) return;
        if (await ReloadPlayersQuietlyAsync(server))
        {
            SetStatus("main.vm.status.playersReloaded", server.Name);
        }
    }

    private async Task SendPlayerCommandAsync(string command, string actionLabelKey)
    {
        var server = SelectedServer ?? throw new InvalidOperationException(L("main.vm.error.selectServer"));
        EnsureServerAcceptsAdministrativeCommands(server);
        var playerName = string.IsNullOrWhiteSpace(server.PlayerNameInput)
            ? server.SelectedPlayer?.Name
            : server.PlayerNameInput.Trim();
        if (!IsValidMinecraftPlayerName(playerName))
        {
            throw new InvalidOperationException(L("main.vm.error.playerNameInvalid"));
        }

        await SendCommandOwnedAsync(server.Id, $"{command} {playerName}", CancellationToken.None);
        SetStatus("main.vm.status.playerCommandSent", server.Name, L(actionLabelKey), playerName);
        if (server.CanAccessLocalFiles || server.IsServiceManaged)
        {
            await Task.Delay(400);
            _ = await ReloadPlayersQuietlyAsync(server);
        }
    }

    private async Task SendAdministrativeCommandAsync(string command, string completedMessageKey)
    {
        var server = SelectedServer ?? throw new InvalidOperationException(L("main.vm.error.selectServer"));
        EnsureServerAcceptsAdministrativeCommands(server);
        await SendCommandOwnedAsync(server.Id, command, CancellationToken.None);
        SetStatus("main.vm.status.administrativeCommandSent", server.Name, L(completedMessageKey));
        if (server.CanAccessLocalFiles || server.IsServiceManaged)
        {
            await Task.Delay(400);
            _ = await ReloadPlayersQuietlyAsync(server);
        }
    }

    private static void EnsureServerAcceptsAdministrativeCommands(ServerInstanceViewModel server)
    {
        if (server.State != ServerState.Running)
        {
            throw new InvalidOperationException(L("main.vm.error.playerCommandRequiresRunning"));
        }
    }

    private static bool IsValidMinecraftPlayerName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Length <= 16
           && name.All(character => character is >= 'a' and <= 'z'
               or >= 'A' and <= 'Z'
               or >= '0' and <= '9'
               or '_');

    private void OnDiagnosticOutputPreferenceChanged(ServerInstanceViewModel server)
    {
        if (ReferenceEquals(SelectedServer, server)
            && !server.SeparateDiagnosticOutput
            && SelectedWorkspaceTabKey == DiagnosticWorkspaceTabKey)
        {
            SelectedWorkspaceTabKey = ConsoleWorkspaceTabKey;
        }

        OnPropertyChanged(nameof(IsSplitDiagnosticOutputVisible));
        if (!Servers.Contains(server)) return;
        _lastDiagnosticOutputPreferenceSave = GuardAsync(
            async () =>
            {
                await SaveSettingsAsync();
                SetStatus("main.vm.status.consolePreferenceSaved", server.Name);
            },
            "main.vm.operation.saveConsolePreference");
    }

    private void OpenGeneralSettings()
    {
        var application = Application.Current
            ?? throw new InvalidOperationException(L("main.vm.error.applicationResourcesUnavailable"));
        var mainWindow = application.MainWindow as global::MinecraftServerManager.App.MainWindow;
        var originalWindowLayout = mainWindow?.CaptureLayoutForSettingsPreview();
        var editor = new GeneralSettingsViewModel(
            _settings.UserInterface.Copy(),
            _settings.NewServerDefaults.Copy(),
            _settings.NewClientDefaults.Copy(),
            async (userInterface, defaults, clientDefaults, appearance) =>
            {
                try
                {
                    var normalizedAppearance = _appearanceThemeService.Apply(
                        application.Resources,
                        appearance);
                    ApplyFontResources(application.Resources, userInterface.FontSize);
                    await PersistGeneralSettingsValuesAsync(
                        userInterface,
                        defaults,
                        clientDefaults,
                        normalizedAppearance);
                    _previewWindowWidth = null;
                    _previewWindowHeight = null;
                    OnPropertyChanged(nameof(WindowWidth));
                    OnPropertyChanged(nameof(WindowHeight));
                    SetStatus("main.vm.status.generalSettingsApplied");
                }
                catch
                {
                    _appearanceThemeService.Apply(application.Resources, _settings.Appearance);
                    ApplyFontResources(application.Resources, _settings.UserInterface.FontSize);
                    PreviewGeneralSettings(
                        application,
                        new GeneralSettingsPreview(
                            userInterface,
                            appearance,
                            ResizeMainWindow: _previewWindowWidth is not null),
                        mainWindow);
                    throw;
                }
            },
            _systemMemoryProbe,
            preview => PreviewGeneralSettings(application, preview, mainWindow),
            () => RestoreGeneralSettingsPreview(
                application,
                mainWindow,
                originalWindowLayout),
            _productServiceController,
            _productServiceController is null
                ? null
                : () => OpenNotificationSettings(application),
            _productServiceController is null
                ? null
                : () => OpenProviderManagement(application));
        var dialog = new GeneralSettingsDialog(editor)
        {
            Owner = application.MainWindow
        };
        dialog.ShowDialog();
    }

    private void OpenNotificationSettings(Application application)
    {
        if (_productServiceController is null)
        {
            throw new InvalidOperationException(L("main.vm.error.notificationsRequireService"));
        }

        var editor = new ProductNotificationSettingsViewModel(_productServiceController);
        var dialog = new ProductNotificationSettingsDialog(editor)
        {
            Owner = application.MainWindow,
        };
        dialog.ShowDialog();
    }

    private void OpenProviderManagement(Application application)
    {
        var controller = _productServiceController
            ?? throw new InvalidOperationException(
                LocalizationService.Current.Get("provider.error.serviceRequired"));
        EnsureProductServiceConnected();
        var owner = application.Windows
                        .OfType<GeneralSettingsDialog>()
                        .FirstOrDefault(window => window.IsVisible)
                    ?? application.MainWindow;
        var editor = new ProductProviderManagementViewModel(
            controller,
            message => DarkMessageBox.Show(
                           owner,
                           message,
                           LocalizationService.Current.Get("provider.window.title"),
                           MessageBoxButton.YesNo,
                           MessageBoxImage.Warning,
                           MessageBoxResult.No)
                       == MessageBoxResult.Yes);
        var dialog = new ProductProviderManagementDialog(editor)
        {
            Owner = owner,
        };
        dialog.ShowDialog();
    }

    private void PreviewGeneralSettings(
        Application application,
        GeneralSettingsPreview preview,
        global::MinecraftServerManager.App.MainWindow? mainWindow)
    {
        _appearanceThemeService.Apply(application.Resources, preview.Appearance);
        ApplyFontResources(application.Resources, preview.UserInterface.FontSize);
        if (!preview.ResizeMainWindow)
        {
            return;
        }

        _previewWindowWidth = preview.UserInterface.WindowWidth;
        _previewWindowHeight = preview.UserInterface.WindowHeight;
        OnPropertyChanged(nameof(WindowWidth));
        OnPropertyChanged(nameof(WindowHeight));
        mainWindow?.PreviewNormalLayout(WindowWidth, WindowHeight);
    }

    private void RestoreGeneralSettingsPreview(
        Application application,
        global::MinecraftServerManager.App.MainWindow? mainWindow,
        global::MinecraftServerManager.App.MainWindowLayoutSnapshot? originalWindowLayout)
    {
        _previewWindowWidth = null;
        _previewWindowHeight = null;
        _appearanceThemeService.Apply(application.Resources, _settings.Appearance);
        ApplyFontResources(application.Resources, _settings.UserInterface.FontSize);
        OnPropertyChanged(nameof(WindowWidth));
        OnPropertyChanged(nameof(WindowHeight));
        if (mainWindow is not null && originalWindowLayout is { } layout)
        {
            mainWindow.RestoreLayoutAfterSettingsPreview(layout);
        }
    }

    private void OpenServerAppearance(ServerInstanceViewModel? server)
    {
        if (server is null || !Servers.Contains(server)) return;
        var application = Application.Current
            ?? throw new InvalidOperationException(L("main.vm.error.applicationWindowUnavailable"));
        SelectedServer = server;
        var dialog = new ServerAppearanceSettingsDialog(this)
        {
            Owner = application.MainWindow
        };
        dialog.ShowDialog();
    }

    private async Task SaveServerAppearanceAsync(ServerInstanceViewModel? server)
    {
        if (server is null || !Servers.Contains(server)) return;
        server.BackgroundImageOpacity = Math.Clamp(server.BackgroundImageOpacity, 0, 1);
        PersistServiceAppearancePreference(server);
        await SaveSettingsAsync();
        SetStatus("main.vm.status.appearanceApplied", server.Name);
    }

    internal static bool IsRemoteAccessConfigurationComplete(RemoteControlSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.LocalPort is < 1024 or > 65535 || !Enum.IsDefined(settings.AccessMode))
        {
            return false;
        }

        if (settings.AccessMode == RemoteAccessMode.Tailscale)
        {
            return RemoteIdentity.IsCanonicalGmailLogin(settings.AllowedLogin);
        }

        if (settings.AccessMode == RemoteAccessMode.TailscaleFunnel)
        {
            return true;
        }

        var executablePath = settings.CloudflaredExecutablePath?.Trim() ?? string.Empty;
        var hasExecutable = Path.IsPathFullyQualified(executablePath)
                            && File.Exists(executablePath)
                            && string.Equals(
                                Path.GetFileName(executablePath),
                                "cloudflared.exe",
                                StringComparison.OrdinalIgnoreCase);
        if (!hasExecutable)
        {
            return false;
        }

        return settings.AccessMode != RemoteAccessMode.CloudflareNamedTunnel
               || CloudflareNamedTunnelConfiguration.TryNormalizePublicOrigin(
                   settings.CloudflareNamedPublicOrigin,
                   out _);
    }

    private async Task<string?> InitializeRemoteAccessAsync(Application? application)
    {
        if (application is null
            || !application.Dispatcher.CheckAccess()
            || application.Dispatcher.HasShutdownStarted
            || application.Dispatcher.HasShutdownFinished)
        {
            return null;
        }

        // In formal product mode the Windows Service is the only owner of Kestrel, Funnel,
        // credentials, remembered devices, and remote desired state. Never construct the legacy
        // in-process coordinator (or its WPF backend/security store) on this path.
        if (_productServiceController is not null)
        {
            OpenRemoteManagementCommand.NotifyCanExecuteChanged();
            OpenRemoteWebConsoleCommand.NotifyCanExecuteChanged();
            if (!IsProductServiceConnected)
            {
                return L("main.vm.remote.serviceManaged", ProductServiceConnectionText);
            }

            try
            {
                var status = await _productServiceController.GetRemoteAccessStatusAsync(
                    _applicationShutdownCancellation.Token);
                return status.HostRunning && status.FunnelRunning
                    ? L("main.vm.remote.serviceRunning", status.PublicUrl ?? status.State)
                    : status.DesiredEnabled
                        ? L("main.vm.remote.serviceConnecting", status.ErrorCode ?? status.State)
                        : L("main.vm.remote.serviceDisabled");
            }
            catch (OperationCanceledException) when (_applicationShutdownCancellation.IsCancellationRequested)
            {
                return L("main.vm.remote.guiClosingServiceUnaffected");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return L("main.vm.remote.serviceStatusFailed", error.Message);
            }
        }

        try
        {
            RemoteAccessCoordinator coordinator;
            lock (_remoteAccessLifecycleSync)
            {
                if (_applicationShutdownCancellation.IsCancellationRequested)
                {
                    return L("main.vm.remote.closing");
                }

                _remoteAccessCoordinator ??= new RemoteAccessCoordinator(
                    new WpfRemoteControlBackend(this, application.Dispatcher),
                    securityStore: new RemoteSecurityStore(_paths.RemoteSecurityFile),
                    applicationStopping: _applicationShutdownCancellation.Token);
                coordinator = _remoteAccessCoordinator;
            }

            coordinator.StateChanged -= OnRemoteAccessStateChanged;
            coordinator.StateChanged += OnRemoteAccessStateChanged;
            UpdateRemoteAccessRecoverySettings(_settings.RemoteControl);
            OpenRemoteManagementCommand.NotifyCanExecuteChanged();
            OpenRemoteWebConsoleCommand.NotifyCanExecuteChanged();
            if (!IsRemoteAccessConfigurationComplete(_settings.RemoteControl))
            {
                return L("main.vm.remote.waitingConfiguration");
            }
            if (_settings.RemoteControl.AccessMode == RemoteAccessMode.CloudflareNamedTunnel
                && !coordinator.HasCloudflareNamedTunnelToken)
            {
                return L("main.vm.remote.waitingTunnelToken");
            }

            var runtime = await coordinator.StartAsync(
                _settings.RemoteControl.Copy(),
                _applicationShutdownCancellation.Token);
            return runtime.IsRunning
                ? runtime.StatusMessage
                : runtime.AutoRetryRecommended
                    ? L("main.vm.remote.notEnabledRetrying", runtime.Error ?? runtime.StatusMessage)
                    : L("main.vm.remote.notEnabled", runtime.Error ?? runtime.StatusMessage);
        }
        catch (OperationCanceledException) when (_applicationShutdownCancellation.IsCancellationRequested)
        {
            return L("main.vm.remote.closing");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A malformed tool installation, DPAPI problem, or temporary network failure must
            // never prevent the desktop manager and its Minecraft servers from loading.
            return L("main.vm.remote.initializationFailed", error.Message);
        }
    }

    private void OpenRemoteAccess()
    {
        if (_productServiceController is not null)
        {
            OpenProductServiceRemoteAccess();
            return;
        }

        var application = Application.Current;
        RemoteAccessSettingsViewModel? editor = null;
        RemoteAccessDialog? dialog = null;
        try
        {
            if (application is null)
            {
                throw new InvalidOperationException(L("main.vm.error.applicationWindowUnavailable"));
            }
            var coordinator = _remoteAccessCoordinator
                ?? throw new InvalidOperationException(L("main.vm.error.remoteNotInitialized"));
            if (_remoteAccessDialog is { IsLoaded: true } existing)
            {
                PrimaryDisplayWindowPlacement.ActivateWhenInteractive(existing);
                return;
            }

            editor = new RemoteAccessSettingsViewModel(
                _settings.RemoteControl.Copy(),
                coordinator,
                PersistRemoteAccessSettingsAsync,
                application.Dispatcher,
                _remoteAccessSessionState,
                _applicationShutdownCancellation.Token);
            dialog = new RemoteAccessDialog(editor)
            {
                Owner = application.MainWindow
            };
            dialog.OpenWebConsoleRequested += OnRemoteWebConsoleRequested;
            dialog.Closed += OnRemoteAccessDialogClosed;
            _remoteAccessDialog = dialog;
            dialog.Show();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (dialog is not null)
            {
                dialog.OpenWebConsoleRequested -= OnRemoteWebConsoleRequested;
                dialog.Closed -= OnRemoteAccessDialogClosed;
                if (ReferenceEquals(_remoteAccessDialog, dialog))
                {
                    _remoteAccessDialog = null;
                }

                try
                {
                    dialog.Close();
                }
                catch (Exception cleanupException) when (cleanupException is not OutOfMemoryException)
                {
                    // The original construction/layout exception is the useful failure. The
                    // editor is still explicitly disposed below if WPF cannot close the window.
                }
            }

            editor?.Dispose();
            SetStatus("main.vm.remote.openSettingsFailed", exception.Message);
            if (application?.MainWindow is { IsLoaded: true, IsVisible: true } owner
                && !owner.Dispatcher.HasShutdownStarted
                && !owner.Dispatcher.HasShutdownFinished)
            {
                DarkMessageBox.Show(
                    owner,
                    exception.Message,
                    L("main.vm.remote.openSettingsFailedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    private void OpenRemoteWebConsole()
    {
        if (_productServiceController is not null)
        {
            // The formal Service dialog contains live lifecycle state plus safe copy/open URL
            // actions. Reusing it avoids reintroducing the old in-process console/coordinator.
            OpenProductServiceRemoteAccess();
            return;
        }

        var application = Application.Current
            ?? throw new InvalidOperationException(L("main.vm.error.applicationWindowUnavailable"));
        var coordinator = _remoteAccessCoordinator
            ?? throw new InvalidOperationException(L("main.vm.error.remoteNotInitialized"));
        if (_remoteWebConsoleDialog is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            PrimaryDisplayWindowPlacement.ActivateWhenInteractive(existing);
            return;
        }

        var editor = new RemoteWebConsoleViewModel(
            coordinator,
            StartQuickWebFromConsoleAsync,
            StopQuickWebFromConsoleAsync,
            application.Dispatcher);
        var dialog = new RemoteWebConsoleDialog(editor)
        {
            Owner = application.MainWindow
        };
        dialog.Closed += (_, _) => _remoteWebConsoleDialog = null;
        _remoteWebConsoleDialog = dialog;
        dialog.Show();
    }

    private void OpenProductServiceRemoteAccess()
    {
        var application = Application.Current;
        ProductServiceRemoteAccessViewModel? editor = null;
        ProductServiceRemoteAccessDialog? dialog = null;
        try
        {
            if (application is null)
            {
                throw new InvalidOperationException(L("main.vm.error.applicationWindowUnavailable"));
            }

            var controller = _productServiceController
                ?? throw new InvalidOperationException(L("main.vm.error.serviceRemoteNotInitialized"));
            EnsureProductServiceConnected();
            if (_productServiceRemoteAccessDialog is { IsLoaded: true } existing)
            {
                if (existing.WindowState == WindowState.Minimized)
                {
                    existing.WindowState = WindowState.Normal;
                }
                PrimaryDisplayWindowPlacement.ActivateWhenInteractive(existing);
                return;
            }

            editor = new ProductServiceRemoteAccessViewModel(
                controller,
                Servers.Select(server => new ProductServiceRemoteServerOption(server.Id, server.Name)),
                message => DarkMessageBox.Show(
                               application.MainWindow,
                               message,
                               L("main.vm.remote.confirmSafeOperation"),
                               MessageBoxButton.YesNo,
                               MessageBoxImage.Warning,
                               MessageBoxResult.No)
                           == MessageBoxResult.Yes);
            dialog = new ProductServiceRemoteAccessDialog(editor)
            {
                Owner = application.MainWindow
            };
            dialog.Closed += OnProductServiceRemoteAccessDialogClosed;
            _productServiceRemoteAccessDialog = dialog;
            dialog.Show();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (dialog is not null)
            {
                dialog.Closed -= OnProductServiceRemoteAccessDialogClosed;
                if (ReferenceEquals(_productServiceRemoteAccessDialog, dialog))
                {
                    _productServiceRemoteAccessDialog = null;
                }

                try
                {
                    dialog.Close();
                }
                catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException)
                {
                    _ = cleanupError;
                }
            }
            else
            {
                editor?.Dispose();
            }

            SetStatus("main.vm.remote.openServiceManagementFailed", exception.Message);
            if (application?.MainWindow is { IsLoaded: true, IsVisible: true } owner)
            {
                DarkMessageBox.Show(
                    owner,
                    exception.Message,
                    L("main.vm.remote.openManagementFailedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    private void OnProductServiceRemoteAccessDialogClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_productServiceRemoteAccessDialog, sender))
        {
            _productServiceRemoteAccessDialog = null;
        }
    }

    private async Task StartQuickWebFromConsoleAsync()
    {
        _applicationShutdownCancellation.Token.ThrowIfCancellationRequested();
        var coordinator = _remoteAccessCoordinator
            ?? throw new InvalidOperationException(L("main.vm.error.remoteNotInitialized"));
        var settings = _settings.RemoteControl.Copy();
        if (settings.AccessMode is not (RemoteAccessMode.CloudflareQuickTunnel
            or RemoteAccessMode.CloudflareNamedTunnel
            or RemoteAccessMode.TailscaleFunnel))
        {
            throw new InvalidOperationException(L("main.vm.error.remoteModeUnsupported"));
        }
        if (!IsRemoteAccessConfigurationComplete(settings))
        {
            throw new InvalidOperationException(L("main.vm.error.remoteConfigurationIncomplete"));
        }

        _remoteAccessSessionState.ClearForExplicitReconnect();
        settings.Enabled = true;
        await PersistRemoteAccessSettingsAsync(settings);
        _applicationShutdownCancellation.Token.ThrowIfCancellationRequested();
        var runtime = await coordinator.StartAsync(
            settings,
            _applicationShutdownCancellation.Token);
        if (!runtime.IsRunning)
        {
            throw new InvalidOperationException(runtime.Error ?? runtime.StatusMessage);
        }
    }

    private async Task StopQuickWebFromConsoleAsync()
    {
        _applicationShutdownCancellation.Token.ThrowIfCancellationRequested();
        var coordinator = _remoteAccessCoordinator
            ?? throw new InvalidOperationException(L("main.vm.error.remoteNotInitialized"));
        _remoteAccessSessionState.MarkStoppedForCurrentRun();
        CancelRemoteAccessRecovery();
        var runtime = await coordinator.StopAsync(
            disableOwnedServe: true,
            _applicationShutdownCancellation.Token);
        if (runtime.Error is not null)
        {
            throw new InvalidOperationException(runtime.Error);
        }
    }

    private void OnRemoteAccessDialogClosed(object? sender, EventArgs e)
    {
        if (sender is RemoteAccessDialog dialog)
        {
            dialog.OpenWebConsoleRequested -= OnRemoteWebConsoleRequested;
        }
        if (ReferenceEquals(_remoteAccessDialog, sender))
        {
            _remoteAccessDialog = null;
        }
    }

    private void OnRemoteWebConsoleRequested(object? sender, EventArgs e)
        => OpenRemoteWebConsole();

    private async Task PersistRemoteAccessSettingsAsync(RemoteControlSettings settings)
    {
        _applicationShutdownCancellation.Token.ThrowIfCancellationRequested();
        await _settingsSaveGate.WaitAsync(_applicationShutdownCancellation.Token);
        try
        {
            var previous = _settings.RemoteControl;
            _settings.RemoteControl = settings.Copy();
            _settings.SchemaVersion = Math.Max(
                _settings.SchemaVersion,
                ManagerSettings.CurrentSchemaVersion);
            try
            {
                await SaveSettingsLockedAsync(_applicationShutdownCancellation.Token);
                UpdateRemoteAccessRecoverySettings(settings);
                SetStatus("main.vm.remote.settingsSaved");
            }
            catch
            {
                _settings.RemoteControl = previous;
                throw;
            }
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private void OnRemoteAccessStateChanged(object? sender, RemoteAccessRuntimeState state)
    {
        if (_isDisposed || _applicationShutdownCancellation.IsCancellationRequested) return;
        if (state.IsRunning)
        {
            CancelRemoteAccessRecovery();
            DispatchRemoteAccessStatus(state.StatusMessage);
            return;
        }

        if (!state.IsStarting && state.AutoRetryRecommended)
        {
            ScheduleRemoteAccessRecovery();
        }
        else if (!state.IsStarting)
        {
            CancelRemoteAccessRecovery();
        }
    }

    private void UpdateRemoteAccessRecoverySettings(RemoteControlSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var shouldCancel = false;
        lock (_remoteAccessRecoverySync)
        {
            _remoteAccessRecoverySettings = settings.Copy();
            _remoteAccessRecoveryConfigurationComplete =
                IsRemoteAccessConfigurationComplete(settings);
            shouldCancel = !settings.Enabled
                           || !_remoteAccessRecoveryConfigurationComplete
                           || _remoteAccessSessionState.IsStoppedForCurrentRun;
        }

        if (shouldCancel)
        {
            CancelRemoteAccessRecovery();
        }
    }

    private void ScheduleRemoteAccessRecovery()
    {
        if (_isDisposed || _applicationShutdownCancellation.IsCancellationRequested) return;
        CancellationTokenSource recoveryCancellation;
        lock (_remoteAccessRecoverySync)
        {
            if (!_remoteAccessRecoverySettings.Enabled ||
                !_remoteAccessRecoveryConfigurationComplete ||
                _remoteAccessSessionState.IsStoppedForCurrentRun ||
                _remoteAccessCoordinator is null ||
                _remoteAccessCoordinator.State.IsRunning ||
                _remoteAccessRecoveryCancellation is not null)
            {
                return;
            }

            recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _applicationShutdownCancellation.Token);
            _remoteAccessRecoveryCancellation = recoveryCancellation;
            _remoteAccessRecoveryTask = Task.Run(
                () => RunRemoteAccessRecoveryAsync(recoveryCancellation));
        }
    }

    private async Task RunRemoteAccessRecoveryAsync(CancellationTokenSource owner)
    {
        var delays = new[]
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2)
        };
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await Task.Delay(delays[Math.Min(attempt, delays.Length - 1)], owner.Token)
                    .ConfigureAwait(false);

                RemoteAccessCoordinator? coordinator;
                RemoteControlSettings settings;
                lock (_remoteAccessRecoverySync)
                {
                    if (!_remoteAccessRecoverySettings.Enabled
                        || !_remoteAccessRecoveryConfigurationComplete
                        || _remoteAccessSessionState.IsStoppedForCurrentRun)
                    {
                        return;
                    }
                    coordinator = _remoteAccessCoordinator;
                    settings = _remoteAccessRecoverySettings.Copy();
                }

                if (coordinator is null) return;
                var runtime = await coordinator.StartAsync(settings, owner.Token)
                    .ConfigureAwait(false);
                if (runtime.IsRunning)
                {
                    DispatchLocalizedRemoteAccessStatus(
                        "main.vm.remote.recoveredInBackground",
                        runtime.StatusMessage);
                    return;
                }

                if (!runtime.AutoRetryRecommended) return;

                owner.Token.ThrowIfCancellationRequested();
                DispatchLocalizedRemoteAccessStatus(
                    "main.vm.remote.recoveryRetrying",
                    runtime.Error ?? runtime.StatusMessage);
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
            // Expected when the setting is disabled, service recovers, or the app closes.
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
            // The coordinator was disposed as part of application shutdown.
        }
        finally
        {
            var shouldRestart = false;
            lock (_remoteAccessRecoverySync)
            {
                if (ReferenceEquals(_remoteAccessRecoveryCancellation, owner))
                {
                    _remoteAccessRecoveryCancellation = null;
                    shouldRestart = _remoteAccessRecoverySettings.Enabled
                                    && _remoteAccessRecoveryConfigurationComplete
                                    && !_remoteAccessSessionState.IsStoppedForCurrentRun
                                    && owner.IsCancellationRequested
                                    && _remoteAccessCoordinator is
                                    {
                                        State.IsRunning: false,
                                        State.AutoRetryRecommended: true
                                    };
                }
            }

            owner.Dispose();
            if (shouldRestart && !_isDisposed && !_applicationShutdownCancellation.IsCancellationRequested)
            {
                ScheduleRemoteAccessRecovery();
            }
        }
    }

    private void CancelRemoteAccessRecovery()
    {
        CancellationTokenSource? cancellation;
        lock (_remoteAccessRecoverySync)
        {
            cancellation = _remoteAccessRecoveryCancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker completed between taking the snapshot and cancellation.
        }
    }

    /// <summary>
    /// Starts the process-lifetime remote shutdown exactly once. The returned task never depends
    /// on the WPF dispatcher, so App.OnExit may perform a bounded fallback wait without deadlocking
    /// the UI thread. Failures are deliberately contained because remote access is auxiliary.
    /// </summary>
    internal Task EnsureRemoteAccessStoppedForApplicationExitAsync()
    {
        lock (_remoteAccessShutdownSync)
        {
            if (_remoteAccessShutdownTask is not null)
            {
                return _remoteAccessShutdownTask;
            }

            _remoteAccessSessionState.MarkStoppedForCurrentRun();
            try
            {
                _applicationShutdownCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // DisposeAsync can win a late App.OnExit fallback. Its cached cleanup task is
                // normally returned above; this guard keeps an abnormal exit fail-soft as well.
            }

            _remoteAccessShutdownTask = StopRemoteAccessForApplicationExitCoreAsync();
            return _remoteAccessShutdownTask;
        }
    }

    private async Task StopRemoteAccessForApplicationExitCoreAsync()
    {
        try
        {
            await StopRemoteAccessRecoveryAsync().ConfigureAwait(false);
            RemoteAccessCoordinator? coordinator;
            lock (_remoteAccessLifecycleSync)
            {
                coordinator = _remoteAccessCoordinator;
            }

            if (coordinator is null)
            {
                return;
            }

            var runtime = await coordinator.StopAsync(
                    disableOwnedServe: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (runtime.Error is not null)
            {
                DispatchLocalizedRemoteAccessStatus(
                    "main.vm.remote.stopFailed",
                    runtime.Error);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DispatchLocalizedRemoteAccessStatus(
                "main.vm.remote.stopFailed",
                error.Message);
        }
    }

    private async Task StopRemoteAccessRecoveryAsync()
    {
        Task recoveryTask;
        CancelRemoteAccessRecovery();
        lock (_remoteAccessRecoverySync)
        {
            recoveryTask = _remoteAccessRecoveryTask;
        }

        try
        {
            await recoveryTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the requested shutdown behavior.
        }
    }

    private void DispatchRemoteAccessStatus(string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        _ = dispatcher.BeginInvoke(
            () =>
            {
                if (!_isDisposed) StatusMessage = message;
            },
            DispatcherPriority.Background);
    }

    private void DispatchLocalizedRemoteAccessStatus(string key, params object?[] arguments)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        _ = dispatcher.BeginInvoke(
            () =>
            {
                if (!_isDisposed) SetStatus(key, arguments);
            },
            DispatcherPriority.Background);
    }

    private async Task OpenSelectedFolderAsync()
    {
        var server = SelectedServer;
        if (server is null || !Servers.Contains(server))
        {
            return;
        }

        if (!server.IsServiceManaged)
        {
            if (server.CanAccessLocalFiles)
            {
                OpenFolder(server.DirectoryPath);
            }

            return;
        }

        EnsureProductServiceConnected();
        if (!SupportsProductServiceFileAdministration || _productServiceController is null)
        {
            throw new InvalidOperationException(
                L("main.vm.error.serviceFileAdministrationUnsupported"));
        }

        var directory = await ExecuteProductServiceOperationAsync(
            token => _productServiceController.GetServerDirectoryAsync(server.Id, token),
            CancellationToken.None);
        if (directory.ServerId != server.Id)
        {
            throw new InvalidDataException(L("main.vm.error.serviceDirectoryMismatch"));
        }
        if (!directory.Exists || !Directory.Exists(directory.DirectoryPath))
        {
            throw new DirectoryNotFoundException(
                L("main.vm.error.serviceDirectoryMissing"));
        }

        OpenExistingFolder(directory.DirectoryPath);
    }

    internal async Task RemoveServerAsync(ServerInstanceViewModel? server)
        => await RunServerListMutationAsync(() => RemoveServerCoreAsync(server));

    internal async Task DeleteServerPermanentlyAsync(ServerInstanceViewModel? server)
        => await RunServerListMutationAsync(() => DeleteServerPermanentlyCoreAsync(server));

    private async Task RunServerListMutationAsync(Func<Task> operation)
    {
        if (IsBatchLifecycleOperationRunning)
        {
            throw new InvalidOperationException(L("main.vm.error.batchBlocksRemoval"));
        }
        if (_isServerListMutationRunning)
        {
            throw new InvalidOperationException(L("main.vm.error.listMutationAlreadyRunning"));
        }

        _isServerListMutationRunning = true;
        ToggleBulkSelectionModeCommand.NotifyCanExecuteChanged();
        NotifyCheckedServerCommandsCanExecuteChanged();
        RemoveServerCommand.NotifyCanExecuteChanged();
        DeleteServerCommand.NotifyCanExecuteChanged();
        try
        {
            await operation();
        }
        finally
        {
            _isServerListMutationRunning = false;
            ToggleBulkSelectionModeCommand.NotifyCanExecuteChanged();
            NotifyCheckedServerCommandsCanExecuteChanged();
            RemoveServerCommand.NotifyCanExecuteChanged();
            DeleteServerCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RemoveServerCoreAsync(ServerInstanceViewModel? server)
    {
        if (server is null || !Servers.Contains(server)) return;
        if (server.IsServiceManaged && server.State != ServerState.Stopped)
        {
            throw new InvalidOperationException(
                L("main.vm.error.serviceRemovalRequiresStopped"));
        }
        if (server.State is ServerState.Starting or ServerState.Running or ServerState.Stopping)
        {
            throw new InvalidOperationException(L("main.vm.error.removalRequiresStopped"));
        }

        var displayedDirectory = server.IsServiceManaged
            ? L("main.vm.removal.serviceDirectoryDescription")
            : server.DirectoryPath;
        if (!_serverRemovalConfirmationService.ConfirmRemoval(server.Name, displayedDirectory)) return;

        // A process transition can occur while the modal confirmation is open (for example an
        // automatic restart). Re-check the exact row before mutating the management list.
        if (!Servers.Contains(server)) return;
        if (server.IsServiceManaged && server.State != ServerState.Stopped)
        {
            throw new InvalidOperationException(
                L("main.vm.error.serviceRemovalStateChanged"));
        }
        if (server.State is ServerState.Starting or ServerState.Running or ServerState.Stopping)
        {
            throw new InvalidOperationException(L("main.vm.error.removalServerStarted"));
        }

        if (_productServiceController is not null && server.IsServiceManaged)
        {
            EnsureProductServiceConnected();
            await ExecuteProductServiceOperationAsync(
                async token =>
                {
                    await _productServiceController.RemoveAsync(server.Id, token);
                    return true;
                },
                CancellationToken.None);
            var removedAppearance = _settings.ServiceServerAppearances.Remove(server.Id);
            if (removedAppearance)
            {
                await SaveSettingsAsync();
            }
            if (removedAppearance)
            {
                await ReleaseThemeImageBindingsAsync();
                DeleteManagedThemeCopies(server.Id, "backgrounds");
                DeleteManagedThemeCopies(server.Id, "icons");
                DeleteManagedThemeCopies(server.Id, "catalog-icons");
                DeleteManagedThemeCopies(server.Id, "catalog-previews");
            }
            // Removing the projection is the UI-visible completion signal. Keep it last so code
            // observing Servers no longer sees the row only after its durable preference and all
            // GUI-owned asset copies have finished their causal cleanup.
            RemoveProductServiceProjection(server);
            SetStatus("main.vm.status.removedFromServiceList");
            return;
        }

        InvalidateAutomaticRestartIntent(server.Id);
        // StopCore also advances the Core generation when no process is currently present. This
        // cancels an automatic restart that may have passed its earlier UI-policy check while the
        // confirmation dialog was open; if it already launched, the same call safely stops it.
        await StopServerCoordinatedAsync(server.Id, CancellationToken.None);
        if (!Servers.Contains(server)) return;
        await PersistAndRemoveManagedServerRecordAsync(server);
        SetStatus("main.vm.status.removedFromList");
    }

    private void RemoveProductServiceProjection(ServerInstanceViewModel server)
    {
        if (!Servers.Contains(server)) return;
        var removedIndex = Servers.IndexOf(server);
        var wasSelected = ReferenceEquals(SelectedServer, server);
        _dirtyProductServiceRegistrations.Remove(server.Id);
        _instanceModels.TryRemove(server.Id, out _);
        _playerPresenceCoreTypes.TryRemove(server.Id, out _);
        _playerPresenceSessions.Remove(server.Id);
        Servers.Remove(server);
        if (wasSelected)
        {
            SelectedServer = Servers.Count == 0
                ? null
                : Servers[Math.Min(removedIndex, Servers.Count - 1)];
        }
        if (SecondaryServer?.Id == server.Id)
        {
            SecondaryServer = Servers.FirstOrDefault();
        }
        OnPropertyChanged(nameof(ServerCountText));
        OnPropertyChanged(nameof(RunningSummary));
    }

    private async Task DeleteServerPermanentlyCoreAsync(ServerInstanceViewModel? server)
    {
        if (server is null || !Servers.Contains(server)) return;
        if (server.IsServiceManaged)
        {
            await DeleteProductServiceServerPermanentlyAsync(server);
            return;
        }
        if (IsProductServiceRuntime)
        {
            throw new InvalidOperationException(
                L("main.vm.error.servicePermanentDeleteUnsupported"));
        }

        var expectedDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(server.DirectoryPath));
        var expectedName = server.Name;
        // Capture the directory's stable Windows volume/file identity before the modal prompt.
        // The final handle-based deleter compares this identity while holding the destructive
        // handle, so a rename/swap during confirmation or shutdown cannot redirect consent.
        using var expectedIdentityLease = _serverDirectoryDeletionService.CaptureDeletionIdentity(
            expectedDirectory);
        if (!_serverDeletionConfirmationService.ConfirmDeletion(expectedName, expectedDirectory))
        {
            return;
        }

        // The exact context row and its configured path are security boundaries. A modal dialog
        // can remain open while bindings or an automatic restart change state, so neither a new
        // row nor a newly assigned path may inherit this confirmation.
        if (!Servers.Contains(server)) return;
        if (!PathsEqual(server.DirectoryPath, expectedDirectory))
        {
            throw new InvalidOperationException(
                L("main.vm.error.deletePathChangedDuringConfirm"));
        }

        InvalidateAutomaticRestartIntent(server.Id);
        SetStatus("main.vm.status.waitingBackupAndStopping", expectedName);
        var lifecycleGate = await EnterLifecycleTransitionAsync(server.Id, CancellationToken.None);
        try
        {
            await WaitForBackupIdleAsync(server.Id, CancellationToken.None);
            if (!Servers.Contains(server) || !PathsEqual(server.DirectoryPath, expectedDirectory))
            {
                throw new InvalidOperationException(
                    L("main.vm.error.deletePathChangedBeforeStop"));
            }

            // StopAsync performs the normal Minecraft stop sequence first and only force-kills
            // after the configured timeout. It also advances the process generation when no child
            // is currently present, invalidating a pending automatic restart.
            await _processManager.StopAsync(server.Id, cancellationToken: CancellationToken.None);
            if (_processManager.TryGetSnapshot(server.Id, out var snapshot)
                && snapshot.State is ServerState.Starting or ServerState.Running or ServerState.Stopping)
            {
                throw new InvalidOperationException(
                    L("main.vm.error.deleteProcessStillRunning"));
            }

            if (!Servers.Contains(server) || !PathsEqual(server.DirectoryPath, expectedDirectory))
            {
                throw new InvalidOperationException(
                    L("main.vm.error.deletePathChangedAfterStop"));
            }

            SetStatus("main.vm.status.deletingPermanently", expectedName);
            var otherManagedDirectories = Servers
                .Where(item => !ReferenceEquals(item, server))
                .Select(item => item.DirectoryPath)
                .ToArray();
            await _serverDirectoryDeletionService.DeleteAsync(
                expectedDirectory,
                otherManagedDirectories,
                expectedIdentityLease.Identity,
                CancellationToken.None);

            // Persist the management-list mutation only after physical deletion completed. If
            // validation, stop, no-follow deletion, or settings persistence fails, the row stays.
            await PersistAndRemoveManagedServerRecordAsync(server);
        }
        finally
        {
            _lifecycleTransitions.TryRemove(server.Id, out _);
            lifecycleGate.Release();
        }

        SetStatus("main.vm.status.deletedPermanently", expectedName);
    }

    private async Task DeleteProductServiceServerPermanentlyAsync(
        ServerInstanceViewModel server)
    {
        EnsureProductServiceConnected();
        if (!SupportsProductServiceFileAdministration || _productServiceController is null)
        {
            throw new InvalidOperationException(
                L("main.vm.error.serviceFileAdministrationUnsupported"));
        }

        var directory = await ExecuteProductServiceOperationAsync(
            token => _productServiceController.GetServerDirectoryAsync(server.Id, token),
            CancellationToken.None);
        if (directory.ServerId != server.Id)
        {
            throw new InvalidDataException(L("main.vm.error.serviceDirectoryMismatch"));
        }

        var expectedName = server.Name;
        if (!_serverDeletionConfirmationService.ConfirmDeletion(
                expectedName,
                directory.DirectoryPath))
        {
            return;
        }

        // The selected context row is part of the destructive-operation consent. The Service
        // owns the actual path and resolves it again from its durable registry, so the desktop
        // never submits a deletion path.
        if (!Servers.Contains(server) || !server.IsServiceManaged)
        {
            return;
        }

        SetStatus("main.vm.status.waitingBackupAndStopping", expectedName);
        var result = await ExecuteProductServiceOperationAsync(
            token => _productServiceController.DeleteServerPermanentlyAsync(server.Id, token),
            CancellationToken.None);
        if (result.ServerId != server.Id || !result.Deleted)
        {
            throw new InvalidDataException(L("main.vm.error.serviceDeleteResultMismatch"));
        }

        var removedAppearance = _settings.ServiceServerAppearances.Remove(server.Id);
        if (removedAppearance)
        {
            await SaveSettingsAsync();
            await ReleaseThemeImageBindingsAsync();
            DeleteManagedThemeCopies(server.Id, "backgrounds");
            DeleteManagedThemeCopies(server.Id, "icons");
            DeleteManagedThemeCopies(server.Id, "catalog-icons");
            DeleteManagedThemeCopies(server.Id, "catalog-previews");
        }

        RemoveProductServiceProjection(server);
        SetStatus("main.vm.status.deletedPermanently", expectedName);
    }

    private async Task PersistAndRemoveManagedServerRecordAsync(ServerInstanceViewModel server)
    {
        await _settingsSaveGate.WaitAsync();
        try
        {
            if (!Servers.Contains(server)) return;

            var removedIndex = Servers.IndexOf(server);
            var nextSettings = new ManagerSettings
            {
                SchemaVersion = Math.Max(
                    _settings.SchemaVersion,
                    ManagerSettings.CurrentSchemaVersion),
                Appearance = _settings.Appearance,
                RemoteControl = _settings.RemoteControl.Copy(),
                UserInterface = _settings.UserInterface.Copy(),
                NewServerDefaults = _settings.NewServerDefaults.Copy(),
                NewClientDefaults = _settings.NewClientDefaults.Copy(),
                ServiceServerAppearances = _settings.ServiceServerAppearances.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.Copy()),
                Instances = Servers
                    .Where(item => !ReferenceEquals(item, server))
                    .Select(item => item.Model)
                    .ToList()
            };

            // Serialize the snapshot and UI mutation with background commits. The next writer
            // always observes the exact collection represented by the previous atomic file.
            await _settingsStore.SaveAsync(CloneManagerSettings(nextSettings));
            _settings = nextSettings;

            var wasSelected = ReferenceEquals(SelectedServer, server);
            _serverPropertiesFormats.TryRemove(server.Id, out _);
            _playerPresenceCoreTypes.TryRemove(server.Id, out _);
            _loadedPlayerRegistries.TryRemove(server.Id, out _);
            _instanceModels.TryRemove(server.Id, out _);
            _crashPlans.TryRemove(server.Id, out _);
            _lastHealthyRecoveryPoints.TryRemove(server.Id, out _);
            _latestConsoleSessions.TryRemove(server.Id, out _);
            _pendingResourceSamples.TryRemove(server.Id, out _);
            _scheduledResourceSampleDrains.TryRemove(server.Id, out _);
            _pendingConsoleLines.TryRemove(server.Id, out _);
            _scheduledConsoleDrains.TryRemove(server.Id, out _);
            _playerPresenceBuffer.RemoveInstance(server.Id);
            _scheduledPresenceDrains.TryRemove(server.Id, out _);
            Servers.Remove(server);
            if (wasSelected)
            {
                SelectedServer = Servers.Count == 0
                    ? null
                    : Servers[Math.Min(removedIndex, Servers.Count - 1)];
            }
            if (SecondaryServer?.Id == server.Id) SecondaryServer = Servers.FirstOrDefault();
            OnPropertyChanged(nameof(ServerCountText));
            OnPropertyChanged(nameof(RunningSummary));
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private async Task SaveSelectedSettingsAsync()
    {
        var server = SelectedServer;
        if (server is null) return;
        if (server.State == ServerState.Starting)
        {
            throw new InvalidOperationException(L("main.vm.error.savePortWhileStarting"));
        }
        if (string.IsNullOrWhiteSpace(server.Name)) throw new InvalidOperationException(L("main.vm.error.serverNameRequired"));
        if (server.Port is < 1 or > 65535) throw new InvalidOperationException(L("main.vm.error.portRange"));
        if (server.Model.MemoryAllocationMode is MemoryAllocationMode.Manual or MemoryAllocationMode.Legacy
            && server.MaximumMemoryMb < server.MinimumMemoryMb)
        {
            throw new InvalidOperationException(L("main.vm.error.maximumMemoryBelowMinimum"));
        }

        if (_productServiceController is not null && server.IsServiceManaged)
        {
            EnsureProductServiceConnected();
            if (server.State != ServerState.Stopped)
            {
                throw new InvalidOperationException(
                    L("main.vm.error.serviceSettingsRequireStopped"));
            }

            var authoritative = await ExecuteProductServiceOperationAsync(
                token => _productServiceController.GetRegistrationAsync(server.Id, token),
                CancellationToken.None);
            var updated = authoritative with
            {
                Name = server.Name.Trim(),
                Port = server.Port,
                MinimumMemoryMb = server.MinimumMemoryMb,
                MaximumMemoryMb = server.MaximumMemoryMb,
                AutoRestart = server.AutoRestart,
            };
            var result = await ExecuteProductServiceOperationAsync(
                token => _productServiceController.UpdateRegistrationAsync(updated, token),
                CancellationToken.None);
            _dirtyProductServiceRegistrations.Remove(server.Id);
            _applyingProductServiceProjection = true;
            try
            {
                UpdateServiceProjectionMetadata(server.Model, result.Registration);
                server.NotifyServiceRegistrationChanged();
            }
            finally
            {
                _applyingProductServiceProjection = false;
            }
            ApplyProductServiceStatus(server, result.Status);
            SetStatus("main.vm.status.serviceSettingsSaved", server.Name);
            return;
        }

        ValidateReliabilitySettings(server);
        int? activePort = server.State is ServerState.Starting or ServerState.Running or ServerState.Stopping
            ? server.ActivePort
            : null;
        if (await PersistConfiguredPortAsync(server))
        {
            await ReloadPropertiesQuietlyAsync(server);
        }
        await SaveSettingsAsync();
        EnsureLiveSessionServices(server);
        if (activePort is { } runningPort && runningPort != server.Port)
        {
            SetStatus("main.vm.status.settingsSavedActivePort", server.Name, runningPort);
        }
        else
        {
            SetStatus("main.vm.status.settingsSaved", server.Name);
        }
    }

    private async Task<bool> PersistConfiguredPortAsync(ServerInstanceViewModel server)
    {
        if (server.Model.CoreType == CoreType.Velocity)
        {
            server.Model.ServerArguments ??= [];
            VelocityPortArgumentEditor.SetPort(server.Model.ServerArguments, server.Port);
            return false;
        }

        var propertiesPath = Path.Combine(server.DirectoryPath, "server.properties");
        var configured = await _serverPropertiesPortService.ReadServerPortAsync(propertiesPath);
        if (configured == server.Port)
        {
            return false;
        }

        await _serverPropertiesPortService.SetServerPortAsync(propertiesPath, server.Port);
        return true;
    }

    private void EnsureLiveSessionServices(ServerInstanceViewModel server)
    {
        if (!_processManager.TryGetSnapshot(server.Id, out var snapshot)
            || snapshot.State != ServerState.Running
            || snapshot.SessionId is not { } sessionId)
        {
            return;
        }

        var key = (server.Id, sessionId);
        if (server.EnableHangWatchdog)
        {
            _watchdogTasks.GetOrAdd(key, _ => RunWatchdogSessionAsync(server.Id, sessionId));
        }

        if (server.EnableAutomaticRecoveryPoints)
        {
            _recoveryPointTasks.GetOrAdd(key, _ => RunRecoveryPointSessionAsync(server.Id, sessionId));
        }
    }

    private static void ValidateReliabilitySettings(ServerInstanceViewModel server)
    {
        if (server.WatchdogCheckIntervalSeconds is < 10 or > 300)
        {
            throw new InvalidOperationException(L("main.vm.error.watchdogInterval"));
        }

        if (server.WatchdogProbeTimeoutSeconds is < 2 or > 30
            || server.WatchdogProbeTimeoutSeconds >= server.WatchdogCheckIntervalSeconds)
        {
            throw new InvalidOperationException(L("main.vm.error.watchdogTimeout"));
        }

        if (server.WatchdogFailureThreshold is < 2 or > 10)
        {
            throw new InvalidOperationException(L("main.vm.error.watchdogThreshold"));
        }

        if (server.WatchdogStartupGraceSeconds is < 30 or > 3600)
        {
            throw new InvalidOperationException(L("main.vm.error.watchdogGrace"));
        }

        if (server.RecoveryPointIntervalMinutes is < 10 or > 1440)
        {
            throw new InvalidOperationException(L("main.vm.error.recoveryInterval"));
        }

        if (server.RecoveryPointRetentionCount is < 1 or > 20)
        {
            throw new InvalidOperationException(L("main.vm.error.recoveryRetention"));
        }
    }

    private async Task ChooseBackgroundAsync()
    {
        var server = SelectedServer;
        if (server is null) return;
        var picker = new OpenFileDialog
        {
            Title = L("main.vm.filePicker.backgroundTitle"),
            Filter = L("main.vm.filePicker.backgroundFilter")
        };
        if (PrimaryDisplayWindowPlacement.ShowDialogOnProductDisplay(picker) != true) return;

        var backgroundRoot = Path.Combine(_paths.Themes, "backgrounds");
        Directory.CreateDirectory(backgroundRoot);
        ValidateThemeAsset(picker.FileName);
        var extension = Path.GetExtension(picker.FileName).ToLowerInvariant();
        var destination = Path.Combine(backgroundRoot, $"{server.Id:N}.{Guid.NewGuid():N}{extension}");
        File.Copy(picker.FileName, destination, overwrite: false);
        server.BackgroundImagePath = destination;
        OnPropertyChanged(nameof(BackgroundImagePath));
        PersistServiceAppearancePreference(server);
        await SaveSettingsAsync();
        await ReleaseThemeImageBindingsAsync();
        DeleteManagedThemeCopies(server.Id, "backgrounds", destination);
        SetStatus("main.vm.status.backgroundApplied", server.Name);
    }

    private async Task ChooseIconAsync()
    {
        var server = SelectedServer;
        if (server is null) return;
        var picker = new OpenFileDialog
        {
            Title = L("main.vm.filePicker.iconTitle"),
            Filter = L("main.vm.filePicker.iconFilter")
        };
        if (PrimaryDisplayWindowPlacement.ShowDialogOnProductDisplay(picker) != true) return;

        var iconRoot = Path.Combine(_paths.Themes, "icons");
        Directory.CreateDirectory(iconRoot);
        ValidateThemeAsset(picker.FileName);
        var extension = Path.GetExtension(picker.FileName).ToLowerInvariant();
        var destination = Path.Combine(iconRoot, $"{server.Id:N}.{Guid.NewGuid():N}{extension}");
        File.Copy(picker.FileName, destination, overwrite: false);
        server.IconImagePath = destination;
        PersistServiceAppearancePreference(server);
        await SaveSettingsAsync();
        await ReleaseThemeImageBindingsAsync();
        DeleteManagedThemeCopies(server.Id, "icons", destination);
        SetStatus("main.vm.status.iconApplied", server.Name);
    }

    private async Task ClearBackgroundAsync()
    {
        var server = SelectedServer;
        if (server is null) return;
        server.BackgroundImagePath = null;
        OnPropertyChanged(nameof(BackgroundImagePath));
        PersistServiceAppearancePreference(server);
        await SaveSettingsAsync();
        await ReleaseThemeImageBindingsAsync();
        DeleteManagedThemeCopies(server.Id, "backgrounds");
        SetStatus("main.vm.status.backgroundCleared", server.Name);
    }

    private async Task ClearIconAsync()
    {
        var server = SelectedServer;
        if (server is null) return;
        server.IconImagePath = null;
        PersistServiceAppearancePreference(server);
        await SaveSettingsAsync();
        await ReleaseThemeImageBindingsAsync();
        DeleteManagedThemeCopies(server.Id, "icons");
        SetStatus("main.vm.status.iconCleared", server.Name);
    }

    private static void ValidateThemeAsset(string path)
    {
        const long maximumThemeAssetBytes = 64L * 1024 * 1024;
        const long maximumDecodedPixels = 64_000_000;
        var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".ico"
        };
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException(L("main.vm.error.imageNotFound"), path);
        }

        if (!supportedExtensions.Contains(file.Extension))
        {
            throw new InvalidDataException(L("main.vm.error.imageFormatUnsupported"));
        }

        if (file.Length > maximumThemeAssetBytes)
        {
            throw new InvalidDataException(L("main.vm.error.imageTooLarge"));
        }

        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                throw new InvalidDataException(L("main.vm.error.imageNoFrames"));
            }

            var frame = decoder.Frames[0];
            var decodedPixels = checked((long)frame.PixelWidth * frame.PixelHeight);
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || decodedPixels > maximumDecodedPixels)
            {
                throw new InvalidDataException(L("main.vm.error.imageDimensionsInvalid"));
            }
        }
        catch (Exception exception) when (exception is NotSupportedException or FileFormatException or OverflowException)
        {
            throw new InvalidDataException(L("main.vm.error.imageInvalid"), exception);
        }
    }

    private static async Task ReleaseThemeImageBindingsAsync()
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            await dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void DeleteManagedThemeCopies(Guid instanceId, string category, string? exceptPath = null)
    {
        var permittedRoot = Path.Combine(_paths.Themes, category);
        if (!Directory.Exists(permittedRoot)) return;
        var canonicalException = string.IsNullOrWhiteSpace(exceptPath) ? null : Path.GetFullPath(exceptPath);
        foreach (var candidate in Directory.EnumerateFiles(permittedRoot, $"{instanceId:N}.*", SearchOption.TopDirectoryOnly))
        {
            if (canonicalException is not null && PathsEqual(candidate, canonicalException)) continue;
            if (!SafePath.IsWithinRoot(permittedRoot, candidate)) continue;
            try
            {
                File.Delete(candidate);
            }
            catch (IOException)
            {
                // A WPF decoder may briefly hold the previous image. The active path is already
                // persisted, so a leftover copy cannot override the new selection.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the active setting valid even when cleanup of an obsolete copy is blocked.
            }
        }
    }

    private async Task ReloadPropertiesAsync()
    {
        if (SelectedServer is null) return;
        await ReloadPropertiesQuietlyAsync(SelectedServer);
        SetStatus("main.vm.status.propertiesReloaded");
    }

    private async Task ReloadPropertiesQuietlyAsync(ServerInstanceViewModel server)
    {
        if (server.IsServiceManaged)
        {
            await ReloadServiceServerPropertiesQuietlyAsync(server);
            return;
        }

        var requestVersion = Interlocked.Increment(ref _serverPropertiesReloadVersion);
        if (!server.CanAccessLocalFiles)
        {
            return;
        }

        var path = Path.Combine(server.DirectoryPath, "server.properties");
        var document = await _serverPropertiesPortService.ReadDocumentAsync(path);
        if (requestVersion != Volatile.Read(ref _serverPropertiesReloadVersion) ||
            !ReferenceEquals(server, SelectedServer) ||
            !Servers.Contains(server))
        {
            return;
        }
        if (document is null)
        {
            _serverPropertiesFormats.TryRemove(server.Id, out _);
            server.ServerPropertiesText = L("main.vm.properties.notGenerated");
            return;
        }

        _serverPropertiesFormats[server.Id] = document.FormatToken;
        server.ServerPropertiesText = document.Text;
    }

    private async Task ReloadServiceServerPropertiesQuietlyAsync(ServerInstanceViewModel server)
    {
        var generation = BeginServiceServerPropertiesReload(server.Id);
        try
        {
            var controller = _productServiceController
                ?? throw new InvalidOperationException(L("main.vm.service.notReady"));
            EnsureProductServiceConnected();
            if (!SupportsProductServicePropertiesEditor)
            {
                throw new InvalidOperationException(L("main.vm.service.incompatible"));
            }

            var serviceDocument = await ExecuteProductServiceOperationAsync(
                token => controller.ReadServerPropertiesAsync(server.Id, token),
                _sessionServicesCancellation.Token);
            if (!IsCurrentServiceServerPropertiesReload(server.Id, generation) ||
                !ReferenceEquals(server, SelectedServer) ||
                !Servers.Contains(server))
            {
                return;
            }

            _serviceServerPropertiesRevisions[server.Id] = serviceDocument.RevisionSha256;
            server.ServerPropertiesText = serviceDocument.Exists
                ? serviceDocument.Text
                : L("main.vm.properties.notGenerated");
            NotifySelectedServerPropertiesStateChanged(server.Id);
        }
        finally
        {
            EndServiceServerPropertiesReload(server.Id, generation);
        }
    }

    private long BeginServiceServerPropertiesReload(Guid serverId)
    {
        long generation;
        lock (_serviceServerPropertiesStateSync)
        {
            generation = _serviceServerPropertiesReloadGenerations.GetValueOrDefault(serverId) + 1;
            _serviceServerPropertiesReloadGenerations[serverId] = generation;
            _serviceServerPropertiesReloadsInFlight[serverId] = generation;
        }

        _serviceServerPropertiesRevisions.TryRemove(serverId, out _);
        NotifySelectedServerPropertiesStateChanged(serverId);
        return generation;
    }

    private bool IsCurrentServiceServerPropertiesReload(Guid serverId, long generation)
    {
        lock (_serviceServerPropertiesStateSync)
        {
            return _serviceServerPropertiesReloadGenerations.GetValueOrDefault(serverId) == generation;
        }
    }

    private void EndServiceServerPropertiesReload(Guid serverId, long generation)
    {
        var changed = false;
        lock (_serviceServerPropertiesStateSync)
        {
            if (_serviceServerPropertiesReloadsInFlight.GetValueOrDefault(serverId) == generation)
            {
                _serviceServerPropertiesReloadsInFlight.Remove(serverId);
                changed = true;
            }
        }

        if (changed)
        {
            NotifySelectedServerPropertiesStateChanged(serverId);
        }
    }

    private void BeginServiceServerPropertiesSave(Guid serverId)
    {
        lock (_serviceServerPropertiesStateSync)
        {
            if (_serviceServerPropertiesReloadsInFlight.ContainsKey(serverId) ||
                !_serviceServerPropertiesSavesInFlight.Add(serverId))
            {
                throw new InvalidOperationException(L("main.vm.error.propertiesOperationRunning"));
            }
        }

        NotifySelectedServerPropertiesStateChanged(serverId);
    }

    private void EndServiceServerPropertiesSave(Guid serverId)
    {
        lock (_serviceServerPropertiesStateSync)
        {
            _serviceServerPropertiesSavesInFlight.Remove(serverId);
        }

        NotifySelectedServerPropertiesStateChanged(serverId);
    }

    private bool IsServiceServerPropertiesOperationRunning(Guid serverId)
    {
        lock (_serviceServerPropertiesStateSync)
        {
            return _serviceServerPropertiesReloadsInFlight.ContainsKey(serverId) ||
                   _serviceServerPropertiesSavesInFlight.Contains(serverId);
        }
    }

    private void RemoveServiceServerPropertiesState(Guid serverId)
    {
        lock (_serviceServerPropertiesStateSync)
        {
            _serviceServerPropertiesReloadGenerations.Remove(serverId);
            _serviceServerPropertiesReloadsInFlight.Remove(serverId);
            _serviceServerPropertiesSavesInFlight.Remove(serverId);
        }
    }

    private void NotifySelectedServerPropertiesStateChanged(Guid serverId)
    {
        if (SelectedServer?.Id != serverId)
        {
            return;
        }

        OnPropertyChanged(nameof(IsSelectedServerPropertiesOperationRunning));
        OnPropertyChanged(nameof(CanReloadSelectedServerProperties));
        OnPropertyChanged(nameof(CanEditSelectedServerProperties));
        OnPropertyChanged(nameof(CanSaveSelectedServerProperties));
        ReloadPropertiesCommand.NotifyCanExecuteChanged();
        SavePropertiesCommand.NotifyCanExecuteChanged();
    }

    private void QueuePlayerRegistryLoadIfNeeded(ServerInstanceViewModel server)
    {
        if (_loadedPlayerRegistries.ContainsKey(server.Id)) return;
        _lastPlayerRegistryReload = LoadSelectedPlayerRegistryAsync(server);
    }

    private async Task LoadSelectedPlayerRegistryAsync(ServerInstanceViewModel server)
    {
        try
        {
            _ = await ReloadPlayersQuietlyAsync(server);
        }
        catch (Exception exception)
        {
            server.AppendConsole(SystemConsoleLineFactory.Create(
                server.Id,
                L("main.vm.console.playerDataReadFailed", exception.Message),
                ConsoleLineSeverity.Warning));
        }
    }

    private async Task<bool> ReloadPlayersQuietlyAsync(ServerInstanceViewModel server)
    {
        var requestVersion = Interlocked.Increment(ref _playerRegistryReloadVersion);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _sessionServicesCancellation.Token);
        lock (_playerRegistryReloadSync)
        {
            _playerRegistryReloadCancellation?.Cancel();
            _playerRegistryReloadCancellation = cancellation;
        }

        try
        {
            if (server.IsServiceManaged)
            {
                var controller = _productServiceController
                    ?? throw new InvalidOperationException(L("main.vm.service.notReady"));
                EnsureProductServiceConnected();
                var servicePlayers = await controller.ListPlayersAsync(server.Id, cancellation.Token);
                if (cancellation.IsCancellationRequested
                    || requestVersion != Volatile.Read(ref _playerRegistryReloadVersion)
                    || !ReferenceEquals(server, SelectedServer))
                {
                    return false;
                }

                server.UpdateOnlinePlayers(servicePlayers.Players.Select(static player => player.Name));
                _loadedPlayerRegistries[server.Id] = 0;
                return true;
            }

            if (!server.CanAccessLocalFiles)
            {
                return false;
            }

            var result = await PlayerRegistryReader.ReadAsync(
                server.DirectoryPath,
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || requestVersion != Volatile.Read(ref _playerRegistryReloadVersion)
                || !ReferenceEquals(server, SelectedServer))
            {
                return false;
            }

            server.ReplacePlayers(result.Players);
            foreach (var warning in result.Warnings)
            {
                server.AppendConsole(SystemConsoleLineFactory.Create(
                    server.Id,
                    L("main.vm.console.playerFileReadFailed", warning.FileName, warning.Message),
                    ConsoleLineSeverity.Warning));
            }

            _loadedPlayerRegistries[server.Id] = 0;
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            lock (_playerRegistryReloadSync)
            {
                if (ReferenceEquals(_playerRegistryReloadCancellation, cancellation))
                {
                    _playerRegistryReloadCancellation = null;
                }

                cancellation.Dispose();
            }
        }
    }

    private void CancelPlayerRegistryReload()
    {
        Interlocked.Increment(ref _playerRegistryReloadVersion);
        lock (_playerRegistryReloadSync)
        {
            _playerRegistryReloadCancellation?.Cancel();
        }
    }

    private async Task SavePropertiesAsync()
    {
        var server = SelectedServer;
        if (server is null) return;
        if (server.State == ServerState.Starting)
        {
            throw new InvalidOperationException(L("main.vm.error.savePropertiesWhileStarting"));
        }
        if (server.IsServiceManaged)
        {
            if (server.State is not (ServerState.Stopped or ServerState.Crashed or ServerState.Faulted))
            {
                throw new InvalidOperationException(L("main.vm.error.savePropertiesWhileActive"));
            }

            var controller = _productServiceController
                ?? throw new InvalidOperationException(L("main.vm.service.notReady"));
            EnsureProductServiceConnected();
            if (!SupportsProductServicePropertiesEditor)
            {
                throw new InvalidOperationException(L("main.vm.service.incompatible"));
            }

            if (!_serviceServerPropertiesRevisions.TryGetValue(server.Id, out var revision))
            {
                throw new InvalidOperationException(L("main.vm.error.propertiesNotLoaded"));
            }

            BeginServiceServerPropertiesSave(server.Id);
            try
            {
                var document = await ExecuteProductServiceOperationAsync(
                    token => controller.UpdateServerPropertiesAsync(
                        server.Id,
                        new ProductServerPropertiesUpdateRequest(
                            server.ServerPropertiesText,
                            revision),
                        token),
                    CancellationToken.None);
                _serviceServerPropertiesRevisions[server.Id] = document.RevisionSha256;
                if (ReferenceEquals(server, SelectedServer) && Servers.Contains(server))
                {
                    server.ServerPropertiesText = document.Text;
                    var registration = await ExecuteProductServiceOperationAsync(
                        token => controller.GetRegistrationAsync(server.Id, token),
                        CancellationToken.None);
                    _dirtyProductServiceRegistrations.Remove(server.Id);
                    _applyingProductServiceProjection = true;
                    try
                    {
                        UpdateServiceProjectionMetadata(server.Model, registration);
                        server.NotifyServiceRegistrationChanged();
                    }
                    finally
                    {
                        _applyingProductServiceProjection = false;
                    }
                }

                if (server.ActivePort is { } serviceActivePort && serviceActivePort != server.Port)
                {
                    SetStatus("main.vm.status.propertiesSavedActivePort", serviceActivePort);
                }
                else
                {
                    SetStatus("main.vm.status.propertiesSaved");
                }
            }
            finally
            {
                EndServiceServerPropertiesSave(server.Id);
            }
            return;
        }

        if (!server.CanAccessLocalFiles)
        {
            throw new InvalidOperationException(L("main.vm.service.incompatible"));
        }
        var path = Path.Combine(server.DirectoryPath, "server.properties");
        _serverPropertiesFormats.TryGetValue(server.Id, out var formatToken);
        var saveResult = await _serverPropertiesPortService.SaveDocumentAsync(
            path,
            server.ServerPropertiesText,
            formatToken);
        _serverPropertiesFormats[server.Id] = saveResult.FormatToken;

        int? configuredPort = null;
        if (server.Model.CoreType != CoreType.Velocity
            && ServerPropertiesPortEditor.TryReadServerPort(
                server.ServerPropertiesText,
                out var parsedPort))
        {
            configuredPort = parsedPort;
            server.Port = parsedPort;
            await SaveSettingsAsync();
        }

        if (configuredPort is { } savedPort
            && server.ActivePort is { } activePort
            && activePort != savedPort)
        {
            SetStatus("main.vm.status.propertiesSavedActivePort", activePort);
        }
        else
        {
            SetStatus("main.vm.status.propertiesSaved");
        }
    }

    private async Task DownloadSelectedJavaAsync()
    {
        var targetServer = SelectedServer;
        var major = SelectedJavaMajor;
        var installedJavaPath = await DownloadJavaForMajorAsync(major);
        if (targetServer is not null
            && Servers.Contains(targetServer)
            && targetServer.Model.JavaMajorVersion == major)
        {
            targetServer.JavaExecutablePath = installedJavaPath;
            await SaveSettingsAsync();
        }
    }

    private async Task<string> DownloadJavaForMajorAsync(int major)
    {
        var progress = new Progress<double>(value => SetStatus("main.vm.status.javaDownloading", major, value));
        var installed = await _javaProvider.InstallAsync(major, _paths.Runtimes, progress);
        var item = new JavaRuntimeItemViewModel(installed);
        if (InstalledJavaRuntimes.All(runtime => !runtime.ExecutablePath.Equals(item.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
        {
            InstalledJavaRuntimes.Add(item);
        }

        SetStatus("main.vm.status.javaInstalled", major);
        return installed.JavaExecutablePath;
    }

    private Task RefreshJavaAsync()
    {
        InstalledJavaRuntimes.Clear();
        if (IsProductServiceRuntime)
        {
            if (SelectedWorkspaceTabKey == JavaRuntimeWorkspaceTabKey
                && SelectedServer is { IsServiceManaged: true } server
                && SupportsProductServiceFileAdministration)
            {
                QueueServerAdministrationSnapshot(server);
            }

            return Task.CompletedTask;
        }

        if (!Directory.Exists(_paths.Runtimes)) return Task.CompletedTask;
        foreach (var runtime in EnumerateManagedJavaRuntimes())
        {
            InstalledJavaRuntimes.Add(new JavaRuntimeItemViewModel(runtime));
        }

        return Task.CompletedTask;
    }

    private async Task CreateSelectedBackupAsync()
    {
        var server = SelectedServer;
        if (server is null) return;
        if (_productServiceController is not null && server.IsServiceManaged)
        {
            EnsureProductServiceConnected();
            if (server.State != ServerState.Stopped)
            {
                throw new InvalidOperationException(
                    L("main.vm.error.serviceBackupRequiresStopped"));
            }

            SetStatus("main.vm.status.serviceBackupCreating", server.Name);
            var result = await ExecuteProductServiceOperationAsync(
                token => _productServiceController.CreateBackupAsync(server.Id, token),
                CancellationToken.None);
            if (ReferenceEquals(server, SelectedServer) && Servers.Contains(server))
            {
                await RefreshBackupsForServerAsync(server, CancellationToken.None);
            }
            SetStatus("main.vm.status.backupCompleted", result.Backup.FileName);
            return;
        }
        await CreateBackupAsync(server, CancellationToken.None);
    }

    private async Task RefreshSelectedBackupsAsync()
    {
        var server = SelectedServer;
        if (server is null) return;
        if (_productServiceController is null || !server.IsServiceManaged)
        {
            server.RefreshBackups();
            return;
        }

        EnsureProductServiceConnected();
        await RefreshBackupsForServerAsync(server, CancellationToken.None);
    }

    private async Task RefreshBackupsForServerAsync(
        ServerInstanceViewModel server,
        CancellationToken cancellationToken)
    {
        var backups = await ExecuteProductServiceOperationAsync(
            token => _productServiceController!.ListBackupsAsync(server.Id, token),
            cancellationToken);
        if (Servers.Contains(server))
        {
            server.ReplaceBackups(backups);
        }
    }

    private async Task RestoreSelectedBackupAsync(BackupItemViewModel? backup)
    {
        var server = SelectedServer;
        if (server is null || backup is null) return;
        if (_productServiceController is null || !server.IsServiceManaged ||
            string.IsNullOrWhiteSpace(backup.BackupId))
        {
            throw new InvalidOperationException(
                L("main.vm.error.restoreRequiresServiceBackup"));
        }
        EnsureProductServiceConnected();
        if (server.State != ServerState.Stopped)
        {
            throw new InvalidOperationException(L("main.vm.error.restoreRequiresStopped"));
        }

        var answer = DarkMessageBox.Show(
            Application.Current.MainWindow,
            L("main.vm.confirm.restoreServiceBackup", backup.FileName, server.Name),
            L("main.vm.confirm.restoreServiceBackupTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        // The modal prompt is not an authorization lease. Re-check both the exact row and state;
        // the Service repeats this check while holding its runtime mutation gate.
        if (!ReferenceEquals(server, SelectedServer) || !Servers.Contains(server) ||
            server.State != ServerState.Stopped)
        {
            throw new InvalidOperationException(L("main.vm.error.restoreSelectionChanged"));
        }

        SetStatus("main.vm.status.serviceBackupRestoring", backup.FileName);
        await ExecuteProductServiceOperationAsync(
            token => _productServiceController.RestoreBackupAsync(
                server.Id,
                backup.BackupId,
                token),
            CancellationToken.None);
        await RefreshBackupsForServerAsync(server, CancellationToken.None);
        SetStatus("main.vm.status.serviceBackupRestored", backup.FileName);
    }

    internal Task CreateBackupForRemoteAsync(Guid instanceId, CancellationToken cancellationToken)
        => CreateBackupAsync(
            FindServerForRemote(instanceId),
            cancellationToken,
            failIfAnotherBackupIsActive: true);

    private async Task CreateBackupAsync(
        ServerInstanceViewModel server,
        CancellationToken cancellationToken,
        bool failIfAnotherBackupIsActive = false)
    {
        if (!server.CanAccessLocalFiles)
        {
            throw new InvalidOperationException(
                LocalizationService.Current.Get("service.readOnly.backupOperation"));
        }

        var gate = _backupGates.GetOrAdd(server.Id, static _ => new SemaphoreSlim(1, 1));
        var entered = failIfAnotherBackupIsActive
            ? await gate.WaitAsync(TimeSpan.Zero, cancellationToken)
            : await gate.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken);
        if (!entered)
        {
            throw new InvalidOperationException(L("main.vm.error.backupAlreadyRunning"));
        }
        Guid? runningSessionId = null;
        ServerDirectoryLease? stoppedServerLease = null;
        var savesPaused = false;
        Exception? operationError = null;
        try
        {
            if (_lifecycleTransitions.ContainsKey(server.Id)
                || _pendingLaunchPorts.ContainsKey(server.Id)
                || server.State is ServerState.Starting or ServerState.Stopping)
            {
                throw new InvalidOperationException(L("main.vm.error.backupDuringTransition"));
            }
            if (server.AutoRestart
                && _crashPlans.TryGetValue(server.Id, out var pendingRestart)
                && pendingRestart.Decision.ShouldRestart)
            {
                throw new InvalidOperationException(
                    L("main.vm.error.backupDuringAutoRestart"));
            }

            if (server.State == ServerState.Running
                && _processManager.TryGetSnapshot(server.Id, out var snapshot)
                && snapshot.State == ServerState.Running
                && snapshot.SessionId is { } sessionId)
            {
                runningSessionId = sessionId;
                savesPaused = true;
                var flushConfirmed = await FlushAndPauseServerSavesAsync(
                    server.Id,
                    sessionId,
                    cancellationToken);
                if (!flushConfirmed)
                {
                    throw new TimeoutException(
                        L("main.vm.error.backupFlushTimeout"));
                }
            }
            else
            {
                // A stopped Server has no ProcessManager session holding the cross-process lock.
                // Take a maintenance lease so another GUI cannot launch the same world halfway
                // through this ZIP operation.
                stoppedServerLease = ServerDirectoryLease.Acquire(server.DirectoryPath);
            }

            var progress = new Progress<BackupProgress>(value =>
            {
                if (value.Stage == BackupStage.Scanning)
                {
                    SetStatus("main.vm.status.backupScanning");
                }
                else
                {
                    SetStatus("main.vm.status.backupProgress", value.CompletedFiles, value.TotalFiles);
                }
            });
            var result = await _backupService.CreateBackupAsync(
                server.Model,
                CreateBackupOptions(server.Model),
                progress,
                cancellationToken);
            if (runningSessionId is { } expectedSessionId
                && !IsCurrentRunningSession(server.Id, expectedSessionId))
            {
                DeleteInvalidatedBackup(result.ArchivePath);
                throw new InvalidOperationException(
                    L("main.vm.error.backupSessionChanged"));
            }

            server.RefreshBackups();
            SetStatus("main.vm.status.backupCompleted", Path.GetFileName(result.ArchivePath));
        }
        catch (Exception error)
        {
            operationError = error;
            throw;
        }
        finally
        {
            try
            {
                if (savesPaused
                    && runningSessionId is { } sessionId
                    && IsCurrentRunningSession(server.Id, sessionId))
                {
                    await SendCommandOwnedAsync(server.Id, "save-on", CancellationToken.None);
                }
            }
            catch (Exception saveOnError) when (saveOnError is not OutOfMemoryException)
            {
                PostSystemMessage(
                    server.Id,
                    L("main.vm.console.backupSaveOnFailed", saveOnError.Message),
                    ConsoleLineSeverity.Warning);
                if (operationError is null)
                {
                    throw;
                }
            }
            finally
            {
                try
                {
                    if (stoppedServerLease is not null)
                    {
                        await stoppedServerLease.DisposeAsync();
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
        }
    }

    private void OpenSelectedBackupFolder()
    {
        if (SelectedServer is null) return;
        if (SelectedServer.IsServiceManaged)
        {
            SetStatus("main.vm.status.serviceBackupPathHidden");
            return;
        }
        var path = Path.Combine(SelectedServer.DirectoryPath, "backups");
        Directory.CreateDirectory(path);
        OpenFolder(path);
    }

    private void OpenSelectedCrashReportsFolder()
    {
        if (SelectedServer is null) return;
        var path = SafePath.CombineUnderRoot(
            _paths.CrashReports,
            $"{SafePath.SanitizeFileName(SelectedServer.Name, maxLength: 48)}-{SelectedServer.Id:N}");
        Directory.CreateDirectory(path);
        OpenFolder(path);
    }

    private void OpenSelectedRecoveryPointsFolder()
    {
        if (!CanManageLocalRecoveryPoints || SelectedServer is null)
        {
            SetStatus("main.vm.status.serviceRecoveryPointsUnavailable");
            return;
        }
        var path = SafePath.CombineUnderRoot(_paths.RecoveryPoints, SelectedServer.Id.ToString("N"));
        Directory.CreateDirectory(path);
        OpenFolder(path);
    }

    private async Task RestoreRecoveryPointAsync()
    {
        var sourceServer = SelectedServer;
        if (sourceServer is null) return;
        if (!CanManageLocalRecoveryPoints)
        {
            throw new InvalidOperationException(
                L("main.vm.error.serviceRecoveryPointRestoreUnavailable"));
        }
        var recoveryRoot = SafePath.CombineUnderRoot(
            _paths.RecoveryPoints,
            sourceServer.Id.ToString("N"));
        Directory.CreateDirectory(recoveryRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(_paths.Root, recoveryRoot);
        var picker = new OpenFileDialog
        {
            Title = L("main.vm.filePicker.recoveryPointTitle"),
            InitialDirectory = recoveryRoot,
            Filter = L("main.vm.filePicker.recoveryPointFilter"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (PrimaryDisplayWindowPlacement.ShowDialogOnProductDisplay(picker) != true) return;
        var archivePath = SafePath.EnsureWithinRoot(recoveryRoot, picker.FileName, allowRoot: false);
        SafePath.EnsureNoReparsePointsUnderRoot(recoveryRoot, archivePath);
        var answer = DarkMessageBox.Show(
            Application.Current.MainWindow,
            L("main.vm.confirm.restoreRecoveryPoint", archivePath),
            L("main.vm.confirm.restoreRecoveryPointTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        var recoveredName = L(
            "main.vm.recovery.copyName",
            sourceServer.Name,
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmm", LocalizationService.Current.Culture));
        var destination = SafePath.CreateUniqueDirectoryPath(_paths.Servers, recoveredName);
        var progress = new Progress<BackupRestoreProgress>(value =>
        {
            if (value.Stage == BackupRestoreStage.Extracting)
            {
                SetStatus(
                    "main.vm.status.recoveryExtracting",
                    value.CompletedFiles,
                    value.TotalFiles);
                return;
            }

            SetStatus(value.Stage switch
            {
                BackupRestoreStage.Validating => "main.vm.status.recoveryValidating",
                BackupRestoreStage.Committing => "main.vm.status.recoveryCommitting",
                _ => "main.vm.status.recoveryCompleted"
            });
        });
        await _backupRestoreService.RestoreAsync(
            archivePath,
            destination,
            new BackupRestoreOptions { TrustedDestinationRoot = _paths.Servers },
            progress: progress);

        ServerInstance recovered;
        try
        {
            recovered = await CreateRecoveredInstanceAsync(sourceServer.Model, destination, recoveredName);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new InvalidDataException(
                L("main.vm.error.recoveryLaunchDetectionFailed", destination, error.Message),
                error);
        }

        AddInstance(recovered);
        await SaveSettingsAsync();
        SelectedServer = Servers.First(server => server.Id == recovered.Id);
        SelectedServer.AppendConsole(SystemConsoleLineFactory.Create(
            recovered.Id,
            L("main.vm.console.recoveryCopyCreated", Path.GetFileName(archivePath)),
            ConsoleLineSeverity.Information));
        SetStatus("main.vm.status.recoveryCopyCreated", recovered.Name);
    }

    private async Task<ServerInstance> CreateRecoveredInstanceAsync(
        ServerInstance source,
        string destination,
        string recoveredName)
    {
        if (source.LaunchKind == ServerLaunchKind.JavaArgumentFiles)
        {
            var detection = await _serverPackDetector.DetectAsync(destination);
            if (!detection.IsRecognized || !detection.IsRunnable)
            {
                throw new InvalidDataException(detection.Error ?? L("main.vm.error.recoveryPackInvalid"));
            }

            return new ServerInstance
            {
                Name = recoveredName,
                DirectoryPath = destination,
                ServerJarPath = string.Empty,
                LaunchKind = ServerLaunchKind.JavaArgumentFiles,
                JavaArgumentFilePaths = [.. detection.JavaArgumentFilePaths],
                SourceLaunchScriptPath = detection.SourceLaunchScriptPath,
                CoreType = detection.CoreType,
                MinecraftVersion = detection.MinecraftVersion,
                JavaMajorVersion = detection.JavaMajorVersion,
                JavaExecutablePath = detection.JavaExecutablePath,
                MinimumMemoryMb = detection.MinimumMemoryMb ?? source.MinimumMemoryMb,
                MaximumMemoryMb = detection.MaximumMemoryMb ?? source.MaximumMemoryMb,
                MemoryAllocationMode = source.MemoryAllocationMode,
                ServerArguments = [.. detection.ServerArguments]
            };
        }

        var sourceRoot = Path.GetFullPath(source.DirectoryPath);
        var originalJar = Path.GetFullPath(source.ServerJarPath);
        if (!SafePath.IsWithinRoot(sourceRoot, originalJar))
        {
            throw new InvalidDataException(L("main.vm.error.recoveryJarOutsideSource"));
        }

        var recoveredJar = SafePath.CombineUnderRoot(
            destination,
            Path.GetRelativePath(sourceRoot, originalJar));
        var detectionResult = await _coreDetector.DetectAsync(recoveredJar);
        if (!detectionResult.IsValidJar)
        {
            throw new InvalidDataException(detectionResult.Error ?? L("main.vm.error.recoveryJarInvalid"));
        }

        var javaPath = source.JavaExecutablePath;
        if (!string.IsNullOrWhiteSpace(javaPath)
            && SafePath.IsWithinRoot(sourceRoot, Path.GetFullPath(javaPath)))
        {
            javaPath = SafePath.CombineUnderRoot(
                destination,
                Path.GetRelativePath(sourceRoot, Path.GetFullPath(javaPath)));
        }

        return new ServerInstance
        {
            Name = recoveredName,
            DirectoryPath = destination,
            ServerJarPath = recoveredJar,
            LaunchKind = ServerLaunchKind.ExecutableJar,
            CoreType = detectionResult.IsRecognized ? detectionResult.CoreType : source.CoreType,
            MinecraftVersion = detectionResult.MinecraftVersion ?? source.MinecraftVersion,
            JavaMajorVersion = source.JavaMajorVersion,
            JavaExecutablePath = javaPath,
            MinimumMemoryMb = source.MinimumMemoryMb,
            MaximumMemoryMb = source.MaximumMemoryMb,
            MemoryAllocationMode = source.MemoryAllocationMode,
            JvmArguments = [.. source.JvmArguments],
            ServerArguments = [.. source.ServerArguments]
        };
    }

    private async Task RunTrackedSelectedModpackUpdateAsync()
    {
        var server = SelectedServer
            ?? throw new InvalidOperationException(L("main.vm.error.selectServerForUpdate"));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_modpackUpdateTasks.TryAdd(server.Id, completion.Task))
        {
            throw new InvalidOperationException(L("main.vm.error.modpackUpdateAlreadyRunning", server.Name));
        }

        try
        {
            await SelectAndUpdateModpackAsync(server, _applicationShutdownCancellation.Token);
        }
        finally
        {
            completion.TrySetResult();
            _modpackUpdateTasks.TryRemove(
                new KeyValuePair<Guid, Task>(server.Id, completion.Task));
        }
    }

    internal async Task SelectAndUpdateModpackAsync(
        ServerInstanceViewModel server,
        CancellationToken cancellationToken)
    {
        if (!Servers.Contains(server))
        {
            throw new InvalidOperationException(L("main.vm.error.selectedServerNoLongerManaged"));
        }

        if (_productServiceController is not null)
        {
            EnsureProductServiceConnected();
            if (!server.IsServiceManaged)
            {
                throw new InvalidOperationException(
                    L("main.vm.error.serviceUpdateRequiresMigratedServer"));
            }
        }
        else if (!server.CanAccessLocalFiles)
        {
            throw new InvalidOperationException(
                L("main.vm.error.localModpackFilesUnavailable"));
        }

        var provider = GetOnlineModpackProvider(server.Model);
        using var transientApiKey = provider == OnlineModpackProvider.CurseForge
            ? _curseForgeUpdateCredentialPrompt.RequestCredential(GetDialogOwner())
            : null;
        if (provider == OnlineModpackProvider.CurseForge && transientApiKey is null)
        {
            SetStatus("main.vm.status.modpackUpdateCancelled");
            return;
        }

        if (transientApiKey is not null && !transientApiKey.IsReadOnly())
        {
            transientApiKey.MakeReadOnly();
        }

        var projectId = server.Model.ModpackProjectId?.Trim()
            ?? throw new InvalidOperationException(L("main.vm.error.modpackProjectIdMissing"));
        var currentVersionId = server.Model.ModpackVersionId?.Trim()
            ?? throw new InvalidOperationException(L("main.vm.error.modpackVersionIdMissing"));
        var project = new OnlineModpackSearchResult(
            provider,
            projectId,
            server.Name,
            L("main.vm.modpack.verifiedSource"),
            L("main.vm.modpack.verifiedByMcsv"));

        SetStatus("main.vm.status.modpackVersionsLoading", server.Name);
        var versions = await _onlineModpackWorkflow.GetVersionsAsync(
            project,
            transientApiKey,
            cancellationToken);
        var currentMatches = versions
            .Where(version => version.Provider == provider
                              && version.ProjectId.Equals(projectId, StringComparison.Ordinal)
                              && version.VersionId.Equals(currentVersionId, StringComparison.Ordinal))
            .ToArray();
        if (currentMatches.Length != 1)
        {
            throw new InvalidOperationException(
                L("main.vm.error.modpackCurrentVersionAmbiguous"));
        }

        var current = currentMatches[0];
        var available = versions
            .Where(version => version.Provider == provider
                              && version.ProjectId.Equals(projectId, StringComparison.Ordinal)
                              && !version.VersionId.Equals(currentVersionId, StringComparison.Ordinal)
                              && version.HasOfficialServerPack
                              && version.ReleasedAtUtc > current.ReleasedAtUtc)
            .OrderByDescending(version => version.ReleasedAtUtc)
            .ThenBy(version => version.VersionName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (available.Length == 0)
        {
            SetStatus("main.vm.status.modpackNoUpdate", server.Name);
            DarkMessageBox.Show(
                Application.Current?.MainWindow,
                L("main.vm.modpack.noNewerServerPack"),
                L("main.vm.modpack.upToDateTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var updateSelection = _modpackUpdateSelectionService.SelectUpdate(
            server.Model,
            available,
            GetDialogOwner());
        if (updateSelection is null)
        {
            SetStatus("main.vm.status.modpackUpdateCancelled");
            return;
        }

        await ApplyModpackUpdateAsync(
            server,
            project,
            updateSelection.Version,
            cancellationToken,
            transientApiKey,
            updateSelection.MinecraftEulaAccepted);
    }

    private static Window? GetDialogOwner()
    {
        var application = Application.Current;
        return application is not null && application.Dispatcher.CheckAccess()
            ? application.MainWindow
            : null;
    }

    internal async Task ApplyModpackUpdateAsync(
        ServerInstanceViewModel server,
        OnlineModpackSearchResult project,
        OnlineModpackVersion targetVersion,
        CancellationToken cancellationToken,
        SecureString? transientApiKey = null,
        bool minecraftEulaAccepted = false)
    {
        if (!_modpackUpdates.TryAdd(server.Id, 0))
        {
            throw new InvalidOperationException(L("main.vm.error.modpackUpdateAlreadyRunning", server.Name));
        }

        StartSelectedCommand.NotifyCanExecuteChanged();
        UpdateSelectedModpackCommand.NotifyCanExecuteChanged();
        if (_productServiceController is not null)
        {
            try
            {
                await ApplyProductServiceModpackUpdateAsync(
                    server,
                    project,
                    targetVersion,
                    cancellationToken,
                    transientApiKey,
                    minecraftEulaAccepted);
            }
            finally
            {
                _modpackUpdates.TryRemove(server.Id, out _);
                StartSelectedCommand.NotifyCanExecuteChanged();
                UpdateSelectedModpackCommand.NotifyCanExecuteChanged();
            }

            return;
        }

        SemaphoreSlim? lifecycleGate = null;
        SemaphoreSlim? backupGate = null;
        var backupGateEntered = false;
        var serverRegistryGateEntered = false;
        ServerInstance? candidate = null;
        string? candidateOwnedRoot = null;
        SafePathObjectIdentityLease? candidateOwnedRootIdentity = null;
        ModpackUpdateTransactionResult? transaction = null;
        var candidateCleanupAllowed = true;
        BackupResult? backupResult = null;
        try
        {
            lifecycleGate = await EnterLifecycleTransitionAsync(server.Id, cancellationToken);
            backupGate = _backupGates.GetOrAdd(server.Id, static _ => new SemaphoreSlim(1, 1));
            await backupGate.WaitAsync(cancellationToken);
            backupGateEntered = true;

            ValidateModpackUpdateStillAllowed(server, project, targetVersion);
            var candidateName = SafePath.SanitizeFileName(
                L("main.vm.modpack.candidateName", server.Name, targetVersion.VersionName),
                fallback: "modpack-update-candidate",
                maxLength: 80);
            var installProgress = new Progress<OnlineModpackInstallProgress>(value =>
            {
                var percentage = value.Percentage is { } number ? $" {number:0}%" : string.Empty;
                SetStatus("main.vm.status.modpackUpdateProgress", value.Message, percentage);
            });
            candidate = await _onlineModpackWorkflow.InstallAsync(
                new OnlineModpackInstallRequest(
                    project,
                    targetVersion,
                    candidateName,
                    minecraftEulaAccepted),
                transientApiKey,
                installProgress,
                cancellationToken);
            candidateOwnedRoot = ResolveOwnedCandidateInstallRoot(candidate.DirectoryPath);

            // The online workflow normally promotes a fresh unique direct child of `servers`, but
            // that is not by itself an ownership proof. A manual/background import can claim the
            // just-created directory during the handoff, and an injected workflow could return an
            // already-managed directory. Serialize the final ownership check through transaction
            // completion and cleanup so no existing Server can have its files moved or deleted.
            // Once InstallAsync has returned, cancellation must still wait for this short critical
            // section: otherwise a valid unregistered candidate would be stranded on shutdown.
            await _serverRegistryGate.WaitAsync(CancellationToken.None);
            serverRegistryGateEntered = true;
            // Reuse the permanent-deletion target validator as a read-only ownership boundary.
            // It rejects equality as well as either ancestor direction, canonical aliases,
            // reparse roots and protected locations. A nested managed Server must be just as
            // protected as one whose root exactly equals the candidate.
            try
            {
                candidateOwnedRoot = _serverDirectoryDeletionService.ValidateDeletionTarget(
                    candidateOwnedRoot,
                    Servers.Select(managed => managed.DirectoryPath));
            }
            catch (UnauthorizedAccessException ownershipError)
            {
                throw new InvalidOperationException(
                    L("main.vm.error.modpackCandidateOwnership"),
                    ownershipError);
            }

            candidateOwnedRootIdentity = SafePath.CaptureExistingObjectIdentityLease(candidateOwnedRoot);
            ValidateCandidateForUpdate(server.Model, candidate, targetVersion);

            var previousLaunchFields = CaptureModpackLaunchFields(server.Model);
            transaction = await _modpackUpdateTransactionService.CommitAsync(
                server.Model,
                candidate,
                async callbackCancellation =>
                {
                    SetStatus("main.vm.status.modpackBackupCreating");
                    var backupPlan = await _modpackUpdateBackupPlanner.CreatePlanAsync(
                        server.Model,
                        targetVersion.VersionName,
                        callbackCancellation);
                    var backupProgress = new Progress<BackupProgress>(value =>
                    {
                        if (value.Stage == BackupStage.Scanning)
                        {
                            SetStatus("main.vm.status.modpackBackupScanning");
                        }
                        else
                        {
                            SetStatus(
                                "main.vm.status.modpackBackupProgress",
                                value.CompletedFiles,
                                value.TotalFiles);
                        }
                    });
                    backupResult = await _backupService.CreateBackupAsync(
                        server.Model,
                        backupPlan.Options,
                        backupProgress,
                        callbackCancellation);
                },
                cancellationToken);
            // Candidate and rollback roots are the durable recovery material for the first real
            // launch. From this point only AcknowledgeCommitAsync or RollbackCommittedAsync may
            // remove them; the generic candidate cleanup path must not race that journal.
            candidateCleanupAllowed = false;

            transaction.LaunchFields.ApplyTo(server.Model);
            server.NotifyModpackConfigurationChanged();
            _playerPresenceCoreTypes[server.Id] = server.Model.CoreType;
            try
            {
                await SaveSettingsAsync();
            }
            catch (Exception saveError) when (saveError is not OutOfMemoryException)
            {
                previousLaunchFields.ApplyTo(server.Model);
                server.NotifyModpackConfigurationChanged();
                _playerPresenceCoreTypes[server.Id] = server.Model.CoreType;
                try
                {
                    await _modpackUpdateTransactionService.RollbackCommittedAsync(
                        server.Model,
                        transaction.TransactionId,
                        CancellationToken.None);
                    candidateCleanupAllowed = true;
                }
                catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException)
                {
                    candidateCleanupAllowed = false;
                    throw new IOException(
                        L("main.vm.error.modpackSettingsRollbackIncomplete"),
                        new AggregateException(saveError, rollbackError));
                }

                throw;
            }

            RegisterPendingModpackHealthValidation(
                server.Id,
                transaction.TransactionId,
                transaction.PreviousLaunchFields);

            var backupText = backupResult is null
                ? L("main.vm.modpack.backupCreated")
                : L("main.vm.modpack.backupPath", backupResult.ArchivePath);
            server.AppendConsole(SystemConsoleLineFactory.Create(
                server.Id,
                L(
                    "main.vm.console.modpackUpdatedAwaitingHealth",
                    previousLaunchFields.ModpackVersionName ?? previousLaunchFields.ModpackVersionId,
                    targetVersion.VersionName,
                    backupText),
                ConsoleLineSeverity.Information));
            SetStatus("main.vm.status.modpackUpdatedAwaitingHealth", server.Name, targetVersion.VersionName);
        }
        catch (ModpackUpdateRollbackException)
        {
            candidateCleanupAllowed = false;
            throw;
        }
        finally
        {
            if (candidateCleanupAllowed
                && candidateOwnedRoot is not null
                && candidateOwnedRootIdentity is not null)
            {
                await TryCleanupOwnedUpdateCandidateAsync(
                    candidateOwnedRoot,
                    candidateOwnedRootIdentity.Identity,
                    server);
            }

            candidateOwnedRootIdentity?.Dispose();
            if (serverRegistryGateEntered)
            {
                _serverRegistryGate.Release();
            }
            if (backupGateEntered)
            {
                backupGate!.Release();
            }
            if (lifecycleGate is not null)
            {
                _lifecycleTransitions.TryRemove(server.Id, out _);
                lifecycleGate.Release();
            }
            _modpackUpdates.TryRemove(server.Id, out _);
            StartSelectedCommand.NotifyCanExecuteChanged();
            UpdateSelectedModpackCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task ApplyProductServiceModpackUpdateAsync(
        ServerInstanceViewModel server,
        OnlineModpackSearchResult project,
        OnlineModpackVersion targetVersion,
        CancellationToken cancellationToken,
        SecureString? transientApiKey,
        bool minecraftEulaAccepted)
    {
        var controller = _productServiceController
            ?? throw new InvalidOperationException(L("main.vm.error.serviceModpackUpdateNotInitialized"));
        EnsureProductServiceConnected();
        if (!server.IsServiceManaged)
        {
            throw new InvalidOperationException(
                L("main.vm.error.serviceModpackUpdateRequiresMigratedServer"));
        }

        ValidateModpackUpdateStillAllowed(server, project, targetVersion);
        var candidateName = SafePath.SanitizeFileName(
            L("main.vm.modpack.candidateName", server.Name, targetVersion.VersionName),
            fallback: "modpack-update-candidate",
            maxLength: 80);
        var installProgress = new Progress<OnlineModpackInstallProgress>(value =>
        {
            var percentage = value.Percentage is { } number ? $" {number:0}%" : string.Empty;
            SetStatus("main.vm.status.modpackUpdateProgress", value.Message, percentage);
        });

        ServerInstance? candidate = null;
        string? candidateOwnedRoot = null;
        SafePathObjectIdentityLease? candidateOwnedRootIdentity = null;
        var serverRegistryGateEntered = false;
        try
        {
            candidate = await _onlineModpackWorkflow.InstallAsync(
                new OnlineModpackInstallRequest(
                    project,
                    targetVersion,
                    candidateName,
                    minecraftEulaAccepted),
                transientApiKey,
                installProgress,
                cancellationToken);
            candidateOwnedRoot = ResolveOwnedCandidateInstallRoot(candidate.DirectoryPath);

            // The candidate remains GUI-owned until the Service has copied and committed it. Keep
            // the registry gate across that handoff so a simultaneous local import cannot claim
            // the same tree and later have it removed by candidate cleanup.
            await _serverRegistryGate.WaitAsync(CancellationToken.None);
            serverRegistryGateEntered = true;
            try
            {
                candidateOwnedRoot = _serverDirectoryDeletionService.ValidateDeletionTarget(
                    candidateOwnedRoot,
                    Servers
                        .Where(managed => managed.CanAccessLocalFiles)
                        .Select(managed => managed.DirectoryPath));
            }
            catch (UnauthorizedAccessException ownershipError)
            {
                throw new InvalidOperationException(
                    L("main.vm.error.modpackLocalCandidateOwnership"),
                    ownershipError);
            }

            candidateOwnedRootIdentity = SafePath.CaptureExistingObjectIdentityLease(candidateOwnedRoot);
            ValidateModpackUpdateStillAllowed(server, project, targetVersion);
            ValidateCandidateForUpdate(server.Model, candidate, targetVersion);
            var expectedCurrentVersionId = server.Model.ModpackVersionId
                ?? throw new InvalidOperationException(L("main.vm.error.modpackCurrentVersionMissing"));
            var definition = CreateProductServiceModpackUpdateDefinition(
                candidate,
                targetVersion);

            SetStatus("main.vm.status.modpackSendingToService", targetVersion.VersionName);
            var status = await ExecuteProductServiceOperationAsync(
                token => controller.UpdateModpackAsync(
                    candidate,
                    server.Id,
                    expectedCurrentVersionId,
                    definition,
                    token),
                cancellationToken);

            var authoritative = await ExecuteProductServiceOperationAsync(
                token => controller.GetRegistrationAsync(server.Id, token),
                cancellationToken);
            if (Servers.Contains(server))
            {
                ApplyProductServiceModpackMetadata(server.Model, authoritative);
                if (CaptureServiceCatalogAppearancePreference(server, candidate))
                {
                    await SaveSettingsAsync();
                }
                _playerPresenceCoreTypes[server.Id] = server.Model.CoreType;
                server.NotifyModpackConfigurationChanged();
                server.AppendConsole(SystemConsoleLineFactory.Create(
                    server.Id,
                    status.State switch
                    {
                        ProductServerModpackUpdateState.AwaitingHealth =>
                            L("main.vm.console.serviceModpackAwaitingHealth", targetVersion.VersionName),
                        ProductServerModpackUpdateState.HealthyAwaitingStop =>
                            L("main.vm.console.serviceModpackHealthyAwaitingStop", targetVersion.VersionName),
                        ProductServerModpackUpdateState.Completed =>
                            L("main.vm.console.serviceModpackCompleted", targetVersion.VersionName),
                        _ => L("main.vm.console.serviceModpackState", status.State),
                    },
                    ConsoleLineSeverity.Information));
            }

            var statusKey = status.State switch
            {
                ProductServerModpackUpdateState.AwaitingHealth =>
                    "main.vm.status.modpackUpdatedAwaitingHealth",
                ProductServerModpackUpdateState.HealthyAwaitingStop =>
                    "main.vm.status.serviceModpackHealthyAwaitingStop",
                ProductServerModpackUpdateState.Completed =>
                    "main.vm.status.serviceModpackCompleted",
                _ => "main.vm.status.serviceModpackState",
            };
            if (status.State == ProductServerModpackUpdateState.HealthyAwaitingStop)
            {
                SetStatus(statusKey, server.Name);
            }
            else if (status.State == ProductServerModpackUpdateState.Completed
                     || status.State == ProductServerModpackUpdateState.AwaitingHealth)
            {
                SetStatus(statusKey, server.Name, targetVersion.VersionName);
            }
            else
            {
                SetStatus(statusKey, server.Name, status.State);
            }
        }
        finally
        {
            if (candidateOwnedRoot is not null && candidateOwnedRootIdentity is not null)
            {
                await TryCleanupOwnedUpdateCandidateAsync(
                    candidateOwnedRoot,
                    candidateOwnedRootIdentity.Identity,
                    server);
            }

            candidateOwnedRootIdentity?.Dispose();
            if (serverRegistryGateEntered)
            {
                _serverRegistryGate.Release();
            }
        }
    }

    private static ProductServerModpackUpdateDefinition CreateProductServiceModpackUpdateDefinition(
        ServerInstance candidate,
        OnlineModpackVersion targetVersion)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(targetVersion);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.DirectoryPath));
        var launchKind = candidate.LaunchKind switch
        {
            ServerLaunchKind.ExecutableJar => ProductServerLaunchKind.ExecutableJar,
            ServerLaunchKind.JavaArgumentFiles => ProductServerLaunchKind.JavaArgumentFiles,
            _ => throw new InvalidDataException(L("main.vm.error.modpackLaunchKindUnsupported")),
        };
        var source = candidate.ModpackSource switch
        {
            ModpackSourceKind.Ftb => ProductModpackSourceKind.Ftb,
            ModpackSourceKind.Modrinth => ProductModpackSourceKind.Modrinth,
            ModpackSourceKind.CurseForge => ProductModpackSourceKind.CurseForge,
            _ => throw new InvalidDataException(L("main.vm.error.modpackSourceUnsupported")),
        };
        var jar = launchKind == ProductServerLaunchKind.ExecutableJar
            ? MapProductServiceCandidateRelativePath(root, candidate.ServerJarPath, "Server JAR")
            : string.Empty;
        var argumentFiles = candidate.JavaArgumentFilePaths
            .Select(path => MapProductServiceCandidateRelativePath(root, path, "Java argument file"))
            .ToArray();

        return new ProductServerModpackUpdateDefinition
        {
            LaunchKind = launchKind,
            ServerJarPath = jar,
            JavaArgumentFilePaths = argumentFiles,
            CoreType = candidate.CoreType.ToString(),
            MinecraftVersion = candidate.MinecraftVersion,
            ServerArguments = candidate.ServerArguments.ToArray(),
            ModpackProviderId = candidate.ModpackProviderId,
            ModpackSource = source,
            ModpackProjectId = targetVersion.ProjectId,
            ModpackVersionId = targetVersion.VersionId,
            ModpackVersionName = targetVersion.VersionName,
            IsInstallerArtifact = candidate.IsInstallerArtifact,
        };
    }

    private static string MapProductServiceCandidateRelativePath(
        string candidateRoot,
        string value,
        string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(L("main.vm.error.modpackCandidatePathMissing", label));
        }

        var full = Path.IsPathFullyQualified(value)
            ? Path.GetFullPath(value)
            : SafePath.EnsureWithinRoot(candidateRoot, value, allowRoot: false);
        if (!SafePath.IsWithinRoot(candidateRoot, full) || !File.Exists(full))
        {
            throw new InvalidDataException(L("main.vm.error.modpackCandidatePathOutside", label));
        }

        SafePath.EnsureNoReparsePointsUnderRoot(candidateRoot, full);
        var relative = Path.GetRelativePath(candidateRoot, full).Replace('\\', '/');
        if (relative.Length is < 1 or > 512 || relative.StartsWith("../", StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relative))
        {
            throw new InvalidDataException(L("main.vm.error.modpackCandidateRelativePathInvalid", label));
        }

        return relative;
    }

    private static void ApplyProductServiceModpackMetadata(
        ServerInstance model,
        ProductServerRegistration registration)
    {
        if (model.Id != registration.Id)
        {
            throw new InvalidDataException(L("main.vm.error.serviceModpackServerMismatch"));
        }

        model.LaunchKind = (ServerLaunchKind)registration.LaunchKind;
        model.ServerJarPath = registration.ServerJarPath;
        model.JavaArgumentFilePaths = registration.JavaArgumentFilePaths.ToList();
        model.CoreType = ParseProductCoreType(registration.CoreType);
        model.MinecraftVersion = registration.MinecraftVersion;
        model.ServerArguments = registration.ServerArguments.ToList();
        model.ModpackProviderId = registration.ModpackProviderId;
        model.ModpackSource = (ModpackSourceKind)registration.ModpackSource;
        model.ModpackProjectId = registration.ModpackProjectId;
        model.ModpackVersionId = registration.ModpackVersionId;
        model.ModpackVersionName = registration.ModpackVersionName;
        model.IsInstallerArtifact = registration.IsInstallerArtifact;
    }

    private static OnlineModpackProvider GetOnlineModpackProvider(ServerInstance server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.ModpackSource switch
        {
            ModpackSourceKind.Ftb => OnlineModpackProvider.Ftb,
            ModpackSourceKind.Modrinth => OnlineModpackProvider.Modrinth,
            ModpackSourceKind.CurseForge => OnlineModpackProvider.CurseForge,
            _ => throw new InvalidOperationException(
                L("main.vm.error.modpackProviderUnsupported"))
        };
    }

    private void ValidateModpackUpdateStillAllowed(
        ServerInstanceViewModel server,
        OnlineModpackSearchResult project,
        OnlineModpackVersion targetVersion)
    {
        if (!Servers.Contains(server))
        {
            throw new InvalidOperationException(L("main.vm.error.selectedServerNoLongerManaged"));
        }

        if (server.State != ServerState.Stopped
            || (_processManager.TryGetSnapshot(server.Id, out var snapshot)
                && snapshot.State is ServerState.Starting or ServerState.Running or ServerState.Stopping))
        {
            throw new InvalidOperationException(L("main.vm.error.modpackUpdateRequiresStopped"));
        }

        var provider = GetOnlineModpackProvider(server.Model);
        if (project.Provider != provider
            || targetVersion.Provider != provider
            || !string.Equals(project.ProjectId, server.Model.ModpackProjectId, StringComparison.Ordinal)
            || !string.Equals(targetVersion.ProjectId, server.Model.ModpackProjectId, StringComparison.Ordinal)
            || string.Equals(targetVersion.VersionId, server.Model.ModpackVersionId, StringComparison.Ordinal)
            || !targetVersion.HasOfficialServerPack)
        {
            throw new InvalidOperationException(
                L("main.vm.error.modpackSelectionChanged"));
        }

        if (server.Model.IsInstallerArtifact)
        {
            throw new InvalidOperationException(L("main.vm.error.modpackInstallerCannotUpdate"));
        }
    }

    private static void ValidateCandidateForUpdate(
        ServerInstance live,
        ServerInstance candidate,
        OnlineModpackVersion targetVersion)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(candidate);
        if (PathsEqual(live.DirectoryPath, candidate.DirectoryPath))
        {
            throw new InvalidDataException(L("main.vm.error.modpackCandidateSameFolder"));
        }

        var expectedSource = targetVersion.Provider switch
        {
            OnlineModpackProvider.Ftb => ModpackSourceKind.Ftb,
            OnlineModpackProvider.Modrinth => ModpackSourceKind.Modrinth,
            OnlineModpackProvider.CurseForge => ModpackSourceKind.CurseForge,
            _ => ModpackSourceKind.None
        };
        if (expectedSource == ModpackSourceKind.None
            || candidate.ModpackSource != expectedSource
            || !string.Equals(candidate.ModpackProjectId, targetVersion.ProjectId, StringComparison.Ordinal)
            || !string.Equals(candidate.ModpackVersionId, targetVersion.VersionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(L("main.vm.error.modpackCandidateVersionMismatch"));
        }

        if (candidate.IsInstallerArtifact)
        {
            throw new InvalidDataException(L("main.vm.error.modpackCandidateIsInstaller"));
        }

        if (candidate.LaunchKind == ServerLaunchKind.ExecutableJar)
        {
            var jarPath = Path.GetFullPath(candidate.ServerJarPath);
            if (!SafePath.IsWithinRoot(candidate.DirectoryPath, jarPath) || !File.Exists(jarPath))
            {
                throw new InvalidDataException(L("main.vm.error.modpackCandidateJarInvalid"));
            }
        }
        else if (candidate.JavaArgumentFilePaths.Count == 0)
        {
            throw new InvalidDataException(L("main.vm.error.modpackCandidateArgumentFileMissing"));
        }
    }

    private string ResolveOwnedCandidateInstallRoot(string candidateDirectoryPath)
    {
        var serversRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.Servers));
        var candidateRoot = Path.TrimEndingDirectorySeparator(
            SafePath.EnsureWithinRoot(serversRoot, candidateDirectoryPath, allowRoot: false));
        if (!Directory.Exists(candidateRoot))
        {
            throw new DirectoryNotFoundException(L("main.vm.error.modpackCandidateFolderMissing", candidateRoot));
        }

        var parent = Directory.GetParent(candidateRoot)?.FullName;
        if (string.IsNullOrWhiteSpace(parent) || !PathsEqual(parent, serversRoot))
        {
            throw new UnauthorizedAccessException(
                L("main.vm.error.modpackCandidateRootInvalid"));
        }

        SafePath.EnsureTreeContainsNoReparsePoints(candidateRoot);
        return candidateRoot;
    }

    private static ModpackUpdateLaunchFields CaptureModpackLaunchFields(ServerInstance server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return new ModpackUpdateLaunchFields(
            Path.GetFullPath(server.DirectoryPath),
            server.ServerJarPath,
            server.LaunchKind,
            [.. server.JavaArgumentFilePaths],
            server.SourceLaunchScriptPath,
            server.CoreType,
            server.MinecraftVersion,
            server.JavaMajorVersion,
            server.JavaExecutablePath,
            [.. server.ServerArguments],
            server.ModpackSource,
            server.ModpackProjectId,
            server.ModpackVersionId,
            server.ModpackVersionName,
            server.IsInstallerArtifact);
    }

    private async Task TryCleanupOwnedUpdateCandidateAsync(
        string candidateRoot,
        SafePathObjectIdentity expectedIdentity,
        ServerInstanceViewModel server)
    {
        if (!Directory.Exists(candidateRoot)) return;
        try
        {
            await _serverDirectoryDeletionService.DeleteAsync(
                candidateRoot,
                Servers.Select(item => item.DirectoryPath),
                expectedIdentity,
                CancellationToken.None);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            server.AppendConsole(SystemConsoleLineFactory.Create(
                server.Id,
                L("main.vm.console.modpackCandidateCleanupFailed", candidateRoot, error.Message),
                ConsoleLineSeverity.Warning));
        }
    }

    private async Task<string?> RecoverPendingModpackUpdatesAsync(CancellationToken cancellationToken)
    {
        var rolledBack = 0;
        var awaitingHealthValidation = 0;
        foreach (var server in _settings.Instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_modpackUpdateTransactionService.HasPendingArtifacts(server))
            {
                continue;
            }

            var recovery = await _modpackUpdateTransactionService.RecoverPendingAsync(
                server,
                cancellationToken);
            if (recovery.Action == ModpackUpdateRecoveryAction.None)
            {
                continue;
            }

            if (recovery.Action == ModpackUpdateRecoveryAction.RolledBack)
            {
                if (recovery.PreviousLaunchFields is not { } rolledBackLaunchFields)
                {
                    throw new InvalidDataException(
                        L("main.vm.error.modpackRecoveredJournalMissingPrevious"));
                }

                rolledBackLaunchFields.ApplyTo(server);
                _settings.SchemaVersion = Math.Max(
                    _settings.SchemaVersion,
                    ManagerSettings.CurrentSchemaVersion);
                await _settingsStore.SaveAsync(
                    PrepareSettingsForPersistence(),
                    cancellationToken);
                rolledBack++;
                continue;
            }

            if (recovery.TransactionId is not { } transactionId
                || recovery.LaunchFields is not { } launchFields
                || recovery.PreviousLaunchFields is not { } previousFields)
            {
                throw new InvalidDataException(
                    L("main.vm.error.modpackCommittedJournalIncomplete"));
            }

            launchFields.ApplyTo(server);
            try
            {
                _settings.SchemaVersion = Math.Max(
                    _settings.SchemaVersion,
                    ManagerSettings.CurrentSchemaVersion);
                await _settingsStore.SaveAsync(
                    PrepareSettingsForPersistence(),
                    cancellationToken);
            }
            catch (Exception saveError) when (saveError is not OutOfMemoryException)
            {
                previousFields.ApplyTo(server);
                try
                {
                    await _modpackUpdateTransactionService.RollbackCommittedAsync(
                        server,
                        transactionId,
                        CancellationToken.None);
                }
                catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException)
                {
                    throw new IOException(
                        L("main.vm.error.modpackStartupRollbackIncomplete"),
                        new AggregateException(saveError, rollbackError));
                }

                throw;
            }

            RegisterPendingModpackHealthValidation(server.Id, transactionId, previousFields);
            awaitingHealthValidation++;
        }

        var summaries = new List<string>(2);
        if (rolledBack > 0) summaries.Add(L("main.vm.modpack.recoveredCount", rolledBack));
        if (awaitingHealthValidation > 0)
        {
            summaries.Add(L("main.vm.modpack.awaitingHealthCount", awaitingHealthValidation));
        }
        return summaries.Count == 0 ? null : string.Join("；", summaries);
    }

    private void RegisterPendingModpackHealthValidation(
        Guid instanceId,
        Guid transactionId,
        ModpackUpdateLaunchFields previousLaunchFields)
    {
        var validation = new PendingModpackHealthValidation(
            transactionId,
            previousLaunchFields);
        if (_pendingModpackHealthValidations.TryAdd(instanceId, validation))
        {
            _modpackRecoveryFailures.TryRemove(instanceId, out _);
            return;
        }

        validation.Dispose();
        if (_pendingModpackHealthValidations.TryGetValue(instanceId, out var existing)
            && existing.TransactionId == transactionId)
        {
            return;
        }

        throw new InvalidOperationException(
            L("main.vm.error.modpackConflictingValidation"));
    }

    internal bool HasPendingModpackHealthValidation(Guid instanceId)
        => _pendingModpackHealthValidations.ContainsKey(instanceId);

    internal void ObservePendingModpackHealthStateChange(ServerStateChangedEventArgs eventArgs)
    {
        if (!_pendingModpackHealthValidations.TryGetValue(
                eventArgs.InstanceId,
                out var validation))
        {
            return;
        }

        var manualStopEpoch = _manualStopEpochs.GetValueOrDefault(eventArgs.InstanceId);
        if (eventArgs.State == ServerState.Starting)
        {
            validation.BeginSession(
                eventArgs.SessionId,
                manualStopEpoch,
                _sessionServicesCancellation.Token);
            return;
        }

        if (eventArgs.State == ServerState.Running)
        {
            validation.BeginSessionIfMissing(
                eventArgs.SessionId,
                manualStopEpoch,
                _sessionServicesCancellation.Token);
            StartPendingModpackStatusProbe(eventArgs.InstanceId, eventArgs.SessionId, validation);
            return;
        }

        if (eventArgs.State is not (ServerState.Stopped or ServerState.Crashed or ServerState.Faulted))
        {
            return;
        }

        var action = validation.ObserveTerminalState(
            eventArgs.SessionId,
            eventArgs.State,
            manualStopEpoch);
        if (action == PendingModpackFinalizationAction.Rollback)
        {
            // This is written before ProcessManager evaluates its auto-restart callback. Even if
            // rollback finishes very quickly, the failed candidate session must never launch a
            // second time automatically.
            _modpackAutoRestartBlocks[eventArgs.InstanceId] = 0;
        }

        QueuePendingModpackFinalization(eventArgs.InstanceId, validation, action);
    }

    internal void MarkPendingModpackSessionHealthy(
        Guid instanceId,
        Guid sessionId,
        string evidence)
    {
        if (!_pendingModpackHealthValidations.TryGetValue(instanceId, out var validation)
            || !validation.TryMarkHealthy(sessionId))
        {
            return;
        }

        PostSystemMessage(
            instanceId,
            L("main.vm.console.modpackHealthPassed", evidence),
            ConsoleLineSeverity.Information);
    }

    private void StartPendingModpackStatusProbe(
        Guid instanceId,
        Guid sessionId,
        PendingModpackHealthValidation validation)
    {
        if (!validation.TryGetProbeToken(sessionId, out var cancellationToken)) return;
        var key = (instanceId, sessionId);
        var task = RunPendingModpackStatusProbeAsync(
            instanceId,
            sessionId,
            cancellationToken);
        if (!_modpackHealthProbeTasks.TryAdd(key, task)) return;
        _ = RemovePendingModpackProbeWhenCompleteAsync(key, task);
    }

    private async Task RunPendingModpackStatusProbeAsync(
        Guid instanceId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_instanceModels.TryGetValue(instanceId, out var instance)) return;
            var endpoint = await ReadStatusEndpointAsync(instance, cancellationToken)
                .ConfigureAwait(false);
            if (!endpoint.StatusEnabled) return;

            // One bounded probe loop per pending Server. Done-line recognition remains active
            // after this window, so a very unusual server taking longer does not lose recovery.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            while (DateTimeOffset.UtcNow < deadline
                   && IsCurrentRunningSession(instanceId, sessionId))
            {
                var activePort = _sessionLaunchPorts.TryGetValue(
                    (instanceId, sessionId),
                    out var sessionPort)
                    ? sessionPort
                    : instance.Port;
                var result = await _minecraftStatusProbe.ProbeAsync(
                        endpoint.Host,
                        activePort,
                        TimeSpan.FromSeconds(3),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.IsHealthy)
                {
                    MarkPendingModpackSessionHealthy(
                        instanceId,
                        sessionId,
                        L("main.vm.modpack.healthEvidence.statusProtocol"));
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal terminal-state, replacement-session or application-shutdown cancellation.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            PostSystemMessage(
                instanceId,
                L("main.vm.console.modpackHealthProbeStopped", error.Message),
                ConsoleLineSeverity.Warning);
        }
    }

    private async Task RemovePendingModpackProbeWhenCompleteAsync(
        (Guid InstanceId, Guid SessionId) key,
        Task probeTask)
    {
        try
        {
            await probeTask.ConfigureAwait(false);
        }
        finally
        {
            _modpackHealthProbeTasks.TryRemove(
                new KeyValuePair<(Guid InstanceId, Guid SessionId), Task>(key, probeTask));
        }
    }

    private void QueuePendingModpackFinalization(
        Guid instanceId,
        PendingModpackHealthValidation validation,
        PendingModpackFinalizationAction action)
    {
        if (action == PendingModpackFinalizationAction.None) return;
        var task = Task.Run(() => FinalizePendingModpackHealthAsync(
            instanceId,
            validation,
            action));
        if (!_modpackHealthFinalizationTasks.TryAdd(instanceId, task))
        {
            validation.ReleaseFinalization();
            return;
        }

        _ = RemovePendingModpackFinalizationWhenCompleteAsync(instanceId, task);
    }

    private async Task FinalizePendingModpackHealthAsync(
        Guid instanceId,
        PendingModpackHealthValidation validation,
        PendingModpackFinalizationAction action)
    {
        try
        {
            if (!_instanceModels.TryGetValue(instanceId, out var instance))
            {
                throw new KeyNotFoundException(
                    L("main.vm.error.modpackHealthInstanceMissing"));
            }

            if (action == PendingModpackFinalizationAction.Acknowledge)
            {
                await ExecuteAfterServerDirectoryLockReleasedAsync(
                    () => _modpackUpdateTransactionService.AcknowledgeCommitAsync(
                        instance,
                        validation.TransactionId,
                        CancellationToken.None));
                RemovePendingModpackHealthValidation(instanceId, validation);
                PostSystemMessage(
                    instanceId,
                    L("main.vm.console.modpackHealthCleanupCompleted"),
                    ConsoleLineSeverity.Information);
                return;
            }

            try
            {
                // Persist the failed-health decision before changing manager.json. If MCSV exits
                // at any following instruction, startup recovery will finish the same rollback
                // and apply PreviousLaunchFields from the journal instead of guessing settings.
                await _modpackUpdateTransactionService.RequestCommittedRollbackAsync(
                    instance,
                    validation.TransactionId,
                    CancellationToken.None);
                await ApplyModpackLaunchFieldsAndSaveAsync(
                    instanceId,
                    validation.PreviousLaunchFields);
                await ExecuteAfterServerDirectoryLockReleasedAsync(
                    () => _modpackUpdateTransactionService.RollbackCommittedAsync(
                        instance,
                        validation.TransactionId,
                        CancellationToken.None));
            }
            catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException)
            {
                _modpackRecoveryFailures[instanceId] = 0;
                throw;
            }

            RemovePendingModpackHealthValidation(instanceId, validation);
            _modpackRecoveryFailures.TryRemove(instanceId, out _);
            PostSystemMessage(
                instanceId,
                L("main.vm.console.modpackHealthRollbackCompleted"),
                ConsoleLineSeverity.Error);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            validation.ReleaseFinalization();
            PostSystemMessage(
                instanceId,
                action == PendingModpackFinalizationAction.Rollback
                    ? L("main.vm.console.modpackHealthRollbackFailed", error.Message)
                    : L("main.vm.console.modpackHealthCleanupDeferred", error.Message),
                ConsoleLineSeverity.Error);
        }
    }

    private async Task ApplyModpackLaunchFieldsAndSaveAsync(
        Guid instanceId,
        ModpackUpdateLaunchFields launchFields)
    {
        async Task ApplyAndSaveOnUiAsync()
        {
            var server = Servers.FirstOrDefault(item => item.Id == instanceId)
                ?? throw new KeyNotFoundException(L("main.vm.error.modpackServerMissing"));
            launchFields.ApplyTo(server.Model);
            server.NotifyModpackConfigurationChanged();
            _playerPresenceCoreTypes[instanceId] = server.Model.CoreType;
            await SaveSettingsAsync();
            UpdateSelectedModpackCommand.NotifyCanExecuteChanged();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            await ApplyAndSaveOnUiAsync();
            return;
        }

        await dispatcher.InvokeAsync(
                ApplyAndSaveOnUiAsync,
                DispatcherPriority.Send,
                CancellationToken.None)
            .Task
            .Unwrap();
    }

    private static async Task ExecuteAfterServerDirectoryLockReleasedAsync(
        Func<Task> operation)
    {
        const int maximumAttempts = 50;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await operation().ConfigureAwait(false);
                return;
            }
            catch (ServerDirectoryLockException) when (attempt < maximumAttempts)
            {
                // Terminal state is published immediately before ProcessManager disposes its
                // directory lease. Retry only that known handoff race, with a strict 5 s bound.
                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }
        }
    }

    private void RemovePendingModpackHealthValidation(
        Guid instanceId,
        PendingModpackHealthValidation validation)
    {
        if (_pendingModpackHealthValidations.TryRemove(
                new KeyValuePair<Guid, PendingModpackHealthValidation>(instanceId, validation)))
        {
            validation.Dispose();
        }
    }

    private async Task RemovePendingModpackFinalizationWhenCompleteAsync(
        Guid instanceId,
        Task finalizationTask)
    {
        try
        {
            await finalizationTask.ConfigureAwait(false);
        }
        finally
        {
            _modpackHealthFinalizationTasks.TryRemove(
                new KeyValuePair<Guid, Task>(instanceId, finalizationTask));
        }
    }

    internal async Task WaitForPendingModpackHealthActionsAsync()
    {
        while (true)
        {
            var tasks = _modpackHealthFinalizationTasks.Values
                .Concat(_modpackHealthProbeTasks.Values)
                .Distinct()
                .ToArray();
            if (tasks.Length == 0) return;
            await Task.WhenAll(tasks);
        }
    }

    private async Task WaitForModpackUpdatesToFinishAsync()
    {
        while (true)
        {
            var activeUpdates = _modpackUpdateTasks.Values.Distinct().ToArray();
            if (activeUpdates.Length == 0) return;
            await Task.WhenAll(activeUpdates);
        }
    }

    private void OpenModpackUpdateBackupsFolder()
    {
        var server = SelectedServer;
        if (server is null) return;
        OpenFolder(Path.Combine(server.DirectoryPath, "backups", "modpack-updates"));
    }

    private async Task CheckAddonUpdatesAsync()
    {
        var server = SelectedServer;
        if (server is null) return;
        await StartAddonScanAsync(server);
    }

    private void QueueAddonScan(ServerInstanceViewModel server)
        => _lastAddonScan = GuardAsync(
            () => StartAddonScanAsync(server),
            "main.vm.operation.checkAddonUpdates");

    private void QueueServerAdministrationSnapshot(ServerInstanceViewModel server)
        => _lastAddonScan = GuardAsync(
            () => LoadServerAdministrationSnapshotAsync(server),
            "main.vm.operation.checkAddonUpdates");

    private async Task LoadServerAdministrationSnapshotAsync(ServerInstanceViewModel server)
    {
        if (_productServiceController is null || !Servers.Contains(server)) return;
        EnsureProductServiceConnected();
        if (!SupportsProductServiceFileAdministration) return;

        var requestVersion = Interlocked.Increment(ref _addonScanVersion);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _sessionServicesCancellation.Token);
        lock (_addonScanSync)
        {
            _addonScanCancellation?.Cancel();
            _addonScanCancellation = cancellation;
        }

        try
        {
            var snapshot = await ExecuteProductServiceOperationAsync(
                token => _productServiceController.GetServerAdministrationAsync(server.Id, token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || requestVersion != Volatile.Read(ref _addonScanVersion)
                || !ReferenceEquals(SelectedServer, server)
                || !Servers.Contains(server))
            {
                return;
            }

            if (SelectedWorkspaceTabKey == AddonsWorkspaceTabKey)
            {
                server.AddonUpdates.Clear();
                foreach (var addon in snapshot.Addons)
                {
                    server.AddonUpdates.Add(new AddonUpdateViewModel(addon));
                }
            }

            if (SelectedWorkspaceTabKey == JavaRuntimeWorkspaceTabKey)
            {
                InstalledJavaRuntimes.Clear();
                if (snapshot.Java.Configured)
                {
                    InstalledJavaRuntimes.Add(new JavaRuntimeItemViewModel(snapshot.Java));
                }
            }
        }
        finally
        {
            lock (_addonScanSync)
            {
                if (ReferenceEquals(_addonScanCancellation, cancellation))
                {
                    _addonScanCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private Task StartAddonScanAsync(ServerInstanceViewModel server)
    {
        if (!Servers.Contains(server)) return Task.CompletedTask;
        var requestVersion = Interlocked.Increment(ref _addonScanVersion);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _sessionServicesCancellation.Token);
        lock (_addonScanSync)
        {
            _addonScanCancellation?.Cancel();
            _addonScanCancellation = cancellation;
        }

        return _lastAddonScan = ScanAddonUpdatesAsync(
            server,
            requestVersion,
            cancellation);
    }

    private async Task ScanAddonUpdatesAsync(
        ServerInstanceViewModel server,
        long requestVersion,
        CancellationTokenSource cancellation)
    {
        CheckAddonUpdatesCommand.NotifyCanExecuteChanged();
        try
        {
            var serverRoot = await ResolveBrowseableServerDirectoryAsync(
                server,
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || requestVersion != Volatile.Read(ref _addonScanVersion)
                || !Servers.Contains(server))
            {
                return;
            }
            ValidateAddonDirectoriesForRead(serverRoot, server.Model.CoreType);
            var scanModel = new ServerInstance
            {
                DirectoryPath = serverRoot,
                CoreType = server.Model.CoreType,
                MinecraftVersion = server.Model.MinecraftVersion,
            };

            server.AddonUpdates.Clear();
            var progress = new Progress<(int Completed, int Total)>(value =>
                SetStatus("main.vm.status.addonHashing", value.Completed, value.Total));
            var updates = await _modrinthProvider.CheckUpdatesAsync(
                scanModel,
                progress,
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || requestVersion != Volatile.Read(ref _addonScanVersion)
                || !Servers.Contains(server))
            {
                return;
            }
            foreach (var update in updates)
            {
                server.AddonUpdates.Add(new AddonUpdateViewModel(update));
            }

            var available = updates.Count(update => update.IsUpdateAvailable);
            if (updates.Count == 0)
            {
                SetStatus("main.vm.status.addonNone");
            }
            else
            {
                SetStatus("main.vm.status.addonScanCompleted", updates.Count, available);
            }
        }
        finally
        {
            lock (_addonScanSync)
            {
                if (ReferenceEquals(_addonScanCancellation, cancellation))
                {
                    _addonScanCancellation = null;
                }
            }
            cancellation.Dispose();
            CheckAddonUpdatesCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task<string> ResolveBrowseableServerDirectoryAsync(
        ServerInstanceViewModel server,
        CancellationToken cancellationToken = default)
    {
        if (server.CanAccessLocalFiles)
        {
            var localRoot = Path.GetFullPath(server.DirectoryPath);
            if (!Directory.Exists(localRoot))
            {
                throw new DirectoryNotFoundException(
                    L("main.vm.error.serviceDirectoryMissing"));
            }

            return localRoot;
        }

        if (!server.IsServiceManaged || _productServiceController is null)
        {
            throw new InvalidOperationException(
                L("main.vm.error.serviceFileAdministrationUnsupported"));
        }

        EnsureProductServiceConnected();
        if (!SupportsProductServiceFileAdministration)
        {
            throw new InvalidOperationException(
                L("main.vm.error.serviceFileAdministrationUnsupported"));
        }

        var directory = await ExecuteProductServiceOperationAsync(
            token => _productServiceController.GetServerDirectoryAsync(server.Id, token),
            cancellationToken);
        if (directory.ServerId != server.Id)
        {
            throw new InvalidDataException(L("main.vm.error.serviceDirectoryMismatch"));
        }
        if (!directory.Exists || !Directory.Exists(directory.DirectoryPath))
        {
            throw new DirectoryNotFoundException(
                L("main.vm.error.serviceDirectoryMissing"));
        }

        return Path.GetFullPath(directory.DirectoryPath);
    }

    private void CancelAddonScan()
    {
        Interlocked.Increment(ref _addonScanVersion);
        lock (_addonScanSync)
        {
            _addonScanCancellation?.Cancel();
        }
    }

    private async Task OpenSelectedAddonFolderAsync()
    {
        var server = SelectedServer;
        if (server is null || !Servers.Contains(server)) return;
        var serverRoot = await ResolveBrowseableServerDirectoryAsync(server);
        var candidates = ResolveAddonDirectories(serverRoot, server.Model.CoreType).ToArray();
        if (candidates.Length == 0)
        {
            throw new DirectoryNotFoundException(L("main.vm.error.addonFolderMissing"));
        }

        var existing = candidates.FirstOrDefault(Directory.Exists);
        if (server.IsServiceManaged)
        {
            if (existing is null)
            {
                throw new DirectoryNotFoundException(L("main.vm.error.addonFolderMissing"));
            }

            ValidateAddonDirectoriesForRead(serverRoot, server.Model.CoreType);
            OpenExistingFolder(existing);
            return;
        }

        var path = existing ?? candidates[0];
        Directory.CreateDirectory(path);
        ValidateAddonDirectoriesForRead(serverRoot, server.Model.CoreType);
        OpenExistingFolder(path);
    }

    private static IEnumerable<string> ResolveAddonDirectories(string serverRoot, CoreType coreType)
    {
        if (coreType is CoreType.Fabric or CoreType.Forge or CoreType.NeoForge)
        {
            yield return Path.Combine(serverRoot, "mods");
            yield break;
        }

        if (coreType is CoreType.Mohist or CoreType.Arclight or CoreType.CatServer)
        {
            yield return Path.Combine(serverRoot, "plugins");
            yield return Path.Combine(serverRoot, "mods");
            yield break;
        }

        if (coreType == CoreType.Vanilla) yield break;
        yield return Path.Combine(serverRoot, "plugins");
        if (coreType is CoreType.Unknown or CoreType.CustomJar)
        {
            yield return Path.Combine(serverRoot, "mods");
        }
    }

    private static void ValidateAddonDirectoriesForRead(string serverRoot, CoreType coreType)
    {
        var fullRoot = Path.GetFullPath(serverRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(fullRoot, fullRoot);
        foreach (var path in ResolveAddonDirectories(fullRoot, coreType))
        {
            if (File.Exists(path) && !Directory.Exists(path))
            {
                throw new InvalidDataException($"Addon directory path is a file: {path}");
            }
            if (Directory.Exists(path))
            {
                SafePath.EnsureNoReparsePointsUnderRoot(fullRoot, path);
            }
        }
    }

    private async Task EnsureEulaAcceptedUnderLockAsync(
        string launchDirectoryPath,
        bool userConfirmedMinecraftEula,
        CancellationToken cancellationToken)
    {
        // ProcessManager invokes this only while holding the cross-process directory lease. The
        // confirmation belongs to this exact StartAsync call; automatic restarts always carry the
        // default context and may proceed only when eula.txt was already accepted.
        await _minecraftEulaAcceptanceService.EnsureAcceptedAsync(
            launchDirectoryPath,
            userConfirmedMinecraftEula,
            cancellationToken);
    }

    private string? FindManagedJavaExecutable(int major)
    {
        return EnumerateManagedJavaRuntimes()
            .FirstOrDefault(runtime => runtime.MajorVersion == major)
            ?.JavaExecutablePath;
    }

    private IEnumerable<JavaRuntimeInfo> EnumerateManagedJavaRuntimes()
    {
        if (!Directory.Exists(_paths.Runtimes)) yield break;
        var inspected = 0;
        foreach (var runtimeDirectory in Directory.EnumerateDirectories(
                     _paths.Runtimes,
                     "temurin-*",
                     SearchOption.TopDirectoryOnly))
        {
            if (inspected++ >= 64) yield break;
            JavaRuntimeInfo? runtime = null;
            try
            {
                var directory = new DirectoryInfo(runtimeDirectory);
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var binDirectory = new DirectoryInfo(Path.Combine(directory.FullName, "bin"));
                if (!binDirectory.Exists || binDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var executable = Path.Combine(binDirectory.FullName, "java.exe");
                var releaseFile = Path.Combine(directory.FullName, "release");
                if (!File.Exists(executable) || !File.Exists(releaseFile)) continue;
                if (File.GetAttributes(executable).HasFlag(FileAttributes.ReparsePoint)
                    || File.GetAttributes(releaseFile).HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var releaseInfo = ReadJavaReleaseFile(releaseFile);
                if (!releaseInfo.TryGetValue("JAVA_VERSION", out var fullVersion)
                    || !TryParseJavaMajorVersion(fullVersion, out var major))
                {
                    continue;
                }

                runtime = new JavaRuntimeInfo
                {
                    MajorVersion = major,
                    FullVersion = fullVersion,
                    Vendor = releaseInfo.GetValueOrDefault("IMPLEMENTOR") ?? "Eclipse Temurin",
                    JavaExecutablePath = executable,
                    HomeDirectory = directory.FullName,
                    Architecture = releaseInfo.GetValueOrDefault("OS_ARCH") ?? "x64",
                    IsManaged = true,
                    IsValid = true
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Static discovery never executes a candidate runtime. Invalid folders remain unused.
            }

            if (runtime is not null) yield return runtime;
        }
    }

    private static IReadOnlyDictionary<string, string> ReadJavaReleaseFile(string path)
    {
        const int maximumReleaseFileBytes = 128 * 1024;
        var file = new FileInfo(path);
        if (file.Length > maximumReleaseFileBytes)
        {
            throw new InvalidDataException("Java release metadata is unexpectedly large.");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path).Take(128))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');
            if (key.Length > 0 && value.Length > 0) values[key] = value;
        }

        return values;
    }

    private static bool TryParseJavaMajorVersion(string version, out int major)
    {
        var numeric = version.StartsWith("1.", StringComparison.Ordinal)
            ? version[2..]
            : version;
        var digits = new string(numeric.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out major)
            && major is >= 8 and <= 99;
    }

    private bool RepairServiceAppearancePreferences()
    {
        var changed = false;
        foreach (var entry in _settings.ServiceServerAppearances.ToArray())
        {
            if (entry.Key == Guid.Empty || entry.Value is null)
            {
                _settings.ServiceServerAppearances.Remove(entry.Key);
                changed = true;
                continue;
            }

            var preference = entry.Value;
            var opacity = double.IsFinite(preference.BackgroundImageOpacity)
                ? Math.Clamp(preference.BackgroundImageOpacity, 0, 1)
                : 0.25;
            var background = RepairManagedServiceAppearanceAsset(
                preference.BackgroundImagePath,
                "backgrounds",
                entry.Key);
            var icon = RepairManagedServiceAppearanceAsset(
                preference.IconImagePath,
                "icons",
                entry.Key);
            var catalogIcon = RepairManagedServiceAppearanceAsset(
                preference.CatalogIconImagePath,
                "catalog-icons",
                entry.Key);
            var catalogPreview = RepairManagedServiceAppearanceAsset(
                preference.CatalogPreviewImagePath,
                "catalog-previews",
                entry.Key);
            if (opacity.Equals(preference.BackgroundImageOpacity)
                && string.Equals(background, preference.BackgroundImagePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(icon, preference.IconImagePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(catalogIcon, preference.CatalogIconImagePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(catalogPreview, preference.CatalogPreviewImagePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            preference.BackgroundImageOpacity = opacity;
            preference.BackgroundImagePath = background;
            preference.IconImagePath = icon;
            preference.CatalogIconImagePath = catalogIcon;
            preference.CatalogPreviewImagePath = catalogPreview;
            changed = true;
        }

        return changed;
    }

    private bool CaptureInitialServiceAppearancePreference(ServerInstance model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _settings.ServiceServerAppearances.TryGetValue(model.Id, out var persisted);
        var preference = persisted?.Copy() ?? new ServerAppearancePreference();
        var before = JsonSerializer.Serialize(preference);

        preference.BackgroundImageOpacity = persisted is null
            ? double.IsFinite(model.BackgroundImageOpacity)
                ? Math.Clamp(model.BackgroundImageOpacity, 0, 1)
                : 0.25
            : Math.Clamp(preference.BackgroundImageOpacity, 0, 1);
        preference.BackgroundImagePath ??= CopyServiceAppearanceAsset(
            model.BackgroundImagePath,
            "backgrounds",
            model.Id);
        preference.IconImagePath ??= CopyServiceAppearanceAsset(
            model.IconImagePath,
            "icons",
            model.Id);
        preference.CatalogIconImagePath ??= CopyServiceAppearanceAsset(
            model.CatalogIconImagePath,
            "catalog-icons",
            model.Id);
        preference.CatalogPreviewImagePath ??= CopyServiceAppearanceAsset(
            model.CatalogPreviewImagePath,
            "catalog-previews",
            model.Id);

        _settings.ServiceServerAppearances[model.Id] = preference;
        preference.ApplyTo(model);
        return persisted is null
               || !string.Equals(before, JsonSerializer.Serialize(preference), StringComparison.Ordinal);
    }

    private void PersistServiceAppearancePreference(ServerInstanceViewModel server)
    {
        if (!server.IsServiceManaged) return;
        var model = server.Model;
        var preference = new ServerAppearancePreference
        {
            BackgroundImagePath = CopyServiceAppearanceAsset(
                model.BackgroundImagePath,
                "backgrounds",
                model.Id),
            BackgroundImageOpacity = double.IsFinite(model.BackgroundImageOpacity)
                ? Math.Clamp(model.BackgroundImageOpacity, 0, 1)
                : 0.25,
            IconImagePath = CopyServiceAppearanceAsset(
                model.IconImagePath,
                "icons",
                model.Id),
            CatalogIconImagePath = CopyServiceAppearanceAsset(
                model.CatalogIconImagePath,
                "catalog-icons",
                model.Id),
            CatalogPreviewImagePath = CopyServiceAppearanceAsset(
                model.CatalogPreviewImagePath,
                "catalog-previews",
                model.Id),
        };
        _settings.ServiceServerAppearances[model.Id] = preference;
        preference.ApplyTo(model);
    }

    private bool CaptureServiceCatalogAppearancePreference(
        ServerInstanceViewModel server,
        ServerInstance candidate)
    {
        var model = server.Model;
        var preference = _settings.ServiceServerAppearances.TryGetValue(model.Id, out var persisted)
            ? persisted.Copy()
            : ServerAppearancePreference.From(model);
        var icon = CopyServiceAppearanceAsset(
            candidate.CatalogIconImagePath,
            "catalog-icons",
            model.Id);
        var preview = CopyServiceAppearanceAsset(
            candidate.CatalogPreviewImagePath,
            "catalog-previews",
            model.Id);
        if (icon is null && preview is null)
        {
            return false;
        }

        if (icon is not null) preference.CatalogIconImagePath = icon;
        if (preview is not null) preference.CatalogPreviewImagePath = preview;
        _settings.ServiceServerAppearances[model.Id] = preference;
        preference.ApplyTo(model);
        return true;
    }

    private void ApplyServiceAppearancePreference(ServerInstance model)
    {
        if (_settings.ServiceServerAppearances.TryGetValue(model.Id, out var preference)
            && preference is not null)
        {
            preference.ApplyTo(model);
        }
    }

    private string? CopyServiceAppearanceAsset(string? sourcePath, string category, Guid serverId)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
        {
            return null;
        }

        var managed = RepairManagedServiceAppearanceAsset(sourcePath, category, serverId);
        if (managed is not null)
        {
            return managed;
        }

        try
        {
            ValidateThemeAsset(sourcePath);
            var source = Path.GetFullPath(sourcePath);
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            var root = Path.Combine(_paths.Themes, category);
            Directory.CreateDirectory(root);
            SafePath.EnsureNoReparsePointsUnderRoot(_paths.Themes, root);
            var extension = Path.GetExtension(source).ToLowerInvariant();
            var destination = Path.Combine(root, $"{serverId:N}.{Guid.NewGuid():N}{extension}");
            File.Copy(source, destination, overwrite: false);
            return RepairManagedServiceAppearanceAsset(destination, category, serverId);
        }
        catch (Exception error) when (error is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException
                                      or ArgumentException
                                      or NotSupportedException)
        {
            return null;
        }
    }

    private string? RepairManagedServiceAppearanceAsset(
        string? currentPath,
        string category,
        Guid serverId)
    {
        if (string.IsNullOrWhiteSpace(currentPath)) return null;
        var root = Path.Combine(_paths.Themes, category);
        if (TryNormalizeManagedServiceAppearanceAsset(currentPath, root, serverId, out var normalized))
        {
            return normalized;
        }

        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateFiles(root, $"{serverId:N}.*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.FullName)
                .FirstOrDefault(path => TryNormalizeManagedServiceAppearanceAsset(
                    path,
                    root,
                    serverId,
                    out _));
        }
        catch (Exception error) when (error is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryNormalizeManagedServiceAppearanceAsset(
        string? path,
        string root,
        Guid serverId,
        out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            var candidate = Path.GetFullPath(path);
            var canonicalRoot = Path.GetFullPath(root);
            var file = new FileInfo(candidate);
            var extension = file.Extension;
            if (!file.Exists
                || file.Length is <= 0 or > 64L * 1024 * 1024
                || !file.Name.StartsWith($"{serverId:N}.", StringComparison.OrdinalIgnoreCase)
                || !SafePath.IsWithinRoot(canonicalRoot, candidate)
                || !extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                   && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                   && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                   && !extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                   && !extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            SafePath.EnsureNoReparsePointsUnderRoot(canonicalRoot, candidate);
            normalized = candidate;
            return true;
        }
        catch (Exception error) when (error is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException)
        {
            return false;
        }
    }

    internal void RepairPortablePaths(ServerInstance model)
    {
        if (string.IsNullOrWhiteSpace(model.DirectoryPath) || !Directory.Exists(model.DirectoryPath))
        {
            var previousLeaf = string.IsNullOrWhiteSpace(model.DirectoryPath)
                ? SafePath.SanitizeFileName(model.Name)
                : Path.GetFileName(Path.TrimEndingDirectorySeparator(model.DirectoryPath));
            var candidate = Path.Combine(_paths.Servers, previousLeaf);
            if (Directory.Exists(candidate))
            {
                model.DirectoryPath = candidate;
            }
        }

        if (model.LaunchKind == ServerLaunchKind.ExecutableJar
            && !string.IsNullOrWhiteSpace(model.DirectoryPath)
            && Directory.Exists(model.DirectoryPath)
            && (string.IsNullOrWhiteSpace(model.ServerJarPath) || !File.Exists(model.ServerJarPath)))
        {
            var artifactName = string.IsNullOrWhiteSpace(model.ServerJarPath)
                ? "server.jar"
                : Path.GetFileName(model.ServerJarPath);
            var candidate = Path.Combine(model.DirectoryPath, artifactName);
            if (File.Exists(candidate)) model.ServerJarPath = candidate;
        }

        if (model.JavaMajorVersion is { } javaMajor
            && (string.IsNullOrWhiteSpace(model.JavaExecutablePath) || !File.Exists(model.JavaExecutablePath)))
        {
            model.JavaExecutablePath = model.LaunchKind == ServerLaunchKind.JavaArgumentFiles
                ? FindBundledJavaExecutable(model.DirectoryPath, javaMajor) ?? FindManagedJavaExecutable(javaMajor)
                : FindManagedJavaExecutable(javaMajor);
        }

        model.BackgroundImagePath = RepairThemeAsset(model.BackgroundImagePath, "backgrounds", model.Id);
        model.IconImagePath = RepairThemeAsset(model.IconImagePath, "icons", model.Id);
        model.ModpackProviderId = NormalizeModpackProviderId(
            model.ModpackProviderId,
            model.ModpackSource);
        model.CatalogIconImagePath = RepairCatalogArtworkAsset(
            model.CatalogIconImagePath,
            model,
            "icon",
            "icons");
        model.CatalogPreviewImagePath = RepairCatalogArtworkAsset(
            model.CatalogPreviewImagePath,
            model,
            "preview",
            "previews");
    }

    private string? RepairThemeAsset(string? currentPath, string category, Guid instanceId)
    {
        if (string.IsNullOrWhiteSpace(currentPath)) return null;
        if (File.Exists(currentPath)) return currentPath;
        var root = Path.Combine(_paths.Themes, category);
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, $"{instanceId:N}.*", SearchOption.TopDirectoryOnly).FirstOrDefault();
    }

    private string? RepairCatalogArtworkAsset(
        string? currentPath,
        ServerInstance model,
        string assetStem,
        string cacheCategory)
    {
        var roots = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(model.DirectoryPath)
            && Path.IsPathFullyQualified(model.DirectoryPath))
        {
            roots.Add(Path.Combine(model.DirectoryPath, ".mcsv", "assets"));
        }

        // The content-addressed download cache may be shared by multiple instances. Persisting
        // its local result is safe because user overrides remain in IconImagePath and take
        // precedence. The legacy/category roots remain migration fallbacks for earlier previews.
        roots.Add(_paths.OnlineModpackArtworkCache);
        roots.Add(Path.Combine(_paths.Cache, "modpack-artwork", cacheCategory));
        roots.Add(Path.Combine(_paths.Cache, "modpack-artwork", model.Id.ToString("N")));

        if (TryNormalizeCatalogArtworkPath(currentPath, roots, out var current))
        {
            return current;
        }

        var fileName = GetSafeFileName(currentPath);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            if (fileName is not null)
            {
                var relocated = Path.Combine(root, fileName);
                if (TryNormalizeCatalogArtworkPath(relocated, [root], out var repaired))
                {
                    return repaired;
                }
            }

            foreach (var baseName in new[]
                     {
                         $"catalog-{assetStem}",
                         $"modpack-{assetStem}",
                         model.Id.ToString("N"),
                     })
            {
                try
                {
                    foreach (var candidate in Directory.EnumerateFiles(
                                 root,
                                 $"{baseName}.*",
                                 SearchOption.TopDirectoryOnly))
                    {
                        if (TryNormalizeCatalogArtworkPath(candidate, [root], out var repaired))
                        {
                            return repaired;
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or ArgumentException
                                                  or NotSupportedException)
                {
                    // Artwork is optional. A blocked or malformed cache must not prevent the
                    // server record from loading, and the original path is never deleted here.
                }
            }
        }

        return null;
    }

    private static bool TryNormalizeCatalogArtworkPath(
        string? path,
        IEnumerable<string> permittedRoots,
        out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(path);
            var extension = Path.GetExtension(candidate);
            if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var file = new FileInfo(candidate);
            if (!file.Exists || file.Length is <= 0 or > 64L * 1024 * 1024)
            {
                return false;
            }

            foreach (var rawRoot in permittedRoots)
            {
                var root = Path.GetFullPath(rawRoot);
                if (!Directory.Exists(root) || !SafePath.IsWithinRoot(root, candidate)) continue;
                SafePath.EnsureNoReparsePointsUnderRoot(root, candidate);
                normalized = candidate;
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            // Persisted catalog paths are untrusted input. Invalid entries degrade to the normal
            // core-initial fallback instead of blocking application startup.
        }

        return false;
    }

    private static string? GetSafeFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var fileName = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? NormalizeModpackProviderId(
        string? providerId,
        ModpackSourceKind legacySource)
    {
        var fallback = legacySource switch
        {
            ModpackSourceKind.Ftb => "ftb",
            ModpackSourceKind.Modrinth => "modrinth",
            ModpackSourceKind.CurseForge => "curseforge",
            _ => null,
        };
        var candidate = string.IsNullOrWhiteSpace(providerId)
            ? fallback
            : providerId.Trim().ToLowerInvariant();
        if (candidate is null
            || candidate.Length is < 1 or > 64
            || candidate.Any(character => !(character is >= 'a' and <= 'z'
                                              or >= '0' and <= '9'
                                              or '-' or '_' or '.')))
        {
            return fallback;
        }

        return candidate;
    }

    private async Task SaveSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _settingsSaveGate.WaitAsync(cancellationToken);
        try
        {
            await SaveSettingsLockedAsync(cancellationToken);
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    /// <summary>
    /// Saves while the caller owns <see cref="_settingsSaveGate"/>. Every writer receives a
    /// detached object graph because JsonSettingsStore serializes after its first asynchronous
    /// wait; passing the live graph would allow an unrelated UI mutation to change the file.
    /// </summary>
    private async Task SaveSettingsLockedAsync(CancellationToken cancellationToken = default)
    {
        _settings.SchemaVersion = Math.Max(
            _settings.SchemaVersion,
            ManagerSettings.CurrentSchemaVersion);
        if (_productServiceController is null)
        {
            _settings.Instances = Servers.Select(server => server.Model).ToList();
        }
        await _settingsStore.SaveAsync(PrepareSettingsForPersistence(), cancellationToken);
    }

    private ManagerSettings PrepareSettingsForPersistence()
    {
        if (_productServiceController is not null)
        {
            _settings.Instances = CloneServerInstances(_readOnlyLegacyInstances ?? []);
        }

        // JsonSettingsStore serializes asynchronously. Hand it an immutable-by-convention deep
        // snapshot so a concurrent UI edit cannot change the object graph while the stream is
        // being written.
        return CloneManagerSettings(_settings);
    }

    private static ManagerSettings CloneManagerSettings(ManagerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var payload = JsonSerializer.SerializeToUtf8Bytes(settings);
        return JsonSerializer.Deserialize<ManagerSettings>(payload)
               ?? throw new InvalidDataException(L("main.vm.error.settingsSnapshotCloneFailed"));
    }

    private static List<ServerInstance> CloneServerInstances(
        IEnumerable<ServerInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var payload = JsonSerializer.SerializeToUtf8Bytes(instances.ToArray());
        return JsonSerializer.Deserialize<List<ServerInstance>>(payload)
               ?? throw new InvalidDataException(L("main.vm.error.settingsSnapshotCloneFailed"));
    }

    private void ApplyNewServerDefaults(ServerInstance model)
    {
        var defaults = _settings.NewServerDefaults ?? new NewServerDefaultsSettings();
        model.MemoryAllocationMode = MemoryAllocationMode.UseManagerDefault;
        model.MinimumMemoryMb = defaults.MinimumMemoryMb;
        model.MaximumMemoryMb = defaults.MaximumMemoryMb;
        model.SeparateDiagnosticOutput = defaults.SeparateDiagnosticOutput;
        model.AutoRestart = defaults.AutoRestart;
        model.EnableHangWatchdog = defaults.EnableHangWatchdog;
        model.EnableAutomaticRecoveryPoints = defaults.EnableAutomaticRecoveryPoints;
    }

    private static void RepairManagerUiSettings(ManagerUiSettings settings)
    {
        settings.ThemePresetId = string.IsNullOrWhiteSpace(settings.ThemePresetId)
            ? ThemePresetCatalog.DefaultId
            : ThemePresetCatalog.GetOrDefault(settings.ThemePresetId).Id;
        settings.WindowWidth = double.IsFinite(settings.WindowWidth)
            ? Math.Clamp(
                settings.WindowWidth,
                ManagerUiSettings.MinimumPersistedWindowWidth,
                ManagerUiSettings.MaximumPersistedWindowWidth)
            : ManagerUiSettings.DefaultWindowWidth;
        settings.WindowHeight = double.IsFinite(settings.WindowHeight)
            ? Math.Clamp(
                settings.WindowHeight,
                ManagerUiSettings.MinimumPersistedWindowHeight,
                ManagerUiSettings.MaximumPersistedWindowHeight)
            : ManagerUiSettings.DefaultWindowHeight;
        settings.FontSize = double.IsFinite(settings.FontSize)
            ? Math.Clamp(settings.FontSize, 11, 20)
            : ManagerUiSettings.DefaultFontSize;
    }

    private static void RepairNewServerDefaults(NewServerDefaultsSettings settings)
    {
        // Safe migration: the old global Automatic strategy is no longer user-visible. Automatic
        // remains available only as an explicit per-server selection.
        settings.MemoryMode = MemoryAllocationMode.Manual;
        settings.MinimumMemoryMb = Math.Clamp(settings.MinimumMemoryMb, 512, 131072);
        settings.MaximumMemoryMb = Math.Clamp(settings.MaximumMemoryMb, settings.MinimumMemoryMb, 131072);
    }

    private static void RepairNewMinecraftClientDefaults(
        NewMinecraftClientDefaultsSettings settings)
    {
        settings.MemoryMode = settings.MemoryMode == MinecraftClientMemoryMode.Manual
            ? MinecraftClientMemoryMode.Manual
            : MinecraftClientMemoryMode.Automatic;
        settings.MinimumMemoryMb = Math.Clamp(settings.MinimumMemoryMb, 512, 32768);
        settings.MaximumMemoryMb = Math.Clamp(
            settings.MaximumMemoryMb,
            settings.MinimumMemoryMb,
            32768);
        settings.WindowWidth = Math.Clamp(settings.WindowWidth, 640, 16384);
        settings.WindowHeight = Math.Clamp(settings.WindowHeight, 360, 16384);
    }

    private static void ApplyFontResources(ResourceDictionary resources, double baseFontSize)
    {
        var normalized = Math.Clamp(double.IsFinite(baseFontSize) ? baseFontSize : 13, 11, 20);
        resources[ThemeResourceKeys.BaseFontSize] = normalized;
        resources[ThemeResourceKeys.SmallFontSize] = Math.Max(10, normalized - 2);
        resources[ThemeResourceKeys.SectionFontSize] = normalized + 5;
        resources[ThemeResourceKeys.TitleFontSize] = normalized + 8;
        resources[ThemeResourceKeys.ConsoleFontSize] = Math.Max(11, normalized);
    }

    private void OnConsoleLineReceived(object? sender, ConsoleLineReceivedEventArgs e)
    {
        if (_isDisposed) return;
        // Parsing is deliberately performed before entering the UI dispatcher. A noisy console
        // can enqueue thousands of visual log updates, while sparse presence changes must remain
        // responsive and must never require an active `list` query.
        var isServerOutput = e.Line.Stream is ConsoleStream.StandardOutput or ConsoleStream.StandardError;
        if (isServerOutput && MinecraftServerReadinessDetector.IsReadyLine(e.Line.Text))
        {
            MarkPendingModpackSessionHealthy(e.InstanceId, e.SessionId, "Minecraft Done");
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (isServerOutput
            && !_pendingSaveFlushes.IsEmpty
            && e.Line.Text.Length <= 4096
            && e.Line.Text.Contains("saved", StringComparison.OrdinalIgnoreCase)
            && SaveCompletionPattern.IsMatch(e.Line.Text)
            && _pendingSaveFlushes.TryGetValue((e.InstanceId, e.SessionId), out var saveCompletion))
        {
            saveCompletion.TrySetResult();
        }
        PlayerPresenceChange? presenceChange = null;
        var coreType = _playerPresenceCoreTypes.TryGetValue(e.InstanceId, out var configuredCoreType)
            ? configuredCoreType
            : CoreType.Unknown;
        if (isServerOutput
            && PlayerPresenceEventParser.TryParse(e.Line.Text, coreType, out var parsedPresenceChange))
        {
            presenceChange = parsedPresenceChange;
        }

        EnqueueConsoleLine(dispatcher, e);

        if (presenceChange is null) return;
        EnqueuePresenceChange(dispatcher, e.InstanceId, e.SessionId, presenceChange);
    }

    private void OnServerStateChanged(object? sender, ServerStateChangedEventArgs e)
    {
        ObservePendingModpackHealthStateChange(e);
        RegisterReliabilityStateChange(e);
        var sessionKey = (e.InstanceId, e.SessionId);

        if (e.State is ServerState.Starting or ServerState.Running)
        {
            // Update this snapshot before the UI callback is queued. Output can arrive on another
            // thread immediately after Process.Start; stale lines from an older session are then
            // discarded even if the visual console is still draining a bounded batch.
            _latestConsoleSessions[e.InstanceId] = e.SessionId;
            _playerPresenceBuffer.StartSession(e.InstanceId, e.SessionId);
            if (_pendingLaunchPorts.TryGetValue(e.InstanceId, out var launchedPort))
            {
                // Keep the exact immutable port selected for this process session. The editable
                // model may already contain settings intended for the next launch.
                _sessionLaunchPorts[sessionKey] = launchedPort;
                _pendingLaunchPortSessions[e.InstanceId] = e.SessionId;
            }
        }
        else
        {
            _playerPresenceBuffer.EndSession(e.InstanceId, e.SessionId);
            _pendingResourceSamples.TryRemove(e.InstanceId, out _);
            _sessionLaunchPorts.TryRemove(sessionKey, out _);
        }

        Application.Current?.Dispatcher.BeginInvoke(StateDispatcherPriority, () =>
        {
            var server = Servers.FirstOrDefault(item => item.Id == e.InstanceId);
            if (server is null) return;
            server.SetState(e.State);
            if (e.State is ServerState.Starting or ServerState.Running)
            {
                if (!_playerPresenceSessions.TryGetValue(e.InstanceId, out var activeSessionId)
                    || activeSessionId != e.SessionId)
                {
                    server.UpdateOnlinePlayers([]);
                }

                _playerPresenceSessions[e.InstanceId] = e.SessionId;
            }
            else
            {
                _playerPresenceSessions.Remove(e.InstanceId);
                server.UpdateOnlinePlayers([]);
            }

            if (e.State == ServerState.Running)
            {
                var activePort = _sessionLaunchPorts.TryGetValue(sessionKey, out var sessionPort)
                    ? sessionPort
                    : _pendingLaunchPorts.TryGetValue(server.Id, out var reservedPort)
                        ? reservedPort
                        : server.Port;
                server.MarkPortAsActive(activePort);
                ReleasePendingLaunchPort(server.Id, e.SessionId);
            }
            else if (e.State is ServerState.Stopped or ServerState.Crashed or ServerState.Faulted)
            {
                ReleasePendingLaunchPort(server.Id, e.SessionId);
            }
            if (e.Error is not null)
            {
                server.AppendConsole(SystemConsoleLineFactory.Create(
                    e.InstanceId,
                    e.Error.Message,
                    ConsoleLineSeverity.Error));
            }
            if ((e.State is ServerState.Crashed or ServerState.Faulted)
                && _crashPlans.TryGetValue(e.InstanceId, out var crashPlan)
                && crashPlan.SessionId == e.SessionId)
            {
                server.AppendConsole(SystemConsoleLineFactory.Create(
                    e.InstanceId,
                    server.AutoRestart
                        ? crashPlan.Decision.Message
                        : L("main.vm.console.crashDiagnosticAutoRestartDisabled"),
                    ConsoleLineSeverity.Warning));
            }
            SetStatus("main.vm.status.serverState", server.Name, server.StateText);
            OnPropertyChanged(nameof(RunningSummary));
            OnPropertyChanged(nameof(HasRunningServers));
            if (ReferenceEquals(server, SelectedServer))
            {
                UpdateSelectedModpackCommand.NotifyCanExecuteChanged();
            }
        });
    }

    private void RegisterReliabilityStateChange(ServerStateChangedEventArgs eventArgs)
    {
        var key = (eventArgs.InstanceId, eventArgs.SessionId);
        var now = DateTimeOffset.UtcNow;
        if (eventArgs.State == ServerState.Starting)
        {
            _sessionStartedAt[key] = now;
            _watchdogState.StartSession(eventArgs.InstanceId, eventArgs.SessionId, now);
            _crashPlans.TryRemove(eventArgs.InstanceId, out _);
            return;
        }

        if (eventArgs.State == ServerState.Running)
        {
            var startedAt = _sessionStartedAt.GetOrAdd(key, now);
            _sessionsThatReachedRunning[key] = 0;
            _watchdogState.StartSession(eventArgs.InstanceId, eventArgs.SessionId, startedAt);
            _watchdogTasks.GetOrAdd(
                key,
                _ => RunWatchdogSessionAsync(eventArgs.InstanceId, eventArgs.SessionId));
            _recoveryPointTasks.GetOrAdd(
                key,
                _ => RunRecoveryPointSessionAsync(eventArgs.InstanceId, eventArgs.SessionId));
            return;
        }

        _watchdogState.EndSession(eventArgs.InstanceId, eventArgs.SessionId);
        if (eventArgs.State is not (ServerState.Stopped or ServerState.Crashed or ServerState.Faulted))
        {
            return;
        }

        var reachedRunning = _sessionsThatReachedRunning.TryRemove(key, out _);
        var started = _sessionStartedAt.TryRemove(key, out var recordedStart)
            ? recordedStart
            : now;
        if (eventArgs.State is not (ServerState.Crashed or ServerState.Faulted))
        {
            return;
        }

        if (_crashPlans.TryGetValue(eventArgs.InstanceId, out var existing)
            && existing.SessionId == eventArgs.SessionId)
        {
            if (eventArgs.State == ServerState.Faulted && eventArgs.Error is not null)
            {
                _crashPlans[eventArgs.InstanceId] = existing with
                {
                    Decision = new CrashRestartDecision(
                        false,
                        TimeSpan.Zero,
                        existing.Decision.CrashesInWindow,
                        L("main.vm.console.autoRestartPreparationFailed"))
                };
            }
            return;
        }

        var uptime = now > started ? now - started : TimeSpan.Zero;
        var decision = eventArgs.State == ServerState.Faulted && !reachedRunning
            ? new CrashRestartDecision(
                false,
                TimeSpan.Zero,
                0,
                L("main.vm.console.javaFailedBeforeRunning"))
            : _crashRestartLimiter.RecordCrash(eventArgs.InstanceId, now, uptime);
        var plan = new CrashSessionPlan(eventArgs.SessionId, decision, eventArgs.State.ToString());
        _crashPlans[eventArgs.InstanceId] = plan;
        if (_instanceModels.TryGetValue(eventArgs.InstanceId, out var instance))
        {
            var reportTask = _crashReportTasks.GetOrAdd(
                key,
                _ => CreateCrashReportSafeAsync(
                    instance,
                    eventArgs.SessionId,
                    eventArgs.State == ServerState.Crashed ? "ProcessCrashed" : "ProcessMonitorFaulted",
                    eventArgs.ExitCode,
                    eventArgs.Error,
                    started,
                    _sessionServicesCancellation.Token));
            _ = RemoveCrashReportTaskWhenCompleteAsync(key, reportTask);
        }
    }

    private async Task CreateCrashReportSafeAsync(
        ServerInstance instance,
        Guid sessionId,
        string trigger,
        int? exitCode,
        Exception? error,
        DateTimeOffset sessionStartedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var latestLog = await ReadSafeServerTailAsync(
                    instance.DirectoryPath,
                    Path.Combine("logs", "latest.log"),
                    1024 * 1024,
                    cancellationToken)
                .ConfigureAwait(false);
            var nativeCrashReport = await ReadNewestNativeCrashReportAsync(
                    instance.DirectoryPath,
                    sessionStartedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            var consoleLines = _processManager.GetRecentConsoleLines(instance.Id);
            _lastHealthyRecoveryPoints.TryGetValue(instance.Id, out var recoveryPoint);
            var artifacts = await _crashDiagnosticService.CreateReportAsync(
                    _paths.CrashReports,
                    new CrashDiagnosticInput(
                        instance,
                        sessionId,
                        DateTimeOffset.UtcNow,
                        trigger,
                        exitCode,
                        error,
                        consoleLines,
                        latestLog,
                        nativeCrashReport,
                        recoveryPoint),
                    cancellationToken)
                .ConfigureAwait(false);
            PostSystemMessage(
                instance.Id,
                L("main.vm.console.crashDiagnosticCreated", artifacts.MarkdownPath),
                ConsoleLineSeverity.Information);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown intentionally cancels report generation.
        }
        catch (Exception reportError) when (reportError is not OutOfMemoryException)
        {
            PostSystemMessage(
                instance.Id,
                L("main.vm.console.crashDiagnosticFailed", reportError.Message),
                ConsoleLineSeverity.Error);
        }
    }

    private async Task RunWatchdogSessionAsync(Guid instanceId, Guid sessionId)
    {
        var key = (instanceId, sessionId);
        var cancellationToken = _sessionServicesCancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_instanceModels.TryGetValue(instanceId, out var instance)
                    || !instance.EnableHangWatchdog)
                {
                    return;
                }

                var interval = TimeSpan.FromSeconds(
                    Math.Clamp(instance.WatchdogCheckIntervalSeconds, 10, 300));
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                if (!IsCurrentRunningSession(instanceId, sessionId)) return;

                var endpoint = await ReadStatusEndpointAsync(instance, cancellationToken).ConfigureAwait(false);
                if (!endpoint.StatusEnabled)
                {
                    PostSystemMessage(
                        instanceId,
                        L("main.vm.console.watchdogStatusDisabled"),
                        ConsoleLineSeverity.Warning);
                    return;
                }

                var timeout = TimeSpan.FromSeconds(
                    Math.Clamp(instance.WatchdogProbeTimeoutSeconds, 2, 30));
                var activePort = _sessionLaunchPorts.TryGetValue(key, out var sessionPort)
                    ? sessionPort
                    : instance.Port;
                var result = await _minecraftStatusProbe.ProbeAsync(
                        endpoint.Host,
                        activePort,
                        timeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                var policy = new ServerWatchdogPolicy(
                    TimeSpan.FromSeconds(Math.Clamp(instance.WatchdogStartupGraceSeconds, 30, 3600)),
                    Math.Clamp(instance.WatchdogFailureThreshold, 2, 10));
                var observation = _watchdogState.Record(
                    instanceId,
                    sessionId,
                    DateTimeOffset.UtcNow,
                    result.IsHealthy,
                    policy,
                    result.Error);
                if (!observation.IsCurrentSession) return;
                if (!observation.IsHealthy && !observation.IsInsideStartupGrace)
                {
                    PostSystemMessage(
                        instanceId,
                        L(
                            "main.vm.console.watchdogProbeFailed",
                            observation.ConsecutiveFailures,
                            policy.ConsecutiveFailureThreshold,
                            result.Error ?? L("main.vm.watchdog.noResponse")),
                        ConsoleLineSeverity.Warning);
                }

                if (observation.ShouldRestart)
                {
                    await RecoverUnresponsiveServerAsync(instance, sessionId, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown cancels the monitor.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            PostSystemMessage(
                instanceId,
                L("main.vm.console.watchdogFailed", error.Message),
                ConsoleLineSeverity.Error);
        }
        finally
        {
            _watchdogTasks.TryRemove(key, out _);
        }
    }

    private async Task RecoverUnresponsiveServerAsync(
        ServerInstance instance,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var key = (instance.Id, sessionId);
        if (!_watchdogRecoveryInProgress.TryAdd(key, 0)) return;
        try
        {
            if (!IsCurrentRunningSession(instance.Id, sessionId)) return;
            var restartIntentEpoch = _manualStopEpochs.GetValueOrDefault(instance.Id);
            var startedAt = _sessionStartedAt.TryGetValue(key, out var recordedStart)
                ? recordedStart
                : DateTimeOffset.UtcNow;
            PostSystemMessage(
                instance.Id,
                L("main.vm.console.watchdogRecovering"),
                ConsoleLineSeverity.Warning);
            var stopResult = await StopServerDetailedCoordinatedAsync(
                    instance.Id,
                    TimeSpan.FromSeconds(30),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!stopResult.WasRunning || stopResult.SessionId != sessionId) return;

            var now = DateTimeOffset.UtcNow;
            var uptime = now > startedAt ? now - startedAt : TimeSpan.Zero;
            var decision = _crashRestartLimiter.RecordCrash(instance.Id, now, uptime);
            _crashPlans[instance.Id] = new CrashSessionPlan(sessionId, decision, "StatusWatchdog");
            var trigger = stopResult.Mode == ServerStopMode.Graceful
                ? "StatusWatchdog-GracefulStop"
                : "StatusWatchdog-ForcedTermination";
            var reportTask = _crashReportTasks.GetOrAdd(
                key,
                _ => CreateCrashReportSafeAsync(
                    instance,
                    sessionId,
                    trigger,
                    null,
                    new TimeoutException("Minecraft status protocol failed the configured consecutive-health threshold."),
                    startedAt,
                    cancellationToken));
            _ = RemoveCrashReportTaskWhenCompleteAsync(key, reportTask);
            await reportTask.ConfigureAwait(false);

            PostSystemMessage(
                instance.Id,
                stopResult.Mode == ServerStopMode.Graceful
                    ? L("main.vm.console.watchdogStoppedGracefully")
                    : L("main.vm.console.watchdogForcedTermination"),
                stopResult.Mode == ServerStopMode.Graceful
                    ? ConsoleLineSeverity.Information
                    : ConsoleLineSeverity.Warning);
            if (!decision.ShouldRestart)
            {
                PostSystemMessage(
                    instance.Id,
                    decision.Message,
                    ConsoleLineSeverity.Warning);
                return;
            }

            PostSystemMessage(
                instance.Id,
                decision.Message,
                ConsoleLineSeverity.Warning);
            await Task.Delay(decision.Delay, cancellationToken).ConfigureAwait(false);
            await TryStartProcessCoordinatedAsync(
                    instance,
                    () => instance.EnableHangWatchdog
                          && _manualStopEpochs.GetValueOrDefault(instance.Id) == restartIntentEpoch
                          && CanRestartStoppedSession(instance.Id, sessionId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (!CanRestartStoppedSession(instance.Id, sessionId))
        {
            // A manual replacement session won the race; never disturb it.
        }
        finally
        {
            _watchdogRecoveryInProgress.TryRemove(key, out _);
        }
    }

    private bool IsCurrentRunningSession(Guid instanceId, Guid sessionId)
        => _processManager.TryGetSnapshot(instanceId, out var snapshot)
            && snapshot.SessionId == sessionId
            && snapshot.State == ServerState.Running;

    private bool CanRestartStoppedSession(Guid instanceId, Guid previousSessionId)
        => _processManager.TryGetSnapshot(instanceId, out var snapshot)
            && snapshot.SessionId == previousSessionId
            && snapshot.State is ServerState.Stopped or ServerState.Crashed or ServerState.Faulted;

    private void InvalidateAutomaticRestartIntent(Guid instanceId)
        => _manualStopEpochs.AddOrUpdate(instanceId, 1, static (_, current) => unchecked(current + 1));

    private async Task<StatusEndpoint> ReadStatusEndpointAsync(
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        var propertiesPath = Path.Combine(instance.DirectoryPath, "server.properties");
        if (!File.Exists(propertiesPath)) return new StatusEndpoint("127.0.0.1", true);
        var document = await _serverPropertiesPortService.ReadDocumentAsync(propertiesPath, cancellationToken)
            .ConfigureAwait(false);
        if (document is null) return new StatusEndpoint("127.0.0.1", true);
        var host = ReadJavaProperty(document.Text, "server-ip");
        var status = ReadJavaProperty(document.Text, "enable-status");
        if (string.IsNullOrWhiteSpace(host)
            || host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::", StringComparison.OrdinalIgnoreCase))
        {
            host = "127.0.0.1";
        }

        host = host.Trim().Trim('[', ']');
        return new StatusEndpoint(
            host,
            !string.Equals(status?.Trim(), "false", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadJavaProperty(string contents, string key)
    {
        string? result = null;
        foreach (var rawLine in contents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimStart();
            if (line.Length == 0 || line[0] is '#' or '!') continue;
            var separator = line.IndexOfAny(['=', ':']);
            if (separator < 0) continue;
            if (!line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            result = line[(separator + 1)..].Trim();
        }

        return result;
    }

    private async Task RunRecoveryPointSessionAsync(Guid instanceId, Guid sessionId)
    {
        var key = (instanceId, sessionId);
        var cancellationToken = _sessionServicesCancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_instanceModels.TryGetValue(instanceId, out var instance)
                    || !instance.EnableAutomaticRecoveryPoints)
                {
                    return;
                }

                var delay = TimeSpan.FromMinutes(
                    Math.Clamp(instance.RecoveryPointIntervalMinutes, 10, 1440));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (!IsCurrentRunningSession(instanceId, sessionId)) return;
                await CreateHealthyRecoveryPointAsync(instance, sessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown cancels scheduled recovery points.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            PostSystemMessage(
                instanceId,
                L("main.vm.console.recoveryPointFailed", error.Message),
                ConsoleLineSeverity.Error);
        }
        finally
        {
            _recoveryPointTasks.TryRemove(key, out _);
        }
    }

    private async Task CreateHealthyRecoveryPointAsync(
        ServerInstance instance,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var gate = _backupGates.GetOrAdd(instance.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var savesPaused = false;
        Exception? operationError = null;
        try
        {
            if (_lifecycleTransitions.ContainsKey(instance.Id)) return;
            if (!IsCurrentRunningSession(instance.Id, sessionId)) return;
            savesPaused = true;
            var flushConfirmed = await FlushAndPauseServerSavesAsync(instance.Id, sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (!flushConfirmed)
            {
                PostSystemMessage(
                    instance.Id,
                    L("main.vm.console.recoveryPointFlushTimeout"),
                    ConsoleLineSeverity.Warning);
                return;
            }

            var destination = SafePath.CombineUnderRoot(_paths.RecoveryPoints, instance.Id.ToString("N"));
            Directory.CreateDirectory(destination);
            SafePath.EnsureNoReparsePointsUnderRoot(_paths.Root, destination);
            var result = await _backupService.CreateBackupAsync(
                    instance,
                    CreateBackupOptions(
                        instance,
                        destination,
                        $"recovery-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip",
                        CompressionLevel.Fastest),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!IsCurrentRunningSession(instance.Id, sessionId))
            {
                DeleteInvalidatedBackup(result.ArchivePath);
                throw new InvalidOperationException(
                    L("main.vm.error.recoveryPointSessionChanged"));
            }

            _lastHealthyRecoveryPoints[instance.Id] = result.ArchivePath;
            PruneRecoveryPoints(
                destination,
                Math.Clamp(instance.RecoveryPointRetentionCount, 1, 20));
            PostSystemMessage(
                instance.Id,
                L("main.vm.console.recoveryPointCreated", result.ArchivePath),
                ConsoleLineSeverity.Information);
        }
        catch (Exception error)
        {
            operationError = error;
            throw;
        }
        finally
        {
            try
            {
                if (savesPaused && IsCurrentRunningSession(instance.Id, sessionId))
                {
                    await SendCommandOwnedAsync(instance.Id, "save-on", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception saveOnError) when (saveOnError is not OutOfMemoryException)
            {
                PostSystemMessage(
                    instance.Id,
                    L("main.vm.console.recoveryPointSaveOnFailed", saveOnError.Message),
                    ConsoleLineSeverity.Warning);
                if (operationError is null)
                {
                    throw;
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task<bool> FlushAndPauseServerSavesAsync(
        Guid instanceId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var key = (instanceId, sessionId);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingSaveFlushes.TryAdd(key, completion))
        {
            throw new InvalidOperationException(L("main.vm.error.saveFlushAlreadyPending"));
        }

        try
        {
            await SendCommandOwnedAsync(instanceId, "save-off", cancellationToken)
                .ConfigureAwait(false);
            await SendCommandOwnedAsync(instanceId, "save-all flush", cancellationToken)
                .ConfigureAwait(false);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            _pendingSaveFlushes.TryRemove(key, out _);
        }
    }

    private BackupOptions CreateBackupOptions(
        ServerInstance instance,
        string? destinationDirectory = null,
        string? archiveFileName = null,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        var excludedDirectories = new List<string> { "backups", "cache" };
        var excludedFiles = new List<string>();
        if (PathsEqual(instance.DirectoryPath, _paths.Root))
        {
            excludedDirectories.AddRange(["runtimes", "servers", "themes", "crash-reports"]);
            excludedFiles.AddRange(
            [
                "Muhun MCSV Manager 0.5.0 Preview 9.exe",
                "Muhun MCSV Manager 0.5.0-preview.9.exe",
                "Muhun MCSV Manager 0.5.0 Preview 8.exe",
                "Muhun MCSV Manager 0.5.0-preview.8.exe",
                "Muhun MCSV Manager 0.5.0 Preview 7.exe",
                "Muhun MCSV Manager 0.5.0-preview.7.exe",
                "Muhun MCSV Manager 0.5.0 Preview 6.exe",
                "Muhun MCSV Manager 0.5.0-preview.6.exe",
                Path.GetFileName(Environment.ProcessPath ?? string.Empty),
                "Muhun MCSV Manager 0.4.11 Remote Preview 4.exe",
                "Muhun MCSV Manager 0.4.11 Remote Preview 3.exe",
                "Muhun MCSV Manager 0.4.11 Remote Preview 2.exe",
                "Muhun MCSV Manager 0.4.11 Remote Preview 1.exe",
                "Muhun MCSV Manager 0.4.10.exe",
                "Muhun MCSV Manager 0.4.9.exe",
                "Muhun MCSV Manager 0.5.0 Preview 5.exe",
                "Muhun MCSV Manager 0.5.0-preview.5.exe",
                "Muhun MCSV Manager 0.5.0 Preview 4.exe",
                "Muhun MCSV Manager 0.5.0-preview.4.exe",
                "Muhun MCSV Manager 0.5.0 Preview 3.exe",
                "Muhun MCSV Manager 0.5.0-preview.3.exe",
                "Muhun MCSV Manager 0.5.0 Preview 2.exe",
                "Muhun MCSV Manager 0.5.0-preview.2.exe",
                "Muhun MCSV Manager 0.5.0 Preview 1.exe",
                "Muhun MCSV Manager 0.5.0-preview.1.exe",
                "Muhun MCSV Manager 0.4.8.exe",
                "Muhun MCSV Manager 0.4.7.exe",
                "Muhun MCSV Manager 0.4.6.exe",
                "Muhun MCSV Manager 0.4.5.exe",
                "Muhun MCSV Manager 0.4.4.exe",
                "Muhun MCSV Manager 0.4.3.exe",
                "Muhun MCSV Manager 0.4.2.exe",
                "Muhun MCSV Manager 0.4.1.exe",
                "Muhun MCSV Manager 0.4.0.exe",
                "Muhun MCSV Manager 0.3.1.exe",
                "Muhun MCSV Manager 0.3.0.exe",
                "Muhun MCSV Manager 0.2.5.exe",
                "MinecraftServerManager.exe",
                "manager.json",
                "remote-security.dat",
                SingleInstanceGuard.LockFileName,
                "smoke-test-error.txt"
            ]);
        }

        return new BackupOptions
        {
            DestinationDirectory = destinationDirectory,
            ArchiveFileName = archiveFileName,
            CompressionLevel = compressionLevel,
            ExcludedDirectoryNames = excludedDirectories,
            ExcludedFileNames = excludedFiles,
            ExcludedFileNamePrefixes = PathsEqual(instance.DirectoryPath, _paths.Root)
                ? [".remote-security.dat."]
                : []
        };
    }

    private static void PruneRecoveryPoints(string destinationDirectory, int retentionCount)
    {
        var root = Path.GetFullPath(destinationDirectory);
        var files = Directory.EnumerateFiles(root, "recovery-*.zip", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(retentionCount)
            .ToArray();
        foreach (var file in files)
        {
            SafePath.EnsureWithinRoot(root, file.FullName);
            SafePath.EnsureNoReparsePointsUnderRoot(root, file.FullName);
            file.Delete();
        }
    }

    private static void DeleteInvalidatedBackup(string archivePath)
    {
        try
        {
            File.Delete(archivePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                L("main.vm.console.invalidBackupDeleteFailed", archivePath),
                error);
        }
    }

    private void PostSystemMessage(
        Guid instanceId,
        string message,
        ConsoleLineSeverity severity)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Servers.FirstOrDefault(server => server.Id == instanceId)?.AppendConsole(
                SystemConsoleLineFactory.Create(instanceId, message, severity));
            StatusMessage = message;
        });
    }

    private async Task RemoveCrashReportTaskWhenCompleteAsync(
        (Guid InstanceId, Guid SessionId) key,
        Task reportTask)
    {
        try
        {
            await reportTask.ConfigureAwait(false);
        }
        finally
        {
            _crashReportTasks.TryRemove(
                new KeyValuePair<(Guid InstanceId, Guid SessionId), Task>(key, reportTask));
        }
    }

    private static async Task<string?> ReadNewestNativeCrashReportAsync(
        string serverDirectory,
        DateTimeOffset notBeforeUtc,
        CancellationToken cancellationToken)
    {
        var crashRoot = Path.Combine(Path.GetFullPath(serverDirectory), "crash-reports");
        if (!Directory.Exists(crashRoot)) return null;
        try
        {
            SafePath.EnsureNoReparsePointsUnderRoot(serverDirectory, crashRoot);
        }
        catch (Exception error) when (
            error is InvalidDataException or UnauthorizedAccessException or IOException)
        {
            return null;
        }

        var oldestAcceptedWrite = notBeforeUtc.UtcDateTime - TimeSpan.FromSeconds(5);
        var candidates = new List<(string Path, DateTime LastWriteUtc)>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         crashRoot,
                         "*.txt",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    SafePath.EnsureNoReparsePointsUnderRoot(serverDirectory, path);
                    var lastWrite = File.GetLastWriteTimeUtc(path);
                    if (lastWrite >= oldestAcceptedWrite)
                    {
                        candidates.Add((path, lastWrite));
                    }
                }
                catch (Exception error) when (
                    error is InvalidDataException or UnauthorizedAccessException or IOException)
                {
                    // One suspicious/unreadable candidate must not suppress the complete crash
                    // report. It is omitted while other bounded, session-current files remain
                    // eligible.
                    // Continue with other crash reports from the same current session.
                }
            }
        }
        catch (Exception error) when (
            error is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            return null;
        }

        foreach (var candidate in candidates.OrderByDescending(item => item.LastWriteUtc))
        {
            try
            {
                // Re-check immediately before opening: a file that was safe during enumeration
                // can be replaced by a junction/symlink before this read.
                SafePath.EnsureNoReparsePointsUnderRoot(serverDirectory, candidate.Path);
                var contents = await ReadBoundedTailAsync(
                            candidate.Path,
                            1024 * 1024,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (contents is not null)
                {
                    return contents;
                }
            }
            catch (Exception error) when (
                error is InvalidDataException
                    or FileNotFoundException
                    or DirectoryNotFoundException
                    or UnauthorizedAccessException
                    or IOException)
            {
                // A candidate can disappear or become locked between enumeration and reading.
                // Try the next session-current file instead of discarding the entire report.
            }
        }

        return null;
    }

    private static async Task<string?> ReadBoundedTailAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytesToRead = checked((int)Math.Min(stream.Length, maximumBytes));
        if (stream.Length > bytesToRead) stream.Seek(-bytesToRead, SeekOrigin.End);
        var bytes = new byte[bytesToRead];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static Task<string?> ReadSafeServerTailAsync(
        string serverDirectory,
        string relativePath,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(serverDirectory);
        var path = SafePath.CombineUnderRoot(root, relativePath);
        if (!File.Exists(path)) return Task.FromResult<string?>(null);
        SafePath.EnsureNoReparsePointsUnderRoot(root, path);
        return ReadBoundedTailAsync(path, maximumBytes, cancellationToken);
    }

    private void OnResourceSampled(object? sender, ServerResourceSampledEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        _pendingResourceSamples[e.InstanceId] = e.Sample;
        if (_scheduledResourceSampleDrains.TryAdd(e.InstanceId, 0))
        {
            dispatcher.BeginInvoke(
                () => DrainResourceSample(dispatcher, e.InstanceId),
                DispatcherPriority.Background);
        }
    }

    private void DrainResourceSample(Dispatcher dispatcher, Guid instanceId)
    {
        if (_pendingResourceSamples.TryRemove(instanceId, out var sample)
            && _latestConsoleSessions.TryGetValue(instanceId, out var currentSessionId)
            && currentSessionId == sample.SessionId)
        {
            var server = Servers.FirstOrDefault(item => item.Id == instanceId);
            if (server?.State == ServerState.Running)
            {
                server.UpdateMetrics(
                    sample.CpuPercent,
                    sample.WorkingSetBytes,
                    sample.Uptime);
            }
        }

        _scheduledResourceSampleDrains.TryRemove(instanceId, out _);
        // Close the producer/drain race: a sampler can replace the latest value immediately
        // before the scheduled marker is removed. Only one replacement callback is required.
        if (_pendingResourceSamples.ContainsKey(instanceId)
            && _scheduledResourceSampleDrains.TryAdd(instanceId, 0))
        {
            dispatcher.BeginInvoke(
                () => DrainResourceSample(dispatcher, instanceId),
                DispatcherPriority.Background);
        }
    }

    internal int GetPendingResourceSampleCount(Guid instanceId)
        => _pendingResourceSamples.ContainsKey(instanceId) ? 1 : 0;

    internal bool HasScheduledResourceSampleDrain(Guid instanceId)
        => _scheduledResourceSampleDrains.ContainsKey(instanceId);

    private void EnqueueConsoleLine(Dispatcher dispatcher, ConsoleLineReceivedEventArgs eventArgs)
    {
        var queue = _pendingConsoleLines.GetOrAdd(
            eventArgs.InstanceId,
            static _ => new BoundedDropOldestQueue<PendingConsoleLine>(MaximumPendingConsoleLinesPerInstance));
        queue.Enqueue(new PendingConsoleLine(eventArgs.SessionId, eventArgs.Line));
        if (_scheduledConsoleDrains.TryAdd(eventArgs.InstanceId, 0))
        {
            ScheduleConsoleDrain(dispatcher, eventArgs.InstanceId, queue);
        }
    }

    private void ScheduleConsoleDrain(
        Dispatcher dispatcher,
        Guid instanceId,
        BoundedDropOldestQueue<PendingConsoleLine> queue)
    {
        if (_isDisposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            _scheduledConsoleDrains.TryRemove(instanceId, out _);
            return;
        }

        _ = ScheduleConsoleDrainAsync(dispatcher, instanceId, queue);
    }

    private async Task ScheduleConsoleDrainAsync(
        Dispatcher dispatcher,
        Guid instanceId,
        BoundedDropOldestQueue<PendingConsoleLine> queue)
    {
        try
        {
            // Human-readable console output does not need a render for every process line. A
            // fixed refresh cadence coalesces sustained startup bursts and leaves WPF input and
            // rendering time between projection resets. The process reader and JVM are never
            // delayed by this UI throttle; only the bounded visual snapshot is deferred.
            await Task.Delay(ConsoleUiRefreshInterval, _sessionServicesCancellation.Token)
                .ConfigureAwait(false);
            if (_isDisposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                _scheduledConsoleDrains.TryRemove(instanceId, out _);
                return;
            }

            _ = dispatcher.BeginInvoke(
                () => DrainConsoleLines(dispatcher, instanceId, queue),
                DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            _scheduledConsoleDrains.TryRemove(instanceId, out _);
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
            // A final process-output callback can race disposal after event handlers are detached.
            // Treat it exactly like cancellation; never leave a fire-and-forget fault behind.
            _scheduledConsoleDrains.TryRemove(instanceId, out _);
        }
        catch (InvalidOperationException) when (
            _isDisposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            _scheduledConsoleDrains.TryRemove(instanceId, out _);
        }
    }

    private void DrainConsoleLines(
        Dispatcher dispatcher,
        Guid instanceId,
        BoundedDropOldestQueue<PendingConsoleLine> queue)
    {
        if (!_pendingConsoleLines.TryGetValue(instanceId, out var currentQueue)
            || !ReferenceEquals(currentQueue, queue))
        {
            _scheduledConsoleDrains.TryRemove(instanceId, out _);
            return;
        }

        var server = Servers.FirstOrDefault(item => item.Id == instanceId);
        var batch = queue.Take(ConsoleDrainBatchSize);
        if (server is not null && _latestConsoleSessions.TryGetValue(instanceId, out var currentSessionId))
        {
            server.AppendConsoleBatch(batch
                .Where(item => item.SessionId == currentSessionId)
                .Select(item => item.Line));
        }

        if (queue.Count > 0)
        {
            ScheduleConsoleDrain(dispatcher, instanceId, queue);
            return;
        }

        _scheduledConsoleDrains.TryRemove(instanceId, out _);
        // Close the enqueue/remove race: an output thread may have observed the old scheduled
        // marker just before it was removed and therefore relied on this drain to reschedule.
        if (queue.Count > 0 && _scheduledConsoleDrains.TryAdd(instanceId, 0))
        {
            ScheduleConsoleDrain(dispatcher, instanceId, queue);
        }
    }

    internal int GetPendingConsoleLineCount(Guid instanceId)
        => _pendingConsoleLines.TryGetValue(instanceId, out var queue) ? queue.Count : 0;

    internal bool HasScheduledConsoleDrain(Guid instanceId)
        => _scheduledConsoleDrains.ContainsKey(instanceId);

    private void EnqueuePresenceChange(
        Dispatcher dispatcher,
        Guid instanceId,
        Guid sessionId,
        PlayerPresenceChange change)
    {
        if (!_playerPresenceBuffer.Apply(instanceId, sessionId, change))
        {
            return;
        }

        if (_scheduledPresenceDrains.TryAdd(instanceId, 0))
        {
            SchedulePresenceDrain(dispatcher, instanceId);
        }
    }

    private void SchedulePresenceDrain(Dispatcher dispatcher, Guid instanceId)
    {
        if (_isDisposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            _scheduledPresenceDrains.TryRemove(instanceId, out _);
            return;
        }

        _ = SchedulePresenceDrainAsync(dispatcher, instanceId);
    }

    private async Task SchedulePresenceDrainAsync(Dispatcher dispatcher, Guid instanceId)
    {
        try
        {
            // Join/leave events remain authoritative in the thread-safe buffer immediately. Only
            // the visual projection is rate-limited so a login burst cannot monopolize WPF.
            await Task.Delay(PresenceUiRefreshInterval, _sessionServicesCancellation.Token)
                .ConfigureAwait(false);
            if (_isDisposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                _scheduledPresenceDrains.TryRemove(instanceId, out _);
                return;
            }

            _ = dispatcher.BeginInvoke(
                () => DrainPresenceChanges(dispatcher, instanceId),
                PresenceDispatcherPriority);
        }
        catch (OperationCanceledException) when (_sessionServicesCancellation.IsCancellationRequested)
        {
            _scheduledPresenceDrains.TryRemove(instanceId, out _);
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
            _scheduledPresenceDrains.TryRemove(instanceId, out _);
        }
        catch (InvalidOperationException) when (
            _isDisposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            _scheduledPresenceDrains.TryRemove(instanceId, out _);
        }
    }

    private void DrainPresenceChanges(Dispatcher dispatcher, Guid instanceId)
    {
        var snapshot = _playerPresenceBuffer.Capture(instanceId);
        if (snapshot is not null)
        {
            var server = Servers.FirstOrDefault(item => item.Id == instanceId);
            if (server is not null
                && IsCurrentPlayerPresenceSession(server, instanceId, snapshot.SessionId))
            {
                server.UpdateOnlinePlayers(snapshot.OnlinePlayers);
            }
        }

        if (snapshot is not null
            && _playerPresenceBuffer.HasChangedSince(instanceId, snapshot.SessionId, snapshot.Version))
        {
            SchedulePresenceDrain(dispatcher, instanceId);
            return;
        }

        _scheduledPresenceDrains.TryRemove(instanceId, out _);
        // Close the same producer/drain race as the console queue. If a new event observed the
        // old marker immediately before removal, its version differs from the captured snapshot.
        var latest = _playerPresenceBuffer.Capture(instanceId);
        var hasNewState = latest is not null
            && (snapshot is null
                || latest.SessionId != snapshot.SessionId
                || latest.Version != snapshot.Version);
        if (hasNewState && _scheduledPresenceDrains.TryAdd(instanceId, 0))
        {
            SchedulePresenceDrain(dispatcher, instanceId);
        }
    }

    internal int GetBufferedOnlinePlayerCount(Guid instanceId)
        => _playerPresenceBuffer.Capture(instanceId)?.OnlinePlayers.Count ?? 0;

    internal bool HasScheduledPresenceDrain(Guid instanceId)
        => _scheduledPresenceDrains.ContainsKey(instanceId);

    private async Task GuardAsync(Func<Task> operation, string operationNameKey)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            SetStatus("main.vm.operation.cancelled", L(operationNameKey));
        }
        catch (Exception exception)
        {
            var operationName = L(operationNameKey);
            SetStatus("main.vm.operation.failed", operationName, exception.Message);
            DarkMessageBox.Show(
                Application.Current.MainWindow,
                exception.Message,
                L("main.vm.operation.failedTitle", operationName),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SetStatus(string key, params object?[] arguments)
    {
        _statusMessageKey = key;
        _statusMessageArguments = arguments.ToArray();
        SetProperty(ref _statusMessage, L(key, arguments), nameof(StatusMessage));
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);

    internal async Task<PortAssignment> AssignAvailablePortAsync(
        string directoryPath,
        Guid instanceId,
        int? requestedPort = null,
        int? ownedActivePort = null,
        bool reserveForLaunch = false,
        ServerInstance? launchConfiguration = null,
        CancellationToken cancellationToken = default)
    {
        await _portAssignmentGate.WaitAsync(cancellationToken);
        try
        {
            var instance = launchConfiguration;
            if (instance is null)
            {
                _instanceModels.TryGetValue(instanceId, out instance);
            }

            var usesVelocityArguments = instance?.CoreType == CoreType.Velocity;
            var propertiesPath = Path.Combine(directoryPath, "server.properties");
            int? configuredPort;
            if (usesVelocityArguments)
            {
                instance!.ServerArguments ??= [];
                configuredPort = VelocityPortArgumentEditor.TryReadPort(
                    instance.ServerArguments,
                    out var velocityPort)
                    ? velocityPort
                    : instance.Port is >= 1 and <= ServerPortAllocator.MaximumPort
                        ? instance.Port
                        : null;
            }
            else
            {
                configuredPort = await _serverPropertiesPortService.ReadServerPortAsync(
                    propertiesPath,
                    cancellationToken);
            }
            // Automatic allocation is a launch-time decision. Never keep yesterday's 25566 as a
            // sticky preference: every real launch starts at 25565 and immediately reuses it when
            // no TCP listener or other in-flight MCSV launch owns it.
            var preferredPort = reserveForLaunch
                ? ServerPortAllocator.DefaultPreferredPort
                : requestedPort ?? configuredPort;
            var occupancy = _capturePortOccupancy();
            var occupiedTcpPorts = occupancy.TcpPorts.ToHashSet();
            if (ownedActivePort is { } owned)
            {
                occupiedTcpPorts.Remove(owned);
            }

            var reservedPorts = new HashSet<int>();
            foreach (var server in Servers.Where(server => server.Id != instanceId))
            {
                var activePort = server.ActivePort;
                if (!_processManager.TryGetSnapshot(server.Id, out var processSnapshot)
                    || processSnapshot.State is not (ServerState.Starting or ServerState.Running or ServerState.Stopping)
                    || activePort is not (>= 1 and <= ServerPortAllocator.MaximumPort))
                {
                    continue;
                }

                reservedPorts.Add(activePort.Value);
            }

            foreach (var pending in _pendingLaunchPorts.Where(item => item.Key != instanceId))
            {
                reservedPorts.Add(pending.Value);
            }

            var desiredIsAvailable = preferredPort is >= 1 and <= ServerPortAllocator.MaximumPort
                && !occupiedTcpPorts.Contains(preferredPort.Value)
                && !reservedPorts.Contains(preferredPort.Value);
            var assignedPort = desiredIsAvailable
                ? preferredPort!.Value
                : ServerPortAllocator.FindFirstAvailablePort(
                    preferredPort: ServerPortAllocator.DefaultPreferredPort,
                    occupiedTcpPorts: occupiedTcpPorts,
                    // Minecraft's primary server-port is a TCP endpoint. query.port and RCON are
                    // configured separately, so a UDP-only endpoint must not force server-port up.
                    occupiedUdpPorts: null,
                    managerReservedPorts: reservedPorts);
            var fileUpdated = !usesVelocityArguments && configuredPort != assignedPort;
            if (usesVelocityArguments)
            {
                instance!.ServerArguments ??= [];
                VelocityPortArgumentEditor.SetPort(instance.ServerArguments, assignedPort);
            }
            else if (fileUpdated)
            {
                await _serverPropertiesPortService.SetServerPortAsync(propertiesPath, assignedPort, cancellationToken);
            }

            if (reserveForLaunch)
            {
                // A reservation is unbound until ProcessManager raises Starting with the exact
                // session ID. Removing an older association first prevents a late terminal UI
                // callback from an earlier session from releasing this new launch.
                _pendingLaunchPortSessions.TryRemove(instanceId, out _);
                _pendingLaunchPorts[instanceId] = assignedPort;
            }

            return new PortAssignment(
                assignedPort,
                preferredPort,
                preferredPort is not null && !desiredIsAvailable,
                fileUpdated);
        }
        finally
        {
            _portAssignmentGate.Release();
        }
    }

    internal void ReleasePendingLaunchPort(Guid instanceId, Guid? expectedSessionId = null)
    {
        if (expectedSessionId is { } sessionId)
        {
            if (!_pendingLaunchPortSessions.TryRemove(
                    new KeyValuePair<Guid, Guid>(instanceId, sessionId)))
            {
                return;
            }
        }
        else
        {
            _pendingLaunchPortSessions.TryRemove(instanceId, out _);
        }

        _pendingLaunchPorts.TryRemove(instanceId, out _);
    }

    private static string? FindBundledJavaExecutable(string directoryPath, int major)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return null;
        var serverRoot = Path.GetFullPath(directoryPath);
        var runtimeRoot = Path.Combine(serverRoot, "jre");
        if (!Directory.Exists(runtimeRoot)) return null;

        try
        {
            SafePath.EnsureNoReparsePointsUnderRoot(serverRoot, runtimeRoot);
            var inspected = 0;
            foreach (var runtimeDirectory in Directory.EnumerateDirectories(runtimeRoot, "*", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                if (inspected++ >= 32) break;
                var releaseFile = Path.Combine(runtimeDirectory, "release");
                var javaExecutable = Path.Combine(runtimeDirectory, "bin", "java.exe");
                if (!File.Exists(releaseFile) || !File.Exists(javaExecutable)) continue;
                SafePath.EnsureNoReparsePointsUnderRoot(serverRoot, releaseFile);
                SafePath.EnsureNoReparsePointsUnderRoot(serverRoot, javaExecutable);

                var releaseInfo = ReadJavaReleaseFile(releaseFile);
                if (!releaseInfo.TryGetValue("JAVA_VERSION", out var fullVersion)
                    || !TryParseJavaMajorVersion(fullVersion, out var actualMajor)
                    || actualMajor != major)
                {
                    continue;
                }

                var operatingSystem = releaseInfo.GetValueOrDefault("OS_NAME") ?? string.Empty;
                var architecture = releaseInfo.GetValueOrDefault("OS_ARCH") ?? string.Empty;
                var isX64 = architecture.Equals("x86_64", StringComparison.OrdinalIgnoreCase)
                    || architecture.Equals("amd64", StringComparison.OrdinalIgnoreCase)
                    || architecture.Equals("x64", StringComparison.OrdinalIgnoreCase);
                if (!operatingSystem.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)
                    || !isX64)
                {
                    continue;
                }

                return javaExecutable;
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private bool IsCurrentPlayerPresenceSession(
        ServerInstanceViewModel server,
        Guid instanceId,
        Guid sessionId)
    {
        // The process can emit its first lines between Process.Start and the dispatcher applying
        // the immediately-following Running transition. Starting already carries the definitive
        // session ID, so retain a valid early presence event instead of losing it to UI ordering.
        return (server.State is ServerState.Starting or ServerState.Running)
            && _playerPresenceSessions.TryGetValue(instanceId, out var activeSessionId)
            && activeSessionId == sessionId;
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        OpenExistingFolder(path);
    }

    private static void OpenExistingFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Folder does not exist: {path}");
        }

        var startInfo = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }

    internal sealed record PortAssignment(
        int Port,
        int? PreviousPort,
        bool WasConflict,
        bool FileUpdated);

    private sealed record CrashSessionPlan(
        Guid SessionId,
        CrashRestartDecision Decision,
        string Trigger);

    private sealed record StatusEndpoint(string Host, bool StatusEnabled);

    private enum PendingModpackFinalizationAction
    {
        None,
        Acknowledge,
        Rollback,
    }

    private sealed class PendingModpackHealthValidation : IDisposable
    {
        private readonly object _sync = new();
        private CancellationTokenSource? _sessionProbeCancellation;
        private Guid? _sessionId;
        private long _manualStopEpochAtStart;
        private bool _isHealthy;
        private bool _finalizationClaimed;
        private bool _isDisposed;

        internal PendingModpackHealthValidation(
            Guid transactionId,
            ModpackUpdateLaunchFields previousLaunchFields)
        {
            TransactionId = transactionId;
            PreviousLaunchFields = previousLaunchFields
                ?? throw new ArgumentNullException(nameof(previousLaunchFields));
        }

        internal Guid TransactionId { get; }

        internal ModpackUpdateLaunchFields PreviousLaunchFields { get; }

        internal bool IsHealthy
        {
            get
            {
                lock (_sync)
                {
                    return _isHealthy;
                }
            }
        }

        internal void BeginSession(
            Guid sessionId,
            long manualStopEpoch,
            CancellationToken applicationStopping)
        {
            lock (_sync)
            {
                if (_isDisposed || _finalizationClaimed) return;
                CancelSessionProbeUnderLock();
                _sessionId = sessionId;
                _manualStopEpochAtStart = manualStopEpoch;
                _isHealthy = false;
                _sessionProbeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    applicationStopping);
            }
        }

        internal void BeginSessionIfMissing(
            Guid sessionId,
            long manualStopEpoch,
            CancellationToken applicationStopping)
        {
            lock (_sync)
            {
                if (_isDisposed || _finalizationClaimed || _sessionId == sessionId) return;
            }

            BeginSession(sessionId, manualStopEpoch, applicationStopping);
        }

        internal bool TryGetProbeToken(Guid sessionId, out CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_isDisposed
                    || _finalizationClaimed
                    || _sessionId != sessionId
                    || _sessionProbeCancellation is null)
                {
                    cancellationToken = default;
                    return false;
                }

                cancellationToken = _sessionProbeCancellation.Token;
                return true;
            }
        }

        internal bool TryMarkHealthy(Guid sessionId)
        {
            lock (_sync)
            {
                if (_isDisposed
                    || _finalizationClaimed
                    || _sessionId != sessionId
                    || _isHealthy)
                {
                    return false;
                }

                _isHealthy = true;
                CancelSessionProbeUnderLock();
                return true;
            }
        }

        internal PendingModpackFinalizationAction ObserveTerminalState(
            Guid sessionId,
            ServerState state,
            long currentManualStopEpoch)
        {
            lock (_sync)
            {
                if (_isDisposed || _finalizationClaimed || _sessionId != sessionId)
                {
                    return PendingModpackFinalizationAction.None;
                }

                CancelSessionProbeUnderLock();
                if (_isHealthy)
                {
                    _finalizationClaimed = true;
                    return PendingModpackFinalizationAction.Acknowledge;
                }

                var failedBeforeHealth = state is ServerState.Crashed or ServerState.Faulted
                    && currentManualStopEpoch == _manualStopEpochAtStart;
                if (failedBeforeHealth)
                {
                    _finalizationClaimed = true;
                    return PendingModpackFinalizationAction.Rollback;
                }

                // An explicit stop before readiness is not evidence that the candidate is bad.
                // Retain the durable journal and require the next actual launch to validate it.
                _sessionId = null;
                _isHealthy = false;
                return PendingModpackFinalizationAction.None;
            }
        }

        internal void ReleaseFinalization()
        {
            lock (_sync)
            {
                if (_isDisposed) return;
                _finalizationClaimed = false;
                _sessionId = null;
                _isHealthy = false;
                CancelSessionProbeUnderLock();
            }
        }

        internal void CancelSessionProbe()
        {
            lock (_sync)
            {
                CancelSessionProbeUnderLock();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                CancelSessionProbeUnderLock();
                _sessionId = null;
            }
        }

        private void CancelSessionProbeUnderLock()
        {
            var cancellation = _sessionProbeCancellation;
            _sessionProbeCancellation = null;
            if (cancellation is null) return;
            try
            {
                cancellation.Cancel();
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }

    private readonly record struct PendingConsoleLine(Guid SessionId, ConsoleLine Line);

}
