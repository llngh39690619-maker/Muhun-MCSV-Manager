using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class DpapiCurseForgeCredentialStoreTests
{
    [Fact]
    public void ApplicationPaths_KeepCredentialInsideCurrentUsersClientSecrets()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);

        Assert.Equal(
            Path.Combine(paths.ClientSecrets, "curseforge-api-key.v1.secret"),
            paths.CurseForgeCredentialFile);
        Assert.StartsWith(
            Path.GetFullPath(paths.ClientSecrets) + Path.DirectorySeparatorChar,
            Path.GetFullPath(paths.CurseForgeCredentialFile),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionConstructor_BindsCredentialToTheClientInstallationIdentity()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var value = CreateSyntheticCredential();
        var store = new DpapiCurseForgeCredentialStore(paths);
        using var source = ToSecureString(value);

        store.Save(source);

        Assert.True(File.Exists(Path.Combine(paths.ClientRoot, "installation.id")));
        Assert.True(File.Exists(paths.CurseForgeCredentialFile));
        using var acquired = Assert.IsType<SecureString>(
            new DpapiCurseForgeCredentialStore(paths).AcquireReadOnly());
        Assert.Equal(value, ReadForAssertion(acquired));
    }

    [Fact]
    public void RoundTripAndReopen_UsesReadOnlyCopyWithoutPersistingPlaintext()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var path = Path.Combine(directory.Path, "curseforge-api-key.v1.secret");
        var installationId = Guid.NewGuid();
        var value = CreateSyntheticCredential();
        var store = new DpapiCurseForgeCredentialStore(path, installationId);
        using var source = ToSecureString(value);

        Assert.False(store.HasCredential);
        store.Save(source);

        Assert.True(store.HasCredential);
        using (var acquired = Assert.IsType<SecureString>(store.AcquireReadOnly()))
        {
            Assert.True(acquired.IsReadOnly());
            Assert.Equal(value, ReadForAssertion(acquired));
        }

        var raw = File.ReadAllBytes(path);
        Assert.DoesNotContain(value, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));

        var reopened = new DpapiCurseForgeCredentialStore(path, installationId);
        using var reopenedCredential = Assert.IsType<SecureString>(reopened.AcquireReadOnly());
        Assert.Equal(value, ReadForAssertion(reopenedCredential));
    }

    [Fact]
    public void ReplaceAndDelete_ExposeOnlyTheLatestCredentialAndDeleteIsIdempotent()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var path = Path.Combine(directory.Path, "curseforge-api-key.v1.secret");
        var installationId = Guid.NewGuid();
        var first = CreateSyntheticCredential();
        var second = CreateSyntheticCredential();
        var store = new DpapiCurseForgeCredentialStore(path, installationId);
        using var firstSecret = ToSecureString(first);
        using var secondSecret = ToSecureString(second);

        store.Save(firstSecret);
        store.Save(secondSecret);

        using (var acquired = Assert.IsType<SecureString>(
                   new DpapiCurseForgeCredentialStore(path, installationId).AcquireReadOnly()))
        {
            Assert.Equal(second, ReadForAssertion(acquired));
        }

        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.DoesNotContain(first, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(second, raw, StringComparison.Ordinal);
        Assert.True(store.Delete());
        Assert.False(File.Exists(path));
        Assert.Null(store.AcquireReadOnly());
        Assert.False(store.Delete());
    }

    [Fact]
    public void DifferentInstallationEntropy_CannotDecryptCredential()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var path = Path.Combine(directory.Path, "curseforge-api-key.v1.secret");
        var store = new DpapiCurseForgeCredentialStore(path, Guid.NewGuid());
        using var credential = ToSecureString(CreateSyntheticCredential());
        store.Save(credential);

        var wrongInstallation = new DpapiCurseForgeCredentialStore(path, Guid.NewGuid());

        Assert.Throws<CryptographicException>(() => wrongInstallation.AcquireReadOnly());
    }

    [Fact]
    public void InvalidReplacement_IsRejectedWithoutChangingExistingCredential()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var path = Path.Combine(directory.Path, "curseforge-api-key.v1.secret");
        var installationId = Guid.NewGuid();
        var original = CreateSyntheticCredential();
        var store = new DpapiCurseForgeCredentialStore(path, installationId);
        using var originalSecret = ToSecureString(original);
        store.Save(originalSecret);
        var protectedOriginal = File.ReadAllBytes(path);
        using var invalid = ToSecureString("invalid\r\nheader");
        using var empty = new SecureString();
        using var oversized = ToSecureString(new string('x', 257));

        Assert.Throws<ArgumentException>(() => store.Save(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(oversized));
        Assert.Equal(protectedOriginal, File.ReadAllBytes(path));
        using var acquired = Assert.IsType<SecureString>(store.AcquireReadOnly());
        Assert.Equal(original, ReadForAssertion(acquired));
    }

    [Fact]
    public void CorruptOrOversizedFiles_FailClosedWithoutRewritingInput()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var path = Path.Combine(directory.Path, "curseforge-api-key.v1.secret");
        var store = new DpapiCurseForgeCredentialStore(path, Guid.NewGuid());
        using var credential = ToSecureString(CreateSyntheticCredential());
        store.Save(credential);

        var corrupt = File.ReadAllBytes(path);
        corrupt[0] ^= 0x7f;
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllBytes(path, corrupt);
        Assert.Throws<InvalidDataException>(() => store.AcquireReadOnly());
        Assert.Throws<InvalidDataException>(() => _ = store.HasCredential);
        Assert.Equal(corrupt, File.ReadAllBytes(path));

        var oversized = new byte[128 * 1024];
        RandomNumberGenerator.Fill(oversized);
        File.WriteAllBytes(path, oversized);
        Assert.Throws<InvalidDataException>(() => store.AcquireReadOnly());
        Assert.Equal(oversized, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ConcurrentReplacements_AreSerializedAndNeverExposeAPartialCredential()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var path = Path.Combine(directory.Path, "curseforge-api-key.v1.secret");
        var store = new DpapiCurseForgeCredentialStore(path, Guid.NewGuid());
        var candidates = Enumerable.Range(0, 8)
            .Select(_ => CreateSyntheticCredential())
            .ToArray();

        await Task.WhenAll(candidates.Select(value => Task.Run(() =>
        {
            using var credential = ToSecureString(value);
            store.Save(credential);
            using var acquired = Assert.IsType<SecureString>(store.AcquireReadOnly());
            Assert.Contains(ReadForAssertion(acquired), candidates);
        })));

        using var final = Assert.IsType<SecureString>(store.AcquireReadOnly());
        Assert.Contains(ReadForAssertion(final), candidates);
        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.All(candidates, value => Assert.DoesNotContain(value, raw, StringComparison.Ordinal));
    }

    [Fact]
    public void SourceContract_UsesCurrentUserDpapiAndNeverConvertsCredentialToManagedString()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource(
            Path.Combine("Services", "DpapiCurseForgeCredentialStore.cs")));

        Assert.Contains("DataProtectionScope.CurrentUser", source, StringComparison.Ordinal);
        Assert.Contains("FileOptions.WriteThrough", source, StringComparison.Ordinal);
        Assert.Contains("File.Move(temporaryPath, _credentialPath, overwrite: true)", source, StringComparison.Ordinal);
        Assert.Contains("RejectReparsePoint", source, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory", source, StringComparison.Ordinal);
        Assert.Contains("Marshal.ZeroFreeGlobalAllocUnicode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PtrToString", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.SetEnvironmentVariable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ILogger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.", source, StringComparison.Ordinal);
    }

    private static string CreateSyntheticCredential()
        => $"test-only-{Guid.NewGuid():N}-{Guid.NewGuid():N}";

    private static SecureString ToSecureString(string value)
    {
        var result = new SecureString();
        foreach (var character in value)
        {
            result.AppendChar(character);
        }

        return result;
    }

    private static string ReadForAssertion(SecureString value)
    {
        var native = IntPtr.Zero;
        try
        {
            native = Marshal.SecureStringToBSTR(value);
            return Marshal.PtrToStringBSTR(native);
        }
        finally
        {
            if (native != IntPtr.Zero)
            {
                Marshal.ZeroFreeBSTR(native);
            }
        }
    }
}
