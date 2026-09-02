using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Installer;

internal sealed record InstallerLocalGroupIdentity(
    string Name,
    string Description,
    SecurityIdentifier Sid,
    bool Created);

internal interface IInstallerLocalGroupManager
{
    InstallerLocalGroupIdentity EnsureGroup(
        string name,
        string description,
        Action groupCreated);
    InstallerLocalGroupIdentity? TryGetGroup(string name);
    IReadOnlyList<SecurityIdentifier> GetMembers(string name);
    bool AddMember(string name, SecurityIdentifier memberSid);
    void RemoveMember(string name, SecurityIdentifier memberSid);
    void DeleteGroup(string name);
}

internal interface IInstallerBindingSecurityStore
{
    string Capture(string path);
    void Apply(string path, string descriptor);
}

internal sealed class WindowsInstallerBindingSecurityStore : IInstallerBindingSecurityStore
{
    public string Capture(string path)
    {
        InstallerLayout.RejectExistingReparsePoints(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("安裝者 SID 綁定檔在 ACL 驗證前遺失。", path);
        }
        return new FileInfo(path).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner)
            .GetSecurityDescriptorSddlForm(
                AccessControlSections.Access | AccessControlSections.Owner);
    }

    public void Apply(string path, string descriptor)
    {
        InstallerLayout.RejectExistingReparsePoints(path);
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(
            descriptor,
            AccessControlSections.Access | AccessControlSections.Owner);
        new FileInfo(path).SetAccessControl(security);
    }
}

internal static class InstallerSecurityDescriptorComparer
{
    public static bool EqualsAllowingDaclAutoInherited(
        string actualDescriptor,
        string expectedDescriptor)
    {
        try
        {
            var actual = new RawSecurityDescriptor(actualDescriptor);
            var expected = new RawSecurityDescriptor(expectedDescriptor);
            const ControlFlags allowedWindowsNormalization =
                ControlFlags.DiscretionaryAclAutoInherited;
            return (actual.ControlFlags & ~allowedWindowsNormalization) ==
                       (expected.ControlFlags & ~allowedWindowsNormalization) &&
                   actual.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) &&
                   Equals(actual.Owner, expected.Owner) &&
                   Equals(actual.Group, expected.Group) &&
                   AclBinaryEquals(actual.DiscretionaryAcl, expected.DiscretionaryAcl) &&
                   AclBinaryEquals(actual.SystemAcl, expected.SystemAcl);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool AclBinaryEquals(GenericAcl? left, GenericAcl? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }
        if (left.BinaryLength != right.BinaryLength)
        {
            return false;
        }

        var leftBytes = new byte[left.BinaryLength];
        var rightBytes = new byte[right.BinaryLength];
        left.GetBinaryForm(leftBytes, 0);
        right.GetBinaryForm(rightBytes, 0);
        return leftBytes.AsSpan().SequenceEqual(rightBytes);
    }
}

internal static class InstallerOperatorAccessTransaction
{
    internal const string GroupName = "Muhun MCSV Operators";
    internal const string GroupDescription = "Accounts authorized to control Muhun MCSV.";
    private const int MaximumBindingBytes = ProductLocalIpcAccess.MaximumSidFileBytes;
    private static readonly IInstallerBindingSecurityStore NativeBindingSecurity =
        new WindowsInstallerBindingSecurityStore();

