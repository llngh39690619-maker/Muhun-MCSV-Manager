using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Owns one remotely-managed Cloudflare Named Tunnel connector. The public route is configured
/// in Cloudflare's dashboard and always targets the loopback-only MCSV web host. The connector
/// token is supplied only in the child's environment and is never placed in arguments, config,
/// logs, or a Windows service.
/// </summary>
internal sealed class CloudflareNamedTunnelService : IWebTunnelService
{
    internal const string TunnelTokenEnvironmentVariable = "TUNNEL_TOKEN";
    internal const int MaximumLogEntries = CloudflareQuickTunnelService.MaximumLogEntries;
    internal const int MaximumLogLineCharacters = CloudflareQuickTunnelService.MaximumLogLineCharacters;
    internal static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex RegisteredConnectionRegex = new(
        @"(?i)(?<![A-Za-z])Registered\s+tunnel\s+connection(?![A-Za-z])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private readonly string _executablePath;
    private readonly Uri _publicOrigin;
    private readonly string _tunnelToken;
    private readonly ICloudflareQuickTunnelProcessFactory _processFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _stopTimeout;
    private readonly Func<string, string> _versionReader;
    private readonly BoundedRedactedWebTunnelLog _logs;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateGate = new();

    private WebTunnelLifecycleState _state = WebTunnelLifecycleState.Stopped;
    private ICloudflareQuickTunnelProcess? _activeProcess;
    private Uri? _activePublicUrl;
    private DateTimeOffset? _startedAtUtc;
    private string? _error;
    private string _executableVersion;
    private int? _localPort;
    private long _generation;
    private Task _monitorTask = Task.CompletedTask;
    private bool _disposed;

    public CloudflareNamedTunnelService(
        string executablePath,
        Uri publicOrigin,
        string tunnelToken)
        : this(
            executablePath,
            publicOrigin,
            tunnelToken,
            new SystemCloudflareQuickTunnelProcessFactory(),
            TimeProvider.System,
            DefaultStartupTimeout,
            DefaultStopTimeout,
            ReadExecutableVersion)
    {
    }

    internal CloudflareNamedTunnelService(
        string executablePath,
        Uri publicOrigin,
        string tunnelToken,
        ICloudflareQuickTunnelProcessFactory processFactory,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? stopTimeout = null,
        Func<string, string>? versionReader = null)
    {
        _executablePath = ValidateExecutablePath(executablePath);
        _publicOrigin = ValidatePublicOrigin(publicOrigin);
        _tunnelToken = ValidateTunnelToken(tunnelToken);
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startupTimeout = startupTimeout ?? DefaultStartupTimeout;
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
        _versionReader = versionReader ?? ReadExecutableVersion;

        if (_startupTimeout <= TimeSpan.Zero ||
            _startupTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        }

        if (_stopTimeout <= TimeSpan.Zero || _stopTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(stopTimeout));
        }

        _executableVersion = SanitizeVersion(_versionReader(_executablePath));
        _logs = new BoundedRedactedWebTunnelLog(
            MaximumLogEntries,
            MaximumLogLineCharacters,
            _timeProvider);
    }

    public event EventHandler<WebTunnelSnapshot>? StateChanged;

    public WebTunnelSnapshot Snapshot => CreateSnapshot();

    public async Task<WebTunnelSnapshot> StartAsync(
        int localPort,
        CancellationToken cancellationToken = default)
    {
        ValidatePort(localPort);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeProcess is { HasExited: false } &&
                _state == WebTunnelLifecycleState.Running)
            {
                if (_localPort != localPort)
                {
                    throw new InvalidOperationException(
                        "Named Tunnel 已在另一個本機 Port 執行；請先停止再重新啟動。");
                }

                return CreateSnapshot();
            }

            if (_activeProcess is not null)
            {
                await TerminateProcessAsync(_activeProcess).ConfigureAwait(false);
                _activeProcess = null;
            }

            if (!IsExecutableFile(_executablePath))
            {
                return SetState(
                    WebTunnelLifecycleState.Faulted,
                    null,
                    null,
                    "指定的 cloudflared.exe 已不存在或不再是一般檔案。");
            }

            _executableVersion = SanitizeVersion(_versionReader(_executablePath));
            _localPort = localPort;
            _activePublicUrl = null;
            _startedAtUtc = null;
            _error = null;
            var generation = Interlocked.Increment(ref _generation);
            var attempt = new StartupAttempt();
            SetState(WebTunnelLifecycleState.Starting, null, null, null);
            AddLog(WebTunnelLogChannel.Service, "正在啟動 Cloudflare Named Tunnel。");

