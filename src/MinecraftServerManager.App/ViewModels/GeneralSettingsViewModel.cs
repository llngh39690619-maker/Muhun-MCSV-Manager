using System.Windows;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.ViewModels;

public sealed record WindowSizeChoice(
    string DisplayName,
    double Width,
    double Height,
    bool IsCustom = false);

public sealed record LanguageChoice(string CultureName, string DisplayName);

public sealed record ProductUpdateChannelChoice(
    ProductUpdateChannel Channel,
    string DisplayName);

public sealed record GeneralSettingsPreview(
    ManagerUiSettings UserInterface,
    ApplicationAppearanceSettings Appearance,
    bool ResizeMainWindow = false);

/// <summary>
/// Transactional editor for manager UI and defaults applied only to future servers. Display
/// changes are previewed immediately, while persistence remains explicit and discardable.
/// </summary>
public sealed class GeneralSettingsViewModel : ObservableObject
{
    private readonly Func<ManagerUiSettings, NewServerDefaultsSettings, NewMinecraftClientDefaultsSettings, ApplicationAppearanceSettings, Task> _saveAsync;
    private readonly Action<GeneralSettingsPreview>? _preview;
    private readonly Action? _restorePreview;
    private readonly IProductUpdateClient? _updateClient;
    private readonly Action? _openNotificationSettings;
    private readonly Action? _openProviderManagement;
    private readonly long _availableMemoryBytes;
    private readonly long _totalMemoryBytes;
    private readonly bool _memoryIsFallback;
    private readonly double _automaticClientMinimumMemoryMb;
    private readonly double _automaticClientMaximumMemoryMb;
    private ManagerUiSettings _baselineUi;
    private NewServerDefaultsSettings _baselineDefaults;
    private NewMinecraftClientDefaultsSettings _baselineClientDefaults;
    private IReadOnlyList<ThemePreset> _themes;
    private IReadOnlyList<WindowSizeChoice> _windowSizeOptions;
    private IReadOnlyList<LanguageChoice> _languages;
    private IReadOnlyList<ProductUpdateChannelChoice> _updateChannelOptions;
    private ThemePreset _selectedTheme;
    private WindowSizeChoice _selectedWindowSize;
    private double _windowWidth;
    private double _windowHeight;
    private double _fontSize;
    private double _defaultMinimumMemoryMb;
    private double _defaultMaximumMemoryMb;
    private bool _separateDiagnosticOutput;
    private bool _autoRestart;
    private bool _enableHangWatchdog;
    private bool _enableAutomaticRecoveryPoints;
    private ClientMemoryModeChoice _selectedClientMemoryMode;
    private double _clientMinimumMemoryMb;
    private double _clientMaximumMemoryMb;
    private bool _applyingClientMemoryPreset;
    private double _clientWindowWidth;
    private double _clientWindowHeight;
    private bool _clientFullScreen;
    private bool _clientQuickLaunch;
    private bool _clientHideLauncherAfterGameStarts;
    private bool _clientShowGameLog;
    private bool _clientEnableDedicatedGpu;
    private bool _clientEnableDiscordPresence;
    private bool _isBusy;
    private string _validationMessage = string.Empty;
    private LanguageChoice _selectedLanguage;
    private ProductUpdateChannelChoice _selectedUpdateChannelChoice;
    private ProductUpdateChannel _selectedUpdateChannel = ProductUpdateChannel.Stable;
    private ProductUpdateStatus? _updateStatus;
    private bool _isUpdateBusy;
    private string _updateStatusText = string.Empty;
    private string _updateMessage = string.Empty;
    private string? _updateStatusTextKey;
    private object?[] _updateStatusTextArguments = [];
    private string? _updateMessageKey;
    private object?[] _updateMessageArguments = [];

    public GeneralSettingsViewModel(
        ManagerUiSettings currentUi,
        NewServerDefaultsSettings currentDefaults,
        Func<ManagerUiSettings, NewServerDefaultsSettings, ApplicationAppearanceSettings, Task> saveAsync,
        ISystemMemoryProbe? systemMemoryProbe = null,
        Action<GeneralSettingsPreview>? preview = null,
        Action? restorePreview = null,
        IProductUpdateClient? updateClient = null,
        Action? openNotificationSettings = null,
        Action? openProviderManagement = null)
        : this(
            currentUi,
            currentDefaults,
            new NewMinecraftClientDefaultsSettings(),
            AdaptLegacySave(saveAsync),
            systemMemoryProbe,
            preview,
            restorePreview,
            updateClient,
            openNotificationSettings,
            openProviderManagement)
    {
    }

