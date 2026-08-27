using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.App.Services;

internal readonly record struct CloudflareQuickTunnelProcessLine(
    WebTunnelLogChannel Channel,
    string Text);

internal interface ICloudflareQuickTunnelProcess : IAsyncDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
    Task<int> Completion { get; }

    void KillEntireProcessTree();
}

internal interface ICloudflareQuickTunnelProcessFactory
{
    Task<ICloudflareQuickTunnelProcess> StartAsync(
        ProcessStartInfo startInfo,
        Action<CloudflareQuickTunnelProcessLine> outputSink,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns one development-only TryCloudflare connector. The origin remains loopback-only and
/// cloudflared is always a hidden, shell-free child assigned to a Windows Kill-on-close Job.
/// This type never installs cloudflared as a service and never changes firewall configuration.
/// </summary>
internal sealed class CloudflareQuickTunnelService : IWebTunnelService
{
    internal const int MaximumLogEntries = 500;
    internal const int MaximumLogLineCharacters = 4096;
    internal static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex QuickTunnelUrlRegex = new(
        @"(?<![A-Za-z0-9.-])https://(?<label>[a-z0-9](?:[a-z0-9-]{6,61}[a-z0-9]))\.trycloudflare\.com/?(?![A-Za-z0-9._~:/?#\[\]@!$&'()*+,;=%-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private readonly string _executablePath;
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
    private Uri? _publicUrl;
    private DateTimeOffset? _startedAtUtc;
    private string? _error;
    private string _executableVersion;
    private int? _localPort;
    private long _generation;
    private Task _monitorTask = Task.CompletedTask;
    private bool _disposed;

    public CloudflareQuickTunnelService(string executablePath)
        : this(
            executablePath,
            new SystemCloudflareQuickTunnelProcessFactory(),
            TimeProvider.System,
            DefaultStartupTimeout,
            DefaultStopTimeout,
            ReadExecutableVersion)
    {
    }

    internal CloudflareQuickTunnelService(
        string executablePath,
        ICloudflareQuickTunnelProcessFactory processFactory,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? stopTimeout = null,
        Func<string, string>? versionReader = null)
    {
        _executablePath = ValidateExecutablePath(executablePath);
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startupTimeout = startupTimeout ?? DefaultStartupTimeout;
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
        _versionReader = versionReader ?? ReadExecutableVersion;

        if (_startupTimeout <= TimeSpan.Zero || _startupTimeout > TimeSpan.FromMinutes(2))
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
                        "Quick Tunnel 已在另一個本機 Port 執行；請先停止再重新啟動。");
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
                    null,
                    "指定的 cloudflared.exe 已不存在或不再是一般檔案。");
            }

            _executableVersion = SanitizeVersion(_versionReader(_executablePath));
            _localPort = localPort;
            _publicUrl = null;
            _startedAtUtc = null;
            _error = null;
            var generation = Interlocked.Increment(ref _generation);
            var attempt = new StartupAttempt();
            SetState(
                WebTunnelLifecycleState.Starting,
                null,
                null,
                null,
                null);
            _logs.Add(WebTunnelLogChannel.Service, "正在啟動 Cloudflare Quick Tunnel（測試用途）。");

            ICloudflareQuickTunnelProcess process;
            try
            {
                process = await _processFactory.StartAsync(
                        CreateStartInfo(_executablePath, localPort),
                        line => HandleProcessLine(generation, attempt, line),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetState(WebTunnelLifecycleState.Stopped, null, null, null, null);
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var message = $"無法啟動 cloudflared.exe：{SanitizeError(exception.Message)}";
                _logs.Add(WebTunnelLogChannel.Service, message);
                return SetState(WebTunnelLifecycleState.Faulted, null, null, null, message);
            }

            _activeProcess = process;
            _startedAtUtc = _timeProvider.GetUtcNow();
            _monitorTask = MonitorProcessAsync(process, attempt, generation);

            using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            startupCancellation.CancelAfter(_startupTimeout);
            var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, startupCancellation.Token);
            var completed = await Task.WhenAny(
                    attempt.CandidateUrl.Task,
                    attempt.ProtocolViolation.Task,
                    process.Completion,
                    timeoutTask)
                .ConfigureAwait(false);

            if (completed == attempt.CandidateUrl.Task)
            {
                if (attempt.ProtocolViolation.Task.IsCompletedSuccessfully)
                {
                    return await FailStartAsync(
                            process,
                            attempt.ProtocolViolation.Task.Result)
                        .ConfigureAwait(false);
                }

                if (process.Completion.IsCompleted || process.HasExited)
                {
                    var exitCode = await process.Completion.ConfigureAwait(false);
                    return await FailStartAsync(
                            process,
                            $"cloudflared.exe 在建立 Tunnel 後立即結束（ExitCode={exitCode}）。")
                        .ConfigureAwait(false);
                }

                var publicUrl = await attempt.CandidateUrl.Task.ConfigureAwait(false);
                _logs.Add(
                    WebTunnelLogChannel.Service,
                    $"Quick Tunnel 已建立：{publicUrl.GetLeftPart(UriPartial.Authority)}");
                return SetState(
                    WebTunnelLifecycleState.Running,
                    publicUrl,
                    process.ProcessId,
                    _startedAtUtc,
                    null);
            }

            if (completed == attempt.ProtocolViolation.Task)
            {
                return await FailStartAsync(
                        process,
                        await attempt.ProtocolViolation.Task.ConfigureAwait(false))
                    .ConfigureAwait(false);
            }

            if (completed == process.Completion)
            {
                var exitCode = await process.Completion.ConfigureAwait(false);
                return await FailStartAsync(
                        process,
                        $"cloudflared.exe 尚未提供有效網址便已結束（ExitCode={exitCode}）。")
                    .ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await TerminateAndClearAsync(process).ConfigureAwait(false);
                SetState(WebTunnelLifecycleState.Stopped, null, null, null, null);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await FailStartAsync(
                    process,
                    $"cloudflared.exe 未在 {_startupTimeout.TotalSeconds:0.#} 秒內提供唯一且有效的 Quick Tunnel HTTPS 網址。")
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<WebTunnelSnapshot> StopAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return CreateSnapshot();
            }

            if (_activeProcess is null && _state == WebTunnelLifecycleState.Stopped)
            {
                return CreateSnapshot();
            }

            SetState(
                WebTunnelLifecycleState.Stopping,
                _publicUrl,
                _activeProcess?.ProcessId,
                _startedAtUtc,
                null);
            Interlocked.Increment(ref _generation);
            var process = _activeProcess;
            _activeProcess = null;
            _publicUrl = null;
            _localPort = null;
            if (process is not null)
            {
                await TerminateProcessAsync(process).ConfigureAwait(false);
            }

            _startedAtUtc = null;
            _logs.Add(WebTunnelLogChannel.Service, "Cloudflare Quick Tunnel 已停止。");
            return SetState(WebTunnelLifecycleState.Stopped, null, null, null, null);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task monitor;
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Interlocked.Increment(ref _generation);
            var process = _activeProcess;
            _activeProcess = null;
            _publicUrl = null;
            _localPort = null;
            if (process is not null)
            {
                await TerminateProcessAsync(process).ConfigureAwait(false);
            }

            _startedAtUtc = null;
            SetState(WebTunnelLifecycleState.Stopped, null, null, null, null);
            monitor = _monitorTask;
        }
        finally
        {
            _operationGate.Release();
        }

