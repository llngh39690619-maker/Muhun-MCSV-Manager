using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Service;

public enum ProductLocalApiAuthenticationResult
{
    Authenticated,
    Missing,
    Rejected,
}

/// <summary>
/// Service-local REST capability. The secret is never exposed by REST or IPC and is intended only
/// for a co-hosted, authenticated adapter after its user authorization has already succeeded.
/// </summary>
public sealed class ProductLocalApiAuthenticator(ProductDataLayout layout)
{
    public const string FileName = "service-rest-token.v1";
    private readonly object _gate = new();
    private byte[]? _tokenBytes;

    public string FilePath => Path.Combine(layout.Secrets, FileName);

    public void Initialize()
    {
        lock (_gate)
        {
            if (_tokenBytes is not null)
            {
                return;
            }

            Directory.CreateDirectory(layout.Secrets);
            RejectExistingReparsePoints(layout.Secrets);
            string token;
            if (File.Exists(FilePath))
            {
                RejectExistingReparsePoints(FilePath);
                token = ReadExisting();
            }
            else
            {
                token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var temporary = Path.Combine(
                    layout.Secrets,
                    $".{FileName}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
                try
                {
                    using var stream = new FileStream(
                        temporary,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        256,
                        FileOptions.WriteThrough);
                    var bytes = Encoding.ASCII.GetBytes(token + Environment.NewLine);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                    stream.Dispose();
                    try
                    {
                        File.Move(temporary, FilePath, overwrite: false);
                    }
                    catch (IOException) when (File.Exists(FilePath))
                    {
                        token = ReadExisting();
                    }
                }
                finally
                {
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }

            _tokenBytes = Encoding.ASCII.GetBytes(token);
        }
    }

    public ProductLocalApiAuthenticationResult Authenticate(string? suppliedToken)
    {
        if (string.IsNullOrEmpty(suppliedToken))
        {
            return ProductLocalApiAuthenticationResult.Missing;
        }

        byte[] expected;
        lock (_gate)
        {
            expected = _tokenBytes?.ToArray()
                ?? throw new InvalidOperationException("Local REST authentication is not initialized.");
        }

        var supplied = Encoding.ASCII.GetBytes(suppliedToken);
        var valid = supplied.Length == expected.Length &&
                    CryptographicOperations.FixedTimeEquals(supplied, expected);
        CryptographicOperations.ZeroMemory(supplied);
        CryptographicOperations.ZeroMemory(expected);
        return valid
            ? ProductLocalApiAuthenticationResult.Authenticated
            : ProductLocalApiAuthenticationResult.Rejected;
    }

    private string ReadExisting()
    {
        RejectExistingReparsePoints(FilePath);
        if (!File.Exists(FilePath) ||
            (File.GetAttributes(FilePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Stored Service REST token must be a regular file.");
        }

        var length = new FileInfo(FilePath).Length;
        if (length is < 64 or > ProductLocalApiAuthentication.MaximumCredentialFileBytes)
        {
            throw new InvalidDataException("Stored Service REST token has an invalid length.");
        }

        var text = File.ReadAllText(FilePath, Encoding.ASCII).Trim();
        if (text.Length != 64 || text.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Stored Service REST token is invalid.");
        }

        return text.ToUpperInvariant();
    }

    private static void RejectExistingReparsePoints(string path)
    {
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(Path.GetFullPath(path))
            : new DirectoryInfo(Path.GetFullPath(path));
        for (; current is not null; current = current switch
               {
                   FileInfo file => file.Directory,
                   DirectoryInfo directory => directory.Parent,
                   _ => null,
               })
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    "Service REST token paths cannot traverse a reparse point.");
            }
        }
    }
}
