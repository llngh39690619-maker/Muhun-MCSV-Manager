using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Services;

public enum ModrinthModpackLoaderKind
{
    Vanilla,
    Fabric,
    Forge,
    NeoForge,
    Quilt
}

public sealed record ModrinthModpackLoaderInstallRequest(
    ModrinthModpackLoaderKind Kind,
    string MinecraftVersion,
    string? LoaderVersion);

public sealed record SafeModpackArchiveLimits(
    int MaxEntries = 100_000,
    long MaxArchiveBytes = 8L * 1024 * 1024 * 1024,
    long MaxManifestBytes = 8L * 1024 * 1024,
    long MaxEntryUncompressedBytes = 4L * 1024 * 1024 * 1024,
    long MaxArchiveUncompressedBytes = 16L * 1024 * 1024 * 1024,
    long MaxRemoteContentBytes = 64L * 1024 * 1024 * 1024,
    double MaxCompressionRatio = 1_000d)
{
    internal void Validate()
    {
        if (MaxEntries < 1 || MaxArchiveBytes < 1 || MaxManifestBytes < 1 || MaxEntryUncompressedBytes < 1
            || MaxArchiveUncompressedBytes < 1 || MaxRemoteContentBytes < 1
            || !double.IsFinite(MaxCompressionRatio) || MaxCompressionRatio < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SafeModpackArchiveLimits), "模組包安全限制必須是有效的正數。");
        }
    }
}

public sealed record SafeModpackContentFile(
    string Path,
    IReadOnlyList<Uri> Downloads,
    long FileSize,
    string Sha512,
    string Sha1,
    bool IsOptional);

public sealed record SafeModpackOptionalFile(string Path, long FileSize);

public sealed record SafeModpackOverrideEntry(
    int ArchiveEntryIndex,
    string ArchivePath,
    string RelativePath,
    long Length);

public sealed record SafeModpackArchivePlan(
    string Name,
    string VersionId,
    string MinecraftVersion,
    ModrinthModpackLoaderInstallRequest LoaderInstallRequest,
    IReadOnlyList<SafeModpackContentFile> Files,
    IReadOnlyList<SafeModpackOptionalFile> OptionalFiles,
    int SkippedUnsupportedFiles,
    IReadOnlyList<SafeModpackOverrideEntry> Overrides,
    IReadOnlyList<SafeModpackOverrideEntry> ServerOverrides);

/// <summary>
/// Performs a non-extracting validation pass over a Modrinth format-1 archive, then exposes only
/// prevalidated server-relevant entries. Client overrides are deliberately never returned.
/// </summary>
public static class SafeModpackArchive
{
    private const string ManifestName = "modrinth.index.json";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static async Task<SafeModpackArchivePlan> InspectAsync(
        string archivePath,
        IModrinthModpackUriPolicy? uriPolicy = null,
        SafeModpackArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        limits ??= new SafeModpackArchiveLimits();
        limits.Validate();
        uriPolicy ??= new OfficialModrinthModpackUriPolicy();

        var fullArchivePath = Path.GetFullPath(archivePath);
        RejectExistingReparse(fullArchivePath);
        await using var stream = new FileStream(
            fullArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > limits.MaxArchiveBytes) throw new InvalidDataException("模組包壓縮檔超過安全大小上限。");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > limits.MaxEntries)
        {
            throw new InvalidDataException($"模組包項目數超過安全上限 {limits.MaxEntries}。");
        }

        ZipArchiveEntry? manifestEntry = null;
        var archiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressed = 0;
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.Entries[index];
            RejectLink(entry);
            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            var normalized = NormalizeRelativePath(entry.FullName, allowTrailingSlash: isDirectory);
            var collisionKey = isDirectory ? normalized + "/" : normalized;
            if (!archiveNames.Add(collisionKey))
            {
                throw new InvalidDataException($"模組包含大小寫或 Unicode 正規化後重複的項目：{entry.FullName}");
            }

            totalUncompressed = CheckedAdd(totalUncompressed, entry.Length, "模組包解壓縮總大小溢位。");
            if (entry.Length > limits.MaxEntryUncompressedBytes
                || totalUncompressed > limits.MaxArchiveUncompressedBytes)
            {
                throw new InvalidDataException("模組包超過允許的解壓縮大小上限。");
            }

