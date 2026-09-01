using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MinecraftServerManager.Updater;

internal sealed record ProductRepairStagingIdentity(
    string InstallRoot,
    string LauncherRoot,
    string StagingRoot,
    string Version,
    string Nonce);

/// <summary>
/// Restricts local Service repair input to a protected, one-use copy below the managed install.
/// The interactive App creates this directory through Windows IFileOperation before launching the
/// copy of the signed Updater that lives inside it. Raw files from Documents are never consumed by
/// an elevated repair process.
/// </summary>
internal static class ProductRepairStagingPolicy
{
    private const string StagingPrefix = ".repair-staging-";
    private const int NonceLength = 32;
    private const int MaximumTreeEntries = ProductUpdateManifestParser.MaximumFiles * 3;

    // NTFS access-mask bits which can mutate an object or its security descriptor. Generic bits
    // must be checked explicitly because inherited CREATOR OWNER rules commonly use GENERIC_ALL.
    private const int DangerousAccessMask =
        0x00000002 | // FILE_WRITE_DATA / FILE_ADD_FILE
        0x00000004 | // FILE_APPEND_DATA / FILE_ADD_SUBDIRECTORY
        0x00000010 | // FILE_WRITE_EA
        0x00000040 | // FILE_DELETE_CHILD
        0x00000100 | // FILE_WRITE_ATTRIBUTES
        0x00010000 | // DELETE
        0x00040000 | // WRITE_DAC
        0x00080000 | // WRITE_OWNER
        0x10000000 | // GENERIC_ALL
        0x40000000;  // GENERIC_WRITE

    private static readonly SecurityIdentifier SystemSid =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstallerSid =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public static ProductRepairStagingIdentity ResolveBoundary(string releaseRoot, string installRoot)
    {
        var managedRoot = ProductGuiActivationBroker.ValidateInstallRoot(installRoot);
        var launcherRoot = Path.GetFullPath(Path.Combine(managedRoot, "launcher"));
        if (!Directory.Exists(launcherRoot) ||
            !string.Equals(
                Directory.GetParent(launcherRoot)?.FullName,
                managedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The managed launcher directory is missing or invalid.");
        }

        if (string.IsNullOrWhiteSpace(releaseRoot) || !Path.IsPathFullyQualified(releaseRoot))
        {
            throw new InvalidDataException("The repair staging root must be absolute.");
        }

        var stagingRoot = Path.GetFullPath(releaseRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (stagingRoot.StartsWith(@"\\", StringComparison.Ordinal) ||
            stagingRoot.IndexOf('"') >= 0 ||
            !Directory.Exists(stagingRoot) ||
            !string.Equals(
                Directory.GetParent(stagingRoot)?.FullName,
                launcherRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Repair input must be a direct staging child of the managed launcher directory.");
        }

        var name = Path.GetFileName(stagingRoot);
        if (!name.StartsWith(StagingPrefix, StringComparison.Ordinal) ||
            name.Length <= StagingPrefix.Length + NonceLength + 1)
        {
            throw new InvalidDataException("The repair staging directory name is invalid.");
        }

        var nonceSeparator = name.Length - NonceLength - 1;
        if (name[nonceSeparator] != '-')
        {
            throw new InvalidDataException("The repair staging nonce separator is invalid.");
        }

        var version = name[StagingPrefix.Length..nonceSeparator];
        var nonce = name[(nonceSeparator + 1)..];
        ProductUpdateManifestParser.ValidateVersion(version);
        if (nonce.Length != NonceLength || nonce.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The repair staging nonce is invalid.");
        }

        ProductActivationPathPolicy.RejectExistingReparsePoints(managedRoot);
        ProductActivationPathPolicy.RejectExistingReparsePoints(launcherRoot);
        ProductActivationPathPolicy.RejectExistingReparsePoints(stagingRoot);
        return new ProductRepairStagingIdentity(
            managedRoot,
            launcherRoot,
            stagingRoot,
            version,
            nonce.ToLowerInvariant());
    }

    public static void ValidateProtectedTree(
        ProductRepairStagingIdentity identity,
        Action<string, bool>? securityValidator = null,
        Func<string, uint>? hardLinkCountReader = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var resolved = ResolveBoundary(identity.StagingRoot, identity.InstallRoot);
        EnsureSameIdentity(identity, resolved);
        var validateSecurity = securityValidator ?? ValidateProtectedSecurity;
        var readLinks = hardLinkCountReader ?? ReadHardLinkCount;

        validateSecurity(resolved.InstallRoot, true);
        validateSecurity(resolved.LauncherRoot, true);
        validateSecurity(resolved.StagingRoot, true);

        var pending = new Stack<string>();
        pending.Push(resolved.StagingRoot);
        var observed = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++observed > MaximumTreeEntries)
                {
                    throw new InvalidDataException("The repair staging tree exceeds its bounded entry limit.");
                }

                EnsureWithinStaging(resolved.StagingRoot, path);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Repair staging cannot contain a reparse point.");
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                validateSecurity(path, isDirectory);
                if (isDirectory)
                {
                    pending.Push(path);
                }
                else if (readLinks(path) != 1)
                {
                    throw new InvalidDataException("Repair staging files must have exactly one hard link.");
                }
            }
        }
    }

