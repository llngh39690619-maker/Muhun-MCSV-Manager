using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
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
    private static readonly string UserAgent =
        $"XMCSV/{GetRunningProductVersion()} (Windows; client-launcher)";
    private readonly ApplicationPaths _paths;
    private readonly Func<NewMinecraftClientDefaultsSettings> _getGlobalDefaults;
    private readonly HttpClient _catalogHttpClient;
    private readonly HttpClient _runtimeHttpClient;
    private readonly HttpClient _gameHttpClient;
    private readonly HttpClient _authenticationHttpClient;
    private readonly MinecraftClientRegistry _registry;
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
    private readonly FtbClientCatalog _ftbCatalog;
    private readonly IOnlineModpackArtworkCache _artworkCache;
    private readonly BedrockOfficialHandoffService _bedrockOfficialHandoff;
    private readonly SemaphoreSlim _contentGate = new(1, 1);
    private readonly LatestOperationCoordinator _contentRefreshCoordinator;
    private readonly BatchObservableCollection<ClientContentItemViewModel> _contentItems = [];
    private readonly Dictionary<Guid, MinecraftClientProcessSession> _runningSessions = [];
    private readonly HashSet<Task> _sessionObserverTasks = [];
    private readonly object _runningSessionGate = new();
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
    private MinecraftClientAccountInfo? _selectedAccount;
    private bool _isInitialized;
    private bool _isBusy;
    private bool _isCreatePage = true;
    private bool _isSettingsPage;
    private bool _isCatalogPage;
    private bool _isCatalogBusy;
    private bool _isJavaEdition = true;
    private string _newInstanceName = "Minecraft";
    private string _statusText = string.Empty;
    private string _errorText = string.Empty;
    private double _progressValue;
    private int _minimumMemoryMb = 2_048;
    private int _maximumMemoryMb = 4_096;
    private int _windowWidth = 1280;
    private int _windowHeight = 720;
    private bool _fullScreen;
    private MinecraftClientMemoryMode _memoryMode = MinecraftClientMemoryMode.Automatic;
    private bool _applyingMemoryPreset;
    private MinecraftClientContentKind _selectedContentKind = MinecraftClientContentKind.Mod;
    private ClientContentItemViewModel? _selectedContentItem;
    private bool _showRecycleBin;
    private string _contentStatusText = string.Empty;
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
    private ClientModpackProjectItemViewModel? _selectedCatalogProject;
    private ClientCatalogVersionItemViewModel? _selectedCatalogVersion;
    private string _catalogInstanceName = string.Empty;
    private bool _includeOptionalPackFiles;
    private bool _disposed;
    private IReadOnlyList<ClientCatalogLoaderChoice> _catalogLoaders = [];
    private IReadOnlyList<ClientCatalogCategoryChoice> _catalogCategories = [];
    private IReadOnlyList<ClientCatalogSortChoice> _catalogSortOptions = [];

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
        _ftbCatalog = new FtbClientCatalog(new FtbCatalogProvider(_catalogHttpClient, UserAgent));
        _modrinthInstaller = new ModrinthMinecraftClientPackInstaller(
            _paths.Clients,
            _paths.ClientStaging,
            _registry,
            _releaseCatalog,
            payloadInstaller,
            _modrinthCatalog,
            _gameHttpClient);
        _artworkCache = new OnlineModpackArtworkCache(_paths);
        _bedrockOfficialHandoff = new BedrockOfficialHandoffService();
        _statusText = L("client.vm.status.initial");
        _contentStatusText = L("client.vm.content.initial");
        _catalogStatusText = L("client.vm.catalog.initial");
        RefreshLocalizedCatalogChoices();
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
        CloseCatalogCommand = new RelayCommand(ShowSelectedInstance);
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
        CloseCreateCommand = new RelayCommand(ShowSelectedInstance, () => SelectedInstance is not null);
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
            () => RunGuardedAsync(AddAccountAsync),
            () => !IsBusy);
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
        SelectJavaEditionCommand = new RelayCommand(() => IsJavaEdition = true);
        SelectBedrockEditionCommand = new RelayCommand(() => IsJavaEdition = false);
        OpenBedrockOfficialCommand = new RelayCommand(
            OpenBedrockOfficial,
            () => IsBedrockEdition && !IsBusy);
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

    public ObservableCollection<MinecraftReleaseInfo> Releases { get; } = [];

    public ObservableCollection<ClientLoaderChoiceViewModel> LoaderChoices { get; } = [];

    public ObservableCollection<MinecraftLoaderCatalogEntry> LoaderVersions { get; } = [];

    public ObservableCollection<MinecraftClientAccountInfo> Accounts { get; } = [];

    public ObservableCollection<ClientContentItemViewModel> ContentItems => _contentItems;

    public ObservableCollection<ClientModpackProjectItemViewModel> CatalogProjects { get; } = [];

    public ObservableCollection<ClientCatalogVersionItemViewModel> CatalogVersions { get; } = [];

    public ObservableCollection<ClientCatalogGameVersionChoice> CatalogGameVersions { get; } = [];

    public IReadOnlyList<ClientCatalogLoaderChoice> CatalogLoaders => _catalogLoaders;

    public IReadOnlyList<ClientCatalogCategoryChoice> CatalogCategories => _catalogCategories;

    public IReadOnlyList<ClientCatalogSortChoice> CatalogSortOptions => _catalogSortOptions;

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
    public RelayCommand CloseCreateCommand { get; }
    public AsyncRelayCommand CreateInstanceCommand { get; }
    public RelayCommand CancelOperationCommand { get; }
    public AsyncRelayCommand LaunchCommand { get; }
    public AsyncRelayCommand QuickLaunchCommand { get; }
    public AsyncRelayCommand StopClientCommand { get; }
    public AsyncRelayCommand AddAccountCommand { get; }
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
    public RelayCommand SelectJavaEditionCommand { get; }
    public RelayCommand SelectBedrockEditionCommand { get; }
    public RelayCommand OpenBedrockOfficialCommand { get; }
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
                IsCreatePage = false;
                IsSettingsPage = false;
                IsCatalogPage = false;
            }

            SettingsEditor = null;

            OnPropertyChanged(nameof(HasSelectedInstance));
            LaunchCommand.NotifyCanExecuteChanged();
            QuickLaunchCommand.NotifyCanExecuteChanged();
            StopClientCommand.NotifyCanExecuteChanged();
            OpenInstanceFolderCommand.NotifyCanExecuteChanged();
            DeleteClientInstanceCommand.NotifyCanExecuteChanged();
            OpenContentFolderCommand.NotifyCanExecuteChanged();
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
            if (value is not null)
            {
                _ = RunGuardedAsync(RefreshContentAsync);
            }
        }
    }

    public MinecraftClientAccountInfo? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value))
            {
                LaunchCommand.NotifyCanExecuteChanged();
                RemoveSelectedAccountCommand.NotifyCanExecuteChanged();
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
                NotifyCreateStateChanged();
                LaunchCommand.NotifyCanExecuteChanged();
                QuickLaunchCommand.NotifyCanExecuteChanged();
                AddAccountCommand.NotifyCanExecuteChanged();
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
            }
        }
    }

    public bool IsDashboardPage => !IsCreatePage && !IsSettingsPage && !IsCatalogPage;

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
            OnPropertyChanged(nameof(IsBrowsableCatalogSource));
            OnPropertyChanged(nameof(IsUnavailableCatalogSource));
            OnPropertyChanged(nameof(ShowsCatalogSortFilter));
            OnPropertyChanged(nameof(ShowsCatalogCategoryFilter));
            OnPropertyChanged(nameof(CatalogResultsHeading));
            OnPropertyChanged(nameof(CatalogInstallHeading));
            OnPropertyChanged(nameof(CatalogInstallActionText));
            OnPropertyChanged(nameof(ShowsModrinthInstallOptions));
            SearchCatalogCommand.NotifyCanExecuteChanged();
            LoadMoreCatalogCommand.NotifyCanExecuteChanged();
            InstallCatalogPackCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsModrinthCatalogSource => CatalogSourceId == "modrinth";

    public bool IsFtbCatalogSource => CatalogSourceId == "ftb";

    public bool IsBrowsableCatalogSource => IsModrinthCatalogSource || IsFtbCatalogSource;

    public bool IsUnavailableCatalogSource => !IsBrowsableCatalogSource;

    public bool ShowsCatalogSortFilter => IsModrinthCatalogSource;

    public bool ShowsCatalogCategoryFilter => IsModrinthCatalogSource;

    public bool ShowsModrinthInstallOptions => IsModrinthCatalogSource;

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
        set => SetProperty(ref _windowWidth, Math.Clamp(value, 640, 16_384));
    }

    public int WindowHeight
    {
        get => _windowHeight;
        set => SetProperty(ref _windowHeight, Math.Clamp(value, 360, 16_384));
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
            var document = await _registry.LoadAsync();
            Instances.Clear();
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
            await RefreshCatalogCoreAsync();
            if (recoveredProcessCount > 0)
            {
                StatusText = L("client.vm.status.recovered", recoveredProcessCount);
            }
            IsInitialized = true;
            SelectedInstance ??= Instances.FirstOrDefault();
            if (SelectedInstance is null)
            {
                ShowCreatePage();
            }
        }
        finally
        {
            IsBusy = false;
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
        LoaderChoices.Clear();
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        if (selectedRelease is null || snapshot is null)
        {
            return;
        }

        LoaderChoices.Add(new ClientLoaderChoiceViewModel(MinecraftClientLoader.Vanilla, []));
        SelectedLoader = LoaderChoices[0];
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

        foreach (var result in results)
        {
            if (result.Error is null && result.Versions.Count > 0)
            {
                LoaderChoices.Add(new ClientLoaderChoiceViewModel(result.Loader, result.Versions));
            }
        }

        var failedLoaders = results
            .Where(static result => result.Error is not null)
            .Select(static result => result.Loader == MinecraftClientLoader.NeoForge
                ? "NeoForge"
                : result.Loader.ToString())
            .ToArray();
        StatusText = failedLoaders.Length == 0
            ? L("client.vm.status.loaderMethods", selectedRelease.Id, LoaderChoices.Count)
            : L(
                "client.vm.status.loaderPartialFailure",
                selectedRelease.Id,
                LoaderChoices.Count,
                string.Join(", ", failedLoaders));
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
        (IsFtbCatalogSource || !string.IsNullOrWhiteSpace(CatalogInstanceName));

    private async Task InstallSelectedCatalogPackAsync()
    {
        var project = SelectedCatalogProject
                      ?? throw new InvalidOperationException(L("client.vm.validation.pack"));
        var version = SelectedCatalogVersion
                      ?? throw new InvalidOperationException(L("client.vm.validation.packVersion"));
        if (IsFtbCatalogSource)
        {
            await OpenSelectedFtbPackAsync(project);
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
        try
        {
            Process.Start(new ProcessStartInfo(FtbAppProtocol.OfficialDownloadPage.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception
                                     or InvalidOperationException
                                     or FileNotFoundException)
        {
            throw new InvalidOperationException(
                L("client.vm.catalog.ftb.downloadPageFailed", FtbAppProtocol.OfficialDownloadPage),
                error);
        }

        return Task.CompletedTask;
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
        var release = SelectedRelease ?? throw new InvalidOperationException(L("client.vm.validation.release"));
        var loaderChoice = SelectedLoader ?? throw new InvalidOperationException(L("client.vm.validation.loader"));
        if (!IsJavaEdition)
        {
            throw new NotSupportedException(L("client.vm.validation.bedrockUnsupported"));
        }

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

    private async Task AddAccountAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = L("client.vm.status.accountLogin");
            var session = await _authenticationService.AddAccountInteractivelyAsync();
            RefreshAccounts();
            SelectedAccount = Accounts.FirstOrDefault(account => account.Id == session.AccountId);
            StatusText = L("client.vm.status.accountAdded", session.Username);
        }
        finally
        {
            IsBusy = false;
        }
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
                authenticated = await _authenticationService.AddAccountInteractivelyAsync();
                RefreshAccounts();
                account = Accounts.FirstOrDefault(candidate => candidate.Id == authenticated.AccountId);
                SelectedAccount = account;
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

            StartObservingSession(item, session);
            item.State = MinecraftClientInstanceState.Running;
            StatusText = L("client.vm.status.launched", item.Name);
            if (item.Model.HideLauncherAfterGameStarts)
            {
                HideLauncherRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch
        {
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
        MinecraftClientProcessSession session)
    {
        await Task.Yield();
        try
        {
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
            lock (_runningSessionGate)
            {
                _runningSessions.Remove(item.Id);
            }

            await session.DisposeAsync();
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
        MinecraftClientProcessSession session)
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
            observer = ObserveSessionAsync(item, session);
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
                ShowCreatePage();
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
        RemoveSelectedAccountCommand.NotifyCanExecuteChanged();
        SignOutAllAccountsCommand.NotifyCanExecuteChanged();
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
        if (SelectedInstance is not null)
        {
            IsSettingsPage = false;
            IsCatalogPage = false;
            IsCreatePage = false;
        }
        else
        {
            ShowCreatePage();
        }
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
        IsCreatePage = SelectedInstance is null;
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

    private bool CanCreateInstance() =>
        !IsBusy && IsJavaEdition && SelectedRelease is not null && SelectedLoader?.IsManaged == true &&
        !string.IsNullOrWhiteSpace(NewInstanceName) &&
        (SelectedLoader.Loader == MinecraftClientLoader.Vanilla || SelectedLoaderVersion is not null);

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
        if (!_bedrockOfficialHandoff.TryOpen(out var target))
        {
            ErrorText = L("client.vm.validation.bedrockHandoffFailed");
            StatusText = L("client.vm.status.operationFailed");
            return;
        }

        StatusText = target == BedrockOfficialHandoffTarget.Minecraft
            ? L("client.vm.status.bedrockOpened")
            : L("client.vm.status.bedrockStoreOpened");
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
        OnPropertyChanged(nameof(CatalogLoaders));
        OnPropertyChanged(nameof(CatalogCategories));
        OnPropertyChanged(nameof(CatalogSortOptions));
        OnPropertyChanged(nameof(SelectedCatalogLoader));
        OnPropertyChanged(nameof(SelectedCatalogCategory));
        OnPropertyChanged(nameof(SelectedCatalogSort));

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
        _lifetimeCancellation.Cancel();
        _loaderRefreshCancellation?.Cancel();
        _operationCancellation?.Cancel();
        _catalogBrowseCancellation?.Cancel();
        _catalogVersionCancellation?.Cancel();
        await _contentRefreshCoordinator.DisposeAsync();
        Task[] observerTasks;
        lock (_runningSessionGate)
        {
            observerTasks = _sessionObserverTasks.ToArray();
        }

        try
        {
            await Task.WhenAll(
                observerTasks.Append(_catalogBrowseTask)
                    .Append(_catalogArtworkTask)
                    .Append(_catalogVersionTask));
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
        _lifetimeCancellation.Dispose();
        if (_artworkCache is IDisposable disposableArtworkCache)
        {
            disposableArtworkCache.Dispose();
        }

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
}
