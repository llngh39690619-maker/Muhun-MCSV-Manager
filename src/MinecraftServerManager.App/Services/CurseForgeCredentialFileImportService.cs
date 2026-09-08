using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace MinecraftServerManager.App.Services;

internal enum CurseForgeCredentialImportResult
{
    Missing,
    TemplateEmpty,
    Imported,
    Invalid,
    Failed
}

internal interface ICurseForgeCredentialFileImportService
{
    string ImportFilePath { get; }

    string PrepareEditableFile();

    CurseForgeCredentialImportResult ImportIfPresent();
}

/// <summary>
/// Imports one user-edited plaintext CurseForge API key into the per-user DPAPI credential store.
/// The plaintext file is scrubbed and deleted before the DPAPI replacement is committed.
/// </summary>
internal sealed class CurseForgeCredentialFileImportService : ICurseForgeCredentialFileImportService
{
    private const int MaximumCredentialCharacters = 256;
    private const int MaximumImportBytes = 4 * 1024;
    private const int ScrubBufferSize = 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] EmptyTemplate = """
        # X MCSV CurseForge API Key one-time import
        # 請在下一行貼上且只貼上一組官方 API Key，儲存後回到 X MCSV 直接搜尋。
        # X MCSV 會自動讀取，以目前 Windows 使用者的 DPAPI 加密，並清除此明文檔案。

        """u8.ToArray();

    private readonly string[] _ownedDirectoryChain;
    private readonly string _clientSecretsDirectory;
    private readonly string _importFilePath;
    private readonly ICurseForgeCredentialStore _credentialStore;
    private readonly object _gate = new();

    public CurseForgeCredentialFileImportService(
        ApplicationPaths paths,
        ICurseForgeCredentialStore credentialStore)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(credentialStore);

        _ownedDirectoryChain =
        [
            Path.GetFullPath(paths.Root),
            Path.GetFullPath(paths.ClientRoot),
            Path.GetFullPath(paths.ClientSecrets)
        ];
        _clientSecretsDirectory = _ownedDirectoryChain[^1];
        _importFilePath = Path.GetFullPath(paths.CurseForgeCredentialImportFile);
        if (!string.Equals(
                Path.GetDirectoryName(_importFilePath),
                _clientSecretsDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The CurseForge credential import file must be inside ClientSecrets.",
                nameof(paths));
        }

        _credentialStore = credentialStore;
    }

    public string ImportFilePath => _importFilePath;

    public string PrepareEditableFile()
    {
        lock (_gate)
        {
            EnsureSafeSecretsDirectory();

            if (TryGetExistingAttributes(_importFilePath, out var attributes))
            {
                RejectUnsafeImportFile(attributes);
                return _importFilePath;
            }

            var created = false;
            try
            {
                using var stream = new FileStream(
                    _importFilePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 1024,
                    FileOptions.WriteThrough);
                created = true;
                if (!TryValidateExistingSecretsDirectory())
                {
                    throw new InvalidDataException(
                        "The CurseForge credential import directory changed during template creation.");
                }

                stream.Write(EmptyTemplate);
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                if (created)
                {
                    TryDeleteFile(_importFilePath);
                }

                throw;
            }

            return _importFilePath;
        }
    }

    public CurseForgeCredentialImportResult ImportIfPresent()
    {
        lock (_gate)
        {
            FileStream? stream = null;
            byte[]? bytes = null;
            char[]? characters = null;
            SecureString? credential = null;
            var containsCredential = false;
            long originalLength = 0;

            try
            {
                if (!TryValidateExistingSecretsDirectory())
                {
                    return CurseForgeCredentialImportResult.Missing;
                }

                if (!TryGetExistingAttributes(_importFilePath, out var attributes))
                {
                    return CurseForgeCredentialImportResult.Missing;
                }

                RejectUnsafeImportFile(attributes);
                stream = new FileStream(
                    _importFilePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1024,
                    FileOptions.SequentialScan);
                if (!TryValidateExistingSecretsDirectory())
                {
                    throw new InvalidDataException(
                        "The CurseForge credential import directory changed during import.");
                }

                RejectUnsafeImportFile(File.GetAttributes(_importFilePath));

                originalLength = stream.Length;
                if (originalLength > MaximumImportBytes)
                {
                    throw new InvalidDataException(
                        "The CurseForge credential import file exceeds the safe size limit.");
                }

                bytes = new byte[checked((int)originalLength)];
                stream.ReadExactly(bytes);
                var characterCount = StrictUtf8.GetCharCount(bytes);
                characters = new char[characterCount];
                StrictUtf8.GetChars(bytes, characters);

                credential = ParseCredential(characters);
                if (credential is null)
                {
                    return CurseForgeCredentialImportResult.TemplateEmpty;
                }

                containsCredential = true;
                if (!TryScrubPlaintext(stream, originalLength))
                {
                    return CurseForgeCredentialImportResult.Failed;
                }

                stream.Dispose();
                stream = null;
                if (!TryDeleteFile(_importFilePath))
                {
                    return CurseForgeCredentialImportResult.Failed;
                }

                // Commit the DPAPI replacement only after the plaintext import has been removed.
                // A credential-store failure therefore leaves any previous encrypted value intact
                // and never leaves the newly entered key in the import file for a later retry.
                _credentialStore.Save(credential);
                return CurseForgeCredentialImportResult.Imported;
            }
            catch (DecoderFallbackException)
            {
                return CurseForgeCredentialImportResult.Invalid;
            }
            catch (InvalidDataException)
            {
                return CurseForgeCredentialImportResult.Invalid;
            }
            catch (ArgumentException)
            {
                return CurseForgeCredentialImportResult.Invalid;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                CryptographicException or
                PlatformNotSupportedException)
            {
                return CurseForgeCredentialImportResult.Failed;
            }
            finally
            {
                if (bytes is not null)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }

                if (characters is not null)
                {
                    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
                }

                if (containsCredential && stream is not null)
                {
                    _ = TryScrubPlaintext(stream, originalLength);
                }

                stream?.Dispose();
                if (containsCredential)
                {
                    _ = TryDeleteFile(_importFilePath);
                }

                credential?.Dispose();
            }
        }
    }

    private static SecureString? ParseCredential(Span<char> contents)
    {
        ReadOnlySpan<char> remaining = contents;
        if (!remaining.IsEmpty && remaining[0] == '\uFEFF')
        {
            remaining = remaining[1..];
        }

        foreach (var character in remaining)
        {
            if (char.IsControl(character) && character is not '\r' and not '\n')
            {
                throw new InvalidDataException(
                    "The CurseForge credential import file contains a control character.");
            }
        }

        ReadOnlySpan<char> candidate = default;
        var foundCandidate = false;
        while (true)
        {
            var separator = remaining.IndexOfAny('\r', '\n');
            var line = separator < 0 ? remaining : remaining[..separator];
            if (!line.IsEmpty && line[0] != '#')
            {
                if (foundCandidate)
                {
                    throw new InvalidDataException(
                        "The CurseForge credential import file contains more than one value.");
                }

                candidate = line;
                foundCandidate = true;
            }

            if (separator < 0)
            {
                break;
            }

            if (remaining[separator] == '\r')
            {
                if (separator + 1 >= remaining.Length || remaining[separator + 1] != '\n')
                {
                    throw new InvalidDataException(
                        "The CurseForge credential import file contains an invalid line ending.");
                }

                remaining = remaining[(separator + 2)..];
            }
            else
            {
                remaining = remaining[(separator + 1)..];
            }
        }

        if (!foundCandidate)
        {
            return null;
        }

        if (candidate.Length is < 1 or > MaximumCredentialCharacters ||
            char.IsWhiteSpace(candidate[0]) ||
            char.IsWhiteSpace(candidate[^1]))
        {
            throw new InvalidDataException(
                "The CurseForge credential import file contains an invalid value.");
        }

        var credential = new SecureString();
        try
        {
            foreach (var character in candidate)
            {
                credential.AppendChar(character);
            }

            credential.MakeReadOnly();
            return credential;
        }
        catch
        {
            credential.Dispose();
            throw;
        }
    }

    private void EnsureSafeSecretsDirectory()
    {
        RejectExistingUnsafeOwnedDirectories();
        Directory.CreateDirectory(_clientSecretsDirectory);
        if (!TryValidateExistingSecretsDirectory())
        {
            throw new IOException(
                "The CurseForge credential import directory could not be created.");
        }
    }

    private void RejectExistingUnsafeOwnedDirectories()
    {
        foreach (var path in _ownedDirectoryChain)
        {
            if (TryGetExistingAttributes(path, out var attributes))
            {
                RejectUnsafeDirectory(path, attributes);
            }
        }
    }

    private bool TryValidateExistingSecretsDirectory()
    {
        foreach (var path in _ownedDirectoryChain)
        {
            if (!TryGetExistingAttributes(path, out var attributes))
            {
                return false;
            }

            RejectUnsafeDirectory(path, attributes);
        }

        return true;
    }

    private static void RejectUnsafeImportFile(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new InvalidDataException(
                "The CurseForge credential import path is not a regular file.");
        }
    }

    private static void RejectUnsafeDirectory(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"The CurseForge credential import path is not a regular directory: {Path.GetFileName(path)}.");
        }
    }

    private static bool TryGetExistingAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool TryScrubPlaintext(FileStream stream, long length)
    {
        byte[]? zeros = null;
        try
        {
            stream.Position = 0;
            zeros = new byte[Math.Min(ScrubBufferSize, checked((int)Math.Max(length, 1)))];
            var remaining = length;
            while (remaining > 0)
            {
                var count = checked((int)Math.Min(zeros.Length, remaining));
                stream.Write(zeros, 0, count);
                remaining -= count;
            }

            stream.Flush(flushToDisk: true);
            stream.SetLength(0);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (zeros is not null)
            {
                CryptographicOperations.ZeroMemory(zeros);
            }
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (TryGetExistingAttributes(path, out var attributes))
            {
                RejectUnsafeImportFile(attributes);
                File.Delete(path);
            }

            return !File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
