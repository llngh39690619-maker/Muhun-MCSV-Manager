using System.Security.Cryptography;
using MinecraftServerManager.Data;

namespace MinecraftServerManager.Data.Tests;

public sealed class ProductRememberedDeviceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "muhun-mcsv-device-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Rotation_IsIdempotentForSameRequestAndRevokesDifferentReplay()
    {
        var fixture = await CreateFixtureAsync();
        var firstSecret = RandomNumberGenerator.GetBytes(32);
        var secondSecret = RandomNumberGenerator.GetBytes(32);
        var firstHash = SHA256.HashData(firstSecret);
        var secondHash = SHA256.HashData(secondSecret);
        var issued = fixture.Devices.Issue("manager1", "iPhone", firstHash);
        var requestId = Guid.NewGuid();

        var rotated = fixture.Devices.Rotate(
            issued.DeviceId,
            issued.Generation,
            firstHash,
            secondHash,
            requestId);
        var duplicate = fixture.Devices.Rotate(
            issued.DeviceId,
            issued.Generation,
            firstHash,
            secondHash,
            requestId);
        var replay = fixture.Devices.Rotate(
            issued.DeviceId,
            issued.Generation,
            firstHash,
            SHA256.HashData(RandomNumberGenerator.GetBytes(32)),
            Guid.NewGuid());

        Assert.Equal(ProductRememberedDeviceRefreshStatus.Success, rotated.Status);
        Assert.NotNull(rotated.Device);
        Assert.Equal((ulong)2, rotated.Device.Generation);
        Assert.Equal(ProductRememberedDeviceRefreshStatus.Success, duplicate.Status);
        Assert.Equal(rotated.Device?.Generation, duplicate.Device?.Generation);
        Assert.Equal(ProductRememberedDeviceRefreshStatus.ReplayDetected, replay.Status);
        Assert.Equal(ProductRememberedDeviceStatus.Revoked, fixture.Devices.List().Single().Status);
        Assert.Equal("replay_detected", fixture.Devices.List().Single().RevocationReason);
    }

    [Fact]
    public async Task IdleExpiration_FailsClosedAndCannotBeRotated()
    {
        var fixture = await CreateFixtureAsync();
        var secretHash = SHA256.HashData(RandomNumberGenerator.GetBytes(32));
        var issued = fixture.Devices.Issue("manager1", "Safari", secretHash);
        fixture.Time.Advance(ProductRememberedDeviceStore.IdleLifetime + TimeSpan.FromSeconds(1));

        var result = fixture.Devices.Rotate(
            issued.DeviceId,
            issued.Generation,
            secretHash,
            SHA256.HashData(RandomNumberGenerator.GetBytes(32)),
            Guid.NewGuid());

        Assert.Equal(ProductRememberedDeviceRefreshStatus.Expired, result.Status);
        Assert.Equal(ProductRememberedDeviceStatus.Expired, fixture.Devices.List().Single().Status);
        Assert.Equal("expired", fixture.Devices.List().Single().RevocationReason);
    }

    [Fact]
    public async Task AuthorizationChange_RevokesEveryActiveDeviceForAccount()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Devices.Issue(
            "manager1",
            "iPhone",
            SHA256.HashData(RandomNumberGenerator.GetBytes(32)));
        fixture.Devices.Issue(
            "manager1",
            "iPad",
            SHA256.HashData(RandomNumberGenerator.GetBytes(32)));

        var account = fixture.Accounts.List().Single();
        await fixture.Accounts.UpdateAuthorizationAsync(
            "manager1",
            enabled: true,
            account.Grants);

        Assert.All(
            fixture.Devices.List(),
            device =>
            {
                Assert.Equal(ProductRememberedDeviceStatus.Revoked, device.Status);
                Assert.Equal("authorization_changed", device.RevocationReason);
            });
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
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var accounts = new ProductRemoteAccountStore(database, new MemoryVault(), time, 10_000);
        await accounts.CreateAsync(
            "manager1",
            "mcsv-local-approved-account",
            null,
            "12345678",
            []);
        return new Fixture(accounts, new ProductRememberedDeviceStore(database, time), time);
    }

    private sealed record Fixture(
        ProductRemoteAccountStore Accounts,
        ProductRememberedDeviceStore Devices,
        MutableTimeProvider Time);

    private sealed class MemoryVault : IProductSecretVault
    {
        private readonly Dictionary<string, string> _secrets = [];

        public Task SetSecretAsync(string secretReference, string secret, CancellationToken cancellationToken = default)
        {
            _secrets[secretReference] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken = default)
            => Task.FromResult(_secrets.GetValueOrDefault(secretReference));

        public Task<bool> DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default)
            => Task.FromResult(_secrets.Remove(secretReference));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
