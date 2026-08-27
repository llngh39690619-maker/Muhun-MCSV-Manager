using System.Diagnostics;
using System.IO;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class CloudflareQuickTunnelServiceTests
{
    private const int LocalPort = 39049;
    private const string PublicOrigin = "https://gentle-cloud-1234.trycloudflare.com/";

    [Fact]
    public async Task Constructor_RequiresCallerSpecifiedAbsoluteRegularCloudflaredExe()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            _ = new CloudflareQuickTunnelService("cloudflared.exe");
            return Task.CompletedTask;
        });

        using var executable = new TemporaryExecutable();
        var wrongName = Path.Combine(executable.DirectoryPath, "not-cloudflared.exe");
        File.WriteAllBytes(wrongName, [0x4D, 0x5A]);
        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            _ = new CloudflareQuickTunnelService(wrongName);
            return Task.CompletedTask;
        });

        var missing = Path.Combine(executable.DirectoryPath, "missing", "cloudflared.exe");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
        {
            _ = new CloudflareQuickTunnelService(missing);
            return Task.CompletedTask;
        });

        var directory = Path.Combine(executable.DirectoryPath, "nested", "cloudflared.exe");
        Directory.CreateDirectory(directory);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
        {
            _ = new CloudflareQuickTunnelService(directory);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartInfo_IsHiddenShellFreeAndUsesOnlyExpectedQuickTunnelArguments()
    {
        using var executable = new TemporaryExecutable();

        var startInfo = CloudflareQuickTunnelService.CreateStartInfo(
            executable.FilePath,
            LocalPort);

        Assert.Equal(executable.FilePath, startInfo.FileName);
        Assert.Equal(executable.DirectoryPath, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(
            ["tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{LocalPort}"],
            startInfo.ArgumentList);
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            argument => argument.Contains("service", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            startInfo.Environment.Keys,
            key => key.Contains("TUNNEL_TOKEN", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https://gentle-cloud-1234.trycloudflare.com")]
    [InlineData("notice: https://gentle-cloud-1234.trycloudflare.com/")]
    [InlineData("{\"url\":\"https://gentle-cloud-1234.trycloudflare.com\"}")]
    public void StrictUrlParser_AcceptsOnlyCanonicalHttpsQuickTunnelOrigin(string value)
    {
        var urls = CloudflareQuickTunnelService.ExtractStrictQuickTunnelUrls(value);

        var result = Assert.Single(urls);
        Assert.Equal(PublicOrigin, result.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://gentle-cloud-1234.trycloudflare.com")]
    [InlineData("https://gentle-cloud-1234.trycloudflare.com.evil.example")]
    [InlineData("https://trycloudflare.com")]
    [InlineData("https://short.trycloudflare.com")]
    [InlineData("https://Gentle-cloud-1234.trycloudflare.com")]
    [InlineData("https://gentle-cloud-1234.trycloudflare.com:8443")]
    [InlineData("https://gentle-cloud-1234.trycloudflare.com/admin")]
    [InlineData("https://gentle-cloud-1234.trycloudflare.com?token=secret")]
    [InlineData("https://user@gentle-cloud-1234.trycloudflare.com")]
    [InlineData("https://gentle_cloud_1234.trycloudflare.com")]
    public void StrictUrlParser_RejectsWrongOrMaliciousUrls(string value)
    {
        Assert.Empty(CloudflareQuickTunnelService.ExtractStrictQuickTunnelUrls(value));
    }

    [Fact]
    public void StrictUrlParser_ReturnsDistinctOriginsForProtocolViolationDetection()
    {
        const string text =
            "https://gentle-cloud-1234.trycloudflare.com " +
            "https://another-cloud-5678.trycloudflare.com";

        var urls = CloudflareQuickTunnelService.ExtractStrictQuickTunnelUrls(text);

        Assert.Equal(2, urls.Count);
    }

    [Theory]
    [InlineData("Authorization: Bearer top-secret", "top-secret")]
    [InlineData("Cookie: session=private-value", "private-value")]
    [InlineData("token=connector-secret", "connector-secret")]
    [InlineData("token connector-secret", "connector-secret")]
    [InlineData("https://example.invalid/path?token=query-secret&x=1", "query-secret")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzZWNyZXQifQ.signaturevalue", "eyJhbGci")]
    public void Redactor_RemovesCredentialsAndUrlQueries(string raw, string forbidden)
    {
        var result = BoundedRedactedWebTunnelLog.Redact(raw);

        Assert.DoesNotContain(forbidden, result, StringComparison.Ordinal);
        Assert.Contains("REDACTED", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_TruncatesOneOversizedLineBeforeRetainingIt()
    {
        var log = new BoundedRedactedWebTunnelLog(
            5,
            CloudflareQuickTunnelService.MaximumLogLineCharacters,
            TimeProvider.System);

        log.Add(WebTunnelLogChannel.StandardOutput, new string('x', 100_000));

        var entry = Assert.Single(log.Snapshot());
        Assert.True(
            entry.Message.Length <= CloudflareQuickTunnelService.MaximumLogLineCharacters + 10,
            $"Retained line was unexpectedly large: {entry.Message.Length}");
        Assert.EndsWith("…[截斷]", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_UsesInjectedProcessAndPublishesValidatedRuntimeState()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(executable, factory);

        var startTask = service.StartAsync(LocalPort);
        await factory.WaitUntilStartedAsync();
        factory.Emit(WebTunnelLogChannel.StandardError, $"INF URL {PublicOrigin}");
        var result = await startTask;

        Assert.Equal(WebTunnelLifecycleState.Running, result.State);
        Assert.Equal(PublicOrigin, result.PublicUrl?.AbsoluteUri);
        Assert.Equal(factory.Process.ProcessId, result.ProcessId);
        Assert.Equal("2026.8.0-test", result.ExecutableVersion);
        Assert.NotNull(result.StartedAtUtc);
        Assert.NotNull(result.RunningFor);
        Assert.Null(result.Error);
        Assert.NotNull(factory.StartInfo);
        Assert.Equal(
            ["tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{LocalPort}"],
            factory.StartInfo!.ArgumentList);
    }

    [Fact]
    public async Task Start_AllowsRepeatedCopiesOfOneIdenticalOrigin()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(executable, factory);

        var startTask = service.StartAsync(LocalPort);
        await factory.WaitUntilStartedAsync();
        factory.Emit(
            WebTunnelLogChannel.StandardError,
            $"{PublicOrigin} {PublicOrigin}");

        var result = await startTask;
        Assert.Equal(WebTunnelLifecycleState.Running, result.State);
        Assert.Equal(PublicOrigin, result.PublicUrl?.AbsoluteUri);
    }

    [Fact]
    public async Task Start_MultipleDistinctOriginsFailsClosedAndKillsProcess()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(executable, factory);

        var startTask = service.StartAsync(LocalPort);
        await factory.WaitUntilStartedAsync();
        factory.Emit(
            WebTunnelLogChannel.StandardError,
            "https://gentle-cloud-1234.trycloudflare.com " +
            "https://another-cloud-5678.trycloudflare.com");

        var result = await startTask;
        Assert.Equal(WebTunnelLifecycleState.Faulted, result.State);
        Assert.Null(result.PublicUrl);
        Assert.Contains("多個不同", result.Error);
        Assert.True(factory.Process.KillCount > 0);
        Assert.True(factory.Process.DisposeCount > 0);
    }

    [Fact]
    public async Task Start_InvalidUrlTimesOutAndFailsClosed()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(
            executable,
            factory,
            startupTimeout: TimeSpan.FromMilliseconds(80));

        var startTask = service.StartAsync(LocalPort);
        await factory.WaitUntilStartedAsync();
        factory.Emit(
            WebTunnelLogChannel.StandardError,
            "https://gentle-cloud-1234.trycloudflare.com.evil.example?token=secret");

        var result = await startTask;
        Assert.Equal(WebTunnelLifecycleState.Faulted, result.State);
        Assert.Null(result.PublicUrl);
        Assert.Contains("未在", result.Error);
        Assert.True(factory.Process.KillCount > 0);
    }

    [Fact]
    public async Task Start_ProcessExitBeforeUrlFailsClosed()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(executable, factory);

        var startTask = service.StartAsync(LocalPort);
        await factory.WaitUntilStartedAsync();
        factory.Process.Exit(23);

        var result = await startTask;
        Assert.Equal(WebTunnelLifecycleState.Faulted, result.State);
        Assert.Null(result.PublicUrl);
        Assert.Contains("ExitCode=23", result.Error);
    }

    [Fact]
    public async Task Logs_AreBoundedAndSecretsAndUrlQueriesAreRedacted()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(executable, factory);

        var startTask = service.StartAsync(LocalPort);
        await factory.WaitUntilStartedAsync();
        factory.Emit(WebTunnelLogChannel.StandardError, "Authorization: Bearer top-secret");
        factory.Emit(WebTunnelLogChannel.StandardError, "Cookie: session=private-value");
        factory.Emit(WebTunnelLogChannel.StandardOutput, "token=connector-secret");
        factory.Emit(
            WebTunnelLogChannel.StandardOutput,
            "GET https://example.invalid/callback?token=query-secret&x=1");
        for (var index = 0; index < 900; index++)
        {
            factory.Emit(WebTunnelLogChannel.StandardOutput, $"bounded-line-{index}");
        }
        factory.Emit(WebTunnelLogChannel.StandardError, $"INF URL {PublicOrigin}");

        var result = await startTask;
        Assert.Equal(WebTunnelLifecycleState.Running, result.State);
        Assert.Equal(CloudflareQuickTunnelService.MaximumLogEntries, result.RecentLogs.Count);
        var combined = string.Join('\n', result.RecentLogs.Select(entry => entry.Message));
        Assert.DoesNotContain("top-secret", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("connector-secret", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", combined, StringComparison.Ordinal);
        Assert.Contains("bounded-line-899", combined, StringComparison.Ordinal);
        Assert.Contains(PublicOrigin.TrimEnd('/'), combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_IsIdempotentAndTerminatesTheOwnedProcessTree()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(executable, factory);
        var startTask = service.StartAsync(LocalPort);
        await factory.WaitUntilStartedAsync();
        factory.Emit(WebTunnelLogChannel.StandardError, PublicOrigin);
        _ = await startTask;

        var first = await service.StopAsync();
        var second = await service.StopAsync();

        Assert.Equal(WebTunnelLifecycleState.Stopped, first.State);
        Assert.Equal(WebTunnelLifecycleState.Stopped, second.State);
        Assert.Null(first.PublicUrl);
        Assert.Null(first.ProcessId);
        Assert.Equal(1, factory.Process.KillCount);
        Assert.Equal(1, factory.Process.DisposeCount);
    }

    [Fact]
    public async Task UnexpectedExitAfterStartupMovesServiceToFaulted()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(executable, factory);
        var startTask = service.StartAsync(LocalPort);
        await factory.WaitUntilStartedAsync();
        factory.Emit(WebTunnelLogChannel.StandardError, PublicOrigin);
        Assert.Equal(WebTunnelLifecycleState.Running, (await startTask).State);

        factory.Process.Exit(17);
        await WaitForStateAsync(service, WebTunnelLifecycleState.Faulted);

        Assert.Null(service.Snapshot.PublicUrl);
        Assert.Contains("ExitCode=17", service.Snapshot.Error);
    }

    [Fact]
    public async Task DifferentOriginAfterStartupTriggersProtocolFailure()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(executable, factory);
        var startTask = service.StartAsync(LocalPort);
        await factory.WaitUntilStartedAsync();
        factory.Emit(WebTunnelLogChannel.StandardError, PublicOrigin);
        Assert.Equal(WebTunnelLifecycleState.Running, (await startTask).State);

        factory.Emit(
            WebTunnelLogChannel.StandardError,
            "https://another-cloud-5678.trycloudflare.com");
        await WaitForStateAsync(service, WebTunnelLifecycleState.Faulted);

        Assert.Null(service.Snapshot.PublicUrl);
        Assert.True(factory.Process.KillCount > 0);
        Assert.Contains("多個不同", service.Snapshot.Error);
    }

    [Fact]
    public async Task Dispose_IsIdempotentAndStopsRunningProcess()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        var service = CreateService(executable, factory);
        var startTask = service.StartAsync(LocalPort);
        await factory.WaitUntilStartedAsync();
        factory.Emit(WebTunnelLogChannel.StandardError, PublicOrigin);
        _ = await startTask;

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(WebTunnelLifecycleState.Stopped, service.Snapshot.State);
        Assert.Equal(1, factory.Process.KillCount);
        Assert.Equal(1, factory.Process.DisposeCount);
    }

    [Fact]
    public async Task ThrowingStateSubscriberCannotBreakOwnedProcessLifecycle()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(executable, factory);
        var notificationCount = 0;
        service.StateChanged += (_, _) =>
        {
            Interlocked.Increment(ref notificationCount);
            throw new InvalidOperationException("subscriber token=event-secret");
        };

        var startTask = service.StartAsync(LocalPort);
        await factory.WaitUntilStartedAsync();
        factory.Emit(WebTunnelLogChannel.StandardError, PublicOrigin);
        var started = await startTask;

        Assert.Equal(WebTunnelLifecycleState.Running, started.State);
        Assert.True(Volatile.Read(ref notificationCount) >= 2);

        var stopped = await service.StopAsync();

        Assert.Equal(WebTunnelLifecycleState.Stopped, stopped.State);
        Assert.Equal(1, factory.Process.KillCount);
        Assert.Equal(1, factory.Process.DisposeCount);
        var logs = string.Join('\n', service.Snapshot.RecentLogs.Select(entry => entry.Message));
        Assert.Contains("狀態通知接收端發生錯誤", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("event-secret", logs, StringComparison.Ordinal);
    }

    private static CloudflareQuickTunnelService CreateService(
        TemporaryExecutable executable,
        FakeProcessFactory factory,
        TimeSpan? startupTimeout = null)
        => new(
            executable.FilePath,
            factory,
            TimeProvider.System,
            startupTimeout ?? TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(200),
            _ => "2026.8.0-test");

    private static async Task WaitForStateAsync(
        CloudflareQuickTunnelService service,
        WebTunnelLifecycleState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (service.Snapshot.State != expected)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeProcessFactory : ICloudflareQuickTunnelProcessFactory
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<CloudflareQuickTunnelProcessLine>? _outputSink;

        public FakeProcess Process { get; } = new();
        public ProcessStartInfo? StartInfo { get; private set; }

        public Task<ICloudflareQuickTunnelProcess> StartAsync(
            ProcessStartInfo startInfo,
            Action<CloudflareQuickTunnelProcessLine> outputSink,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartInfo = startInfo;
            _outputSink = outputSink;
            _started.TrySetResult();
            return Task.FromResult<ICloudflareQuickTunnelProcess>(Process);
        }

        public Task WaitUntilStartedAsync()
            => _started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        public void Emit(WebTunnelLogChannel channel, string text)
            => (_outputSink ?? throw new InvalidOperationException("Process has not started."))(
                new CloudflareQuickTunnelProcessLine(channel, text));
    }

    private sealed class FakeProcess : ICloudflareQuickTunnelProcess
    {
        private readonly TaskCompletionSource<int> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _killCount;
        private int _disposeCount;

        public int ProcessId { get; } = 42420;
        public bool HasExited => _completion.Task.IsCompleted;
        public Task<int> Completion => _completion.Task;
        public int KillCount => Volatile.Read(ref _killCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void KillEntireProcessTree()
        {
            Interlocked.Increment(ref _killCount);
            _completion.TrySetResult(-9);
        }

        public void Exit(int exitCode) => _completion.TrySetResult(exitCode);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            _completion.TrySetResult(-9);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryExecutable : IDisposable
    {
        public TemporaryExecutable()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "McsvQuickTunnelTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            FilePath = Path.Combine(DirectoryPath, "cloudflared.exe");
            File.WriteAllBytes(FilePath, [0x4D, 0x5A]);
        }

        public string DirectoryPath { get; }
        public string FilePath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
