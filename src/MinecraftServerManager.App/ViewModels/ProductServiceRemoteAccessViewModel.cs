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
    private readonly IProductRemoteManagementClient _client;
    private readonly IReadOnlyList<ProductServiceRemoteServerOption> _servers;
    private readonly Func<string, bool> _confirmDestructiveAction;
    private readonly Action<string> _copyText;
    private readonly Action<string> _openUrl;
    private readonly CancellationTokenSource _lifetime = new();
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
    private ProductRemoteAccountRole _newRole = ProductRemoteAccountRole.Viewer;
    private int _disposed;

    public ProductServiceRemoteAccessViewModel(
        IProductRemoteManagementClient client,
        IEnumerable<ProductServiceRemoteServerOption> servers,
        Func<string, bool>? confirmDestructiveAction = null,
        Action<string>? copyText = null,
        Action<string>? openUrl = null)
    {
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
        LocalizationService.Current.CultureChanged += OnCultureChanged;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && RemoteStatus?.DesiredEnabled != true);
        StopCommand = new AsyncRelayCommand(StopAsync, () => !IsBusy && RemoteStatus?.DesiredEnabled == true);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync, () => !IsBusy);
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
            NotifyCommands();
        }
    }

    public string ConnectionStateText => RemoteStatus?.State ?? L("remote.service.unknown");
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
            StatusMessage = L("remote.service.refreshed");
        });
    }

    private Task StartAsync() => ChangeRuntimeAsync(
        token => _client.StartRemoteAccessAsync(token),
        "remote.service.started");

    private Task StopAsync() => ChangeRuntimeAsync(
        token => _client.StopRemoteAccessAsync(token),
        "remote.service.stopped");

    private Task ReconnectAsync() => ChangeRuntimeAsync(
        token => _client.ReconnectRemoteAccessAsync(token),
        "remote.service.reconnected");

    private async Task ChangeRuntimeAsync(
        Func<CancellationToken, Task<ProductRemoteAccessStatus>> operation,
        string successMessageKey)
    {
        await RunAsync(async cancellationToken =>
        {
            RemoteStatus = await operation(cancellationToken);
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
        OnPropertyChanged(nameof(SelectedAccountNameText));
        OnPropertyChanged(nameof(AvailableRoles));
        StatusMessage = L("remote.service.refreshed");
    }

    private static string L(string key, params object?[] arguments)
        => LocalizationService.Current.Get(key, arguments);
}
