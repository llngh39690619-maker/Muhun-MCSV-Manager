using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Updater;

public sealed record ProductUpdatePendingActivation(
    int SchemaVersion,
    ProductUpdateChannel Channel,
    string Version,
    DateTimeOffset NotBeforeUtc,
    Guid OperationId,
    string ManifestSha256,
    string PackageSha256,
    string SigningKeyId,
    string SigningPublicKeySha256,
    string AllowedHostsSha256,
    DateTimeOffset? LastLaunchAtUtc = null,
    int LaunchAttempts = 0);

/// <summary>
/// The durable Service-owned authorization for one activation operation. An authenticated
/// request may outlive its short replay window only while this record still authorizes the
/// exact signed artifacts and trust binding.
/// </summary>
public static class ProductUpdatePendingActivationProtocol
{
    public const int CurrentSchemaVersion = 3;
    public const string FileName = "pending-activation.v1.json";
    private const int MaximumBytes = 8 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static ProductUpdatePendingActivation? Read(string updatesRoot)
    {
        var root = NormalizeRoot(updatesRoot);
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        RejectReparseFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4_096);
        if (stream.Length is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException("Pending update schedule has an invalid size.");
        }

        ProductUpdatePendingActivation pending;
        try
        {
            pending = JsonSerializer.Deserialize<ProductUpdatePendingActivation>(stream, JsonOptions)
                ?? throw new InvalidDataException("Pending update schedule is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Pending update schedule JSON is invalid.", exception);
        }

        Validate(pending);
        return pending;
    }

    public static void Write(string updatesRoot, ProductUpdatePendingActivation pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        Validate(pending);
        if (pending.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("Only the current pending update schema can be written.");
        }

        var root = NormalizeRoot(updatesRoot);
        Directory.CreateDirectory(root);
        RejectExistingReparsePoints(root);
        WriteAtomic(
            Path.Combine(root, FileName),
            Utf8NoBom.GetBytes(JsonSerializer.Serialize(pending, JsonOptions) + "\n"));
    }

    public static void ValidateRequestBinding(
        ProductUpdatePendingActivation pending,
        ProductUpdateActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(request);
        Validate(pending);
        var expectedChannel = pending.Channel == ProductUpdateChannel.Stable ? "stable" : "beta";
        if (pending.OperationId != request.OperationId ||
            !string.Equals(pending.Version, request.TargetVersion, StringComparison.Ordinal) ||
            !string.Equals(expectedChannel, request.Channel, StringComparison.Ordinal) ||
            !string.Equals(pending.ManifestSha256, request.ManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pending.PackageSha256, request.PackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pending.SigningKeyId, request.SigningKeyId, StringComparison.Ordinal) ||
            !string.Equals(
                pending.SigningPublicKeySha256,
                request.SigningPublicKeySha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                pending.AllowedHostsSha256,
                HashAllowedHosts(request.AllowedPackageHosts),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException(
                "Activation request does not match the durable pending authorization.");
        }
    }

    public static string HashAllowedHosts(IEnumerable<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var canonical = string.Join(
            '\n',
            hosts.Select(host => host.ToLowerInvariant()).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(canonical)));
    }

    private static void Validate(ProductUpdatePendingActivation pending)
    {
        if (pending.SchemaVersion is not (2 or CurrentSchemaVersion) ||
            !Enum.IsDefined(pending.Channel) ||
            pending.OperationId == Guid.Empty ||
            pending.NotBeforeUtc.Offset != TimeSpan.Zero ||
            (pending.LastLaunchAtUtc is { } lastLaunch && lastLaunch.Offset != TimeSpan.Zero) ||
            pending.LaunchAttempts is < 0 or > 10_000 ||
            (pending.SchemaVersion == 2 &&
             (pending.LastLaunchAtUtc is not null || pending.LaunchAttempts != 0)))
        {
            throw new InvalidDataException("Pending update schedule is invalid or unsupported.");
        }

        ProductUpdateManifestParser.ValidateVersion(pending.Version);
        ValidateSha256(pending.ManifestSha256);
        ValidateSha256(pending.PackageSha256);
        ValidateSha256(pending.SigningPublicKeySha256);
        ValidateSha256(pending.AllowedHostsSha256);
        if (string.IsNullOrWhiteSpace(pending.SigningKeyId) || pending.SigningKeyId.Length > 64)
        {
            throw new InvalidDataException("Pending update trust binding is invalid.");
        }
    }

    private static void ValidateSha256(string value)
    {
        if (value is null || value.Length != 64)
        {
            throw new InvalidDataException("Pending update hash binding is invalid.");
        }

        try
        {
            _ = Convert.FromHexString(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Pending update hash binding is invalid.", exception);
        }
    }

    private static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Updates root must be an absolute path.");
        }

        return Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static void RejectExistingReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Pending update paths must not traverse a reparse point.");
            }
        }
    }

    private static void RejectReparseFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Pending update state cannot be a reparse point.");
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        if (Directory.Exists(path) ||
            (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
        {
            throw new IOException("Pending update state path is not a regular file.");
        }

        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4_096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<ProductUpdateActivationReceiptOutcome>))]
public enum ProductUpdateActivationReceiptOutcome
{
    Committed,
    RolledBack,
    Rejected,
}

