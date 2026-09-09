using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Providers;

public sealed record CurseForgeManifestInspectionLimits(
    int MaxEntries = 100_000,
    long MaxArchiveBytes = 2L * 1024 * 1024 * 1024,
    long MaxManifestBytes = 2L * 1024 * 1024,
    double MaxCompressionRatio = 1_000d,
    int MaxManifestFiles = 100_000,
    long MaxEntryUncompressedBytes = 4L * 1024 * 1024 * 1024,
    long MaxArchiveUncompressedBytes = 16L * 1024 * 1024 * 1024)
{
    internal void Validate()
    {
        if (MaxEntries < 1
            || MaxArchiveBytes < 1
            || MaxManifestBytes < 1
            || MaxManifestBytes > int.MaxValue
            || MaxManifestFiles < 1
            || MaxEntryUncompressedBytes < 1
            || MaxArchiveUncompressedBytes < 1
            || !double.IsFinite(MaxCompressionRatio)
            || MaxCompressionRatio < 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CurseForgeManifestInspectionLimits),
                "CurseForge manifest 安全限制必須是有效的正數。");
        }
    }
}

public sealed record CurseForgeModpackManifestFile(
    int ProjectId,
    int FileId,
    bool IsRequired);

public sealed record CurseForgeModpackOverrideEntry(
    int ArchiveEntryIndex,
    string ArchivePath,
    string RelativePath,
    long Length);

public sealed record CurseForgeModpackManifestInfo(
    string Name,
    string PackVersion,
    string MinecraftVersion,
    ModrinthModpackLoaderInstallRequest LoaderInstallRequest,
    IReadOnlyList<CurseForgeModpackManifestFile> Files,
    string OverridesDirectory,
    IReadOnlyList<CurseForgeModpackOverrideEntry> OverrideEntries);

