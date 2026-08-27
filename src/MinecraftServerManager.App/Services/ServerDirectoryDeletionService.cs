using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

internal sealed class ServerDirectoryDeletionService
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly ApplicationPaths _paths;

    public ServerDirectoryDeletionService(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    public async Task DeleteAsync(
        string directoryPath,
        IEnumerable<string> otherManagedServerDirectories,
        SafePathObjectIdentity expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        var target = ValidateDeletionTarget(directoryPath, otherManagedServerDirectories);
        var trustedBoundary = ResolveTrustedBoundary(target);
        var protectedIdentities = CaptureProtectedManagedIdentities(
            otherManagedServerDirectories);

        // Re-run the complete validation immediately before entering the no-follow deleter. If
        // the target itself was replaced by a junction while a process was stopping, it is
        // rejected here; links encountered below the owned root are removed without traversal.
        target = ValidateDeletionTarget(target, otherManagedServerDirectories);
        ValidateNoRedirectingIntermediates(trustedBoundary, target);
        await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
            trustedBoundary,
            target,
            expectedIdentity,
            protectedIdentities,
            cancellationToken);

        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"Server 資料夾未能完整刪除：{target}");
        }
    }

    internal string ValidateDeletionTarget(
        string directoryPath,
        IEnumerable<string> otherManagedServerDirectories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(otherManagedServerDirectories);

        RejectDeviceSyntax(directoryPath);
        ValidateNoRedirectingIntermediates(
            ResolveLexicalTrustedBoundary(directoryPath),
            Normalize(directoryPath));
        var target = CanonicalizeExisting(directoryPath);
        var volumeRoot = Path.GetPathRoot(target);
        if (string.IsNullOrWhiteSpace(volumeRoot)
            || PathsEqual(target, volumeRoot))
        {
            throw new UnauthorizedAccessException("拒絕刪除磁碟根目錄。");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(target);
        }
        catch (FileNotFoundException exception)
        {
            throw new DirectoryNotFoundException($"找不到 Server 資料夾：{target}", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new DirectoryNotFoundException($"找不到 Server 資料夾：{target}", exception);
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new UnauthorizedAccessException($"記錄路徑不是 Server 資料夾：{target}");
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                $"拒絕完全刪除 symbolic link、junction 或其他 reparse point 根目錄：{target}");
        }

        foreach (var protectedRoot in EnumerateProtectedRoots())
        {
            if (IsSameOrAncestor(target, protectedRoot))
            {
                throw new UnauthorizedAccessException(
                    $"拒絕刪除系統、使用者或管理器的重要根目錄：{target}");
            }
        }

        foreach (var forbiddenRoot in EnumerateForbiddenSubtreeRoots())
        {
            if (IsSameOrAncestor(forbiddenRoot, target))
            {
                throw new UnauthorizedAccessException(
                    $"拒絕刪除 Windows、Program Files 或 ProgramData 的任何子目錄：{target}");
            }
        }

        foreach (var managedDirectory in otherManagedServerDirectories)
        {
            if (string.IsNullOrWhiteSpace(managedDirectory))
            {
                continue;
            }

            RejectDeviceSyntax(managedDirectory);
            string other;
            try
            {
                other = CanonicalizeExisting(managedDirectory);
            }
            catch (DirectoryNotFoundException)
            {
                other = Normalize(managedDirectory);
            }
            catch (FileNotFoundException)
            {
                other = Normalize(managedDirectory);
            }
            if (IsSameOrAncestor(target, other)
                || IsSameOrAncestor(other, target))
            {
                throw new UnauthorizedAccessException(
                    $"拒絕刪除與另一個受管理 Server 路徑重疊的資料夾：{target}");
            }
        }

        return target;
    }

    internal SafePathObjectIdentityLease CaptureDeletionIdentity(string directoryPath)
    {
        RejectDeviceSyntax(directoryPath);
        ValidateNoRedirectingIntermediates(
            ResolveLexicalTrustedBoundary(directoryPath),
            Normalize(directoryPath));
        var target = CanonicalizeExisting(directoryPath);
        var attributes = File.GetAttributes(target);
        if (!attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                $"永久刪除目標不可是 symbolic link、junction 或其他 reparse point：{target}");
        }

        return SafePath.CaptureExistingObjectIdentityLease(target);
    }

    private string ResolveTrustedBoundary(string target)
    {
        // A configured application/user cloud folder is an explicit trust boundary. Below that
        // boundary SafePath checks every intermediate component, so a forged record cannot place
        // the Server behind a junction and make deletion escape to another tree. The boundary
        // itself remains trusted to preserve legitimate portable installs under OneDrive.
        var configuredBoundaries = new[]
        {
            _paths.Root,
            Environment.GetEnvironmentVariable("OneDrive"),
            Environment.GetEnvironmentVariable("OneDriveConsumer"),
            Environment.GetEnvironmentVariable("OneDriveCommercial"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        foreach (var candidate in configuredBoundaries
                     .Where(static path => !string.IsNullOrWhiteSpace(path))
                     .SelectMany(static path => EnumerateCanonicalExistingAliases(path!))
                     .Distinct(PathComparer.Instance)
                     .OrderByDescending(static path => path.Length))
        {
            if (!PathsEqual(candidate, target) && IsSameOrAncestor(candidate, target))
            {
                return candidate;
            }
        }

        var volumeRoot = Path.GetPathRoot(target);
        if (string.IsNullOrWhiteSpace(volumeRoot) || PathsEqual(volumeRoot, target))
        {
            throw new UnauthorizedAccessException("拒絕刪除沒有安全信任邊界的 Server 路徑。");
        }

        return Normalize(volumeRoot);
    }

    private string ResolveLexicalTrustedBoundary(string directoryPath)
    {
        var target = Normalize(directoryPath);
        var configuredBoundaries = new[]
        {
            _paths.Root,
            Environment.GetEnvironmentVariable("OneDrive"),
            Environment.GetEnvironmentVariable("OneDriveConsumer"),
            Environment.GetEnvironmentVariable("OneDriveCommercial"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        var boundary = configuredBoundaries
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Normalize(path!))
            .Where(candidate => !PathsEqual(candidate, target)
                && IsSameOrAncestor(candidate, target))
            .OrderByDescending(static path => path.Length)
            .FirstOrDefault();
        if (boundary is not null)
        {
            return boundary;
        }

        var volumeRoot = Path.GetPathRoot(target);
        if (string.IsNullOrWhiteSpace(volumeRoot) || PathsEqual(volumeRoot, target))
        {
            throw new UnauthorizedAccessException("拒絕刪除沒有安全信任邊界的 Server 路徑。");
        }

        return Normalize(volumeRoot);
    }

    private static void ValidateNoRedirectingIntermediates(
        string trustedBoundary,
        string target)
    {
        var relative = Path.GetRelativePath(trustedBoundary, target);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = trustedBoundary;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException(
                    $"拒絕透過 redirecting directory 刪除 Server：{current}");
            }
        }
    }

    private IEnumerable<string> EnumerateProtectedRoots()
    {
        var candidates = new[]
        {
            _paths.Root,
            _paths.Servers,
            _paths.Runtimes,
            _paths.Backups,
            _paths.Cache,
            _paths.Themes,
            _paths.Logs,
            _paths.CrashReports,
            _paths.RecoveryPoints,
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads"),
            Environment.GetEnvironmentVariable("OneDrive"),
            Environment.GetEnvironmentVariable("OneDriveConsumer"),
            Environment.GetEnvironmentVariable("OneDriveCommercial")
        };

        return candidates
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .SelectMany(static path => EnumerateCanonicalExistingAliases(path!))
            .Distinct(PathComparer.Instance);
    }

    private static IEnumerable<string> EnumerateForbiddenSubtreeRoots()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        return candidates
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .SelectMany(static path => EnumerateCanonicalExistingAliases(path!))
            .Distinct(PathComparer.Instance);
    }

    private static IReadOnlySet<SafePathObjectIdentity> CaptureProtectedManagedIdentities(
        IEnumerable<string> managedDirectories)
    {
        var identities = new HashSet<SafePathObjectIdentity>();
        foreach (var directory in managedDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            RejectDeviceSyntax(directory);
            try
            {
                identities.Add(SafePath.GetExistingObjectIdentity(
                    CanonicalizeExisting(directory)));
            }
            catch (FileNotFoundException)
            {
                // A missing stale management record cannot be moved into the target tree.
            }
            catch (DirectoryNotFoundException)
            {
                // A missing stale management record cannot be moved into the target tree.
            }
        }

        return identities;
    }

    private static bool IsSameOrAncestor(string possibleAncestor, string path)
    {
        if (PathsEqual(possibleAncestor, path))
        {
            return true;
        }

        var prefix = possibleAncestor.EndsWith(Path.DirectorySeparatorChar)
            || possibleAncestor.EndsWith(Path.AltDirectorySeparatorChar)
                ? possibleAncestor
                : possibleAncestor + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Normalize(left), Normalize(right), PathComparison);

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string CanonicalizeExisting(string path)
        => SafePath.GetCanonicalExistingPath(path);

    private static IEnumerable<string> EnumerateCanonicalExistingAliases(string path)
    {
        RejectDeviceSyntax(path);
        string linkIdentity;
        try
        {
            linkIdentity = SafePath.GetCanonicalExistingPath(
                path,
                followFinalReparsePoint: false);
        }
        catch (FileNotFoundException)
        {
            yield break;
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }

        yield return linkIdentity;
        var resolvedIdentity = SafePath.GetCanonicalExistingPath(
            path,
            followFinalReparsePoint: true);
        if (!PathsEqual(linkIdentity, resolvedIdentity))
        {
            yield return resolvedIdentity;
        }
    }

    private static void RejectDeviceSyntax(string path)
    {
        var normalizedPrefix = path.TrimStart().Replace('/', '\\');
        if (normalizedPrefix.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            || normalizedPrefix.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)
            || normalizedPrefix.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"拒絕以 extended 或 device path 執行永久刪除：{path}");
        }
    }

    private sealed class PathComparer : IEqualityComparer<string>
    {
        public static PathComparer Instance { get; } = new();

        public bool Equals(string? left, string? right)
            => left is not null && right is not null && PathsEqual(left, right);

        public int GetHashCode(string path)
            => (OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path).GetHashCode();
    }
}