        try
        {
            await monitor.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The lifecycle state is already stopped and the owned process has been disposed.
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, int localPort)
    {
        ValidatePort(localPort);
        var validatedPath = ValidateExecutablePath(executablePath);
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

        // Do not inherit a caller-controlled cloudflared token, config path, proxy command, or
        // auto-update setting. The exact executable path means PATH is unnecessary.
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

        startInfo.ArgumentList.Add("tunnel");
        startInfo.ArgumentList.Add("--no-autoupdate");
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add($"http://127.0.0.1:{localPort}");
        return startInfo;
    }

    internal static IReadOnlyList<Uri> ExtractStrictQuickTunnelUrls(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var urls = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (Match match in QuickTunnelUrlRegex.Matches(text))
        {
            var raw = match.Value.EndsWith("/", StringComparison.Ordinal)
                ? match.Value[..^1]
                : match.Value;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var candidate) ||
                candidate.Scheme != Uri.UriSchemeHttps ||
                candidate.Port != 443 ||
                candidate.UserInfo.Length != 0 ||
                candidate.AbsolutePath != "/" ||
                candidate.Query.Length != 0 ||
                candidate.Fragment.Length != 0 ||
                !IsStrictQuickTunnelHost(candidate.Host))
            {
                continue;
            }

            var origin = new Uri(candidate.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
            urls.TryAdd(origin.AbsoluteUri, origin);
        }

