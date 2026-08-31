using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MinecraftServerManager.Core.Services;

public readonly record struct SafePathObjectIdentity(
    ulong VolumeSerialNumber,
    Guid FileId);

public sealed class SafePathObjectIdentityLease : IDisposable
{
    private SafeFileHandle? _handle;

    internal SafePathObjectIdentityLease(
        SafeFileHandle handle,
        SafePathObjectIdentity identity)
    {
        _handle = handle;
        Identity = identity;
    }

    public SafePathObjectIdentity Identity { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _handle, null)?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Holds no-follow handles for a trusted directory boundary and every descendant directory in a
/// resolved chain. On Windows the handles deny delete sharing, preventing the validated objects
/// from being renamed or replaced until the lease is disposed.
/// </summary>
public sealed class SafePathDirectoryChainLease : IDisposable
{
    private List<SafeFileHandle>? _handles;

    internal SafePathDirectoryChainLease(List<SafeFileHandle> handles)
    {
        _handles = handles;
    }

    public void Dispose()
    {
        var handles = Interlocked.Exchange(ref _handles, null);
        if (handles is null)
        {
            return;
        }

        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }
    }
}

public sealed class SafePathExclusiveFileLease : IDisposable
{
    private IDisposable? _handle;

    internal SafePathExclusiveFileLease(IDisposable handle)
    {
        _handle = handle;
    }

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
}

/// <summary>Windows-safe instance naming and root-containment helpers.</summary>
public static class SafePath
{
    private static readonly SearchValues<char> InvalidNameCharacters =
        SearchValues.Create("<>:\"/\\|?*");

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string SanitizeFileName(
        string? value,
        string fallback = "server",
        int maxLength = 80)
    {
        if (maxLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "Maximum length must be positive.");
        }

        var cleaned = CleanName(value);
        if (cleaned.Length == 0)
        {
            cleaned = CleanName(fallback);
        }

        if (cleaned.Length == 0)
        {
            cleaned = "server";
        }

