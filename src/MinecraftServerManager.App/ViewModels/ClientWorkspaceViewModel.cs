using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MinecraftServerManager.App.Controls;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.ViewModels;

internal sealed record LoaderCatalogQueryResult(
    MinecraftClientLoader Loader,
    IReadOnlyList<MinecraftLoaderCatalogEntry> Versions,
    Exception? Error);

public sealed class ClientWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly MinecraftClientLoader[] FixedLoaderOrder =
    [
        MinecraftClientLoader.Vanilla,
        MinecraftClientLoader.Forge,
        MinecraftClientLoader.Fabric,
        MinecraftClientLoader.Quilt,
        MinecraftClientLoader.NeoForge,
        MinecraftClientLoader.OptiFine,
        MinecraftClientLoader.LabyMod,
    ];

    private static readonly string UserAgent =
        $"XMCSV/{GetRunningProductVersion()} (Windows; client-launcher)";
    private readonly ApplicationPaths _paths;
    private readonly Func<NewMinecraftClientDefaultsSettings> _getGlobalDefaults;
    private readonly HttpClient _catalogHttpClient;
    private readonly HttpClient _runtimeHttpClient;
    private readonly HttpClient _gameHttpClient;
    private readonly HttpClient _authenticationHttpClient;
    private readonly MinecraftClientRegistry _registry;
    private readonly BedrockClientShortcutRegistry _bedrockShortcutRegistry;
    private readonly IMinecraftReleaseCatalog _releaseCatalog;
    private readonly IReadOnlyList<IMinecraftLoaderCatalogProvider> _loaderCatalogs;
    private readonly MinecraftClientInstanceManager _instanceManager;
    private readonly MinecraftClientInstanceSettingsService _instanceSettingsService;
    private readonly AdoptiumRuntimeProvider _javaProvider;
    private readonly JavaVersionRecommendationService _javaRecommendation = new();
    private readonly IMinecraftAccountAuthenticationService _authenticationService;
    private readonly MinecraftClientMemoryRecommendationService _memoryRecommendationService;
    private readonly MinecraftClientProcessRecoveryService _processRecoveryService;
    private readonly MinecraftClientLaunchCoordinator _launchCoordinator;
    private readonly IModrinthClientModpackCatalog _modrinthCatalog;
    private readonly ModrinthMinecraftClientPackInstaller _modrinthInstaller;
    private readonly IModrinthClientContentCatalog _modrinthContentCatalog;
    private readonly ModrinthClientContentInstaller _modrinthContentInstaller;
    private readonly FtbClientCatalog _ftbCatalog;
    private readonly FtbMinecraftClientPackInstaller _ftbInstaller;
    private readonly ClientOperationDiagnosticStore _clientOperationDiagnosticStore;
    private readonly IOnlineModpackArtworkCache _artworkCache;
    private readonly BedrockOfficialHandoffService _bedrockOfficialHandoff;
    private readonly SemaphoreSlim _contentGate = new(1, 1);
    private readonly LatestOperationCoordinator _contentRefreshCoordinator;
    private readonly BatchObservableCollection<ClientContentItemViewModel> _contentItems = [];
    private readonly Dictionary<Guid, MinecraftClientProcessSession> _runningSessions = [];
    private readonly HashSet<Task> _sessionObserverTasks = [];
    private readonly ClientLauncherWindowLifecycle _launcherWindowLifecycle = new();
    private readonly object _runningSessionGate = new();
    private readonly HashSet<Task> _profileSynchronizationTasks = [];
    private readonly object _profileSynchronizationGate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _loaderRefreshCancellation;
    private CancellationTokenSource? _catalogBrowseCancellation;
    private CancellationTokenSource? _catalogVersionCancellation;
    private Task _catalogBrowseTask = Task.CompletedTask;
    private Task _catalogArtworkTask = Task.CompletedTask;
    private Task _catalogVersionTask = Task.CompletedTask;
    private Task _loaderRefreshTask = Task.CompletedTask;
    private MinecraftReleaseCatalogSnapshot? _releaseSnapshot;
    private MinecraftReleaseInfo? _selectedRelease;
    private ClientLoaderChoiceViewModel? _selectedLoader;
    private MinecraftLoaderCatalogEntry? _selectedLoaderVersion;
    private ClientInstanceItemViewModel? _selectedInstance;
    private BedrockClientShortcutItemViewModel? _selectedBedrockShortcut;
    private BedrockChannelChoiceViewModel? _selectedBedrockChannel;
    private MinecraftClientAccountInfo? _selectedAccount;
    private bool _isAccountPanelOpen;
    private bool _isAccountLoginChoiceOpen;
    private bool _isAccountExpiryExpanded;
    private bool _isDeviceCodePromptVisible;
    private string _microsoftAccountLoginHint = string.Empty;
    private string _deviceCode = string.Empty;
    private Uri? _deviceCodeVerificationUri;
    private DateTimeOffset? _deviceCodeExpiresAtUtc;
    private CancellationTokenSource? _accountLoginCancellation;
    private Task _accountRefreshTask = Task.CompletedTask;
    private Task _accountLoginTask = Task.CompletedTask;
    private MinecraftClientSkinVariant _skinPreviewVariant = MinecraftClientSkinVariant.Classic;
    private string? _selectedSkinFilePath;
    private MinecraftClientCapeInfo? _selectedCape;
    private ImageSource? _selectedPlayerSkinTexture;
    private ImageSource? _selectedPlayerHeadTexture;
    private CancellationTokenSource? _skinTextureLoadCancellation;
    private bool _isInitialized;
    private bool _isBusy;
    private bool _isCreatePage;
    private bool _isSettingsPage;
    private bool _isCatalogPage;
    private bool _isCatalogBusy;
    private bool _suppressSelectedInstanceNavigation;
    private bool _changingClientSelection;
    private bool _isJavaEdition = true;
    private string _newInstanceName = "Minecraft";
    private string _newBedrockShortcutName = string.Empty;
    private string _statusText = string.Empty;
    private string _errorText = string.Empty;
    private double _progressValue;
    private int _minimumMemoryMb = 2_048;
    private int _maximumMemoryMb = 4_096;
    private int _windowWidth = 1280;
    private int _windowHeight = 720;
    private IReadOnlyList<ClientResolutionChoice> _resolutionChoices =
        ClientResolutionCatalog.CreateChoices(1280, 720);
    private bool _fullScreen;
    private MinecraftClientMemoryMode _memoryMode = MinecraftClientMemoryMode.Automatic;
    private bool _applyingMemoryPreset;
    private MinecraftClientContentKind _selectedContentKind = MinecraftClientContentKind.Mod;
    private ClientContentItemViewModel? _selectedContentItem;
    private bool _showRecycleBin;
    private string _contentStatusText = string.Empty;
    private bool _isContentDownloadOpen;
    private MinecraftClientContentKind _contentDownloadKind = MinecraftClientContentKind.Mod;
    private string _contentDownloadSearchText = string.Empty;
    private string _contentDownloadStatusText = string.Empty;
    private ClientContentDownloadProjectItemViewModel? _selectedContentDownloadProject;
    private ClientContentDownloadLoaderChoice? _selectedContentDownloadLoader;
    private Uri? _contentDownloadFallbackUri;
    private ClientInstanceSettingsEditorViewModel? _settingsEditor;
    private bool _isClientSettingsClosePromptOpen;
    private string _catalogSourceId = "modrinth";
    private string _catalogSearchText = string.Empty;
    private ClientCatalogGameVersionChoice? _selectedCatalogGameVersion;
    private ClientCatalogLoaderChoice? _selectedCatalogLoader;
    private ClientCatalogCategoryChoice? _selectedCatalogCategory;
    private ClientCatalogSortChoice? _selectedCatalogSort;
    private int _catalogResultLimit = 20;
    private int _catalogTotalHits;
    private int _catalogNextOffset;
    private string _catalogStatusText = string.Empty;
    private string? _lastFtbInstallFailureLocalizationKey;
    private string? _lastFtbInstallDiagnosticId;
    private bool _isShowingFtbInstallFailure;
    private bool _hasFtbInstallDiagnostic;
    private ClientModpackProjectItemViewModel? _selectedCatalogProject;
    private ClientCatalogVersionItemViewModel? _selectedCatalogVersion;
    private string _catalogInstanceName = string.Empty;
    private bool _includeOptionalPackFiles;
    private bool _disposed;
    private IReadOnlyList<ClientCatalogLoaderChoice> _catalogLoaders = [];
    private IReadOnlyList<ClientCatalogCategoryChoice> _catalogCategories = [];
    private IReadOnlyList<ClientCatalogSortChoice> _catalogSortOptions = [];
    private IReadOnlyList<ClientContentDownloadLoaderChoice> _contentDownloadLoaders = [];
    private IReadOnlyList<BedrockChannelChoiceViewModel> _bedrockChannelChoices = [];

    public ClientWorkspaceViewModel(
        ApplicationPaths paths,
        Func<NewMinecraftClientDefaultsSettings> getGlobalDefaults)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _getGlobalDefaults = getGlobalDefaults ?? throw new ArgumentNullException(nameof(getGlobalDefaults));
        _paths.EnsureCreated();
        _contentRefreshCoordinator = new LatestOperationCoordinator(
            _lifetimeCancellation.Token);

        _catalogHttpClient = CreateHttpClient(TimeSpan.FromSeconds(30));
        _runtimeHttpClient = CreateHttpClient(TimeSpan.FromMinutes(10));
        _gameHttpClient = CreateHttpClient(TimeSpan.FromMinutes(30));
        _authenticationHttpClient = CreateHttpClient(TimeSpan.FromMinutes(5));
        _registry = new MinecraftClientRegistry(_paths.ClientRegistryFile);
        _bedrockShortcutRegistry = new BedrockClientShortcutRegistry(
            _paths.BedrockShortcutRegistryFile);
        _releaseCatalog = new MojangReleaseCatalog(_catalogHttpClient);
        _loaderCatalogs =
        [
            new FabricLoaderCatalogProvider(_catalogHttpClient),
            new ForgeLoaderCatalogProvider(_catalogHttpClient),
            new NeoForgeLoaderCatalogProvider(_catalogHttpClient),
            new QuiltLoaderCatalogProvider(_catalogHttpClient),
            new OptiFineExternalInstallerCatalogProvider(),
            new LabyModExternalInstallerCatalogProvider(),
        ];
        var payloadInstaller = new CmlMinecraftClientPayloadInstaller(_gameHttpClient);
        _instanceManager = new MinecraftClientInstanceManager(
            _paths.Clients,
            _paths.ClientStaging,
            _registry,
            _releaseCatalog,
            payloadInstaller);
        _instanceSettingsService = new MinecraftClientInstanceSettingsService(_registry, _paths.Clients);
        _javaProvider = new AdoptiumRuntimeProvider(_runtimeHttpClient, UserAgent);
        var installationId = MinecraftClientInstallationIdentity.LoadOrCreate(
            Path.Combine(_paths.ClientRoot, "installation.id"));
        _authenticationService = new MicrosoftMinecraftAuthenticationService(
            Path.Combine(_paths.ClientSecrets, "microsoft-accounts.v1.bin"),
            installationId,
            _authenticationHttpClient);
        _memoryRecommendationService = new MinecraftClientMemoryRecommendationService(
            new WindowsSystemMemoryProbe());
        _processRecoveryService = new MinecraftClientProcessRecoveryService();
        _launchCoordinator = new MinecraftClientLaunchCoordinator(
            _memoryRecommendationService,
            new CmlMinecraftClientProcessBuilder(),
            _processRecoveryService);
        _modrinthCatalog = new ModrinthClientModpackCatalog(_catalogHttpClient, UserAgent);
        _modrinthContentCatalog = new ModrinthClientContentCatalog(_catalogHttpClient, UserAgent);
        _modrinthContentInstaller = new ModrinthClientContentInstaller(
            Path.Combine(_paths.ClientStaging, "content-downloads"),
            _modrinthContentCatalog,
            _gameHttpClient);
        _ftbCatalog = new FtbClientCatalog(new FtbCatalogProvider(_catalogHttpClient, UserAgent));
        _modrinthInstaller = new ModrinthMinecraftClientPackInstaller(
            _paths.Clients,
            _paths.ClientStaging,
            _registry,
            _releaseCatalog,
            payloadInstaller,
            _modrinthCatalog,
            _gameHttpClient);
        _ftbInstaller = new FtbMinecraftClientPackInstaller(
            _paths.Clients,
            _paths.ClientStaging,
            _registry,
            _releaseCatalog,
            payloadInstaller,
            _ftbCatalog,
            _gameHttpClient);
        _clientOperationDiagnosticStore = new ClientOperationDiagnosticStore(_paths);
        _artworkCache = new OnlineModpackArtworkCache(_paths);
        _bedrockOfficialHandoff = new BedrockOfficialHandoffService();
        _statusText = L("client.vm.status.initial");
        _contentStatusText = L("client.vm.content.initial");
        _contentDownloadStatusText = L("client.vm.contentDownload.initial");
        _catalogStatusText = L("client.vm.catalog.initial");
        RefreshLocalizedCatalogChoices();
        RefreshLocalizedContentDownloadChoices();
        RefreshLocalizedBedrockChoices();
        _newBedrockShortcutName = L("client.create.bedrockDefaultName");
        _selectedCatalogLoader = CatalogLoaders[0];
        _selectedCatalogCategory = CatalogCategories[0];
        _selectedCatalogSort = CatalogSortOptions[0];
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);

        InitializeCommand = new AsyncRelayCommand(() => RunGuardedAsync(InitializeAsync), () => !_isInitialized);
        RefreshCatalogCommand = new AsyncRelayCommand(() => RunGuardedAsync(RefreshCatalogAsync), () => !IsBusy);
        NewInstanceCommand = new RelayCommand(ShowCreatePage);
        OpenCatalogCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(OpenCatalogAsync));
        CloseCatalogCommand = new RelayCommand(ShowSelectedInstance, () => !IsBusy);
        SelectCatalogSourceCommand = new AsyncRelayCommand(
            parameter => RunGuardedAsync(() => SelectCatalogSourceAsync(parameter)));
        SearchCatalogCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(() => LoadCatalogAsync(append: false)),
            () => IsBrowsableCatalogSource && !IsCatalogBusy);
        LoadMoreCatalogCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(() => LoadCatalogAsync(append: true)),
            () => IsModrinthCatalogSource && !IsCatalogBusy && HasMoreCatalogResults);
        InstallCatalogPackCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(InstallSelectedCatalogPackAsync),
            CanInstallCatalogPack);
        OpenFtbFallbackCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(OpenSelectedFtbFallbackAsync),
            CanOpenFtbFallback);
        OpenClientDiagnosticsFolderCommand = new RelayCommand(
            OpenClientDiagnosticsFolder,
            () => HasFtbInstallDiagnostic);
        CloseCreateCommand = new RelayCommand(ShowSelectedInstance);
        CreateInstanceCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(CreateInstanceAsync),
            CanCreateInstance);
        CancelOperationCommand = new RelayCommand(CancelCurrentOperation, () => IsBusy);
        LaunchCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(LaunchSelectedAsync),
            CanLaunchSelected);
        QuickLaunchCommand = new AsyncRelayCommand(
            parameter => RunGuardedAsync(() => QuickLaunchAsync(parameter)),
            CanQuickLaunch);
        StopClientCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(StopSelectedAsync),
            CanStopSelected);
        AddAccountCommand = new AsyncRelayCommand(
            OpenAccountLoginChoiceAsync,
            () => !IsBusy);
        ToggleAccountPanelCommand = new RelayCommand(ToggleAccountPanel);
        ToggleAccountExpiryCommand = new RelayCommand(
            () => IsAccountExpiryExpanded = !IsAccountExpiryExpanded,
            () => SelectedAccount is not null);
        StartBrowserAccountLoginCommand = new AsyncRelayCommand(
            () => _accountLoginTask = RunGuardedAsync(AddAccountInBrowserAsync),
            () => !IsBusy && IsValidMicrosoftAccountLoginHint(MicrosoftAccountLoginHint));
        StartDeviceCodeAccountLoginCommand = new AsyncRelayCommand(
            () => _accountLoginTask = RunGuardedAsync(AddAccountWithDeviceCodeAsync),
            () => !IsBusy);
        CancelAccountLoginCommand = new RelayCommand(CancelAccountLogin);
        CopyDeviceCodeCommand = new RelayCommand(CopyDeviceCode, () => IsDeviceCodePromptVisible);
        OpenDeviceLoginPageCommand = new RelayCommand(
            OpenDeviceLoginPage,
            () => IsDeviceCodePromptVisible && DeviceCodeVerificationUri is not null);
        SelectClassicSkinCommand = new RelayCommand(
            () => SkinPreviewVariant = MinecraftClientSkinVariant.Classic,
            () => SelectedAccount is not null && !IsBusy);
        SelectSlimSkinCommand = new RelayCommand(
            () => SkinPreviewVariant = MinecraftClientSkinVariant.Slim,
            () => SelectedAccount is not null && !IsBusy);
        ChooseSkinFileCommand = new RelayCommand(
            ChooseSkinFile,
            () => SelectedAccount is not null && !IsBusy);
        SaveSkinCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(SaveSkinAsync),
            () => SelectedAccount is not null && !IsBusy);
        ApplySelectedCapeCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(ApplySelectedCapeAsync),
            () => SelectedAccount is not null && SelectedCape is not null && !IsBusy);
        DisableCapeCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(DisableCapeAsync),
            () => SelectedAccount is not null && SelectedAccount.Capes.Any(cape => cape.IsActive) && !IsBusy);
        RemoveSelectedAccountCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(RemoveSelectedAccountAsync),
            () => SelectedAccount is not null && !IsBusy);
        SignOutAllAccountsCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(SignOutAllAccountsAsync),
            () => Accounts.Count > 0 && !IsBusy);
        OpenInstanceFolderCommand = new RelayCommand(
            OpenSelectedInstanceFolder,
            () => SelectedInstance is not null && Directory.Exists(SelectedInstance.Model.DirectoryPath));
        DeleteClientInstanceCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(DeleteSelectedInstanceAsync),
            CanDeleteSelectedInstance);
        OpenExternalInstallerCommand = new RelayCommand(
            OpenSelectedExternalInstaller,
            () => SelectedLoaderVersion?.InstallKind ==
                  MinecraftClientLoaderInstallKind.ExternalInstallerRequired);
        UseGlobalMemoryCommand = new RelayCommand(() => ApplyMemoryMode(MinecraftClientMemoryMode.UseGlobalDefault));
        UseAutomaticMemoryCommand = new RelayCommand(() => ApplyMemoryMode(MinecraftClientMemoryMode.Automatic));
        UseManualMemoryCommand = new RelayCommand(() => ApplyMemoryMode(MinecraftClientMemoryMode.Manual));
        ApplyResolutionPresetCommand = new RelayCommand(ApplyResolutionPreset);
        OpenContentFolderCommand = new RelayCommand(
            OpenSelectedContentFolder,
            parameter => SelectedInstance is not null && parameter is string folder && IsAllowedContentFolder(folder));
        OpenContentDownloadCommand = new AsyncRelayCommand(
            parameter => RunGuardedAsync(() => OpenContentDownloadAsync(parameter)),
            parameter => CanOpenContentDownload(parameter));
        CloseContentDownloadCommand = new RelayCommand(CloseContentDownload, () => !IsBusy);
        SearchContentDownloadCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(SearchContentDownloadAsync),
            () => IsContentDownloadOpen && SelectedInstance is not null && !IsBusy);
        InstallContentDownloadCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(InstallSelectedContentDownloadAsync),
            CanInstallSelectedContentDownload);
        OpenSelectedContentProjectPageCommand = new RelayCommand(
            OpenSelectedContentProjectPage,
            () => SelectedContentDownloadProject is not null);
        OpenContentFallbackCommand = new RelayCommand(
            OpenContentFallback,
            () => ContentDownloadFallbackUri is not null);
        SelectJavaEditionCommand = new RelayCommand(() => IsJavaEdition = true);
        SelectBedrockEditionCommand = new RelayCommand(() => IsJavaEdition = false);
        OpenBedrockOfficialCommand = new RelayCommand(
            OpenBedrockOfficial,
            () => IsBedrockEdition && !IsBusy);
        OpenSelectedBedrockOfficialCommand = new RelayCommand(
            OpenSelectedBedrockOfficial,
            () => SelectedBedrockShortcut is not null && !IsBusy);
        DeleteBedrockShortcutCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(DeleteSelectedBedrockShortcutAsync),
            () => SelectedBedrockShortcut is not null && !IsBusy);
        SelectContentKindCommand = new RelayCommand(
            parameter => _ = RunGuardedAsync(() => SelectContentKindAsync(parameter)));
        RefreshContentCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(RefreshContentAsync),
            () => SelectedInstance is not null && !IsBusy);
        ImportContentCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(ImportContentAsync),
            () => CanMutateSelectedContent() && !ShowRecycleBin);
        ToggleContentEnabledCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(ToggleSelectedContentEnabledAsync),
            () => CanMutateSelectedContent() && SelectedContentItem?.IsActive == true);
        RecycleContentCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(RecycleSelectedContentAsync),
            () => CanMutateSelectedContent() && SelectedContentItem?.IsActive == true);
        RestoreContentCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(RestoreSelectedContentAsync),
            () => CanMutateSelectedContent() && SelectedContentItem?.IsRecycled == true);
        ToggleRecycleBinCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(ToggleRecycleBinAsync),
            () => SelectedInstance is not null && !IsBusy);
        OpenClientSettingsCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(OpenClientSettingsAsync),
            () => SelectedInstance is { IsRunning: false } && !IsBusy);
        SaveClientSettingsCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(SaveClientSettingsAsync),
            () => SelectedInstance is { IsRunning: false } && SettingsEditor?.CanSave == true && !IsBusy);
        CloseClientSettingsCommand = new RelayCommand(CloseClientSettings);
        DiscardClientSettingsChangesCommand = new RelayCommand(DiscardClientSettingsChanges);
        CancelClientSettingsCloseCommand = new RelayCommand(CancelClientSettingsClose);
        ChooseClientIconCommand = new RelayCommand(ChooseClientIcon, () => SettingsEditor is not null);
        ChooseClientJavaCommand = new AsyncRelayCommand(
            () => RunGuardedAsync(ChooseClientJavaAsync),
            () => SettingsEditor is not null && SelectedInstance is { IsRunning: false } && !IsBusy);
    }

    public ObservableCollection<ClientInstanceItemViewModel> Instances { get; } = [];

    public ObservableCollection<BedrockClientShortcutItemViewModel> BedrockShortcuts { get; } = [];

    public ObservableCollection<MinecraftReleaseInfo> Releases { get; } = [];

    public ObservableCollection<ClientLoaderChoiceViewModel> LoaderChoices { get; } = [];

    public ObservableCollection<MinecraftLoaderCatalogEntry> LoaderVersions { get; } = [];

    public ObservableCollection<MinecraftClientAccountInfo> Accounts { get; } = [];

    public ObservableCollection<ClientContentItemViewModel> ContentItems => _contentItems;

    public ObservableCollection<ClientContentDownloadProjectItemViewModel> ContentDownloadResults { get; } = [];

    public IReadOnlyList<ClientContentDownloadLoaderChoice> ContentDownloadLoaders =>
        _contentDownloadLoaders;

    public ObservableCollection<ClientModpackProjectItemViewModel> CatalogProjects { get; } = [];

    public ObservableCollection<ClientCatalogVersionItemViewModel> CatalogVersions { get; } = [];

    public ObservableCollection<ClientCatalogGameVersionChoice> CatalogGameVersions { get; } = [];

    public IReadOnlyList<ClientCatalogLoaderChoice> CatalogLoaders => _catalogLoaders;

    public IReadOnlyList<ClientCatalogCategoryChoice> CatalogCategories => _catalogCategories;

    public IReadOnlyList<ClientCatalogSortChoice> CatalogSortOptions => _catalogSortOptions;

    public IReadOnlyList<BedrockChannelChoiceViewModel> BedrockChannelChoices =>
        _bedrockChannelChoices;

    public IReadOnlyList<int> CatalogResultLimits { get; } = [20, 40, 60, 80, 100];

    public AsyncRelayCommand InitializeCommand { get; }
    public AsyncRelayCommand RefreshCatalogCommand { get; }
    public RelayCommand NewInstanceCommand { get; }
    public AsyncRelayCommand OpenCatalogCommand { get; }
    public RelayCommand CloseCatalogCommand { get; }
    public AsyncRelayCommand SelectCatalogSourceCommand { get; }
    public AsyncRelayCommand SearchCatalogCommand { get; }
    public AsyncRelayCommand LoadMoreCatalogCommand { get; }
    public AsyncRelayCommand InstallCatalogPackCommand { get; }
    public AsyncRelayCommand OpenFtbFallbackCommand { get; }
    public RelayCommand OpenClientDiagnosticsFolderCommand { get; }
    public RelayCommand CloseCreateCommand { get; }
    public AsyncRelayCommand CreateInstanceCommand { get; }
    public RelayCommand CancelOperationCommand { get; }
    public AsyncRelayCommand LaunchCommand { get; }
    public AsyncRelayCommand QuickLaunchCommand { get; }
    public AsyncRelayCommand StopClientCommand { get; }
    public AsyncRelayCommand AddAccountCommand { get; }
    public RelayCommand ToggleAccountPanelCommand { get; }
    public RelayCommand ToggleAccountExpiryCommand { get; }
    public AsyncRelayCommand StartBrowserAccountLoginCommand { get; }
    public AsyncRelayCommand StartDeviceCodeAccountLoginCommand { get; }
    public RelayCommand CancelAccountLoginCommand { get; }
    public RelayCommand CopyDeviceCodeCommand { get; }
    public RelayCommand OpenDeviceLoginPageCommand { get; }
    public RelayCommand SelectClassicSkinCommand { get; }
    public RelayCommand SelectSlimSkinCommand { get; }
    public RelayCommand ChooseSkinFileCommand { get; }
    public AsyncRelayCommand SaveSkinCommand { get; }
    public AsyncRelayCommand ApplySelectedCapeCommand { get; }
    public AsyncRelayCommand DisableCapeCommand { get; }
    public AsyncRelayCommand RemoveSelectedAccountCommand { get; }
    public AsyncRelayCommand SignOutAllAccountsCommand { get; }
    public RelayCommand OpenInstanceFolderCommand { get; }
    public AsyncRelayCommand DeleteClientInstanceCommand { get; }
    public RelayCommand OpenExternalInstallerCommand { get; }
    public RelayCommand UseGlobalMemoryCommand { get; }
    public RelayCommand UseAutomaticMemoryCommand { get; }
    public RelayCommand UseManualMemoryCommand { get; }
    public RelayCommand ApplyResolutionPresetCommand { get; }
    public RelayCommand OpenContentFolderCommand { get; }
    public AsyncRelayCommand OpenContentDownloadCommand { get; }
    public RelayCommand CloseContentDownloadCommand { get; }
    public AsyncRelayCommand SearchContentDownloadCommand { get; }
    public AsyncRelayCommand InstallContentDownloadCommand { get; }
    public RelayCommand OpenSelectedContentProjectPageCommand { get; }
    public RelayCommand OpenContentFallbackCommand { get; }
    public RelayCommand SelectJavaEditionCommand { get; }
    public RelayCommand SelectBedrockEditionCommand { get; }
    public RelayCommand OpenBedrockOfficialCommand { get; }
    public RelayCommand OpenSelectedBedrockOfficialCommand { get; }
    public AsyncRelayCommand DeleteBedrockShortcutCommand { get; }
    public RelayCommand SelectContentKindCommand { get; }
    public AsyncRelayCommand RefreshContentCommand { get; }
    public AsyncRelayCommand ImportContentCommand { get; }
    public AsyncRelayCommand ToggleContentEnabledCommand { get; }
    public AsyncRelayCommand RecycleContentCommand { get; }
    public AsyncRelayCommand RestoreContentCommand { get; }
    public AsyncRelayCommand ToggleRecycleBinCommand { get; }
    public AsyncRelayCommand OpenClientSettingsCommand { get; }
    public AsyncRelayCommand SaveClientSettingsCommand { get; }
    public RelayCommand CloseClientSettingsCommand { get; }
    public RelayCommand DiscardClientSettingsChangesCommand { get; }
    public RelayCommand CancelClientSettingsCloseCommand { get; }
    public RelayCommand ChooseClientIconCommand { get; }
    public AsyncRelayCommand ChooseClientJavaCommand { get; }

    public MinecraftReleaseInfo? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            if (!SetProperty(ref _selectedRelease, value))
            {
                return;
            }

            if (value is not null && (NewInstanceName == "Minecraft" || string.IsNullOrWhiteSpace(NewInstanceName)))
            {
                NewInstanceName = $"Minecraft {value.Id}";
            }

            _loaderRefreshTask = RunGuardedAsync(RefreshLoaderChoicesAsync);
            NotifyCreateStateChanged();
        }
    }

    public ClientLoaderChoiceViewModel? SelectedLoader
    {
        get => _selectedLoader;
        set
        {
            if (!SetProperty(ref _selectedLoader, value))
            {
                return;
            }

            LoaderVersions.Clear();
            if (value is not null)
            {
                foreach (var version in value.Versions)
                {
                    LoaderVersions.Add(version);
                }
            }

            SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
            if (MemoryMode == MinecraftClientMemoryMode.Automatic)
            {
                ApplyAutomaticMemoryRecommendation();
            }

            OnPropertyChanged(nameof(IsExternalLoaderSelected));
            NotifyCreateStateChanged();
        }
    }

    public MinecraftLoaderCatalogEntry? SelectedLoaderVersion
    {
        get => _selectedLoaderVersion;
        set
        {
            if (SetProperty(ref _selectedLoaderVersion, value))
            {
                OnPropertyChanged(nameof(IsExternalLoaderSelected));
                OpenExternalInstallerCommand.NotifyCanExecuteChanged();
                NotifyCreateStateChanged();
            }
        }
    }

    public ClientInstanceItemViewModel? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value))
            {
                return;
            }

            if (value is not null)
            {
                if (!_changingClientSelection)
                {
                    _changingClientSelection = true;
                    try
                    {
                        SelectedBedrockShortcut = null;
                    }
                    finally
                    {
                        _changingClientSelection = false;
                    }
                }

                if (!_suppressSelectedInstanceNavigation)
                {
                    IsCreatePage = false;
                    IsSettingsPage = false;
                    IsCatalogPage = false;
                }
            }

            SettingsEditor = null;

            OnPropertyChanged(nameof(HasSelectedInstance));
            OnPropertyChanged(nameof(HasAnySelectedClient));
            OnPropertyChanged(nameof(IsDashboardPage));
            OnPropertyChanged(nameof(IsBedrockShortcutPage));
            LaunchCommand.NotifyCanExecuteChanged();
            QuickLaunchCommand.NotifyCanExecuteChanged();
            StopClientCommand.NotifyCanExecuteChanged();
            OpenInstanceFolderCommand.NotifyCanExecuteChanged();
            DeleteClientInstanceCommand.NotifyCanExecuteChanged();
            OpenContentFolderCommand.NotifyCanExecuteChanged();
            OpenContentDownloadCommand.NotifyCanExecuteChanged();
            SearchContentDownloadCommand.NotifyCanExecuteChanged();
            InstallContentDownloadCommand.NotifyCanExecuteChanged();
            OpenClientSettingsCommand.NotifyCanExecuteChanged();
            SaveClientSettingsCommand.NotifyCanExecuteChanged();
            RefreshContentCommand.NotifyCanExecuteChanged();
            ImportContentCommand.NotifyCanExecuteChanged();
            ToggleContentEnabledCommand.NotifyCanExecuteChanged();
            RecycleContentCommand.NotifyCanExecuteChanged();
            RestoreContentCommand.NotifyCanExecuteChanged();
            ToggleRecycleBinCommand.NotifyCanExecuteChanged();
            CloseCreateCommand.NotifyCanExecuteChanged();
            _contentRefreshCoordinator.CancelCurrent();
            _contentItems.ReplaceAll([]);
            SelectedContentItem = null;
            IsContentDownloadOpen = false;
            ContentDownloadResults.Clear();
            SelectedContentDownloadProject = null;
            ContentDownloadFallbackUri = null;
            OnPropertyChanged(nameof(ContentDownloadDescription));
            if (value is not null)
            {
                _ = RunGuardedAsync(RefreshContentAsync);
            }
        }
    }

    public BedrockClientShortcutItemViewModel? SelectedBedrockShortcut
    {
        get => _selectedBedrockShortcut;
        set
        {
            if (!SetProperty(ref _selectedBedrockShortcut, value))
            {
                return;
            }

            if (value is not null)
            {
                if (!_changingClientSelection)
                {
                    _changingClientSelection = true;
                    try
                    {
                        SelectedInstance = null;
                    }
                    finally
                    {
                        _changingClientSelection = false;
                    }
                }

                if (!_suppressSelectedInstanceNavigation)
                {
                    IsCreatePage = false;
                    IsSettingsPage = false;
                    IsCatalogPage = false;
                }
            }

            OnPropertyChanged(nameof(HasSelectedBedrockShortcut));
            OnPropertyChanged(nameof(HasAnySelectedClient));
            OnPropertyChanged(nameof(IsDashboardPage));
            OnPropertyChanged(nameof(IsBedrockShortcutPage));
            OpenSelectedBedrockOfficialCommand.NotifyCanExecuteChanged();
            DeleteBedrockShortcutCommand.NotifyCanExecuteChanged();
        }
    }

    public MinecraftClientAccountInfo? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value))
            {
                BeginLoadOfficialSkinTexture(value);
                _skinPreviewVariant = value?.ActiveSkin?.Variant ?? MinecraftClientSkinVariant.Classic;
                _selectedSkinFilePath = null;
                _selectedCape = value?.Capes.FirstOrDefault(cape => cape.IsActive)
                                ?? value?.Capes.FirstOrDefault();
                LaunchCommand.NotifyCanExecuteChanged();
                RemoveSelectedAccountCommand.NotifyCanExecuteChanged();
                ToggleAccountExpiryCommand.NotifyCanExecuteChanged();
                NotifyAccountCosmeticStateChanged();
                OnPropertyChanged(nameof(HasSelectedAccount));
                OnPropertyChanged(nameof(SelectedPlayerName));
                OnPropertyChanged(nameof(AccountButtonAccessibleName));
                OnPropertyChanged(nameof(SelectedPlayerUuid));
                OnPropertyChanged(nameof(SelectedPlayerSkinUri));
                OnPropertyChanged(nameof(SelectedPlayerSkinTexture));
                OnPropertyChanged(nameof(SelectedPlayerHeadTexture));
                OnPropertyChanged(nameof(SelectedAccountExpiresAtUtc));
                OnPropertyChanged(nameof(SelectedAccountCapes));
                OnPropertyChanged(nameof(SelectedAccountExpirySummary));
            }
        }
    }

    public bool HasSelectedAccount => SelectedAccount is not null;

    public string SelectedPlayerName => SelectedAccount?.Username
        ?? L("client.account.none");

    public string AccountButtonAccessibleName =>
        L("client.account.openPlayerInfo", SelectedPlayerName);

    public string SelectedPlayerUuid => SelectedAccount?.MinecraftUuid ?? string.Empty;

    public Uri? SelectedPlayerSkinUri => SelectedAccount?.ActiveSkin?.TextureUri;

    public ImageSource? SelectedPlayerSkinTexture => _selectedPlayerSkinTexture;

    public ImageSource? SelectedPlayerHeadTexture => _selectedPlayerHeadTexture;

    public DateTimeOffset? SelectedAccountExpiresAtUtc =>
        SelectedAccount?.AuthenticationExpiresAtUtc?.ToLocalTime();

    public IReadOnlyList<MinecraftClientCapeInfo> SelectedAccountCapes =>
        SelectedAccount?.Capes ?? [];

    public string SelectedAccountExpirySummary => SelectedAccountExpiresAtUtc is { } expiry
        ? L("client.account.expiry.value", expiry)
        : L("client.account.expiry.unknown");

    public bool IsAccountPanelOpen
    {
        get => _isAccountPanelOpen;
        private set => SetProperty(ref _isAccountPanelOpen, value);
    }

    public bool IsAccountLoginChoiceOpen
    {
        get => _isAccountLoginChoiceOpen;
        private set => SetProperty(ref _isAccountLoginChoiceOpen, value);
    }

    public bool IsAccountExpiryExpanded
    {
        get => _isAccountExpiryExpanded;
        private set => SetProperty(ref _isAccountExpiryExpanded, value);
    }

    public bool IsDeviceCodePromptVisible
    {
        get => _isDeviceCodePromptVisible;
        private set
        {
            if (SetProperty(ref _isDeviceCodePromptVisible, value))
            {
                CopyDeviceCodeCommand.NotifyCanExecuteChanged();
                OpenDeviceLoginPageCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string DeviceCode
    {
        get => _deviceCode;
        private set => SetProperty(ref _deviceCode, value);
    }

    public Uri? DeviceCodeVerificationUri
    {
        get => _deviceCodeVerificationUri;
        private set
        {
            if (SetProperty(ref _deviceCodeVerificationUri, value))
            {
                OpenDeviceLoginPageCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public DateTimeOffset? DeviceCodeExpiresAtUtc
    {
        get => _deviceCodeExpiresAtUtc;
        private set
        {
            if (SetProperty(ref _deviceCodeExpiresAtUtc, value))
            {
                OnPropertyChanged(nameof(DeviceCodeExpirySummary));
            }
        }
    }

    public string DeviceCodeExpirySummary => DeviceCodeExpiresAtUtc is { } expiry
        ? L("client.account.device.expires", expiry.ToLocalTime())
        : string.Empty;

    /// <summary>
    /// Ephemeral, non-secret identifier passed to Microsoft's official browser as a login hint.
    /// It is never written to X MCSV settings or used as a password field.
    /// </summary>
    public string MicrosoftAccountLoginHint
    {
        get => _microsoftAccountLoginHint;
        set
        {
            if (SetProperty(ref _microsoftAccountLoginHint, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasValidMicrosoftAccountLoginHint));
                StartBrowserAccountLoginCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasValidMicrosoftAccountLoginHint =>
        IsValidMicrosoftAccountLoginHint(MicrosoftAccountLoginHint);

    public MinecraftClientSkinVariant SkinPreviewVariant
    {
        get => _skinPreviewVariant;
        private set
        {
            if (SetProperty(ref _skinPreviewVariant, value))
            {
                OnPropertyChanged(nameof(IsClassicSkinPreview));
                OnPropertyChanged(nameof(IsSlimSkinPreview));
            }
        }
    }

    public bool IsClassicSkinPreview => SkinPreviewVariant == MinecraftClientSkinVariant.Classic;

    public bool IsSlimSkinPreview => SkinPreviewVariant == MinecraftClientSkinVariant.Slim;

    public string? SelectedSkinFilePath
    {
        get => _selectedSkinFilePath;
        private set
        {
            if (SetProperty(ref _selectedSkinFilePath, value))
            {
                OnPropertyChanged(nameof(SelectedSkinFileName));
                OnPropertyChanged(nameof(SkinPreviewTextureSource));
            }
        }
    }

    public string SelectedSkinFileName => string.IsNullOrWhiteSpace(SelectedSkinFilePath)
        ? L("client.account.skin.current")
        : Path.GetFileName(SelectedSkinFilePath);

    // A selected local PNG intentionally suppresses the remote official texture because the
    // 3D control gives TextureSource precedence over SkinPath. This is preview-only state.
    public ImageSource? SkinPreviewTextureSource => SelectedSkinFilePath is null
        ? SelectedPlayerSkinTexture
        : null;

    public MinecraftClientCapeInfo? SelectedCape
    {
        get => _selectedCape;
        set
        {
            if (SetProperty(ref _selectedCape, value))
            {
                ApplySelectedCapeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsInitialized
    {
        get => _isInitialized;
        private set => SetProperty(ref _isInitialized, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                CancelOperationCommand.NotifyCanExecuteChanged();
                CloseCatalogCommand.NotifyCanExecuteChanged();
                NotifyCreateStateChanged();
                LaunchCommand.NotifyCanExecuteChanged();
                QuickLaunchCommand.NotifyCanExecuteChanged();
                AddAccountCommand.NotifyCanExecuteChanged();
                StartBrowserAccountLoginCommand.NotifyCanExecuteChanged();
                StartDeviceCodeAccountLoginCommand.NotifyCanExecuteChanged();
                SelectClassicSkinCommand.NotifyCanExecuteChanged();
                SelectSlimSkinCommand.NotifyCanExecuteChanged();
                ChooseSkinFileCommand.NotifyCanExecuteChanged();
                SaveSkinCommand.NotifyCanExecuteChanged();
                ApplySelectedCapeCommand.NotifyCanExecuteChanged();
                DisableCapeCommand.NotifyCanExecuteChanged();
                RemoveSelectedAccountCommand.NotifyCanExecuteChanged();
                SignOutAllAccountsCommand.NotifyCanExecuteChanged();
                DeleteClientInstanceCommand.NotifyCanExecuteChanged();
                RefreshContentCommand.NotifyCanExecuteChanged();
                ImportContentCommand.NotifyCanExecuteChanged();
                ToggleContentEnabledCommand.NotifyCanExecuteChanged();
                RecycleContentCommand.NotifyCanExecuteChanged();
                RestoreContentCommand.NotifyCanExecuteChanged();
                ToggleRecycleBinCommand.NotifyCanExecuteChanged();
                OpenClientSettingsCommand.NotifyCanExecuteChanged();
                SaveClientSettingsCommand.NotifyCanExecuteChanged();
                ChooseClientJavaCommand.NotifyCanExecuteChanged();
                OpenBedrockOfficialCommand.NotifyCanExecuteChanged();
                OpenSelectedBedrockOfficialCommand.NotifyCanExecuteChanged();
                DeleteBedrockShortcutCommand.NotifyCanExecuteChanged();
                OpenContentDownloadCommand.NotifyCanExecuteChanged();
                CloseContentDownloadCommand.NotifyCanExecuteChanged();
                SearchContentDownloadCommand.NotifyCanExecuteChanged();
                InstallContentDownloadCommand.NotifyCanExecuteChanged();
                InstallCatalogPackCommand.NotifyCanExecuteChanged();
                OpenFtbFallbackCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsCreatePage
    {
        get => _isCreatePage;
        private set
        {
            if (SetProperty(ref _isCreatePage, value))
            {
                OnPropertyChanged(nameof(IsDashboardPage));
                OnPropertyChanged(nameof(IsBedrockShortcutPage));
            }
        }
    }

    public bool IsSettingsPage
    {
        get => _isSettingsPage;
        private set
        {
            if (SetProperty(ref _isSettingsPage, value))
            {
                OnPropertyChanged(nameof(IsDashboardPage));
                OnPropertyChanged(nameof(IsBedrockShortcutPage));
            }
        }
    }

    public bool IsCatalogPage
    {
        get => _isCatalogPage;
        private set
        {
            if (SetProperty(ref _isCatalogPage, value))
            {
                OnPropertyChanged(nameof(IsDashboardPage));
                OnPropertyChanged(nameof(IsBedrockShortcutPage));
            }
        }
    }

    public bool IsDashboardPage =>
        HasSelectedInstance && !IsCreatePage && !IsSettingsPage && !IsCatalogPage;

    public bool IsBedrockShortcutPage =>
        HasSelectedBedrockShortcut && !IsCreatePage && !IsSettingsPage && !IsCatalogPage;

    public bool IsCatalogBusy
    {
        get => _isCatalogBusy;
        private set
        {
            if (!SetProperty(ref _isCatalogBusy, value))
            {
                return;
            }

            SearchCatalogCommand.NotifyCanExecuteChanged();
            LoadMoreCatalogCommand.NotifyCanExecuteChanged();
            InstallCatalogPackCommand.NotifyCanExecuteChanged();
        }
    }

    public string CatalogSourceId
    {
        get => _catalogSourceId;
        private set
        {
            if (!SetProperty(ref _catalogSourceId, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsModrinthCatalogSource));
            OnPropertyChanged(nameof(IsFtbCatalogSource));
            OnPropertyChanged(nameof(ShowsFtbInstallDiagnostic));
            OnPropertyChanged(nameof(IsBrowsableCatalogSource));
            OnPropertyChanged(nameof(IsUnavailableCatalogSource));
            OnPropertyChanged(nameof(ShowsCatalogSortFilter));
            OnPropertyChanged(nameof(ShowsCatalogCategoryFilter));
            OnPropertyChanged(nameof(CatalogResultsHeading));
            OnPropertyChanged(nameof(CatalogInstallHeading));
            OnPropertyChanged(nameof(CatalogInstallActionText));
            OnPropertyChanged(nameof(ShowsCatalogInstallOptions));
#pragma warning disable CS0618
            OnPropertyChanged(nameof(ShowsModrinthInstallOptions));
#pragma warning restore CS0618
            SearchCatalogCommand.NotifyCanExecuteChanged();
            LoadMoreCatalogCommand.NotifyCanExecuteChanged();
            InstallCatalogPackCommand.NotifyCanExecuteChanged();
            OpenFtbFallbackCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsModrinthCatalogSource => CatalogSourceId == "modrinth";

    public bool IsFtbCatalogSource => CatalogSourceId == "ftb";

    public bool HasFtbInstallDiagnostic
    {
        get => _hasFtbInstallDiagnostic;
        private set
        {
            if (SetProperty(ref _hasFtbInstallDiagnostic, value))
            {
                OnPropertyChanged(nameof(ShowsFtbInstallDiagnostic));
                OpenClientDiagnosticsFolderCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowsFtbInstallDiagnostic => IsFtbCatalogSource && HasFtbInstallDiagnostic;

    public bool IsBrowsableCatalogSource => IsModrinthCatalogSource || IsFtbCatalogSource;

    public bool IsUnavailableCatalogSource => !IsBrowsableCatalogSource;

    public bool ShowsCatalogSortFilter => IsModrinthCatalogSource;

    public bool ShowsCatalogCategoryFilter => IsModrinthCatalogSource;

    public bool ShowsCatalogInstallOptions => IsBrowsableCatalogSource;

    [Obsolete("Use ShowsCatalogInstallOptions.")]
    public bool ShowsModrinthInstallOptions => ShowsCatalogInstallOptions;

    public string CatalogResultsHeading => IsFtbCatalogSource
        ? L("client.catalog.ftbProjects")
        : L("client.catalog.projects");

    public string CatalogInstallHeading => IsFtbCatalogSource
        ? L("client.catalog.ftbInstallHeading")
        : L("client.catalog.installHeading");

    public string CatalogInstallActionText => IsFtbCatalogSource
        ? L("client.catalog.ftbInstallAction")
        : L("client.action.install");

    public string CatalogSearchText
    {
        get => _catalogSearchText;
        set
        {
            if (SetProperty(ref _catalogSearchText, value))
            {
                ScheduleCatalogRefresh();
            }
        }
    }

    public ClientCatalogGameVersionChoice? SelectedCatalogGameVersion
    {
        get => _selectedCatalogGameVersion;
        set
        {
            if (SetProperty(ref _selectedCatalogGameVersion, value))
            {
                ScheduleCatalogRefresh();
            }
        }
    }

    public ClientCatalogLoaderChoice? SelectedCatalogLoader
    {
        get => _selectedCatalogLoader;
        set
        {
            if (SetProperty(ref _selectedCatalogLoader, value))
            {
                ScheduleCatalogRefresh();
            }
        }
    }

    public ClientCatalogCategoryChoice? SelectedCatalogCategory
    {
        get => _selectedCatalogCategory;
        set
        {
            if (SetProperty(ref _selectedCatalogCategory, value))
            {
                ScheduleCatalogRefresh();
            }
        }
    }

    public ClientCatalogSortChoice? SelectedCatalogSort
    {
        get => _selectedCatalogSort;
        set
        {
            if (SetProperty(ref _selectedCatalogSort, value))
            {
                ScheduleCatalogRefresh();
            }
        }
    }

    public int CatalogResultLimit
    {
        get => _catalogResultLimit;
        set
        {
            var normalized = CatalogResultLimits.Contains(value) ? value : 20;
            if (SetProperty(ref _catalogResultLimit, normalized))
            {
                ScheduleCatalogRefresh();
            }
        }
    }

    public int CatalogTotalHits
    {
        get => _catalogTotalHits;
        private set
        {
            if (SetProperty(ref _catalogTotalHits, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(CatalogResultsSummary));
                OnPropertyChanged(nameof(HasMoreCatalogResults));
                LoadMoreCatalogCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string CatalogResultsSummary =>
        L("client.vm.catalog.resultsSummary", CatalogProjects.Count, CatalogTotalHits);

    public bool HasMoreCatalogResults => _catalogNextOffset < CatalogTotalHits;

    public string CatalogStatusText
    {
        get => _catalogStatusText;
        private set => SetProperty(ref _catalogStatusText, value);
    }

    public ClientModpackProjectItemViewModel? SelectedCatalogProject
    {
        get => _selectedCatalogProject;
        set
        {
            if (!SetProperty(ref _selectedCatalogProject, value))
            {
                return;
            }

            CatalogVersions.Clear();
            SelectedCatalogVersion = null;
            if (value is not null)
            {
                CatalogInstanceName = value.Title;
                _catalogVersionTask = RunGuardedAsync(() => LoadSelectedCatalogVersionsAsync(value));
            }

            InstallCatalogPackCommand.NotifyCanExecuteChanged();
            OpenFtbFallbackCommand.NotifyCanExecuteChanged();
        }
    }

    public ClientCatalogVersionItemViewModel? SelectedCatalogVersion
    {
        get => _selectedCatalogVersion;
        set
        {
            if (SetProperty(ref _selectedCatalogVersion, value))
            {
                InstallCatalogPackCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string CatalogInstanceName
    {
        get => _catalogInstanceName;
        set
        {
            if (SetProperty(ref _catalogInstanceName, value))
            {
                InstallCatalogPackCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IncludeOptionalPackFiles
    {
        get => _includeOptionalPackFiles;
        set => SetProperty(ref _includeOptionalPackFiles, value);
    }

    public bool HasSelectedInstance => SelectedInstance is not null;

    public bool HasSelectedBedrockShortcut => SelectedBedrockShortcut is not null;

    public bool HasBedrockShortcuts => BedrockShortcuts.Count > 0;

    public bool HasAnySelectedClient => HasSelectedInstance || HasSelectedBedrockShortcut;

    public bool IsJavaEdition
    {
        get => _isJavaEdition;
        set
        {
            if (SetProperty(ref _isJavaEdition, value))
            {
                OnPropertyChanged(nameof(IsBedrockEdition));
                NotifyCreateStateChanged();
                OpenBedrockOfficialCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBedrockEdition => !IsJavaEdition;

    public BedrockChannelChoiceViewModel? SelectedBedrockChannel
    {
        get => _selectedBedrockChannel;
        set
        {
            if (SetProperty(ref _selectedBedrockChannel, value))
            {
                NotifyCreateStateChanged();
            }
        }
    }

    public bool IsExternalLoaderSelected => SelectedLoaderVersion?.InstallKind ==
        MinecraftClientLoaderInstallKind.ExternalInstallerRequired;

    public string NewInstanceName
    {
        get => _newInstanceName;
        set
        {
            if (SetProperty(ref _newInstanceName, value))
            {
                NotifyCreateStateChanged();
            }
        }
    }

    public string NewBedrockShortcutName
    {
        get => _newBedrockShortcutName;
        set
        {
            if (SetProperty(ref _newBedrockShortcutName, value ?? string.Empty))
            {
                NotifyCreateStateChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public event EventHandler? HideLauncherRequested;

    public event EventHandler? RestoreLauncherRequested;

    internal void PublishLauncherWindowTransition(ClientLauncherWindowTransition transition)
    {
        switch (transition)
        {
            case ClientLauncherWindowTransition.None:
                return;
            case ClientLauncherWindowTransition.Minimize:
                HideLauncherRequested?.Invoke(this, EventArgs.Empty);
                return;
            case ClientLauncherWindowTransition.Restore:
                RestoreLauncherRequested?.Invoke(this, EventArgs.Empty);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition), transition, null);
        }
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, Math.Clamp(value, 0d, 1d));
    }

    public int MinimumMemoryMb
    {
        get => _minimumMemoryMb;
        set
        {
            var normalized = Math.Clamp(value, 512, MaximumMemoryMb);
            if (SetProperty(ref _minimumMemoryMb, normalized))
            {
                if (!_applyingMemoryPreset)
                {
                    MemoryMode = MinecraftClientMemoryMode.Manual;
                }

                NotifyCreateStateChanged();
            }
        }
    }

    public int MaximumMemoryMb
    {
        get => _maximumMemoryMb;
        set
        {
            var normalized = Math.Clamp(value, 512, 32_768);
            if (SetProperty(ref _maximumMemoryMb, normalized))
            {
                if (!_applyingMemoryPreset)
                {
                    MemoryMode = MinecraftClientMemoryMode.Manual;
                }

                if (MinimumMemoryMb > normalized)
                {
                    MinimumMemoryMb = normalized;
                }

                NotifyCreateStateChanged();
            }
        }
    }

    public int WindowWidth
    {
        get => _windowWidth;
        set
        {
            if (SetProperty(ref _windowWidth, Math.Clamp(value, 640, 16_384)))
            {
                RefreshResolutionChoices();
            }
        }
    }

    public int WindowHeight
    {
        get => _windowHeight;
        set
        {
            if (SetProperty(ref _windowHeight, Math.Clamp(value, 360, 16_384)))
            {
                RefreshResolutionChoices();
            }
        }
    }

    public IReadOnlyList<ClientResolutionChoice> ResolutionChoices => _resolutionChoices;

    public ClientResolutionChoice? SelectedResolution
    {
        get => ClientResolutionCatalog.Find(ResolutionChoices, WindowWidth, WindowHeight);
        set
        {
            if (value is null || !ClientResolutionCatalog.IsValid(value.Width, value.Height) ||
                value.Width == _windowWidth && value.Height == _windowHeight)
            {
                return;
            }

            _windowWidth = value.Width;
            _windowHeight = value.Height;
            OnPropertyChanged(nameof(WindowWidth));
            OnPropertyChanged(nameof(WindowHeight));
            RefreshResolutionChoices();
        }
    }

    public bool FullScreen
    {
        get => _fullScreen;
        set => SetProperty(ref _fullScreen, value);
    }

    public MinecraftClientMemoryMode MemoryMode
    {
        get => _memoryMode;
        private set
        {
            if (SetProperty(ref _memoryMode, value))
            {
                OnPropertyChanged(nameof(IsGlobalMemory));
                OnPropertyChanged(nameof(IsAutomaticMemory));
                OnPropertyChanged(nameof(IsManualMemory));
            }
        }
    }

    public bool IsGlobalMemory => MemoryMode == MinecraftClientMemoryMode.UseGlobalDefault;

    public bool IsAutomaticMemory => MemoryMode == MinecraftClientMemoryMode.Automatic;

    public bool IsManualMemory => MemoryMode == MinecraftClientMemoryMode.Manual;

    public MinecraftClientContentKind SelectedContentKind
    {
        get => _selectedContentKind;
        private set
        {
            if (SetProperty(ref _selectedContentKind, value))
            {
                CancelVisibleContentProjection();
                OnPropertyChanged(nameof(SelectedContentKindText));
            }
        }
    }

    public string SelectedContentKindText => SelectedContentKind switch
    {
        MinecraftClientContentKind.Mod => L("client.vm.content.kind.mod"),
        MinecraftClientContentKind.ResourcePack => L("client.vm.content.kind.resourcePack"),
        MinecraftClientContentKind.ShaderPack => L("client.vm.content.kind.shaderPack"),
        MinecraftClientContentKind.Save => L("client.vm.content.kind.save"),
        MinecraftClientContentKind.Screenshot => L("client.vm.content.kind.screenshot"),
        _ => L("client.vm.content.kind.content"),
    };

    public ClientContentItemViewModel? SelectedContentItem
    {
        get => _selectedContentItem;
        set
        {
            if (SetProperty(ref _selectedContentItem, value))
            {
                ToggleContentEnabledCommand.NotifyCanExecuteChanged();
                RecycleContentCommand.NotifyCanExecuteChanged();
                RestoreContentCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowRecycleBin
    {
        get => _showRecycleBin;
        private set
        {
            if (SetProperty(ref _showRecycleBin, value))
            {
                CancelVisibleContentProjection();
                OnPropertyChanged(nameof(ContentModeText));
                ImportContentCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ContentModeText => ShowRecycleBin
        ? L("client.vm.content.mode.recyclable")
        : L("client.vm.content.mode.installed");

    public string ContentStatusText
    {
        get => _contentStatusText;
        private set => SetProperty(ref _contentStatusText, value);
    }

    public bool IsContentDownloadOpen
    {
        get => _isContentDownloadOpen;
        private set => SetProperty(ref _isContentDownloadOpen, value);
    }

    public bool IsModContentDownload =>
        ContentDownloadKind == MinecraftClientContentKind.Mod;

    public MinecraftClientContentKind ContentDownloadKind
    {
        get => _contentDownloadKind;
        private set
        {
            if (SetProperty(ref _contentDownloadKind, value))
            {
                OnPropertyChanged(nameof(IsModContentDownload));
                OnPropertyChanged(nameof(ContentDownloadHeading));
                OnPropertyChanged(nameof(ContentDownloadDescription));
            }
        }
    }

    public string ContentDownloadHeading => ContentDownloadKind switch
    {
        MinecraftClientContentKind.Mod => L("client.content.download.heading.mod"),
        MinecraftClientContentKind.ResourcePack => L("client.content.download.heading.resourcePack"),
        MinecraftClientContentKind.ShaderPack => L("client.content.download.heading.shaderPack"),
        _ => L("client.content.download.heading.content"),
    };

    public string ContentDownloadDescription =>
        L("client.content.download.description", SelectedInstance?.Model.GameVersion ?? string.Empty);

    public string ContentDownloadSearchText
    {
        get => _contentDownloadSearchText;
        set => SetProperty(ref _contentDownloadSearchText, value ?? string.Empty);
    }

    public string ContentDownloadStatusText
    {
        get => _contentDownloadStatusText;
        private set => SetProperty(ref _contentDownloadStatusText, value);
    }

    public ClientContentDownloadLoaderChoice? SelectedContentDownloadLoader
    {
        get => _selectedContentDownloadLoader;
        set
        {
            if (SetProperty(ref _selectedContentDownloadLoader, value))
            {
                ContentDownloadFallbackUri = null;
            }
        }
    }

    public ClientContentDownloadProjectItemViewModel? SelectedContentDownloadProject
    {
        get => _selectedContentDownloadProject;
        set
        {
            if (SetProperty(ref _selectedContentDownloadProject, value))
            {
                ContentDownloadFallbackUri = null;
                InstallContentDownloadCommand.NotifyCanExecuteChanged();
                OpenSelectedContentProjectPageCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public Uri? ContentDownloadFallbackUri
    {
        get => _contentDownloadFallbackUri;
        private set
        {
            if (SetProperty(ref _contentDownloadFallbackUri, value))
            {
                OnPropertyChanged(nameof(HasContentDownloadFallback));
                OpenContentFallbackCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasContentDownloadFallback => ContentDownloadFallbackUri is not null;

    public ClientInstanceSettingsEditorViewModel? SettingsEditor
    {
        get => _settingsEditor;
        private set
        {
            if (ReferenceEquals(_settingsEditor, value))
            {
                return;
            }

            if (_settingsEditor is not null)
            {
                _settingsEditor.PropertyChanged -= ClientSettingsEditorOnPropertyChanged;
            }

            if (SetProperty(ref _settingsEditor, value))
            {
                if (_settingsEditor is not null)
                {
                    _settingsEditor.PropertyChanged += ClientSettingsEditorOnPropertyChanged;
                }
                else
                {
                    IsClientSettingsClosePromptOpen = false;
                }

                SaveClientSettingsCommand.NotifyCanExecuteChanged();
                ChooseClientIconCommand.NotifyCanExecuteChanged();
                ChooseClientJavaCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsClientSettingsClosePromptOpen
    {
        get => _isClientSettingsClosePromptOpen;
        private set => SetProperty(ref _isClientSettingsClosePromptOpen, value);
    }

    internal async Task InitializeForDiagnosticsAsync()
    {
        await RunGuardedAsync(InitializeAsync);
        await _loaderRefreshTask;
    }

    internal async Task ShowCatalogForDiagnosticsAsync()
    {
        await RunGuardedAsync(OpenCatalogAsync);
        await _catalogArtworkTask;
    }

    private async Task InitializeAsync()
    {
        if (IsInitialized)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var defaults = _getGlobalDefaults();
            _applyingMemoryPreset = true;
            try
            {
                _minimumMemoryMb = Math.Clamp(defaults.MinimumMemoryMb, 512, 32_768);
                _maximumMemoryMb = Math.Clamp(defaults.MaximumMemoryMb, _minimumMemoryMb, 32_768);
                _windowWidth = Math.Clamp(defaults.WindowWidth, 640, 16_384);
                _windowHeight = Math.Clamp(defaults.WindowHeight, 360, 16_384);
                RefreshResolutionChoices();
                _fullScreen = defaults.FullScreen;
                OnPropertyChanged(nameof(MinimumMemoryMb));
                OnPropertyChanged(nameof(MaximumMemoryMb));
                OnPropertyChanged(nameof(WindowWidth));
                OnPropertyChanged(nameof(WindowHeight));
                OnPropertyChanged(nameof(FullScreen));
            }
            finally
            {
                _applyingMemoryPreset = false;
            }

            ApplyMemoryMode(defaults.MemoryMode);
            await _ftbInstaller.RecoverPendingPromotionsAsync(_lifetimeCancellation.Token);
            var document = await _registry.LoadAsync();
            var bedrockDocument = await _bedrockShortcutRegistry.LoadAsync(
                _lifetimeCancellation.Token);
            Instances.Clear();
            BedrockShortcuts.Clear();
            var staleProcessMarkers = new Dictionary<Guid, MinecraftClientProcessIdentity>();
            var recoveredProcessCount = 0;
            foreach (var instance in document.Instances.OrderByDescending(item => item.LastPlayedAtUtc ?? item.CreatedAtUtc))
            {
                var item = new ClientInstanceItemViewModel(instance);
                var recoveredSession = _processRecoveryService.TryAttach(instance);
                if (recoveredSession is not null)
                {
                    item.State = MinecraftClientInstanceState.Running;
                    StartObservingSession(item, recoveredSession);
                    recoveredProcessCount++;
                }
                else
                {
                    item.State = MinecraftClientInstanceState.Ready;
                    if (MinecraftClientProcessRecoveryService.TryGetPersistedIdentity(
                            instance,
                            out var staleIdentity))
                    {
                        staleProcessMarkers[instance.Id] = staleIdentity;
                        MinecraftClientProcessRecoveryService.ClearIdentity(instance);
                    }
                }

                Instances.Add(item);
            }

            foreach (var shortcut in bedrockDocument.Shortcuts
                         .OrderByDescending(item => item.CreatedAtUtc))
            {
                BedrockShortcuts.Add(new BedrockClientShortcutItemViewModel(shortcut));
            }
            OnPropertyChanged(nameof(HasBedrockShortcuts));

            if (staleProcessMarkers.Count > 0)
            {
                await _registry.UpdateAsync(
                    storedDocument =>
                    {
                        foreach (var stored in storedDocument.Instances)
                        {
                            if (staleProcessMarkers.TryGetValue(stored.Id, out var staleIdentity) &&
                                MinecraftClientProcessRecoveryService.MarkerMatches(stored, staleIdentity))
                            {
                                MinecraftClientProcessRecoveryService.ClearIdentity(stored);
                            }
                        }

                        return true;
                    });
            }

            RefreshAccounts();
            _accountRefreshTask = RefreshAccountsInBackgroundAsync(_lifetimeCancellation.Token);
            await RefreshCatalogCoreAsync();
            if (recoveredProcessCount > 0)
            {
                StatusText = L("client.vm.status.recovered", recoveredProcessCount);
            }
            IsInitialized = true;
            ApplyInitialInstanceSelection(Instances.FirstOrDefault());

            if (SelectedInstance is null)
            {
                ApplyInitialBedrockSelection(BedrockShortcuts.FirstOrDefault());
            }

            if (!HasAnySelectedClient)
            {
                // Preserve the blank empty state as well as a create/catalog page opened by the
                // user during initialization. There is no implicit create page.
                if (!IsCreatePage && !IsCatalogPage && !IsSettingsPage)
                {
                    ShowSelectedInstance();
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal void ApplyInitialInstanceSelection(ClientInstanceItemViewModel? initialInstance)
    {
        // Initial discovery must not close a page the user explicitly opened while the
        // asynchronous catalog/registry load was still running.
        _suppressSelectedInstanceNavigation = true;
        try
        {
            SelectedInstance ??= initialInstance;
        }
        finally
        {
            _suppressSelectedInstanceNavigation = false;
        }
    }

    internal void ApplyInitialBedrockSelection(
        BedrockClientShortcutItemViewModel? initialShortcut)
    {
        _suppressSelectedInstanceNavigation = true;
        try
        {
            SelectedBedrockShortcut ??= initialShortcut;
        }
        finally
        {
            _suppressSelectedInstanceNavigation = false;
        }
    }

    private async Task RefreshCatalogAsync()
    {
        IsBusy = true;
        try
        {
            await RefreshCatalogCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshCatalogCoreAsync()
    {
        StatusText = L("client.vm.status.loadingMojang");
        ProgressValue = 0.05;
        var snapshot = await _releaseCatalog.GetStableReleasesAsync();
        _releaseSnapshot = snapshot;
        Releases.Clear();
        foreach (var release in snapshot.Releases)
        {
            Releases.Add(release);
        }

        var selectedCatalogVersion = SelectedCatalogGameVersion?.Version;
        CatalogGameVersions.Clear();
        CatalogGameVersions.Add(new ClientCatalogGameVersionChoice(null, L("client.vm.catalog.allVersions")));
        foreach (var release in snapshot.Releases)
        {
            CatalogGameVersions.Add(new ClientCatalogGameVersionChoice(release.Id, release.Id));
        }

        SelectedCatalogGameVersion = CatalogGameVersions.FirstOrDefault(choice =>
                                         string.Equals(
                                             choice.Version,
                                             selectedCatalogVersion,
                                             StringComparison.Ordinal))
                                     ?? CatalogGameVersions[0];

        SelectedRelease = Releases.FirstOrDefault();
        ProgressValue = 1d;
        StatusText = L("client.vm.status.catalogLoaded", Releases.Count, snapshot.LatestReleaseId);
    }

    private async Task RefreshLoaderChoicesAsync()
    {
        _loaderRefreshCancellation?.Cancel();
        _loaderRefreshCancellation?.Dispose();
        _loaderRefreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var cancellationToken = _loaderRefreshCancellation.Token;
        var selectedRelease = SelectedRelease;
        var snapshot = _releaseSnapshot;
        var preferredLoader = SelectedLoader?.Loader ?? MinecraftClientLoader.Vanilla;
        if (selectedRelease is null || snapshot is null)
        {
            ReplaceLoaderChoices(CreateFixedLoaderChoices(results: null, isChecking: false));
            return;
        }

        // Keep every supported product type in a stable position while its official catalog is
        // queried. A loader that has no release for this Minecraft version remains visible and is
        // disabled after the query instead of making the grid jump or falsely disappearing.
        ReplaceLoaderChoices(CreateFixedLoaderChoices(results: null, isChecking: true));
        StatusText = L("client.vm.status.checkingLoaders", selectedRelease.Id);
        var results = await QueryLoaderCatalogsAsync(
            _loaderCatalogs,
            snapshot,
            selectedRelease.Id,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(selectedRelease, SelectedRelease))
        {
            return;
        }

        ReplaceLoaderChoices(
            CreateFixedLoaderChoices(results, isChecking: false),
            preferredLoader);

        var failedLoaders = results
            .Where(static result => result.Error is not null)
            .Select(static result => result.Loader == MinecraftClientLoader.NeoForge
                ? "NeoForge"
                : result.Loader.ToString())
            .ToArray();
        var availableLoaderCount = LoaderChoices.Count(static choice => choice.IsAvailable);
        StatusText = failedLoaders.Length == 0
            ? L("client.vm.status.loaderMethods", selectedRelease.Id, availableLoaderCount)
            : L(
                "client.vm.status.loaderPartialFailure",
                selectedRelease.Id,
                availableLoaderCount,
                string.Join(", ", failedLoaders));
    }

    internal static IReadOnlyList<ClientLoaderChoiceViewModel> CreateFixedLoaderChoices(
        IReadOnlyList<LoaderCatalogQueryResult>? results,
        bool isChecking)
    {
        return FixedLoaderOrder
            .Select(loader =>
            {
                var result = results?.FirstOrDefault(item => item.Loader == loader);
                var queryFailed = loader != MinecraftClientLoader.Vanilla &&
                                  results is not null &&
                                  (result is null || result.Error is not null);
                return new ClientLoaderChoiceViewModel(
                    loader,
                    result?.Versions ?? [],
                    isChecking && loader != MinecraftClientLoader.Vanilla,
                    queryFailed);
            })
            .ToArray();
    }

    private void ReplaceLoaderChoices(
        IReadOnlyList<ClientLoaderChoiceViewModel> choices,
        MinecraftClientLoader preferredLoader = MinecraftClientLoader.Vanilla)
    {
        SelectedLoader = null;
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        LoaderChoices.Clear();
        foreach (var choice in choices)
        {
            LoaderChoices.Add(choice);
        }

        SelectedLoader = LoaderChoices.FirstOrDefault(choice =>
                             choice.Loader == preferredLoader && choice.IsAvailable)
                         ?? LoaderChoices.FirstOrDefault(choice =>
                             choice.Loader == MinecraftClientLoader.Vanilla);
    }

    internal static async Task<IReadOnlyList<LoaderCatalogQueryResult>> QueryLoaderCatalogsAsync(
        IReadOnlyList<IMinecraftLoaderCatalogProvider> providers,
        MinecraftReleaseCatalogSnapshot stableReleases,
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(stableReleases);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);

        var tasks = providers.Select(async provider =>
        {
            try
            {
                var versions = await provider.GetVersionsAsync(
                    stableReleases,
                    gameVersion,
                    cancellationToken);
                return new LoaderCatalogQueryResult(provider.Loader, versions, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return new LoaderCatalogQueryResult(provider.Loader, [], error);
            }
        });
        return await Task.WhenAll(tasks);
    }

    private async Task OpenCatalogAsync()
    {
        IsCreatePage = false;
        IsSettingsPage = false;
        IsCatalogPage = true;
        ErrorText = string.Empty;

        if (!IsBrowsableCatalogSource)
        {
            CatalogStatusText = GetUnavailableCatalogMessage(CatalogSourceId);
            return;
        }

        if (CatalogProjects.Count == 0)
        {
            await LoadCatalogAsync(append: false);
        }
    }

    private async Task SelectCatalogSourceAsync(object? parameter)
    {
        if (parameter is not string source)
        {
            return;
        }

        source = source.Trim().ToLowerInvariant();
        if (source is not ("modrinth" or "curseforge" or "ftb"))
        {
            return;
        }

        if (string.Equals(CatalogSourceId, source, StringComparison.Ordinal))
        {
            if (IsBrowsableCatalogSource && CatalogProjects.Count == 0)
            {
                await LoadCatalogAsync(append: false);
            }

            return;
        }

        CancelCatalogRequests();
        CatalogSourceId = source;
        ClearCatalogResults();
        if (source is "modrinth" or "ftb")
        {
            await LoadCatalogAsync(append: false);
            return;
        }

        CatalogStatusText = GetUnavailableCatalogMessage(source);
    }

    private static string GetUnavailableCatalogMessage(string source) => source switch
    {
        "curseforge" => L("client.vm.catalog.unavailable.curseForge"),
        "ftb" => L("client.vm.catalog.ftb.unavailable"),
        _ => L("client.vm.catalog.unavailable.default"),
    };

    private void ScheduleCatalogRefresh()
    {
        if (!IsCatalogPage || !IsBrowsableCatalogSource || _disposed)
        {
            return;
        }

        var cancellation = ReplaceCatalogBrowseCancellation();
        _catalogBrowseTask = DebouncedCatalogRefreshAsync(cancellation);
    }

    private async Task DebouncedCatalogRefreshAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellation.Token);
            await LoadCatalogPageCoreAsync(append: false, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ErrorText = LocalizeFtbValidationFailure(error);
            CatalogStatusText = L("client.vm.catalog.loadFailed");
        }
    }

    private async Task LoadCatalogAsync(bool append)
    {
        if (!IsBrowsableCatalogSource)
        {
            CatalogStatusText = GetUnavailableCatalogMessage(CatalogSourceId);
            return;
        }

        var cancellation = ReplaceCatalogBrowseCancellation();
        var task = LoadCatalogPageCoreAsync(append, cancellation);
        _catalogBrowseTask = task;
        await task;
    }

    private async Task LoadCatalogPageCoreAsync(
        bool append,
        CancellationTokenSource requestCancellation)
    {
        var cancellationToken = requestCancellation.Token;
        IsCatalogBusy = true;
        try
        {
            if (!append)
            {
                ClearCatalogResults();
            }

            if (IsFtbCatalogSource)
            {
                await LoadFtbCatalogPageAsync(requestCancellation);
                return;
            }

            var offset = append ? _catalogNextOffset : 0;
            CatalogStatusText = string.IsNullOrWhiteSpace(CatalogSearchText)
                ? L("client.vm.catalog.loadingFeatured")
                : L("client.vm.catalog.searching", CatalogSearchText.Trim());
            var request = new ModrinthClientModpackSearchRequest(
                CatalogSearchText.Trim(),
                SelectedCatalogGameVersion?.Version,
                SelectedCatalogLoader?.Loader,
                SelectedCatalogCategory?.Category,
                SelectedCatalogSort?.Sort ?? ModrinthClientModpackSort.Downloads,
                offset,
                CatalogResultLimit);
            var page = string.IsNullOrWhiteSpace(request.Query)
                ? await _modrinthCatalog.GetPopularAsync(request, cancellationToken)
                : await _modrinthCatalog.SearchAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_catalogBrowseCancellation, requestCancellation) ||
                !IsModrinthCatalogSource)
            {
                return;
            }

            var knownIds = CatalogProjects
                .Select(item => item.ProjectId)
                .ToHashSet(StringComparer.Ordinal);
            var additions = page.Projects
                .Where(project => knownIds.Add(project.ProjectId))
                .Select(project => new ClientModpackProjectItemViewModel(project))
                .ToArray();
            foreach (var item in additions)
            {
                CatalogProjects.Add(item);
            }

            _catalogNextOffset = Math.Max(offset, page.Offset) + Math.Max(1, page.Limit);
            CatalogTotalHits = page.TotalHits;
            OnPropertyChanged(nameof(CatalogResultsSummary));
            OnPropertyChanged(nameof(HasMoreCatalogResults));
            LoadMoreCatalogCommand.NotifyCanExecuteChanged();
            SelectedCatalogProject ??= CatalogProjects.FirstOrDefault();
            CatalogStatusText = CatalogProjects.Count == 0
                ? L("client.vm.catalog.noResults")
                : L("client.vm.catalog.loaded", CatalogProjects.Count);

            if (additions.Length > 0)
            {
                _catalogArtworkTask = CacheCatalogArtworkAsync(additions, cancellationToken);
            }
        }
        finally
        {
            if (ReferenceEquals(_catalogBrowseCancellation, requestCancellation))
            {
                IsCatalogBusy = false;
            }
        }
    }

    private async Task LoadFtbCatalogPageAsync(CancellationTokenSource requestCancellation)
    {
        var cancellationToken = requestCancellation.Token;
        CatalogStatusText = string.IsNullOrWhiteSpace(CatalogSearchText)
            ? L("client.vm.catalog.ftb.loadingFeatured")
            : L("client.vm.catalog.ftb.searching", CatalogSearchText.Trim());
        var page = await _ftbCatalog.BrowseAsync(
            new FtbClientCatalogRequest(
                CatalogSearchText.Trim(),
                SelectedCatalogGameVersion?.Version,
                SelectedCatalogLoader?.Loader,
                CatalogResultLimit),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_catalogBrowseCancellation, requestCancellation)
            || !IsFtbCatalogSource)
        {
            return;
        }

        var additions = page.Projects
            .Select(static project => new ClientModpackProjectItemViewModel(project))
            .ToArray();
        foreach (var item in additions)
        {
            CatalogProjects.Add(item);
        }

        _catalogNextOffset = additions.Length;
        CatalogTotalHits = page.TotalHits;
        OnPropertyChanged(nameof(CatalogResultsSummary));
        OnPropertyChanged(nameof(HasMoreCatalogResults));
        LoadMoreCatalogCommand.NotifyCanExecuteChanged();
        SelectedCatalogProject ??= CatalogProjects.FirstOrDefault();
        CatalogStatusText = CatalogProjects.Count == 0
            ? L("client.vm.catalog.ftb.noResults")
            : L("client.vm.catalog.ftb.loaded", CatalogProjects.Count);

        if (additions.Length > 0)
        {
            _catalogArtworkTask = CacheCatalogArtworkAsync(additions, cancellationToken);
        }
    }

    private async Task CacheCatalogArtworkAsync(
        IReadOnlyList<ClientModpackProjectItemViewModel> items,
        CancellationToken cancellationToken)
    {
        try
        {
            for (var offset = 0; offset < items.Count; offset += OnlineModpackArtworkCache.MaximumConcurrentDownloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = items
                    .Skip(offset)
                    .Take(OnlineModpackArtworkCache.MaximumConcurrentDownloads)
                    .Select(async item =>
                    {
                        var provider = item.SourceId == "ftb"
                            ? OnlineModpackProvider.Ftb
                            : OnlineModpackProvider.Modrinth;
                        var icon = await _artworkCache.GetOrCacheAsync(
                            provider,
                            item.IconUri,
                            cancellationToken);
                        var preview = await _artworkCache.GetOrCacheAsync(
                            provider,
                            item.PreviewImageUri,
                            cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (CatalogProjects.Contains(item))
                        {
                            item.SetCachedArtwork(icon, preview);
                        }
                    })
                    .ToArray();
                await Task.WhenAll(batch);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Debug.WriteLine($"Client catalogue artwork cache failed: {error}");
        }
    }

    private async Task LoadSelectedCatalogVersionsAsync(ClientModpackProjectItemViewModel project)
    {
        _catalogVersionCancellation?.Cancel();
        _catalogVersionCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _catalogVersionCancellation = cancellation;
        try
        {
            CatalogStatusText = L("client.vm.catalog.loadingVersions", project.Title);
            if (project.FtbProject is { } ftbProject)
            {
                CatalogVersions.Clear();
                foreach (var version in ftbProject.StableVersions)
                {
                    CatalogVersions.Add(new ClientCatalogVersionItemViewModel(version));
                }

                SelectedCatalogVersion = CatalogVersions.FirstOrDefault();
                CatalogStatusText = CatalogVersions.Count == 0
                    ? L("client.vm.catalog.ftb.noVersions", project.Title)
                    : L("client.vm.catalog.ftb.versionsLoaded", project.Title, CatalogVersions.Count);
                return;
            }

            var versions = await _modrinthCatalog.GetStableVersionsAsync(
                project.ProjectId,
                SelectedCatalogGameVersion?.Version,
                SelectedCatalogLoader?.Loader,
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(project, SelectedCatalogProject) ||
                !ReferenceEquals(cancellation, _catalogVersionCancellation))
            {
                return;
            }

            CatalogVersions.Clear();
            foreach (var version in versions)
            {
                CatalogVersions.Add(new ClientCatalogVersionItemViewModel(version));
            }

            SelectedCatalogVersion = CatalogVersions.FirstOrDefault();
            CatalogStatusText = CatalogVersions.Count == 0
                ? L("client.vm.catalog.noVersions", project.Title)
                : L("client.vm.catalog.versionsLoaded", project.Title, CatalogVersions.Count);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private bool CanInstallCatalogPack() =>
        IsBrowsableCatalogSource && !IsBusy && !IsCatalogBusy &&
        SelectedCatalogProject is not null && SelectedCatalogVersion is not null &&
        !string.IsNullOrWhiteSpace(CatalogInstanceName);

    private bool CanOpenFtbFallback() =>
        IsFtbCatalogSource && !IsBusy;

    private async Task InstallSelectedCatalogPackAsync()
    {
        var project = SelectedCatalogProject
                      ?? throw new InvalidOperationException(L("client.vm.validation.pack"));
        var version = SelectedCatalogVersion
                      ?? throw new InvalidOperationException(L("client.vm.validation.packVersion"));
        if (IsFtbCatalogSource)
        {
            await InstallSelectedFtbPackAsync(project, version);
            return;
        }

        var modrinthVersion = version.ModrinthVersion
                              ?? throw new InvalidOperationException(L("client.vm.validation.modrinthVersion"));
        var gameVersion = version.GameVersions.FirstOrDefault(value =>
                              _releaseSnapshot?.Releases.Any(release =>
                                  string.Equals(release.Id, value, StringComparison.Ordinal)) == true)
                          ?? throw new InvalidOperationException(L("client.vm.validation.catalogGameVersion"));
        var operation = BeginOperation();
        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            var defaults = _getGlobalDefaults();
            var javaMajor = _javaRecommendation.GetRecommendation(gameVersion, CoreType.Unknown).MajorVersion;
            StatusText = L("client.vm.status.preparingPackJava", project.Title, javaMajor);
            var java = await ResolveJavaAsync(javaMajor, operation.Token);
            await CacheCatalogArtworkAsync([project], operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var request = new ModrinthClientPackInstallRequest(
                Guid.NewGuid(),
                CatalogInstanceName.Trim(),
                project.ProjectId,
                modrinthVersion.VersionId,
                MemoryMode,
                MinimumMemoryMb,
                MaximumMemoryMb,
                WindowWidth,
                WindowHeight,
                FullScreen,
                IncludeOptionalFiles: IncludeOptionalPackFiles,
                EnableQuickLaunch: defaults.EnableQuickLaunch,
                HideLauncherAfterGameStarts: defaults.HideLauncherAfterGameStarts,
                ShowGameLog: defaults.ShowGameLog,
                EnableDedicatedGpu: defaults.EnableDedicatedGpu,
                EnableDiscordPresence: defaults.EnableDiscordPresence,
                JavaMajorVersion: javaMajor,
                CatalogIconImagePath: project.IconImagePath,
                CatalogPreviewImagePath: project.PreviewImagePath);
            var progress = new Progress<ModrinthClientPackInstallProgress>(value =>
            {
                StatusText = LocalizeModrinthProgress(value);
                if (value.Fraction is { } fraction)
                {
                    ProgressValue = Math.Clamp(fraction, 0d, 1d);
                }
                else if (value.TotalItems > 0)
                {
                    ProgressValue = Math.Clamp(
                        value.CompletedItems / (double)value.TotalItems,
                        0d,
                        1d);
                }
            });
            var result = await _modrinthInstaller.InstallAsync(
                request,
                java,
                progress,
                operation.Token);

            var item = new ClientInstanceItemViewModel(result.Instance)
            {
                State = MinecraftClientInstanceState.Ready,
            };
            Instances.Insert(0, item);
            SelectedInstance = item;
            ProgressValue = 1d;
            StatusText = L("client.vm.status.packInstalled", item.Name);
        }
        finally
        {
            IsBusy = false;
            CompleteOperation(operation);
            InstallCatalogPackCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task InstallSelectedFtbPackAsync(
        ClientModpackProjectItemViewModel project,
        ClientCatalogVersionItemViewModel version)
    {
        var ftbProject = project.FtbProject
                         ?? throw new InvalidOperationException(L("client.vm.validation.ftbProject"));
        var ftbVersion = version.FtbVersion
                         ?? throw new InvalidOperationException(L("client.vm.validation.packVersion"));
        var gameVersion = ftbVersion.GameVersion;
        if (string.IsNullOrWhiteSpace(gameVersion) ||
            _releaseSnapshot?.Releases.Any(release =>
                string.Equals(release.Id, gameVersion, StringComparison.Ordinal)) != true)
        {
            throw new InvalidOperationException(L("client.vm.validation.catalogGameVersion"));
        }

        var operation = BeginOperation();
        var progressTracker = new FtbInstallProgressTracker(
            new Progress<FtbClientPackInstallProgress>(value =>
            {
                StatusText = LocalizeFtbInstallProgress(value);
                if (value.Fraction is { } fraction)
                {
                    ProgressValue = Math.Clamp(fraction, 0d, 1d);
                }
                else if (value.TotalItems > 0)
                {
                    ProgressValue = Math.Clamp(
                        value.CompletedItems / (double)value.TotalItems,
                        0d,
                        1d);
                }
            }));
        progressTracker.SetStage("prepare-java");
        int? diagnosticJavaMajor = null;
        IsBusy = true;
        ClearFtbInstallFailureState();
        ErrorText = string.Empty;
        try
        {
            var defaults = _getGlobalDefaults();
            var javaMajor = TryParseFtbJavaMajor(ftbVersion.JavaVersion) ??
                            _javaRecommendation.GetRecommendation(
                                gameVersion,
                                CoreType.Unknown).MajorVersion;
            diagnosticJavaMajor = javaMajor;
            StatusText = L("client.vm.status.preparingPackJava", project.Title, javaMajor);
            var java = await ResolveJavaAsync(javaMajor, operation.Token);
            progressTracker.SetStage("cache-artwork");
            await CacheCatalogArtworkAsync([project], operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var request = new FtbClientPackInstallRequest(
                Guid.NewGuid(),
                CatalogInstanceName.Trim(),
                ftbProject.PackId,
                ftbVersion.VersionId,
                MemoryMode,
                MinimumMemoryMb,
                MaximumMemoryMb,
                WindowWidth,
                WindowHeight,
                FullScreen,
                // FTB marks some runtime dependencies as optional even though the official app
                // includes them in a normal client install. Preserve the complete client pack;
                // only server-only entries are excluded by the installer.
                IncludeOptionalFiles: true,
                EnableQuickLaunch: defaults.EnableQuickLaunch,
                HideLauncherAfterGameStarts: defaults.HideLauncherAfterGameStarts,
                ShowGameLog: defaults.ShowGameLog,
                EnableDedicatedGpu: defaults.EnableDedicatedGpu,
                EnableDiscordPresence: defaults.EnableDiscordPresence,
                JavaMajorVersion: javaMajor,
                CatalogIconImagePath: project.IconImagePath,
                CatalogPreviewImagePath: project.PreviewImagePath);
            progressTracker.SetStage("install-game");
            var result = await _ftbInstaller.InstallAsync(
                request,
                java,
                progressTracker,
                operation.Token);

            var item = new ClientInstanceItemViewModel(result.Instance)
            {
                State = MinecraftClientInstanceState.Ready,
            };
            Instances.Insert(0, item);
            SelectedInstance = item;
            ProgressValue = 1d;
            CatalogStatusText = L("client.vm.catalog.ftb.directInstalled", item.Name);
            StatusText = CatalogStatusText;
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var classification = FtbClientInstallFailurePolicy.Classify(
                error,
                progressTracker.LastStage);
            ClientOperationDiagnosticReference? diagnostic = null;
            try
            {
                diagnostic = await _clientOperationDiagnosticStore.WriteFailureAsync(
                    new ClientOperationDiagnosticWriteRequest(
                        "ftb-client-install",
                        progressTracker.LastStage,
                        classification.FailureCode,
                        error,
                        new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                            ["provider"] = "ftb",
                            ["packId"] = ftbProject.PackId.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            ["versionId"] = ftbVersion.VersionId.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            ["gameVersion"] = gameVersion,
                            ["loader"] = ftbVersion.LoaderName,
                            ["loaderVersion"] = ftbVersion.LoaderVersion,
                            ["javaVersion"] = diagnosticJavaMajor?.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                        }),
                    CancellationToken.None);
            }
            catch (Exception diagnosticError) when (diagnosticError is not OutOfMemoryException)
            {
                Debug.WriteLine($"FTB client diagnostic persistence failed: {diagnosticError.GetType().Name}");
            }

            var failureLocalizationKey = SelectFtbInstallFailureLocalizationKey(
                classification,
                progressTracker.LastStage);
            _lastFtbInstallFailureLocalizationKey = failureLocalizationKey;
            _lastFtbInstallDiagnosticId = diagnostic?.DiagnosticId;
            HasFtbInstallDiagnostic = diagnostic is not null;
            _isShowingFtbInstallFailure = true;
            ErrorText = LocalizeFtbInstallFailure(
                failureLocalizationKey,
                diagnostic?.DiagnosticId);
            CatalogStatusText = L("client.vm.catalog.ftb.fallbackAvailable");
            StatusText = CatalogStatusText;
        }
        finally
        {
            IsBusy = false;
            CompleteOperation(operation);
            InstallCatalogPackCommand.NotifyCanExecuteChanged();
            OpenFtbFallbackCommand.NotifyCanExecuteChanged();
        }
    }

    private Task OpenSelectedFtbFallbackAsync()
    {
        if (SelectedCatalogProject?.FtbProject is not null)
        {
            return OpenSelectedFtbPackAsync(SelectedCatalogProject);
        }

        CatalogStatusText = L("client.vm.catalog.ftb.appPageOpened");
        StatusText = CatalogStatusText;
        OpenOfficialFtbDownloadPage();
        return Task.CompletedTask;
    }

    private Task OpenSelectedFtbPackAsync(ClientModpackProjectItemViewModel project)
    {
        var ftbProject = project.FtbProject
                         ?? throw new InvalidOperationException(L("client.vm.validation.ftbProject"));
        var installUri = FtbAppProtocol.CreateInstallUri(ftbProject.PackId);
        if (!FtbAppProtocol.TryReadInstallPackId(installUri, out var validatedPackId)
            || validatedPackId != ftbProject.PackId)
        {
            throw new InvalidDataException(L("client.vm.validation.ftbInstallUri"));
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo(installUri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
            if (process is not null)
            {
                process.Dispose();
                CatalogStatusText = L("client.vm.catalog.ftb.appOpened", project.Title);
                StatusText = CatalogStatusText;
                return Task.CompletedTask;
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception
                                     or InvalidOperationException
                                     or FileNotFoundException)
        {
            Debug.WriteLine($"FTB protocol handler unavailable: {error}");
        }

        CatalogStatusText = L("client.vm.catalog.ftb.appMissing");
        StatusText = CatalogStatusText;
        OpenOfficialFtbDownloadPage();
        return Task.CompletedTask;
    }

    private static void OpenOfficialFtbDownloadPage()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                FtbAppProtocol.OfficialDownloadPage.AbsoluteUri)
            {
                UseShellExecute = true,
            });
            if (process is null)
            {
                throw new InvalidOperationException("The shell did not start the official FTB page.");
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception
                                     or InvalidOperationException
                                     or FileNotFoundException)
        {
            throw new InvalidOperationException(
                L("client.vm.catalog.ftb.downloadPageFailed", FtbAppProtocol.OfficialDownloadPage),
                error);
        }
    }

    private CancellationTokenSource ReplaceCatalogBrowseCancellation()
    {
        _catalogBrowseCancellation?.Cancel();
        _catalogBrowseCancellation?.Dispose();
        _catalogBrowseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        return _catalogBrowseCancellation;
    }

    private void ClearCatalogResults()
    {
        _catalogVersionCancellation?.Cancel();
        CatalogProjects.Clear();
        CatalogVersions.Clear();
        SelectedCatalogProject = null;
        SelectedCatalogVersion = null;
        CatalogTotalHits = 0;
        _catalogNextOffset = 0;
        OnPropertyChanged(nameof(CatalogResultsSummary));
        OnPropertyChanged(nameof(HasMoreCatalogResults));
    }

    private void CancelCatalogRequests()
    {
        _catalogBrowseCancellation?.Cancel();
        _catalogVersionCancellation?.Cancel();
    }

    private async Task CreateInstanceAsync()
    {
        if (IsBedrockEdition)
        {
            await CreateBedrockShortcutAsync();
            return;
        }

        var release = SelectedRelease ?? throw new InvalidOperationException(L("client.vm.validation.release"));
        var loaderChoice = SelectedLoader ?? throw new InvalidOperationException(L("client.vm.validation.loader"));

        if (!loaderChoice.IsManaged)
        {
            OpenSelectedExternalInstaller();
            return;
        }

        var loaderVersion = loaderChoice.Loader == MinecraftClientLoader.Vanilla
            ? null
            : SelectedLoaderVersion?.Version
              ?? throw new InvalidOperationException(L("client.vm.validation.loaderVersion"));
        var operation = BeginOperation();
        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            var defaults = _getGlobalDefaults();
            var javaMajor = _javaRecommendation.GetRecommendation(release.Id, CoreType.Unknown).MajorVersion;
            StatusText = L("client.vm.status.preparingJava", javaMajor);
            var java = await ResolveJavaAsync(javaMajor, operation.Token);
            var request = new MinecraftClientInstallRequest(
                Guid.NewGuid(),
                NewInstanceName.Trim(),
                MinecraftClientEdition.Java,
                release.Id,
                loaderChoice.Loader,
                loaderVersion,
                MemoryMode,
                MinimumMemoryMb,
                MaximumMemoryMb,
                WindowWidth,
                WindowHeight,
                FullScreen,
                EnableQuickLaunch: defaults.EnableQuickLaunch,
                HideLauncherAfterGameStarts: defaults.HideLauncherAfterGameStarts,
                ShowGameLog: defaults.ShowGameLog,
                EnableDedicatedGpu: defaults.EnableDedicatedGpu,
                EnableDiscordPresence: defaults.EnableDiscordPresence,
                JavaMajorVersion: javaMajor);
            var progress = new Progress<MinecraftClientInstallProgress>(value =>
            {
                StatusText = LocalizeClientInstallProgress(value);
                if (value.Fraction is { } fraction)
                {
                    ProgressValue = 0.2 + fraction * 0.8;
                }
            });
            var result = await _instanceManager.InstallAsync(
                request,
                java,
                progress,
                operation.Token);
            var item = new ClientInstanceItemViewModel(result.Instance)
            {
                State = MinecraftClientInstanceState.Ready,
            };
            Instances.Insert(0, item);
            SelectedInstance = item;
            StatusText = L("client.vm.status.instanceCreated", item.Name);
            ProgressValue = 1d;
        }
        finally
        {
            IsBusy = false;
            CompleteOperation(operation);
        }
    }

    private async Task<string> ResolveJavaAsync(int majorVersion, CancellationToken cancellationToken)
    {
        if (Directory.Exists(_paths.ClientRuntimes))
        {
            foreach (var candidate in Directory.EnumerateFiles(
                         _paths.ClientRuntimes,
                         "java.exe",
                         SearchOption.AllDirectories).Take(64))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (await AdoptiumRuntimeProvider.ReadJavaMajorVersionAsync(candidate, cancellationToken) == majorVersion)
                    {
                        ProgressValue = 0.2;
                        return candidate;
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                }
            }
        }

        var progress = new Progress<double>(value => ProgressValue = value * 0.2);
        var installed = await _javaProvider.InstallAsync(
            majorVersion,
            _paths.ClientRuntimes,
            progress,
            cancellationToken);
        return installed.JavaExecutablePath;
    }

    private Task OpenAccountLoginChoiceAsync()
    {
        IsAccountPanelOpen = true;
        IsAccountLoginChoiceOpen = true;
        MicrosoftAccountLoginHint = string.Empty;
        ClearDeviceCodePrompt();
        return Task.CompletedTask;
    }

    private async Task AddAccountInBrowserAsync()
    {
        ReplaceAccountLoginCancellation();
        IsBusy = true;
        try
        {
            StatusText = L("client.vm.status.accountBrowserLogin");
            var session = await _authenticationService.AddAccountInteractivelyAsync(
                MicrosoftAccountLoginHint.Trim(),
                _accountLoginCancellation!.Token);
            RefreshAccounts();
            SelectedAccount = Accounts.FirstOrDefault(account => account.Id == session.AccountId);
            IsAccountLoginChoiceOpen = false;
            MicrosoftAccountLoginHint = string.Empty;
            ClearDeviceCodePrompt();
            StatusText = L("client.vm.status.accountAdded", session.Username);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddAccountWithDeviceCodeAsync()
    {
        ReplaceAccountLoginCancellation();
        IsBusy = true;
        try
        {
            ClearDeviceCodePrompt();
            StatusText = L("client.vm.status.accountDeviceCodeStarting");
            var session = await _authenticationService.AddAccountWithDeviceCodeAsync(
                PublishDeviceCodePromptAsync,
                _accountLoginCancellation!.Token);
            RefreshAccounts();
            SelectedAccount = Accounts.FirstOrDefault(account => account.Id == session.AccountId);
            IsAccountLoginChoiceOpen = false;
            MicrosoftAccountLoginHint = string.Empty;
            ClearDeviceCodePrompt();
            StatusText = L("client.vm.status.accountAdded", session.Username);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PublishDeviceCodePromptAsync(MinecraftDeviceCodePrompt prompt)
    {
        async Task PublishAsync()
        {
            DeviceCode = prompt.UserCode;
            DeviceCodeVerificationUri = prompt.VerificationUri;
            DeviceCodeExpiresAtUtc = prompt.ExpiresAtUtc;
            IsDeviceCodePromptVisible = true;
            StatusText = L("client.vm.status.accountDeviceCodeWaiting");
            await Task.CompletedTask;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(PublishAsync).Task.Unwrap();
        }
        else
        {
            await PublishAsync();
        }
    }

    private void ToggleAccountPanel()
    {
        IsAccountPanelOpen = !IsAccountPanelOpen;
        if (!IsAccountPanelOpen && !IsBusy)
        {
            IsAccountLoginChoiceOpen = false;
            MicrosoftAccountLoginHint = string.Empty;
            ClearDeviceCodePrompt();
        }
    }

    private void ReplaceAccountLoginCancellation()
    {
        _accountLoginCancellation?.Cancel();
        _accountLoginCancellation?.Dispose();
        _accountLoginCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
    }

    private void CancelAccountLogin()
    {
        _accountLoginCancellation?.Cancel();
        IsAccountLoginChoiceOpen = false;
        MicrosoftAccountLoginHint = string.Empty;
        ClearDeviceCodePrompt();
    }

    internal static bool IsValidMicrosoftAccountLoginHint(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 254 &&
               !normalized.Any(static character =>
                   char.IsControl(character) || char.IsWhiteSpace(character));
    }

    private void ClearDeviceCodePrompt()
    {
        IsDeviceCodePromptVisible = false;
        DeviceCode = string.Empty;
        DeviceCodeVerificationUri = null;
        DeviceCodeExpiresAtUtc = null;
    }

    private void CopyDeviceCode()
    {
        if (!IsDeviceCodePromptVisible || string.IsNullOrWhiteSpace(DeviceCode))
        {
            return;
        }

        try
        {
            Clipboard.SetText(DeviceCode);
            StatusText = L("client.vm.status.accountDeviceCodeCopied");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ErrorText = error.Message;
        }
    }

    private void OpenDeviceLoginPage()
    {
        if (!IsDeviceCodePromptVisible || DeviceCodeVerificationUri is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(DeviceCodeVerificationUri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
    }

    private void ChooseSkinFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = L("client.account.skin.choose"),
            Filter = L("client.account.skin.fileFilter"),
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            AddExtension = true,
            DefaultExt = ".png",
        };
        if (dialog.ShowDialog() == true)
        {
            if (!MinecraftSkin3DView.TryLoadSkinFileForPreview(dialog.FileName, out _))
            {
                ErrorText = L("client.vm.validation.skinFile");
                StatusText = L("client.vm.status.operationFailed");
                return;
            }

            SelectedSkinFilePath = Path.GetFullPath(dialog.FileName);
            StatusText = L("client.vm.status.skinPreviewReady", Path.GetFileName(dialog.FileName));
        }
    }

    private async Task SaveSkinAsync()
    {
        var account = SelectedAccount
            ?? throw new InvalidOperationException(L("client.vm.validation.account"));
        var selectedAccountId = account.Id;
        var filePath = SelectedSkinFilePath;
        var variant = SkinPreviewVariant;
        var previousSkinId = account.ActiveSkin?.Id;
        var previousSkinUri = account.ActiveSkin?.TextureUri;
        IsBusy = true;
        try
        {
            StatusText = L("client.vm.status.skinSaving", account.Username);
            await _authenticationService.UpdateSkinAsync(
                selectedAccountId,
                variant,
                filePath,
                _lifetimeCancellation.Token);
            RefreshAccounts();
            SelectedAccount = Accounts.FirstOrDefault(item => item.Id == selectedAccountId);
            StatusText = L("client.vm.status.skinSaved", account.Username);
        }
        catch (MinecraftProfileSynchronizationPendingException)
        {
            StatusText = L("client.vm.status.profileSynchronizationPending");
            ScheduleProfileSynchronization(
                selectedAccountId,
                profile => profile.ActiveSkin is { } activeSkin
                    && activeSkin.Variant == variant
                    && (filePath is null
                        || previousSkinId is null
                        || !string.Equals(activeSkin.Id, previousSkinId, StringComparison.Ordinal)
                        || activeSkin.TextureUri != previousSkinUri));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplySelectedCapeAsync()
    {
        var account = SelectedAccount
            ?? throw new InvalidOperationException(L("client.vm.validation.account"));
        var cape = SelectedCape
            ?? throw new InvalidOperationException(L("client.vm.validation.cape"));
        var selectedAccountId = account.Id;
        IsBusy = true;
        try
        {
            StatusText = L("client.vm.status.capeSaving", cape.Alias);
            await _authenticationService.SetActiveCapeAsync(
                selectedAccountId,
                cape.Id,
                _lifetimeCancellation.Token);
            RefreshAccounts();
            SelectedAccount = Accounts.FirstOrDefault(item => item.Id == selectedAccountId);
            StatusText = L("client.vm.status.capeSaved", cape.Alias);
        }
        catch (MinecraftProfileSynchronizationPendingException)
        {
            StatusText = L("client.vm.status.profileSynchronizationPending");
            ScheduleProfileSynchronization(
                selectedAccountId,
                profile => profile.Capes.Any(candidate =>
                    candidate.IsActive
                    && string.Equals(candidate.Id, cape.Id, StringComparison.Ordinal)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisableCapeAsync()
    {
        var account = SelectedAccount
            ?? throw new InvalidOperationException(L("client.vm.validation.account"));
        var selectedAccountId = account.Id;
        IsBusy = true;
        try
        {
            StatusText = L("client.vm.status.capeDisabling");
            await _authenticationService.SetActiveCapeAsync(
                selectedAccountId,
                capeId: null,
                _lifetimeCancellation.Token);
            RefreshAccounts();
            SelectedAccount = Accounts.FirstOrDefault(item => item.Id == selectedAccountId);
            StatusText = L("client.vm.status.capeDisabled");
        }
        catch (MinecraftProfileSynchronizationPendingException)
        {
            StatusText = L("client.vm.status.profileSynchronizationPending");
            ScheduleProfileSynchronization(
                selectedAccountId,
                profile => profile.Capes.All(candidate => !candidate.IsActive));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyAccountCosmeticStateChanged()
    {
        OnPropertyChanged(nameof(SkinPreviewVariant));
        OnPropertyChanged(nameof(IsClassicSkinPreview));
        OnPropertyChanged(nameof(IsSlimSkinPreview));
        OnPropertyChanged(nameof(SelectedSkinFilePath));
        OnPropertyChanged(nameof(SelectedSkinFileName));
        OnPropertyChanged(nameof(SkinPreviewTextureSource));
        OnPropertyChanged(nameof(SelectedCape));
        SelectClassicSkinCommand.NotifyCanExecuteChanged();
        SelectSlimSkinCommand.NotifyCanExecuteChanged();
        ChooseSkinFileCommand.NotifyCanExecuteChanged();
        SaveSkinCommand.NotifyCanExecuteChanged();
        ApplySelectedCapeCommand.NotifyCanExecuteChanged();
        DisableCapeCommand.NotifyCanExecuteChanged();
    }

    private void BeginLoadOfficialSkinTexture(MinecraftClientAccountInfo? account)
    {
        _skinTextureLoadCancellation?.Cancel();
        _skinTextureLoadCancellation?.Dispose();
        _skinTextureLoadCancellation = null;
        _selectedPlayerSkinTexture = null;
        _selectedPlayerHeadTexture = null;
        OnPropertyChanged(nameof(SelectedPlayerSkinTexture));
        OnPropertyChanged(nameof(SelectedPlayerHeadTexture));
        OnPropertyChanged(nameof(SkinPreviewTextureSource));

        if (account?.ActiveSkin?.TextureUri is not { } textureUri)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _skinTextureLoadCancellation = cancellation;
        _ = LoadOfficialSkinTextureAsync(account.Id, textureUri, cancellation.Token);
    }

    private async Task LoadOfficialSkinTextureAsync(
        string accountId,
        Uri textureUri,
        CancellationToken cancellationToken)
    {
        const int maximumTextureBytes = 1024 * 1024;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, textureUri);
            using var response = await _authenticationHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > maximumTextureBytes)
            {
                return;
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var block = new byte[16 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(block, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maximumTextureBytes)
                {
                    return;
                }

                await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
            }

            buffer.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = buffer;
            image.EndInit();
            if (!MinecraftSkin3DView.TryNormalizeSkinForPreview(image, out var normalizedSkin))
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            void Publish()
            {
                if (!cancellationToken.IsCancellationRequested &&
                    SelectedAccount is { Id: var selectedId, ActiveSkin.TextureUri: var selectedUri } &&
                    string.Equals(selectedId, accountId, StringComparison.Ordinal) &&
                    selectedUri == textureUri)
                {
                    _selectedPlayerSkinTexture = normalizedSkin;
                    _selectedPlayerHeadTexture = CreatePlayerHeadTexture(normalizedSkin);
                    OnPropertyChanged(nameof(SelectedPlayerSkinTexture));
                    OnPropertyChanged(nameof(SelectedPlayerHeadTexture));
                    OnPropertyChanged(nameof(SkinPreviewTextureSource));
                }
            }

            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(Publish);
            }
            else
            {
                Publish();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is
            HttpRequestException or
            IOException or
            NotSupportedException or
            InvalidOperationException or
            ArgumentException or
            FormatException or
            System.Runtime.InteropServices.ExternalException)
        {
            Debug.WriteLine($"Official Minecraft skin preview deferred: {error.GetType().Name}");
        }
    }

    internal static BitmapSource CreatePlayerHeadTexture(BitmapSource normalizedSkin)
    {
        ArgumentNullException.ThrowIfNull(normalizedSkin);
        if (normalizedSkin.PixelWidth != MinecraftSkinLayout.TextureSize ||
            normalizedSkin.PixelHeight != MinecraftSkinLayout.TextureSize)
        {
            throw new ArgumentException("A normalized 64 × 64 Minecraft skin is required.", nameof(normalizedSkin));
        }

        const int texturePixels = 64;
        const int bytesPerPixel = 4;
        const int textureStride = texturePixels * bytesPerPixel;
        const int headPixels = 8;
        const int headStride = headPixels * bytesPerPixel;
        var premultipliedSkin = normalizedSkin.Format == PixelFormats.Pbgra32
            ? normalizedSkin
            : new FormatConvertedBitmap(normalizedSkin, PixelFormats.Pbgra32, null, 0);
        var skinPixels = new byte[textureStride * texturePixels];
        var composedHead = new byte[headStride * headPixels];
        premultipliedSkin.CopyPixels(skinPixels, textureStride, 0);

        for (var y = 0; y < headPixels; y++)
        {
            for (var x = 0; x < headPixels; x++)
            {
                var faceOffset = ((y + 8) * texturePixels + x + 8) * bytesPerPixel;
                var overlayOffset = ((y + 8) * texturePixels + x + 40) * bytesPerPixel;
                var destinationOffset = (y * headPixels + x) * bytesPerPixel;
                var overlayAlpha = skinPixels[overlayOffset + 3];
                var inverseOverlayAlpha = 255 - overlayAlpha;

                for (var channel = 0; channel < 3; channel++)
                {
                    composedHead[destinationOffset + channel] = (byte)Math.Min(
                        byte.MaxValue,
                        skinPixels[overlayOffset + channel] +
                        ((skinPixels[faceOffset + channel] * inverseOverlayAlpha + 127) / 255));
                }

                composedHead[destinationOffset + 3] = (byte)Math.Min(
                    byte.MaxValue,
                    overlayAlpha + ((skinPixels[faceOffset + 3] * inverseOverlayAlpha + 127) / 255));
            }
        }

        // Keep the source as the native Minecraft 8 x 8 pixel grid. A 32-DIP Image
        // maps each texel to an integer number of device pixels at common 100-200%
        // Windows DPI steps when WPF uses nearest-neighbour scaling.
        var head = BitmapSource.Create(
            headPixels,
            headPixels,
            96d,
            96d,
            PixelFormats.Pbgra32,
            null,
            composedHead,
            headStride);
        head.Freeze();
        return head;
    }

    private async Task RemoveSelectedAccountAsync()
    {
        var account = SelectedAccount
            ?? throw new InvalidOperationException(L("client.vm.validation.account"));
        IsBusy = true;
        try
        {
            StatusText = L("client.vm.status.accountRemoving", account.Username);
            await _authenticationService.SignOutAsync(account.Id);
            RefreshAccounts();
            StatusText = L("client.vm.status.accountRemoved", account.Username);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SignOutAllAccountsAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = L("client.vm.status.accountsSigningOut");
            await _authenticationService.SignOutAllAsync();
            RefreshAccounts();
            StatusText = L("client.vm.status.accountsSignedOut");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LaunchSelectedAsync()
    {
        var item = SelectedInstance ?? throw new InvalidOperationException(L("client.vm.validation.instance"));
        if (item.IsRunning)
        {
            return;
        }

        var account = SelectedAccount;
        AuthenticatedMinecraftSession authenticated;
        IsBusy = true;
        item.State = MinecraftClientInstanceState.Starting;
        try
        {
            if (account is null)
            {
                StatusText = L("client.vm.status.noAccount");
                item.State = MinecraftClientInstanceState.Ready;
                IsAccountPanelOpen = true;
                IsAccountLoginChoiceOpen = true;
                return;
            }
            else
            {
                StatusText = L("client.vm.status.verifyingAccount", account.Username);
                authenticated = await _authenticationService.AuthenticateAsync(account.Id);
            }

            item.Model.AccountId = authenticated.AccountId;
            item.ClearGameLog();
            await UpdateStoredInstanceAsync(
                item.Id,
                stored => stored.AccountId = authenticated.AccountId,
                CancellationToken.None);
            var session = await _launchCoordinator.LaunchAsync(
                item.Model,
                _getGlobalDefaults(),
                authenticated,
                async (identity, cancellationToken) =>
                    await UpdateStoredInstanceAsync(
                        item.Id,
                        stored => MinecraftClientProcessRecoveryService.RecordIdentity(stored, identity),
                        cancellationToken));
            var identity = session.PersistentIdentity
                           ?? throw new InvalidOperationException(
                               "The launch coordinator returned without a recovery identity.");
            MinecraftClientProcessRecoveryService.RecordIdentity(item.Model, identity);

            var launcherVisibilityReady = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            StartObservingSession(item, session, launcherVisibilityReady.Task);
            try
            {
                item.State = MinecraftClientInstanceState.Running;
                StatusText = L("client.vm.status.launched", item.Name);
                if (_launcherWindowLifecycle.CompleteLaunch(
                        item.Id,
                        launchSucceeded: true,
                        item.Model.HideLauncherAfterGameStarts) ==
                    ClientLauncherWindowTransition.Minimize)
                {
                    PublishLauncherWindowTransition(ClientLauncherWindowTransition.Minimize);
                }
            }
            finally
            {
                // A process may exit immediately after Process.Start. Do not let its observer
                // request a restore before this successful launch has issued its minimize request.
                launcherVisibilityReady.TrySetResult();
            }
        }
        catch
        {
            _ = _launcherWindowLifecycle.CompleteLaunch(
                item.Id,
                launchSucceeded: false,
                item.Model.HideLauncherAfterGameStarts);
            item.State = MinecraftClientInstanceState.Failed;
            throw;
        }
        finally
        {
            IsBusy = false;
            LaunchCommand.NotifyCanExecuteChanged();
            QuickLaunchCommand.NotifyCanExecuteChanged();
            StopClientCommand.NotifyCanExecuteChanged();
            DeleteClientInstanceCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task QuickLaunchAsync(object? parameter)
    {
        if (parameter is not ClientInstanceItemViewModel item || !item.Model.EnableQuickLaunch)
        {
            return;
        }

        SelectedInstance = item;
        await LaunchSelectedAsync();
    }

    private async Task ObserveSessionAsync(
        ClientInstanceItemViewModel item,
        MinecraftClientProcessSession session,
        Task launcherVisibilityReady)
    {
        ArgumentNullException.ThrowIfNull(launcherVisibilityReady);
        await Task.Yield();
        try
        {
            await launcherVisibilityReady.WaitAsync(_lifetimeCancellation.Token);
            var result = await session.Completion.WaitAsync(_lifetimeCancellation.Token);
            var endedAtUtc = DateTimeOffset.UtcNow;
            var elapsedSeconds = Math.Max(0, (long)result.PlayTime.TotalSeconds);
            var persistedTotal = await _registry.UpdateAsync(
                document =>
                {
                    var stored = document.Instances.FirstOrDefault(candidate => candidate.Id == item.Id)
                                 ?? throw new InvalidDataException(
                                     "The Minecraft client instance is missing from the registry.");
                    stored.LastPlayedAtUtc = endedAtUtc;
                    stored.TotalPlayTimeSeconds = checked(
                        stored.TotalPlayTimeSeconds + elapsedSeconds);
                    if (session.PersistentIdentity is { } identity &&
                        MinecraftClientProcessRecoveryService.MarkerMatches(stored, identity))
                    {
                        MinecraftClientProcessRecoveryService.ClearIdentity(stored);
                    }

                    return stored.TotalPlayTimeSeconds;
                },
                CancellationToken.None);
            item.Model.LastPlayedAtUtc = endedAtUtc;
            item.Model.TotalPlayTimeSeconds = persistedTotal;
            if (session.PersistentIdentity is { } completedIdentity &&
                MinecraftClientProcessRecoveryService.MarkerMatches(item.Model, completedIdentity))
            {
                MinecraftClientProcessRecoveryService.ClearIdentity(item.Model);
            }

            item.RefreshMetadata();
            item.State = result.ExitCode == 0
                ? MinecraftClientInstanceState.Ready
                : MinecraftClientInstanceState.Failed;
            StatusText = result.ExitCode == 0
                ? L("client.vm.status.ended", item.Name)
                : L("client.vm.status.endedCode", item.Name, result.ExitCode);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Manager shutdown intentionally detaches. The durable marker remains so the next
            // manager process can safely reattach while Minecraft keeps running.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            item.State = MinecraftClientInstanceState.Failed;
            ErrorText = L("client.vm.error.monitor", error.Message);
        }
        finally
        {
            var launcherTransition = _launcherWindowLifecycle.CompleteSession(item.Id);
            lock (_runningSessionGate)
            {
                _runningSessions.Remove(item.Id);
            }

            await session.DisposeAsync();
            if (launcherTransition == ClientLauncherWindowTransition.Restore)
            {
                PublishLauncherWindowTransition(ClientLauncherWindowTransition.Restore);
            }
            LaunchCommand.NotifyCanExecuteChanged();
            QuickLaunchCommand.NotifyCanExecuteChanged();
            StopClientCommand.NotifyCanExecuteChanged();
            OpenClientSettingsCommand.NotifyCanExecuteChanged();
            DeleteClientInstanceCommand.NotifyCanExecuteChanged();
            NotifyContentMutationStateChanged();
        }
    }

    private void StartObservingSession(
        ClientInstanceItemViewModel item,
        MinecraftClientProcessSession session,
        Task? launcherVisibilityReady = null)
    {
        if (item.Model.ShowGameLog)
        {
            if (session.LogCaptureAvailable)
            {
                session.OutputReceived += (_, line) => item.QueueGameLogLine(line);
            }
            else
            {
                item.QueueGameLogLine(L("client.vm.log.reattachedUnavailable"));
            }
        }

        Task observer;
        lock (_runningSessionGate)
        {
            if (_disposed || _lifetimeCancellation.IsCancellationRequested)
            {
                _ = session.DisposeAsync().AsTask();
                return;
            }

            if (_runningSessions.TryGetValue(item.Id, out var existing) &&
                !ReferenceEquals(existing, session))
            {
                throw new InvalidOperationException("This Minecraft client instance is already being monitored.");
            }

            _runningSessions[item.Id] = session;
            observer = ObserveSessionAsync(
                item,
                session,
                launcherVisibilityReady ?? Task.CompletedTask);
            _sessionObserverTasks.Add(observer);
        }

        _ = observer.ContinueWith(
            completed =>
            {
                lock (_runningSessionGate)
                {
                    _sessionObserverTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task StopSelectedAsync()
    {
        var item = SelectedInstance ?? throw new InvalidOperationException(L("client.vm.validation.instance"));
        MinecraftClientProcessSession? session;
        lock (_runningSessionGate)
        {
            _runningSessions.TryGetValue(item.Id, out session);
        }

        if (session is null)
        {
            return;
        }

        item.State = MinecraftClientInstanceState.Stopping;
        StatusText = L("client.vm.status.stopping", item.Name);
        await session.StopAsync(TimeSpan.FromSeconds(10));
    }

    private bool CanDeleteSelectedInstance()
    {
        if (IsBusy || SelectedInstance is not { IsRunning: false } instance)
        {
            return false;
        }

        lock (_runningSessionGate)
        {
            return !_runningSessions.ContainsKey(instance.Id);
        }
    }

    private async Task DeleteSelectedInstanceAsync()
    {
        var instance = SelectedInstance
                       ?? throw new InvalidOperationException(L("client.vm.validation.instance"));
        await EnsureInstanceDeletionAllowedAsync(instance, _lifetimeCancellation.Token);

        var answer = DarkMessageBox.Show(
            L("client.delete.confirm", instance.Name, instance.Model.DirectoryPath),
            L("client.delete.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // Recheck after the modal confirmation. A launch request or another manager process
            // may have changed the instance while the user was reading the warning.
            await EnsureInstanceDeletionAllowedAsync(instance, CancellationToken.None);
            StatusText = L("client.vm.status.instanceDeleting", instance.Name);
            await _instanceManager.DeleteAsync(instance.Id, CancellationToken.None);

            var removedIndex = Instances.IndexOf(instance);
            _contentRefreshCoordinator.CancelCurrent();
            _contentItems.ReplaceAll([]);
            SelectedContentItem = null;
            Instances.Remove(instance);
            SelectedInstance = Instances.Count == 0
                ? null
                : Instances[Math.Min(Math.Max(removedIndex, 0), Instances.Count - 1)];
            StatusText = L("client.vm.status.instanceDeleted", instance.Name);
            if (SelectedInstance is null)
            {
                ShowSelectedInstance();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EnsureInstanceDeletionAllowedAsync(
        ClientInstanceItemViewModel instance,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(instance, SelectedInstance) || instance.IsRunning)
        {
            throw new InvalidOperationException(L("client.vm.validation.deleteWhileRunning"));
        }

        lock (_runningSessionGate)
        {
            if (_runningSessions.ContainsKey(instance.Id))
            {
                throw new InvalidOperationException(L("client.vm.validation.deleteWhileRunning"));
            }
        }

        var document = await _registry.LoadAsync(cancellationToken);
        var stored = document.Instances.FirstOrDefault(candidate => candidate.Id == instance.Id)
                     ?? throw new InvalidDataException(L("client.vm.validation.instance"));
        if (_processRecoveryService.IsMatchingProcessActive(stored))
        {
            throw new InvalidOperationException(L("client.vm.validation.deleteWhileRunning"));
        }
    }

    private async Task UpdateStoredInstanceAsync(
        Guid instanceId,
        Action<MinecraftClientInstance> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _registry.UpdateAsync(
            document =>
            {
                var stored = document.Instances.FirstOrDefault(item => item.Id == instanceId)
                             ?? throw new InvalidDataException(
                                 "The Minecraft client instance is missing from the registry.");
                update(stored);
                return true;
            },
            cancellationToken);
    }

    private void RefreshAccounts()
    {
        var selectedId = SelectedAccount?.Id;
        Accounts.Clear();
        foreach (var account in _authenticationService.GetAccounts())
        {
            Accounts.Add(account);
        }

        SelectedAccount = Accounts.FirstOrDefault(account => account.Id == selectedId)
                          ?? Accounts.FirstOrDefault();
        OnPropertyChanged(nameof(HasSelectedAccount));
        OnPropertyChanged(nameof(SelectedPlayerName));
        OnPropertyChanged(nameof(AccountButtonAccessibleName));
        OnPropertyChanged(nameof(SelectedPlayerUuid));
        OnPropertyChanged(nameof(SelectedPlayerSkinUri));
        OnPropertyChanged(nameof(SelectedAccountExpiresAtUtc));
        OnPropertyChanged(nameof(SelectedAccountCapes));
        OnPropertyChanged(nameof(SelectedAccountExpirySummary));
        RemoveSelectedAccountCommand.NotifyCanExecuteChanged();
        SignOutAllAccountsCommand.NotifyCanExecuteChanged();
    }

    private void ScheduleProfileSynchronization(
        string accountId,
        Func<MinecraftClientAccountInfo, bool> hasExpectedState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(hasExpectedState);
        if (_disposed || _lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        var synchronization = SynchronizeAcceptedProfileAsync(
            accountId,
            hasExpectedState,
            _lifetimeCancellation.Token);
        lock (_profileSynchronizationGate)
        {
            _profileSynchronizationTasks.Add(synchronization);
        }

        _ = synchronization.ContinueWith(
            completed =>
            {
                lock (_profileSynchronizationGate)
                {
                    _profileSynchronizationTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task SynchronizeAcceptedProfileAsync(
        string accountId,
        Func<MinecraftClientAccountInfo, bool> hasExpectedState,
        CancellationToken cancellationToken)
    {
        // These are absolute checkpoints after the accepted mutation. Only the GET profile
        // operation is repeated; the skin/cape mutation is never sent again.
        TimeSpan elapsed = TimeSpan.Zero;
        foreach (var checkpoint in new[]
                 {
                     TimeSpan.FromSeconds(5),
                     TimeSpan.FromSeconds(15),
                     TimeSpan.FromSeconds(30),
                 })
        {
            try
            {
                await Task.Delay(checkpoint - elapsed, cancellationToken);
                elapsed = checkpoint;
                var refreshed = await _authenticationService.RefreshProfileAsync(
                    accountId,
                    cancellationToken);
                if (!hasExpectedState(refreshed))
                {
                    continue;
                }

                RefreshAccounts();
                StatusText = L("client.vm.status.profileSynchronized");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Debug.WriteLine(
                    $"Minecraft profile synchronization deferred: {error.GetType().Name}");
            }
        }
    }

    private async Task RefreshAccountsInBackgroundAsync(CancellationToken cancellationToken)
    {
        // The Java access token is short-lived. Refresh shortly before it expires using only the
        // DPAPI/MSAL token caches. Transient network failures leave the account intact and are
        // retried on the next cycle; only the authentication service's authoritative failures
        // remove an account and require interaction again.
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var changed = await RefreshAccountSetIndependentlyAsync(
                    Accounts.Select(account => account.Id).ToArray(),
                    (accountId, token) => _authenticationService.RefreshIfExpiringAsync(
                        accountId,
                        TimeSpan.FromMinutes(20),
                        token),
                    cancellationToken);

                if (changed)
                {
                    RefreshAccounts();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Debug.WriteLine($"Minecraft account background refresh deferred: {error.GetType().Name}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal static async Task<bool> RefreshAccountSetIndependentlyAsync(
        IEnumerable<string> accountIds,
        Func<string, CancellationToken, Task<bool>> refreshAccount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        ArgumentNullException.ThrowIfNull(refreshAccount);
        var changed = false;
        foreach (var accountId in accountIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                changed |= await refreshAccount(accountId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is KeyNotFoundException or UnauthorizedAccessException)
            {
                // The authentication service has already removed or invalidated this account.
                // Refresh the visible list after every remaining account had its own chance.
                changed = true;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // DNS, timeout, transport and ambiguous upstream failures are isolated to the
                // affected account. Never let account A defer renewal for B or C.
                Debug.WriteLine(
                    $"Minecraft account background refresh deferred: {error.GetType().Name}");
            }
        }

        return changed;
    }

    private void ShowCreatePage()
    {
        IsSettingsPage = false;
        IsCatalogPage = false;
        IsCreatePage = true;
        ErrorText = string.Empty;
    }

    private void ShowSelectedInstance()
    {
        IsSettingsPage = false;
        IsCatalogPage = false;
        IsCreatePage = false;
    }

    private async Task OpenClientSettingsAsync()
    {
        var instance = SelectedInstance ?? throw new InvalidOperationException(L("client.vm.validation.instance"));
        var settings = await _instanceSettingsService.GetSettingsAsync(instance.Id);
        if (!ReferenceEquals(instance, SelectedInstance))
        {
            return;
        }

        SettingsEditor = CreateClientSettingsEditor(instance, settings);
        IsClientSettingsClosePromptOpen = false;
        IsCreatePage = false;
        IsCatalogPage = false;
        IsSettingsPage = true;
        StatusText = L("client.vm.status.editingSettings", instance.Name);
    }

    private async Task SaveClientSettingsAsync()
    {
        var instance = SelectedInstance ?? throw new InvalidOperationException(L("client.vm.validation.instance"));
        var editor = SettingsEditor ?? throw new InvalidOperationException(L("client.vm.validation.settingsMissing"));
        if (editor.InstanceId != instance.Id)
        {
            throw new InvalidOperationException(L("client.vm.validation.settingsChangedInstance"));
        }

        if (editor.HasErrors)
        {
            ErrorText = editor.ValidationSummary;
            StatusText = L("client.vm.status.fixSettings");
            return;
        }

        IsBusy = true;
        try
        {
            var updated = await _instanceSettingsService.UpdateAsync(instance.Id, editor.BuildUpdate());
            instance.ReplaceModel(updated);
            var savedSettings = await _instanceSettingsService.GetSettingsAsync(instance.Id);
            if (!ReferenceEquals(instance, SelectedInstance))
            {
                SettingsEditor = null;
                IsSettingsPage = false;
                return;
            }

            SettingsEditor = CreateClientSettingsEditor(instance, savedSettings);
            StatusText = L("client.vm.status.settingsSaved", instance.Name);
            ErrorText = string.Empty;
            IsClientSettingsClosePromptOpen = false;
            IsSettingsPage = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CloseClientSettings()
    {
        if (SettingsEditor?.IsDirty == true)
        {
            IsClientSettingsClosePromptOpen = true;
            StatusText = L("client.vm.status.unsavedSettings");
            return;
        }

        DiscardClientSettingsChanges();
    }

    private void DiscardClientSettingsChanges()
    {
        IsClientSettingsClosePromptOpen = false;
        SettingsEditor = null;
        IsSettingsPage = false;
        IsCatalogPage = false;
        IsCreatePage = false;
    }

    private void CancelClientSettingsClose()
    {
        IsClientSettingsClosePromptOpen = false;
        StatusText = L("client.vm.status.continueSettings");
    }

    private void ClientSettingsEditorOnPropertyChanged(object? sender, PropertyChangedEventArgs error)
    {
        if (error.PropertyName is nameof(ClientInstanceSettingsEditorViewModel.IsDirty)
            or nameof(ClientInstanceSettingsEditorViewModel.HasErrors)
            or nameof(ClientInstanceSettingsEditorViewModel.CanSave))
        {
            SaveClientSettingsCommand.NotifyCanExecuteChanged();
        }
    }

    private ClientInstanceSettingsEditorViewModel CreateClientSettingsEditor(
        ClientInstanceItemViewModel instance,
        MinecraftClientInstanceSettingsUpdate settings) =>
        new(
            instance.Id,
            settings,
            mode => ResolveClientSettingsMemoryRange(instance.Model, mode));

    private ClientMemoryRangePreview ResolveClientSettingsMemoryRange(
        MinecraftClientInstance source,
        MinecraftClientMemoryMode mode)
    {
        var probe = new MinecraftClientInstance
        {
            Edition = source.Edition,
            DirectoryPath = source.DirectoryPath,
            Loader = source.Loader,
            MemoryMode = mode,
            MinimumMemoryMb = source.MinimumMemoryMb,
            MaximumMemoryMb = source.MaximumMemoryMb,
        };
        var resolution = _memoryRecommendationService.Resolve(probe, _getGlobalDefaults());
        return new ClientMemoryRangePreview(
            resolution.MinimumMemoryMb,
            resolution.MaximumMemoryMb);
    }

    private void ChooseClientIcon()
    {
        if (SettingsEditor is null)
        {
            return;
        }

        var picker = new OpenFileDialog
        {
            Title = L("client.vm.dialog.selectIcon"),
            CheckFileExists = true,
            Multiselect = false,
            Filter = L("client.vm.dialog.iconFilter"),
        };
        if (picker.ShowDialog() == true)
        {
            SettingsEditor.IconImagePath = picker.FileName;
        }
    }

    private async Task ChooseClientJavaAsync()
    {
        if (SettingsEditor is not { } editor || SelectedInstance is not { } instance
            || editor.InstanceId != instance.Id || instance.IsRunning)
        {
            return;
        }

        var picker = new OpenFileDialog
        {
            Title = L("client.vm.dialog.selectJava"),
            CheckFileExists = true,
            Multiselect = false,
            Filter = L("client.vm.dialog.javaFilter"),
        };
        if (picker.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var detectedMajorVersion = await _instanceSettingsService.ValidateJavaExecutableAsync(
                instance.Id,
                picker.FileName,
                _lifetimeCancellation.Token);
            if (!ReferenceEquals(editor, SettingsEditor)
                || !ReferenceEquals(instance, SelectedInstance))
            {
                return;
            }

            editor.JavaExecutablePath = picker.FileName;
            StatusText = L("client.vm.status.javaValidated", detectedMajorVersion);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CreateBedrockShortcutAsync()
    {
        var choice = SelectedBedrockChannel
                     ?? throw new InvalidOperationException(
                         L("client.vm.validation.bedrockUnsupported"));
        if (!IsValidBedrockShortcutName(NewBedrockShortcutName))
        {
            throw new InvalidOperationException(L("client.vm.validation.bedrockName"));
        }

        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            var shortcut = await _bedrockShortcutRegistry.AddAsync(
                new BedrockClientShortcut
                {
                    Id = Guid.NewGuid(),
                    DisplayName = NewBedrockShortcutName.Trim(),
                    Channel = choice.Channel,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                },
                _lifetimeCancellation.Token);
            var item = new BedrockClientShortcutItemViewModel(shortcut);
            BedrockShortcuts.Insert(0, item);
            OnPropertyChanged(nameof(HasBedrockShortcuts));
            SelectedBedrockShortcut = item;
            StatusText = L("client.vm.status.bedrockShortcutCreated", item.Name);

            if (_bedrockOfficialHandoff.TryOpenStore(choice.Channel))
            {
                StatusText = L(
                    "client.vm.status.bedrockOfficialInstallerOpened",
                    item.ChannelText);
            }
            else
            {
                ErrorText = L("client.vm.validation.bedrockHandoffFailed");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenSelectedBedrockOfficial()
    {
        var shortcut = SelectedBedrockShortcut
                       ?? throw new InvalidOperationException(
                           L("client.vm.validation.bedrockUnsupported"));
        ErrorText = string.Empty;
        if (!_bedrockOfficialHandoff.TryOpenStore(shortcut.Channel))
        {
            ErrorText = L("client.vm.validation.bedrockHandoffFailed");
            StatusText = L("client.vm.status.operationFailed");
            return;
        }

        StatusText = L(
            "client.vm.status.bedrockOfficialInstallerOpened",
            shortcut.ChannelText);
    }

    private async Task DeleteSelectedBedrockShortcutAsync()
    {
        var shortcut = SelectedBedrockShortcut
                       ?? throw new InvalidOperationException(
                           L("client.vm.validation.bedrockUnsupported"));
        var answer = DarkMessageBox.Show(
            L("client.bedrock.remove.confirm", shortcut.Name),
            L("client.bedrock.remove.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var removedIndex = BedrockShortcuts.IndexOf(shortcut);
            var removed = await _bedrockShortcutRegistry.RemoveAsync(
                shortcut.Id,
                CancellationToken.None);
            BedrockShortcuts.Remove(shortcut);
            OnPropertyChanged(nameof(HasBedrockShortcuts));
            SelectedBedrockShortcut = BedrockShortcuts.Count == 0
                ? null
                : BedrockShortcuts[Math.Min(
                    Math.Max(removedIndex, 0),
                    BedrockShortcuts.Count - 1)];
            StatusText = L("client.vm.status.bedrockShortcutRemoved", removed.DisplayName);
            if (!HasAnySelectedClient)
            {
                ShowSelectedInstance();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool IsValidBedrockShortcutName(string? value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrWhiteSpace(trimmed) &&
               trimmed.Length <= 128 &&
               !trimmed.Any(char.IsControl);
    }

    private bool CanCreateInstance() =>
        !IsBusy && (IsJavaEdition
            ? SelectedRelease is not null && SelectedLoader?.IsManaged == true &&
              !string.IsNullOrWhiteSpace(NewInstanceName) &&
              (SelectedLoader.Loader == MinecraftClientLoader.Vanilla ||
               SelectedLoaderVersion is not null)
            : SelectedBedrockChannel is not null &&
              IsValidBedrockShortcutName(NewBedrockShortcutName));

    private bool CanLaunchSelected() =>
        !IsBusy && SelectedInstance is { IsRunning: false };

    private bool CanQuickLaunch(object? parameter) =>
        !IsBusy && parameter is ClientInstanceItemViewModel
        {
            IsRunning: false,
            Model.EnableQuickLaunch: true,
        };

    private bool CanStopSelected() => SelectedInstance?.IsRunning == true;

    private void OpenBedrockOfficial()
    {
        ErrorText = string.Empty;
        var choice = SelectedBedrockChannel;
        if (choice is null || !_bedrockOfficialHandoff.TryOpenStore(choice.Channel))
        {
            ErrorText = L("client.vm.validation.bedrockHandoffFailed");
            StatusText = L("client.vm.status.operationFailed");
            return;
        }

        StatusText = L("client.vm.status.bedrockOfficialInstallerOpened", choice.Name);
    }

    private void NotifyCreateStateChanged()
    {
        CreateInstanceCommand.NotifyCanExecuteChanged();
        RefreshCatalogCommand.NotifyCanExecuteChanged();
    }

    private CancellationTokenSource BeginOperation()
    {
        CancelCurrentOperation();
        _operationCancellation?.Dispose();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        return _operationCancellation;
    }

    private void CompleteOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_operationCancellation, operation))
        {
            _operationCancellation = null;
        }

        operation.Dispose();
    }

    private void CancelCurrentOperation() => _operationCancellation?.Cancel();

    private void OpenSelectedInstanceFolder()
    {
        if (SelectedInstance is not { } item)
        {
            return;
        }

        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(item.Model.DirectoryPath);
        Process.Start(start);
    }

    private void OpenClientDiagnosticsFolder()
    {
        if (!HasFtbInstallDiagnostic)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(_clientOperationDiagnosticStore.DirectoryPath))
            {
                HasFtbInstallDiagnostic = false;
                ShowClientDiagnosticsFolderError();
                return;
            }

            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(_clientOperationDiagnosticStore.DirectoryPath);
            using var process = Process.Start(start);
            if (process is null)
            {
                ShowClientDiagnosticsFolderError();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Debug.WriteLine($"Opening the FTB client diagnostics folder failed: {error.GetType().Name}");
            ShowClientDiagnosticsFolderError();
        }
    }

    private void ShowClientDiagnosticsFolderError()
    {
        _isShowingFtbInstallFailure = false;
        ErrorText = L("client.vm.catalog.ftb.diagnosticsFolderOpenFailed");
    }

    private void OpenSelectedExternalInstaller()
    {
        if (SelectedLoaderVersion?.OfficialSourceUri is not { } uri)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void ApplyMemoryMode(MinecraftClientMemoryMode mode)
    {
        MemoryMode = mode;
        _applyingMemoryPreset = true;
        try
        {
            if (mode == MinecraftClientMemoryMode.UseGlobalDefault)
            {
                var defaults = _getGlobalDefaults();
                _minimumMemoryMb = Math.Clamp(defaults.MinimumMemoryMb, 512, 32_768);
                _maximumMemoryMb = Math.Clamp(defaults.MaximumMemoryMb, _minimumMemoryMb, 32_768);
                OnPropertyChanged(nameof(MinimumMemoryMb));
                OnPropertyChanged(nameof(MaximumMemoryMb));
            }
            else if (mode == MinecraftClientMemoryMode.Automatic)
            {
                ApplyAutomaticMemoryRecommendation();
            }
        }
        finally
        {
            _applyingMemoryPreset = false;
        }
    }

    private void ApplyAutomaticMemoryRecommendation()
    {
        var probe = new MinecraftClientInstance
        {
            Edition = MinecraftClientEdition.Java,
            DirectoryPath = _paths.ClientStaging,
            Loader = SelectedLoader?.Loader ?? MinecraftClientLoader.Vanilla,
            MemoryMode = MinecraftClientMemoryMode.Automatic,
        };
        var resolution = _memoryRecommendationService.Resolve(probe, _getGlobalDefaults());
        _minimumMemoryMb = resolution.MinimumMemoryMb;
        _maximumMemoryMb = resolution.MaximumMemoryMb;
        OnPropertyChanged(nameof(MinimumMemoryMb));
        OnPropertyChanged(nameof(MaximumMemoryMb));
    }

    private void ApplyResolutionPreset(object? parameter)
    {
        if (parameter is not string preset)
        {
            return;
        }

        var parts = preset.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
        {
            return;
        }

        WindowWidth = width;
        WindowHeight = height;
    }

    private void RefreshResolutionChoices()
    {
        _resolutionChoices = ClientResolutionCatalog.CreateChoices(
            _windowWidth,
            _windowHeight);
        OnPropertyChanged(nameof(ResolutionChoices));
        OnPropertyChanged(nameof(SelectedResolution));
    }

    private void OpenSelectedContentFolder(object? parameter)
    {
        if (SelectedInstance is not { } item || parameter is not string folder || !IsAllowedContentFolder(folder))
        {
            return;
        }

        var path = SafePath.CombineUnderRoot(item.Model.DirectoryPath, folder);
        Directory.CreateDirectory(path);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        Process.Start(start);
    }

    private bool CanOpenContentDownload(object? parameter) =>
        SelectedInstance is { IsRunning: false } &&
        !IsBusy &&
        TryGetDownloadContentKind(parameter, out _);

    private async Task OpenContentDownloadAsync(object? parameter)
    {
        if (SelectedInstance is not { IsRunning: false } instance ||
            !TryGetDownloadContentKind(parameter, out var kind))
        {
            throw new InvalidOperationException(L("client.vm.validation.contentDownload"));
        }

        ContentDownloadKind = kind;
        ContentDownloadSearchText = string.Empty;
        ContentDownloadResults.Clear();
        SelectedContentDownloadProject = null;
        ContentDownloadFallbackUri = null;
        var preferredLoader = kind == MinecraftClientContentKind.Mod &&
                              IsSupportedDownloadLoader(instance.Model.Loader)
            ? instance.Model.Loader
            : (MinecraftClientLoader?)null;
        SelectedContentDownloadLoader =
            ContentDownloadLoaders.FirstOrDefault(choice => choice.Loader == preferredLoader)
            ?? ContentDownloadLoaders[0];
        ContentDownloadStatusText = L("client.vm.contentDownload.initial");
        IsContentDownloadOpen = true;
        OnPropertyChanged(nameof(ContentDownloadDescription));
        await SearchContentDownloadAsync();
    }

    private void CloseContentDownload()
    {
        if (IsBusy)
        {
            return;
        }

        IsContentDownloadOpen = false;
        ContentDownloadResults.Clear();
        SelectedContentDownloadProject = null;
        ContentDownloadFallbackUri = null;
        ContentDownloadStatusText = L("client.vm.contentDownload.initial");
    }

    private async Task SearchContentDownloadAsync()
    {
        var instance = SelectedInstance
                       ?? throw new InvalidOperationException(L("client.vm.validation.instance"));
        if (!IsContentDownloadOpen)
        {
            return;
        }

        var instanceId = instance.Id;
        var kind = ContentDownloadKind;
        var loader = ResolveRequestedContentLoader(instance, kind);
        var operation = BeginOperation();
        IsBusy = true;
        try
        {
            ContentDownloadFallbackUri = null;
            ContentDownloadStatusText = L("client.vm.contentDownload.searching");
            var page = await _modrinthContentCatalog.SearchAsync(
                new ModrinthClientContentSearchRequest(
                    kind,
                    ContentDownloadSearchText.Trim(),
                    instance.Model.GameVersion,
                    loader,
                    ModrinthClientContentSort.Relevance,
                    Offset: 0,
                    Limit: 20),
                operation.Token);
            if (!IsContentDownloadOpen ||
                SelectedInstance?.Id != instanceId ||
                ContentDownloadKind != kind)
            {
                return;
            }

            ContentDownloadResults.Clear();
            foreach (var project in page.Projects)
            {
                ContentDownloadResults.Add(CreateContentDownloadProjectItem(
                    project,
                    instance.Model.GameVersion));
            }

            SelectedContentDownloadProject = ContentDownloadResults.FirstOrDefault();
            ContentDownloadStatusText = ContentDownloadResults.Count == 0
                ? L("client.vm.contentDownload.noResults")
                : L(
                    "client.vm.contentDownload.found",
                    ContentDownloadResults.Count,
                    page.TotalHits);
        }
        finally
        {
            IsBusy = false;
            CompleteOperation(operation);
        }
    }

    private bool CanInstallSelectedContentDownload() =>
        IsContentDownloadOpen &&
        SelectedInstance is { IsRunning: false } &&
        SelectedContentDownloadProject is not null &&
        !IsBusy;

    private async Task InstallSelectedContentDownloadAsync()
    {
        var instance = SelectedInstance
                       ?? throw new InvalidOperationException(L("client.vm.validation.instance"));
        var project = SelectedContentDownloadProject
                      ?? throw new InvalidOperationException(L("client.vm.validation.contentDownloadProject"));
        var kind = ContentDownloadKind;
        var loader = ResolveRequestedContentLoader(instance, kind);
        var effectiveLoader = loader;
        var operation = BeginOperation();
        IsBusy = true;
        try
        {
            ContentDownloadFallbackUri = null;
            ContentDownloadStatusText = L("client.vm.contentDownload.planning", project.Title);
            await EnsureContentMutationAllowedAsync(instance, operation.Token);
            var plan = await _modrinthContentInstaller.PlanAsync(
                project.ProjectId,
                kind,
                instance.Model.GameVersion,
                loader,
                operation.Token);

            if (!plan.CanInstallAutomatically)
            {
                ApplyContentDownloadFallback(plan.Fallbacks, plan.Project.ProjectPageUri);
                return;
            }

            if (kind == MinecraftClientContentKind.Mod &&
                plan.RequiredLoader is { } requiredLoader)
            {
                effectiveLoader = requiredLoader;
                if (instance.Model.Loader != requiredLoader)
                {
                    ContentDownloadFallbackUri =
                        plan.Artifacts.FirstOrDefault()?.VersionPageUri ??
                        plan.Project.ProjectPageUri;
                    ContentDownloadStatusText = L(
                        "client.vm.contentDownload.installingLoader",
                        requiredLoader);
                    try
                    {
                        var switched = await SwitchContentLoaderAsync(
                            instance,
                            requiredLoader,
                            operation.Token);
                        instance.ReplaceModel(switched.Instance);
                        SelectedContentDownloadLoader =
                            ContentDownloadLoaders.FirstOrDefault(
                                choice => choice.Loader == requiredLoader)
                            ?? SelectedContentDownloadLoader;
                        ContentDownloadFallbackUri = null;
                    }
                    catch (Exception error) when (
                        error is HttpRequestException or IOException or InvalidDataException or
                            InvalidOperationException or UnauthorizedAccessException or
                            NotSupportedException or ArgumentException or
                            System.ComponentModel.Win32Exception)
                    {
                        ContentDownloadStatusText = L(
                            "client.vm.contentDownload.loaderInstallFailed",
                            requiredLoader);
                        return;
                    }
                }
            }

            var progress = new Progress<ModrinthClientContentInstallProgress>(value =>
            {
                ContentDownloadStatusText = value.Stage switch
                {
                    "commit" => L("client.vm.contentDownload.committing"),
                    "complete" => L("client.vm.contentDownload.finishing"),
                    _ => L(
                        "client.vm.contentDownload.downloading",
                        Math.Min(value.CompletedItems + 1, Math.Max(1, value.TotalItems)),
                        Math.Max(1, value.TotalItems)),
                };
            });
            await EnsureContentMutationAllowedAsync(instance, operation.Token);
            var result = await _modrinthContentInstaller.InstallAsync(
                new ModrinthClientContentInstallRequest(
                    instance.Model.DirectoryPath,
                    project.ProjectId,
                    kind,
                    instance.Model.GameVersion,
                    effectiveLoader),
                progress,
                operation.Token);
            if (!result.Installed)
            {
                ApplyContentDownloadFallback(result.Fallbacks, result.Plan.Project.ProjectPageUri);
                return;
            }

            SelectedContentKind = kind;
            await RefreshContentAsync();
            ContentDownloadStatusText = L(
                "client.vm.contentDownload.installed",
                result.InstalledEntries.Count,
                project.Title);
        }
        finally
        {
            IsBusy = false;
            CompleteOperation(operation);
        }
    }

    private async Task<MinecraftClientInstallResult> SwitchContentLoaderAsync(
        ClientInstanceItemViewModel instance,
        MinecraftClientLoader requiredLoader,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedDownloadLoader(requiredLoader))
        {
            throw new NotSupportedException(
                $"{requiredLoader} is not supported by the managed content loader switcher.");
        }

        var snapshot = _releaseSnapshot;
        if (snapshot is null || !snapshot.Releases.Any(release => string.Equals(
                release.Id,
                instance.Model.GameVersion,
                StringComparison.Ordinal)))
        {
            snapshot = await _releaseCatalog.GetStableReleasesAsync(cancellationToken);
        }

        var provider = _loaderCatalogs.FirstOrDefault(candidate =>
                           candidate.Loader == requiredLoader)
                       ?? throw new NotSupportedException(
                           $"No official catalog is configured for {requiredLoader}.");
        var versions = await provider.GetVersionsAsync(
            snapshot,
            instance.Model.GameVersion,
            cancellationToken);
        var selectedVersion = versions.FirstOrDefault(version =>
                                  version.Loader == requiredLoader &&
                                  string.Equals(
                                      version.GameVersion,
                                      instance.Model.GameVersion,
                                      StringComparison.Ordinal) &&
                                  version.InstallKind == MinecraftClientLoaderInstallKind.Managed &&
                                  version.ReleaseChannel is MinecraftLoaderReleaseChannel.Stable or
                                      MinecraftLoaderReleaseChannel.Recommended)
                              ?? throw new InvalidOperationException(
                                  $"No official stable {requiredLoader} release supports Minecraft {instance.Model.GameVersion}.");

        var javaPath = instance.Model.JavaExecutablePath;
        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
        {
            var javaMajor = instance.Model.JavaMajorVersion ??
                            _javaRecommendation.GetRecommendation(
                                instance.Model.GameVersion,
                                CoreType.Unknown).MajorVersion;
            javaPath = await ResolveJavaAsync(javaMajor, cancellationToken);
        }

        var progress = new Progress<MinecraftClientInstallProgress>(_ =>
            ContentDownloadStatusText = L(
                "client.vm.contentDownload.installingLoader",
                requiredLoader));
        return await _instanceManager.SwitchLoaderAsync(
            instance.Id,
            requiredLoader,
            selectedVersion.Version,
            javaPath,
            progress,
            cancellationToken);
    }

    private void ApplyContentDownloadFallback(
        IReadOnlyList<ModrinthClientContentFallback> fallbacks,
        Uri projectPageUri)
    {
        var fallback = fallbacks.FirstOrDefault();
        ContentDownloadFallbackUri =
            fallback?.DirectDownloadUri ??
            fallback?.VersionPageUri ??
            projectPageUri;
        ContentDownloadStatusText = L(
            "client.vm.contentDownload.fallback",
            fallback?.DisplayName ?? SelectedContentDownloadProject?.Title ?? string.Empty);
    }

    private void OpenSelectedContentProjectPage()
    {
        if (SelectedContentDownloadProject is { ProjectPageUri: { } uri })
        {
            OpenVerifiedModrinthUri(uri);
        }
    }

    private void OpenContentFallback()
    {
        if (ContentDownloadFallbackUri is { } uri)
        {
            OpenVerifiedModrinthUri(uri);
        }
    }

    private static void OpenVerifiedModrinthUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsAllowedModrinthHost(uri.IdnHost))
        {
            throw new InvalidOperationException("Only official Modrinth HTTPS links can be opened.");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static bool IsAllowedModrinthHost(string host) =>
        string.Equals(host, "modrinth.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "www.modrinth.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "cdn.modrinth.com", StringComparison.OrdinalIgnoreCase);

    private MinecraftClientLoader? ResolveRequestedContentLoader(
        ClientInstanceItemViewModel instance,
        MinecraftClientContentKind kind)
    {
        if (kind != MinecraftClientContentKind.Mod)
        {
            return null;
        }

        return SelectedContentDownloadLoader?.Loader ??
               (IsSupportedDownloadLoader(instance.Model.Loader)
                   ? instance.Model.Loader
                   : null);
    }

    private static bool TryGetDownloadContentKind(
        object? parameter,
        out MinecraftClientContentKind kind)
    {
        if (parameter is string text &&
            Enum.TryParse(text, ignoreCase: true, out kind) &&
            kind is MinecraftClientContentKind.Mod or
                MinecraftClientContentKind.ResourcePack or
                MinecraftClientContentKind.ShaderPack)
        {
            return true;
        }

        kind = default;
        return false;
    }

    private static bool IsSupportedDownloadLoader(MinecraftClientLoader loader) =>
        loader is MinecraftClientLoader.Forge or
            MinecraftClientLoader.Fabric or
            MinecraftClientLoader.Quilt or
            MinecraftClientLoader.NeoForge;

    private static bool IsAllowedContentFolder(string folder) => folder is
        "mods" or "resourcepacks" or "shaderpacks" or "saves" or "screenshots" or "logs";

    private async Task SelectContentKindAsync(object? parameter)
    {
        if (parameter is not string text ||
            !Enum.TryParse<MinecraftClientContentKind>(text, ignoreCase: true, out var kind) ||
            !Enum.IsDefined(kind))
        {
            return;
        }

        SelectedContentKind = kind;
        ShowRecycleBin = false;
        await RefreshContentAsync();
    }

    private async Task ToggleRecycleBinAsync()
    {
        ShowRecycleBin = !ShowRecycleBin;
        await RefreshContentAsync();
    }

    private async Task RefreshContentAsync()
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            _contentRefreshCoordinator.CancelCurrent();
            _contentItems.ReplaceAll([]);
            SelectedContentItem = null;
            ContentStatusText = L("client.vm.content.noInstance");
            return;
        }

        var request = new ContentRefreshRequest(
            instance.Id,
            instance.Model.DirectoryPath,
            SelectedContentKind,
            ShowRecycleBin,
            SelectedContentKindText,
            ContentModeText);
        ContentStatusText = L("client.vm.content.refresh.loading", request.KindText);
        LatestOperationResult<ContentRefreshProjection> result;
        long requestGeneration = 0;
        try
        {
            result = await _contentRefreshCoordinator.RunLatestAsync(
                context =>
                {
                    requestGeneration = context.Generation;
                    return ScanContentAsync(request, context.CancellationToken);
                });
        }
        catch (OperationCanceledException)
        {
            // Selecting another instance/category or closing the workspace intentionally makes
            // this result stale. The newer request owns the visible status and projection.
            return;
        }
        catch (Exception error) when (
            error is not OutOfMemoryException
            && !_contentRefreshCoordinator.IsCurrent(requestGeneration))
        {
            // A filesystem error can race cancellation. Once a newer selection owns the view,
            // the stale operation must not overwrite its status through RunGuardedAsync.
            return;
        }

        if (!_contentRefreshCoordinator.IsCurrent(result.Generation)
            || SelectedInstance?.Id != request.InstanceId
            || SelectedContentKind != request.Kind
            || ShowRecycleBin != request.ShowRecycleBin)
        {
            return;
        }

        _contentItems.ReplaceAll(result.Value.Items);
        SelectedContentItem = _contentItems.FirstOrDefault();
        ContentStatusText = _contentItems.Count == 0
            ? L("client.vm.content.refresh.empty", request.ModeText, request.KindText)
            : result.Value.LimitReached
                ? L("client.vm.content.refresh.limit", request.ModeText, _contentItems.Count)
                : L("client.vm.content.refresh.loaded", request.ModeText, _contentItems.Count);
    }

    private async Task<ContentRefreshProjection> ScanContentAsync(
        ContentRefreshRequest request,
        CancellationToken cancellationToken)
        => await Task.Run(async () =>
        {
            await _contentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var manager = new MinecraftClientContentManager(request.InstanceDirectory);
                IReadOnlyList<MinecraftClientContentEntry> entries;
                var limitReached = false;
                if (request.ShowRecycleBin)
                {
                    entries = await manager.ListRecycleBinAsync(request.Kind, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    var snapshot = await manager.ListAsync(
                            request.Kind,
                            includeDisabled: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                    entries = snapshot.Entries;
                    limitReached = snapshot.ItemLimitReached;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var projection = new ClientContentItemViewModel[entries.Count];
                for (var index = 0; index < entries.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    projection[index] = new ClientContentItemViewModel(entries[index]);
                }

                return new ContentRefreshProjection(projection, limitReached);
            }
            finally
            {
                _contentGate.Release();
            }
        }, cancellationToken).ConfigureAwait(false);

    private void CancelVisibleContentProjection()
    {
        _contentRefreshCoordinator.CancelCurrent();
        _contentItems.ReplaceAll([]);
        SelectedContentItem = null;
    }

    private async Task ImportContentAsync()
    {
        var instance = SelectedInstance ?? throw new InvalidOperationException(L("client.vm.validation.instance"));
        var sourcePaths = SelectContentImportSources();
        if (sourcePaths.Count == 0)
        {
            return;
        }

        var operation = BeginOperation();
        IsBusy = true;
        var gateAcquired = false;
        try
        {
            await _contentGate.WaitAsync(operation.Token);
            gateAcquired = true;
            await EnsureContentMutationAllowedAsync(instance, operation.Token);
            using var manager = new MinecraftClientContentManager(instance.Model.DirectoryPath);
            var progress = new Progress<MinecraftClientContentProgress>(value =>
            {
                ContentStatusText = LocalizeContentProgress(value);
                ProgressValue = value.TotalItems <= 0
                    ? 0
                    : value.CompletedItems / (double)value.TotalItems;
            });
            await manager.ImportAsync(
                new MinecraftClientContentImportRequest(SelectedContentKind, sourcePaths),
                progress,
                operation.Token);
        }
        finally
        {
            if (gateAcquired)
            {
                _contentGate.Release();
            }

            IsBusy = false;
            CompleteOperation(operation);
        }

        await RefreshContentAsync();
    }

    private IReadOnlyList<string> SelectContentImportSources()
    {
        if (SelectedContentKind == MinecraftClientContentKind.Save)
        {
            var folderPicker = new OpenFolderDialog
            {
                Title = L("client.vm.dialog.importWorldTitle"),
                Multiselect = false,
            };
            return folderPicker.ShowDialog() == true ? [folderPicker.FolderName] : [];
        }

        var filePicker = new OpenFileDialog
        {
            Title = L("client.vm.dialog.importTitle", SelectedContentKindText),
            Multiselect = true,
            CheckFileExists = true,
            Filter = SelectedContentKind switch
            {
                MinecraftClientContentKind.Mod => "Minecraft Mod (*.jar)|*.jar",
                MinecraftClientContentKind.ResourcePack => L("client.vm.dialog.importResourcePackFilter"),
                MinecraftClientContentKind.ShaderPack => L("client.vm.dialog.importShaderPackFilter"),
                MinecraftClientContentKind.Screenshot => L("client.vm.dialog.importImageFilter"),
                _ => L("client.vm.dialog.importAllFilter"),
            },
        };
        return filePicker.ShowDialog() == true ? filePicker.FileNames : [];
    }

    private async Task ToggleSelectedContentEnabledAsync()
    {
        var instance = SelectedInstance ?? throw new InvalidOperationException(L("client.vm.validation.instance"));
        var content = SelectedContentItem ?? throw new InvalidOperationException(L("client.vm.validation.content"));
        IsBusy = true;
        await _contentGate.WaitAsync();
        try
        {
            await EnsureContentMutationAllowedAsync(instance, CancellationToken.None);
            using var manager = new MinecraftClientContentManager(instance.Model.DirectoryPath);
            await manager.SetEnabledAsync(content.Entry.Key, !content.IsEnabled);
        }
        finally
        {
            _contentGate.Release();
            IsBusy = false;
        }

        await RefreshContentAsync();
    }

    private async Task RecycleSelectedContentAsync()
    {
        var instance = SelectedInstance ?? throw new InvalidOperationException(L("client.vm.validation.instance"));
        var content = SelectedContentItem ?? throw new InvalidOperationException(L("client.vm.validation.content"));
        IsBusy = true;
        await _contentGate.WaitAsync();
        try
        {
            await EnsureContentMutationAllowedAsync(instance, CancellationToken.None);
            using var manager = new MinecraftClientContentManager(instance.Model.DirectoryPath);
            await manager.RemoveAsync(content.Entry.Key, permanently: false);
        }
        finally
        {
            _contentGate.Release();
            IsBusy = false;
        }

        await RefreshContentAsync();
    }

    private async Task RestoreSelectedContentAsync()
    {
        var instance = SelectedInstance ?? throw new InvalidOperationException(L("client.vm.validation.instance"));
        var content = SelectedContentItem ?? throw new InvalidOperationException(L("client.vm.validation.recyclableContent"));
        IsBusy = true;
        await _contentGate.WaitAsync();
        try
        {
            await EnsureContentMutationAllowedAsync(instance, CancellationToken.None);
            using var manager = new MinecraftClientContentManager(instance.Model.DirectoryPath);
            await manager.RestoreAsync(content.Entry.Key);
        }
        finally
        {
            _contentGate.Release();
            IsBusy = false;
        }

        await RefreshContentAsync();
    }

    private bool CanMutateSelectedContent() =>
        !IsBusy && SelectedInstance is { IsRunning: false };

    private void NotifyContentMutationStateChanged()
    {
        ImportContentCommand.NotifyCanExecuteChanged();
        ToggleContentEnabledCommand.NotifyCanExecuteChanged();
        RecycleContentCommand.NotifyCanExecuteChanged();
        RestoreContentCommand.NotifyCanExecuteChanged();
    }

    private async Task EnsureContentMutationAllowedAsync(
        ClientInstanceItemViewModel instance,
        CancellationToken cancellationToken)
    {
        if (instance.IsRunning)
        {
            throw new InvalidOperationException(L("client.vm.validation.contentWhileRunning"));
        }

        // Re-read the durable marker immediately before a filesystem mutation. A UI state flag
        // alone is insufficient after manager restart or when another GUI process launched the
        // same instance. PID reuse is rejected by matching PID, process creation time and the
        // normalized java executable path.
        var document = await _registry.LoadAsync(cancellationToken);
        var stored = document.Instances.FirstOrDefault(candidate => candidate.Id == instance.Id)
                     ?? throw new InvalidDataException(L("client.vm.validation.instance"));
        if (_processRecoveryService.IsMatchingProcessActive(stored))
        {
            throw new InvalidOperationException(L("client.vm.validation.contentWhileRunning"));
        }
    }

    private async Task RunGuardedAsync(Func<Task> operation)
    {
        try
        {
            _isShowingFtbInstallFailure = false;
            ErrorText = string.Empty;
            await operation();
        }
        catch (OperationCanceledException)
        {
            StatusText = L("client.vm.status.operationCanceled");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ErrorText = LocalizeFtbValidationFailure(error);
            StatusText = L("client.vm.status.operationFailed");
        }
    }

    private void ClearFtbInstallFailureState()
    {
        _lastFtbInstallFailureLocalizationKey = null;
        _lastFtbInstallDiagnosticId = null;
        _isShowingFtbInstallFailure = false;
        HasFtbInstallDiagnostic = false;
    }

    internal static string LocalizeFtbValidationFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!FtbClientValidation.TryGetFailure(error, out var failure))
        {
            return error.Message;
        }

        return failure switch
        {
            FtbClientValidationFailure.ResultLimitOutOfRange =>
                L("client.vm.validation.ftbResultLimit"),
            FtbClientValidationFailure.QueryTooLong =>
                L("client.vm.validation.ftbQueryLength"),
            FtbClientValidationFailure.GameVersionTooLong =>
                L("client.vm.validation.ftbGameVersionLength"),
            FtbClientValidationFailure.InvalidPackId =>
                L("client.vm.validation.ftbPackId"),
            _ => error.Message,
        };
    }

    private static string LocalizeClientInstallProgress(MinecraftClientInstallProgress progress) =>
        progress.Stage switch
        {
            "prepare" => L("client.vm.progress.install.prepare"),
            "download" => L("client.vm.progress.install.download"),
            "base" => L("client.vm.progress.install.base"),
            "verify" => L("client.vm.progress.install.verify"),
            "complete" => L("client.vm.progress.install.complete"),
            _ => L("client.vm.progress.working"),
        };

    private static string LocalizeModrinthProgress(ModrinthClientPackInstallProgress progress) =>
        progress.Stage switch
        {
            "download-pack" => L("client.vm.progress.modrinth.downloadPack"),
            "inspect-pack" => L("client.vm.progress.modrinth.inspectPack"),
            "install-game" => L("client.vm.progress.modrinth.installGame"),
            "extract-overrides" => L("client.vm.progress.modrinth.extractOverrides"),
            "download-content" => L(
                "client.vm.progress.modrinth.downloadContent",
                progress.CompletedItems,
                progress.TotalItems),
            "complete" => L("client.vm.progress.modrinth.complete"),
            _ => L("client.vm.progress.working"),
        };

    private static string LocalizeFtbInstallProgress(FtbClientPackInstallProgress progress) =>
        progress.Stage switch
        {
            "install-game" => L("client.vm.progress.ftb.installGame"),
            "download-content" => L(
                "client.vm.progress.ftb.downloadContent",
                progress.CompletedItems,
                progress.TotalItems),
            "complete" => L("client.vm.progress.ftb.complete"),
            _ => L("client.vm.progress.working"),
        };

    private static string LocalizeFtbInstallFailure(
        string localizationKey,
        string? diagnosticId) =>
        string.IsNullOrWhiteSpace(diagnosticId)
            ? L(localizationKey + ".withoutDiagnostic")
            : L(localizationKey, diagnosticId);

    internal static string SelectFtbInstallFailureLocalizationKey(
        FtbClientInstallFailureClassification classification,
        string lastStage)
    {
        ArgumentNullException.ThrowIfNull(classification);
        if (classification.FailureCode == FtbClientInstallFailurePolicy.RollbackIncomplete)
        {
            return "client.vm.catalog.ftb.failure.rollback";
        }

        if (classification.FailureCode == FtbClientInstallFailurePolicy.RecoveryRequired)
        {
            return "client.vm.catalog.ftb.failure.recovery";
        }

        if (classification.FailureCode == FtbClientInstallFailurePolicy.Unknown &&
            !string.IsNullOrWhiteSpace(lastStage) &&
            lastStage.Contains("java", StringComparison.OrdinalIgnoreCase))
        {
            return "client.vm.catalog.ftb.failure.java";
        }

        return classification.LocalizationKey;
    }

    private static int? TryParseFtbJavaMajor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var first = value.Trim().Split('.', '-', '+')[0];
        return int.TryParse(
                   first,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var major) && major is >= 8 and <= 99
            ? major
            : null;
    }

    private static string LocalizeContentProgress(MinecraftClientContentProgress progress) =>
        progress.Stage switch
        {
            "copy" => L("client.vm.progress.content.copy", progress.CompletedItems, progress.TotalItems),
            "complete" => L("client.vm.progress.content.complete", progress.CompletedItems),
            _ => L("client.vm.progress.working"),
        };

    private static string GetRunningProductVersion()
    {
        var assembly = typeof(ClientWorkspaceViewModel).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2, StringSplitOptions.TrimEntries)[0];
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private void RefreshLocalizedBedrockChoices()
    {
        var selectedChannel = _selectedBedrockChannel?.Channel
                              ?? MinecraftBedrockChannel.Stable;
        _bedrockChannelChoices =
        [
            new BedrockChannelChoiceViewModel(
                MinecraftBedrockChannel.Stable,
                L("client.bedrock.channel.stable"),
                L("client.create.bedrockChannelHint")),
            new BedrockChannelChoiceViewModel(
                MinecraftBedrockChannel.Preview,
                L("client.bedrock.channel.preview"),
                L("client.create.bedrockChannelHint")),
        ];
        _selectedBedrockChannel = _bedrockChannelChoices.First(choice =>
            choice.Channel == selectedChannel);
    }

    private void RefreshLocalizedContentDownloadChoices()
    {
        var selectedLoader = _selectedContentDownloadLoader?.Loader;
        _contentDownloadLoaders =
        [
            new(null, L("client.content.download.loader.auto")),
            new(MinecraftClientLoader.Forge, "Forge"),
            new(MinecraftClientLoader.NeoForge, "NeoForge"),
            new(MinecraftClientLoader.Fabric, "Fabric"),
            new(MinecraftClientLoader.Quilt, "Quilt"),
        ];
        _selectedContentDownloadLoader =
            _contentDownloadLoaders.FirstOrDefault(choice => choice.Loader == selectedLoader)
            ?? _contentDownloadLoaders[0];
    }

    private static ClientContentDownloadProjectItemViewModel CreateContentDownloadProjectItem(
        ModrinthClientContentProject project,
        string gameVersion)
    {
        var compatibility = project.Loaders.Count == 0
            ? gameVersion
            : string.Join(" / ", project.Loaders);
        return new ClientContentDownloadProjectItemViewModel(
            project,
            L("client.vm.contentDownload.compatibility", project.Downloads, compatibility));
    }

    private void RefreshLocalizedCatalogChoices()
    {
        var selectedLoader = _selectedCatalogLoader?.Loader;
        var selectedCategory = _selectedCatalogCategory?.Category;
        var selectedSort = _selectedCatalogSort?.Sort ?? ModrinthClientModpackSort.Downloads;

        _catalogLoaders =
        [
            new(null, L("client.vm.catalog.allLoaders")),
            new(MinecraftClientLoader.Fabric, "Fabric"),
            new(MinecraftClientLoader.Forge, "Forge"),
            new(MinecraftClientLoader.NeoForge, "NeoForge"),
            new(MinecraftClientLoader.Quilt, "Quilt"),
        ];
        _catalogCategories =
        [
            new(null, L("client.vm.catalog.allCategories")),
            new("adventure", L("client.vm.catalog.category.adventure")),
            new("challenging", L("client.vm.catalog.category.challenging")),
            new("combat", L("client.vm.catalog.category.combat")),
            new("kitchen-sink", L("client.vm.catalog.category.kitchenSink")),
            new("lightweight", L("client.vm.catalog.category.lightweight")),
            new("magic", L("client.vm.catalog.category.magic")),
            new("multiplayer", L("client.vm.catalog.category.multiplayer")),
            new("optimization", L("client.vm.catalog.category.optimization")),
            new("quests", L("client.vm.catalog.category.quests")),
            new("technology", L("client.vm.catalog.category.technology")),
        ];
        _catalogSortOptions =
        [
            new(ModrinthClientModpackSort.Downloads, L("client.vm.catalog.sort.downloads")),
            new(ModrinthClientModpackSort.Relevance, L("client.vm.catalog.sort.relevance")),
            new(ModrinthClientModpackSort.Updated, L("client.vm.catalog.sort.updated")),
            new(ModrinthClientModpackSort.Newest, L("client.vm.catalog.sort.newest")),
            new(ModrinthClientModpackSort.Follows, L("client.vm.catalog.sort.follows")),
        ];

        _selectedCatalogLoader = _catalogLoaders.First(item => item.Loader == selectedLoader);
        _selectedCatalogCategory = _catalogCategories.First(item => item.Category == selectedCategory);
        _selectedCatalogSort = _catalogSortOptions.First(item => item.Sort == selectedSort);
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedCatalogChoices();
        RefreshLocalizedBedrockChoices();
        var selectedDownloadProjectId = _selectedContentDownloadProject?.ProjectId;
        var downloadFallbackUri = ContentDownloadFallbackUri;
        var downloadProjects = ContentDownloadResults
            .Select(item => item.Project)
            .ToArray();
        RefreshLocalizedContentDownloadChoices();
        OnPropertyChanged(nameof(CatalogLoaders));
        OnPropertyChanged(nameof(CatalogCategories));
        OnPropertyChanged(nameof(CatalogSortOptions));
        OnPropertyChanged(nameof(SelectedCatalogLoader));
        OnPropertyChanged(nameof(SelectedCatalogCategory));
        OnPropertyChanged(nameof(SelectedCatalogSort));
        OnPropertyChanged(nameof(ContentDownloadLoaders));
        OnPropertyChanged(nameof(SelectedContentDownloadLoader));
        OnPropertyChanged(nameof(BedrockChannelChoices));
        OnPropertyChanged(nameof(SelectedBedrockChannel));
        OnPropertyChanged(nameof(ContentDownloadHeading));
        OnPropertyChanged(nameof(ContentDownloadDescription));

        if (downloadProjects.Length > 0)
        {
            var gameVersion = SelectedInstance?.Model.GameVersion ?? string.Empty;
            ContentDownloadResults.Clear();
            foreach (var project in downloadProjects)
            {
                ContentDownloadResults.Add(CreateContentDownloadProjectItem(project, gameVersion));
            }

            SelectedContentDownloadProject = ContentDownloadResults.FirstOrDefault(item =>
                string.Equals(item.ProjectId, selectedDownloadProjectId, StringComparison.Ordinal));
        }

        ContentDownloadFallbackUri = downloadFallbackUri;
        ContentDownloadStatusText = IsBusy
            ? L("client.vm.contentDownload.working")
            : downloadFallbackUri is not null
                ? L(
                    "client.vm.contentDownload.fallback",
                    SelectedContentDownloadProject?.Title ?? string.Empty)
                : !IsContentDownloadOpen
                    ? L("client.vm.contentDownload.initial")
                    : ContentDownloadResults.Count == 0
                        ? L("client.vm.contentDownload.noResults")
                        : L("client.vm.contentDownload.visible", ContentDownloadResults.Count);

        if (CatalogGameVersions.FirstOrDefault() is { Version: null } first)
        {
            var replacement = new ClientCatalogGameVersionChoice(null, L("client.vm.catalog.allVersions"));
            var wasSelected = ReferenceEquals(_selectedCatalogGameVersion, first);
            CatalogGameVersions[0] = replacement;
            if (wasSelected)
            {
                _selectedCatalogGameVersion = replacement;
                OnPropertyChanged(nameof(SelectedCatalogGameVersion));
            }
        }

        OnPropertyChanged(nameof(CatalogResultsHeading));
        OnPropertyChanged(nameof(CatalogInstallHeading));
        OnPropertyChanged(nameof(CatalogInstallActionText));
        OnPropertyChanged(nameof(CatalogResultsSummary));
        OnPropertyChanged(nameof(SelectedContentKindText));
        OnPropertyChanged(nameof(ContentModeText));
        OnPropertyChanged(nameof(SelectedPlayerName));
        OnPropertyChanged(nameof(AccountButtonAccessibleName));
        OnPropertyChanged(nameof(SelectedAccountExpirySummary));
        OnPropertyChanged(nameof(DeviceCodeExpirySummary));
        OnPropertyChanged(nameof(SelectedSkinFileName));
        if (_isShowingFtbInstallFailure &&
            !string.IsNullOrWhiteSpace(_lastFtbInstallFailureLocalizationKey))
        {
            ErrorText = LocalizeFtbInstallFailure(
                _lastFtbInstallFailureLocalizationKey,
                _lastFtbInstallDiagnosticId);
        }
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _launcherWindowLifecycle.BeginShutdown();
        _lifetimeCancellation.Cancel();
        _loaderRefreshCancellation?.Cancel();
        _operationCancellation?.Cancel();
        _catalogBrowseCancellation?.Cancel();
        _catalogVersionCancellation?.Cancel();
        _accountLoginCancellation?.Cancel();
        _skinTextureLoadCancellation?.Cancel();
        await _contentRefreshCoordinator.DisposeAsync();
        Task[] observerTasks;
        lock (_runningSessionGate)
        {
            observerTasks = _sessionObserverTasks.ToArray();
        }

        Task[] profileSynchronizationTasks;
        lock (_profileSynchronizationGate)
        {
            profileSynchronizationTasks = _profileSynchronizationTasks.ToArray();
        }

        try
        {
            await Task.WhenAll(
                observerTasks.Concat(profileSynchronizationTasks)
                    .Append(_catalogBrowseTask)
                    .Append(_catalogArtworkTask)
                    .Append(_catalogVersionTask)
                    .Append(_accountRefreshTask)
                    .Append(_accountLoginTask));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Debug.WriteLine($"Client workspace shutdown task failed: {error}");
        }

        MinecraftClientProcessSession[] remainingSessions;
        lock (_runningSessionGate)
        {
            remainingSessions = _runningSessions.Values.ToArray();
            _runningSessions.Clear();
            _sessionObserverTasks.Clear();
        }

        // Intentionally detach without killing active games. Minecraft is an interactive user
        // process and the durable identity remains available for the next manager session.
        foreach (var session in remainingSessions)
        {
            await session.DisposeAsync();
        }

        _loaderRefreshCancellation?.Dispose();
        _operationCancellation?.Dispose();
        _catalogBrowseCancellation?.Dispose();
        _catalogVersionCancellation?.Dispose();
        _accountLoginCancellation?.Dispose();
        _skinTextureLoadCancellation?.Dispose();
        _lifetimeCancellation.Dispose();
        if (_artworkCache is IDisposable disposableArtworkCache)
        {
            disposableArtworkCache.Dispose();
        }

        _modrinthContentInstaller.Dispose();
        _bedrockShortcutRegistry.Dispose();
        _registry.Dispose();
        _catalogHttpClient.Dispose();
        _runtimeHttpClient.Dispose();
        _gameHttpClient.Dispose();
        _authenticationHttpClient.Dispose();
        _contentGate.Dispose();
    }

    private sealed record ContentRefreshRequest(
        Guid InstanceId,
        string InstanceDirectory,
        MinecraftClientContentKind Kind,
        bool ShowRecycleBin,
        string KindText,
        string ModeText);

    private sealed record ContentRefreshProjection(
        IReadOnlyList<ClientContentItemViewModel> Items,
        bool LimitReached);

    private sealed class FtbInstallProgressTracker(
        IProgress<FtbClientPackInstallProgress> presentationProgress)
        : IProgress<FtbClientPackInstallProgress>
    {
        private string _lastStage = "prepare-java";

        public string LastStage => Volatile.Read(ref _lastStage);

        public void SetStage(string stage)
        {
            var normalized = !string.IsNullOrWhiteSpace(stage) &&
                             stage.Length <= 64 &&
                             stage.All(character =>
                                 char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                ? stage
                : "unknown";
            Volatile.Write(ref _lastStage, normalized);
        }

        public void Report(FtbClientPackInstallProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            SetStage(value.Stage);
            presentationProgress.Report(value);
        }
    }
}
