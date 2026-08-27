using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.ProviderHost;

public interface IProviderProcess : IAsyncDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    void Kill();
}

public interface IProviderProcessFactory
{
    ValueTask<IProviderProcess> StartAsync(
        ProviderRegistration registration,
        CancellationToken cancellationToken);
}

public sealed record ProviderProcessIsolationOptions(
    long MaximumProcessMemoryBytes,
    int MaximumActiveProcesses,
    int MaximumCpuRatePercent = 50)
{
    public static ProviderProcessIsolationOptions Default { get; } = new(
        512L * 1024 * 1024,
        1,
        50);

    public void Validate()
    {
        if (MaximumProcessMemoryBytes is < 64L * 1024 * 1024 or > 2L * 1024 * 1024 * 1024 ||
            MaximumActiveProcesses != 1 ||
            MaximumCpuRatePercent is < 5 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumProcessMemoryBytes),
                "Provider process isolation limits are outside their allowed range.");
        }
    }
}

/// <summary>
/// Windows provider launcher. The process is born suspended as an AppContainer with no network
/// capabilities, receives only three explicit RPC pipe handles, enters a kill-on-close job before
/// its first instruction, and is resumed only after every boundary succeeds. Any unavailable OS
/// primitive fails closed.
/// </summary>
public sealed class ProviderProcessFactory : IProviderProcessFactory
{
    private readonly ProviderHostLayout _layout;
    private readonly ProviderProcessIsolationOptions _isolation;

    public ProviderProcessFactory(
        ProviderHostLayout layout,
        ProviderProcessIsolationOptions? isolation = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _isolation = isolation ?? ProviderProcessIsolationOptions.Default;
        _isolation.Validate();
    }

    public async ValueTask<IProviderProcess> StartAsync(
        ProviderRegistration registration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Provider processes require Windows AppContainer and job-object isolation.");
        }

        var verified = await ProviderInstalledPackageIntegrity.VerifyAsync(
                _layout,
                registration,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var sandbox = ProviderSandbox.Prepare(_layout, registration, verified);
        using var job = ProviderJobObject.Create(_isolation);
        NativeSuspendedProcess? suspended = null;
        try
        {
            suspended = NativeSuspendedProcess.Create(
                sandbox.ExecutablePath,
                sandbox.PayloadDirectory,
                sandbox.ScratchDirectory,
                sandbox.AppContainerSidPointer);
            job.Assign(suspended.ProcessHandle);
            cancellationToken.ThrowIfCancellationRequested();
            suspended.Resume();
            cancellationToken.ThrowIfCancellationRequested();
            return suspended.TransferToChild(job.TransferOwnership(), sandbox.TransferCleanupOwnership());
        }
        catch
        {
            suspended?.Dispose();
            throw;
        }
    }

    /// <summary>Compatibility/test inspection only; production launch uses the native suspended path.</summary>
    internal static ProcessStartInfo CreateStartInfo(
        ProviderHostLayout layout,
        ProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(registration);
        var verified = ProviderInstalledPackageIntegrity.Verify(layout, registration);
        var startInfo = new ProcessStartInfo
        {
            FileName = verified.ExecutablePath,
            WorkingDirectory = verified.InstallDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            ErrorDialog = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--mcsv-provider-rpc");
        startInfo.ArgumentList.Add(ProductApiProtocol.CurrentVersion.ToString());
        CopyMinimalEnvironment(startInfo.Environment, scratchDirectory: null);
        return startInfo;
    }

    private static void CopyMinimalEnvironment(
        IDictionary<string, string?> destination,
        string? scratchDirectory)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        destination.Clear();
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            destination["SystemRoot"] = systemRoot;
            destination["WINDIR"] = systemRoot;
            destination["SystemDrive"] = Path.GetPathRoot(systemRoot);
        }

