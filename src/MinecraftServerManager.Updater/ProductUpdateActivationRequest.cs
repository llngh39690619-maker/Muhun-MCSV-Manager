using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.Updater;

public sealed record ProductUpdateActivationRequest(
    int SchemaVersion,
    string ProductId,
    Guid RequestId,
    Guid OperationId,
    Guid InstallationId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string TargetVersion,
    string Channel,
    string ManifestSha256,
    string PackageSha256,
    int ServicePort,
    string SigningKeyId,
    string SigningPublicKeySha256,
    IReadOnlyList<string> AllowedPackageHosts);

public sealed record VerifiedProductUpdateActivationRequest(
    ProductUpdateActivationRequest Request,
    string UpdatesRoot,
    string RequestPath,
    string ManifestPath,
    string ManifestSignaturePath,
    string PublicKeyDocumentPath,
    string ConsumptionMarkerPath);

/// <summary>
/// Authenticates the one-shot handoff from the privileged Service to the updater. The random
/// installation key lives below the installer-ACL-protected product data root; no key or local
/// path is accepted from request JSON or from additional command-line switches.
/// </summary>
public static partial class ProductUpdateActivationRequestProtocol
{
    public const int CurrentSchemaVersion = 3;
    public const int AuthenticationKeyBytes = 32;
    public const int MaximumRequestBytes = 16 * 1024;
    public const string RequestDirectoryName = "activation-requests";
    public const string AuthenticationKeyFileName = "activation-authentication.v1.key";
    public const string TrustDirectoryName = "trust";
    public const string PublicKeyDocumentFileName = "update-signing-public-key.json";
    public const string VerifiedDirectoryName = "verified";
    public const string ManifestFileName = "manifest.v1.json";
    public const string ManifestSignatureFileName = "manifest.v1.sig";

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Create(
        string updatesRoot,
        Guid installationId,
        string targetVersion,
        string channel,
        string manifestSha256,
        string packageSha256,
        int servicePort,
        string signingKeyId,
        string signingPublicKeySha256,
        IReadOnlyCollection<string> allowedPackageHosts,
        ReadOnlySpan<byte> authenticationKey,
        TimeProvider? timeProvider = null,
        TimeSpan? lifetime = null,
        Guid? operationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updatesRoot);
        if (installationId == Guid.Empty)
        {
            throw new ArgumentException("Installation id must not be empty.", nameof(installationId));
        }

