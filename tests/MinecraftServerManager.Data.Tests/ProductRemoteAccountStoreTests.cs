using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Security;
using MinecraftServerManager.Data;

namespace MinecraftServerManager.Data.Tests;

public sealed class ProductRemoteAccountStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "muhun-mcsv-remote-account-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AccountLifecycle_PersistsScopedGrantsAndRecoverablePinWithoutExposingItInMetadata()
    {
        var fixture = await CreateFixtureAsync();
        var serverId = Guid.NewGuid();
        var grants = new[]
        {
            new ProductPermissionGrant(
                ProductPermissionCodes.ServerRead,
                ProductPermissionScope.ForServer(serverId)),
            new ProductPermissionGrant(
                ProductPermissionCodes.ServerStart,
                ProductPermissionScope.ForServer(serverId)),
        };

        var created = await fixture.Store.CreateAsync(
            "Admin123",
            "mcsv-local-approved-account",
            "owner@gmail.com",
            "482913",
            grants);

        Assert.Equal("admin123", created.Username);
        Assert.Equal("owner@gmail.com", created.Email);
        Assert.Equal(ProductRemoteAccountRole.Owner, created.Role);
        Assert.All(grants, grant => Assert.Contains(grant, created.Grants));
        AssertOwnerManagementGrants(created.Grants);
        Assert.DoesNotContain("482913", created.ToString(), StringComparison.Ordinal);
        Assert.Equal("482913", await fixture.Store.RevealPinAsync("ADMIN123"));
        Assert.True(fixture.Store.HasEnabledAccountForSubject("MCSV-LOCAL-APPROVED-ACCOUNT"));

        var authenticated = fixture.Store.Authenticate(
            "mcsv-local-approved-account",
            "Admin123",
            "482913");
        Assert.Equal(ProductRemoteAuthenticationStatus.Success, authenticated.Status);
        Assert.Equal(created.SecurityStamp, authenticated.Account?.SecurityStamp);
        Assert.Single(fixture.Store.List());
        Assert.DoesNotContain("482913", File.ReadAllText(fixture.Database.DatabasePath));
    }

    [Fact]
    public async Task FiveFailures_LockAccountAndSuccessfulLoginWorksAfterBoundedLockout()
    {
        var fixture = await CreateFixtureAsync();
        await fixture.Store.CreateAsync(
            "manager1",
            "mcsv-local-approved-account",
            null,
            "12345678",
            []);

        for (var attempt = 1; attempt < ProductRemoteAccountStore.MaximumFailedAttempts; attempt++)
        {
            Assert.Equal(
                ProductRemoteAuthenticationStatus.InvalidCredentials,
                fixture.Store.Authenticate(
                    "mcsv-local-approved-account",
                    "manager1",
                    "00000000").Status);
        }

        var locked = fixture.Store.Authenticate(
            "mcsv-local-approved-account",
            "manager1",
            "00000000");
        Assert.Equal(ProductRemoteAuthenticationStatus.LockedOut, locked.Status);
        Assert.NotNull(locked.LockedUntilUtc);
        Assert.Equal(
            ProductRemoteAuthenticationStatus.LockedOut,
            fixture.Store.Authenticate(
                "mcsv-local-approved-account",
                "manager1",
                "12345678").Status);

        fixture.Time.Advance(ProductRemoteAccountStore.LockoutDuration + TimeSpan.FromSeconds(1));
        Assert.Equal(
            ProductRemoteAuthenticationStatus.Success,
            fixture.Store.Authenticate(
                "mcsv-local-approved-account",
                "manager1",
                "12345678").Status);
    }

    [Fact]
    public async Task PinAndAuthorizationChanges_RotateSecurityStampAndRevokeOldCredential()
    {
        var fixture = await CreateFixtureAsync();
        var created = await fixture.Store.CreateAsync(
            "operator7",
            "mcsv-local-approved-account",
            null,
            "456789",
            []);

        var changedAuthorization = await fixture.Store.UpdateAuthorizationAsync(
            "operator7",
            enabled: true,
            created.Grants);
        Assert.NotEqual(created.SecurityStamp, changedAuthorization.SecurityStamp);

        var changedPin = await fixture.Store.UpdatePinAsync("operator7", "987654");
        Assert.NotEqual(changedAuthorization.SecurityStamp, changedPin.SecurityStamp);
        Assert.Equal("987654", await fixture.Store.RevealPinAsync("operator7"));
        Assert.Equal(
            ProductRemoteAuthenticationStatus.InvalidCredentials,
            fixture.Store.Authenticate(
                "mcsv-local-approved-account",
                "operator7",
                "456789").Status);
        Assert.Equal(
            ProductRemoteAuthenticationStatus.Success,
            fixture.Store.Authenticate(
                "mcsv-local-approved-account",
                "operator7",
                "987654").Status);

        await fixture.Store.CreateAsync(
            "recovery8",
            "mcsv-local-approved-account",
            null,
            "246810",
            [],
            role: ProductRemoteAccountRole.Owner);
        await fixture.Store.UpdateAuthorizationAsync(
            "operator7",
            enabled: false,
            created.Grants);
        Assert.True(fixture.Store.HasEnabledAccountForSubject("mcsv-local-approved-account"));
        Assert.Equal(
            ProductRemoteAuthenticationStatus.InvalidCredentials,
            fixture.Store.Authenticate(
                "mcsv-local-approved-account",
                "operator7",
                "987654").Status);
    }

    [Fact]
    public async Task DuplicateCreate_CleansUnreferencedVaultSecretAndDeleteRemovesCommittedSecret()
    {
        var fixture = await CreateFixtureAsync();
        var owner = await fixture.Store.CreateAsync(
            "partner8",
            "mcsv-local-approved-account",
            null,
            "1234",
            []);
        Assert.Single(fixture.Vault.Secrets);

        await fixture.Store.CreateAsync(
            "viewer9",
            "mcsv-local-approved-account",
            null,
            "9012",
            [],
            role: ProductRemoteAccountRole.Viewer);

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Store.CreateAsync(
            "partner8",
            "mcsv-local-approved-account",
            null,
            "5678",
            []));
        Assert.Equal(2, fixture.Vault.Secrets.Count);

        await fixture.Store.DeleteAsync("viewer9");
        var remaining = fixture.Store.List().Single();
        Assert.Equal(owner.Username, remaining.Username);
        Assert.Equal(ProductRemoteAccountRole.Owner, remaining.Role);
        Assert.Single(fixture.Vault.Secrets);
    }

    [Fact]
    public async Task LastEnabledOwner_CannotBeDeletedDisabledDowngradedOrLoseManagementGrants()
    {
        var fixture = await CreateFixtureAsync();
        var owner = await fixture.Store.CreateAsync(
            "primary1",
            "mcsv-local-approved-account",
            null,
            "123456",
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.DeleteAsync("primary1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.UpdateAuthorizationAsync(
            "primary1",
            enabled: false,
            owner.Grants));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.UpdateAuthorizationAsync(
            "primary1",
            enabled: true,
            owner.Grants,
            role: ProductRemoteAccountRole.Admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.UpdateAuthorizationAsync(
            "primary1",
            enabled: true,
            owner.Grants.Where(grant => grant.PermissionCode != ProductPermissionCodes.PermissionManage).ToArray()));

        var preserved = fixture.Store.List().Single();
        Assert.True(preserved.Enabled);
        Assert.Equal(ProductRemoteAccountRole.Owner, preserved.Role);
        AssertOwnerManagementGrants(preserved.Grants);
    }

    [Fact]
    public async Task ConcurrentMutations_CannotRemoveBothEnabledOwners()
    {
        var fixture = await CreateFixtureAsync();
        var first = await fixture.Store.CreateAsync(
            "primary1", "mcsv-local-approved-account", null, "123456", []);
        var second = await fixture.Store.CreateAsync(
            "primary2", "mcsv-local-approved-account", null, "654321", [],
            role: ProductRemoteAccountRole.Owner);

        static async Task<bool> TryDisableAsync(
            ProductRemoteAccountStore store,
            ProductRemoteAccountInfo account)
        {
            try
            {
                await store.UpdateAuthorizationAsync(
                    account.Username,
                    enabled: false,
                    account.Grants);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(
            Task.Run(() => TryDisableAsync(fixture.Store, first)),
            Task.Run(() => TryDisableAsync(fixture.Store, second)));

        Assert.Single(results, result => result);
        Assert.Single(fixture.Store.List(), account =>
            account.Enabled && account.Role == ProductRemoteAccountRole.Owner);
    }

    private static void AssertOwnerManagementGrants(
        IReadOnlyCollection<ProductPermissionGrant> grants)
    {
        foreach (var code in new[]
                 {
                     ProductPermissionCodes.UserRead,
                     ProductPermissionCodes.UserManage,
                     ProductPermissionCodes.PermissionManage,
                 })
        {
            Assert.Contains(grants, grant =>
                grant.PermissionCode == code &&
                grant.Scope.Kind == ProductPermissionScopeKind.Global);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        Directory.CreateDirectory(_root);
        var database = new ProductDatabase(Path.Combine(_root, "product.v1.db"));
        await database.InitializeAsync();
        var vault = new MemoryVault();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var store = new ProductRemoteAccountStore(database, vault, time, kdfIterations: 10_000);
        return new Fixture(database, vault, time, store);
    }

    private sealed record Fixture(
        ProductDatabase Database,
        MemoryVault Vault,
        MutableTimeProvider Time,
        ProductRemoteAccountStore Store);

    private sealed class MemoryVault : IProductSecretVault
    {
        public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

        public Task SetSecretAsync(
            string secretReference,
            string secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Secrets[secretReference] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretAsync(
            string secretReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Secrets.GetValueOrDefault(secretReference));
        }

        public Task<bool> DeleteSecretAsync(
            string secretReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Secrets.Remove(secretReference));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }
}
