using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Remote;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.App.ViewModels;

/// <summary>
/// Process-local user intent shared by every remote dialog. It is deliberately never serialized:
/// closing Web lasts only until this MCSV process exits, while the next launch auto-connects.
/// </summary>
internal sealed class RemoteAccessSessionState
{
    private int _isStoppedForCurrentRun;

    public RemoteAccessSessionState(bool initiallyStopped = false)
    {
        _isStoppedForCurrentRun = initiallyStopped ? 1 : 0;
    }

    public event EventHandler? Changed;

    public bool IsStoppedForCurrentRun => Volatile.Read(ref _isStoppedForCurrentRun) != 0;

    public void MarkStoppedForCurrentRun()
        => SetStoppedForCurrentRun(true);

    public void ClearForExplicitReconnect()
        => SetStoppedForCurrentRun(false);

    private void SetStoppedForCurrentRun(bool value)
    {
        var next = value ? 1 : 0;
        if (Interlocked.Exchange(ref _isStoppedForCurrentRun, next) == next)
        {
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class RemoteAccessSettingsViewModel : ObservableObject, IDisposable
{
    private const string TailscaleWindowsDownloadUrl = "https://tailscale.com/download/windows";
    private const string TailscaleHttpsSettingsUrl = "https://console.tailscale.com/admin/dns";
    private const string TailscaleFunnelDocumentationUrl = "https://tailscale.com/kb/1223/funnel";
    private const string GoogleAppPasswordsUrl = "https://myaccount.google.com/apppasswords";
    private const string CloudflaredDownloadUrl = "https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/downloads/";
    private readonly RemoteAccessCoordinator _coordinator;
    private readonly Func<RemoteControlSettings, Task> _persistAsync;
    private readonly Dispatcher _dispatcher;
    private readonly ICloudflaredBootstrapService _cloudflaredBootstrapService;
    private readonly bool _ownsCloudflaredBootstrapService;
    private readonly RemoteAccessSessionState _remoteAccessSessionState;
    private readonly CancellationToken _applicationStopping;
    private bool _enabled;
    private string _allowedLogin;
    private int _localPort;
    private RemoteAccessMode _accessMode;
    private string _cloudflaredExecutablePath;
    private string _cloudflareNamedPublicOrigin;
    private string _cloudflareNamedTunnelToken = string.Empty;
    private string _cloudflareNamedTunnelTokenStatus;
    private bool _isBusy;
    private string _statusMessage;
    private string? _errorMessage;
    private string? _publicUrl;
    private string _smtpSenderGmail;
    private string _smtpAppPassword = string.Empty;
    private string _registrationGmail;
    private string _verificationCode = string.Empty;
    private string _remoteUsername = string.Empty;
    private string _remotePin = string.Empty;
    private string _confirmedRemotePin = string.Empty;
    private bool _isEmailVerified;
    private bool _allowStartServer = true;
    private bool _allowStopServer = true;
    private bool _allowRestartServer = true;
    private bool _allowSendConsoleCommand = true;
    private bool _allowManagePlayers = true;
    private bool _allowCreateBackup = true;
    private string _provisioningStatus = string.Empty;
    private string? _provisioningError;
    private string _cloudflaredInstallStatus = L("remote.legacy.cloudflared.installHint");
    private string? _statusMessageLocalizationKey;
    private object?[] _statusMessageLocalizationArguments = [];
    private string? _provisioningStatusLocalizationKey;
    private object?[] _provisioningStatusLocalizationArguments = [];
    private string? _cloudflareTokenStatusLocalizationKey;
    private object?[] _cloudflareTokenStatusLocalizationArguments = [];
    private string? _cloudflaredInstallStatusLocalizationKey = "remote.legacy.cloudflared.installHint";
    private object?[] _cloudflaredInstallStatusLocalizationArguments = [];
    private bool _disposed;

    public RemoteAccessSettingsViewModel(
        RemoteControlSettings settings,
        RemoteAccessCoordinator coordinator,
        Func<RemoteControlSettings, Task> persistAsync,
        Dispatcher dispatcher)
        : this(
            settings,
            coordinator,
            persistAsync,
            dispatcher,
            new CloudflaredBootstrapService(AppContext.BaseDirectory),
            ownsCloudflaredBootstrapService: true,
            sessionState: null,
            applicationStopping: default)
    {
    }

    internal RemoteAccessSettingsViewModel(
        RemoteControlSettings settings,
        RemoteAccessCoordinator coordinator,
        Func<RemoteControlSettings, Task> persistAsync,
        Dispatcher dispatcher,
        RemoteAccessSessionState sessionState,
        CancellationToken applicationStopping = default)
        : this(
            settings,
            coordinator,
            persistAsync,
            dispatcher,
            new CloudflaredBootstrapService(AppContext.BaseDirectory),
            ownsCloudflaredBootstrapService: true,
            sessionState: sessionState,
            applicationStopping: applicationStopping)
    {
    }

    internal RemoteAccessSettingsViewModel(
        RemoteControlSettings settings,
        RemoteAccessCoordinator coordinator,
        Func<RemoteControlSettings, Task> persistAsync,
        Dispatcher dispatcher,
        ICloudflaredBootstrapService cloudflaredBootstrapService,
        bool ownsCloudflaredBootstrapService = false,
        RemoteAccessSessionState? sessionState = null,
        CancellationToken applicationStopping = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _persistAsync = persistAsync ?? throw new ArgumentNullException(nameof(persistAsync));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _cloudflaredBootstrapService = cloudflaredBootstrapService
            ?? throw new ArgumentNullException(nameof(cloudflaredBootstrapService));
        _ownsCloudflaredBootstrapService = ownsCloudflaredBootstrapService;
        _remoteAccessSessionState = sessionState
            ?? new RemoteAccessSessionState(initiallyStopped: !settings.Enabled);
        _applicationStopping = applicationStopping;
        _enabled = settings.Enabled;
        _allowedLogin = settings.AllowedLogin;
        _localPort = settings.LocalPort is >= 1024 and <= 65535
            ? settings.LocalPort
            : RemoteControlSettings.DefaultLocalPort;
        _accessMode = Enum.IsDefined(settings.AccessMode)
            ? settings.AccessMode
            : RemoteAccessMode.Tailscale;
        _cloudflaredExecutablePath = settings.CloudflaredExecutablePath ?? string.Empty;
        _cloudflareNamedPublicOrigin = settings.CloudflareNamedPublicOrigin ?? string.Empty;
        _cloudflareTokenStatusLocalizationKey = coordinator.HasCloudflareNamedTunnelToken
            ? "remote.legacy.token.stored"
            : "remote.legacy.token.notStored";
        _cloudflareNamedTunnelTokenStatus = L(_cloudflareTokenStatusLocalizationKey);
        var runtime = coordinator.State;
        _statusMessage = runtime.StatusMessage;
        _errorMessage = runtime.Error;
        _publicUrl = runtime.PublicUrl?.AbsoluteUri;
        _smtpSenderGmail = coordinator.SmtpSenderGmail ?? _allowedLogin;
        _registrationGmail = _allowedLogin;
        _provisioningStatusLocalizationKey = _accessMode == RemoteAccessMode.Tailscale
            ? "remote.legacy.gmail.notSent"
            : null;
        _provisioningStatus = _provisioningStatusLocalizationKey is null
            ? string.Empty
            : L(_provisioningStatusLocalizationKey);
        // New accounts always begin from an explicit desktop default. Existing account
        // permissions live only in their own rows and must never bleed into this editor.
        LoadPermissionSelection(RemoteWebPermission.All);

        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanApply);
        // Closing Web is an explicit runtime action. It must remain available while the service
        // is starting, faulted, or waiting for a retry instead of disappearing exactly when the
        // user needs to cancel this run's connection attempt.
        StopCommand = new AsyncRelayCommand(StopAsync, () => !IsBusy);
        CopyPublicUrlCommand = new RelayCommand(() => CopyText(PublicUrl), () => HasPublicUrl);
        RevokeSessionsCommand = new RelayCommand(
            RevokeSessions,
            () => !IsBusy && (IsRunning || HasRememberedDevices));
        RevokeRememberedDeviceCommand = new RelayCommand(
            (Action<object?>)RevokeRememberedDevice,
            (Predicate<object?>)(device =>
                !IsBusy && device is RemoteRememberedDeviceInfo
                {
                    Status: RemoteRememberedDeviceStatus.Active
                }));
        RefreshRememberedDevicesCommand = new RelayCommand(
            RebuildRememberedDevices,
            () => !IsBusy && IsSecurityStoreAvailable);
        SaveSmtpCommand = new AsyncRelayCommand(SaveSmtpAsync, () => CanSaveSmtp);
        DeleteSmtpCommand = new AsyncRelayCommand(DeleteSmtpAsync, () => !IsBusy && HasSavedSmtpCredential);
        SendVerificationCodeCommand = new AsyncRelayCommand(
            SendVerificationCodeAsync,
            () => CanSendVerificationCode);
        VerifyCodeCommand = new RelayCommand(VerifyCode, () => CanVerifyCode);
        RegisterAccountCommand = new AsyncRelayCommand(RegisterAccountAsync, () => CanRegisterAccount);
        SaveAccountPermissionsCommand = new RelayCommand(
            (Action<object?>)SaveAccountPermissions,
            (Predicate<object?>)(account =>
                !IsBusy && IsSecurityStoreAvailable && account is RemoteAccountRowViewModel));
        ResetAccountPinCommand = new AsyncRelayCommand(
            (Func<object?, Task>)ResetAccountPinAsync,
            (Predicate<object?>)(account =>
                !IsBusy && IsSecurityStoreAvailable &&
                account is RemoteAccountRowViewModel { CanResetPin: true }));
        DeleteAccountCommand = new RelayCommand(
            (Action<object?>)DeleteAccount,
            (Predicate<object?>)(account => !IsBusy && account is RemoteAccountRowViewModel));
        OpenGoogleAppPasswordsCommand = new RelayCommand(OpenGoogleAppPasswords);
        OpenTailscaleDownloadCommand = new RelayCommand(OpenTailscaleDownload);
        OpenTailscaleHttpsSettingsCommand = new RelayCommand(OpenTailscaleHttpsSettings);
        OpenTailscaleFunnelDocumentationCommand = new RelayCommand(OpenTailscaleFunnelDocumentation);
        OpenCloudflaredDownloadCommand = new RelayCommand(OpenCloudflaredDownload);
        SaveCloudflareNamedTunnelTokenCommand = new RelayCommand(
            SaveCloudflareNamedTunnelToken,
            () => CanSaveCloudflareNamedTunnelToken);
        DeleteCloudflareNamedTunnelTokenCommand = new AsyncRelayCommand(
            DeleteCloudflareNamedTunnelTokenAsync,
            () => CanDeleteCloudflareNamedTunnelToken);
        InstallCloudflaredCommand = new AsyncRelayCommand(
            InstallCloudflaredAsync,
            () => !IsBusy && !IsRunning);
        ChooseCloudflaredCommand = new RelayCommand(ChooseCloudflaredExecutable);
        CloseCommand = new RelayCommand(Close, () => !IsBusy);
        RebuildAccountRows();
        RebuildRememberedDevices();
        _remoteAccessSessionState.Changed += OnRemoteAccessSessionStateChanged;
        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _coordinator.ApprovedAccountChanged += OnApprovedAccountChanged;
        LocalizationService.Current.CultureChanged += OnCultureChanged;
    }

    public event EventHandler? CloseRequested;

    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public RelayCommand CopyPublicUrlCommand { get; }
    public RelayCommand RevokeSessionsCommand { get; }
    public RelayCommand RevokeRememberedDeviceCommand { get; }
    public RelayCommand RefreshRememberedDevicesCommand { get; }
    public AsyncRelayCommand SaveSmtpCommand { get; }
    public AsyncRelayCommand DeleteSmtpCommand { get; }
    public AsyncRelayCommand SendVerificationCodeCommand { get; }
    public RelayCommand VerifyCodeCommand { get; }
    public AsyncRelayCommand RegisterAccountCommand { get; }
    public RelayCommand SaveAccountPermissionsCommand { get; }
    public AsyncRelayCommand ResetAccountPinCommand { get; }
    public RelayCommand DeleteAccountCommand { get; }
    public RelayCommand OpenGoogleAppPasswordsCommand { get; }
    public RelayCommand OpenTailscaleDownloadCommand { get; }
    public RelayCommand OpenTailscaleHttpsSettingsCommand { get; }
    public RelayCommand OpenTailscaleFunnelDocumentationCommand { get; }
    public RelayCommand OpenCloudflaredDownloadCommand { get; }
    public RelayCommand SaveCloudflareNamedTunnelTokenCommand { get; }
    public AsyncRelayCommand DeleteCloudflareNamedTunnelTokenCommand { get; }
    public AsyncRelayCommand InstallCloudflaredCommand { get; }
    public RelayCommand ChooseCloudflaredCommand { get; }
    public RelayCommand CloseCommand { get; }
    public ObservableCollection<RemoteAccountRowViewModel> AccountRows { get; } = [];
    public ObservableCollection<RemoteRememberedDeviceInfo> RememberedDevices { get; } = [];

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
            NotifyConnectionStatusChanged();
            NotifyCommandStates();
        }
    }

