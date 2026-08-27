using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MinecraftServerManager.App.Services;

internal interface ITailscaleExecutableLocator
{
    string? FindExecutable();
}

internal interface ITailscaleCommandRunner
{
    Task<TailscaleCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal interface ITailscaleForegroundProcessFactory
{
    Task<ITailscaleForegroundProcess> StartAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal interface ITailscaleForegroundProcess : IAsyncDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    string StandardOutput { get; }
    string StandardError { get; }
    Task Completion { get; }

    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);

    void KillEntireProcessTree();
}

internal interface ITailscaleDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed record TailscaleCommandResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

internal sealed record TailscaleServeStatus(
    bool IsInstalled,
    bool IsBackendRunning,
    bool IsConfigured,
    bool IsOwnedByThisService,
    bool HasHttpsPortConflict,
    string? ExecutablePath,
    string? BackendState,
    string? DnsName,
    Uri? CandidateUrl,
    Uri? Url,
    string? Error)
{
    /// <summary>
    /// The node is connected, but its tailnet has not enabled HTTPS certificate
    /// provisioning yet. This is distinct from a command timeout or Serve conflict.
    /// </summary>
    public bool RequiresHttpsCertificateEnablement { get; init; }
}

internal sealed record TailscaleServeOperationResult(
    bool Succeeded,
    bool Changed,
    TailscaleServeStatus Status,
    string? Error);

internal sealed record TailscaleRouteProcessExitedEventArgs(
    int ProcessId,
    int? ExitCode,
    string Error,
    bool AutoRetryRecommended);

internal interface ITailscaleServeService : IAsyncDisposable
{
    event EventHandler<TailscaleRouteProcessExitedEventArgs>? ForegroundProcessExited;

    Task<TailscaleServeStatus> GetStatusAsync(
        int localPort,
        CancellationToken cancellationToken = default);

    Task<TailscaleServeOperationResult> EnableAsync(
        int localPort,
        CancellationToken cancellationToken = default);

    Task<TailscaleServeOperationResult> DisableAsync(
        int localPort,
        CancellationToken cancellationToken = default);
}

internal enum TailscaleHttpsIngressKind
{
    Serve,
    Funnel,
}

/// <summary>
/// Owns one foreground <c>tailscale serve</c> child process. No persistent Serve command is ever
/// issued: the child IPN-bus session is the ownership boundary and its configuration disappears
/// when that process exits. Existing configuration on HTTPS 8443 is always treated as user-owned.
/// </summary>
internal sealed class TailscaleServeService : ITailscaleServeService
{
    internal const int ServeHttpsPort = 8443;
    internal const int DefaultStartupProbeAttempts = 20;
    internal static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DefaultStartupProbeInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan NaturalExitGracePeriod = TimeSpan.FromMilliseconds(150);
    internal static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(3);

    private readonly ITailscaleExecutableLocator _executableLocator;
    private readonly ITailscaleCommandRunner _commandRunner;
    private readonly ITailscaleForegroundProcessFactory _foregroundProcessFactory;
    private readonly ITailscaleDelay _delay;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeSpan _startupProbeInterval;
    private readonly int _startupProbeAttempts;
    private readonly TailscaleHttpsIngressKind _ingressKind;
    private readonly int _httpsPort;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private ITailscaleForegroundProcess? _foregroundProcess;
    private string? _ownedTarget;
    private string? _ownedSessionId;
    private long _foregroundProcessGeneration;
    private bool _disposed;

    public event EventHandler<TailscaleRouteProcessExitedEventArgs>? ForegroundProcessExited;

    public TailscaleServeService()
        : this(
            new TailscaleExecutableLocator(),
            new SystemTailscaleCommandRunner(),
            new SystemTailscaleForegroundProcessFactory(),
            new SystemTailscaleDelay())
    {
    }

