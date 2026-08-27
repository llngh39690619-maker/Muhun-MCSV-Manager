using System.Security.Cryptography;
using System.Text.Json;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductUpdateActivatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-ActivatorTests",
        Guid.NewGuid().ToString("N"));

    public ProductUpdateActivatorTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "versions", "0.9.0"));
        Directory.CreateDirectory(Path.Combine(_directory, "versions", "1.0.0"));
        File.WriteAllText(Path.Combine(_directory, "active-version.v1"), "0.9.0\n");
        File.WriteAllBytes(Path.Combine(_directory, "versions", "0.9.0", "Muhun MCSV Manager.exe"), "MZ"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_directory, "versions", "1.0.0", "Muhun MCSV Manager.exe"), "MZ"u8.ToArray());
        ProductInstalledVersionMetadataStore.Write(
            Path.Combine(_directory, "versions", "0.9.0"),
            new ProductInstalledVersionMetadata(
                1,
                "muhun.mcsv.manager",
                "0.9.0",
                "Muhun MCSV Manager.exe"));
        ProductInstalledVersionMetadataStore.Write(
            Path.Combine(_directory, "versions", "1.0.0"),
            new ProductInstalledVersionMetadata(
                1,
                "muhun.mcsv.manager",
                "1.0.0",
                "Muhun MCSV Manager.exe"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task HealthyTarget_CommitsPointer()
    {
        var controller = new FakeHealthController(true);
        var activator = new ProductUpdateActivator(_directory, controller);

        var result = await activator.ActivateAsync(
            CreateTargetManifest(),
            TimeSpan.FromSeconds(10));

        Assert.False(result.RolledBack);
        Assert.Equal("1.0.0", activator.ReadActiveVersion());
        Assert.Single(controller.Launched);
    }

    [Fact]
    public async Task UnhealthyTarget_AutomaticallyRestoresPreviousVersion()
    {
        var controller = new FakeHealthController(false, true);
        var activator = new ProductUpdateActivator(_directory, controller);

        var result = await activator.ActivateAsync(
            CreateTargetManifest(),
            TimeSpan.FromSeconds(10));

        Assert.True(result.RolledBack);
        Assert.Equal("0.9.0", activator.ReadActiveVersion());
        Assert.Equal(2, controller.Launched.Count);
    }

    [Fact]
    public async Task MissingRollbackMetadata_FailsBeforeChangingActivePointer()
    {
        File.Delete(Path.Combine(
            _directory,
            "versions",
            "0.9.0",
            ProductInstalledVersionMetadataStore.FileName));
        var activator = new ProductUpdateActivator(_directory, new FakeHealthController(true));

        await Assert.ThrowsAsync<FileNotFoundException>(() => activator.ActivateAsync(
            CreateTargetManifest(),
            TimeSpan.FromSeconds(10)));

        Assert.Equal("0.9.0", activator.ReadActiveVersion());
    }

    [Fact]
    public async Task ExistingTargetWithTamperedSignedFile_IsRejectedBeforePointerSwitch()
    {
        var manifest = CreateTargetManifest();
        File.WriteAllBytes(
            Path.Combine(_directory, "versions", "1.0.0", "Muhun MCSV Manager.exe"),
            "XX"u8.ToArray());
        var controller = new FakeHealthController(true);
        var activator = new ProductUpdateActivator(_directory, controller);

        await Assert.ThrowsAsync<InvalidDataException>(() => activator.ActivateAsync(
            manifest,
            TimeSpan.FromSeconds(10)));

        Assert.Equal("0.9.0", activator.ReadActiveVersion());
        Assert.Empty(controller.Launched);
    }

    [Fact]
    public async Task ConcurrentActivation_IsRejectedByExclusiveABSwitchLock()
    {
        var health = new BlockingHealthController();
        var firstActivator = new ProductUpdateActivator(_directory, health);
        var secondActivator = new ProductUpdateActivator(_directory, new FakeHealthController(true));
        var first = firstActivator.ActivateAsync(CreateTargetManifest(), TimeSpan.FromSeconds(10));
        await health.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<IOException>(() => secondActivator.ActivateAsync(
            CreateTargetManifest(),
            TimeSpan.FromSeconds(10)));

        health.Release.TrySetResult();
        var result = await first;
        Assert.False(result.RolledBack);
        Assert.Equal("1.0.0", firstActivator.ReadActiveVersion());
    }

    [Fact]
    public void ConcurrentUpdaterWorkflow_IsRejectedBeforeConsumptionOrProvisioning()
    {
        var first = new ProductUpdateActivator(_directory, new FakeHealthController(true));
        var second = new ProductUpdateActivator(_directory, new FakeHealthController(true));
        using var lease = first.AcquireUpdaterLease();

        Assert.Throws<IOException>(() => second.AcquireUpdaterLease());
        Assert.Equal("0.9.0", first.ReadActiveVersion());
    }

    [Fact]
    public async Task JournalIoFailureDuringHealthFailure_DoesNotPreventPointerRollback()
    {
        var health = new JournalFailingHealthController(_directory);
        var activator = new ProductUpdateActivator(_directory, health);

        var result = await activator.ActivateAsync(
            CreateTargetManifest(),
            TimeSpan.FromSeconds(10));

        Assert.True(result.RolledBack);
        Assert.Equal("0.9.0", activator.ReadActiveVersion());
        Assert.Equal(2, health.LaunchCount);
    }

    [Fact]
    public async Task InterruptedHealthCheck_IsRecoveredToPreviousVersion()
    {
        File.WriteAllText(Path.Combine(_directory, "active-version.v1"), "1.0.0\n");
        var operationId = Guid.NewGuid();
        var journal = new ProductUpdateActivationJournal(
            1,
            operationId,
            "0.9.0",
            "1.0.0",
            ProductUpdateActivationState.HealthChecking,
            DateTimeOffset.UtcNow.AddMinutes(-1));
        File.WriteAllText(
            PrepareActivationJournalPath(),
            JsonSerializer.Serialize(journal, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var controller = new FakeHealthController(true);
        var activator = new ProductUpdateActivator(_directory, controller);

        var result = await activator.RecoverInterruptedActivationAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(result);
        Assert.True(result.RolledBack);
        Assert.Equal(operationId, result.OperationId);
        Assert.Equal("0.9.0", activator.ReadActiveVersion());
        Assert.Single(controller.Launched);
    }

    private sealed class FakeHealthController(params bool[] results) : IProductUpdateHealthController
    {
        private readonly Queue<bool> _results = new(results);
        public List<string> Launched { get; } = [];

        public Task LaunchAsync(string executablePath, CancellationToken cancellationToken)
        {
            Launched.Add(executablePath);
            return Task.CompletedTask;
        }

        public Task<bool> WaitForHealthyAsync(
            string version,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            => Task.FromResult(_results.Dequeue());
    }

    private sealed class BlockingHealthController : IProductUpdateHealthController
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LaunchAsync(string executablePath, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public async Task<bool> WaitForHealthyAsync(
            string version,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return true;
        }
    }

    private sealed class JournalFailingHealthController(string root) : IProductUpdateHealthController
    {
        private int _healthChecks;
        public int LaunchCount { get; private set; }

        public Task LaunchAsync(string executablePath, CancellationToken cancellationToken)
        {
            LaunchCount++;
            return Task.CompletedTask;
        }

        public Task<bool> WaitForHealthyAsync(
            string version,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _healthChecks) == 1)
            {
                var journal = Path.Combine(
                    root,
                    ProductUpdateActivator.ActivationStateDirectoryName,
                    "activation-journal.v1.json");
                File.Delete(journal);
                Directory.CreateDirectory(journal);
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }
    }

    private ProductUpdateManifest CreateTargetManifest()
    {
        var path = Path.Combine(_directory, "versions", "1.0.0", "Muhun MCSV Manager.exe");
        var bytes = File.ReadAllBytes(path);
        return ProductUpdateManifestTests.CreateManifest(
            [new ProductUpdateFile(
                "Muhun MCSV Manager.exe",
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes))) ]);
    }

    private string PrepareActivationJournalPath()
    {
        var root = Path.Combine(_directory, ProductUpdateActivator.ActivationStateDirectoryName);
        Directory.CreateDirectory(root);
        return Path.Combine(root, "activation-journal.v1.json");
    }
}
