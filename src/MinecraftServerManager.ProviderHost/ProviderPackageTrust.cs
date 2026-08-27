using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MinecraftServerManager.ProviderHost;

public sealed record ProviderPackageSignature(
    string PublisherId,
    string Algorithm,
    string SignatureBase64,
    int FormatVersion = ProviderPackageSignatureFormat.CurrentVersion);

public static class ProviderPackageSignatureFormat
{
    public const int CurrentVersion = 1;
    private static ReadOnlySpan<byte> Domain => "Muhun-MCSV-Provider-Package\0v1\0"u8;

    /// <summary>
    /// Produces the domain-separated bytes publishers sign. Domain separation prevents a
    /// detached provider signature from being reused as a signature in another protocol.
    /// </summary>
    public static byte[] CreatePayload(ProviderPackageTrustContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.PackageLength <= 0 || context.Sha256 is null || context.Sha256.Length != 64 ||
            !context.Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Provider package trust context is invalid.", nameof(context));
        }

        var digest = Convert.FromHexString(context.Sha256);
        var payload = GC.AllocateUninitializedArray<byte>(Domain.Length + sizeof(long) + digest.Length);
        Domain.CopyTo(payload);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(Domain.Length, sizeof(long)), context.PackageLength);
        digest.CopyTo(payload.AsSpan(Domain.Length + sizeof(long)));
        CryptographicOperations.ZeroMemory(digest);
        return payload;
    }
}

public sealed record ProviderPackageTrustContext(
    string PackageFileName,
    long PackageLength,
    string Sha256);

public sealed record ProviderPackageTrustDecision(bool IsTrusted, string? FailureReason = null)
{
    public static ProviderPackageTrustDecision Trusted { get; } = new(true);
}

public interface IProviderPackageTrustVerifier
{
    ValueTask<ProviderPackageTrustDecision> VerifyAsync(
        ProviderPackageTrustContext context,
        ProviderPackageSignature signature,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Verifies detached DER-encoded ECDSA P-256 signatures over the package SHA-256 digest.
/// Publisher ids are accepted only when an administrator pinned their PEM public key.
/// </summary>
public sealed class EcdsaProviderPackageTrustVerifier : IProviderPackageTrustVerifier
{
    public const string SupportedAlgorithm = "ECDSA-P256-SHA256";
    private readonly IReadOnlyDictionary<string, string> _trustedPublishers;

    public EcdsaProviderPackageTrustVerifier(IReadOnlyDictionary<string, string> trustedPublisherPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedPublisherPublicKeys);
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (publisherId, publicKeyPem) in trustedPublisherPublicKeys)
        {
            ValidatePublisherId(publisherId);
            ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
            using var probe = ECDsa.Create();
            probe.ImportFromPem(publicKeyPem);
            if (probe.KeySize != 256)
            {
                throw new ArgumentException("Provider publisher keys must use ECDSA P-256.");
            }

            copy.Add(publisherId, publicKeyPem);
        }

        _trustedPublishers = copy;
    }

    public ValueTask<ProviderPackageTrustDecision> VerifyAsync(
        ProviderPackageTrustContext context,
        ProviderPackageSignature signature,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(signature);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(signature.PublisherId) ||
            signature.PublisherId.Length > 128 ||
            signature.PublisherId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return ValueTask.FromResult(new ProviderPackageTrustDecision(false, "Publisher identity is invalid."));
        }

        if (!_trustedPublishers.TryGetValue(signature.PublisherId, out var publicKey))
        {
            return ValueTask.FromResult(new ProviderPackageTrustDecision(false, "Publisher is not trusted."));
        }

        if (!string.Equals(signature.Algorithm, SupportedAlgorithm, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(new ProviderPackageTrustDecision(false, "Signature algorithm is unsupported."));
        }

        if (signature.FormatVersion != ProviderPackageSignatureFormat.CurrentVersion)
        {
            return ValueTask.FromResult(new ProviderPackageTrustDecision(false, "Signature format is unsupported."));
        }

        byte[] signedPayload;
        byte[] detachedSignature;
        try
        {
            signedPayload = ProviderPackageSignatureFormat.CreatePayload(context);
            detachedSignature = Convert.FromBase64String(signature.SignatureBase64);
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            return ValueTask.FromResult(new ProviderPackageTrustDecision(false, "Signature encoding is invalid."));
        }

        if (detachedSignature.Length is < 8 or > 256)
        {
            CryptographicOperations.ZeroMemory(signedPayload);
            CryptographicOperations.ZeroMemory(detachedSignature);
            return ValueTask.FromResult(new ProviderPackageTrustDecision(false, "Signature size is invalid."));
        }

        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(publicKey);
        bool valid;
        try
        {
            valid = verifier.VerifyData(
                signedPayload,
                detachedSignature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            valid = false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signedPayload);
            CryptographicOperations.ZeroMemory(detachedSignature);
        }

        return ValueTask.FromResult(valid
            ? ProviderPackageTrustDecision.Trusted
            : new ProviderPackageTrustDecision(false, "Package signature is invalid."));
    }

    private static void ValidatePublisherId(string publisherId)
    {
        if (string.IsNullOrWhiteSpace(publisherId) || publisherId.Length > 128 ||
            publisherId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("Provider publisher id is invalid.", nameof(publisherId));
        }
    }
}

public sealed class RejectUntrustedProviderPackages : IProviderPackageTrustVerifier
{
    public ValueTask<ProviderPackageTrustDecision> VerifyAsync(
        ProviderPackageTrustContext context,
        ProviderPackageSignature signature,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new ProviderPackageTrustDecision(false, "No trusted publisher store is configured."));
    }
}
