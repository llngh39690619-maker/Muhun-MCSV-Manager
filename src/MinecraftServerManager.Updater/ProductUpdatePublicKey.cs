using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.Updater;

public sealed record ProductUpdatePublicKeyDocument(
    int SchemaVersion,
    string ProductId,
    string KeyId,
    string SignatureAlgorithm,
    string KeyAlgorithm,
    int KeySize,
    string SubjectPublicKeyInfoSha256,
    string SubjectPublicKeyInfo,
    string PublisherCertificateSha256,
    string PublisherCertificateSubject,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset NotAfterUtc);

public static partial class ProductUpdatePublicKeyLoader
{
    public const int MaximumDocumentBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static RSA Load(
        ReadOnlySpan<byte> utf8Json,
        out ProductUpdatePublicKeyDocument document)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException("Update public-key document has an invalid size.");
        }

        try
        {
            document = JsonSerializer.Deserialize<ProductUpdatePublicKeyDocument>(utf8Json, JsonOptions)
                ?? throw new InvalidDataException("Update public-key document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Update public-key document JSON is invalid.", exception);
        }

        if (document.SchemaVersion != 1 ||
            !string.Equals(document.ProductId, "muhun.mcsv.manager", StringComparison.Ordinal) ||
            !KeyIdPattern().IsMatch(document.KeyId ?? string.Empty) ||
            !string.Equals(document.SignatureAlgorithm, "rsa-pss-sha256", StringComparison.Ordinal) ||
            !string.Equals(document.KeyAlgorithm, "RSA", StringComparison.Ordinal) ||
            document.KeySize is < 3072 or > 8192 ||
            !Sha256Pattern().IsMatch(document.SubjectPublicKeyInfoSha256 ?? string.Empty) ||
            !Sha256Pattern().IsMatch(document.PublisherCertificateSha256 ?? string.Empty) ||
            string.IsNullOrWhiteSpace(document.PublisherCertificateSubject) ||
            document.PublisherCertificateSubject.Length > 512 ||
            document.NotBeforeUtc.Offset != TimeSpan.Zero ||
            document.NotAfterUtc.Offset != TimeSpan.Zero ||
            document.NotAfterUtc <= document.NotBeforeUtc)
        {
            throw new InvalidDataException("Update public-key metadata is invalid or unsupported.");
        }

        byte[] subjectPublicKeyInfo;
        try
        {
            subjectPublicKeyInfo = Convert.FromBase64String(document.SubjectPublicKeyInfo ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Update public key is not valid Base64.", exception);
        }

        if (subjectPublicKeyInfo.Length is < 384 or > 2_048)
        {
            throw new InvalidDataException("Update public key has an invalid encoded size.");
        }

        var expectedHash = Convert.FromHexString(document.SubjectPublicKeyInfoSha256!);
        var actualHash = SHA256.HashData(subjectPublicKeyInfo);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new CryptographicException("Update public-key fingerprint does not match its document.");
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != subjectPublicKeyInfo.Length ||
                rsa.KeySize != document.KeySize ||
                rsa.KeySize is < 3072 or > 8192)
            {
                throw new CryptographicException("Update public key strength or encoding is invalid.");
            }

            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9._-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
