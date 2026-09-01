using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MinecraftServerManager.App.Services;

internal interface IManagedProductLauncherDirectoryResolver
{
    string ResolveLauncherDirectory();
}

internal interface IWindowsProtectedDirectoryCopyBroker
{
    Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationParentDirectory,
        string destinationName,
        CancellationToken cancellationToken);

    Task DeleteDirectoryAsync(string directory, CancellationToken cancellationToken);
}

internal interface IProtectedProductPathSecurityValidator
{
    void ValidateContainer(string path, bool requireProtectedAccessRules);

    void ValidateTree(string root);
}

/// <summary>
/// Stages an untrusted loose-release directory through the Windows Shell elevation broker. The
/// broker performs a data-only copy into the existing protected launcher directory; the copied
/// executable is never run until a separate verifier has accepted the protected result.
/// </summary>
internal sealed class WindowsShellProtectedFormalReleaseStager : IProtectedFormalReleaseStager
{
    private readonly IManagedProductLauncherDirectoryResolver _launcherResolver;
    private readonly IWindowsProtectedDirectoryCopyBroker _copyBroker;
    private readonly IProtectedProductPathSecurityValidator _securityValidator;
    private readonly Func<string> _nonceFactory;

    public WindowsShellProtectedFormalReleaseStager()
        : this(
            new WindowsManagedProductLauncherDirectoryResolver(),
            new WindowsFileOperationCopyBroker(),
            new WindowsProtectedProductPathSecurityValidator(),
            () => Guid.NewGuid().ToString("N"))
    {
    }

    internal WindowsShellProtectedFormalReleaseStager(
        IManagedProductLauncherDirectoryResolver launcherResolver,
        IWindowsProtectedDirectoryCopyBroker copyBroker,
        IProtectedProductPathSecurityValidator securityValidator,
        Func<string> nonceFactory)
    {
        _launcherResolver = launcherResolver ?? throw new ArgumentNullException(nameof(launcherResolver));
        _copyBroker = copyBroker ?? throw new ArgumentNullException(nameof(copyBroker));
        _securityValidator = securityValidator ?? throw new ArgumentNullException(nameof(securityValidator));
        _nonceFactory = nonceFactory ?? throw new ArgumentNullException(nameof(nonceFactory));
    }

    public async Task<ProtectedFormalReleaseStage> StageAsync(
        string sourceReleaseRoot,
        string expectedProductVersion,
        CancellationToken cancellationToken)
    {
        var source = NormalizeExistingLocalDirectory(sourceReleaseRoot);
        var version = ValidateVersionPathSegment(expectedProductVersion);
        var nonce = _nonceFactory();
        if (nonce.Length != 32 || nonce.Any(character => !IsLowerHex(character)))
        {
            throw new InvalidDataException("The protected repair staging nonce is invalid.");
        }

        var launcher = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            _launcherResolver.ResolveLauncherDirectory()));
        var installRoot = Directory.GetParent(launcher)?.FullName
            ?? throw new InvalidDataException("The managed install root is unavailable.");
        _securityValidator.ValidateContainer(installRoot, requireProtectedAccessRules: false);
        _securityValidator.ValidateContainer(launcher, requireProtectedAccessRules: true);

        var stageName = $".repair-staging-{version}-{nonce}";
        var stageRoot = Path.GetFullPath(Path.Combine(launcher, stageName));
        var launcherPrefix = launcher + Path.DirectorySeparatorChar;
        if (!stageRoot.StartsWith(launcherPrefix, StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(stageRoot) ||
            File.Exists(stageRoot))
        {
            throw new InvalidDataException("The protected repair staging destination is unsafe or already exists.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _copyBroker.CopyDirectoryAsync(
                    source,
                    launcher,
                    stageName,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(stageRoot))
            {
                throw new IOException("The Windows file-operation broker did not create the repair staging directory.");
            }

            _securityValidator.ValidateTree(stageRoot);
            return new ProtectedFormalReleaseStage(stageRoot);
        }
        catch
        {
            if (Directory.Exists(stageRoot))
            {
                await TryCleanupAsync(new ProtectedFormalReleaseStage(stageRoot)).ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task TryCleanupAsync(ProtectedFormalReleaseStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        try
        {
            var launcher = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                _launcherResolver.ResolveLauncherDirectory()));
            var installRoot = Directory.GetParent(launcher)?.FullName
                ?? throw new InvalidDataException("The managed install root is unavailable.");
            _securityValidator.ValidateContainer(installRoot, requireProtectedAccessRules: false);
            _securityValidator.ValidateContainer(launcher, requireProtectedAccessRules: true);
            var stageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stage.ReleaseRoot));
            if (!IsOwnedStagePath(launcher, stageRoot) || !Directory.Exists(stageRoot))
            {
                return;
            }

            // This stage name is the nonce-bound direct child created by this stager, while the
            // validated launcher parent cannot be replaced by an unprivileged caller. Cleanup is
            // intentionally allowed even when the copied root/tree ACL was the validation failure:
            // IFileOperation receives FOF_NORECURSEREPARSE and deletes the exact directory entry,
            // so it cannot traverse attacker-controlled child junctions.
            await _copyBroker.DeleteDirectoryAsync(stageRoot, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // A broker/UAC failure leaves the stage inert beneath the protected launcher. Broad
            // or user-selected deletion is never attempted here.
        }
    }

    internal static string ValidateVersionPathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value[0] is '.' or '-' || value[^1] is '.' or '-' ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new InvalidDataException("The product version is unsafe for protected staging.");
        }

        return value;
    }

    private static bool IsOwnedStagePath(string launcher, string stageRoot)
    {
        if (!string.Equals(
                Directory.GetParent(stageRoot)?.FullName,
                launcher,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        const string prefix = ".repair-staging-";
        var name = Path.GetFileName(stageRoot);
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length <= prefix.Length + 33)
        {
            return false;
        }

        var nonceSeparator = name.Length - 33;
        if (name[nonceSeparator] != '-')
        {
            return false;
        }

        var version = name[prefix.Length..nonceSeparator];
        var nonce = name[(nonceSeparator + 1)..];
        try
        {
            _ = ValidateVersionPathSegment(version);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        return nonce.Length == 32 && nonce.All(IsLowerHex);
    }

    private static string NormalizeExistingLocalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("The formal release source must be absolute.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.IndexOf('"') >= 0 ||
            !Directory.Exists(root) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The formal release source must be an existing local directory.");
        }

        return root;
    }

    private static bool IsLowerHex(char value)
        => value is >= '0' and <= '9' or >= 'a' and <= 'f';
}

