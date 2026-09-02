using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using MinecraftServerManager.Installer;

namespace MinecraftServerManager.Updater.Tests;

public sealed class InstallerOperatorAccessReviewTests
{
    private static readonly SecurityIdentifier InstallerSid =
        new("S-1-5-21-1-2-3-1001");
    private static readonly SecurityIdentifier GroupSid =
        new("S-1-5-21-1-2-3-2001");

    [Fact]
    public void ProvisionPublishesRollbackJournalBeforeFirstGroupMutation()
    {
        using var directory = new TemporaryOperatorDirectory();
        InstallerOperatorAccessRollback? captured = null;
        var groups = new ReviewGroupManager
        {
            BeforeEnsure = () => Assert.NotNull(captured),
        };

        var rollback = InstallerOperatorAccessTransaction.Provision(
            groups,
            directory.Layout,
            InstallerSid,
            snapshot => captured = snapshot);

        Assert.Same(rollback, captured);
        InstallerOperatorAccessTransaction.Restore(groups, rollback);
    }

    [Fact]
    public void PostCreateIdentityFailureRecordsCreationAndNeverDeletesByNameAlone()
    {
        using var directory = new TemporaryOperatorDirectory();
        InstallerOperatorAccessRollback? captured = null;
        var groups = new ReviewGroupManager { ThrowAfterCreateNotification = true };

        Assert.Throws<IOException>(() => InstallerOperatorAccessTransaction.Provision(
            groups,
            directory.Layout,
            InstallerSid,
            snapshot => captured = snapshot));

        Assert.NotNull(captured);
        Assert.True(captured.GroupCreated);
        Assert.Null(captured.GroupSid);
        var rollbackFailure = Assert.Throws<AggregateException>(() =>
            InstallerOperatorAccessTransaction.Restore(groups, captured));
        Assert.Contains(
            rollbackFailure.InnerExceptions,
            error => error.Message.Contains("安全保留", StringComparison.Ordinal));
        Assert.Equal(0, groups.DeleteCalls);
        Assert.NotNull(groups.Group);
    }

    [Fact]
    public void RollbackPreservesMembershipThatExistedBeforeProvision()
    {
        using var directory = new TemporaryOperatorDirectory();
        var groups = new ReviewGroupManager(createExistingGroup: true);
        groups.Members.Add(InstallerSid);

        var rollback = InstallerOperatorAccessTransaction.Provision(
            groups,
            directory.Layout,
            InstallerSid,
            _ => { });
        Assert.False(rollback.MemberAdded);

        InstallerOperatorAccessTransaction.Restore(groups, rollback);

        Assert.Contains(InstallerSid, groups.Members);
        Assert.Equal(0, groups.RemoveCalls);
        Assert.NotNull(groups.Group);
    }

    [Fact]
    public void RollbackRemovesOnlyItsMemberAndRetainsGroupWithExternalMember()
    {
        using var directory = new TemporaryOperatorDirectory();
        var groups = new ReviewGroupManager();
        var externalSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        var rollback = InstallerOperatorAccessTransaction.Provision(
            groups,
            directory.Layout,
            InstallerSid,
            _ => { });
        groups.Members.Add(externalSid);

        var rollbackFailure = Assert.Throws<AggregateException>(() =>
            InstallerOperatorAccessTransaction.Restore(groups, rollback));

        Assert.Contains(
            rollbackFailure.InnerExceptions,
            error => error.Message.Contains("其他成員", StringComparison.Ordinal));
        Assert.DoesNotContain(InstallerSid, groups.Members);
        Assert.Contains(externalSid, groups.Members);
        Assert.Equal(1, groups.RemoveCalls);
        Assert.Equal(0, groups.DeleteCalls);
        Assert.NotNull(groups.Group);
    }