            ICloudflareQuickTunnelProcess process;
            try
            {
                process = await _processFactory.StartAsync(
                        CreateStartInfo(_executablePath, _tunnelToken),
                        line => HandleProcessLine(generation, attempt, line),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetState(WebTunnelLifecycleState.Stopped, null, null, null);
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var message = $"無法啟動 cloudflared.exe：{SanitizeNamedError(exception.Message)}";
                AddLog(WebTunnelLogChannel.Service, message);
                return SetState(WebTunnelLifecycleState.Faulted, null, null, message);
            }

            _activeProcess = process;
            _startedAtUtc = _timeProvider.GetUtcNow();

            try
            {
                var timeout = Task.Delay(
                    _startupTimeout,
                    _timeProvider,
                    cancellationToken);
                var completed = await Task.WhenAny(
                        process.Completion,
                        attempt.ConnectionRegistered.Task,
                        timeout)
                    .ConfigureAwait(false);
                if (ReferenceEquals(completed, process.Completion))
                {
                    var exitCode = await process.Completion.ConfigureAwait(false);
                    return await FailStartAsync(
                            process,
                            $"cloudflared.exe 在確認 Named Tunnel 連線前結束（ExitCode={exitCode}）。")
                        .ConfigureAwait(false);
                }

                if (ReferenceEquals(completed, timeout))
                {
                    await timeout.ConfigureAwait(false);
                    return await FailStartAsync(
                            process,
                            $"cloudflared.exe 未在 {_startupTimeout.TotalSeconds:0.#} 秒內確認 Named Tunnel 連線。")
                        .ConfigureAwait(false);
                }

                await attempt.ConnectionRegistered.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TerminateAndClearAsync(process).ConfigureAwait(false);
                SetState(WebTunnelLifecycleState.Stopped, null, null, null);
                throw;
            }

            if (process.HasExited)
            {
                var exitCode = await process.Completion.ConfigureAwait(false);
                return await FailStartAsync(
                        process,
                        $"cloudflared.exe 在 Named Tunnel 啟用前結束（ExitCode={exitCode}）。")
                    .ConfigureAwait(false);
            }

            _activePublicUrl = _publicOrigin;
            AddLog(
                WebTunnelLogChannel.Service,
                $"Named Tunnel 已啟動：{_publicOrigin.GetLeftPart(UriPartial.Authority)}");
            var running = SetState(
                WebTunnelLifecycleState.Running,
                _publicOrigin,
                _startedAtUtc,
                null,
                process.ProcessId);
            _monitorTask = MonitorProcessAsync(process, generation);
            return running;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<WebTunnelSnapshot> StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task monitor;
        try
        {
            if (_disposed && _activeProcess is null)
            {
                return CreateSnapshot();
            }

            Interlocked.Increment(ref _generation);
            var process = _activeProcess;
            _activeProcess = null;
            _activePublicUrl = null;
            if (process is null && _state == WebTunnelLifecycleState.Stopped)
            {
                return CreateSnapshot();
            }

            SetState(WebTunnelLifecycleState.Stopping, null, _startedAtUtc, null);
            if (process is not null)
            {
                await TerminateProcessAsync(process).ConfigureAwait(false);
            }

            _startedAtUtc = null;
            _localPort = null;
            AddLog(WebTunnelLogChannel.Service, "Cloudflare Named Tunnel 已停止。");
            var stopped = SetState(WebTunnelLifecycleState.Stopped, null, null, null);
            monitor = _monitorTask;
            _monitorTask = Task.CompletedTask;

            try
            {
                await monitor.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Stop already owns and disposed the connector; monitor failures are diagnostic.
            }

            return stopped;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
        }
        finally
        {
            _operationGate.Release();
        }

        await StopAsync().ConfigureAwait(false);
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string tunnelToken)
    {
        var validatedPath = ValidateExecutablePath(executablePath);
        var validatedToken = ValidateTunnelToken(tunnelToken);
        var startInfo = new ProcessStartInfo
        {
            FileName = validatedPath,
            WorkingDirectory = Path.GetDirectoryName(validatedPath)
                               ?? throw new InvalidOperationException(
                                   "cloudflared.exe 缺少有效的父目錄。"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var requiredEnvironment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
            ["TEMP"] = Environment.GetEnvironmentVariable("TEMP"),
            ["TMP"] = Environment.GetEnvironmentVariable("TMP")
        };
        startInfo.Environment.Clear();
        foreach (var pair in requiredEnvironment)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        startInfo.Environment[TunnelTokenEnvironmentVariable] = validatedToken;
        startInfo.ArgumentList.Add("tunnel");
        startInfo.ArgumentList.Add("--no-autoupdate");
        startInfo.ArgumentList.Add("run");
        return startInfo;
    }

    private async Task<WebTunnelSnapshot> FailStartAsync(
        ICloudflareQuickTunnelProcess process,
        string message)
    {
        AddLog(WebTunnelLogChannel.Service, message);
        await TerminateAndClearAsync(process).ConfigureAwait(false);
        return SetState(WebTunnelLifecycleState.Faulted, null, null, message);
    }

    private async Task TerminateAndClearAsync(ICloudflareQuickTunnelProcess process)
    {
        if (ReferenceEquals(_activeProcess, process))
        {
            _activeProcess = null;
        }

        _activePublicUrl = null;
        _startedAtUtc = null;
        _localPort = null;
        await TerminateProcessAsync(process).ConfigureAwait(false);
    }

    private async Task TerminateProcessAsync(ICloudflareQuickTunnelProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.KillEntireProcessTree();
            }

            try
            {
                _ = await process.Completion.WaitAsync(_stopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                AddLog(
                    WebTunnelLogChannel.Service,
                    "等待 cloudflared.exe 結束逾時；正在關閉 Kill-on-close Job。");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AddLog(
                WebTunnelLogChannel.Service,
                $"終止 cloudflared.exe 時收到非致命錯誤：{SanitizeNamedError(exception.Message)}");
        }
        finally
        {
            try
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AddLog(
                    WebTunnelLogChannel.Service,
                    $"釋放 cloudflared.exe 時收到非致命錯誤：{SanitizeNamedError(exception.Message)}");
            }
        }
    }

