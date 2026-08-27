using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Providers;

internal sealed record SpigotBuildToolsSourceCacheOptions
{
    public static SpigotBuildToolsSourceCacheOptions Default { get; } = new();

    public long MaximumBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    public int MaximumEntries { get; init; } = 4;

    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromDays(45);

    public TimeSpan LockRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public void Validate()
    {
        if (MaximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumBytes));
        }

        if (MaximumEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries));
        }

        if (MaximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAge));
        }

        if (LockRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LockRetryDelay));
        }
    }
}

internal delegate Task<ModrinthLoaderBootstrapProcessResult> SpigotBuildToolsGitCommand(
    IReadOnlyList<string> arguments,
    IProgress<ModrinthLoaderBootstrapOutputLine>? output,
    CancellationToken cancellationToken);

/// <summary>
/// Owns persistent, fixed-upstream bare mirrors used only as sources for independent operation
/// clones. Every promoted mirror has canonical configuration, no hooks or alternates, passes a
/// strict Git object check, and contains the immutable commit selected by the BuildTools plan.
/// </summary>
internal sealed class SpigotBuildToolsSourceCache
{
    private const int MaximumScannedEntries = 500_000;
    private const string MirrorSuffix = ".git";
    private const string PartialSuffix = ".partial";

    private readonly string _root;
    private readonly string _mirrorsRoot;
    private readonly string _incomingRoot;
    private readonly string _locksRoot;
    private readonly string _metadataRoot;
    private readonly SpigotBuildToolsSourceCacheOptions _options;
    private readonly Func<string, string, CancellationToken, Task> _deleteTreeAsync;

    public SpigotBuildToolsSourceCache(
        string cacheRoot,
        SpigotBuildToolsSourceCacheOptions? options = null,
        Func<string, string, CancellationToken, Task>? deleteTreeAsync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        _mirrorsRoot = Path.Combine(_root, "mirrors");
        _incomingRoot = Path.Combine(_root, "incoming");
        _locksRoot = Path.Combine(_root, "locks");
        _metadataRoot = Path.Combine(_root, "metadata");
        _options = options ?? SpigotBuildToolsSourceCacheOptions.Default;
        _options.Validate();
        _deleteTreeAsync = deleteTreeAsync ?? DeleteTreeAsync;
    }

    public async Task<SpigotBuildToolsSourceMirrorLease> AcquireAsync(
        string repositoryName,
        Uri remote,
        string expectedCommit,
        SpigotBuildToolsGitCommand runGitAsync,
        IProgress<ModrinthLoaderBootstrapOutputLine>? output,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCommit);
        ArgumentNullException.ThrowIfNull(runGitAsync);
        RequireSafeRemote(remote);
        RequireCommit(expectedCommit);
        EnsureCacheDirectories();

