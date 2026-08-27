using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductUpdateManifestTests
{
    [Fact]
    public void ValidRsaPssSignature_IsAccepted_AndMutationIsRejected()
    {
        using var rsa = RSA.Create(3072);
        var manifest = CreateManifest();
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var signature = rsa.SignData(json, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var verifier = new SignedProductUpdateManifestVerifier(
            new Dictionary<string, RSA> { [manifest.KeyId] = rsa },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "updates.example.com" });

        Assert.Equal(manifest.Version, verifier.Verify(json, signature).Version);

        var mutated = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(json).Replace(
                "\"version\":\"1.0.0\"",
                "\"version\":\"2.0.0\"",
                StringComparison.Ordinal));
        Assert.Throws<CryptographicException>(() => verifier.Verify(mutated, signature));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("folder\\escape.exe")]
    [InlineData("CON.txt")]
    [InlineData("folder/trailing./file.exe")]
    public void UnsafeManifestPath_IsRejected(string path)
    {
        var manifest = CreateManifest() with
        {
            EntryPoint = path,
            Files = [new ProductUpdateFile(path, 1, new string('a', 64))],
        };

        Assert.Throws<InvalidDataException>(() => ProductUpdateManifestParser.ValidateAndThrow(
            manifest,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "updates.example.com" },
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PublicKeyDocument_LoadsPinnedRsa_AndRejectsFingerprintMutation()
    {
        using var rsa = RSA.Create(3072);
        var subjectPublicKeyInfo = rsa.ExportSubjectPublicKeyInfo();
        var document = new ProductUpdatePublicKeyDocument(
            1,
            "muhun.mcsv.manager",
            "muhun.release.0123456789abcdef",
            "rsa-pss-sha256",
            "RSA",
            rsa.KeySize,
            Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo)),
            Convert.ToBase64String(subjectPublicKeyInfo),
            new string('a', 64),
            "CN=Muhun MCSV Manager Release Signing, O=Muhun",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
        var json = JsonSerializer.SerializeToUtf8Bytes(
            document,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var loaded = ProductUpdatePublicKeyLoader.Load(json, out var parsed);

        Assert.Equal(document.KeyId, parsed.KeyId);
        Assert.Equal(rsa.ExportSubjectPublicKeyInfo(), loaded.ExportSubjectPublicKeyInfo());

        var mutated = document with { SubjectPublicKeyInfoSha256 = new string('0', 64) };
        var mutatedJson = JsonSerializer.SerializeToUtf8Bytes(
            mutated,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Throws<CryptographicException>(() =>
            ProductUpdatePublicKeyLoader.Load(mutatedJson, out _));
    }

    [Fact]
    public void ProductChannelRuntimeVersionAndExactHostBindings_AreFailClosed()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "updates.example.com",
        };
        var now = DateTimeOffset.UtcNow;
        var valid = CreateManifest();

        Assert.Throws<InvalidDataException>(() => ProductUpdateManifestParser.ValidateAndThrow(
            valid with { ProductId = "other.product" }, allowed, now));
        Assert.Throws<InvalidDataException>(() => ProductUpdateManifestParser.ValidateAndThrow(
            valid with { Channel = "nightly" }, allowed, now));
        Assert.Throws<InvalidDataException>(() => ProductUpdateManifestParser.ValidateAndThrow(
            valid with { RuntimeIdentifier = "linux-x64" }, allowed, now));
        Assert.Throws<InvalidDataException>(() => ProductUpdateManifestParser.ValidateAndThrow(
            valid with { Version = "01.0.0" }, allowed, now));
        Assert.Throws<InvalidDataException>(() => ProductUpdateManifestParser.ValidateAndThrow(
            valid with
            {
                Package = valid.Package with
                {
                    Url = "https://sub.updates.example.com/mcsv/1.0.0.zip",
                },
            },
            allowed,
            now));
    }

    internal static ProductUpdateManifest CreateManifest(IReadOnlyList<ProductUpdateFile>? files = null)
        => new(
            1,
            "muhun.mcsv.manager",
            "1.0.0",
            "stable",
            "win-x64",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "muhun.release",
            "rsa-pss-sha256",
            new ProductUpdatePackage(
                "https://updates.example.com/mcsv/1.0.0.zip",
                1024,
                new string('b', 64)),
            "Muhun MCSV Manager.exe",
            files ?? [new ProductUpdateFile("Muhun MCSV Manager.exe", 1, new string('a', 64))]);
}