        return urls.Values.ToArray();
    }

    private async Task<WebTunnelSnapshot> FailStartAsync(
        ICloudflareQuickTunnelProcess process,
        string message)
    {
        message = SanitizeError(message);
        _logs.Add(WebTunnelLogChannel.Service, message);
        await TerminateAndClearAsync(process).ConfigureAwait(false);
        return SetState(WebTunnelLifecycleState.Faulted, null, null, null, message);
    }

    private async Task TerminateAndClearAsync(ICloudflareQuickTunnelProcess process)
    {
        if (ReferenceEquals(_activeProcess, process))
        {
            _activeProcess = null;
        }

        _publicUrl = null;
        _startedAtUtc = null;
        _localPort = null;
        Interlocked.Increment(ref _generation);
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
                _logs.Add(
                    WebTunnelLogChannel.Service,
                    "等待 cloudflared.exe 結束逾時；正在關閉 Kill-on-close Job。");
            }
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            NotSupportedException or
            Win32Exception or
            ObjectDisposedException)
        {
            _logs.Add(
                WebTunnelLogChannel.Service,
                $"終止 cloudflared.exe 時收到非致命錯誤：{SanitizeError(exception.Message)}");
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task MonitorProcessAsync(
        ICloudflareQuickTunnelProcess process,
        StartupAttempt attempt,
        long generation)
    {
        string message;
        try
        {
            var completed = await Task.WhenAny(
                    process.Completion,
                    attempt.ProtocolViolation.Task)
                .ConfigureAwait(false);
            message = completed == attempt.ProtocolViolation.Task
                ? await attempt.ProtocolViolation.Task.ConfigureAwait(false)
                : $"cloudflared.exe 已意外結束（ExitCode={await process.Completion.ConfigureAwait(false)}）。";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            message = $"監看 cloudflared.exe 時發生錯誤：{SanitizeError(exception.Message)}";
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || generation != _generation ||
                !ReferenceEquals(_activeProcess, process))
            {
                return;
            }

            message = SanitizeError(message);
            _logs.Add(WebTunnelLogChannel.Service, message);
            _activeProcess = null;
            _publicUrl = null;
            _startedAtUtc = null;
            _localPort = null;
            Interlocked.Increment(ref _generation);
            await TerminateProcessAsync(process).ConfigureAwait(false);
            SetState(WebTunnelLifecycleState.Faulted, null, null, null, message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void HandleProcessLine(
        long generation,
        StartupAttempt attempt,
        CloudflareQuickTunnelProcessLine line)
    {
        if (generation != Interlocked.Read(ref _generation))
        {
            return;
        }

        _logs.Add(line.Channel, line.Text);
        var urls = ExtractStrictQuickTunnelUrls(line.Text);
        if (urls.Count > 0)
        {
            attempt.Register(urls);
        }
    }

    private WebTunnelSnapshot SetState(
        WebTunnelLifecycleState state,
        Uri? publicUrl,
        int? processId,
        DateTimeOffset? startedAtUtc,
        string? error)
    {
        lock (_stateGate)
        {
            _state = state;
            _publicUrl = publicUrl;
            _startedAtUtc = startedAtUtc;
            _error = error;
        }

        var snapshot = CreateSnapshot(processId);
        try
        {
            StateChanged?.Invoke(this, snapshot);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A presentation-layer subscriber must never break process ownership or leave a
            // connector alive merely because a window/dispatcher is already closing.
            _logs.Add(
                WebTunnelLogChannel.Service,
                $"Tunnel 狀態通知接收端發生錯誤：{SanitizeError(exception.Message)}");
        }

        return snapshot;
    }

    private WebTunnelSnapshot CreateSnapshot(int? processIdOverride = null)
    {
        WebTunnelLifecycleState state;
        Uri? publicUrl;
        DateTimeOffset? startedAtUtc;
        string? error;
        int? processId;
        lock (_stateGate)
        {
            state = _state;
            publicUrl = _publicUrl;
            startedAtUtc = _startedAtUtc;
            error = _error;
            processId = processIdOverride ?? _activeProcess?.ProcessId;
        }

        var now = _timeProvider.GetUtcNow();
        TimeSpan? runningFor = startedAtUtc is { } started
            ? TimeSpan.FromTicks(Math.Max(0, (now - started).Ticks))
            : null;
        return new WebTunnelSnapshot(
            state,
            publicUrl,
            processId,
            _executableVersion,
            startedAtUtc,
            runningFor,
            error,
            _logs.Snapshot());
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

    private static bool IsStrictQuickTunnelHost(string host)
    {
        const string suffix = ".trycloudflare.com";
        if (!host.EndsWith(suffix, StringComparison.Ordinal) ||
            host.Length <= suffix.Length)
        {
            return false;
        }

        var label = host[..^suffix.Length];
        if (label.Length is < 8 or > 63 || label[0] == '-' || label[^1] == '-')
        {
            return false;
        }

        return label.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }

    private static void ValidatePort(int localPort)
    {
        if (localPort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localPort),
                "本機 Quick Tunnel 目標 Port 必須介於 1024–65535。");
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
        if (string.IsNullOrWhiteSpace(version))
        {
            return "unknown";
        }

        var normalized = version.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 128 ? normalized : normalized[..128];
    }

    private static string SanitizeError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "未知錯誤。";
        }

        var normalized = BoundedRedactedWebTunnelLog.Redact(message)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500] + "…";
    }

    private sealed class StartupAttempt
    {
        private readonly object _gate = new();
        private Uri? _candidate;

        public TaskCompletionSource<Uri> CandidateUrl { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> ProtocolViolation { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Register(IReadOnlyList<Uri> urls)
        {
            lock (_gate)
            {
                foreach (var url in urls)
                {
                    if (_candidate is null)
                    {
                        _candidate = url;
                        continue;
                    }

                    if (!string.Equals(
                            _candidate.AbsoluteUri,
                            url.AbsoluteUri,
                            StringComparison.Ordinal))
                    {
                        ProtocolViolation.TrySetResult(
                            "cloudflared.exe 輸出了多個不同的 Quick Tunnel 網址；已安全停止。");
                        return;
                    }
                }

                if (_candidate is not null)
                {
                    CandidateUrl.TrySetResult(_candidate);
                }
            }
        }
    }
}

internal sealed class BoundedRedactedWebTunnelLog
{
    private static readonly Regex SensitiveHeaderRegex = new(
        @"(?i)\b(authorization|proxy-authorization|cookie|set-cookie)\b\s*[:=]\s*.*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex SecretValueRegex = new(
        @"(?i)\b(token|secret|password|passwd|pin|credential|client_secret|api[_-]?key)\b(?:\s*[:=]\s*|\s+)(?:""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex JwtRegex = new(
        @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?:\.[A-Za-z0-9_-]{8,})?\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex CloudflareTunnelTokenRegex = new(
        @"(?<![A-Za-z0-9_+/-])eyJ[A-Za-z0-9_+/=-]{61,}(?![A-Za-z0-9_+/-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex UrlRegex = new(
        @"https?://[^\s<>""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private readonly object _gate = new();
    private readonly Queue<WebTunnelLogEntry> _entries;
    private readonly int _maximumEntries;
    private readonly int _maximumLineCharacters;
    private readonly TimeProvider _timeProvider;

    public BoundedRedactedWebTunnelLog(
        int maximumEntries,
        int maximumLineCharacters,
        TimeProvider timeProvider)
    {
        if (maximumEntries is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        if (maximumLineCharacters is < 128 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLineCharacters));
        }

        _maximumEntries = maximumEntries;
        _maximumLineCharacters = maximumLineCharacters;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _entries = new Queue<WebTunnelLogEntry>(maximumEntries);
    }

    public void Add(WebTunnelLogChannel channel, string? rawMessage)
    {
        var message = Redact(rawMessage);
        if (message.Length > _maximumLineCharacters)
        {
            message = message[.._maximumLineCharacters] + "…[截斷]";
        }

        lock (_gate)
        {
            while (_entries.Count >= _maximumEntries)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(new WebTunnelLogEntry(
                _timeProvider.GetUtcNow(),
                channel,
                message));
        }
    }

    public IReadOnlyList<WebTunnelLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    internal static string Redact(string? rawMessage)
    {
        if (string.IsNullOrEmpty(rawMessage))
        {
            return string.Empty;
        }

        var normalized = rawMessage
            .Replace('\0', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        normalized = SensitiveHeaderRegex.Replace(normalized, "$1: [REDACTED]");
        normalized = SecretValueRegex.Replace(normalized, "$1=[REDACTED]");
        normalized = JwtRegex.Replace(normalized, "[REDACTED-TOKEN]");
        normalized = CloudflareTunnelTokenRegex.Replace(normalized, "[REDACTED-TOKEN]");
        normalized = UrlRegex.Replace(normalized, static match =>
        {
            var value = match.Value;
            var sensitiveIndex = value.IndexOfAny(['?', '#']);
            return sensitiveIndex < 0
                ? value
                : value[..sensitiveIndex] + "?[REDACTED]";
        });
        return normalized.Trim();
    }
}

internal sealed class SystemCloudflareQuickTunnelProcessFactory
    : ICloudflareQuickTunnelProcessFactory
{
    public Task<ICloudflareQuickTunnelProcess> StartAsync(
        ProcessStartInfo startInfo,
        Action<CloudflareQuickTunnelProcessLine> outputSink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(outputSink);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Cloudflare Quick Tunnel 子程序需要 Windows Kill-on-close Job。");
        }

        if (startInfo.UseShellExecute || !startInfo.CreateNoWindow ||
            !startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError ||
            startInfo.RedirectStandardInput)
        {
            throw new InvalidOperationException("拒絕不安全的 cloudflared.exe 啟動設定。");
        }

        var job = WindowsKillOnCloseJob.Create();
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        try
        {
            if (!process.Start())
            {
                throw new Win32Exception("作業系統未啟動 cloudflared.exe。");
            }

            WindowsKillOnCloseJob.Assign(job, process);
            ICloudflareQuickTunnelProcess owned = new SystemCloudflareQuickTunnelProcess(
                process,
                job,
                outputSink);
            return Task.FromResult(owned);
        }
        catch
        {
            ProcessIo.TryKillProcessTree(process);
            process.Dispose();
            job.Dispose();
            throw;
        }
        finally
        {
            // A remotely-managed Named Tunnel token is copied into the child environment by
            // CreateProcess. Remove the parent-side ProcessStartInfo copy immediately, whether
            // startup succeeds or fails, so diagnostics cannot retain or later display it.
            startInfo.Environment.Remove(
                CloudflareNamedTunnelService.TunnelTokenEnvironmentVariable);
        }
    }
}

internal sealed class SystemCloudflareQuickTunnelProcess : ICloudflareQuickTunnelProcess
{
    private readonly Process _process;
    private readonly SafeJobHandle _job;
    private readonly CancellationTokenSource _readCancellation = new();
    private readonly Task _stdoutTask;
    private readonly Task _stderrTask;
    private readonly Task<int> _completion;
    private readonly int _processId;
    private int _disposeStarted;

    public SystemCloudflareQuickTunnelProcess(
        Process process,
        SafeJobHandle job,
        Action<CloudflareQuickTunnelProcessLine> outputSink)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _job = job ?? throw new ArgumentNullException(nameof(job));
        ArgumentNullException.ThrowIfNull(outputSink);
        _processId = process.Id;
        _stdoutTask = PumpLinesAsync(
            process.StandardOutput,
            WebTunnelLogChannel.StandardOutput,
            outputSink,
            _readCancellation.Token);
        _stderrTask = PumpLinesAsync(
            process.StandardError,
            WebTunnelLogChannel.StandardError,
            outputSink,
            _readCancellation.Token);
        _completion = CompleteAsync();
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
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public Task<int> Completion => _completion;

    public void KillEntireProcessTree() => ProcessIo.TryKillProcessTree(_process);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        ProcessIo.TryKillProcessTree(_process);
        // Closing the Job is the crash-safe ownership boundary and kills descendants too.
        _job.Dispose();
        try
        {
            _ = await _completion.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _readCancellation.Cancel();
        }
        finally
        {
            _readCancellation.Cancel();
            try
            {
                await Task.WhenAll(_stdoutTask, _stderrTask)
                    .WaitAsync(TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                TimeoutException or
                OperationCanceledException)
            {
            }

            _readCancellation.Dispose();
            _process.Dispose();
        }
    }

    private async Task<int> CompleteAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(_stdoutTask, _stderrTask).ConfigureAwait(false);
        return _process.ExitCode;
    }

    private static async Task PumpLinesAsync(
        StreamReader reader,
        WebTunnelLogChannel channel,
        Action<CloudflareQuickTunnelProcessLine> outputSink,
        CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var line = new StringBuilder(Math.Min(
            CloudflareQuickTunnelService.MaximumLogLineCharacters,
            1024));
        var truncated = false;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (character == '\n')
                    {
                        EmitLine(channel, outputSink, line, truncated);
                        truncated = false;
                        continue;
                    }

                    if (character == '\r')
                    {
                        continue;
                    }

                    if (line.Length < CloudflareQuickTunnelService.MaximumLogLineCharacters)
                    {
                        line.Append(character);
                    }
                    else
                    {
                        truncated = true;
                    }
                }
            }

            if (line.Length > 0 || truncated)
            {
                EmitLine(channel, outputSink, line, truncated);
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

    private static void EmitLine(
        WebTunnelLogChannel channel,
        Action<CloudflareQuickTunnelProcessLine> outputSink,
        StringBuilder line,
        bool truncated)
    {
        var text = line.ToString();
        line.Clear();
        if (truncated)
        {
            text += "…[截斷]";
        }

        if (text.Length > 0)
        {
            outputSink(new CloudflareQuickTunnelProcessLine(channel, text));
        }
    }
}
