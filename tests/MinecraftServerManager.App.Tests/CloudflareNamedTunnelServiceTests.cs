using System.Diagnostics;
using System.IO;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class CloudflareNamedTunnelServiceTests
{
    private const int LocalPort = 39049;
    private const string PublicOrigin = "https://mcsv.example.com/";
    private const string RegisteredConnectionLine =
        "INF Registered tunnel connection connIndex=0 connection=01234567-89ab-cdef-0123-456789abcdef";
    private static readonly string TunnelToken = "eyJ" + new string('T', 157) + "=";

    [Fact]
    public void StartInfo_IsHiddenShellFreeAndPlacesTokenOnlyInChildEnvironment()
    {
        using var executable = new TemporaryExecutable();

        var startInfo = CloudflareNamedTunnelService.CreateStartInfo(
            executable.FilePath,
            TunnelToken);

        Assert.Equal(executable.FilePath, startInfo.FileName);
        Assert.Equal(executable.DirectoryPath, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(["tunnel", "--no-autoupdate", "run"], startInfo.ArgumentList);
        Assert.DoesNotContain(TunnelToken, startInfo.ArgumentList);
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            value => value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("config", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("service", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("--url", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            TunnelToken,
            startInfo.Environment[CloudflareNamedTunnelService.TunnelTokenEnvironmentVariable]);
        Assert.DoesNotContain("CLOUDFLARED_CONFIG", startInfo.Environment.Keys);
    }

    [Theory]
    [InlineData("https://quiet-lake-abc123.trycloudflare.com/")]
    [InlineData("https://localhost/")]
    [InlineData("https://mcsv.example.com:8443/")]
    [InlineData("http://mcsv.example.com/")]
    public void Constructor_RejectsOriginsOutsideNamedTunnelPolicy(string origin)
    {
        using var executable = new TemporaryExecutable();

        Assert.Throws<ArgumentException>(() =>
            new CloudflareNamedTunnelService(
                executable.FilePath,
                new Uri(origin),
                TunnelToken));
    }

    [Fact]
    public void Constructor_RejectsMalformedTokenBeforeStartingAProcess()
    {
        using var executable = new TemporaryExecutable();

        Assert.Throws<ArgumentException>(() =>
            new CloudflareNamedTunnelService(
                executable.FilePath,
                new Uri(PublicOrigin),
                "short"));
    }

    [Fact]
    public async Task Start_PublishesConfiguredFixedOriginAfterChildStabilityWindow()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(executable, factory);

        var start = service.StartAsync(LocalPort);
        await factory.WaitUntilStartedAsync();
        factory.Emit(WebTunnelLogChannel.StandardError, RegisteredConnectionLine);
        var result = await start;

        Assert.Equal(WebTunnelLifecycleState.Running, result.State);
        Assert.Equal(PublicOrigin, result.PublicUrl?.AbsoluteUri);
        Assert.Equal(factory.Process.ProcessId, result.ProcessId);
        Assert.Equal("2026.8.0-test", result.ExecutableVersion);
        Assert.NotNull(result.StartedAtUtc);
        Assert.Null(result.Error);
        Assert.Equal(["tunnel", "--no-autoupdate", "run"], factory.StartInfo?.ArgumentList);
        Assert.DoesNotContain(
            TunnelToken,
            string.Join(' ', factory.StartInfo!.ArgumentList),
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(executable.DirectoryPath, "config.yml")));
    }

    [Fact]
    public async Task Start_ProcessExitBeforeStabilityFailsClosedAndDisposesChild()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory { ExitImmediatelyWith = 23 };
        await using var service = CreateService(executable, factory);

        var result = await service.StartAsync(LocalPort);

        Assert.Equal(WebTunnelLifecycleState.Faulted, result.State);
        Assert.Null(result.PublicUrl);
        Assert.Contains("ExitCode=23", result.Error);
        Assert.True(factory.Process.DisposeCount > 0);
    }

    [Fact]
    public async Task Start_LiveButOfflineChildTimesOutWithoutClaimingConnected()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(executable, factory);

        var result = await service.StartAsync(LocalPort);

        Assert.Equal(WebTunnelLifecycleState.Faulted, result.State);
        Assert.Null(result.PublicUrl);
        Assert.Contains("未在", result.Error);
        Assert.Equal(1, factory.Process.KillCount);
    }

    [Fact]
    public async Task UnexpectedExitAfterStartupFailsClosedAndStopRemainsIdempotent()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(executable, factory);
        Assert.Equal(
            WebTunnelLifecycleState.Running,
            (await StartConnectedAsync(service, factory)).State);

        factory.Process.Exit(17);
        await WaitForStateAsync(service, WebTunnelLifecycleState.Faulted);

        Assert.Null(service.Snapshot.PublicUrl);
        Assert.Contains("ExitCode=17", service.Snapshot.Error);
        Assert.Equal(WebTunnelLifecycleState.Stopped, (await service.StopAsync()).State);
        Assert.Equal(WebTunnelLifecycleState.Stopped, (await service.StopAsync()).State);
    }

    [Fact]
    public async Task Stop_KillsTheOwnedProcessTreeAndDisposeIsIdempotent()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        var service = CreateService(executable, factory);
        _ = await StartConnectedAsync(service, factory);

        var stopped = await service.StopAsync();
        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(WebTunnelLifecycleState.Stopped, stopped.State);
        Assert.Equal(1, factory.Process.KillCount);
        Assert.Equal(1, factory.Process.DisposeCount);
    }

    [Fact]
    public async Task ChildOutputCannotExposeNamedTunnelTokenInBoundedLogs()
    {
        using var executable = new TemporaryExecutable();
        var factory = new FakeProcessFactory();
        await using var service = CreateService(executable, factory);
        _ = await StartConnectedAsync(service, factory);

        factory.Emit(WebTunnelLogChannel.StandardError, TunnelToken);
        factory.Emit(
            WebTunnelLogChannel.StandardError,
            $"TUNNEL_TOKEN={TunnelToken}");

        var combined = string.Join('\n', service.Snapshot.RecentLogs.Select(entry => entry.Message));
        Assert.DoesNotContain(TunnelToken, combined, StringComparison.Ordinal);
        Assert.Contains("REDACTED", combined, StringComparison.Ordinal);
    }

    private static CloudflareNamedTunnelService CreateService(
        TemporaryExecutable executable,
        FakeProcessFactory factory)
        => new(
            executable.FilePath,
            new Uri(PublicOrigin),
            TunnelToken,
            factory,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(200),
            _ => "2026.8.0-test");

    private static async Task<WebTunnelSnapshot> StartConnectedAsync(
        CloudflareNamedTunnelService service,
        FakeProcessFactory factory)
    {
        var start = service.StartAsync(LocalPort);
        await factory.WaitUntilStartedAsync();
        factory.Emit(WebTunnelLogChannel.StandardError, RegisteredConnectionLine);
        return await start;
    }

    private static async Task WaitForStateAsync(
        CloudflareNamedTunnelService service,
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
        public int? ExitImmediatelyWith { get; init; }

        public Task<ICloudflareQuickTunnelProcess> StartAsync(
            ProcessStartInfo startInfo,
            Action<CloudflareQuickTunnelProcessLine> outputSink,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartInfo = startInfo;
            _outputSink = outputSink;
            _started.TrySetResult();
            if (ExitImmediatelyWith is { } exitCode)
            {
                Process.Exit(exitCode);
            }

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

        public int ProcessId { get; } = 42421;
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
                "McsvNamedTunnelTests",
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
