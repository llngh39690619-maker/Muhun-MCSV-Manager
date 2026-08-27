using System.Diagnostics;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class ModrinthLoaderBootstrapProcessTests
{
    [Fact]
    public void CommandBuilder_FabricUsesExactArgumentListAndChinesePathsWithoutShell()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var java = Path.Combine(temporaryDirectory.Path, "Java 二十一", "bin", "java.exe");
        var installer = Path.Combine(temporaryDirectory.Path, "官方 工具", "fabric installer.jar");
        var staging = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "模組包 staging", "天空工廠")).FullName;
        var privateHome = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "private environment", "home")).FullName;
        var privateTemp = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "private environment", "temp")).FullName;
        var request = new ModrinthModpackLoaderInstallRequest(
            ModrinthModpackLoaderKind.Fabric,
            "1.20.1",
            "0.16.9");

        var startInfo = new ModrinthLoaderBootstrapCommandBuilder().Build(
            request,
            java,
            installer,
            staging,
            privateHome,
            privateTemp);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(Path.GetFullPath(java), startInfo.FileName);
        Assert.Equal(Path.GetFullPath(staging), startInfo.WorkingDirectory);
        Assert.Equal(
            [
                $"-Duser.home={Path.GetFullPath(privateHome)}",
                $"-Djava.io.tmpdir={Path.GetFullPath(privateTemp)}",
                $"-Duser.dir={Path.GetFullPath(staging)}",
                "-jar", Path.GetFullPath(installer),
                "server",
                "-dir", Path.GetFullPath(staging),
                "-mcversion", "1.20.1",
                "-loader", "0.16.9",
                "-downloadMinecraft",
            ],
            startInfo.ArgumentList);
        AssertIsolatedJavaEnvironment(startInfo, java, privateHome, privateTemp);
        Assert.DoesNotContain("cmd.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sh", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ModrinthModpackLoaderKind.Forge, "47.2.0")]
    [InlineData(ModrinthModpackLoaderKind.NeoForge, "21.1.248")]
    public void CommandBuilder_ForgeFamilyUsesDirectInstallServerOnly(
        ModrinthModpackLoaderKind kind,
        string loaderVersion)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var java = Path.Combine(temporaryDirectory.Path, "managed java", "bin", "java.exe");
        var installer = Path.Combine(temporaryDirectory.Path, "tools", "official-installer.jar");
        var working = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "fresh staging")).FullName;
        var privateHome = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "environment", "home")).FullName;
        var privateTemp = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "environment", "temp")).FullName;
        var request = new ModrinthModpackLoaderInstallRequest(kind, "1.20.1", loaderVersion);
        var builder = new ModrinthLoaderBootstrapCommandBuilder();

        var startInfo = builder.Build(
            request,
            java,
            installer,
            working,
            privateHome,
            privateTemp);

        Assert.Equal(
            [
                $"-Duser.home={Path.GetFullPath(privateHome)}",
                $"-Djava.io.tmpdir={Path.GetFullPath(privateTemp)}",
                $"-Duser.dir={Path.GetFullPath(working)}",
                "-jar", Path.GetFullPath(installer),
                "--installServer", Path.GetFullPath(working),
            ],
            startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        AssertIsolatedJavaEnvironment(startInfo, java, privateHome, privateTemp);
    }

    [Fact]
    public void ManagedJavaEnvironment_RemovesHostileJvmBuildAndGitInjection()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var java = Path.Combine(temporaryDirectory.Path, "managed java", "bin", "java.exe");
        var privateHome = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "environment", "home")).FullName;
        var privateTemp = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "environment", "temp")).FullName;
        var startInfo = new ProcessStartInfo();
        foreach (var key in HostileEnvironmentKeys)
        {
            startInfo.Environment[key] = $"hostile-{key}";
        }

        startInfo.Environment["JAVA_HOME"] = "unmanaged java";
        startInfo.Environment["PATH"] = "untrusted tools";

        ManagedJavaProcessEnvironment.Configure(startInfo, java, privateHome, privateTemp);

        AssertIsolatedJavaEnvironment(startInfo, java, privateHome, privateTemp);
        foreach (var key in HostileEnvironmentKeys)
        {
            Assert.False(startInfo.Environment.ContainsKey(key), key);
        }
    }

    [Fact]
    public void JavaVersionProbe_UsesExactExecutableControlledWorkingDirectoryAndMinimalEnvironment()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var javaBin = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "managed java", "bin")).FullName;
        var java = Path.Combine(javaBin, "java.exe");

        var startInfo = AdoptiumRuntimeProvider.BuildJavaToolVersionStartInfo(java);

        Assert.Equal(Path.GetFullPath(java), startInfo.FileName);
        Assert.Equal(Path.GetFullPath(javaBin), startInfo.WorkingDirectory);
        Assert.Equal(["-version"], startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        AssertIsolatedJavaEnvironment(startInfo, java);
        foreach (var key in HostileEnvironmentKeys)
        {
            Assert.False(startInfo.Environment.ContainsKey(key), key);
        }
    }

    [Fact]
    public async Task Runner_CapturesBothStreamsAndOnlyExitZeroPasses()
    {
        var process = FakeRunningProcess.Completed(0, "下載中\n安裝完成\n", "警告\n");
        var output = new List<ModrinthLoaderBootstrapOutputLine>();
        var runner = new ModrinthLoaderBootstrapProcessRunner(new FakeProcessHost(process));

        var result = await runner.RunAsync(SafeStartInfo(), new InlineProgress(output.Add));

        Assert.Equal(["下載中", "安裝完成"], result.StandardOutput);
        Assert.Equal(["警告"], result.StandardError);
        Assert.False(result.OutputTruncated);
        Assert.Contains(output, line => line.IsError && line.Text == "警告");
        Assert.False(process.KillCalled);
    }

    [Fact]
    public async Task Runner_NonZeroThrowsWithCapturedResult()
    {
        var runner = new ModrinthLoaderBootstrapProcessRunner(
            new FakeProcessHost(FakeRunningProcess.Completed(9, "partial\n", "fatal\n")));

        var error = await Assert.ThrowsAsync<ModrinthLoaderBootstrapProcessException>(() =>
            runner.RunAsync(SafeStartInfo()));

        Assert.Equal(9, error.Result.ExitCode);
        Assert.Equal(["partial"], error.Result.StandardOutput);
        Assert.Equal(["fatal"], error.Result.StandardError);
    }

    [Fact]
    public async Task Runner_CancellationKillsEntireProcessTree()
    {
        var process = FakeRunningProcess.Pending();
        var runner = new ModrinthLoaderBootstrapProcessRunner(new FakeProcessHost(process));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(SafeStartInfo(), cancellationToken: cancellation.Token));

        Assert.True(process.KillCalled);
        Assert.True(process.KillEntireTree);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Runner_CancellationRetriesKillBeforeReportingCancellation()
    {
        var process = FakeRunningProcess.Pending(killsBeforeExit: 2);
        var runner = new ModrinthLoaderBootstrapProcessRunner(
            new FakeProcessHost(process),
            drainTimeout: TimeSpan.FromMilliseconds(25));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(SafeStartInfo(), cancellationToken: cancellation.Token));

        Assert.Equal(2, process.KillCallCount);
        Assert.True(process.KillEntireTree);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Runner_BoundsAnIndividualOutputLine()
    {
        var process = FakeRunningProcess.Completed(0, new string('x', 100_000), string.Empty);
        var runner = new ModrinthLoaderBootstrapProcessRunner(new FakeProcessHost(process));

        var result = await runner.RunAsync(SafeStartInfo());

        Assert.True(result.OutputTruncated);
        Assert.Equal(64 * 1024, Assert.Single(result.StandardOutput).Length);
    }

    [Fact]
    public async Task Runner_CancellationThrowsTerminationFailureWhenKilledProcessNeverSignalsExit()
    {
        var process = FakeRunningProcess.Pending(completeWhenKilled: false);
        var runner = new ModrinthLoaderBootstrapProcessRunner(
            new FakeProcessHost(process),
            drainTimeout: TimeSpan.FromMilliseconds(25));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<ManagedProcessTerminationException>(() =>
            runner.RunAsync(SafeStartInfo(), cancellationToken: cancellation.Token));

        Assert.Equal("ModLoader Installer", error.ProcessDisplayName);
        Assert.Equal(TimeSpan.FromMilliseconds(25), error.ConfirmationTimeout);
        Assert.Equal(2, error.KillAttempts);
        var causes = Assert.IsType<AggregateException>(error.InnerException).Flatten().InnerExceptions;
        Assert.Contains(causes, exception => exception is OperationCanceledException);
        Assert.Equal(2, causes.Count(exception => exception is TimeoutException));
        Assert.True(process.KillCalled);
        Assert.True(process.KillEntireTree);
        Assert.Equal(2, process.KillCallCount);
        Assert.False(process.HasExited);
        Assert.True(process.Disposed);
    }

    private static ProcessStartInfo SafeStartInfo() => new()
    {
        FileName = "java.exe",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    private static readonly string[] HostileEnvironmentKeys =
    [
        "_JAVA_OPTIONS",
        "JAVA_TOOL_OPTIONS",
        "JDK_JAVA_OPTIONS",
        "CLASSPATH",
        "MAVEN_OPTS",
        "MAVEN_ARGS",
        "MAVEN_CONFIG",
        "MAVEN_USER_HOME",
        "GRADLE_OPTS",
        "GRADLE_USER_HOME",
        "GIT_CONFIG_COUNT",
        "GIT_CONFIG_KEY_0",
        "GIT_CONFIG_VALUE_0",
        "GIT_CONFIG_GLOBAL",
        "GIT_CONFIG_SYSTEM",
        "GIT_DIR",
        "GIT_WORK_TREE",
    ];

    private static void AssertIsolatedJavaEnvironment(
        ProcessStartInfo startInfo,
        string javaExecutable,
        string? privateHome = null,
        string? privateTemp = null)
    {
        var java = Path.GetFullPath(javaExecutable);
        var javaBin = Path.GetDirectoryName(java)!;
        var javaHome = Directory.GetParent(javaBin)!.FullName;
        var expectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "JAVA_HOME",
            "PATH",
        };
        var expectedPath = new List<string> { javaBin };

        if (OperatingSystem.IsWindows())
        {
            expectedKeys.UnionWith(["COMSPEC", "SystemRoot"]);
            expectedPath.Add(Path.GetFullPath(Environment.SystemDirectory));
            Assert.Equal(
                Path.Combine(Path.GetFullPath(Environment.SystemDirectory), "cmd.exe"),
                startInfo.Environment["COMSPEC"]);
            Assert.Equal(
                Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
                startInfo.Environment["SystemRoot"]);
        }

        if (privateHome is not null && privateTemp is not null)
        {
            expectedKeys.UnionWith(["HOME", "USERPROFILE", "TEMP", "TMP"]);
            Assert.Equal(Path.GetFullPath(privateHome), startInfo.Environment["HOME"]);
            Assert.Equal(Path.GetFullPath(privateHome), startInfo.Environment["USERPROFILE"]);
            Assert.Equal(Path.GetFullPath(privateTemp), startInfo.Environment["TEMP"]);
            Assert.Equal(Path.GetFullPath(privateTemp), startInfo.Environment["TMP"]);
        }

        Assert.Equal(javaHome, startInfo.Environment["JAVA_HOME"]);
        Assert.Equal(string.Join(Path.PathSeparator, expectedPath), startInfo.Environment["PATH"]);
        Assert.True(
            expectedKeys.SetEquals(startInfo.Environment.Keys),
            $"Unexpected environment keys: {string.Join(", ", startInfo.Environment.Keys)}");
    }

    private sealed class InlineProgress(Action<ModrinthLoaderBootstrapOutputLine> report)
        : IProgress<ModrinthLoaderBootstrapOutputLine>
    {
        public void Report(ModrinthLoaderBootstrapOutputLine value) => report(value);
    }

    private sealed class FakeProcessHost(FakeRunningProcess process) : IModrinthLoaderProcessHost
    {
        public IModrinthLoaderRunningProcess Start(ProcessStartInfo startInfo)
        {
            process.StartInfo = startInfo;
            return process;
        }
    }

    private sealed class FakeRunningProcess : IModrinthLoaderRunningProcess
    {
        private readonly TaskCompletionSource _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _killsBeforeExit;
        private int _exitCode;

        private FakeRunningProcess(
            string standardOutput,
            string standardError,
            int killsBeforeExit = 1)
        {
            StandardOutput = new StringReader(standardOutput);
            StandardError = new StringReader(standardError);
            _killsBeforeExit = killsBeforeExit;
        }

        public TextReader StandardOutput { get; }

        public TextReader StandardError { get; }

        public bool HasExited => _exit.Task.IsCompleted;

        public int ExitCode => HasExited
            ? _exitCode
            : throw new InvalidOperationException("Process has not exited.");

        public bool KillCalled => KillCallCount > 0;

        public int KillCallCount { get; private set; }

        public bool KillEntireTree { get; private set; }

        public bool Disposed { get; private set; }

        public ProcessStartInfo? StartInfo { get; set; }

        public static FakeRunningProcess Completed(
            int exitCode,
            string standardOutput,
            string standardError)
        {
            var process = new FakeRunningProcess(standardOutput, standardError)
            {
                _exitCode = exitCode,
            };
            process._exit.SetResult();
            return process;
        }

        public static FakeRunningProcess Pending(bool completeWhenKilled = true)
            => new(string.Empty, string.Empty, completeWhenKilled ? 1 : int.MaxValue);

        public static FakeRunningProcess Pending(int killsBeforeExit)
            => new(string.Empty, string.Empty, killsBeforeExit);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _exit.Task.WaitAsync(cancellationToken);

        public void Kill(bool entireProcessTree)
        {
            KillCallCount++;
            KillEntireTree = entireProcessTree;
            if (KillCallCount >= _killsBeforeExit)
            {
                _exitCode = -1;
                _exit.TrySetResult();
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            StandardOutput.Dispose();
            StandardError.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