        ProductUpdateManifestParser.ValidateVersion(targetVersion);
        ValidateSignedArtifactBinding(channel, manifestSha256, packageSha256);
        if (servicePort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(servicePort));
        }

        ValidateTrustBinding(signingKeyId, signingPublicKeySha256, allowedPackageHosts);

        if (authenticationKey.Length != AuthenticationKeyBytes)
        {
            throw new ArgumentException("Activation authentication key must contain exactly 256 bits.", nameof(authenticationKey));
        }

        var duration = lifetime ?? TimeSpan.FromMinutes(10);
        if (duration < TimeSpan.FromMinutes(1) || duration > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var root = NormalizeUpdatesRoot(updatesRoot);
        Directory.CreateDirectory(root);
        RejectExistingReparsePoints(root);
        var requestsRoot = Path.Combine(root, RequestDirectoryName);
        Directory.CreateDirectory(requestsRoot);
        RejectExistingReparsePoints(requestsRoot);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Activation request clock must use UTC.");
        }

        var durableOperationId = operationId ?? Guid.NewGuid();
        if (durableOperationId == Guid.Empty)
        {
            throw new ArgumentException("Activation operation id must not be empty.", nameof(operationId));
        }

        var request = new ProductUpdateActivationRequest(
            CurrentSchemaVersion,
            "muhun.mcsv.manager",
            durableOperationId,
            durableOperationId,
            installationId,
            now,
            now.Add(duration),
            targetVersion,
            channel,
            manifestSha256,
            packageSha256,
            servicePort,
            signingKeyId,
            signingPublicKeySha256,
            allowedPackageHosts.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(request, JsonOptions) + "\n");
        var signature = HMACSHA256.HashData(authenticationKey, bytes);
        var stem = request.RequestId.ToString("D");
        var requestPath = Path.Combine(requestsRoot, $"{stem}.request.json");
        var signaturePath = Path.Combine(requestsRoot, $"{stem}.request.sig");
        if (operationId.HasValue && !File.Exists(requestPath) && File.Exists(signaturePath))
        {
            RejectReparseFile(signaturePath);
            File.Delete(signaturePath);
        }

        WriteNewFile(signaturePath, signature);
        try
        {
            WriteNewFile(requestPath, bytes);
        }
        catch
        {
            TryDelete(signaturePath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }

        return requestPath;
    }

    public static VerifiedProductUpdateActivationRequest Verify(
        string requestPath,
        TimeProvider? timeProvider = null)
        => VerifyCore(requestPath, timeProvider, authorizeExpiredFromPending: false);

    /// <summary>
    /// Verifies the authenticated handoff and permits an expired request only while the exact
    /// Service-owned pending activation still exists. This is the crash/reboot recovery entry
    /// point; callers cannot replay a completed request after pending cleanup.
    /// </summary>
    public static VerifiedProductUpdateActivationRequest VerifyForActivation(
        string requestPath,
        TimeProvider? timeProvider = null)
        => VerifyCore(requestPath, timeProvider, authorizeExpiredFromPending: true);

    private static VerifiedProductUpdateActivationRequest VerifyCore(
        string requestPath,
        TimeProvider? timeProvider,
        bool authorizeExpiredFromPending)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);
        if (!Path.IsPathFullyQualified(requestPath))
        {
            throw new InvalidDataException("Activation request path must be absolute.");
        }

        var normalizedRequestPath = Path.GetFullPath(requestPath);
        var requestDirectory = Path.GetDirectoryName(normalizedRequestPath)
            ?? throw new InvalidDataException("Activation request directory is invalid.");
        if (!string.Equals(Path.GetFileName(requestDirectory), RequestDirectoryName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Activation request is outside the fixed request directory.");
        }

        var updatesRoot = NormalizeUpdatesRoot(Path.GetDirectoryName(requestDirectory)
            ?? throw new InvalidDataException("Activation updates root is invalid."));
        if (!string.Equals(requestDirectory, Path.Combine(updatesRoot, RequestDirectoryName), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Activation request directory is not canonical.");
        }

        var match = RequestFilePattern().Match(Path.GetFileName(normalizedRequestPath));
        if (!match.Success || !Guid.TryParseExact(match.Groups["id"].Value, "D", out var fileRequestId))
        {
            throw new InvalidDataException("Activation request filename is invalid.");
        }

        RejectExistingReparsePoints(updatesRoot);
        RejectExistingReparsePoints(requestDirectory);
        RejectReparseFile(normalizedRequestPath);
        var stem = fileRequestId.ToString("D");
        var signaturePath = Path.Combine(requestDirectory, $"{stem}.request.sig");
        var keyPath = Path.Combine(updatesRoot, AuthenticationKeyFileName);
        RejectReparseFile(signaturePath);
        RejectReparseFile(keyPath);

        var requestBytes = ReadBounded(normalizedRequestPath, MaximumRequestBytes);
        var signature = ReadExact(signaturePath, SHA256.HashSizeInBytes);
        var key = ReadExact(keyPath, AuthenticationKeyBytes);
        try
        {
            var expected = HMACSHA256.HashData(key, requestBytes);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(signature, expected))
                {
                    throw new CryptographicException("Activation request authentication failed.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(signature);
        }

        ProductUpdateActivationRequest request;
        try
        {
            request = JsonSerializer.Deserialize<ProductUpdateActivationRequest>(requestBytes, JsonOptions)
                ?? throw new InvalidDataException("Activation request is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Activation request JSON is invalid.", exception);
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        ValidateRequest(request, fileRequestId, now, allowExpired: authorizeExpiredFromPending);
        if (request.ExpiresAtUtc < now)
        {
            var pending = ProductUpdatePendingActivationProtocol.Read(updatesRoot)
                ?? throw new InvalidDataException(
                    "Expired activation request has no durable pending authorization.");
            ProductUpdatePendingActivationProtocol.ValidateRequestBinding(pending, request);
        }
        var verifiedRoot = Path.Combine(updatesRoot, VerifiedDirectoryName, request.TargetVersion);
        var manifestPath = Path.Combine(verifiedRoot, ManifestFileName);
        var manifestSignaturePath = Path.Combine(verifiedRoot, ManifestSignatureFileName);
        var publicKeyPath = Path.Combine(updatesRoot, TrustDirectoryName, PublicKeyDocumentFileName);
        RejectReparseFile(manifestPath);
        RejectReparseFile(manifestSignaturePath);
        RejectReparseFile(publicKeyPath);

        return new VerifiedProductUpdateActivationRequest(
            request,
            updatesRoot,
            normalizedRequestPath,
            manifestPath,
            manifestSignaturePath,
            publicKeyPath,
            Path.Combine(requestDirectory, $"{stem}.consumed"));
    }

    public static void MarkConsumed(VerifiedProductUpdateActivationRequest verified)
    {
        ArgumentNullException.ThrowIfNull(verified);
        WriteNewFile(
            verified.ConsumptionMarkerPath,
            Utf8NoBom.GetBytes(DateTimeOffset.UtcNow.ToString("O") + "\n"));
    }

    public static bool IsConsumed(VerifiedProductUpdateActivationRequest verified)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (!File.Exists(verified.ConsumptionMarkerPath))
        {
            return false;
        }

        if ((File.GetAttributes(verified.ConsumptionMarkerPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Activation consumption marker cannot be a reparse point.");
        }

        var bytes = ReadBounded(verified.ConsumptionMarkerPath, 128);
        if (!DateTimeOffset.TryParseExact(
                Encoding.UTF8.GetString(bytes).Trim(),
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var consumedAt) ||
            consumedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Activation consumption marker is invalid.");
        }

        return true;
    }

    public static string GetRequestPath(string updatesRoot, Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("Request id must not be empty.", nameof(requestId));
        }

        var root = NormalizeUpdatesRoot(updatesRoot);
        return Path.Combine(root, RequestDirectoryName, requestId.ToString("D") + ".request.json");
    }

    public static void DeleteCompletedArtifacts(VerifiedProductUpdateActivationRequest verified)
    {
        ArgumentNullException.ThrowIfNull(verified);
        DeleteCompletedArtifacts(verified.UpdatesRoot, verified.Request.RequestId);
    }

    public static void DeleteCompletedArtifacts(string updatesRoot, Guid requestId)
    {
        var requestPath = GetRequestPath(updatesRoot, requestId);
        var requestDirectory = Path.GetDirectoryName(requestPath)
            ?? throw new InvalidDataException("Activation request directory is invalid.");
        var stem = requestId.ToString("D");
        var signaturePath = Path.Combine(requestDirectory, stem + ".request.sig");
        var consumptionMarkerPath = Path.Combine(requestDirectory, stem + ".consumed");
        foreach (var path in new[]
                 {
                     requestPath,
                     signaturePath,
                     consumptionMarkerPath,
                 })
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Completed activation artifact cannot be a reparse point.");
            }
        }

        File.Delete(requestPath);
        File.Delete(signaturePath);
        File.Delete(consumptionMarkerPath);
    }

    private static void ValidateRequest(
        ProductUpdateActivationRequest request,
        Guid fileRequestId,
        DateTimeOffset nowUtc,
        bool allowExpired)
    {
        if (nowUtc.Offset != TimeSpan.Zero ||
            request.SchemaVersion != CurrentSchemaVersion ||
            !string.Equals(request.ProductId, "muhun.mcsv.manager", StringComparison.Ordinal) ||
            request.RequestId == Guid.Empty || request.RequestId != fileRequestId ||
            request.OperationId == Guid.Empty ||
            request.InstallationId == Guid.Empty ||
            request.IssuedAtUtc.Offset != TimeSpan.Zero || request.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            request.ExpiresAtUtc <= request.IssuedAtUtc ||
            request.ExpiresAtUtc - request.IssuedAtUtc > TimeSpan.FromMinutes(15) ||
            request.IssuedAtUtc > nowUtc.AddMinutes(1) || (!allowExpired && request.ExpiresAtUtc < nowUtc) ||
            request.ServicePort is < 1024 or > 65535)
        {
            throw new InvalidDataException("Activation request metadata is invalid, expired or unsupported.");
        }

        ProductUpdateManifestParser.ValidateVersion(request.TargetVersion);
        ValidateSignedArtifactBinding(
            request.Channel,
            request.ManifestSha256,
            request.PackageSha256);
        ValidateTrustBinding(
            request.SigningKeyId,
            request.SigningPublicKeySha256,
            request.AllowedPackageHosts);
    }

    private static void ValidateSignedArtifactBinding(
        string channel,
        string manifestSha256,
        string packageSha256)
    {
        if (channel is not ("stable" or "beta") ||
            !Sha256Pattern().IsMatch(manifestSha256 ?? string.Empty) ||
            !Sha256Pattern().IsMatch(packageSha256 ?? string.Empty))
        {
            throw new InvalidDataException("Activation signed-artifact binding is invalid.");
        }
    }

    private static void ValidateTrustBinding(
        string signingKeyId,
        string signingPublicKeySha256,
        IReadOnlyCollection<string>? allowedPackageHosts)
    {
        if (!KeyIdPattern().IsMatch(signingKeyId ?? string.Empty) ||
            !Sha256Pattern().IsMatch(signingPublicKeySha256 ?? string.Empty) ||
            allowedPackageHosts is null || allowedPackageHosts.Count is < 1 or > 8)
        {
            throw new InvalidDataException("Activation trust binding is invalid.");
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in allowedPackageHosts)
        {
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253 ||
                !Uri.CheckHostName(host).Equals(UriHostNameType.Dns) ||
                !string.Equals(new UriBuilder(Uri.UriSchemeHttps, host).Host, host, StringComparison.OrdinalIgnoreCase) ||
                !unique.Add(host))
            {
                throw new InvalidDataException("Activation package host allowlist is invalid.");
            }
        }
    }

    private static string NormalizeUpdatesRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Updates root must be absolute.");
        }

        var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var driveRoot = Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(root, driveRoot, StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Updates root must be a non-root directory on a local drive.");
        }

        return root;
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4_096);
        if (stream.Length is < 1 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException("Activation file has an invalid size.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] ReadExact(string path, int expectedBytes)
    {
        var bytes = ReadBounded(path, expectedBytes);
        if (bytes.Length != expectedBytes)
        {
            throw new InvalidDataException("Activation authentication material has an invalid size.");
        }

        return bytes;
    }

    private static void WriteNewFile(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4_096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void RejectExistingReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Activation paths must not traverse a reparse point.");
            }
        }
    }

    private static void RejectReparseFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Required activation file was not found.", path);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Activation files must not be reparse points.");
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
    }

    [GeneratedRegex("^(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\\.request\\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestFilePattern();

    [GeneratedRegex("^[a-z][a-z0-9._-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
