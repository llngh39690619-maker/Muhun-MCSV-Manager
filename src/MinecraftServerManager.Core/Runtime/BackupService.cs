using System.Buffers;
using System.IO.Compression;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Runtime;

public enum BackupStage
{
    Scanning,
    Compressing,
    Completed,
}

public sealed record BackupProgress(
    BackupStage Stage,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string? CurrentRelativePath = null);

public sealed record BackupResult(
    string ArchivePath,
    int FileCount,
    long UncompressedBytes,
    long ArchiveBytes,
    DateTimeOffset CompletedAtUtc);

public sealed record BackupOptions
{
    public string? DestinationDirectory { get; init; }

    public string? ArchiveFileName { get; init; }

    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Optimal;

    public IReadOnlyCollection<string> ExcludedDirectoryNames { get; init; } =
        ["backups", "cache"];

    /// <summary>File names (not paths) to omit at any depth, compared case-insensitively.</summary>
    public IReadOnlyCollection<string> ExcludedFileNames { get; init; } = [];

    /// <summary>File-name prefixes (not paths) to omit at any depth, compared case-insensitively.</summary>
    public IReadOnlyCollection<string> ExcludedFileNamePrefixes { get; init; } = [];

    /// <summary>
    /// Optional root-relative allowlist of files or directories to include. A <see langword="null"/>
    /// value preserves the historical behavior of scanning the complete source root. An empty
    /// collection creates an empty archive. Every supplied path is validated as a strict descendant
    /// of the source root before any archive is created; rooted paths, empty segments and dot
    /// segments are rejected rather than normalized into a different target.
    /// </summary>
    public IReadOnlyCollection<string>? IncludedRelativePaths { get; init; }

    public int BufferSize { get; init; } = 128 * 1024;

    /// <summary>
    /// A published recovery point must never silently omit redirecting junctions or symbolic
    /// links. Windows cloud placeholders that do not expose a link target are read normally; a
    /// hydration/read failure aborts the complete backup instead of omitting that file.
    /// </summary>
    public bool FailOnReparsePoint { get; init; } = true;
}

/// <summary>
/// Creates cancellable ZIP backups without ever exposing a partially-created archive as a
/// completed backup. Redirecting reparse points are rejected by default so a server directory
/// cannot make a backup escape into an unrelated directory tree.
/// </summary>
public sealed class BackupService
{
    public Task<BackupResult> CreateBackupAsync(
        ServerInstance instance,
        BackupOptions? options = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return CreateBackupAsync(
            instance.DirectoryPath,
            instance.Name,
            options,
            progress,
            cancellationToken);
    }

    public async Task<BackupResult> CreateBackupAsync(
        string sourceDirectory,
        string backupName,
        BackupOptions? options = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupName);

