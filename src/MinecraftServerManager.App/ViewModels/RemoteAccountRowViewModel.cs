using System.Windows;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.App.ViewModels;

/// <summary>
/// Editable desktop-only projection of one stored remote account. A recoverable PIN is fetched
/// from the DPAPI vault only on an explicit eye-button click and is cleared again when hidden.
/// </summary>
internal sealed class RemoteAccountRowViewModel : ObservableObject
{
    private readonly Func<string?>? _revealPin;
    private bool _isExpanded;
    private bool _isPinRevealed;
    private string _revealedPin = string.Empty;
    private bool _allowStartServer;
    private bool _allowStopServer;
    private bool _allowRestartServer;
    private bool _allowSendConsoleCommand;
    private bool _allowManagePlayers;
    private bool _allowCreateBackup;
    private string _newPin = string.Empty;
    private string _confirmedNewPin = string.Empty;

    public RemoteAccountRowViewModel(
        RemoteApprovedAccount account,
        bool showTailscaleIdentity,
        Func<string?>? revealPin = null)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));
        ShowTailscaleIdentity = showTailscaleIdentity;
        _revealPin = revealPin;
        ToggleSettingsCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        TogglePinVisibilityCommand = new RelayCommand(
            TogglePinVisibility,
            () => Account.HasRecoverablePin && _revealPin is not null);
        _allowStartServer = account.Permissions.HasFlag(RemoteWebPermission.StartServer);
        _allowStopServer = account.Permissions.HasFlag(RemoteWebPermission.StopServer);
        _allowRestartServer = account.Permissions.HasFlag(RemoteWebPermission.RestartServer);
        _allowSendConsoleCommand = account.Permissions.HasFlag(RemoteWebPermission.SendConsoleCommand);
        _allowManagePlayers = account.Permissions.HasFlag(RemoteWebPermission.ManagePlayers);
        _allowCreateBackup = account.Permissions.HasFlag(RemoteWebPermission.CreateBackup);
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);
    }

    internal event EventHandler? EditorStateChanged;

    internal RemoteApprovedAccount Account { get; }
    internal bool ShowTailscaleIdentity { get; }
    public string Username => Account.Username;
    public string IdentityText => !ShowTailscaleIdentity
        ? L("remote.legacy.account.local")
        : Account.Gmail is null
            ? L("remote.legacy.account.local")
            : L(
                "remote.legacy.account.verifiedIdentity",
                Account.Gmail,
                Account.EmailVerifiedAtUtc!.Value.ToLocalTime());
    public string PinDisplayText => IsPinRevealed
        ? L("remote.legacy.account.pinRevealed", _revealedPin)
        : L("remote.legacy.account.pinHidden");
    public string PinVisibilityToolTip => Account.HasRecoverablePin
        ? IsPinRevealed
            ? L("remote.service.hidePin")
            : L("remote.service.showPin")
        : L("remote.legacy.account.pinUnavailable");
    public bool IsPinRevealEnabled => Account.HasRecoverablePin && _revealPin is not null;
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand TogglePinVisibilityCommand { get; }

    public bool IsPinRevealed
    {
        get => _isPinRevealed;
        private set
        {
            if (!SetProperty(ref _isPinRevealed, value)) return;
            OnPropertyChanged(nameof(PinDisplayText));
            OnPropertyChanged(nameof(PinVisibilityToolTip));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetEditorProperty(ref _isExpanded, value);
    }

    public bool AllowStartServer
    {
        get => _allowStartServer;
        set => SetEditorProperty(ref _allowStartServer, value);
    }

    public bool AllowStopServer
    {
        get => _allowStopServer;
        set => SetEditorProperty(ref _allowStopServer, value);
    }

    public bool AllowRestartServer
    {
        get => _allowRestartServer;
        set => SetEditorProperty(ref _allowRestartServer, value);
    }

    public bool AllowSendConsoleCommand
    {
        get => _allowSendConsoleCommand;
        set => SetEditorProperty(ref _allowSendConsoleCommand, value);
    }

    public bool AllowManagePlayers
    {
        get => _allowManagePlayers;
        set => SetEditorProperty(ref _allowManagePlayers, value);
    }

    public bool AllowCreateBackup
    {
        get => _allowCreateBackup;
        set => SetEditorProperty(ref _allowCreateBackup, value);
    }

    public string NewPin
    {
        get => _newPin;
        set => SetEditorProperty(ref _newPin, value ?? string.Empty);
    }

    public string ConfirmedNewPin
    {
        get => _confirmedNewPin;
        set => SetEditorProperty(ref _confirmedNewPin, value ?? string.Empty);
    }

    public bool CanResetPin => RemoteCredentialRules.IsValidPin(NewPin)
                               && string.Equals(NewPin, ConfirmedNewPin, StringComparison.Ordinal);

    public RemoteWebPermission SelectedPermissions
    {
        get
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
    }

    internal void ClearResetPin()
    {
        NewPin = string.Empty;
        ConfirmedNewPin = string.Empty;
    }

    internal void HideRevealedPin()
    {
        _revealedPin = string.Empty;
        IsPinRevealed = false;
        OnPropertyChanged(nameof(PinDisplayText));
    }

    private void TogglePinVisibility()
    {
        if (IsPinRevealed)
        {
            HideRevealedPin();
            return;
        }

        string? pin;
        try
        {
            pin = _revealPin?.Invoke();
        }
        catch (Exception exception) when (
            exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            HideRevealedPin();
            return;
        }

        if (!RemoteCredentialRules.IsValidPin(pin))
        {
            HideRevealedPin();
            return;
        }

        _revealedPin = pin!;
        IsPinRevealed = true;
        OnPropertyChanged(nameof(PinDisplayText));
    }

    private void SetEditorProperty<T>(ref T storage, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref storage, value, propertyName)) return;
        if (propertyName is nameof(NewPin) or nameof(ConfirmedNewPin))
        {
            OnPropertyChanged(nameof(CanResetPin));
        }

        EditorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IdentityText));
        OnPropertyChanged(nameof(PinDisplayText));
        OnPropertyChanged(nameof(PinVisibilityToolTip));
    }

    private static string L(string key, params object?[] arguments)
        => LocalizationService.Current.Get(key, arguments);
}