        if (!string.IsNullOrWhiteSpace(scratchDirectory))
        {
            destination["TEMP"] = scratchDirectory;
            destination["TMP"] = scratchDirectory;
            AddRequiredProfileEnvironment(
                destination,
                "LOCALAPPDATA",
                Environment.SpecialFolder.LocalApplicationData);
            AddRequiredProfileEnvironment(
                destination,
                "APPDATA",
                Environment.SpecialFolder.ApplicationData);
            AddRequiredProfileEnvironment(
                destination,
                "USERPROFILE",
                Environment.SpecialFolder.UserProfile);
        }

        destination["MCSV_PROVIDER_API_VERSION"] = ProductApiProtocol.CurrentVersion.ToString();
        destination["MCSV_PROVIDER_NETWORK"] = "broker-only";
    }

    private static void AddRequiredProfileEnvironment(
        IDictionary<string, string?> destination,
        string name,
        Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"Required provider AppContainer environment variable {name} is unavailable.");
        }

        destination[name] = path;
    }

    private sealed record ProviderSandboxCleanup(
        IReadOnlyList<string> Directories,
        string ProfileName);

    private sealed class ProviderChildProcess(
        SafeKernelObjectHandle process,
        FileStream standardInput,
        FileStream standardOutput,
        FileStream standardError,
        SafeJobHandle job,
        ProviderSandboxCleanup cleanup) : IProviderProcess
    {
        private int _disposed;

        public Stream StandardInput => standardInput;
        public Stream StandardOutput => standardOutput;
        public Stream StandardError => standardError;
        public bool HasExited => TryGetExitCode(out _);
        public int? ExitCode => TryGetExitCode(out var code) ? unchecked((int)code) : null;

        public void Kill()
        {
            try
            {
                if (!process.IsInvalid && !HasExited)
                {
                    _ = NativeMethods.TerminateProcess(process, 1);
                }
            }
            catch (Win32Exception)
            {
                // Closing the job remains the non-bypassable kill-on-close boundary.
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Kill();
                job.Dispose();
                standardInput.Dispose();
                standardOutput.Dispose();
                standardError.Dispose();
                _ = NativeMethods.WaitForSingleObject(process, 2_000);
                process.Dispose();
                foreach (var directory in cleanup.Directories)
                {
                    TryDeleteScratch(directory);
                }

                _ = NativeMethods.DeleteAppContainerProfile(cleanup.ProfileName);
            }

            return ValueTask.CompletedTask;
        }

        private bool TryGetExitCode(out uint code)
        {
            if (!NativeMethods.GetExitCodeProcess(process, out code))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Provider exit code is unavailable.");
            }

            return code != NativeMethods.StillActive;
        }
    }

    private sealed class NativeSuspendedProcess : IDisposable
    {
        private readonly SafeKernelObjectHandle _thread;
        private readonly FileStream _standardInput;
        private readonly FileStream _standardOutput;
        private readonly FileStream _standardError;
        private bool _transferred;

        private NativeSuspendedProcess(
            SafeKernelObjectHandle process,
            SafeKernelObjectHandle thread,
            FileStream standardInput,
            FileStream standardOutput,
            FileStream standardError)
        {
            ProcessHandle = process;
            _thread = thread;
            _standardInput = standardInput;
            _standardOutput = standardOutput;
            _standardError = standardError;
        }

        public SafeKernelObjectHandle ProcessHandle { get; }

        public static NativeSuspendedProcess Create(
            string executablePath,
            string workingDirectory,
            string scratchDirectory,
            IntPtr appContainerSid)
        {
            CreatePipePair(out var childStandardInput, out var parentStandardInput, parentReads: false);
            CreatePipePair(out var childStandardOutput, out var parentStandardOutput, parentReads: true);
            CreatePipePair(out var childStandardError, out var parentStandardError, parentReads: true);
            try
            {
                using var attributes = ProcessAttributeList.Create(
                    appContainerSid,
                    childStandardInput,
                    childStandardOutput,
                    childStandardError);
                var startup = new NativeMethods.StartupInfoEx
                {
                    StartupInfo = new NativeMethods.StartupInfo
                    {
                        Cb = checked((uint)Marshal.SizeOf<NativeMethods.StartupInfoEx>()),
                        Flags = NativeMethods.StartfUseStdHandles,
                        StandardInput = childStandardInput.DangerousGetHandle(),
                        StandardOutput = childStandardOutput.DangerousGetHandle(),
                        StandardError = childStandardError.DangerousGetHandle(),
                    },
                    AttributeList = attributes.Pointer,
                };
                var environmentPointer = AllocateEnvironmentBlock(scratchDirectory);
                try
                {
                    var commandLine = new StringBuilder(
                        QuoteCommandLineArgument(executablePath) +
                        " --mcsv-provider-rpc " +
                        ProductApiProtocol.CurrentVersion);
                    if (!NativeMethods.CreateProcessW(
                            executablePath,
                            commandLine,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            inheritHandles: true,
                            NativeMethods.CreateSuspended |
                            NativeMethods.CreateNoWindow |
                            NativeMethods.ExtendedStartupInfoPresent |
                            NativeMethods.CreateUnicodeEnvironment,
                            environmentPointer,
                            workingDirectory,
                            ref startup,
                            out var processInformation))
                    {
                        var error = Marshal.GetLastWin32Error();
                        throw new Win32Exception(
                            error,
                            $"Provider AppContainer process could not be created (Win32 {error}).");
                    }

                    var processHandle = new SafeKernelObjectHandle(processInformation.Process);
                    var threadHandle = new SafeKernelObjectHandle(processInformation.Thread);
                    FileStream? standardInput = null;
                    FileStream? standardOutput = null;
                    FileStream? standardError = null;
                    try
                    {
                        standardInput = new FileStream(
                            parentStandardInput,
                            FileAccess.Write,
                            4096,
                            isAsync: false);
                        standardOutput = new FileStream(
                            parentStandardOutput,
                            FileAccess.Read,
                            4096,
                            isAsync: false);
                        standardError = new FileStream(
                            parentStandardError,
                            FileAccess.Read,
                            4096,
                            isAsync: false);
                        return new NativeSuspendedProcess(
                            processHandle,
                            threadHandle,
                            standardInput,
                            standardOutput,
                            standardError);
                    }
                    catch
                    {
                        _ = NativeMethods.TerminateProcess(processHandle, 1);
                        standardInput?.Dispose();
                        standardOutput?.Dispose();
                        standardError?.Dispose();
                        threadHandle.Dispose();
                        processHandle.Dispose();
                        throw;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(environmentPointer);
                }
            }
            catch
            {
                parentStandardInput.Dispose();
                parentStandardOutput.Dispose();
                parentStandardError.Dispose();
                throw;
            }
            finally
            {
                childStandardInput.Dispose();
                childStandardOutput.Dispose();
                childStandardError.Dispose();
            }
        }

        public void Resume()
        {
            if (NativeMethods.ResumeThread(_thread) == uint.MaxValue)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Provider process could not be resumed inside its isolation job.");
            }
        }

        public ProviderChildProcess TransferToChild(
            SafeJobHandle job,
            ProviderSandboxCleanup cleanup)
        {
            _transferred = true;
            _thread.Dispose();
            return new ProviderChildProcess(
                ProcessHandle,
                _standardInput,
                _standardOutput,
                _standardError,
                job,
                cleanup);
        }

        public void Dispose()
        {
            if (_transferred)
            {
                return;
            }

            if (!ProcessHandle.IsInvalid)
            {
                _ = NativeMethods.TerminateProcess(ProcessHandle, 1);
                _ = NativeMethods.WaitForSingleObject(ProcessHandle, 2_000);
            }

            _thread.Dispose();
            _standardInput.Dispose();
            _standardOutput.Dispose();
            _standardError.Dispose();
            ProcessHandle.Dispose();
        }

        private static void CreatePipePair(
            out SafeFileHandle child,
            out SafeFileHandle parent,
            bool parentReads)
        {
            var security = new NativeMethods.SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<NativeMethods.SecurityAttributes>()),
                InheritHandle = true,
            };
            if (!NativeMethods.CreatePipe(out var read, out var write, ref security, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Provider RPC pipe could not be created.");
            }

            child = parentReads ? write : read;
            parent = parentReads ? read : write;
            if (!NativeMethods.SetHandleInformation(parent, NativeMethods.HandleFlagInherit, 0))
            {
                var error = Marshal.GetLastWin32Error();
                child.Dispose();
                parent.Dispose();
                throw new Win32Exception(error, "Provider RPC pipe inheritance could not be constrained.");
            }
        }

        private static string BuildEnvironmentBlock(string scratchDirectory)
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            CopyMinimalEnvironment(values, scratchDirectory);
            return string.Join('\0', values
                       .Where(pair => pair.Value is not null)
                       .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                       .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        }

        private static IntPtr AllocateEnvironmentBlock(string scratchDirectory)
        {
            var characters = BuildEnvironmentBlock(scratchDirectory).ToCharArray();
            var pointer = Marshal.AllocHGlobal(checked(characters.Length * sizeof(char)));
            Marshal.Copy(characters, 0, pointer, characters.Length);
            return pointer;
        }

        private static string QuoteCommandLineArgument(string value)
        {
            var result = new StringBuilder(value.Length + 2).Append('"');
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    slashes++;
                    continue;
                }

                if (character == '"')
                {
                    result.Append('\\', slashes * 2 + 1).Append('"');
                    slashes = 0;
                    continue;
                }

                result.Append('\\', slashes).Append(character);
                slashes = 0;
            }

            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class ProviderSandbox : IDisposable
    {
        private IntPtr _sidPointer;
        private bool _cleanupTransferred;
        private readonly string _invocationDirectory;
        private readonly string _profileName;

        private ProviderSandbox(
            IntPtr sidPointer,
            string profileName,
            string invocationDirectory,
            string payloadDirectory,
            string scratchDirectory,
            string executablePath)
        {
            _sidPointer = sidPointer;
            _profileName = profileName;
            _invocationDirectory = invocationDirectory;
            PayloadDirectory = payloadDirectory;
            ScratchDirectory = scratchDirectory;
            ExecutablePath = executablePath;
        }

        public IntPtr AppContainerSidPointer => _sidPointer;
        public string PayloadDirectory { get; }
        public string ScratchDirectory { get; }
        public string ExecutablePath { get; }

        public static ProviderSandbox Prepare(
            ProviderHostLayout layout,
            ProviderRegistration registration,
            VerifiedProviderPackage verified)
        {
            var profileName = CreateProfileName(registration.Manifest.Id) + "." +
                              Guid.NewGuid().ToString("N")[..8];
            var sidPointer = CreateOrOpenProfile(profileName);
            string? invocation = null;
            try
            {
                var sid = new SecurityIdentifier(sidPointer);
                var profileRoot = GetProfileFolder(sid.Value);
                invocation = Path.Combine(
                    layout.State,
                    "provider-scratch",
                    registration.Manifest.Id,
                    Guid.NewGuid().ToString("N"));
                var payload = Path.Combine(invocation, "payload");
                var scratch = Path.Combine(profileRoot, "Temp");
                EnsureNoReparseAncestors(layout.State, invocation);
                Directory.CreateDirectory(payload);
                Directory.CreateDirectory(scratch);
                CopyVerifiedPayload(registration, verified.InstallDirectory, payload);
                var executable = ResolveContainedPath(payload, registration.Manifest.EntryPoint);
                LockPayloadReadExecute(invocation, sid);
                GrantScratchAccess(scratch, sid);
                return new ProviderSandbox(
                    sidPointer,
                    profileName,
                    invocation,
                    payload,
                    scratch,
                    executable);
            }
            catch
            {
                _ = NativeMethods.FreeSid(sidPointer);
                if (invocation is not null)
                {
                    TryDeleteScratch(invocation);
                }

                _ = NativeMethods.DeleteAppContainerProfile(profileName);
                throw;
            }
        }

        public ProviderSandboxCleanup TransferCleanupOwnership()
        {
            _cleanupTransferred = true;
            return new ProviderSandboxCleanup(
                [_invocationDirectory],
                _profileName);
        }

        public void Dispose()
        {
            if (_sidPointer != IntPtr.Zero)
            {
                _ = NativeMethods.FreeSid(_sidPointer);
                _sidPointer = IntPtr.Zero;
            }

            if (!_cleanupTransferred)
            {
                TryDeleteScratch(_invocationDirectory);
                _ = NativeMethods.DeleteAppContainerProfile(_profileName);
            }
        }

        private static string CreateProfileName(string providerId)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(providerId));
            return "Muhun.MCSV.Provider." + Convert.ToHexString(digest[..16]).ToLowerInvariant();
        }

        private static IntPtr CreateOrOpenProfile(string profileName)
        {
            var result = NativeMethods.CreateAppContainerProfile(
                profileName,
                "Muhun MCSV isolated provider",
                "Muhun MCSV provider process boundary",
                IntPtr.Zero,
                0,
                out var sidPointer);
            if (result == 0 && sidPointer != IntPtr.Zero)
            {
                return sidPointer;
            }

            if (result != NativeMethods.HResultAlreadyExists)
            {
                throw new Win32Exception(
                    result & 0xFFFF,
                    $"Provider AppContainer profile could not be created (HRESULT 0x{result:x8}).");
            }

            result = NativeMethods.DeriveAppContainerSidFromAppContainerName(profileName, out sidPointer);
            if (result != 0 || sidPointer == IntPtr.Zero)
            {
                throw new Win32Exception(result, "Provider AppContainer identity could not be derived.");
            }

            return sidPointer;
        }

        private static string GetProfileFolder(string sid)
        {
            var result = NativeMethods.GetAppContainerFolderPath(sid, out var pathPointer);
            if (result != 0 || pathPointer == IntPtr.Zero)
            {
                throw new Win32Exception(
                    result & 0xFFFF,
                    $"Provider AppContainer folder is unavailable (HRESULT 0x{result:x8}).");
            }

            try
            {
                return Path.GetFullPath(Marshal.PtrToStringUni(pathPointer)
                                        ?? throw new InvalidDataException(
                                            "Provider AppContainer folder path is empty."));
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }

        private static void CopyVerifiedPayload(
            ProviderRegistration registration,
            string sourceRoot,
            string destinationRoot)
        {
            foreach (var (relativePath, expectedDigest) in registration.Manifest.FileSha256)
            {
                var source = ResolveContainedPath(sourceRoot, relativePath);
                var destination = ResolveContainedPath(destinationRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using (var input = new FileStream(
                           source,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read,
                           128 * 1024,
                           FileOptions.SequentialScan))
                using (var output = new FileStream(
                           destination,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           128 * 1024,
                           FileOptions.WriteThrough))
                {
                    input.CopyTo(output, 128 * 1024);
                    output.Flush(flushToDisk: true);
                }

                using var verification = new FileStream(
                    destination,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.SequentialScan);
                var actual = SHA256.HashData(verification);
                var expected = Convert.FromHexString(expectedDigest);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                    {
                        throw new CryptographicException(
                            "Provider payload changed while entering its AppContainer shadow.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(actual);
                    CryptographicOperations.ZeroMemory(expected);
                }
            }

            File.WriteAllText(
                Path.Combine(destinationRoot, ProviderPackageInstaller.ManifestFileName),
                JsonSerializer.Serialize(
                    registration.Manifest,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                new UTF8Encoding(false, true));
        }

        private static string ResolveContainedPath(string root, string relativePath)
        {
            var normalized = ProviderPathSafety.NormalizeRelativePath(relativePath);
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var result = Path.GetFullPath(Path.Combine(
                fullRoot,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Provider AppContainer shadow path escaped its root.");
            }

            return result;
        }

        private static void EnsureNoReparseAncestors(string profileRoot, string destination)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileRoot));
            ProviderPathSafety.RejectExistingReparseAncestors(root);
            if (Directory.Exists(root))
            {
                ProviderPathSafety.RejectExistingReparsePoint(root);
            }

            var fullDestination = Path.GetFullPath(destination);
            if (!fullDestination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Provider AppContainer shadow path escaped its trusted root.");
            }
        }

        private static void LockPayloadReadExecute(string directory, SecurityIdentifier appContainerSid)
        {
            using var currentIdentity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var currentSid = currentIdentity.User
                             ?? throw new InvalidOperationException(
                                 "Provider host Windows identity has no user SID.");
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administratorsSid = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
            var childDirectories = Directory.EnumerateDirectories(
                    directory,
                    "*",
                    SearchOption.AllDirectories)
                .Prepend(directory)
                .ToArray();
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .ToArray();

            // Files and deepest directories are secured before their parents so a partial ACL
            // failure never removes the host's ability to traverse and clean the remaining tree.
            foreach (var file in files)
            {
                var security = new FileSecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                AddFullControl(security, currentSid);
                AddFullControl(security, systemSid);
                AddFullControl(security, administratorsSid);
                security.AddAccessRule(new FileSystemAccessRule(
                    appContainerSid,
                    FileSystemRights.ReadAndExecute,
                    AccessControlType.Allow));
                new FileInfo(file).SetAccessControl(security);
            }

            foreach (var childDirectory in childDirectories.OrderByDescending(path => path.Length))
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                AddFullControl(security, currentSid);
                AddFullControl(security, systemSid);
                AddFullControl(security, administratorsSid);
                security.AddAccessRule(new FileSystemAccessRule(
                    appContainerSid,
                    FileSystemRights.ReadAndExecute,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                new DirectoryInfo(childDirectory).SetAccessControl(security);
            }
        }

        private static void AddFullControl(FileSystemSecurity security, SecurityIdentifier sid)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        private static void GrantScratchAccess(string directory, SecurityIdentifier sid)
        {
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
    }

    private sealed class ProcessAttributeList : IDisposable
    {
        private IntPtr _pointer;
        private IntPtr _handleList;
        private IntPtr _securityCapabilities;

        private ProcessAttributeList(IntPtr pointer) => _pointer = pointer;
        public IntPtr Pointer => _pointer;

        public static ProcessAttributeList Create(
            IntPtr appContainerSid,
            SafeFileHandle standardInput,
            SafeFileHandle standardOutput,
            SafeFileHandle standardError)
        {
            nuint size = 0;
            _ = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            if (size == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Provider process attribute list size is unavailable.");
            }

            var pointer = Marshal.AllocHGlobal(checked((nint)size));
            var result = new ProcessAttributeList(pointer);
            try
            {
                if (!NativeMethods.InitializeProcThreadAttributeList(pointer, 2, 0, ref size))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Provider process attribute list could not be initialized.");
                }

                var handles = new[]
                {
                    standardInput.DangerousGetHandle(),
                    standardOutput.DangerousGetHandle(),
                    standardError.DangerousGetHandle(),
                };
                result._handleList = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
                Marshal.Copy(handles, 0, result._handleList, handles.Length);
                if (!NativeMethods.UpdateProcThreadAttribute(
                        pointer,
                        0,
                        NativeMethods.ProcThreadAttributeHandleList,
                        result._handleList,
                        checked((nuint)(IntPtr.Size * handles.Length)),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Provider inherited-handle allowlist could not be applied.");
                }

                var capabilities = new NativeMethods.SecurityCapabilities
                {
                    AppContainerSid = appContainerSid,
                };
                result._securityCapabilities = Marshal.AllocHGlobal(
                    Marshal.SizeOf<NativeMethods.SecurityCapabilities>());
                Marshal.StructureToPtr(capabilities, result._securityCapabilities, false);
                if (!NativeMethods.UpdateProcThreadAttribute(
                        pointer,
                        0,
                        NativeMethods.ProcThreadAttributeSecurityCapabilities,
                        result._securityCapabilities,
                        checked((nuint)Marshal.SizeOf<NativeMethods.SecurityCapabilities>()),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Provider AppContainer security capabilities could not be applied.");
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_pointer != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(_pointer);
                Marshal.FreeHGlobal(_pointer);
                _pointer = IntPtr.Zero;
            }

            Marshal.FreeHGlobal(_handleList);
            Marshal.FreeHGlobal(_securityCapabilities);
            _handleList = IntPtr.Zero;
            _securityCapabilities = IntPtr.Zero;
        }
    }

    private sealed class ProviderJobObject : IDisposable
    {
        private SafeJobHandle _handle;
        private bool _transferred;

        private ProviderJobObject(SafeJobHandle handle) => _handle = handle;

        public static ProviderJobObject Create(ProviderProcessIsolationOptions options)
        {
            var handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Provider isolation job could not be created.");
            }

            var result = new ProviderJobObject(handle);
            try
            {
                var limits = new NativeMethods.JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
                    {
                        LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose |
                                     NativeMethods.JobObjectLimitActiveProcess |
                                     NativeMethods.JobObjectLimitProcessMemory |
                                     NativeMethods.JobObjectLimitDieOnUnhandledException,
                        ActiveProcessLimit = checked((uint)options.MaximumActiveProcesses),
                    },
                    ProcessMemoryLimit = checked((nuint)options.MaximumProcessMemoryBytes),
                };
                SetJobInformation(
                    handle,
                    NativeMethods.JobObjectExtendedLimitInformationClass,
                    limits,
                    "Provider isolation job limits could not be applied.");

                var cpu = new NativeMethods.JobObjectCpuRateControlInformation
                {
                    ControlFlags = NativeMethods.JobObjectCpuRateControlEnable |
                                   NativeMethods.JobObjectCpuRateControlHardCap,
                    CpuRate = checked((uint)(options.MaximumCpuRatePercent * 100)),
                };
                SetJobInformation(
                    handle,
                    NativeMethods.JobObjectCpuRateControlInformationClass,
                    cpu,
                    "Provider CPU limit could not be applied.");

                var uiRestrictions = NativeMethods.JobObjectUiLimitHandles |
                                     NativeMethods.JobObjectUiLimitReadClipboard |
                                     NativeMethods.JobObjectUiLimitWriteClipboard |
                                     NativeMethods.JobObjectUiLimitSystemParameters |
                                     NativeMethods.JobObjectUiLimitDisplaySettings |
                                     NativeMethods.JobObjectUiLimitGlobalAtoms |
                                     NativeMethods.JobObjectUiLimitDesktop |
                                     NativeMethods.JobObjectUiLimitExitWindows;
                SetJobInformation(
                    handle,
                    NativeMethods.JobObjectBasicUiRestrictionsClass,
                    uiRestrictions,
                    "Provider UI isolation limits could not be applied.");
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        public void Assign(SafeKernelObjectHandle process)
        {
            if (!NativeMethods.AssignProcessToJobObject(_handle, process))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Provider process could not enter its isolation job before resume.");
            }
        }

        public SafeJobHandle TransferOwnership()
        {
            _transferred = true;
            return _handle;
        }

        public void Dispose()
        {
            if (!_transferred)
            {
                _handle.Dispose();
            }
        }

        private static void SetJobInformation<T>(
            SafeJobHandle handle,
            int informationClass,
            T value,
            string errorMessage) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, pointer, false);
                if (!NativeMethods.SetInformationJobObject(
                        handle,
                        informationClass,
                        pointer,
                        checked((uint)size)))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), errorMessage);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private sealed class SafeKernelObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeKernelObjectHandle() : base(ownsHandle: true) { }
        internal SafeKernelObjectHandle(IntPtr value) : base(ownsHandle: true) => SetHandle(value);
        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static void TryDeleteScratch(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                ProviderPathSafety.DeleteOwnedTree(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A later host-start cleanup removes orphaned invocation scratch directories.
        }
    }

    private static class NativeMethods
    {
        internal const int HResultAlreadyExists = unchecked((int)0x800700B7);
        internal const int ErrorAccessDenied = 5;
        internal const uint StillActive = 259;
        internal const uint HandleFlagInherit = 0x00000001;
        internal const uint StartfUseStdHandles = 0x00000100;
        internal const uint CreateSuspended = 0x00000004;
        internal const uint CreateUnicodeEnvironment = 0x00000400;
        internal const uint ExtendedStartupInfoPresent = 0x00080000;
        internal const uint CreateNoWindow = 0x08000000;
        internal const nuint ProcThreadAttributeHandleList = 0x00020002;
        internal const nuint ProcThreadAttributeSecurityCapabilities = 0x00020009;

        internal const int JobObjectBasicUiRestrictionsClass = 4;
        internal const int JobObjectExtendedLimitInformationClass = 9;
        internal const int JobObjectCpuRateControlInformationClass = 15;
        internal const uint JobObjectLimitActiveProcess = 0x00000008;
        internal const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
        internal const uint JobObjectLimitProcessMemory = 0x00000100;
        internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
        internal const uint JobObjectCpuRateControlEnable = 0x1;
        internal const uint JobObjectCpuRateControlHardCap = 0x4;
        internal const uint JobObjectUiLimitHandles = 0x1;
        internal const uint JobObjectUiLimitReadClipboard = 0x2;
        internal const uint JobObjectUiLimitWriteClipboard = 0x4;
        internal const uint JobObjectUiLimitSystemParameters = 0x8;
        internal const uint JobObjectUiLimitDisplaySettings = 0x10;
        internal const uint JobObjectUiLimitGlobalAtoms = 0x20;
        internal const uint JobObjectUiLimitDesktop = 0x40;
        internal const uint JobObjectUiLimitExitWindows = 0x80;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(
            out SafeFileHandle readPipe,
            out SafeFileHandle writePipe,
            ref SecurityAttributes pipeAttributes,
            uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            uint flags,
            ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            nuint attribute,
            IntPtr value,
            nuint size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(SafeKernelObjectHandle thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(SafeKernelObjectHandle process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeKernelObjectHandle process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(
            SafeKernelObjectHandle handle,
            uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeJobHandle job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(
            SafeJobHandle job,
            SafeKernelObjectHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        internal static extern int CreateAppContainerProfile(
            string appContainerName,
            string displayName,
            string description,
            IntPtr capabilities,
            uint capabilityCount,
            out IntPtr appContainerSid);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        internal static extern int DeriveAppContainerSidFromAppContainerName(
            string appContainerName,
            out IntPtr appContainerSid);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetAppContainerFolderPath(
            string appContainerSid,
            out IntPtr path);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        internal static extern int DeleteAppContainerProfile(string appContainerName);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern IntPtr FreeSid(IntPtr sid);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            internal uint Length;
            internal IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            internal uint Cb;
            internal string? Reserved;
            internal string? Desktop;
            internal string? Title;
            internal uint X;
            internal uint Y;
            internal uint XSize;
            internal uint YSize;
            internal uint XCountChars;
            internal uint YCountChars;
            internal uint FillAttribute;
            internal uint Flags;
            internal ushort ShowWindow;
            internal ushort Reserved2;
            internal IntPtr ReservedPointer;
            internal IntPtr StandardInput;
            internal IntPtr StandardOutput;
            internal IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx
        {
            internal StartupInfo StartupInfo;
            internal IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            internal IntPtr Process;
            internal IntPtr Thread;
            internal uint ProcessId;
            internal uint ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityCapabilities
        {
            internal IntPtr AppContainerSid;
            internal IntPtr Capabilities;
            internal uint CapabilityCount;
            internal uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectBasicLimitInformation
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal nuint MinimumWorkingSetSize;
            internal nuint MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal nuint Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectExtendedLimitInformation
        {
            internal JobObjectBasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal nuint ProcessMemoryLimit;
            internal nuint JobMemoryLimit;
            internal nuint PeakProcessMemoryUsed;
            internal nuint PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectCpuRateControlInformation
        {
            internal uint ControlFlags;
            internal uint CpuRate;
        }
    }
}
