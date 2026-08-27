using System.Text;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Security;
using MinecraftServerManager.Data;
using MinecraftServerManager.Remote;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductRemoteCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "muhun-mcsv-service-credential-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DurableCredential_AuthenticatesAndReturnsExactServerScopedAuthorization()
    {
        var fixture = await CreateFixtureAsync();

        var authentication = fixture.Adapter.Authenticate(
            "mcsv-local-approved-account",
            "manager1",
            "482913");
        var found = fixture.Adapter.TryGetAuthorization(
            "mcsv-local-approved-account",
            "manager1",
            out var authorization);

        Assert.Equal(RemoteCredentialAuthenticationStatus.Success, authentication.Status);
        Assert.True(found);
        Assert.Equal(fixture.Grants, authorization.Grants);
        Assert.NotEmpty(authorization.SecurityStamp);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.InvalidCredentials,
            fixture.Adapter.Authenticate(
                "mcsv-local-approved-account",
                "manager1",
                "000000").Status);
        Assert.False(fixture.Adapter.TryGetAuthorization(
            "different-subject",
            "manager1",
            out _));
    }

    [Fact]
    public async Task RememberedToken_RotatesIdempotentlyAndDifferentReplayRevokesDevice()
    {
        var fixture = await CreateFixtureAsync();
        var issued = fixture.Adapter.IssueRememberedDevice(
            "mcsv-local-approved-account",
            "manager1",
            "iPhone 17");
        var requestId = Guid.NewGuid();

        var first = fixture.Adapter.RefreshRememberedDevice(
            "mcsv-local-approved-account",
            issued.Token,
            requestId);
        var duplicate = fixture.Adapter.RefreshRememberedDevice(
            "mcsv-local-approved-account",
            issued.Token,
            requestId);
        var replay = fixture.Adapter.RefreshRememberedDevice(
            "mcsv-local-approved-account",
            issued.Token,
            Guid.NewGuid());

        Assert.Equal(RemoteRememberedDeviceRefreshStatus.Success, first.Status);
        Assert.Equal(first.ReplacementToken, duplicate.ReplacementToken);
        Assert.NotEqual(issued.Token, first.ReplacementToken);
        Assert.Equal(RemoteRememberedDeviceRefreshStatus.ReplayDetected, replay.Status);
        Assert.Equal(RemoteRememberedDeviceStatus.Revoked, fixture.Adapter.GetRememberedDevices().Single().Status);
        var databaseBytes = await File.ReadAllBytesAsync(fixture.Database.DatabasePath);
        Assert.DoesNotContain(issued.Token, Encoding.Latin1.GetString(databaseBytes), StringComparison.Ordinal);
        Assert.DoesNotContain(first.ReplacementToken!, Encoding.Latin1.GetString(databaseBytes), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
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
        var accounts = new ProductRemoteAccountStore(database, new MemoryVault());
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
        await accounts.CreateAsync(
            "recovery1",
            "mcsv-local-approved-account",
            null,
            "193746",
            []);
        await accounts.CreateAsync(
            "manager1",
            "mcsv-local-approved-account",
            null,
            "482913",
            grants,
            role: ProductRemoteAccountRole.Viewer);
        var devices = new ProductRememberedDeviceStore(database);
        return new Fixture(
            database,
            grants,
            new ProductRemoteCredentialStore(accounts, devices));
    }

    private sealed record Fixture(
        ProductDatabase Database,
        IReadOnlyList<ProductPermissionGrant> Grants,
        ProductRemoteCredentialStore Adapter);

    private sealed class MemoryVault : IProductSecretVault
    {
        private readonly Dictionary<string, string> _values = [];

        public Task SetSecretAsync(string secretReference, string secret, CancellationToken cancellationToken = default)
        {
            _values[secretReference] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.GetValueOrDefault(secretReference));

        public Task<bool> DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.Remove(secretReference));
    }
}
