using System.Buffers;
using System.IO.Compression;
using System.Text;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Runtime;

public enum BackupRestoreStage
{
    Validating,
    Extracting,
    Committing,
    Completed,
}

public sealed record BackupRestoreProgress(
    BackupRestoreStage Stage,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string? CurrentRelativePath = null);

public sealed record BackupRestoreResult(
    string ArchivePath,
    string DestinationDirectory,
    int RestoredFileCount,
    int RestoredDirectoryCount,
    long RestoredUncompressedBytes,
    long ArchiveBytes,
    DateTimeOffset CompletedAtUtc);

public sealed record BackupRestoreOptions
{
    /// <summary>
    /// Existing directory that the restore destination must be contained by. The directory itself
    /// is an explicit trust boundary: ancestors at or above it are not rejected merely because a
    /// cloud-sync provider such as OneDrive exposes them as reparse points. Existing descendants
    /// between this root and the destination parent are still rejected when they are reparse
    /// points. When omitted, the destination's immediate parent is treated as the trust boundary.
    /// </summary>
    public string? TrustedDestinationRoot { get; init; }

    /// <summary>Maximum number of central-directory entries, including directory entries.</summary>
    public int MaxEntryCount { get; init; } = 200_000;

    public int MaxFileCount { get; init; } = 150_000;

    public long MaxFileUncompressedBytes { get; init; } = 16L * 1024 * 1024 * 1024;

    public long MaxTotalUncompressedBytes { get; init; } = 128L * 1024 * 1024 * 1024;

    /// <summary>
    /// Maximum uncompressed-to-compressed ratio for both an individual file and the complete
    /// archive. Empty files are excluded from the ratio calculation.
    /// </summary>
    public double MaxCompressionRatio { get; init; } = 1_000d;

    public int MaxRelativePathLength { get; init; } = 4_096;

    public int BufferSize { get; init; } = 128 * 1024;
}

/// <summary>
/// Restores a manager ZIP backup into a brand-new sibling directory. Files are first extracted
/// into a same-parent staging directory and become visible at the requested destination only
/// after the complete archive has passed validation and extraction.
/// </summary>
public sealed class BackupRestoreService
{
    private const int DosReparsePointAttribute = (int)FileAttributes.ReparsePoint;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFileType = 0x8000;
    private const int UnixDirectoryType = 0x4000;
    private const int UnixSymbolicLinkType = 0xA000;