    public static InstallerOperatorAccessRollback Provision(
        IInstallerLocalGroupManager groups,
        InstallerLayout layout,
        SecurityIdentifier installerSid,
        Action<InstallerOperatorAccessRollback> snapshotCaptured,
        Action<string, byte[]>? writeBinding = null,
        IInstallerBindingSecurityStore? bindingSecurity = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(installerSid);
        ArgumentNullException.ThrowIfNull(snapshotCaptured);
        ValidateInstallerAccountSid(installerSid);
        bindingSecurity ??= NativeBindingSecurity;

        var bindingPath = ResolveBindingPath(layout);
        var previousBinding = ReadOptionalValidatedBinding(bindingPath);
        var previousBindingSecurityDescriptor = previousBinding is null
            ? null
            : bindingSecurity.Capture(bindingPath);
        var intendedBinding = Encoding.ASCII.GetBytes(installerSid.Value + Environment.NewLine);
        var rollback = new InstallerOperatorAccessRollback
        {
            GroupName = GroupName,
            GroupDescription = GroupDescription,
            InstallerSid = installerSid.Value,
            BindingPath = bindingPath,
            BindingExisted = previousBinding is not null,
            PreviousBindingContent = previousBinding,
            PreviousBindingSecurityDescriptor = previousBindingSecurityDescriptor,
            IntendedBindingContent = intendedBinding,
            IntendedBindingSecurityDescriptor = previousBindingSecurityDescriptor,
        };
        // The outer installer owns this live mutation journal before the first machine-wide
        // group or membership mutation can occur.
        snapshotCaptured(rollback);

        var group = groups.EnsureGroup(
            GroupName,
            GroupDescription,
            () => rollback.GroupCreated = true);
        ValidateExactGroupIdentity(group, expectedSid: null);
        rollback.GroupSid = group.Sid.Value;
        rollback.GroupCreated |= group.Created;
        rollback.MemberAdded = groups.AddMember(GroupName, installerSid);

        if (writeBinding is null)
        {
            WriteAtomicBytes(
                bindingPath,
                intendedBinding,
                previousBindingSecurityDescriptor,
                bindingSecurity,
                descriptor =>
                {
                    rollback.IntendedBindingSecurityDescriptor = descriptor;
                    rollback.BindingWriteAttempted = true;
                });
        }
        else
        {
            if (rollback.IntendedBindingSecurityDescriptor is null)
            {
                throw new InvalidOperationException(
                    "自訂 SID 綁定寫入器必須以既有綁定 ACL 作為可驗證的預定狀態。");
            }
            rollback.BindingWriteAttempted = true;
            writeBinding(bindingPath, intendedBinding);
        }

        rollback.InstalledBindingContent = intendedBinding;
        var installedSecurityDescriptor = bindingSecurity.Capture(bindingPath);
        if (!string.Equals(
                installedSecurityDescriptor,
                rollback.IntendedBindingSecurityDescriptor,
                StringComparison.Ordinal))
        {
            throw new IOException("安裝者 SID 綁定檔的寫入後 ACL 不符合交易預定狀態。");
        }
        rollback.InstalledBindingSecurityDescriptor = installedSecurityDescriptor;
        return rollback;
    }

    public static void HardenBindingAccess(
        InstallerLayout layout,
        InstallerOperatorAccessRollback rollback,
        SecurityIdentifier serviceSid,
        IInstallerBindingSecurityStore? bindingSecurity = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rollback);
        ArgumentNullException.ThrowIfNull(serviceSid);
        bindingSecurity ??= NativeBindingSecurity;

        var bindingPath = ResolveBindingPath(layout);
        if (!string.Equals(
                bindingPath,
                rollback.BindingPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("安裝者 SID 綁定檔與回復交易的路徑不一致。");
        }
        if (rollback.InstalledBindingContent is null ||
            rollback.InstalledBindingSecurityDescriptor is null)
        {
            throw new InvalidOperationException("安裝者 SID 綁定檔尚未完成交易式寫入。");
        }

        ValidateInstalledBindingContent(rollback);
        var currentSecurityDescriptor = bindingSecurity.Capture(bindingPath);
        if (!string.Equals(
                currentSecurityDescriptor,
                rollback.InstalledBindingSecurityDescriptor,
                StringComparison.Ordinal))
        {
            throw new IOException("安裝者 SID 綁定檔 ACL 在強化前遭到外部變更；拒絕覆寫。");
        }

        var hardenedSecurity = CreateExactBindingSecurity(serviceSid);
        var hardenedSecurityDescriptor = hardenedSecurity.GetSecurityDescriptorSddlForm(
            AccessControlSections.Access | AccessControlSections.Owner);
        // Publish both admissible rollback states before the ACL mutation. If the setter
        // reports an ambiguous failure, rollback may safely accept either the old installed
        // descriptor or the exact descriptor this transaction intended to install.
        rollback.HardenedBindingSecurityDescriptor = hardenedSecurityDescriptor;
        rollback.BindingAclMutationAttempted = true;
        bindingSecurity.Apply(bindingPath, hardenedSecurityDescriptor);
        rollback.HardenedBindingSecurityDescriptor = ValidateExactBindingSecurity(
            bindingPath,
            hardenedSecurityDescriptor,
            bindingSecurity);
    }