    internal TailscaleServeService(
        ITailscaleExecutableLocator executableLocator,
        ITailscaleCommandRunner commandRunner,
        ITailscaleForegroundProcessFactory foregroundProcessFactory,
        ITailscaleDelay delay,
        TimeSpan? commandTimeout = null,
        int startupProbeAttempts = DefaultStartupProbeAttempts,
        TimeSpan? startupProbeInterval = null,
        TimeSpan? operationTimeout = null,
        TailscaleHttpsIngressKind ingressKind = TailscaleHttpsIngressKind.Serve)
    {
        _executableLocator = executableLocator
            ?? throw new ArgumentNullException(nameof(executableLocator));
        _commandRunner = commandRunner
            ?? throw new ArgumentNullException(nameof(commandRunner));
        _foregroundProcessFactory = foregroundProcessFactory
            ?? throw new ArgumentNullException(nameof(foregroundProcessFactory));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;
        _startupProbeInterval = startupProbeInterval ?? DefaultStartupProbeInterval;
        _startupProbeAttempts = startupProbeAttempts;
        _ingressKind = ingressKind;
        _httpsPort = ingressKind == TailscaleHttpsIngressKind.Funnel
            ? TailscaleFunnelService.FunnelHttpsPort
            : ServeHttpsPort;

        if (!Enum.IsDefined(ingressKind))
        {
            throw new ArgumentOutOfRangeException(nameof(ingressKind));
        }

        if (_commandTimeout <= TimeSpan.Zero || _commandTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        }

        if (_operationTimeout <= TimeSpan.Zero || _operationTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        if (_startupProbeInterval < TimeSpan.Zero
            || _startupProbeInterval > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(startupProbeInterval));
        }

        if (_startupProbeAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(startupProbeAttempts));
        }
    }

    public async Task<TailscaleServeStatus> GetStatusAsync(
        int localPort,
        CancellationToken cancellationToken = default)
    {
        ValidateLocalPort(localPort);
        ThrowIfDisposed();
        using var timeoutCancellation = new CancellationTokenSource(_operationTimeout);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        var operationToken = operationCancellation.Token;
        var gateEntered = false;
        try
        {
            await _operationGate.WaitAsync(operationToken).ConfigureAwait(false);
            gateEntered = true;
            ThrowIfDisposed();
            operationToken.ThrowIfCancellationRequested();
            var executablePath = _executableLocator.FindExecutable();
            operationToken.ThrowIfCancellationRequested();
            var status = string.IsNullOrWhiteSpace(executablePath)
                ? CreateUnavailableStatus(
                    "找不到 Tailscale CLI。請先安裝 Tailscale，或將 tailscale.exe 加入 PATH。")
                : await ProbeAsync(executablePath, CreateTarget(localPort), operationToken)
                    .ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            return status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            return CreateUnavailableStatus(CreateOperationTimeoutError("讀取 Tailscale Serve 狀態"));
        }
        finally
        {
            if (gateEntered)
            {
                _operationGate.Release();
            }
        }
    }

    public async Task<TailscaleServeOperationResult> EnableAsync(
        int localPort,
        CancellationToken cancellationToken = default)
    {
        ValidateLocalPort(localPort);
        ThrowIfDisposed();
        var target = CreateTarget(localPort);
        using var timeoutCancellation = new CancellationTokenSource(_operationTimeout);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        var operationToken = operationCancellation.Token;
        var gateEntered = false;
        ITailscaleForegroundProcess? startedProcess = null;
        var timeoutStatus = CreateUnavailableStatus(
            CreateOperationTimeoutError("啟用 Tailscale Serve"));
        try
        {
            await _operationGate.WaitAsync(operationToken).ConfigureAwait(false);
            gateEntered = true;
            ThrowIfDisposed();
            operationToken.ThrowIfCancellationRequested();

            var executablePath = _executableLocator.FindExecutable();
            operationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                var unavailable = CreateUnavailableStatus(
                    "找不到 Tailscale CLI。請先安裝 Tailscale，或將 tailscale.exe 加入 PATH。");
                return Failure(unavailable, unavailable.Error!);
            }

            if (_foregroundProcess is { } existingProcess)
            {
                var current = await ProbeAsync(executablePath, target, operationToken)
                    .ConfigureAwait(false);
                timeoutStatus = current;
                operationToken.ThrowIfCancellationRequested();
                if (string.Equals(_ownedTarget, target, StringComparison.Ordinal)
                    && !existingProcess.HasExited
                    && current.IsOwnedByThisService
                    && current.Url is not null)
                {
                    return new TailscaleServeOperationResult(true, false, current, null);
                }

                return Failure(
                    current,
                    $"本程式已持有 Tailscale Serve 前景子程序 PID {existingProcess.ProcessId}，未啟動另一個。");
            }

            var before = await ProbeAsync(executablePath, target, operationToken)
                .ConfigureAwait(false);
            timeoutStatus = before;
            operationToken.ThrowIfCancellationRequested();
            if (before.Error is not null)
            {
                return Failure(before, before.Error);
            }

            if (!before.IsBackendRunning)
            {
                return Failure(
                    before,
                    $"Tailscale 尚未連線（BackendState={before.BackendState ?? "unknown"}）。");
            }

            if (before.CandidateUrl is null)
            {
                return Failure(before, "Tailscale status 沒有提供可安全使用的 MagicDNS 名稱。");
            }

            if (before.HasHttpsPortConflict || before.IsConfigured)
            {
                return Failure(
                    before,
                    $"Tailscale {IngressDisplayName} HTTPS {_httpsPort} 已有既存設定；為保護使用者設定，本程式不會覆寫。");
            }

            ITailscaleForegroundProcess process;
            try
            {
                process = await _foregroundProcessFactory.StartAsync(
                        executablePath,
                        [IngressCommand, "--yes", $"--https={_httpsPort}", target],
                        operationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Failure(before, $"無法啟動 Tailscale Serve 前景子程序：{exception.Message}");
            }

            _foregroundProcess = process;
            _ownedTarget = target;
            _ownedSessionId = null;
            var processGeneration = unchecked(++_foregroundProcessGeneration);
            _ = ObserveForegroundProcessExitAsync(process, processGeneration);
            startedProcess = process;
            operationToken.ThrowIfCancellationRequested();
            string? lastProbeError = null;
            for (var attempt = 0; attempt < _startupProbeAttempts; attempt++)
            {
                operationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    var error = DescribeForegroundExit(process);
                    await StopAndClearOwnedProcessAsync(process).ConfigureAwait(false);
                    return Failure(before, error);
                }

                var configResult = await RunCommandSafeAsync(
                        executablePath,
                        [IngressCommand, "status", "--json"],
                        operationToken)
                    .ConfigureAwait(false);
                operationToken.ThrowIfCancellationRequested();
                if (configResult.Succeeded)
                {
                    if (!TryAnalyzeServeConfig(
                            configResult.StandardOutput,
                            before.DnsName,
                            target,
                            _httpsPort,
                            _ingressKind == TailscaleHttpsIngressKind.Funnel,
                            out var analysis,
                            out var parseError))
                    {
                        lastProbeError = parseError;
                    }
                    else if (analysis.IsExactForegroundTarget)
                    {
                        if (!process.HasExited
                            && analysis.ExactForegroundSessionId is { Length: > 0 } sessionId
                            && HasForegroundServeSuccessMarker(process, before.CandidateUrl))
                        {
                            _ownedSessionId ??= sessionId;
                            var active = CreateStatusFromAnalysis(
                                executablePath,
                                before.BackendState,
                                before.DnsName,
                                before.CandidateUrl,
                                target,
                                analysis);
                            operationToken.ThrowIfCancellationRequested();
                            if (string.Equals(
                                    _ownedSessionId,
                                    sessionId,
                                    StringComparison.Ordinal)
                                && !process.HasExited
                                && active.IsOwnedByThisService
                                && active.Url is not null)
                            {
                                return new TailscaleServeOperationResult(true, true, active, null);
                            }
                        }

                        if (process.HasExited)
                        {
                            var error = DescribeForegroundExit(process);
                            await StopAndClearOwnedProcessAsync(process).ConfigureAwait(false);
                            return Failure(before, error);
                        }

                        lastProbeError =
                            "尚未取得可與本程式前景子程序建立因果關聯的 Tailscale session。";
                    }
                    else if (analysis.HasAnyHttpsPortConfiguration)
                    {
                        await StopAndClearOwnedProcessAsync(process).ConfigureAwait(false);
                        return Failure(
                            before,
                            $"Tailscale {IngressDisplayName} HTTPS {_httpsPort} 出現非本程式預期的設定；已停止本程式的前景子程序。");
                    }
                    else
                    {
                        lastProbeError = null;
                    }
                }
                else
                {
                    lastProbeError = DescribeCommandFailure(
                        "確認 Tailscale Serve 前景狀態",
                        configResult);
                }

                if (attempt + 1 < _startupProbeAttempts)
                {
                    await _delay.DelayAsync(_startupProbeInterval, operationToken)
                        .ConfigureAwait(false);
                }
            }

            await StopAndClearOwnedProcessAsync(process).ConfigureAwait(false);
            return Failure(
                before,
                lastProbeError
                ?? $"Tailscale {IngressDisplayName} 未在預期時間內建立預期的 HTTPS {_httpsPort} 設定。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupStartedProcessAsync(startedProcess).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            await CleanupStartedProcessAsync(startedProcess).ConfigureAwait(false);
            var error = CreateOperationTimeoutError("啟用 Tailscale Serve");
            return Failure(timeoutStatus, error);
        }
        catch
        {
            await CleanupStartedProcessAsync(startedProcess).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (gateEntered)
            {
                _operationGate.Release();
            }
        }
    }

    public async Task<TailscaleServeOperationResult> DisableAsync(
        int localPort,
        CancellationToken cancellationToken = default)
    {
        ValidateLocalPort(localPort);
        ThrowIfDisposed();
        var target = CreateTarget(localPort);
        using var timeoutCancellation = new CancellationTokenSource(_operationTimeout);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        var operationToken = operationCancellation.Token;
        var gateEntered = false;
        var changed = false;
        var timeoutStatus = CreateUnavailableStatus(
            CreateOperationTimeoutError("停用 Tailscale Serve"));
        try
        {
            await _operationGate.WaitAsync(operationToken).ConfigureAwait(false);
            gateEntered = true;
            ThrowIfDisposed();
            var process = _foregroundProcess;
            if (process is null)
            {
                var status = await ProbeIfAvailableAsync(target, operationToken)
                    .ConfigureAwait(false);
                timeoutStatus = status;
                operationToken.ThrowIfCancellationRequested();
                if (status.Error is not null
                    || status.IsConfigured
                    || status.HasHttpsPortConflict)
                {
                    return Failure(
                        status,
                        status.Error
                        ?? $"偵測到本程式未持有的 HTTPS {_httpsPort} 設定；未做任何變更。");
                }

                return new TailscaleServeOperationResult(true, false, status, null);
            }

            if (!string.Equals(_ownedTarget, target, StringComparison.Ordinal))
            {
                var status = await ProbeIfAvailableAsync(target, operationToken)
                    .ConfigureAwait(false);
                timeoutStatus = status;
                operationToken.ThrowIfCancellationRequested();
                return Failure(
                    status,
                    $"本程式持有的前景子程序 PID {process.ProcessId} 不屬於指定的本機 Port。");
            }

            operationToken.ThrowIfCancellationRequested();
            var wasRunning = !process.HasExited;
            changed = wasRunning;
            var stop = await StopAndClearOwnedProcessAsync(process).ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            var after = await WaitForHttpsPortToClearAsync(target, operationToken)
                .ConfigureAwait(false);
            timeoutStatus = after;
            operationToken.ThrowIfCancellationRequested();
            if (!stop.ExitConfirmed)
            {
                var error = $"已關閉 PID {process.ProcessId} 的 Kill-on-close Job，但無法在時限內確認子程序退出。";
                return Failure(after, error, wasRunning);
            }

            if (after.Error is not null
                || !after.IsBackendRunning
                || after.IsConfigured
                || after.HasHttpsPortConflict)
            {
                var error = after.Error
                            ?? (!after.IsBackendRunning
                                ? "Tailscale 已離線，無法確認前景 Serve 設定已撤除。"
                                : $"HTTPS {_httpsPort} 在停止本程式子程序後仍有設定；為避免 stale route，停用不視為成功。");
                return Failure(after, error, wasRunning);
            }

            return new TailscaleServeOperationResult(true, wasRunning, after, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            var error = CreateOperationTimeoutError("停用 Tailscale Serve");
            return Failure(timeoutStatus, error, changed);
        }
        finally
        {
            if (gateEntered)
            {
                _operationGate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_foregroundProcess is { } process)
            {
                await StopAndClearOwnedProcessAsync(process).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<TailscaleServeStatus> ProbeIfAvailableAsync(
        string expectedTarget,
        CancellationToken cancellationToken)
    {
        var executablePath = _executableLocator.FindExecutable();
        return string.IsNullOrWhiteSpace(executablePath)
            ? CreateUnavailableStatus("Tailscale CLI 目前不可用。")
            : await ProbeAsync(executablePath, expectedTarget, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<TailscaleServeStatus> WaitForHttpsPortToClearAsync(
        string expectedTarget,
        CancellationToken cancellationToken)
    {
        TailscaleServeStatus? last = null;
        for (var attempt = 0; attempt < _startupProbeAttempts; attempt++)
        {
            last = await ProbeIfAvailableAsync(expectedTarget, cancellationToken)
                .ConfigureAwait(false);
            if (last.Error is null
                && last.IsBackendRunning
                && !last.IsConfigured
                && !last.HasHttpsPortConflict)
            {
                return last;
            }

            if (attempt + 1 < _startupProbeAttempts)
            {
                await _delay.DelayAsync(_startupProbeInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return last ?? CreateUnavailableStatus("無法確認 Tailscale Serve 狀態。");
    }

    private async Task<TailscaleServeStatus> ProbeAsync(
        string executablePath,
        string expectedTarget,
        CancellationToken cancellationToken)
    {
        var nodeResult = await RunCommandSafeAsync(
                executablePath,
                ["status", "--json"],
                cancellationToken)
            .ConfigureAwait(false);
        if (!nodeResult.Succeeded)
        {
            return CreateInstalledErrorStatus(
                executablePath,
                DescribeCommandFailure("讀取 Tailscale 狀態", nodeResult));
        }

        if (!TryParseNodeStatus(
                nodeResult.StandardOutput,
                out var backendState,
                out var dnsName,
                out var requiresHttpsCertificateEnablement,
                out var nodeParseError))
        {
            return CreateInstalledErrorStatus(executablePath, nodeParseError!);
        }

        var backendRunning = string.Equals(
            backendState,
            "Running",
            StringComparison.OrdinalIgnoreCase);
        var candidateUrl = backendRunning && TryCreateServeUrl(dnsName, _httpsPort, out var parsedCandidate)
            ? parsedCandidate
            : null;
        if (!backendRunning)
        {
            return new TailscaleServeStatus(
                true,
                false,
                false,
                false,
                false,
                executablePath,
                backendState,
                dnsName,
                null,
                null,
                null);
        }

        if (requiresHttpsCertificateEnablement)
        {
            return new TailscaleServeStatus(
                true,
                true,
                false,
                false,
                false,
                executablePath,
                backendState,
                dnsName,
                candidateUrl,
                null,
                "此 Tailnet 尚未啟用 Tailscale HTTPS 憑證。請先在 Tailscale 管理頁完成 Enable HTTPS 授權，再重新套用。")
            {
                RequiresHttpsCertificateEnablement = true
            };
        }

        var serveResult = await RunCommandSafeAsync(
                executablePath,
                [IngressCommand, "status", "--json"],
                cancellationToken)
            .ConfigureAwait(false);
        if (!serveResult.Succeeded)
        {
            return new TailscaleServeStatus(
                true,
                true,
                false,
                false,
                false,
                executablePath,
                backendState,
                dnsName,
                candidateUrl,
                null,
                DescribeCommandFailure("讀取 Tailscale Serve 狀態", serveResult));
        }

        if (!TryAnalyzeServeConfig(
                serveResult.StandardOutput,
                dnsName,
                expectedTarget,
                _httpsPort,
                _ingressKind == TailscaleHttpsIngressKind.Funnel,
                out var analysis,
                out var configParseError))
        {
            return new TailscaleServeStatus(
                true,
                true,
                false,
                false,
                false,
                executablePath,
                backendState,
                dnsName,
                candidateUrl,
                null,
                configParseError);
        }

        return CreateStatusFromAnalysis(
            executablePath,
            backendState,
            dnsName,
            candidateUrl,
            expectedTarget,
            analysis);
    }

    private TailscaleServeStatus CreateStatusFromAnalysis(
        string executablePath,
        string? backendState,
        string? dnsName,
        Uri? candidateUrl,
        string expectedTarget,
        ServeConfigAnalysis analysis)
    {
        var ownsLiveProcess = _foregroundProcess is { HasExited: false }
                              && string.Equals(
                                  _ownedTarget,
                                  expectedTarget,
                                  StringComparison.Ordinal);
        var owned = ownsLiveProcess
                    && analysis.IsExactForegroundTarget
                    && !string.IsNullOrEmpty(_ownedSessionId)
                    && string.Equals(
                        _ownedSessionId,
                        analysis.ExactForegroundSessionId,
                        StringComparison.Ordinal);
        return new TailscaleServeStatus(
            true,
            true,
            analysis.IsExactPrivateTarget,
            owned,
            analysis.HasAnyHttpsPortConfiguration && !owned,
            executablePath,
            backendState,
            dnsName,
            candidateUrl,
            analysis.IsExactPrivateTarget ? candidateUrl : null,
            null);
    }

    private async Task<TailscaleCommandResult> RunCommandSafeAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _commandRunner.RunAsync(
                    executablePath,
                    arguments,
                    _commandTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new TailscaleCommandResult(null, string.Empty, exception.Message);
        }
    }

    private async Task ObserveForegroundProcessExitAsync(
        ITailscaleForegroundProcess process,
        long generation)
    {
        try
        {
            await process.Completion.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Completion is only a lifetime signal. A broken adapter is treated exactly like an
            // exited child below if it still owns the current generation.
        }

        TailscaleRouteProcessExitedEventArgs? notification = null;
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Enable/Disable/Dispose all serialize through this gate. A normal shutdown clears
            // ownership before it terminates the child, and an old delayed completion can never
            // invalidate a newer child because both the reference and generation must match.
            if (_disposed
                || generation != _foregroundProcessGeneration
                || !ReferenceEquals(_foregroundProcess, process))
            {
                return;
            }

            _foregroundProcess = null;
            _ownedTarget = null;
            _ownedSessionId = null;
            // Process.Exited can precede the final redirected-output callbacks. Disposal closes
            // the owned Job and settles both bounded drains before their stable snapshots are
            // used for diagnostics and transient-failure classification.
            await process.DisposeAsync().ConfigureAwait(false);
            var error = DescribeForegroundExit(process);
            notification = new TailscaleRouteProcessExitedEventArgs(
                process.ProcessId,
                process.ExitCode,
                error,
                IsTransientBackendOrNetworkExit(process.StandardOutput, process.StandardError));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Never let a diagnostic/disposal failure become an unobserved task exception. The
            // route has already lost its ownership boundary and must still fail closed.
            notification ??= new TailscaleRouteProcessExitedEventArgs(
                process.ProcessId,
                process.ExitCode,
                $"Tailscale {IngressDisplayName} 前景子程序已意外中斷：{exception.Message}",
                IsTransientBackendOrNetworkExit(process.StandardOutput, process.StandardError));
        }
        finally
        {
            if (notification is not null)
            {
                // Finish notifying the current generation before another Enable can commit a
                // replacement child. This prevents a delayed old-child callback from ever
                // invalidating a newer route.
                NotifyForegroundProcessExited(notification);
            }

            _operationGate.Release();
        }
    }

    private void NotifyForegroundProcessExited(
        TailscaleRouteProcessExitedEventArgs notification)
    {
        foreach (var observer in ForegroundProcessExited?.GetInvocationList()
                     .Cast<EventHandler<TailscaleRouteProcessExitedEventArgs>>()
                 ?? [])
        {
            try
            {
                observer(this, notification);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A coordinator/UI observer never owns this service's process lifetime.
            }
        }
    }

    private async Task CleanupStartedProcessAsync(ITailscaleForegroundProcess? process)
    {
        if (process is not null && ReferenceEquals(_foregroundProcess, process))
        {
            await StopAndClearOwnedProcessAsync(process).ConfigureAwait(false);
        }
    }

    private async Task<ForegroundStopResult> StopAndClearOwnedProcessAsync(
        ITailscaleForegroundProcess process)
    {
        if (ReferenceEquals(_foregroundProcess, process))
        {
            _foregroundProcess = null;
            _ownedTarget = null;
            _ownedSessionId = null;
        }

        var exitConfirmed = process.HasExited;
        if (!exitConfirmed)
        {
            exitConfirmed = await process.WaitForExitAsync(
                    NaturalExitGracePeriod,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (!exitConfirmed)
        {
            try
            {
                process.KillEntireProcessTree();
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or
                NotSupportedException or
                Win32Exception)
            {
                // Dispose below closes the owned Kill-on-close Job as the final safety net.
            }

            exitConfirmed = process.HasExited
                            || await process.WaitForExitAsync(
                                    ForcedExitTimeout,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
        }

        var result = new ForegroundStopResult(
            exitConfirmed,
            process.ExitCode,
            process.StandardOutput,
            process.StandardError);
        await process.DisposeAsync().ConfigureAwait(false);
        return result;
    }

    private static TailscaleServeOperationResult Failure(
        TailscaleServeStatus status,
        string error,
        bool changed = false)
        => new(false, changed, status with { Error = error }, error);

    private string CreateOperationTimeoutError(string operation)
        => $"{operation}超過整體時限（{_operationTimeout.TotalSeconds:0.###} 秒）；未確認成功。";

    private static bool HasForegroundServeSuccessMarker(
        ITailscaleForegroundProcess process,
        Uri? candidateUrl)
        => candidateUrl is not null
           && process.StandardOutput.Contains(
               candidateUrl.GetLeftPart(UriPartial.Authority),
               StringComparison.OrdinalIgnoreCase);

    private static TailscaleServeStatus CreateUnavailableStatus(string error)
        => new(
            false,
            false,
            false,
            false,
            false,
            null,
            null,
            null,
            null,
            null,
            error);

    private static TailscaleServeStatus CreateInstalledErrorStatus(
        string executablePath,
        string error)
        => new(
            true,
            false,
            false,
            false,
            false,
            executablePath,
            null,
            null,
            null,
            null,
            error);

    private static string DescribeCommandFailure(
        string operation,
        TailscaleCommandResult result)
    {
        if (result.TimedOut)
        {
            return $"{operation}逾時。";
        }

        return DescribeFailureDetail(
            operation,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError);
    }

    private string DescribeForegroundExit(ITailscaleForegroundProcess process)
        => DescribeFailureDetail(
            $"Tailscale {IngressDisplayName} 前景子程序 PID {process.ProcessId} 已提前結束",
            process.ExitCode,
            process.StandardOutput,
            process.StandardError);

    private static bool IsTransientBackendOrNetworkExit(
        string standardOutput,
        string standardError)
    {
        var detail = string.Concat(standardError, "\n", standardOutput);
        if (string.IsNullOrWhiteSpace(detail))
        {
            return false;
        }

        ReadOnlySpan<string> transientMarkers =
        [
            "backend",
            "tailscaled",
            "tailscale service",
            "network",
            "offline",
            "disconnected",
            "connection reset",
            "connection refused",
            "connection closed",
            "failed to connect",
            "temporarily unavailable",
            "timeout",
            "timed out",
            "no internet",
            "dns lookup"
        ];
        foreach (var marker in transientMarkers)
        {
            if (detail.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeFailureDetail(
        string operation,
        int? exitCode,
        string standardOutput,
        string standardError)
    {
        var detail = string.IsNullOrWhiteSpace(standardError)
            ? standardOutput
            : standardError;
        detail = detail.Trim();
        if (detail.Length > 1_024)
        {
            detail = detail[..1_024] + "…";
        }

        var code = exitCode is { } value ? value.ToString() : "unknown";
        return detail.Length == 0
            ? $"{operation}（ExitCode={code}）。"
            : $"{operation}（ExitCode={code}）：{detail}";
    }

    private static bool TryParseNodeStatus(
        string json,
        out string? backendState,
        out string? dnsName,
        out bool requiresHttpsCertificateEnablement,
        out string? error)
    {
        backendState = null;
        dnsName = null;
        requiresHttpsCertificateEnablement = false;
        error = null;
        try
        {
            using var document = ParseJson(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(
                    document.RootElement,
                    "BackendState",
                    out var backendElement)
                || backendElement.ValueKind != JsonValueKind.String)
            {
                error = "Tailscale status JSON 缺少 BackendState。";
                return false;
            }

            backendState = backendElement.GetString()?.Trim();
            if (TryGetPropertyIgnoreCase(document.RootElement, "Self", out var selfElement)
                && selfElement.ValueKind == JsonValueKind.Object
                && TryGetPropertyIgnoreCase(selfElement, "DNSName", out var dnsElement)
                && dnsElement.ValueKind == JsonValueKind.String)
            {
                dnsName = NormalizeDnsName(dnsElement.GetString());
            }

            if (!TryGetPropertyIgnoreCase(
                    document.RootElement,
                    "CertDomains",
                    out var certDomainsElement))
            {
                error = "Tailscale status JSON 缺少 CertDomains；無法確認 HTTPS 憑證功能狀態。";
                return false;
            }

            if (certDomainsElement.ValueKind == JsonValueKind.Null)
            {
                requiresHttpsCertificateEnablement = true;
                return true;
            }

            if (certDomainsElement.ValueKind != JsonValueKind.Array)
            {
                error = "Tailscale status JSON 的 CertDomains 不是陣列或 null。";
                return false;
            }

            var hasCertificateForCurrentDnsName = false;
            foreach (var domainElement in certDomainsElement.EnumerateArray())
            {
                if (domainElement.ValueKind != JsonValueKind.String)
                {
                    error = "Tailscale status JSON 的 CertDomains 含無效網域名稱。";
                    return false;
                }

                var certificateDomain = NormalizeDnsName(domainElement.GetString());
                if (certificateDomain is null)
                {
                    error = "Tailscale status JSON 的 CertDomains 含無效網域名稱。";
                    return false;
                }

                hasCertificateForCurrentDnsName |= string.Equals(
                    certificateDomain,
                    dnsName,
                    StringComparison.OrdinalIgnoreCase);
            }

            requiresHttpsCertificateEnablement = !hasCertificateForCurrentDnsName;

            return true;
        }
        catch (JsonException exception)
        {
            error = $"無法解析 Tailscale status JSON：{exception.Message}";
            return false;
        }
    }

    private static bool TryAnalyzeServeConfig(
        string json,
        string? dnsName,
        string expectedTarget,
        int httpsPort,
        bool requireFunnel,
        out ServeConfigAnalysis analysis,
        out string? error)
    {
        analysis = default;
        error = null;
        try
        {
            using var document = ParseJson(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Tailscale Serve status JSON 根節點不是物件。";
                return false;
            }

            var candidates = new List<ServeConfigCandidate>();
            if (!TryCollectNodeHttpsPortCandidates(
                    document.RootElement,
                    httpsPort,
                    candidates,
                    out error))
            {
                return false;
            }

            if (candidates.Count == 0)
            {
                analysis = new ServeConfigAnalysis(false, false, false, null);
                return true;
            }

            if (candidates.Count != 1)
            {
                analysis = new ServeConfigAnalysis(true, false, false, null);
                return true;
            }

            var candidate = candidates[0];
            var exact = IsExactIngressTarget(
                candidate.Config,
                dnsName,
                expectedTarget,
                httpsPort,
                requireFunnel);
            analysis = new ServeConfigAnalysis(
                true,
                exact,
                exact && candidate.Location == ServeConfigLocation.ForegroundSession,
                exact && candidate.Location == ServeConfigLocation.ForegroundSession
                    ? candidate.ForegroundSessionId
                    : null);
            return true;
        }
        catch (JsonException exception)
        {
            error = $"無法解析 Tailscale Serve status JSON：{exception.Message}";
            return false;
        }
    }

    private static JsonDocument ParseJson(string json)
        => JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });

    private static bool TryCollectNodeHttpsPortCandidates(
        JsonElement root,
        int httpsPort,
        List<ServeConfigCandidate> candidates,
        out string? error)
    {
        error = null;
        if (!TryValidateServeConfigProperties(
                root,
                allowForegroundAndServices: true,
                "根節點",
                out error)
            || !TryHasDirectHttpsPortConfiguration(root, httpsPort, out var rootHasHttps, out error))
        {
            return false;
        }

        if (rootHasHttps)
        {
            candidates.Add(new ServeConfigCandidate(
                root,
                ServeConfigLocation.TopLevel,
                null));
        }

        if (TryGetPropertyIgnoreCase(root, "Services", out var services)
            && services.ValueKind != JsonValueKind.Object)
        {
            error = "Tailscale Serve status JSON 的 Services 不是物件。";
            return false;
        }

        if (!TryGetPropertyIgnoreCase(root, "Foreground", out var foreground))
        {
            return true;
        }

        if (foreground.ValueKind != JsonValueKind.Object)
        {
            error = "Tailscale Serve status JSON 的 Foreground 不是 session 物件。";
            return false;
        }

        var sessionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in foreground.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(session.Name)
                || !sessionIds.Add(session.Name))
            {
                error = "Tailscale Serve status JSON 含空白或重複的 Foreground session ID。";
                return false;
            }

            if (session.Value.ValueKind != JsonValueKind.Object
                || !TryValidateServeConfigProperties(
                    session.Value,
                    allowForegroundAndServices: false,
                    $"Foreground session '{session.Name}'",
                    out error)
                || !TryHasDirectHttpsPortConfiguration(
                    session.Value,
                    httpsPort,
                    out var sessionHasHttps,
                    out error))
            {
                error ??= $"Tailscale Serve Foreground session '{session.Name}' 格式不受支援。";
                return false;
            }

            if (sessionHasHttps)
            {
                candidates.Add(new ServeConfigCandidate(
                    session.Value,
                    ServeConfigLocation.ForegroundSession,
                    session.Name));
            }
        }

        return true;
    }

    private static bool TryValidateServeConfigProperties(
        JsonElement element,
        bool allowForegroundAndServices,
        string context,
        out string? error)
    {
        error = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"Tailscale Serve status JSON 的 {context} 不是物件。";
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                error = $"Tailscale Serve status JSON 的 {context} 含重複欄位 '{property.Name}'。";
                return false;
            }

            var knownNodeProperty = property.Name.Equals("TCP", StringComparison.OrdinalIgnoreCase)
                                    || property.Name.Equals("Web", StringComparison.OrdinalIgnoreCase)
                                    || property.Name.Equals(
                                        "AllowFunnel",
                                        StringComparison.OrdinalIgnoreCase);
            var knownRootProperty = allowForegroundAndServices
                                    && (property.Name.Equals(
                                            "Foreground",
                                            StringComparison.OrdinalIgnoreCase)
                                        || property.Name.Equals(
                                            "Services",
                                            StringComparison.OrdinalIgnoreCase));
            if (!knownNodeProperty && !knownRootProperty)
            {
                error = $"Tailscale Serve status JSON 的 {context} 含不支援欄位 '{property.Name}'；為避免誤判 route 不存在，已拒絕處理。";
                return false;
            }
        }

        return true;
    }

    private static bool TryHasDirectHttpsPortConfiguration(
        JsonElement element,
        int httpsPort,
        out bool hasHttps,
        out string? error)
    {
        hasHttps = false;
        error = null;
        if (TryGetPropertyIgnoreCase(element, "TCP", out var tcp))
        {
            if (tcp.ValueKind != JsonValueKind.Object)
            {
                error = "Tailscale Serve status JSON 的 TCP 不是物件。";
                return false;
            }

            foreach (var portEntry in tcp.EnumerateObject())
            {
                if (!ushort.TryParse(
                        portEntry.Name,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var port))
                {
                    error = $"Tailscale Serve status JSON 含無效 TCP port '{portEntry.Name}'。";
                    return false;
                }

                hasHttps |= port == httpsPort;
            }
        }

        if (TryGetPropertyIgnoreCase(element, "Web", out var web))
        {
            if (web.ValueKind != JsonValueKind.Object)
            {
                error = "Tailscale Serve status JSON 的 Web 不是物件。";
                return false;
            }

            foreach (var entry in web.EnumerateObject())
            {
                if (!TryParseHostPort(entry.Name, out _, out var port))
                {
                    error = $"Tailscale Serve status JSON 含無效 Web host:port '{entry.Name}'。";
                    return false;
                }

                hasHttps |= port == httpsPort;
            }
        }

        if (TryGetPropertyIgnoreCase(element, "AllowFunnel", out var allowFunnel))
        {
            if (allowFunnel.ValueKind != JsonValueKind.Object)
            {
                error = "Tailscale Serve status JSON 的 AllowFunnel 不是物件。";
                return false;
            }

            foreach (var entry in allowFunnel.EnumerateObject())
            {
                if (!TryParseHostPort(entry.Name, out _, out var port)
                    || entry.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = $"Tailscale Serve status JSON 含無效 AllowFunnel 項目 '{entry.Name}'。";
                    return false;
                }

                hasHttps |= port == httpsPort && entry.Value.GetBoolean();
            }
        }

        return true;
    }

    private static bool IsExactIngressTarget(
        JsonElement config,
        string? dnsName,
        string expectedTarget,
        int httpsPort,
        bool requireFunnel)
    {
        var normalizedDnsName = NormalizeDnsName(dnsName);
        var configProperties = config.EnumerateObject().ToArray();
        if (normalizedDnsName is null
            || configProperties.Length is < 2 or > 3
            || configProperties.Any(property =>
                !property.Name.Equals("TCP", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("Web", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("AllowFunnel", StringComparison.OrdinalIgnoreCase))
            || !TryGetPropertyIgnoreCase(config, "TCP", out var tcp)
            || tcp.ValueKind != JsonValueKind.Object
            || tcp.EnumerateObject().Count() != 1
            || !TryGetPropertyIgnoreCase(tcp, httpsPort.ToString(), out var portHandler)
            || portHandler.ValueKind != JsonValueKind.Object
            || portHandler.EnumerateObject().Count() != 1
            || !TryGetBooleanProperty(portHandler, "HTTPS", out var https)
            || !https)
        {
            return false;
        }

        if (requireFunnel)
        {
            if (!TryGetPropertyIgnoreCase(config, "AllowFunnel", out var allowFunnel)
                || allowFunnel.ValueKind != JsonValueKind.Object
                || allowFunnel.EnumerateObject().Count() != 1
                || !allowFunnel.EnumerateObject().All(entry =>
                    TryParseHostPort(entry.Name, out var host, out var port)
                    && port == httpsPort
                    && string.Equals(NormalizeDnsName(host), normalizedDnsName, StringComparison.OrdinalIgnoreCase)
                    && entry.Value.ValueKind == JsonValueKind.True
                    && entry.Value.GetBoolean()))
            {
                return false;
            }
        }
        else if (TryGetPropertyIgnoreCase(config, "AllowFunnel", out var allowFunnel)
                 && (allowFunnel.ValueKind != JsonValueKind.Object
                     || allowFunnel.EnumerateObject().Any(entry =>
                         entry.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                         || entry.Value.GetBoolean())))
        {
            return false;
        }

        if (!TryGetPropertyIgnoreCase(config, "Web", out var web)
            || web.ValueKind != JsonValueKind.Object
            || web.EnumerateObject().Count() != 1)
        {
            return false;
        }

        JsonElement expectedWeb = default;
        var matchingPortEntries = 0;
        foreach (var property in web.EnumerateObject())
        {
            if (!TryParseHostPort(property.Name, out var host, out var port)
                || port != httpsPort)
            {
                continue;
            }

            matchingPortEntries++;
            if (string.Equals(
                    NormalizeDnsName(host),
                    normalizedDnsName,
                    StringComparison.OrdinalIgnoreCase))
            {
                expectedWeb = property.Value;
            }
        }

        if (matchingPortEntries != 1
            || expectedWeb.ValueKind != JsonValueKind.Object
            || expectedWeb.EnumerateObject().Count() != 1
            || !TryGetPropertyIgnoreCase(expectedWeb, "Handlers", out var handlers)
            || handlers.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var handlerProperties = handlers.EnumerateObject().ToArray();
        if (handlerProperties.Length != 1
            || !string.Equals(handlerProperties[0].Name, "/", StringComparison.Ordinal)
            || handlerProperties[0].Value.ValueKind != JsonValueKind.Object
            || handlerProperties[0].Value.EnumerateObject().Count() != 1
            || !TryGetPropertyIgnoreCase(handlerProperties[0].Value, "Proxy", out var proxy)
            || proxy.ValueKind != JsonValueKind.String
            || !string.Equals(proxy.GetString(), expectedTarget, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetBooleanProperty(
        JsonElement element,
        string name,
        out bool value)
    {
        if (TryGetPropertyIgnoreCase(element, name, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryParseHostPort(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        host = value[..separator];
        return int.TryParse(
                   value.AsSpan(separator + 1),
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out port)
               && port is >= 1 and <= 65_535;
    }

    private static string? NormalizeDnsName(string? dnsName)
    {
        var normalized = dnsName?.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool TryCreateServeUrl(string? dnsName, int httpsPort, out Uri? url)
    {
        url = null;
        var host = NormalizeDnsName(dnsName);
        if (host is null
            || !host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
            || Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            return false;
        }

        return Uri.TryCreate(
            httpsPort == 443 ? $"https://{host}/" : $"https://{host}:{httpsPort}/",
            UriKind.Absolute,
            out url);
    }

    private static string CreateTarget(int localPort)
        => $"http://127.0.0.1:{localPort}";

    private static void ValidateLocalPort(int localPort)
    {
        if (localPort is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localPort),
                "Local web-server port must be between 1 and 65535.");
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private string IngressCommand
        => _ingressKind == TailscaleHttpsIngressKind.Funnel ? "funnel" : "serve";

    private string IngressDisplayName
        => _ingressKind == TailscaleHttpsIngressKind.Funnel ? "Funnel" : "Serve";

    private readonly record struct ServeConfigCandidate(
        JsonElement Config,
        ServeConfigLocation Location,
        string? ForegroundSessionId);

    private enum ServeConfigLocation
    {
        TopLevel,
        ForegroundSession
    }

    private readonly record struct ServeConfigAnalysis(
        bool HasAnyHttpsPortConfiguration,
        bool IsExactPrivateTarget,
        bool IsExactForegroundTarget,
        string? ExactForegroundSessionId);

    private readonly record struct ForegroundStopResult(
        bool ExitConfirmed,
        int? ExitCode,
        string StandardOutput,
        string StandardError);
}

/// <summary>
/// Owns one foreground public HTTPS Funnel route on 443. It deliberately does not use
/// <c>--bg</c>: closing MCSV closes the owning child and the route must then disappear.
/// </summary>
internal sealed class TailscaleFunnelService : ITailscaleServeService
{
    internal const int FunnelHttpsPort = 443;

    private readonly TailscaleServeService _inner;

    public TailscaleFunnelService()
        : this(
            new TailscaleExecutableLocator(),
            new SystemTailscaleCommandRunner(),
            new SystemTailscaleForegroundProcessFactory(),
            new SystemTailscaleDelay())
    {
    }

    internal TailscaleFunnelService(
        ITailscaleExecutableLocator executableLocator,
        ITailscaleCommandRunner commandRunner,
        ITailscaleForegroundProcessFactory foregroundProcessFactory,
        ITailscaleDelay delay,
        TimeSpan? commandTimeout = null,
        int startupProbeAttempts = TailscaleServeService.DefaultStartupProbeAttempts,
        TimeSpan? startupProbeInterval = null,
        TimeSpan? operationTimeout = null)
    {
        _inner = new TailscaleServeService(
            executableLocator,
            commandRunner,
            foregroundProcessFactory,
            delay,
            commandTimeout,
            startupProbeAttempts,
            startupProbeInterval,
            operationTimeout,
            TailscaleHttpsIngressKind.Funnel);
        _inner.ForegroundProcessExited += OnInnerForegroundProcessExited;
    }

    public event EventHandler<TailscaleRouteProcessExitedEventArgs>? ForegroundProcessExited;

    public Task<TailscaleServeStatus> GetStatusAsync(
        int localPort,
        CancellationToken cancellationToken = default)
        => _inner.GetStatusAsync(localPort, cancellationToken);

    public Task<TailscaleServeOperationResult> EnableAsync(
        int localPort,
        CancellationToken cancellationToken = default)
        => _inner.EnableAsync(localPort, cancellationToken);

    public Task<TailscaleServeOperationResult> DisableAsync(
        int localPort,
        CancellationToken cancellationToken = default)
        => _inner.DisableAsync(localPort, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _inner.ForegroundProcessExited -= OnInnerForegroundProcessExited;
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    private void OnInnerForegroundProcessExited(
        object? sender,
        TailscaleRouteProcessExitedEventArgs fault)
    {
        foreach (var observer in ForegroundProcessExited?.GetInvocationList()
                     .Cast<EventHandler<TailscaleRouteProcessExitedEventArgs>>()
                 ?? [])
        {
            try
            {
                observer(this, fault);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The public Funnel wrapper preserves the inner service's observer isolation.
            }
        }
    }
}

internal sealed class TailscaleExecutableLocator : ITailscaleExecutableLocator
{
    internal const string ProgramFilesExecutablePath = @"C:\Program Files\Tailscale\tailscale.exe";

    private readonly Func<string?> _readPath;
    private readonly Func<string, bool> _fileExists;

    public TailscaleExecutableLocator()
        : this(
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists)
    {
    }

    internal TailscaleExecutableLocator(
        Func<string?> readPath,
        Func<string, bool> fileExists)
    {
        _readPath = readPath ?? throw new ArgumentNullException(nameof(readPath));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    }

    public string? FindExecutable()
    {
        // Prefer the vendor's fixed machine-wide install over PATH to reduce binary-planting
        // exposure. PATH remains the only supported fallback discovery mechanism.
        if (_fileExists(ProgramFilesExecutablePath))
        {
            return ProgramFilesExecutablePath;
        }

        var pathValue = _readPath();
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var entry in pathValue.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var directory = entry.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(directory))
                {
                    continue;
                }

                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(directory, "tailscale.exe"));
                    if (_fileExists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception exception) when (exception is
                    ArgumentException or
                    NotSupportedException or
                    PathTooLongException)
                {
                    // Ignore malformed PATH entries. No other discovery mechanism is used.
                }
            }
        }

        return null;
    }
}

internal sealed class SystemTailscaleDelay : ITailscaleDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}

internal sealed class SystemTailscaleCommandRunner : ITailscaleCommandRunner
{
    internal const int MaximumCapturedCharactersPerStream = 128 * 1024;

    public async Task<TailscaleCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(executablePath, arguments),
            EnableRaisingEvents = true
        };
        if (!process.Start())
        {
            return new TailscaleCommandResult(
                null,
                string.Empty,
                "The operating system did not start tailscale.exe.");
        }

        var stdout = new BoundedTextCapture(MaximumCapturedCharactersPerStream);
        var stderr = new BoundedTextCapture(MaximumCapturedCharactersPerStream);
        using var readCancellation = new CancellationTokenSource();
        var stdoutTask = ProcessIo.DrainAsync(
            process.StandardOutput,
            stdout,
            readCancellation.Token);
        var stderrTask = ProcessIo.DrainAsync(
            process.StandardError,
            stderr,
            readCancellation.Token);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            ProcessIo.TryKillProcessTree(process);
            readCancellation.Cancel();
        }
        catch (OperationCanceledException)
        {
            ProcessIo.TryKillProcessTree(process);
            readCancellation.Cancel();
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return timedOut
            ? new TailscaleCommandResult(
                null,
                stdout.Snapshot(),
                stderr.Snapshot(),
                TimedOut: true)
            : new TailscaleCommandResult(
                process.ExitCode,
                stdout.Snapshot(),
                stderr.Snapshot());
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };
        foreach (var argument in arguments)
        {
            if (argument is null
                || argument.Contains('\0')
                || argument.Contains('\r')
                || argument.Contains('\n'))
            {
                throw new ArgumentException(
                    "Tailscale command arguments cannot contain nulls or newlines.",
                    nameof(arguments));
            }

            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

internal sealed class SystemTailscaleForegroundProcessFactory : ITailscaleForegroundProcessFactory
{
    public async Task<ITailscaleForegroundProcess> StartAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Tailscale foreground ownership requires a Windows Kill-on-close Job.");
        }

        var job = WindowsKillOnCloseJob.Create();
        var process = new Process
        {
            StartInfo = SystemTailscaleCommandRunner.CreateStartInfo(executablePath, arguments),
            EnableRaisingEvents = true
        };
        SystemTailscaleForegroundProcess? ownedProcess = null;
        try
        {
            if (!process.Start())
            {
                throw new Win32Exception("The operating system did not start tailscale.exe.");
            }

            ownedProcess = new SystemTailscaleForegroundProcess(process, job);
            WindowsKillOnCloseJob.Assign(job, process);
            return ownedProcess;
        }
        catch
        {
            if (ownedProcess is not null)
            {
                await ownedProcess.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                ProcessIo.TryKillProcessTree(process);
                process.Dispose();
                job.Dispose();
            }

            throw;
        }
    }
}

internal sealed class SystemTailscaleForegroundProcess : ITailscaleForegroundProcess
{
    private readonly Process _process;
    private readonly SafeJobHandle _job;
    private readonly BoundedTextCapture _stdout = new(
        SystemTailscaleCommandRunner.MaximumCapturedCharactersPerStream);
    private readonly BoundedTextCapture _stderr = new(
        SystemTailscaleCommandRunner.MaximumCapturedCharactersPerStream);
    private readonly CancellationTokenSource _readCancellation = new();
    private readonly TaskCompletionSource<bool> _exitSignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stdoutTask;
    private readonly Task _stderrTask;
    private readonly int _processId;
    private int? _exitCode;
    private int _disposeStarted;

    public SystemTailscaleForegroundProcess(Process process, SafeJobHandle job)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _job = job ?? throw new ArgumentNullException(nameof(job));
        _processId = process.Id;
        _stdoutTask = ProcessIo.DrainAsync(
            process.StandardOutput,
            _stdout,
            _readCancellation.Token);
        _stderrTask = ProcessIo.DrainAsync(
            process.StandardError,
            _stderr,
            _readCancellation.Token);
        _process.Exited += OnExited;
        try
        {
            if (_process.HasExited)
            {
                CaptureExitAndComplete();
            }
        }
        catch (InvalidOperationException)
        {
            _exitSignal.TrySetResult(true);
        }
    }

    public int ProcessId => _processId;

    public bool HasExited
    {
        get
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                return true;
            }

            try
            {
                var exited = _process.HasExited;
                if (exited)
                {
                    _exitCode = _process.ExitCode;
                    _exitSignal.TrySetResult(true);
                }

                return exited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            _ = HasExited;
            return _exitCode;
        }
    }

    public string StandardOutput => _stdout.Snapshot();

    public string StandardError => _stderr.Snapshot();

    public Task Completion => _exitSignal.Task;

    public async Task<bool> WaitForExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (HasExited)
        {
            return true;
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            _exitCode = _process.ExitCode;
            _exitSignal.TrySetResult(true);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HasExited;
        }
    }

    public void KillEntireProcessTree() => _process.Kill(entireProcessTree: true);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        if (!_process.HasExited)
        {
            ProcessIo.TryKillProcessTree(_process);
        }

        // Closing this handle terminates every process assigned to the owned Job, including on
        // parent-process teardown when normal Dispose cannot run.
        _job.Dispose();
        using var exitTimeout = new CancellationTokenSource(
            TailscaleServeService.ForcedExitTimeout);
        try
        {
            await _process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
            _exitCode = _process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // The Job handle has already been closed; do not block application shutdown.
        }
        catch (InvalidOperationException)
        {
        }

        _readCancellation.Cancel();
        await Task.WhenAll(_stdoutTask, _stderrTask).ConfigureAwait(false);
        _readCancellation.Dispose();
        CaptureExitAndComplete();
        _process.Exited -= OnExited;
        _process.Dispose();
    }

    private void OnExited(object? sender, EventArgs e) => CaptureExitAndComplete();

    private void CaptureExitAndComplete()
    {
        try
        {
            if (_process.HasExited)
            {
                _exitCode = _process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
        }

        _exitSignal.TrySetResult(true);
    }

}

internal static class WindowsKillOnCloseJob
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    public static SafeJobHandle Create()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        if (!SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformationClass,
                ref information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            handle.Dispose();
            throw error;
        }

        return handle;
    }

    public static void Assign(SafeJobHandle job, Process process)
    {
        if (!AssignProcessToJobObject(job, process.Handle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to assign tailscale.exe to the Kill-on-close Job.");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(
        IntPtr jobAttributes,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeJobHandle job,
        IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed class BoundedTextCapture(int maximumCharacters)
{
    private readonly object _gate = new();
    private readonly StringBuilder _builder = new(Math.Min(maximumCharacters, 4 * 1024));

    public void Append(char[] buffer, int count)
    {
        lock (_gate)
        {
            var remaining = maximumCharacters - _builder.Length;
            if (remaining > 0)
            {
                _builder.Append(buffer, 0, Math.Min(count, remaining));
            }
        }
    }

    public string Snapshot()
    {
        lock (_gate)
        {
            return _builder.ToString();
        }
    }
}

internal static class ProcessIo
{
    public static async Task DrainAsync(
        StreamReader reader,
        BoundedTextCapture capture,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4 * 1024];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                capture.Append(buffer, read);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            NotSupportedException or
            Win32Exception)
        {
        }
    }
}
