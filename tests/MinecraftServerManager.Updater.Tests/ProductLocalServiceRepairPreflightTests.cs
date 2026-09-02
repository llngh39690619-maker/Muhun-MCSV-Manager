using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductLocalServiceRepairPreflightTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "mcsv-local-service-repair-tests",
        Guid.NewGuid().ToString("N"));

    public ProductLocalServiceRepairPreflightTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void ProductionTrustPolicy_AllowsSignedGithubAndPrivateFormalReleaseUris()
    {
        var githubPackage = new Uri(
            "https://github.com/llngh39690619-maker/Muhun-MCSV-Manager/releases/download/" +
            "v1.2.9-beta.6/Muhun-MCSV-1.2.9-beta.6-win-x64.zip");
        var privatePackage = new Uri(
            "https://muhun.tailafea21.ts.net/mcsv-updates/Muhun-MCSV-1.2.9-beta.5-win-x64.zip");

        Assert.Contains(
            githubPackage.DnsSafeHost,
            ProductLocalRepairTrustPolicy.Production.AllowedPackageHosts);
        Assert.Contains(
            privatePackage.DnsSafeHost,
            ProductLocalRepairTrustPolicy.Production.AllowedPackageHosts);
        Assert.DoesNotContain(
            "github.example.com",
            ProductLocalRepairTrustPolicy.Production.AllowedPackageHosts);
    }

    [Fact]
    public void PinnedPackageUri_AcceptsActualBeta6GithubReleasePath()
    {
        var manifest = CreatePackageBindingManifest(
            "https://github.com/llngh39690619-maker/Muhun-MCSV-Manager/releases/download/" +
            "v1.2.9-beta.6/Muhun-MCSV-1.2.9-beta.6-win-x64.zip");

        ProductLocalFormalReleaseVerifier.ValidatePinnedPackageUri(manifest);
    }

    [Theory]
    [InlineData("https://github.com/other/Muhun-MCSV-Manager/releases/download/v1.2.9-beta.6/Muhun-MCSV-1.2.9-beta.6-win-x64.zip")]
    [InlineData("https://github.com/llngh39690619-maker/Muhun-MCSV-Manager/releases/latest/Muhun-MCSV-1.2.9-beta.6-win-x64.zip")]
    [InlineData("https://github.com/llngh39690619-maker/Muhun-MCSV-Manager/releases/download/v1.2.9-beta.6/Muhun-MCSV-latest-win-x64.zip")]
    public void PinnedPackageUri_RejectsOtherGithubRepositoryPathOrFileName(string packageUrl)
    {
        var manifest = CreatePackageBindingManifest(packageUrl);

        Assert.Throws<InvalidDataException>(() =>
            ProductLocalFormalReleaseVerifier.ValidatePinnedPackageUri(manifest));
    }

    [Theory]
    [InlineData()]
    [InlineData("--repair-product-service")]
    [InlineData("--repair-product-service", "--release-root")]
    [InlineData("--repair-product-service", "--wrong", "C:\\release")]
    [InlineData("--repair-product-service", "--release-root", "relative-release")]
    [InlineData("--repair-product-service", "--release-root", "C:\\release", "unexpected")]
    public async Task MalformedCommand_IsRejectedBeforeElevationOrInstallation(params string[] args)
    {
        var administratorProbeCalled = false;
        var installationResolverCalled = false;

        var exitCode = await ProductLocalServiceRepairApplication.RunAsync(
            args,
            administratorProbe: () =>
            {
                administratorProbeCalled = true;
                return true;
            },
            installationResolver: _ =>
            {
                installationResolverCalled = true;
                throw new InvalidOperationException("must not resolve");
            });

        Assert.Equal(2, exitCode);
        Assert.False(administratorProbeCalled);
        Assert.False(installationResolverCalled);
    }

    [Fact]
    public async Task NonAdministrator_IsRejectedBeforeReadingReleaseOrManagedInstallation()
    {
        Exception? observed = null;
        var installationResolverCalled = false;

        var exitCode = await ProductLocalServiceRepairApplication.RunAsync(
            RepairArguments(_directory),
            administratorProbe: () => false,
            installationResolver: _ =>
            {
                installationResolverCalled = true;
                throw new InvalidOperationException("must not resolve");
            },
            failureObserver: error => observed = error);

        Assert.Equal(12, exitCode);
        Assert.IsType<UnauthorizedAccessException>(observed);
        Assert.False(installationResolverCalled);
    }

    [Fact]
    public async Task IncompleteFormalRelease_IsRejectedAfterReadOnlyInstallationResolutionBeforeHealthLaunch()
    {
        File.WriteAllText(Path.Combine(_directory, "release-manifest.json"), "{}");
        var installationResolverCalled = false;
        var healthControllerCalled = false;
        var cleanupScheduled = false;
        Exception? observed = null;

        var exitCode = await ProductLocalServiceRepairApplication.RunAsync(
            RepairArguments(_directory),
            administratorProbe: () => true,
            installationResolver: _ =>
            {
                installationResolverCalled = true;
                return new ProductManagedInstallation(
                    _directory,
                    Path.Combine(_directory, "data"),
                    "1.0.8",
                    "1.0.8",
                    Path.Combine(_directory, "service.exe"));
            },
            healthControllerFactory: _ =>
            {
                healthControllerCalled = true;
                throw new InvalidOperationException("must not launch");
            },
            failureObserver: error => observed = error,
            requireRunningFromReleaseUpdater: false,
            stagingResolver: (release, install) => new ProductRepairStagingIdentity(
                install,
                Path.Combine(install, "launcher"),
                release,
                "1.2.9-beta.4",
                new string('a', 32)),
            stagingContentValidator: _ => { },
            stagingCleanupScheduler: _ =>
            {
                cleanupScheduled = true;
                return true;
            });

        Assert.Equal(12, exitCode);
        Assert.NotNull(observed);
        Assert.True(installationResolverCalled);
        Assert.False(healthControllerCalled);
        Assert.True(cleanupScheduled);
    }

    [Fact]
    public async Task SignedFormalRelease_IsAcceptedButPayloadTamperingIsRejected()
    {
        using var fixture = await SignedReleaseFixture.CreateAsync(_directory);
        var verified = await fixture.VerifyAsync();

        Assert.Equal(fixture.Version, verified.UpdateManifest.Version);
        Assert.Equal(Path.GetFullPath(_directory), verified.ReleaseRoot);

        File.WriteAllBytes(
            Path.Combine(_directory, ProductFormalUpdateManifestValidator.ServiceEntryPoint),
            "MZ-SERVICF"u8.ToArray());

        await Assert.ThrowsAnyAsync<CryptographicException>(() => fixture.VerifyAsync());
    }

    [Fact]
    public async Task SignedFormalRelease_WithUnsignedExtraFile_IsRejected()
    {
        using var fixture = await SignedReleaseFixture.CreateAsync(_directory);
        File.WriteAllText(Path.Combine(_directory, "unsigned-extra.txt"), "not signed");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync());
    }

    [Fact]
    public async Task SignedFormalRelease_WithInvalidUtf8Checksums_IsRejected()
    {
        using var fixture = await SignedReleaseFixture.CreateAsync(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "SHA256SUMS.txt"), [0xff, 0xfe, 0xfd]);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync());
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

    private static string[] RepairArguments(string releaseRoot) =>
        ["--repair-product-service", "--release-root", releaseRoot];

    private static ProductUpdateManifest CreatePackageBindingManifest(string packageUrl)
        => new(
            1,
            "muhun.mcsv.manager",
            "1.2.9-beta.6",
            "beta",
            "win-x64",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "muhun.release.test",
            "rsa-pss-sha256",
            new ProductUpdatePackage(packageUrl, 1, new string('a', 64)),
            ProductFormalUpdateManifestValidator.GuiEntryPoint,
            []);

    private sealed class SignedReleaseFixture : IDisposable
    {
        private readonly RSA _rsa;
        private readonly DateTimeOffset _now;

        private SignedReleaseFixture(
            string root,
            RSA rsa,
            DateTimeOffset now,
            ProductLocalRepairTrustPolicy trustPolicy)
        {
            Root = root;
            _rsa = rsa;
            _now = now;
            TrustPolicy = trustPolicy;
        }

        public string Root { get; }
        public string Version { get; } = "1.2.9-beta.4";
        public ProductLocalRepairTrustPolicy TrustPolicy { get; }

        public static async Task<SignedReleaseFixture> CreateAsync(string root)
        {
            var now = DateTimeOffset.UtcNow;
            var rsa = RSA.Create(3072);
            var request = new CertificateRequest(
                "CN=Muhun MCSV Manager Release Signing, O=Muhun",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
            var certificateBytes = certificate.Export(X509ContentType.Cert);
            var spki = rsa.ExportSubjectPublicKeyInfo();
            var certificateSha256 = HexSha256(certificateBytes);
            var spkiSha256 = HexSha256(spki);
            var trust = new ProductLocalRepairTrustPolicy(
                spkiSha256,
                certificateSha256,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "updates.example.com" });
            var fixture = new SignedReleaseFixture(root, rsa, now, trust);
            try
            {
                await fixture.WriteAsync(certificate, certificateBytes, spki, spkiSha256, certificateSha256);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public Task<VerifiedProductLocalRelease> VerifyAsync()
            => ProductLocalFormalReleaseVerifier.VerifyAsync(
                Root,
                TrustPolicy,
                new FixedTimeProvider(_now),
                executableVersionValidator: _ => { },
                executableSignerValidator: (_, _) => { },
                requireRunningFromReleaseUpdater: false);

        public void Dispose() => _rsa.Dispose();

        private async Task WriteAsync(
            X509Certificate2 certificate,
            byte[] certificateBytes,
            byte[] spki,
            string spkiSha256,
            string certificateSha256)
        {
            const string keyId = "muhun.release.test";
            var publicKeyDocument = new ProductUpdatePublicKeyDocument(
                1,
                "muhun.mcsv.manager",
                keyId,
                "rsa-pss-sha256",
                "RSA",
                _rsa.KeySize,
                spkiSha256,
                Convert.ToBase64String(spki),
                certificateSha256,
                certificate.Subject,
                _now.AddDays(-1),
                _now.AddDays(30));
            var publicKeyBytes = JsonSerializer.SerializeToUtf8Bytes(publicKeyDocument, WebJson);

            var updatePayload = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [ProductFormalUpdateManifestValidator.GuiEntryPoint] = "MZ-GUI"u8.ToArray(),
                [ProductFormalUpdateManifestValidator.ServiceEntryPoint] = "MZ-SERVICE"u8.ToArray(),
                [ProductFormalUpdateManifestValidator.UpdaterEntryPoint] = "MZ-UPDATER"u8.ToArray(),
                ["tools/Uninstall-MuhunMcsv.ps1"] = "# signed uninstaller"u8.ToArray(),
                ["service-win-x64/update-signing-public-key.json"] = publicKeyBytes,
                ["providers/muhun.catalog/deployment.v1.json"] = "{}"u8.ToArray(),
                ["providers/muhun.catalog/muhun.catalog.mcsvp"] = "provider"u8.ToArray(),
                ["providers/muhun.catalog/publisher-public.pem"] = "PUBLIC KEY"u8.ToArray(),
                ["update-signing-public-key.json"] = publicKeyBytes,
                ["service-win-x64/appsettings.json"] =
                    "{\"Mcsv\":{\"Service\":{\"Port\":38123}}}"u8.ToArray(),
            };
            foreach (var (path, bytes) in updatePayload)
            {
                WriteFile(path, bytes);
            }

            var updateFiles = updatePayload
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new ProductUpdateFile(
                    pair.Key,
                    pair.Value.LongLength,
                    HexSha256(pair.Value)))
                .ToArray();
            var updateManifest = new ProductUpdateManifest(
                1,
                "muhun.mcsv.manager",
                Version,
                "beta",
                "win-x64",
                _now.AddMinutes(-1),
                keyId,
                "rsa-pss-sha256",
                new ProductUpdatePackage(
                    "https://updates.example.com/mcsv/test.zip",
                    1,
                    new string('a', 64)),
                ProductFormalUpdateManifestValidator.GuiEntryPoint,
                updateFiles);
            var updateManifestBytes = JsonSerializer.SerializeToUtf8Bytes(updateManifest, WebJson);
            var updateSignature = _rsa.SignData(
                updateManifestBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);

            var formalFiles = new Dictionary<string, byte[]>(updatePayload, StringComparer.OrdinalIgnoreCase)
            {
                ["publisher.cer"] = certificateBytes,
                ["update-manifest.json"] = updateManifestBytes,
                ["update-manifest.json.sig"] = updateSignature,
                ["installed-version.v1.json"] = JsonSerializer.SerializeToUtf8Bytes(
                    new ProductInstalledVersionMetadata(
                        1,
                        "muhun.mcsv.manager",
                        Version,
                        ProductFormalUpdateManifestValidator.GuiEntryPoint),
                    WebJson),
                ["Install-MuhunMcsv.ps1"] = "# signed installer"u8.ToArray(),
                ["Uninstall-MuhunMcsv.ps1"] = "# signed uninstaller"u8.ToArray(),
                ["Test-MuhunMcsvRelease.ps1"] = "# signed verifier"u8.ToArray(),
                ["開始使用.txt"] = "X MCSV 使用說明"u8.ToArray(),
            };
            foreach (var (path, bytes) in formalFiles)
            {
                WriteFile(path, bytes);
            }

            var formalEntries = formalFiles
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new
                {
                    path = pair.Key,
                    sizeBytes = pair.Value.LongLength,
                    sha256 = HexSha256(pair.Value),
                })
                .ToArray();
            var releaseManifest = new
            {
                schemaVersion = 1,
                productId = "muhun.mcsv.manager",
                version = Version,
                channel = "beta",
                runtimeIdentifier = "win-x64",
                installable = true,
                signatureAlgorithm = "rsa-pss-sha256",
                publisherTrustMode = "self-signed-local",
                publisherCertificateSha256 = certificateSha256,
                keyId,
                entryPoint = ProductFormalUpdateManifestValidator.GuiEntryPoint,
                serviceEntryPoint = ProductFormalUpdateManifestValidator.ServiceEntryPoint,
                updaterEntryPoint = ProductFormalUpdateManifestValidator.UpdaterEntryPoint,
                updatePublicKey = new { path = "update-signing-public-key.json" },
                updateManifest = new
                {
                    path = "update-manifest.json",
                    signaturePath = "update-manifest.json.sig",
                },
                authenticodeFiles = new[]
                {
                    ProductFormalUpdateManifestValidator.ServiceEntryPoint,
                    ProductFormalUpdateManifestValidator.GuiEntryPoint,
                    ProductFormalUpdateManifestValidator.UpdaterEntryPoint,
                    "Install-MuhunMcsv.ps1",
                    "Uninstall-MuhunMcsv.ps1",
                    "Test-MuhunMcsvRelease.ps1",
                    "tools/Uninstall-MuhunMcsv.ps1",
                },
                files = formalEntries,
            };
            var releaseManifestBytes = JsonSerializer.SerializeToUtf8Bytes(releaseManifest, WebJson);
            var releaseSignature = _rsa.SignData(
                releaseManifestBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
            WriteFile("release-manifest.json", releaseManifestBytes);
            WriteFile("release-manifest.json.sig", releaseSignature);
            var checksums = string.Join(
                "\r\n",
                formalEntries.Select(file => $"{file.sha256} *{file.path}")) + "\r\n";
            WriteFile("SHA256SUMS.txt", new UTF8Encoding(false, true).GetBytes(checksums));
            await Task.CompletedTask;
        }

        private void WriteFile(string relativePath, byte[] bytes)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        private static string HexSha256(ReadOnlySpan<byte> bytes)
            => Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

}
