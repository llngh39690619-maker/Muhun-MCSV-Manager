using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Providers;

public sealed record CurseForgeManifestInspectionLimits(
    int MaxEntries = 100_000,
    long MaxArchiveBytes = 2L * 1024 * 1024 * 1024,
    long MaxManifestBytes = 2L * 1024 * 1024,
    double MaxCompressionRatio = 1_000d)
{
    internal void Validate()
    {
        if (MaxEntries < 1
            || MaxArchiveBytes < 1
            || MaxManifestBytes < 1
            || MaxManifestBytes > int.MaxValue
            || !double.IsFinite(MaxCompressionRatio)
            || MaxCompressionRatio < 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CurseForgeManifestInspectionLimits),
                "CurseForge manifest 安全限制必須是有效的正數。");
        }
    }
}

public sealed record CurseForgeModpackManifestInfo(
    string Name,
    string PackVersion,
    string MinecraftVersion,
    ModrinthModpackLoaderInstallRequest LoaderInstallRequest);

/// <summary>
/// Reads only the bounded root manifest.json from a verified CurseForge client export. No archive
/// content is extracted and no script/JAR from the pack is loaded or executed.
/// </summary>
public sealed class CurseForgeModpackManifestInspector
{
    private const string ManifestName = "manifest.json";
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFileType = 0x8000;
    private const int UnixSymbolicLinkType = 0xA000;
    private const int DosReparsePointAttribute = (int)FileAttributes.ReparsePoint;

    public async Task<CurseForgeModpackManifestInfo> InspectAsync(
        string archivePath,
        CurseForgeManifestInspectionLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        limits ??= new CurseForgeManifestInspectionLimits();
        limits.Validate();

        var fullPath = Path.GetFullPath(archivePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("找不到 CurseForge client pack。", fullPath);
        }

        file.Refresh();
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("CurseForge client pack 不得是 reparse point。");
        }

        if (file.Length is < 1 || file.Length > limits.MaxArchiveBytes)
        {
            throw new InvalidDataException(
                $"CurseForge client pack 大小必須介於 1 byte 與 {limits.MaxArchiveBytes:N0} bytes。");
        }

