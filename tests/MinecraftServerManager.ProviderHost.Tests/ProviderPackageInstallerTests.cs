using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost.Tests;

public sealed class ProviderPackageInstallerTests
{
    [Fact]
    public async Task InstallAsync_VerifiesAndAtomicallyRegistersPackageDisabled()
    {
        using var fixture = new PackageFixture();
        var package = fixture.CreatePackage();
        var (installer, registry) = await fixture.CreateInstallerAsync();

        var result = await installer.InstallAsync(fixture.RequestFor(package));

        Assert.True(Directory.Exists(result.InstalledDirectory));
        Assert.True(File.Exists(Path.Combine(result.InstalledDirectory, "bin", "provider.exe")));
        Assert.False(result.Registration.IsEnabled);
        Assert.Equal(ProviderHealthStatus.Disabled, result.Registration.Health);
        var registered = Assert.Single(registry.GetAll());
        Assert.Equal("example.catalog", registered.Manifest.Id);
        Assert.Equal(fixture.Sha256(package), registered.PackageSha256);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.Packages, ".install-*"));
    }

    [Fact]
    public async Task InstallAsync_RejectsTraversalWithoutWritingOutsideStaging()
    {
        using var fixture = new PackageFixture();
        var package = fixture.CreatePackage(archive =>
        {
            WriteEntry(archive, "../escaped.txt", "must-not-escape");
        });
        var (installer, _) = await fixture.CreateInstallerAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(fixture.RequestFor(package)));

        Assert.False(File.Exists(Path.Combine(fixture.Root, "escaped.txt")));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.Packages, ".install-*"));
    }

    [Fact]
    public async Task InstallAsync_RejectsArchiveReparsePoint()
    {
        using var fixture = new PackageFixture();
        var package = fixture.CreatePackage(archive =>
        {
            var link = archive.CreateEntry("link");
            link.ExternalAttributes = (int)FileAttributes.ReparsePoint;
            using var writer = new StreamWriter(link.Open());
            writer.Write("target");
        });
        var (installer, _) = await fixture.CreateInstallerAsync();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(fixture.RequestFor(package)));

        Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_RejectsManifestIdentityMismatch()
    {
        using var fixture = new PackageFixture();
        var package = fixture.CreatePackage();
        var (installer, registry) = await fixture.CreateInstallerAsync();
        var request = fixture.RequestFor(package) with { ExpectedProviderId = "different.provider" };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(request));

        Assert.Contains("does not match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public async Task InstallAsync_RejectsHostApiManifestMismatch()
    {
        using var fixture = new PackageFixture();
        var incompatible = PackageFixture.ValidManifest() with { ApiVersion = new ProductApiVersion(2, 0) };
        var package = fixture.CreatePackage(manifest: incompatible);
        var (installer, _) = await fixture.CreateInstallerAsync();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(fixture.RequestFor(package)));

        Assert.Contains("manifest validation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_RejectsPayloadDigestMismatch()
    {
        using var fixture = new PackageFixture();
        var manifest = PackageFixture.ValidManifest() with
        {
            FileSha256 = new Dictionary<string, string>
            {
                ["bin/provider.exe"] = new string('0', 64),
            },
        };
        var package = fixture.CreatePackage(manifest: manifest);
        var (installer, registry) = await fixture.CreateInstallerAsync();

        await Assert.ThrowsAsync<CryptographicException>(() =>
            installer.InstallAsync(fixture.RequestFor(package)));

        Assert.Empty(registry.GetAll());
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.Packages, ".install-*"));
    }

    [Fact]
    public async Task InstallAsync_RejectsUndeclaredPayloadFile()
    {
        using var fixture = new PackageFixture();
        var package = fixture.CreatePackage(archive => WriteEntry(archive, "extra.dll", "extra"));
        var (installer, registry) = await fixture.CreateInstallerAsync();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(fixture.RequestFor(package)));

        Assert.Contains("digest table", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public async Task InstallAsync_RejectsProviderIdentityTakeoverByDifferentPublisher()
    {
        using var fixture = new PackageFixture();
        var initial = fixture.CreatePackage();
        var (installer, registry) = await fixture.CreateInstallerAsync();
        await installer.InstallAsync(fixture.RequestFor(initial));
        var replacementManifest = PackageFixture.ValidManifest() with { Version = "1.3.0" };
        var replacement = fixture.CreatePackage(manifest: replacementManifest);
        var request = fixture.RequestFor(replacement) with
        {
            ExpectedVersion = "1.3.0",
            ExpectedPublisherId = "other.publisher",
            Signature = new ProviderPackageSignature(
                "other.publisher",
                EcdsaProviderPackageTrustVerifier.SupportedAlgorithm,
                Convert.ToBase64String([1, 2, 3])),
        };

        var error = await Assert.ThrowsAsync<CryptographicException>(() =>
            installer.InstallAsync(request));

        Assert.Contains("different publisher", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1.2.3", Assert.Single(registry.GetAll()).Manifest.Version);
    }

    [Fact]
    public async Task InstallAsync_RejectsDowngradeUnlessExplicitlyAuthorized()
    {
        using var fixture = new PackageFixture();
        var initial = fixture.CreatePackage();
        var (installer, registry) = await fixture.CreateInstallerAsync();
        await installer.InstallAsync(fixture.RequestFor(initial));
        var oldManifest = PackageFixture.ValidManifest() with { Version = "1.1.0" };
        var oldPackage = fixture.CreatePackage(manifest: oldManifest);
        var request = fixture.RequestFor(oldPackage) with { ExpectedVersion = "1.1.0" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(request));

        Assert.Contains("downgrade", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1.2.3", Assert.Single(registry.GetAll()).Manifest.Version);
    }

    [Fact]
    public async Task InstallAsync_RejectsSha256MismatchBeforeTrustDecision()
    {
        using var fixture = new PackageFixture();
        var package = fixture.CreatePackage();
        var verifier = new RecordingTrustVerifier();
        var (installer, _) = await fixture.CreateInstallerAsync(verifier);
        var request = fixture.RequestFor(package) with { ExpectedSha256 = new string('0', 64) };

        await Assert.ThrowsAsync<CryptographicException>(() => installer.InstallAsync(request));

        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public async Task InstallAsync_RejectsUntrustedPublisherBeforeArchiveParsing()
    {
        using var fixture = new PackageFixture();
        var malformed = Path.Combine(fixture.Root, "malformed.zip");
        await File.WriteAllTextAsync(malformed, "this is not a zip");
        var registry = new ProviderRegistry(fixture.Layout);
        await registry.LoadAsync();
        var installer = new ProviderPackageInstaller(
            fixture.Layout,
            registry,
            new RejectUntrustedProviderPackages());

        var error = await Assert.ThrowsAsync<CryptographicException>(() =>
            installer.InstallAsync(fixture.RequestFor(malformed)));

        Assert.Contains("verification failed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EcdsaVerifier_AcceptsPinnedPublisherAndRejectsTamperedDigest()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var digest = SHA256.HashData("provider package"u8.ToArray());
        var context = new ProviderPackageTrustContext(
            "provider.zip",
            1,
            Convert.ToHexString(digest));
        var signedPayload = ProviderPackageSignatureFormat.CreatePayload(context);
        var signature = signer.SignData(
            signedPayload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var envelope = new ProviderPackageSignature(
            "muhun.test-publisher",
            EcdsaProviderPackageTrustVerifier.SupportedAlgorithm,
            Convert.ToBase64String(signature));
        var verifier = new EcdsaProviderPackageTrustVerifier(
            new Dictionary<string, string>
            {
                ["muhun.test-publisher"] = signer.ExportSubjectPublicKeyInfoPem(),
            });

        var accepted = await verifier.VerifyAsync(
            context,
            envelope);
        var tampered = digest.ToArray();
        tampered[0] ^= 0xff;
        var rejected = await verifier.VerifyAsync(
            new ProviderPackageTrustContext("provider.zip", 1, Convert.ToHexString(tampered)),
            envelope);

        Assert.True(accepted.IsTrusted);
        Assert.False(rejected.IsTrusted);
    }

    [Fact]
    public async Task EcdsaVerifier_RejectsUnknownSignatureEnvelopeVersion()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var digest = SHA256.HashData("provider package"u8.ToArray());
        var context = new ProviderPackageTrustContext("provider.zip", 42, Convert.ToHexString(digest));
        var signature = signer.SignData(
            ProviderPackageSignatureFormat.CreatePayload(context),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var verifier = new EcdsaProviderPackageTrustVerifier(
            new Dictionary<string, string>
            {
                ["muhun.test-publisher"] = signer.ExportSubjectPublicKeyInfoPem(),
            });

        var result = await verifier.VerifyAsync(
            context,
            new ProviderPackageSignature(
                "muhun.test-publisher",
                EcdsaProviderPackageTrustVerifier.SupportedAlgorithm,
                Convert.ToBase64String(signature),
                FormatVersion: 99));

        Assert.False(result.IsTrusted);
        Assert.Contains("format", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EcdsaVerifier_FailsClosedForMalformedDerSignature()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var digest = SHA256.HashData("provider package"u8.ToArray());
        var verifier = new EcdsaProviderPackageTrustVerifier(
            new Dictionary<string, string>
            {
                ["muhun.test-publisher"] = signer.ExportSubjectPublicKeyInfoPem(),
            });

        var result = await verifier.VerifyAsync(
            new ProviderPackageTrustContext("provider.zip", 42, Convert.ToHexString(digest)),
            new ProviderPackageSignature(
                "muhun.test-publisher",
                EcdsaProviderPackageTrustVerifier.SupportedAlgorithm,
                Convert.ToBase64String(new byte[8])));

        Assert.False(result.IsTrusted);
    }

    private static void WriteEntry(ZipArchive archive, string path, string text)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    private sealed class RecordingTrustVerifier : IProviderPackageTrustVerifier
    {
        public int CallCount { get; private set; }

        public ValueTask<ProviderPackageTrustDecision> VerifyAsync(
            ProviderPackageTrustContext context,
            ProviderPackageSignature signature,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(ProviderPackageTrustDecision.Trusted);
        }
    }

    private sealed class PackageFixture : IDisposable
    {
        public PackageFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "mcsv-provider-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Layout = new ProviderHostLayout(Path.Combine(Root, "provider-host"));
        }

        public string Root { get; }
        public ProviderHostLayout Layout { get; }

        public string CreatePackage(
            Action<ZipArchive>? customize = null,
            ProductProviderManifest? manifest = null)
        {
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            WriteEntry(
                archive,
                ProviderPackageInstaller.ManifestFileName,
                JsonSerializer.Serialize(manifest ?? ValidManifest(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            WriteEntry(archive, "bin/provider.exe", "not-a-real-executable-but-never-loaded-by-installer");
            customize?.Invoke(archive);
            return path;
        }

        public async Task<(ProviderPackageInstaller Installer, ProviderRegistry Registry)> CreateInstallerAsync(
            IProviderPackageTrustVerifier? trustVerifier = null)
        {
            var registry = new ProviderRegistry(Layout);
            await registry.LoadAsync();
            return (
                new ProviderPackageInstaller(
                    Layout,
                    registry,
                    trustVerifier ?? new RecordingTrustVerifier()),
                registry);
        }

        public ProviderPackageInstallRequest RequestFor(string package) => new(
            package,
            Sha256(package),
            "example.catalog",
            "1.2.3",
            "muhun.test-publisher",
            new ProviderPackageSignature(
                "muhun.test-publisher",
                EcdsaProviderPackageTrustVerifier.SupportedAlgorithm,
                Convert.ToBase64String([1, 2, 3])));

        public string Sha256(string path)
        {
            using var input = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        }

        public static ProductProviderManifest ValidManifest() => new(
            ProductProviderManifestValidator.CurrentSchemaVersion,
            "example.catalog",
            "Example Catalog",
            "1.2.3",
            ProductApiProtocol.CurrentVersion,
            "bin/provider.exe",
            [ProductProviderCapabilities.ModpackCatalog],
            [ProductProviderPermissions.Http],
            ["api.example.com"],
            new Dictionary<string, string>
            {
                ["bin/provider.exe"] = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(
                        "not-a-real-executable-but-never-loaded-by-installer"))).ToLowerInvariant(),
            });

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