            if (!isDirectory && entry.Length > 0)
            {
                if (entry.CompressedLength <= 0
                    || (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                {
                    throw new InvalidDataException($"模組包項目的壓縮比超過安全上限：{entry.FullName}");
                }
            }

            if (normalized.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
            {
                if (isDirectory || !entry.FullName.Equals(ManifestName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("modrinth.index.json 必須是根目錄中名稱完全相符的單一檔案。");
                }

                if (manifestEntry is not null) throw new InvalidDataException("模組包含重複 manifest。");
                manifestEntry = entry;
            }
        }

        if (manifestEntry is null) throw new InvalidDataException("模組包缺少 modrinth.index.json。");
        if (manifestEntry.Length > limits.MaxManifestBytes) throw new InvalidDataException("Modrinth manifest 過大。");

        byte[] manifestBytes;
        await using (var input = manifestEntry.Open())
        {
            manifestBytes = new byte[checked((int)manifestEntry.Length)];
            await input.ReadExactlyAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            if (input.ReadByte() != -1) throw new InvalidDataException("Manifest 實際大小與 ZIP 宣告不符。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                StrictUtf8.GetString(manifestBytes),
                new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new InvalidDataException("Modrinth manifest 不是有效的嚴格 UTF-8 JSON。", exception);
        }

        using (document)
        {
            RejectDuplicateJsonProperties(document.RootElement);
            return ParseManifest(document.RootElement, archive, uriPolicy, limits);
        }
    }

    public static async Task ExtractOverridesAsync(
        string archivePath,
        string stagingDirectory,
        IReadOnlyList<SafeModpackOverrideEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(entries);
        var root = EnsureSafeStagingDirectory(stagingDirectory, requireEmpty: false);

        await using var stream = new FileStream(
            Path.GetFullPath(archivePath), FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var planned in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (planned.ArchiveEntryIndex < 0 || planned.ArchiveEntryIndex >= archive.Entries.Count)
            {
                throw new InvalidDataException("預先驗證的 ZIP 項目索引已失效。");
            }

            var entry = archive.Entries[planned.ArchiveEntryIndex];
            RejectLink(entry);
            if (!entry.FullName.Equals(planned.ArchivePath, StringComparison.Ordinal)
                || entry.Length != planned.Length)
            {
                throw new InvalidDataException("模組包在驗證後發生變更。");
            }

            var destination = ResolveDestination(root, planned.RelativePath);
            var parent = Path.GetDirectoryName(destination)!;
            CreateSafeParents(root, parent);
            RejectExistingReparse(destination);
            if (Directory.Exists(destination)) throw new IOException($"Override 目的地是資料夾：{planned.RelativePath}");

            var temporary = destination + ".override-" + Guid.NewGuid().ToString("N");
            try
            {
                await using var input = entry.Open();
                await using var output = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[128 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    total = CheckedAdd(total, read, "Override 解壓縮大小溢位。");
                    if (total > planned.Length) throw new InvalidDataException("Override 超過 ZIP 宣告大小。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (total != planned.Length) throw new InvalidDataException("Override 大小與 ZIP 宣告不符。");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Close();
                RejectExistingReparse(destination);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
    }

    public static string EnsureSafeStagingDirectory(string stagingDirectory, bool requireEmpty)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Staging 資料夾不存在：{root}");
        RejectExistingReparse(root);
        if (requireEmpty && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new InvalidOperationException("安裝只允許寫入 caller 建立的全新空白 staging 資料夾。");
        }

        return root;
    }

    public static string ResolveDestination(string stagingDirectory, string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
        var normalized = NormalizeRelativePath(relativePath, allowTrailingSlash: false);
        var destination = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"路徑離開 staging：{relativePath}");
        }

        return destination;
    }

    internal static string PrepareDestination(string stagingDirectory, string relativePath)
    {
        var destination = ResolveDestination(stagingDirectory, relativePath);
        CreateSafeParents(Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory)), Path.GetDirectoryName(destination)!);
        RejectExistingReparse(destination);
        if (Directory.Exists(destination)) throw new IOException($"檔案目的地已是資料夾：{relativePath}");
        return destination;
    }

    private static SafeModpackArchivePlan ParseManifest(
        JsonElement root,
        ZipArchive archive,
        IModrinthModpackUriPolicy uriPolicy,
        SafeModpackArchiveLimits limits)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Modrinth manifest 根節點必須是物件。");
        if (!root.TryGetProperty("formatVersion", out var format) || !format.TryGetInt32(out var formatVersion) || formatVersion != 1)
        {
            throw new InvalidDataException("僅支援 Modrinth pack formatVersion 1。");
        }

        if (!RequiredString(root, "game").Equals("minecraft", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Modrinth 模組包 game 必須是 minecraft。");
        }

        var name = RequiredString(root, "name");
        var versionId = RequiredString(root, "versionId");
        var loader = ParseDependencies(root);
        var files = new List<SafeModpackContentFile>();
        var optional = new List<SafeModpackOptionalFile>();
        var unsupported = 0;
        var remotePaths = new PathRegistry();
        long remoteTotal = 0;
        if (!root.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Modrinth manifest files 必須是陣列。");
        }

        foreach (var item in filesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Manifest file 必須是物件。");
            var path = NormalizeRelativePath(RequiredString(item, "path"), allowTrailingSlash: false);
            remotePaths.AddFile(path, "manifest files");
            if (!item.TryGetProperty("fileSize", out var sizeElement)
                || !sizeElement.TryGetInt64(out var size) || size < 0)
            {
                throw new InvalidDataException($"Manifest fileSize 無效：{path}");
            }

            remoteTotal = CheckedAdd(remoteTotal, size, "遠端檔案總大小溢位。");
            if (remoteTotal > limits.MaxRemoteContentBytes) throw new InvalidDataException("遠端檔案總大小超過安全上限。");
            if (!item.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Manifest file 缺少 hashes：{path}");
            }

            var sha512 = RequiredHex(hashes, "sha512", 128, path);
            var sha1 = RequiredHex(hashes, "sha1", 40, path);
            if (!item.TryGetProperty("downloads", out var downloadsElement)
                || downloadsElement.ValueKind != JsonValueKind.Array || downloadsElement.GetArrayLength() == 0)
            {
                throw new InvalidDataException($"Manifest file 缺少 downloads：{path}");
            }

            var downloads = new List<Uri>();
            foreach (var value in downloadsElement.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String
                    || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri))
                {
                    throw new InvalidDataException($"Manifest file 下載網址無效：{path}");
                }

                uriPolicy.EnsureAllowed(uri, isRedirect: false);
                downloads.Add(uri);
            }

            var server = ReadServerEnvironment(item, path);
            if (server == "unsupported")
            {
                unsupported++;
                continue;
            }

            var isOptional = server == "optional";
            files.Add(new SafeModpackContentFile(path, downloads, size, sha512, sha1, isOptional));
            if (isOptional) optional.Add(new SafeModpackOptionalFile(path, size));
        }

        var overrides = CollectLayer(archive, "overrides/");
        var serverOverrides = CollectLayer(archive, "server-overrides/");
        return new SafeModpackArchivePlan(
            name, versionId, loader.MinecraftVersion, loader, files, optional, unsupported, overrides, serverOverrides);
    }

