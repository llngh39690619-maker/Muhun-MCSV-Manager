using System.ComponentModel;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class ProtectedFormalReleaseStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mcsv-protected-release-staging-tests",
        Guid.NewGuid().ToString("N"));

    public ProtectedFormalReleaseStagingTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Stage_UsesUniqueChildOfVerifiedLauncherAndValidatesCopiedTree()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var launcher = Directory.CreateDirectory(Path.Combine(_root, "managed", "launcher")).FullName;
        var copy = new RecordingCopyBroker(createDestination: true);
        var security = new RecordingSecurityValidator();
        var stager = new WindowsShellProtectedFormalReleaseStager(
            new FixedLauncherResolver(launcher),
            copy,
            security,
            () => "0123456789abcdef0123456789abcdef");

        var result = await stager.StageAsync(source, "1.2.9-beta.3", CancellationToken.None);

        var expectedName = ".repair-staging-1.2.9-beta.3-0123456789abcdef0123456789abcdef";
        var expectedRoot = Path.Combine(launcher, expectedName);
        Assert.Equal(expectedRoot, result.ReleaseRoot);
        Assert.Equal((source, launcher, expectedName), Assert.Single(copy.Invocations));
        Assert.Equal(
            new[]
            {
                (Directory.GetParent(launcher)!.FullName, false),
                (launcher, true),
            },
            security.ContainerInvocations);
        Assert.Equal(expectedRoot, Assert.Single(security.TreeInvocations));
    }

    [Theory]
    [InlineData("UPPERCASE0123456789ABCDEF01234567")]
    [InlineData("too-short")]
    public async Task Stage_InvalidNonceFailsBeforeWindowsBroker(string nonce)
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
        var launcher = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"), "launcher")).FullName;
        var copy = new RecordingCopyBroker(createDestination: true);
        var stager = new WindowsShellProtectedFormalReleaseStager(
            new FixedLauncherResolver(launcher),
            copy,
            new RecordingSecurityValidator(),
            () => nonce);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            stager.StageAsync(source, "1.2.9-beta.3", CancellationToken.None));

        Assert.Empty(copy.Invocations);
    }

    [Fact]
    public async Task Stage_UacCancellationIsPropagatedWithoutReturningAProtectedPath()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "cancel-source")).FullName;
        var launcher = Directory.CreateDirectory(Path.Combine(_root, "cancel-managed", "launcher")).FullName;
        var stager = new WindowsShellProtectedFormalReleaseStager(
            new FixedLauncherResolver(launcher),
            new RecordingCopyBroker(new Win32Exception(1223)),
            new RecordingSecurityValidator(),
            () => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var error = await Assert.ThrowsAsync<Win32Exception>(() =>
            stager.StageAsync(source, "1.2.9-beta.3", CancellationToken.None));

        Assert.Equal(1223, error.NativeErrorCode);
    }

    [Fact]
    public async Task Stage_PostCopyValidationFailure_DeletesOnlyVerifiedOwnedStage()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "invalid-source")).FullName;
        var launcher = Directory.CreateDirectory(Path.Combine(_root, "invalid-managed", "launcher")).FullName;
        var copy = new RecordingCopyBroker(createDestination: true);
        var security = new FailingTreeValidationSecurityValidator();
        var stager = new WindowsShellProtectedFormalReleaseStager(
            new FixedLauncherResolver(launcher),
            copy,
            security,
            () => "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            stager.StageAsync(source, "1.2.9-beta.3", CancellationToken.None));

        var deleted = Assert.Single(copy.DeleteInvocations);
        Assert.Equal(
            Path.Combine(launcher, ".repair-staging-1.2.9-beta.3-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            deleted);
    }

    [Fact]
    public async Task Cleanup_RefusesMalformedOrNonLauncherDirectories()
    {
        var launcher = Directory.CreateDirectory(Path.Combine(_root, "cleanup-managed", "launcher")).FullName;
        var malformed = Directory.CreateDirectory(Path.Combine(launcher, "repair-staging-not-owned")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(
            _root,
            ".repair-staging-1.2.9-beta.3-cccccccccccccccccccccccccccccccc")).FullName;
        var copy = new RecordingCopyBroker(createDestination: false);
        var stager = new WindowsShellProtectedFormalReleaseStager(
            new FixedLauncherResolver(launcher),
            copy,
            new RecordingSecurityValidator(),
            () => "dddddddddddddddddddddddddddddddd");

        await stager.TryCleanupAsync(new ProtectedFormalReleaseStage(malformed));
        await stager.TryCleanupAsync(new ProtectedFormalReleaseStage(outside));

        Assert.Empty(copy.DeleteInvocations);
        Assert.True(Directory.Exists(malformed));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task Cleanup_ValidOwnedStageUsesInheritedInstallRootAndProtectedLauncherBoundary()
    {
        var launcher = Directory.CreateDirectory(Path.Combine(_root, "owned-cleanup", "launcher")).FullName;
        var stage = Directory.CreateDirectory(Path.Combine(
            launcher,
            ".repair-staging-1.2.9-beta.3-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")).FullName;
        var copy = new RecordingCopyBroker(createDestination: false);
        var security = new RecordingSecurityValidator();
        var stager = new WindowsShellProtectedFormalReleaseStager(
            new FixedLauncherResolver(launcher),
            copy,
            security,
            () => "ffffffffffffffffffffffffffffffff");

        await stager.TryCleanupAsync(new ProtectedFormalReleaseStage(stage));

        Assert.Equal(
            new[]
            {
                (Directory.GetParent(launcher)!.FullName, false),
                (launcher, true),
            },
            security.ContainerInvocations);
        Assert.Equal(stage, Assert.Single(copy.DeleteInvocations));
    }

    [Fact]
    public void ProtectedAcl_AllowsReadOnlyUsersButRejectsWritableBroadOrCurrentUserRules()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var currentUser = new SecurityIdentifier("S-1-5-21-1000-1000-1000-1001");
        var readOnly = CreateDescriptor(administrators);
        readOnly.AddAccessRule(new FileSystemAccessRule(
            users,
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));

        WindowsProtectedProductPathSecurityValidator.ValidateSecurityDescriptor(
            readOnly,
            requireProtectedAccessRules: true,
            currentUser);

        var broadWrite = CreateDescriptor(administrators);
        broadWrite.AddAccessRule(new FileSystemAccessRule(
            users,
            FileSystemRights.Modify,
            AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsProtectedProductPathSecurityValidator.ValidateSecurityDescriptor(
                broadWrite,
                requireProtectedAccessRules: true,
                currentUser));

        var currentUserWrite = CreateDescriptor(administrators);
        currentUserWrite.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.Write | FileSystemRights.Delete,
            AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsProtectedProductPathSecurityValidator.ValidateSecurityDescriptor(
                currentUserWrite,
                requireProtectedAccessRules: true,
                currentUser));
    }

    [Fact]
    public void ProtectedAcl_RejectsCurrentUserOwnerEvenWhenDaclIsReadOnly()
    {
        var currentUser = new SecurityIdentifier("S-1-5-21-1000-1000-1000-1001");
        var descriptor = CreateDescriptor(currentUser);

        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsProtectedProductPathSecurityValidator.ValidateSecurityDescriptor(
                descriptor,
                requireProtectedAccessRules: true,
                currentUser));
    }

    [Theory]
    [InlineData("S-1-5-32-547")] // Power Users
    [InlineData("S-1-5-32-551")] // Backup Operators
    [InlineData("S-1-5-21-1000-1000-1000-4321")] // arbitrary custom principal
    public void ProtectedAcl_RejectsDangerousAllowAceForEveryNonPrivilegedSid(string sidValue)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var descriptor = CreateDescriptor(administrators);
        descriptor.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sidValue),
            FileSystemRights.WriteData | FileSystemRights.Delete,
            AccessControlType.Allow));

        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsProtectedProductPathSecurityValidator.ValidateSecurityDescriptor(
                descriptor,
                requireProtectedAccessRules: true));
    }

    [Theory]
    [InlineData("GA")]
    [InlineData("GW")]
    public void ProtectedAcl_RejectsRawGenericDangerousRightsForUnknownSid(string sddlRights)
    {
        var descriptor = new DirectorySecurity();
        descriptor.SetSecurityDescriptorSddlForm(
            $"O:BAG:BAD:P(A;;{sddlRights};;;S-1-5-21-1000-1000-1000-7654)");

        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsProtectedProductPathSecurityValidator.ValidateSecurityDescriptor(
                descriptor,
                requireProtectedAccessRules: true));
    }

    [Fact]
    public void ProtectedAcl_RejectsNullDacl()
    {
        var descriptor = new DirectorySecurity();
        descriptor.SetSecurityDescriptorSddlForm("O:BAG:BA");

        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsProtectedProductPathSecurityValidator.ValidateSecurityDescriptor(
                descriptor,
                requireProtectedAccessRules: false));
    }

    [Fact]
    public void ProtectedAcl_AllowsProgramFilesStyleInheritedRootWithInheritOnlyCreatorOwner()
    {
        var descriptor = new DirectorySecurity();
        descriptor.SetSecurityDescriptorSddlForm(
            "O:BAG:BAD:(A;OICIIO;GA;;;CO)(A;;GRGX;;;BU)");

        WindowsProtectedProductPathSecurityValidator.ValidateSecurityDescriptor(
            descriptor,
            requireProtectedAccessRules: false);

        Assert.False(descriptor.AreAccessRulesProtected);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup on Windows CI.
        }
    }

    private static DirectorySecurity CreateDescriptor(SecurityIdentifier owner)
    {
        var descriptor = new DirectorySecurity();
        descriptor.SetOwner(owner);
        descriptor.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        return descriptor;
    }

    private sealed class FixedLauncherResolver(string launcher)
        : IManagedProductLauncherDirectoryResolver
    {
        public string ResolveLauncherDirectory() => launcher;
    }

    private sealed class RecordingCopyBroker : IWindowsProtectedDirectoryCopyBroker
    {
        private readonly bool _createDestination;
        private readonly Exception? _error;

        public RecordingCopyBroker(bool createDestination)
        {
            _createDestination = createDestination;
        }

        public RecordingCopyBroker(Exception error)
        {
            _error = error;
        }

        public List<(string Source, string Destination, string Name)> Invocations { get; } = [];
        public List<string> DeleteInvocations { get; } = [];

        public Task CopyDirectoryAsync(
            string sourceDirectory,
            string destinationParentDirectory,
            string destinationName,
            CancellationToken cancellationToken)
        {
            Invocations.Add((sourceDirectory, destinationParentDirectory, destinationName));
            if (_error is not null)
            {
                return Task.FromException(_error);
            }

            if (_createDestination)
            {
                Directory.CreateDirectory(Path.Combine(destinationParentDirectory, destinationName));
            }

            return Task.CompletedTask;
        }

        public Task DeleteDirectoryAsync(string directory, CancellationToken cancellationToken)
        {
            DeleteInvocations.Add(directory);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSecurityValidator : IProtectedProductPathSecurityValidator
    {
        public List<(string Path, bool RequireProtected)> ContainerInvocations { get; } = [];
        public List<string> TreeInvocations { get; } = [];

        public void ValidateContainer(string path, bool requireProtectedAccessRules)
            => ContainerInvocations.Add((path, requireProtectedAccessRules));

        public void ValidateTree(string root)
            => TreeInvocations.Add(root);
    }

    private sealed class FailingTreeValidationSecurityValidator
        : IProtectedProductPathSecurityValidator
    {
        public void ValidateContainer(string path, bool requireProtectedAccessRules)
        {
        }

        public void ValidateTree(string root)
            => throw new InvalidDataException("post-copy validation failed");
    }
}
