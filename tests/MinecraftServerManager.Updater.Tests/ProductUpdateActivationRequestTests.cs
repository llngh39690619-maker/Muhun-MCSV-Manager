using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductUpdateActivationRequestTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-ActivationRequestTests",
        Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    public ProductUpdateActivationRequestTests() => Directory.CreateDirectory(_directory);

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
    public void AuthenticatedRequest_Verifies_AndMutationIsRejected()
    {
        var updatesRoot = PrepareProtocolFiles("1.0.0");
        var key = File.ReadAllBytes(Path.Combine(
            updatesRoot,
            ProductUpdateActivationRequestProtocol.AuthenticationKeyFileName));
        var requestPath = ProductUpdateActivationRequestProtocol.Create(
            updatesRoot,
            Guid.NewGuid(),
            "1.0.0",
            "stable",
            new string('c', 64),
            new string('d', 64),
            39050,
            "muhun.release",
            new string('a', 64),
            ["updates.example.com"],
            key,
            new FixedTimeProvider(_now));

        var verified = ProductUpdateActivationRequestProtocol.Verify(
            requestPath,
            new FixedTimeProvider(_now.AddMinutes(1)));

        Assert.Equal("1.0.0", verified.Request.TargetVersion);
        Assert.Equal("stable", verified.Request.Channel);
        Assert.Equal(new string('c', 64), verified.Request.ManifestSha256);
        Assert.Equal(new string('d', 64), verified.Request.PackageSha256);
        Assert.Equal(updatesRoot, verified.UpdatesRoot);

        File.AppendAllText(requestPath, " ");
        Assert.Throws<CryptographicException>(() =>
            ProductUpdateActivationRequestProtocol.Verify(
                requestPath,
                new FixedTimeProvider(_now.AddMinutes(1))));
    }

    [Fact]
    public void ExpiredRequest_IsRejected()
    {
        var updatesRoot = PrepareProtocolFiles("1.0.0");
        var key = File.ReadAllBytes(Path.Combine(
            updatesRoot,
            ProductUpdateActivationRequestProtocol.AuthenticationKeyFileName));
        var requestPath = ProductUpdateActivationRequestProtocol.Create(
            updatesRoot,
            Guid.NewGuid(),
            "1.0.0",
            "stable",
            new string('c', 64),
            new string('d', 64),
            39050,
            "muhun.release",
            new string('a', 64),
            ["updates.example.com"],
            key,
            new FixedTimeProvider(_now),
            TimeSpan.FromMinutes(1));

        Assert.Throws<InvalidDataException>(() =>
            ProductUpdateActivationRequestProtocol.Verify(
                requestPath,
                new FixedTimeProvider(_now.AddMinutes(2))));
    }

    [Fact]
    public async Task UpdaterApplication_VerifiesProvisionsAndCommitsA_BVersion()
    {
        const string previousVersion = "0.9.0";
        const string targetVersion = "1.0.0";
        const string entryPoint = ProductFormalUpdateManifestValidator.GuiEntryPoint;
        var updatesRoot = Path.Combine(_directory, "data", "updates");
        var installRoot = Path.Combine(_directory, "install");
        Directory.CreateDirectory(updatesRoot);
        Directory.CreateDirectory(Path.Combine(installRoot, "versions", previousVersion));
        Directory.CreateDirectory(Path.Combine(
            installRoot,
            "versions",
            previousVersion,
            "gui-win-x64"));
        File.WriteAllText(Path.Combine(installRoot, ".muhun-mcsv-install-root"), "muhun.mcsv.manager:1\n");
        File.WriteAllText(Path.Combine(installRoot, "active-version.v1"), previousVersion + "\n");
        File.WriteAllBytes(
            Path.Combine(installRoot, "versions", previousVersion, entryPoint),
            "MZ previous"u8.ToArray());
        ProductInstalledVersionMetadataStore.Write(
            Path.Combine(installRoot, "versions", previousVersion),
            new ProductInstalledVersionMetadata(1, "muhun.mcsv.manager", previousVersion, entryPoint));

        var guiBytes = "MZ target"u8.ToArray();
        var serviceBytes = "MZ service"u8.ToArray();
        var updaterBytes = "MZ updater"u8.ToArray();
        var packageBytes = CreatePackage(
            (entryPoint, guiBytes),
            (ProductFormalUpdateManifestValidator.ServiceEntryPoint, serviceBytes),
            (ProductFormalUpdateManifestValidator.UpdaterEntryPoint, updaterBytes));
        var packagesRoot = Path.Combine(updatesRoot, "packages");
        Directory.CreateDirectory(packagesRoot);
        await File.WriteAllBytesAsync(Path.Combine(packagesRoot, targetVersion + ".zip"), packageBytes);

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
            entryPoint,
            [
                CreateFile(entryPoint, guiBytes),
                CreateFile(ProductFormalUpdateManifestValidator.ServiceEntryPoint, serviceBytes),
                CreateFile(ProductFormalUpdateManifestValidator.UpdaterEntryPoint, updaterBytes),
            ]);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, jsonOptions);
        var signature = rsa.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var verifiedRoot = Path.Combine(
            updatesRoot,
            ProductUpdateActivationRequestProtocol.VerifiedDirectoryName,
            targetVersion);
        var trustRoot = Path.Combine(updatesRoot, ProductUpdateActivationRequestProtocol.TrustDirectoryName);
        Directory.CreateDirectory(verifiedRoot);
        Directory.CreateDirectory(trustRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(verifiedRoot, ProductUpdateActivationRequestProtocol.ManifestFileName),
            manifestBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(verifiedRoot, ProductUpdateActivationRequestProtocol.ManifestSignatureFileName),
            signature);
        await File.WriteAllBytesAsync(
            Path.Combine(trustRoot, ProductUpdateActivationRequestProtocol.PublicKeyDocumentFileName),
            JsonSerializer.SerializeToUtf8Bytes(keyDocument, jsonOptions));

        var activationKey = RandomNumberGenerator.GetBytes(
            ProductUpdateActivationRequestProtocol.AuthenticationKeyBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(updatesRoot, ProductUpdateActivationRequestProtocol.AuthenticationKeyFileName),
            activationKey);
        var requestPath = ProductUpdateActivationRequestProtocol.Create(
            updatesRoot,
            Guid.NewGuid(),
            targetVersion,
            manifest.Channel,
            Convert.ToHexString(SHA256.HashData(manifestBytes)),
            manifest.Package.Sha256,
            39050,
            keyDocument.KeyId,
            keyDocument.SubjectPublicKeyInfoSha256,
            ["updates.example.com"],
            activationKey,
            new FixedTimeProvider(_now));
        var health = new HealthyController();

        Exception? observedFailure = null;
        var exitCode = await ProductUpdaterApplication.RunAsync(
            ["--activation-request", requestPath],
            (_, _) => health,
            () => installRoot,
            new FixedTimeProvider(_now.AddMinutes(1)),
            failureObserver: exception => observedFailure = exception);

        Assert.True(exitCode == 0, observedFailure?.ToString() ?? $"Updater exit code: {exitCode}");
        Assert.Equal(targetVersion, File.ReadAllText(Path.Combine(installRoot, "active-version.v1")).Trim());
        Assert.True(File.Exists(Path.Combine(installRoot, "versions", targetVersion, entryPoint)));
        Assert.Single(health.Launched);

        var reboundRequest = ProductUpdateActivationRequestProtocol.Create(
            updatesRoot,
            Guid.NewGuid(),
            targetVersion,
            manifest.Channel,
            Convert.ToHexString(SHA256.HashData(manifestBytes)),
            manifest.Package.Sha256,
            39050,
            keyDocument.KeyId,
            keyDocument.SubjectPublicKeyInfoSha256,
            ["updates.example.com"],
            activationKey,
            new FixedTimeProvider(_now));
        var crossChannelManifest = manifest with { Channel = "beta" };
        var crossChannelBytes = JsonSerializer.SerializeToUtf8Bytes(crossChannelManifest, jsonOptions);
        await File.WriteAllBytesAsync(
            Path.Combine(verifiedRoot, ProductUpdateActivationRequestProtocol.ManifestFileName),
            crossChannelBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(verifiedRoot, ProductUpdateActivationRequestProtocol.ManifestSignatureFileName),
            rsa.SignData(crossChannelBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

        observedFailure = null;
        var rejected = await ProductUpdaterApplication.RunAsync(
            ["--activation-request", reboundRequest],
            (_, _) => health,
            () => installRoot,
            new FixedTimeProvider(_now.AddMinutes(1)),
            failureObserver: exception => observedFailure = exception);

        Assert.Equal(12, rejected);
        Assert.IsType<CryptographicException>(observedFailure);
        Assert.Single(health.Launched);
    }

    private string PrepareProtocolFiles(string version)
    {
        var updatesRoot = Path.Combine(_directory, Guid.NewGuid().ToString("N"), "updates");
        var verifiedRoot = Path.Combine(
            updatesRoot,
            ProductUpdateActivationRequestProtocol.VerifiedDirectoryName,
            version);
        var trustRoot = Path.Combine(updatesRoot, ProductUpdateActivationRequestProtocol.TrustDirectoryName);
        Directory.CreateDirectory(verifiedRoot);
        Directory.CreateDirectory(trustRoot);
        File.WriteAllBytes(
            Path.Combine(updatesRoot, ProductUpdateActivationRequestProtocol.AuthenticationKeyFileName),
            RandomNumberGenerator.GetBytes(ProductUpdateActivationRequestProtocol.AuthenticationKeyBytes));
        File.WriteAllText(
            Path.Combine(verifiedRoot, ProductUpdateActivationRequestProtocol.ManifestFileName),
            "{}");
        File.WriteAllBytes(
            Path.Combine(verifiedRoot, ProductUpdateActivationRequestProtocol.ManifestSignatureFileName),
            [1]);
        File.WriteAllText(
            Path.Combine(trustRoot, ProductUpdateActivationRequestProtocol.PublicKeyDocumentFileName),
            "{}");
        return updatesRoot;
    }

    private static byte[] CreatePackage(params (string Path, byte[] Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Optimal);
                using var output = entry.Open();
                output.Write(file.Content);
            }
        }

        return stream.ToArray();
    }

    private static ProductUpdateFile CreateFile(string path, byte[] content)
        => new(path, content.Length, Convert.ToHexString(SHA256.HashData(content)));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
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
