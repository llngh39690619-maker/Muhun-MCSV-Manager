using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using MinecraftServerManager.Installer;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ManagedInstallerContractTests
{
    [Fact]
    public void InstallerActivationDiagnostic_IsStrictBoundedAndNeverEchoesUntrustedText()
    {
        var installationId = Guid.NewGuid();
        var json = $$"""
            {
              "status": "starting",
              "product": "Muhun MCSV Manager",
              "version": "1.2.9-beta.9",
              "installationId": "{{installationId:D}}",
              "startedAtUtc": "2026-09-02T10:00:00Z",
              "ready": false,
              "startupFailure": {
                "code": "ipc.operator_group_missing",
                "exceptionType": "InvalidOperationException",
                "hResult": -2146233079,
                "innerExceptionType": "IdentityNotMappedException",
                "innerHResult": -2146233087
              }
            }
            """;
        var parsed = WindowsInstallerPlatform.ReadActivationReadyResponse(
            Encoding.UTF8.GetBytes(json));
        var message = WindowsInstallerPlatform.FormatSafeStartupFailure(
            parsed.StartupFailure!);

        Assert.Equal(installationId, parsed.InstallationId);
        Assert.Contains("本機操作員群組", message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", message, StringComparison.Ordinal);
        Assert.Contains("IdentityNotMappedException", message, StringComparison.Ordinal);
        Assert.Contains("HRESULT 0x", message, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() =>
            WindowsInstallerPlatform.FormatSafeStartupFailure(
                new InstallerActivationFailureDiagnostic(
                    "ipc.access_denied",
                    "UnauthorizedAccessException\r\nsecret-token",
                    unchecked((int)0x80070005),
                    null,
                    null)));

        var unexpectedField = json.Replace(
            "\"code\": \"ipc.operator_group_missing\"",
            "\"message\": \"secret-token\", \"code\": \"ipc.operator_group_missing\"",
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() =>
            WindowsInstallerPlatform.ReadActivationReadyResponse(
                Encoding.UTF8.GetBytes(unexpectedField)));
    }

    [Fact]
    public async Task InstallerProcessLock_CanReleaseAfterAwaitOnAnotherThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = @"Local\Muhun.MCSV.Installer.Tests." + Guid.NewGuid().ToString("N");
        var policy = CreateTestMutexSecurityPolicy();
        var acquired = await InstallerProcessLock.AcquireAsync(name, policy);

        var releaseThreadId = await RunOnDedicatedThreadAsync(() =>
        {
            acquired.Dispose();
            return Environment.CurrentManagedThreadId;
        });

        Assert.NotEqual(acquired.OwnerThreadId, releaseThreadId);
        await using var reacquired = await InstallerProcessLock.AcquireAsync(name, policy);
    }

    [Fact]
    public async Task InstallerProcessLock_FailsClosedDuringNamedLockContention()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = @"Local\Muhun.MCSV.Installer.Tests." + Guid.NewGuid().ToString("N");
        var policy = CreateTestMutexSecurityPolicy();
        await using var held = await InstallerProcessLock.AcquireAsync(name, policy);
        using var secondHandle = MutexAcl.OpenExisting(name, MutexRights.Synchronize);

        Assert.False(secondHandle.WaitOne(TimeSpan.Zero));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InstallerProcessLock.AcquireAsync(name, policy));
        Assert.Contains("另一個 X MCSV 安裝工作", error.Message, StringComparison.Ordinal);

        await held.DisposeAsync();
        await using var reacquired = await InstallerProcessLock.AcquireAsync(name, policy);
    }

    [Fact]
    public async Task InstallerProcessLock_RecoversAnAbandonedNamedMutex()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = @"Local\Muhun.MCSV.Installer.Tests." + Guid.NewGuid().ToString("N");
        var policy = CreateTestMutexSecurityPolicy();
        using var keeper = MutexAcl.Create(
            initiallyOwned: false,
            name,
            out var createdNew,
            policy.CreationSecurity);
        Assert.True(createdNew);

        Exception? ownerError = null;
        var abandonedOwner = new Thread(() =>
        {
            try
            {
                using var mutex = MutexAcl.OpenExisting(name, MutexRights.FullControl);
                Assert.True(mutex.WaitOne(millisecondsTimeout: 0));
                // Deliberately leave without ReleaseMutex. The thread ending abandons the mutex;
                // the keeper handle above ensures the named kernel object remains observable.
            }
            catch (Exception error)
            {
                ownerError = error;
            }
        })
        {
            IsBackground = true,
        };
        abandonedOwner.Start();
        Assert.True(abandonedOwner.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(ownerError);

        await using var recovered = await InstallerProcessLock.AcquireAsync(name, policy);
    }

    [Fact]
    public void InstallerProcessLock_MachinePolicyAllowsOnlyAdministratorsAndSystem()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = Assert.IsType<SecurityIdentifier>(identity.User);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            domainSid: null);
        var localSystem = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            domainSid: null);
        var expected = new HashSet<SecurityIdentifier> { administrators, localSystem };

        var policy = InstallerProcessLock.CreateMachineMutexSecurityPolicy();
        Assert.Equal(
            administrators,
            policy.CreationSecurity.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(policy.CreationSecurity.AreAccessRulesProtected);
        Assert.True(expected.SetEquals(policy.AllowedOwners));
        Assert.True(expected.SetEquals(policy.FullControlPrincipals));
        Assert.DoesNotContain(currentUser, policy.FullControlPrincipals);
        var actualRules = policy.CreationSecurity.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<MutexAccessRule>()
            .ToArray();
        Assert.Equal(2, actualRules.Length);
        Assert.All(actualRules, rule =>
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(MutexRights.FullControl, rule.MutexRights);
            Assert.Contains((SecurityIdentifier)rule.IdentityReference, expected);
        });
    }

    [Fact]
    public async Task InstallerProcessLock_RejectsPrecreatedMutexWithUntrustedAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = @"Local\Muhun.MCSV.Installer.Tests." + Guid.NewGuid().ToString("N");
        using var precreated = new Mutex(initiallyOwned: false, name);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InstallerProcessLock.AcquireAsync(name, CreateTestMutexSecurityPolicy()));
        Assert.Contains("安裝鎖", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultRoot_IsExactlyProgramFilesMcsv()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "MCSV"),
            InstallerLayout.DefaultRoot);
    }

    [Fact]
    public void Resolve_PreservesExistingUpdaterLayoutAndChannelSeparatesMutableData()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcsv-installer-layout", Guid.NewGuid().ToString("N"));
        var layout = InstallerLayout.Resolve(root, "beta", "S-1-5-21-1-2-3-1001");

        Assert.Equal(Path.Combine(root, "versions"), layout.VersionsRoot);
        Assert.Equal(Path.Combine(root, "activation-state"), layout.ActivationRoot);
        Assert.Equal(Path.Combine(root, "service", "beta"), layout.ServiceRoot);
        Assert.Equal(Path.Combine(root, "exchange", "beta"), layout.ExchangeRoot);
        Assert.Equal(
            Path.Combine(root, "users", "S-1-5-21-1-2-3-1001", "beta"),
            layout.UserRoot);
        Assert.Equal(Path.Combine(root, "install-staging"), layout.StagingRoot);
        Assert.Equal(Path.Combine(root, "launcher"), layout.LauncherRoot);
    }

    [Theory]
    [InlineData(@"D:\MCSV", null)]
    [InlineData(@"d:\mcsv\", null)]
    [InlineData(@"D:\MCSV\child\grandchild", null)]
    [InlineData(@"D:\MCSV.", null)]
    [InlineData(@"D:\MCSV   ", null)]
    [InlineData(@"D:\MCSV...\child", null)]
    [InlineData(@"D:/foo/../MCSV", null)]
    [InlineData(@"D:\foo\..\MCSV\child", null)]
    [InlineData(@"\\?\D:\MCSV", null)]
    [InlineData(@"\\.\D:\MCSV", null)]
    [InlineData(@"//?/D:/MCSV/child", null)]
    [InlineData(@"\??\D:\MCSV", null)]
    [InlineData(@"..\MCSV", @"D:\folder")]
    [InlineData(@"..\..\MCSV\child", @"D:\one\two")]
    public void PureProtectionGuard_RejectsPreservedDMcsvWithoutFilesystemAccess(
        string path,
        string? basePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<ArgumentException>(() =>
            InstallerLayout.RejectPreservedDataTreePath(path, basePath));
    }

    [Fact]
    public void PureProtectionGuard_AllowsProgramFilesMcsvWithoutFilesystemAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = Record.Exception(() => InstallerLayout.RejectPreservedDataTreePath(
            @"C:\Program Files\MCSV",
            @"C:\safe"));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateRoot_InvokesPureProtectionGuardBeforeAnyFilesystemProbe()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerLayout.cs"));
        var validateStart = source.IndexOf(
            "internal static string ValidateRoot(string root)",
            StringComparison.Ordinal);
        var guardDeclaration = source.IndexOf(
            "internal static void RejectPreservedDataTreePath",
            validateStart,
            StringComparison.Ordinal);
        Assert.True(validateStart >= 0 && guardDeclaration > validateStart);

        var validateBody = source[validateStart..guardDeclaration];
        var guardCall = validateBody.IndexOf("RejectPreservedDataTreePath(root);", StringComparison.Ordinal);
        var driveProbe = validateBody.IndexOf("new DriveInfo", StringComparison.Ordinal);
        var reparseProbe = validateBody.IndexOf("RejectExistingReparsePoints(normalized);", StringComparison.Ordinal);
        Assert.True(guardCall >= 0 && driveProbe > guardCall && reparseProbe > guardCall);

        var reparseDeclaration = source.IndexOf(
            "internal static void RejectExistingReparsePoints",
            guardDeclaration,
            StringComparison.Ordinal);
        Assert.True(reparseDeclaration > guardDeclaration);
        var pureGuard = source[guardDeclaration..reparseDeclaration];
        var rawDeviceGuard = pureGuard.IndexOf(
            "RejectWindowsDeviceOrUncSyntax(rawPath",
            StringComparison.Ordinal);
        var firstFullPath = pureGuard.IndexOf("Path.GetFullPath(rawPath)", StringComparison.Ordinal);
        Assert.True(rawDeviceGuard >= 0 && firstFullPath > rawDeviceGuard);
        Assert.DoesNotContain("new DriveInfo", pureGuard, StringComparison.Ordinal);
        Assert.DoesNotContain("new DirectoryInfo", pureGuard, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", pureGuard, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", pureGuard, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedStream_CannotReadOrSeekOutsidePayload()
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes("beforePAYLOADafter"));
        using var bounded = new BoundedReadStream(source, 6, 7, leaveOpen: true);
        using var reader = new StreamReader(bounded, Encoding.ASCII, leaveOpen: true);

        Assert.Equal("PAYLOAD", reader.ReadToEnd());
        Assert.Throws<IOException>(() => bounded.Seek(8, SeekOrigin.Begin));
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void LocateTrailer_UsesPeCertificateTableInsteadOfSignedFileEof(int alignmentPadding)
    {
        using var executable = CreateSyntheticSignedPe(alignmentPadding, corruptPadding: false);

        var location = InstallerBundle.LocateTrailer(executable);

        Assert.Equal(0x400 - alignmentPadding - InstallerBundle.TrailerLength, location.Offset);
        Assert.Equal(0x400, location.LogicalContentEnd);
        Assert.Equal(alignmentPadding, location.AuthenticodeAlignmentPaddingBytes);
    }

    [Fact]
    public void LocateTrailer_RejectsNonZeroAuthenticodeAlignmentPadding()
    {
        using var executable = CreateSyntheticSignedPe(alignmentPadding: 7, corruptPadding: true);

        var exception = Assert.Throws<InvalidDataException>(() => InstallerBundle.LocateTrailer(executable));

        Assert.Contains("Authenticode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerSource_DoesNotInvokeExternalPowerShell()
    {
        var repository = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repository, "src", "MinecraftServerManager.Installer");
        var source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(sourceRoot, "*.cs").Select(File.ReadAllText));

        Assert.DoesNotContain("powershell.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwsh.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Install-MuhunMcsv.ps1", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FolderBrowserDialog", source, StringComparison.Ordinal);
        Assert.Contains("requireAdministrator", File.ReadAllText(Path.Combine(sourceRoot, "app.manifest")));
        var program = File.ReadAllText(Path.Combine(sourceRoot, "Program.cs"));
        Assert.Contains("[STAThread]", program, StringComparison.Ordinal);
        Assert.True(
            program.IndexOf("GetAwaiter().GetResult()", StringComparison.Ordinal) <
            program.IndexOf("Application.Run(new InstallerForm", StringComparison.Ordinal));
        Assert.Contains("--verify-bundle", program, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerAccessPolicy_DoesNotGrantUsersModifyOnRoot()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));

        Assert.Contains("SetAccessRuleProtection(isProtected: true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuiltinUsersSid", source, StringComparison.Ordinal);
        Assert.Contains("ResolveServiceSid(serviceName)", source, StringComparison.Ordinal);
        Assert.Contains("FileSystemRights.ReadAndExecute", source, StringComparison.Ordinal);
        Assert.Contains("FileSystemRights.Modify", source, StringComparison.Ordinal);
        Assert.Contains("layout.ExchangeRoot", source, StringComparison.Ordinal);
        Assert.Contains("layout.UserRoot", source, StringComparison.Ordinal);
        Assert.Contains("layout.ServiceRoot", source, StringComparison.Ordinal);
        Assert.Contains("--Mcsv:Service:DataRoot=", source, StringComparison.Ordinal);
        Assert.Contains("--Mcsv:Service:ExchangeRoot=", source, StringComparison.Ordinal);
        Assert.Contains("CCDCLCRPWP", source, StringComparison.Ordinal);
        Assert.Contains("IsCurrentIdentityInteractiveShellUser", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FormalPipeline_EmitsOnlyTheSignedSetupExecutableAndRemovesStaging()
    {
        var repository = FindRepositoryRoot();
        var formal = File.ReadAllText(Path.Combine(
            repository,
            "scripts",
            "Build-MuhunMcsvFormalRelease.ps1"));
        var release = File.ReadAllText(Path.Combine(
            repository,
            "scripts",
            "New-MuhunMcsvRelease.ps1"));
        var bundle = File.ReadAllText(Path.Combine(
            repository,
            "scripts",
            "New-MuhunMcsvInstallerBundle.ps1"));
        var verifier = File.ReadAllText(Path.Combine(
            repository,
            "scripts",
            "Test-MuhunMcsvRelease.ps1"));

        Assert.Contains("[bool]$InstallerOnly = $true", formal, StringComparison.Ordinal);
        Assert.Contains("installer-host-win-x64", formal, StringComparison.Ordinal);
        Assert.Contains("$finalFiles.Count -ne 1", formal, StringComparison.Ordinal);
        Assert.Contains("$installerHostRoot", formal, StringComparison.Ordinal);
        Assert.DoesNotContain("$buildCompleted", formal, StringComparison.Ordinal);
        Assert.Contains("if (-not $KeepStaging -and", formal, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::ReadAllText($stagingMarker) -eq 'muhun.mcsv.formal-staging:1'", formal, StringComparison.Ordinal);
        Assert.Contains("Refusing to clean a staging directory outside artifacts/formal-staging", formal, StringComparison.Ordinal);
        Assert.Contains("New-MuhunMcsvInstallerBundle.ps1", release, StringComparison.Ordinal);
        Assert.True(
            release.IndexOf("New-MuhunMcsvInstallerBundle.ps1", StringComparison.Ordinal) <
            release.IndexOf("Set-ProductAuthenticodeSignature -Path $installerPath", StringComparison.Ordinal));
        Assert.True(
            release.IndexOf("Set-ProductAuthenticodeSignature -Path $installerPath", StringComparison.Ordinal) <
            release.IndexOf("'--verify-bundle'", StringComparison.Ordinal));
        Assert.Contains("InstallerVerifierAssemblyPath", formal, StringComparison.Ordinal);
        Assert.Contains("DotNetHostPath", formal, StringComparison.Ordinal);
        Assert.Contains("MCSV-INSTALL-V1!", bundle, StringComparison.Ordinal);
        Assert.Contains("Muhun-MCSV-$($manifest.version)-Setup.exe", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("powershell.exe", bundle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwsh.exe", bundle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerLocksAndProtectsCustomRootBeforeAnyMarkerOrChildIo()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));
        var acquire = source.IndexOf(
            "rootLease = _platform.AcquireInstallRootLease(layout.Root);",
            StringComparison.Ordinal);
        var prepare = source.IndexOf("PrepareOwnedRoot(layout, rootLease);", StringComparison.Ordinal);
        var stage = source.IndexOf("Directory.CreateDirectory(stage);", StringComparison.Ordinal);

        Assert.True(acquire >= 0 && prepare > acquire && stage > prepare);
        Assert.Contains("SetExactDirectoryAcl", source, StringComparison.Ordinal);
        Assert.Contains("SetOwner(AdministratorsSid)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunIcacls", source, StringComparison.Ordinal);
        Assert.Contains("Global\\Muhun.MCSV.Installer\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Global\\Muhun.MCSV.Installer.{layout.Channel}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerRootLeaseRejectsReparseAndLocksEveryAncestorAgainstReplacement()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));

        var acquireStart = source.IndexOf(
            "public IInstallerRootLease AcquireInstallRootLease",
            StringComparison.Ordinal);
        var createDirectoryStart = source.IndexOf(
            "private static void CreateAdministrativeOnlyDirectory",
            acquireStart,
            StringComparison.Ordinal);
        Assert.True(acquireStart >= 0 && createDirectoryStart > acquireStart);
        var acquireBody = source[acquireStart..createDirectoryStart];

        Assert.Contains("var volumeHandle = OpenLockedDirectoryHandle(current);", acquireBody, StringComparison.Ordinal);
        Assert.Contains("ValidateTrustedInstallAncestorAcl(current);", acquireBody, StringComparison.Ordinal);
        Assert.Contains("var handle = TryOpenLockedDirectoryHandle(next);", acquireBody, StringComparison.Ordinal);
        Assert.Contains("CreateAdministrativeOnlyDirectory(next);", acquireBody, StringComparison.Ordinal);
        Assert.Contains("handle = OpenLockedDirectoryHandle(next, requireDelete: true);", acquireBody, StringComparison.Ordinal);
        Assert.Contains("handles.Add(handle);", acquireBody, StringComparison.Ordinal);
        Assert.Contains("ValidateLockedDirectoryHandle(handles[^1], normalizedRoot);", acquireBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SetExactDirectoryAcl", acquireBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SetAdministrativeOnlyDirectoryAcl", acquireBody, StringComparison.Ordinal);

        var lockStart = source.IndexOf("internal static SafeFileHandle OpenLockedDirectoryHandle", StringComparison.Ordinal);
        var markerLockStart = source.IndexOf("internal static SafeFileHandle OpenLockedRegularFileHandle", lockStart, StringComparison.Ordinal);
        Assert.True(lockStart >= 0 && markerLockStart > lockStart);
        var lockBody = source[lockStart..markerLockStart];
        Assert.Contains("FileFlagOpenReparsePoint | FileFlagBackupSemantics", lockBody, StringComparison.Ordinal);
        Assert.Contains("? FileShare.Read | FileShare.Write | FileShare.Delete", lockBody, StringComparison.Ordinal);
        Assert.Contains(": FileShare.Read;", lockBody, StringComparison.Ordinal);

        var leaseStart = source.IndexOf("private sealed class WindowsInstallerRootLease", markerLockStart, StringComparison.Ordinal);
        var createServiceStart = source.IndexOf(
            "public async Task<InstallerServiceSnapshot> CaptureAndStopServiceAsync",
            leaseStart,
            StringComparison.Ordinal);
        Assert.True(leaseStart >= 0 && createServiceStart > leaseStart);
        var leaseBody = source[leaseStart..createServiceStart];
        Assert.Contains("ProtectOwnedRootAndPinExistingDirectories", leaseBody, StringComparison.Ordinal);
        Assert.Contains("TryOpenLockedDirectoryHandle(path)", leaseBody, StringComparison.Ordinal);
        Assert.Contains("CaptureOriginalSecurity(_normalizedRoot", leaseBody, StringComparison.Ordinal);
        Assert.Contains("SetAdministrativeOnlyDirectoryAcl(_normalizedRoot);", leaseBody, StringComparison.Ordinal);
        Assert.Contains("CreateAndProtectMissingDirectories", leaseBody, StringComparison.Ordinal);
        Assert.Contains("CreateAdministrativeOnlyDirectory(path);", leaseBody, StringComparison.Ordinal);
        Assert.Contains("OpenLockedDirectoryHandle(path, requireDelete: true)", leaseBody, StringComparison.Ordinal);
        Assert.Contains("ValidateLockedDirectoryHandle(handle, path);", leaseBody, StringComparison.Ordinal);
        Assert.Contains("_directoryHandles.Add(path, handle", leaseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("FileShare.Write", leaseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("FileShare.Delete", acquireBody, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerRecoversOnlyExactEmptyAdministrativeDefaultRootResidue()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));
        var helperStart = source.IndexOf(
            "internal static bool IsRecoverableDefaultRootResidue",
            StringComparison.Ordinal);
        var helperEnd = source.IndexOf(
            "internal static SafeFileHandle OpenLockedDirectoryHandle",
            helperStart,
            StringComparison.Ordinal);
        Assert.True(helperStart >= 0 && helperEnd > helperStart);
        var helper = source[helperStart..helperEnd];

        Assert.Contains("InstallerLayout.DefaultRoot", helper, StringComparison.Ordinal);
        Assert.Contains("StringComparison.OrdinalIgnoreCase", helper, StringComparison.Ordinal);
        Assert.Contains("RejectExistingReparsePoints(normalized)", helper, StringComparison.Ordinal);
        Assert.Contains("Directory.EnumerateFileSystemEntries(normalized).Any()", helper, StringComparison.Ordinal);
        Assert.Contains("CreateAdministrativeOnlyDirectorySecurity()", helper, StringComparison.Ordinal);
        Assert.Contains(
            "InstallerSecurityDescriptorComparer.EqualsAllowingDaclAutoInherited",
            helper,
            StringComparison.Ordinal);

        var acquireStart = source.IndexOf(
            "public IInstallerRootLease AcquireInstallRootLease",
            StringComparison.Ordinal);
        var createStart = source.IndexOf(
            "private static void CreateAdministrativeOnlyDirectory",
            acquireStart,
            StringComparison.Ordinal);
        var acquire = source[acquireStart..createStart];
        Assert.Contains("ValidateTrustedInstallAncestorAcl(next);", acquire, StringComparison.Ordinal);
        Assert.Contains("else if (IsRecoverableDefaultRootResidue(next))", acquire, StringComparison.Ordinal);
        Assert.Contains("OpenLockedDirectoryHandle(next, requireDelete: true)", acquire, StringComparison.Ordinal);
        Assert.Contains("if (!IsRecoverableDefaultRootResidue(next))", acquire, StringComparison.Ordinal);
        Assert.Contains("rootCreated = true;", acquire, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerValidatesOwnershipBeforeChangingRootAclOrCreatingCanonicalChildren()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));
        var prepareStart = source.IndexOf(
            "private static bool PrepareOwnedRoot",
            StringComparison.Ordinal);
        var readHelperStart = source.IndexOf(
            "private static LockedTextFile? OpenOptionalLockedTextFile",
            prepareStart,
            StringComparison.Ordinal);
        Assert.True(prepareStart >= 0 && readHelperStart > prepareStart);
        var prepare = source[prepareStart..readHelperStart];

        var markerRead = prepare.IndexOf("OpenOptionalLockedTextFile(marker", StringComparison.Ordinal);
        var emptyCheck = prepare.IndexOf("Directory.EnumerateFileSystemEntries(layout.Root).Any()", StringComparison.Ordinal);
        var protect = prepare.IndexOf("rootLease.ProtectOwnedRootAndPinExistingDirectories", StringComparison.Ordinal);
        var postProtectEmptyCheck = prepare.IndexOf(
            "Directory.EnumerateFileSystemEntries(layout.Root).Any()",
            emptyCheck + 1,
            StringComparison.Ordinal);
        var createMissing = prepare.IndexOf("rootLease.CreateAndProtectMissingDirectories();", StringComparison.Ordinal);
        var markerWrite = prepare.IndexOf("WriteAtomicText(", StringComparison.Ordinal);
        Assert.True(
            markerRead >= 0 && emptyCheck > markerRead && protect > emptyCheck &&
            postProtectEmptyCheck > protect && createMissing > postProtectEmptyCheck && markerWrite > createMissing);
        Assert.Contains("layout.VersionsRoot", prepare, StringComparison.Ordinal);
        Assert.Contains("layout.ActivationRoot", prepare, StringComparison.Ordinal);
        Assert.Contains("layout.ServiceRoot", prepare, StringComparison.Ordinal);
        Assert.Contains("layout.ExchangeRoot", prepare, StringComparison.Ordinal);
        Assert.Contains("layout.UserRoot", prepare, StringComparison.Ordinal);
        Assert.Contains("layout.StagingRoot", prepare, StringComparison.Ordinal);
        Assert.Contains("layout.LauncherRoot", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.CreateDirectory(directory)", prepare, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerReadsExistingMarkerOnlyAfterRejectingLinksAndDirectories()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));
        var helperStart = source.IndexOf(
            "private static LockedTextFile? OpenOptionalLockedTextFile",
            StringComparison.Ordinal);
        var nextHelper = source.IndexOf(
            "private static void PrepareRuntimeDirectories",
            helperStart,
            StringComparison.Ordinal);
        Assert.True(helperStart >= 0 && nextHelper > helperStart);
        var helper = source[helperStart..nextHelper];

        var lockedOpen = helper.IndexOf("OpenLockedRegularFileHandle", StringComparison.Ordinal);
        Assert.True(lockedOpen >= 0);
        Assert.DoesNotContain("File.GetAttributes", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllText", helper, StringComparison.Ordinal);
        Assert.Contains("new LockedTextFile", helper, StringComparison.Ordinal);

        var lockedTextStart = source.IndexOf("private sealed class LockedTextFile", StringComparison.Ordinal);
        var nextMethod = source.IndexOf("private static InstallerLauncherRollback", lockedTextStart, StringComparison.Ordinal);
        Assert.True(lockedTextStart >= 0 && nextMethod > lockedTextStart);
        var lockedText = source[lockedTextStart..nextMethod];
        Assert.Contains("_stream.ReadExactly(bytes);", lockedText, StringComparison.Ordinal);
        Assert.Contains("_stream.Position = 0;", lockedText, StringComparison.Ordinal);

        var lockedHelperStart = source.IndexOf(
            "internal static SafeFileHandle OpenLockedRegularFileHandle",
            StringComparison.Ordinal);
        var validationStart = source.IndexOf(
            "private static void ValidateLockedDirectoryHandle",
            lockedHelperStart,
            StringComparison.Ordinal);
        Assert.True(lockedHelperStart >= 0 && validationStart > lockedHelperStart);
        var lockedHelper = source[lockedHelperStart..validationStart];
        Assert.Contains("FileFlagOpenReparsePoint", lockedHelper, StringComparison.Ordinal);
        Assert.Contains("FileShare.Read,", lockedHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("FileShare.Write", lockedHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("FileShare.Delete", lockedHelper, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.ReparsePoint", lockedHelper, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.Directory", lockedHelper, StringComparison.Ordinal);
        Assert.Contains("NormalizeFinalHandlePath(GetFinalPath(handle))", lockedHelper, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerRollbackRestoresAclsAndDeletesNewRootByPinnedHandle()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));
        var catchStart = source.IndexOf("catch (Exception installationError)", StringComparison.Ordinal);
        var finallyStart = source.IndexOf("finally", catchStart, StringComparison.Ordinal);
        Assert.True(catchStart >= 0 && finallyStart > catchStart);
        var rollback = source[catchStart..finallyStart];

        var rollbackProtection = rollback.IndexOf("rootLease.RollbackProtectionChanges();", StringComparison.Ordinal);
        var delete = rollback.IndexOf("rootLease.DeleteNewRootIfEmpty();", StringComparison.Ordinal);
        Assert.True(rollbackProtection >= 0 && delete > rollbackProtection);
        Assert.Contains("rootLease?.Dispose();", source[finallyStart..], StringComparison.Ordinal);
        Assert.Contains("SetFileInformationByHandle(", source, StringComparison.Ordinal);
        Assert.Contains("FileDispositionInfoClass", source, StringComparison.Ordinal);
        Assert.Contains("DeleteEmptyDirectoryByHandle(owned[_rootHandleIndex]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseRootForDeletion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete(normalized, recursive: true)", source, StringComparison.Ordinal);
        Assert.Contains("new DirectoryInfo(pair.Key).SetAccessControl(pair.Value)", source, StringComparison.Ordinal);
        Assert.Contains("CommitProtectionChanges();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerSkipsDescendantPreOpenAfterAParentWasMissing()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));
        var leaseStart = source.IndexOf("private sealed class WindowsInstallerRootLease", StringComparison.Ordinal);
        var serviceStart = source.IndexOf(
            "public async Task<InstallerServiceSnapshot> CaptureAndStopServiceAsync",
            leaseStart,
            StringComparison.Ordinal);
        Assert.True(leaseStart >= 0 && serviceStart > leaseStart);
        var lease = source[leaseStart..serviceStart];

        var missingAncestorCheck = lease.IndexOf("_missingDirectories.Any(missing => path.StartsWith", StringComparison.Ordinal);
        var preOpen = lease.IndexOf("var handle = TryOpenLockedDirectoryHandle(path);", StringComparison.Ordinal);
        Assert.True(missingAncestorCheck >= 0 && preOpen > missingAncestorCheck);
        Assert.Contains("_missingDirectories.Add(path);", lease, StringComparison.Ordinal);
        Assert.Contains("安裝目錄在權限保護期間遭到建立或替換", lease, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerRestartsOldServiceOnlyAfterFileAndAclRollback()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));
        var catchStart = source.IndexOf("catch (Exception installationError)", StringComparison.Ordinal);
        var finallyStart = source.IndexOf("finally", catchStart, StringComparison.Ordinal);
        Assert.True(catchStart >= 0 && finallyStart > catchStart);
        var rollback = source[catchStart..finallyStart];

        var quiesce = rollback.IndexOf("restart: false", StringComparison.Ordinal);
        var aclRollback = rollback.IndexOf("rootLease.RollbackProtectionChanges();", StringComparison.Ordinal);
        var restart = rollback.IndexOf("restart: true", StringComparison.Ordinal);
        Assert.True(quiesce >= 0 && aclRollback > quiesce && restart > aclRollback);
        Assert.Contains("snapshot => serviceSnapshot = snapshot", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await RestoreServiceAsync(name, snapshot, CancellationToken.None)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerAncestorGateSkipsFinalRootButRejectsDangerousParentAcls()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));

        Assert.Contains("else if (!isRoot)", source, StringComparison.Ordinal);
        Assert.Contains("ValidateTrustedInstallAncestorAcl(next);", source, StringComparison.Ordinal);
        Assert.Contains("security.GetOwner(typeof(SecurityIdentifier))", source, StringComparison.Ordinal);
        Assert.Contains("FileSystemRights.DeleteSubdirectoriesAndFiles", source, StringComparison.Ordinal);
        Assert.Contains("FileSystemRights.ChangePermissions", source, StringComparison.Ordinal);
        Assert.Contains("FileSystemRights.TakeOwnership", source, StringComparison.Ordinal);
        Assert.Contains("rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)", source, StringComparison.Ordinal);
        Assert.Contains("自訂安裝位置的父目錄必須已存在", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPathAncestorLeaseRuntimeProbe_IsReadOnlyAndShareCompatible()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        using var volume = WindowsInstallerPlatform.OpenLockedDirectoryHandle(Path.GetPathRoot(programFiles)!);
        using var directory = WindowsInstallerPlatform.OpenLockedDirectoryHandle(programFiles);
        WindowsInstallerPlatform.ValidateTrustedInstallAncestorAcl(Path.GetPathRoot(programFiles)!);
        WindowsInstallerPlatform.ValidateTrustedInstallAncestorAcl(programFiles);

        Assert.False(volume.IsInvalid);
        Assert.False(directory.IsInvalid);
    }

    [Fact]
    public void FreshRootDeleteCapableLease_AllowsAtomicMarkerCommit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-fresh-root-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, ".marker.tmp");
        var marker = Path.Combine(root, ".muhun-mcsv-install-root");
        try
        {
            File.WriteAllText(temporary, "muhun.mcsv.install-root:1\n");
            using (WindowsInstallerPlatform.OpenLockedDirectoryHandle(root, requireDelete: true))
            {
                // This is the exact fresh-root regression: DELETE + FileShare.Read caused
                // File.Move to fail with ERROR_SHARING_VIOLATION at the 3% prepare stage.
                File.Move(temporary, marker, overwrite: false);
            }

            Assert.Equal("muhun.mcsv.install-root:1\n", File.ReadAllText(marker));
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: false);
            }
        }
    }

    [Fact]
    public async Task FreshCanonicalDirectoryLeases_AllowStageVersionActivationMove()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-fresh-root-version-" + Guid.NewGuid().ToString("N"));
        var versions = Path.Combine(root, "versions");
        var staging = Path.Combine(root, "install-staging");
        var stage = Path.Combine(staging, "probe");
        var stagedVersion = Path.Combine(stage, "version");
        var targetVersion = Path.Combine(versions, "1.2.9-beta.9");
        Directory.CreateDirectory(stagedVersion);
        Directory.CreateDirectory(versions);
        File.WriteAllText(Path.Combine(stagedVersion, "payload.txt"), "verified");
        try
        {
            using (WindowsInstallerPlatform.OpenLockedDirectoryHandle(root, requireDelete: true))
            using (WindowsInstallerPlatform.OpenLockedDirectoryHandle(versions, requireDelete: true))
            using (WindowsInstallerPlatform.OpenLockedDirectoryHandle(staging, requireDelete: true))
            {
                await InstallerEngine.MoveStagedVersionWithTransientLockRetryAsync(
                    stagedVersion,
                    targetVersion,
                    CancellationToken.None,
                    timeoutOverride: TimeSpan.FromSeconds(2));
            }

            Assert.Equal("verified", File.ReadAllText(Path.Combine(targetVersion, "payload.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StagedVersionMove_RetriesRealTransientDescendantFileLock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-staged-move-lock-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "install-staging", "transaction", "version");
        var destination = Path.Combine(root, "versions", "1.2.9-beta.9");
        var payload = Path.Combine(source, "Muhun MCSV Manager.exe");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(payload, "verified payload");
        FileStream? lockStream = null;
        Task? delayedRelease = null;
        try
        {
            lockStream = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read);
            var observed = Record.Exception(() => Directory.Move(source, destination));
            Assert.NotNull(observed);
            Assert.True(observed.HResult is
                unchecked((int)0x80070005) or
                unchecked((int)0x80070020) or
                unchecked((int)0x80070021));

            var capturedLock = lockStream;
            delayedRelease = Task.Run(async () =>
            {
                await Task.Delay(250);
                capturedLock.Dispose();
            });
            await InstallerEngine.MoveStagedVersionWithTransientLockRetryAsync(
                source,
                destination,
                CancellationToken.None,
                timeoutOverride: TimeSpan.FromSeconds(5));
            await delayedRelease;

            Assert.False(Directory.Exists(source));
            Assert.Equal("verified payload", File.ReadAllText(Path.Combine(
                destination,
                "Muhun MCSV Manager.exe")));
        }
        finally
        {
            lockStream?.Dispose();
            if (delayedRelease is not null)
            {
                await delayedRelease;
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StagedVersionMove_CancellationDuringBackoffPreservesSource()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-staged-move-cancel-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "install-staging", "transaction", "version");
        var destination = Path.Combine(root, "versions", "1.2.9-beta.9");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var cancellation = new CancellationTokenSource();
        var moveAttempts = 0;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                InstallerEngine.MoveStagedVersionWithTransientLockRetryAsync(
                    source,
                    destination,
                    cancellation.Token,
                    timeoutOverride: TimeSpan.FromSeconds(5),
                    delayAsync: (_, token) =>
                    {
                        cancellation.Cancel();
                        return Task.Delay(TimeSpan.FromSeconds(1), token);
                    },
                    moveDirectory: (_, _) =>
                    {
                        moveAttempts++;
                        throw new IOException("locked", unchecked((int)0x80070020));
                    }));

            Assert.Equal(1, moveAttempts);
            Assert.True(Directory.Exists(source));
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StagedVersionMove_DestinationRaceFailsWithoutRetry()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-staged-move-race-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "install-staging", "transaction", "version");
        var destination = Path.Combine(root, "versions", "1.2.9-beta.9");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var moveAttempts = 0;
        try
        {
            var failure = await Assert.ThrowsAsync<IOException>(() =>
                InstallerEngine.MoveStagedVersionWithTransientLockRetryAsync(
                    source,
                    destination,
                    CancellationToken.None,
                    moveDirectory: (_, _) => moveAttempts++));

            Assert.Contains("已存在", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, moveAttempts);
            Assert.True(Directory.Exists(source));
            Assert.True(Directory.Exists(destination));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StagedVersionMove_CompletedRenameStateContinuesToExistingVerification()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-staged-move-completed-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "install-staging", "transaction", "version");
        var destination = Path.Combine(root, "versions", "1.2.9-beta.9");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(Path.Combine(source, "payload.txt"), "verified");
        try
        {
            await InstallerEngine.MoveStagedVersionWithTransientLockRetryAsync(
                source,
                destination,
                CancellationToken.None,
                moveDirectory: (from, to) =>
                {
                    Directory.Move(from, to);
                    throw new IOException("post-rename lock signal", unchecked((int)0x80070020));
                });

            Assert.False(Directory.Exists(source));
            Assert.Equal("verified", File.ReadAllText(Path.Combine(destination, "payload.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StagedVersionMove_DeadlineReportsLastWin32ErrorAndPreservesSource()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-staged-move-timeout-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "install-staging", "transaction", "version");
        var destination = Path.Combine(root, "versions", "1.2.9-beta.9");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var moveAttempts = 0;
        try
        {
            var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
                InstallerEngine.MoveStagedVersionWithTransientLockRetryAsync(
                    source,
                    destination,
                    CancellationToken.None,
                    timeoutOverride: TimeSpan.Zero,
                    maximumAttempts: 24,
                    moveDirectory: (_, _) =>
                    {
                        moveAttempts++;
                        throw new IOException("locked", unchecked((int)0x80070021));
                    }));

            Assert.Equal(1, moveAttempts);
            Assert.Contains("attempts=1", failure.Message, StringComparison.Ordinal);
            Assert.Contains("elapsedMs=", failure.Message, StringComparison.Ordinal);
            Assert.Contains("lastHResult=0x80070021", failure.Message, StringComparison.Ordinal);
            Assert.Equal(unchecked((int)0x80070021), failure.InnerException?.HResult);
            Assert.True(Directory.Exists(source));
            Assert.False(Directory.Exists(destination));

            moveAttempts = 0;
            var capped = await Assert.ThrowsAsync<TimeoutException>(() =>
                InstallerEngine.MoveStagedVersionWithTransientLockRetryAsync(
                    source,
                    destination,
                    CancellationToken.None,
                    timeoutOverride: TimeSpan.FromSeconds(5),
                    maximumAttempts: 3,
                    delayAsync: (_, _) => Task.CompletedTask,
                    moveDirectory: (_, _) =>
                    {
                        moveAttempts++;
                        throw new IOException("locked", unchecked((int)0x80070020));
                    }));
            Assert.Equal(3, moveAttempts);
            Assert.Contains("attempts=3", capped.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StagedVersionMove_NonTransientFailureIsNotRetried()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-staged-move-fatal-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "install-staging", "transaction", "version");
        var destination = Path.Combine(root, "versions", "1.2.9-beta.9");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var moveAttempts = 0;
        try
        {
            var injected = new IOException("not transient", unchecked((int)0x80070003));
            var failure = await Assert.ThrowsAsync<IOException>(() =>
                InstallerEngine.MoveStagedVersionWithTransientLockRetryAsync(
                    source,
                    destination,
                    CancellationToken.None,
                    moveDirectory: (_, _) =>
                    {
                        moveAttempts++;
                        throw injected;
                    }));

            Assert.Same(injected, failure);
            Assert.Equal(1, moveAttempts);
            Assert.True(Directory.Exists(source));
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ExistingActivePointerLeaseIsReleasedBeforeAtomicReplacementButKeepsAclSnapshot()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));
        var releaseCall = source.IndexOf(
            "rootLease.ReleaseFileForReplacement(activePointer);",
            StringComparison.Ordinal);
        var replacement = source.IndexOf(
            "WriteAtomicText(activePointer, bundle.Metadata.Version",
            releaseCall,
            StringComparison.Ordinal);
        var mutationRecorded = source.IndexOf("pointerCommitted = true;", replacement, StringComparison.Ordinal);
        var applyAcl = source.IndexOf(
            "_platform.ApplyActivePointerAccessControl(layout, ServiceName);",
            mutationRecorded,
            StringComparison.Ordinal);
        Assert.True(
            releaseCall >= 0 && replacement > releaseCall &&
            mutationRecorded > replacement && applyAcl > mutationRecorded);

        var methodStart = source.IndexOf(
            "public void ReleaseFileForReplacement(string file)",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "public void CommitProtectionChanges()",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("ValidateLockedRegularFileHandle(handle, normalized);", method, StringComparison.Ordinal);
        Assert.Contains("_fileHandles.Remove(normalized, out var handle)", method, StringComparison.Ordinal);
        Assert.Contains("handle.Dispose();", method, StringComparison.Ordinal);
        Assert.DoesNotContain("_originalFileSecurity.Remove", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingActivePointerRuntimeProbe_ValidatedReleaseAllowsAtomicReplacement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-existing-pointer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pointer = Path.Combine(root, "active-version.v1");
        var replacement = Path.Combine(root, ".active-version.v1.tmp");
        try
        {
            File.WriteAllText(pointer, "1.2.9-beta.8\n");
            File.WriteAllText(replacement, "1.2.9-beta.9\n");
            var handle = WindowsInstallerPlatform.OpenLockedRegularFileHandle(pointer);
            try
            {
                var blocked = Record.Exception(
                    () => File.Move(replacement, pointer, overwrite: true));
                Assert.NotNull(blocked);
                Assert.True(
                    blocked is IOException or UnauthorizedAccessException,
                    $"Unexpected lock failure type: {blocked.GetType().FullName}");
                Assert.True(
                    blocked.HResult == unchecked((int)0x80070020) ||
                    blocked.HResult == unchecked((int)0x80070005),
                    $"Unexpected lock failure HRESULT: 0x{blocked.HResult:X8}");
            }
            finally
            {
                handle.Dispose();
            }

            File.Move(replacement, pointer, overwrite: true);
            Assert.Equal("1.2.9-beta.9\n", File.ReadAllText(pointer));
        }
        finally
        {
            if (File.Exists(replacement))
            {
                File.Delete(replacement);
            }
            if (File.Exists(pointer))
            {
                File.Delete(pointer);
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: false);
            }
        }
    }

    [Fact]
    public void InstallerFailureReportsStageAndPathWithoutClaimingCleanupSucceeded()
    {
        var path = Path.Combine(Path.GetTempPath(), "muhun-mcsv-stage-diagnostic");
        var failure = new InstallerStageException(
            "準備受管理安裝根目錄",
            path,
            rollbackHadErrors: false,
            new IOException("The process cannot access the file."));

        Assert.Equal("準備受管理安裝根目錄", failure.Stage);
        Assert.Equal(path, failure.TargetPath);
        Assert.Contains("準備受管理安裝根目錄", failure.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(path), failure.Message, StringComparison.Ordinal);
        Assert.Contains("不宣稱所有暫存均已刪除", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("暫存已安全清除", failure.Message, StringComparison.Ordinal);

        var formSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MinecraftServerManager.Installer",
            "InstallerForm.cs"));
        Assert.DoesNotContain("未完成的暫存已安全清除", formSource, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerCommitsOnlyAfterActivationReadyAndRollsBackNewService()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));
        var start = source.IndexOf("StartServiceAsync", StringComparison.Ordinal);
        var health = source.IndexOf("WaitForServiceHealthAsync", start, StringComparison.Ordinal);
        var commit = source.IndexOf(
            "WriteAtomicText(activePointer, bundle.Metadata.Version",
            health,
            StringComparison.Ordinal);

        Assert.True(start >= 0 && health > start && commit > health);
        Assert.Contains("activation-ready", source, StringComparison.Ordinal);
        Assert.Contains("installationId", source, StringComparison.Ordinal);
        Assert.Contains("if (!snapshot.Existed)", source, StringComparison.Ordinal);
        Assert.Contains("[\"delete\", name]", source, StringComparison.Ordinal);
        Assert.Contains("ValidateOwnedServiceImagePath", source, StringComparison.Ordinal);
        Assert.Contains("if (snapshot.WasRunning)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerAvoidsBrokenAppsAndFeaturesRegistration()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));

        Assert.Contains("PublisherCertificateSha256", source, StringComparison.Ordinal);
        Assert.Contains("muhun.mcsv.manager", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MuhunMCSV",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedResolversAllowOnlySafeLocalFixedNtfsRootsInsteadOfHardCodingProgramFiles()
    {
        var repository = FindRepositoryRoot();
        var updater = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Updater",
            "ProductManagedInstallationResolver.cs"));
        var app = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.App",
            "Services",
            "ProtectedFormalReleaseStaging.cs"));

        Assert.DoesNotContain("ValidateProgramFiles", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("ValidateProgramFiles", app, StringComparison.Ordinal);
        Assert.Contains("DriveType.Fixed", updater, StringComparison.Ordinal);
        Assert.Contains("DriveType.Fixed", app, StringComparison.Ordinal);
        Assert.Contains("DriveFormat", updater, StringComparison.Ordinal);
        Assert.Contains("DriveFormat", app, StringComparison.Ordinal);
        Assert.Contains("ValidateInstallMarker(installRoot)", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellIntegrationFailureAfterRegistrationRestoresExistingRegistrationAndShortcut()
    {
        var platform = new FailingShellIntegrationPlatform
        {
            RegistrationState = "old-version",
            ShortcutState = Encoding.UTF8.GetBytes("old-shortcut"),
            FailShortcutMutation = true,
        };
        InstallerShellIntegrationRollback? captured = null;

        Assert.Throws<IOException>(() => InstallerShellIntegrationTransaction.Apply(
            platform,
            CreateSyntheticLayout(),
            "new-version",
            @"C:\Program Files\MCSV\versions\new\gui.exe",
            @"C:\Program Files\MCSV\launcher\updater.exe",
            snapshot => captured = snapshot));

        Assert.NotNull(captured);
        Assert.Equal("old-version", platform.RegistrationState);
        Assert.Equal("old-shortcut", Encoding.UTF8.GetString(platform.ShortcutState!));
    }

    [Fact]
    public void ShellIntegrationSnapshotIsAvailableForOuterRetryWhenFirstRestoreFails()
    {
        var platform = new FailingShellIntegrationPlatform
        {
            RegistrationState = "old-version",
            ShortcutState = Encoding.UTF8.GetBytes("old-shortcut"),
            FailShortcutMutation = true,
            RemainingShortcutRestoreFailures = 1,
        };
        InstallerShellIntegrationRollback? captured = null;

        Assert.Throws<AggregateException>(() => InstallerShellIntegrationTransaction.Apply(
            platform,
            CreateSyntheticLayout(),
            "new-version",
            @"C:\Program Files\MCSV\versions\new\gui.exe",
            @"C:\Program Files\MCSV\launcher\updater.exe",
            snapshot => captured = snapshot));

        Assert.NotNull(captured);
        Assert.Equal("old-version", platform.RegistrationState);
        Assert.NotEqual("old-shortcut", Encoding.UTF8.GetString(platform.ShortcutState!));
        InstallerShellIntegrationTransaction.Restore(platform, captured);
        Assert.Equal("old-version", platform.RegistrationState);
        Assert.Equal("old-shortcut", Encoding.UTF8.GetString(platform.ShortcutState!));
    }

    [Fact]
    public void LockedRegularFileHandleRejectsExistingWriterAndBlocksNewWriteOrDelete()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "mcsv-file-lease", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "payload.bin");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            using (var writer = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.ThrowsAny<Exception>(() =>
                    WindowsInstallerPlatform.OpenLockedRegularFileHandle(path));
            }

            using (WindowsInstallerPlatform.OpenLockedRegularFileHandle(path))
            {
                using (var reader = new FileStream(
                           path,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read))
                {
                    Assert.Equal(1, reader.ReadByte());
                }
                Assert.ThrowsAny<IOException>(() => new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.Read));
                Assert.ThrowsAny<IOException>(() => File.Delete(path));
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: false);
            }
        }
    }

    [Fact]
    public void LockedRegularFileHandleRejectsNtfsHardLinks()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "mcsv-hardlink-lease", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "payload.bin");
        var link = Path.Combine(directory, "outside-link.bin");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            Assert.True(
                CreateHardLinkW(link, path, IntPtr.Zero),
                $"CreateHardLinkW failed: {Marshal.GetLastWin32Error()}");
            Assert.Throws<IOException>(() =>
                WindowsInstallerPlatform.OpenLockedRegularFileHandle(path));
        }
        finally
        {
            if (File.Exists(link))
            {
                File.Delete(link);
            }
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: false);
            }
        }
    }

    [Fact]
    public void ExistingVersionTreeIsPinnedAndHardenedBeforeVerificationAndRuntimeLeavesGetFinalAcls()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));
        var existingBranch = source.IndexOf("if (Directory.Exists(targetVersion))", StringComparison.Ordinal);
        var pin = source.IndexOf("rootLease.PinAndHardenExistingVersionTree(targetVersion);", existingBranch, StringComparison.Ordinal);
        var verify = source.IndexOf("ProductInstalledVersionVerifier.VerifyAsync(", pin, StringComparison.Ordinal);
        Assert.True(existingBranch >= 0 && pin > existingBranch && verify > pin);
        Assert.Contains("OpenLockedRegularFileHandle(path)", source, StringComparison.Ordinal);
        Assert.Contains("_originalFileSecurity", source, StringComparison.Ordinal);
        Assert.Contains("EnumerateVersionNamespace", source, StringComparison.Ordinal);
        Assert.Contains("maximumEntries", source, StringComparison.Ordinal);
        Assert.Contains("maximumDepth", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var name in InstallerEngine.ServiceRuntimeDirectoryNames)", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var name in InstallerEngine.UserRuntimeDirectoryNames)", source, StringComparison.Ordinal);
        var move = source.IndexOf(
            "MoveStagedVersionWithTransientLockRetryAsync(",
            existingBranch,
            StringComparison.Ordinal);
        var pinNew = source.IndexOf("rootLease.PinAndHardenNewVersionTree(targetVersion);", move, StringComparison.Ordinal);
        var reverifyNew = source.IndexOf("ProductInstalledVersionVerifier.VerifyAsync(", pinNew, StringComparison.Ordinal);
        Assert.True(move > existingBranch && pinNew > move && reverifyNew > pinNew);
    }

    [Fact]
    public void ExistingServiceIsCapturedAndStoppedBeforeRootAclMutation()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));
        var acquire = source.IndexOf("rootLease = _platform.AcquireInstallRootLease", StringComparison.Ordinal);
        var trust = source.IndexOf("rootLease.ValidateAndPinExistingManagedInstallation(", acquire, StringComparison.Ordinal);
        var stop = source.IndexOf("_platform.CaptureAndStopServiceAsync(", trust, StringComparison.Ordinal);
        var protect = source.IndexOf("PrepareOwnedRoot(layout, rootLease);", stop, StringComparison.Ordinal);
        var configure = source.IndexOf("_platform.ConfigureServiceAsync(", protect, StringComparison.Ordinal);
        Assert.True(acquire >= 0 && trust > acquire && stop > trust && protect > stop && configure > protect);
        Assert.Contains("ValidateExactDirectoryAcl(pair.Key, pair.Value);", source, StringComparison.Ordinal);
        Assert.Contains("ValidateExactFileAcl(", source, StringComparison.Ordinal);
        Assert.Contains("!rootLease.RootCreated", source, StringComparison.Ordinal);
        Assert.Contains("請改選尚不存在的新子目錄", source, StringComparison.Ordinal);

        var catchStart = source.IndexOf("catch (Exception installationError)", StringComparison.Ordinal);
        var catchEnd = source.IndexOf("finally", catchStart, StringComparison.Ordinal);
        var rollback = source[catchStart..catchEnd];
        var quiescedGate = rollback.IndexOf("if (serviceQuiesced)", StringComparison.Ordinal);
        var destructive = rollback.IndexOf("RestoreLauncherTransaction", StringComparison.Ordinal);
        var restart = rollback.IndexOf("restart: true", StringComparison.Ordinal);
        Assert.True(quiescedGate >= 0 && destructive > quiescedGate && restart > destructive);
        Assert.Contains("serviceQuiesced && rollbackErrors.Count == 0", rollback, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingBetaRootAllowsFirstStableChannelDirectoriesToBeMissing()
    {
        var stable = CreateSyntheticLayout() with
        {
            Channel = "stable",
            ServiceRoot = @"C:\Program Files\MCSV\service\stable",
            ExchangeRoot = @"C:\Program Files\MCSV\exchange\stable",
            UserRoot = @"C:\Program Files\MCSV\users\S-1-5-21-1-2-3-1001\stable",
        };

        Assert.True(WindowsInstallerPlatform.IsRequiredSharedManagedDirectory(stable, stable.Root));
        Assert.True(WindowsInstallerPlatform.IsRequiredSharedManagedDirectory(stable, stable.VersionsRoot));
        Assert.True(WindowsInstallerPlatform.IsRequiredSharedManagedDirectory(
            stable,
            Path.GetDirectoryName(stable.ServiceRoot)!));
        Assert.False(WindowsInstallerPlatform.IsRequiredSharedManagedDirectory(stable, stable.ServiceRoot));
        Assert.False(WindowsInstallerPlatform.IsRequiredSharedManagedDirectory(stable, stable.ExchangeRoot));
        Assert.False(WindowsInstallerPlatform.IsRequiredSharedManagedDirectory(stable, stable.UserRoot));

        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));
        Assert.Contains("_prevalidatedMissingDirectories.Add(pair.Key);", source, StringComparison.Ordinal);
        Assert.Contains("安裝目錄在嚴格信任檢查後遭到建立或替換", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorAccessFreshProvisionAndRollbackAreTransactional()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-operator-fresh-" + Guid.NewGuid().ToString("N"));
        var layout = CreateOperatorTestLayout(root);
        var groups = new RecordingLocalGroupManager();
        var installerSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        InstallerOperatorAccessRollback? captured = null;
        try
        {
            var rollback = InstallerOperatorAccessTransaction.Provision(
                groups,
                layout,
                installerSid,
                snapshot => captured = snapshot);

            Assert.Same(rollback, captured);
            Assert.True(rollback.GroupCreated);
            Assert.True(rollback.MemberAdded);
            Assert.Equal(groups.GroupSid.Value, rollback.GroupSid);
            Assert.Contains(installerSid, groups.Members);
            Assert.Equal(
                installerSid.Value + Environment.NewLine,
                File.ReadAllText(rollback.BindingPath, Encoding.ASCII));

            InstallerOperatorAccessTransaction.Restore(groups, rollback);

            Assert.Null(groups.Group);
            Assert.False(File.Exists(rollback.BindingPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void OperatorAccessRollbackRestoresExactExistingBindingAndMembership()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-operator-existing-" + Guid.NewGuid().ToString("N"));
        var layout = CreateOperatorTestLayout(root);
        var groups = new RecordingLocalGroupManager(createGroup: false);
        var installerSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var previous = Encoding.ASCII.GetBytes("S-1-5-21-1-2-3-1002\r\n");
        var binding = Path.Combine(layout.ServiceRoot, "data", "installer-operator-sid.v1");
        Directory.CreateDirectory(Path.GetDirectoryName(binding)!);
        File.WriteAllBytes(binding, previous);
        try
        {
            var rollback = InstallerOperatorAccessTransaction.Provision(
                groups,
                layout,
                installerSid,
                _ => { });
            Assert.False(rollback.GroupCreated);
            Assert.True(rollback.BindingExisted);
            Assert.True(rollback.MemberAdded);

            InstallerOperatorAccessTransaction.Restore(groups, rollback);

            Assert.NotNull(groups.Group);
            Assert.DoesNotContain(installerSid, groups.Members);
            Assert.Equal(previous, File.ReadAllBytes(binding));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void OperatorAccessRollbackRefusesExternallyReplacedBindingButStillRestoresGroup()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-operator-race-" + Guid.NewGuid().ToString("N"));
        var layout = CreateOperatorTestLayout(root);
        var groups = new RecordingLocalGroupManager();
        var installerSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        try
        {
            var rollback = InstallerOperatorAccessTransaction.Provision(
                groups,
                layout,
                installerSid,
                _ => { });
            var external = Encoding.ASCII.GetBytes("S-1-5-21-1-2-3-1999\n");
            File.WriteAllBytes(rollback.BindingPath, external);

            var failure = Assert.Throws<AggregateException>(() =>
                InstallerOperatorAccessTransaction.Restore(groups, rollback));

            Assert.Contains(
                failure.InnerExceptions,
                error => error.Message.Contains("外部變更", StringComparison.Ordinal));
            Assert.Equal(external, File.ReadAllBytes(rollback.BindingPath));
            Assert.Null(groups.Group);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void OperatorAccessWriteFailureLeavesExistingBindingUntouchedAndRollbackRemainsExact()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-operator-write-failure-" + Guid.NewGuid().ToString("N"));
        var layout = CreateOperatorTestLayout(root);
        var groups = new RecordingLocalGroupManager();
        var installerSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var previous = Encoding.ASCII.GetBytes("S-1-5-21-1-2-3-1002\r\n");
        var binding = Path.Combine(layout.ServiceRoot, "data", "installer-operator-sid.v1");
        Directory.CreateDirectory(Path.GetDirectoryName(binding)!);
        File.WriteAllBytes(binding, previous);
        InstallerOperatorAccessRollback? captured = null;
        try
        {
            Assert.Throws<IOException>(() => InstallerOperatorAccessTransaction.Provision(
                groups,
                layout,
                installerSid,
                snapshot => captured = snapshot,
                (_, _) => throw new IOException("Injected pre-commit write failure.")));

            var rollback = Assert.IsType<InstallerOperatorAccessRollback>(captured);
            Assert.Null(rollback.InstalledBindingContent);
            Assert.Equal(previous, File.ReadAllBytes(binding));

            InstallerOperatorAccessTransaction.Restore(groups, rollback);
            Assert.Equal(previous, File.ReadAllBytes(binding));
            Assert.Null(groups.Group);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void OperatorAccessRollbackNeverDeletesNameOnlyGroupWhenCreatedSidWasNotCaptured()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-operator-no-sid-" + Guid.NewGuid().ToString("N"));
        var layout = CreateOperatorTestLayout(root);
        var groups = new RecordingLocalGroupManager { FailAfterCreatingGroup = true };
        InstallerOperatorAccessRollback? captured = null;
        try
        {
            Assert.Throws<IOException>(() => InstallerOperatorAccessTransaction.Provision(
                groups,
                layout,
                new SecurityIdentifier("S-1-5-21-1-2-3-1001"),
                snapshot => captured = snapshot));

            var rollback = Assert.IsType<InstallerOperatorAccessRollback>(captured);
            Assert.True(rollback.GroupCreated);
            Assert.Null(rollback.GroupSid);
            Assert.Throws<AggregateException>(() =>
                InstallerOperatorAccessTransaction.Restore(groups, rollback));
            Assert.NotNull(groups.Group);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void OperatorAccessRollbackPreservesPreexistingMembership()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-operator-member-" + Guid.NewGuid().ToString("N"));
        var layout = CreateOperatorTestLayout(root);
        var groups = new RecordingLocalGroupManager(createGroup: false);
        var installerSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        groups.Members.Add(installerSid);
        try
        {
            var rollback = InstallerOperatorAccessTransaction.Provision(
                groups,
                layout,
                installerSid,
                _ => { });
            Assert.False(rollback.MemberAdded);

            InstallerOperatorAccessTransaction.Restore(groups, rollback);

            Assert.Contains(installerSid, groups.Members);
            Assert.NotNull(groups.Group);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void OperatorAccessRollbackPreservesFreshGroupWhenExternalMemberAppears()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-operator-external-member-" + Guid.NewGuid().ToString("N"));
        var layout = CreateOperatorTestLayout(root);
        var groups = new RecordingLocalGroupManager();
        var installerSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var externalSid = new SecurityIdentifier("S-1-5-21-1-2-3-1999");
        try
        {
            var rollback = InstallerOperatorAccessTransaction.Provision(
                groups,
                layout,
                installerSid,
                _ => { });
            groups.Members.Add(externalSid);

            Assert.Throws<AggregateException>(() =>
                InstallerOperatorAccessTransaction.Restore(groups, rollback));

            Assert.NotNull(groups.Group);
            Assert.DoesNotContain(installerSid, groups.Members);
            Assert.Contains(externalSid, groups.Members);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void InstallerProvisionsOperatorBoundaryBeforeServiceActivationAndRollsItBackWhileQuiesced()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerEngine.cs"));
        var runtime = source.IndexOf("PrepareRuntimeDirectories(layout", StringComparison.Ordinal);
        var provision = source.IndexOf("_platform.ProvisionOperatorAccess(", runtime, StringComparison.Ordinal);
        var configure = source.IndexOf("_platform.ConfigureServiceAsync(", provision, StringComparison.Ordinal);
        var harden = source.IndexOf("_platform.HardenOperatorBindingAccess(", configure, StringComparison.Ordinal);
        var access = source.IndexOf("_platform.ApplyAccessControl(", harden, StringComparison.Ordinal);
        var start = source.IndexOf("_platform.StartServiceAsync(", access, StringComparison.Ordinal);
        var health = source.IndexOf("_platform.WaitForServiceHealthAsync(", start, StringComparison.Ordinal);
        Assert.True(
            runtime >= 0 && provision > runtime && configure > provision && harden > configure &&
            access > harden && start > access && health > start);

        var catchStart = source.IndexOf("catch (Exception installationError)", StringComparison.Ordinal);
        var quiesced = source.IndexOf("if (serviceQuiesced)", catchStart, StringComparison.Ordinal);
        var restoreOperator = source.IndexOf(
            "_platform.RestoreOperatorAccess(operatorAccessRollback);",
            quiesced,
            StringComparison.Ordinal);
        var restartPrevious = source.IndexOf("restart: true", restoreOperator, StringComparison.Ordinal);
        Assert.True(quiesced >= 0 && restoreOperator > quiesced && restartPrevious > restoreOperator);
    }

    [Fact]
    public void NativeOperatorProvisioningUsesNetApiAndNeverLaunchesPowerShell()
    {
        var repository = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Installer",
            "InstallerOperatorAccess.cs"));
        var service = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "MinecraftServerManager.Service",
            "ProductNamedPipeFactory.cs"));

        Assert.Contains("NetLocalGroupAdd(", installer, StringComparison.Ordinal);
        Assert.Contains("NetLocalGroupAddMembers(", installer, StringComparison.Ordinal);
        Assert.Contains("NetLocalGroupDelMembers(", installer, StringComparison.Ordinal);
        Assert.Contains("NetLocalGroupDel(", installer, StringComparison.Ordinal);
        Assert.Contains("DefaultDllImportSearchPaths(DllImportSearchPath.System32)", installer, StringComparison.Ordinal);
        Assert.Contains("UIntPtr resume = UIntPtr.Zero", installer, StringComparison.Ordinal);
        Assert.Contains("MaximumPreferredLength = 64 * 1024", installer, StringComparison.Ordinal);
        Assert.Contains("status is not Success and not ErrorMoreData", installer, StringComparison.Ordinal);
        Assert.Contains("if (entriesRead > 0 && buffer == IntPtr.Zero)", installer, StringComparison.Ordinal);
        Assert.Contains("NetApiBufferFree(buffer)", installer, StringComparison.Ordinal);
        Assert.Contains("IntendedBindingContent = intendedBinding", installer, StringComparison.Ordinal);
        Assert.Contains("rollback.BindingWriteAttempted = true;", installer, StringComparison.Ordinal);
        Assert.Contains("beforeCommit?.Invoke(intendedSecurityDescriptor);", installer, StringComparison.Ordinal);
        Assert.Contains("rollback.InstalledBindingContent = intendedBinding;", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("powershell", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ProductLocalIpcAccess.InstallerOperatorSidRelativePath", service, StringComparison.Ordinal);
        Assert.Contains(InstallerOperatorAccessTransaction.GroupName, service, StringComparison.Ordinal);
    }

    private static InstallerLayout CreateOperatorTestLayout(string root)
        => new(
            root,
            "beta",
            Path.Combine(root, "versions"),
            Path.Combine(root, "activation-state"),
            Path.Combine(root, "service", "beta"),
            Path.Combine(root, "exchange", "beta"),
            Path.Combine(root, "users", "S-1-5-21-1-2-3-1001", "beta"),
            Path.Combine(root, "install-staging"),
            Path.Combine(root, "launcher"));

    private static InstallerLayout CreateSyntheticLayout()
    {
        const string root = @"C:\Program Files\MCSV";
        return new InstallerLayout(
            root,
            "beta",
            Path.Combine(root, "versions"),
            Path.Combine(root, "activation-state"),
            Path.Combine(root, "service", "beta"),
            Path.Combine(root, "exchange", "beta"),
            Path.Combine(root, "users", "S-1-5-21-1-2-3-1001", "beta"),
            Path.Combine(root, "install-staging"),
            Path.Combine(root, "launcher"));
    }

    private static Task<T> RunOnDedicatedThreadAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static InstallerMutexSecurityPolicy CreateTestMutexSecurityPolicy()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User
            ?? throw new InvalidOperationException("The test identity does not have a user SID.");
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        security.AddAccessRule(new MutexAccessRule(
            currentUser,
            MutexRights.FullControl,
            AccessControlType.Allow));
        var expected = new HashSet<SecurityIdentifier> { currentUser };
        return new InstallerMutexSecurityPolicy(security, expected, expected);
    }

    private sealed class RecordingLocalGroupManager : IInstallerLocalGroupManager
    {
        public RecordingLocalGroupManager(bool createGroup = true)
        {
            GroupSid = new SecurityIdentifier("S-1-5-21-1-2-3-2001");
            if (!createGroup)
            {
                Group = new InstallerLocalGroupIdentity(
                    InstallerOperatorAccessTransaction.GroupName,
                    InstallerOperatorAccessTransaction.GroupDescription,
                    GroupSid,
                    Created: false);
            }
        }

        public SecurityIdentifier GroupSid { get; }
        public InstallerLocalGroupIdentity? Group { get; private set; }
        public HashSet<SecurityIdentifier> Members { get; } = [];
        public bool FailAfterCreatingGroup { get; init; }

        public InstallerLocalGroupIdentity EnsureGroup(
            string name,
            string description,
            Action groupCreated)
        {
            if (Group is not null)
            {
                return Group with { Created = false };
            }
            Group = new InstallerLocalGroupIdentity(name, description, GroupSid, Created: true);
            groupCreated();
            if (FailAfterCreatingGroup)
            {
                throw new IOException("Injected SID re-query failure after group creation.");
            }
            return Group;
        }

        public InstallerLocalGroupIdentity? TryGetGroup(string name)
            => Group is not null && string.Equals(Group.Name, name, StringComparison.Ordinal)
                ? Group with { Created = false }
                : null;

        public IReadOnlyList<SecurityIdentifier> GetMembers(string name)
        {
            _ = TryGetGroup(name)
                ?? throw new IOException("Group is missing.");
            return Members.ToArray();
        }

        public bool AddMember(string name, SecurityIdentifier memberSid)
        {
            _ = TryGetGroup(name)
                ?? throw new IOException("Group is missing.");
            return Members.Add(memberSid);
        }

        public void RemoveMember(string name, SecurityIdentifier memberSid)
        {
            _ = TryGetGroup(name)
                ?? throw new IOException("Group is missing.");
            Members.Remove(memberSid);
        }

        public void DeleteGroup(string name)
        {
            _ = TryGetGroup(name)
                ?? throw new IOException("Group is missing.");
            if (Members.Count != 0)
            {
                throw new IOException("Group is not empty.");
            }
            Group = null;
        }
    }

    private sealed class FailingShellIntegrationPlatform : IInstallerShellIntegrationPlatform
    {
        public string? RegistrationState { get; set; }
        public byte[]? ShortcutState { get; set; }
        public bool FailShortcutMutation { get; init; }
        public int RemainingShortcutRestoreFailures { get; set; }

        public InstallerRegistrationSnapshot CaptureInstallationRegistration()
        {
            var existed = RegistrationState is not null;
            return new InstallerRegistrationSnapshot(
                existed,
                new Dictionary<string, InstallerRegistryValueSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Version"] = new InstallerRegistryValueSnapshot(
                        existed,
                        RegistrationState,
                        existed ? RegistryValueKind.String : RegistryValueKind.Unknown),
                });
        }

        public InstallerShortcutSnapshot CaptureStartMenuShortcut(string channel)
            => new(
                @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\X MCSV",
                $@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\X MCSV\X MCSV ({channel}).lnk",
                ProductDirectoryExisted: true,
                ShortcutState is not null,
                ShortcutState?.ToArray());

        public void WriteInstallationRegistration(
            InstallerLayout layout,
            string version,
            string guiExecutable,
            string launcherExecutable)
            => RegistrationState = version;

        public void CreateStartMenuShortcut(string launcherExecutable, string installRoot, string channel)
        {
            ShortcutState = Encoding.UTF8.GetBytes("new-shortcut");
            if (FailShortcutMutation)
            {
                throw new IOException("Injected shortcut failure.");
            }
        }

        public void RestoreInstallationRegistration(InstallerRegistrationSnapshot snapshot)
        {
            RegistrationState = snapshot.KeyExisted
                ? snapshot.Values["Version"].Value as string
                : null;
        }

        public void RestoreStartMenuShortcut(InstallerShortcutSnapshot snapshot)
        {
            if (RemainingShortcutRestoreFailures-- > 0)
            {
                throw new IOException("Injected first restore failure.");
            }
            ShortcutState = snapshot.ShortcutExisted ? snapshot.Content?.ToArray() : null;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var cursor = new DirectoryInfo(AppContext.BaseDirectory); cursor is not null; cursor = cursor.Parent)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "MinecraftServerManager.sln")))
            {
                return cursor.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private static MemoryStream CreateSyntheticSignedPe(int alignmentPadding, bool corruptPadding)
    {
        const int peHeaderOffset = 0x80;
        const int optionalHeaderOffset = peHeaderOffset + 24;
        const int optionalHeaderSize = 0xf0;
        const int certificateOffset = 0x400;
        const int certificateSize = 0x80;
        var bytes = new byte[certificateOffset + certificateSize];

        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0), 0x5a4d);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3c), peHeaderOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(peHeaderOffset), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peHeaderOffset + 4), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peHeaderOffset + 20), optionalHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(optionalHeaderOffset), 0x20b);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optionalHeaderOffset + 108), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optionalHeaderOffset + 144), certificateOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optionalHeaderOffset + 148), certificateSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(certificateOffset), certificateSize);

        var trailerOffset = certificateOffset - alignmentPadding - InstallerBundle.TrailerLength;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(trailerOffset), 64);
        Encoding.ASCII.GetBytes(InstallerBundle.TrailerMagic)
            .CopyTo(bytes.AsSpan(trailerOffset + 40));
        if (corruptPadding)
        {
            bytes[trailerOffset + InstallerBundle.TrailerLength] = 0x5a;
        }

        return new MemoryStream(bytes, writable: false);
    }
}
