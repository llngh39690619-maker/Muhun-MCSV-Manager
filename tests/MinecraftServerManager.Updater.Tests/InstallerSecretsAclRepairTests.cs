using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using MinecraftServerManager.Installer;
using MinecraftServerManager.Updater;

namespace MinecraftServerManager.Updater.Tests;

public sealed class InstallerSecretsAclRepairTests
{
    private static readonly string InstallerSource = File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "src",
        "MinecraftServerManager.Installer",
        "InstallerEngine.cs"));
    private static readonly SecurityIdentifier LocalSystemSid =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier ServiceSid =
        new("S-1-5-80-1-2-3-4-5");
    private static readonly SecurityIdentifier CurrentUserSid =
        new("S-1-5-21-1-2-3-1001");

    [Fact]
    public void UpgradeRepairsEligibleSecretsAclOnlyAfterTheExistingServiceIsStopped()
    {
        var validate = InstallerSource.IndexOf(
            "rootLease.ValidateAndPinExistingManagedInstallation(",
            StringComparison.Ordinal);
        var stop = InstallerSource.IndexOf(
            "_platform.CaptureAndStopServiceAsync(",
            validate,
            StringComparison.Ordinal);
        var begin = InstallerSource.IndexOf(
            "rootLease.BeginRecoverableSecretsAclRemediation(",
            stop,
            StringComparison.Ordinal);
        var disable = InstallerSource.IndexOf(
            "_platform.DisableServiceForSecurityRemediationAsync(",
            begin,
            StringComparison.Ordinal);
        var repair = InstallerSource.IndexOf(
            "rootLease.RepairRecoverableSecretsAclDriftAfterServiceStopped();",
            disable,
            StringComparison.Ordinal);
        var prepare = InstallerSource.IndexOf(
            "PrepareOwnedRoot(layout, rootLease);",
            repair,
            StringComparison.Ordinal);
        var configure = InstallerSource.IndexOf(
            "_platform.ConfigureServiceAsync(",
            prepare,
            StringComparison.Ordinal);

        Assert.True(
            validate >= 0 && stop > validate && begin > stop && disable > begin && repair > disable &&
            prepare > repair && configure > prepare,
            "Legacy secrets remediation must run after the old service is quiesced and before the upgrade mutates the managed installation.");
        Assert.Contains(
            "failureStage = \"修復既有敏感資料目錄權限\";",
            InstallerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "failurePath = Path.Combine(layout.ServiceRoot, \"secrets\");",
            InstallerSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FailedUpgradeRestoresTheOldServiceAndPointerWithoutRestoringOldSecrets()
    {
        var catchStart = InstallerSource.IndexOf(
            "catch (Exception installationError)",
            StringComparison.Ordinal);
        var catchEnd = InstallerSource.IndexOf("finally", catchStart, StringComparison.Ordinal);
        Assert.True(catchStart >= 0 && catchEnd > catchStart);

        var rollback = InstallerSource[catchStart..catchEnd];
        var quiesce = rollback.IndexOf("restart: false", StringComparison.Ordinal);
        var restorePointer = rollback.IndexOf("RestoreActivePointer(", StringComparison.Ordinal);
        var rollbackProtection = rollback.IndexOf(
            "rootLease.RollbackProtectionChanges();",
            StringComparison.Ordinal);
        var restartOldService = rollback.IndexOf("restart: true", StringComparison.Ordinal);

        Assert.True(
            quiesce >= 0 && restorePointer > quiesce && rollbackProtection > restorePointer &&
            restartOldService > rollbackProtection,
            "A failed upgrade must restore the old active pointer and old service only after ordinary file/ACL rollback is complete.");

        Assert.DoesNotContain("RestoreUncommittedSecretsTokenMutation", InstallerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("OriginalTokenContent", InstallerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("OriginalContent", ExtractSecretsRepairImplementation(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledRemediationRetryWithoutRegistryDelayRepairsThenRestoresDelayedAutoService()
    {
        if (!CanRunElevatedInstallerOrchestrationProbe())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-secrets-repair-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, InstallerLayout.InstallMarkerName),
            InstallerLayout.InstallMarkerValue + Environment.NewLine);
        var activePointer = Path.Combine(root, "active-version.v1");
        File.WriteAllText(activePointer, "1.2.9-beta.9" + Environment.NewLine);

        var calls = new List<string>();
        var lease = new RecordingRootLease(calls);
        var previousService = new InstallerServiceSnapshot(
            Existed: true,
            ImagePath: $"\"{Path.Combine(root, "versions", "1.2.9-beta.9", "service-win-x64", "Muhun MCSV Service.exe")}\"",
            WasRunning: false,
            SecurityDescriptor: "D:(A;;RP;;;SY)",
            DelayedAutoStart: WindowsInstallerPlatform.ResolvePreviousDelayedAutoStart(
                startValue: 4,
                registryDelayed: false),
            WasDisabledForSecurityRemediationRetry: true);
        var platform = new RecordingInstallerPlatform(
            calls,
            lease,
            previousService,
            serviceStartsDisabled: true);

        try
        {
            using var bundle = CreateBundleThatFailsDuringPackageCopy(root);
            var failure = await Assert.ThrowsAsync<InstallerStageException>(() =>
                new InstallerEngine(platform).InstallAsync(bundle, root));

            Assert.Equal("複製並驗證內含套件", failure.Stage);
            Assert.Equal("1.2.9-beta.9", File.ReadAllText(activePointer).Trim());
            Assert.Collection(
                platform.RestoreRequests,
                request =>
                {
                    Assert.False(request.Restart);
                    Assert.Same(previousService, request.Snapshot);
                    Assert.True(request.Snapshot.DelayedAutoStart);
                },
                request =>
                {
                    Assert.True(request.Restart);
                    Assert.Same(previousService, request.Snapshot);
                    Assert.True(request.Snapshot.DelayedAutoStart);
                });

            Assert.True(calls.IndexOf("validate") < calls.IndexOf("capture-stop"));
            Assert.Equal(new[] { true }, platform.DisabledRetryAllowances);
            Assert.Equal(new[] { true }, lease.ForcedTokenRotationRequests);
            Assert.False(lease.HasPendingSecretsSecurityRemediation);
            Assert.True(calls.IndexOf("capture-stop") < calls.IndexOf("begin-secrets-remediation"));
            Assert.True(calls.IndexOf("begin-secrets-remediation") < calls.IndexOf("disable-service"));
            Assert.True(calls.IndexOf("disable-service") < calls.IndexOf("repair-secrets"));
            Assert.True(calls.IndexOf("repair-secrets") < calls.IndexOf("protect-root"));
            Assert.True(calls.IndexOf("restore-service-stopped") < calls.IndexOf("rollback-protection"));
            Assert.True(calls.IndexOf("rollback-protection") < calls.IndexOf("restore-service-running"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisabledRetryCaptureFailureAfterSnapshotStaysFailClosedWithoutTokenRotationOrRestore()
    {
        if (!CanRunElevatedInstallerOrchestrationProbe())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-disabled-retry-capture-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, InstallerLayout.InstallMarkerName),
            InstallerLayout.InstallMarkerValue + Environment.NewLine);
        var activePointer = Path.Combine(root, "active-version.v1");
        File.WriteAllText(activePointer, "1.2.9-beta.9" + Environment.NewLine);

        var calls = new List<string>();
        var lease = new RecordingRootLease(calls);
        var previousService = new InstallerServiceSnapshot(
            Existed: true,
            ImagePath: $"\"{Path.Combine(root, "versions", "1.2.9-beta.9", "service-win-x64", "Muhun MCSV Service.exe")}\"",
            WasRunning: false,
            SecurityDescriptor: "D:(A;;RP;;;SY)",
            DelayedAutoStart: true,
            WasDisabledForSecurityRemediationRetry: true);
        var platform = new RecordingInstallerPlatform(
            calls,
            lease,
            previousService,
            serviceStartsDisabled: true,
            failCaptureAfterSnapshot: true);

        try
        {
            using var bundle = CreateBundleThatFailsDuringPackageCopy(root);
            var failure = await Assert.ThrowsAsync<InstallerStageException>(() =>
                new InstallerEngine(platform).InstallAsync(bundle, root));

            Assert.Equal("擷取並停止既有 Windows Service", failure.Stage);
            Assert.Equal("1.2.9-beta.9", File.ReadAllText(activePointer).Trim());
            Assert.Equal(new[] { true }, platform.DisabledRetryAllowances);
            Assert.Empty(lease.ForcedTokenRotationRequests);
            Assert.DoesNotContain("begin-secrets-remediation", calls);
            Assert.DoesNotContain("repair-secrets", calls);
            Assert.Empty(platform.RestoreRequests);
            Assert.Equal(1, calls.Count(call => call == "disable-service"));
            Assert.DoesNotContain("restore-service-running", calls);
            Assert.DoesNotContain("restore-service-stopped", calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisabledServiceWithoutPendingRemediationIsRejectedBeforeAnyServiceMutation()
    {
        if (!CanRunElevatedInstallerOrchestrationProbe())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-disabled-service-reject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, InstallerLayout.InstallMarkerName),
            InstallerLayout.InstallMarkerValue + Environment.NewLine);
        var calls = new List<string>();
        var lease = new RecordingRootLease(calls, hasPendingRemediation: false);
        var snapshot = new InstallerServiceSnapshot(
            Existed: true,
            ImagePath: $"\"{Path.Combine(root, "versions", "1.2.9-beta.9", "service-win-x64", "Muhun MCSV Service.exe")}\"",
            WasRunning: false,
            SecurityDescriptor: "D:(A;;RP;;;SY)",
            DelayedAutoStart: true);
        var platform = new RecordingInstallerPlatform(
            calls,
            lease,
            snapshot,
            serviceStartsDisabled: true);

        try
        {
            using var bundle = CreateBundleThatFailsDuringPackageCopy(root);
            var failure = await Assert.ThrowsAsync<InstallerStageException>(() =>
                new InstallerEngine(platform).InstallAsync(bundle, root));

            Assert.Equal("擷取並停止既有 Windows Service", failure.Stage);
            Assert.Equal(new[] { false }, platform.DisabledRetryAllowances);
            Assert.DoesNotContain("begin-secrets-remediation", calls);
            Assert.DoesNotContain("disable-service", calls);
            Assert.Empty(platform.RestoreRequests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisabledRetryRepairFailureLeavesTheServiceDisabledAndNeverRestoresIt()
    {
        if (!CanRunElevatedInstallerOrchestrationProbe())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-secrets-fail-closed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, InstallerLayout.InstallMarkerName),
            InstallerLayout.InstallMarkerValue + Environment.NewLine);
        var activePointer = Path.Combine(root, "active-version.v1");
        File.WriteAllText(activePointer, "1.2.9-beta.9" + Environment.NewLine);

        var calls = new List<string>();
        var lease = new RecordingRootLease(calls, failRepair: true);
        var previousService = new InstallerServiceSnapshot(
            Existed: true,
            ImagePath: $"\"{Path.Combine(root, "versions", "1.2.9-beta.9", "service-win-x64", "Muhun MCSV Service.exe")}\"",
            WasRunning: false,
            SecurityDescriptor: "D:(A;;RP;;;SY)",
            DelayedAutoStart: WindowsInstallerPlatform.ResolvePreviousDelayedAutoStart(
                startValue: 4,
                registryDelayed: false),
            WasDisabledForSecurityRemediationRetry: true);
        var platform = new RecordingInstallerPlatform(
            calls,
            lease,
            previousService,
            serviceStartsDisabled: true);

        try
        {
            using var bundle = CreateBundleThatFailsDuringPackageCopy(root);
            var failure = await Assert.ThrowsAsync<InstallerStageException>(() =>
                new InstallerEngine(platform).InstallAsync(bundle, root));

            Assert.Equal("修復既有敏感資料目錄權限", failure.Stage);
            Assert.Equal("1.2.9-beta.9", File.ReadAllText(activePointer).Trim());
            Assert.Equal(new[] { true }, platform.DisabledRetryAllowances);
            Assert.Equal(new[] { true }, lease.ForcedTokenRotationRequests);
            Assert.Empty(platform.RestoreRequests);
            Assert.Equal(2, calls.Count(call => call == "disable-service"));
            Assert.Contains("rollback-protection", calls);
            Assert.DoesNotContain("restore-service-stopped", calls);
            Assert.DoesNotContain("restore-service-running", calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExactBeta9CurrentUserDirectoryAclDriftIsRecoverable()
    {
        var expected = CreateStrictSecretsGrants();
        var actual = CreateStrictSecretsSecurity(expected);
        AddDirectoryRule(actual, CurrentUserSid, FileSystemRights.FullControl, AccessControlType.Allow);

        Assert.True(WindowsInstallerPlatform.HasOnlyRecoverableCurrentUserDirectoryAclDrift(
            actual,
            expected,
            CurrentUserSid));
    }

    [Fact]
    public void CapturedDescriptorComparerAcceptsUnchangedInheritedSnapshotsWithoutWeakeningProtectedAclValidation()
    {
        foreach (var isDirectory in new[] { true, false })
        {
            var captured = CreateBeta9InheritedSecretsSecurity(
                isDirectory,
                includeCurrentUser: true);
            var unchanged = CreateBeta9InheritedSecretsSecurity(
                isDirectory,
                includeCurrentUser: true);
            var capturedDescriptor = GetSecurityDescriptor(captured);
            var unchangedDescriptor = GetSecurityDescriptor(unchanged);
            var capturedRaw = new RawSecurityDescriptor(capturedDescriptor);

            Assert.False(captured.AreAccessRulesProtected);
            Assert.False(unchanged.AreAccessRulesProtected);
            Assert.Equal(ServiceSid, capturedRaw.Owner);
            Assert.Equal(AdministratorsSid, capturedRaw.Group);
            Assert.NotNull(capturedRaw.DiscretionaryAcl);
            Assert.True(InstallerSecurityDescriptorComparer
                .EqualsCapturedDescriptorAllowingDaclAutoInherited(
                    unchangedDescriptor,
                    capturedDescriptor));
            Assert.False(InstallerSecurityDescriptorComparer
                .EqualsAllowingDaclAutoInherited(
                    unchangedDescriptor,
                    capturedDescriptor));
        }
    }

    [Fact]
    public void CapturedDescriptorComparerRejectsEverySecurityRelevantInheritedSnapshotMutation()
    {
        var captured = GetSecurityDescriptor(CreateBeta9InheritedSecretsSecurity(
            isDirectory: true,
            includeCurrentUser: true));
        var mutations = new Dictionary<string, FileSystemSecurity>
        {
            ["DACL protection"] = CreateBeta9InheritedSecretsSecurity(
                isDirectory: true,
                includeCurrentUser: true,
                daclProtected: true),
            ["owner"] = CreateBeta9InheritedSecretsSecurity(
                isDirectory: true,
                includeCurrentUser: true,
                owner: AdministratorsSid),
            ["group"] = CreateBeta9InheritedSecretsSecurity(
                isDirectory: true,
                includeCurrentUser: true,
                group: LocalSystemSid),
            ["access mask"] = CreateBeta9InheritedSecretsSecurity(
                isDirectory: true,
                includeCurrentUser: true,
                serviceRights: FileSystemRights.ReadAndExecute),
            ["inheritance flags"] = CreateBeta9InheritedSecretsSecurity(
                isDirectory: true,
                includeCurrentUser: true,
                aceFlags: AceFlags.ContainerInherit | AceFlags.Inherited),
            ["SID"] = CreateBeta9InheritedSecretsSecurity(
                isDirectory: true,
                includeCurrentUser: true,
                serviceGrantSid: new SecurityIdentifier("S-1-5-80-6-7-8-9-10")),
            ["extra ACE"] = CreateBeta9InheritedSecretsSecurity(
                isDirectory: true,
                includeCurrentUser: true,
                addExtraAce: true),
        };

        foreach (var mutation in mutations)
        {
            Assert.False(
                InstallerSecurityDescriptorComparer
                    .EqualsCapturedDescriptorAllowingDaclAutoInherited(
                        GetSecurityDescriptor(mutation.Value),
                        captured),
                mutation.Key);
        }

        var withoutAutoInheritedMarker = GetSecurityDescriptor(
            CreateBeta9InheritedSecretsSecurity(
                isDirectory: true,
                includeCurrentUser: true,
                daclAutoInherited: false));
        Assert.True(InstallerSecurityDescriptorComparer
            .EqualsCapturedDescriptorAllowingDaclAutoInherited(
                withoutAutoInheritedMarker,
                captured));
    }

    [Fact]
    public void CapturedDescriptorComparerRejectsSaclMutationAsAPureSddlUnitTest()
    {
        const string captured =
            "O:BAG:BAS:(AU;SA;FA;;;WD)D:AI(A;OICIID;FA;;;SY)";
        const string changedAuditMask =
            "O:BAG:BAS:(AU;SA;FR;;;WD)D:AI(A;OICIID;FA;;;SY)";

        Assert.False(InstallerSecurityDescriptorComparer
            .EqualsCapturedDescriptorAllowingDaclAutoInherited(
                changedAuditMask,
                captured));
    }

    [Fact]
    public void ActualBeta9InheritedSecretsTreeIsRecoverableAndStableBeforeRepair()
    {
        var directoryGrants = CreateStrictSecretsGrants();
        var fileGrants = CreateStrictSecretsFileGrants();
        var root = CreateStrictSecretsSecurity(directoryGrants);
        AddDirectoryRule(root, CurrentUserSid, FileSystemRights.FullControl, AccessControlType.Allow);
        var childDirectory = CreateBeta9InheritedSecretsSecurity(
            isDirectory: true,
            includeCurrentUser: true);
        var token = CreateBeta9InheritedSecretsSecurity(
            isDirectory: false,
            includeCurrentUser: true);

        Assert.True(root.AreAccessRulesProtected);
        Assert.False(childDirectory.AreAccessRulesProtected);
        Assert.False(token.AreAccessRulesProtected);
        Assert.Equal(AdministratorsSid, root.GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal(ServiceSid, childDirectory.GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal(ServiceSid, token.GetOwner(typeof(SecurityIdentifier)));

        Assert.All(
            GetCommonAces(root),
            ace => Assert.Equal(
                AceFlags.ContainerInherit | AceFlags.ObjectInherit,
                ace.AceFlags));
        Assert.All(
            GetCommonAces(childDirectory),
            ace => Assert.Equal(
                AceFlags.ContainerInherit | AceFlags.ObjectInherit | AceFlags.Inherited,
                ace.AceFlags));
        Assert.All(
            GetCommonAces(token),
            ace => Assert.Equal(AceFlags.Inherited, ace.AceFlags));

        var recoverableDirectoryGrants = new List<InstallerAclGrant>(directoryGrants)
        {
            DirectoryGrant(CurrentUserSid, FileSystemRights.FullControl),
        };
        var recoverableFileGrants = new List<InstallerAclGrant>(fileGrants)
        {
            new(CurrentUserSid, FileSystemRights.FullControl),
        };
        Assert.False(HasExactAccessRules(
            childDirectory,
            directoryGrants,
            requireInherited: true,
            isDirectory: true));
        Assert.True(HasExactAccessRules(
            childDirectory,
            recoverableDirectoryGrants,
            requireInherited: true,
            isDirectory: true));
        Assert.False(HasExactAccessRules(
            token,
            fileGrants,
            requireInherited: true,
            isDirectory: false));
        Assert.True(HasExactAccessRules(
            token,
            recoverableFileGrants,
            requireInherited: true,
            isDirectory: false));

        Assert.Equal(
            "Recoverable",
            ClassifySecretsAcl(root, isDirectory: true, expectedGrants: directoryGrants));
        Assert.Equal(
            "Recoverable",
            ClassifySecretsAcl(
                childDirectory,
                isDirectory: true,
                expectedGrants: directoryGrants));
        Assert.Equal(
            "Recoverable",
            ClassifySecretsAcl(token, isDirectory: false, expectedGrants: fileGrants));

        foreach (var security in new FileSystemSecurity[] { root, childDirectory, token })
        {
            var descriptor = GetSecurityDescriptor(security);
            Assert.True(InstallerSecurityDescriptorComparer
                .EqualsCapturedDescriptorAllowingDaclAutoInherited(descriptor, descriptor));
        }
    }

    [Fact]
    public void ElevatedTemporaryDirectoryRoundTripsRecoverableAndStrictSecretsAcls()
    {
        if (!CanRunElevatedInstallerOrchestrationProbe())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = Assert.IsType<SecurityIdentifier>(identity.User);
        var expected = CreateStrictSecretsGrants();
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-real-secrets-acl-" + Guid.NewGuid().ToString("N"));
        var tokenPath = Path.Combine(root, "service-rest-token.v1");
        Directory.CreateDirectory(root);
        var token = WindowsInstallerPlatform.CreateServiceTokenContentForRepair();
        File.WriteAllBytes(tokenPath, token);
        var tokenHash = SHA256.HashData(token);
        CryptographicOperations.ZeroMemory(token);

        try
        {
            var legacy = CreateStrictSecretsSecurity(expected);
            AddDirectoryRule(
                legacy,
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow);
            new DirectoryInfo(root).SetAccessControl(legacy);
            var observedLegacy = new DirectoryInfo(root).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner);
            Assert.True(WindowsInstallerPlatform.HasOnlyRecoverableCurrentUserDirectoryAclDrift(
                observedLegacy,
                expected,
                currentUser));

            var unsafeAcl = CreateStrictSecretsSecurity(expected);
            AddDirectoryRule(
                unsafeAcl,
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow);
            AddDirectoryRule(
                unsafeAcl,
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadAndExecute,
                AccessControlType.Allow);
            new DirectoryInfo(root).SetAccessControl(unsafeAcl);
            var observedUnsafe = new DirectoryInfo(root).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner);
            Assert.False(WindowsInstallerPlatform.HasOnlyRecoverableCurrentUserDirectoryAclDrift(
                observedUnsafe,
                expected,
                currentUser));
            Assert.Equal(tokenHash, SHA256.HashData(File.ReadAllBytes(tokenPath)));

            var strict = CreateStrictSecretsSecurity(expected);
            new DirectoryInfo(root).SetAccessControl(strict);
            var observedStrict = new DirectoryInfo(root).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner);
            Assert.True(InstallerSecurityDescriptorComparer.EqualsAllowingDaclAutoInherited(
                strict.GetSecurityDescriptorSddlForm(
                    AccessControlSections.Access | AccessControlSections.Owner),
                observedStrict.GetSecurityDescriptorSddlForm(
                    AccessControlSections.Access | AccessControlSections.Owner)));
            Assert.False(WindowsInstallerPlatform.HasOnlyRecoverableCurrentUserDirectoryAclDrift(
                observedStrict,
                expected,
                currentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenHash);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnknownBroadOrDenyAcesAreNeverTreatedAsRecoverableDrift()
    {
        var unknownSid = new SecurityIdentifier("S-1-5-21-1-2-3-1999");
        var everyoneSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        Action<DirectorySecurity>[] mutations =
        [
            security => AddDirectoryRule(
                security, unknownSid, FileSystemRights.FullControl, AccessControlType.Allow),
            security => AddDirectoryRule(
                security, everyoneSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow),
            security => AddDirectoryRule(
                security, usersSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow),
            security => AddDirectoryRule(
                security, CurrentUserSid, FileSystemRights.Read, AccessControlType.Deny),
        ];

        foreach (var mutate in mutations)
        {
            var expected = CreateStrictSecretsGrants();
            var actual = CreateStrictSecretsSecurity(expected);
            AddDirectoryRule(actual, CurrentUserSid, FileSystemRights.FullControl, AccessControlType.Allow);
            mutate(actual);

            Assert.False(WindowsInstallerPlatform.HasOnlyRecoverableCurrentUserDirectoryAclDrift(
                actual,
                expected,
                CurrentUserSid));
        }

        var wrongRights = CreateStrictSecretsSecurity(CreateStrictSecretsGrants());
        AddDirectoryRule(
            wrongRights,
            CurrentUserSid,
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow);
        Assert.False(WindowsInstallerPlatform.HasOnlyRecoverableCurrentUserDirectoryAclDrift(
            wrongRights,
            CreateStrictSecretsGrants(),
            CurrentUserSid));

        var wrongInheritance = CreateStrictSecretsSecurity(CreateStrictSecretsGrants());
        wrongInheritance.AddAccessRule(new FileSystemAccessRule(
            CurrentUserSid,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        Assert.False(WindowsInstallerPlatform.HasOnlyRecoverableCurrentUserDirectoryAclDrift(
            wrongInheritance,
            CreateStrictSecretsGrants(),
            CurrentUserSid));

        var inherited = AddInheritedCurrentUserRule(
            CreateStrictSecretsSecurity(CreateStrictSecretsGrants()));
        Assert.False(WindowsInstallerPlatform.HasOnlyRecoverableCurrentUserDirectoryAclDrift(
            inherited,
            CreateStrictSecretsGrants(),
            CurrentUserSid));
    }

    [Fact]
    public void CallbackObjectAndOpaqueAcesAreNeverRecoverableAclDrift()
    {
        GenericAce[] unsupportedAces =
        [
            new CommonAce(
                AceFlags.ContainerInherit | AceFlags.ObjectInherit,
                AceQualifier.AccessAllowed,
                (int)FileSystemRights.FullControl,
                CurrentUserSid,
                isCallback: true,
                opaque: null),
            new ObjectAce(
                AceFlags.ContainerInherit | AceFlags.ObjectInherit,
                AceQualifier.AccessAllowed,
                (int)FileSystemRights.FullControl,
                CurrentUserSid,
                ObjectAceFlags.ObjectAceTypePresent,
                Guid.NewGuid(),
                Guid.Empty,
                isCallback: false,
                opaque: null),
            new CommonAce(
                AceFlags.ContainerInherit | AceFlags.ObjectInherit,
                AceQualifier.AccessAllowed,
                (int)FileSystemRights.FullControl,
                CurrentUserSid,
                isCallback: true,
                opaque: [0xde, 0xad, 0xbe, 0xef]),
            new CustomAce(
                (AceType)0x42,
                AceFlags.ContainerInherit | AceFlags.ObjectInherit,
                [0xca, 0xfe, 0xba, 0xbe]),
        ];

        foreach (var unsupportedAce in unsupportedAces)
        {
            var security = AppendAce(
                CreateStrictSecretsSecurity(CreateStrictSecretsGrants()),
                unsupportedAce);
            AssertAclDriftRejected(security);
        }
    }

    [Fact]
    public void MixedStrictAndRecoverableSecretsEntriesRemainAValidRetryPlan()
    {
        var expected = CreateStrictSecretsGrants();
        var strict = CreateStrictSecretsSecurity(expected);
        var recoverable = CreateStrictSecretsSecurity(expected);
        AddDirectoryRule(
            recoverable,
            CurrentUserSid,
            FileSystemRights.FullControl,
            AccessControlType.Allow);
        var callback = AppendAce(
            CreateStrictSecretsSecurity(expected),
            new CommonAce(
                AceFlags.ContainerInherit | AceFlags.ObjectInherit,
                AceQualifier.AccessAllowed,
                (int)FileSystemRights.FullControl,
                CurrentUserSid,
                isCallback: true,
                opaque: null));

        var states = new[]
        {
            ClassifySecretsDirectoryAcl(strict, expected),
            ClassifySecretsDirectoryAcl(recoverable, expected),
        };
        Assert.Equal(new[] { "Strict", "Recoverable" }, states);
        Assert.DoesNotContain("Invalid", states);
        Assert.Equal("Invalid", ClassifySecretsDirectoryAcl(callback, expected));
    }

    [Fact]
    public void SecretsRepairPinsTheNamespaceAndRejectsReparsePointsAndHardLinks()
    {
        var scan = ExtractMethodBody(
            InstallerSource,
            "private PendingSecretsAclRepair PinAndValidateRecoverableSecretsNamespace(");
        var lockedFile = ExtractMethodBody(
            InstallerSource,
            "internal static SafeFileHandle OpenLockedRegularFileHandle(string path)");
        var validateFile = ExtractMethodBody(
            InstallerSource,
            "private static void ValidateLockedRegularFileHandle(SafeFileHandle handle, string expectedPath)");

        Assert.Contains("FileAttributes.ReparsePoint", scan, StringComparison.Ordinal);
        Assert.Contains("OpenLockedDirectoryHandle(path)", scan, StringComparison.Ordinal);
        Assert.Contains("OpenLockedRegularFileHandle(path)", scan, StringComparison.Ordinal);
        Assert.Contains("ValidateLockedRegularFileHandle(handle, path);", lockedFile, StringComparison.Ordinal);
        Assert.Contains("standardInformation.NumberOfLinks != 1", validateFile, StringComparison.Ordinal);
        Assert.Contains("GetFinalPath(handle)", validateFile, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulRepairAtomicallyRotatesAndCommitsTheTokenBeforeUpgradeContinues()
    {
        var repair = ExtractMethodBody(
            InstallerSource,
            "public void RepairRecoverableSecretsAclDriftAfterServiceStopped()");
        var harden = repair.IndexOf("HardenAndValidateSecretsNamespace(repair);", StringComparison.Ordinal);
        var release = repair.IndexOf("ReleaseFileForReplacement(repair.TokenPath);", StringComparison.Ordinal);
        var write = repair.IndexOf("WriteAtomicServiceToken(", StringComparison.Ordinal);
        var validate = repair.IndexOf(
            "ValidateServiceTokenContent(",
            write,
            StringComparison.Ordinal);
        var commit = repair.IndexOf("CommitSecretsSecurityRemediation(repair);", StringComparison.Ordinal);

        Assert.Contains("CreateServiceTokenContentForRepair()", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("Convert.ToHexString", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("Encoding.GetBytes", repair, StringComparison.Ordinal);
        Assert.True(harden >= 0 && release > harden && write > release && validate > write && commit > validate);

        var commitMethod = ExtractMethodBody(
            InstallerSource,
            "private void CommitSecretsSecurityRemediation(PendingSecretsAclRepair repair)");
        Assert.Contains("_originalSecurity.Remove(pair.Key)", commitMethod, StringComparison.Ordinal);
        Assert.Contains("_originalFileSecurity.Remove(pair.Key)", commitMethod, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory(repair.OriginalTokenHash);", commitMethod, StringComparison.Ordinal);
        Assert.Contains("_pendingSecretsAclRepair = null;", commitMethod, StringComparison.Ordinal);
        Assert.Contains("_secretsRemediationCommitted = true;", commitMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void TokenRepairHelpersGenerateFreshValidatedByteOnlyCredentials()
    {
        var first = WindowsInstallerPlatform.CreateServiceTokenContentForRepair();
        var second = WindowsInstallerPlatform.CreateServiceTokenContentForRepair();
        try
        {
            Assert.Equal(66, first.Length);
            Assert.Equal((byte)'\r', first[^2]);
            Assert.Equal((byte)'\n', first[^1]);
            Assert.True(WindowsInstallerPlatform.IsValidServiceTokenContent(first));
            Assert.True(WindowsInstallerPlatform.IsValidServiceTokenContent(second));
            Assert.False(first.AsSpan().SequenceEqual(second));
            Assert.All(first[..64], value => Assert.True(
                (value >= (byte)'0' && value <= (byte)'9') ||
                (value >= (byte)'A' && value <= (byte)'F')));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
        }
    }

    [Fact]
    public void TokenRepairValidationFailsClosedForMalformedByteSequences()
    {
        var tooShort = new byte[63];
        var nonHex = Enumerable.Repeat((byte)'A', 64).ToArray();
        nonHex[17] = (byte)'G';
        var badTerminator = Enumerable.Repeat((byte)'A', 66).ToArray();
        badTerminator[^2] = (byte)'\n';
        badTerminator[^1] = (byte)'\r';
        var nonAscii = Enumerable.Repeat((byte)'A', 64).ToArray();
        nonAscii[31] = 0xff;

        Assert.False(WindowsInstallerPlatform.IsValidServiceTokenContent(tooShort));
        Assert.False(WindowsInstallerPlatform.IsValidServiceTokenContent(nonHex));
        Assert.False(WindowsInstallerPlatform.IsValidServiceTokenContent(badTerminator));
        Assert.False(WindowsInstallerPlatform.IsValidServiceTokenContent(nonAscii));
    }

    [Fact]
    public void DisabledServiceStartTypeIsAllowedOnlyForAPrevalidatedPendingRemediation()
    {
        Assert.True(WindowsInstallerPlatform.IsManagedServiceStartTypeAllowed(
            startValue: 2,
            allowDisabledSecurityRemediationRetry: false));
        Assert.True(WindowsInstallerPlatform.IsManagedServiceStartTypeAllowed(
            startValue: 2,
            allowDisabledSecurityRemediationRetry: true));
        Assert.False(WindowsInstallerPlatform.IsManagedServiceStartTypeAllowed(
            startValue: 4,
            allowDisabledSecurityRemediationRetry: false));
        Assert.True(WindowsInstallerPlatform.IsManagedServiceStartTypeAllowed(
            startValue: 4,
            allowDisabledSecurityRemediationRetry: true));
        Assert.All(new[] { 0, 1, 3, 5 }, startValue =>
            Assert.False(WindowsInstallerPlatform.IsManagedServiceStartTypeAllowed(
                startValue,
                allowDisabledSecurityRemediationRetry: true)));
    }

    [Theory]
    [InlineData(2, false, false)]
    [InlineData(2, true, true)]
    [InlineData(4, false, true)]
    [InlineData(4, true, true)]
    public void DisabledRemediationRetryRestoresTheManagedDelayedAutoDefault(
        int startValue,
        bool registryDelayed,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsInstallerPlatform.ResolvePreviousDelayedAutoStart(
                startValue,
                registryDelayed));
    }

    [Fact]
    public void DisabledRetryStillRequiresTheSameTrustedManagedServiceIdentity()
    {
        var capture = ExtractMethodBody(
            InstallerSource,
            "public async Task<InstallerServiceSnapshot> CaptureAndStopServiceAsync(");
        var read = ExtractMethodBody(
            InstallerSource,
            "private static InstallerServiceSnapshot ReadExistingServiceSnapshot(");
        var restore = ExtractMethodBody(
            InstallerSource,
            "public async Task RestoreServiceAsync(");

        var sidType = capture.IndexOf("UNRESTRICTED", StringComparison.Ordinal);
        var readSnapshot = capture.IndexOf("ReadExistingServiceSnapshot(", StringComparison.Ordinal);
        Assert.True(sidType >= 0 && readSnapshot > sidType);
        Assert.Contains("ObjectName", read, StringComparison.Ordinal);
        Assert.Contains("IsManagedServiceStartTypeAllowed(", read, StringComparison.Ordinal);
        Assert.Contains(
            "DelayedAutoStart: ResolvePreviousDelayedAutoStart(start, registryDelayed)",
            read,
            StringComparison.Ordinal);
        Assert.Contains("ValidateOwnedServiceImagePath(imagePath, installRoot);", read, StringComparison.Ordinal);
        Assert.Contains("InstallerLayout.InstallMarkerName", InstallerSource, StringComparison.Ordinal);
        Assert.Contains("snapshot.DelayedAutoStart ? \"delayed-auto\" : \"auto\"", restore, StringComparison.Ordinal);
        var retryRestartGate = restore.IndexOf(
            "snapshot.WasRunning || snapshot.WasDisabledForSecurityRemediationRetry",
            StringComparison.Ordinal);
        var start = restore.IndexOf(
            "RunScAsync([\"start\", name]",
            retryRestartGate,
            StringComparison.Ordinal);
        Assert.True(retryRestartGate >= 0 && start > retryRestartGate);

        var catchStart = InstallerSource.IndexOf(
            "catch (Exception installationError)",
            StringComparison.Ordinal);
        var finalRestoreGate = InstallerSource.IndexOf(
            "!remediationFailedClosed",
            catchStart,
            StringComparison.Ordinal);
        var finalRestore = InstallerSource.IndexOf(
            "restart: true",
            finalRestoreGate,
            StringComparison.Ordinal);
        Assert.True(finalRestoreGate >= 0 && finalRestore > finalRestoreGate);
    }

    [Fact]
    public void TokenValidationNeverMaterializesTheBearerTokenAsAString()
    {
        var read = ExtractMethodBody(
            InstallerSource,
            "private static byte[] ReadAndValidateServiceToken(string path)");
        var write = ExtractMethodBody(
            InstallerSource,
            "private static void WriteAtomicServiceToken(");

        Assert.DoesNotContain("Encoding.GetString", read, StringComparison.Ordinal);
        Assert.DoesNotContain("Encoding.ASCII.GetString", read, StringComparison.Ordinal);
        Assert.DoesNotContain("new string", read, StringComparison.Ordinal);
        Assert.DoesNotContain("Encoding.GetString", write, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryExistingSecretsEntryIsHandleRevalidatedBeforeItsAclIsChanged()
    {
        var harden = ExtractMethodBody(
            InstallerSource,
            "private void HardenAndValidateSecretsNamespace(PendingSecretsAclRepair repair)");
        var directoryValidate = harden.IndexOf(
            "ValidatePinnedSecretsEntryUnchanged(path, isDirectory: true, handle, repair);",
            StringComparison.Ordinal);
        var directorySet = harden.IndexOf(
            "SetExactDirectoryAcl(path, repair.DirectoryGrants);",
            StringComparison.Ordinal);
        var fileValidate = harden.IndexOf(
            "ValidatePinnedSecretsEntryUnchanged(path, isDirectory: false, handle, repair);",
            StringComparison.Ordinal);
        var fileSet = harden.IndexOf(
            "SetExactFileAcl(path, repair.FileGrants);",
            StringComparison.Ordinal);

        Assert.True(directoryValidate >= 0 && directorySet > directoryValidate);
        Assert.True(fileValidate >= 0 && fileSet > fileValidate);

        var revalidate = ExtractMethodBody(
            InstallerSource,
            "private void ValidatePinnedSecretsEntryUnchanged(");
        var lockedDirectory = revalidate.IndexOf("ValidateLockedDirectoryHandle(handle, path);", StringComparison.Ordinal);
        var lockedFile = revalidate.IndexOf("ValidateLockedRegularFileHandle(handle, path);", StringComparison.Ordinal);
        var readSecurity = revalidate.IndexOf("var security = isDirectory", StringComparison.Ordinal);
        Assert.True(lockedDirectory >= 0 && lockedDirectory < readSecurity);
        Assert.True(lockedFile >= 0 && lockedFile < readSecurity);
        Assert.Contains(
            "EqualsCapturedDescriptorAllowingDaclAutoInherited(",
            revalidate,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "!InstallerSecurityDescriptorComparer.EqualsAllowingDaclAutoInherited(",
            revalidate,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SecretsRepairSnapshotsReadAndCaptureOwnerGroupAndAccessWithoutRequestingAudit()
    {
        static string Slice(string source, string startSignature, string endSignature)
        {
            var start = source.IndexOf(startSignature, StringComparison.Ordinal);
            var end = source.IndexOf(endSignature, start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start);
            return source[start..end];
        }

        var readDirectory = Slice(
            InstallerSource,
            "private static DirectorySecurity ReadDirectorySecurity(string path)",
            "private static FileSecurity ReadFileSecurity(string path)");
        var readFile = Slice(
            InstallerSource,
            "private static FileSecurity ReadFileSecurity(string path)",
            "private static bool HasExactDirectoryAcl(");
        var captureDescriptor = Slice(
            InstallerSource,
            "private static string GetSecurityDescriptor(FileSystemSecurity security)",
            "private static Dictionary<string, bool> EnumerateSecretsNamespace(string root)");

        Assert.All(new[] { readDirectory, readFile, captureDescriptor }, source =>
        {
            Assert.Contains("AccessControlSections.Access", source, StringComparison.Ordinal);
            Assert.Contains("AccessControlSections.Owner", source, StringComparison.Ordinal);
            Assert.Contains("AccessControlSections.Group", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AccessControlSections.Audit", source, StringComparison.Ordinal);
        });

        var repair = ExtractSecretsRepairImplementation();
        Assert.Contains("ReadDirectorySecurity(", repair, StringComparison.Ordinal);
        Assert.Contains("ReadFileSecurity(", repair, StringComparison.Ordinal);
        Assert.Contains("GetSecurityDescriptor(", repair, StringComparison.Ordinal);
    }

    private static string ExtractSecretsRepairImplementation()
    {
        var start = InstallerSource.IndexOf(
            "private sealed record PendingSecretsAclRepair",
            StringComparison.Ordinal);
        var end = InstallerSource.IndexOf(
            "public void ProtectOwnedRootAndPinExistingDirectories",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return InstallerSource[start..end];
    }

    private static IReadOnlyList<InstallerAclGrant> CreateStrictSecretsGrants() =>
    [
        DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
        DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
        DirectoryGrant(ServiceSid, FileSystemRights.Modify),
    ];

    private static IReadOnlyList<InstallerAclGrant> CreateStrictSecretsFileGrants() =>
    [
        new InstallerAclGrant(LocalSystemSid, FileSystemRights.FullControl),
        new InstallerAclGrant(AdministratorsSid, FileSystemRights.FullControl),
        new InstallerAclGrant(ServiceSid, FileSystemRights.Modify),
    ];

    private static DirectorySecurity CreateStrictSecretsSecurity(
        IReadOnlyList<InstallerAclGrant> grants)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        foreach (var grant in grants)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                grant.Sid,
                grant.Rights,
                grant.InheritanceFlags,
                grant.PropagationFlags,
                AccessControlType.Allow));
        }
        return security;
    }

    private static FileSystemSecurity CreateBeta9InheritedSecretsSecurity(
        bool isDirectory,
        bool includeCurrentUser,
        bool daclProtected = false,
        bool daclAutoInherited = true,
        SecurityIdentifier? owner = null,
        SecurityIdentifier? group = null,
        FileSystemRights? serviceRights = null,
        SecurityIdentifier? serviceGrantSid = null,
        AceFlags? aceFlags = null,
        bool addExtraAce = false)
    {
        var entries = new List<(SecurityIdentifier Sid, FileSystemRights Rights)>
        {
            (LocalSystemSid, FileSystemRights.FullControl),
            (AdministratorsSid, FileSystemRights.FullControl),
            (serviceGrantSid ?? ServiceSid, serviceRights ?? FileSystemRights.Modify),
        };
        if (includeCurrentUser)
        {
            entries.Add((CurrentUserSid, FileSystemRights.FullControl));
        }
        if (addExtraAce)
        {
            entries.Add((
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadAndExecute));
        }

        var inheritedFlags = aceFlags ?? (isDirectory
            ? AceFlags.ContainerInherit | AceFlags.ObjectInherit | AceFlags.Inherited
            : AceFlags.Inherited);
        var dacl = new RawAcl(GenericAcl.AclRevision, entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var rule = isDirectory
                ? new FileSystemAccessRule(
                    entry.Sid,
                    entry.Rights,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow)
                : new FileSystemAccessRule(entry.Sid, entry.Rights, AccessControlType.Allow);
            dacl.InsertAce(
                index,
                new CommonAce(
                    inheritedFlags,
                    AceQualifier.AccessAllowed,
                    (int)rule.FileSystemRights,
                    entry.Sid,
                    isCallback: false,
                    opaque: null));
        }

        var controlFlags = ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative;
        if (daclProtected)
        {
            controlFlags |= ControlFlags.DiscretionaryAclProtected;
        }
        if (daclAutoInherited)
        {
            controlFlags |= ControlFlags.DiscretionaryAclAutoInherited;
        }

        var descriptor = new RawSecurityDescriptor(
            controlFlags,
            owner ?? ServiceSid,
            group ?? AdministratorsSid,
            systemAcl: null,
            discretionaryAcl: dacl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        FileSystemSecurity security = isDirectory
            ? new DirectorySecurity()
            : new FileSecurity();
        security.SetSecurityDescriptorBinaryForm(bytes);
        return security;
    }

    private static string GetSecurityDescriptor(FileSystemSecurity security)
        => security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Access |
            AccessControlSections.Owner |
            AccessControlSections.Group);

    private static IReadOnlyList<CommonAce> GetCommonAces(FileSystemSecurity security)
    {
        var descriptor = new RawSecurityDescriptor(
            security.GetSecurityDescriptorBinaryForm(),
            offset: 0);
        var dacl = descriptor.DiscretionaryAcl
            ?? throw new InvalidDataException("Test security descriptor is missing its DACL.");
        var result = new List<CommonAce>(dacl.Count);
        for (var index = 0; index < dacl.Count; index++)
        {
            result.Add(Assert.IsType<CommonAce>(dacl[index]));
        }
        return result;
    }

    private static InstallerAclGrant DirectoryGrant(
        SecurityIdentifier sid,
        FileSystemRights rights) => new(
        sid,
        rights,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None);

    private static void AddDirectoryRule(
        DirectorySecurity security,
        SecurityIdentifier sid,
        FileSystemRights rights,
        AccessControlType type) => security.AddAccessRule(new FileSystemAccessRule(
        sid,
        rights,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        type));

    private static DirectorySecurity AddInheritedCurrentUserRule(DirectorySecurity security)
    {
        return AppendAce(
            security,
            new CommonAce(
                AceFlags.ContainerInherit | AceFlags.ObjectInherit | AceFlags.Inherited,
                AceQualifier.AccessAllowed,
                (int)FileSystemRights.FullControl,
                CurrentUserSid,
                isCallback: false,
                opaque: null));
    }

    private static DirectorySecurity AppendAce(DirectorySecurity security, GenericAce ace)
    {
        var descriptor = new RawSecurityDescriptor(
            security.GetSecurityDescriptorBinaryForm(),
            offset: 0);
        var dacl = descriptor.DiscretionaryAcl
            ?? throw new InvalidDataException("Test security descriptor is missing its DACL.");
        dacl.InsertAce(dacl.Count, ace);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        var result = new DirectorySecurity();
        result.SetSecurityDescriptorBinaryForm(bytes);
        return result;
    }

    private static void AssertAclDriftRejected(DirectorySecurity security)
    {
        try
        {
            Assert.False(WindowsInstallerPlatform.HasOnlyRecoverableCurrentUserDirectoryAclDrift(
                security,
                CreateStrictSecretsGrants(),
                CurrentUserSid));
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // An unsupported ACE that cannot be projected by FileSystemSecurity is still a
            // fail-closed rejection. It must never be normalized into an allowed beta.9 drift.
        }
    }

    private static string ClassifySecretsDirectoryAcl(
        DirectorySecurity security,
        IReadOnlyList<InstallerAclGrant> expectedGrants)
        => ClassifySecretsAcl(
            security,
            isDirectory: true,
            expectedGrants: expectedGrants);

    private static string ClassifySecretsAcl(
        FileSystemSecurity security,
        bool isDirectory,
        IReadOnlyList<InstallerAclGrant> expectedGrants)
    {
        var leaseType = typeof(WindowsInstallerPlatform).GetNestedType(
            "WindowsInstallerRootLease",
            BindingFlags.NonPublic) ?? throw new MissingMemberException(
            "WindowsInstallerRootLease type was not found.");
        var classify = leaseType.GetMethod(
            "ClassifySecretsEntryAcl",
            BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException(
            "ClassifySecretsEntryAcl helper was not found.");
        var state = classify.Invoke(
            obj: null,
            [security, isDirectory, expectedGrants, CurrentUserSid, ServiceSid]);
        return state?.ToString() ?? throw new InvalidDataException(
            "Secrets ACL classifier returned no state.");
    }

    private static bool HasExactAccessRules(
        FileSystemSecurity security,
        IReadOnlyList<InstallerAclGrant> expectedGrants,
        bool requireInherited,
        bool isDirectory)
    {
        var exact = typeof(WindowsInstallerPlatform).GetMethod(
            "HasExactAccessRules",
            BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException(
            "HasExactAccessRules helper was not found.");
        return Assert.IsType<bool>(exact.Invoke(
            obj: null,
            [security, expectedGrants, requireInherited, isDirectory]));
    }

    private static InstallerBundle CreateBundleThatFailsDuringPackageCopy(string directory)
    {
        var path = Path.Combine(directory, "empty-installer-bundle.zip");
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
        {
        }

        var executable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bounded = new BoundedReadStream(executable, 0, executable.Length, leaveOpen: true);
        var archiveReader = new ZipArchive(bounded, ZipArchiveMode.Read, leaveOpen: true);
        var metadata = new InstallerBundleMetadata(
            1,
            "muhun.mcsv.manager",
            "1.2.9-beta.11",
            "beta",
            "missing-package.zip",
            1,
            new string('0', 64));
        var manifest = new ProductUpdateManifest(
            1,
            "muhun.mcsv.manager",
            metadata.Version,
            metadata.Channel,
            "win-x64",
            DateTimeOffset.UtcNow,
            "test-key",
            "RS256",
            new ProductUpdatePackage("https://example.invalid/package.zip", 1, new string('0', 64)),
            "gui-win-x64/Muhun MCSV Manager.exe",
            []);
        var constructor = typeof(InstallerBundle).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(FileStream),
                typeof(BoundedReadStream),
                typeof(ZipArchive),
                typeof(InstallerBundleMetadata),
                typeof(ProductUpdateManifest),
            ],
            modifiers: null) ?? throw new MissingMethodException("InstallerBundle test constructor not found.");

        try
        {
            return (InstallerBundle)constructor.Invoke(
                [executable, bounded, archiveReader, metadata, manifest]);
        }
        catch
        {
            archiveReader.Dispose();
            bounded.Dispose();
            executable.Dispose();
            throw;
        }
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method: {signature}");
        var openingBrace = source.IndexOf('{', start);
        Assert.True(openingBrace > start, $"Could not find opening brace for: {signature}");

        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return source[openingBrace..(index + 1)];
                    }
                    break;
            }
        }

        throw new InvalidDataException($"Could not find closing brace for: {signature}");
    }

    private static string FindRepositoryRoot()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            if (File.Exists(Path.Combine(
                    cursor.FullName,
                    "src",
                    "MinecraftServerManager.Installer",
                    "InstallerEngine.cs")))
            {
                return cursor.FullName;
            }

            cursor = cursor.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static bool CanRunElevatedInstallerOrchestrationProbe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed class RecordingRootLease(
        List<string> calls,
        bool failRepair = false,
        bool hasPendingRemediation = true)
        : IInstallerRootLease
    {
        private bool _remediationStarted;
        private bool _remediationCommitted;

        public bool RootCreated => false;
        public bool HasPendingSecretsSecurityRemediation =>
            hasPendingRemediation && !_remediationCommitted;
        public bool SecretsSecurityRemediationFailedClosed =>
            _remediationStarted && !_remediationCommitted;
        public List<bool> ForcedTokenRotationRequests { get; } = [];

        public void ValidateAndPinExistingManagedInstallation(
            InstallerLayout layout,
            string targetVersion,
            string currentUserSid,
            string serviceName) => calls.Add("validate");

        public bool BeginRecoverableSecretsAclRemediation(
            bool forceTokenRotationForDisabledServiceRetry = false)
        {
            ForcedTokenRotationRequests.Add(forceTokenRotationForDisabledServiceRetry);
            if (!hasPendingRemediation)
            {
                return false;
            }
            calls.Add("begin-secrets-remediation");
            _remediationStarted = true;
            return true;
        }

        public void RepairRecoverableSecretsAclDriftAfterServiceStopped()
        {
            calls.Add("repair-secrets");
            if (failRepair)
            {
                throw new IOException("Injected secrets remediation failure.");
            }
            _remediationCommitted = true;
        }

        public void ProtectOwnedRootAndPinExistingDirectories(IReadOnlyList<string> directories)
            => calls.Add("protect-root");

        public void CreateAndProtectMissingDirectories() => calls.Add("create-protected");

        public void ProtectOwnedRootAndDirectories(IReadOnlyList<string> directories)
            => calls.Add("protect-directories");

        public void PinAndHardenExistingVersionTree(
            string directory,
            int maximumEntries = 100_000,
            int maximumDepth = 64) => calls.Add("pin-existing-version");

        public void PinAndHardenNewVersionTree(
            string directory,
            int maximumEntries = 100_000,
            int maximumDepth = 64) => calls.Add("pin-new-version");

        public void ReleaseFileForReplacement(string file) => calls.Add("release-file");

        public void ReleaseDirectoryForDeletion(string directory) => calls.Add("release-directory");

        public void CommitProtectionChanges() => calls.Add("commit-protection");

        public void RollbackProtectionChanges()
        {
            calls.Add("rollback-protection");
        }

        public void DeleteNewRootIfEmpty() => calls.Add("delete-root");

        public void Dispose() => calls.Add("dispose-lease");
    }

    private sealed class RecordingInstallerPlatform(
        List<string> calls,
        IInstallerRootLease lease,
        InstallerServiceSnapshot previousService,
        bool serviceStartsDisabled = false,
        bool failCaptureAfterSnapshot = false) : IInstallerPlatform
    {
        public string CurrentUserSid => "S-1-5-21-1-2-3-1001";

        public List<(InstallerServiceSnapshot Snapshot, bool Restart)> RestoreRequests { get; } = [];
        public List<bool> DisabledRetryAllowances { get; } = [];

        public bool IsAdministrator() => true;

        public bool IsCurrentIdentityInteractiveShellUser() => true;

        public IInstallerRootLease AcquireInstallRootLease(string installRoot)
        {
            calls.Add("acquire-lease");
            return lease;
        }

        public Task<InstallerServiceSnapshot> CaptureAndStopServiceAsync(
            string name,
            string installRoot,
            bool allowDisabledSecurityRemediationRetry,
            Action<InstallerServiceSnapshot> snapshotCaptured,
            CancellationToken cancellationToken)
        {
            calls.Add("capture-stop");
            DisabledRetryAllowances.Add(allowDisabledSecurityRemediationRetry);
            if (serviceStartsDisabled && !allowDisabledSecurityRemediationRetry)
            {
                throw new InvalidDataException(
                    "A disabled service is not trusted outside a pending security remediation.");
            }
            snapshotCaptured(previousService);
            if (failCaptureAfterSnapshot)
            {
                throw new IOException("Injected capture failure after snapshot callback.");
            }
            return Task.FromResult(previousService);
        }

        public Task RestoreServiceAsync(
            string name,
            InstallerServiceSnapshot snapshot,
            bool restart,
            CancellationToken cancellationToken)
        {
            calls.Add(restart ? "restore-service-running" : "restore-service-stopped");
            RestoreRequests.Add((snapshot, restart));
            return Task.CompletedTask;
        }

        public Task DisableServiceForSecurityRemediationAsync(
            string name,
            CancellationToken cancellationToken)
        {
            calls.Add("disable-service");
            return Task.CompletedTask;
        }

        public Task ConfigureServiceAsync(
            string name,
            string executable,
            string dataRoot,
            string exchangeRoot,
            string installRoot,
            InstallerServiceSnapshot snapshot,
            CancellationToken cancellationToken) => throw Unexpected();

        public Task StartServiceAsync(string name, CancellationToken cancellationToken)
            => throw Unexpected();

        public Task WaitForServiceHealthAsync(
            string serviceExecutable,
            string dataRoot,
            string version,
            CancellationToken cancellationToken) => throw Unexpected();

        public InstallerOperatorAccessRollback ProvisionOperatorAccess(
            InstallerLayout layout,
            Action<InstallerOperatorAccessRollback> snapshotCaptured) => throw Unexpected();

        public void HardenOperatorBindingAccess(
            InstallerLayout layout,
            string serviceName,
            InstallerOperatorAccessRollback rollback) => throw Unexpected();

        public void RestoreOperatorAccess(InstallerOperatorAccessRollback? rollback)
        {
            Assert.Null(rollback);
            calls.Add("restore-operator");
        }

        public void ApplyAccessControl(
            InstallerLayout layout,
            string serviceName,
            string targetVersionRoot,
            string operatorsGroupSid) => throw Unexpected();

        public void ApplyActivePointerAccessControl(InstallerLayout layout, string serviceName)
            => calls.Add("apply-active-pointer-acl");

        public InstallerRegistrationSnapshot CaptureInstallationRegistration() => throw Unexpected();

        public InstallerShortcutSnapshot CaptureStartMenuShortcut(string channel) => throw Unexpected();

        public void WriteInstallationRegistration(
            InstallerLayout layout,
            string version,
            string guiExecutable,
            string launcherExecutable) => throw Unexpected();

        public void CreateStartMenuShortcut(
            string launcherExecutable,
            string installRoot,
            string channel) => throw Unexpected();

        public void RestoreInstallationRegistration(InstallerRegistrationSnapshot snapshot)
            => throw Unexpected();

        public void RestoreStartMenuShortcut(InstallerShortcutSnapshot snapshot)
            => throw Unexpected();

        private static InvalidOperationException Unexpected()
            => new("The installer advanced past the intentional package-copy failure.");
    }
}
