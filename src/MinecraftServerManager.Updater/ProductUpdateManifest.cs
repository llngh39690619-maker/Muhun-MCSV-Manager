using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.Updater;

public sealed record ProductUpdatePackage(
    string Url,
    long SizeBytes,
    string Sha256);

public sealed record ProductUpdateFile(
    string Path,
    long SizeBytes,
    string Sha256);

public sealed record ProductUpdateManifest(
    int SchemaVersion,
    string ProductId,
    string Version,
    string Channel,
    string RuntimeIdentifier,
    DateTimeOffset PublishedAtUtc,
    string KeyId,
    string SignatureAlgorithm,
    ProductUpdatePackage Package,
    string EntryPoint,
    IReadOnlyList<ProductUpdateFile> Files);

public static partial class ProductUpdateManifestParser
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumManifestBytes = 256 * 1024;
    public const int MaximumFiles = 10_000;
    public const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static ProductUpdateManifest ParseAndValidate(
        ReadOnlySpan<byte> utf8Json,
        IReadOnlySet<string> allowedPackageHosts,
        DateTimeOffset nowUtc)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("Update manifest has an invalid size.");
        }

        ProductUpdateManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ProductUpdateManifest>(utf8Json, JsonOptions)
                ?? throw new InvalidDataException("Update manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Update manifest JSON is invalid.", exception);
        }

        ValidateAndThrow(manifest, allowedPackageHosts, nowUtc);
        return manifest;
    }

    public static void ValidateAndThrow(
        ProductUpdateManifest manifest,
        IReadOnlySet<string> allowedPackageHosts,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(allowedPackageHosts);
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Validation clock must use UTC.", nameof(nowUtc));
        }

        if (manifest.SchemaVersion != CurrentSchemaVersion ||
            !string.Equals(manifest.ProductId, "muhun.mcsv.manager", StringComparison.Ordinal) ||
            !IsValidVersion(manifest.Version) ||
            manifest.Channel is not ("stable" or "beta") ||
            !string.Equals(manifest.RuntimeIdentifier, "win-x64", StringComparison.Ordinal) ||
            manifest.PublishedAtUtc.Offset != TimeSpan.Zero ||
            manifest.PublishedAtUtc > nowUtc.AddMinutes(5) ||
            !KeyIdPattern().IsMatch(manifest.KeyId ?? string.Empty) ||
            !string.Equals(manifest.SignatureAlgorithm, "rsa-pss-sha256", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update manifest metadata is invalid or unsupported.");
        }

        ValidatePackage(manifest.Package, allowedPackageHosts);
        if (manifest.Files is null || manifest.Files.Count is < 1 or > MaximumFiles)
        {
            throw new InvalidDataException("Update manifest file list is missing or too large.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;
        foreach (var file in manifest.Files)
        {
            ProductUpdatePath.ValidateRelativeFilePath(file.Path);
            if (!paths.Add(file.Path) || file.SizeBytes is < 0 or > MaximumPackageBytes ||
                !Sha256Pattern().IsMatch(file.Sha256 ?? string.Empty))
            {
                throw new InvalidDataException("Update manifest contains an invalid or duplicate file.");
            }

            try
            {
                totalSize = checked(totalSize + file.SizeBytes);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("Update manifest total size is invalid.", exception);
            }
        }

        if (totalSize > MaximumPackageBytes)
        {
            throw new InvalidDataException("Update manifest extracted size exceeds the product limit.");
        }

        ProductUpdatePath.ValidateRelativeFilePath(manifest.EntryPoint);
        if (!paths.Contains(manifest.EntryPoint) ||
            !manifest.EntryPoint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update manifest entry point is missing or invalid.");
        }
    }

    public static void ValidateVersion(string version)
    {
        if (!IsValidVersion(version))
        {
            throw new InvalidDataException("Product version is invalid.");
        }
    }

    private static bool IsValidVersion(string? version)
        => SemanticVersionPattern().IsMatch(version ?? string.Empty);

    private static void ValidatePackage(
        ProductUpdatePackage? package,
        IReadOnlySet<string> allowedPackageHosts)
    {
        if (package is null || package.SizeBytes is < 1 or > MaximumPackageBytes ||
            !Sha256Pattern().IsMatch(package.Sha256 ?? string.Empty) ||
            !Uri.TryCreate(package.Url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !allowedPackageHosts.Contains(uri.IdnHost))
        {
            throw new InvalidDataException("Update package metadata is invalid or untrusted.");
        }
    }

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-(?:[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();

    [GeneratedRegex("^[a-z][a-z0-9._-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
