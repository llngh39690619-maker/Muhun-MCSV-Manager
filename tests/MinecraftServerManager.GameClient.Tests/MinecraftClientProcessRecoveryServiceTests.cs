using System.Diagnostics;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class MinecraftClientProcessRecoveryServiceTests : IDisposable
{
    private const long Gibibyte = 1024L * 1024 * 1024;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-client-process-recovery-tests",
        Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    [Fact]
    public async Task TryAttach_RequiresAllThreeIdentityValuesAndDetachDoesNotStopMinecraft()
    {
        var process = StartJavaNamedProcess();
        var instance = CreateInstance(process);
        var recovery = new MinecraftClientProcessRecoveryService();

        var session = recovery.TryAttach(instance);

        Assert.NotNull(session);
        Assert.Equal(process.Id, session.ProcessId);
        Assert.False(session.LogCaptureAvailable);
        Assert.Equal(instance.ActiveProcessStartedAtUtc, session.PersistentIdentity?.StartedAtUtc);
        Assert.Equal(instance.ActiveProcessExecutablePath, session.PersistentIdentity?.ExecutablePath);
        await session.DisposeAsync();
        Assert.False(process.HasExited);
    }

    [Fact]
    public void TryAttach_RejectsPidReuseSignalsWithoutTouchingTheUnrelatedProcess()
    {
        var process = StartJavaNamedProcess();
        var instance = CreateInstance(process);
        var recovery = new MinecraftClientProcessRecoveryService();

        instance.ActiveProcessStartedAtUtc = instance.ActiveProcessStartedAtUtc!.Value.AddTicks(1);
        Assert.Null(recovery.TryAttach(instance));
        Assert.False(process.HasExited);

        instance = CreateInstance(process);
        instance.ActiveProcessExecutablePath = Path.Combine(_root, "different", "java.exe");
        instance.JavaExecutablePath = instance.ActiveProcessExecutablePath;
        Assert.Null(recovery.TryAttach(instance));
        Assert.False(process.HasExited);

        instance = CreateInstance(process);
        instance.ActiveProcessId = int.MaxValue;
        Assert.Null(recovery.TryAttach(instance));
        Assert.False(process.HasExited);
    }

    [Fact]
    public async Task LaunchAsync_BlocksAnAlreadyRunningPersistedInstanceBeforeBuildingAnotherProcess()
    {
        var process = StartJavaNamedProcess();
        var instance = CreateInstance(process);
        var builder = new CountingProcessBuilder();
        var coordinator = new MinecraftClientLaunchCoordinator(
            new MinecraftClientMemoryRecommendationService(
                new FixedMemoryProbe(new SystemMemorySnapshot(16 * Gibibyte, 12 * Gibibyte))),
            builder,
            new MinecraftClientProcessRecoveryService());

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.LaunchAsync(
            instance,
            new NewMinecraftClientDefaultsSettings(),
            CreateAuthenticatedSession()));

        Assert.Equal(0, builder.CallCount);
        Assert.False(process.HasExited);
    }

    [Fact]
    public void MarkerHelpers_RejectNonJavaImagesAndOnlyClearTheExpectedIdentity()
    {
        var instance = new MinecraftClientInstance();
        var invalid = new MinecraftClientProcessIdentity(
            12,
            DateTimeOffset.UtcNow,
            Path.Combine(_root, "notepad.exe"));

        Assert.Throws<ArgumentException>(() =>
            MinecraftClientProcessRecoveryService.RecordIdentity(instance, invalid));
        Assert.False(MinecraftClientProcessRecoveryService.HasPersistedIdentity(instance));

        var valid = invalid with { ExecutablePath = Path.Combine(_root, "bin", "javaw.exe") };
        MinecraftClientProcessRecoveryService.RecordIdentity(instance, valid);
        Assert.True(MinecraftClientProcessRecoveryService.MarkerMatches(instance, valid));

        MinecraftClientProcessRecoveryService.ClearIdentity(instance);
        Assert.False(MinecraftClientProcessRecoveryService.HasPersistedIdentity(instance));
    }

    private Process StartJavaNamedProcess()
    {
        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandInterpreter) || !File.Exists(commandInterpreter))
        {
            commandInterpreter = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
        }

        var javaPath = Path.Combine(_root, "runtime", "bin", "java.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
        File.Copy(commandInterpreter, javaPath, overwrite: false);
        var startInfo = new ProcessStartInfo(javaPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("The test Java process did not start.");
        _processes.Add(process);
        Assert.False(process.WaitForExit(100));
        return process;
    }

    private MinecraftClientInstance CreateInstance(Process process)
    {
        var executablePath = process.MainModule?.FileName
                             ?? throw new InvalidOperationException("Cannot inspect the test Java image.");
        var identity = new MinecraftClientProcessIdentity(
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
            executablePath);
        var instanceDirectory = Path.Combine(_root, "instance");
        Directory.CreateDirectory(instanceDirectory);
        var instance = new MinecraftClientInstance
        {
            Name = "Running client",
            DirectoryPath = instanceDirectory,
            GameVersion = "1.21.8",
            InstalledVersionId = "1.21.8",
            JavaExecutablePath = executablePath,
            AccountId = "account-a",
        };
        MinecraftClientProcessRecoveryService.RecordIdentity(instance, identity);
        return instance;
    }

    private static AuthenticatedMinecraftSession CreateAuthenticatedSession() => new(
        "account-a",
        "PlayerOne",
        "0123456789abcdef0123456789abcdef",
        "test-access-token");

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
            }
            catch (Exception error) when (
                error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedMemoryProbe(SystemMemorySnapshot snapshot) : ISystemMemoryProbe
    {
        public SystemMemorySnapshot GetSnapshot() => snapshot;
    }

    private sealed class CountingProcessBuilder : IMinecraftClientProcessBuilder
    {
        public int CallCount { get; private set; }

        public Task<Process> BuildAsync(
            MinecraftClientInstance instance,
            AuthenticatedMinecraftSession authenticatedSession,
            MinecraftClientMemoryResolution memory,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("The builder must not run for an active instance.");
        }
    }
}