    public string AllowedLogin
    {
        get => _allowedLogin;
        set
        {
            if (!SetProperty(ref _allowedLogin, value)) return;
            RegistrationGmail = value?.Trim() ?? string.Empty;
            NotifyCommandStates();
        }
    }

    public int LocalPort
    {
        get => _localPort;
        set
        {
            if (!SetProperty(ref _localPort, value)) return;
            OnPropertyChanged(nameof(LocalServiceUrl));
            NotifyCommandStates();
        }
    }

    public string LocalServiceUrl => $"http://127.0.0.1:{LocalPort}";

    public IReadOnlyList<RemoteAccessMode> AccessModes { get; } =
        [
            RemoteAccessMode.CloudflareQuickTunnel,
            RemoteAccessMode.Tailscale,
            RemoteAccessMode.TailscaleFunnel,
            RemoteAccessMode.CloudflareNamedTunnel
        ];

    public RemoteAccessMode AccessMode
    {
        get => _accessMode;
        set
        {
            if (!SetProperty(ref _accessMode, value)) return;
            OnPropertyChanged(nameof(IsTailscaleMode));
            OnPropertyChanged(nameof(IsFunnelMode));
            OnPropertyChanged(nameof(IsTailscaleProviderMode));
            OnPropertyChanged(nameof(IsQuickTunnelMode));
            OnPropertyChanged(nameof(IsNamedTunnelMode));
            OnPropertyChanged(nameof(IsCloudflareMode));
            OnPropertyChanged(nameof(IsLocalAccountMode));
            OnPropertyChanged(nameof(RequiresEmailVerification));
            OnPropertyChanged(nameof(TailscaleHttpsCertificateGuidanceText));
            OnPropertyChanged(nameof(CanEditAccountCredentialFields));
            OnPropertyChanged(nameof(AccessModeDescription));
            OnPropertyChanged(nameof(NetworkProviderStatusText));
            OnPropertyChanged(nameof(ApprovedAccountText));
            OnPropertyChanged(nameof(HasTailscaleApprovedAccount));
            if (!IsNamedTunnelMode)
            {
                // The PasswordBox is merely hidden outside Named mode. Discard its transient
                // replacement value so a secret never lingers behind another mode's UI.
                CloudflareNamedTunnelToken = string.Empty;
            }
            if (IsLocalAccountMode)
            {
                ClearTransientGmailUiState();
            }
            else if (IsTailscaleMode && string.IsNullOrWhiteSpace(ProvisioningStatus))
            {
                SetProvisioningStatus("remote.legacy.gmail.notSent");
            }
            RebuildAccountRows();
            NotifyCommandStates();
        }
    }

