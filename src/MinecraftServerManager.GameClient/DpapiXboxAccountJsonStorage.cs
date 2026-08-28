using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XboxAuthNet.Game.Accounts.JsonStorage;

namespace MinecraftServerManager.GameClient;

/// <summary>DPAPI CurrentUser storage for Microsoft/Xbox refresh-token state.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiXboxAccountJsonStorage : IJsonStorage
{
    private const int MaximumPlaintextBytes = 1024 * 1024;
    private const int MaximumProtectedBytes = MaximumPlaintextBytes + 64 * 1024;
    private static readonly byte[] Header = "XMCSV-MSA-1\0"u8.ToArray();
    private readonly string _path;
    private readonly byte[] _entropy;
    private readonly object _gate = new();

    public DpapiXboxAccountJsonStorage(string path, Guid installationId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Microsoft account persistence requires Windows DPAPI.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (installationId == Guid.Empty)
        {
            throw new ArgumentException("Installation id must not be empty.", nameof(installationId));
        }

        _path = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("Account vault path has no parent directory.", nameof(path));
        Directory.CreateDirectory(parent);
        RejectReparsePoint(parent);
        _entropy = SHA256.HashData(
            Encoding.UTF8.GetBytes($"Muhun MCSV client account vault v1\0{installationId:D}"));
    }

    public JsonNode ReadAsJsonNode()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new JsonObject();
            }

            RejectReparsePoint(_path);
            var blob = File.ReadAllBytes(_path);
            byte[]? protectedBytes = null;
            byte[]? plaintext = null;
            try
            {
                if (blob.Length <= Header.Length || blob.Length > MaximumProtectedBytes ||
                    !blob.AsSpan(0, Header.Length).SequenceEqual(Header))
                {
                    throw new InvalidDataException("Microsoft account vault has an invalid format.");
                }

                protectedBytes = blob.AsSpan(Header.Length).ToArray();
                plaintext = ProtectedData.Unprotect(protectedBytes, _entropy, DataProtectionScope.CurrentUser);
                if (plaintext.Length is < 2 or > MaximumPlaintextBytes)
                {
                    throw new InvalidDataException("Microsoft account vault has an invalid payload size.");
                }

                return JsonNode.Parse(
                           plaintext,
                           documentOptions: new JsonDocumentOptions
                           {
                               AllowTrailingCommas = false,
                               CommentHandling = JsonCommentHandling.Disallow,
                               MaxDepth = 64,
                           })
                       ?? new JsonObject();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(blob);
                if (protectedBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }

                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
    }

    public void Write(JsonNode jsonNode, JsonSerializerOptions? jsonSerializerOptions)
    {
        ArgumentNullException.ThrowIfNull(jsonNode);
        jsonSerializerOptions ??= new JsonSerializerOptions(JsonSerializerDefaults.Web);
        lock (_gate)
        {
            var parent = Path.GetDirectoryName(_path)!;
            RejectReparsePoint(parent);
            byte[]? plaintext = null;
            byte[]? protectedBytes = null;
            try
            {
                plaintext = Encoding.UTF8.GetBytes(jsonNode.ToJsonString(jsonSerializerOptions));
                if (plaintext.Length is < 2 or > MaximumPlaintextBytes)
                {
                    throw new InvalidDataException("Microsoft account data exceeds the safe storage limit.");
                }

                protectedBytes = ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);
                if (protectedBytes.Length + Header.Length > MaximumProtectedBytes)
                {
                    throw new CryptographicException("Protected Microsoft account data exceeds the safe limit.");
                }

                var temporaryPath = Path.Combine(
                    parent,
                    $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var stream = new FileStream(
                               temporaryPath,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.None,
                               16 * 1024,
                               FileOptions.WriteThrough))
                    {
                        stream.Write(Header);
                        stream.Write(protectedBytes);
                        stream.Flush(flushToDisk: true);
                    }

                    File.Move(temporaryPath, _path, overwrite: true);
                    File.SetAttributes(_path, File.GetAttributes(_path) | FileAttributes.Hidden);
                }
                finally
                {
                    TryDelete(temporaryPath);
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
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Microsoft account vault paths cannot be reparse points.");
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