        options ??= new BackupOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BufferSize, 4 * 1024);

        var sourceRoot = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"Server directory was not found: {sourceRoot}");
        }
        EnsureNoRedirectingReparsePointsUnderRoot(sourceRoot, sourceRoot);

        var destinationDirectory = Path.GetFullPath(
            options.DestinationDirectory ?? Path.Combine(sourceRoot, "backups"));
        Directory.CreateDirectory(destinationDirectory);
        EnsureNoRedirectingReparsePointsUnderRoot(destinationDirectory, destinationDirectory);
        if (SafePath.IsWithinRoot(sourceRoot, destinationDirectory))
        {
            EnsureNoRedirectingReparsePointsUnderRoot(sourceRoot, destinationDirectory);
        }

        var archiveFileName = CreateArchiveFileName(backupName, options.ArchiveFileName);
        var archivePath = FindAvailableArchivePath(destinationDirectory, archiveFileName);
        var partialPath = archivePath + $".{Guid.NewGuid():N}.partial";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new BackupProgress(BackupStage.Scanning, 0, 0, 0, 0));
            var exclusions = new HashSet<string>(
                options.ExcludedDirectoryNames.Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            var excludedFileNames = new HashSet<string>(
                options.ExcludedFileNames.Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            var excludedFileNamePrefixes = options.ExcludedFileNamePrefixes
                .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // The lock file is manager coordination state, not server data. It is always omitted,
            // even when callers replace the configurable exclusion list.
            excludedFileNames.Add(ServerDirectoryLock.FileName);
            var files = EnumerateSafeFiles(
                sourceRoot,
                exclusions,
                excludedFileNames,
                excludedFileNamePrefixes,
                options.IncludedRelativePaths,
                archivePath,
                partialPath,
                options.FailOnReparsePoint,
                cancellationToken);
            var totalBytes = files.Sum(file => file.Length);

            progress?.Report(new BackupProgress(
                BackupStage.Compressing,
                0,
                files.Count,
                0,
                totalBytes));

            await using (var archiveStream = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                options.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true);
                var completedFiles = 0;
                long completedBytes = 0;
                var buffer = ArrayPool<byte>.Shared.Rent(options.BufferSize);

                try
                {
                    foreach (var file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relativePath = Path.GetRelativePath(sourceRoot, file.FullName)
                            .Replace(Path.DirectorySeparatorChar, '/');
                        EnsureSafeEntryName(relativePath);

                        var entry = archive.CreateEntry(relativePath, options.CompressionLevel);
                        entry.LastWriteTime = GetSafeZipTimestamp(file.LastWriteTimeUtc);

                        await using var input = new FileStream(
                            file.FullName,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            options.BufferSize,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var output = entry.Open();

                        int bytesRead;
                        while ((bytesRead = await input.ReadAsync(
                                   buffer.AsMemory(0, buffer.Length),
                                   cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(
                                buffer.AsMemory(0, bytesRead),
                                cancellationToken).ConfigureAwait(false);
                            completedBytes += bytesRead;
                        }

                        completedFiles++;
                        progress?.Report(new BackupProgress(
                            BackupStage.Compressing,
                            completedFiles,
                            files.Count,
                            completedBytes,
                            totalBytes,
                            relativePath));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, archivePath, overwrite: false);
            var archiveBytes = new FileInfo(archivePath).Length;
            var result = new BackupResult(
                archivePath,
                files.Count,
                totalBytes,
                archiveBytes,
                DateTimeOffset.UtcNow);

            progress?.Report(new BackupProgress(
                BackupStage.Completed,
                files.Count,
                files.Count,
                totalBytes,
                totalBytes));
            return result;
        }
        catch
        {
            TryDeletePartialFile(partialPath);
            throw;
        }
    }

    private static List<FileInfo> EnumerateSafeFiles(
        string sourceRoot,
        HashSet<string> exclusions,
        HashSet<string> excludedFileNames,
        IReadOnlyList<string> excludedFileNamePrefixes,
        IReadOnlyCollection<string>? includedRelativePaths,
        string archivePath,
        string partialPath,
        bool failOnReparsePoint,
        CancellationToken cancellationToken)
    {
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var files = new Dictionary<string, FileInfo>(pathComparer);
        var visitedDirectories = new HashSet<string>(pathComparer);
        var directories = new Stack<DirectoryInfo>();
        if (includedRelativePaths is null)
        {
            directories.Push(new DirectoryInfo(sourceRoot));
        }
        else
        {
            foreach (var includedPath in NormalizeIncludedPaths(sourceRoot, includedRelativePaths))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(sourceRoot, includedPath);
                var isDirectory = Directory.Exists(includedPath);
                var isFile = File.Exists(includedPath);
                if (!isDirectory && !isFile)
                {
                    continue;
                }

                // A directly selected descendant can otherwise enter through a junction in one
                // of its ancestors without the normal root traversal ever observing that junction.
                try
                {
                    EnsureNoRedirectingReparsePointsUnderRoot(sourceRoot, includedPath);
                }
                catch (InvalidDataException) when (!failOnReparsePoint)
                {
                    continue;
                }
                if (isDirectory)
                {
                    if (ContainsExcludedDirectorySegment(relativePath, exclusions, isDirectory: true))
                    {
                        continue;
                    }

                    var directory = new DirectoryInfo(includedPath);
                    if (IsRedirectingReparsePoint(directory))
                    {
                        if (failOnReparsePoint)
                        {
                            throw new InvalidDataException(
                                $"備份 allowlist 遇到 reparse/cloud placeholder 目錄，已取消以避免越界：{directory.FullName}");
                        }

                        continue;
                    }

                    directories.Push(directory);
                }
                else
                {
                    if (ContainsExcludedDirectorySegment(relativePath, exclusions, isDirectory: false))
                    {
                        continue;
                    }

                    AddFileIfAllowed(new FileInfo(includedPath));
                }
            }
        }

        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visitedDirectories.Add(Path.GetFullPath(directory.FullName)))
            {
                continue;
            }

            foreach (var childDirectory in directory.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (exclusions.Contains(childDirectory.Name))
                {
                    continue;
                }

                if (IsRedirectingReparsePoint(childDirectory))
                {
                    if (failOnReparsePoint)
                    {
                        throw new InvalidDataException(
                            $"備份遇到 reparse/cloud placeholder 目錄，已取消以避免產生不完整恢復點：{childDirectory.FullName}");
                    }

                    continue;
                }

                directories.Push(childDirectory);
            }

            foreach (var file in directory.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddFileIfAllowed(file);
            }
        }

        var result = files.Values.ToList();
        result.Sort((left, right) => string.Compare(
            left.FullName,
            right.FullName,
            StringComparison.OrdinalIgnoreCase));
        return result;

        void AddFileIfAllowed(FileInfo file)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludedFileNames.Contains(file.Name)
                || excludedFileNamePrefixes.Any(prefix =>
                    file.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                || PathsEqual(file.FullName, archivePath)
                || PathsEqual(file.FullName, partialPath))
            {
                return;
            }

            if (IsRedirectingReparsePoint(file))
            {
                if (failOnReparsePoint)
                {
                    throw new InvalidDataException(
                        $"備份遇到 reparse/cloud placeholder 檔案，已取消以避免產生不完整恢復點：{file.FullName}");
                }

                return;
            }

            files.TryAdd(Path.GetFullPath(file.FullName), file);
        }
    }

    private static IReadOnlyList<string> NormalizeIncludedPaths(
        string sourceRoot,
        IReadOnlyCollection<string> includedRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(includedRelativePaths);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var normalized = new HashSet<string>(comparison);
        foreach (var suppliedPath in includedRelativePaths)
        {
            if (string.IsNullOrWhiteSpace(suppliedPath))
            {
                throw new ArgumentException(
                    "IncludedRelativePaths cannot contain an empty path.",
                    nameof(includedRelativePaths));
            }

            if (Path.IsPathRooted(suppliedPath) || suppliedPath.Contains(':'))
            {
                throw new ArgumentException(
                    $"Backup allowlist paths must be root-relative: {suppliedPath}",
                    nameof(includedRelativePaths));
            }

            var segments = suppliedPath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.None);
            if (segments.Any(static segment =>
                    segment.Length == 0
                    || segment is "." or ".."
                    || segment.EndsWith(' ')
                    || segment.EndsWith('.')))
            {
                throw new ArgumentException(
                    $"Backup allowlist paths cannot contain empty, dot or ambiguous segments: {suppliedPath}",
                    nameof(includedRelativePaths));
            }

            string candidate;
            try
            {
                candidate = SafePath.EnsureWithinRoot(
                    sourceRoot,
                    Path.Combine(sourceRoot, Path.Combine(segments)),
                    allowRoot: false);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException
                    or UnauthorizedAccessException)
            {
                throw new ArgumentException(
                    $"Backup allowlist path is invalid or escapes the source root: {suppliedPath}",
                    nameof(includedRelativePaths),
                    exception);
            }

            normalized.Add(candidate);
        }

        return normalized.OrderBy(static path => path, comparison).ToArray();
    }

    private static bool ContainsExcludedDirectorySegment(
        string relativePath,
        IReadOnlySet<string> exclusions,
        bool isDirectory)
    {
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var directorySegmentCount = isDirectory
            ? segments.Length
            : Math.Max(segments.Length - 1, 0);
        for (var index = 0; index < directorySegmentCount; index++)
        {
            if (exclusions.Contains(segments[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureNoRedirectingReparsePointsUnderRoot(string root, string candidate)
    {
        var safeCandidate = SafePath.EnsureWithinRoot(root, candidate);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var relative = Path.GetRelativePath(fullRoot, safeCandidate);
        var current = fullRoot;
        RejectRedirectingReparsePoint(new DirectoryInfo(current));
        if (relative == ".") return;

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            RejectRedirectingReparsePoint(info);
        }
    }

    private static void RejectRedirectingReparsePoint(FileSystemInfo info)
    {
        if (IsRedirectingReparsePoint(info))
        {
            throw new InvalidDataException(
                $"備份路徑包含會重新導向的 symbolic link 或 junction：{info.FullName}");
        }
    }

    private static bool IsRedirectingReparsePoint(FileSystemInfo info)
    {
        if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        try
        {
            // OneDrive/cloud placeholder and Windows compression reparse points normally expose
            // no link target. Reading them below either hydrates the exact file or fails the whole
            // operation. Symbolic links and junctions expose a target and must never be followed.
            return info.LinkTarget is not null;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // An unclassifiable reparse point is unsafe by default.
            return true;
        }
    }

    private static string CreateArchiveFileName(string backupName, string? requestedFileName)
    {
        if (!string.IsNullOrWhiteSpace(requestedFileName))
        {
            if (Path.IsPathRooted(requestedFileName)
                || !string.Equals(
                    requestedFileName,
                    Path.GetFileName(requestedFileName),
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "ArchiveFileName must be a file name, not a path.",
                    nameof(requestedFileName));
            }

            return requestedFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? requestedFileName
                : requestedFileName + ".zip";
        }

        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var safeName = new string(backupName
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(safeName))
        {
            safeName = "server";
        }

        return $"{safeName}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip";
    }

    private static string FindAvailableArchivePath(string destinationDirectory, string fileName)
    {
        var candidate = Path.Combine(destinationDirectory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            candidate = Path.Combine(destinationDirectory, $"{baseName}-{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("No available backup archive name could be allocated.");
    }

    private static void EnsureSafeEntryName(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)
            || relativePath[0] == '/'
            || relativePath.Split('/').Any(segment => segment is ".." or "."))
        {
            throw new IOException($"Unsafe ZIP entry path: {relativePath}");
        }
    }

    private static DateTimeOffset GetSafeZipTimestamp(DateTime lastWriteTimeUtc)
    {
        // ZIP timestamps cannot represent dates before 1980 or after 2107.
        var minimum = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var maximum = new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);
        var timestamp = new DateTimeOffset(DateTime.SpecifyKind(lastWriteTimeUtc, DateTimeKind.Utc));
        return timestamp < minimum ? minimum : timestamp > maximum ? maximum : timestamp;
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static void TryDeletePartialFile(string partialPath)
    {
        try
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
        catch (IOException)
        {
            // Preserve the original exception. A stale .partial file is clearly marked incomplete.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original exception. A stale .partial file is clearly marked incomplete.
        }
    }
}