        var key = BuildKey(repositoryName, remote);
        var lockPath = SafePath.CombineUnderRoot(_locksRoot, key + ".lock");
        var cacheLock = await AcquireLockAsync(lockPath, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await DeleteOwnedPartialsAsync(key, cancellationToken).ConfigureAwait(false);
            var mirrorPath = SafePath.CombineUnderRoot(
                _mirrorsRoot,
                key + MirrorSuffix);
            if (Directory.Exists(mirrorPath))
            {
                try
                {
                    await ValidateMirrorAsync(
                            mirrorPath,
                            remote,
                            expectedCommit,
                            runGitAsync,
                            allowFetchMissingCommit: true,
                            runFullFsck: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                    TouchAccessMarker(key);
                    output?.Report(new ModrinthLoaderBootstrapOutputLine(
                        false,
                        $"已驗證 {repositoryName} source mirror cache；本次免除上游 clone。"));
                    return new SpigotBuildToolsSourceMirrorLease(mirrorPath, cacheLock);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (IsInvalidCacheException(exception))
                {
                    output?.Report(new ModrinthLoaderBootstrapOutputLine(
                        true,
                        $"{repositoryName} source mirror cache 已損壞或缺少指定 commit，"
                        + "將安全丟棄並由官方來源重建。"));
                    await DiscardDirectoryAsync(mirrorPath, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else if (File.Exists(mirrorPath))
            {
                throw new UnauthorizedAccessException(
                    $"BuildTools mirror cache 目標被一般檔案占用：{mirrorPath}");
            }

            var incoming = SafePath.CombineUnderRoot(
                _incomingRoot,
                $"{key}.{Guid.NewGuid():N}{PartialSuffix}");
            Exception? incomingFailure = null;
            try
            {
                output?.Report(new ModrinthLoaderBootstrapOutputLine(
                    false,
                    $"首次建立 {repositoryName} 官方 source mirror cache…"));
                await runGitAsync(
                        [
                            "clone",
                            "--mirror",
                            "--no-progress",
                            "--",
                            remote.AbsoluteUri,
                            incoming
                        ],
                        output,
                        cancellationToken)
                    .ConfigureAwait(false);

                CanonicalizeNewMirror(incoming, remote);
                await ValidateMirrorAsync(
                        incoming,
                        remote,
                        expectedCommit,
                        runGitAsync,
                        allowFetchMissingCommit: false,
                        runFullFsck: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(incoming, mirrorPath);
                // Re-read the promoted path rather than trusting the pre-move name. This also
                // directly covers the incoming-to-mirror boundary used by atomic promotion.
                await ValidateMirrorAsync(
                        mirrorPath,
                        remote,
                        expectedCommit,
                        runGitAsync,
                        allowFetchMissingCommit: false,
                        runFullFsck: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                TouchAccessMarker(key);
                return new SpigotBuildToolsSourceMirrorLease(mirrorPath, cacheLock);
            }
            catch (Exception exception)
            {
                incomingFailure = exception;
                throw;
            }
            finally
            {
                try
                {
                    await TryDeleteDirectoryAsync(incoming, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception) when (incomingFailure is not null)
                {
                    // Keep cancellation and the original clone/validation failure observable.
                    // A stale GUID-owned partial is isolated under incoming and the next holder of
                    // this repository lock will retry its no-follow cleanup.
                }
            }
        }
        catch
        {
            cacheLock.Dispose();
            throw;
        }
    }

    public async Task TrimAsync(CancellationToken cancellationToken)
    {
        try
        {
            await TrimCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsInvalidCacheException(exception))
        {
            // Trimming is maintenance after independent operation clones are ready. A transient
            // cache IO/ACL problem must never turn a successfully prepared build into a failure.
        }
    }

    private async Task TrimCoreAsync(CancellationToken cancellationToken)
    {
        EnsureCacheDirectories();
        var trimLockPath = SafePath.CombineUnderRoot(_locksRoot, "cache-trim.lock");
        using var trimLock = await AcquireLockAsync(trimLockPath, cancellationToken)
            .ConfigureAwait(false);

        var entries = new List<CacheEntry>();
        foreach (var directory in Directory.EnumerateDirectories(
                     _mirrorsRoot,
                     "*" + MirrorSuffix,
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name)
                || !name.EndsWith(MirrorSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var key = name[..^MirrorSuffix.Length];
            var accessPath = SafePath.CombineUnderRoot(_metadataRoot, key + ".access");
            DateTime accessedUtc;
            long size;
            try
            {
                var attributes = File.GetAttributes(directory);
                if (!attributes.HasFlag(FileAttributes.Directory)
                    || attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Never consult metadata or enumerate a redirecting mirror root. Mark it for
                    // no-follow eviction without touching the target behind the link.
                    accessedUtc = DateTime.MinValue;
                    size = OversizedEntrySize();
                }
                else
                {
                    accessedUtc = File.Exists(accessPath)
                        ? File.GetLastWriteTimeUtc(accessPath)
                        : Directory.GetLastWriteTimeUtc(directory);
                    size = MeasureTree(directory, cancellationToken);
                }
            }
            catch (Exception exception) when (IsInvalidCacheException(exception))
            {
                accessedUtc = DateTime.MinValue;
                size = OversizedEntrySize();
            }

            entries.Add(new CacheEntry(key, directory, accessPath, accessedUtc, size));
        }

        var totalBytes = entries.Aggregate(0L, static (current, entry) =>
        {
            if (current == long.MaxValue || entry.Size >= long.MaxValue - current)
            {
                return long.MaxValue;
            }

            return current + entry.Size;
        });
        var remaining = entries.Count;
        var cutoff = DateTime.UtcNow - _options.MaximumAge;
        foreach (var entry in entries
                     .OrderBy(static item => item.AccessedUtc)
                     .ThenBy(static item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expired = entry.AccessedUtc < cutoff;
            var overCount = remaining > _options.MaximumEntries;
            var overBytes = totalBytes > _options.MaximumBytes;
            if (!expired && !overCount && !overBytes)
            {
                continue;
            }

            var repositoryLockPath = SafePath.CombineUnderRoot(
                _locksRoot,
                entry.Key + ".lock");
            using var repositoryLock = TryAcquireLock(repositoryLockPath);
            if (repositoryLock is null)
            {
                continue;
            }

            await TryDeleteDirectoryAsync(entry.Path, cancellationToken).ConfigureAwait(false);
            TryDeleteFile(entry.AccessMarkerPath);
            remaining--;
            totalBytes = totalBytes == long.MaxValue
                ? entries.Where(candidate => Directory.Exists(candidate.Path))
                    .Aggregate(0L, static (current, candidate) =>
                        current >= long.MaxValue - candidate.Size
                            ? long.MaxValue
                            : current + candidate.Size)
                : Math.Max(0, totalBytes - entry.Size);
        }

        long OversizedEntrySize()
            => _options.MaximumBytes == long.MaxValue
                ? long.MaxValue
                : _options.MaximumBytes + 1;
    }

    internal static string BuildKey(string repositoryName, Uri remote)
    {
        var safeName = new string(repositoryName
            .Where(static character => char.IsAsciiLetterOrDigit(character))
            .ToArray());
        if (safeName.Length == 0)
        {
            throw new ArgumentException("Repository name 沒有安全字元。", nameof(repositoryName));
        }

        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(remote.AbsoluteUri)))
            .ToLowerInvariant();
        return $"{safeName}-{hash[..16]}";
    }

    private async Task ValidateMirrorAsync(
        string mirrorPath,
        Uri remote,
        string expectedCommit,
        SpigotBuildToolsGitCommand runGitAsync,
        bool allowFetchMissingCommit,
        bool runFullFsck,
        CancellationToken cancellationToken)
    {
        ValidateMirrorFileSystem(mirrorPath, remote, cancellationToken);

        var bareResult = await runGitAsync(
                ["-C", mirrorPath, "rev-parse", "--is-bare-repository"],
                output: null,
                cancellationToken)
            .ConfigureAwait(false);
        RequireSingleLine(bareResult, "true", "bare repository");

        var remoteResult = await runGitAsync(
                ["-C", mirrorPath, "remote", "get-url", "--all", "origin"],
                output: null,
                cancellationToken)
            .ConfigureAwait(false);
        RequireSingleLine(remoteResult, remote.AbsoluteUri, "origin URL");

        try
        {
            await RequireCommitAsync(
                    mirrorPath,
                    expectedCommit,
                    runGitAsync,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException) when (allowFetchMissingCommit)
        {
            // The immutable plan may select a commit added after this mirror was created. Fetch
            // only the already validated fixed origin, with hooks disabled and no prompting.
            await runGitAsync(
                    [
                        "-c",
                        "core.hooksPath=NUL",
                        "-C",
                        mirrorPath,
                        "fetch",
                        "--force",
                        "--prune",
                        "--no-tags",
                        "--no-recurse-submodules",
                        "--no-write-fetch-head",
                        "--no-auto-maintenance",
                        "origin",
                        "+refs/*:refs/*"
                    ],
                    output: null,
                    cancellationToken)
                .ConfigureAwait(false);

            ValidateMirrorFileSystem(mirrorPath, remote, cancellationToken);
            var fetchedRemote = await runGitAsync(
                    ["-C", mirrorPath, "remote", "get-url", "--all", "origin"],
                    output: null,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireSingleLine(fetchedRemote, remote.AbsoluteUri, "origin URL after fetch");
            await RequireCommitAsync(
                    mirrorPath,
                    expectedCommit,
                    runGitAsync,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!runFullFsck)
        {
            return;
        }

        _ = await runGitAsync(
                [
                    "-C",
                    mirrorPath,
                    "fsck",
                    "--full",
                    "--strict",
                    "--no-dangling",
                    "--no-progress"
                ],
                output: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task RequireCommitAsync(
        string mirrorPath,
        string expectedCommit,
        SpigotBuildToolsGitCommand runGitAsync,
        CancellationToken cancellationToken)
    {
        _ = await runGitAsync(
                ["-C", mirrorPath, "cat-file", "-e", expectedCommit + "^{commit}"],
                output: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void ValidateMirrorFileSystem(
        string mirrorPath,
        Uri remote,
        CancellationToken cancellationToken)
    {
        var containerRoot = SafePath.IsWithinRoot(_mirrorsRoot, mirrorPath)
            ? _mirrorsRoot
            : SafePath.IsWithinRoot(_incomingRoot, mirrorPath)
                ? _incomingRoot
                : throw new UnauthorizedAccessException(
                    "BuildTools source mirror 超出 cache 邊界。");
        var normalized = SafePath.EnsureWithinRoot(
            containerRoot,
            mirrorPath,
            allowRoot: false);

        if (!Directory.Exists(normalized)
            || File.GetAttributes(normalized).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("BuildTools source mirror 不是一般資料夾。");
        }

        var configPath = Path.Combine(normalized, "config");
        var headPath = Path.Combine(normalized, "HEAD");
        var objectsPath = Path.Combine(normalized, "objects");
        RequireRegularCacheFile(normalized, configPath, maximumBytes: 64 * 1024);
        RequireRegularCacheFile(normalized, headPath, maximumBytes: 4096);
        RequireRegularCacheDirectory(normalized, objectsPath);
        if (!File.ReadAllBytes(configPath).AsSpan().SequenceEqual(CanonicalConfig(remote)))
        {
            throw new InvalidDataException("BuildTools source mirror config 已遭變更。");
        }

        foreach (var alternate in new[]
                 {
                     Path.Combine(objectsPath, "info", "alternates"),
                     Path.Combine(objectsPath, "info", "http-alternates")
                 })
        {
            if (File.Exists(alternate) || Directory.Exists(alternate))
            {
                throw new InvalidDataException("BuildTools source mirror 不允許 object alternates。");
            }
        }

        var hooksPath = Path.Combine(normalized, "hooks");
        if (Directory.Exists(hooksPath) || File.Exists(hooksPath))
        {
            throw new InvalidDataException("BuildTools source mirror 不允許 Git hooks。");
        }

        _ = MeasureTree(normalized, cancellationToken);
    }

    private void CanonicalizeNewMirror(string mirrorPath, Uri remote)
    {
        if (!Directory.Exists(mirrorPath))
        {
            throw new InvalidDataException("官方 source mirror clone 未建立輸出資料夾。");
        }

        SafePath.EnsureNoReparsePointsUnderRoot(_incomingRoot, mirrorPath);
        var hooksPath = SafePath.CombineUnderRoot(mirrorPath, "hooks");
        if (Directory.Exists(hooksPath))
        {
            SafePath.DeleteTreeWithoutFollowingReparsePoints(mirrorPath, hooksPath);
        }
        else if (File.Exists(hooksPath))
        {
            File.Delete(hooksPath);
        }

        var configPath = SafePath.CombineUnderRoot(mirrorPath, "config");
        WriteAtomicFile(mirrorPath, configPath, CanonicalConfig(remote));
    }

    private void EnsureCacheDirectories()
    {
        Directory.CreateDirectory(_root);
        RequireRegularCacheDirectory(_root, _root);
        foreach (var directory in new[]
                 {
                     _mirrorsRoot,
                     _incomingRoot,
                     _locksRoot,
                     _metadataRoot
                 })
        {
            Directory.CreateDirectory(directory);
            SafePath.EnsureNoReparsePointsUnderRoot(_root, directory);
            RequireRegularCacheDirectory(_root, directory);
        }
    }

    private async Task DeleteOwnedPartialsAsync(
        string key,
        CancellationToken cancellationToken)
    {
        foreach (var partial in Directory.EnumerateDirectories(
                     _incomingRoot,
                     key + ".*" + PartialSuffix,
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryDeleteDirectoryAsync(partial, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DiscardDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var quarantine = SafePath.CombineUnderRoot(
            _incomingRoot,
            Path.GetFileName(path) + ".discarded-" + Guid.NewGuid().ToString("N"));
        Directory.Move(path, quarantine);
        await TryDeleteDirectoryAsync(quarantine, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryDeleteDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        var trustedRoot = SafePath.IsWithinRoot(_incomingRoot, path)
            ? _incomingRoot
            : _mirrorsRoot;
        await _deleteTreeAsync(trustedRoot, path, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task DeleteTreeAsync(
        string trustedRoot,
        string path,
        CancellationToken cancellationToken)
        => SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
            trustedRoot,
            path,
            cancellationToken);

    private void TouchAccessMarker(string key)
    {
        var path = SafePath.CombineUnderRoot(_metadataRoot, key + ".access");
        var bytes = Encoding.ASCII.GetBytes(
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\n");
        WriteAtomicFile(_metadataRoot, path, bytes);
    }

    private static byte[] CanonicalConfig(Uri remote)
        => Encoding.ASCII.GetBytes(
            "[core]\n"
            + "\trepositoryformatversion = 0\n"
            + "\tfilemode = false\n"
            + "\tbare = true\n"
            + "[remote \"origin\"]\n"
            + $"\turl = {remote.AbsoluteUri}\n"
            + "\tfetch = +refs/*:refs/*\n"
            + "\tmirror = true\n");

    private static void WriteAtomicFile(string root, string destination, byte[] bytes)
    {
        var partial = SafePath.CombineUnderRoot(
            root,
            Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + PartialSuffix);
        try
        {
            using (var stream = new FileStream(
                partial,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(partial);
        }
    }

    private static long MeasureTree(string path, CancellationToken cancellationToken)
    {
        long total = 0;
        var scanned = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > MaximumScannedEntries)
                {
                    throw new InvalidDataException(
                        "BuildTools source mirror 項目數超過安全上限。");
                }

                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException(
                        "BuildTools source mirror 含 reparse point。");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                    continue;
                }

                total = checked(total + new FileInfo(entry).Length);
            }
        }

        return total;
    }

    private static void RequireSingleLine(
        ModrinthLoaderBootstrapProcessResult result,
        string expected,
        string context)
    {
        if (result.OutputTruncated
            || result.StandardError.Count != 0
            || result.StandardOutput.Count != 1
            || !result.StandardOutput[0].Equals(expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"BuildTools source mirror {context} 驗證失敗。");
        }
    }

    private static void RequireSafeRemote(Uri remote)
    {
        if (!remote.IsAbsoluteUri
            || !remote.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(remote.UserInfo)
            || !remote.Host.Equals("hub.spigotmc.org", StringComparison.OrdinalIgnoreCase)
            || !remote.IsDefaultPort
            || !string.IsNullOrEmpty(remote.Query)
            || !string.IsNullOrEmpty(remote.Fragment))
        {
            throw new InvalidDataException(
                "BuildTools source mirror 只接受無憑證的 Spigot 官方 HTTPS upstream。");
        }
    }

    private static void RequireCommit(string expectedCommit)
    {
        if (expectedCommit.Length != 40
            || expectedCommit.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("BuildTools source mirror commit 格式無效。");
        }
    }

    private static void RequireRegularCacheDirectory(string root, string path)
    {
        var normalized = SafePath.EnsureNoReparsePointsUnderRoot(root, path);
        var attributes = File.GetAttributes(normalized);
        if (!attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                $"BuildTools cache 路徑不是一般資料夾：{normalized}");
        }
    }

    private static void RequireRegularCacheFile(
        string root,
        string path,
        long maximumBytes)
    {
        var normalized = SafePath.EnsureNoReparsePointsUnderRoot(root, path);
        var attributes = File.GetAttributes(normalized);
        var length = new FileInfo(normalized).Length;
        if (attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint)
            || length > maximumBytes)
        {
            throw new InvalidDataException(
                $"BuildTools cache 檔案類型或大小無效：{normalized}");
        }
    }

    private async Task<FileStream> AcquireLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(_options.LockRetryDelay, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static FileStream? TryAcquireLock(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsInvalidCacheException(Exception exception)
        => exception is InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or OverflowException;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A stale metadata/partial file is harmless and will be retried on the next use.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the primary cache operation result; later validation remains fail closed.
        }
    }

    private sealed record CacheEntry(
        string Key,
        string Path,
        string AccessMarkerPath,
        DateTime AccessedUtc,
        long Size);
}

internal sealed class SpigotBuildToolsSourceMirrorLease(
    string mirrorPath,
    FileStream cacheLock) : IDisposable
{
    private FileStream? _cacheLock = cacheLock;

    public string MirrorPath { get; } = Path.GetFullPath(mirrorPath);

    public void Dispose()
    {
        Interlocked.Exchange(ref _cacheLock, null)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