        try
        {
            await using var input = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length is < 1 || input.Length > limits.MaxArchiveBytes)
            {
                throw new InvalidDataException("CurseForge client pack 在開啟後大小超出安全限制。");
            }

            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? manifest = null;
            var entryCount = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entryCount > limits.MaxEntries)
                {
                    throw new InvalidDataException(
                        $"CurseForge client pack 超過 {limits.MaxEntries:N0} 個 ZIP entries 的安全上限。");
                }

                var normalizedName = entry.FullName
                    .Replace('\\', '/')
                    .Normalize(NormalizationForm.FormC);
                if (!normalizedName.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (manifest is not null)
                {
                    throw new InvalidDataException(
                        "CurseForge client pack 含有重複或大小寫／Unicode 衝突的 root manifest.json。");
                }

                if (!entry.FullName.Equals(ManifestName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "CurseForge manifest 必須以精確名稱 manifest.json 位於 ZIP 根目錄。");
                }

                manifest = entry;
            }

            if (manifest is null)
            {
                throw new InvalidDataException(
                    "CurseForge client pack 的 ZIP 根目錄找不到 manifest.json。");
            }

            ValidateManifestEntry(manifest, limits);
            var bytes = await ReadEntryExactlyAsync(
                    manifest,
                    checked((int)limits.MaxManifestBytes),
                    cancellationToken)
                .ConfigureAwait(false);
            return ParseManifest(bytes);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("CurseForge client pack 使用不支援的 ZIP 功能。", exception);
        }
    }

    private static void ValidateManifestEntry(
        ZipArchiveEntry entry,
        CurseForgeManifestInspectionLimits limits)
    {
        RejectLinkOrSpecialEntry(entry);
        if (string.IsNullOrEmpty(entry.Name) || entry.Length < 1)
        {
            throw new InvalidDataException("CurseForge manifest.json 不得是資料夾或空檔案。");
        }

        if (entry.Length > limits.MaxManifestBytes)
        {
            throw new InvalidDataException(
                $"CurseForge manifest.json 超過 {limits.MaxManifestBytes:N0} bytes 的安全上限。");
        }

        if (entry.CompressedLength < 1
            || (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
        {
            throw new InvalidDataException("CurseForge manifest.json 的壓縮比例超過安全上限。");
        }
    }

    private static void RejectLinkOrSpecialEntry(ZipArchiveEntry entry)
    {
        var attributes = entry.ExternalAttributes;
        var dosAttributes = attributes & 0xFFFF;
        var upperAttributes = (attributes >> 16) & 0xFFFF;
        var unixType = upperAttributes & UnixFileTypeMask;
        if ((dosAttributes & DosReparsePointAttribute) != 0
            || (unixType == 0 && (upperAttributes & DosReparsePointAttribute) != 0)
            || unixType == UnixSymbolicLinkType
            || (unixType != 0 && unixType != UnixRegularFileType))
        {
            throw new InvalidDataException(
                "CurseForge manifest.json 不得是符號連結、reparse point 或特殊檔案。");
        }
    }

    private static async Task<byte[]> ReadEntryExactlyAsync(
        ZipArchiveEntry entry,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var declaredLength = checked((int)entry.Length);
        if (declaredLength > maximumBytes)
        {
            throw new InvalidDataException("CurseForge manifest.json 超過安全讀取上限。");
        }

        var result = new byte[declaredLength];
        await using var stream = entry.Open();
        var offset = 0;
        while (offset < result.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(result.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                throw new InvalidDataException("CurseForge manifest.json 比 ZIP 宣告的長度短。");
            }

            offset += count;
        }

        var extra = new byte[1];
        if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("CurseForge manifest.json 比 ZIP 宣告的長度長。");
        }

        return result;
    }

    private static CurseForgeModpackManifestInfo ParseManifest(byte[] bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("CurseForge manifest.json 不是有效且受限的 JSON。", exception);
        }

        using (document)
        {
            var root = RequireObject(document.RootElement, "manifest root");
            var manifestType = ReadRequiredString(root, "manifestType", "manifest root", 64);
            if (!manifestType.Equals("minecraftModpack", StringComparison.Ordinal))
            {
                throw new InvalidDataException("CurseForge manifestType 必須是 minecraftModpack。");
            }

            var manifestVersionElement = ReadRequiredProperty(root, "manifestVersion", "manifest root");
            if (manifestVersionElement.ValueKind != JsonValueKind.Number
                || !manifestVersionElement.TryGetInt32(out var manifestVersion)
                || manifestVersion != 1)
            {
                throw new InvalidDataException("CurseForge manifestVersion 必須是整數 1。");
            }

            var minecraft = RequireObject(
                ReadRequiredProperty(root, "minecraft", "manifest root"),
                "minecraft");
            var minecraftVersion = ReadRequiredString(minecraft, "version", "minecraft", 128);
            ValidateVersionToken(minecraftVersion, "Minecraft version");
            var loaders = ReadRequiredProperty(minecraft, "modLoaders", "minecraft");
            if (loaders.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("CurseForge minecraft.modLoaders 必須是陣列。");
            }

            if (loaders.GetArrayLength() > 16)
            {
                throw new InvalidDataException("CurseForge minecraft.modLoaders 超過 16 筆安全上限。");
            }

            var installRequest = ParseLoader(loaders, minecraftVersion);
            var name = ReadOptionalString(root, "name", "manifest root", 256) ?? "CurseForge Modpack";
            var packVersion = ReadOptionalString(root, "version", "manifest root", 256) ?? string.Empty;
            return new CurseForgeModpackManifestInfo(
                name,
                packVersion,
                minecraftVersion,
                installRequest);
        }
    }

    private static ModrinthModpackLoaderInstallRequest ParseLoader(
        JsonElement loaders,
        string minecraftVersion)
    {
        if (loaders.GetArrayLength() == 0)
        {
            return new ModrinthModpackLoaderInstallRequest(
                ModrinthModpackLoaderKind.Vanilla,
                minecraftVersion,
                null);
        }

        string? primaryId = null;
        var index = 0;
        foreach (var loaderElement in loaders.EnumerateArray())
        {
            var loader = RequireObject(loaderElement, $"minecraft.modLoaders[{index}]");
            var id = ReadRequiredString(loader, "id", $"minecraft.modLoaders[{index}]", 256);
            var primaryElement = ReadRequiredProperty(
                loader,
                "primary",
                $"minecraft.modLoaders[{index}]");
            if (primaryElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new InvalidDataException(
                    $"CurseForge minecraft.modLoaders[{index}].primary 必須是 boolean。");
            }

            if (primaryElement.GetBoolean())
            {
                if (primaryId is not null)
                {
                    throw new InvalidDataException("CurseForge manifest 含有多個 primary mod loader。");
                }

                primaryId = id;
            }

            index++;
        }

        if (primaryId is null)
        {
            throw new InvalidDataException("CurseForge manifest 沒有 primary mod loader。");
        }

        var (kind, version) = ParseLoaderId(primaryId);
        ValidateVersionToken(version, $"{kind} loader version");
        return new ModrinthModpackLoaderInstallRequest(kind, minecraftVersion, version);
    }

    private static (ModrinthModpackLoaderKind Kind, string Version) ParseLoaderId(string loaderId)
    {
        (string Prefix, ModrinthModpackLoaderKind Kind)[] supported =
        [
            ("neoforge-", ModrinthModpackLoaderKind.NeoForge),
            ("fabric-", ModrinthModpackLoaderKind.Fabric),
            ("forge-", ModrinthModpackLoaderKind.Forge),
            ("quilt-", ModrinthModpackLoaderKind.Quilt)
        ];
        foreach (var (prefix, kind) in supported)
        {
            if (loaderId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var version = loaderId[prefix.Length..];
                if (version.Length == 0)
                {
                    throw new InvalidDataException($"CurseForge {kind} loader 缺少版本。");
                }

                return (kind, version);
            }
        }

        throw new InvalidDataException($"CurseForge manifest 使用不支援的 mod loader：{loaderId}");
    }

    private static void ValidateVersionToken(string value, string context)
    {
        if (value.Length is < 1 or > 128
            || !IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !IsAsciiLetterOrDigit(character)
                                      && character is not '.' and not '_' and not '+' and not '-'))
        {
            throw new InvalidDataException($"CurseForge {context} 含有不安全的版本字元。");
        }
    }

    private static bool IsAsciiLetterOrDigit(char value)
        => value is >= '0' and <= '9'
           || value is >= 'A' and <= 'Z'
           || value is >= 'a' and <= 'z';

    private static JsonElement RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"CurseForge {context} 必須是 JSON object。");
        }

        return element;
    }

    private static JsonElement ReadRequiredProperty(
        JsonElement element,
        string propertyName,
        string context)
    {
        JsonElement? result = null;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!property.Name.Equals(propertyName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"CurseForge {context}.{propertyName} 的大小寫不符合格式。");
            }

            if (result is not null)
            {
                throw new InvalidDataException(
                    $"CurseForge {context} 含有重複的 {propertyName} 欄位。");
            }

            result = property.Value;
        }

        return result
            ?? throw new InvalidDataException($"CurseForge {context} 缺少 {propertyName}。");
    }

    private static string ReadRequiredString(
        JsonElement element,
        string propertyName,
        string context,
        int maximumLength)
    {
        var value = ReadRequiredProperty(element, propertyName, context);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"CurseForge {context}.{propertyName} 必須是字串。");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)
            || text.Length > maximumLength
            || !text.Equals(text.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"CurseForge {context}.{propertyName} 必須是 1 到 {maximumLength} 字元且不得有首尾空白。");
        }

        return text;
    }

    private static string? ReadOptionalString(
        JsonElement element,
        string propertyName,
        string context,
        int maximumLength)
    {
        JsonElement? result = null;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!property.Name.Equals(propertyName, StringComparison.Ordinal) || result is not null)
            {
                throw new InvalidDataException(
                    $"CurseForge {context}.{propertyName} 欄位大小寫錯誤或重複。");
            }

            result = property.Value;
        }

        if (result is null || result.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (result.Value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"CurseForge {context}.{propertyName} 必須是字串。");
        }

        var text = result.Value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength)
        {
            return null;
        }

        return text.Trim();
    }
}
