using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.App.Tests;

public sealed class RemoteSecurityStoreTests
{
    [Fact]
    public void DpapiFile_PersistsSmtpAndCredentialWithoutPlaintext()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);

        store.SaveSmtpCredential("sender@gmail.com", "abcd efgh ijkl mnop");
        store.RegisterAccount("owner@gmail.com", "Account1", "01234567");

        var fileText = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.DoesNotContain("sender@gmail.com", fileText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner@gmail.com", fileText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account1", fileText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abcdefghijklmnop", fileText, StringComparison.Ordinal);
        Assert.DoesNotContain("01234567", fileText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "01234567",
            ReadProtectedJson(path).ToJsonString(),
            StringComparison.Ordinal);

        var reopened = new RemoteSecurityStore(path);
        Assert.True(reopened.IsAvailable);
        Assert.Equal("sender@gmail.com", reopened.SmtpSenderGmail);
        Assert.Equal("account1", reopened.ApprovedAccount?.Username);
        Assert.True(reopened.ApprovedAccount?.HasRecoverablePin);
        Assert.Equal("01234567", reopened.GetRecoverablePin("account1"));
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            reopened.Authenticate("owner@gmail.com", "account1", "01234567").Status);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.InvalidCredentials,
            reopened.Authenticate("owner@gmail.com", "account1", "87654321").Status);
    }

    [Fact]
    public void FifthWrongPin_LocksCredentialAndLockoutSurvivesReload()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 22, 4, 0, 0, TimeSpan.Zero));
        var store = new RemoteSecurityStore(path, time);
        store.RegisterAccount("owner@gmail.com", "account1", "12345678");

        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.Equal(
                RemoteCredentialAuthenticationStatus.InvalidCredentials,
                store.Authenticate("owner@gmail.com", "account1", "00000000").Status);
        }

        var locked = store.Authenticate("owner@gmail.com", "account1", "00000000");
        Assert.Equal(RemoteCredentialAuthenticationStatus.LockedOut, locked.Status);
        Assert.Equal(time.GetUtcNow().AddMinutes(15), locked.LockedUntilUtc);

        var reopened = new RemoteSecurityStore(path, time);
        var stillLocked = reopened.Authenticate("owner@gmail.com", "account1", "12345678");
        Assert.Equal(RemoteCredentialAuthenticationStatus.LockedOut, stillLocked.Status);
        Assert.Equal(locked.LockedUntilUtc, stillLocked.LockedUntilUtc);

        time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            reopened.Authenticate("owner@gmail.com", "account1", "12345678").Status);
    }

    [Fact]
    public void UnknownUsername_DoesNotConsumeAnotherAccountsPersistentLockout()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 22, 4, 0, 0, TimeSpan.Zero));
        var store = new RemoteSecurityStore(path, time);
        store.RegisterAccount("owner@gmail.com", "account1", "12345678");

        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.Equal(
                RemoteCredentialAuthenticationStatus.InvalidCredentials,
                store.Authenticate("owner@gmail.com", "unknown1", "12345678").Status);
        }

        Assert.Equal(
            RemoteCredentialAuthenticationStatus.InvalidCredentials,
            store.Authenticate("owner@gmail.com", "account1", "00000000").Status);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(
                RemoteCredentialAuthenticationStatus.InvalidCredentials,
                store.Authenticate("owner@gmail.com", "account1", "00000000").Status);
        }

        var locked = store.Authenticate("owner@gmail.com", "account1", "00000000");
        Assert.Equal(RemoteCredentialAuthenticationStatus.LockedOut, locked.Status);
        Assert.Equal(time.GetUtcNow().AddMinutes(15), locked.LockedUntilUtc);
    }

    [Fact]
    public void CorruptFile_FailsClosedAndIsNotOverwritten()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var original = "not-a-valid-dpapi-file"u8.ToArray();
        File.WriteAllBytes(path, original);
        var store = new RemoteSecurityStore(path);

        Assert.False(store.IsAvailable);
        Assert.NotNull(store.AvailabilityError);
        Assert.Throws<InvalidOperationException>(() =>
            store.SaveSmtpCredential("sender@gmail.com", "abcdefghijklmnop"));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void OversizedFile_FailsClosedBeforeAttemptingDpapiDecryption()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        File.WriteAllBytes(path, new byte[(128 * 1024) + 1]);

        var store = new RemoteSecurityStore(path);

        Assert.False(store.IsAvailable);
        Assert.NotNull(store.AvailabilityError);
        Assert.Equal((128 * 1024) + 1, new FileInfo(path).Length);
    }

    [Fact]
    public void DeleteSmtpCredential_PersistsRemovalWithoutDeletingApprovedAccount()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.SaveSmtpCredential("sender@gmail.com", "abcdefghijklmnop");
        store.RegisterAccount("owner@gmail.com", "account1", "01234567");

        store.DeleteSmtpCredential();

        var reopened = new RemoteSecurityStore(path);
        Assert.Null(reopened.SmtpSenderGmail);
        Assert.Throws<InvalidOperationException>(reopened.GetSmtpCredential);
        Assert.Equal("account1", reopened.ApprovedAccount?.Username);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            reopened.Authenticate("owner@gmail.com", "account1", "01234567").Status);
    }

    [Fact]
    public void LocalAccount_PersistsWithoutGmail_AndAuthenticatesOnlyQuickTunnelSubject()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var permissions = RemoteWebPermission.StartServer | RemoteWebPermission.CreateBackup;
        var store = new RemoteSecurityStore(path);

        store.RegisterAccount(null, "account1", "01234567", permissions);

        var reopened = new RemoteSecurityStore(path);
        Assert.True(reopened.IsAvailable);
        Assert.Null(reopened.ApprovedAccount?.Gmail);
        Assert.Null(reopened.ApprovedAccount?.EmailVerifiedAtUtc);
        Assert.Equal(permissions, reopened.ApprovedAccount?.Permissions);
        Assert.False(reopened.HasCredentialForLogin("owner@gmail.com"));
        Assert.True(reopened.HasCredentialForLogin(RemoteControlOptions.QuickTunnelCredentialSubject));
        var authenticated = reopened.Authenticate(
            RemoteControlOptions.QuickTunnelCredentialSubject,
            "account1",
            "01234567");
        Assert.Equal(RemoteCredentialAuthenticationStatus.Success, authenticated.Status);
        Assert.Equal(permissions, authenticated.Permissions);
    }

    [Fact]
    public void GmailAndLocalAccounts_CannotCrossIngressIdentityBoundaries()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.RegisterAccount("owner@gmail.com", "gmail01", "12345678");
        store.RegisterAccount(null, "local001", "87654321");

        Assert.Equal(
            RemoteCredentialAuthenticationStatus.InvalidCredentials,
            store.Authenticate(
                RemoteControlOptions.QuickTunnelCredentialSubject,
                "gmail01",
                "12345678").Status);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.InvalidCredentials,
            store.Authenticate("owner@gmail.com", "local001", "87654321").Status);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            store.Authenticate("owner@gmail.com", "gmail01", "12345678").Status);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            store.Authenticate(
                RemoteControlOptions.QuickTunnelCredentialSubject,
                "local001",
                "87654321").Status);
    }

    [Fact]
    public void SchemaOneCredential_MigratesAtomicallyWithLegacyFullPermissions()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.RegisterAccount(
            "owner@gmail.com",
            "account1",
            "01234567",
            RemoteWebPermission.StartServer);
        RewriteAsLegacySchemaOne(path);
        var protectedLegacyVault = File.ReadAllBytes(path);

        var migrated = new RemoteSecurityStore(path);

        Assert.True(migrated.IsAvailable);
        Assert.Equal(RemoteWebPermission.All, migrated.ApprovedAccount?.Permissions);
        Assert.Equal(
            RemoteWebPermission.All,
            migrated.Authenticate("owner@gmail.com", "account1", "01234567").Permissions);
        var document = ReadProtectedJson(path);
        Assert.Equal(7, document["schemaVersion"]?.GetValue<int>());
        Assert.False(migrated.ApprovedAccount?.HasRecoverablePin);
        Assert.Null(migrated.GetRecoverablePin("account1"));
        Assert.Equal(
            protectedLegacyVault,
            File.ReadAllBytes(RemoteSecurityStore.GetLegacyMigrationBackupPath(path)));
    }

    [Fact]
    public void SchemaTwoCredential_MigratesWithoutChangingIdentityPermissionsOrPin()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var permissions = RemoteWebPermission.StartServer | RemoteWebPermission.CreateBackup;
        var store = new RemoteSecurityStore(path);
        store.RegisterAccount("owner@gmail.com", "account1", "01234567", permissions);
        RewriteAsLegacySchemaTwo(path);
        var protectedLegacyVault = File.ReadAllBytes(path);

        var migrated = new RemoteSecurityStore(path);

        Assert.True(migrated.IsAvailable);
        var account = Assert.Single(migrated.ApprovedAccounts);
        Assert.Equal("account1", account.Username);
        Assert.Equal("owner@gmail.com", account.Gmail);
        Assert.Equal(permissions, account.Permissions);
        var authenticated = migrated.Authenticate("owner@gmail.com", "account1", "01234567");
        Assert.Equal(RemoteCredentialAuthenticationStatus.Success, authenticated.Status);
        Assert.Equal(permissions, authenticated.Permissions);
        Assert.Equal(7, ReadProtectedJson(path)["schemaVersion"]?.GetValue<int>());
        Assert.False(account.HasRecoverablePin);
        Assert.Null(migrated.GetRecoverablePin("account1"));
        Assert.Equal(
            protectedLegacyVault,
            File.ReadAllBytes(RemoteSecurityStore.GetLegacyMigrationBackupPath(path)));
    }

    [Fact]
    public void SchemaThreeCredential_MigratesWithExactIdempotentBackupAndRequiresOneResetToReveal()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.RegisterAccount(null, "account1", "01234567", RemoteWebPermission.All);
        RewriteAsSchemaThree(path);
        var protectedSchemaThreeVault = File.ReadAllBytes(path);

        var migrated = new RemoteSecurityStore(path);

        Assert.True(migrated.IsAvailable);
        Assert.Equal(7, ReadProtectedJson(path)["schemaVersion"]?.GetValue<int>());
        Assert.False(Assert.Single(migrated.ApprovedAccounts).HasRecoverablePin);
        Assert.Null(migrated.GetRecoverablePin("account1"));
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            migrated.Authenticate(
                RemoteControlOptions.QuickTunnelCredentialSubject,
                "account1",
                "01234567").Status);
        var backupPath = RemoteSecurityStore.GetSchemaThreeMigrationBackupPath(path);
        Assert.Equal(protectedSchemaThreeVault, File.ReadAllBytes(backupPath));
        Assert.DoesNotContain(
            "01234567",
            Encoding.UTF8.GetString(File.ReadAllBytes(backupPath)),
            StringComparison.Ordinal);

        var backupTimestamp = File.GetLastWriteTimeUtc(backupPath);
        var reopened = new RemoteSecurityStore(path);
        Assert.True(reopened.IsAvailable);
        Assert.Equal(protectedSchemaThreeVault, File.ReadAllBytes(backupPath));
        Assert.Equal(backupTimestamp, File.GetLastWriteTimeUtc(backupPath));

        reopened.ResetAccountPin("account1", "87654321");
        Assert.True(Assert.Single(reopened.ApprovedAccounts).HasRecoverablePin);
        Assert.Equal("87654321", reopened.GetRecoverablePin("account1"));
    }

    [Fact]
    public void SchemaThreeMigrationBackupFailure_FailsClosedWithoutReplacingPrimaryVault()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.RegisterAccount(null, "account1", "01234567");
        RewriteAsSchemaThree(path);
        var protectedSchemaThreeVault = File.ReadAllBytes(path);
        Directory.CreateDirectory(RemoteSecurityStore.GetSchemaThreeMigrationBackupPath(path));

        var failed = new RemoteSecurityStore(path);

        Assert.False(failed.IsAvailable);
        Assert.Equal(protectedSchemaThreeVault, File.ReadAllBytes(path));
    }

    [Fact]
    public void SchemaFourCredential_MigratesWithExactIdempotentBackupAndPreservesRecoverablePin()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.RegisterAccount(
            null,
            "account1",
            "01234567",
            RemoteWebPermission.StartServer | RemoteWebPermission.CreateBackup);
        RewriteAsSchemaFour(path);
        var protectedSchemaFourVault = File.ReadAllBytes(path);

        var migrated = new RemoteSecurityStore(path);

        Assert.True(migrated.IsAvailable);
        Assert.Equal("01234567", migrated.GetRecoverablePin("account1"));
        Assert.Empty(migrated.GetRememberedDevices());
        var document = ReadProtectedJson(path);
        Assert.Equal(7, document["schemaVersion"]?.GetValue<int>());
        Assert.Equal(
            32,
            Convert.FromBase64String(
                document["deviceTokenKey"]?.GetValue<string>()
                ?? throw new InvalidDataException("Migrated vault has no device master key.")).Length);
        Assert.Empty(document["devices"]?.AsArray()
                     ?? throw new InvalidDataException("Migrated vault has no device list."));

        var backupPath = RemoteSecurityStore.GetSchemaFourMigrationBackupPath(path);
        Assert.Equal(protectedSchemaFourVault, File.ReadAllBytes(backupPath));
        var backupTimestamp = File.GetLastWriteTimeUtc(backupPath);

        var reopened = new RemoteSecurityStore(path);

        Assert.True(reopened.IsAvailable);
        Assert.Equal("01234567", reopened.GetRecoverablePin("account1"));
        Assert.Equal(protectedSchemaFourVault, File.ReadAllBytes(backupPath));
        Assert.Equal(backupTimestamp, File.GetLastWriteTimeUtc(backupPath));
    }

    [Fact]
    public void SchemaFourMigrationBackupFailure_FailsClosedWithoutReplacingPrimaryVault()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.RegisterAccount(null, "account1", "01234567");
        RewriteAsSchemaFour(path);
        var protectedSchemaFourVault = File.ReadAllBytes(path);
        Directory.CreateDirectory(RemoteSecurityStore.GetSchemaFourMigrationBackupPath(path));

        var failed = new RemoteSecurityStore(path);

        Assert.False(failed.IsAvailable);
        Assert.Equal(protectedSchemaFourVault, File.ReadAllBytes(path));
    }

    [Fact]
    public void SchemaFiveVault_MigratesWithExactBackupAndPreservesRememberedDevices()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.RegisterAccount(null, "account1", "01234567");
        var issued = store.IssueRememberedDevice(
            RemoteControlOptions.PublicTunnelCredentialSubject,
            "account1",
            "Owner iPhone");
        RewriteAsSchemaFive(path);
        var protectedSchemaFiveVault = File.ReadAllBytes(path);

        var migrated = new RemoteSecurityStore(path);

        Assert.True(migrated.IsAvailable);
        Assert.False(migrated.HasCloudflareNamedTunnelToken);
        Assert.Equal(7, ReadProtectedJson(path)["schemaVersion"]?.GetValue<int>());
        Assert.Equal(issued.Device.DeviceId, Assert.Single(migrated.GetRememberedDevices()).DeviceId);
        var backupPath = RemoteSecurityStore.GetSchemaFiveMigrationBackupPath(path);
        Assert.Equal(protectedSchemaFiveVault, File.ReadAllBytes(backupPath));
        var backupTimestamp = File.GetLastWriteTimeUtc(backupPath);

        var reopened = new RemoteSecurityStore(path);

        Assert.True(reopened.IsAvailable);
        Assert.Equal(protectedSchemaFiveVault, File.ReadAllBytes(backupPath));
        Assert.Equal(backupTimestamp, File.GetLastWriteTimeUtc(backupPath));
    }

    [Fact]
    public void SchemaSixVault_MigratesWithExactBackupAndPreservesNamedTokenAndDevices()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var token = "eyJ" + new string('S', 157) + "=";
        var store = new RemoteSecurityStore(path);
        store.RegisterAccount(null, "account1", "01234567");
        store.SaveCloudflareNamedTunnelToken(token);
        var issued = store.IssueRememberedDevice(
            RemoteControlOptions.PublicTunnelCredentialSubject,
            "account1",
            "Owner iPhone");
        RewriteAsSchemaSix(path);
        var protectedSchemaSixVault = File.ReadAllBytes(path);

        var migrated = new RemoteSecurityStore(path);

        Assert.True(migrated.IsAvailable);
        Assert.True(migrated.HasCloudflareNamedTunnelToken);
        Assert.Equal(token, migrated.GetCloudflareNamedTunnelCredential().Token);
        Assert.False(migrated.HasCloudflaredInstallationReceipt);
        Assert.Throws<InvalidOperationException>(migrated.GetCloudflaredInstallationReceipt);
        Assert.Equal(7, ReadProtectedJson(path)["schemaVersion"]?.GetValue<int>());
        Assert.Equal(issued.Device.DeviceId, Assert.Single(migrated.GetRememberedDevices()).DeviceId);
        var backupPath = RemoteSecurityStore.GetSchemaSixMigrationBackupPath(path);
        Assert.Equal(protectedSchemaSixVault, File.ReadAllBytes(backupPath));
        var backupTimestamp = File.GetLastWriteTimeUtc(backupPath);

        var reopened = new RemoteSecurityStore(path);

        Assert.True(reopened.IsAvailable);
        Assert.Equal(token, reopened.GetCloudflareNamedTunnelCredential().Token);
        Assert.Equal(protectedSchemaSixVault, File.ReadAllBytes(backupPath));
        Assert.Equal(backupTimestamp, File.GetLastWriteTimeUtc(backupPath));
    }

    [Fact]
    public void SchemaSixMigrationBackupFailure_FailsClosedWithoutReplacingPrimaryVault()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.SaveCloudflareNamedTunnelToken("eyJ" + new string('S', 157) + "=");
        RewriteAsSchemaSix(path);
        var protectedSchemaSixVault = File.ReadAllBytes(path);
        Directory.CreateDirectory(RemoteSecurityStore.GetSchemaSixMigrationBackupPath(path));

        var failed = new RemoteSecurityStore(path);

        Assert.False(failed.IsAvailable);
        Assert.Equal(protectedSchemaSixVault, File.ReadAllBytes(path));
    }

    [Fact]
    public void CloudflaredReceipt_IsDpapiProtectedReloadableReplaceableAndDeletable()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var first = CloudflaredInstallationReceipt.Create(
            new CloudflaredBootstrapResult(
                @"C:\MCSV\tools\cloudflared\cloudflared.exe",
                "2026.8.1",
                12_345,
                new string('a', 64)),
            new DateTimeOffset(2026, 8, 24, 15, 30, 0, TimeSpan.FromHours(8)));
        var second = CloudflaredInstallationReceipt.Create(
            new CloudflaredBootstrapResult(
                @"C:\MCSV\tools\cloudflared\cloudflared.exe",
                "2026.8.2",
                54_321,
                new string('B', 64)),
            new DateTimeOffset(2026, 8, 25, 1, 2, 3, TimeSpan.Zero));
        var store = new RemoteSecurityStore(path);

        store.SaveCloudflaredInstallationReceipt(first);

        Assert.True(store.HasCloudflaredInstallationReceipt);
        Assert.Equal(first, store.GetCloudflaredInstallationReceipt());
        Assert.Equal(TimeSpan.Zero, first.InstalledAtUtc.Offset);
        var protectedBytes = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.DoesNotContain(first.ReleaseTag, protectedBytes, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Sha256, protectedBytes, StringComparison.Ordinal);

        var reopened = new RemoteSecurityStore(path);
        Assert.Equal(first, reopened.GetCloudflaredInstallationReceipt());
        reopened.SaveCloudflaredInstallationReceipt(second);
        Assert.Equal(second, new RemoteSecurityStore(path).GetCloudflaredInstallationReceipt());
        reopened.DeleteCloudflaredInstallationReceipt();

        var deleted = new RemoteSecurityStore(path);
        Assert.False(deleted.HasCloudflaredInstallationReceipt);
        Assert.Throws<InvalidOperationException>(deleted.GetCloudflaredInstallationReceipt);
    }

    [Fact]
    public void InvalidCloudflaredReceipt_FailsClosedWithoutOverwritingProtectedVault()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.SaveCloudflaredInstallationReceipt(
            CloudflaredInstallationReceipt.Create(
                new CloudflaredBootstrapResult(
                    @"C:\MCSV\tools\cloudflared\cloudflared.exe",
                    "2026.8.1",
                    12_345,
                    new string('a', 64)),
                new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero)));
        var document = ReadProtectedJson(path);
        document["cloudflaredInstallationReceipt"]!["assetIdentity"] =
            "attacker.example/cloudflared.exe";
        WriteProtectedJson(path, document);
        var corruptProtectedVault = File.ReadAllBytes(path);

        var failed = new RemoteSecurityStore(path);

        Assert.False(failed.IsAvailable);
        Assert.False(failed.HasCloudflaredInstallationReceipt);
        Assert.Equal(corruptProtectedVault, File.ReadAllBytes(path));
    }

    [Fact]
    public void NamedTunnelToken_IsDpapiProtectedReloadableReplaceableAndDeletable()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var firstToken = "eyJ" + new string('A', 157) + "=";
        var secondToken = "eyJ" + new string('B', 189) + "=";
        var store = new RemoteSecurityStore(path);

        store.SaveCloudflareNamedTunnelToken($"  {firstToken}\r\n");

        Assert.True(store.HasCloudflareNamedTunnelToken);
        Assert.Equal(firstToken, store.GetCloudflareNamedTunnelCredential().Token);
        Assert.Equal("[REDACTED]", store.GetCloudflareNamedTunnelCredential().ToString());
        Assert.DoesNotContain(
            firstToken,
            Encoding.UTF8.GetString(File.ReadAllBytes(path)),
            StringComparison.Ordinal);

        var reopened = new RemoteSecurityStore(path);
        Assert.True(reopened.IsAvailable);
        Assert.True(reopened.HasCloudflareNamedTunnelToken);
        Assert.Equal(firstToken, reopened.GetCloudflareNamedTunnelCredential().Token);

        reopened.SaveCloudflareNamedTunnelToken(secondToken);
        Assert.Equal(secondToken, new RemoteSecurityStore(path).GetCloudflareNamedTunnelCredential().Token);
        reopened.DeleteCloudflareNamedTunnelToken();

        var deleted = new RemoteSecurityStore(path);
        Assert.False(deleted.HasCloudflareNamedTunnelToken);
        Assert.Throws<InvalidOperationException>(deleted.GetCloudflareNamedTunnelCredential);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short-token")]
    [InlineData("eyJ token with whitespace AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void NamedTunnelToken_RejectsMalformedValues(string token)
    {
        using var directory = new TemporaryDirectory();
        var store = new RemoteSecurityStore(Path.Combine(directory.Path, "remote-security.dat"));

        Assert.Throws<InvalidOperationException>(() =>
            store.SaveCloudflareNamedTunnelToken(token));
        Assert.False(store.HasCloudflareNamedTunnelToken);
    }

    [Fact]
    public void RememberedDevice_PersistsWithoutRawTokenAndRefreshReturnsLatestPermissions()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 24, 2, 0, 0, TimeSpan.Zero));
        var store = new RemoteSecurityStore(path, time);
        store.RegisterAccount(
            null,
            "account1",
            "01234567",
            RemoteWebPermission.StartServer);

        var issued = store.IssueRememberedDevice(
            RemoteControlOptions.QuickTunnelCredentialSubject,
            "account1",
            "Owner iPhone");

        var tokenParts = issued.Token.Split('.', StringSplitOptions.None);
        Assert.Equal(4, tokenParts.Length);
        Assert.Equal("mrd1", tokenParts[0]);
        Assert.Equal(32, tokenParts[1].Length);
        Assert.True(Guid.TryParseExact(tokenParts[1], "N", out var tokenDeviceId));
        Assert.Equal(issued.Device.DeviceId, tokenDeviceId);
        Assert.Equal("0", tokenParts[2]);
        Assert.Equal(43, tokenParts[3].Length);

        var decryptedVault = ReadProtectedJson(path).ToJsonString();
        Assert.DoesNotContain(issued.Token, decryptedVault, StringComparison.Ordinal);
        Assert.DoesNotContain(tokenParts[3], decryptedVault, StringComparison.Ordinal);
        Assert.DoesNotContain(
            issued.Token,
            Encoding.UTF8.GetString(File.ReadAllBytes(path)),
            StringComparison.Ordinal);

        var reopened = new RemoteSecurityStore(path, time);
        var persisted = Assert.Single(reopened.GetRememberedDevices());
        Assert.Equal(issued.Device.DeviceId, persisted.DeviceId);
        Assert.Equal("Owner iPhone", persisted.Label);
        reopened.UpdateAccountPermissions("account1", RemoteWebPermission.CreateBackup);

        var refreshed = reopened.RefreshRememberedDevice(
            RemoteControlOptions.QuickTunnelCredentialSubject,
            issued.Token,
            Guid.NewGuid());

        Assert.Equal(RemoteRememberedDeviceRefreshStatus.Success, refreshed.Status);
        Assert.Equal("account1", refreshed.Username);
        Assert.Equal(RemoteWebPermission.CreateBackup, refreshed.Permissions);
        Assert.NotNull(refreshed.ReplacementToken);
        Assert.NotEqual(issued.Token, refreshed.ReplacementToken);
        Assert.Equal("1", refreshed.ReplacementToken!.Split('.')[2]);

        var reloadedAgain = new RemoteSecurityStore(path, time);
        Assert.Equal(
            RemoteRememberedDeviceRefreshStatus.Success,
            reloadedAgain.RefreshRememberedDevice(
                RemoteControlOptions.QuickTunnelCredentialSubject,
                refreshed.ReplacementToken,
                Guid.NewGuid()).Status);
    }

    [Fact]
    public void RememberedDevice_RotationIsIdempotentAndReplayRevokesButInvalidSecretDoesNot()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 24, 2, 0, 0, TimeSpan.Zero));
        var store = new RemoteSecurityStore(path, time);
        store.RegisterAccount(null, "account1", "01234567");
        var login = RemoteControlOptions.QuickTunnelCredentialSubject;
        var first = store.IssueRememberedDevice(login, "account1", "First iPhone");
        var requestId = Guid.NewGuid();

        var rotated = store.RefreshRememberedDevice(login, first.Token, requestId);
        Assert.Equal(RemoteRememberedDeviceRefreshStatus.Success, rotated.Status);
        Assert.NotNull(rotated.ReplacementToken);

        time.Advance(TimeSpan.FromDays(10));
        store = new RemoteSecurityStore(path, time);
        var retried = store.RefreshRememberedDevice(login, first.Token, requestId);
        Assert.Equal(RemoteRememberedDeviceRefreshStatus.Success, retried.Status);
        Assert.Equal(rotated.ReplacementToken, retried.ReplacementToken);

        var forgedCurrent = ReplaceTokenSecret(rotated.ReplacementToken!);
        Assert.False(store.RevokeRememberedDevice(login, forgedCurrent));
        Assert.Equal(
            RemoteRememberedDeviceRefreshStatus.Invalid,
            store.RefreshRememberedDevice(login, forgedCurrent, Guid.NewGuid()).Status);
        var afterForgery = store.RefreshRememberedDevice(
            login,
            rotated.ReplacementToken!,
            Guid.NewGuid());
        Assert.Equal(RemoteRememberedDeviceRefreshStatus.Success, afterForgery.Status);

        var second = store.IssueRememberedDevice(login, "account1", "Second iPhone");
        var secondRotated = store.RefreshRememberedDevice(login, second.Token, Guid.NewGuid());
        Assert.Equal(RemoteRememberedDeviceRefreshStatus.Success, secondRotated.Status);

        Assert.Equal(
            RemoteRememberedDeviceRefreshStatus.ReplayDetected,
            store.RefreshRememberedDevice(login, second.Token, Guid.NewGuid()).Status);
        Assert.Equal(
            RemoteRememberedDeviceRefreshStatus.Revoked,
            store.RefreshRememberedDevice(
                login,
                secondRotated.ReplacementToken!,
                Guid.NewGuid()).Status);
    }

    [Fact]
    public void RememberedDevice_ExpiresAtIdleAndAbsoluteBoundaries()
    {
        using var directory = new TemporaryDirectory();
        var start = new DateTimeOffset(2026, 8, 24, 2, 0, 0, TimeSpan.Zero);
        var login = RemoteControlOptions.QuickTunnelCredentialSubject;

        var idlePath = Path.Combine(directory.Path, "idle.dat");
        var idleTime = new MutableTimeProvider(start);
        var idleStore = new RemoteSecurityStore(idlePath, idleTime);
        idleStore.RegisterAccount(null, "account1", "01234567");
        var idleDevice = idleStore.IssueRememberedDevice(login, "account1", "Idle iPhone");
        idleTime.Advance(TimeSpan.FromDays(90));

        Assert.Equal(
            RemoteRememberedDeviceRefreshStatus.Expired,
            idleStore.RefreshRememberedDevice(login, idleDevice.Token, Guid.NewGuid()).Status);

        var absolutePath = Path.Combine(directory.Path, "absolute.dat");
        var absoluteTime = new MutableTimeProvider(start);
        var absoluteStore = new RemoteSecurityStore(absolutePath, absoluteTime);
        absoluteStore.RegisterAccount(null, "account1", "01234567");
        var absoluteDevice = absoluteStore.IssueRememberedDevice(
            login,
            "account1",
            "Long-lived iPhone");
        var currentToken = absoluteDevice.Token;
        for (var refresh = 0; refresh < 4; refresh++)
        {
            absoluteTime.Advance(TimeSpan.FromDays(89));
            var result = absoluteStore.RefreshRememberedDevice(
                login,
                currentToken,
                Guid.NewGuid());
            Assert.Equal(RemoteRememberedDeviceRefreshStatus.Success, result.Status);
            currentToken = result.ReplacementToken!;
        }

        absoluteTime.Advance(TimeSpan.FromDays(9));
        Assert.Equal(
            RemoteRememberedDeviceRefreshStatus.Expired,
            absoluteStore.RefreshRememberedDevice(login, currentToken, Guid.NewGuid()).Status);
    }

    [Fact]
    public void RememberedDevice_AccountSecurityAndDesktopRevocationAreScoped()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        var login = RemoteControlOptions.QuickTunnelCredentialSubject;
        store.RegisterAccount(null, "account1", "11111111");
        store.RegisterAccount(null, "account2", "22222222");
        var first = store.IssueRememberedDevice(login, "account1", "First iPhone");
        var second = store.IssueRememberedDevice(login, "account2", "Second iPhone");

        store.ResetAccountPin("account1", "33333333");

        Assert.Equal(
            RemoteRememberedDeviceRefreshStatus.Revoked,
            store.RefreshRememberedDevice(login, first.Token, Guid.NewGuid()).Status);
        var secondRotated = store.RefreshRememberedDevice(login, second.Token, Guid.NewGuid());
        Assert.Equal(RemoteRememberedDeviceRefreshStatus.Success, secondRotated.Status);

        Assert.False(store.RevokeRememberedDevice("owner@gmail.com", secondRotated.ReplacementToken!));
        Assert.False(store.RevokeRememberedDevice(login, ReplaceTokenSecret(secondRotated.ReplacementToken!)));
        Assert.True(store.RevokeRememberedDevice(login, secondRotated.ReplacementToken!));
        Assert.Equal(
            RemoteRememberedDeviceRefreshStatus.Revoked,
            store.RefreshRememberedDevice(
                login,
                secondRotated.ReplacementToken!,
                Guid.NewGuid()).Status);

        var desktopOne = store.IssueRememberedDevice(login, "account1", "Desktop one");
        var desktopTwo = store.IssueRememberedDevice(login, "account1", "Desktop two");
        Assert.True(store.RevokeRememberedDevice(desktopOne.Device.DeviceId));
        Assert.False(store.RevokeRememberedDevice(desktopOne.Device.DeviceId));
        Assert.Equal(1, store.RevokeRememberedDevicesForAccount("account1"));
        Assert.Equal(
            RemoteRememberedDeviceStatus.Revoked,
            store.GetRememberedDevices().Single(device =>
                device.DeviceId == desktopTwo.Device.DeviceId).Status);

        var deleteMe = store.IssueRememberedDevice(login, "account2", "Delete me");
        store.DeleteAccount("account2");
        Assert.DoesNotContain(
            store.GetRememberedDevices(),
            device => string.Equals(device.Username, "account2", StringComparison.Ordinal));
        Assert.Equal(
            RemoteRememberedDeviceRefreshStatus.Invalid,
            store.RefreshRememberedDevice(login, deleteMe.Token, Guid.NewGuid()).Status);

        var allOne = store.IssueRememberedDevice(login, "account1", "All one");
        var allTwo = store.IssueRememberedDevice(login, "account1", "All two");
        Assert.Equal(2, store.RevokeAllRememberedDevices());
        Assert.All(
            store.GetRememberedDevices().Where(device =>
                device.DeviceId == allOne.Device.DeviceId ||
                device.DeviceId == allTwo.Device.DeviceId),
            device => Assert.Equal(RemoteRememberedDeviceStatus.Revoked, device.Status));
    }

    [Fact]
    public void RememberedDevice_PerAccountLimitIsEight()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        var login = RemoteControlOptions.QuickTunnelCredentialSubject;
        store.RegisterAccount(null, "account1", "01234567");

        for (var index = 0; index < 8; index++)
        {
            store.IssueRememberedDevice(login, "account1", $"iPhone {index + 1}");
        }

        Assert.Throws<InvalidOperationException>(() =>
            store.IssueRememberedDevice(login, "account1", "iPhone 9"));
        Assert.Equal(8, store.GetRememberedDevices().Count);
    }

    [Fact]
    public void EphemeralRememberedDevice_RotatesAndSafelyRevokes()
    {
        var store = new EphemeralRemoteSecurityStore();
        var login = RemoteControlOptions.QuickTunnelCredentialSubject;
        store.RegisterAccount(null, "account1", "01234567", RemoteWebPermission.StartServer);
        var issued = store.IssueRememberedDevice(login, "account1", "Test iPhone");
        store.UpdateAccountPermissions("account1", RemoteWebPermission.CreateBackup);

        var refreshed = store.RefreshRememberedDevice(login, issued.Token, Guid.NewGuid());

        Assert.Equal(RemoteRememberedDeviceRefreshStatus.Success, refreshed.Status);
        Assert.Equal(RemoteWebPermission.CreateBackup, refreshed.Permissions);
        Assert.False(store.RevokeRememberedDevice(login, ReplaceTokenSecret(refreshed.ReplacementToken!)));
        Assert.True(store.RevokeRememberedDevice(login, refreshed.ReplacementToken!));
        Assert.Equal(
            RemoteRememberedDeviceRefreshStatus.Revoked,
            store.RefreshRememberedDevice(login, refreshed.ReplacementToken!, Guid.NewGuid()).Status);
    }

    [Fact]
    public void RecoverablePin_SurvivesPermissionAndAuthenticationWritesWithoutPlaintextLeak()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.RegisterAccount(null, "account1", "97531864", RemoteWebPermission.StartServer);

        store.UpdateAccountPermissions("account1", RemoteWebPermission.CreateBackup);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.InvalidCredentials,
            store.Authenticate(
                RemoteControlOptions.QuickTunnelCredentialSubject,
                "account1",
                "00000000").Status);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            store.Authenticate(
                RemoteControlOptions.QuickTunnelCredentialSubject,
                "account1",
                "97531864").Status);

        var reopened = new RemoteSecurityStore(path);
        Assert.Equal("97531864", reopened.GetRecoverablePin("account1"));
        Assert.Equal(RemoteWebPermission.CreateBackup, reopened.ApprovedAccount?.Permissions);
        Assert.DoesNotContain(
            "97531864",
            ReadProtectedJson(path).ToJsonString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "97531864",
            Encoding.UTF8.GetString(File.ReadAllBytes(path)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecoverablePinCiphertext_IsBoundToUsernameAndCannotBeSwappedAcrossAccounts()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.RegisterAccount(null, "account1", "11111111");
        store.RegisterAccount(null, "account2", "22222222");
        var document = ReadProtectedJson(path);
        var credentials = document["credentials"]!.AsArray();
        var first = credentials[0]!.AsObject();
        var second = credentials[1]!.AsObject();
        (first["recoverablePinCiphertext"], second["recoverablePinCiphertext"]) =
            (second["recoverablePinCiphertext"]!.DeepClone(), first["recoverablePinCiphertext"]!.DeepClone());
        WriteProtectedJson(path, document);

        var failed = new RemoteSecurityStore(path);

        Assert.False(failed.IsAvailable);
        Assert.Empty(failed.ApprovedAccounts);
    }

    [Fact]
    public void LegacyMigrationBackupFailure_FailsClosedWithoutReplacingPrimaryVault()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.RegisterAccount("owner@gmail.com", "account1", "01234567");
        RewriteAsLegacySchemaTwo(path);
        var protectedLegacyVault = File.ReadAllBytes(path);
        Directory.CreateDirectory(RemoteSecurityStore.GetLegacyMigrationBackupPath(path));

        var failed = new RemoteSecurityStore(path);

        Assert.False(failed.IsAvailable);
        Assert.Equal(protectedLegacyVault, File.ReadAllBytes(path));
    }

    [Fact]
    public void MultipleAccounts_AreUniqueAndCanBeManagedIndependently()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        store.RegisterAccount(null, "account1", "11111111", RemoteWebPermission.StartServer);
        store.RegisterAccount(null, "account2", "22222222", RemoteWebPermission.StopServer);

        Assert.Equal(2, store.ApprovedAccounts.Count);
        Assert.Throws<InvalidOperationException>(() =>
            store.RegisterAccount(null, "ACCOUNT1", "33333333"));
        store.UpdateAccountPermissions("account2", RemoteWebPermission.CreateBackup);
        store.ResetAccountPin("account1", "44444444");
        store.DeleteAccount("account2");

        var reopened = new RemoteSecurityStore(path);
        var remaining = Assert.Single(reopened.ApprovedAccounts);
        Assert.Equal("account1", remaining.Username);
        Assert.True(remaining.HasRecoverablePin);
        Assert.Equal("44444444", reopened.GetRecoverablePin("account1"));
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.InvalidCredentials,
            reopened.Authenticate(RemoteControlOptions.QuickTunnelCredentialSubject, "account1", "11111111").Status);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            reopened.Authenticate(RemoteControlOptions.QuickTunnelCredentialSubject, "account1", "44444444").Status);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.InvalidCredentials,
            reopened.Authenticate(RemoteControlOptions.QuickTunnelCredentialSubject, "account2", "22222222").Status);
    }

    [Fact]
    public void MaximumAccountSet_KeepsEachRecoverablePinBoundToItsOwnUsername()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-security.dat");
        var store = new RemoteSecurityStore(path);
        var expected = Enumerable.Range(0, 32)
            .ToDictionary(
                index => $"user{index:00}",
                index => (10_000_000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparer.Ordinal);

        foreach (var account in expected)
        {
            store.RegisterAccount(null, account.Key, account.Value);
        }

        var reopened = new RemoteSecurityStore(path);
        Assert.Equal(32, reopened.ApprovedAccounts.Count);
        foreach (var account in expected)
        {
            Assert.Equal(account.Value, reopened.GetRecoverablePin(account.Key));
        }

        Assert.Throws<InvalidOperationException>(() =>
            reopened.RegisterAccount(null, "user999", "99999999"));
    }

    [Theory]
    [InlineData("abcd efgh ijkl mnop", "abcdefghijklmnop")]
    [InlineData("ABCDEFGHIJKLMNOP", "abcdefghijklmnop")]
    public void GoogleAppPassword_NormalizesOnlyExpectedShape(string input, string expected)
    {
        Assert.True(RemoteSecurityStore.TryNormalizeGoogleAppPassword(input, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.False(RemoteSecurityStore.TryNormalizeGoogleAppPassword("ordinary-password", out _));
        Assert.False(RemoteSecurityStore.TryNormalizeGoogleAppPassword("1234567890123456", out _));
    }

    private static void RewriteAsLegacySchemaOne(string path)
    {
        var document = ReadProtectedJson(path);
        RemoveSchemaFiveFields(document);
        MoveFirstCurrentCredentialToLegacySlot(document);
        document["schemaVersion"] = 1;
        document["credential"]?.AsObject().Remove("permissions");
        document["credential"]?.AsObject().Remove("recoverablePinCiphertext");
        WriteProtectedJson(path, document);
    }

    private static void RewriteAsLegacySchemaTwo(string path)
    {
        var document = ReadProtectedJson(path);
        RemoveSchemaFiveFields(document);
        MoveFirstCurrentCredentialToLegacySlot(document);
        document["schemaVersion"] = 2;
        document["credential"]?.AsObject().Remove("recoverablePinCiphertext");
        WriteProtectedJson(path, document);
    }

    private static void RewriteAsSchemaThree(string path)
    {
        var document = ReadProtectedJson(path);
        RemoveSchemaFiveFields(document);
        document["schemaVersion"] = 3;
        foreach (var credential in document["credentials"]!.AsArray())
        {
            credential!.AsObject().Remove("recoverablePinCiphertext");
        }

        WriteProtectedJson(path, document);
    }

    private static void RewriteAsSchemaFour(string path)
    {
        var document = ReadProtectedJson(path);
        RemoveSchemaFiveFields(document);
        document["schemaVersion"] = 4;
        WriteProtectedJson(path, document);
    }

    private static void RewriteAsSchemaFive(string path)
    {
        var document = ReadProtectedJson(path);
        document.Remove("cloudflareNamedTunnelToken");
        document.Remove("cloudflaredInstallationReceipt");
        document["schemaVersion"] = 5;
        WriteProtectedJson(path, document);
    }

    private static void RewriteAsSchemaSix(string path)
    {
        var document = ReadProtectedJson(path);
        document.Remove("cloudflaredInstallationReceipt");
        document["schemaVersion"] = 6;
        WriteProtectedJson(path, document);
    }

    private static void RemoveSchemaFiveFields(JsonObject document)
    {
        document.Remove("deviceTokenKey");
        document.Remove("devices");
        document.Remove("cloudflareNamedTunnelToken");
        document.Remove("cloudflaredInstallationReceipt");
    }

    private static void MoveFirstCurrentCredentialToLegacySlot(JsonObject document)
    {
        var credentials = document["credentials"]?.AsArray()
            ?? throw new InvalidDataException("Test schema-3 vault has no credentials array.");
        document["credential"] = credentials.FirstOrDefault()?.DeepClone();
        document.Remove("credentials");
    }

    private static string ReplaceTokenSecret(string token)
    {
        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 4)
        {
            throw new InvalidDataException("Test remembered-device token is malformed.");
        }

        byte[] replacement;
        string encoded;
        do
        {
            replacement = RandomNumberGenerator.GetBytes(32);
            try
            {
                encoded = Convert.ToBase64String(replacement)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
            }
            finally
            {
                CryptographicOperations.ZeroMemory(replacement);
            }
        }
        while (string.Equals(encoded, parts[3], StringComparison.Ordinal));

        parts[3] = encoded;
        return string.Join('.', parts);
    }

    private static JsonObject ReadProtectedJson(string path)
    {
        var header = "MCSV-REMOTE-SECURITY-1\n"u8.ToArray();
        var file = File.ReadAllBytes(path);
        var plaintext = ProtectedData.Unprotect(
            file.AsSpan(header.Length).ToArray(),
            SHA256.HashData("Muhun MCSV Manager remote security v1"u8),
            DataProtectionScope.CurrentUser);
        try
        {
            return JsonNode.Parse(plaintext)?.AsObject()
                   ?? throw new InvalidDataException("Test vault JSON is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void WriteProtectedJson(string path, JsonObject document)
    {
        var header = "MCSV-REMOTE-SECURITY-1\n"u8.ToArray();
        var plaintext = Encoding.UTF8.GetBytes(document.ToJsonString());
        var encrypted = ProtectedData.Protect(
            plaintext,
            SHA256.HashData("Muhun MCSV Manager remote security v1"u8),
            DataProtectionScope.CurrentUser);
        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.Write(header);
            stream.Write(encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mcsv-remote-security-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
