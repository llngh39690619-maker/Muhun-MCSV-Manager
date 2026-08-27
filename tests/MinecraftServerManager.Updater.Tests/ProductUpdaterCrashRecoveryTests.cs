using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductUpdaterCrashRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-UpdaterCrashRecoveryTests",
        Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    public ProductUpdaterCrashRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(ProductUpdaterApplicationCheckpoint.ProvisionedBeforeConsumption, false)]
    [InlineData(ProductUpdaterApplicationCheckpoint.ConsumedBeforeActivationJournal, true)]
    public async Task ConsumeBoundaryInterruption_ResumesSameOperationExactlyOnce(
        ProductUpdaterApplicationCheckpoint checkpoint,
        bool expectedConsumed)
    {
        var fixture = await CreateFixtureAsync();
        var interrupted = await RunAsync(
            fixture,
            _now.AddMinutes(1),
            checkpointObserver: reached =>
            {
                if (reached == checkpoint)
                {
                    throw new ProductUpdateInterruptionException(reached.ToString());
                }
            });

        Assert.Equal(13, interrupted);
        Assert.Equal(expectedConsumed, File.Exists(fixture.Verified.ConsumptionMarkerPath));
        Assert.Null(ProductUpdateActivationReceiptProtocol.Read(
            fixture.UpdatesRoot,
            fixture.OperationId));
        Assert.Equal(fixture.PreviousVersion, ReadActiveVersion(fixture.InstallRoot));

        // The authenticated request itself is expired. The exact durable pending binding is what
        // authorizes recovery after a long Service outage.
        var resumed = await RunAsync(fixture, _now.AddMinutes(20));

        Assert.Equal(0, resumed);
        Assert.Equal(fixture.TargetVersion, ReadActiveVersion(fixture.InstallRoot));
        var receipt = ProductUpdateActivationReceiptProtocol.Read(
            fixture.UpdatesRoot,
            fixture.OperationId);
        Assert.NotNull(receipt);
        Assert.Equal(ProductUpdateActivationReceiptOutcome.Committed, receipt.Outcome);
        Assert.Equal(fixture.OperationId, receipt.OperationId);
        Assert.Single(fixture.Health.Launched);

        // A second updater invocation observes the terminal journal and only re-publishes the
        // same receipt; it does not consume or switch again.
        Assert.Equal(0, await RunAsync(fixture, _now.AddMinutes(21)));
        Assert.Single(fixture.Health.Launched);
    }

    [Theory]
    [InlineData(ProductUpdateActivationCheckpoint.JournalPersisted, false)]
    [InlineData(ProductUpdateActivationCheckpoint.PointerSwitched, false)]
    [InlineData(ProductUpdateActivationCheckpoint.HealthCheckingJournalPersisted, false)]
    [InlineData(ProductUpdateActivationCheckpoint.TerminalJournalPersisted, true)]
    public async Task JournalAndPointerInterruption_RecoversOrAcknowledgesTerminalStateIdempotently(
        ProductUpdateActivationCheckpoint checkpoint,
        bool committedBeforeInterruption)
    {
        var fixture = await CreateFixtureAsync();
        var interrupted = await RunAsync(
            fixture,
            _now.AddMinutes(1),
            activationCheckpointObserver: reached =>
            {
                if (reached == checkpoint)
                {
                    throw new ProductUpdateInterruptionException(reached.ToString());
                }
            });

        Assert.Equal(13, interrupted);
        Assert.Null(ProductUpdateActivationReceiptProtocol.Read(
            fixture.UpdatesRoot,
            fixture.OperationId));
        var interruptedJournal = ReadJournal(fixture.InstallRoot);
        Assert.Equal(fixture.OperationId, interruptedJournal.OperationId);

        var resumed = await RunAsync(fixture, _now.AddMinutes(20));
        var receipt = ProductUpdateActivationReceiptProtocol.Read(
            fixture.UpdatesRoot,
            fixture.OperationId);

        Assert.NotNull(receipt);
        if (committedBeforeInterruption)
        {
            Assert.Equal(0, resumed);
            Assert.Equal(ProductUpdateActivationReceiptOutcome.Committed, receipt.Outcome);
            Assert.Equal(fixture.TargetVersion, ReadActiveVersion(fixture.InstallRoot));
        }
        else
        {
            Assert.Equal(10, resumed);
            Assert.Equal(ProductUpdateActivationReceiptOutcome.RolledBack, receipt.Outcome);
            Assert.Equal(fixture.PreviousVersion, ReadActiveVersion(fixture.InstallRoot));
        }

        var launchCount = fixture.Health.Launched.Count;
        Assert.Equal(resumed, await RunAsync(fixture, _now.AddMinutes(21)));
        Assert.Equal(launchCount, fixture.Health.Launched.Count);
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        const string previousVersion = "0.9.0";
        const string targetVersion = "1.0.0";
        var operationId = Guid.NewGuid();
        var caseRoot = Path.Combine(_root, operationId.ToString("N"));
        var updatesRoot = Path.Combine(caseRoot, "data", "updates");
        var installRoot = Path.Combine(caseRoot, "install");
        var previousRoot = Path.Combine(installRoot, "versions", previousVersion);
        Directory.CreateDirectory(Path.Combine(previousRoot, "gui-win-x64"));
        Directory.CreateDirectory(updatesRoot);
        File.WriteAllText(Path.Combine(installRoot, ".muhun-mcsv-install-root"), "muhun.mcsv.manager:1\n");
        File.WriteAllText(Path.Combine(installRoot, "active-version.v1"), previousVersion + "\n");
        var previousEntry = ProductFormalUpdateManifestValidator.GuiEntryPoint;
        File.WriteAllBytes(Path.Combine(previousRoot, previousEntry), "MZ previous"u8.ToArray());
        ProductInstalledVersionMetadataStore.Write(
            previousRoot,
            new ProductInstalledVersionMetadata(
                1,
                "muhun.mcsv.manager",
                previousVersion,
                previousEntry));

        var guiBytes = "MZ target"u8.ToArray();
        var serviceBytes = "MZ service"u8.ToArray();
        var updaterBytes = "MZ updater"u8.ToArray();
        var packageBytes = CreatePackage(
            (ProductFormalUpdateManifestValidator.GuiEntryPoint, guiBytes),
            (ProductFormalUpdateManifestValidator.ServiceEntryPoint, serviceBytes),
            (ProductFormalUpdateManifestValidator.UpdaterEntryPoint, updaterBytes));
        Directory.CreateDirectory(Path.Combine(updatesRoot, "packages"));
        await File.WriteAllBytesAsync(
            Path.Combine(updatesRoot, "packages", targetVersion + ".zip"),
            packageBytes);

        using var rsa = RSA.Create(3072);
        var subjectPublicKeyInfo = rsa.ExportSubjectPublicKeyInfo();
        var keyDocument = new ProductUpdatePublicKeyDocument(
            1,
            "muhun.mcsv.manager",
            "muhun.release",
            "rsa-pss-sha256",
            "RSA",
            rsa.KeySize,
            Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo)),
            Convert.ToBase64String(subjectPublicKeyInfo),
            new string('b', 64),
            "CN=Muhun MCSV Manager Release Signing, O=Muhun",
            _now.AddDays(-1),
            _now.AddYears(1));
        var manifest = new ProductUpdateManifest(
            1,
            "muhun.mcsv.manager",
            targetVersion,
            "stable",
            "win-x64",
            _now.AddMinutes(-1),
            keyDocument.KeyId,
            "rsa-pss-sha256",
            new ProductUpdatePackage(
                "https://updates.example.com/product.zip",
                packageBytes.Length,
                Convert.ToHexString(SHA256.HashData(packageBytes))),
            ProductFormalUpdateManifestValidator.GuiEntryPoint,
            [
                CreateFile(ProductFormalUpdateManifestValidator.GuiEntryPoint, guiBytes),
                CreateFile(ProductFormalUpdateManifestValidator.ServiceEntryPoint, serviceBytes),
                CreateFile(ProductFormalUpdateManifestValidator.UpdaterEntryPoint, updaterBytes),
            ]);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, jsonOptions);
        var verifiedRoot = Path.Combine(
            updatesRoot,
            ProductUpdateActivationRequestProtocol.VerifiedDirectoryName,
            targetVersion);
        var trustRoot = Path.Combine(
            updatesRoot,
            ProductUpdateActivationRequestProtocol.TrustDirectoryName);
        Directory.CreateDirectory(verifiedRoot);
        Directory.CreateDirectory(trustRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(verifiedRoot, ProductUpdateActivationRequestProtocol.ManifestFileName),
            manifestBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(verifiedRoot, ProductUpdateActivationRequestProtocol.ManifestSignatureFileName),
            rsa.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        await File.WriteAllBytesAsync(
            Path.Combine(trustRoot, ProductUpdateActivationRequestProtocol.PublicKeyDocumentFileName),
            JsonSerializer.SerializeToUtf8Bytes(keyDocument, jsonOptions));

        var activationKey = RandomNumberGenerator.GetBytes(
            ProductUpdateActivationRequestProtocol.AuthenticationKeyBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(updatesRoot, ProductUpdateActivationRequestProtocol.AuthenticationKeyFileName),
            activationKey);
        var manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));
        ProductUpdatePendingActivationProtocol.Write(
            updatesRoot,
            new ProductUpdatePendingActivation(
                ProductUpdatePendingActivationProtocol.CurrentSchemaVersion,
                ProductUpdateChannel.Stable,
                targetVersion,
                _now,
                operationId,
                manifestSha256,
                manifest.Package.Sha256,
                keyDocument.KeyId,
                keyDocument.SubjectPublicKeyInfoSha256,
                ProductUpdatePendingActivationProtocol.HashAllowedHosts(["updates.example.com"])));
        var requestPath = ProductUpdateActivationRequestProtocol.Create(
            updatesRoot,
            Guid.NewGuid(),
            targetVersion,
            manifest.Channel,
            manifestSha256,
            manifest.Package.Sha256,
            39050,
            keyDocument.KeyId,
            keyDocument.SubjectPublicKeyInfoSha256,
            ["updates.example.com"],
            activationKey,
            new FixedTimeProvider(_now),
            operationId: operationId);
        var verified = ProductUpdateActivationRequestProtocol.Verify(
            requestPath,
            new FixedTimeProvider(_now.AddMinutes(1)));
        return new Fixture(
            updatesRoot,
            installRoot,
            requestPath,
            operationId,
            previousVersion,
            targetVersion,
            verified,
            new HealthyController());
    }

    private static Task<int> RunAsync(
        Fixture fixture,
        DateTimeOffset now,
        Action<ProductUpdaterApplicationCheckpoint>? checkpointObserver = null,
        Action<ProductUpdateActivationCheckpoint>? activationCheckpointObserver = null)
        => ProductUpdaterApplication.RunAsync(
            ["--activation-request", fixture.RequestPath],
            (_, _) => fixture.Health,
            () => fixture.InstallRoot,
            new FixedTimeProvider(now),
            checkpointObserver: checkpointObserver,
            activationCheckpointObserver: activationCheckpointObserver);

    private static ProductUpdateActivationJournal ReadJournal(string installRoot)
    {
        var path = Path.Combine(
            installRoot,
            ProductUpdateActivator.ActivationStateDirectoryName,
            "activation-journal.v1.json");
        return JsonSerializer.Deserialize<ProductUpdateActivationJournal>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidDataException("Test activation journal is empty.");
    }

    private static string ReadActiveVersion(string installRoot)
        => File.ReadAllText(Path.Combine(installRoot, "active-version.v1")).Trim();

    private static ProductUpdateFile CreateFile(string path, byte[] content)
        => new(path, content.LongLength, Convert.ToHexString(SHA256.HashData(content)));

    private static byte[] CreatePackage(params (string Path, byte[] Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Fastest);
                using var destination = entry.Open();
                destination.Write(file.Content);
            }
        }

        return stream.ToArray();
    }

    private sealed record Fixture(
        string UpdatesRoot,
        string InstallRoot,
        string RequestPath,
        Guid OperationId,
        string PreviousVersion,
        string TargetVersion,
        VerifiedProductUpdateActivationRequest Verified,
        HealthyController Health);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class HealthyController : IProductUpdateHealthController
    {
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
            => Task.FromResult(true);
    }
}