    private static readonly HashSet<string> ReservedWindowsNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    public async Task<BackupRestoreResult> RestoreAsync(
        string archivePath,
        string destinationDirectory,
        BackupRestoreOptions? options = null,
        IProgress<BackupRestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        options ??= new BackupRestoreOptions();
        ValidateOptions(options);

        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath))
        {
            throw new FileNotFoundException("Backup archive was not found.", fullArchivePath);
        }

        if (File.GetAttributes(fullArchivePath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The backup archive cannot be a symbolic link or reparse point.");
        }

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"Restore destination already exists: {destination}");
        }

        var parent = Directory.GetParent(destination)?.FullName
            ?? throw new ArgumentException("Restore destination must have a parent directory.", nameof(destinationDirectory));
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException($"Restore destination parent was not found: {parent}");
        }

        var trustedDestinationRoot = ResolveTrustedDestinationRoot(
            options.TrustedDestinationRoot,
            parent,
            destination);
        RejectReparsePointsBelowTrustedRoot(trustedDestinationRoot, parent);
        ValidateDestinationName(Path.GetFileName(destination));

        var staging = Path.Combine(
            parent,
            $".{Path.GetFileName(destination)}.restore-{Guid.NewGuid():N}.partial");
        var archiveBytes = new FileInfo(fullArchivePath).Length;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new BackupRestoreProgress(BackupRestoreStage.Validating, 0, 0, 0, 0));

            await using var archiveStream = new FileStream(
                fullArchivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                options.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            var plan = InspectArchive(archive, options, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePointsBelowTrustedRoot(trustedDestinationRoot, parent);
            Directory.CreateDirectory(staging);
            RejectReparsePointsBelowTrustedRoot(trustedDestinationRoot, staging);

            progress?.Report(new BackupRestoreProgress(
                BackupRestoreStage.Extracting,
                0,
                plan.FileCount,
                0,
                plan.TotalUncompressedBytes));

            var completedFiles = 0;
            long completedBytes = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(options.BufferSize);
            try
            {
                foreach (var item in plan.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var outputPath = CombineUnderStaging(staging, item.RelativePath);
                    if (item.IsDirectory)
                    {
                        CreateSafeDirectory(staging, outputPath);
                        SetDirectoryTimestamp(outputPath, item.Entry.LastWriteTime);
                        continue;
                    }

                    CreateSafeDirectory(staging, Path.GetDirectoryName(outputPath)!);
                    await using var input = item.Entry.Open();
                    await using (var output = new FileStream(
                                     outputPath,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     options.BufferSize,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        long fileBytes = 0;
                        while (true)
                        {
                            var bytesRead = await input.ReadAsync(
                                buffer.AsMemory(0, buffer.Length),
                                cancellationToken).ConfigureAwait(false);
                            if (bytesRead == 0)
                            {
                                break;
                            }

                            fileBytes = CheckedAdd(fileBytes, bytesRead, "Restored file size overflowed.");
                            completedBytes = CheckedAdd(
                                completedBytes,
                                bytesRead,
                                "Total restored size overflowed.");
                            if (fileBytes > item.UncompressedBytes
                                || fileBytes > options.MaxFileUncompressedBytes
                                || completedBytes > options.MaxTotalUncompressedBytes)
                            {
                                throw new InvalidDataException(
                                    $"Backup entry expanded beyond its validated limit: {item.RelativePath}");
                            }

                            await output.WriteAsync(
                                buffer.AsMemory(0, bytesRead),
                                cancellationToken).ConfigureAwait(false);

                            progress?.Report(new BackupRestoreProgress(
                                BackupRestoreStage.Extracting,
                                completedFiles,
                                plan.FileCount,
                                completedBytes,
                                plan.TotalUncompressedBytes,
                                item.RelativePath));
                        }

                        if (fileBytes != item.UncompressedBytes)
                        {
                            throw new InvalidDataException(
                                $"Backup entry length did not match its ZIP metadata: {item.RelativePath}");
                        }
                    }

                    SetFileTimestamp(outputPath, item.Entry.LastWriteTime);
                    completedFiles++;
                    progress?.Report(new BackupRestoreProgress(
                        BackupRestoreStage.Extracting,
                        completedFiles,
                        plan.FileCount,
                        completedBytes,
                        plan.TotalUncompressedBytes,
                        item.RelativePath));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePointsBelowTrustedRoot(trustedDestinationRoot, parent);
            RejectReparsePointsBelowTrustedRoot(trustedDestinationRoot, staging);
            RejectReparseTree(staging);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new IOException($"Restore destination appeared before commit: {destination}");
            }

            progress?.Report(new BackupRestoreProgress(
                BackupRestoreStage.Committing,
                completedFiles,
                plan.FileCount,
                completedBytes,
                plan.TotalUncompressedBytes));

            // The staging directory is a sibling of the final directory. Directory.Move therefore
            // commits by a same-volume rename and never merges into an existing server directory.
            Directory.Move(staging, destination);

            var result = new BackupRestoreResult(
                fullArchivePath,
                destination,
                plan.FileCount,
                plan.DirectoryCount,
                plan.TotalUncompressedBytes,
                archiveBytes,
                DateTimeOffset.UtcNow);
            progress?.Report(new BackupRestoreProgress(
                BackupRestoreStage.Completed,
                plan.FileCount,
                plan.FileCount,
                plan.TotalUncompressedBytes,
                plan.TotalUncompressedBytes));
            return result;
        }
        catch
        {
            TryDeleteStaging(staging, parent);
            throw;
        }
    }

    private static RestorePlan InspectArchive(
        ZipArchive archive,
        BackupRestoreOptions options,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > options.MaxEntryCount)
        {
            throw new InvalidDataException(
                $"Backup contains too many entries ({archive.Entries.Count:N0}).");
        }

        var registry = new PathRegistry();
        var entries = new List<RestoreEntry>(archive.Entries.Count);
        var fileCount = 0;
        long totalUncompressed = 0;
        long totalCompressed = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinkOrSpecialEntry(entry);
            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            var relativePath = NormalizeRelativePath(
                entry.FullName,
                isDirectory,
                options.MaxRelativePathLength);
            ValidateEntryKind(entry, isDirectory);

            registry.Add(relativePath, entry.FullName, isDirectory);
            if (isDirectory)
            {
                entries.Add(new RestoreEntry(entry, relativePath, true, 0));
                continue;
            }

            fileCount++;
            if (fileCount > options.MaxFileCount)
            {
                throw new InvalidDataException($"Backup contains more than {options.MaxFileCount:N0} files.");
            }

            var length = entry.Length;
            var compressedLength = entry.CompressedLength;
            if (length < 0 || compressedLength < 0)
            {
                throw new InvalidDataException($"Backup entry has an invalid length: {entry.FullName}");
            }

            if (length > options.MaxFileUncompressedBytes)
            {
                throw new InvalidDataException($"Backup entry is too large: {entry.FullName}");
            }

            RejectCompressionBomb(length, compressedLength, options.MaxCompressionRatio, entry.FullName);
            totalUncompressed = CheckedAdd(
                totalUncompressed,
                length,
                "Backup total uncompressed size overflowed.");
            totalCompressed = CheckedAdd(
                totalCompressed,
                compressedLength,
                "Backup total compressed size overflowed.");
            if (totalUncompressed > options.MaxTotalUncompressedBytes)
            {
                throw new InvalidDataException("Backup exceeds the total extraction size limit.");
            }

            entries.Add(new RestoreEntry(entry, relativePath, false, length));
        }

        RejectCompressionBomb(
            totalUncompressed,
            totalCompressed,
            options.MaxCompressionRatio,
            "complete archive");
        return new RestorePlan(
            entries,
            fileCount,
            registry.DirectoryCount,
            totalUncompressed);
    }

    private static string NormalizeRelativePath(
        string entryName,
        bool isDirectory,
        int maxRelativePathLength)
    {
        if (string.IsNullOrEmpty(entryName)
            || entryName.Length > maxRelativePathLength
            || entryName.StartsWith("/", StringComparison.Ordinal)
            || entryName.Contains('\\')
            || Path.IsPathRooted(entryName))
        {
            throw new InvalidDataException($"Unsafe backup entry path: {entryName}");
        }

        var candidate = isDirectory ? entryName[..^1] : entryName;
        if (candidate.Length == 0)
        {
            throw new InvalidDataException("Backup contains an empty root directory entry.");
        }

        var rawSegments = candidate.Split('/');
        if (rawSegments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException($"Unsafe backup entry path: {entryName}");
        }

        var normalizedSegments = new string[rawSegments.Length];
        for (var index = 0; index < rawSegments.Length; index++)
        {
            var rawSegment = rawSegments[index];
            string normalized;
            try
            {
                normalized = rawSegment.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException($"Backup path contains invalid Unicode: {entryName}", exception);
            }

            if (normalized.Length == 0
                || normalized.Length > 255
                || normalized.EndsWith(' ')
                || normalized.EndsWith('.')
                || normalized.Contains(':')
                || normalized.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
                || normalized.Any(static character => char.IsControl(character) || char.IsSurrogate(character)))
            {
                throw new InvalidDataException($"Windows does not support this backup path: {entryName}");
            }

            var deviceBaseName = normalized.Split('.')[0].TrimEnd(' ', '.');
            if (IsReservedWindowsName(deviceBaseName))
            {
                throw new InvalidDataException($"Backup uses a reserved Windows name: {entryName}");
            }

            normalizedSegments[index] = normalized;
        }

        var normalizedPath = string.Join('/', normalizedSegments);
        if (normalizedPath.Length > maxRelativePathLength)
        {
            throw new InvalidDataException($"Backup entry path is too long: {entryName}");
        }

        return normalizedPath;
    }

    private static bool IsReservedWindowsName(string baseName)
    {
        if (ReservedWindowsNames.Contains(baseName))
        {
            return true;
        }

        // Windows also treats superscript 1, 2 and 3 as digits in COM/LPT device names.
        return baseName.Length == 4
               && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                   || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
               && baseName[3] is '\u00B9' or '\u00B2' or '\u00B3';
    }

    private static void ValidateDestinationName(string name)
    {
        _ = NormalizeRelativePath(name, isDirectory: false, maxRelativePathLength: 255);
    }

    private static void RejectLinkOrSpecialEntry(ZipArchiveEntry entry)
    {
        var attributes = entry.ExternalAttributes;
        var dosAttributes = attributes & 0xFFFF;
        var upperAttributes = (attributes >> 16) & 0xFFFF;
        var unixType = upperAttributes & UnixFileTypeMask;

        if ((dosAttributes & DosReparsePointAttribute) != 0
            // Some Windows ZIP writers place raw DOS attributes in the upper word. Only treat
            // that bit as a Windows reparse marker when no Unix file type is present, because
            // 0x0400 is also the ordinary Unix owner-read permission bit.
            || (unixType == 0 && (upperAttributes & DosReparsePointAttribute) != 0)
            || unixType == UnixSymbolicLinkType
            || (unixType != 0 && unixType != UnixRegularFileType && unixType != UnixDirectoryType))
        {
            throw new InvalidDataException(
                $"Backup cannot contain symbolic links, reparse points, or special files: {entry.FullName}");
        }
    }

    private static void ValidateEntryKind(ZipArchiveEntry entry, bool isDirectory)
    {
        var unixType = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        if (isDirectory)
        {
            if (entry.Length != 0 || entry.CompressedLength != 0 || unixType == UnixRegularFileType)
            {
                throw new InvalidDataException($"Malformed backup directory entry: {entry.FullName}");
            }
        }
        else if (unixType == UnixDirectoryType)
        {
            throw new InvalidDataException($"Malformed backup file entry: {entry.FullName}");
        }
    }

    private static void RejectCompressionBomb(
        long uncompressedBytes,
        long compressedBytes,
        double maximumRatio,
        string description)
    {
        if (uncompressedBytes == 0)
        {
            return;
        }

        if (compressedBytes == 0
            || uncompressedBytes / (double)compressedBytes > maximumRatio)
        {
            throw new InvalidDataException($"Backup compression ratio is unsafe: {description}");
        }
    }

    private static string CombineUnderStaging(string staging, string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(
            staging,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(staging) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(
                prefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Backup path escaped the staging directory: {relativePath}");
        }

        return combined;
    }

    private static void CreateSafeDirectory(string staging, string directoryPath)
    {
        var relative = Path.GetRelativePath(staging, directoryPath);
        var current = staging;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
            {
                throw new IOException($"Restore path conflicts with a file: {current}");
            }

            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }

            RejectReparsePoint(current);
        }
    }

    private static string ResolveTrustedDestinationRoot(
        string? configuredRoot,
        string destinationParent,
        string destination)
    {
        if (configuredRoot is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);
        }

        var trustedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            configuredRoot ?? destinationParent));
        if (!Directory.Exists(trustedRoot))
        {
            throw new DirectoryNotFoundException(
                $"Trusted restore destination root was not found: {trustedRoot}");
        }

        SafePath.EnsureWithinRoot(trustedRoot, destination, allowRoot: false);
        return trustedRoot;
    }

    private static void RejectReparsePointsBelowTrustedRoot(
        string trustedRoot,
        string candidatePath)
    {
        var candidate = SafePath.EnsureWithinRoot(trustedRoot, candidatePath);
        var relativePath = Path.GetRelativePath(trustedRoot, candidate);
        if (relativePath == ".")
        {
            return;
        }

        var current = trustedRoot;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    private static void RejectReparseTree(string root)
    {
        RejectReparsePoint(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                RejectReparsePoint(item.FullName);
                if (item is DirectoryInfo)
                {
                    pending.Push(item.FullName);
                }
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"Restore path contains a reparse point: {path}");
        }
    }

    private static void SetFileTimestamp(string path, DateTimeOffset timestamp)
    {
        if (timestamp != default)
        {
            File.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);
        }
    }

    private static void SetDirectoryTimestamp(string path, DateTimeOffset timestamp)
    {
        if (timestamp != default)
        {
            Directory.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);
        }
    }

    private static void ValidateOptions(BackupRestoreOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxEntryCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFileCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFileUncompressedBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTotalUncompressedBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxRelativePathLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BufferSize, 4 * 1024);
        if (!double.IsFinite(options.MaxCompressionRatio) || options.MaxCompressionRatio < 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxCompressionRatio),
                "Maximum compression ratio must be finite and at least one.");
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

    private static void TryDeleteStaging(string staging, string expectedParent)
    {
        try
        {
            if (!Directory.Exists(staging) && !File.Exists(staging))
            {
                return;
            }

            var actualParent = Directory.GetParent(Path.GetFullPath(staging))?.FullName;
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(actualParent ?? string.Empty),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedParent)),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !Path.GetFileName(staging).EndsWith(".partial", StringComparison.Ordinal))
            {
                return;
            }

            DeleteWithoutFollowingReparsePoints(staging);
        }
        catch (IOException)
        {
            // Preserve the original restore failure. Staging names remain visibly marked partial.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original restore failure. Staging names remain visibly marked partial.
        }
    }

    private static void DeleteWithoutFollowingReparsePoints(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }

            return;
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            File.Delete(path);
            return;
        }

        foreach (var child in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            DeleteWithoutFollowingReparsePoints(child.FullName);
        }

        Directory.Delete(path, recursive: false);
    }

    private sealed record RestoreEntry(
        ZipArchiveEntry Entry,
        string RelativePath,
        bool IsDirectory,
        long UncompressedBytes);

    private sealed record RestorePlan(
        IReadOnlyList<RestoreEntry> Entries,
        int FileCount,
        int DirectoryCount,
        long TotalUncompressedBytes);

    private sealed class PathRegistry
    {
        private readonly Dictionary<string, string> _prefixSpellings = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _entryPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

        public int DirectoryCount => _directories.Count;

        public void Add(string normalizedPath, string originalPath, bool isDirectory)
        {
            var normalizedSegments = normalizedPath.Split('/');
            var originalCandidate = originalPath.EndsWith("/", StringComparison.Ordinal)
                ? originalPath[..^1]
                : originalPath;
            var originalSegments = originalCandidate.Split('/');
            var normalizedPrefix = string.Empty;
            var originalPrefix = string.Empty;
            for (var index = 0; index < normalizedSegments.Length; index++)
            {
                normalizedPrefix = index == 0
                    ? normalizedSegments[index]
                    : $"{normalizedPrefix}/{normalizedSegments[index]}";
                originalPrefix = index == 0
                    ? originalSegments[index]
                    : $"{originalPrefix}/{originalSegments[index]}";

                if (_prefixSpellings.TryGetValue(normalizedPrefix, out var existingSpelling))
                {
                    if (!string.Equals(existingSpelling, originalPrefix, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Backup contains a case-insensitive or Unicode-normalized path collision: {originalPath}");
                    }
                }
                else
                {
                    _prefixSpellings.Add(normalizedPrefix, originalPrefix);
                }
            }

            if (!_entryPaths.Add(normalizedPath))
            {
                throw new InvalidDataException($"Backup contains a duplicate path: {originalPath}");
            }

            var slash = normalizedPath.IndexOf('/');
            while (slash >= 0)
            {
                var parent = normalizedPath[..slash];
                if (_files.Contains(parent))
                {
                    throw new InvalidDataException($"Backup contains a file/directory conflict: {originalPath}");
                }

                _directories.Add(parent);
                slash = normalizedPath.IndexOf('/', slash + 1);
            }

            if (isDirectory)
            {
                if (_files.Contains(normalizedPath))
                {
                    throw new InvalidDataException($"Backup contains a file/directory conflict: {originalPath}");
                }

                _directories.Add(normalizedPath);
            }
            else
            {
                if (_directories.Contains(normalizedPath) || !_files.Add(normalizedPath))
                {
                    throw new InvalidDataException($"Backup contains a file/directory conflict: {originalPath}");
                }
            }
        }
    }
}