    public bool IsTailscaleMode
    {
        get => AccessMode == RemoteAccessMode.Tailscale;
        set { if (value) AccessMode = RemoteAccessMode.Tailscale; }
    }

    public bool IsFunnelMode
    {
        get => AccessMode == RemoteAccessMode.TailscaleFunnel;
        set { if (value) AccessMode = RemoteAccessMode.TailscaleFunnel; }
    }

    public bool IsTailscaleProviderMode => IsTailscaleMode || IsFunnelMode;

    public bool IsQuickTunnelMode
    {
        get => AccessMode == RemoteAccessMode.CloudflareQuickTunnel;
        set { if (value) AccessMode = RemoteAccessMode.CloudflareQuickTunnel; }
    }

    public bool IsNamedTunnelMode
    {
        get => AccessMode == RemoteAccessMode.CloudflareNamedTunnel;
        set { if (value) AccessMode = RemoteAccessMode.CloudflareNamedTunnel; }
    }

    public bool IsCloudflareMode => IsQuickTunnelMode || IsNamedTunnelMode;

    public bool IsLocalAccountMode => IsCloudflareMode || IsFunnelMode;

    public bool RequiresEmailVerification => IsTailscaleMode;
    public bool CanEditAccountCredentialFields => IsLocalAccountMode || IsEmailVerified;
    public string AccessModeDescription => AccessMode switch
    {
        RemoteAccessMode.CloudflareQuickTunnel =>
            L("remote.legacy.mode.quickDescription"),
        RemoteAccessMode.CloudflareNamedTunnel =>
            L("remote.legacy.mode.namedDescription"),
        RemoteAccessMode.TailscaleFunnel =>
            L("remote.legacy.mode.funnelDescription"),
        _ => L("remote.legacy.mode.tailscaleDescription")
    };

    public string CloudflaredExecutablePath
    {
        get => _cloudflaredExecutablePath;
        set
        {
            if (SetProperty(ref _cloudflaredExecutablePath, value ?? string.Empty))
            {
                NotifyCommandStates();
            }
        }
    }

    public string CloudflareNamedPublicOrigin
    {
        get => _cloudflareNamedPublicOrigin;
        set
        {
            if (SetProperty(ref _cloudflareNamedPublicOrigin, value ?? string.Empty))
            {
                NotifyCommandStates();
            }
        }
    }

    /// <summary>
    /// Transient replacement input only. The persisted token is never loaded into this property.
    /// </summary>
    public string CloudflareNamedTunnelToken
    {
        get => _cloudflareNamedTunnelToken;
        set
        {
            if (SetProperty(ref _cloudflareNamedTunnelToken, value ?? string.Empty))
            {
                NotifyCommandStates();
            }
        }
    }

    public bool HasCloudflareNamedTunnelToken => _coordinator.HasCloudflareNamedTunnelToken;

    public string CloudflareNamedTunnelTokenStatus
    {
        get => _cloudflareNamedTunnelTokenStatus;
        private set => SetProperty(ref _cloudflareNamedTunnelTokenStatus, value);
    }

    public bool CanSaveCloudflareNamedTunnelToken =>
        IsNamedTunnelMode
        && !IsBusy
        && IsSecurityStoreAvailable
        && !string.IsNullOrWhiteSpace(CloudflareNamedTunnelToken);

    public bool CanDeleteCloudflareNamedTunnelToken =>
        IsNamedTunnelMode
        && !IsBusy
        && IsSecurityStoreAvailable
        && HasCloudflareNamedTunnelToken;