internal sealed class WindowsManagedProductLauncherDirectoryResolver
    : IManagedProductLauncherDirectoryResolver
{
    private const string ServiceRegistryPath = @"SYSTEM\CurrentControlSet\Services\MuhunMCSV";

    public string ResolveLauncherDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected product repair requires Windows.");
        }

        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var service = machine.OpenSubKey(ServiceRegistryPath, writable: false)
            ?? throw new InvalidOperationException("The managed Muhun MCSV Service is not installed.");
        if (!string.Equals(
                service.GetValue("ObjectName", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                @"NT SERVICE\MuhunMCSV",
                StringComparison.OrdinalIgnoreCase) ||
            service.GetValue("Start", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not int start ||
            start != 2)
        {
            throw new InvalidDataException("The installed Service identity is not managed by X MCSV.");
        }

        var imagePath = service.GetValue(
                "ImagePath",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string
            ?? throw new InvalidDataException("The installed Service image path is missing.");
        var serviceExecutable = ParseServiceExecutable(imagePath);
        var serviceDirectory = Directory.GetParent(serviceExecutable)?.FullName
            ?? throw new InvalidDataException("The installed Service directory is invalid.");
        var versionRoot = Directory.GetParent(serviceDirectory)?.FullName
            ?? throw new InvalidDataException("The installed version directory is invalid.");
        var versionsRoot = Directory.GetParent(versionRoot)?.FullName
            ?? throw new InvalidDataException("The managed versions directory is invalid.");
        var installRoot = Directory.GetParent(versionsRoot)?.FullName
            ?? throw new InvalidDataException("The managed install directory is invalid.");
        if (!string.Equals(Path.GetFileName(serviceDirectory), "service-win-x64", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(versionsRoot), "versions", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The installed Service is not beneath a managed version layout.");
        }

        ValidateProgramFilesInstallRoot(installRoot);
        ValidateInstallMarker(installRoot);
        var launcher = Path.Combine(installRoot, "launcher");
        if (!Directory.Exists(launcher))
        {
            throw new DirectoryNotFoundException("The protected product launcher directory is missing.");
        }

        WindowsProtectedProductPathSecurityValidator.RejectExistingReparsePoints(launcher);
        return launcher;
    }

    private static string ParseServiceExecutable(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || imagePath.Length > 4_096 || imagePath[0] != '"')
        {
            throw new InvalidDataException("The installed Service command line is invalid.");
        }

        var executableEnd = imagePath.IndexOf('"', 1);
        if (executableEnd <= 1)
        {
            throw new InvalidDataException("The installed Service executable path is invalid.");
        }

        var executable = imagePath[1..executableEnd];
        if (!Path.IsPathFullyQualified(executable) ||
            !string.Equals(Path.GetFileName(executable), "Muhun MCSV Service.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable))
        {
            throw new InvalidDataException("The installed Service executable is unavailable.");
        }

        WindowsProtectedProductPathSecurityValidator.RejectExistingReparsePoints(executable);
        return Path.GetFullPath(executable);
    }

    private static void ValidateProgramFilesInstallRoot(string installRoot)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            throw new InvalidOperationException("Windows Program Files is unavailable.");
        }

        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFiles))
                     + Path.DirectorySeparatorChar;
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot))
                        + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The managed product is not installed below Program Files.");
        }

        WindowsProtectedProductPathSecurityValidator.RejectExistingReparsePoints(installRoot);
    }

    private static void ValidateInstallMarker(string installRoot)
    {
        var marker = Path.Combine(installRoot, ".muhun-mcsv-install-root");
        WindowsProtectedProductPathSecurityValidator.RejectExistingReparsePoints(marker);
        if (!File.Exists(marker) ||
            (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(marker).Length is < 1 or > 64 ||
            !string.Equals(File.ReadAllText(marker).Trim(), "muhun.mcsv.manager:1", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The managed install root marker is invalid.");
        }
    }
}

internal sealed class WindowsProtectedProductPathSecurityValidator
    : IProtectedProductPathSecurityValidator
{
    private const int MaximumTreeEntries = 16_384;
    private const FileSystemRights DangerousRights =
        FileSystemRights.WriteData |
        FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership |
        (FileSystemRights)0x10000000 | // GENERIC_ALL (raw ACEs are not always expanded by .NET)
        (FileSystemRights)0x40000000;  // GENERIC_WRITE

    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = ResolveTrustedInstaller();
    public void ValidateContainer(string path, bool requireProtectedAccessRules)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        RejectExistingReparsePoints(fullPath);
        if (!Directory.Exists(fullPath) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A protected product container is missing or unsafe.");
        }

        var security = new DirectoryInfo(fullPath).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        ValidateSecurityDescriptor(security, requireProtectedAccessRules);
    }

    public void ValidateTree(string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        ValidateContainer(normalizedRoot, requireProtectedAccessRules: false);
        var pending = new Stack<string>();
        pending.Push(normalizedRoot);
        var observed = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++observed > MaximumTreeEntries)
                {
                    throw new InvalidDataException("The protected repair staging tree is too large.");
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The protected repair staging tree contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var directorySecurity = new DirectoryInfo(path).GetAccessControl(
                        AccessControlSections.Owner | AccessControlSections.Access);
                    ValidateSecurityDescriptor(directorySecurity, requireProtectedAccessRules: false);
                    pending.Push(path);
                    continue;
                }

                var fileSecurity = new FileInfo(path).GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access);
                ValidateSecurityDescriptor(fileSecurity, requireProtectedAccessRules: false);
                RequireSingleLinkFile(path);
            }
        }
    }

    internal static void ValidateSecurityDescriptor(
        FileSystemSecurity security,
        bool requireProtectedAccessRules,
        SecurityIdentifier? currentUserOverride = null)
    {
        ArgumentNullException.ThrowIfNull(security);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new InvalidDataException("A protected product path has no SID owner.");
        if (!owner.Equals(Administrators) && !owner.Equals(LocalSystem) && !owner.Equals(TrustedInstaller))
        {
            throw new UnauthorizedAccessException("A protected product path has an untrusted owner.");
        }

        if (requireProtectedAccessRules && !security.AreAccessRulesProtected)
        {
            throw new UnauthorizedAccessException("The product launcher ACL must be protected from inheritance.");
        }

        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null)
        {
            throw new UnauthorizedAccessException("A protected product path cannot have a null DACL.");
        }

        _ = currentUserOverride;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                rule.IdentityReference is SecurityIdentifier sid &&
                (rule.FileSystemRights & DangerousRights) != 0 &&
                !sid.Equals(Administrators) &&
                !sid.Equals(LocalSystem) &&
                !sid.Equals(TrustedInstaller))
            {
                throw new UnauthorizedAccessException(
                    "A protected product path grants write, delete or ACL ownership rights outside the privileged allowlist.");
            }
        }
    }

    internal static void RejectExistingReparsePoints(string path)
    {
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(Path.GetFullPath(path))
            : new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Protected product paths cannot traverse a reparse point.");
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }
    }

    private static void RequireSingleLinkFile(string path)
    {
        using var handle = CreateFileW(
            ToExtendedPath(path),
            0x00000080,
            0x00000001,
            IntPtr.Zero,
            3,
            0x00200000,
            IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
        {
            var code = Marshal.GetLastWin32Error();
            throw new IOException(
                $"A protected staged file could not be inspected (Win32 {code}).",
                new Win32Exception(code));
        }

        if (((FileAttributes)information.FileAttributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            information.NumberOfLinks != 1)
        {
            throw new InvalidDataException("A protected staged file is a reparse point or hard link.");
        }
    }

    private static SecurityIdentifier ResolveTrustedInstaller()
    {
        try
        {
            return (SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller")
                .Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
        }
    }

    private static string ToExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return full;
        }

        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

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

/// <summary>
/// A data-only Windows Shell copy. FOF_NOCOPYSECURITYATTRIBS is mandatory so the protected
/// destination inherits the launcher's ACL rather than carrying permissions from the user's
/// extracted release directory.
/// </summary>
internal sealed class WindowsFileOperationCopyBroker : IWindowsProtectedDirectoryCopyBroker
{
    private const uint CopyFlags =
        0x00000004 | // FOF_SILENT
        0x00000010 | // FOF_NOCONFIRMATION
        0x00000200 | // FOF_NOCONFIRMMKDIR
        0x00000400 | // FOF_NOERRORUI
        0x00000800 | // FOF_NOCOPYSECURITYATTRIBS
        0x00002000 | // FOF_NO_CONNECTED_ELEMENTS
        0x00008000 | // FOF_NORECURSEREPARSE
        0x00040000 | // FOFX_SHOWELEVATIONPROMPT
        0x00100000 | // FOFX_EARLYFAILURE
        0x00800000 | // FOFX_NOCOPYHOOKS
        0x10000000;  // FOFX_REQUIREELEVATION

    public Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationParentDirectory,
        string destinationName,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected release staging requires Windows.");
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyDirectory(sourceDirectory, destinationParentDirectory, destinationName);
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult();
            }
            catch (OperationCanceledException error)
            {
                completion.TrySetCanceled(error.CancellationToken);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        })
        {
            IsBackground = true,
            Name = "X MCSV protected release staging",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public Task DeleteDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected release cleanup requires Windows.");
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteDirectory(directory);
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult();
            }
            catch (OperationCanceledException error)
            {
                completion.TrySetCanceled(error.CancellationToken);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        })
        {
            IsBackground = true,
            Name = "X MCSV protected release cleanup",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void CopyDirectory(string source, string destination, string destinationName)
    {
        IFileOperation? operation = null;
        IShellItem? sourceItem = null;
        IShellItem? destinationItem = null;
        try
        {
            var operationType = Type.GetTypeFromCLSID(
                new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"),
                throwOnError: true)
                ?? throw new COMException("The Windows FileOperation broker is unavailable.");
            operation = (IFileOperation)(Activator.CreateInstance(operationType)
                ?? throw new COMException("The Windows FileOperation broker could not be created."));
            ThrowIfFailed(operation.SetOperationFlags(CopyFlags));
            sourceItem = CreateShellItem(source);
            destinationItem = CreateShellItem(destination);
            ThrowIfFailed(operation.CopyItem(sourceItem, destinationItem, destinationName, IntPtr.Zero));
            ThrowIfFailed(operation.PerformOperations());
            ThrowIfFailed(operation.GetAnyOperationsAborted(out var aborted));
            if (aborted)
            {
                throw new Win32Exception(1223, "The protected release staging operation was cancelled.");
            }
        }
        finally
        {
            ReleaseComObject(destinationItem);
            ReleaseComObject(sourceItem);
            ReleaseComObject(operation);
        }
    }

    private static void DeleteDirectory(string directory)
    {
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            operation = CreateFileOperation();
            ThrowIfFailed(operation.SetOperationFlags(CopyFlags));
            item = CreateShellItem(directory);
            ThrowIfFailed(operation.DeleteItem(item, IntPtr.Zero));
            ThrowIfFailed(operation.PerformOperations());
            ThrowIfFailed(operation.GetAnyOperationsAborted(out var aborted));
            if (aborted)
            {
                throw new Win32Exception(1223, "The protected release cleanup operation was cancelled.");
            }
        }
        finally
        {
            ReleaseComObject(item);
            ReleaseComObject(operation);
        }
    }

    private static IFileOperation CreateFileOperation()
    {
        var operationType = Type.GetTypeFromCLSID(
            new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"),
            throwOnError: true)
            ?? throw new COMException("The Windows FileOperation broker is unavailable.");
        return (IFileOperation)(Activator.CreateInstance(operationType)
            ?? throw new COMException("The Windows FileOperation broker could not be created."));
    }

    private static IShellItem CreateShellItem(string path)
    {
        var iid = typeof(IShellItem).GUID;
        ThrowIfFailed(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item));
        return item;
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint displayNameType, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr progressSink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint operationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr progressDialog);
        [PreserveSig] int SetProperties(IntPtr propertyChangeArray);
        [PreserveSig] int SetOwnerWindow(uint ownerWindow);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr progressSink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr progressSink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr progressSink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IntPtr progressSink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destination, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, IntPtr progressSink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
