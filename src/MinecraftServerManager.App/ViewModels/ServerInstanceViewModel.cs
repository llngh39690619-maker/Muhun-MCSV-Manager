using System.Collections.ObjectModel;
using System.Windows;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.ViewModels;

public sealed class ServerInstanceViewModel : ObservableObject
{
    private const int MaximumUiConsoleLines = 2_000;
    private const int MaximumTrackedOnlinePlayers = 4_096;
    private readonly Func<Guid, string, Task> _sendCommand;
    private readonly Action<ServerInstanceViewModel>? _diagnosticOutputPreferenceChanged;
    private readonly Action<ServerInstanceViewModel, MemoryAllocationMode>? _memoryModeRequested;
    private readonly bool _isServiceManaged;
    private readonly bool _hasLocalMetadata;
    private readonly List<ConsoleLineViewModel> _consoleHistory = [];
    private readonly BatchObservableCollection<ConsoleLineViewModel> _consoleLines = [];
    private readonly BatchObservableCollection<ConsoleLineViewModel> _diagnosticLines = [];
    private readonly OnlinePlayerRoster _onlinePlayers = new();
    private readonly HashSet<string> _registryPlayerNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly BatchObservableCollection<PlayerEntryViewModel> _players = [];
    private readonly BatchObservableCollection<PlayerEntryViewModel> _visiblePlayers = [];
    private ServerState _state = ServerState.Stopped;
    private string _commandText = string.Empty;
    private string _serverPropertiesText = string.Empty;
    private double _cpuPercent;
    private long _workingSetBytes;
    private TimeSpan _uptime;
    private int? _activePort;
    private bool? _portListening;
    private ProductServerJavaRuntimeSummary? _serviceJavaRuntime;
    private PlayerEntryViewModel? _selectedPlayer;
    private string _playerNameInput = string.Empty;
    private bool _showKnownPlayers;
    private int _diagnosticIncidentCount;
    private long _nextConsoleSequence;
    private string _systemMemoryDisplay = L("server.memory.systemLoading");
    private long _availableSystemMemoryBytes;
    private long _totalSystemMemoryBytes;
    private bool _hasSystemMemorySnapshot;
    private bool _isAutomaticMemoryRecommendationRunning;
    private bool _hasSuccessfulAutomaticMemoryRecommendation;
    private bool _isBulkSelected;
    private bool _isControlChannelAvailable = true;
    private string? _automaticMemoryRecommendationStatusKey;
    private object?[] _automaticMemoryRecommendationStatusArguments = [];

    public ServerInstanceViewModel(
        ServerInstance model,
        Func<Guid, string, Task> sendCommand,
        Action<ServerInstanceViewModel>? diagnosticOutputPreferenceChanged = null,
        IEnumerable<string?>? configuredOneDriveRoots = null,
        Action<ServerInstanceViewModel, MemoryAllocationMode>? memoryModeRequested = null)
        : this(
            model,
            sendCommand,
            diagnosticOutputPreferenceChanged,
            configuredOneDriveRoots,
            memoryModeRequested,
            isServiceManaged: false,
            hasLocalMetadata: true)
    {
    }

