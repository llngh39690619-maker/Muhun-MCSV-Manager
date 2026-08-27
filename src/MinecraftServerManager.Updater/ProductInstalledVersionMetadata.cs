using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftServerManager.Updater;

public sealed record ProductInstalledVersionMetadata(
    int SchemaVersion,
    string ProductId,
    string Version,
    string EntryPoint);

public static class ProductInstalledVersionMetadataStore
{
    public const string FileName = "installed-version.v1.json";
    private const int MaximumBytes = 8 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static ProductInstalledVersionMetadata Read(string versionRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionRoot);
        var normalizedRoot = Path.GetFullPath(versionRoot);
        RejectReparse(normalizedRoot);
        var path = Path.Combine(normalizedRoot, FileName);
        RejectReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4_096);
        if (stream.Length is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException("Installed-version metadata has an invalid size.");
        }

        ProductInstalledVersionMetadata metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<ProductInstalledVersionMetadata>(stream, JsonOptions)
                ?? throw new InvalidDataException("Installed-version metadata is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Installed-version metadata JSON is invalid.", exception);
        }

        Validate(metadata);
        var entryPoint = ProductUpdatePath.ResolveUnderRoot(normalizedRoot, metadata.EntryPoint);
        if (!File.Exists(entryPoint))
        {
            throw new FileNotFoundException("Installed product entry point was not found.", entryPoint);
        }
        RejectReparse(entryPoint);

        return metadata;
    }

    public static void Write(string versionRoot, ProductInstalledVersionMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionRoot);
        ArgumentNullException.ThrowIfNull(metadata);
        Validate(metadata);
        var normalizedRoot = Path.GetFullPath(versionRoot);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException("Installed version root does not exist.");
        }
        RejectReparse(normalizedRoot);

        var entryPoint = ProductUpdatePath.ResolveUnderRoot(normalizedRoot, metadata.EntryPoint);
        if (!File.Exists(entryPoint))
        {
            throw new FileNotFoundException("Installed product entry point was not found.", entryPoint);
        }
        RejectReparse(entryPoint);

        var path = Path.Combine(normalizedRoot, FileName);
        var temporaryPath = Path.Combine(
            normalizedRoot,
            $".{FileName}.{Guid.NewGuid():N}.tmp");
        var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(metadata, JsonOptions) + Environment.NewLine);
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4_096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Validate(ProductInstalledVersionMetadata metadata)
    {
        if (metadata.SchemaVersion != 1 ||
            !string.Equals(metadata.ProductId, "muhun.mcsv.manager", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Installed-version metadata is invalid or unsupported.");
        }

        ProductUpdateManifestParser.ValidateVersion(metadata.Version);
        ProductUpdatePath.ValidateRelativeFilePath(metadata.EntryPoint);
        if (!metadata.EntryPoint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Installed-version entry point is invalid.");
        }
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Installed-version metadata paths cannot be reparse points.");
        }
    }
}