/// <summary>
/// Validates the bounded root manifest and override layer of a verified CurseForge client export.
/// Inspection never extracts or executes archive content; callers may apply the already validated
/// override layer with <see cref="ExtractOverridesAsync"/>.
/// </summary>
public sealed class CurseForgeModpackManifestInspector
{
    private const string ManifestName = "manifest.json";
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFileType = 0x8000;
    private const int UnixDirectoryFileType = 0x4000;
    private const int UnixSymbolicLinkType = 0xA000;
    private const int DosReparsePointAttribute = (int)FileAttributes.ReparsePoint;
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
    private static readonly HashSet<string> ProtectedOverrideTopLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "versions",
        "libraries",
        "assets",
        "runtime",
        "runtimes",
        "jre",
        "natives",
        "launcher",
        ".x-mcsv-content",
        ".x-mcsv",
        "installation.id",
        "launcher_accounts.json",
        "launcher_profiles.json",
    };

    public async Task<CurseForgeModpackManifestInfo> InspectAsync(
        string archivePath,
        CurseForgeManifestInspectionLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        limits ??= new CurseForgeManifestInspectionLimits();
        limits.Validate();
        var fullPath = ValidateArchiveFile(archivePath, limits);

        try
        {
            await using var input = OpenArchiveFile(fullPath, limits);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            return await InspectOpenArchiveAsync(archive, limits, cancellationToken)
                .ConfigureAwait(false);
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

    /// <summary>
    /// Inspects a caller-owned seekable stream without extracting content or closing the stream.
    /// This validates ZIP/manifest structure only; a remote preview stream has not verified the
    /// complete archive hash and must not be treated as a verified installation package.
    /// </summary>
    public async Task<CurseForgeModpackManifestInfo> InspectAsync(
        Stream archiveStream,
        CurseForgeManifestInspectionLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        limits ??= new CurseForgeManifestInspectionLimits();
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!archiveStream.CanRead || !archiveStream.CanSeek
            || archiveStream.Length < 1 || archiveStream.Length > limits.MaxArchiveBytes)
        {
            throw new InvalidDataException("CurseForge ZIP preview requires a bounded readable, seekable archive.");
        }

        try
        {
            archiveStream.Position = 0;
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            return await InspectOpenArchiveAsync(archive, limits, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("CurseForge client pack 使用不支援的 ZIP 功能。", exception);
        }
    }

    /// <summary>
    /// Revalidates the archive and applies only its manifest-declared <c>overrides</c> files to an
    /// existing staging directory. The archive remains open from validation through extraction so
    /// a path swap cannot substitute unvalidated ZIP content between those phases.
    /// </summary>
    public async Task ExtractOverridesAsync(
        string archivePath,
        string stagingDirectory,
        CurseForgeManifestInspectionLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        limits ??= new CurseForgeManifestInspectionLimits();
        limits.Validate();

        var fullPath = ValidateArchiveFile(archivePath, limits);
        var stagingRoot = EnsureSafeStagingRoot(stagingDirectory);
        try
        {
            await using var input = OpenArchiveFile(fullPath, limits);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            var inspection = await InspectOpenArchiveAsync(archive, limits, cancellationToken)
                .ConfigureAwait(false);
            var destinations = PreflightOverrideDestinations(stagingRoot, inspection.OverrideEntries);

            for (var index = 0; index < inspection.OverrideEntries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var planned = inspection.OverrideEntries[index];
                var destination = destinations[index];
                var entry = archive.Entries[planned.ArchiveEntryIndex];
                await ExtractOverrideEntryAsync(
                        entry,
                        planned,
                        stagingRoot,
                        destination,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
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

    private static string ValidateArchiveFile(
        string archivePath,
        CurseForgeManifestInspectionLimits limits)
    {
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

        return fullPath;
    }

    private static FileStream OpenArchiveFile(
        string fullPath,
        CurseForgeManifestInspectionLimits limits)
    {
        var input = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length is < 1 || input.Length > limits.MaxArchiveBytes)
        {
            input.Dispose();
            throw new InvalidDataException("CurseForge client pack 在開啟後大小超出安全限制。");
        }

        return input;
    }

    private static async Task<CurseForgeModpackManifestInfo> InspectOpenArchiveAsync(
        ZipArchive archive,
        CurseForgeManifestInspectionLimits limits,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry? manifest = null;
        var validatedEntries = new List<ValidatedArchiveEntry>(archive.Entries.Count);
        var archivePaths = new ArchivePathRegistry();
        var entryCount = 0;
        long totalUncompressedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entryCount > limits.MaxEntries)
            {
                throw new InvalidDataException(
                    $"CurseForge client pack 超過 {limits.MaxEntries:N0} 個 ZIP entries 的安全上限。");
            }

            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            var normalizedName = NormalizeArchivePath(entry.FullName, isDirectory);
            RejectLinkOrSpecialEntry(entry, isDirectory);
            archivePaths.Add(normalizedName, isDirectory, entry.FullName);
            totalUncompressedBytes = CheckedAdd(
                totalUncompressedBytes,
                entry.Length,
                "CurseForge client pack 的解壓縮總大小溢位。");
            if (entry.Length > limits.MaxEntryUncompressedBytes
                || totalUncompressedBytes > limits.MaxArchiveUncompressedBytes)
            {
                throw new InvalidDataException(
                    "CurseForge client pack 超過允許的解壓縮大小安全上限。");
            }

            if (isDirectory)
            {
                if (entry.Length != 0)
                {
                    throw new InvalidDataException(
                        $"CurseForge client pack 的資料夾 entry 宣告了檔案內容：{entry.FullName}");
                }
            }
            else if (entry.Length > 0
                     && (entry.CompressedLength < 1
                         || (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio))
            {
                throw new InvalidDataException(
                    $"CurseForge client pack 項目的壓縮比例超過安全上限：{entry.FullName}");
            }

            validatedEntries.Add(new ValidatedArchiveEntry(
                entryCount - 1,
                entry.FullName,
                normalizedName,
                isDirectory,
                entry.Length,
                entry.CompressedLength));
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
        var result = ParseManifest(bytes, limits);
        var overrideEntries = CollectOverrides(validatedEntries, result.OverridesDirectory);
        return result with { OverrideEntries = overrideEntries };
    }

    private static void ValidateManifestEntry(
        ZipArchiveEntry entry,
        CurseForgeManifestInspectionLimits limits)
    {
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

    private static void RejectLinkOrSpecialEntry(ZipArchiveEntry entry, bool isDirectory)
    {
        var attributes = entry.ExternalAttributes;
        var dosAttributes = attributes & 0xFFFF;
        var upperAttributes = (attributes >> 16) & 0xFFFF;
        var unixType = upperAttributes & UnixFileTypeMask;
        if ((dosAttributes & DosReparsePointAttribute) != 0
            || (unixType == 0 && (upperAttributes & DosReparsePointAttribute) != 0)
            || unixType == UnixSymbolicLinkType
            || (unixType != 0
                && unixType != (isDirectory ? UnixDirectoryFileType : UnixRegularFileType)))
        {
            throw new InvalidDataException(
                $"CurseForge client pack 不得包含符號連結、reparse point 或特殊檔案：{entry.FullName}");
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

    private static CurseForgeModpackManifestInfo ParseManifest(
        byte[] bytes,
        CurseForgeManifestInspectionLimits limits)
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
            RejectDuplicateJsonProperties(document.RootElement);
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
            var files = ParseManifestFiles(root, limits);
            var overridesDirectory = NormalizeArchivePath(
                ReadRequiredString(root, "overrides", "manifest root", 1_024),
                isDirectory: true);
            var name = ReadOptionalString(root, "name", "manifest root", 256) ?? "CurseForge Modpack";
            var packVersion = ReadOptionalString(root, "version", "manifest root", 256) ?? string.Empty;
            return new CurseForgeModpackManifestInfo(
                name,
                packVersion,
                minecraftVersion,
                installRequest,
                files,
                overridesDirectory,
                []);
        }
    }

    private static IReadOnlyList<CurseForgeModpackManifestFile> ParseManifestFiles(
        JsonElement root,
        CurseForgeManifestInspectionLimits limits)
    {
        var filesElement = ReadRequiredProperty(root, "files", "manifest root");
        if (filesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("CurseForge manifest files 必須是陣列。");
        }

        if (filesElement.GetArrayLength() > limits.MaxManifestFiles)
        {
            throw new InvalidDataException(
                $"CurseForge manifest files 超過 {limits.MaxManifestFiles:N0} 筆安全上限。");
        }

        var files = new List<CurseForgeModpackManifestFile>(filesElement.GetArrayLength());
        var identities = new HashSet<(int ProjectId, int FileId)>();
        var index = 0;
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            var context = $"files[{index}]";
            var file = RequireObject(fileElement, context);
            var projectId = ReadRequiredPositiveInt32(file, "projectID", context);
            var fileId = ReadRequiredPositiveInt32(file, "fileID", context);
            var required = ReadRequiredBoolean(file, "required", context);
            if (!identities.Add((projectId, fileId)))
            {
                throw new InvalidDataException(
                    $"CurseForge manifest files 含有重複的 projectID/fileID：{projectId}/{fileId}");
            }

            files.Add(new CurseForgeModpackManifestFile(projectId, fileId, required));
            index++;
        }

        return files;
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

    private static IReadOnlyList<CurseForgeModpackOverrideEntry> CollectOverrides(
        IReadOnlyList<ValidatedArchiveEntry> archiveEntries,
        string overridesDirectory)
    {
        var result = new List<CurseForgeModpackOverrideEntry>();
        var targetPaths = new ArchivePathRegistry();
        var prefix = overridesDirectory + "/";
        foreach (var entry in archiveEntries)
        {
            var isOverrideRoot = entry.NormalizedPath.Equals(
                overridesDirectory,
                StringComparison.OrdinalIgnoreCase);
            var isOverrideDescendant = entry.NormalizedPath.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase);
            if (!isOverrideRoot && !isOverrideDescendant)
            {
                continue;
            }

            if (isOverrideRoot)
            {
                if (!entry.NormalizedPath.Equals(overridesDirectory, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "CurseForge overrides 根目錄含大小寫衝突。");
                }

                if (!entry.IsDirectory)
                {
                    throw new InvalidDataException(
                        "CurseForge manifest 指定的 overrides 路徑必須是資料夾。");
                }

                continue;
            }

            if (!entry.NormalizedPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"CurseForge overrides 路徑含大小寫衝突：{entry.ArchivePath}");
            }

            var relativePath = entry.NormalizedPath[prefix.Length..];
            ValidateProtectedOverridePath(relativePath);
            targetPaths.Add(relativePath, entry.IsDirectory, entry.ArchivePath);
            if (!entry.IsDirectory)
            {
                result.Add(new CurseForgeModpackOverrideEntry(
                    entry.ArchiveEntryIndex,
                    entry.ArchivePath,
                    relativePath,
                    entry.Length));
            }
        }

        return result;
    }

    private static string NormalizeArchivePath(string path, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > 1_024
            || path.Contains('\\')
            || path.Contains(':')
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            throw new InvalidDataException($"CurseForge client pack 含不安全路徑：{path}");
        }

        var candidate = path.EndsWith("/", StringComparison.Ordinal)
            ? isDirectory
                ? path[..^1]
                : throw new InvalidDataException($"CurseForge client pack 檔案路徑不得以 / 結尾：{path}")
            : path;
        if (candidate.Length == 0)
        {
            throw new InvalidDataException("CurseForge client pack 不得含空白根目錄 entry。");
        }

        var parts = candidate.Split('/');
        if (parts.Any(static part => part.Length == 0 || part is "." or ".."))
        {
            throw new InvalidDataException($"CurseForge client pack 含路徑穿越或空白片段：{path}");
        }

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index].Normalize(NormalizationForm.FormC);
            if (part.Length > 255
                || part.EndsWith(' ')
                || part.EndsWith('.')
                || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            {
                throw new InvalidDataException($"CurseForge client pack 含 Windows 不支援的路徑：{path}");
            }

            var baseName = part.Split('.')[0].TrimEnd(' ', '.');
            if (ReservedWindowsNames.Contains(baseName))
            {
                throw new InvalidDataException($"CurseForge client pack 含 Windows 保留名稱：{path}");
            }

            parts[index] = part;
        }

        return string.Join('/', parts);
    }

    private static void ValidateProtectedOverridePath(string relativePath)
    {
        var separator = relativePath.IndexOf('/');
        var topLevel = separator < 0 ? relativePath : relativePath[..separator];
        if (ProtectedOverrideTopLevels.Contains(topLevel))
        {
            throw new InvalidDataException(
                $"CurseForge overrides 不得覆寫受保護的 client 頂層：{topLevel}");
        }
    }

    private static string EnsureSafeStagingRoot(string stagingDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"CurseForge overrides staging 資料夾不存在：{root}");
        }

        RejectExistingReparsePoint(root);
        return root;
    }

    private static IReadOnlyList<string> PreflightOverrideDestinations(
        string stagingRoot,
        IReadOnlyList<CurseForgeModpackOverrideEntry> entries)
    {
        var result = new List<string>(entries.Count);
        var paths = new ArchivePathRegistry();
        foreach (var entry in entries)
        {
            var relativePath = NormalizeArchivePath(entry.RelativePath, isDirectory: false);
            ValidateProtectedOverridePath(relativePath);
            paths.Add(relativePath, isDirectory: false, entry.ArchivePath);
            var destination = ResolveStagingDestination(stagingRoot, relativePath);
            ValidateExistingDestinationChain(stagingRoot, relativePath, destination);
            result.Add(destination);
        }

        return result;
    }

    private static string ResolveStagingDestination(string stagingRoot, string relativePath)
    {
        var destination = Path.GetFullPath(Path.Combine(
            stagingRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.EndsInDirectorySeparator(stagingRoot)
            ? stagingRoot
            : stagingRoot + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"CurseForge override 路徑離開 staging：{relativePath}");
        }

        return destination;
    }

    private static void ValidateExistingDestinationChain(
        string stagingRoot,
        string relativePath,
        string destination)
    {
        RejectExistingReparsePoint(stagingRoot);
        var parts = relativePath.Split('/');
        var current = stagingRoot;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            current = Path.Combine(current, parts[index]);
            RejectExistingReparsePoint(current);
            if (File.Exists(current))
            {
                throw new InvalidDataException(
                    $"CurseForge override 目的地父路徑是檔案：{relativePath}");
            }
        }

        RejectExistingReparsePoint(destination);
        if (Directory.Exists(destination))
        {
            throw new InvalidDataException(
                $"CurseForge override 檔案目的地已是資料夾：{relativePath}");
        }
    }

    private static void CreateSafeDestinationParents(string stagingRoot, string relativePath)
    {
        RejectExistingReparsePoint(stagingRoot);
        var parts = relativePath.Split('/');
        var current = stagingRoot;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            current = Path.Combine(current, parts[index]);
            RejectExistingReparsePoint(current);
            if (File.Exists(current))
            {
                throw new InvalidDataException(
                    $"CurseForge override 目的地父路徑是檔案：{relativePath}");
            }

            Directory.CreateDirectory(current);
            RejectExistingReparsePoint(current);
        }
    }

    private static async Task ExtractOverrideEntryAsync(
        ZipArchiveEntry entry,
        CurseForgeModpackOverrideEntry planned,
        string stagingRoot,
        string destination,
        CancellationToken cancellationToken)
    {
        var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
        RejectLinkOrSpecialEntry(entry, isDirectory);
        if (isDirectory
            || !entry.FullName.Equals(planned.ArchivePath, StringComparison.Ordinal)
            || entry.Length != planned.Length)
        {
            throw new InvalidDataException(
                "CurseForge client pack 在驗證後發生變更，拒絕套用 overrides。");
        }

        CreateSafeDestinationParents(stagingRoot, planned.RelativePath);
        ValidateExistingDestinationChain(stagingRoot, planned.RelativePath, destination);
        var parent = Path.GetDirectoryName(destination)
                     ?? throw new InvalidDataException("CurseForge override 目的地缺少父路徑。");
        var temporary = Path.Combine(
            parent,
            $".x-mcsv-override-{Guid.NewGuid():N}.tmp");
        try
        {
            long total = 0;
            await using (var input = entry.Open())
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total = CheckedAdd(total, read, "CurseForge override 解壓縮大小溢位。");
                    if (total > planned.Length)
                    {
                        throw new InvalidDataException(
                            "CurseForge override 實際內容超過 ZIP 宣告大小。");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }

                if (total != planned.Length)
                {
                    throw new InvalidDataException(
                        "CurseForge override 實際內容與 ZIP 宣告大小不符。");
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            ValidateExistingDestinationChain(stagingRoot, planned.RelativePath, destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static void RejectExistingReparsePoint(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"CurseForge override 路徑不得包含符號連結或 reparse point：{path}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup must not mask the validation or extraction failure.
        }
    }

    private static long CheckedAdd(long left, long right, string message)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(message, exception);
        }
    }

    private static void RejectDuplicateJsonProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"CurseForge manifest 含重複 JSON 欄位：{property.Name}");
                }

                RejectDuplicateJsonProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateJsonProperties(item);
            }
        }
    }

    private static int ReadRequiredPositiveInt32(
        JsonElement element,
        string propertyName,
        string context)
    {
        var value = ReadRequiredProperty(element, propertyName, context);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result)
            || result < 1)
        {
            throw new InvalidDataException(
                $"CurseForge {context}.{propertyName} 必須是正整數。");
        }

        return result;
    }

    private static bool ReadRequiredBoolean(
        JsonElement element,
        string propertyName,
        string context)
    {
        var value = ReadRequiredProperty(element, propertyName, context);
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException(
                $"CurseForge {context}.{propertyName} 必須是 boolean。");
        }

        return value.GetBoolean();
    }

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

    private sealed record ValidatedArchiveEntry(
        int ArchiveEntryIndex,
        string ArchivePath,
        string NormalizedPath,
        bool IsDirectory,
        long Length,
        long CompressedLength);

    private sealed class ArchivePathRegistry
    {
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _explicitDirectories = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string path, bool isDirectory, string source)
        {
            if (isDirectory)
            {
                if (_files.Contains(path))
                {
                    throw new InvalidDataException(
                        $"CurseForge client pack 含檔案／資料夾衝突：{source}");
                }

                if (!_explicitDirectories.Add(path))
                {
                    throw new InvalidDataException(
                        $"CurseForge client pack 含大小寫或 Unicode 正規化後重複的資料夾：{source}");
                }
            }
            else
            {
                if (_directories.Contains(path))
                {
                    throw new InvalidDataException(
                        $"CurseForge client pack 含檔案／資料夾衝突：{source}");
                }

                if (!_files.Add(path))
                {
                    throw new InvalidDataException(
                        $"CurseForge client pack 含大小寫或 Unicode 正規化後重複的檔案：{source}");
                }
            }

            var separator = path.IndexOf('/');
            while (separator >= 0)
            {
                var parent = path[..separator];
                if (_files.Contains(parent))
                {
                    throw new InvalidDataException(
                        $"CurseForge client pack 含檔案／資料夾衝突：{source}");
                }

                _directories.Add(parent);
                separator = path.IndexOf('/', separator + 1);
            }

            if (isDirectory)
            {
                _directories.Add(path);
            }
        }
    }
}