    public GeneralSettingsViewModel(
        ManagerUiSettings currentUi,
        NewServerDefaultsSettings currentDefaults,
        NewMinecraftClientDefaultsSettings currentClientDefaults,
        Func<ManagerUiSettings, NewServerDefaultsSettings, NewMinecraftClientDefaultsSettings, ApplicationAppearanceSettings, Task> saveAsync,
        ISystemMemoryProbe? systemMemoryProbe = null,
        Action<GeneralSettingsPreview>? preview = null,
        Action? restorePreview = null,
        IProductUpdateClient? updateClient = null,
        Action? openNotificationSettings = null,
        Action? openProviderManagement = null)
    {
        ArgumentNullException.ThrowIfNull(currentUi);
        ArgumentNullException.ThrowIfNull(currentDefaults);
        ArgumentNullException.ThrowIfNull(currentClientDefaults);
        _saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        _preview = preview;
        _restorePreview = restorePreview;
        _updateClient = updateClient;
        _openNotificationSettings = openNotificationSettings;
        _openProviderManagement = openProviderManagement;
        _baselineUi = currentUi.Copy();
        _baselineDefaults = currentDefaults.Copy();
        _baselineClientDefaults = currentClientDefaults.Copy();
        _themes = ThemePresetCatalog.All;
        _windowSizeOptions = CreateWindowSizes();
        _languages = CreateLanguageOptions();
        _updateChannelOptions = CreateUpdateChannelOptions();
        _selectedLanguage = _languages.First(option =>
            option.CultureName.Equals(
                LocalizationService.Current.CultureName,
                StringComparison.OrdinalIgnoreCase));
        _selectedUpdateChannelChoice = _updateChannelOptions[0];

        var memory = (systemMemoryProbe ?? new WindowsSystemMemoryProbe()).GetSnapshot();
        var totalBytes = Math.Max(512L * 1024 * 1024, memory.TotalPhysicalBytes);
        var availableBytes = Math.Clamp(memory.AvailablePhysicalBytes, 0, totalBytes);
        _totalMemoryBytes = totalBytes;
        _availableMemoryBytes = availableBytes;
        _memoryIsFallback = memory.IsFallback;
        DefaultMemorySliderMaximumMb = Math.Clamp(
            (int)Math.Min(131072, totalBytes / (1024L * 1024) / 256 * 256),
            512,
            131072);
        _selectedTheme = GetThemeOrDefault(_themes, currentUi.ThemePresetId);
        _windowWidth = currentUi.WindowWidth;
        _windowHeight = currentUi.WindowHeight;
        _fontSize = currentUi.FontSize;
        _selectedWindowSize = _windowSizeOptions.FirstOrDefault(item =>
                                  !item.IsCustom
                                  && Math.Abs(item.Width - _windowWidth) < 0.5
                                  && Math.Abs(item.Height - _windowHeight) < 0.5)
                              ?? _windowSizeOptions[^1];
        _defaultMinimumMemoryMb = currentDefaults.MinimumMemoryMb;
        _defaultMaximumMemoryMb = currentDefaults.MaximumMemoryMb;
        _separateDiagnosticOutput = currentDefaults.SeparateDiagnosticOutput;
        _autoRestart = currentDefaults.AutoRestart;
        _enableHangWatchdog = currentDefaults.EnableHangWatchdog;
        _enableAutomaticRecoveryPoints = currentDefaults.EnableAutomaticRecoveryPoints;
        ClientMemoryModes = CreateClientMemoryModes();
        _selectedClientMemoryMode = ClientMemoryModes.First(choice => choice.Mode ==
            (currentClientDefaults.MemoryMode == MinecraftClientMemoryMode.Manual
                ? MinecraftClientMemoryMode.Manual
                : MinecraftClientMemoryMode.Automatic));
        var automaticDefaults = currentClientDefaults.MemoryMode == MinecraftClientMemoryMode.Automatic
            ? currentClientDefaults
            : new NewMinecraftClientDefaultsSettings();
        _automaticClientMinimumMemoryMb = Math.Clamp(
            automaticDefaults.MinimumMemoryMb,
            512,
            32_768);
        _automaticClientMaximumMemoryMb = Math.Clamp(
            automaticDefaults.MaximumMemoryMb,
            _automaticClientMinimumMemoryMb,
            32_768);
        _clientMinimumMemoryMb = currentClientDefaults.MinimumMemoryMb;
        _clientMaximumMemoryMb = currentClientDefaults.MaximumMemoryMb;
        _clientWindowWidth = currentClientDefaults.WindowWidth;
        _clientWindowHeight = currentClientDefaults.WindowHeight;
        _clientFullScreen = currentClientDefaults.FullScreen;
        _clientQuickLaunch = currentClientDefaults.EnableQuickLaunch;
        _clientHideLauncherAfterGameStarts = currentClientDefaults.HideLauncherAfterGameStarts;
        _clientShowGameLog = currentClientDefaults.ShowGameLog;
        _clientEnableDedicatedGpu = currentClientDefaults.EnableDedicatedGpu;
        _clientEnableDiscordPresence = currentClientDefaults.EnableDiscordPresence;

        ApplyCommand = new AsyncRelayCommand(SaveAndApplyAsync, () => !IsBusy);
        CloseCommand = new RelayCommand(
            () => CloseRequested?.Invoke(this, EventArgs.Empty),
            () => !IsBusy);
        RefreshUpdateCommand = new AsyncRelayCommand(
            RefreshUpdateAsync,
            () => _updateClient is not null && !IsUpdateBusy);
        CheckUpdateCommand = new AsyncRelayCommand(
            CheckUpdateAsync,
            () => _updateClient is not null && !IsUpdateBusy);
        DownloadUpdateCommand = new AsyncRelayCommand(
            DownloadUpdateAsync,
            () => _updateClient is not null && !IsUpdateBusy &&
                  _updateStatus?.Phase == ProductUpdatePhase.Available);
        ScheduleUpdateCommand = new AsyncRelayCommand(
            ScheduleUpdateAsync,
            () => _updateClient is not null && !IsUpdateBusy &&
                  _updateStatus?.Phase == ProductUpdatePhase.Ready);
        OpenNotificationSettingsCommand = new RelayCommand(
            () => _openNotificationSettings?.Invoke(),
            () => _openNotificationSettings is not null);
        OpenProviderManagementCommand = new RelayCommand(
            () => _openProviderManagement?.Invoke(),
            () => _openProviderManagement is not null);
        if (_updateClient is null)
        {
            SetUpdateStatusText("settings.update.status.serviceRequired");
        }
        else
        {
            SetUpdateStatusText("settings.update.status.unread");
        }
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);
        Validate();
    }

    public event EventHandler? Saved;
    public event EventHandler? CloseRequested;

    public static IReadOnlyList<WindowSizeChoice> WindowSizes => CreateWindowSizes();

    public static IReadOnlyList<LanguageChoice> LanguageOptions => CreateLanguageOptions();

    public IReadOnlyList<ThemePreset> Themes => _themes;
    public IReadOnlyList<WindowSizeChoice> WindowSizeOptions => _windowSizeOptions;
    public IReadOnlyList<LanguageChoice> Languages => _languages;
    public int DefaultMemorySliderMaximumMb { get; }
    public IReadOnlyList<ClientMemoryModeChoice> ClientMemoryModes { get; private set; }
    public string SystemMemoryDisplay => L(
        "settings.memory.system",
        FormatGibibytes(_availableMemoryBytes),
        FormatGibibytes(_totalMemoryBytes),
        _memoryIsFallback ? L("settings.memory.fallbackSuffix") : string.Empty);
    public string DefaultAllocatedMemoryDisplay =>
        L(
            "settings.memory.defaultAllocated",
            DefaultMinimumMemoryMb,
            DefaultMaximumMemoryMb);

    public ClientMemoryModeChoice SelectedClientMemoryMode
    {
        get => _selectedClientMemoryMode;
        set
        {
            if (!SetProperty(ref _selectedClientMemoryMode, value ?? ClientMemoryModes[0])) return;
            if (_selectedClientMemoryMode.Mode == MinecraftClientMemoryMode.Automatic)
            {
                ApplyAutomaticClientMemoryPreview();
            }

            NotifyEdited(previewDisplay: false);
            Validate();
        }
    }

    public double ClientMinimumMemoryMb
    {
        get => _clientMinimumMemoryMb;
        set
        {
            var normalized = Math.Clamp(value, 512, ClientMaximumMemoryMb);
            if (!SetProperty(ref _clientMinimumMemoryMb, normalized)) return;
            SwitchClientMemoryModeToManualForUserEdit();
            NotifyEdited(previewDisplay: false);
            Validate();
        }
    }

    public double ClientMaximumMemoryMb
    {
        get => _clientMaximumMemoryMb;
        set
        {
            var normalized = Math.Clamp(value, 512, 32_768);
            if (!SetProperty(ref _clientMaximumMemoryMb, normalized)) return;
            SwitchClientMemoryModeToManualForUserEdit();
            if (ClientMinimumMemoryMb > normalized)
            {
                _clientMinimumMemoryMb = normalized;
                OnPropertyChanged(nameof(ClientMinimumMemoryMb));
            }
            NotifyEdited(previewDisplay: false);
            Validate();
        }
    }

    private void ApplyAutomaticClientMemoryPreview()
    {
        _applyingClientMemoryPreset = true;
        try
        {
            if (SetProperty(
                    ref _clientMinimumMemoryMb,
                    _automaticClientMinimumMemoryMb,
                    nameof(ClientMinimumMemoryMb)))
            {
                OnPropertyChanged(nameof(HasUnsavedChanges));
            }

            if (SetProperty(
                    ref _clientMaximumMemoryMb,
                    _automaticClientMaximumMemoryMb,
                    nameof(ClientMaximumMemoryMb)))
            {
                OnPropertyChanged(nameof(HasUnsavedChanges));
            }
        }
        finally
        {
            _applyingClientMemoryPreset = false;
        }
    }

    private void SwitchClientMemoryModeToManualForUserEdit()
    {
        if (_applyingClientMemoryPreset ||
            _selectedClientMemoryMode.Mode == MinecraftClientMemoryMode.Manual)
        {
            return;
        }

        _selectedClientMemoryMode = ClientMemoryModes.Single(choice =>
            choice.Mode == MinecraftClientMemoryMode.Manual);
        OnPropertyChanged(nameof(SelectedClientMemoryMode));
    }

    public double ClientWindowWidth
    {
        get => _clientWindowWidth;
        set
        {
            if (!SetProperty(ref _clientWindowWidth, value)) return;
            NotifyEdited(previewDisplay: false);
            Validate();
        }
    }

    public double ClientWindowHeight
    {
        get => _clientWindowHeight;
        set
        {
            if (!SetProperty(ref _clientWindowHeight, value)) return;
            NotifyEdited(previewDisplay: false);
            Validate();
        }
    }

    public bool ClientFullScreen { get => _clientFullScreen; set { if (SetProperty(ref _clientFullScreen, value)) NotifyEdited(false); } }
    public bool ClientQuickLaunch { get => _clientQuickLaunch; set { if (SetProperty(ref _clientQuickLaunch, value)) NotifyEdited(false); } }
    public bool ClientHideLauncherAfterGameStarts { get => _clientHideLauncherAfterGameStarts; set { if (SetProperty(ref _clientHideLauncherAfterGameStarts, value)) NotifyEdited(false); } }
    public bool ClientShowGameLog { get => _clientShowGameLog; set { if (SetProperty(ref _clientShowGameLog, value)) NotifyEdited(false); } }
    public bool ClientEnableDedicatedGpu { get => _clientEnableDedicatedGpu; set { if (SetProperty(ref _clientEnableDedicatedGpu, value)) NotifyEdited(false); } }
    public bool ClientEnableDiscordPresence { get => _clientEnableDiscordPresence; set { if (SetProperty(ref _clientEnableDiscordPresence, value)) NotifyEdited(false); } }

    public ThemePreset SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetProperty(ref _selectedTheme, value ?? _themes[0])) return;
            NotifyEdited(previewDisplay: true, resizeMainWindow: false);
        }
    }

    public LanguageChoice SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            var selected = value ?? _languages[0];
            if (!SetProperty(ref _selectedLanguage, selected)) return;
            LocalizationService.Current.SetCulture(selected.CultureName);
        }
    }

    public WindowSizeChoice SelectedWindowSize
    {
        get => _selectedWindowSize;
        set
        {
            if (!SetProperty(ref _selectedWindowSize, value ?? _windowSizeOptions[^1])) return;
            OnPropertyChanged(nameof(IsCustomWindowSize));
            if (!_selectedWindowSize.IsCustom)
            {
                WindowWidth = _selectedWindowSize.Width;
                WindowHeight = _selectedWindowSize.Height;
            }
            else
            {
                NotifyEdited(previewDisplay: true, resizeMainWindow: false);
            }
        }
    }

    public bool IsCustomWindowSize => SelectedWindowSize.IsCustom;

    public double WindowWidth
    {
        get => _windowWidth;
        set
        {
            if (!SetProperty(ref _windowWidth, value)) return;
            Validate();
            NotifyEdited(previewDisplay: true, resizeMainWindow: true);
        }
    }

    public double WindowHeight
    {
        get => _windowHeight;
        set
        {
            if (!SetProperty(ref _windowHeight, value)) return;
            Validate();
            NotifyEdited(previewDisplay: true, resizeMainWindow: true);
        }
    }

    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (!SetProperty(ref _fontSize, value)) return;
            Validate();
            NotifyEdited(previewDisplay: true, resizeMainWindow: false);
        }
    }

    public double DefaultMinimumMemoryMb
    {
        get => _defaultMinimumMemoryMb;
        set
        {
            if (!SetProperty(ref _defaultMinimumMemoryMb, RoundMemory(value))) return;
            if (_defaultMaximumMemoryMb < _defaultMinimumMemoryMb)
            {
                _defaultMaximumMemoryMb = _defaultMinimumMemoryMb;
                OnPropertyChanged(nameof(DefaultMaximumMemoryMb));
            }
            OnPropertyChanged(nameof(DefaultAllocatedMemoryDisplay));
            Validate();
            NotifyEdited(previewDisplay: false);
        }
    }

    public double DefaultMaximumMemoryMb
    {
        get => _defaultMaximumMemoryMb;
        set
        {
            if (!SetProperty(ref _defaultMaximumMemoryMb, RoundMemory(value))) return;
            if (_defaultMinimumMemoryMb > _defaultMaximumMemoryMb)
            {
                _defaultMinimumMemoryMb = _defaultMaximumMemoryMb;
                OnPropertyChanged(nameof(DefaultMinimumMemoryMb));
            }
            OnPropertyChanged(nameof(DefaultAllocatedMemoryDisplay));
            Validate();
            NotifyEdited(previewDisplay: false);
        }
    }

    public bool SeparateDiagnosticOutput
    {
        get => _separateDiagnosticOutput;
        set { if (SetProperty(ref _separateDiagnosticOutput, value)) NotifyEdited(false); }
    }

    public bool AutoRestart
    {
        get => _autoRestart;
        set { if (SetProperty(ref _autoRestart, value)) NotifyEdited(false); }
    }

    public bool EnableHangWatchdog
    {
        get => _enableHangWatchdog;
        set { if (SetProperty(ref _enableHangWatchdog, value)) NotifyEdited(false); }
    }

    public bool EnableAutomaticRecoveryPoints
    {
        get => _enableAutomaticRecoveryPoints;
        set { if (SetProperty(ref _enableAutomaticRecoveryPoints, value)) NotifyEdited(false); }
    }

    public IReadOnlyList<ProductUpdateChannel> UpdateChannels { get; } =
        [ProductUpdateChannel.Stable, ProductUpdateChannel.Beta];

    public IReadOnlyList<ProductUpdateChannelChoice> UpdateChannelOptions =>
        _updateChannelOptions;

    public ProductUpdateChannelChoice SelectedUpdateChannelChoice
    {
        get => _selectedUpdateChannelChoice;
        set
        {
            var selected = value ?? _updateChannelOptions[0];
            if (!SetProperty(ref _selectedUpdateChannelChoice, selected)) return;
            SelectedUpdateChannel = selected.Channel;
        }
    }

    public ProductUpdateChannel SelectedUpdateChannel
    {
        get => _selectedUpdateChannel;
        set
        {
            if (!SetProperty(ref _selectedUpdateChannel, value)) return;
            _updateStatus = null;
            var matching = _updateChannelOptions.First(option => option.Channel == value);
            if (!ReferenceEquals(_selectedUpdateChannelChoice, matching))
            {
                _selectedUpdateChannelChoice = matching;
                OnPropertyChanged(nameof(SelectedUpdateChannelChoice));
            }
            SetUpdateStatusText("settings.update.status.channelChanged");
            ClearUpdateMessage();
            NotifyUpdateCommands();
        }
    }

    public bool IsUpdateBusy
    {
        get => _isUpdateBusy;
        private set
        {
            if (!SetProperty(ref _isUpdateBusy, value)) return;
            NotifyUpdateCommands();
        }
    }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetProperty(ref _updateStatusText, value);
    }

    public string UpdateMessage
    {
        get => _updateMessage;
        private set => SetProperty(ref _updateMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            ApplyCommand.NotifyCanExecuteChanged();
            CloseCommand.NotifyCanExecuteChanged();
        }
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (!SetProperty(ref _validationMessage, value)) return;
            OnPropertyChanged(nameof(HasValidationError));
        }
    }

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool HasUnsavedChanges => CalculateHasUnsavedChanges();
    public AsyncRelayCommand ApplyCommand { get; }
    public RelayCommand CloseCommand { get; }
    public AsyncRelayCommand RefreshUpdateCommand { get; }
    public AsyncRelayCommand CheckUpdateCommand { get; }
    public AsyncRelayCommand DownloadUpdateCommand { get; }
    public AsyncRelayCommand ScheduleUpdateCommand { get; }
    public RelayCommand OpenNotificationSettingsCommand { get; }
    public RelayCommand OpenProviderManagementCommand { get; }

    // Compatibility aliases for code compiled against the first Preview 5 settings editor.
    public AsyncRelayCommand SaveCommand => ApplyCommand;
    public RelayCommand CancelCommand => CloseCommand;

    internal async Task<bool> SaveAndApplyAsync()
    {
        Validate();
        if (HasValidationError) return false;

        IsBusy = true;
        try
        {
            var ui = CreateUiSnapshot();
            var defaults = CreateDefaultsSnapshot();
            var clientDefaults = CreateClientDefaultsSnapshot();
            await _saveAsync(ui, defaults, clientDefaults, SelectedTheme.Appearance.Copy());
            _baselineUi = ui.Copy();
            _baselineDefaults = defaults.Copy();
            _baselineClientDefaults = clientDefaults.Copy();
            OnPropertyChanged(nameof(HasUnsavedChanges));
            Saved?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ValidationMessage = L("common.errorWithDetail", error.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal void RestorePreview() => _restorePreview?.Invoke();

    internal async Task RefreshUpdateAsync()
    {
        if (_updateClient is null) return;
        await ExecuteUpdateAsync(
            token => _updateClient.GetUpdateStatusAsync(SelectedUpdateChannel, token),
            static status => status,
            "settings.update.status.refreshed").ConfigureAwait(true);
    }

    private async Task CheckUpdateAsync()
    {
        if (_updateClient is null) return;
        await ExecuteUpdateAsync(
            token => _updateClient.CheckForUpdateAsync(SelectedUpdateChannel, token),
            static result => result.Status,
            "settings.update.status.checked").ConfigureAwait(true);
    }

    private async Task DownloadUpdateAsync()
    {
        if (_updateClient is null) return;
        await ExecuteUpdateAsync(
            token => _updateClient.DownloadUpdateAsync(SelectedUpdateChannel, token),
            static result => result.Status,
            "settings.update.status.downloaded").ConfigureAwait(true);
    }

    private async Task ScheduleUpdateAsync()
    {
        if (_updateClient is null) return;
        await ExecuteUpdateAsync(
            token => _updateClient.ScheduleUpdateAsync(
                SelectedUpdateChannel,
                DateTimeOffset.UtcNow,
                token),
            static result => result.Status,
            "settings.update.status.scheduled").ConfigureAwait(true);
    }

    private async Task ExecuteUpdateAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<T, ProductUpdateStatus> statusSelector,
        string successMessageKey)
    {
        IsUpdateBusy = true;
        ClearUpdateMessage();
        try
        {
            var result = await operation(CancellationToken.None).ConfigureAwait(true);
            var status = statusSelector(result);
            ApplyUpdateStatus(status);
            if (string.IsNullOrWhiteSpace(status.ErrorCode) &&
                status.Phase != ProductUpdatePhase.Failed)
            {
                SetUpdateMessage(successMessageKey);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SetUpdateMessage("settings.update.status.operationFailed", error.Message);
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    private void ApplyUpdateStatus(ProductUpdateStatus status)
    {
        _updateStatus = status;
        RefreshUpdateStatusText(status);
        if (!string.IsNullOrWhiteSpace(status.Message))
        {
            SetRawUpdateMessage(status.Message);
        }
        else if (!string.IsNullOrWhiteSpace(status.ErrorCode))
        {
            SetUpdateMessage("settings.update.errorCode", status.ErrorCode);
        }
        NotifyUpdateCommands();
    }

    private void RefreshUpdateStatusText(ProductUpdateStatus status)
    {
        var candidate = string.IsNullOrWhiteSpace(status.AvailableVersion)
            ? L("settings.update.candidate.none")
            : L("settings.update.candidate.value", status.AvailableVersion);
        var consistency = status.InstalledVersionsMatch
            ? L("settings.update.versions.match")
            : L("settings.update.versions.mismatch");
        SetUpdateStatusText(
            "settings.update.status.summary",
            LocalizeUpdatePhase(status.Phase),
            status.CurrentServiceVersion,
            candidate,
            consistency);
    }

    private void NotifyUpdateCommands()
    {
        RefreshUpdateCommand?.NotifyCanExecuteChanged();
        CheckUpdateCommand?.NotifyCanExecuteChanged();
        DownloadUpdateCommand?.NotifyCanExecuteChanged();
        ScheduleUpdateCommand?.NotifyCanExecuteChanged();
    }

    private void NotifyEdited(bool previewDisplay, bool resizeMainWindow = false)
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        if (!previewDisplay || _preview is null) return;
        if (!double.IsFinite(WindowWidth)
            || !double.IsFinite(WindowHeight)
            || !double.IsFinite(FontSize))
        {
            return;
        }

        var canResizeMainWindow = resizeMainWindow
                                  && WindowWidth is >= 1120 and <= 7680
                                  && WindowHeight is >= 700 and <= 4320;
        _preview(new GeneralSettingsPreview(
            CreateUiSnapshot(),
            SelectedTheme.Appearance.Copy(),
            canResizeMainWindow));
    }

    private ManagerUiSettings CreateUiSnapshot() => new()
    {
        ThemePresetId = SelectedTheme.Id,
        WindowWidth = Math.Round(WindowWidth),
        WindowHeight = Math.Round(WindowHeight),
        FontSize = Math.Round(FontSize, 1),
    };

    private NewServerDefaultsSettings CreateDefaultsSnapshot() => new()
    {
        // The global default is deliberately one fixed range. Per-server Automatic remains an
        // explicit opt-in and is never inherited from this compatibility field.
        MemoryMode = MemoryAllocationMode.Manual,
        MinimumMemoryMb = (int)DefaultMinimumMemoryMb,
        MaximumMemoryMb = (int)DefaultMaximumMemoryMb,
        SeparateDiagnosticOutput = SeparateDiagnosticOutput,
        AutoRestart = AutoRestart,
        EnableHangWatchdog = EnableHangWatchdog,
        EnableAutomaticRecoveryPoints = EnableAutomaticRecoveryPoints,
    };

    private NewMinecraftClientDefaultsSettings CreateClientDefaultsSnapshot() => new()
    {
        MemoryMode = SelectedClientMemoryMode.Mode == MinecraftClientMemoryMode.Manual
            ? MinecraftClientMemoryMode.Manual
            : MinecraftClientMemoryMode.Automatic,
        MinimumMemoryMb = (int)Math.Round(ClientMinimumMemoryMb),
        MaximumMemoryMb = (int)Math.Round(ClientMaximumMemoryMb),
        WindowWidth = (int)Math.Round(ClientWindowWidth),
        WindowHeight = (int)Math.Round(ClientWindowHeight),
        FullScreen = ClientFullScreen,
        EnableQuickLaunch = ClientQuickLaunch,
        HideLauncherAfterGameStarts = ClientHideLauncherAfterGameStarts,
        ShowGameLog = ClientShowGameLog,
        EnableDedicatedGpu = ClientEnableDedicatedGpu,
        EnableDiscordPresence = ClientEnableDiscordPresence,
    };

    private bool CalculateHasUnsavedChanges()
        => !string.Equals(
               SelectedTheme.Id,
               _baselineUi.ThemePresetId,
               StringComparison.OrdinalIgnoreCase)
           || Math.Abs(WindowWidth - _baselineUi.WindowWidth) >= 0.05
           || Math.Abs(WindowHeight - _baselineUi.WindowHeight) >= 0.05
           || Math.Abs(FontSize - _baselineUi.FontSize) >= 0.05
           || (int)DefaultMinimumMemoryMb != _baselineDefaults.MinimumMemoryMb
           || (int)DefaultMaximumMemoryMb != _baselineDefaults.MaximumMemoryMb
           || SeparateDiagnosticOutput != _baselineDefaults.SeparateDiagnosticOutput
           || AutoRestart != _baselineDefaults.AutoRestart
           || EnableHangWatchdog != _baselineDefaults.EnableHangWatchdog
           || EnableAutomaticRecoveryPoints != _baselineDefaults.EnableAutomaticRecoveryPoints
           || SelectedClientMemoryMode.Mode != NormalizeClientDefaultsMode(
               _baselineClientDefaults.MemoryMode)
           || (int)Math.Round(ClientMinimumMemoryMb) != _baselineClientDefaults.MinimumMemoryMb
           || (int)Math.Round(ClientMaximumMemoryMb) != _baselineClientDefaults.MaximumMemoryMb
           || (int)Math.Round(ClientWindowWidth) != _baselineClientDefaults.WindowWidth
           || (int)Math.Round(ClientWindowHeight) != _baselineClientDefaults.WindowHeight
           || ClientFullScreen != _baselineClientDefaults.FullScreen
           || ClientQuickLaunch != _baselineClientDefaults.EnableQuickLaunch
           || ClientHideLauncherAfterGameStarts != _baselineClientDefaults.HideLauncherAfterGameStarts
           || ClientShowGameLog != _baselineClientDefaults.ShowGameLog
           || ClientEnableDedicatedGpu != _baselineClientDefaults.EnableDedicatedGpu
           || ClientEnableDiscordPresence != _baselineClientDefaults.EnableDiscordPresence;

    private void Validate()
    {
        // A high-DPI/small-work-area resize may legitimately persist below the design minimum.
        // Keep that monitor-derived baseline valid while retaining the existing 1120x700 lower
        // bound for values manually entered in this settings dialog.
        var widthIsPersistedMonitorSize = WindowWidth >= ManagerUiSettings.MinimumPersistedWindowWidth
                                          && WindowWidth < 1120
                                          && Math.Abs(WindowWidth - _baselineUi.WindowWidth) < 0.5;
        var heightIsPersistedMonitorSize = WindowHeight >= ManagerUiSettings.MinimumPersistedWindowHeight
                                           && WindowHeight < 700
                                           && Math.Abs(WindowHeight - _baselineUi.WindowHeight) < 0.5;
        ValidationMessage = WindowWidth > ManagerUiSettings.MaximumPersistedWindowWidth
                            || WindowWidth < 1120 && !widthIsPersistedMonitorSize
            ? L("settings.validation.width")
            : WindowHeight > ManagerUiSettings.MaximumPersistedWindowHeight
              || WindowHeight < 700 && !heightIsPersistedMonitorSize
                ? L("settings.validation.height")
                : FontSize is < 11 or > 20
                    ? L("settings.validation.font")
                    : DefaultMinimumMemoryMb is < 512 or > 131072
                        ? L("settings.validation.minimumMemory")
                        : DefaultMaximumMemoryMb < DefaultMinimumMemoryMb || DefaultMaximumMemoryMb > 131072
                            ? L("settings.validation.maximumMemory")
                            : ClientMinimumMemoryMb is < 512 or > 32768
                                ? L("settings.validation.clientMinimumMemory")
                                : ClientMaximumMemoryMb < ClientMinimumMemoryMb
                                  || ClientMaximumMemoryMb > 32768
                                    ? L("settings.validation.clientMaximumMemory")
                                    : ClientWindowWidth is < 640 or > 16384
                                        ? L("settings.validation.clientWindowWidth")
                                        : ClientWindowHeight is < 360 or > 16384
                                            ? L("settings.validation.clientWindowHeight")
                                            : string.Empty;
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        var themeId = _selectedTheme.Id;
        var selectedWidth = _selectedWindowSize.Width;
        var selectedHeight = _selectedWindowSize.Height;
        var selectedIsCustom = _selectedWindowSize.IsCustom;

        _themes = ThemePresetCatalog.All;
        _selectedTheme = GetThemeOrDefault(_themes, themeId);
        _windowSizeOptions = CreateWindowSizes();
        _selectedWindowSize = _windowSizeOptions.First(option =>
            option.IsCustom == selectedIsCustom
            && (selectedIsCustom
                || Math.Abs(option.Width - selectedWidth) < 0.5
                && Math.Abs(option.Height - selectedHeight) < 0.5));
        _languages = CreateLanguageOptions();
        _selectedLanguage = _languages.First(option =>
            option.CultureName.Equals(
                LocalizationService.Current.CultureName,
                StringComparison.OrdinalIgnoreCase));
        _updateChannelOptions = CreateUpdateChannelOptions();
        _selectedUpdateChannelChoice = _updateChannelOptions.First(option =>
            option.Channel == SelectedUpdateChannel);
        var clientMemoryMode = _selectedClientMemoryMode.Mode;
        ClientMemoryModes = CreateClientMemoryModes();
        _selectedClientMemoryMode = ClientMemoryModes.First(option =>
            option.Mode == clientMemoryMode);

        OnPropertyChanged(nameof(Themes));
        OnPropertyChanged(nameof(SelectedTheme));
        OnPropertyChanged(nameof(WindowSizeOptions));
        OnPropertyChanged(nameof(SelectedWindowSize));
        OnPropertyChanged(nameof(Languages));
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(UpdateChannelOptions));
        OnPropertyChanged(nameof(SelectedUpdateChannelChoice));
        OnPropertyChanged(nameof(ClientMemoryModes));
        OnPropertyChanged(nameof(SelectedClientMemoryMode));
        OnPropertyChanged(nameof(SystemMemoryDisplay));
        OnPropertyChanged(nameof(DefaultAllocatedMemoryDisplay));

        if (_updateStatus is not null)
        {
            RefreshUpdateStatusText(_updateStatus);
        }
        else if (_updateStatusTextKey is not null)
        {
            UpdateStatusText = L(_updateStatusTextKey, _updateStatusTextArguments);
        }
        if (_updateMessageKey is not null)
        {
            UpdateMessage = L(_updateMessageKey, _updateMessageArguments);
        }
        Validate();
    }

    private void SetUpdateStatusText(string key, params object?[] arguments)
    {
        _updateStatusTextKey = key;
        _updateStatusTextArguments = arguments;
        UpdateStatusText = L(key, arguments);
    }

    private void SetUpdateMessage(string key, params object?[] arguments)
    {
        _updateMessageKey = key;
        _updateMessageArguments = arguments;
        UpdateMessage = L(key, arguments);
    }

    private void SetRawUpdateMessage(string message)
    {
        _updateMessageKey = null;
        _updateMessageArguments = [];
        UpdateMessage = message;
    }

    private void ClearUpdateMessage()
    {
        _updateMessageKey = null;
        _updateMessageArguments = [];
        UpdateMessage = string.Empty;
    }

    private static string LocalizeUpdatePhase(ProductUpdatePhase phase) => phase switch
    {
        ProductUpdatePhase.Disabled => L("settings.update.phase.disabled"),
        ProductUpdatePhase.Idle => L("settings.update.phase.idle"),
        ProductUpdatePhase.Checking => L("settings.update.phase.checking"),
        ProductUpdatePhase.Available => L("settings.update.phase.available"),
        ProductUpdatePhase.Downloading => L("settings.update.phase.downloading"),
        ProductUpdatePhase.Ready => L("settings.update.phase.ready"),
        ProductUpdatePhase.Scheduled => L("settings.update.phase.scheduled"),
        ProductUpdatePhase.Applying => L("settings.update.phase.applying"),
        ProductUpdatePhase.RollingBack => L("settings.update.phase.rollingBack"),
        ProductUpdatePhase.Failed => L("settings.update.phase.failed"),
        _ => L("settings.update.phase.unknown"),
    };

    private static IReadOnlyList<WindowSizeChoice> CreateWindowSizes() =>
    [
        new("1280 × 720", 1280, 720),
        new(L("settings.windowSize.recommended", "1480 × 900"), 1480, 900),
        new("1600 × 900", 1600, 900),
        new("1920 × 1080", 1920, 1080),
        new(L("settings.windowSize.custom"), 0, 0, IsCustom: true),
    ];

    private static IReadOnlyList<LanguageChoice> CreateLanguageOptions() =>
    [
        new("zh-TW", L("language.zh-TW")),
        new("en-US", L("language.en-US")),
    ];

    private static IReadOnlyList<ProductUpdateChannelChoice> CreateUpdateChannelOptions() =>
    [
        new(ProductUpdateChannel.Stable, L("settings.update.channel.stable")),
        new(ProductUpdateChannel.Beta, L("settings.update.channel.beta")),
    ];

    private static IReadOnlyList<ClientMemoryModeChoice> CreateClientMemoryModes() =>
    [
        new(MinecraftClientMemoryMode.Automatic, L("settings.clientMemoryAutomatic")),
        new(MinecraftClientMemoryMode.Manual, L("settings.clientMemoryManual")),
    ];

    private static ThemePreset GetThemeOrDefault(
        IReadOnlyList<ThemePreset> themes,
        string? id) =>
        themes.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? themes[0];

    private static Func<ManagerUiSettings, NewServerDefaultsSettings, NewMinecraftClientDefaultsSettings, ApplicationAppearanceSettings, Task>
        AdaptLegacySave(
            Func<ManagerUiSettings, NewServerDefaultsSettings, ApplicationAppearanceSettings, Task> saveAsync)
    {
        ArgumentNullException.ThrowIfNull(saveAsync);
        return (ui, serverDefaults, _, appearance) => saveAsync(ui, serverDefaults, appearance);
    }

    private static MinecraftClientMemoryMode NormalizeClientDefaultsMode(
        MinecraftClientMemoryMode mode)
        => mode == MinecraftClientMemoryMode.Manual
            ? MinecraftClientMemoryMode.Manual
            : MinecraftClientMemoryMode.Automatic;

    private double RoundMemory(double value)
        => Math.Clamp(
            Math.Round(value / 256) * 256,
            512,
            Math.Max(512, DefaultMemorySliderMaximumMb));

    private static string FormatGibibytes(long bytes)
        => $"{bytes / (1024d * 1024 * 1024):0.00} GB";

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);
}
