using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Remote;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.App.Services;

internal sealed record RemoteAccessRuntimeState(
    bool IsStarting,
    bool IsRunning,
    bool IsTailscaleInstalled,
    bool IsTailscaleConnected,
    Uri? PublicUrl,
    string StatusMessage,
    string? Error)
{
    public bool RequiresTailscaleHttpsCertificateEnablement { get; init; }
    public bool AutoRetryRecommended { get; init; }
    public RemoteAccessMode AccessMode { get; init; } = RemoteAccessMode.Tailscale;

    public static RemoteAccessRuntimeState Stopped { get; } = new(
        false,
        false,
        false,
        false,
        null,
        "手機遠端控制目前未啟用。",
        null);
}

/// <summary>
/// Coordinates a loopback-only Kestrel host with one owned foreground ingress process.
/// Kestrel is always bound before the public/private route is created, and its port is released
/// only after route removal has been positively confirmed.
/// </summary>
internal sealed class RemoteAccessCoordinator : IAsyncDisposable
{
    private static readonly object FailClosedKeeperGate = new();
    private static readonly List<RemoteControlHost> FailClosedHosts = [];

    private readonly IRemoteControlBackend _backend;
    private readonly ITailscaleServeService _tailscale;
    private readonly ITailscaleServeService _funnel;
    private readonly IRemoteSecurityStore _securityStore;
    private readonly RemoteAccountProvisioningService _provisioning;
    private readonly CancellationToken _applicationStopping;
    private readonly Func<string, IWebTunnelService> _quickTunnelFactory;
    private readonly Func<string, Uri, string, IWebTunnelService> _namedTunnelFactory;
    private readonly Func<
        string,
        string,
        CloudflaredInstallationReceipt,
        CancellationToken,
        Task<CloudflaredExecutableVerificationLease>> _namedTunnelExecutableVerifier;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _hostGate = new();
    private readonly object _quickTunnelStateGate = new();
    private readonly object _runtimeStateGate = new();
    private RemoteControlHost? _activeHost;
    private RemoteControlHost? _boundHost;
    private int? _boundPort;
    private RemoteAccessMode? _boundAccessMode;
    private IWebTunnelService? _quickTunnel;
    private IWebTunnelService? _faultArmedQuickTunnel;
    private WebTunnelSnapshot? _lastWebTunnelSnapshot;
    private RemoteAccessRuntimeState _state = RemoteAccessRuntimeState.Stopped;
    private bool _disposed;

    public RemoteAccessCoordinator(
        IRemoteControlBackend backend,
        ITailscaleServeService? tailscale = null,
        IRemoteSecurityStore? securityStore = null,
        IVerificationEmailSender? verificationEmailSender = null,
        CancellationToken applicationStopping = default,
        Func<string, IWebTunnelService>? quickTunnelFactory = null,
        Func<string, Uri, string, IWebTunnelService>? namedTunnelFactory = null,
        ITailscaleServeService? funnel = null,
        Func<
            string,
            string,
            CloudflaredInstallationReceipt,
            CancellationToken,
            Task<CloudflaredExecutableVerificationLease>>? namedTunnelExecutableVerifier = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _tailscale = tailscale ?? new TailscaleServeService();
        _funnel = funnel ?? new TailscaleFunnelService();
        _securityStore = securityStore ?? new EphemeralRemoteSecurityStore();
        _provisioning = new RemoteAccountProvisioningService(
            _securityStore,
            verificationEmailSender);
        _applicationStopping = applicationStopping;
        _quickTunnelFactory = quickTunnelFactory ?? (path => new CloudflareQuickTunnelService(path));
        _namedTunnelFactory = namedTunnelFactory
            ?? ((path, publicOrigin, token) =>
                new CloudflareNamedTunnelService(path, publicOrigin, token));
        _namedTunnelExecutableVerifier = namedTunnelExecutableVerifier
            ?? CloudflaredNamedTunnelExecutableVerifier.VerifyAsync;
        _tailscale.ForegroundProcessExited += OnTailscaleServeProcessExited;
        _funnel.ForegroundProcessExited += OnTailscaleFunnelProcessExited;
    }

    public event EventHandler<RemoteAccessRuntimeState>? StateChanged;
    public event EventHandler? ApprovedAccountChanged;
    public event EventHandler<WebTunnelSnapshot>? WebTunnelStateChanged;

    public RemoteAccessRuntimeState State => Volatile.Read(ref _state);
    public bool IsSecurityStoreAvailable => _securityStore.IsAvailable;
    public string? SecurityStoreError => _securityStore.AvailabilityError;
    public bool HasCloudflareNamedTunnelToken =>
        _securityStore.HasCloudflareNamedTunnelToken;
    internal bool HasCloudflaredInstallationReceipt =>
        _securityStore.HasCloudflaredInstallationReceipt;
    public string? SmtpSenderGmail => _securityStore.SmtpSenderGmail;
    public RemoteApprovedAccount? ApprovedAccount => _securityStore.ApprovedAccount;
    public IReadOnlyList<RemoteApprovedAccount> ApprovedAccounts => _securityStore.ApprovedAccounts;
    internal string? GetRecoverableApprovedAccountPin(string username)
        => _securityStore.GetRecoverablePin(username);

    public void SaveCloudflareNamedTunnelToken(string token)
        => _securityStore.SaveCloudflareNamedTunnelToken(token);

    public void DeleteCloudflareNamedTunnelToken()
        => _securityStore.DeleteCloudflareNamedTunnelToken();

