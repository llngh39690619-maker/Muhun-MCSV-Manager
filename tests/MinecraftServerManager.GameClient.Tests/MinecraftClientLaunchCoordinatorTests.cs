using System.Collections.Concurrent;
using System.Diagnostics;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class MinecraftClientLaunchCoordinatorTests : IDisposable
{
    private const long Gibibyte = 1024L * 1024 * 1024;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-client-launch-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LaunchAsync_StartsInteractiveProcessAndReportsExit()
    {
        var instance = CreateInstance();
        var builder = new CommandProcessBuilder(exitCode: 7);
        var coordinator = new MinecraftClientLaunchCoordinator(
            new MinecraftClientMemoryRecommendationService(
                new FixedMemoryProbe(new SystemMemorySnapshot(16 * Gibibyte, 12 * Gibibyte))),
            builder);
        var account = CreateSession("account-a");

        await using var session = await coordinator.LaunchAsync(
            instance,
            new NewMinecraftClientDefaultsSettings(),
            account);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(7, result.ExitCode);
        Assert.True(result.PlayTime >= TimeSpan.Zero);
        Assert.Equal(MinecraftClientMemoryMode.Automatic, builder.Memory?.EffectiveMode);
    }

    [Fact]
    public async Task LaunchAsync_RejectsAccountMismatchBeforeBuildingProcess()
    {
        var instance = CreateInstance();
        instance.AccountId = "account-a";
        var builder = new CommandProcessBuilder(exitCode: 0);
        var coordinator = new MinecraftClientLaunchCoordinator(
            new MinecraftClientMemoryRecommendationService(
                new FixedMemoryProbe(new SystemMemorySnapshot(16 * Gibibyte, 12 * Gibibyte))),
            builder);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.LaunchAsync(
            instance,
            new NewMinecraftClientDefaultsSettings(),
            CreateSession("account-b")));

        Assert.Equal(0, builder.CallCount);
    }

    [Fact]
    public async Task LaunchAsync_DoesNotLetAStalePidMarkerPermanentlyBlockTheInstance()
    {
        var instance = CreateInstance();
        instance.ActiveProcessId = int.MaxValue;
        instance.ActiveProcessStartedAtUtc = new DateTimeOffset(2026, 8, 28, 1, 2, 3, TimeSpan.Zero);
        instance.ActiveProcessExecutablePath = Path.Combine(_root, "missing-runtime", "java.exe");
        var builder = new CommandProcessBuilder(exitCode: 0);
        var coordinator = new MinecraftClientLaunchCoordinator(
            new MinecraftClientMemoryRecommendationService(
                new FixedMemoryProbe(new SystemMemorySnapshot(16 * Gibibyte, 12 * Gibibyte))),
            builder);

        await using var session = await coordinator.LaunchAsync(
            instance,
            new NewMinecraftClientDefaultsSettings(),
            CreateSession("account-a"));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, builder.CallCount);
    }

    [Fact]
    public async Task LaunchAsync_ReplaysBoundedEarlyOutputAfterTheUiSubscribes()
    {
        var instance = CreateInstance();
        var builder = new DelayedOutputProcessBuilder();
        var coordinator = new MinecraftClientLaunchCoordinator(
            new MinecraftClientMemoryRecommendationService(
                new FixedMemoryProbe(new SystemMemorySnapshot(16 * Gibibyte, 12 * Gibibyte))),
            builder);

        await using var session = await coordinator.LaunchAsync(
            instance,
            new NewMinecraftClientDefaultsSettings(),
            CreateSession("account-a"));
        Assert.True(session.LogCaptureAvailable);
        await Task.Delay(350);

        var output = new ConcurrentQueue<string>();
        session.OutputReceived += (_, line) => output.Enqueue(line);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (output.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Contains(output, line => line.Trim().Equals("EARLY-OUT", StringComparison.Ordinal));
        Assert.Contains(output, line => line.Trim().Equals("EARLY-ERR", StringComparison.Ordinal));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task LaunchAsync_DrainsLargeBackgroundOutputWithoutAUiSubscriber()
    {
        var instance = CreateInstance();
        instance.ShowGameLog = false;
        var coordinator = new MinecraftClientLaunchCoordinator(
            new MinecraftClientMemoryRecommendationService(
                new FixedMemoryProbe(new SystemMemorySnapshot(16 * Gibibyte, 12 * Gibibyte))),
            new LargeOutputProcessBuilder());

        await using var session = await coordinator.LaunchAsync(
            instance,
            new NewMinecraftClientDefaultsSettings(),
            CreateSession("account-a"));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(session.LogCaptureAvailable);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task LaunchAsync_PersistenceFailureTerminatesTheJustStartedJavaProcess()
    {
        var instance = CreateInstance();
        var builder = new LongRunningJavaProcessBuilder(_root);
        var coordinator = new MinecraftClientLaunchCoordinator(
            new MinecraftClientMemoryRecommendationService(
                new FixedMemoryProbe(new SystemMemorySnapshot(16 * Gibibyte, 12 * Gibibyte))),
            builder);
        MinecraftClientProcessIdentity? attemptedIdentity = null;
        CancellationToken persistenceToken = new(canceled: true);

        var error = await Assert.ThrowsAsync<IOException>(() => coordinator.LaunchAsync(
            instance,
            new NewMinecraftClientDefaultsSettings(),
            CreateSession("account-a"),
            (identity, token) =>
            {
                attemptedIdentity = identity;
                persistenceToken = token;
                return Task.FromException(new IOException("Registry persistence failed."));
            }));

        Assert.Equal("Registry persistence failed.", error.Message);
        Assert.NotNull(attemptedIdentity);
        Assert.False(persistenceToken.CanBeCanceled);
        Assert.True(
            SpinWait.SpinUntil(
                () => !IsProcessRunning(attemptedIdentity!.ProcessId),
                TimeSpan.FromSeconds(5)),
            "The failed launch left its Java process running.");
    }

    [Fact]
    public async Task LaunchAsync_RequiredPersistenceRejectsAndStopsAnUnidentifiableProcess()
    {
        var instance = CreateInstance();
        var builder = new LongRunningCommandProcessBuilder();
        var coordinator = new MinecraftClientLaunchCoordinator(
            new MinecraftClientMemoryRecommendationService(
                new FixedMemoryProbe(new SystemMemorySnapshot(16 * Gibibyte, 12 * Gibibyte))),
            builder);
        var persistenceCalled = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.LaunchAsync(
            instance,
            new NewMinecraftClientDefaultsSettings(),
            CreateSession("account-a"),
            (_, _) =>
            {
                persistenceCalled = true;
                return Task.CompletedTask;
            }));

        Assert.Contains("process identity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(persistenceCalled);
        Assert.True(await builder.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LaunchAsync_RejectsJavaExecutableChangedAfterItsMajorWasSaved()
    {
        var instance = CreateInstance();
        var java = Path.Combine(_root, "custom-java", "bin", "java.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(java)!);
        await File.WriteAllTextAsync(java, "17");
        instance.GameVersion = "1.20.1";
        instance.InstalledVersionId = "1.20.1";
        instance.JavaExecutablePath = java;
        instance.JavaMajorVersion = 17;
        await File.WriteAllTextAsync(java, "21");
        var builder = new CommandProcessBuilder(exitCode: 0);
        var coordinator = new MinecraftClientLaunchCoordinator(
            new MinecraftClientMemoryRecommendationService(
                new FixedMemoryProbe(new SystemMemorySnapshot(16 * Gibibyte, 12 * Gibibyte))),
            builder,
            new MinecraftClientProcessRecoveryService(),
            new FileContentJavaProbe());

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.LaunchAsync(
            instance,
            new NewMinecraftClientDefaultsSettings(),
            CreateSession("account-a")));

        Assert.Contains("changed after it was saved", error.Message, StringComparison.Ordinal);
        Assert.Contains("saved Java 17", error.Message, StringComparison.Ordinal);
        Assert.Contains("current Java 21", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, builder.CallCount);
    }

    [Fact]
    public void AuthenticatedSession_DoesNotRevealAccessToken()
    {
        const string token = "super-secret-access-token-value";
        var session = new AuthenticatedMinecraftSession(
            "account-a",
            "PlayerOne",
            "0123456789abcdef0123456789abcdef",
            token);

        Assert.DoesNotContain(token, session.ToString(), StringComparison.Ordinal);
        Assert.Equal("PlayerOne (0123456789abcdef0123456789abcdef)", session.ToString());
    }

    private MinecraftClientInstance CreateInstance()
    {
        Directory.CreateDirectory(_root);
        return new MinecraftClientInstance
        {
            Name = "Launch test",
            DirectoryPath = _root,
            GameVersion = "26.2",
            InstalledVersionId = "26.2",
            Loader = MinecraftClientLoader.Vanilla,
            MemoryMode = MinecraftClientMemoryMode.Automatic,
            AccountId = "account-a",
        };
    }

    private static AuthenticatedMinecraftSession CreateSession(string accountId) =>
        new(
            accountId,
            "PlayerOne",
            "0123456789abcdef0123456789abcdef",
            "super-secret-access-token-value");

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedMemoryProbe(SystemMemorySnapshot snapshot) : ISystemMemoryProbe
    {
        public SystemMemorySnapshot GetSnapshot() => snapshot;
    }

    private sealed class FileContentJavaProbe : IMinecraftClientJavaExecutableProbe
    {
        public async Task<int> ProbeMajorVersionAsync(
            string javaExecutablePath,
            CancellationToken cancellationToken = default)
        {
            var value = await File.ReadAllTextAsync(javaExecutablePath, cancellationToken);
            return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed class CommandProcessBuilder(int exitCode) : IMinecraftClientProcessBuilder
    {
        public int CallCount { get; private set; }

        public MinecraftClientMemoryResolution? Memory { get; private set; }

        public Task<Process> BuildAsync(
            MinecraftClientInstance instance,
            AuthenticatedMinecraftSession authenticatedSession,
            MinecraftClientMemoryResolution memory,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Memory = memory;
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"exit {exitCode}");
            return Task.FromResult(new Process { StartInfo = startInfo });
        }
    }

    private sealed class DelayedOutputProcessBuilder : IMinecraftClientProcessBuilder
    {
        public Task<Process> BuildAsync(
            MinecraftClientInstance instance,
            AuthenticatedMinecraftSession authenticatedSession,
            MinecraftClientMemoryResolution memory,
            CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("echo EARLY-OUT & echo EARLY-ERR 1>&2 & ping -n 3 127.0.0.1 >nul");
            return Task.FromResult(new Process { StartInfo = startInfo });
        }
    }

    private sealed class LargeOutputProcessBuilder : IMinecraftClientProcessBuilder
    {
        public Task<Process> BuildAsync(
            MinecraftClientInstance instance,
            AuthenticatedMinecraftSession authenticatedSession,
            MinecraftClientMemoryResolution memory,
            CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("for /L %i in (1,1,12000) do @echo OUT-%i & @echo ERR-%i 1>&2");
            return Task.FromResult(new Process { StartInfo = startInfo });
        }
    }

    private sealed class LongRunningJavaProcessBuilder(string root) : IMinecraftClientProcessBuilder
    {
        public Task<Process> BuildAsync(
            MinecraftClientInstance instance,
            AuthenticatedMinecraftSession authenticatedSession,
            MinecraftClientMemoryResolution memory,
            CancellationToken cancellationToken = default)
        {
            var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec")
                                     ?? Path.Combine(
                                         Environment.GetFolderPath(Environment.SpecialFolder.System),
                                         "cmd.exe");
            var javaPath = Path.Combine(root, "rollback-runtime", "bin", "java.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
            File.Copy(commandInterpreter, javaPath, overwrite: true);
            return Task.FromResult(CreateLongRunningProcess(javaPath));
        }
    }

    private sealed class LongRunningCommandProcessBuilder : IMinecraftClientProcessBuilder
    {
        public TaskCompletionSource<bool> Exited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Process> BuildAsync(
            MinecraftClientInstance instance,
            AuthenticatedMinecraftSession authenticatedSession,
            MinecraftClientMemoryResolution memory,
            CancellationToken cancellationToken = default)
        {
            var process = CreateLongRunningProcess("cmd.exe");
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Exited.TrySetResult(true);
            return Task.FromResult(process);
        }
    }

    private static Process CreateLongRunningProcess(string executablePath)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
        return new Process { StartInfo = startInfo };
    }
}
