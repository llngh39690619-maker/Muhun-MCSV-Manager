using System.Collections.Concurrent;
using System.Diagnostics;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.Core.Tests.Runtime;

public sealed class ServerProcessManagerTests
{
    [Fact]
    public async Task StopAllAsync_StopsEveryIndependentlyManagedServiceProcess()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = CreateManager(factory, maximumLines: 10);
        var first = CreateInstance("service-stop-all-first");
        var second = CreateInstance("service-stop-all-second");
        await manager.StartAsync(first);
        await manager.StartAsync(second);

        await manager.StopAllAsync();

        Assert.Equal(2, factory.Processes.Count);
        Assert.All(factory.Processes, process => Assert.Contains("stop", process.Commands));
        Assert.All(
            new[] { first, second },
            instance =>
            {
                Assert.True(manager.TryGetSnapshot(instance.Id, out var snapshot));
                Assert.Equal(ServerState.Stopped, snapshot.State);
            });
    }

    [Fact]
    public void BuildStartInfo_UsesArgumentListWithoutShellQuoting()
    {
        var instance = CreateInstance("server with spaces");
        instance.ServerJarPath = "paper server.jar";
        File.WriteAllBytes(Path.Combine(instance.DirectoryPath, instance.ServerJarPath), []);
        instance.MinimumMemoryMb = 1536;
        instance.MaximumMemoryMb = 4096;
        instance.JvmArguments = ["-Dmessage=hello world", "-XX:+UseG1GC"];
        instance.ServerArguments = ["nogui", "--demo"];
        var manager = new ServerProcessManager();

        var startInfo = manager.BuildStartInfo(instance);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(instance.JavaExecutablePath, startInfo.FileName);
        Assert.Equal(
            [
                "-Xms1536M",
                "-Xmx4096M",
                "-Dmessage=hello world",
                "-XX:+UseG1GC",
                "-jar",
                "paper server.jar",
                "nogui",
                "--demo",
            ],
            startInfo.ArgumentList);
    }

    [Theory]
    [InlineData(CoreType.Forge)]
    [InlineData(CoreType.NeoForge)]
    public void BuildStartInfo_RejectsInstallerJar(CoreType coreType)
    {
        var instance = CreateInstance("installer");
        instance.CoreType = coreType;
        instance.ServerJarPath = "server-installer.jar";
        var manager = new ServerProcessManager();

        var error = Assert.Throws<InvalidOperationException>(() => manager.BuildStartInfo(instance));

        Assert.Contains("installer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStartInfo_ArgumentFileMode_UsesExactOrderedArgumentListWithoutJarOrGuiMemory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var loaderPath = "libraries/net/neoforged/neoforge/21.1.248/win_args.txt";
        Directory.CreateDirectory(Path.Combine(
            temporaryDirectory.Path,
            "libraries",
            "net",
            "neoforged",
            "neoforge",
            "21.1.248"));
        File.WriteAllText(Path.Combine(temporaryDirectory.Path, "user_jvm_args.txt"), "-Xmx8G");
        File.WriteAllText(
            Path.Combine(temporaryDirectory.Path, loaderPath.Replace('/', Path.DirectorySeparatorChar)),
            "--launchTarget forgeserver");
        var instance = new ServerInstance
        {
            Name = "argument files",
            DirectoryPath = temporaryDirectory.Path,
            ServerJarPath = string.Empty,
            LaunchKind = ServerLaunchKind.JavaArgumentFiles,
            CoreType = CoreType.NeoForge,
            JavaExecutablePath = "java.exe",
            JavaArgumentFilePaths = ["user_jvm_args.txt", loaderPath],
            JvmArguments = ["-Dmust.not.be.injected=true"],
            MinimumMemoryMb = 3072,
            MaximumMemoryMb = 12288,
            ServerArguments = ["--demo"],
        };

        var startInfo = new ServerProcessManager().BuildStartInfo(instance);

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            ["@user_jvm_args.txt", "@" + loaderPath, "--demo", "nogui"],
            startInfo.ArgumentList);
        Assert.DoesNotContain("-jar", startInfo.ArgumentList);
        Assert.DoesNotContain(startInfo.ArgumentList, argument => argument.StartsWith("-Xm"));
        Assert.DoesNotContain("-Dmust.not.be.injected=true", startInfo.ArgumentList);
        Assert.Equal(["--demo"], instance.ServerArguments);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("@user_jvm_args.txt")]
    [InlineData("missing.txt")]
    public void BuildStartInfo_ArgumentFileMode_RejectsUnsafeOrMissingPath(string path)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var instance = new ServerInstance
        {
            DirectoryPath = temporaryDirectory.Path,
            LaunchKind = ServerLaunchKind.JavaArgumentFiles,
            JavaExecutablePath = "java.exe",
            JavaArgumentFilePaths = [path],
        };

        Assert.ThrowsAny<Exception>(() => new ServerProcessManager().BuildStartInfo(instance));
    }

    [Fact]
    public void BuildStartInfo_ArgumentFileBehindIntermediateJunction_IsRejected()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var serverRoot = Path.Combine(temporaryDirectory.Path, "server");
        var outsideRoot = Path.Combine(temporaryDirectory.Path, "outside");
        Directory.CreateDirectory(serverRoot);
        Directory.CreateDirectory(Path.Combine(outsideRoot, "nested"));
        File.WriteAllText(Path.Combine(outsideRoot, "nested", "args.txt"), "-version");
        var linkPath = Path.Combine(serverRoot, "linked");
        ReparsePointTestHelper.CreateDirectoryLink(linkPath, outsideRoot);
        var instance = new ServerInstance
        {
            DirectoryPath = serverRoot,
            LaunchKind = ServerLaunchKind.JavaArgumentFiles,
            JavaExecutablePath = "java.exe",
            JavaArgumentFilePaths = ["linked/nested/args.txt"],
        };

        try
        {
            var error = Assert.Throws<ArgumentException>(
                () => new ServerProcessManager().BuildStartInfo(instance));

            Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<UnauthorizedAccessException>(error.InnerException);
        }
        finally
        {
            Directory.Delete(linkPath);
        }
    }

    [Fact]
    public async Task AutoRestart_UsesDeepCopiedArgumentFileLaunchSnapshot()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "original.txt"), "-version");
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "mutated.txt"), "-version");
        var factory = new FakeServerProcessFactory();
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                AutoRestartDelay = TimeSpan.FromMilliseconds(5),
            },
            factory);
        var instance = new ServerInstance
        {
            Name = "snapshot",
            DirectoryPath = temporaryDirectory.Path,
            LaunchKind = ServerLaunchKind.JavaArgumentFiles,
            JavaExecutablePath = "java.exe",
            JavaArgumentFilePaths = ["original.txt"],
            ServerArguments = ["original-server-argument"],
            AutoRestart = true,
        };
        await manager.StartAsync(instance);

        instance.JavaArgumentFilePaths[0] = "mutated.txt";
        instance.ServerArguments[0] = "mutated-server-argument";
        instance.LaunchKind = ServerLaunchKind.ExecutableJar;
        factory.Processes[0].Complete(1);
        await EventuallyAsync(() => factory.Processes.Count == 2);

        Assert.Equal(
            ["@original.txt", "original-server-argument"],
            factory.Processes[1].StartInfo!.ArgumentList);
        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task AutoRestart_PreparationHookCanUpdatePrivateRestartSnapshot()
    {
        var factory = new FakeServerProcessFactory();
        ServerInstance? preparedSnapshot = null;
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                AutoRestartDelay = TimeSpan.FromMilliseconds(5),
                PrepareAutoRestartAsync = (snapshot, _) =>
                {
                    preparedSnapshot = snapshot;
                    snapshot.JavaExecutablePath = "prepared-java.exe";
                    snapshot.Port = 25566;
                    return Task.CompletedTask;
                },
            },
            factory);
        var instance = CreateInstance("prepared-restart");
        instance.AutoRestart = true;
        instance.Port = 25565;
        await manager.StartAsync(instance);

        factory.Processes[0].Complete(1);
        await EventuallyAsync(() => factory.Processes.Count == 2);

        Assert.NotNull(preparedSnapshot);
        Assert.NotSame(instance, preparedSnapshot);
        Assert.Equal(25566, preparedSnapshot.Port);
        Assert.Equal("prepared-java.exe", factory.Processes[1].StartInfo!.FileName);
        Assert.Equal(25565, instance.Port);
        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task AutoRestart_InvokesLockedPrepareStartHookForEveryProcessLaunch()
    {
        var factory = new FakeServerProcessFactory();
        var preparationCount = 0;
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                AutoRestartDelay = TimeSpan.FromMilliseconds(5),
                PrepareStartAsync = (snapshot, _) =>
                {
                    var attempt = Interlocked.Increment(ref preparationCount);
                    snapshot.ServerArguments = [$"--prepared-attempt={attempt}"];
                    return Task.CompletedTask;
                },
            },
            factory);
        var instance = CreateInstance("prepare-every-launch");
        instance.AutoRestart = true;

        await manager.StartAsync(instance);
        factory.Processes[0].Complete(1);
        await EventuallyAsync(() => factory.Processes.Count == 2);

        Assert.Equal(2, Volatile.Read(ref preparationCount));
        Assert.Contains("--prepared-attempt=1", factory.Processes[0].StartInfo!.ArgumentList);
        Assert.Contains("--prepared-attempt=2", factory.Processes[1].StartInfo!.ArgumentList);
        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task AutoRestart_DelayProviderReceivesExactExitedSession()
    {
        var factory = new FakeServerProcessFactory();
        Guid observedInstance = Guid.Empty;
        Guid observedSession = Guid.Empty;
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                GetAutoRestartDelayAsync = (instanceId, sessionId, _) =>
                {
                    observedInstance = instanceId;
                    observedSession = sessionId;
                    return Task.FromResult(TimeSpan.FromMilliseconds(1));
                },
            },
            factory);
        var instance = CreateInstance("dynamic-restart-delay");
        instance.AutoRestart = true;
        var originalSession = await manager.StartAsync(instance);

        factory.Processes[0].Complete(1);
        await EventuallyAsync(() => factory.Processes.Count == 2);

        Assert.Equal(instance.Id, observedInstance);
        Assert.Equal(originalSession, observedSession);
        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task StopAfterCrashDuringDelay_CancelsTheQueuedAutomaticRestart()
    {
        var factory = new FakeServerProcessFactory();
        var delayWasSelected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                GetAutoRestartDelayAsync = (_, _, _) =>
                {
                    delayWasSelected.TrySetResult();
                    return Task.FromResult(TimeSpan.FromMilliseconds(250));
                },
            },
            factory);
        var instance = CreateInstance("cancel-queued-restart");
        instance.AutoRestart = true;
        await manager.StartAsync(instance);

        Assert.Single(factory.Processes).Complete(17);
        await delayWasSelected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // No process is alive at this point, but StopAsync still advances the instance generation.
        // That explicit stop intent must invalidate the already queued restart.
        Assert.False(await manager.StopAsync(instance.Id));
        await Task.Delay(400);

        Assert.Single(factory.Processes);
        Assert.True(manager.TryGetSnapshot(instance.Id, out var snapshot));
        Assert.Equal(ServerState.Stopped, snapshot.State);
    }

    [Fact]
    public async Task StopWhileAutoRestartPreparationIsBlocked_InvalidatesRestartAtCommit()
    {
        var factory = new FakeServerProcessFactory();
        var preparationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationExited = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                AutoRestartDelay = TimeSpan.Zero,
                PrepareAutoRestartAsync = async (_, cancellationToken) =>
                {
                    preparationEntered.TrySetResult();
                    await releasePreparation.Task.WaitAsync(cancellationToken);
                    preparationExited.TrySetResult();
                },
            },
            factory);
        var instance = CreateInstance("stop-during-restart-preparation");
        instance.AutoRestart = true;
        await manager.StartAsync(instance);

        Assert.Single(factory.Processes).Complete(17);
        await preparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(await manager.StopAsync(instance.Id));
        releasePreparation.TrySetResult();
        await preparationExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.Single(factory.Processes);
        Assert.True(manager.TryGetSnapshot(instance.Id, out var snapshot));
        Assert.Equal(ServerState.Stopped, snapshot.State);
    }

    [Fact]
    public async Task LivePolicyDisabledDuringAutoRestartPreparation_PreventsRestart()
    {
        var factory = new FakeServerProcessFactory();
        var preparationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disabledPolicyObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var restartEnabled = 1;
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                AutoRestartDelay = TimeSpan.Zero,
                ShouldAutoRestartAsync = (_, _) =>
                {
                    var enabled = Volatile.Read(ref restartEnabled) != 0;
                    if (!enabled)
                    {
                        disabledPolicyObserved.TrySetResult();
                    }

                    return Task.FromResult(enabled);
                },
                PrepareAutoRestartAsync = async (_, cancellationToken) =>
                {
                    preparationEntered.TrySetResult();
                    await releasePreparation.Task.WaitAsync(cancellationToken);
                },
            },
            factory);
        var instance = CreateInstance("policy-change-during-restart-preparation");
        instance.AutoRestart = true;
        await manager.StartAsync(instance);

        Assert.Single(factory.Processes).Complete(17);
        await preparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Volatile.Write(ref restartEnabled, 0);
        releasePreparation.TrySetResult();
        await disabledPolicyObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.Single(factory.Processes);
        Assert.True(manager.TryGetSnapshot(instance.Id, out var snapshot));
        Assert.Equal(ServerState.Crashed, snapshot.State);
    }

    [Fact]
    public async Task FailedAutomaticRestart_PublishesFaultedOnlyForNewSession()
    {
        var factory = new FakeServerProcessFactory();
        var stateChanges = new ConcurrentQueue<ServerStateChangedEventArgs>();
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                AutoRestartDelay = TimeSpan.Zero,
            },
            factory);
        manager.StateChanged += (_, eventArgs) => stateChanges.Enqueue(eventArgs);
        var instance = CreateInstance("failed-automatic-restart");
        instance.AutoRestart = true;
        var originalSession = await manager.StartAsync(instance);
        factory.StartResult = false;

        Assert.Single(factory.Processes).Complete(17);

        await EventuallyAsync(() => stateChanges.Any(change => change.State == ServerState.Faulted));
        await Task.Delay(50);
        var faulted = stateChanges.Where(change => change.State == ServerState.Faulted).ToArray();
        var failedRestart = Assert.Single(faulted);
        Assert.NotEqual(originalSession, failedRestart.SessionId);
        Assert.Equal(2, factory.Processes.Count);
        Assert.True(factory.Processes[1].Disposed);
    }

    [Fact]
    public async Task DisposeAsync_WhenForcedKillDoesNotExit_IsBoundedAndDetachesMonitor()
    {
        var factory = new FakeServerProcessFactory
        {
            ExitWhenStopCommandIsWritten = false,
            IgnoreKill = true,
        };
        var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromMilliseconds(20),
                ForcedKillWaitTimeout = TimeSpan.FromMilliseconds(20),
                MonitorDrainTimeout = TimeSpan.FromMilliseconds(30),
            },
            factory);
        var instance = CreateInstance("dispose-unkillable-process");
        await manager.StartAsync(instance);
        var process = Assert.Single(factory.Processes);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => manager.DisposeAsync().AsTask());
        stopwatch.Stop();

        Assert.True(process.KillCalled);
        Assert.True(process.EntireProcessTreeKilled);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.StartAsync(instance));

        // The detached monitor retains ownership until the real process exit arrives, then still
        // performs its process and directory-lock cleanup without calling disposed subscribers.
        process.Complete(-1);
        await EventuallyAsync(() => process.Disposed);
        await manager.DisposeAsync();
    }

    [Fact]
    public void Constructor_RejectsUnboundedMonitorDrainTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                MonitorDrainTimeout = Timeout.InfiniteTimeSpan,
            }));
    }

    [Fact]
    public async Task MultipleInstances_HaveIndependentCommandsLogsAndEventIdentity()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = CreateManager(factory, maximumLines: 10);
        var first = CreateInstance("first");
        var second = CreateInstance("second");
        var received = new List<ConsoleLineReceivedEventArgs>();
        manager.ConsoleLineReceived += (_, eventArgs) => received.Add(eventArgs);

        var firstSession = await manager.StartAsync(first);
        var secondSession = await manager.StartAsync(second);
        var firstProcess = factory.Processes[0];
        var secondProcess = factory.Processes[1];
        firstProcess.EmitOutput("first-line");
        secondProcess.EmitError("second-line");
        await manager.SendCommandAsync(first.Id, "say first");
        await manager.SendCommandAsync(second.Id, "say second");

        Assert.Equal(["say first"], firstProcess.Commands);
        Assert.Equal(["say second"], secondProcess.Commands);
        Assert.Collection(
            received,
            item =>
            {
                Assert.Equal(first.Id, item.InstanceId);
                Assert.Equal(firstSession, item.SessionId);
                Assert.Equal(ConsoleStream.StandardOutput, item.Line.Stream);
            },
            item =>
            {
                Assert.Equal(second.Id, item.InstanceId);
                Assert.Equal(secondSession, item.SessionId);
                Assert.Equal(ConsoleStream.StandardError, item.Line.Stream);
            });
        Assert.Equal("first-line", Assert.Single(manager.GetRecentConsoleLines(first.Id)).Text);
        Assert.Equal("second-line", Assert.Single(manager.GetRecentConsoleLines(second.Id)).Text);

        await Task.WhenAll(manager.StopAsync(first.Id), manager.StopAsync(second.Id));
        await EventuallyAsync(() =>
            manager.TryGetSnapshot(first.Id, out var firstSnapshot)
            && firstSnapshot.State == ServerState.Stopped
            && manager.TryGetSnapshot(second.Id, out var secondSnapshot)
            && secondSnapshot.State == ServerState.Stopped);
    }

    [Fact]
    public async Task DirectoryLock_TwoManagersRejectSameDirectoryUntilFirstStops()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var firstFactory = new FakeServerProcessFactory();
        var secondFactory = new FakeServerProcessFactory();
        var firstPrepareCount = 0;
        var secondPrepareCount = 0;
        await using var firstManager = CreateManager(
            firstFactory,
            maximumLines: 10,
            prepareStart: (_, _) =>
            {
                Interlocked.Increment(ref firstPrepareCount);
                return Task.CompletedTask;
            });
        await using var secondManager = CreateManager(
            secondFactory,
            maximumLines: 10,
            prepareStart: (_, _) =>
            {
                Interlocked.Increment(ref secondPrepareCount);
                return Task.CompletedTask;
            });
        var first = CreateInstance("first-owner", temporaryDirectory.Path);
        var second = CreateInstance("second-owner", temporaryDirectory.Path);

        await firstManager.StartAsync(first);

        var error = await Assert.ThrowsAsync<ServerDirectoryLockException>(
            () => secondManager.StartAsync(second));
        Assert.Equal(Path.GetFullPath(temporaryDirectory.Path), error.ServerDirectoryPath);
        Assert.EndsWith(
            ".minecraft-server-manager.lock",
            error.LockFilePath,
            StringComparison.Ordinal);
        Assert.Contains("只更換 Port 並不安全", error.Message);
        Assert.Equal(1, firstPrepareCount);
        Assert.Equal(0, secondPrepareCount);
        Assert.Empty(secondFactory.Processes);

        Assert.True(await firstManager.StopAsync(first.Id));
        Assert.True(File.Exists(error.LockFilePath));

        await secondManager.StartAsync(second);
        Assert.Equal(1, secondPrepareCount);
        Assert.Single(secondFactory.Processes);
        await secondManager.StopAsync(second.Id);
    }

    [Fact]
    public async Task CustomDirectoryLease_IsRetainedForCompleteProcessSession()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var factory = new FakeServerProcessFactory();
        TrackingDisposable? lease = null;
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                AcquireDirectoryLease = _ => lease = new TrackingDisposable(),
            },
            factory);
        var instance = CreateInstance("custom-directory-lease", temporaryDirectory.Path);

        await manager.StartAsync(instance);

        Assert.NotNull(lease);
        Assert.False(lease.IsDisposed);
        await manager.StopAsync(instance.Id);
        Assert.True(lease.IsDisposed);
    }

    [Fact]
    public async Task ExecuteWhileInactive_SerializesAgainstStartSessionCommit()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var factory = new FakeServerProcessFactory();
        var preparationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                PrepareStartAsync = async (_, cancellationToken) =>
                {
                    preparationEntered.TrySetResult();
                    await releasePreparation.Task.WaitAsync(cancellationToken);
                },
            },
            factory);
        var instance = CreateInstance("inactive-mutation-gate", temporaryDirectory.Path);
        var start = manager.StartAsync(instance);
        await preparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var mutationCalls = 0;
        var mutation = manager.ExecuteWhileInactiveAsync(
            instance.Id,
            _ =>
            {
                Interlocked.Increment(ref mutationCalls);
                return Task.CompletedTask;
            });

        await Task.Delay(30);
        Assert.False(mutation.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref mutationCalls));
        releasePreparation.TrySetResult();
        await start;

        await Assert.ThrowsAsync<InvalidOperationException>(() => mutation);
        Assert.Equal(0, Volatile.Read(ref mutationCalls));
        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task PrepareStart_RunsBeforeLaunchResolutionAndUsesPrivateSnapshotChanges()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var factory = new FakeServerProcessFactory();
        bool? preparedDiagnosticSeparation = null;
        await using var manager = CreateManager(
            factory,
            maximumLines: 10,
            prepareStart: (snapshot, _) =>
            {
                preparedDiagnosticSeparation = snapshot.SeparateDiagnosticOutput;
                snapshot.Port = 25566;
                snapshot.JavaExecutablePath = "prepared-java.exe";
                snapshot.ServerArguments = [$"--prepared-port={snapshot.Port}"];
                return Task.CompletedTask;
            });
        var instance = CreateInstance("prepared-start", temporaryDirectory.Path);
        instance.Port = 25565;
        instance.ServerArguments = ["original-argument"];
        instance.SeparateDiagnosticOutput = true;

        await manager.StartAsync(instance);

        var startInfo = Assert.Single(factory.Processes).StartInfo!;
        Assert.Equal("prepared-java.exe", startInfo.FileName);
        Assert.Contains("--prepared-port=25566", startInfo.ArgumentList);
        Assert.DoesNotContain("original-argument", startInfo.ArgumentList);
        Assert.True(preparedDiagnosticSeparation);
        Assert.Equal(25565, instance.Port);
        Assert.Equal(["original-argument"], instance.ServerArguments);
        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task PreparedStartAborted_PostPrepareLivePolicyDisable_CleansOnceWithoutRestarting()
    {
        var factory = new FakeServerProcessFactory();
        var restartPreparationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestartPreparation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupObserved = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var prepareCount = 0;
        var cleanupCount = 0;
        var restartEnabled = 1;
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                AutoRestartDelay = TimeSpan.Zero,
                ShouldAutoRestartAsync = (_, _) =>
                    Task.FromResult(Volatile.Read(ref restartEnabled) != 0),
                PrepareStartAsync = async (_, cancellationToken) =>
                {
                    if (Interlocked.Increment(ref prepareCount) == 1)
                    {
                        return;
                    }

                    restartPreparationEntered.TrySetResult();
                    await releaseRestartPreparation.Task.WaitAsync(cancellationToken);
                },
                PreparedStartAborted = instanceId =>
                {
                    Interlocked.Increment(ref cleanupCount);
                    cleanupObserved.TrySetResult(instanceId);
                },
            },
            factory);
        var instance = CreateInstance("post-prepare-policy-disable");
        instance.AutoRestart = true;

        await manager.StartAsync(instance);
        Assert.Equal(0, Volatile.Read(ref cleanupCount));
        Assert.Single(factory.Processes).Complete(17);
        await restartPreparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Volatile.Write(ref restartEnabled, 0);
        releaseRestartPreparation.TrySetResult();
        var cleanedInstanceId = await cleanupObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(30);

        Assert.Equal(instance.Id, cleanedInstanceId);
        Assert.Equal(2, Volatile.Read(ref prepareCount));
        Assert.Equal(1, Volatile.Read(ref cleanupCount));
        Assert.Single(factory.Processes);
        Assert.True(manager.TryGetSnapshot(instance.Id, out var snapshot));
        Assert.Equal(ServerState.Crashed, snapshot.State);
    }

    [Fact]
    public async Task PreparedStartAborted_ManualStartSuccess_DoesNotClean()
    {
        var factory = new FakeServerProcessFactory();
        var prepareCount = 0;
        var cleanupCount = 0;
        await using var manager = CreateManager(
            factory,
            maximumLines: 10,
            prepareStart: (_, _) =>
            {
                Interlocked.Increment(ref prepareCount);
                return Task.CompletedTask;
            },
            preparedStartAborted: _ => Interlocked.Increment(ref cleanupCount));
        var instance = CreateInstance("successful-prepared-start");

        await manager.StartAsync(instance);

        Assert.Equal(1, Volatile.Read(ref prepareCount));
        Assert.Equal(0, Volatile.Read(ref cleanupCount));
        Assert.Single(factory.Processes);
        await manager.StopAsync(instance.Id);
        Assert.Equal(0, Volatile.Read(ref cleanupCount));
    }

    [Fact]
    public async Task PreparedStartAborted_ProcessLaunchFailure_CleansOnceAndSwallowsCleanupError()
    {
        var factory = new FakeServerProcessFactory { StartResult = false };
        var cleanupCount = 0;
        await using var manager = CreateManager(
            factory,
            maximumLines: 10,
            prepareStart: (_, _) => Task.CompletedTask,
            preparedStartAborted: _ =>
            {
                Interlocked.Increment(ref cleanupCount);
                throw new ApplicationException("cleanup failed");
            });
        var instance = CreateInstance("failed-prepared-process-start");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StartAsync(instance));

        Assert.Contains("did not start", error.Message);
        Assert.Equal(1, Volatile.Read(ref cleanupCount));
        Assert.True(Assert.Single(factory.Processes).Disposed);
    }

    [Fact]
    public async Task PreparedStartAborted_PostPrepareCancellation_CleansOnceWithoutCreatingProcess()
    {
        var factory = new FakeServerProcessFactory();
        using var cancellation = new CancellationTokenSource();
        var cleanupCount = 0;
        await using var manager = CreateManager(
            factory,
            maximumLines: 10,
            prepareStart: (_, _) =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
            preparedStartAborted: _ => Interlocked.Increment(ref cleanupCount));
        var instance = CreateInstance("cancelled-prepared-start");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.StartAsync(instance, cancellation.Token));

        Assert.Equal(1, Volatile.Read(ref cleanupCount));
        Assert.Empty(factory.Processes);
    }

    [Fact]
    public async Task PreparedStartAborted_PostPrepareBuildFailure_CleansOnceWithoutCreatingProcess()
    {
        var factory = new FakeServerProcessFactory();
        var cleanupCount = 0;
        await using var manager = CreateManager(
            factory,
            maximumLines: 10,
            prepareStart: (snapshot, _) =>
            {
                snapshot.LaunchKind = ServerLaunchKind.JavaArgumentFiles;
                snapshot.JavaArgumentFilePaths = ["missing-arguments.txt"];
                return Task.CompletedTask;
            },
            preparedStartAborted: _ => Interlocked.Increment(ref cleanupCount));
        var instance = CreateInstance("invalid-prepared-launch-definition");

        await Assert.ThrowsAnyAsync<Exception>(() => manager.StartAsync(instance));

        Assert.Equal(1, Volatile.Read(ref cleanupCount));
        Assert.Empty(factory.Processes);
    }

    [Fact]
    public async Task DirectoryLock_DifferentDirectoriesCanRunInParallelAcrossManagers()
    {
        using var firstDirectory = new TemporaryDirectory();
        using var secondDirectory = new TemporaryDirectory();
        var firstFactory = new FakeServerProcessFactory();
        var secondFactory = new FakeServerProcessFactory();
        await using var firstManager = CreateManager(firstFactory, maximumLines: 10);
        await using var secondManager = CreateManager(secondFactory, maximumLines: 10);
        var first = CreateInstance("parallel-one", firstDirectory.Path);
        var second = CreateInstance("parallel-two", secondDirectory.Path);

        await Task.WhenAll(firstManager.StartAsync(first), secondManager.StartAsync(second));

        Assert.Single(firstFactory.Processes);
        Assert.Single(secondFactory.Processes);
        await Task.WhenAll(
            firstManager.StopAsync(first.Id),
            secondManager.StopAsync(second.Id));
    }

    [Fact]
    public async Task DirectoryLock_PathAliasStillRejectsSamePhysicalDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var serverDirectory = Path.Combine(temporaryDirectory.Path, "server");
        var aliasDirectory = Path.Combine(temporaryDirectory.Path, "server-alias");
        Directory.CreateDirectory(serverDirectory);
        ReparsePointTestHelper.CreateDirectoryLink(aliasDirectory, serverDirectory);
        try
        {
            var firstFactory = new FakeServerProcessFactory();
            var secondFactory = new FakeServerProcessFactory();
            await using var firstManager = CreateManager(firstFactory, maximumLines: 10);
            await using var secondManager = CreateManager(secondFactory, maximumLines: 10);
            var first = CreateInstance("physical-path", serverDirectory);
            var alias = CreateInstance("alias-path", aliasDirectory);

            await firstManager.StartAsync(first);

            await Assert.ThrowsAsync<ServerDirectoryLockException>(
                () => secondManager.StartAsync(alias));
            Assert.Empty(secondFactory.Processes);
            await firstManager.StopAsync(first.Id);
        }
        finally
        {
            Directory.Delete(aliasDirectory);
        }
    }

    [Fact]
    public async Task DirectoryLock_ProcessStartFailureReleasesLockForAnotherManager()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var failingFactory = new FakeServerProcessFactory { StartResult = false };
        var replacementFactory = new FakeServerProcessFactory();
        await using var failingManager = CreateManager(failingFactory, maximumLines: 10);
        await using var replacementManager = CreateManager(replacementFactory, maximumLines: 10);
        var failing = CreateInstance("failed-start", temporaryDirectory.Path);
        var replacement = CreateInstance("replacement", temporaryDirectory.Path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingManager.StartAsync(failing));
        Assert.Contains("did not start", error.Message);
        Assert.True(Assert.Single(failingFactory.Processes).Disposed);

        await replacementManager.StartAsync(replacement);
        Assert.Single(replacementFactory.Processes);
        await replacementManager.StopAsync(replacement.Id);
    }

    [Fact]
    public async Task DirectoryLock_PrepareStartFailureReleasesLockForAnotherManager()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var failingFactory = new FakeServerProcessFactory();
        var replacementFactory = new FakeServerProcessFactory();
        await using var failingManager = CreateManager(
            failingFactory,
            maximumLines: 10,
            prepareStart: (_, _) => throw new InvalidOperationException("preparation failed"));
        await using var replacementManager = CreateManager(replacementFactory, maximumLines: 10);
        var failing = CreateInstance("failed-preparation", temporaryDirectory.Path);
        var replacement = CreateInstance("replacement-after-preparation", temporaryDirectory.Path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingManager.StartAsync(failing));
        Assert.Equal("preparation failed", error.Message);
        Assert.Empty(failingFactory.Processes);

        await replacementManager.StartAsync(replacement);
        Assert.Single(replacementFactory.Processes);
        await replacementManager.StopAsync(replacement.Id);
    }

    [Fact]
    public async Task ConsoleHistory_IsBoundedToNewestLines()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = CreateManager(factory, maximumLines: 3);
        var instance = CreateInstance("bounded");
        await manager.StartAsync(instance);
        var process = Assert.Single(factory.Processes);

        for (var index = 0; index < 5; index++)
        {
            process.EmitOutput($"line-{index}");
        }

        Assert.Equal(
            ["line-2", "line-3", "line-4"],
            manager.GetRecentConsoleLines(instance.Id).Select(line => line.Text));
        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task ConsoleClassification_IsAttachedToEventAndRetainedHistory()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = CreateManager(factory, maximumLines: 10);
        var instance = CreateInstance("classified-console");
        var received = new List<ConsoleLineReceivedEventArgs>();
        manager.ConsoleLineReceived += (_, eventArgs) => received.Add(eventArgs);
        var sessionId = await manager.StartAsync(instance);
        var process = Assert.Single(factory.Processes);

        process.EmitError(
            "2026-08-18T10:30:52.954706900Z Server thread ERROR An exception occurred processing Appender DebugFile");
        process.EmitError(
            "\tat org.apache.logging.log4j.core.config.AppenderControl.tryCallAppender(AppenderControl.java:165)");

        var history = manager.GetRecentConsoleLines(instance.Id);
        Assert.Equal(2, history.Count);
        var root = history[0];
        var continuation = history[1];
        Assert.Equal(instance.Id, root.ServerInstanceId);
        Assert.Equal(sessionId, root.SessionId);
        Assert.Equal(ConsoleLineSeverity.Error, root.Severity);
        Assert.True(root.StartsDiagnostic);
        Assert.NotNull(root.DiagnosticId);
        Assert.Equal(root.DiagnosticId, continuation.DiagnosticId);
        Assert.True(continuation.IsDiagnosticContinuation);
        Assert.Equal(root, received[0].Line);
        Assert.Equal(continuation, received[1].Line);
        Assert.All(received, item => Assert.Equal(sessionId, item.SessionId));

        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task ConsoleClassification_KeepsStreamContextsIndependent()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = CreateManager(factory, maximumLines: 10);
        var instance = CreateInstance("stream-classification");
        await manager.StartAsync(instance);
        var process = Assert.Single(factory.Processes);

        process.EmitError("[12:00:00] [Server thread/ERROR]: stderr root");
        process.EmitOutput("\tat example.Stdout.run(Stdout.java:1)");
        process.EmitError("\tat example.Stderr.run(Stderr.java:1)");

        var lines = manager.GetRecentConsoleLines(instance.Id);
        Assert.Equal(ConsoleLineSeverity.Error, lines[0].Severity);
        Assert.Equal(ConsoleLineSeverity.Information, lines[1].Severity);
        Assert.Null(lines[1].DiagnosticId);
        Assert.Equal(ConsoleLineSeverity.Error, lines[2].Severity);
        Assert.Equal(lines[0].DiagnosticId, lines[2].DiagnosticId);

        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task ClearConsole_AlsoResetsActiveDiagnosticContext()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = CreateManager(factory, maximumLines: 10);
        var instance = CreateInstance("clear-classification");
        await manager.StartAsync(instance);
        var process = Assert.Single(factory.Processes);
        process.EmitError("[12:00:00] [Server thread/ERROR]: root");

        Assert.True(manager.ClearConsole(instance.Id));
        process.EmitError("\tat example.Orphan.run(Orphan.java:1)");

        var orphan = Assert.Single(manager.GetRecentConsoleLines(instance.Id));
        Assert.Equal(ConsoleLineSeverity.Unclassified, orphan.Severity);
        Assert.Null(orphan.DiagnosticId);
        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task BoundedHistoryEviction_DoesNotOwnClassifierState()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = CreateManager(factory, maximumLines: 1);
        var instance = CreateInstance("bounded-classification");
        await manager.StartAsync(instance);
        var process = Assert.Single(factory.Processes);
        process.EmitError("[12:00:00] [Server thread/ERROR]: root");
        process.EmitError("\tat example.Frame.run(Frame.java:1)");

        var retained = Assert.Single(manager.GetRecentConsoleLines(instance.Id));
        Assert.Equal(ConsoleLineSeverity.Error, retained.Severity);
        Assert.True(retained.IsDiagnosticContinuation);
        Assert.NotNull(retained.DiagnosticId);
        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task StopAsync_KillsWholeTreeAfterTimeout_AndDoesNotAutoRestart()
    {
        var factory = new FakeServerProcessFactory
        {
            ExitWhenStopCommandIsWritten = false,
        };
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.Zero,
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                AutoRestartDelay = TimeSpan.FromMilliseconds(5),
            },
            factory);
        var instance = CreateInstance("force-stop");
        instance.AutoRestart = true;
        await manager.StartAsync(instance);
        var process = Assert.Single(factory.Processes);

        var stopResult = await manager.StopDetailedAsync(instance.Id);
        Assert.True(stopResult.WasRunning);
        Assert.Equal(ServerStopMode.Forced, stopResult.Mode);
        Assert.NotNull(stopResult.SessionId);
        await EventuallyAsync(() =>
            manager.TryGetSnapshot(instance.Id, out var snapshot)
            && snapshot.State == ServerState.Stopped);
        await Task.Delay(30);

        Assert.True(process.KillCalled);
        Assert.True(process.EntireProcessTreeKilled);
        Assert.Equal(["stop"], process.Commands);
        var timeoutWarning = Assert.Single(
            manager.GetRecentConsoleLines(instance.Id),
            line => line.Text.Contains("Graceful stop timed out", StringComparison.Ordinal));
        Assert.Equal(ConsoleStream.System, timeoutWarning.Stream);
        Assert.Equal(ConsoleLineSeverity.Warning, timeoutWarning.Severity);
        Assert.True(timeoutWarning.StartsDiagnostic);
        Assert.Single(factory.Processes);
    }

    [Fact]
    public async Task StopDetailedAsync_ReportsGracefulShutdownAndNotRunning()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = CreateManager(factory, maximumLines: 10);
        var instance = CreateInstance("detailed-graceful");
        var sessionId = await manager.StartAsync(instance);

        var graceful = await manager.StopDetailedAsync(instance.Id);

        Assert.True(graceful.WasRunning);
        Assert.Equal(sessionId, graceful.SessionId);
        Assert.Equal(ServerStopMode.Graceful, graceful.Mode);
        Assert.False((await manager.StopDetailedAsync(instance.Id)).WasRunning);
        Assert.Equal(
            ServerStopMode.NotRunning,
            (await manager.StopDetailedAsync(Guid.NewGuid())).Mode);
    }

    [Fact]
    public async Task StopAsync_UsesIndependentPerInstanceGracefulCommands()
    {
        var factory = new FakeServerProcessFactory
        {
            ExitWhenStopCommandIsWritten = false,
        };
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.Zero,
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
            },
            factory);
        var minecraft = CreateInstance("per-instance-stop");
        var velocity = CreateInstance("per-instance-shutdown");
        velocity.StopCommand = "shutdown";
        await manager.StartAsync(minecraft);
        await manager.StartAsync(velocity);

        await Task.WhenAll(
            manager.StopDetailedAsync(minecraft.Id),
            manager.StopDetailedAsync(velocity.Id));

        Assert.Equal(["stop"], factory.Processes[0].Commands);
        Assert.Equal(["shutdown"], factory.Processes[1].Commands);
    }

    [Fact]
    public async Task StopAsync_NullInstanceOverrideUsesLegacyOptionsFallback()
    {
        var factory = new FakeServerProcessFactory
        {
            ExitWhenStopCommandIsWritten = false,
        };
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.Zero,
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                StopCommand = "legacy-halt",
            },
            factory);
        var instance = CreateInstance("legacy-stop-fallback");
        Assert.Null(instance.StopCommand);
        await manager.StartAsync(instance);

        var result = await manager.StopDetailedAsync(instance.Id);

        Assert.True(result.WasRunning);
        Assert.Equal(ServerStopMode.Forced, result.Mode);
        Assert.Equal(["legacy-halt"], Assert.Single(factory.Processes).Commands);
    }

    [Fact]
    public async Task TypedPerInstanceStopCommand_MarksManualStopAndSuppressesAutoRestart()
    {
        var livePolicyCalls = 0;
        var factory = new FakeServerProcessFactory
        {
            ExitWhenStopCommandIsWritten = false,
        };
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                AutoRestartDelay = TimeSpan.FromMilliseconds(5),
                ShouldAutoRestartAsync = (_, _) =>
                {
                    Interlocked.Increment(ref livePolicyCalls);
                    return Task.FromResult(true);
                },
            },
            factory);
        var instance = CreateInstance("typed-shutdown");
        instance.StopCommand = "shutdown";
        instance.AutoRestart = true;
        await manager.StartAsync(instance);
        var process = Assert.Single(factory.Processes);

        await manager.SendCommandAsync(instance.Id, " shutdown ");
        process.Complete(17);

        await EventuallyAsync(() =>
            manager.TryGetSnapshot(instance.Id, out var snapshot)
            && snapshot.State == ServerState.Stopped);
        await Task.Delay(30);
        Assert.Equal(["shutdown"], process.Commands);
        Assert.Single(factory.Processes);
        Assert.Equal(0, livePolicyCalls);
    }

    [Fact]
    public async Task StopCommand_IsDeepCopiedForRunningSession()
    {
        var factory = new FakeServerProcessFactory
        {
            ExitWhenStopCommandIsWritten = false,
        };
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.Zero,
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
            },
            factory);
        var instance = CreateInstance("stop-snapshot");
        instance.StopCommand = "shutdown";
        await manager.StartAsync(instance);

        instance.StopCommand = "halt";
        await manager.StopAsync(instance.Id);

        Assert.Equal(["shutdown"], Assert.Single(factory.Processes).Commands);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("stop\nreload")]
    [InlineData("stop\0reload")]
    public async Task StartAsync_RejectsInvalidPerInstanceStopCommand(string command)
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = CreateManager(factory, maximumLines: 10);
        var instance = CreateInstance("invalid-stop-command");
        instance.StopCommand = command;

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => manager.StartAsync(instance));

        Assert.Contains("stop command", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(factory.Processes);
    }

    [Fact]
    public async Task StartAsync_RevalidatesStopCommandAfterPreparationHook()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = CreateManager(
            factory,
            maximumLines: 10,
            prepareStart: (snapshot, _) =>
            {
                snapshot.StopCommand = "shutdown\nreload";
                return Task.CompletedTask;
            });
        var instance = CreateInstance("prepared-invalid-stop-command");

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => manager.StartAsync(instance));

        Assert.Contains("stop command", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(factory.Processes);
    }

    [Fact]
    public async Task StopAsync_CompletesExitCleanupBeforeImmediateRestart()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = CreateManager(factory, maximumLines: 10);
        var instance = CreateInstance("immediate-restart");
        var firstSession = await manager.StartAsync(instance);

        Assert.True(await manager.StopAsync(instance.Id));
        Assert.True(manager.TryGetSnapshot(instance.Id, out var stopped));
        Assert.Equal(ServerState.Stopped, stopped.State);

        var secondSession = await manager.StartAsync(instance);
        Assert.NotEqual(firstSession, secondSession);
        Assert.Equal(2, factory.Processes.Count);
        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task CrashAutoRestart_IgnoresLateOutputFromOldSession()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                MaximumRetainedConsoleLines = 20,
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                AutoRestartDelay = TimeSpan.FromMilliseconds(5),
            },
            factory);
        var instance = CreateInstance("restart");
        instance.AutoRestart = true;
        var firstSession = await manager.StartAsync(instance);
        var firstProcess = Assert.Single(factory.Processes);
        firstProcess.Complete(17);

        await EventuallyAsync(() => factory.Processes.Count == 2);
        var secondProcess = factory.Processes[1];
        Assert.True(manager.TryGetSnapshot(instance.Id, out var restarted));
        Assert.Equal(ServerState.Running, restarted.State);
        Assert.NotEqual(firstSession, restarted.SessionId);

        firstProcess.EmitLateOutput("stale-session-line");
        secondProcess.EmitOutput("current-session-line");

        var text = manager.GetRecentConsoleLines(instance.Id).Select(line => line.Text).ToArray();
        Assert.DoesNotContain("stale-session-line", text);
        Assert.Contains("current-session-line", text);
        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task AutoRestart_StartsWithFreshConsoleClassifierState()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                MaximumRetainedConsoleLines = 20,
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                AutoRestartDelay = TimeSpan.FromMilliseconds(5),
            },
            factory);
        var instance = CreateInstance("restart-classifier");
        instance.AutoRestart = true;
        var firstSessionId = await manager.StartAsync(instance);
        var firstProcess = Assert.Single(factory.Processes);
        firstProcess.EmitError("[12:00:00] [Server thread/ERROR]: first-session root");
        firstProcess.Complete(17);

        await EventuallyAsync(() => factory.Processes.Count == 2);
        Assert.True(manager.TryGetSnapshot(instance.Id, out var restarted));
        var secondSessionId = Assert.IsType<Guid>(restarted.SessionId);
        Assert.NotEqual(firstSessionId, secondSessionId);
        factory.Processes[1].EmitError("\tat example.Orphan.run(Orphan.java:1)");

        var orphan = Assert.Single(
            manager.GetRecentConsoleLines(instance.Id),
            line => line.Text.Contains("example.Orphan", StringComparison.Ordinal));
        Assert.Equal(secondSessionId, orphan.SessionId);
        Assert.Equal(ConsoleLineSeverity.Unclassified, orphan.Severity);
        Assert.Null(orphan.DiagnosticId);
        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task CleanProcessExit_DoesNotAutoRestart()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = CreateManager(factory, maximumLines: 10);
        var instance = CreateInstance("clean-exit");
        instance.AutoRestart = true;
        await manager.StartAsync(instance);
        var process = Assert.Single(factory.Processes);

        process.EmitError("[12:00:00] [Server thread/ERROR]: recoverable diagnostic");
        process.Complete(0);

        await EventuallyAsync(() =>
            manager.TryGetSnapshot(instance.Id, out var snapshot)
            && snapshot.State == ServerState.Stopped);
        await Task.Delay(30);
        Assert.Equal(
            ConsoleLineSeverity.Error,
            Assert.Single(
                manager.GetRecentConsoleLines(instance.Id),
                line => line.Text.Contains("recoverable diagnostic", StringComparison.Ordinal)).Severity);
        Assert.Single(factory.Processes);
    }

    [Fact]
    public async Task LiveAutoRestartPolicy_CanDisableLaunchTimeSetting()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                AutoRestartDelay = TimeSpan.FromMilliseconds(5),
                ShouldAutoRestartAsync = (_, _) => Task.FromResult(false)
            },
            factory);
        var instance = CreateInstance("live-restart-disabled");
        instance.AutoRestart = true;
        await manager.StartAsync(instance);

        Assert.Single(factory.Processes).Complete(17);

        await EventuallyAsync(() =>
            manager.TryGetSnapshot(instance.Id, out var snapshot)
            && snapshot.State == ServerState.Crashed);
        await Task.Delay(30);
        Assert.Single(factory.Processes);
    }

    [Fact]
    public async Task LiveAutoRestartPolicy_CanEnableAfterLaunch()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                AutoRestartDelay = TimeSpan.FromMilliseconds(5),
                ShouldAutoRestartAsync = (_, _) => Task.FromResult(true)
            },
            factory);
        var instance = CreateInstance("live-restart-enabled");
        instance.AutoRestart = false;
        await manager.StartAsync(instance);

        Assert.Single(factory.Processes).Complete(17);

        await EventuallyAsync(() => factory.Processes.Count == 2);
        await manager.StopAsync(instance.Id);
    }

    [Fact]
    public async Task ResourceSample_ContainsInstanceAndSessionIdentity()
    {
        var factory = new FakeServerProcessFactory();
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = TimeSpan.FromMilliseconds(10),
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
            },
            factory);
        var instance = CreateInstance("metrics");
        var sampleCompletion = new TaskCompletionSource<ServerResourceSample>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ResourceSampled += (_, eventArgs) => sampleCompletion.TrySetResult(eventArgs.Sample);

        var sessionId = await manager.StartAsync(instance);
        var process = Assert.Single(factory.Processes);
        process.SetMetrics(new ProcessMetrics(TimeSpan.FromMilliseconds(20), 123_456, 234_567));
        var sample = await sampleCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(instance.Id, sample.InstanceId);
        Assert.Equal(sessionId, sample.SessionId);
        Assert.Equal(123_456, sample.WorkingSetBytes);
        Assert.Equal(234_567, sample.PrivateMemoryBytes);
        Assert.InRange(sample.CpuPercent, 0, 100);
        await manager.StopAsync(instance.Id);
    }

    private static ServerProcessManager CreateManager(
        FakeServerProcessFactory factory,
        int maximumLines,
        Func<ServerInstance, CancellationToken, Task>? prepareStart = null,
        Action<Guid>? preparedStartAborted = null) => new(
        new ServerProcessManagerOptions
        {
            MaximumRetainedConsoleLines = maximumLines,
            ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
            GracefulStopTimeout = TimeSpan.FromSeconds(1),
            ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
            AutoRestartDelay = TimeSpan.FromMilliseconds(5),
            PrepareStartAsync = prepareStart,
            PreparedStartAborted = preparedStartAborted,
        },
        factory);

    private static ServerInstance CreateInstance(string name) =>
        CreateInstance(
            name,
            Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(),
                "Minecraft Server Tests",
                name)).FullName);

    private static ServerInstance CreateInstance(string name, string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        var serverJarPath = Path.Combine(directoryPath, "server.jar");
        if (!File.Exists(serverJarPath))
        {
            File.WriteAllBytes(serverJarPath, []);
        }

        return new ServerInstance
        {
            Name = name,
            DirectoryPath = directoryPath,
            ServerJarPath = "server.jar",
            JavaExecutablePath = Path.Combine("runtime with spaces", "bin", "java.exe"),
            MinimumMemoryMb = 1024,
            MaximumMemoryMb = 2048,
            ServerArguments = ["nogui"],
        };
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
