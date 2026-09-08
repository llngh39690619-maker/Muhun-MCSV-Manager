using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.GameClient;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Owns the current Windows user's local CurseForge credential. Callers receive a new read-only
/// <see cref="SecureString"/> and must dispose it immediately after the provider operation.
/// </summary>
internal interface ICurseForgeCredentialStore
{
    bool HasCredential { get; }

    SecureString? AcquireReadOnly();

    void Save(SecureString credential);

    bool Delete();
}

/// <summary>
/// Persists one opaque CurseForge API key with Windows DPAPI CurrentUser protection. The file lives
/// below the managed installation's SID-scoped GUI data root and is never shared with the Service.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiCurseForgeCredentialStore : ICurseForgeCredentialStore
{
    private const int MaximumCredentialCharacters = 256;
    private const int MaximumPlaintextBytes = MaximumCredentialCharacters * 4;
    private const int MaximumProtectedBytes = 16 * 1024;
    private static readonly byte[] Header = "XMCSV-CFKEY-1\0"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string _credentialPath;
    private readonly byte[] _entropy;
    private readonly object _gate = new();

    public DpapiCurseForgeCredentialStore(ApplicationPaths paths)
        : this(
            GetCredentialPath(paths),
            MinecraftClientInstallationIdentity.LoadOrCreate(GetInstallationIdentityPath(paths)))
    {
    }

    internal DpapiCurseForgeCredentialStore(string credentialPath, Guid installationId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "CurseForge credential persistence requires Windows DPAPI.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(credentialPath);
        if (installationId == Guid.Empty)
        {
            throw new ArgumentException("Installation id must not be empty.", nameof(installationId));
        }

        _credentialPath = Path.GetFullPath(credentialPath);
        var parent = Path.GetDirectoryName(_credentialPath)
            ?? throw new ArgumentException(
                "CurseForge credential path has no parent directory.",
                nameof(credentialPath));
        Directory.CreateDirectory(parent);
        RejectReparsePoint(parent);

        var entropyMaterial = Encoding.UTF8.GetBytes(
            $"Muhun MCSV CurseForge credential v1\0{installationId:D}");
        try
        {
            _entropy = SHA256.HashData(entropyMaterial);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropyMaterial);
        }
    }

    public bool HasCredential
    {
        get
        {
            using var credential = AcquireReadOnly();
            return credential is not null;
        }
    }

    public SecureString? AcquireReadOnly()
    {
        lock (_gate)
        {
            var parent = Path.GetDirectoryName(_credentialPath)!;
            RejectReparsePoint(parent);
            RejectExistingReparsePoint(_credentialPath);
            if (!File.Exists(_credentialPath))
            {
                return null;
            }

            byte[]? blob = null;
            byte[]? protectedBytes = null;
            byte[]? plaintext = null;
            char[]? characters = null;
            SecureString? result = null;
            try
            {
                using (var stream = new FileStream(
                           _credentialPath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read,
                           bufferSize: 4 * 1024,
                           FileOptions.SequentialScan))
                {
                    var length = stream.Length;
                    if (length <= Header.Length || length > MaximumProtectedBytes)
                    {
                        throw new InvalidDataException(
                            "The protected CurseForge credential has an invalid size.");
                    }

                    blob = new byte[checked((int)length)];
                    stream.ReadExactly(blob);
                }

                if (blob.Length <= Header.Length ||
                    !blob.AsSpan(0, Header.Length).SequenceEqual(Header))
                {
                    throw new InvalidDataException(
                        "The protected CurseForge credential has an unsupported format.");
                }

                protectedBytes = blob.AsSpan(Header.Length).ToArray();
                plaintext = ProtectedData.Unprotect(
                    protectedBytes,
                    _entropy,
                    DataProtectionScope.CurrentUser);
                if (plaintext.Length is < 1 or > MaximumPlaintextBytes)
                {
                    throw new InvalidDataException(
                        "The protected CurseForge credential has an invalid payload size.");
                }

                var characterCount = StrictUtf8.GetCharCount(plaintext);
                if (characterCount is < 1 or > MaximumCredentialCharacters)
                {
                    throw new InvalidDataException(
                        "The protected CurseForge credential has an invalid character count.");
                }

                characters = new char[characterCount];
                StrictUtf8.GetChars(plaintext, characters);
                if (ContainsInvalidHeaderCharacter(characters))
                {
                    throw new InvalidDataException(
                        "The protected CurseForge credential contains an invalid header character.");
                }

                result = new SecureString();
                foreach (var character in characters)
                {
                    result.AppendChar(character);
                }

                result.MakeReadOnly();
                var acquired = result;
                result = null;
                return acquired;
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    "The protected CurseForge credential is not valid UTF-8.",
                    exception);
            }
            finally
            {
                result?.Dispose();
                Zero(blob);
                Zero(protectedBytes);
                Zero(plaintext);
                if (characters is not null)
                {
                    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
                }
            }
        }
    }

    public void Save(SecureString credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var snapshot = credential.Copy();
        snapshot.MakeReadOnly();
        if (snapshot.Length is < 1 or > MaximumCredentialCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(credential));
        }

        lock (_gate)
        {
            var parent = Path.GetDirectoryName(_credentialPath)!;
            Directory.CreateDirectory(parent);
            RejectReparsePoint(parent);
            RejectExistingReparsePoint(_credentialPath);

            char[]? characters = null;
            byte[]? plaintext = null;
            byte[]? protectedBytes = null;
            var native = IntPtr.Zero;
            string? temporaryPath = null;
            try
            {
                characters = new char[snapshot.Length];
                native = Marshal.SecureStringToGlobalAllocUnicode(snapshot);
                Marshal.Copy(native, characters, 0, characters.Length);
                ValidateCharacters(characters, nameof(credential));

                plaintext = StrictUtf8.GetBytes(characters);
                if (plaintext.Length is < 1 or > MaximumPlaintextBytes)
                {
                    throw new ArgumentOutOfRangeException(nameof(credential));
                }

                protectedBytes = ProtectedData.Protect(
                    plaintext,
                    _entropy,
                    DataProtectionScope.CurrentUser);
                if (protectedBytes.Length + Header.Length > MaximumProtectedBytes)
                {
                    throw new CryptographicException(
                        "The protected CurseForge credential exceeds the safe size limit.");
                }

                temporaryPath = Path.Combine(
                    parent,
                    $".{Path.GetFileName(_credentialPath)}.{Guid.NewGuid():N}.tmp");
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 4 * 1024,
                           FileOptions.WriteThrough))
                {
                    stream.Write(Header);
                    stream.Write(protectedBytes);
                    stream.Flush(flushToDisk: true);
                }

                File.SetAttributes(temporaryPath, FileAttributes.Hidden);
                File.Move(temporaryPath, _credentialPath, overwrite: true);
                temporaryPath = null;
            }
            finally
            {
                if (native != IntPtr.Zero)
                {
                    Marshal.ZeroFreeGlobalAllocUnicode(native);
                }

                if (characters is not null)
                {
                    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
                }

                Zero(plaintext);
                Zero(protectedBytes);
                if (temporaryPath is not null)
                {
                    TryDelete(temporaryPath);
                }
            }
        }
    }

    public bool Delete()
    {
        lock (_gate)
        {
            var parent = Path.GetDirectoryName(_credentialPath)!;
            RejectReparsePoint(parent);
            RejectExistingReparsePoint(_credentialPath);
            if (!File.Exists(_credentialPath))
            {
                return false;
            }

            File.Delete(_credentialPath);
            return true;
        }
    }

    private static string GetCredentialPath(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.CurseForgeCredentialFile;
    }

    private static string GetInstallationIdentityPath(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.ClientRoot, "installation.id");
    }

    private static void ValidateCharacters(ReadOnlySpan<char> characters, string parameterName)
    {
        if (characters.IsEmpty || characters.Length > MaximumCredentialCharacters)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        if (ContainsInvalidHeaderCharacter(characters))
        {
            throw new ArgumentException(
                "The CurseForge credential contains an invalid header character.",
                parameterName);
        }
    }

    private static bool ContainsInvalidHeaderCharacter(ReadOnlySpan<char> characters)
    {
        foreach (var character in characters)
        {
            if (character is '\0' or '\r' or '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("CurseForge credential paths must not be reparse points.");
        }
    }

    private static void RejectExistingReparsePoint(string path)
    {
        try
        {
            RejectReparsePoint(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void Zero(byte[]? buffer)
    {
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void TryDelete(string path)
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
}
