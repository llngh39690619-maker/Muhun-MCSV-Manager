using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.Data;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsDpapiSecretVault : IProductSecretVault
{
    private const int MaximumSecretLength = 4_096;
    private const int MaximumBlobLength = 65_536;
    private static readonly byte[] Header = "MCSV-VAULT-1\0"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _vaultDirectory;
    private readonly byte[] _entropy;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsDpapiSecretVault(string vaultDirectory, Guid installationId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The product vault requires Windows DPAPI.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(vaultDirectory);
        if (installationId == Guid.Empty)
        {
            throw new ArgumentException("Installation id must not be empty.", nameof(installationId));
        }

        _vaultDirectory = Path.GetFullPath(vaultDirectory);
        Directory.CreateDirectory(_vaultDirectory);
        RejectReparsePoint(_vaultDirectory);
        _entropy = SHA256.HashData(
            Encoding.UTF8.GetBytes($"Muhun MCSV product vault v1\0{installationId:D}"));
    }

    public async Task SetSecretAsync(
        string secretReference,
        string secret,
        CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(secretReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        if (secret.Length > MaximumSecretLength || secret.Contains('\0'))
        {
            throw new ArgumentOutOfRangeException(nameof(secret));
        }

        byte[]? plaintext = null;
        byte[]? protectedBytes = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RejectReparsePoint(_vaultDirectory);
            plaintext = StrictUtf8.GetBytes(secret);
            protectedBytes = ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);
            if (protectedBytes.Length + Header.Length > MaximumBlobLength)
            {
                throw new CryptographicException("Protected secret exceeds the vault size limit.");
            }

            var temporaryPath = Path.Combine(
                _vaultDirectory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4_096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(Header, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, path, overwrite: true);
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            _gate.Release();
        }
    }

    public async Task<string?> GetSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(secretReference);
        byte[]? blob = null;
        byte[]? protectedBytes = null;
        byte[]? plaintext = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RejectReparsePoint(_vaultDirectory);
            if (!File.Exists(path))
            {
                return null;
            }

            RejectReparsePoint(path);
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length < Header.Length + 1 || fileInfo.Length > MaximumBlobLength)
            {
                throw new InvalidDataException("Secret vault entry has an invalid size.");
            }

            blob = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!blob.AsSpan(0, Header.Length).SequenceEqual(Header))
            {
                throw new InvalidDataException("Secret vault entry has an unsupported format.");
            }

            protectedBytes = blob.AsSpan(Header.Length).ToArray();
            plaintext = ProtectedData.Unprotect(protectedBytes, _entropy, DataProtectionScope.CurrentUser);
            if (plaintext.Length == 0 || plaintext.Length > MaximumSecretLength * 4)
            {
                throw new InvalidDataException("Secret vault entry contains an invalid payload.");
            }

            return StrictUtf8.GetString(plaintext);
        }
        finally
        {
            if (blob is not null)
            {
                CryptographicOperations.ZeroMemory(blob);
            }

            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            _gate.Release();
        }
    }

    public async Task<bool> DeleteSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(secretReference);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RejectReparsePoint(_vaultDirectory);
            if (!File.Exists(path))
            {
                return false;
            }

            RejectReparsePoint(path);
            File.Delete(path);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetSecretPath(string secretReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        if (!SecretReferencePattern().IsMatch(secretReference))
        {
            throw new ArgumentException("Secret reference is invalid.", nameof(secretReference));
        }

        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secretReference)))
            .ToLowerInvariant();
        return Path.Combine(_vaultDirectory, $"{name}.secret");
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Secret vault paths must not be reparse points.");
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[._:-][a-z0-9]+){0,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretReferencePattern();
}