    internal ServerInstanceViewModel(
        ServerInstance model,
        Func<Guid, string, Task> sendCommand,
        Action<ServerInstanceViewModel>? diagnosticOutputPreferenceChanged,
        IEnumerable<string?>? configuredOneDriveRoots,
        Action<ServerInstanceViewModel, MemoryAllocationMode>? memoryModeRequested,
        bool isServiceManaged,
        bool hasLocalMetadata)
    {
        Model = model;
        _sendCommand = sendCommand;
        _diagnosticOutputPreferenceChanged = diagnosticOutputPreferenceChanged;
        _memoryModeRequested = memoryModeRequested;
        _isServiceManaged = isServiceManaged;
        _hasLocalMetadata = hasLocalMetadata;
        IsInOneDriveSyncFolder = OneDriveSyncPathDetector.IsInConfiguredRoot(
            model.DirectoryPath,
            configuredOneDriveRoots);
        SendCommandCommand = new AsyncRelayCommand(SendCommandAsync, () => CanSendCommand && !string.IsNullOrWhiteSpace(CommandText));
        RecalculateAutomaticMemoryCommand = new RelayCommand(
            RequestAutomaticMemoryRecommendation,
            () => MemoryAllocationMode == MemoryAllocationMode.Automatic
                  && !IsAutomaticMemoryRecommendationRunning);
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);
    }

    public ServerInstance Model { get; }
    public bool IsServiceManaged => _isServiceManaged;
    public bool HasLocalMetadata => _hasLocalMetadata;
    public bool CanAccessLocalFiles => !_isServiceManaged && _hasLocalMetadata;
    public Guid Id => Model.Id;
    public ObservableCollection<ConsoleLineViewModel> ConsoleLines => _consoleLines;
    public ObservableCollection<ConsoleLineViewModel> DiagnosticLines => _diagnosticLines;
    public ObservableCollection<BackupItemViewModel> Backups { get; } = [];
    public ObservableCollection<AddonUpdateViewModel> AddonUpdates { get; } = [];
    public ObservableCollection<PlayerEntryViewModel> Players => _players;
    public ObservableCollection<PlayerEntryViewModel> VisiblePlayers => _visiblePlayers;
    public AsyncRelayCommand SendCommandCommand { get; }
    public RelayCommand RecalculateAutomaticMemoryCommand { get; }

    /// <summary>
    /// Gets or sets whether this row participates in the temporary bulk-selection operation.
    /// This UI-only state is deliberately not persisted in <see cref="ServerInstance"/>.
    /// </summary>
    public bool IsBulkSelected
    {
        get => _isBulkSelected;
        set => SetProperty(ref _isBulkSelected, value);
    }

    /// <summary>
    /// Gets or sets whether commands may be sent through the instance's control channel.
    /// Service-managed rows keep their last projected state while the service is unavailable,
    /// so this separate gate prevents stale projections from leaving console/player controls enabled.
    /// </summary>
    public bool IsControlChannelAvailable
    {
        get => _isControlChannelAvailable;
        set
        {
            if (!SetProperty(ref _isControlChannelAvailable, value)) return;
            OnPropertyChanged(nameof(CanSendCommand));
            OnPropertyChanged(nameof(CanManagePlayers));
            SendCommandCommand.NotifyCanExecuteChanged();
        }
    }

    public string Name
    {
        get => Model.Name;
        set
        {
            if (Model.Name == value) return;
            Model.Name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(DetailSubtitle));
        }
    }

    public string ServerJarPath => Model.ServerJarPath;
    public string LaunchTargetPath => Model.LaunchKind == ServerLaunchKind.JavaArgumentFiles
        ? string.Join("  ", Model.JavaArgumentFilePaths.Select(path => "@" + path))
        : Model.ServerJarPath;
    public string DirectoryPath => Model.DirectoryPath;
    public bool IsInOneDriveSyncFolder { get; }
    public string OneDrivePerformanceWarning => L("server.oneDrive.performanceWarning");
    public string CoreInitial => Model.CoreType == CoreType.Unknown
        ? "?"
        : Model.CoreType.ToString()[0].ToString();
    public string CoreTypeText => Model.CoreType == CoreType.Unknown
        ? L("server.core.custom")
        : Model.CoreType.ToString();
    public string MinecraftVersionDisplay => string.IsNullOrWhiteSpace(Model.MinecraftVersion)
        ? L("server.version.unknown")
        : Model.MinecraftVersion;
    public string Subtitle => $"{CoreTypeText} · {MinecraftVersionDisplay}";
    public string DetailSubtitle => IsServiceManaged
        ? L("server.detail.serviceManaged", CoreTypeText, MinecraftVersionDisplay)
        : L("server.detail.local", CoreTypeText, MinecraftVersionDisplay, Model.DirectoryPath);

    public string? JavaExecutablePath
    {
        get => Model.JavaExecutablePath;
        set { if (Model.JavaExecutablePath == value) return; Model.JavaExecutablePath = value; OnPropertyChanged(); }
    }

    public int MinimumMemoryMb
    {
        get => Model.MinimumMemoryMb;
        set
        {
            if (Model.MinimumMemoryMb == value) return;
            Model.MinimumMemoryMb = value;
            if (Model.MaximumMemoryMb < value)
            {
                Model.MaximumMemoryMb = value;
                OnPropertyChanged(nameof(MaximumMemoryMb));
                OnPropertyChanged(nameof(MaximumMemorySliderMb));
            }
            SetManualMemoryMode();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MinimumMemorySliderMb));
            OnPropertyChanged(nameof(MemoryRangeDisplay));
        }
    }

    public int MaximumMemoryMb
    {
        get => Model.MaximumMemoryMb;
        set
        {
            if (Model.MaximumMemoryMb == value) return;
            Model.MaximumMemoryMb = value;
            if (Model.MinimumMemoryMb > value)
            {
                Model.MinimumMemoryMb = value;
                OnPropertyChanged(nameof(MinimumMemoryMb));
                OnPropertyChanged(nameof(MinimumMemorySliderMb));
            }
            SetManualMemoryMode();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaximumMemorySliderMb));
            OnPropertyChanged(nameof(MemoryRangeDisplay));
        }
    }

    public double MinimumMemorySliderMb
    {
        get => MinimumMemoryMb;
        set => MinimumMemoryMb = NormalizeMemorySliderValue(value);
    }

    public double MaximumMemorySliderMb
    {
        get => MaximumMemoryMb;
        set => MaximumMemoryMb = NormalizeMemorySliderValue(value);
    }

    public double MemorySliderMaximumMb { get; private set; } = 32768;

    public string MemoryRangeDisplay => $"{MinimumMemoryMb:N0}–{MaximumMemoryMb:N0} MB";
    public string SystemMemoryDisplay => _systemMemoryDisplay;
    public bool IsAutomaticMemoryRecommendationRunning
    {
        get => _isAutomaticMemoryRecommendationRunning;
        private set
        {
            if (!SetProperty(ref _isAutomaticMemoryRecommendationRunning, value)) return;
            RecalculateAutomaticMemoryCommand.NotifyCanExecuteChanged();
        }
    }
    public bool HasSuccessfulAutomaticMemoryRecommendation
    {
        get => _hasSuccessfulAutomaticMemoryRecommendation;
        private set => SetProperty(ref _hasSuccessfulAutomaticMemoryRecommendation, value);
    }

    public MemoryAllocationMode MemoryAllocationMode
    {
        get => Model.MemoryAllocationMode;
        set
        {
            if (Model.MemoryAllocationMode == value) return;
            Model.MemoryAllocationMode = value;
            RaiseMemoryModeProperties();
            _memoryModeRequested?.Invoke(this, value);
        }
    }

    public bool IsMemoryUsingDefault
    {
        get => MemoryAllocationMode == MemoryAllocationMode.UseManagerDefault;
        set { if (value) MemoryAllocationMode = MemoryAllocationMode.UseManagerDefault; }
    }

    public bool IsMemoryAutomatic
    {
        get => MemoryAllocationMode == MemoryAllocationMode.Automatic;
        set
        {
            if (!value) return;
            if (MemoryAllocationMode == MemoryAllocationMode.Automatic)
            {
                RequestAutomaticMemoryRecommendation();
                return;
            }

            MemoryAllocationMode = MemoryAllocationMode.Automatic;
        }
    }

    public bool IsMemoryManual
    {
        get => MemoryAllocationMode is MemoryAllocationMode.Manual or MemoryAllocationMode.Legacy;
        set { if (value) MemoryAllocationMode = MemoryAllocationMode.Manual; }
    }

    public int Port
    {
        get => Model.Port;
        set
        {
            if (Model.Port == value) return;
            Model.Port = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectionDisplay));
            OnPropertyChanged(nameof(ConnectionAddress));
        }
    }

    /// <summary>
    /// The port used by the currently running process. This deliberately remains separate from
    /// <see cref="Port"/> so editing the next-start value cannot make the allocator ignore an
    /// unrelated process that already owns the newly entered port.
    /// </summary>
    public int? ActivePort => _activePort;

    public string ConnectionAddress => $"localhost:{ActivePort ?? Port}";

    public string ConnectionDisplay
    {
        get
        {
            var port = ActivePort ?? Port;
            return State == ServerState.Running && _portListening is { } listening
                ? L(listening ? "server.port.listening" : "server.port.notListening", port)
                : port.ToString(LocalizationService.Current.Culture);
        }
    }

    public bool AutoRestart
    {
        get => Model.AutoRestart;
        set { if (Model.AutoRestart == value) return; Model.AutoRestart = value; OnPropertyChanged(); }
    }

    public bool SeparateDiagnosticOutput
    {
        get => Model.SeparateDiagnosticOutput == true;
        set
        {
            if (SeparateDiagnosticOutput == value && Model.SeparateDiagnosticOutput is not null) return;
            Model.SeparateDiagnosticOutput = value;
            OnPropertyChanged();
            ReflowConsoleLines();
            _diagnosticOutputPreferenceChanged?.Invoke(this);
        }
    }

    public bool EnableHangWatchdog
    {
        get => Model.EnableHangWatchdog;
        set
        {
            if (Model.EnableHangWatchdog == value) return;
            Model.EnableHangWatchdog = value;
            OnPropertyChanged();
        }
    }

    public int WatchdogCheckIntervalSeconds
    {
        get => Model.WatchdogCheckIntervalSeconds;
        set
        {
            if (Model.WatchdogCheckIntervalSeconds == value) return;
            Model.WatchdogCheckIntervalSeconds = value;
            OnPropertyChanged();
        }
    }

    public int WatchdogProbeTimeoutSeconds
    {
        get => Model.WatchdogProbeTimeoutSeconds;
        set
        {
            if (Model.WatchdogProbeTimeoutSeconds == value) return;
            Model.WatchdogProbeTimeoutSeconds = value;
            OnPropertyChanged();
        }
    }

    public int WatchdogFailureThreshold
    {
        get => Model.WatchdogFailureThreshold;
        set
        {
            if (Model.WatchdogFailureThreshold == value) return;
            Model.WatchdogFailureThreshold = value;
            OnPropertyChanged();
        }
    }

    public int WatchdogStartupGraceSeconds
    {
        get => Model.WatchdogStartupGraceSeconds;
        set
        {
            if (Model.WatchdogStartupGraceSeconds == value) return;
            Model.WatchdogStartupGraceSeconds = value;
            OnPropertyChanged();
        }
    }

    public bool EnableAutomaticRecoveryPoints
    {
        get => Model.EnableAutomaticRecoveryPoints;
        set
        {
            if (Model.EnableAutomaticRecoveryPoints == value) return;
            Model.EnableAutomaticRecoveryPoints = value;
            OnPropertyChanged();
        }
    }

    public int RecoveryPointIntervalMinutes
    {
        get => Model.RecoveryPointIntervalMinutes;
        set
        {
            if (Model.RecoveryPointIntervalMinutes == value) return;
            Model.RecoveryPointIntervalMinutes = value;
            OnPropertyChanged();
        }
    }

    public int RecoveryPointRetentionCount
    {
        get => Model.RecoveryPointRetentionCount;
        set
        {
            if (Model.RecoveryPointRetentionCount == value) return;
            Model.RecoveryPointRetentionCount = value;
            OnPropertyChanged();
        }
    }

    public string? BackgroundImagePath
    {
        get => Model.BackgroundImagePath;
        set
        {
            if (Model.BackgroundImagePath == value) return;
            Model.BackgroundImagePath = value;
            OnPropertyChanged();
        }
    }

    public double BackgroundImageOpacity
    {
        get => Math.Clamp(Model.BackgroundImageOpacity, 0, 1);
        set
        {
            var normalized = Math.Clamp(double.IsFinite(value) ? value : 0.25, 0, 1);
            if (Math.Abs(Model.BackgroundImageOpacity - normalized) < 0.0001) return;
            Model.BackgroundImageOpacity = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BackgroundImageOpacityPercent));
        }
    }

    public double BackgroundImageOpacityPercent
    {
        get => BackgroundImageOpacity * 100;
        set => BackgroundImageOpacity = value / 100;
    }

    public string? IconImagePath
    {
        get => Model.IconImagePath;
        set
        {
            if (Model.IconImagePath == value) return;
            Model.IconImagePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveIconImagePath));
        }
    }

    /// <summary>Catalog artwork remains independent from the user's explicit icon override.</summary>
    public string? CatalogIconImagePath
    {
        get => Model.CatalogIconImagePath;
        set
        {
            if (Model.CatalogIconImagePath == value) return;
            Model.CatalogIconImagePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveIconImagePath));
        }
    }

    public string? CatalogPreviewImagePath
    {
        get => Model.CatalogPreviewImagePath;
        set
        {
            if (Model.CatalogPreviewImagePath == value) return;
            Model.CatalogPreviewImagePath = value;
            OnPropertyChanged();
        }
    }

    public string? ModpackProviderId
    {
        get => Model.ModpackProviderId;
        set
        {
            if (Model.ModpackProviderId == value) return;
            Model.ModpackProviderId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModpackSourceDisplay));
        }
    }

    /// <summary>
    /// The list-card image contract: a user override wins, followed by managed catalog artwork.
    /// A missing value lets XAML display the existing core-initial fallback.
    /// </summary>
    public string? EffectiveIconImagePath => !string.IsNullOrWhiteSpace(IconImagePath)
        ? IconImagePath
        : !string.IsNullOrWhiteSpace(CatalogIconImagePath)
            ? CatalogIconImagePath
            : null;

    public string CommandText
    {
        get => _commandText;
        set
        {
            if (SetProperty(ref _commandText, value))
            {
                SendCommandCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ServerPropertiesText
    {
        get => _serverPropertiesText;
        set => SetProperty(ref _serverPropertiesText, value);
    }

    public ServerState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(CanSendCommand));
            OnPropertyChanged(nameof(CanManagePlayers));
            OnPropertyChanged(nameof(CanIterativelyUpdateModpack));
            OnPropertyChanged(nameof(ConnectionDisplay));
            SendCommandCommand.NotifyCanExecuteChanged();
        }
    }

    public string StateText => State switch
    {
        ServerState.Starting => L("server.state.starting"),
        ServerState.Running => L("server.state.running"),
        ServerState.Stopping => L("server.state.stopping"),
        ServerState.Crashed => L("server.state.crashed"),
        ServerState.Faulted => L("server.state.faulted"),
        _ => L("server.state.stopped")
    };

    public bool CanSendCommand => IsControlChannelAvailable
                                  && State is (ServerState.Starting or ServerState.Running);
    public bool CanManagePlayers => IsControlChannelAvailable && State == ServerState.Running;
    public double CpuPercent => _cpuPercent;
    public long WorkingSetBytes => _workingSetBytes;
    public TimeSpan Uptime => _uptime;
    public int OnlinePlayerCount => _onlinePlayers.Count;
    public string CpuDisplay => State == ServerState.Running ? $"{_cpuPercent:0.0}%" : "—";
    public string MemoryDisplay => State == ServerState.Running ? FormatBytes(_workingSetBytes) : "—";
    public string UptimeDisplay => State == ServerState.Running ? FormatUptime(_uptime) : "—";
    private int? EffectiveJavaMajorVersion => _serviceJavaRuntime?.Available == true
        ? _serviceJavaRuntime.MajorVersion ?? Model.JavaMajorVersion
        : Model.JavaMajorVersion;

    public string JavaDisplay => EffectiveJavaMajorVersion is { } version
        ? $"Java {version}"
        : L("server.java.unspecified");
    public bool UsesGuiMemorySettings => true;
    public string MemoryConfigurationHint =>
        MemoryAllocationMode == MemoryAllocationMode.Automatic
        && !string.IsNullOrWhiteSpace(_automaticMemoryRecommendationStatusKey)
            ? L(_automaticMemoryRecommendationStatusKey, _automaticMemoryRecommendationStatusArguments)
            : Model.LaunchKind == ServerLaunchKind.ExecutableJar
                ? L("server.memory.executableHint")
                : L("server.memory.argumentFileHint");
    public string ConsoleCountText => L("server.console.lines", ConsoleLines.Count);
    public int DiagnosticIncidentCount => _diagnosticIncidentCount;
    public string DiagnosticCountText => L(
        "server.diagnostics.summary",
        DiagnosticIncidentCount,
        DiagnosticLines.Count);
    public string DiagnosticsTabHeader => L("server.diagnostics.header", DiagnosticIncidentCount);
    public bool HasDiagnosticLines => DiagnosticLines.Count > 0;
    public string PlayerSummary => ShowKnownPlayers
        ? L("server.players.summaryKnown", _onlinePlayers.Count, Players.Count)
        : L("server.players.summaryOnline", _onlinePlayers.Count);
    public bool HasVisiblePlayers => VisiblePlayers.Count > 0;
    public string EmptyPlayerListText => ShowKnownPlayers
        ? L("server.players.emptyKnown")
        : L("server.players.emptyOnline");

    public string ModpackSourceDisplay => Model.ModpackSource switch
    {
        ModpackSourceKind.Ftb => L(
            "server.modpack.source",
            "FTB",
            Model.ModpackVersionName ?? Model.ModpackVersionId ?? L("server.version.unknown")),
        ModpackSourceKind.Modrinth => L(
            "server.modpack.source",
            "Modrinth",
            Model.ModpackVersionName ?? Model.ModpackVersionId ?? L("server.version.unknown")),
        ModpackSourceKind.CurseForge => L(
            "server.modpack.source",
            "CurseForge",
            Model.ModpackVersionName ?? Model.ModpackVersionId ?? L("server.version.unknown")),
        _ => L("server.modpack.unlinked"),
    };

    public bool CanIterativelyUpdateModpack => Model.ModpackSource != ModpackSourceKind.None
                                               && !string.IsNullOrWhiteSpace(Model.ModpackProjectId)
                                               && !string.IsNullOrWhiteSpace(Model.ModpackVersionId)
                                               && State == ServerState.Stopped;

    public bool ShowKnownPlayers
    {
        get => _showKnownPlayers;
        set
        {
            if (!SetProperty(ref _showKnownPlayers, value)) return;
            RefreshVisiblePlayers(SelectedPlayer?.Name);
            OnPropertyChanged(nameof(PlayerSummary));
            OnPropertyChanged(nameof(EmptyPlayerListText));
        }
    }

    public PlayerEntryViewModel? SelectedPlayer
    {
        get => _selectedPlayer;
        set
        {
            if (!SetProperty(ref _selectedPlayer, value) || value is null) return;
            PlayerNameInput = value.Name;
        }
    }

    public string PlayerNameInput
    {
        get => _playerNameInput;
        set => SetProperty(ref _playerNameInput, value);
    }

    public void SetState(ServerState state)
    {
        State = state;
        if (state is ServerState.Stopped or ServerState.Crashed or ServerState.Faulted)
        {
            _activePort = null;
            _portListening = null;
            _cpuPercent = 0;
            _workingSetBytes = 0;
            _uptime = TimeSpan.Zero;
            OnPropertyChanged(nameof(ActivePort));
            OnPropertyChanged(nameof(ConnectionDisplay));
            OnPropertyChanged(nameof(ConnectionAddress));
            NotifyMetricsChanged();
            UpdateOnlinePlayers([]);
        }
    }

    public void NotifyModpackConfigurationChanged()
    {
        OnPropertyChanged(nameof(ServerJarPath));
        OnPropertyChanged(nameof(LaunchTargetPath));
        OnPropertyChanged(nameof(CoreInitial));
        OnPropertyChanged(nameof(CoreTypeText));
        OnPropertyChanged(nameof(MinecraftVersionDisplay));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(DetailSubtitle));
        OnPropertyChanged(nameof(JavaExecutablePath));
        OnPropertyChanged(nameof(JavaDisplay));
        OnPropertyChanged(nameof(ModpackSourceDisplay));
        OnPropertyChanged(nameof(CanIterativelyUpdateModpack));
        OnPropertyChanged(nameof(MemoryConfigurationHint));
        OnPropertyChanged(nameof(CatalogIconImagePath));
        OnPropertyChanged(nameof(CatalogPreviewImagePath));
        OnPropertyChanged(nameof(ModpackProviderId));
        OnPropertyChanged(nameof(EffectiveIconImagePath));
    }

    public void NotifyServiceRegistrationChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(ConnectionDisplay));
        OnPropertyChanged(nameof(ConnectionAddress));
        OnPropertyChanged(nameof(MinimumMemoryMb));
        OnPropertyChanged(nameof(MaximumMemoryMb));
        OnPropertyChanged(nameof(MinimumMemorySliderMb));
        OnPropertyChanged(nameof(MaximumMemorySliderMb));
        OnPropertyChanged(nameof(MemoryRangeDisplay));
        OnPropertyChanged(nameof(AutoRestart));
        OnPropertyChanged(nameof(SeparateDiagnosticOutput));
        OnPropertyChanged(nameof(EnableHangWatchdog));
        OnPropertyChanged(nameof(WatchdogCheckIntervalSeconds));
        OnPropertyChanged(nameof(WatchdogProbeTimeoutSeconds));
        OnPropertyChanged(nameof(WatchdogFailureThreshold));
        OnPropertyChanged(nameof(WatchdogStartupGraceSeconds));
        OnPropertyChanged(nameof(EnableAutomaticRecoveryPoints));
        OnPropertyChanged(nameof(RecoveryPointIntervalMinutes));
        OnPropertyChanged(nameof(RecoveryPointRetentionCount));
        RaiseMemoryModeProperties();
        NotifyModpackConfigurationChanged();
    }

    public void MarkPortAsActive(int port)
    {
        if (_activePort == port) return;
        _activePort = port;
        OnPropertyChanged(nameof(ActivePort));
        OnPropertyChanged(nameof(ConnectionDisplay));
        OnPropertyChanged(nameof(ConnectionAddress));
    }

    public void UpdateServiceRuntimeStatus(
        ProductServerJavaRuntimeSummary? java,
        bool? portListening)
    {
        _serviceJavaRuntime = java;
        _portListening = portListening;
        if (java?.Available == true && java.MajorVersion is { } major)
        {
            Model.JavaMajorVersion = major;
        }

        OnPropertyChanged(nameof(JavaDisplay));
        OnPropertyChanged(nameof(ConnectionDisplay));
    }

    public void UpdateMetrics(double cpuPercent, long workingSetBytes, TimeSpan uptime)
    {
        _cpuPercent = cpuPercent;
        _workingSetBytes = workingSetBytes;
        _uptime = uptime;
        NotifyMetricsChanged();
    }

    public void AppendConsole(ConsoleLine line)
    {
        AppendConsoleBatch([line]);
    }

    public void AppendConsoleBatch(IEnumerable<ConsoleLine> lines)
    {
        var additions = lines
            .TakeLast(MaximumUiConsoleLines)
            .Select(line => new ConsoleLineViewModel(line, ++_nextConsoleSequence))
            .ToArray();
        if (additions.Length == 0) return;

        var overflow = Math.Max(0, _consoleHistory.Count + additions.Length - MaximumUiConsoleLines);
        if (overflow > 0)
        {
            _consoleHistory.RemoveRange(0, overflow);
        }

        _consoleHistory.AddRange(additions);
        PublishConsoleProjections();

        NotifyConsoleCollectionsChanged();
    }

    public void ReplaceConsoleBatch(IEnumerable<ConsoleLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _consoleHistory.Clear();
        _consoleLines.Clear();
        _diagnosticLines.Clear();
        _diagnosticIncidentCount = 0;
        _nextConsoleSequence = 0;
        AppendConsoleBatch(lines);
    }

    private void ReflowConsoleLines()
    {
        _consoleLines.ReplaceAll(_consoleHistory.Where(line =>
            !SeparateDiagnosticOutput || !line.IsDiagnostic));
        OnPropertyChanged(nameof(ConsoleCountText));
    }

    private void PublishConsoleProjections()
    {
        _diagnosticLines.ReplaceAll(_consoleHistory.Where(line => line.IsDiagnostic));
        _consoleLines.ReplaceAll(_consoleHistory.Where(line =>
            !SeparateDiagnosticOutput || !line.IsDiagnostic));

        var incidentIds = new HashSet<Guid>();
        var incidentsWithoutIdentity = 0;
        foreach (var line in _consoleHistory)
        {
            if (!line.IsDiagnostic)
            {
                continue;
            }

            if (line.DiagnosticId is { } diagnosticId)
            {
                incidentIds.Add(diagnosticId);
            }
            else if (line.StartsDiagnostic)
            {
                incidentsWithoutIdentity++;
            }
        }

        _diagnosticIncidentCount = incidentIds.Count + incidentsWithoutIdentity;
    }

    private void NotifyConsoleCollectionsChanged()
    {
        OnPropertyChanged(nameof(ConsoleCountText));
        OnPropertyChanged(nameof(DiagnosticIncidentCount));
        OnPropertyChanged(nameof(DiagnosticCountText));
        OnPropertyChanged(nameof(DiagnosticsTabHeader));
        OnPropertyChanged(nameof(HasDiagnosticLines));
    }

    public void ReplacePlayers(IEnumerable<PlayerStatusRecord> records)
    {
        var selectedName = SelectedPlayer?.Name;
        var existing = Players.ToDictionary(player => player.Name, StringComparer.OrdinalIgnoreCase);
        var mergedRecords = records
            .Where(record => !string.IsNullOrWhiteSpace(record.Name))
            .Select(record => record with { IsOnline = _onlinePlayers.Contains(record.Name) })
            .ToDictionary(record => record.Name, StringComparer.OrdinalIgnoreCase);
        _registryPlayerNames.Clear();
        _registryPlayerNames.UnionWith(mergedRecords.Keys);
        foreach (var onlineName in _onlinePlayers.Snapshot())
        {
            if (mergedRecords.ContainsKey(onlineName)) continue;
            if (existing.TryGetValue(onlineName, out var knownPlayer))
            {
                mergedRecords[onlineName] = new PlayerStatusRecord(
                    knownPlayer.Name,
                    knownPlayer.Uuid,
                    true,
                    knownPlayer.IsOperator,
                    knownPlayer.IsWhitelisted,
                    knownPlayer.IsBanned);
            }
            else
            {
                mergedRecords[onlineName] = new PlayerStatusRecord(
                    onlineName, null, true, false, false, false);
            }
        }

        var ordered = mergedRecords.Values
            .OrderBy(player => player.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var replacement = new PlayerEntryViewModel[ordered.Length];
        var replacementIndex = 0;
        foreach (var record in ordered)
        {
            if (existing.TryGetValue(record.Name, out var player))
            {
                player.Update(record);
            }
            else
            {
                player = new PlayerEntryViewModel(record);
            }

            replacement[replacementIndex++] = player;
        }

        _players.ReplaceAll(replacement);

        RefreshVisiblePlayers(selectedName);
        OnPropertyChanged(nameof(PlayerSummary));
    }

    public void UpdateOnlinePlayers(IEnumerable<string> onlineNames)
    {
        var selectedName = SelectedPlayer?.Name;
        var names = onlineNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumTrackedOnlinePlayers)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_onlinePlayers.Count == names.Count && names.All(_onlinePlayers.Contains))
        {
            return;
        }

        _onlinePlayers.Replace(names);
        var existing = Players.ToDictionary(player => player.Name, StringComparer.OrdinalIgnoreCase);
        var visibleNames = VisiblePlayers
            .Select(player => player.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hiddenPlayers = new List<PlayerEntryViewModel>();
        var removedPlayers = new List<PlayerEntryViewModel>();
        for (var index = Players.Count - 1; index >= 0; index--)
        {
            var player = Players[index];
            player.UpdatePresence(names.Contains(player.Name));
            if (!player.IsOnline && !_registryPlayerNames.Contains(player.Name))
            {
                if (visibleNames.Remove(player.Name)) hiddenPlayers.Add(player);
                existing.Remove(player.Name);
                removedPlayers.Add(player);
            }
            else if (!ShowKnownPlayers && !player.IsOnline)
            {
                if (visibleNames.Remove(player.Name)) hiddenPlayers.Add(player);
            }
        }

        var additions = new List<PlayerEntryViewModel>();
        var newlyVisible = new List<PlayerEntryViewModel>();
        foreach (var name in names)
        {
            if (!existing.TryGetValue(name, out var player))
            {
                player = new PlayerEntryViewModel(
                    new PlayerStatusRecord(name, null, true, false, false, false));
                existing.Add(name, player);
                additions.Add(player);
            }

            if (visibleNames.Add(player.Name))
            {
                newlyVisible.Add(player);
            }
        }

        if (removedPlayers.Count + additions.Count > 64)
        {
            var removed = removedPlayers.ToHashSet();
            var orderedPlayers = Players
                .Where(player => !removed.Contains(player))
                .Concat(additions)
                .OrderBy(player => player.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            _players.ReplaceAll(orderedPlayers);
        }
        else
        {
            foreach (var player in removedPlayers)
            {
                Players.Remove(player);
            }

            foreach (var player in additions.OrderBy(
                         player => player.Name,
                         StringComparer.CurrentCultureIgnoreCase))
            {
                InsertKnownPlayerInNameOrder(player);
            }
        }

        if (hiddenPlayers.Count + newlyVisible.Count > 64)
        {
            RefreshVisiblePlayers(selectedName);
            return;
        }

        foreach (var player in hiddenPlayers)
        {
            VisiblePlayers.Remove(player);
        }

        foreach (var player in newlyVisible.OrderBy(
                     player => player.Name,
                     StringComparer.CurrentCultureIgnoreCase))
        {
            InsertVisiblePlayerInNameOrder(player);
        }

        SelectedPlayer = selectedName is null
            ? null
            : VisiblePlayers.FirstOrDefault(player =>
                player.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
        NotifyVisiblePlayersChanged();
    }

    public void UpdatePlayerPresence(string playerName, bool isOnline)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return;
        var normalizedName = playerName.Trim();
        if (isOnline
            && !_onlinePlayers.Contains(normalizedName)
            && _onlinePlayers.Count >= MaximumTrackedOnlinePlayers)
        {
            return;
        }

        _onlinePlayers.SetPresence(normalizedName, isOnline);

        var player = Players.FirstOrDefault(item =>
            item.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        if (player is null)
        {
            // A leave event without a matching join must not create another historical row.
            if (!isOnline) return;
            player = new PlayerEntryViewModel(
                new PlayerStatusRecord(normalizedName, null, true, false, false, false));
            InsertKnownPlayerInNameOrder(player);
        }
        else
        {
            player.UpdatePresence(isOnline);
        }

        if (!isOnline && !_registryPlayerNames.Contains(player.Name))
        {
            var wasSelected = ReferenceEquals(SelectedPlayer, player);
            VisiblePlayers.Remove(player);
            Players.Remove(player);
            if (wasSelected) SelectedPlayer = null;
            NotifyVisiblePlayersChanged();
            return;
        }

        var selectedName = SelectedPlayer?.Name;
        if (ShowKnownPlayers || isOnline)
        {
            InsertVisiblePlayerInNameOrder(player);
        }
        else
        {
            VisiblePlayers.Remove(player);
        }

        SelectedPlayer = selectedName is null
            ? null
            : VisiblePlayers.FirstOrDefault(item =>
                item.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
        NotifyVisiblePlayersChanged();
    }

    public void RefreshBackups()
    {
        Backups.Clear();
        var directory = Path.Combine(Model.DirectoryPath, "backups");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            Backups.Add(new BackupItemViewModel(path));
        }
    }

    public void ReplaceBackups(IEnumerable<ProductServerBackupSummary> backups)
    {
        ArgumentNullException.ThrowIfNull(backups);
        Backups.Clear();
        foreach (var backup in backups)
        {
            Backups.Add(new BackupItemViewModel(backup));
        }
    }

    private async Task SendCommandAsync()
    {
        var command = CommandText.Trim();
        if (command.Length == 0) return;
        try
        {
            await _sendCommand(Id, command);
            CommandText = string.Empty;
        }
        catch (Exception exception)
        {
            AppendConsole(SystemConsoleLineFactory.Create(
                Id,
                L("server.command.sendFailed", exception.Message),
                ConsoleLineSeverity.Error));
        }
    }

    private void NotifyMetricsChanged()
    {
        OnPropertyChanged(nameof(CpuPercent));
        OnPropertyChanged(nameof(WorkingSetBytes));
        OnPropertyChanged(nameof(Uptime));
        OnPropertyChanged(nameof(CpuDisplay));
        OnPropertyChanged(nameof(MemoryDisplay));
        OnPropertyChanged(nameof(UptimeDisplay));
    }

    public void SetMemorySliderMaximum(int maximumMb)
    {
        var normalized = Math.Max(2048, NormalizeMemorySliderValue(maximumMb));
        if (Math.Abs(MemorySliderMaximumMb - normalized) < 0.5) return;
        MemorySliderMaximumMb = normalized;
        OnPropertyChanged(nameof(MemorySliderMaximumMb));
    }

    public void SetSystemMemoryDisplay(long availableBytes, long totalBytes)
    {
        _availableSystemMemoryBytes = availableBytes;
        _totalSystemMemoryBytes = totalBytes;
        _hasSystemMemorySnapshot = true;
        UpdateSystemMemoryDisplay();
    }

    private void UpdateSystemMemoryDisplay()
    {
        var text = _hasSystemMemorySnapshot
            ? L(
                "server.memory.systemAvailable",
                _availableSystemMemoryBytes / 1024d / 1024d / 1024d,
                _totalSystemMemoryBytes / 1024d / 1024d / 1024d)
            : L("server.memory.systemLoading");
        if (_systemMemoryDisplay == text) return;
        _systemMemoryDisplay = text;
        OnPropertyChanged(nameof(SystemMemoryDisplay));
    }

    internal void BeginAutomaticMemoryRecommendation()
    {
        if (MemoryAllocationMode != MemoryAllocationMode.Automatic) return;
        HasSuccessfulAutomaticMemoryRecommendation = false;
        SetAutomaticMemoryRecommendationStatus("server.memory.autoEstimating");
        IsAutomaticMemoryRecommendationRunning = true;
    }

    internal void ApplyAutomaticMemoryRecommendation(MemoryRecommendation recommendation)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        if (MemoryAllocationMode != MemoryAllocationMode.Automatic) return;
        Model.MinimumMemoryMb = recommendation.MinimumMemoryMb;
        Model.MaximumMemoryMb = recommendation.MaximumMemoryMb;
        SetMemorySliderMaximum(Math.Max(
            recommendation.MaximumMemoryMb,
            recommendation.SafeAllocationCeilingMb));
        SetAutomaticMemoryRecommendationStatus(
            recommendation.WasConstrainedBySystemMemory
                ? "server.memory.autoCompleteConstrained"
                : "server.memory.autoComplete",
            recommendation.AddonJarCount,
            recommendation.AddonJarBytes / 1024d / 1024d,
            recommendation.MinimumMemoryMb,
            recommendation.MaximumMemoryMb,
            recommendation.ReservedSystemMemoryMb);
        HasSuccessfulAutomaticMemoryRecommendation = true;
        IsAutomaticMemoryRecommendationRunning = false;
        OnPropertyChanged(nameof(MinimumMemoryMb));
        OnPropertyChanged(nameof(MaximumMemoryMb));
        OnPropertyChanged(nameof(MinimumMemorySliderMb));
        OnPropertyChanged(nameof(MaximumMemorySliderMb));
        OnPropertyChanged(nameof(MemoryRangeDisplay));
    }

    internal void FailAutomaticMemoryRecommendation(string message)
    {
        if (MemoryAllocationMode != MemoryAllocationMode.Automatic) return;
        var normalized = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        var detail = normalized.Length == 0
            ? L("common.unknown")
            : normalized[..Math.Min(240, normalized.Length)];
        SetAutomaticMemoryRecommendationStatus("server.memory.autoFailed", detail);
        HasSuccessfulAutomaticMemoryRecommendation = false;
        IsAutomaticMemoryRecommendationRunning = false;
    }

    internal void CancelAutomaticMemoryRecommendation()
    {
        HasSuccessfulAutomaticMemoryRecommendation = false;
        if (!IsAutomaticMemoryRecommendationRunning) return;
        IsAutomaticMemoryRecommendationRunning = false;
        SetAutomaticMemoryRecommendationStatus(null);
    }

    public void ApplyEffectiveMemoryDisplay(int minimumMb, int maximumMb)
    {
        Model.MinimumMemoryMb = minimumMb;
        Model.MaximumMemoryMb = maximumMb;
        OnPropertyChanged(nameof(MinimumMemoryMb));
        OnPropertyChanged(nameof(MaximumMemoryMb));
        OnPropertyChanged(nameof(MinimumMemorySliderMb));
        OnPropertyChanged(nameof(MaximumMemorySliderMb));
        OnPropertyChanged(nameof(MemoryRangeDisplay));
    }

    private void SetManualMemoryMode()
    {
        if (Model.MemoryAllocationMode == MemoryAllocationMode.Manual) return;
        Model.MemoryAllocationMode = MemoryAllocationMode.Manual;
        RaiseMemoryModeProperties();
        _memoryModeRequested?.Invoke(this, MemoryAllocationMode.Manual);
    }

    private void RaiseMemoryModeProperties()
    {
        OnPropertyChanged(nameof(MemoryAllocationMode));
        OnPropertyChanged(nameof(IsMemoryUsingDefault));
        OnPropertyChanged(nameof(IsMemoryAutomatic));
        OnPropertyChanged(nameof(IsMemoryManual));
        OnPropertyChanged(nameof(MemoryConfigurationHint));
        RecalculateAutomaticMemoryCommand.NotifyCanExecuteChanged();
    }

    private void SetAutomaticMemoryRecommendationStatus(string? key, params object?[] arguments)
    {
        _automaticMemoryRecommendationStatusKey = key;
        _automaticMemoryRecommendationStatusArguments = arguments;
        OnPropertyChanged(nameof(MemoryConfigurationHint));
    }

    private void RequestAutomaticMemoryRecommendation()
    {
        if (MemoryAllocationMode != MemoryAllocationMode.Automatic) return;
        _memoryModeRequested?.Invoke(this, MemoryAllocationMode.Automatic);
    }

    private static int NormalizeMemorySliderValue(double value)
        => (int)Math.Clamp(Math.Round(value / 256) * 256, 512, 131072);

    private void RefreshVisiblePlayers(string? selectedName)
    {
        var displayMode = ShowKnownPlayers
            ? PlayerRosterDisplayMode.AllKnown
            : PlayerRosterDisplayMode.OnlineOnly;
        var visible = PlayerRosterFilter.Apply(Players, player => player.IsOnline, displayMode);

        _visiblePlayers.ReplaceAll(visible);

        SelectedPlayer = selectedName is null
            ? null
            : VisiblePlayers.FirstOrDefault(player =>
                player.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
        NotifyVisiblePlayersChanged();
    }

    private void InsertVisiblePlayerInNameOrder(PlayerEntryViewModel player)
    {
        VisiblePlayers.Remove(player);
        var index = 0;
        while (index < VisiblePlayers.Count
               && StringComparer.CurrentCultureIgnoreCase.Compare(VisiblePlayers[index].Name, player.Name) < 0)
        {
            index++;
        }

        VisiblePlayers.Insert(index, player);
    }

    private void InsertKnownPlayerInNameOrder(PlayerEntryViewModel player)
    {
        var index = 0;
        while (index < Players.Count
               && StringComparer.CurrentCultureIgnoreCase.Compare(Players[index].Name, player.Name) < 0)
        {
            index++;
        }

        Players.Insert(index, player);
    }

    private void NotifyVisiblePlayersChanged()
    {
        OnPropertyChanged(nameof(OnlinePlayerCount));
        OnPropertyChanged(nameof(PlayerSummary));
        OnPropertyChanged(nameof(HasVisiblePlayers));
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        UpdateSystemMemoryDisplay();
        OnPropertyChanged(nameof(OneDrivePerformanceWarning));
        OnPropertyChanged(nameof(CoreTypeText));
        OnPropertyChanged(nameof(MinecraftVersionDisplay));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(DetailSubtitle));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(UptimeDisplay));
        OnPropertyChanged(nameof(JavaDisplay));
        OnPropertyChanged(nameof(ConnectionDisplay));
        OnPropertyChanged(nameof(MemoryConfigurationHint));
        OnPropertyChanged(nameof(ConsoleCountText));
        OnPropertyChanged(nameof(DiagnosticCountText));
        OnPropertyChanged(nameof(DiagnosticsTabHeader));
        OnPropertyChanged(nameof(PlayerSummary));
        OnPropertyChanged(nameof(EmptyPlayerListText));
        OnPropertyChanged(nameof(ModpackSourceDisplay));
    }

    private static string FormatBytes(long bytes)
    {
        var megabytes = bytes / 1024d / 1024d;
        return megabytes >= 1024 ? $"{megabytes / 1024:0.00} GB" : $"{megabytes:0} MB";
    }

    private static string FormatUptime(TimeSpan uptime)
        => uptime.TotalDays >= 1
            ? L("server.uptime.daysHours", (int)uptime.TotalDays, uptime.ToString("hh\\:mm"))
            : uptime.ToString("hh\\:mm\\:ss");

    private static string L(string key, params object?[] arguments)
        => LocalizationService.Current.Get(key, arguments);
}
