using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Client;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Security;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.App.ViewModels;

internal sealed record ProductServiceRemoteServerOption(Guid Id, string Name);

internal sealed record ProductServiceRemoteRoleOption(
    ProductRemoteAccountRole Role,
    string Label);

internal sealed class ProductServiceRemotePermissionServerViewModel : ObservableObject
{
    private bool _isGranted;

    public ProductServiceRemotePermissionServerViewModel(
        ProductServiceRemoteServerOption server,
        bool isGranted)
    {
        Server = server;
        _isGranted = isGranted;
    }

    public ProductServiceRemoteServerOption Server { get; }
    public Guid ServerId => Server.Id;
    public string Name => Server.Name;

    public bool IsGranted
    {
        get => _isGranted;
        set => SetProperty(ref _isGranted, value);
    }
}

internal sealed class ProductServiceRemotePermissionViewModel : ObservableObject
{
    private bool _isGlobalGranted;

    public ProductServiceRemotePermissionViewModel(
        ProductPermissionDescriptor descriptor,
        IReadOnlyList<ProductServiceRemoteServerOption> servers,
        IReadOnlyCollection<ProductPermissionGrant> grants)
    {
        Descriptor = descriptor;
        _isGlobalGranted = grants.Any(grant =>
            string.Equals(grant.PermissionCode, descriptor.Code, StringComparison.Ordinal)
            && grant.Scope.Kind == ProductPermissionScopeKind.Global);
        Servers = new ObservableCollection<ProductServiceRemotePermissionServerViewModel>(
            descriptor.SupportsServerScope
                ? servers.Select(server => new ProductServiceRemotePermissionServerViewModel(
                    server,
                    grants.Any(grant =>
                        string.Equals(grant.PermissionCode, descriptor.Code, StringComparison.Ordinal)
                        && grant.Scope.Kind == ProductPermissionScopeKind.Server
                        && grant.Scope.ServerId == server.Id)))
                : []);
    }

    public ProductPermissionDescriptor Descriptor { get; }
    public string Code => Descriptor.Code;
    public string Category => Descriptor.Category;
    public bool IsHighRisk => Descriptor.IsHighRisk;
    public bool SupportsServerScope => Descriptor.SupportsServerScope;
    public ObservableCollection<ProductServiceRemotePermissionServerViewModel> Servers { get; }

    public bool IsGlobalGranted
    {
        get => _isGlobalGranted;
        set => SetProperty(ref _isGlobalGranted, value);
    }

    public IEnumerable<ProductPermissionGrant> BuildGrants()
    {
        if (IsGlobalGranted)
        {
            yield return new ProductPermissionGrant(Code, ProductPermissionScope.Global);
        }

        foreach (var server in Servers.Where(server => server.IsGranted))
        {
            yield return new ProductPermissionGrant(
                Code,
                ProductPermissionScope.ForServer(server.ServerId));
        }
    }

    public void GrantGlobally()
    {
        IsGlobalGranted = true;
        foreach (var server in Servers)
        {
            server.IsGranted = false;
        }
    }

    public void Clear()
    {
        IsGlobalGranted = false;
        foreach (var server in Servers)
        {
            server.IsGranted = false;
        }
    }
}

internal sealed class ProductServiceRemoteAccountViewModel : ObservableObject
{
    private bool _enabled;
    private bool _isPinRevealed;
    private string _revealedPin = string.Empty;
    private string _newPin = string.Empty;
    private string _confirmedNewPin = string.Empty;
    private ProductRemoteAccountRole _role;

    public ProductServiceRemoteAccountViewModel(
        ProductRemoteAccountSummary account,
        IReadOnlyList<ProductServiceRemoteServerOption> servers)
    {
        Account = account;
        _enabled = account.Enabled;
        _role = account.Role;
        Permissions = new ObservableCollection<ProductServiceRemotePermissionViewModel>(
            ProductPermissionCatalog.All
                .OrderBy(descriptor => descriptor.Category, StringComparer.Ordinal)
                .ThenBy(descriptor => descriptor.Code, StringComparer.Ordinal)
                .Select(descriptor => new ProductServiceRemotePermissionViewModel(
                    descriptor,
                    servers,
                    account.Grants)));
    }

