using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class ProtectedFormalReleaseVerifierTests : IDisposable
{
    private const string Version = "1.2.9-beta.4";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mcsv-protected-release-verifier-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Verify_ValidSignedExactRelease_ReturnsOnlyProtectedUpdater()
    {
        using var fixture = CreateSignedRelease();
        var security = new RecordingSecurityValidator();
        var publisherInvocations = new List<(string Path, string Version)>();
        var verifier = fixture.CreateVerifier(
            security,
            (path, version) => publisherInvocations.Add((path, version)));

        var updater = await verifier.VerifyAsync(_root, Version, CancellationToken.None);

        Assert.Equal(
            Path.Combine(_root, "updater-win-x64", "Muhun MCSV Updater.exe"),
            updater);
        Assert.Equal(_root, Assert.Single(security.TreeInvocations));
        Assert.Equal((updater, Version), Assert.Single(publisherInvocations));
    }

    [Fact]
    public async Task Verify_TamperedManifestFile_FailsBeforeAuthenticodeVerification()
    {
        using var fixture = CreateSignedRelease();
        File.AppendAllText(Path.Combine(_root, "installed-version.v1.json"), "tampered");
        var publisherInvocations = new List<(string Path, string Version)>();
        var verifier = fixture.CreateVerifier(
            new RecordingSecurityValidator(),
            (path, version) => publisherInvocations.Add((path, version)));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            verifier.VerifyAsync(_root, Version, CancellationToken.None));

        Assert.Empty(publisherInvocations);
    }

    [Fact]
    public async Task Verify_UnsignedUnexpectedFile_FailsBeforeAuthenticodeVerification()
    {
        using var fixture = CreateSignedRelease();
        File.WriteAllText(Path.Combine(_root, "unexpected.exe"), "MZ");
        var publisherInvocations = new List<(string Path, string Version)>();
        var verifier = fixture.CreateVerifier(
            new RecordingSecurityValidator(),
            (path, version) => publisherInvocations.Add((path, version)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            verifier.VerifyAsync(_root, Version, CancellationToken.None));

        Assert.Empty(publisherInvocations);
    }

    [Fact]
    public async Task Verify_InvalidUtf8ChecksumDocument_IsRejected()
    {
        using var fixture = CreateSignedRelease();
        File.WriteAllBytes(Path.Combine(_root, "SHA256SUMS.txt"), [0xff, 0xfe, 0xfd]);
        var publisherCalled = false;
        var verifier = fixture.CreateVerifier(
            new RecordingSecurityValidator(),
            (_, _) => publisherCalled = true);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            verifier.VerifyAsync(_root, Version, CancellationToken.None));
        Assert.False(publisherCalled);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup on Windows CI.
        }
    }

    private SignedReleaseFixture CreateSignedRelease()
    {
        Directory.CreateDirectory(_root);
        var now = DateTimeOffset.UtcNow;
        var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            "CN=X MCSV protected staging test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        var certificateBytes = certificate.Export(X509ContentType.Cert);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["publisher.cer"] = certificateBytes,
            ["installed-version.v1.json"] = "{\"schemaVersion\":1}"u8.ToArray(),
            ["update-manifest.json"] = "{\"schemaVersion\":1}"u8.ToArray(),
            ["update-manifest.json.sig"] = RandomNumberGenerator.GetBytes(384),
            ["update-signing-public-key.json"] = "{\"schemaVersion\":1}"u8.ToArray(),
            ["gui-win-x64/Muhun MCSV Manager.exe"] = "MZ-GUI"u8.ToArray(),
            ["service-win-x64/Muhun MCSV Service.exe"] = "MZ-SERVICE"u8.ToArray(),
            ["updater-win-x64/Muhun MCSV Updater.exe"] = "MZ-UPDATER"u8.ToArray(),
            ["開始使用.txt"] = "X MCSV 使用說明"u8.ToArray(),
        };
        foreach (var (relative, bytes) in files)
        {
            WriteFile(relative, bytes);
        }

        var entries = files
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new
            {
                path = pair.Key,
                sizeBytes = pair.Value.LongLength,
                sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)),
            })
            .ToArray();
        var certificateSha256 = Convert.ToHexString(SHA256.HashData(certificateBytes));
        var manifest = new
        {
            schemaVersion = 1,
            productId = "muhun.mcsv.manager",
            version = Version,
            channel = "beta",
            runtimeIdentifier = "win-x64",
            installable = true,
            signatureAlgorithm = "rsa-pss-sha256",
            publisherCertificateSha256 = certificateSha256,
            entryPoint = "gui-win-x64/Muhun MCSV Manager.exe",
            serviceEntryPoint = "service-win-x64/Muhun MCSV Service.exe",
            updaterEntryPoint = "updater-win-x64/Muhun MCSV Updater.exe",
            files = entries,
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        WriteFile("release-manifest.json", manifestBytes);
        WriteFile(
            "release-manifest.json.sig",
            rsa.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        WriteFile(
            "SHA256SUMS.txt",
            new UTF8Encoding(false, true).GetBytes(string.Join(
                "\r\n",
                entries.Select(entry => $"{entry.sha256} *{entry.path}")) + "\r\n"));

        return new SignedReleaseFixture(rsa, certificate, now, certificateSha256);
    }

    private void WriteFile(string relativePath, byte[] content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private sealed class SignedReleaseFixture(
        RSA rsa,
        X509Certificate2 certificate,
        DateTimeOffset now,
        string certificateSha256) : IDisposable
    {
        public PinnedProtectedFormalReleaseVerifier CreateVerifier(
            IProtectedProductPathSecurityValidator securityValidator,
            Action<string, string> publisherVerifier)
        {
            using var publicKey = certificate.GetRSAPublicKey()
                ?? throw new InvalidOperationException("Test certificate has no RSA key.");
            return new PinnedProtectedFormalReleaseVerifier(
                securityValidator,
                publisherVerifier,
                new FixedTimeProvider(now),
                certificateSha256,
                Convert.ToHexString(SHA256.HashData(publicKey.ExportSubjectPublicKeyInfo())));
        }

        public void Dispose()
        {
            certificate.Dispose();
            rsa.Dispose();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingSecurityValidator : IProtectedProductPathSecurityValidator
    {
        public List<string> TreeInvocations { get; } = [];

        public void ValidateContainer(string path, bool requireProtectedAccessRules)
        {
        }

        public void ValidateTree(string root) => TreeInvocations.Add(root);
    }
}