    [Fact]
    public void NativeImplementationKeepsPagingAndBindingCommitBoundedAndOrdered()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MinecraftServerManager.Installer",
            "InstallerOperatorAccess.cs"));
        Assert.Contains("IntendedBindingContent = intendedBinding", source, StringComparison.Ordinal);
        Assert.Contains("rollback.BindingWriteAttempted = true;", source, StringComparison.Ordinal);
        Assert.Contains("beforeCommit?.Invoke(intendedSecurityDescriptor);", source, StringComparison.Ordinal);
        Assert.Contains("rollback.InstalledBindingContent = intendedBinding;", source, StringComparison.Ordinal);
        Assert.Contains("private const uint MaximumPreferredLength = 64 * 1024;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MaximumPreferredLength = uint.MaxValue", source, StringComparison.Ordinal);
        Assert.Contains("UIntPtr resume", source, StringComparison.Ordinal);
        Assert.Contains("status is not Success and not ErrorMoreData", source, StringComparison.Ordinal);
        Assert.Contains("if (entriesRead > 0 && buffer == IntPtr.Zero)", source, StringComparison.Ordinal);
        Assert.Contains("NetApiBufferFree(buffer)", source, StringComparison.Ordinal);
        Assert.Contains(
            "DefaultDllImportSearchPaths(DllImportSearchPath.System32)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("powershell", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExactBindingAclIsProtectedAndContainsOnlySystemAdministratorsAndService()
    {
        var serviceSid = new SecurityIdentifier("S-1-5-80-1-2-3-4-5");
        var security = InstallerOperatorAccessTransaction.CreateExactBindingSecurity(serviceSid);

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

        Assert.Equal(3, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
        });
        Assert.Contains(rules, rule =>
            rule.IdentityReference.Equals(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) &&
            rule.FileSystemRights == FileSystemRights.FullControl);
        Assert.Contains(rules, rule =>
            rule.IdentityReference.Equals(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) &&
            rule.FileSystemRights == FileSystemRights.FullControl);
        Assert.Contains(rules, rule =>
            rule.IdentityReference.Equals(serviceSid) &&
            rule.FileSystemRights == (FileSystemRights.Read | FileSystemRights.Synchronize));
    }

    [Fact]
    public void ExactBindingAclComparisonAcceptsEquivalentWindowsSddlNormalization()
    {
        var serviceSid = new SecurityIdentifier("S-1-5-80-1-2-3-4-5");
        var expected = InstallerOperatorAccessTransaction.CreateExactBindingSecurity(serviceSid)
            .GetSecurityDescriptorSddlForm(
                AccessControlSections.Access | AccessControlSections.Owner);
        var normalized = expected.Replace("D:P", "D:PAI", StringComparison.Ordinal);

        Assert.NotEqual(expected, normalized);
        Assert.True(InstallerOperatorAccessTransaction
            .AreEquivalentExactBindingSecurityDescriptors(normalized, expected));

        var autoInheritRequired = expected.Replace("D:P", "D:PAR", StringComparison.Ordinal);
        Assert.False(InstallerOperatorAccessTransaction
            .AreEquivalentExactBindingSecurityDescriptors(autoInheritRequired, expected));
    }

    [Fact]
    public void ExactBindingAclComparisonRejectsEveryNonExactAclShape()
    {
        var serviceSid = new SecurityIdentifier("S-1-5-80-1-2-3-4-5");
        var otherSid = new SecurityIdentifier("S-1-5-80-6-7-8-9-10");
        var expected = InstallerOperatorAccessTransaction.CreateExactBindingSecurity(serviceSid)
            .GetSecurityDescriptorSddlForm(
                AccessControlSections.Access | AccessControlSections.Owner);

        var wrongService = InstallerOperatorAccessTransaction.CreateExactBindingSecurity(otherSid)
            .GetSecurityDescriptorSddlForm(
                AccessControlSections.Access | AccessControlSections.Owner);
        Assert.False(InstallerOperatorAccessTransaction
            .AreEquivalentExactBindingSecurityDescriptors(wrongService, expected));

        var unprotected = new FileSecurity();
        unprotected.SetSecurityDescriptorSddlForm(
            expected,
            AccessControlSections.Access | AccessControlSections.Owner);
        unprotected.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        Assert.False(InstallerOperatorAccessTransaction
            .AreEquivalentExactBindingSecurityDescriptors(
                unprotected.GetSecurityDescriptorSddlForm(
                    AccessControlSections.Access | AccessControlSections.Owner),
                expected));

        var extraAce = InstallerOperatorAccessTransaction.CreateExactBindingSecurity(serviceSid);
        extraAce.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier("S-1-5-21-1-2-3-1999"),
            FileSystemRights.Read,
            AccessControlType.Allow));
        Assert.False(InstallerOperatorAccessTransaction
            .AreEquivalentExactBindingSecurityDescriptors(
                extraAce.GetSecurityDescriptorSddlForm(
                    AccessControlSections.Access | AccessControlSections.Owner),
                expected));

        var denyAce = InstallerOperatorAccessTransaction.CreateExactBindingSecurity(serviceSid);
        denyAce.AddAccessRule(new FileSystemAccessRule(
            serviceSid,
            FileSystemRights.Write,
            AccessControlType.Deny));
        Assert.False(InstallerOperatorAccessTransaction
            .AreEquivalentExactBindingSecurityDescriptors(
                denyAce.GetSecurityDescriptorSddlForm(
                    AccessControlSections.Access | AccessControlSections.Owner),
                expected));

        var inheritedAce = expected.Replace("(A;;FR;;;", "(A;ID;FR;;;", StringComparison.Ordinal);
        Assert.NotEqual(expected, inheritedAce);
        Assert.False(InstallerOperatorAccessTransaction
            .AreEquivalentExactBindingSecurityDescriptors(inheritedAce, expected));

        var objectAce = expected +
            "(OA;;FR;00112233-4455-6677-8899-AABBCCDDEEFF;;S-1-5-21-1-2-3-1999)";
        Assert.False(InstallerOperatorAccessTransaction
            .AreEquivalentExactBindingSecurityDescriptors(objectAce, expected));
    }

    [Fact]
    public void HardenAndRollbackAcceptEquivalentCapturedWindowsNormalization()
    {
        using var directory = new TemporaryOperatorDirectory();
        var groups = new ReviewGroupManager();
        var rollback = InstallerOperatorAccessTransaction.Provision(
            groups,
            directory.Layout,
            InstallerSid,
            _ => { });
        var securityStore = new NormalizingBindingSecurityStore(
            rollback.InstalledBindingSecurityDescriptor!);
        var serviceSid = new SecurityIdentifier("S-1-5-80-1-2-3-4-5");

        InstallerOperatorAccessTransaction.HardenBindingAccess(
            directory.Layout,
            rollback,
            serviceSid,
            securityStore);

        Assert.Contains("D:PAI", rollback.HardenedBindingSecurityDescriptor!);
        InstallerOperatorAccessTransaction.Restore(groups, rollback, securityStore);
        Assert.False(File.Exists(rollback.BindingPath));
        Assert.Null(groups.Group);
    }

    [Fact]
    public void AmbiguousHardenApplyAcceptsOnlyEquivalentNormalizedAclDuringRollback()
    {
        using var directory = new TemporaryOperatorDirectory();
        var groups = new ReviewGroupManager();
        var rollback = InstallerOperatorAccessTransaction.Provision(
            groups,
            directory.Layout,
            InstallerSid,
            _ => { });
        var securityStore = new NormalizeThenThrowBindingSecurityStore(
            rollback.InstalledBindingSecurityDescriptor!);
        var serviceSid = new SecurityIdentifier("S-1-5-80-1-2-3-4-5");

        Assert.Throws<IOException>(() =>
            InstallerOperatorAccessTransaction.HardenBindingAccess(
                directory.Layout,
                rollback,
                serviceSid,
                securityStore));

        Assert.True(rollback.BindingAclMutationAttempted);
        Assert.DoesNotContain("D:PAI", rollback.HardenedBindingSecurityDescriptor!);
        Assert.Contains("D:PAI", securityStore.CurrentDescriptor);
        InstallerOperatorAccessTransaction.Restore(groups, rollback, securityStore);
        Assert.False(File.Exists(rollback.BindingPath));
        Assert.Null(groups.Group);
    }

    [Fact]
    public void UpgradeRollbackSnapshotsAndRestoresPreviousBindingAcl()
    {
        using var directory = new TemporaryOperatorDirectory();
        var groups = new ReviewGroupManager(createExistingGroup: true);
        var binding = Path.Combine(
            directory.Layout.ServiceRoot,
            "data",
            "installer-operator-sid.v1");
        Directory.CreateDirectory(Path.GetDirectoryName(binding)!);
        File.WriteAllText(binding, "S-1-5-21-1-2-3-1002\r\n", Encoding.ASCII);
        var previousDescriptor =
            InstallerOperatorAccessTransaction.CaptureBindingSecurityDescriptor(binding);
        var previousContent = File.ReadAllBytes(binding);

        var rollback = InstallerOperatorAccessTransaction.Provision(
            groups,
            directory.Layout,
            InstallerSid,
            _ => { });

        Assert.Equal(previousDescriptor, rollback.PreviousBindingSecurityDescriptor);
        Assert.NotNull(rollback.InstalledBindingSecurityDescriptor);
        var securityStore = new ReviewBindingSecurityStore(
            rollback.InstalledBindingSecurityDescriptor!);
        var serviceSid = new SecurityIdentifier("S-1-5-80-1-2-3-4-5");
        InstallerOperatorAccessTransaction.HardenBindingAccess(
            directory.Layout,
            rollback,
            serviceSid,
            securityStore);
        var expectedHardenedDescriptor =
            InstallerOperatorAccessTransaction.CreateExactBindingSecurity(serviceSid)
                .GetSecurityDescriptorSddlForm(
                    AccessControlSections.Access | AccessControlSections.Owner);

        Assert.True(rollback.BindingAclMutationAttempted);
        Assert.Equal(expectedHardenedDescriptor, securityStore.CurrentDescriptor);

        InstallerOperatorAccessTransaction.Restore(groups, rollback, securityStore);
        Assert.Equal(previousContent, File.ReadAllBytes(binding));
        Assert.Equal(
            previousDescriptor,
            securityStore.CurrentDescriptor);
    }

    [Fact]
    public void RollbackRefusesExternalBindingAclChangeButStillRestoresGroup()
    {
        using var directory = new TemporaryOperatorDirectory();
        var groups = new ReviewGroupManager();
        var rollback = InstallerOperatorAccessTransaction.Provision(
            groups,
            directory.Layout,
            InstallerSid,
            _ => { });
        var security = new FileInfo(rollback.BindingPath).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier("S-1-5-21-1-2-3-1999"),
            FileSystemRights.Read,
            AccessControlType.Allow));
        new FileInfo(rollback.BindingPath).SetAccessControl(security);

        var failure = Assert.Throws<AggregateException>(() =>
            InstallerOperatorAccessTransaction.Restore(groups, rollback));

        Assert.Contains(
            failure.InnerExceptions,
            error => error.Message.Contains("ACL", StringComparison.Ordinal));
        Assert.True(File.Exists(rollback.BindingPath));
        Assert.Null(groups.Group);
    }

    [Fact]
    public void PostWriteAclCaptureFailureRestoresPreviousContentAndAcl()
    {
        using var directory = new TemporaryOperatorDirectory();
        var groups = new ReviewGroupManager();
        var binding = CreateExistingBinding(directory.Layout, out var previousContent);
        var previousDescriptor =
            InstallerOperatorAccessTransaction.CaptureBindingSecurityDescriptor(binding);
        var securityStore = new ThrowOnCaptureBindingSecurityStore(throwOnCaptureCall: 2);
        InstallerOperatorAccessRollback? captured = null;

        Assert.Throws<IOException>(() => InstallerOperatorAccessTransaction.Provision(
            groups,
            directory.Layout,
            InstallerSid,
            snapshot => captured = snapshot,
            File.WriteAllBytes,
            securityStore));

        var rollback = Assert.IsType<InstallerOperatorAccessRollback>(captured);
        Assert.True(rollback.BindingWriteAttempted);
        Assert.NotNull(rollback.InstalledBindingContent);
        Assert.Null(rollback.InstalledBindingSecurityDescriptor);
        InstallerOperatorAccessTransaction.Restore(groups, rollback, securityStore);
        Assert.Equal(previousContent, File.ReadAllBytes(binding));
        Assert.Equal(
            previousDescriptor,
            InstallerOperatorAccessTransaction.CaptureBindingSecurityDescriptor(binding));
        Assert.Null(groups.Group);
    }

    [Fact]
    public void AmbiguousWriterFailureAfterReplacementRestoresPreviousContentAndAcl()
    {
        using var directory = new TemporaryOperatorDirectory();
        var groups = new ReviewGroupManager();
        var binding = CreateExistingBinding(directory.Layout, out var previousContent);
        var previousDescriptor =
            InstallerOperatorAccessTransaction.CaptureBindingSecurityDescriptor(binding);
        InstallerOperatorAccessRollback? captured = null;

        Assert.Throws<IOException>(() => InstallerOperatorAccessTransaction.Provision(
            groups,
            directory.Layout,
            InstallerSid,
            snapshot => captured = snapshot,
            (path, content) =>
            {
                File.WriteAllBytes(path, content);
                throw new IOException("Injected ambiguous failure after replacement.");
            }));

        var rollback = Assert.IsType<InstallerOperatorAccessRollback>(captured);
        Assert.True(rollback.BindingWriteAttempted);
        Assert.Null(rollback.InstalledBindingContent);
        InstallerOperatorAccessTransaction.Restore(groups, rollback);
        Assert.Equal(previousContent, File.ReadAllBytes(binding));
        Assert.Equal(
            previousDescriptor,
            InstallerOperatorAccessTransaction.CaptureBindingSecurityDescriptor(binding));
        Assert.Null(groups.Group);
    }

    private static string CreateExistingBinding(
        InstallerLayout layout,
        out byte[] previousContent)
    {
        var binding = Path.Combine(
            layout.ServiceRoot,
            "data",
            "installer-operator-sid.v1");
        Directory.CreateDirectory(Path.GetDirectoryName(binding)!);
        previousContent = Encoding.ASCII.GetBytes("S-1-5-21-1-2-3-1002\r\n");
        File.WriteAllBytes(binding, previousContent);
        return binding;
    }

    private static string FindRepositoryRoot()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "MinecraftServerManager.sln")))
            {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class TemporaryOperatorDirectory : IDisposable
    {
        public TemporaryOperatorDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "muhun-mcsv-operator-review-" + Guid.NewGuid().ToString("N"));
            Layout = new InstallerLayout(
                Root,
                "beta",
                Path.Combine(Root, "versions"),
                Path.Combine(Root, "activation-state"),
                Path.Combine(Root, "service", "beta"),
                Path.Combine(Root, "exchange", "beta"),
                Path.Combine(Root, "users", "S-1-5-21-1-2-3-1001", "beta"),
                Path.Combine(Root, "install-staging"),
                Path.Combine(Root, "launcher"));
        }

        public string Root { get; }
        public InstallerLayout Layout { get; }

        public void Dispose()
        {
            var expectedPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith(
                    "muhun-mcsv-operator-review-",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove an unexpected test directory.");
            }
            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    private sealed class ReviewGroupManager : IInstallerLocalGroupManager
    {
        public ReviewGroupManager(bool createExistingGroup = false)
        {
            if (createExistingGroup)
            {
                Group = NewGroup(created: false);
            }
        }

        public Action? BeforeEnsure { get; init; }
        public bool ThrowAfterCreateNotification { get; init; }
        public InstallerLocalGroupIdentity? Group { get; private set; }
        public HashSet<SecurityIdentifier> Members { get; } = [];
        public int RemoveCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public InstallerLocalGroupIdentity EnsureGroup(
            string name,
            string description,
            Action groupCreated)
        {
            BeforeEnsure?.Invoke();
            if (Group is not null)
            {
                return Group with { Created = false };
            }

            Group = NewGroup(created: true);
            groupCreated();
            if (ThrowAfterCreateNotification)
            {
                throw new IOException("Injected post-create identity query failure.");
            }
            return Group;
        }

        public InstallerLocalGroupIdentity? TryGetGroup(string name)
            => Group is not null && string.Equals(Group.Name, name, StringComparison.Ordinal)
                ? Group with { Created = false }
                : null;

        public IReadOnlyList<SecurityIdentifier> GetMembers(string name)
        {
            EnsurePresent(name);
            return Members.ToArray();
        }

        public bool AddMember(string name, SecurityIdentifier memberSid)
        {
            EnsurePresent(name);
            return Members.Add(memberSid);
        }

        public void RemoveMember(string name, SecurityIdentifier memberSid)
        {
            EnsurePresent(name);
            RemoveCalls++;
            Members.Remove(memberSid);
        }

        public void DeleteGroup(string name)
        {
            EnsurePresent(name);
            DeleteCalls++;
            if (Members.Count != 0)
            {
                throw new IOException("Group is not empty.");
            }
            Group = null;
        }

        private void EnsurePresent(string name)
        {
            if (TryGetGroup(name) is null)
            {
                throw new IOException("Group is missing.");
            }
        }

        private static InstallerLocalGroupIdentity NewGroup(bool created)
            => new(
                InstallerOperatorAccessTransaction.GroupName,
                InstallerOperatorAccessTransaction.GroupDescription,
                GroupSid,
                created);
    }

    private sealed class ReviewBindingSecurityStore(string initialDescriptor)
        : IInstallerBindingSecurityStore
    {
        public string CurrentDescriptor { get; private set; } = initialDescriptor;

        public string Capture(string path) => CurrentDescriptor;

        public void Apply(string path, string descriptor)
            => CurrentDescriptor = descriptor;
    }

    private sealed class NormalizingBindingSecurityStore(string initialDescriptor)
        : IInstallerBindingSecurityStore
    {
        public string CurrentDescriptor { get; private set; } = initialDescriptor;

        public string Capture(string path) => CurrentDescriptor;

        public void Apply(string path, string descriptor)
            => CurrentDescriptor = descriptor.Replace(
                "D:P",
                "D:PAI",
                StringComparison.Ordinal);
    }

    private sealed class NormalizeThenThrowBindingSecurityStore(string initialDescriptor)
        : IInstallerBindingSecurityStore
    {
        public string CurrentDescriptor { get; private set; } = initialDescriptor;

        public string Capture(string path) => CurrentDescriptor;

        public void Apply(string path, string descriptor)
        {
            CurrentDescriptor = descriptor.Replace(
                "D:P",
                "D:PAI",
                StringComparison.Ordinal);
            throw new IOException("Injected ambiguous ACL setter failure after OS normalization.");
        }
    }

    private sealed class ThrowOnCaptureBindingSecurityStore(int throwOnCaptureCall)
        : IInstallerBindingSecurityStore
    {
        private readonly WindowsInstallerBindingSecurityStore _inner = new();
        private int _captureCalls;

        public string Capture(string path)
        {
            _captureCalls++;
            if (_captureCalls == throwOnCaptureCall)
            {
                throw new IOException("Injected post-write ACL capture failure.");
            }
            return _inner.Capture(path);
        }

        public void Apply(string path, string descriptor)
            => _inner.Apply(path, descriptor);
    }
}
