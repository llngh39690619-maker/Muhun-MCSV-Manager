using System.Security.Cryptography;

namespace MinecraftServerManager.Updater;

public sealed class SignedProductUpdateManifestVerifier
{
    private readonly IReadOnlyDictionary<string, RSA> _trustedKeys;
    private readonly IReadOnlySet<string> _allowedPackageHosts;
    private readonly TimeProvider _timeProvider;

    public SignedProductUpdateManifestVerifier(
        IReadOnlyDictionary<string, RSA> trustedKeys,
        IReadOnlySet<string> allowedPackageHosts,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        ArgumentNullException.ThrowIfNull(allowedPackageHosts);
        if (trustedKeys.Count is < 1 or > 8 || allowedPackageHosts.Count is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(trustedKeys));
        }

        _trustedKeys = trustedKeys;
        _allowedPackageHosts = new HashSet<string>(allowedPackageHosts, StringComparer.OrdinalIgnoreCase);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ProductUpdateManifest Verify(ReadOnlySpan<byte> manifestJson, ReadOnlySpan<byte> signature)
    {
        var manifest = ProductUpdateManifestParser.ParseAndValidate(
            manifestJson,
            _allowedPackageHosts,
            _timeProvider.GetUtcNow());
        if (!_trustedKeys.TryGetValue(manifest.KeyId, out var key) || key.KeySize < 3072)
        {
            throw new CryptographicException("Update signing key is unknown or too weak.");
        }

        if (signature.Length != key.KeySize / 8 ||
            !key.VerifyData(
                manifestJson,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss))
        {
            throw new CryptographicException("Update manifest signature is invalid.");
        }

        return manifest;
    }
}