    private static ModrinthModpackLoaderInstallRequest ParseDependencies(JsonElement root)
    {
        if (!root.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Modrinth manifest dependencies 必須是物件。");
        }

        string? minecraft = null;
        string? loaderVersion = null;
        ModrinthModpackLoaderKind loaderKind = ModrinthModpackLoaderKind.Vanilla;
        var loaderCount = 0;
        foreach (var property in dependencies.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                throw new InvalidDataException($"Dependency {property.Name} 版本無效。");
            }

            var value = property.Value.GetString()!.Trim();
            switch (property.Name)
            {
                case "minecraft": minecraft = value; break;
                case "fabric-loader": loaderKind = ModrinthModpackLoaderKind.Fabric; loaderVersion = value; loaderCount++; break;
                case "forge": loaderKind = ModrinthModpackLoaderKind.Forge; loaderVersion = value; loaderCount++; break;
                case "neoforge": loaderKind = ModrinthModpackLoaderKind.NeoForge; loaderVersion = value; loaderCount++; break;
                case "quilt-loader": loaderKind = ModrinthModpackLoaderKind.Quilt; loaderVersion = value; loaderCount++; break;
                default: throw new InvalidDataException($"不支援的 Modrinth dependency：{property.Name}");
            }
        }

        if (string.IsNullOrWhiteSpace(minecraft)) throw new InvalidDataException("Dependencies 缺少 minecraft。");
        if (loaderCount > 1) throw new InvalidDataException("一個模組包不可同時指定多個 loader。");
        return new ModrinthModpackLoaderInstallRequest(loaderKind, minecraft, loaderVersion);
    }

    private static string ReadServerEnvironment(JsonElement item, string path)
    {
        if (!item.TryGetProperty("env", out var env)) return "required";
        if (env.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Manifest env 必須是物件：{path}");
        foreach (var property in env.EnumerateObject())
        {
            if (property.Name is not ("client" or "server")
                || property.Value.ValueKind != JsonValueKind.String
                || property.Value.GetString() is not ("required" or "optional" or "unsupported"))
            {
                throw new InvalidDataException($"Manifest env 值無效：{path}");
            }
        }

        return env.TryGetProperty("server", out var server) ? server.GetString()! : "required";
    }

    private static IReadOnlyList<SafeModpackOverrideEntry> CollectLayer(ZipArchive archive, string prefix)
    {
        var result = new List<SafeModpackOverrideEntry>();
        var paths = new PathRegistry();
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            var entry = archive.Entries[index];
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = NormalizeRelativePath(entry.FullName[prefix.Length..], allowTrailingSlash: false);
            paths.AddFile(relative, prefix.TrimEnd('/'));
            result.Add(new SafeModpackOverrideEntry(index, entry.FullName, relative, entry.Length));
        }

        return result;
    }

    private static string NormalizeRelativePath(string path, bool allowTrailingSlash)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1_024 || path.Contains('\\') || path.Contains(':')
            || path.StartsWith('/') || path.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            throw new InvalidDataException($"不安全的模組包路徑：{path}");
        }

        var candidate = allowTrailingSlash ? path.TrimEnd('/') : path;
        if (candidate.Length == 0) throw new InvalidDataException("空白模組包路徑。");
        var parts = candidate.Split('/');
        if (parts.Any(static part => part.Length == 0 || part is "." or ".."))
        {
            throw new InvalidDataException($"不安全的模組包路徑：{path}");
        }

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index].Normalize(NormalizationForm.FormC);
            if (part.Length > 255 || part.EndsWith(' ') || part.EndsWith('.')
                || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            {
                throw new InvalidDataException($"Windows 不支援的模組包路徑：{path}");
            }

            var baseName = part.Split('.')[0].TrimEnd(' ', '.');
            if (ReservedWindowsNames.Contains(baseName)) throw new InvalidDataException($"Windows 保留名稱：{path}");
            parts[index] = part;
        }

        return string.Join('/', parts);
    }

    private static string RequiredString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new InvalidDataException($"Manifest 缺少有效的 {property}。");

    private static string RequiredHex(JsonElement element, string property, int length, string path)
    {
        var value = RequiredString(element, property);
        if (value.Length != length || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"Manifest {property} 格式無效：{path}");
        }

        return value.ToLowerInvariant();
    }

    private static void RejectLink(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var dosAttributes = entry.ExternalAttributes & 0xFFFF;
        var upperAttributes = (entry.ExternalAttributes >> 16) & 0xFFFF;
        if (unixType == 0xA000
            || (dosAttributes & (int)FileAttributes.ReparsePoint) != 0
            || (upperAttributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"模組包不可包含符號連結或 reparse point：{entry.FullName}");
        }
    }

    private static void CreateSafeParents(string root, string parent)
    {
        var relative = Path.GetRelativePath(root, parent);
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current)) throw new IOException($"目的地父路徑是檔案：{current}");
            Directory.CreateDirectory(current);
            RejectExistingReparse(current);
        }
    }

    private static void RejectExistingReparse(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"安裝路徑不可包含 reparse point：{path}");
        }
    }

    private static long CheckedAdd(long left, long right, string message)
    {
        try { return checked(left + right); }
        catch (OverflowException exception) { throw new InvalidDataException(message, exception); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
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
                    throw new InvalidDataException($"Manifest 含重複 JSON 屬性：{property.Name}");
                }

                RejectDuplicateJsonProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateJsonProperties(item);
        }
    }

    private sealed class PathRegistry
    {
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

        public void AddFile(string path, string source)
        {
            if (_files.Contains(path)) throw new InvalidDataException($"{source} 含重複路徑：{path}");
            if (_directories.Contains(path)) throw new InvalidDataException($"{source} 含檔案/資料夾衝突：{path}");
            var separator = path.IndexOf('/');
            while (separator >= 0)
            {
                var parent = path[..separator];
                if (_files.Contains(parent))
                {
                    throw new InvalidDataException($"{source} 含檔案/資料夾衝突：{path}");
                }

                _directories.Add(parent);
                separator = path.IndexOf('/', separator + 1);
            }

            _files.Add(path);
        }
    }
}