    public string CloudflaredInstallStatus
    {
        get => _cloudflaredInstallStatus;
        private set => SetProperty(ref _cloudflaredInstallStatus, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) NotifyCommandStates();
        }
    }

    public bool IsRunning => _coordinator.State.IsRunning;
    public bool IsStarting => _coordinator.State.IsStarting;
    public bool IsRetryPending => !IsRunning
                                  && !IsStarting
                                  && _coordinator.State.AutoRetryRecommended;
    public bool IsTailscaleInstalled => _coordinator.State.IsTailscaleInstalled;
    public bool IsTailscaleConnected => _coordinator.State.IsTailscaleConnected;
    public bool RequiresTailscaleHttpsCertificateEnablement =>
        _coordinator.State.RequiresTailscaleHttpsCertificateEnablement;
    public string TailscaleHttpsCertificateGuidanceText => IsFunnelMode
        ? L("remote.legacy.tailscale.funnelCertificateGuidance")
        : L("remote.legacy.tailscale.certificateGuidance");
    public string TailscaleStatusText => IsTailscaleConnected
        ? L("remote.legacy.state.connected")
        : IsTailscaleInstalled
            ? L("remote.legacy.state.disconnected")
            : L("remote.legacy.state.notInstalled");
    public string NetworkProviderStatusText => AccessMode switch
    {
        RemoteAccessMode.CloudflareQuickTunnel => GetCloudflareStatusText("Quick Tunnel"),
        RemoteAccessMode.CloudflareNamedTunnel => GetCloudflareStatusText(L("remote.legacy.namedTunnelLabel")),
        RemoteAccessMode.TailscaleFunnel => GetCloudflareStatusText("Tailscale Funnel"),
        _ => IsStarting
            ? L("remote.legacy.provider.connecting", "Tailscale")
            : IsRetryPending
                ? L("remote.legacy.provider.reconnecting", "Tailscale")
                : L("remote.legacy.provider.state", "Tailscale", TailscaleStatusText)
    };

    private string GetCloudflareStatusText(string tunnelLabel)
        => IsRunning
            ? L("remote.legacy.provider.connected", tunnelLabel)
            : IsStarting
                ? L("remote.legacy.provider.connecting", tunnelLabel)
                : IsRetryPending
                    ? L("remote.legacy.provider.reconnecting", tunnelLabel)
                    : _remoteAccessSessionState.IsStoppedForCurrentRun
                        ? L("remote.legacy.provider.closedForRun", tunnelLabel)
                        : L("remote.legacy.provider.waiting", tunnelLabel);

    public string RemoteServiceStatusText => IsRunning
        ? L("remote.legacy.state.connected")
        : IsStarting
            ? L("remote.legacy.state.connecting")
            : IsRetryPending
                ? L("remote.legacy.state.reconnecting")
                : _remoteAccessSessionState.IsStoppedForCurrentRun
                    ? L("remote.legacy.state.closedForRun")
                    : L("remote.legacy.state.waiting");
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasPublicUrl => !string.IsNullOrWhiteSpace(PublicUrl);
    public bool HasSavedSmtpCredential => !string.IsNullOrWhiteSpace(_coordinator.SmtpSenderGmail);
    public string SavedSmtpSenderText => _coordinator.SmtpSenderGmail ?? L("remote.legacy.notStored");
    public bool HasApprovedAccount => _coordinator.ApprovedAccounts.Count > 0;
    public bool HasTailscaleApprovedAccount => _coordinator.ApprovedAccounts.Any(account => account.Gmail is not null);
    public bool HasVisibleAccountRows => AccountRows.Count > 0;
    public bool HasRememberedDevices => RememberedDevices.Count > 0;
    public string RememberedDeviceSummary => HasRememberedDevices
        ? L("remote.legacy.devices.summary", RememberedDevices.Count)
        : L("remote.legacy.devices.empty");
    public bool IsSecurityStoreAvailable => _coordinator.IsSecurityStoreAvailable;
    public string SecurityStoreStatus => _coordinator.SecurityStoreError
                                         ?? L("remote.legacy.securityStore.protected");
    public string ApprovedAccountText => HasVisibleAccountRows
        ? L("remote.legacy.accounts.summary", AccountRows.Count)
        : IsLocalAccountMode
            ? L("remote.legacy.accounts.emptyLocal")
            : L("remote.legacy.accounts.emptyTailscale");
    public bool CanApply => !IsBusy && HasValidConnectionSettings;

    private bool HasValidConnectionSettings =>
        LocalPort is >= 1024 and <= 65535
        && (AccessMode switch
        {
            RemoteAccessMode.CloudflareQuickTunnel =>
                IsUsableCloudflaredPath(CloudflaredExecutablePath),
            RemoteAccessMode.CloudflareNamedTunnel =>
                _coordinator.HasCloudflaredInstallationReceipt
                && IsManagedCloudflaredPath(CloudflaredExecutablePath)
                && CloudflareNamedTunnelConfiguration.TryNormalizePublicOrigin(
                    CloudflareNamedPublicOrigin,
                    out _)
                && HasCloudflareNamedTunnelToken,
            RemoteAccessMode.TailscaleFunnel => true,
            _ => RemoteIdentity.IsCanonicalGmailLogin(AllowedLogin?.Trim())
              && _coordinator.ApprovedAccounts
                  .Where(account => account.Gmail is not null)
                  .All(account => string.Equals(
                      account.Gmail,
                      AllowedLogin?.Trim(),
                      StringComparison.OrdinalIgnoreCase))
        });

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (!SetProperty(ref _errorMessage, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    private void SetStatusMessage(string key, params object?[] arguments)
    {
        _statusMessageLocalizationKey = key;
        _statusMessageLocalizationArguments = arguments;
        StatusMessage = L(key, arguments);
    }

    private void SetRawStatusMessage(string value)
    {
        _statusMessageLocalizationKey = null;
        _statusMessageLocalizationArguments = [];
        StatusMessage = value;
    }

    private void SetProvisioningStatus(string key, params object?[] arguments)
    {
        _provisioningStatusLocalizationKey = key;
        _provisioningStatusLocalizationArguments = arguments;
        ProvisioningStatus = L(key, arguments);
    }

    private void SetRawProvisioningStatus(string value)
    {
        _provisioningStatusLocalizationKey = null;
        _provisioningStatusLocalizationArguments = [];
        ProvisioningStatus = value;
    }

    private void SetCloudflareTokenStatus(string key, params object?[] arguments)
    {
        _cloudflareTokenStatusLocalizationKey = key;
        _cloudflareTokenStatusLocalizationArguments = arguments;
        CloudflareNamedTunnelTokenStatus = L(key, arguments);
    }

    private void SetCloudflaredInstallStatus(string key, params object?[] arguments)
    {
        _cloudflaredInstallStatusLocalizationKey = key;
        _cloudflaredInstallStatusLocalizationArguments = arguments;
        CloudflaredInstallStatus = L(key, arguments);
    }

    public string? PublicUrl
    {
        get => _publicUrl;
        private set
        {
            if (!SetProperty(ref _publicUrl, value)) return;
            OnPropertyChanged(nameof(HasPublicUrl));
            CopyPublicUrlCommand.NotifyCanExecuteChanged();
        }
    }

    public string SmtpSenderGmail
    {
        get => _smtpSenderGmail;
        set
        {
            if (SetProperty(ref _smtpSenderGmail, value)) NotifyCommandStates();
        }
    }

    public string SmtpAppPassword
    {
        get => _smtpAppPassword;
        set
        {
            if (SetProperty(ref _smtpAppPassword, value)) NotifyCommandStates();
        }
    }

    public string RegistrationGmail
    {
        get => _registrationGmail;
        set
        {
            if (!SetProperty(ref _registrationGmail, value)) return;
            IsEmailVerified = false;
            NotifyCommandStates();
        }
    }

    public string VerificationCode
    {
        get => _verificationCode;
        set
        {
            if (SetProperty(ref _verificationCode, value)) NotifyCommandStates();
        }
    }

    public string RemoteUsername
    {
        get => _remoteUsername;
        set
        {
            if (SetProperty(ref _remoteUsername, value)) NotifyCommandStates();
        }
    }

    public string RemotePin
    {
        get => _remotePin;
        set
        {
            if (SetProperty(ref _remotePin, value)) NotifyCommandStates();
        }
    }

    public string ConfirmedRemotePin
    {
        get => _confirmedRemotePin;
        set
        {
            if (SetProperty(ref _confirmedRemotePin, value)) NotifyCommandStates();
        }
    }

    public bool AllowStartServer
    {
        get => _allowStartServer;
        set { if (SetProperty(ref _allowStartServer, value)) NotifyCommandStates(); }
    }

    public bool AllowStopServer
    {
        get => _allowStopServer;
        set { if (SetProperty(ref _allowStopServer, value)) NotifyCommandStates(); }
    }

    public bool AllowRestartServer
    {
        get => _allowRestartServer;
        set { if (SetProperty(ref _allowRestartServer, value)) NotifyCommandStates(); }
    }

    public bool AllowSendConsoleCommand
    {
        get => _allowSendConsoleCommand;
        set { if (SetProperty(ref _allowSendConsoleCommand, value)) NotifyCommandStates(); }
    }

    public bool AllowManagePlayers
    {
        get => _allowManagePlayers;
        set { if (SetProperty(ref _allowManagePlayers, value)) NotifyCommandStates(); }
    }

    public bool AllowCreateBackup
    {
        get => _allowCreateBackup;
        set { if (SetProperty(ref _allowCreateBackup, value)) NotifyCommandStates(); }
    }

    public bool IsEmailVerified
    {
        get => _isEmailVerified;
        private set
        {
            if (!SetProperty(ref _isEmailVerified, value)) return;
            OnPropertyChanged(nameof(CanEditAccountCredentialFields));
            NotifyCommandStates();
        }
    }

    public string ProvisioningStatus
    {
        get => _provisioningStatus;
        private set
        {
            if (!SetProperty(ref _provisioningStatus, value)) return;
            OnPropertyChanged(nameof(HasProvisioningStatus));
        }
    }

    public string? ProvisioningError
    {
        get => _provisioningError;
        private set
        {
            if (!SetProperty(ref _provisioningError, value)) return;
            OnPropertyChanged(nameof(HasProvisioningError));
        }
    }

    public bool HasProvisioningError => !string.IsNullOrWhiteSpace(ProvisioningError);
    public bool HasProvisioningStatus => !string.IsNullOrWhiteSpace(ProvisioningStatus);
    public bool CanSaveSmtp => !IsBusy && IsSecurityStoreAvailable &&
                               RemoteIdentity.IsCanonicalGmailLogin(SmtpSenderGmail?.Trim()) &&
                               RemoteSecurityStore.TryNormalizeGoogleAppPassword(SmtpAppPassword, out _);
    public bool CanSendVerificationCode => IsTailscaleMode && !IsBusy && IsSecurityStoreAvailable &&
                                           HasSavedSmtpCredential &&
                                           RemoteIdentity.IsCanonicalGmailLogin(RegistrationGmail?.Trim()) &&
                                           string.Equals(
                                               RegistrationGmail?.Trim(),
                                               AllowedLogin?.Trim(),
                                               StringComparison.OrdinalIgnoreCase);
    public bool CanVerifyCode => IsTailscaleMode && !IsBusy && !IsEmailVerified &&
                                 VerificationCode is { Length: 6 } &&
                                 VerificationCode.All(character => character is >= '0' and <= '9');
    public bool CanRegisterAccount => !IsBusy && (IsLocalAccountMode || IsEmailVerified) &&
                                       IsSecurityStoreAvailable &&
                                      RemoteCredentialRules.TryNormalizeUsername(RemoteUsername, out _) &&
                                      !_coordinator.ApprovedAccounts.Any(account => string.Equals(
                                          account.Username,
                                          RemoteUsername?.Trim(),
                                          StringComparison.OrdinalIgnoreCase)) &&
                                      RemoteCredentialRules.IsValidPin(RemotePin) &&
                                      string.Equals(RemotePin, ConfirmedRemotePin, StringComparison.Ordinal);

    private async Task ApplyAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            _applicationStopping.ThrowIfCancellationRequested();
            // A valid apply always means "save and keep connected". The setting remains true
            // across application restarts; there is deliberately no checkbox that can silently
            // turn Apply into a stop operation.
            Enabled = true;
            _remoteAccessSessionState.ClearForExplicitReconnect();
            var settings = CreateSettings();
            await _persistAsync(settings);
            _applicationStopping.ThrowIfCancellationRequested();
            var result = await _coordinator.StartAsync(settings, _applicationStopping);
            ApplyRuntimeState(result);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ErrorMessage = exception.Message;
            SetStatusMessage("remote.legacy.status.applyFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SaveCloudflareNamedTunnelToken()
    {
        var wasStored = HasCloudflareNamedTunnelToken;
        try
        {
            _applicationStopping.ThrowIfCancellationRequested();
            _coordinator.SaveCloudflareNamedTunnelToken(CloudflareNamedTunnelToken);
            CloudflareNamedTunnelToken = string.Empty;
            OnPropertyChanged(nameof(HasCloudflareNamedTunnelToken));
            SetCloudflareTokenStatus(
                wasStored
                    ? "remote.legacy.token.replaced"
                    : "remote.legacy.token.saved");
            ErrorMessage = null;
            NotifyCommandStates();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CloudflareNamedTunnelToken = string.Empty;
            ErrorMessage = L(
                "remote.legacy.token.saveFailed",
                SanitizeProvisioningError(exception));
            SetCloudflareTokenStatus("remote.legacy.token.unchanged");
            NotifyCommandStates();
        }
    }

    private async Task DeleteCloudflareNamedTunnelTokenAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            _applicationStopping.ThrowIfCancellationRequested();
            // A running child inherits the token in its private environment. Stop it before
            // removing the durable copy so "delete" cannot leave a connector running invisibly.
            _remoteAccessSessionState.MarkStoppedForCurrentRun();
            if (_coordinator.State.AccessMode == RemoteAccessMode.CloudflareNamedTunnel
                && (_coordinator.State.IsRunning
                    || _coordinator.State.IsStarting
                    || _coordinator.State.AutoRetryRecommended))
            {
                var stopped = await _coordinator.StopAsync(
                    disableOwnedServe: true,
                    _applicationStopping);
                ApplyRuntimeState(stopped);
                if (stopped.Error is not null)
                {
                    throw new InvalidOperationException(
                        L("remote.legacy.token.stopFailed", stopped.Error));
                }
            }

            _coordinator.DeleteCloudflareNamedTunnelToken();
            CloudflareNamedTunnelToken = string.Empty;
            OnPropertyChanged(nameof(HasCloudflareNamedTunnelToken));
            SetCloudflareTokenStatus("remote.legacy.token.deleted");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ErrorMessage = L(
                "remote.legacy.token.deleteFailed",
                SanitizeProvisioningError(exception));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            _applicationStopping.ThrowIfCancellationRequested();
            // This is intentionally runtime-only. Persisted Enabled stays true so the next MCSV
            // launch reconnects automatically; closing the settings window also never stops Web.
            _remoteAccessSessionState.MarkStoppedForCurrentRun();
            ApplyRuntimeState(await _coordinator.StopAsync(
                disableOwnedServe: true,
                _applicationStopping));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ErrorMessage = exception.Message;
            SetStatusMessage("remote.legacy.status.stopFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveSmtpAsync()
    {
        IsBusy = true;
        ProvisioningError = null;
        try
        {
            _applicationStopping.ThrowIfCancellationRequested();
            var sender = SmtpSenderGmail;
            var appPassword = SmtpAppPassword;
            await Task.Run(
                () => _coordinator.SaveSmtpCredential(sender, appPassword),
                _applicationStopping);
            SmtpAppPassword = string.Empty;
            OnPropertyChanged(nameof(HasSavedSmtpCredential));
            OnPropertyChanged(nameof(SavedSmtpSenderText));
            SetProvisioningStatus("remote.legacy.smtp.saved");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ProvisioningError = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteSmtpAsync()
    {
        var result = DarkMessageBox.Show(
            GetDialogOwner(),
            L("remote.legacy.smtp.deleteConfirm"),
            L("remote.legacy.smtp.deleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        IsBusy = true;
        ProvisioningError = null;
        try
        {
            _applicationStopping.ThrowIfCancellationRequested();
            await Task.Run(_coordinator.DeleteSmtpCredential, _applicationStopping);
            SmtpAppPassword = string.Empty;
            IsEmailVerified = false;
            OnPropertyChanged(nameof(HasSavedSmtpCredential));
            OnPropertyChanged(nameof(SavedSmtpSenderText));
            SetProvisioningStatus("remote.legacy.smtp.deleted");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ProvisioningError = SanitizeProvisioningError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SendVerificationCodeAsync()
    {
        IsBusy = true;
        ProvisioningError = null;
        IsEmailVerified = false;
        try
        {
            _applicationStopping.ThrowIfCancellationRequested();
            EnsureRegistrationGmailMatchesAllowedLogin();
            var recipientGmail = RegistrationGmail.Trim();
            var result = await _coordinator.SendVerificationCodeAsync(
                recipientGmail,
                _applicationStopping);
            VerificationCode = string.Empty;
            SetProvisioningStatus(
                "remote.legacy.gmail.codeSent",
                recipientGmail,
                result.ExpiresAtUtc.ToLocalTime());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ProvisioningError = SanitizeProvisioningError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void VerifyCode()
    {
        ProvisioningError = null;
        try
        {
            _applicationStopping.ThrowIfCancellationRequested();
            EnsureRegistrationGmailMatchesAllowedLogin();
            var ticketExpiry = _coordinator.VerifyRegistrationCode(
                RegistrationGmail,
                VerificationCode);
            IsEmailVerified = true;
            VerificationCode = string.Empty;
            SetProvisioningStatus(
                "remote.legacy.gmail.verified",
                ticketExpiry.ToLocalTime());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            IsEmailVerified = false;
            ProvisioningError = SanitizeProvisioningError(exception);
        }
    }

    private async Task RegisterAccountAsync()
    {
        IsBusy = true;
        ProvisioningError = null;
        try
        {
            _applicationStopping.ThrowIfCancellationRequested();
            if (IsTailscaleMode)
            {
                EnsureRegistrationGmailMatchesAllowedLogin();
            }
            var username = RemoteUsername;
            var pin = RemotePin;
            var confirmedPin = ConfirmedRemotePin;
            var registrationGmail = RegistrationGmail.Trim();
            var permissions = GetSelectedPermissions();
            var settings = CreateSettings();
            // The approved account and manager.json must commit the same Gmail identity.
            // Persist first so closing the dialog immediately after registration cannot
            // leave a durable credential that the next startup's allowlist rejects.
            await _persistAsync(settings);
            _applicationStopping.ThrowIfCancellationRequested();
            if (IsLocalAccountMode)
            {
                await _coordinator.RegisterLocalApprovedAccountAsync(
                    username,
                    pin,
                    confirmedPin,
                    permissions,
                    _applicationStopping);
            }
            else
            {
                await _coordinator.RegisterApprovedAccountAsync(
                    registrationGmail,
                    username,
                    pin,
                    confirmedPin,
                    permissions,
                    _applicationStopping);
            }
            RemotePin = string.Empty;
            ConfirmedRemotePin = string.Empty;
            RemoteUsername = string.Empty;
            IsEmailVerified = false;
            SetProvisioningStatus(
                IsLocalAccountMode
                    ? "remote.legacy.account.createdLocal"
                    : "remote.legacy.account.createdTailscale");
            RefreshApprovedAccountState();

            // Account maintenance must not override an explicit "close Web for this run". The
            // saved auto-start intent remains true, but only Apply/Reconnect may reopen it now.
            if (settings.Enabled
                && !_remoteAccessSessionState.IsStoppedForCurrentRun
                && HasValidConnectionSettings)
            {
                _applicationStopping.ThrowIfCancellationRequested();
                var runtime = await _coordinator.StartAsync(settings, _applicationStopping);
                ApplyRuntimeState(runtime);
                if (!runtime.IsRunning)
                {
                    ProvisioningError = L(
                        "remote.legacy.account.createdServiceUnavailable",
                        runtime.Error ?? runtime.StatusMessage);
                }
            }
        }
        catch (RemoteEmailVerificationRequiredException exception)
        {
            IsEmailVerified = false;
            ProvisioningError = exception.Message;
            SetProvisioningStatus("remote.legacy.gmail.expired");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ProvisioningError = SanitizeProvisioningError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void DeleteAccount(object? parameter)
    {
        if (parameter is not RemoteAccountRowViewModel account)
        {
            ProvisioningError = L("remote.legacy.account.deleteMissing");
            return;
        }

        var result = DarkMessageBox.Show(
            GetDialogOwner(),
            IsLocalAccountMode
                ? L("remote.legacy.account.deleteLocalConfirm", account.Username)
                : L("remote.legacy.account.deleteTailscaleConfirm", account.Username),
            L("remote.legacy.account.deleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            _applicationStopping.ThrowIfCancellationRequested();
            _coordinator.DeleteApprovedAccount(account.Username);
            IsEmailVerified = false;
            ProvisioningError = null;
            SetProvisioningStatus("remote.legacy.account.deleted", account.Username);
            RefreshApprovedAccountState();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ProvisioningError = SanitizeProvisioningError(exception);
        }
    }

    private void SaveAccountPermissions(object? parameter)
    {
        if (parameter is not RemoteAccountRowViewModel account)
        {
            ProvisioningError = L("remote.legacy.account.permissionsMissing");
            return;
        }

        ProvisioningError = null;
        try
        {
            _applicationStopping.ThrowIfCancellationRequested();
            _coordinator.UpdateApprovedAccountPermissions(account.Username, account.SelectedPermissions);
            SetProvisioningStatus("remote.legacy.account.permissionsSaved", account.Username);
            RefreshApprovedAccountState(preferredExpandedUsername: account.Username);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ProvisioningError = SanitizeProvisioningError(exception);
        }
    }

    private async Task ResetAccountPinAsync(object? parameter)
    {
        if (parameter is not RemoteAccountRowViewModel account)
        {
            ProvisioningError = L("remote.legacy.account.resetMissing");
            return;
        }

        IsBusy = true;
        ProvisioningError = null;
        try
        {
            _applicationStopping.ThrowIfCancellationRequested();
            await _coordinator.ResetApprovedAccountPinAsync(
                account.Username,
                account.NewPin,
                account.ConfirmedNewPin,
                _applicationStopping);
            account.ClearResetPin();
            SetProvisioningStatus("remote.legacy.account.pinReset", account.Username);
            RefreshApprovedAccountState(preferredExpandedUsername: account.Username);
        }
        catch (Exception exception) when (IsRecoverableUiException(exception))
        {
            ProvisioningError = SanitizeProvisioningError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RevokeSessions()
    {
        var revokedDevices = _coordinator.RevokeAllRememberedDevicesAndSessions();
        RebuildRememberedDevices();
        SetStatusMessage(
            revokedDevices > 0
                ? "remote.legacy.devices.allRevoked"
                : "remote.legacy.devices.sessionsRevoked",
            revokedDevices > 0 ? [revokedDevices] : []);
    }

    private void RevokeRememberedDevice(object? parameter)
    {
        if (parameter is not RemoteRememberedDeviceInfo device) return;
        var result = DarkMessageBox.Show(
            GetDialogOwner(),
            L("remote.legacy.devices.revokeConfirm", device.Label),
            L("remote.legacy.devices.revokeTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        if (_coordinator.RevokeRememberedDevice(device.DeviceId))
        {
            SetStatusMessage("remote.legacy.devices.revoked", device.Label);
        }
        else
        {
            SetStatusMessage("remote.legacy.devices.alreadyRevoked");
        }
        RebuildRememberedDevices();
    }

    private void OnApprovedAccountChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        // Keep this an explicit zero-argument delegate. Passing a method group whose signature
        // later gains parameters makes Dispatcher.DynamicInvoke terminate the process with
        // TargetParameterCountException (observed in Preview 2's create-account path).
        _ = _dispatcher.BeginInvoke(
            () =>
            {
                if (_disposed) return;
                try
                {
                    RefreshApprovedAccountState();
                }
                catch (Exception exception) when (IsRecoverableUiException(exception))
                {
                    ProvisioningError = L(
                        "remote.legacy.account.refreshFailed",
                        SanitizeProvisioningError(exception));
                }
            },
            DispatcherPriority.Background);
    }

    private void OnCoordinatorStateChanged(object? sender, RemoteAccessRuntimeState state)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _ = _dispatcher.BeginInvoke(
            () =>
            {
                if (_disposed) return;
                try
                {
                    ApplyRuntimeState(state);
                }
                catch (Exception exception) when (IsRecoverableUiException(exception))
                {
                    ErrorMessage = L(
                        "remote.legacy.status.refreshFailed",
                        SanitizeProvisioningError(exception));
                    SetStatusMessage("remote.legacy.status.refreshRecovery");
                }
            },
            DispatcherPriority.Background);
    }

    private void ApplyRuntimeState(RemoteAccessRuntimeState state)
    {
        SetRawStatusMessage(state.StatusMessage);
        ErrorMessage = state.Error;
        PublicUrl = state.PublicUrl?.AbsoluteUri;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsStarting));
        OnPropertyChanged(nameof(IsRetryPending));
        OnPropertyChanged(nameof(IsTailscaleInstalled));
        OnPropertyChanged(nameof(IsTailscaleConnected));
        OnPropertyChanged(nameof(RequiresTailscaleHttpsCertificateEnablement));
        OnPropertyChanged(nameof(TailscaleStatusText));
        OnPropertyChanged(nameof(NetworkProviderStatusText));
        OnPropertyChanged(nameof(RemoteServiceStatusText));
        NotifyCommandStates();
    }

    private void OnRemoteAccessSessionStateChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            NotifyConnectionStatusChanged();
            return;
        }

        _ = _dispatcher.BeginInvoke(
            () =>
            {
                if (!_disposed)
                {
                    NotifyConnectionStatusChanged();
                }
            },
            DispatcherPriority.Background);
    }

    private void NotifyConnectionStatusChanged()
    {
        OnPropertyChanged(nameof(NetworkProviderStatusText));
        OnPropertyChanged(nameof(RemoteServiceStatusText));
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        if (_statusMessageLocalizationKey is not null)
        {
            StatusMessage = L(
                _statusMessageLocalizationKey,
                _statusMessageLocalizationArguments);
        }

        if (_provisioningStatusLocalizationKey is not null)
        {
            ProvisioningStatus = L(
                _provisioningStatusLocalizationKey,
                _provisioningStatusLocalizationArguments);
        }

        if (_cloudflareTokenStatusLocalizationKey is not null)
        {
            CloudflareNamedTunnelTokenStatus = L(
                _cloudflareTokenStatusLocalizationKey,
                _cloudflareTokenStatusLocalizationArguments);
        }

        if (_cloudflaredInstallStatusLocalizationKey is not null)
        {
            CloudflaredInstallStatus = L(
                _cloudflaredInstallStatusLocalizationKey,
                _cloudflaredInstallStatusLocalizationArguments);
        }

        OnPropertyChanged(nameof(AccessModeDescription));
        OnPropertyChanged(nameof(TailscaleHttpsCertificateGuidanceText));
        OnPropertyChanged(nameof(TailscaleStatusText));
        OnPropertyChanged(nameof(NetworkProviderStatusText));
        OnPropertyChanged(nameof(RemoteServiceStatusText));
        OnPropertyChanged(nameof(SavedSmtpSenderText));
        OnPropertyChanged(nameof(RememberedDeviceSummary));
        OnPropertyChanged(nameof(SecurityStoreStatus));
        OnPropertyChanged(nameof(ApprovedAccountText));
    }

    private RemoteControlSettings CreateSettings() => new()
    {
        Enabled = Enabled,
        AllowedLogin = AllowedLogin?.Trim() ?? string.Empty,
        LocalPort = LocalPort,
        AccessMode = AccessMode,
        CloudflaredExecutablePath = CloudflaredExecutablePath?.Trim() ?? string.Empty,
        CloudflareNamedPublicOrigin =
            CloudflareNamedTunnelConfiguration.TryNormalizePublicOrigin(
                CloudflareNamedPublicOrigin,
                out var publicOrigin)
                ? publicOrigin!.AbsoluteUri
                : CloudflareNamedPublicOrigin?.Trim() ?? string.Empty,
    };

    private void RefreshApprovedAccountState(string? preferredExpandedUsername = null)
    {
        RebuildAccountRows(preferredExpandedUsername);
        RebuildRememberedDevices();
        OnPropertyChanged(nameof(HasApprovedAccount));
        OnPropertyChanged(nameof(HasTailscaleApprovedAccount));
        OnPropertyChanged(nameof(HasVisibleAccountRows));
        OnPropertyChanged(nameof(ApprovedAccountText));
        OnPropertyChanged(nameof(HasSavedSmtpCredential));
        OnPropertyChanged(nameof(SavedSmtpSenderText));
        OnPropertyChanged(nameof(CanApply));
        NotifyCommandStates();
    }

    private void RebuildRememberedDevices()
    {
        RememberedDevices.Clear();
        if (_coordinator.IsSecurityStoreAvailable)
        {
            foreach (var device in _coordinator.RememberedDevices.Where(device =>
                         device.Status == RemoteRememberedDeviceStatus.Active))
            {
                RememberedDevices.Add(device);
            }
        }

        OnPropertyChanged(nameof(HasRememberedDevices));
        OnPropertyChanged(nameof(RememberedDeviceSummary));
        RevokeSessionsCommand?.NotifyCanExecuteChanged();
        RevokeRememberedDeviceCommand?.NotifyCanExecuteChanged();
    }

    private void RebuildAccountRows(string? preferredExpandedUsername = null)
    {
        var expandedUsernames = AccountRows
            .Where(account => account.IsExpanded)
            .Select(account => account.Username)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in AccountRows)
        {
            row.EditorStateChanged -= OnAccountRowEditorStateChanged;
            row.HideRevealedPin();
            row.ClearResetPin();
        }

        AccountRows.Clear();
        foreach (var approvedAccount in _coordinator.ApprovedAccounts.Where(account =>
                     IsLocalAccountMode ? account.Gmail is null : account.Gmail is not null))
        {
            var accountUsername = approvedAccount.Username;
            var row = new RemoteAccountRowViewModel(
                approvedAccount,
                IsTailscaleMode,
                () => RevealPinForAccount(accountUsername))
            {
                IsExpanded = string.Equals(
                                 approvedAccount.Username,
                                 preferredExpandedUsername,
                                 StringComparison.OrdinalIgnoreCase)
                             || expandedUsernames.Contains(approvedAccount.Username)
            };
            row.EditorStateChanged += OnAccountRowEditorStateChanged;
            AccountRows.Add(row);
        }
        OnPropertyChanged(nameof(HasVisibleAccountRows));
        OnPropertyChanged(nameof(ApprovedAccountText));
    }

    private string? RevealPinForAccount(string username)
    {
        foreach (var row in AccountRows.Where(row =>
                     !string.Equals(row.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            row.HideRevealedPin();
        }

        return _coordinator.GetRecoverableApprovedAccountPin(username);
    }

    internal void HideRevealedSecrets()
    {
        foreach (var row in AccountRows)
        {
            row.HideRevealedPin();
        }
    }

    private void OnAccountRowEditorStateChanged(object? sender, EventArgs e)
    {
        SaveAccountPermissionsCommand.NotifyCanExecuteChanged();
        ResetAccountPinCommand.NotifyCanExecuteChanged();
        DeleteAccountCommand.NotifyCanExecuteChanged();
    }

    private void ClearTransientGmailUiState()
    {
        VerificationCode = string.Empty;
        IsEmailVerified = false;
        _coordinator.ResetTransientEmailVerification();
        if (IsGmailProvisioningText(ProvisioningStatus))
        {
            SetRawProvisioningStatus(string.Empty);
        }

        if (IsGmailProvisioningText(ProvisioningError))
        {
            ProvisioningError = null;
        }
    }

    private static bool IsGmailProvisioningText(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           (value.Contains("Gmail", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("SMTP", StringComparison.OrdinalIgnoreCase));

    private RemoteWebPermission GetSelectedPermissions()
    {
        var permissions = RemoteWebPermission.None;
        if (AllowStartServer) permissions |= RemoteWebPermission.StartServer;
        if (AllowStopServer) permissions |= RemoteWebPermission.StopServer;
        if (AllowRestartServer) permissions |= RemoteWebPermission.RestartServer;
        if (AllowSendConsoleCommand) permissions |= RemoteWebPermission.SendConsoleCommand;
        if (AllowManagePlayers) permissions |= RemoteWebPermission.ManagePlayers;
        if (AllowCreateBackup) permissions |= RemoteWebPermission.CreateBackup;
        return permissions;
    }

    private void LoadPermissionSelection(RemoteWebPermission permissions)
    {
        _allowStartServer = permissions.HasFlag(RemoteWebPermission.StartServer);
        _allowStopServer = permissions.HasFlag(RemoteWebPermission.StopServer);
        _allowRestartServer = permissions.HasFlag(RemoteWebPermission.RestartServer);
        _allowSendConsoleCommand = permissions.HasFlag(RemoteWebPermission.SendConsoleCommand);
        _allowManagePlayers = permissions.HasFlag(RemoteWebPermission.ManagePlayers);
        _allowCreateBackup = permissions.HasFlag(RemoteWebPermission.CreateBackup);
        OnPropertyChanged(nameof(AllowStartServer));
        OnPropertyChanged(nameof(AllowStopServer));
        OnPropertyChanged(nameof(AllowRestartServer));
        OnPropertyChanged(nameof(AllowSendConsoleCommand));
        OnPropertyChanged(nameof(AllowManagePlayers));
        OnPropertyChanged(nameof(AllowCreateBackup));
    }

    private void EnsureRegistrationGmailMatchesAllowedLogin()
    {
        if (!RemoteIdentity.IsCanonicalGmailLogin(RegistrationGmail?.Trim()) ||
            !string.Equals(
                RegistrationGmail?.Trim(),
                AllowedLogin?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(L("remote.legacy.gmail.mustMatch"));
        }
    }

    private static string SanitizeProvisioningError(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("credentials", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("535", StringComparison.OrdinalIgnoreCase))
        {
            return L("remote.legacy.smtp.authenticationFailed");
        }

        return message.Length <= 360 ? message : message[..360] + "…";
    }

    private static bool IsRecoverableUiException(Exception exception)
        => exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private void CopyText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            Clipboard.SetText(value);
            SetStatusMessage("remote.legacy.status.copied");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ErrorMessage = L("remote.legacy.status.clipboardFailed", exception.Message);
        }
    }

    private void OpenTailscaleDownload()
        => OpenExternalPage(TailscaleWindowsDownloadUrl, L("remote.legacy.page.tailscaleDownload"));

    private void OpenGoogleAppPasswords()
        => OpenExternalPage(GoogleAppPasswordsUrl, L("remote.legacy.page.googleAppPasswords"));

    private void OpenTailscaleHttpsSettings()
        => OpenExternalPage(TailscaleHttpsSettingsUrl, L("remote.legacy.page.tailscaleHttps"));

    private void OpenTailscaleFunnelDocumentation()
        => OpenExternalPage(TailscaleFunnelDocumentationUrl, L("remote.legacy.page.tailscaleFunnel"));

    private void OpenCloudflaredDownload()
        => OpenExternalPage(CloudflaredDownloadUrl, L("remote.legacy.page.cloudflareDownload"));

    private async Task InstallCloudflaredAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        SetCloudflaredInstallStatus("remote.legacy.cloudflared.preparing");
        try
        {
            _applicationStopping.ThrowIfCancellationRequested();
            var requiresNamedTunnelReceipt = IsNamedTunnelMode;
            var progress = new Progress<CloudflaredBootstrapProgress>(value =>
            {
                if (value.Percentage is { } percentage)
                {
                    SetCloudflaredInstallStatus(
                        "remote.legacy.cloudflared.downloading",
                        Math.Clamp(percentage, 0, 100));
                }
                else
                {
                    SetCloudflaredInstallStatus("remote.legacy.cloudflared.installing");
                }
            });
            var result = await _cloudflaredBootstrapService.InstallLatestAsync(
                progress,
                _applicationStopping);
            var receiptSaved = true;
            try
            {
                _coordinator.SaveCloudflaredInstallationReceipt(result);
            }
            catch (Exception exception) when (
                !requiresNamedTunnelReceipt && exception is not OutOfMemoryException)
            {
                // Quick Tunnel does not consume a fixed-domain credential and retains its
                // existing explicit-path behavior. A Vault problem must not regress that mode.
                receiptSaved = false;
            }

            CloudflaredExecutablePath = result.ExecutablePath;
            SetCloudflaredInstallStatus(
                receiptSaved
                    ? "remote.legacy.cloudflared.verified"
                    : "remote.legacy.cloudflared.verifiedWithoutReceipt",
                result.Version);
            SetStatusMessage(
                receiptSaved
                    ? "remote.legacy.cloudflared.installed"
                    : "remote.legacy.cloudflared.vaultProblem");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (message.Length > 360)
            {
                message = message[..360] + "…";
            }

            ErrorMessage = L("remote.legacy.cloudflared.installFailed", message);
            SetCloudflaredInstallStatus("remote.legacy.cloudflared.trustIncomplete");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ChooseCloudflaredExecutable()
    {
        var picker = new OpenFileDialog
        {
            Title = L("remote.legacy.cloudflared.pickerTitle"),
            Filter = L("remote.legacy.cloudflared.pickerFilter"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(GetDialogOwner()) == true)
        {
            CloudflaredExecutablePath = Path.GetFullPath(picker.FileName);
        }
    }

    private static bool IsUsableCloudflaredPath(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path)
                   && Path.IsPathFullyQualified(path)
                   && File.Exists(path)
                   && string.Equals(Path.GetFileName(path), "cloudflared.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static bool IsManagedCloudflaredPath(string? path)
    {
        try
        {
            if (!IsUsableCloudflaredPath(path))
            {
                return false;
            }

            var configured = Path.GetFullPath(path!);
            var managed = CloudflaredNamedTunnelExecutableVerifier.GetManagedExecutablePath(
                AppContext.BaseDirectory);
            return string.Equals(
                configured,
                managed,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            IOException or
            UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void OpenExternalPage(string url, string pageName)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ErrorMessage = L("remote.legacy.page.openFailed", pageName, exception.Message);
        }
    }

    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private Window? GetDialogOwner()
        => Application.Current?.Windows
               .OfType<Window>()
               .FirstOrDefault(window => window.IsVisible && ReferenceEquals(window.DataContext, this))
           ?? Application.Current?.MainWindow;

    private static string L(string key, params object?[] arguments)
        => LocalizationService.Current.Get(key, arguments);

    private void NotifyCommandStates()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RevokeSessionsCommand.NotifyCanExecuteChanged();
        RevokeRememberedDeviceCommand.NotifyCanExecuteChanged();
        RefreshRememberedDevicesCommand.NotifyCanExecuteChanged();
        SaveSmtpCommand.NotifyCanExecuteChanged();
        DeleteSmtpCommand.NotifyCanExecuteChanged();
        SendVerificationCodeCommand.NotifyCanExecuteChanged();
        VerifyCodeCommand.NotifyCanExecuteChanged();
        RegisterAccountCommand.NotifyCanExecuteChanged();
        SaveAccountPermissionsCommand.NotifyCanExecuteChanged();
        ResetAccountPinCommand.NotifyCanExecuteChanged();
        DeleteAccountCommand.NotifyCanExecuteChanged();
        SaveCloudflareNamedTunnelTokenCommand.NotifyCanExecuteChanged();
        DeleteCloudflareNamedTunnelTokenCommand.NotifyCanExecuteChanged();
        InstallCloudflaredCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _remoteAccessSessionState.Changed -= OnRemoteAccessSessionStateChanged;
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _coordinator.ApprovedAccountChanged -= OnApprovedAccountChanged;
        LocalizationService.Current.CultureChanged -= OnCultureChanged;
        SmtpAppPassword = string.Empty;
        CloudflareNamedTunnelToken = string.Empty;
        RemotePin = string.Empty;
        ConfirmedRemotePin = string.Empty;
        HideRevealedSecrets();
        foreach (var row in AccountRows)
        {
            row.EditorStateChanged -= OnAccountRowEditorStateChanged;
            row.HideRevealedPin();
            row.ClearResetPin();
        }
        AccountRows.Clear();
        RememberedDevices.Clear();
        if (_ownsCloudflaredBootstrapService)
        {
            _cloudflaredBootstrapService.Dispose();
        }
    }
}