        var baseName = cleaned.Split('.', 2)[0];
        if (ReservedDeviceNames.Contains(baseName))
        {
            cleaned = $"_{cleaned}";
        }

        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned[..maxLength].TrimEnd(' ', '.');
        }

        return cleaned.Length == 0 ? "_" : cleaned;
    }

    public static string CombineUnderRoot(string rootPath, params string[] relativeSegments)
    {
        ArgumentNullException.ThrowIfNull(relativeSegments);
        var current = Path.GetFullPath(rootPath);

        foreach (var segment in relativeSegments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
            if (Path.IsPathRooted(segment))
            {
                throw new ArgumentException("Path segments must be relative.", nameof(relativeSegments));
            }

            current = Path.Combine(current, segment);
        }

        return EnsureWithinRoot(rootPath, current);
    }

    public static string EnsureWithinRoot(
        string rootPath,
        string candidatePath,
        bool allowRoot = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        var root = Path.GetFullPath(rootPath);
        var candidate = Path.GetFullPath(
            Path.IsPathRooted(candidatePath)
                ? candidatePath
                : Path.Combine(root, candidatePath));

        if (!IsWithinRoot(root, candidate) ||
            (!allowRoot && PathsEqual(root, candidate)))
        {
            throw new UnauthorizedAccessException(
                $"The path '{candidate}' is outside the permitted root '{root}'.");
        }

        return candidate;
    }

    public static bool IsWithinRoot(string rootPath, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(root, candidate, comparison))
        {
            return true;
        }

        var rootPrefix = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootPrefix, comparison);
    }

    /// <summary>
    /// Rejects a root-confined path when the root, an intermediate directory, or the candidate
    /// itself is a symbolic link, junction, or other reparse point. Lexical root containment on
    /// its own is insufficient because an intermediate junction can redirect file access outside
    /// the apparent root.
    /// </summary>
    /// <returns>The normalized, root-confined candidate path.</returns>
    public static string EnsureNoReparsePointsUnderRoot(string rootPath, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = EnsureWithinRoot(root, candidatePath);

        EnsureNotReparsePoint(root);
        if (PathsEqual(root, candidate))
        {
            return candidate;
        }

        var relativePath = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureNotReparsePoint(current);
        }

        return candidate;

        static void EnsureNotReparsePoint(string path)
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException(
                    $"Rejected path because it contains a reparse point: '{path}'.");
            }
        }
    }

    /// <summary>
    /// Atomically validates and retains a no-follow handle chain from <paramref name="rootPath"/>
    /// through <paramref name="candidatePath"/>. Every object must be a real directory rather
    /// than a junction, symbolic link, mount point, or other reparse point.
    /// </summary>
    public static SafePathDirectoryChainLease AcquireNoReparseDirectoryChainLease(
        string rootPath,
        string candidatePath)
        => AcquireNoReparseDirectoryChainLease(rootPath, candidatePath, OperatingSystem.IsWindows());

    internal static SafePathDirectoryChainLease AcquireNoReparseDirectoryChainLease(
        string rootPath,
        string candidatePath,
        bool isWindows)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = EnsureWithinRoot(root, candidatePath, allowRoot: false);
        if (!isWindows)
        {
            throw new PlatformNotSupportedException(
                "Atomic no-follow directory-chain leases are supported only on Windows.");
        }

        var relativePath = Path.GetRelativePath(root, candidate);
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var handles = new List<SafeFileHandle>(segments.Length + 1);
        try
        {
            OpenAndRetainDirectory(root, handles);
            var current = root;
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                OpenAndRetainDirectory(current, handles);
            }

            return new SafePathDirectoryChainLease(handles);
        }
        catch
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }

            throw;
        }

        static void OpenAndRetainDirectory(string path, List<SafeFileHandle> destination)
        {
            var handle = OpenWindowsPathHandle(
                path,
                WindowsFileAccess.ReadAttributes,
                FileShare.Read | FileShare.Write,
                openReparsePoint: true);
            try
            {
                var attributes = GetWindowsHandleAttributes(handle);
                if (!attributes.HasFlag(FileAttributes.Directory) ||
                    attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UnauthorizedAccessException(
                        $"Rejected directory lease because the path is not a direct directory: '{path}'.");
                }

                destination.Add(handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Creates or opens one file without following a reparse point and retains an exclusive
    /// cross-process handle. The parent directory must already be protected by the caller.
    /// </summary>
    public static SafePathExclusiveFileLease AcquireNoFollowExclusiveFileLease(string filePath)
        => AcquireNoFollowExclusiveFileLease(filePath, OperatingSystem.IsWindows());

    internal static SafePathExclusiveFileLease AcquireNoFollowExclusiveFileLease(
        string filePath,
        bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        if (!isWindows)
        {
            throw new PlatformNotSupportedException(
                "Atomic no-follow exclusive file leases are supported only on Windows.");
        }

        var handle = OpenWindowsPathHandle(
            fullPath,
            WindowsFileAccess.GenericRead
                | WindowsFileAccess.GenericWrite
                | WindowsFileAccess.ReadAttributes,
            FileShare.None,
            openReparsePoint: true,
            creationDisposition: FileMode.OpenOrCreate);
        try
        {
            var attributes = GetWindowsHandleAttributes(handle);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException(
                    $"Rejected exclusive file lease because the path is redirected: '{fullPath}'.");
            }

            return new SafePathExclusiveFileLease(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Returns a non-existing, sanitized directory path without creating it.</summary>
    public static string CreateUniqueDirectoryPath(string rootPath, string preferredName)
    {
        var safeName = SanitizeFileName(preferredName);
        var root = Path.GetFullPath(rootPath);
        var candidate = CombineUnderRoot(root, safeName);

        for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
        {
            candidate = CombineUnderRoot(root, $"{safeName}-{suffix}");
        }

        return candidate;
    }

    /// <summary>
    /// Deletes an owned descendant tree without traversing a symbolic link, junction, mount point,
    /// or other reparse point. The trusted root itself is a caller-declared boundary (which keeps
    /// portable installs below OneDrive working); redirecting descendants between it and the owned
    /// target are rejected.
    /// </summary>
    public static void DeleteTreeWithoutFollowingReparsePoints(
        string trustedRootPath,
        string ownedPath)
        => DeleteTreeWithoutFollowingReparsePoints(
            trustedRootPath,
            ownedPath,
            afterDirectoryLockedForTesting: null);

    internal static void DeleteTreeWithoutFollowingReparsePoints(
        string trustedRootPath,
        string ownedPath,
        Action<string>? afterDirectoryLockedForTesting)
        => DeleteTreeWithoutFollowingReparsePoints(
            trustedRootPath,
            ownedPath,
            afterDirectoryLockedForTesting,
            expectedRootIdentity: null,
            protectedObjectIdentities: null);

    private static void DeleteTreeWithoutFollowingReparsePoints(
        string trustedRootPath,
        string ownedPath,
        Action<string>? afterDirectoryLockedForTesting,
        SafePathObjectIdentity? expectedRootIdentity,
        IReadOnlySet<SafePathObjectIdentity>? protectedObjectIdentities)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRootPath));
        var target = EnsureWithinRoot(root, ownedPath, allowRoot: false);

        if (OperatingSystem.IsWindows())
        {
            WindowsNoFollowDeleteTree(
                root,
                target,
                afterDirectoryLockedForTesting,
                expectedRootIdentity,
                protectedObjectIdentities);
            return;
        }

        var relative = Path.GetRelativePath(root, target);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (TryGetAttributes(current, out var attributes)
                && attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException(
                    $"Refused cleanup through redirecting directory: '{current}'.");
            }
        }

        DeleteNode(target);

        static void DeleteNode(string path)
        {
            if (!TryGetAttributes(path, out var attributes))
            {
                return;
            }

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
                ClearReadOnly(path, attributes);
                File.Delete(path);
                return;
            }

            foreach (var child in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                DeleteNode(child.FullName);
            }

            ClearReadOnly(path, attributes);
            Directory.Delete(path, recursive: false);
        }

        static void ClearReadOnly(string path, FileAttributes attributes)
        {
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    /// <summary>
    /// Resolves the identity of an existing Windows path through an open handle. This collapses
    /// extended-path and 8.3 aliases before an irreversible operation compares protected roots.
    /// Reparse points are opened themselves rather than followed.
    /// </summary>
    public static string GetCanonicalExistingPath(
        string path,
        bool followFinalReparsePoint = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        using var handle = OpenExistingWindowsPathForIdentity(
            fullPath,
            openReparsePoint: !followFinalReparsePoint);
        var requiredLength = GetFinalPathNameByHandleW(handle, null, 0, 0);
        if (requiredLength == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var buffer = new char[checked((int)requiredLength + 1)];
        var written = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (written == 0 || written >= buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var resolved = new string(buffer, 0, checked((int)written));
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (resolved.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            resolved = @"\\" + resolved[extendedUncPrefix.Length..];
        }
        else if (resolved.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            resolved = resolved[extendedPrefix.Length..];
        }

        if (!Path.IsPathFullyQualified(resolved)
            || resolved.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)
            || resolved.StartsWith("GLOBALROOT", StringComparison.OrdinalIgnoreCase)
            || resolved.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Refused a device path that cannot be reduced to a DOS or UNC identity: '{path}'.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
    }

    public static SafePathObjectIdentity GetExistingObjectIdentity(string path)
    {
        using var lease = CaptureExistingObjectIdentityLease(path);
        return lease.Identity;
    }

    public static SafePathObjectIdentityLease CaptureExistingObjectIdentityLease(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Stable filesystem object identities are currently available only on Windows.");
        }

        var handle = OpenExistingWindowsPathForIdentity(
            Path.GetFullPath(path),
            openReparsePoint: true);
        try
        {
            return new SafePathObjectIdentityLease(
                handle,
                GetWindowsHandleIdentity(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens one existing regular file without following a reparse point and captures its stable
    /// filesystem identity while permitting legacy readers and in-place writers. Callers must
    /// compare the identity again after an external process and use identity-bound cleanup.
    /// </summary>
    public static SafePathObjectIdentityLease AcquireNoFollowFileIdentityLease(
        string path,
        SafePathObjectIdentity? expectedIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "No-follow file identity leases are currently available only on Windows.");
        }

        var fullPath = Path.GetFullPath(path);
        var handle = OpenWindowsPathHandle(
            fullPath,
            WindowsFileAccess.ReadAttributes,
            FileShare.Read | FileShare.Write,
            openReparsePoint: true);
        try
        {
            var attributes = GetWindowsHandleAttributes(handle);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException(
                    $"Rejected file lease because the path is not a direct regular file: '{fullPath}'.");
            }

            var identity = GetWindowsHandleIdentity(handle);
            if (expectedIdentity is { } expected && identity != expected)
            {
                throw new SafePathSecurityException(
                    $"Rejected file lease because its filesystem identity changed: '{fullPath}'.");
            }

            return new SafePathObjectIdentityLease(handle, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenExistingWindowsPathForIdentity(
        string path,
        bool openReparsePoint)
    {
        try
        {
            return OpenWindowsPathHandle(
                path,
                WindowsFileAccess.ReadAttributes,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                openReparsePoint);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorFileNotFound)
        {
            throw new FileNotFoundException("Filesystem object does not exist.", path, exception);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorPathNotFound)
        {
            throw new DirectoryNotFoundException(
                $"Filesystem path does not exist: '{path}'.",
                exception);
        }
    }

    /// <summary>
    /// Removes one manager-owned file or directory tree without following reparse points, retrying
    /// only Windows sharing/access failures for a bounded time. This is intended for cancellation
    /// cleanup after a child process has been killed: antivirus or a just-exited Git/Java process
    /// may retain a handle briefly, but an incomplete installation must not be reported as cleaned
    /// while its files are still present.
    /// </summary>
    public static async Task DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
        string trustedRootPath,
        string ownedPath,
        CancellationToken cancellationToken = default)
        => await DeleteTreeWithoutFollowingReparsePointsWithRetryCoreAsync(
                trustedRootPath,
                ownedPath,
                expectedRootIdentity: null,
                protectedObjectIdentities: null,
                cancellationToken)
            .ConfigureAwait(false);

    public static async Task DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
        string trustedRootPath,
        string ownedPath,
        SafePathObjectIdentity expectedRootIdentity,
        IReadOnlySet<SafePathObjectIdentity>? protectedObjectIdentities = null,
        CancellationToken cancellationToken = default)
        => await DeleteTreeWithoutFollowingReparsePointsWithRetryCoreAsync(
                trustedRootPath,
                ownedPath,
                expectedRootIdentity,
                protectedObjectIdentities,
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task DeleteTreeWithoutFollowingReparsePointsWithRetryCoreAsync(
        string trustedRootPath,
        string ownedPath,
        SafePathObjectIdentity? expectedRootIdentity,
        IReadOnlySet<SafePathObjectIdentity>? protectedObjectIdentities,
        CancellationToken cancellationToken)
    {
        // Antivirus and freshly exited Java/Maven/Git processes can retain Windows handles well
        // beyond a few hundred milliseconds. Keep this bounded, but give successful/cancelled
        // BuildTools cleanup a real 30-second release window: initial attempt, then 2/4/8/16s.
        TimeSpan[] retryDelays =
        [
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16)
        ];
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DeleteTreeWithoutFollowingReparsePoints(
                    trustedRootPath,
                    ownedPath,
                    afterDirectoryLockedForTesting: null,
                    expectedRootIdentity,
                    protectedObjectIdentities);
                return;
            }
            catch (Exception exception) when (
                attempt < retryDelays.Length
                && IsTransientCleanupFailure(exception))
            {
                await Task.Delay(retryDelays[attempt], cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientCleanupFailure(Exception exception)
        => exception is SafePathSecurityException
            ? false
            : exception is Win32Exception win32
            ? win32.NativeErrorCode is 5 or 32 or 33 or 145
            : exception is IOException or UnauthorizedAccessException
                && exception.HResult is unchecked((int)0x80070005) // ERROR_ACCESS_DENIED
                    or unchecked((int)0x80070020) // ERROR_SHARING_VIOLATION
                    or unchecked((int)0x80070021) // ERROR_LOCK_VIOLATION
                    or unchecked((int)0x80070091); // ERROR_DIR_NOT_EMPTY

    /// <summary>
    /// Statically verifies that an installation tree contains no symbolic links, junctions, mount
    /// points, or other reparse points before the tree is promoted out of staging. Enumeration is
    /// bounded so an untrusted installer cannot make validation consume unbounded resources.
    /// </summary>
    public static void EnsureTreeContainsNoReparsePoints(
        string treeRootPath,
        int maximumEntries = 500_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRootPath);
        if (maximumEntries < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEntries),
                "Maximum entry count must be positive.");
        }

        var root = Path.GetFullPath(treeRootPath);
        var rootAttributes = File.GetAttributes(root);
        if (!rootAttributes.HasFlag(FileAttributes.Directory))
        {
            throw new IOException($"Installation tree is not a directory: '{root}'.");
        }

        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                $"Rejected installation tree because its root is a reparse point: '{root}'.");
        }

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        var entryCount = 0;

        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                entryCount++;
                if (entryCount > maximumEntries)
                {
                    throw new InvalidDataException(
                        $"Installation tree exceeds the {maximumEntries:N0} entry safety limit.");
                }

                var attributes = entry.Attributes;
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UnauthorizedAccessException(
                        $"Rejected installation tree because it contains a reparse point: '{entry.FullName}'.");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push((DirectoryInfo)entry);
                }
            }
        }
    }

    private static void WindowsNoFollowDeleteTree(
        string trustedRoot,
        string target,
        Action<string>? afterDirectoryLockedForTesting,
        SafePathObjectIdentity? expectedRootIdentity,
        IReadOnlySet<SafePathObjectIdentity>? protectedObjectIdentities)
    {
        var relative = Path.GetRelativePath(trustedRoot, target);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new UnauthorizedAccessException("Refused to delete the trusted root itself.");
        }

        var ancestorHandles = new List<SafeFileHandle>(segments.Length);
        try
        {
            // The caller may deliberately use a OneDrive/junction boundary. Lock both the link
            // object and the resolved directory so neither side can be renamed, rewritten, or
            // deleted while descendant paths are resolved.
            var boundaryLinkHandle = OpenWindowsPathHandle(
                trustedRoot,
                WindowsFileAccess.ReadAttributes,
                FileShare.Read,
                openReparsePoint: true);
            var boundaryAttributes = GetWindowsHandleAttributes(boundaryLinkHandle);
            if (!boundaryAttributes.HasFlag(FileAttributes.Directory))
            {
                boundaryLinkHandle.Dispose();
                throw new UnauthorizedAccessException(
                    $"Trusted cleanup boundary is not a directory: '{trustedRoot}'.");
            }

            ancestorHandles.Add(boundaryLinkHandle);
            var boundaryTargetHandle = OpenWindowsPathHandle(
                trustedRoot,
                WindowsFileAccess.ReadAttributes,
                FileShare.Read,
                openReparsePoint: false);
            if (!GetWindowsHandleAttributes(boundaryTargetHandle)
                    .HasFlag(FileAttributes.Directory))
            {
                boundaryTargetHandle.Dispose();
                throw new UnauthorizedAccessException(
                    $"Resolved cleanup boundary is not a directory: '{trustedRoot}'.");
            }

            ancestorHandles.Add(boundaryTargetHandle);
            var current = trustedRoot;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                current = Path.Combine(current, segments[index]);
                var handle = OpenWindowsPathHandle(
                    current,
                    WindowsFileAccess.Delete
                        | WindowsFileAccess.ReadAttributes
                        | WindowsFileAccess.WriteAttributes,
                    FileShare.Read);
                var attributes = GetWindowsHandleAttributes(handle);
                if (!attributes.HasFlag(FileAttributes.Directory)
                    || attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    handle.Dispose();
                    throw new UnauthorizedAccessException(
                        $"Refused cleanup through redirecting directory: '{current}'.");
                }

                ancestorHandles.Add(handle);
                afterDirectoryLockedForTesting?.Invoke(current);
            }

            // A concurrently created replacement cannot be accepted as success. Keep the parent
            // chain locked and make a bounded number of passes until the exact leaf stays absent.
            for (var pass = 0; pass < 4; pass++)
            {
                DeleteWindowsNode(
                    target,
                    afterDirectoryLockedForTesting,
                    expectedRootIdentity,
                    protectedObjectIdentities,
                    isRoot: true);
                if (!TryGetAttributes(target, out _))
                {
                    return;
                }
            }

            throw new IOException(
                $"Owned path was recreated while it was being deleted: '{target}'.");
        }
        finally
        {
            for (var index = ancestorHandles.Count - 1; index >= 0; index--)
            {
                ancestorHandles[index].Dispose();
            }
        }
    }

    private static void DeleteWindowsNode(
        string path,
        Action<string>? afterDirectoryLockedForTesting,
        SafePathObjectIdentity? expectedRootIdentity,
        IReadOnlySet<SafePathObjectIdentity>? protectedObjectIdentities,
        bool isRoot)
    {
        SafeFileHandle handle;
        try
        {
            handle = OpenWindowsPathHandle(
                path,
                WindowsFileAccess.Delete
                    | WindowsFileAccess.ReadAttributes
                    | WindowsFileAccess.WriteAttributes,
                FileShare.Read);
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            return;
        }

        using (handle)
        {
            var attributes = GetWindowsHandleAttributes(handle);
            var identity = GetWindowsHandleIdentity(handle);
            if (isRoot && expectedRootIdentity is { } expected && identity != expected)
            {
                throw new SafePathSecurityException(
                    $"Refused to delete a path whose filesystem identity changed after confirmation: '{path}'.");
            }

            if (protectedObjectIdentities?.Contains(identity) == true)
            {
                throw new SafePathSecurityException(
                    $"Refused to delete a protected managed filesystem object: '{path}'.");
            }

            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
            if (isDirectory && !isReparsePoint)
            {
                afterDirectoryLockedForTesting?.Invoke(path);
                foreach (var child in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    DeleteWindowsNode(
                        child.FullName,
                        afterDirectoryLockedForTesting,
                        expectedRootIdentity: null,
                        protectedObjectIdentities,
                        isRoot: false);
                }
            }

            SetWindowsDeleteDisposition(handle, attributes, path);
        }
    }

    private static SafeFileHandle OpenWindowsPathHandle(
        string path,
        WindowsFileAccess access,
        FileShare share,
        bool openReparsePoint = true,
        FileMode creationDisposition = FileMode.Open)
    {
        var flags = FileFlagsAndAttributes.BackupSemantics;
        if (openReparsePoint)
        {
            flags |= FileFlagsAndAttributes.OpenReparsePoint;
        }

        var handle = CreateFileW(
            ToExtendedWindowsPath(path),
            access,
            share,
            IntPtr.Zero,
            creationDisposition,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                $"Unable to open path without following links (Win32 {error}): '{path}'.");
        }

        return handle;
    }

    private static string ToExtendedWindowsPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    private static FileAttributes GetWindowsHandleAttributes(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out FileAttributeTagInfo information,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return information.FileAttributes;
    }

    private static SafePathObjectIdentity GetWindowsHandleIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out FileIdInfo information,
                (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new SafePathObjectIdentity(
            information.VolumeSerialNumber,
            information.FileId);
    }

    private static void SetWindowsDeleteDisposition(
        SafeFileHandle handle,
        FileAttributes attributes,
        string path)
    {
        var disposition = new FileDispositionInfoEx
        {
            Flags = FileDispositionFlags.Delete
                | FileDispositionFlags.PosixSemantics
                | FileDispositionFlags.IgnoreReadonlyAttribute
        };
        if (SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileDispositionInfoEx,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfoEx>()))
        {
            return;
        }

        var extendedError = Marshal.GetLastWin32Error();
        if (extendedError is not (ErrorInvalidParameter or ErrorInvalidFunction or ErrorNotSupported))
        {
            throw new Win32Exception(extendedError, $"Unable to delete owned path: '{path}'.");
        }

        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileInfoByHandleClass.FileBasicInfo,
                    out FileBasicInfo basicInformation,
                    (uint)Marshal.SizeOf<FileBasicInfo>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            basicInformation.FileAttributes &= ~FileAttributes.ReadOnly;
            if (!SetFileInformationByHandle(
                    handle,
                    FileInfoByHandleClass.FileBasicInfo,
                    ref basicInformation,
                    (uint)Marshal.SizeOf<FileBasicInfo>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        var legacyDisposition = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileDispositionInfo,
                ref legacyDisposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Unable to delete owned path: '{path}'.");
        }
    }

    [Flags]
    private enum WindowsFileAccess : uint
    {
        GenericRead = 0x80000000,
        GenericWrite = 0x40000000,
        ReadAttributes = 0x00000080,
        WriteAttributes = 0x00000100,
        Delete = 0x00010000
    }

    [Flags]
    private enum FileFlagsAndAttributes : uint
    {
        OpenReparsePoint = 0x00200000,
        BackupSemantics = 0x02000000
    }

    private enum FileInfoByHandleClass
    {
        FileBasicInfo = 0,
        FileDispositionInfo = 4,
        FileAttributeTagInfo = 9,
        FileIdInfo = 18,
        FileDispositionInfoEx = 21
    }

    [Flags]
    private enum FileDispositionFlags : uint
    {
        Delete = 0x00000001,
        PosixSemantics = 0x00000002,
        IgnoreReadonlyAttribute = 0x00000010
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public FileAttributes FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public FileAttributes FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfoEx
    {
        public FileDispositionFlags Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public Guid FileId;
    }

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        WindowsFileAccess desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        FileFlagsAndAttributes flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileBasicInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileIdInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfoEx fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        ref FileBasicInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        [Out] char[]? filePath,
        uint filePathLength,
        uint flags);

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private sealed class SafePathSecurityException(string message)
        : UnauthorizedAccessException(message);

    private static string CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            normalized = value;
        }

        var result = new StringBuilder(normalized.Length);
        var previousWasSpace = false;

        foreach (var character in normalized)
        {
            if (char.IsControl(character) || InvalidNameCharacters.Contains(character))
            {
                result.Append('_');
                previousWasSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                {
                    result.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            result.Append(character);
            previousWasSpace = false;
        }

        return result.ToString().Trim().TrimEnd('.');
    }

    private static bool PathsEqual(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(first),
            Path.TrimEndingDirectorySeparator(second),
            comparison);
    }
}