public sealed record ProductUpdateActivationReceipt(
    int SchemaVersion,
    Guid OperationId,
    Guid RequestId,
    string TargetVersion,
    ProductUpdateActivationReceiptOutcome Outcome,
    string? ActiveVersion,
    DateTimeOffset CompletedAtUtc,
    string? FailureCode = null);

/// <summary>
/// Durable updater acknowledgement. Committed/RolledBack receipts are written only after the
/// matching terminal activation journal is durably observable. Rejected receipts retain a
/// fail-closed pending record for diagnosis and never authorize Service-side cleanup.
/// </summary>
public static class ProductUpdateActivationReceiptProtocol
{
    public const int CurrentSchemaVersion = 1;
    public const string DirectoryName = "activation-results";
    private const int MaximumBytes = 8 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static ProductUpdateActivationReceipt? Read(string updatesRoot, Guid operationId)
    {
        var path = GetPath(updatesRoot, operationId);
        if (!File.Exists(path))
        {
            return null;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Activation receipt cannot be a reparse point.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4_096);
        if (stream.Length is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException("Activation receipt has an invalid size.");
        }

        ProductUpdateActivationReceipt receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<ProductUpdateActivationReceipt>(stream, JsonOptions)
                ?? throw new InvalidDataException("Activation receipt is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Activation receipt JSON is invalid.", exception);
        }

        Validate(receipt);
        if (receipt.OperationId != operationId)
        {
            throw new InvalidDataException("Activation receipt operation binding is invalid.");
        }

        return receipt;
    }

    public static void WriteTerminal(
        VerifiedProductUpdateActivationRequest verified,
        ProductUpdateActivationJournal journal,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.OperationId != verified.Request.OperationId ||
            !string.Equals(journal.TargetVersion, verified.Request.TargetVersion, StringComparison.Ordinal) ||
            journal.State is not (ProductUpdateActivationState.Committed or ProductUpdateActivationState.RolledBack))
        {
            throw new InvalidDataException(
                "A terminal receipt requires the matching durable terminal activation journal.");
        }

        var outcome = journal.State == ProductUpdateActivationState.Committed
            ? ProductUpdateActivationReceiptOutcome.Committed
            : ProductUpdateActivationReceiptOutcome.RolledBack;
        var activeVersion = outcome == ProductUpdateActivationReceiptOutcome.Committed
            ? journal.TargetVersion
            : journal.PreviousVersion;
        Write(
            verified.UpdatesRoot,
            new ProductUpdateActivationReceipt(
                CurrentSchemaVersion,
                journal.OperationId,
                verified.Request.RequestId,
                journal.TargetVersion,
                outcome,
                activeVersion,
                (timeProvider ?? TimeProvider.System).GetUtcNow(),
                journal.FailureCode));
    }

    public static void WriteRejected(
        VerifiedProductUpdateActivationRequest verified,
        string failureCode,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (string.IsNullOrWhiteSpace(failureCode) || failureCode.Length > 128)
        {
            throw new ArgumentException("Activation rejection code is invalid.", nameof(failureCode));
        }

        var existing = Read(verified.UpdatesRoot, verified.Request.OperationId);
        if (existing?.Outcome is ProductUpdateActivationReceiptOutcome.Committed or
            ProductUpdateActivationReceiptOutcome.RolledBack)
        {
            return;
        }

        Write(
            verified.UpdatesRoot,
            new ProductUpdateActivationReceipt(
                CurrentSchemaVersion,
                verified.Request.OperationId,
                verified.Request.RequestId,
                verified.Request.TargetVersion,
                ProductUpdateActivationReceiptOutcome.Rejected,
                null,
                (timeProvider ?? TimeProvider.System).GetUtcNow(),
                failureCode));
    }

    public static string GetPath(string updatesRoot, Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Operation id must not be empty.", nameof(operationId));
        }

        var root = Path.GetFullPath(updatesRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return Path.Combine(root, DirectoryName, operationId.ToString("D") + ".result.json");
    }

    private static void Write(string updatesRoot, ProductUpdateActivationReceipt receipt)
    {
        Validate(receipt);
        var path = GetPath(updatesRoot, receipt.OperationId);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Activation receipt paths must not traverse a reparse point.");
            }
        }

        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4_096,
                       FileOptions.WriteThrough))
            {
                var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(receipt, JsonOptions) + "\n");
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Validate(ProductUpdateActivationReceipt receipt)
    {
        if (receipt.SchemaVersion != CurrentSchemaVersion ||
            receipt.OperationId == Guid.Empty || receipt.RequestId == Guid.Empty ||
            !Enum.IsDefined(receipt.Outcome) || receipt.CompletedAtUtc.Offset != TimeSpan.Zero ||
            (receipt.FailureCode?.Length ?? 0) > 128)
        {
            throw new InvalidDataException("Activation receipt is invalid or unsupported.");
        }

        ProductUpdateManifestParser.ValidateVersion(receipt.TargetVersion);
        if (receipt.Outcome == ProductUpdateActivationReceiptOutcome.Rejected)
        {
            if (receipt.ActiveVersion is not null || string.IsNullOrWhiteSpace(receipt.FailureCode))
            {
                throw new InvalidDataException("Rejected activation receipt is invalid.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(receipt.ActiveVersion))
            {
                throw new InvalidDataException("Terminal activation receipt has no active version.");
            }

            ProductUpdateManifestParser.ValidateVersion(receipt.ActiveVersion);
        }
    }
}