    public static void ValidateVerifiedRelease(
        ProductRepairStagingIdentity identity,
        VerifiedProductLocalRelease release,
        bool requireRunningFromReleaseUpdater)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(release);
        if (!string.Equals(identity.StagingRoot, release.ReleaseRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.StagingRoot, release.Layout.VersionRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.Version, release.UpdateManifest.Version, StringComparison.Ordinal) ||
            !string.Equals(identity.Version, release.Layout.Version, StringComparison.Ordinal) ||
            !release.UpdateManifest.Files.Any(file => string.Equals(
                file.Path,
                ProductFormalUpdateManifestValidator.UpdaterEntryPoint,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The signed release is not bound to its protected repair staging identity.");
        }

        var expectedUpdater = ProductUpdatePath.ResolveUnderRoot(
            identity.StagingRoot,
            ProductFormalUpdateManifestValidator.UpdaterEntryPoint);
        if (!string.Equals(expectedUpdater, release.Layout.UpdaterPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The staged Updater path is not signed by the repair manifest.");
        }

        if (requireRunningFromReleaseUpdater)
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) ||
                !string.Equals(
                    Path.GetFullPath(processPath),
                    expectedUpdater,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Repair must execute the signed Updater from protected staging.");
            }
        }
    }

    internal static void ValidateProtectedSecurity(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected repair staging requires Windows ACLs.");
        }

        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
        ValidateSecurityDescriptor(security);
    }

    internal static void ValidateSecurityDescriptor(FileSystemSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new InvalidDataException("A protected repair object has no SID owner.");
        if (!IsTrustedOwnerOrWriter(owner))
        {
            throw new UnauthorizedAccessException(
                "Repair staging objects must be owned by Administrators, SYSTEM or TrustedInstaller.");
        }

        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null)
        {
            throw new UnauthorizedAccessException("Repair staging cannot have a null DACL.");
        }

        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                IsTrustedOwnerOrWriter(sid))
            {
                continue;
            }

            if (((int)rule.FileSystemRights & DangerousAccessMask) != 0)
            {
                throw new UnauthorizedAccessException(
                    "Repair staging grants mutation rights to an untrusted principal.");
            }
        }
    }

    internal static uint ReadHardLinkCount(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Repair hard-link inspection requires Windows.");
        }

        using var handle = CreateFileW(
            ToExtendedPath(path),
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Repair staging file identity could not be opened (Win32 {code}).",
                new Win32Exception(code));
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            var code = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Repair staging file identity could not be inspected (Win32 {code}).",
                new Win32Exception(code));
        }

        if (((FileAttributes)information.FileAttributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException("Repair staging payload identity is invalid.");
        }

        return information.NumberOfLinks;
    }

    private static bool IsTrustedOwnerOrWriter(SecurityIdentifier sid)
        => sid.Equals(SystemSid) || sid.Equals(AdministratorsSid) || sid.Equals(TrustedInstallerSid);

    private static void EnsureSameIdentity(
        ProductRepairStagingIdentity expected,
        ProductRepairStagingIdentity actual)
    {
        if (!string.Equals(expected.InstallRoot, actual.InstallRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.LauncherRoot, actual.LauncherRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.StagingRoot, actual.StagingRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.Version, actual.Version, StringComparison.Ordinal) ||
            !string.Equals(expected.Nonce, actual.Nonce, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Repair staging identity changed during validation.");
        }
    }

    private static void EnsureWithinStaging(string stagingRoot, string path)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot)) +
                     Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Repair staging entry escaped its protected root.");
        }
    }

    private static string ToExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? full
            : @"\\?\" + full;
    }

    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);
}