    internal void SaveCloudflaredInstallationReceipt(CloudflaredBootstrapResult result)
        => _securityStore.SaveCloudflaredInstallationReceipt(
            CloudflaredInstallationReceipt.Create(result, DateTimeOffset.UtcNow));
    public WebTunnelSnapshot? WebTunnelSnapshot
    {
        get
        {
            var tunnel = Volatile.Read(ref _quickTunnel);
            if (tunnel is null)
            {
                return Volatile.Read(ref _lastWebTunnelSnapshot);
            }

            try
            {
                var snapshot = tunnel.Snapshot;
                RememberWebTunnelSnapshot(snapshot);
                return snapshot;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A disposed or failing adapter must not erase the last bounded diagnostic
                // snapshot that the desktop console can still present to the user.
                return Volatile.Read(ref _lastWebTunnelSnapshot);
            }
        }
    }
    internal bool IsRemoteHostActive
    {
        get
        {
            lock (_hostGate)
            {
                return _activeHost is not null;
            }
        }
    }

    public async Task<RemoteAccessRuntimeState> StartAsync(
        RemoteControlSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateSettings(settings);
            var previousRuntime = Volatile.Read(ref _state);
            var continueAutomaticRouteRecovery =
                previousRuntime.AutoRetryRecommended
                && previousRuntime.AccessMode == settings.AccessMode;
            if (!_securityStore.IsAvailable)
            {
                return Publish(new RemoteAccessRuntimeState(
                    false,
                    false,
                    false,
                    false,
                    null,
                    "手機遠端控制尚未啟用。",
                    _securityStore.AvailabilityError)
                {
                    AccessMode = settings.AccessMode
                });
            }
            Publish(new RemoteAccessRuntimeState(
                true,
                false,
                false,
                false,
                null,
                settings.AccessMode switch
                {
                    RemoteAccessMode.CloudflareQuickTunnel =>
                        "正在啟動內建 Web 與 Cloudflare 隨機 HTTPS 網址…",
                    RemoteAccessMode.CloudflareNamedTunnel =>
                        "正在啟動內建 Web 與 Cloudflare 固定網域 Tunnel…",
                    RemoteAccessMode.TailscaleFunnel =>
                        "正在啟動內建 Web 與 Tailscale Funnel 固定公開網址…",
                    _ => "正在檢查 Tailscale 與建立本機 HTTPS 代理…"
                },
                null)
            {
                AccessMode = settings.AccessMode
            });

            // Reconfiguration uses the same fail-closed shutdown order as an explicit stop:
            // revoke access, remove the route, then release the old listener.
            var previousCleanup = await QuiesceAndRemoveBoundHostAsync(
                    disableOwnedServe: true)
                .ConfigureAwait(false);
            if (!previousCleanup.Succeeded)
            {
                return PublishShutdownFailure(
                    previousCleanup,
                    "無法先安全移除既有手機遠端通道，因此未套用新設定。",
                    continueAutomaticRouteRecovery
                    && IsTransientTailscaleCleanupFailure(previousCleanup, settings.AccessMode));
            }

            if (settings.AccessMode == RemoteAccessMode.CloudflareQuickTunnel)
            {
                return await StartQuickTunnelAsync(settings, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (settings.AccessMode == RemoteAccessMode.CloudflareNamedTunnel)
            {
                return await StartNamedTunnelAsync(settings, cancellationToken)
                    .ConfigureAwait(false);
            }

            var tailscaleRoute = settings.AccessMode == RemoteAccessMode.TailscaleFunnel
                ? _funnel
                : _tailscale;
            return await StartTailscaleRouteAsync(
                    settings,
                    tailscaleRoute,
                    settings.AccessMode,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cleanup = await QuiesceAndRemoveBoundHostAsync(disableOwnedServe: true)
                .ConfigureAwait(false);
            var detail = cleanup.Succeeded
                ? "操作已取消。"
                : $"操作已取消；{cleanup.Error}";
            return Publish(new RemoteAccessRuntimeState(
                false,
                false,
                cleanup.Status?.IsInstalled ?? _state.IsTailscaleInstalled,
                cleanup.Status?.IsBackendRunning ?? _state.IsTailscaleConnected,
                null,
                "啟用已取消。",
                SanitizeError(detail))
            {
                AccessMode = settings.AccessMode
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var cleanup = await QuiesceAndRemoveBoundHostAsync(disableOwnedServe: true)
                .ConfigureAwait(false);
            var detail = cleanup.Succeeded
                ? exception.Message
                : $"{exception.Message}；{cleanup.Error}";
            return Publish(new RemoteAccessRuntimeState(
                false,
                false,
                cleanup.Status?.IsInstalled ?? _state.IsTailscaleInstalled,
                cleanup.Status?.IsBackendRunning ?? _state.IsTailscaleConnected,
                null,
                "手機遠端控制啟用失敗。",
                SanitizeError(detail))
            {
                AccessMode = settings.AccessMode
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public void RevokeAllSessions()
    {
        lock (_hostGate)
        {
            _activeHost?.RevokeAllSessions();
        }
    }

    public IReadOnlyList<RemoteRememberedDeviceInfo> RememberedDevices
        => _securityStore.GetRememberedDevices();

    public bool RevokeRememberedDevice(Guid deviceId)
    {
        // Close both sides of the race: no short session may remain usable while a
        // persistent device authorization is being revoked on disk.
        RevokeAllSessions();
        var revoked = _securityStore.RevokeRememberedDevice(deviceId);
        RevokeAllSessions();
        return revoked;
    }

    public int RevokeAllRememberedDevicesAndSessions()
    {
        RevokeAllSessions();
        var revoked = _securityStore.RevokeAllRememberedDevices();
        RevokeAllSessions();
        return revoked;
    }

    public void SaveSmtpCredential(string senderGmail, string appPassword)
        => _provisioning.SaveSmtpCredential(senderGmail, appPassword);

    public void DeleteSmtpCredential()
        => _provisioning.DeleteSmtpCredential();

    public Task<VerificationCodeDispatchResult> SendVerificationCodeAsync(
        string recipientGmail,
        CancellationToken cancellationToken = default)
        => _provisioning.SendVerificationCodeAsync(recipientGmail, cancellationToken);

    public DateTimeOffset VerifyRegistrationCode(string recipientGmail, string code)
        => _provisioning.VerifyCode(recipientGmail, code);

    public void ResetTransientEmailVerification()
        => _provisioning.ResetTransientVerification();

    public async Task RegisterApprovedAccountAsync(
        string verifiedGmail,
        string username,
        string pin,
        string confirmedPin,
        CancellationToken cancellationToken = default)
        => await RegisterApprovedAccountAsync(
                verifiedGmail,
                username,
                pin,
                confirmedPin,
                RemoteWebPermission.All,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task RegisterApprovedAccountAsync(
        string verifiedGmail,
        string username,
        string pin,
        string confirmedPin,
        RemoteWebPermission permissions,
        CancellationToken cancellationToken = default)
    {
        await _provisioning.RegisterAccountAsync(
                verifiedGmail,
                username,
                pin,
                confirmedPin,
                permissions,
                cancellationToken)
            .ConfigureAwait(false);
        RevokeAllSessions();
        ApprovedAccountChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RegisterLocalApprovedAccountAsync(
        string username,
        string pin,
        string confirmedPin,
        RemoteWebPermission permissions,
        CancellationToken cancellationToken = default)
    {
        await _provisioning.RegisterLocalAccountAsync(
                username,
                pin,
                confirmedPin,
                permissions,
                cancellationToken)
            .ConfigureAwait(false);
        RevokeAllSessions();
        ApprovedAccountChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateApprovedAccountPermissions(
        string username,
        RemoteWebPermission permissions)
    {
        RevokeAllSessions();
        _securityStore.UpdateAccountPermissions(username, permissions);
        // Permission changes take effect for already signed-in phones immediately;
        // no prior session may retain a broader authorization snapshot.
        RevokeAllSessions();
        ApprovedAccountChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateApprovedAccountPermissions(RemoteWebPermission permissions)
        => UpdateApprovedAccountPermissions(
            _securityStore.ApprovedAccount?.Username
            ?? throw new InvalidOperationException("尚未建立遠端帳號。"),
            permissions);

    public async Task ResetApprovedAccountPinAsync(
        string username,
        string newPin,
        string confirmedPin,
        CancellationToken cancellationToken = default)
    {
        RevokeAllSessions();
        await _provisioning.ResetAccountPinAsync(
                username,
                newPin,
                confirmedPin,
                cancellationToken)
            .ConfigureAwait(false);
        RevokeAllSessions();
        ApprovedAccountChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteApprovedAccount(string username)
    {
        RevokeAllSessions();
        _securityStore.DeleteAccount(username);
        _provisioning.ResetTransientVerification();
        RevokeAllSessions();
        ApprovedAccountChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteApprovedAccount()
        => DeleteApprovedAccount(
            _securityStore.ApprovedAccount?.Username
            ?? throw new InvalidOperationException("尚未建立遠端帳號。"));

    public async Task<RemoteAccessRuntimeState> StopAsync(
        bool disableOwnedServe,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var accessMode = _boundAccessMode ?? _state.AccessMode;
            var cleanup = await QuiesceAndRemoveBoundHostAsync(disableOwnedServe)
                .ConfigureAwait(false);
            if (!cleanup.Succeeded)
            {
                return PublishShutdownFailure(
                    cleanup,
                    "手機工作階段已撤銷，但遠端通道尚未確認移除；本機防護連接埠會繼續保留。");
            }

            return Publish(new RemoteAccessRuntimeState(
                false,
                false,
                cleanup.Status?.IsInstalled ?? _state.IsTailscaleInstalled,
                cleanup.Status?.IsBackendRunning ?? _state.IsTailscaleConnected,
                null,
                "手機遠端控制已停止，所有已登入的工作階段都已撤銷。",
                null)
            {
                AccessMode = accessMode
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            _tailscale.ForegroundProcessExited -= OnTailscaleServeProcessExited;
            _funnel.ForegroundProcessExited -= OnTailscaleFunnelProcessExited;

            HostShutdownResult cleanup;
            try
            {
                cleanup = await QuiesceAndRemoveBoundHostAsync(disableOwnedServe: true)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                cleanup = new HostShutdownResult(false, null, exception.Message);
            }

            try
            {
                await _tailscale.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (!ReferenceEquals(_funnel, _tailscale))
                    {
                        await _funnel.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    _provisioning.Dispose();
                    if (!cleanup.Succeeded && _boundHost is { } guardedHost)
                    {
                        // At application shutdown, retain the loopback listener for the remaining
                        // process lifetime whenever route removal cannot be proven. This prevents a
                        // stale route from being redirected to a different local process.
                        lock (FailClosedKeeperGate)
                        {
                            FailClosedHosts.Add(guardedHost);
                        }
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<RemoteAccessRuntimeState> StartTailscaleRouteAsync(
        RemoteControlSettings settings,
        ITailscaleServeService routeService,
        RemoteAccessMode accessMode,
        CancellationToken cancellationToken)
    {
        var isFunnel = accessMode == RemoteAccessMode.TailscaleFunnel;
        var status = await routeService.GetStatusAsync(settings.LocalPort, cancellationToken)
            .ConfigureAwait(false);
        if (!TryGetCleanCandidate(status, accessMode, out var candidateUrl, out var readinessError))
        {
            return PublishFailure(status, readinessError, accessMode);
        }

        var host = await RemoteControlHost.StartAsync(
                _backend,
                new RemoteControlOptions
                {
                    Port = settings.LocalPort,
                    PublicOrigin = candidateUrl,
                    AllowedGoogleLogins = isFunnel
                        ? []
                        : [settings.AllowedLogin.Trim()],
                    IngressMode = isFunnel
                        ? RemoteIngressMode.TailscaleFunnel
                        : RemoteIngressMode.TailscaleServe,
                    OperationCancellationToken = _applicationStopping
                },
                _securityStore,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _boundHost = host;
        _boundPort = settings.LocalPort;
        _boundAccessMode = accessMode;

        // Re-check after binding. EnableAsync performs its own atomic ownership checks too;
        // this catches a concurrent Serve/Funnel configuration before the loopback host is public.
        var afterBind = await routeService.GetStatusAsync(settings.LocalPort, cancellationToken)
            .ConfigureAwait(false);
        if (!TryGetCleanCandidate(afterBind, accessMode, out var confirmedUrl, out readinessError)
            || !SameOrigin(candidateUrl, confirmedUrl))
        {
            return await AbortStartAsync(
                    afterBind,
                    readinessError ?? "Tailscale 網址在啟用期間發生變更，已拒絕公開本機服務。",
                    accessMode)
                .ConfigureAwait(false);
        }

        var enable = await routeService.EnableAsync(settings.LocalPort, cancellationToken)
            .ConfigureAwait(false);
        if (!IsConfirmedOwnedRoute(enable, candidateUrl))
        {
            var fallback = isFunnel
                ? "無法確認本程式持有的 Tailscale Funnel 公開通道；首次使用時請先完成 Tailscale 官方核准。"
                : "無法確認本程式持有的 Tailscale Serve 私有通道。";
            return await AbortStartAsync(
                    enable.Status,
                    enable.Error ?? fallback,
                    accessMode)
                .ConfigureAwait(false);
        }

        lock (_hostGate)
        {
            _activeHost = host;
        }

        string statusMessage;
        if (isFunnel)
        {
            var hasLocalAccount = _securityStore.ApprovedAccounts.Any(account => account.Gmail is null);
            statusMessage = hasLocalAccount
                ? "Web 已透過 Tailscale Funnel 固定公開網址啟用；本機帳號可直接從手機登入。"
                : "Web 已透過 Tailscale Funnel 固定公開網址啟用；請先在電腦端建立本機遠端帳號。";
        }
        else
        {
            statusMessage = !_securityStore.HasCredentialForLogin(settings.AllowedLogin.Trim())
                ? "手機遠端控制已啟用；請先在電腦端驗證 Gmail 並建立遠端帳號。"
                : "手機遠端控制已啟用；已核准帳號可從手機網站登入。";
        }

        return Publish(new RemoteAccessRuntimeState(
            false,
            true,
            enable.Status.IsInstalled,
            enable.Status.IsBackendRunning,
            enable.Status.Url,
            statusMessage,
            null)
        {
            AccessMode = accessMode
        });
    }

    private async Task<RemoteAccessRuntimeState> StartQuickTunnelAsync(
        RemoteControlSettings settings,
        CancellationToken cancellationToken)
    {
        var executablePath = settings.CloudflaredExecutablePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return Publish(new RemoteAccessRuntimeState(
                false, false, false, false, null,
                "Cloudflare 隨機網址尚未啟用。",
                "請先選擇從 Cloudflare 官方下載的 cloudflared.exe。")
            {
                AccessMode = RemoteAccessMode.CloudflareQuickTunnel
            });
        }

        IWebTunnelService tunnel;
        try
        {
            tunnel = _quickTunnelFactory(executablePath);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Publish(new RemoteAccessRuntimeState(
                false, false, false, false, null,
                "Cloudflare 隨機網址尚未啟用。",
                SanitizeError(exception.Message))
            {
                AccessMode = RemoteAccessMode.CloudflareQuickTunnel
            });
        }

        return await StartCloudflareTunnelAsync(
                settings,
                tunnel,
                RemoteAccessMode.CloudflareQuickTunnel,
                expectedPublicOrigin: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<RemoteAccessRuntimeState> StartNamedTunnelAsync(
        RemoteControlSettings settings,
        CancellationToken cancellationToken)
    {
        var executablePath = settings.CloudflaredExecutablePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return PublishCloudflareTunnelFailure(
                RemoteAccessMode.CloudflareNamedTunnel,
                "請先選擇從 Cloudflare 官方下載的 cloudflared.exe。",
                autoRetryRecommended: false);
        }

        if (!CloudflareNamedTunnelConfiguration.TryNormalizePublicOrigin(
                settings.CloudflareNamedPublicOrigin,
                out var publicOrigin))
        {
            return PublishCloudflareTunnelFailure(
                RemoteAccessMode.CloudflareNamedTunnel,
                "請輸入有效的固定 HTTPS 網址，例如 https://mc.example.com。",
                autoRetryRecommended: false);
        }

        if (!_securityStore.HasCloudflareNamedTunnelToken)
        {
            return PublishCloudflareTunnelFailure(
                RemoteAccessMode.CloudflareNamedTunnel,
                "尚未安全儲存 Cloudflare Tunnel Token。",
                autoRetryRecommended: false);
        }

        CloudflaredExecutableVerificationLease verifiedExecutable;
        try
        {
            var receipt = _securityStore.GetCloudflaredInstallationReceipt();
            verifiedExecutable = await _namedTunnelExecutableVerifier(
                    AppContext.BaseDirectory,
                    executablePath,
                    receipt,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return PublishCloudflareTunnelFailure(
                RemoteAccessMode.CloudflareNamedTunnel,
                $"cloudflared 安全驗證失敗：{exception.Message}",
                autoRetryRecommended: false);
        }

        await using var verifiedExecutableScope = verifiedExecutable;

        IWebTunnelService tunnel;
        try
        {
            var credential = _securityStore.GetCloudflareNamedTunnelCredential();
            tunnel = _namedTunnelFactory(
                verifiedExecutable.ExecutablePath,
                publicOrigin!,
                credential.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return PublishCloudflareTunnelFailure(
                RemoteAccessMode.CloudflareNamedTunnel,
                exception.Message,
                autoRetryRecommended: false);
        }

        return await StartCloudflareTunnelAsync(
                settings,
                tunnel,
                RemoteAccessMode.CloudflareNamedTunnel,
                publicOrigin,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<RemoteAccessRuntimeState> StartCloudflareTunnelAsync(
        RemoteControlSettings settings,
        IWebTunnelService tunnel,
        RemoteAccessMode accessMode,
        Uri? expectedPublicOrigin,
        CancellationToken cancellationToken)
    {

        tunnel.StateChanged += OnWebTunnelStateChanged;
        Volatile.Write(ref _quickTunnel, tunnel);
        WebTunnelSnapshot tunnelState;
        try
        {
            tunnelState = await tunnel.StartAsync(settings.LocalPort, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return await FailCloudflareTunnelStartAsync(
                    tunnel,
                    accessMode,
                    exception.Message,
                    autoRetryRecommended: true)
                .ConfigureAwait(false);
        }

        RememberWebTunnelSnapshot(tunnelState);
        if (!tunnelState.IsRunning || tunnelState.PublicUrl is null)
        {
            var error = tunnelState.Error ?? "cloudflared 未提供有效的 HTTPS 網址。";
            return await FailCloudflareTunnelStartAsync(
                    tunnel,
                    accessMode,
                    error,
                    autoRetryRecommended: true)
                .ConfigureAwait(false);
        }

        if (expectedPublicOrigin is not null
            && !SameOrigin(expectedPublicOrigin, tunnelState.PublicUrl))
        {
            return await FailCloudflareTunnelStartAsync(
                    tunnel,
                    accessMode,
                    "cloudflared 回報的公開網址與已設定的固定 HTTPS 網址不一致。",
                    autoRetryRecommended: false)
                .ConfigureAwait(false);
        }

        try
        {
            var host = await RemoteControlHost.StartAsync(
                    _backend,
                    new RemoteControlOptions
                    {
                        Port = settings.LocalPort,
                        PublicOrigin = tunnelState.PublicUrl,
                        AllowedGoogleLogins = [],
                        IngressMode = accessMode == RemoteAccessMode.CloudflareNamedTunnel
                            ? RemoteIngressMode.CloudflareNamedTunnel
                            : RemoteIngressMode.CloudflareQuickTunnel,
                        OperationCancellationToken = _applicationStopping
                    },
                    _securityStore,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _boundHost = host;
            _boundPort = settings.LocalPort;
            _boundAccessMode = accessMode;

            RemoteAccessRuntimeState? committed = null;
            string? commitError = null;
            lock (_quickTunnelStateGate)
            {
                if (!ReferenceEquals(tunnel, Volatile.Read(ref _quickTunnel)))
                {
                    commitError = "Cloudflare Tunnel 在啟用期間已被取代。";
                }
                else
                {
                    WebTunnelSnapshot latest;
                    try
                    {
                        latest = tunnel.Snapshot;
                        RememberWebTunnelSnapshot(latest);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        latest = tunnelState;
                        commitError = $"無法重新確認 Cloudflare Tunnel 狀態：{exception.Message}";
                    }

                    if (commitError is null &&
                        (!latest.IsRunning ||
                         latest.PublicUrl is null ||
                         !SameOrigin(tunnelState.PublicUrl, latest.PublicUrl) ||
                         (expectedPublicOrigin is not null
                          && !SameOrigin(expectedPublicOrigin, latest.PublicUrl))))
                    {
                        commitError = latest.Error ??
                                      "Cloudflare Tunnel 在本機 Web 完成綁定前已斷線或網址已變更。";
                    }

                    if (commitError is null)
                    {
                        lock (_hostGate)
                        {
                            _activeHost = host;
                        }

                        _faultArmedQuickTunnel = tunnel;
                        var routeLabel = accessMode == RemoteAccessMode.CloudflareNamedTunnel
                            ? "Cloudflare 固定網域"
                            : "Cloudflare 測試網址";
                        var hasLocalAccount = _securityStore.ApprovedAccounts.Any(account =>
                            account.Gmail is null);
                        committed = Publish(new RemoteAccessRuntimeState(
                            false,
                            true,
                            true,
                            true,
                            latest.PublicUrl,
                            !hasLocalAccount
                                ? $"Web 已透過 {routeLabel} 啟用；請先在電腦端建立遠端帳號。"
                                : $"Web 已透過 {routeLabel} 啟用；已核准帳號可直接從手機登入。",
                            null)
                        {
                            AccessMode = accessMode
                        });
                    }
                }
            }

            if (committed is not null)
            {
                return committed;
            }

            var cleanup = await QuiesceAndRemoveBoundHostAsync(disableOwnedServe: true)
                .ConfigureAwait(false);
            return PublishCloudflareTunnelFailure(
                accessMode,
                CombineErrors(commitError, cleanup.Succeeded ? null : cleanup.Error),
                autoRetryRecommended: true);
        }
        catch
        {
            await StopAndDisposeQuickTunnelAsync(tunnel).ConfigureAwait(false);
            throw;
        }
    }

    private void OnWebTunnelStateChanged(object? sender, WebTunnelSnapshot snapshot)
    {
        if (sender is not IWebTunnelService source)
        {
            return;
        }

        lock (_quickTunnelStateGate)
        {
            if (!ReferenceEquals(source, Volatile.Read(ref _quickTunnel)))
            {
                return;
            }

            RememberWebTunnelSnapshot(snapshot);
            var runtime = Volatile.Read(ref _state);
            if (snapshot.State == WebTunnelLifecycleState.Faulted &&
                ReferenceEquals(_faultArmedQuickTunnel, source) &&
                runtime.IsRunning &&
                IsCloudflareTunnelMode(runtime.AccessMode))
            {
                _faultArmedQuickTunnel = null;
                string? revokeError = null;
                lock (_hostGate)
                {
                    var activeHost = _activeHost;
                    _activeHost = null;
                    if (activeHost is not null)
                    {
                        try
                        {
                            activeHost.EnterFailClosedMode();
                        }
                        catch (Exception exception) when (exception is not OutOfMemoryException)
                        {
                            revokeError = $"隔離本機遠端 listener 失敗：{exception.Message}";
                        }
                    }
                }

                PublishCloudflareTunnelFailure(
                    runtime.AccessMode,
                    CombineErrors(
                        snapshot.Error ?? "cloudflared.exe 已意外中斷。",
                        revokeError),
                    autoRetryRecommended: true);
            }
        }

        try
        {
            WebTunnelStateChanged?.Invoke(this, snapshot);
        }
        catch (Exception)
        {
            // An optional desktop console subscriber must not own tunnel lifetime.
        }
    }

    private void OnTailscaleServeProcessExited(
        object? sender,
        TailscaleRouteProcessExitedEventArgs fault)
        => OnTailscaleRouteProcessExited(RemoteAccessMode.Tailscale, fault);

    private void OnTailscaleFunnelProcessExited(
        object? sender,
        TailscaleRouteProcessExitedEventArgs fault)
        => OnTailscaleRouteProcessExited(RemoteAccessMode.TailscaleFunnel, fault);

    private void OnTailscaleRouteProcessExited(
        RemoteAccessMode accessMode,
        TailscaleRouteProcessExitedEventArgs fault)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        string? guardError = null;
        RemoteAccessRuntimeState runtime;
        lock (_hostGate)
        {
            runtime = Volatile.Read(ref _state);
            if (!runtime.IsRunning
                || runtime.AccessMode != accessMode
                || _boundAccessMode != accessMode
                || _activeHost is not { } activeHost)
            {
                return;
            }

            // Keep _boundHost/_boundPort intact. The listener becomes an immediate deny-all
            // guard, and the recovery path may release it only after route absence is confirmed.
            _activeHost = null;
            try
            {
                activeHost.EnterFailClosedMode();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                guardError = $"隔離本機遠端 listener 失敗：{exception.Message}";
            }
        }

        var routeName = accessMode == RemoteAccessMode.TailscaleFunnel
            ? "Tailscale Funnel"
            : "Tailscale Serve";
        Publish(new RemoteAccessRuntimeState(
            false,
            false,
            runtime.IsTailscaleInstalled,
            fault.AutoRetryRecommended ? false : runtime.IsTailscaleConnected,
            null,
            $"{routeName} 已意外中斷；本機 Web 已切換為拒絕所有要求。",
            SanitizeError(CombineErrors(fault.Error, guardError)))
        {
            AccessMode = accessMode,
            AutoRetryRecommended = fault.AutoRetryRecommended
        });
    }

    private async Task<RemoteAccessRuntimeState> AbortStartAsync(
        TailscaleServeStatus status,
        string error,
        RemoteAccessMode accessMode)
    {
        var cleanup = await QuiesceAndRemoveBoundHostAsync(disableOwnedServe: true)
            .ConfigureAwait(false);
        if (!cleanup.Succeeded)
        {
            error = $"{error}；{cleanup.Error}";
            status = cleanup.Status ?? status;
        }

        return PublishFailure(status, error, accessMode);
    }

    private async Task<HostShutdownResult> QuiesceAndRemoveBoundHostAsync(
        bool disableOwnedServe)
    {
        var quickTunnel = Volatile.Read(ref _quickTunnel);
        lock (_quickTunnelStateGate)
        {
            // Explicit stop/reconfigure owns the next transition. Stopping/Stopped events from
            // any formerly armed tunnel must never schedule an automatic restart.
            _faultArmedQuickTunnel = null;
        }

        RemoteControlHost? host;
        string? revokeError = null;
        lock (_hostGate)
        {
            _activeHost = null;
            host = _boundHost;
            if (host is not null)
            {
                try
                {
                    host.EnterFailClosedMode();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    revokeError = exception.Message;
                }
            }
        }

        if ((_boundAccessMode is { } boundMode && IsCloudflareTunnelMode(boundMode))
            || quickTunnel is not null)
        {
            if (!disableOwnedServe)
            {
                return new HostShutdownResult(
                    false,
                    null,
                    CombineErrors(
                        revokeError,
                        "未要求停止 Cloudflare Tunnel；為避免 stale route，本機連接埠仍由防護 listener 保留。"));
            }

            var tunnelError = await StopAndDisposeQuickTunnelAsync(quickTunnel)
                .ConfigureAwait(false);
            if (tunnelError is not null)
            {
                return new HostShutdownResult(
                    false,
                    null,
                    CombineErrors(revokeError, tunnelError));
            }

            var quickHostError = await StopAndDisposeHostAsync(host).ConfigureAwait(false);
            return new HostShutdownResult(
                quickHostError is null,
                null,
                CombineErrors(revokeError, quickHostError));
        }

        if (_boundPort is not { } port)
        {
            var hostError = await StopAndDisposeHostAsync(host).ConfigureAwait(false);
            return new HostShutdownResult(
                hostError is null,
                null,
                CombineErrors(revokeError, hostError));
        }

        if (!disableOwnedServe)
        {
            var routeName = _boundAccessMode == RemoteAccessMode.TailscaleFunnel
                ? "Tailscale Funnel"
                : "Tailscale Serve";
            return new HostShutdownResult(
                false,
                null,
                CombineErrors(
                    revokeError,
                    $"未要求移除 {routeName}；為避免 stale route，本機連接埠仍由防護 listener 保留。"));
        }

        var isFunnel = _boundAccessMode == RemoteAccessMode.TailscaleFunnel;
        var routeService = isFunnel ? _funnel : _tailscale;
        var routeLabel = isFunnel ? "Tailscale Funnel" : "Tailscale Serve";
        TailscaleServeOperationResult disable;
        try
        {
            // Once shutdown begins it must complete independently of a disconnected UI client.
            disable = await routeService.DisableAsync(port, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new HostShutdownResult(
                false,
                null,
                CombineErrors(revokeError, $"無法確認 {routeLabel} 已移除：{exception.Message}"));
        }

        if (!IsConfirmedRouteAbsent(disable))
        {
            return new HostShutdownResult(
                false,
                disable.Status,
                CombineErrors(
                    revokeError,
                    disable.Error ?? $"{routeLabel} 尚未確認完全移除。"));
        }

        var stopError = await StopAndDisposeHostAsync(host).ConfigureAwait(false);
        return new HostShutdownResult(
            stopError is null,
            disable.Status,
            CombineErrors(revokeError, stopError));
    }

    private async Task<string?> StopAndDisposeHostAsync(RemoteControlHost? host)
    {
        if (host is null)
        {
            _boundHost = null;
            _boundPort = null;
            _boundAccessMode = null;
            return null;
        }

        string? error = null;
        try
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = $"停止本機遠端 listener 失敗：{exception.Message}";
        }

        try
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = CombineErrors(error, $"釋放本機遠端 listener 失敗：{exception.Message}");
        }
        finally
        {
            if (error is null && ReferenceEquals(_boundHost, host))
            {
                _boundHost = null;
                _boundPort = null;
                _boundAccessMode = null;
            }
        }

        return error;
    }

    private async Task<string?> StopAndDisposeQuickTunnelAsync(IWebTunnelService? tunnel)
    {
        if (tunnel is null)
        {
            return null;
        }

        string? stopError = null;
        try
        {
            var stopped = await tunnel.StopAsync(CancellationToken.None).ConfigureAwait(false);
            RememberWebTunnelSnapshot(stopped);
            if (stopped.State is not WebTunnelLifecycleState.Stopped)
            {
                stopError = stopped.Error ?? "Cloudflare Tunnel 尚未確認停止。";
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            stopError = $"停止 Cloudflare Tunnel 失敗：{exception.Message}";
            TryRememberWebTunnelSnapshot(tunnel);
        }

        var disposeSucceeded = false;
        string? disposeError = null;
        try
        {
            await tunnel.DisposeAsync().ConfigureAwait(false);
            disposeSucceeded = true;
            TryRememberWebTunnelSnapshot(tunnel);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            disposeError = $"釋放 Cloudflare Tunnel 失敗：{exception.Message}";
        }

        if (disposeSucceeded)
        {
            tunnel.StateChanged -= OnWebTunnelStateChanged;
            _ = Interlocked.CompareExchange(ref _quickTunnel, null, tunnel);
            // DisposeAsync is the ownership boundary: after it succeeds, a prior StopAsync
            // diagnostic does not justify retaining the loopback listener or a dead adapter.
            return null;
        }

        if (ReferenceEquals(Volatile.Read(ref _quickTunnel), tunnel))
        {
            // Keep the adapter and its event subscription so a later explicit Stop can retry;
            // dropping the final reference here could orphan a still-running connector.
            TryRememberWebTunnelSnapshot(tunnel);
        }

        return CombineErrors(
                   stopError,
                   disposeError)
               ?? "Cloudflare Tunnel 尚未確認完全停止與釋放。";
    }

    private void RememberWebTunnelSnapshot(WebTunnelSnapshot snapshot)
        => Volatile.Write(ref _lastWebTunnelSnapshot, snapshot);

    private async Task<RemoteAccessRuntimeState> FailCloudflareTunnelStartAsync(
        IWebTunnelService tunnel,
        RemoteAccessMode accessMode,
        string? error,
        bool autoRetryRecommended)
    {
        var cleanupError = await StopAndDisposeQuickTunnelAsync(tunnel).ConfigureAwait(false);
        return PublishCloudflareTunnelFailure(
            accessMode,
            CombineErrors(error, cleanupError),
            autoRetryRecommended);
    }

    private RemoteAccessRuntimeState PublishCloudflareTunnelFailure(
        RemoteAccessMode accessMode,
        string? error,
        bool autoRetryRecommended)
        => Publish(new RemoteAccessRuntimeState(
            false,
            false,
            false,
            false,
            null,
            accessMode == RemoteAccessMode.CloudflareNamedTunnel
                ? "Cloudflare 固定網域尚未啟用。"
                : "Cloudflare 隨機網址尚未啟用。",
            SanitizeError(error))
        {
            AccessMode = accessMode,
            AutoRetryRecommended = autoRetryRecommended
        });

    private static bool IsCloudflareTunnelMode(RemoteAccessMode accessMode)
        => accessMode is RemoteAccessMode.CloudflareQuickTunnel
            or RemoteAccessMode.CloudflareNamedTunnel;

    private void TryRememberWebTunnelSnapshot(IWebTunnelService tunnel)
    {
        try
        {
            RememberWebTunnelSnapshot(tunnel.Snapshot);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Retain the last known snapshot when a failed adapter cannot be queried safely.
        }
    }

    private RemoteAccessMode CurrentAccessMode
        => _boundAccessMode ?? _state.AccessMode;

    private static bool TryGetCleanCandidate(
        TailscaleServeStatus status,
        RemoteAccessMode accessMode,
        out Uri candidateUrl,
        out string? error)
    {
        candidateUrl = status.CandidateUrl!;
        if (!status.IsInstalled)
        {
            error = status.Error ?? "找不到 Tailscale CLI；請先安裝並登入 Tailscale。";
            return false;
        }

        if (!status.IsBackendRunning)
        {
            error = status.Error ?? $"Tailscale 尚未連線（BackendState={status.BackendState ?? "unknown"}）。";
            return false;
        }

        if (status.Error is not null)
        {
            error = status.Error;
            return false;
        }

        if (status.CandidateUrl is null)
        {
            error = accessMode == RemoteAccessMode.TailscaleFunnel
                ? "Tailscale 沒有提供可安全使用的 Funnel 公開 HTTPS 網址。"
                : "Tailscale 沒有提供可安全使用的私人 MagicDNS 網址。";
            return false;
        }

        if (status.HasHttpsPortConflict || status.IsConfigured || status.IsOwnedByThisService)
        {
            var routeName = accessMode == RemoteAccessMode.TailscaleFunnel
                ? "Tailscale Funnel"
                : "Tailscale Serve";
            var httpsPort = accessMode == RemoteAccessMode.TailscaleFunnel
                ? TailscaleFunnelService.FunnelHttpsPort
                : TailscaleServeService.ServeHttpsPort;
            error = $"{routeName} HTTPS {httpsPort} 已有設定；為保護既有設定，本程式不會覆寫。";
            return false;
        }

        candidateUrl = status.CandidateUrl;
        error = null;
        return true;
    }

    private static bool IsConfirmedOwnedRoute(
        TailscaleServeOperationResult result,
        Uri expectedOrigin)
        => result.Succeeded
           && result.Error is null
           && result.Status.IsInstalled
           && result.Status.IsBackendRunning
           && result.Status.IsConfigured
           && result.Status.IsOwnedByThisService
           && !result.Status.HasHttpsPortConflict
           && result.Status.Url is { } url
           && SameOrigin(expectedOrigin, url);

    private static bool IsConfirmedRouteAbsent(TailscaleServeOperationResult result)
        => result.Succeeded
           && result.Error is null
           && result.Status.IsInstalled
           && result.Status.IsBackendRunning
           && !result.Status.IsConfigured
           && !result.Status.IsOwnedByThisService
           && !result.Status.HasHttpsPortConflict
           && result.Status.Url is null;

    private static bool SameOrigin(Uri left, Uri right)
        => Uri.Compare(
               left,
               right,
               UriComponents.SchemeAndServer,
               UriFormat.SafeUnescaped,
               StringComparison.OrdinalIgnoreCase)
           == 0;

    private static bool IsTransientTailscaleCleanupFailure(
        HostShutdownResult cleanup,
        RemoteAccessMode accessMode)
        => accessMode is RemoteAccessMode.Tailscale or RemoteAccessMode.TailscaleFunnel
           && cleanup.Status is
           {
               IsInstalled: true,
               IsBackendRunning: false,
               RequiresHttpsCertificateEnablement: false
           };

    private RemoteAccessRuntimeState PublishFailure(
        TailscaleServeStatus status,
        string? error,
        RemoteAccessMode accessMode = RemoteAccessMode.Tailscale)
        => Publish(new RemoteAccessRuntimeState(
            false,
            false,
            status.IsInstalled,
            status.IsBackendRunning,
            null,
            accessMode == RemoteAccessMode.TailscaleFunnel
                ? "Tailscale Funnel 尚未啟用。"
                : "手機遠端控制尚未啟用。",
            SanitizeError(error))
        {
            RequiresTailscaleHttpsCertificateEnablement =
                status.RequiresHttpsCertificateEnablement,
            AccessMode = accessMode,
            // Retry only when Tailscale is installed but its backend/status command is not
            // ready yet (common during boot or a temporary network outage). Configuration,
            // identity, DPAPI, HTTPS-enablement, and Serve-conflict failures need user action.
            AutoRetryRecommended = status.IsInstalled
                                   && !status.IsBackendRunning
                                   && !status.RequiresHttpsCertificateEnablement
        });

    private RemoteAccessRuntimeState PublishShutdownFailure(
        HostShutdownResult cleanup,
        string message,
        bool autoRetryRecommended = false)
        => Publish(new RemoteAccessRuntimeState(
            false,
            false,
            cleanup.Status?.IsInstalled ?? _state.IsTailscaleInstalled,
            cleanup.Status?.IsBackendRunning ?? _state.IsTailscaleConnected,
            null,
            message,
            SanitizeError(cleanup.Error))
        {
            RequiresTailscaleHttpsCertificateEnablement =
                cleanup.Status?.RequiresHttpsCertificateEnablement ?? false,
            AccessMode = CurrentAccessMode,
            AutoRetryRecommended = autoRetryRecommended
        });

    private RemoteAccessRuntimeState Publish(RemoteAccessRuntimeState state)
    {
        EventHandler<RemoteAccessRuntimeState>[] observers;
        lock (_runtimeStateGate)
        {
            Volatile.Write(ref _state, state);
            observers = StateChanged?.GetInvocationList()
                .Cast<EventHandler<RemoteAccessRuntimeState>>()
                .ToArray()
                ?? [];
        }

        foreach (var observer in observers)
        {
            // A re-entrant or concurrent observer may perform a newer transition. Do not deliver
            // an older state to remaining observers after that transition wins.
            if (!ReferenceEquals(Volatile.Read(ref _state), state))
            {
                break;
            }

            try
            {
                observer(this, state);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // UI/diagnostic observers do not own Kestrel or tunnel lifetime. One bad
                // subscriber must neither abort startup nor prevent later subscribers.
            }
        }

        return state;
    }

    private void ValidateSettings(RemoteControlSettings settings)
    {
        if (settings.LocalPort is < 1024 or > 65535)
        {
            throw new InvalidOperationException("本機遠端服務 Port 必須介於 1024–65535。");
        }

        if (!Enum.IsDefined(settings.AccessMode))
        {
            throw new InvalidOperationException("遠端連線模式無效。");
        }

        if (IsCloudflareTunnelMode(settings.AccessMode)
            || settings.AccessMode == RemoteAccessMode.TailscaleFunnel)
        {
            return;
        }

        if (!RemoteIdentity.IsCanonicalGmailLogin(settings.AllowedLogin))
        {
            throw new InvalidOperationException("請輸入完整且有效的 @gmail.com 帳號。");
        }

        // Multiple local public-ingress accounts may coexist with private Tailscale accounts.
        // The Tailscale identity middleware and credential store select only records
        // whose verified Gmail exactly matches this allowlist value.
    }

    private static string? CombineErrors(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return $"{first}；{second}";
    }

    private static string SanitizeError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "未知錯誤。";
        var normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500] + "…";
    }

    private sealed record HostShutdownResult(
        bool Succeeded,
        TailscaleServeStatus? Status,
        string? Error);
}