    public ProductRemoteAccountSummary Account { get; }
    public string Username => Account.Username;
    public string CredentialSubject => Account.CredentialSubject;
    public string IdentityText => string.IsNullOrWhiteSpace(Account.Email)
        ? L("remote.service.localAccount")
        : Account.Email!;
    public string LockoutText => Account.LockedUntilUtc is { } lockedUntil && lockedUntil > DateTimeOffset.UtcNow
        ? L("remote.service.lockedUntil", lockedUntil.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
        : L("remote.service.canSignIn");
    public ObservableCollection<ProductServiceRemotePermissionViewModel> Permissions { get; }
    public string RoleDisplayText => L($"remote.service.role.{Role.ToString().ToLowerInvariant()}");

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public ProductRemoteAccountRole Role
    {
        get => _role;
        set
        {
            if (!IsDefinedRole(value) || !SetProperty(ref _role, value)) return;
            if (value == ProductRemoteAccountRole.Owner)
            {
                EnsureOwnerManagementGrants();
            }
            OnPropertyChanged(nameof(RoleDisplayText));
        }
    }

    public bool IsPinRevealed
    {
        get => _isPinRevealed;
        private set
        {
            if (!SetProperty(ref _isPinRevealed, value)) return;
            OnPropertyChanged(nameof(PinDisplayText));
            OnPropertyChanged(nameof(PinToggleText));
        }
    }

    public string PinDisplayText => IsPinRevealed ? _revealedPin : "••••••••";
    public string PinToggleText => IsPinRevealed
        ? L("remote.service.hidePin")
        : L("remote.service.showPin");

    public string NewPin
    {
        get => _newPin;
        set => SetProperty(ref _newPin, value ?? string.Empty);
    }

    public string ConfirmedNewPin
    {
        get => _confirmedNewPin;
        set => SetProperty(ref _confirmedNewPin, value ?? string.Empty);
    }

    public IReadOnlyList<ProductPermissionGrant> BuildGrants()
    {
        var grants = Permissions
            .SelectMany(permission => permission.BuildGrants())
            .Distinct()
            .ToArray();
        if (grants.Length > 256)
        {
            throw new InvalidOperationException(
                L("remote.service.grantLimit"));
        }

        return grants;
    }

    public void SetRevealedPin(string pin)
    {
        _revealedPin = pin;
        IsPinRevealed = true;
        OnPropertyChanged(nameof(PinDisplayText));
    }

    public void HidePin()
    {
        _revealedPin = string.Empty;
        IsPinRevealed = false;
        OnPropertyChanged(nameof(PinDisplayText));
    }

    public void ClearPinEditor()
    {
        NewPin = string.Empty;
        ConfirmedNewPin = string.Empty;
    }

    public void GrantAllGlobally()
    {
        foreach (var permission in Permissions)
        {
            permission.GrantGlobally();
        }
    }

    public void ClearAllGrants()
    {
        foreach (var permission in Permissions)
        {
            permission.Clear();
        }

        if (Role == ProductRemoteAccountRole.Owner)
        {
            EnsureOwnerManagementGrants();
        }
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(IdentityText));
        OnPropertyChanged(nameof(LockoutText));
        OnPropertyChanged(nameof(PinToggleText));
        OnPropertyChanged(nameof(RoleDisplayText));
    }

    private void EnsureOwnerManagementGrants()
    {
        foreach (var code in new[]
                 {
                     ProductPermissionCodes.UserRead,
                     ProductPermissionCodes.UserManage,
                     ProductPermissionCodes.PermissionManage,
                 })
        {
            Permissions.First(permission => string.Equals(
                permission.Code,
                code,
                StringComparison.Ordinal)).GrantGlobally();
        }
    }

    private static bool IsDefinedRole(ProductRemoteAccountRole role)
        => role is ProductRemoteAccountRole.Owner or ProductRemoteAccountRole.Admin or
            ProductRemoteAccountRole.Operator or ProductRemoteAccountRole.Viewer;

    private static string L(string key, params object?[] arguments)
        => LocalizationService.Current.Get(key, arguments);
}

/// <summary>
/// View model for the formal Service-owned remote-management window. Every mutation crosses the
/// administrator-only named pipe through <see cref="IProductRemoteManagementClient"/>. This type
/// deliberately has no dependency on RemoteAccessCoordinator, WpfRemoteControlBackend, Kestrel,
/// Tailscale, or any credential file in the GUI profile.
/// </summary>
internal sealed class ProductServiceRemoteAccessViewModel : ObservableObject, IDisposable
{
    private const int DefaultTailscaleRecoveryAttempts = 60;
    private static readonly TimeSpan DefaultTailscaleRecoveryPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultTailscaleRecoveryTimeout = TimeSpan.FromMinutes(2);
    private readonly IProductRemoteManagementClient _client;
    private readonly IReadOnlyList<ProductServiceRemoteServerOption> _servers;
    private readonly Func<string, bool> _confirmDestructiveAction;
    private readonly Action<string> _copyText;
    private readonly Action<string> _openUrl;
    private readonly Func<bool> _launchTailscale;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _tailscaleRecoveryPollInterval;
    private readonly TimeSpan _tailscaleRecoveryTimeout;
    private readonly int _tailscaleRecoveryAttempts;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _tailscaleRecoveryGate = new();
    private CancellationTokenSource? _tailscaleRecoveryCancellation;
    private Task _tailscaleRecoveryTask = Task.CompletedTask;
    private ProductRemoteAccessStatus? _remoteStatus;
    private ProductServiceRemoteAccountViewModel? _selectedAccount;
    private ProductRememberedDeviceSummary? _selectedDevice;
    private bool _isBusy;
    private bool _hasError;
    private string _statusMessage = L("remote.service.loading");
    private string _newUsername = string.Empty;
    private string _newEmail = string.Empty;
    private string _newPin = string.Empty;
    private string _confirmedNewPin = string.Empty;
    private bool _grantAllToNewAccount;
    private bool _isTailscaleRecoveryActive;
    private ProductRemoteAccountRole _newRole = ProductRemoteAccountRole.Viewer;
    private int _disposed;