internal static class ProductRepairStagingCleanup
{
    private const uint MoveFileDelayUntilReboot = 0x00000004;
    private const int MaximumTreeEntries = ProductUpdateManifestParser.MaximumFiles * 3;

    public static bool Schedule(
        ProductRepairStagingIdentity identity,
        Func<string, bool>? deletionScheduler = null,
        Action<string, bool>? securityValidator = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Directory.Exists(identity.StagingRoot))
        {
            return true;
        }

        ProductRepairStagingIdentity current;
        current = ProductRepairStagingPolicy.ResolveBoundary(
            identity.StagingRoot,
            identity.InstallRoot);

        if (!string.Equals(current.LauncherRoot, identity.LauncherRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.StagingRoot, identity.StagingRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.Version, identity.Version, StringComparison.Ordinal) ||
            !string.Equals(current.Nonce, identity.Nonce, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Repair staging cleanup identity changed.");
        }

        var schedule = deletionScheduler ?? ScheduleAtRestart;
        var validateSecurity = securityValidator ?? ProductRepairStagingPolicy.ValidateProtectedSecurity;
        var leaves = new List<string>();
        var directories = new List<string>();
        try
        {
            validateSecurity(current.InstallRoot, true);
            validateSecurity(current.LauncherRoot, true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Do not recurse through a staging path whose protected parent chain is no longer
            // trustworthy. Scheduling the exact already-bounded staging object never targets the
            // install root or versions tree, but cannot promise deletion of nonempty contents.
            _ = schedule(current.StagingRoot);
            return false;
        }

        var pending = new Stack<string>();
        pending.Push(current.StagingRoot);
        var observed = 0;
        var completeTreeScheduled = true;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                var directoryAttributes = File.GetAttributes(directory);
                if ((directoryAttributes & FileAttributes.Directory) == 0 ||
                    (directoryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    leaves.Add(directory);
                    continue;
                }

                // Never enumerate a directory writable by an ordinary principal. On a rejected
                // weak-ACL tree, recursively walking by path could otherwise be raced through a
                // newly substituted junction. Scheduling only that directory object is safe but
                // may leave it nonempty, so report incomplete cleanup to the caller.
                validateSecurity(directory, true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                leaves.Add(directory);
                completeTreeScheduled = false;
                continue;
            }

            directories.Add(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++observed > MaximumTreeEntries)
                {
                    throw new InvalidDataException("Repair staging cleanup exceeded its bounded entry limit.");
                }

                EnsureWithinStaging(current.StagingRoot, path);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0 &&
                    (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(path);
                }
                else
                {
                    // Reparse objects are scheduled as leaves and are never traversed.
                    leaves.Add(path);
                }
            }
        }

        var successful = true;
        foreach (var path in leaves.OrderByDescending(path => path.Length))
        {
            successful &= schedule(path);
        }

        foreach (var path in directories.OrderByDescending(path => path.Length))
        {
            successful &= schedule(path);
        }

        return successful && completeTreeScheduled;
    }

    private static bool ScheduleAtRestart(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Repair staging cleanup requires Windows.");
        }

        return MoveFileExW(ToExtendedPath(path), null, MoveFileDelayUntilReboot);
    }

    private static void EnsureWithinStaging(string stagingRoot, string path)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot)) +
                     Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Repair staging cleanup escaped its protected root.");
        }
    }

    private static string ToExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? full
            : @"\\?\" + full;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(
        string existingFileName,
        string? newFileName,
        uint flags);
}