    private async Task MonitorProcessAsync(
        ICloudflareQuickTunnelProcess process,
        long generation)
    {
        int exitCode;
        try
        {
            exitCode = await process.Completion.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            exitCode = int.MinValue;
            AddLog(
                WebTunnelLogChannel.Service,
                $"監看 cloudflared.exe 時發生錯誤：{SanitizeNamedError(exception.Message)}");
        }

        var message = exitCode == int.MinValue
            ? "Cloudflare Named Tunnel 子程序已意外失效。"
            : $"cloudflared.exe 已意外結束（ExitCode={exitCode}）。";
        WebTunnelSnapshot? faulted = null;
        lock (_stateGate)
        {
            if (generation == Volatile.Read(ref _generation) &&
                ReferenceEquals(_activeProcess, process))
            {
                AddLog(WebTunnelLogChannel.Service, message);
                _activeProcess = null;
                _activePublicUrl = null;
                _startedAtUtc = null;
                _localPort = null;
                _state = WebTunnelLifecycleState.Faulted;
                _error = message;
                faulted = CreateSnapshotUnsafe();
            }
        }

        if (faulted is null) return;

        // Publish before the asynchronous cleanup so a subsequent Start cannot be followed by
        // a stale Faulted notification from this generation.
        PublishStateChanged(faulted);

        try
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AddLog(
                WebTunnelLogChannel.Service,
                $"釋放 cloudflared.exe 時收到非致命錯誤：{SanitizeNamedError(exception.Message)}");
        }
    }

    private void HandleProcessLine(
        long generation,
        StartupAttempt attempt,
        CloudflareQuickTunnelProcessLine line)
    {
        if (generation != Volatile.Read(ref _generation)) return;
        AddLog(line.Channel, line.Text);
        try
        {
            if (RegisteredConnectionRegex.IsMatch(line.Text))
            {
                attempt.ConnectionRegistered.TrySetResult();
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // An oversized or hostile diagnostic line can never establish readiness.
        }
    }

    private WebTunnelSnapshot SetState(
        WebTunnelLifecycleState state,
        Uri? publicUrl,
        DateTimeOffset? startedAtUtc,
        string? error,
        int? processIdOverride = null)
    {
        WebTunnelSnapshot snapshot;
        lock (_stateGate)
        {
            _state = state;
            _activePublicUrl = publicUrl;
            _startedAtUtc = startedAtUtc;
            _error = error;
            snapshot = CreateSnapshotUnsafe(processIdOverride);
        }

        PublishStateChanged(snapshot);
        return snapshot;
    }

    private void PublishStateChanged(WebTunnelSnapshot snapshot)
    {
        var observers = StateChanged?.GetInvocationList()
            .Cast<EventHandler<WebTunnelSnapshot>>()
            .ToArray() ?? [];
        foreach (var observer in observers)
        {
            try
            {
                observer(this, snapshot);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AddLog(
                    WebTunnelLogChannel.Service,
                    $"Tunnel 狀態通知接收端發生錯誤：{SanitizeNamedError(exception.Message)}");
            }
        }
    }

    private WebTunnelSnapshot CreateSnapshot()
    {
        lock (_stateGate)
        {
            return CreateSnapshotUnsafe();
        }
    }

    private WebTunnelSnapshot CreateSnapshotUnsafe(int? processIdOverride = null)
    {
        TimeSpan? runningFor = _startedAtUtc is { } startedAt
            ? _timeProvider.GetUtcNow() - startedAt
            : null;
        return new WebTunnelSnapshot(
            _state,
            _activePublicUrl,
            processIdOverride ?? _activeProcess?.ProcessId,
            _executableVersion,
            _startedAtUtc,
            runningFor,
            _error,
            _logs.Snapshot());
    }

    private static Uri ValidatePublicOrigin(Uri publicOrigin)
    {
        ArgumentNullException.ThrowIfNull(publicOrigin);
        var options = new RemoteControlOptions
        {
            PublicOrigin = publicOrigin,
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.CloudflareNamedTunnel
        };
        var errors = RemoteControlOptionsValidator.Validate(options);
        if (errors.Count != 0)
        {
            throw new ArgumentException(
                "Named Tunnel 固定網址必須是有效的公開 HTTPS Origin。",
                nameof(publicOrigin));
        }

        return new Uri(publicOrigin.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }

    private static string ValidateTunnelToken(string tunnelToken)
    {
        if (!RemoteSecurityStore.TryNormalizeCloudflareNamedTunnelToken(
                tunnelToken,
                out var normalized) ||
            !string.Equals(tunnelToken, normalized, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Cloudflare Named Tunnel Token 格式無效。",
                nameof(tunnelToken));
        }

        return normalized;
    }

    private static string ValidateExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            executablePath != executablePath.Trim() ||
            !Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "cloudflared.exe 必須由呼叫端指定完整絕對路徑。",
                nameof(executablePath));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            throw new ArgumentException("cloudflared.exe 路徑無效。", nameof(executablePath), exception);
        }

        if (!string.Equals(
                Path.GetFileName(fullPath),
                "cloudflared.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "指定檔案名稱必須是 cloudflared.exe。",
                nameof(executablePath));
        }

        if (!IsExecutableFile(fullPath))
        {
            throw new FileNotFoundException(
                "找不到一般檔案 cloudflared.exe，或該路徑是連結／目錄。",
                fullPath);
        }

        return fullPath;
    }

    private static bool IsExecutableFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists &&
                   (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return false;
        }
    }

    private static void ValidatePort(int localPort)
    {
        if (localPort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localPort),
                "本機 Named Tunnel 目標 Port 必須介於 1024–65535。");
        }
    }

    private static string ReadExecutableVersion(string executablePath)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(executablePath);
            return version.ProductVersion ?? version.FileVersion ?? "unknown";
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or
            Win32Exception or
            ArgumentException)
        {
            return "unknown";
        }
    }

    private static string SanitizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "unknown";
        var normalized = BoundedRedactedWebTunnelLog.Redact(version);
        return normalized.Length <= 128 ? normalized : normalized[..128] + "…";
    }

    private static string SanitizeError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "未知錯誤。";
        var normalized = BoundedRedactedWebTunnelLog.Redact(message);
        return normalized.Length <= 500 ? normalized : normalized[..500] + "…";
    }

    private void AddLog(WebTunnelLogChannel channel, string? message)
        => _logs.Add(channel, RedactNamedTunnelToken(message));

    private string SanitizeNamedError(string? message)
        => SanitizeError(RedactNamedTunnelToken(message));

    private string RedactNamedTunnelToken(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        return message.Replace(
            _tunnelToken,
            "[REDACTED-TOKEN]",
            StringComparison.Ordinal);
    }

    private sealed class StartupAttempt
    {
        public TaskCompletionSource ConnectionRegistered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