    public static void Restore(
        IInstallerLocalGroupManager groups,
        InstallerOperatorAccessRollback? rollback,
        IInstallerBindingSecurityStore? bindingSecurity = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (rollback is null)
        {
            return;
        }
        bindingSecurity ??= NativeBindingSecurity;

        var errors = new List<Exception>();
        try
        {
            RestoreBinding(rollback, bindingSecurity);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        try
        {
            RestoreGroupAndMembership(groups, rollback);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        if (errors.Count > 0)
        {
            throw new AggregateException("無法完整回復本機操作員權限交易。", errors);
        }
    }

    internal static SecurityIdentifier ValidateInstallerAccountSid(SecurityIdentifier sid)
    {
        if (!sid.IsAccountSid() ||
            sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
            sid.IsWellKnown(WellKnownSidType.WorldSid) ||
            sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid))
        {
            throw new InvalidDataException("安裝者 SID 必須是單一、非廣泛授權的 Windows 帳號。");
        }
        return sid;
    }

    private static string ResolveBindingPath(InstallerLayout layout)
    {
        var root = Path.GetFullPath(layout.ServiceRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
            layout.ServiceRoot,
            ProductLocalIpcAccess.InstallerOperatorSidRelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("安裝者 SID 綁定檔逸出 Service 資料根目錄。");
        }
        return path;
    }

    private static byte[]? ReadOptionalValidatedBinding(string path)
    {
        InstallerLayout.RejectExistingReparsePoints(path);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = ReadLockedRegularFile(path, 5, MaximumBindingBytes);
        _ = ParseBinding(bytes);
        return bytes;
    }

    private static byte[] ReadRequiredCurrentBinding(string path)
    {
        InstallerLayout.RejectExistingReparsePoints(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("安裝者 SID 綁定檔在回復前遺失。", path);
        }
        return ReadLockedRegularFile(path, 5, MaximumBindingBytes);
    }

    private static byte[] ReadLockedRegularFile(string path, int minimumBytes, int maximumBytes)
    {
        var handle = WindowsInstallerPlatform.OpenLockedRegularFileHandle(path);
        using var stream = new FileStream(handle, FileAccess.Read, 256, isAsync: false);
        if (stream.Length < minimumBytes || stream.Length > maximumBytes)
        {
            throw new InvalidDataException("安裝者 SID 綁定檔大小無效。");
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static SecurityIdentifier ParseBinding(byte[] bytes)
    {
        if (bytes.Any(value => value > 0x7f))
        {
            throw new InvalidDataException("安裝者 SID 綁定檔不是嚴格 ASCII。");
        }

        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(Encoding.ASCII.GetString(bytes).Trim());
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("安裝者 SID 綁定檔內容無效。", error);
        }
        return ValidateInstallerAccountSid(sid);
    }

    private static void RestoreBinding(
        InstallerOperatorAccessRollback rollback,
        IInstallerBindingSecurityStore bindingSecurity)
    {
        if (!rollback.BindingWriteAttempted)
        {
            return;
        }

        InstallerLayout.RejectExistingReparsePoints(rollback.BindingPath);
        if (!File.Exists(rollback.BindingPath))
        {
            if (!rollback.BindingExisted)
            {
                return;
            }
            throw new FileNotFoundException(
                "既有安裝者 SID 綁定檔在寫入交易回復前遺失。",
                rollback.BindingPath);
        }

        var current = ReadRequiredCurrentBinding(rollback.BindingPath);
        var currentSecurityDescriptor = bindingSecurity.Capture(rollback.BindingPath);
        var matchesPreviousState = rollback.BindingExisted &&
            rollback.PreviousBindingContent is not null &&
            rollback.PreviousBindingSecurityDescriptor is not null &&
            current.AsSpan().SequenceEqual(rollback.PreviousBindingContent) &&
            string.Equals(
                currentSecurityDescriptor,
                rollback.PreviousBindingSecurityDescriptor,
                StringComparison.Ordinal);
        var matchesIntendedDescriptor =
            rollback.IntendedBindingSecurityDescriptor is not null &&
            string.Equals(
                currentSecurityDescriptor,
                rollback.IntendedBindingSecurityDescriptor,
                StringComparison.Ordinal);
        var matchesObservedDescriptor =
            rollback.InstalledBindingSecurityDescriptor is not null &&
            string.Equals(
                currentSecurityDescriptor,
                rollback.InstalledBindingSecurityDescriptor,
                StringComparison.Ordinal);
        var matchesHardenedDescriptor =
            rollback.BindingAclMutationAttempted &&
            rollback.HardenedBindingSecurityDescriptor is not null &&
            (string.Equals(
                 currentSecurityDescriptor,
                 rollback.HardenedBindingSecurityDescriptor,
                 StringComparison.Ordinal) ||
             AreEquivalentExactBindingSecurityDescriptors(
                 currentSecurityDescriptor,
                 rollback.HardenedBindingSecurityDescriptor));
        var matchesIntendedState =
            current.AsSpan().SequenceEqual(rollback.IntendedBindingContent) &&
            (matchesIntendedDescriptor || matchesObservedDescriptor || matchesHardenedDescriptor);
        if (!matchesPreviousState && !matchesIntendedState)
        {
            throw new IOException(
                "安裝者 SID 綁定檔內容或 ACL 在安裝期間遭到外部變更；拒絕覆寫或刪除。");
        }
        if (matchesPreviousState)
        {
            return;
        }

        if (rollback.BindingExisted)
        {
            var previous = rollback.PreviousBindingContent
                ?? throw new InvalidDataException("既有 SID 綁定檔的回復快照遺失。");
            var previousSecurityDescriptor = rollback.PreviousBindingSecurityDescriptor
                ?? throw new InvalidDataException("既有 SID 綁定檔的 ACL 回復快照遺失。");
            WriteAtomicBytes(
                rollback.BindingPath,
                previous,
                previousSecurityDescriptor,
                bindingSecurity);
            var restored = ReadRequiredCurrentBinding(rollback.BindingPath);
            if (!restored.AsSpan().SequenceEqual(previous) ||
                !string.Equals(
                    bindingSecurity.Capture(rollback.BindingPath),
                    previousSecurityDescriptor,
                    StringComparison.Ordinal))
            {
                throw new IOException("既有 SID 綁定檔的內容或 ACL 無法精確回復。");
            }
            return;
        }

        File.Delete(rollback.BindingPath);
    }

    private static void ValidateInstalledBindingContent(InstallerOperatorAccessRollback rollback)
    {
        var installed = rollback.InstalledBindingContent
            ?? throw new InvalidOperationException("安裝者 SID 綁定檔尚未完成交易式寫入。");
        var current = ReadRequiredCurrentBinding(rollback.BindingPath);
        if (!current.AsSpan().SequenceEqual(installed))
        {
            throw new IOException("安裝者 SID 綁定檔在安裝期間遭到外部變更；拒絕覆寫或刪除。");
        }
    }

    internal static string CaptureBindingSecurityDescriptor(string path)
        => NativeBindingSecurity.Capture(path);

    internal static FileSecurity CreateExactBindingSecurity(SecurityIdentifier serviceSid)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            serviceSid,
            FileSystemRights.Read,
            AccessControlType.Allow));
        return security;
    }

    private static string ValidateExactBindingSecurity(
        string path,
        string expectedDescriptor,
        IInstallerBindingSecurityStore bindingSecurity)
    {
        var actualDescriptor = bindingSecurity.Capture(path);
        if (!AreEquivalentExactBindingSecurityDescriptors(actualDescriptor, expectedDescriptor))
        {
            throw new UnauthorizedAccessException(
                "安裝者 SID 綁定檔未套用精確且受保護的 Service ACL。");
        }
        return actualDescriptor;
    }

    internal static bool AreEquivalentExactBindingSecurityDescriptors(
        string actualDescriptor,
        string expectedDescriptor)
    {
        try
        {
            if (!InstallerSecurityDescriptorComparer.EqualsAllowingDaclAutoInherited(
                    actualDescriptor,
                    expectedDescriptor))
            {
                return false;
            }

            var expected = ParseBindingSecurity(expectedDescriptor);
            if (!TryGetExactBindingServiceSid(expected, out var serviceSid))
            {
                return false;
            }

            return HasExactBindingSecurity(ParseBindingSecurity(actualDescriptor), serviceSid);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static FileSecurity ParseBindingSecurity(string descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor);
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(
            descriptor,
            AccessControlSections.Access | AccessControlSections.Owner);
        return security;
    }

    private static bool TryGetExactBindingServiceSid(
        FileSecurity security,
        out SecurityIdentifier serviceSid)
    {
        serviceSid = null!;
        if (!TryReadExactBindingRules(security, out var rules))
        {
            return false;
        }

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var candidates = rules
            .Where(rule =>
                !rule.IdentityReference.Equals(systemSid) &&
                !rule.IdentityReference.Equals(administratorsSid))
            .ToArray();
        if (candidates.Length != 1 ||
            candidates[0].IdentityReference is not SecurityIdentifier candidate ||
            candidates[0].FileSystemRights != (FileSystemRights.Read | FileSystemRights.Synchronize))
        {
            return false;
        }

        serviceSid = candidate;
        return HasExactBindingSecurity(security, serviceSid);
    }

    private static bool HasExactBindingSecurity(
        FileSecurity security,
        SecurityIdentifier serviceSid)
    {
        if (!TryReadExactBindingRules(security, out var rules))
        {
            return false;
        }

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        return HasSingleRule(rules, systemSid, FileSystemRights.FullControl) &&
               HasSingleRule(rules, administratorsSid, FileSystemRights.FullControl) &&
               HasSingleRule(
                   rules,
                   serviceSid,
                   FileSystemRights.Read | FileSystemRights.Synchronize);
    }

    private static bool TryReadExactBindingRules(
        FileSecurity security,
        out FileSystemAccessRule[] rules)
    {
        rules = [];
        var administratorsSid = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        if (!security.AreAccessRulesProtected ||
            security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !owner.Equals(administratorsSid))
        {
            return false;
        }

        rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        return rules.Length == 3 && rules.All(rule =>
            !rule.IsInherited &&
            rule.AccessControlType == AccessControlType.Allow &&
            rule.InheritanceFlags == InheritanceFlags.None &&
            rule.PropagationFlags == PropagationFlags.None);
    }

    private static bool HasSingleRule(
        IReadOnlyList<FileSystemAccessRule> rules,
        SecurityIdentifier sid,
        FileSystemRights rights)
        => rules.Count(rule =>
            rule.IdentityReference.Equals(sid) &&
            rule.FileSystemRights == rights) == 1;

    private static void RestoreGroupAndMembership(
        IInstallerLocalGroupManager groups,
        InstallerOperatorAccessRollback rollback)
    {
        if (rollback.GroupSid is null)
        {
            if (rollback.GroupCreated)
            {
                throw new IOException(
                    "本次建立的本機操作員群組無法驗證 SID；為避免誤刪已安全保留。");
            }
            return;
        }

        var group = groups.TryGetGroup(rollback.GroupName);
        if (group is null)
        {
            throw new IOException(
                "本機操作員群組在回復前遺失或遭到重新命名；無法證明交易已精確回復。");
        }
        var expectedGroupSid = new SecurityIdentifier(rollback.GroupSid);
        ValidateExactGroupIdentity(group, expectedGroupSid);

        var installerSid = new SecurityIdentifier(rollback.InstallerSid);
        if (rollback.MemberAdded && groups.GetMembers(group.Name).Contains(installerSid))
        {
            groups.RemoveMember(group.Name, installerSid);
        }

        if (!rollback.GroupCreated)
        {
            return;
        }

        // Re-read both identity and membership immediately before deletion. Only the exact,
        // still-empty group created by this transaction is eligible for removal.
        group = groups.TryGetGroup(rollback.GroupName)
            ?? throw new IOException("本次建立的本機操作員群組在回復驗證期間遺失。");
        ValidateExactGroupIdentity(group, expectedGroupSid);
        if (groups.GetMembers(group.Name).Count != 0)
        {
            throw new IOException("本次建立的本機操作員群組已有其他成員；拒絕刪除。");
        }
        group = groups.TryGetGroup(rollback.GroupName)
            ?? throw new IOException("本次建立的本機操作員群組在刪除前遺失。");
        ValidateExactGroupIdentity(group, expectedGroupSid);
        if (groups.GetMembers(group.Name).Count != 0)
        {
            throw new IOException("本機操作員群組在刪除前遭到變更；拒絕刪除。");
        }
        groups.DeleteGroup(group.Name);
    }

    private static void ValidateExactGroupIdentity(
        InstallerLocalGroupIdentity group,
        SecurityIdentifier? expectedSid)
    {
        if (!string.Equals(group.Name, GroupName, StringComparison.Ordinal) ||
            !string.Equals(group.Description, GroupDescription, StringComparison.Ordinal) ||
            !group.Sid.IsAccountSid() ||
            (expectedSid is not null && !group.Sid.Equals(expectedSid)))
        {
            throw new InvalidDataException("本機操作員群組的名稱、描述或 SID 不符合受管理身分。");
        }
    }

    private static void WriteAtomicBytes(
        string path,
        byte[] content,
        string? replacementSecurityDescriptor = null,
        IInstallerBindingSecurityStore? bindingSecurity = null,
        Action<string>? beforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("SID 綁定檔缺少父目錄。");
        Directory.CreateDirectory(parent);
        InstallerLayout.RejectExistingReparsePoints(parent);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       256,
                       FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            bindingSecurity ??= NativeBindingSecurity;
            if (replacementSecurityDescriptor is not null)
            {
                bindingSecurity.Apply(temporary, replacementSecurityDescriptor);
            }
            var intendedSecurityDescriptor = bindingSecurity.Capture(temporary);
            beforeCommit?.Invoke(intendedSecurityDescriptor);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

internal sealed class WindowsInstallerLocalGroupManager : IInstallerLocalGroupManager
{
    private const uint Success = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoSuchAlias = 1376;
    private const uint ErrorMemberNotInAlias = 1377;
    private const uint ErrorMemberInAlias = 1378;
    private const uint ErrorAliasExists = 1379;
    private const uint ErrorNoSuchMember = 1387;
    private const uint NerrGroupNotFound = 2220;
    private const uint NerrGroupExists = 2223;
    private const uint NerrUserExists = 2224;
    private const uint MaximumPreferredLength = 64 * 1024;
    private const int MaximumMemberPages = 128;
    private const int MaximumMembers = 4096;

    public InstallerLocalGroupIdentity EnsureGroup(
        string name,
        string description,
        Action groupCreated)
    {
        ArgumentNullException.ThrowIfNull(groupCreated);
        var existing = TryGetGroup(name);
        if (existing is not null)
        {
            ValidateManagedGroup(existing, name, description);
            return existing with { Created = false };
        }

        var namePointer = Marshal.StringToHGlobalUni(name);
        var descriptionPointer = Marshal.StringToHGlobalUni(description);
        uint status;
        try
        {
            var info = new LocalGroupInfo1
            {
                Name = namePointer,
                Comment = descriptionPointer,
            };
            status = NetLocalGroupAdd(null, 1, ref info, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(descriptionPointer);
            Marshal.FreeHGlobal(namePointer);
        }

        if (status is ErrorAliasExists or NerrGroupExists or NerrUserExists)
        {
            existing = TryGetGroup(name)
                ?? throw CreateError(status, "本機操作員群組建立競爭後無法重新解析");
            ValidateManagedGroup(existing, name, description);
            return existing with { Created = false };
        }
        ThrowIfError(status, "建立本機操作員群組");
        groupCreated();
        var created = TryGetGroup(name)
            ?? throw new IOException("NetAPI32 回報群組已建立，但無法重新讀取。");
        ValidateManagedGroup(created, name, description);
        return created with { Created = true };
    }

    public InstallerLocalGroupIdentity? TryGetGroup(string name)
    {
        var status = NetLocalGroupGetInfo(null, name, 1, out var buffer);
        try
        {
            if (status is NerrGroupNotFound or ErrorNoSuchAlias)
            {
                return null;
            }
            ThrowIfError(status, "讀取本機操作員群組");
            if (buffer == IntPtr.Zero)
            {
                throw new InvalidDataException("NetAPI32 回傳空白群組資料緩衝區。");
            }
            var info = Marshal.PtrToStructure<LocalGroupInfo1>(buffer);
            var actualName = Marshal.PtrToStringUni(info.Name)
                ?? throw new InvalidDataException("NetAPI32 群組名稱遺失。");
            var description = Marshal.PtrToStringUni(info.Comment) ?? string.Empty;
            SecurityIdentifier sid;
            try
            {
                sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, actualName)
                    .Translate(typeof(SecurityIdentifier));
            }
            catch (IdentityNotMappedException error)
            {
                throw new InvalidDataException("無法將本機操作員群組轉換成 SID。", error);
            }
            return new InstallerLocalGroupIdentity(actualName, description, sid, Created: false);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = NetApiBufferFree(buffer);
            }
        }
    }

    public IReadOnlyList<SecurityIdentifier> GetMembers(string name)
    {
        var result = new HashSet<SecurityIdentifier>();
        UIntPtr resume = UIntPtr.Zero;
        var totalRead = 0;
        for (var page = 0; page < MaximumMemberPages; page++)
        {
            var status = NetLocalGroupGetMembers(
                null,
                name,
                0,
                out var buffer,
                MaximumPreferredLength,
                out var entriesRead,
                out _,
                ref resume);
            try
            {
                if (status is NerrGroupNotFound or ErrorNoSuchAlias)
                {
                    throw new IOException("本機操作員群組在列舉會員時遺失。");
                }
                if (status is not Success and not ErrorMoreData)
                {
                    throw CreateError(status, "列舉本機操作員群組會員");
                }
                if (entriesRead > MaximumMembers - totalRead)
                {
                    throw new InvalidDataException("本機操作員群組會員數超出安全上限。");
                }
                totalRead = checked(totalRead + (int)entriesRead);
                if (entriesRead > 0 && buffer == IntPtr.Zero)
                {
                    throw new InvalidDataException("NetAPI32 回傳空白群組會員緩衝區。");
                }

                var itemSize = Marshal.SizeOf<LocalGroupMembersInfo0>();
                for (uint index = 0; index < entriesRead; index++)
                {
                    var offset = checked((int)(index * (uint)itemSize));
                    var item = Marshal.PtrToStructure<LocalGroupMembersInfo0>(
                        IntPtr.Add(buffer, offset));
                    if (item.Sid == IntPtr.Zero)
                    {
                        throw new InvalidDataException("NetAPI32 回傳空白群組會員 SID。");
                    }
                    result.Add(new SecurityIdentifier(item.Sid));
                }
                if (status == Success)
                {
                    return result.ToArray();
                }
                if (entriesRead == 0)
                {
                    throw new IOException("NetAPI32 群組會員分頁沒有進度。");
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    _ = NetApiBufferFree(buffer);
                }
            }
        }
        throw new InvalidDataException("本機操作員群組會員分頁超出安全上限。");
    }

    public bool AddMember(string name, SecurityIdentifier memberSid)
    {
        if (GetMembers(name).Contains(memberSid))
        {
            return false;
        }
        var status = InvokeMemberMutation(NetLocalGroupAddMembers, name, memberSid);
        if (status == ErrorMemberInAlias)
        {
            return false;
        }
        ThrowIfError(status, "加入本機操作員群組會員");
        return true;
    }

    public void RemoveMember(string name, SecurityIdentifier memberSid)
    {
        var status = InvokeMemberMutation(NetLocalGroupDelMembers, name, memberSid);
        if (status is ErrorMemberNotInAlias or ErrorNoSuchMember)
        {
            return;
        }
        ThrowIfError(status, "移除本機操作員群組會員");
    }

    public void DeleteGroup(string name)
        => ThrowIfError(NetLocalGroupDel(null, name), "刪除本機操作員群組");

    private static uint InvokeMemberMutation(
        LocalGroupMemberMutation mutation,
        string name,
        SecurityIdentifier memberSid)
    {
        var bytes = new byte[memberSid.BinaryLength];
        memberSid.GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var member = new LocalGroupMembersInfo0 { Sid = pinned.AddrOfPinnedObject() };
            return mutation(null, name, 0, ref member, 1);
        }
        finally
        {
            pinned.Free();
        }
    }

    private static void ValidateManagedGroup(
        InstallerLocalGroupIdentity group,
        string expectedName,
        string expectedDescription)
    {
        if (!string.Equals(group.Name, expectedName, StringComparison.Ordinal) ||
            !string.Equals(group.Description, expectedDescription, StringComparison.Ordinal) ||
            !group.Sid.IsAccountSid())
        {
            throw new InvalidDataException("既有本機操作員群組不是受管理的精確身分。");
        }
    }

    private static void ThrowIfError(uint status, string operation)
    {
        if (status != Success)
        {
            throw CreateError(status, operation);
        }
    }

    private static Win32Exception CreateError(uint status, string operation)
        => new(checked((int)status), $"{operation}失敗 (NetAPI32 {status})。");

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupInfo1
    {
        public IntPtr Name;
        public IntPtr Comment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupMembersInfo0
    {
        public IntPtr Sid;
    }

    private delegate uint LocalGroupMemberMutation(
        string? serverName,
        string groupName,
        uint level,
        ref LocalGroupMembersInfo0 buffer,
        uint totalEntries);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint NetLocalGroupGetInfo(
        string? serverName,
        string groupName,
        uint level,
        out IntPtr buffer);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint NetLocalGroupAdd(
        string? serverName,
        uint level,
        ref LocalGroupInfo1 buffer,
        out uint parameterError);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint NetLocalGroupGetMembers(
        string? serverName,
        string groupName,
        uint level,
        out IntPtr buffer,
        uint preferredMaximumLength,
        out uint entriesRead,
        out uint totalEntries,
        ref UIntPtr resumeHandle);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint NetLocalGroupAddMembers(
        string? serverName,
        string groupName,
        uint level,
        ref LocalGroupMembersInfo0 buffer,
        uint totalEntries);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint NetLocalGroupDelMembers(
        string? serverName,
        string groupName,
        uint level,
        ref LocalGroupMembersInfo0 buffer,
        uint totalEntries);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint NetLocalGroupDel(string? serverName, string groupName);

    [DllImport("Netapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint NetApiBufferFree(IntPtr buffer);
}

internal sealed partial class WindowsInstallerPlatform
{
    private readonly IInstallerLocalGroupManager _localGroups;

    internal WindowsInstallerPlatform()
        : this(new WindowsInstallerLocalGroupManager())
    {
    }

    internal WindowsInstallerPlatform(IInstallerLocalGroupManager localGroups)
    {
        _localGroups = localGroups ?? throw new ArgumentNullException(nameof(localGroups));
    }

    public InstallerOperatorAccessRollback ProvisionOperatorAccess(
        InstallerLayout layout,
        Action<InstallerOperatorAccessRollback> snapshotCaptured)
        => InstallerOperatorAccessTransaction.Provision(
            _localGroups,
            layout,
            new SecurityIdentifier(CurrentUserSid),
            snapshotCaptured);

    public void HardenOperatorBindingAccess(
        InstallerLayout layout,
        string serviceName,
        InstallerOperatorAccessRollback rollback)
        => InstallerOperatorAccessTransaction.HardenBindingAccess(
            layout,
            rollback,
            ResolveServiceSid(serviceName));

    public void RestoreOperatorAccess(InstallerOperatorAccessRollback? rollback)
        => InstallerOperatorAccessTransaction.Restore(_localGroups, rollback);

    internal static SecurityIdentifier ResolveRequiredOperatorGroupSid()
    {
        var group = new WindowsInstallerLocalGroupManager().TryGetGroup(
            InstallerOperatorAccessTransaction.GroupName)
            ?? throw new InvalidDataException("既有安裝缺少受管理的本機操作員群組。");
        if (!string.Equals(
                group.Description,
                InstallerOperatorAccessTransaction.GroupDescription,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("既有本機操作員群組描述不符合受管理身分。");
        }
        return ValidateOperatorGroupSid(group.Sid.Value);
    }

    internal static SecurityIdentifier ValidateOperatorGroupSid(string value)
    {
        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(value);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("本機操作員群組 SID 無效。", error);
        }
        if (!sid.IsAccountSid() ||
            sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
            sid.IsWellKnown(WellKnownSidType.WorldSid) ||
            sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid))
        {
            throw new InvalidDataException("本機操作員群組 SID 不是非廣泛授權的帳號 SID。");
        }
        return sid;
    }
}
