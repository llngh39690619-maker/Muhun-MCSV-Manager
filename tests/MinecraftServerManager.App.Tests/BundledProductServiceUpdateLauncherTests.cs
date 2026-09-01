using System.ComponentModel;
using System.IO;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class BundledProductServiceUpdateLauncherTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "mcsv-bundled-service-update-tests",
        Guid.NewGuid().ToString("N"));

    public BundledProductServiceUpdateLauncherTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Update_UsesOnlyBundledUpdaterFromCompleteFormalRelease()
    {
        var guiPath = CreateFormalReleaseLayout(_directory);
        var protectedRoot = Path.Combine(_directory, "protected", ".repair-staging-1.2.9-beta.2-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var protectedUpdater = Path.Combine(protectedRoot, "updater-win-x64", "Muhun MCSV Updater.exe");
        var stager = new RecordingStager(protectedRoot);
        var verifier = new RecordingVerifier(protectedUpdater);
        var runner = new RecordingRunner(exitCode: 0);
        var launcher = new BundledProductServiceUpdateLauncher(
            () => guiPath,
            stager,
            verifier,
            runner,
            "1.2.9-beta.2");

        var result = await launcher.UpdateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(BundledProductServiceUpdateOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal((Path.GetFullPath(_directory), "1.2.9-beta.2"), Assert.Single(stager.Invocations));
        Assert.Equal((protectedRoot, "1.2.9-beta.2"), Assert.Single(verifier.Invocations));
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(protectedUpdater, invocation.UpdaterPath);
        Assert.Equal(protectedRoot, invocation.ReleaseRoot);
        Assert.NotEqual(
            Path.Combine(_directory, "updater-win-x64", "Muhun MCSV Updater.exe"),
            invocation.UpdaterPath);
    }

    [Fact]
    public async Task Update_MissingSignedReleaseInput_FailsBeforePublisherOrElevation()
    {
        var guiPath = CreateFormalReleaseLayout(_directory);
        File.Delete(Path.Combine(_directory, "update-manifest.json.sig"));
        var stager = new RecordingStager(Path.Combine(_directory, "protected"));
        var verifier = new RecordingVerifier(Path.Combine(_directory, "protected", "updater.exe"));
        var runner = new RecordingRunner(exitCode: 0);
        var launcher = new BundledProductServiceUpdateLauncher(
            () => guiPath,
            stager,
            verifier,
            runner,
            "1.2.9-beta.2");

        var result = await launcher.UpdateAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(BundledProductServiceUpdateOutcome.ReleaseLayoutUnavailable, result.Outcome);
        Assert.Empty(stager.Invocations);
        Assert.Empty(verifier.Invocations);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task Update_UntrustedUpdater_FailsBeforeElevation()
    {
        var guiPath = CreateFormalReleaseLayout(_directory);
        var protectedRoot = Path.Combine(_directory, "protected");
        var stager = new RecordingStager(protectedRoot);
        var runner = new RecordingRunner(exitCode: 0);
        var launcher = new BundledProductServiceUpdateLauncher(
            () => guiPath,
            stager,
            new RecordingVerifier(new InvalidDataException("publisher mismatch")),
            runner,
            "1.2.9-beta.2");

        var result = await launcher.UpdateAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(BundledProductServiceUpdateOutcome.PublisherVerificationFailed, result.Outcome);
        Assert.Empty(runner.Invocations);
        Assert.Equal(protectedRoot, Assert.Single(stager.CleanupInvocations));
    }

    [Fact]
    public async Task Update_PreLaunchUacCancellation_CleansProtectedStage()
    {
        var guiPath = CreateFormalReleaseLayout(_directory);
        var protectedRoot = Path.Combine(_directory, "protected");
        var protectedUpdater = Path.Combine(protectedRoot, "updater-win-x64", "Muhun MCSV Updater.exe");
        var stager = new RecordingStager(protectedRoot);
        var launcher = new BundledProductServiceUpdateLauncher(
            () => guiPath,
            stager,
            new RecordingVerifier(protectedUpdater),
            new RecordingRunner(new Win32Exception(1223)),
            "1.2.9-beta.2");

        var result = await launcher.UpdateAsync();

        Assert.Equal(BundledProductServiceUpdateOutcome.Cancelled, result.Outcome);
        Assert.Equal(protectedRoot, Assert.Single(stager.CleanupInvocations));
    }

    [Fact]
    public async Task Update_UacCancellationOrUpdaterFailure_NeverReportsSuccess()
    {
        var guiPath = CreateFormalReleaseLayout(_directory);
        var protectedRoot = Path.Combine(_directory, "protected");
        var protectedUpdater = Path.Combine(protectedRoot, "updater-win-x64", "Muhun MCSV Updater.exe");
        var cancelled = new BundledProductServiceUpdateLauncher(
            () => guiPath,
            new RecordingStager(new Win32Exception(1223)),
            new RecordingVerifier(protectedUpdater),
            new RecordingRunner(exitCode: 0),
            "1.2.9-beta.2");
        var failed = new BundledProductServiceUpdateLauncher(
            () => guiPath,
            new RecordingStager(protectedRoot),
            new RecordingVerifier(protectedUpdater),
            new RecordingRunner(exitCode: 17),
            "1.2.9-beta.2");

        var cancelledResult = await cancelled.UpdateAsync();
        var failedResult = await failed.UpdateAsync();

        Assert.False(cancelledResult.Succeeded);
        Assert.Equal(BundledProductServiceUpdateOutcome.Cancelled, cancelledResult.Outcome);
        Assert.Null(cancelledResult.ExitCode);
        Assert.False(failedResult.Succeeded);
        Assert.Equal(BundledProductServiceUpdateOutcome.UpdateFailed, failedResult.Outcome);
        Assert.Equal(17, failedResult.ExitCode);
    }

    [Fact]
    public async Task Update_ManagedVersionSlot_IsNotReinterpretedAsLooseRelease()
    {
        var releaseRoot = Path.Combine(_directory, "versions", "1.2.9-beta.2");
        var guiPath = CreateFormalReleaseLayout(releaseRoot);
        var stager = new RecordingStager(Path.Combine(_directory, "protected"));
        var runner = new RecordingRunner(exitCode: 0);
        var launcher = new BundledProductServiceUpdateLauncher(
            () => guiPath,
            stager,
            new RecordingVerifier(Path.Combine(_directory, "protected", "updater.exe")),
            runner,
            "1.2.9-beta.2");

        var result = await launcher.UpdateAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(BundledProductServiceUpdateOutcome.ReleaseLayoutUnavailable, result.Outcome);
        Assert.Empty(stager.Invocations);
        Assert.Empty(runner.Invocations);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup on Windows CI.
        }
    }

    private static string CreateFormalReleaseLayout(string releaseRoot)
    {
        var guiRoot = Path.Combine(releaseRoot, "gui-win-x64");
        var updaterRoot = Path.Combine(releaseRoot, "updater-win-x64");
        Directory.CreateDirectory(guiRoot);
        Directory.CreateDirectory(updaterRoot);
        var guiPath = Path.Combine(guiRoot, "Muhun MCSV Manager.exe");
        File.WriteAllBytes(guiPath, "MZ"u8.ToArray());
        File.WriteAllBytes(
            Path.Combine(updaterRoot, "Muhun MCSV Updater.exe"),
            "MZ"u8.ToArray());
        foreach (var name in new[]
                 {
                     "publisher.cer",
                     "release-manifest.json",
                     "release-manifest.json.sig",
                     "update-manifest.json",
                     "update-manifest.json.sig",
                     "update-signing-public-key.json",
                 })
        {
            File.WriteAllText(Path.Combine(releaseRoot, name), "signed-test-input");
        }

        return guiPath;
    }

    private sealed class RecordingRunner : IElevatedProductUpdaterProcessRunner
    {
        private readonly int _exitCode;
        private readonly Exception? _error;

        public RecordingRunner(int exitCode)
        {
            _exitCode = exitCode;
        }

        public RecordingRunner(Exception error)
        {
            _error = error;
        }

        public List<(string UpdaterPath, string ReleaseRoot)> Invocations { get; } = [];

        public Task<int> RunAsync(
            string updaterPath,
            string releaseRoot,
            CancellationToken cancellationToken)
        {
            Invocations.Add((updaterPath, releaseRoot));
            return _error is null
                ? Task.FromResult(_exitCode)
                : Task.FromException<int>(_error);
        }
    }

    private sealed class RecordingStager : IProtectedFormalReleaseStager
    {
        private readonly string? _protectedRoot;
        private readonly Exception? _error;

        public RecordingStager(string protectedRoot)
        {
            _protectedRoot = protectedRoot;
        }

        public RecordingStager(Exception error)
        {
            _error = error;
        }

        public List<(string SourceRoot, string Version)> Invocations { get; } = [];
        public List<string> CleanupInvocations { get; } = [];

        public Task<ProtectedFormalReleaseStage> StageAsync(
            string sourceReleaseRoot,
            string expectedProductVersion,
            CancellationToken cancellationToken)
        {
            Invocations.Add((sourceReleaseRoot, expectedProductVersion));
            return _error is null
                ? Task.FromResult(new ProtectedFormalReleaseStage(_protectedRoot!))
                : Task.FromException<ProtectedFormalReleaseStage>(_error);
        }

        public Task TryCleanupAsync(ProtectedFormalReleaseStage stage)
        {
            CleanupInvocations.Add(stage.ReleaseRoot);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingVerifier : IProtectedFormalReleaseVerifier
    {
        private readonly string? _updaterPath;
        private readonly Exception? _error;

        public RecordingVerifier(string updaterPath)
        {
            _updaterPath = updaterPath;
        }

        public RecordingVerifier(Exception error)
        {
            _error = error;
        }

        public List<(string ReleaseRoot, string Version)> Invocations { get; } = [];

        public Task<string> VerifyAsync(
            string protectedReleaseRoot,
            string expectedProductVersion,
            CancellationToken cancellationToken)
        {
            Invocations.Add((protectedReleaseRoot, expectedProductVersion));
            return _error is null
                ? Task.FromResult(_updaterPath!)
                : Task.FromException<string>(_error);
        }
    }
}
