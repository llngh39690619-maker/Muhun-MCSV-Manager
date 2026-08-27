using System.Diagnostics;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class FtbInstallerProcessTests
{
    [Fact]
    public void CommandBuilder_UsesArgumentListForChinesePathsAndNeverAddsDangerousFlags()
    {
        var request = new FtbInstallRequest(
            134,
            100466,
            Path.Combine(Path.GetTempPath(), "官方 工具", "ftb installer.exe"),
            Path.Combine(Path.GetTempPath(), "伺服器 分區", "FTB 天空：Aero 1.6.1"));

        var startInfo = new FtbInstallerCommandBuilder().Build(request);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(Path.GetFullPath(request.InstallerPath), startInfo.FileName);
        Assert.Equal(Path.GetFullPath(request.InstallationDirectory), startInfo.WorkingDirectory);
        Assert.Equal(
            [
                "-provider", "ftb",
                "-pack", "134",
                "-version", "100466",
                "-dir", Path.GetFullPath(request.InstallationDirectory),
                "-auto",
                "-validate",
                "-no-colours",
                "-threads", "16",
                "-timeout", "5m",
                "-accept-eula",
            ],
            startInfo.ArgumentList);
        Assert.DoesNotContain("-force", startInfo.ArgumentList);
        Assert.DoesNotContain("-no-java", startInfo.ArgumentList);
        Assert.DoesNotContain("-skip-modloader", startInfo.ArgumentList);
        Assert.DoesNotContain("cmd.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessRunner_CapturesBothStreamsAndOnlyPassesExitZero()
    {
        var process = FakeRunningProcess.Completed(
            exitCode: 0,
            standardOutput: "下載中\n安裝完成\n",
            standardError: "警告一\n");
        var output = new List<FtbInstallerOutputLine>();
        var runner = new FtbInstallerProcessRunner(new FakeProcessHost(process));

        var result = await runner.RunAsync(SafeStartInfo(), new InlineProgress(output.Add));

        Assert.Equal(["下載中", "安裝完成"], result.StandardOutput);
        Assert.Equal(["警告一"], result.StandardError);
        Assert.Equal(3, output.Count);
        Assert.Contains(output, line => line.IsError && line.Text == "警告一");
        Assert.False(process.KillCalled);
    }

    [Fact]
    public async Task ProcessRunner_NonZeroThrowsWithCapturedOutput()
    {
        var process = FakeRunningProcess.Completed(7, "partial output\n", "fatal failure\n");
        var runner = new FtbInstallerProcessRunner(new FakeProcessHost(process));

        var error = await Assert.ThrowsAsync<FtbInstallerProcessException>(() =>
            runner.RunAsync(SafeStartInfo()));

        Assert.Equal(7, error.Result.ExitCode);
        Assert.Equal(["partial output"], error.Result.StandardOutput);
        Assert.Equal(["fatal failure"], error.Result.StandardError);
    }

    [Fact]
    public async Task ProcessRunner_CancellationKillsEntireProcessTree()
    {
        var process = FakeRunningProcess.Pending();
        var runner = new FtbInstallerProcessRunner(new FakeProcessHost(process));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(SafeStartInfo(), cancellationToken: cancellation.Token));

        Assert.True(process.KillCalled);
        Assert.True(process.KillEntireTree);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task ProcessRunner_CancellationRetriesKillBeforeReportingCancellation()
    {
        var process = FakeRunningProcess.Pending(killsBeforeExit: 2);
        var runner = new FtbInstallerProcessRunner(
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
    public async Task ProcessRunner_BoundsAnIndividualOutputLine()
    {
        var process = FakeRunningProcess.Completed(0, new string('x', 100_000), string.Empty);
        var runner = new FtbInstallerProcessRunner(new FakeProcessHost(process));

        var result = await runner.RunAsync(SafeStartInfo());

        Assert.True(result.OutputTruncated);
        Assert.Equal(64 * 1024, Assert.Single(result.StandardOutput).Length);
    }

    [Fact]
    public async Task ProcessRunner_CancellationThrowsTerminationFailureWhenKilledProcessNeverSignalsExit()
    {
        var process = FakeRunningProcess.Pending(completeWhenKilled: false);
        var runner = new FtbInstallerProcessRunner(
            new FakeProcessHost(process),
            drainTimeout: TimeSpan.FromMilliseconds(25));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<ManagedProcessTerminationException>(() =>
            runner.RunAsync(SafeStartInfo(), cancellationToken: cancellation.Token));

        Assert.Equal("FTB Installer", error.ProcessDisplayName);
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
        FileName = "ftb-installer.exe",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    private sealed class InlineProgress(Action<FtbInstallerOutputLine> report)
        : IProgress<FtbInstallerOutputLine>
    {
        public void Report(FtbInstallerOutputLine value) => report(value);
    }

    private sealed class FakeProcessHost(FakeRunningProcess process) : IFtbProcessHost
    {
        public IFtbRunningProcess Start(ProcessStartInfo startInfo)
        {
            process.StartInfo = startInfo;
            return process;
        }
    }

    private sealed class FakeRunningProcess : IFtbRunningProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            var process = new FakeRunningProcess(standardOutput, standardError) { _exitCode = exitCode };
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