    public ProductServiceRemoteAccessViewModel(
        IProductRemoteManagementClient client,
        IEnumerable<ProductServiceRemoteServerOption> servers,
        Func<string, bool>? confirmDestructiveAction = null,
        Action<string>? copyText = null,
        Action<string>? openUrl = null,
        Func<bool>? launchTailscale = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? tailscaleRecoveryPollInterval = null,
        TimeSpan? tailscaleRecoveryTimeout = null,
        int tailscaleRecoveryAttempts = DefaultTailscaleRecoveryAttempts)
    {
        if (tailscaleRecoveryAttempts is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tailscaleRecoveryAttempts),
                "Tailscale recovery attempts must be between 1 and 60.");
        }

        var recoveryPollInterval = tailscaleRecoveryPollInterval ?? DefaultTailscaleRecoveryPollInterval;
        if (recoveryPollInterval < TimeSpan.Zero || recoveryPollInterval > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tailscaleRecoveryPollInterval),
                "Tailscale recovery polling interval must be between zero and 30 seconds.");
        }

        var recoveryTimeout = tailscaleRecoveryTimeout ?? DefaultTailscaleRecoveryTimeout;
        if (recoveryTimeout <= TimeSpan.Zero || recoveryTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tailscaleRecoveryTimeout),
                "Tailscale recovery timeout must be greater than zero and no longer than five minutes.");
        }

        _client = client ?? throw new ArgumentNullException(nameof(client));
        _servers = (servers ?? throw new ArgumentNullException(nameof(servers)))
            .Where(server => server.Id != Guid.Empty)
            .GroupBy(server => server.Id)
            .Select(group => group.First())
            .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _confirmDestructiveAction = confirmDestructiveAction ?? (_ => true);
        _copyText = copyText ?? CopyToClipboard;
        _openUrl = openUrl ?? OpenBrowser;
        _launchTailscale = launchTailscale ?? TailscaleInteractiveLauncher.TryLaunch;
        _delayAsync = delayAsync ?? ((delay, token) => Task.Delay(delay, token));
        _tailscaleRecoveryPollInterval = recoveryPollInterval;
        _tailscaleRecoveryTimeout = recoveryTimeout;
        _tailscaleRecoveryAttempts = tailscaleRecoveryAttempts;
        LocalizationService.Current.CultureChanged += OnCultureChanged;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && RemoteStatus?.DesiredEnabled != true);
        StopCommand = new AsyncRelayCommand(StopAsync, () => !IsBusy && RemoteStatus?.DesiredEnabled == true);
        ReconnectCommand = new AsyncRelayCommand(
            ReconnectAsync,
            () => !IsBusy && !IsTailscaleRecoveryActive);
        CopyUrlCommand = new RelayCommand(CopyUrl, () => HasPublicUrl && !IsBusy);
        OpenUrlCommand = new RelayCommand(OpenUrl, () => HasPublicUrl && !IsBusy);
        CreateAccountCommand = new AsyncRelayCommand(CreateAccountAsync, CanCreateAccount);
        SaveAuthorizationCommand = new AsyncRelayCommand(SaveAuthorizationAsync, () => !IsBusy && SelectedAccount is not null);
        TogglePinVisibilityCommand = new AsyncRelayCommand(TogglePinVisibilityAsync, () => !IsBusy && SelectedAccount is not null);
        ResetPinCommand = new AsyncRelayCommand(ResetPinAsync, CanResetPin);
        DeleteAccountCommand = new AsyncRelayCommand(DeleteAccountAsync, () => !IsBusy && SelectedAccount is not null);
        GrantAllCommand = new RelayCommand(GrantAll, () => !IsBusy && SelectedAccount is not null);
        ClearPermissionsCommand = new RelayCommand(ClearPermissions, () => !IsBusy && SelectedAccount is not null);
        RevokeDeviceCommand = new AsyncRelayCommand(RevokeDeviceAsync, () => !IsBusy && SelectedDevice is not null);
    }

    public ObservableCollection<ProductServiceRemoteAccountViewModel> Accounts { get; } = [];
    public ObservableCollection<ProductRememberedDeviceSummary> Devices { get; } = [];
    public IReadOnlyList<ProductServiceRemoteServerOption> Servers => _servers;
    public IReadOnlyList<ProductServiceRemoteRoleOption> AvailableRoles =>
    [
        new(ProductRemoteAccountRole.Owner, L("remote.service.role.owner")),
        new(ProductRemoteAccountRole.Admin, L("remote.service.role.admin")),
        new(ProductRemoteAccountRole.Operator, L("remote.service.role.operator")),
        new(ProductRemoteAccountRole.Viewer, L("remote.service.role.viewer")),
    ];
    public ProductRemoteAccessStatus? RemoteStatus
    {
        get => _remoteStatus;
        private set
        {
            if (!SetProperty(ref _remoteStatus, value)) return;
            OnPropertyChanged(nameof(ConnectionStateText));
            OnPropertyChanged(nameof(PublicUrl));
            OnPropertyChanged(nameof(HasPublicUrl));
            OnPropertyChanged(nameof(DesiredStateText));
            OnPropertyChanged(nameof(HostStateText));
            OnPropertyChanged(nameof(FunnelStateText));
            OnPropertyChanged(nameof(LastUpdatedText));
            OnPropertyChanged(nameof(RetryText));
            OnPropertyChanged(nameof(LifecycleDiagnosticText));
            OnPropertyChanged(nameof(HasLifecycleDiagnostic));
            NotifyCommands();
        }
    }

    public string ConnectionStateText => RemoteStatus?.State switch
    {
        "disabled" => L("remote.service.state.disabled"),
        "waiting" => L("remote.service.state.waiting"),
        "unavailable" => L("remote.service.state.unavailable"),
        "blocked" => L("remote.service.state.blocked"),
        "retrying" => L("remote.service.state.retrying"),
        "running" => L("remote.service.state.running"),
        { Length: > 0 } state => state,
        _ => L("remote.service.unknown"),
    };
    public string PublicUrl => RemoteStatus?.PublicUrl ?? string.Empty;
    public bool HasPublicUrl => Uri.TryCreate(PublicUrl, UriKind.Absolute, out var uri)
                                && uri.Scheme == Uri.UriSchemeHttps;
    public string DesiredStateText => RemoteStatus?.DesiredEnabled == true
        ? L("remote.service.enabled")
        : L("remote.service.disabled");
    public string HostStateText => RemoteStatus?.HostRunning == true
        ? L("remote.service.hostRunning")
        : L("remote.service.hostStopped");
    public string FunnelStateText => RemoteStatus?.FunnelRunning == true
        ? L("remote.service.funnelConnected")
        : L("remote.service.funnelDisconnected");
    public string LastUpdatedText => RemoteStatus is null
        ? "—"
        : RemoteStatus.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string RetryText => RemoteStatus?.NextRetryAtUtc is { } retry
        ? L("remote.service.retryAt", retry.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
        : string.Empty;
    public string LifecycleDiagnosticText => FormatLifecycleDiagnostic(RemoteStatus);
    public bool HasLifecycleDiagnostic => LifecycleDiagnosticText.Length > 0;
    public string SelectedAccountNameText => SelectedAccount?.Username
                                             ?? L("remote.service.selectAccount");

    public ProductServiceRemoteAccountViewModel? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (ReferenceEquals(_selectedAccount, value)) return;
            if (_selectedAccount is not null)
            {
                _selectedAccount.PropertyChanged -= OnSelectedAccountPropertyChanged;
                _selectedAccount.HidePin();
            }
            if (!SetProperty(ref _selectedAccount, value)) return;
            if (_selectedAccount is not null)
            {
                _selectedAccount.PropertyChanged += OnSelectedAccountPropertyChanged;
            }
            OnPropertyChanged(nameof(SelectedAccountNameText));
            NotifyCommands();
        }
    }

    public ProductRememberedDeviceSummary? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value)) return;
            NotifyCommands();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            NotifyCommands();
        }
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public bool IsTailscaleRecoveryActive
    {
        get => _isTailscaleRecoveryActive;
        private set
        {
            if (!SetProperty(ref _isTailscaleRecoveryActive, value)) return;
            NotifyCommands();
        }
    }

    internal Task TailscaleRecoveryTask
    {
        get
        {
            lock (_tailscaleRecoveryGate)
            {
                return _tailscaleRecoveryTask;
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string NewUsername
    {
        get => _newUsername;
        set
        {
            if (!SetProperty(ref _newUsername, value ?? string.Empty)) return;
            CreateAccountCommand.NotifyCanExecuteChanged();
        }
    }

    public string NewEmail
    {
        get => _newEmail;
        set => SetProperty(ref _newEmail, value ?? string.Empty);
    }

    public string NewPin
    {
        get => _newPin;
        set
        {
            if (!SetProperty(ref _newPin, value ?? string.Empty)) return;
            CreateAccountCommand.NotifyCanExecuteChanged();
        }
    }

    public string ConfirmedNewPin
    {
        get => _confirmedNewPin;
        set
        {
            if (!SetProperty(ref _confirmedNewPin, value ?? string.Empty)) return;
            CreateAccountCommand.NotifyCanExecuteChanged();
        }
    }

    public bool GrantAllToNewAccount
    {
        get => _grantAllToNewAccount;
        set => SetProperty(ref _grantAllToNewAccount, value);
    }

    public ProductRemoteAccountRole NewRole
    {
        get => _newRole;
        set
        {
            if (value is not (ProductRemoteAccountRole.Owner or ProductRemoteAccountRole.Admin or
                ProductRemoteAccountRole.Operator or ProductRemoteAccountRole.Viewer)) return;
            SetProperty(ref _newRole, value);
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ReconnectCommand { get; }
    public RelayCommand CopyUrlCommand { get; }
    public RelayCommand OpenUrlCommand { get; }
    public AsyncRelayCommand CreateAccountCommand { get; }
    public AsyncRelayCommand SaveAuthorizationCommand { get; }
    public AsyncRelayCommand TogglePinVisibilityCommand { get; }
    public AsyncRelayCommand ResetPinCommand { get; }
    public AsyncRelayCommand DeleteAccountCommand { get; }
    public RelayCommand GrantAllCommand { get; }
    public RelayCommand ClearPermissionsCommand { get; }
    public AsyncRelayCommand RevokeDeviceCommand { get; }

    public Task InitializeAsync() => RefreshAsync();

    public void HideRevealedPins()
    {
        foreach (var account in Accounts)
        {
            account.HidePin();
        }
    }

    public void ClearRevealedSecrets()
    {
        foreach (var account in Accounts)
        {
            account.HidePin();
            account.ClearPinEditor();
        }

        NewPin = string.Empty;
        ConfirmedNewPin = string.Empty;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelTailscaleRecovery();
        if (_selectedAccount is not null)
        {
            _selectedAccount.PropertyChanged -= OnSelectedAccountPropertyChanged;
        }
        ClearRevealedSecrets();
        LocalizationService.Current.CultureChanged -= OnCultureChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task RefreshAsync()
    {
        await RunAsync(async cancellationToken =>
        {
            var selectedUsername = SelectedAccount?.Username;
            var selectedDeviceId = SelectedDevice?.DeviceId;
            var status = await _client.GetRemoteAccessStatusAsync(cancellationToken);
            var accounts = await _client.ListRemoteAccountsAsync(cancellationToken);
            var devices = await _client.ListRemoteDevicesAsync(cancellationToken);
            RemoteStatus = status;
            ReplaceAccounts(accounts, selectedUsername);
            ReplaceDevices(devices, selectedDeviceId);
            HasError = status.DesiredEnabled && !IsRemoteAccessReady(status);
            StatusMessage = HasError
                ? FormatLifecycleDiagnostic(status)
                : L("remote.service.refreshed");
        });
    }

    private Task StartAsync() => ChangeRuntimeAsync(
        token => _client.StartRemoteAccessAsync(token),
        "remote.service.started",
        requireReady: true);

    private Task StopAsync()
    {
        CancelTailscaleRecovery();
        return ChangeRuntimeAsync(
            token => _client.StopRemoteAccessAsync(token),
            "remote.service.stopped",
            requireReady: false);
    }

    private async Task ReconnectAsync()
    {
        CancelTailscaleRecovery();
        await RunAsync(async cancellationToken =>
        {
            var status = await _client.ReconnectRemoteAccessAsync(cancellationToken);
            RemoteStatus = status;
            if (IsRemoteAccessReady(status))
            {
                HasError = false;
                StatusMessage = L("remote.service.tailscale.connected", status.PublicUrl!);
                return;
            }

            HasError = true;
            var errorCode = GetLifecycleErrorCode(status);
            if (!RequiresTailscaleLogin(status))
            {
                var diagnostic = FormatLifecycleDiagnostic(status);
                StatusMessage = diagnostic.Length > 0
                    ? diagnostic
                    : L("remote.service.lifecycleFailure", errorCode);
                return;
            }

            if (!_launchTailscale())
            {
                StatusMessage = L("remote.service.tailscale.loginLaunchFailed", errorCode);
                return;
            }

            StatusMessage = L("remote.service.tailscale.loginOpening", errorCode);
            StartTailscaleRecovery();
        });
    }

    private void StartTailscaleRecovery()
    {
        CancellationTokenSource cancellation;
        lock (_tailscaleRecoveryGate)
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            cancellation.CancelAfter(_tailscaleRecoveryTimeout);
            _tailscaleRecoveryCancellation = cancellation;
        }

        var recoveryTask = RecoverTailscaleAsync(cancellation);
        lock (_tailscaleRecoveryGate)
        {
            _tailscaleRecoveryTask = ReferenceEquals(_tailscaleRecoveryCancellation, cancellation)
                ? recoveryTask
                : Task.CompletedTask;
        }
    }

    private async Task RecoverTailscaleAsync(CancellationTokenSource cancellation)
    {
        IsTailscaleRecoveryActive = true;
        ProductRemoteAccessStatus? latest = RemoteStatus;
        Exception? latestException = null;
        try
        {
            for (var attempt = 0; attempt < _tailscaleRecoveryAttempts; attempt++)
            {
                await _delayAsync(_tailscaleRecoveryPollInterval, cancellation.Token);

                try
                {
                    latest = await _client.GetRemoteAccessStatusAsync(cancellation.Token);
                    RemoteStatus = latest;
                    if (IsRemoteAccessReady(latest))
                    {
                        HasError = false;
                        StatusMessage = L("remote.service.tailscale.connected", latest.PublicUrl!);
                        return;
                    }

                    if (!CanContinueTailscaleRecovery(latest))
                    {
                        HasError = true;
                        StatusMessage = FormatLifecycleDiagnostic(latest);
                        return;
                    }

                    latest = await _client.ReconnectRemoteAccessAsync(cancellation.Token);
                    RemoteStatus = latest;
                    if (IsRemoteAccessReady(latest))
                    {
                        HasError = false;
                        StatusMessage = L("remote.service.tailscale.connected", latest.PublicUrl!);
                        return;
                    }

                    if (!CanContinueTailscaleRecovery(latest))
                    {
                        HasError = true;
                        StatusMessage = FormatLifecycleDiagnostic(latest);
                        return;
                    }

                    latestException = null;
                    StatusMessage = L(
                        "remote.service.tailscale.waiting",
                        GetLifecycleErrorCode(latest));
                }
                catch (ProductServiceClientException error) when (
                    error.Code is "service.timeout" or "service.connection_failed")
                {
                    latestException = error;
                }
            }

            HasError = true;
            var finalErrorCode = latestException is ProductServiceClientException service
                ? service.Code
                : GetLifecycleErrorCode(latest);
            StatusMessage = L("remote.service.tailscale.recoveryTimedOut", finalErrorCode);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Closing the dialog, stopping Web, or starting a newer recovery attempt owns the
            // cancellation. No stale recovery result may replace the newer UI state.
            if (!_lifetime.IsCancellationRequested && IsCurrentTailscaleRecovery(cancellation))
            {
                HasError = true;
                StatusMessage = L(
                    "remote.service.tailscale.recoveryTimedOut",
                    GetLifecycleErrorCode(latest));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            HasError = true;
            StatusMessage = FormatError(error);
        }
        finally
        {
            var isCurrent = false;
            lock (_tailscaleRecoveryGate)
            {
                if (ReferenceEquals(_tailscaleRecoveryCancellation, cancellation))
                {
                    _tailscaleRecoveryCancellation = null;
                    isCurrent = true;
                }
            }

            if (isCurrent)
            {
                IsTailscaleRecoveryActive = false;
            }

            cancellation.Dispose();
        }
    }

    private void CancelTailscaleRecovery()
    {
        CancellationTokenSource? cancellation;
        lock (_tailscaleRecoveryGate)
        {
            cancellation = _tailscaleRecoveryCancellation;
            _tailscaleRecoveryCancellation = null;
            _tailscaleRecoveryTask = Task.CompletedTask;
        }

        IsTailscaleRecoveryActive = false;
        cancellation?.Cancel();
    }

    private bool IsCurrentTailscaleRecovery(CancellationTokenSource cancellation)
    {
        lock (_tailscaleRecoveryGate)
        {
            return ReferenceEquals(_tailscaleRecoveryCancellation, cancellation);
        }
    }

    private async Task ChangeRuntimeAsync(
        Func<CancellationToken, Task<ProductRemoteAccessStatus>> operation,
        string successMessageKey,
        bool requireReady)
    {
        await RunAsync(async cancellationToken =>
        {
            var status = await operation(cancellationToken);
            RemoteStatus = status;
            if (requireReady && !IsRemoteAccessReady(status))
            {
                HasError = true;
                var diagnostic = FormatLifecycleDiagnostic(status);
                StatusMessage = diagnostic.Length > 0
                    ? diagnostic
                    : L("remote.service.lifecycleFailure", GetLifecycleErrorCode(status));
                return;
            }

            HasError = false;
            StatusMessage = L(successMessageKey);
        });
    }

    private async Task CreateAccountAsync()
    {
        await RunAsync(async cancellationToken =>
        {
            if (!RemoteCredentialRules.TryNormalizeUsername(NewUsername.Trim(), out var username))
            {
                throw new InvalidOperationException(L("remote.service.usernameInvalid"));
            }

            if (!RemoteCredentialRules.IsValidPin(NewPin)
                || !string.Equals(NewPin, ConfirmedNewPin, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(L("remote.service.pinMismatch"));
            }

            var grants = GrantAllToNewAccount
                ? ProductPermissionCatalog.All
                    .Select(descriptor => new ProductPermissionGrant(
                        descriptor.Code,
                        ProductPermissionScope.Global))
                    .ToArray()
                : [];
            var created = await _client.CreateRemoteAccountAsync(
                new ProductCreateRemoteAccountRequest(
                    username,
                    RemoteControlOptions.PublicTunnelCredentialSubject,
                    NormalizeOptionalEmail(NewEmail),
                    NewPin,
                    grants,
                    NewRole),
                cancellationToken);

            NewUsername = string.Empty;
            NewEmail = string.Empty;
            NewPin = string.Empty;
            ConfirmedNewPin = string.Empty;
            GrantAllToNewAccount = false;
            NewRole = ProductRemoteAccountRole.Viewer;
            await ReloadAccountsAsync(created.Username, cancellationToken);
            StatusMessage = L("remote.service.accountCreated", created.Username);
        });
    }

    private async Task SaveAuthorizationAsync()
    {
        await RunAsync(async cancellationToken =>
        {
            var account = SelectedAccount
                ?? throw new InvalidOperationException(L("remote.service.selectAccountError"));
            var updated = await _client.UpdateRemoteAccountAuthorizationAsync(
                account.Username,
                new ProductUpdateRemoteAccountAuthorizationRequest(
                    account.Enabled,
                    account.BuildGrants(),
                    account.Role),
                cancellationToken);
            await ReloadAccountsAsync(updated.Username, cancellationToken);
            StatusMessage = L("remote.service.authorizationSaved", updated.Username);
        });
    }

    private async Task TogglePinVisibilityAsync()
    {
        var account = SelectedAccount;
        if (account is null) return;
        if (account.IsPinRevealed)
        {
            account.HidePin();
            return;
        }

        await RunAsync(async cancellationToken =>
        {
            var response = await _client.RevealRemoteAccountPinAsync(
                account.Username,
                cancellationToken);
            if (!RemoteCredentialRules.IsValidPin(response.Pin))
            {
                throw new InvalidDataException(L("remote.service.invalidRevealedPin"));
            }

            if (ReferenceEquals(SelectedAccount, account))
            {
                account.SetRevealedPin(response.Pin);
            }
            StatusMessage = L("remote.service.pinRevealed");
        });
    }

    private async Task ResetPinAsync()
    {
        await RunAsync(async cancellationToken =>
        {
            var account = SelectedAccount
                ?? throw new InvalidOperationException(L("remote.service.selectAccountError"));
            if (!RemoteCredentialRules.IsValidPin(account.NewPin)
                || !string.Equals(account.NewPin, account.ConfirmedNewPin, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(L("remote.service.pinMismatch"));
            }

            var updated = await _client.UpdateRemoteAccountPinAsync(
                account.Username,
                new ProductUpdateRemoteAccountPinRequest(account.NewPin),
                cancellationToken);
            account.ClearPinEditor();
            account.HidePin();
            await ReloadAccountsAsync(updated.Username, cancellationToken);
            StatusMessage = L("remote.service.pinReset", updated.Username);
        });
    }

    private async Task DeleteAccountAsync()
    {
        var account = SelectedAccount;
        if (account is null
            || !_confirmDestructiveAction(
                L("remote.service.deleteAccountConfirm", account.Username)))
        {
            return;
        }

        await RunAsync(async cancellationToken =>
        {
            await _client.DeleteRemoteAccountAsync(account.Username, cancellationToken);
            account.HidePin();
            await ReloadAccountsAsync(null, cancellationToken);
            await ReloadDevicesAsync(null, cancellationToken);
            StatusMessage = L("remote.service.accountDeleted", account.Username);
        });
    }

    private void GrantAll()
    {
        SelectedAccount?.GrantAllGlobally();
        StatusMessage = L("remote.service.grantAllPending");
    }

    private void ClearPermissions()
    {
        SelectedAccount?.ClearAllGrants();
        StatusMessage = L("remote.service.clearAllPending");
    }

    private async Task RevokeDeviceAsync()
    {
        var device = SelectedDevice;
        if (device is null
            || !_confirmDestructiveAction(
                L("remote.service.revokeDeviceConfirm", device.Username, device.Label)))
        {
            return;
        }

        await RunAsync(async cancellationToken =>
        {
            await _client.RevokeRemoteDeviceAsync(device.DeviceId, cancellationToken);
            await ReloadDevicesAsync(null, cancellationToken);
            StatusMessage = L("remote.service.deviceRevoked");
        });
    }

    private void CopyUrl()
    {
        if (!HasPublicUrl) return;
        try
        {
            _copyText(PublicUrl);
            StatusMessage = L("remote.service.urlCopied");
            HasError = false;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            HasError = true;
            StatusMessage = L("remote.service.urlCopyFailed", error.Message);
        }
    }

    private void OpenUrl()
    {
        if (!HasPublicUrl) return;
        try
        {
            _openUrl(PublicUrl);
            StatusMessage = L("remote.service.urlOpened");
            HasError = false;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            HasError = true;
            StatusMessage = L("remote.service.urlOpenFailed", error.Message);
        }
    }

    private async Task ReloadAccountsAsync(string? selectedUsername, CancellationToken cancellationToken)
        => ReplaceAccounts(
            await _client.ListRemoteAccountsAsync(cancellationToken),
            selectedUsername);

    private async Task ReloadDevicesAsync(Guid? selectedDeviceId, CancellationToken cancellationToken)
        => ReplaceDevices(
            await _client.ListRemoteDevicesAsync(cancellationToken),
            selectedDeviceId);

    private void ReplaceAccounts(
        IReadOnlyList<ProductRemoteAccountSummary> accounts,
        string? selectedUsername)
    {
        SelectedAccount?.HidePin();
        Accounts.Clear();
        foreach (var account in accounts.OrderBy(account => account.Username, StringComparer.OrdinalIgnoreCase))
        {
            Accounts.Add(new ProductServiceRemoteAccountViewModel(account, _servers));
        }

        SelectedAccount = Accounts.FirstOrDefault(account => string.Equals(
                              account.Username,
                              selectedUsername,
                              StringComparison.OrdinalIgnoreCase))
                          ?? Accounts.FirstOrDefault();
    }

    private void ReplaceDevices(
        IReadOnlyList<ProductRememberedDeviceSummary> devices,
        Guid? selectedDeviceId)
    {
        Devices.Clear();
        foreach (var device in devices
                     .OrderByDescending(device => device.LastUsedAtUtc)
                     .ThenBy(device => device.Username, StringComparer.OrdinalIgnoreCase))
        {
            Devices.Add(device);
        }

        SelectedDevice = Devices.FirstOrDefault(device => device.DeviceId == selectedDeviceId)
                         ?? Devices.FirstOrDefault();
    }

    private bool CanCreateAccount()
        => !IsBusy
           && RemoteCredentialRules.TryNormalizeUsername(NewUsername.Trim(), out _)
           && RemoteCredentialRules.IsValidPin(NewPin)
           && string.Equals(NewPin, ConfirmedNewPin, StringComparison.Ordinal);

    private bool CanResetPin()
        => !IsBusy
           && SelectedAccount is { } account
           && RemoteCredentialRules.IsValidPin(account.NewPin)
           && string.Equals(account.NewPin, account.ConfirmedNewPin, StringComparison.Ordinal);

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsBusy) return;
        IsBusy = true;
        HasError = false;
        try
        {
            await operation(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the window cancels only this local IPC request. The Service-owned Web host
            // intentionally remains untouched and continues running.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            HasError = true;
            StatusMessage = FormatError(error);
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                IsBusy = false;
            }
        }
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ReconnectCommand.NotifyCanExecuteChanged();
        CopyUrlCommand.NotifyCanExecuteChanged();
        OpenUrlCommand.NotifyCanExecuteChanged();
        CreateAccountCommand.NotifyCanExecuteChanged();
        SaveAuthorizationCommand.NotifyCanExecuteChanged();
        TogglePinVisibilityCommand.NotifyCanExecuteChanged();
        ResetPinCommand.NotifyCanExecuteChanged();
        DeleteAccountCommand.NotifyCanExecuteChanged();
        GrantAllCommand.NotifyCanExecuteChanged();
        ClearPermissionsCommand.NotifyCanExecuteChanged();
        RevokeDeviceCommand.NotifyCanExecuteChanged();
    }

    private void OnSelectedAccountPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProductServiceRemoteAccountViewModel.NewPin)
            or nameof(ProductServiceRemoteAccountViewModel.ConfirmedNewPin))
        {
            ResetPinCommand.NotifyCanExecuteChanged();
        }
    }

    private static string? NormalizeOptionalEmail(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0) return null;
        if (normalized.Length > 254
            || normalized.Contains('\r', StringComparison.Ordinal)
            || normalized.Contains('\n', StringComparison.Ordinal)
            || normalized.Count(character => character == '@') != 1)
        {
            throw new InvalidOperationException(L("remote.service.emailInvalid"));
        }

        return normalized.ToLowerInvariant();
    }

    private static string FormatError(Exception error)
    {
        if (error is ProductServiceClientException service)
        {
            return service.Code switch
            {
                "service.access_denied" => L("remote.service.error.accessDenied"),
                "remote.account_not_found" => L("remote.service.error.accountNotFound"),
                "remote.device_not_found" => L("remote.service.error.deviceNotFound"),
                "service.timeout" or "service.connection_failed" =>
                    L("remote.service.error.unavailable"),
                _ => L("remote.service.error.rejected", service.Code),
            };
        }

        return error.Message;
    }

    private static bool IsRemoteAccessReady(ProductRemoteAccessStatus status)
        => status.DesiredEnabled &&
           status.HostRunning &&
           status.FunnelRunning &&
           Uri.TryCreate(status.PublicUrl, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttps;

    private static bool RequiresTailscaleLogin(ProductRemoteAccessStatus status)
        => status.ErrorCode is "tailscale.backend_not_running";

    private static bool CanContinueTailscaleRecovery(ProductRemoteAccessStatus status)
    {
        if (!status.DesiredEnabled)
        {
            return false;
        }

        return (status.ErrorCode is null or
                "tailscale.backend_not_running" or
                "tailscale.status_failed" or
                "tailscale.status_timeout" or
                "tailscale.funnel_status_failed" or
                "tailscale.funnel_status_timeout" or
                "tailscale.funnel_process_exited" or
                "tailscale.funnel_start_timeout" or
                "remote.start_timeout" or
                "remote.start_failed") ||
               status.State is "waiting" or "retrying";
    }

    private static string GetLifecycleErrorCode(ProductRemoteAccessStatus? status)
    {
        if (!string.IsNullOrWhiteSpace(status?.ErrorCode))
        {
            return status.ErrorCode!;
        }

        if (status is null)
        {
            return "remote.status_unavailable";
        }

        if (!status.DesiredEnabled)
        {
            return "remote.disabled";
        }

        return status.HostRunning && status.FunnelRunning
            ? "remote.public_url_missing"
            : "remote.lifecycle_not_ready";
    }

    private static string FormatLifecycleDiagnostic(ProductRemoteAccessStatus? status)
    {
        if (status is null || IsRemoteAccessReady(status) || !status.DesiredEnabled)
        {
            return string.Empty;
        }

        var errorCode = GetLifecycleErrorCode(status);
        return errorCode switch
        {
            "tailscale.backend_not_running" =>
                L("remote.service.tailscale.loginRequired", errorCode),
            "tailscale.not_installed" =>
                L("remote.service.tailscale.notInstalled", errorCode),
            "tailscale.status_schema_invalid" or "tailscale.status_payload_invalid" or
                "tailscale.status_json_invalid" =>
                L("remote.service.tailscale.statusInvalid", errorCode),
            "tailscale.https_not_enabled" =>
                L("remote.service.tailscale.httpsRequired", errorCode),
            "tailscale.hostname_unavailable" =>
                L("remote.service.tailscale.hostnameUnavailable", errorCode),
            "tailscale.hostname_set_failed" or "tailscale.hostname_set_timeout" =>
                L("remote.service.tailscale.hostnameSetFailed", errorCode),
            "tailscale.hostname_status_failed" or "tailscale.hostname_status_timeout" or
                "tailscale.hostname_status_invalid" or "tailscale.hostname_verification_failed" =>
                L("remote.service.tailscale.hostnameStatusFailed", errorCode),
            "tailscale.funnel_route_conflict" or "tailscale.precondition_changed" =>
                L("remote.service.tailscale.routeConflict", errorCode),
            _ => L("remote.service.lifecycleFailure", errorCode),
        };
    }

    private static void CopyToClipboard(string value) => Clipboard.SetText(value);

    private static void OpenBrowser(string value)
        => Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (var account in Accounts)
        {
            account.RefreshLocalization();
        }

        OnPropertyChanged(nameof(ConnectionStateText));
        OnPropertyChanged(nameof(DesiredStateText));
        OnPropertyChanged(nameof(HostStateText));
        OnPropertyChanged(nameof(FunnelStateText));
        OnPropertyChanged(nameof(RetryText));
        OnPropertyChanged(nameof(LifecycleDiagnosticText));
        OnPropertyChanged(nameof(HasLifecycleDiagnostic));
        OnPropertyChanged(nameof(SelectedAccountNameText));
        OnPropertyChanged(nameof(AvailableRoles));
        StatusMessage = IsTailscaleRecoveryActive
            ? L("remote.service.tailscale.waiting", GetLifecycleErrorCode(RemoteStatus))
            : HasLifecycleDiagnostic
                ? LifecycleDiagnosticText
                : L("remote.service.refreshed");
    }

    private static string L(string key, params object?[] arguments)
        => LocalizationService.Current.Get(key, arguments);
}
