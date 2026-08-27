using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Security;
using MinecraftServerManager.Data;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductRemoteManagementIpcTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "muhun-remote-ipc-tests",
        Guid.NewGuid().ToString("N"));
    private ProductDatabase _database = null!;
    private ProductRemoteAccountStore _accounts = null!;
    private ProductRememberedDeviceStore _devices = null!;
    private ProductDiscordWebhookSettings _discordWebhook = null!;
    private NotificationOutboxStore _notificationOutbox = null!;
    private FakeRemoteWebSupervisor _remoteWeb = null!;
    private ProductIpcMessageProcessor _processor = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = new ProductDatabase(Path.Combine(_root, "product.v1.db"));
        await _database.InitializeAsync();
        var vault = new MemoryProductSecretVault();
        _accounts = new ProductRemoteAccountStore(_database, vault);
        _devices = new ProductRememberedDeviceStore(_database);
        _discordWebhook = new ProductDiscordWebhookSettings(
            vault,
            new ProductNotificationSecretResolver(vault));
        _notificationOutbox = new NotificationOutboxStore(_database);
        _remoteWeb = new FakeRemoteWebSupervisor();
        var state = new ProductServiceState(TimeProvider.System);
        state.Initialize(Guid.NewGuid());
        state.MarkReady();
        _processor = new ProductIpcMessageProcessor(
            state,
            runtime: null,
            updates: null,
            imports: null,
            _remoteWeb,
            _accounts,
            _devices,
            _discordWebhook,
            _notificationOutbox,
            backups: null,
            notificationPreferences: new ProductNotificationPreferenceStore(
                new ProductDataLayout(_root)));
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task LifecycleMethods_ControlOnlyTheServiceSupervisor()
    {
        var started = await SendAsync(ProductIpcProtocol.RemoteAccessStartMethod);
        var reconnected = await SendAsync(ProductIpcProtocol.RemoteAccessReconnectMethod);
        var stopped = await SendAsync(ProductIpcProtocol.RemoteAccessStopMethod);

        Assert.True(started.RemoteAccess?.DesiredEnabled);
        Assert.True(reconnected.RemoteAccess?.FunnelRunning);
        Assert.False(stopped.RemoteAccess?.DesiredEnabled);
        Assert.Equal(1, _remoteWeb.EnableCount);
        Assert.Equal(1, _remoteWeb.ReconnectCount);
        Assert.Equal(1, _remoteWeb.DisableCount);
    }

    [Fact]
    public async Task AccountLifecycle_PreservesPermissionsAndRecoverablePin()
    {
        var grants = new[]
        {
            new ProductPermissionGrant(
                ProductPermissionCodes.UserRead,
                ProductPermissionScope.Global),
        };
        var createOwner = Request(ProductIpcProtocol.RemoteAccountCreateMethod) with
        {
            RemoteAccountCreate = new ProductCreateRemoteAccountRequest(
                "recovery01",
                "mcsv-local-approved-account",
                null,
                "193746",
                [],
                ProductRemoteAccountRole.Viewer),
        };
        var create = Request(ProductIpcProtocol.RemoteAccountCreateMethod) with
        {
            RemoteAccountCreate = new ProductCreateRemoteAccountRequest(
                "operator01",
                "mcsv-local-approved-account",
                null,
                "482751",
                grants,
                ProductRemoteAccountRole.Operator),
        };

        var ownerCreated = await _processor.ProcessAsync(createOwner, CancellationToken.None);
        var created = await _processor.ProcessAsync(create, CancellationToken.None);
        var listed = await _processor.ProcessAsync(
            Request(ProductIpcProtocol.RemoteAccountListMethod) with
            {
                ListOffset = 0,
                ListLimit = 1,
            },
            CancellationToken.None);
        var revealed = await _processor.ProcessAsync(
            Request(ProductIpcProtocol.RemoteAccountPinRevealMethod) with
            {
                RemoteUsername = "operator01",
            },
            CancellationToken.None);
        var disabled = await _processor.ProcessAsync(
            Request(ProductIpcProtocol.RemoteAccountAuthorizationUpdateMethod) with
            {
                RemoteUsername = "operator01",
                RemoteAccountAuthorization = new ProductUpdateRemoteAccountAuthorizationRequest(
                    false,
                    grants,
                    ProductRemoteAccountRole.Operator),
            },
            CancellationToken.None);

        Assert.Equal(ProductRemoteAccountRole.Owner, ownerCreated.RemoteAccount?.Role);
        Assert.True(created.Success);
        Assert.Equal("operator01", created.RemoteAccount?.Username);
        Assert.Equal(ProductRemoteAccountRole.Operator, created.RemoteAccount?.Role);
        Assert.Single(listed.RemoteAccountPage!.Accounts);
        Assert.Equal("482751", revealed.RemotePin?.Pin);
        Assert.False(disabled.RemoteAccount?.Enabled);
        Assert.Equal(ProductRemoteAccountRole.Operator, disabled.RemoteAccount?.Role);
    }

    [Fact]
    public async Task RememberedDevice_CanBeListedAndRevokedWithoutExposingItsSecret()
    {
        await _accounts.CreateAsync(
            "operator01",
            "mcsv-local-approved-account",
            null,
            "482751",
            [new ProductPermissionGrant(ProductPermissionCodes.UserRead, ProductPermissionScope.Global)]);
        var issued = _devices.Issue("operator01", "iPhone", new byte[32]);

        var listed = await _processor.ProcessAsync(
            Request(ProductIpcProtocol.RemoteDeviceListMethod) with { ListOffset = 0, ListLimit = 50 },
            CancellationToken.None);
        var revoked = await _processor.ProcessAsync(
            Request(ProductIpcProtocol.RemoteDeviceRevokeMethod) with { RemoteDeviceId = issued.DeviceId },
            CancellationToken.None);

        Assert.True(listed.Success);
        var projected = Assert.Single(listed.RemoteDevicePage!.Devices);
        Assert.Equal("iPhone", projected.Label);
        Assert.True(revoked.Success);
        Assert.Equal(ProductRememberedDeviceStatus.Revoked, _devices.List().Single().Status);
    }

    [Fact]
    public async Task LastOwnerMutation_IsRejectedByServiceEvenWhenRequestedOverAdministratorIpc()
    {
        var created = await _processor.ProcessAsync(
            Request(ProductIpcProtocol.RemoteAccountCreateMethod) with
            {
                RemoteAccountCreate = new ProductCreateRemoteAccountRequest(
                    "primary01",
                    "mcsv-local-approved-account",
                    null,
                    "193746",
                    [],
                    ProductRemoteAccountRole.Admin),
            },
            CancellationToken.None);
        var owner = Assert.IsType<ProductRemoteAccountSummary>(created.RemoteAccount);
        Assert.Equal(ProductRemoteAccountRole.Owner, owner.Role);

        var disabled = await _processor.ProcessAsync(
            Request(ProductIpcProtocol.RemoteAccountAuthorizationUpdateMethod) with
            {
                RemoteUsername = owner.Username,
                RemoteAccountAuthorization = new ProductUpdateRemoteAccountAuthorizationRequest(
                    false,
                    owner.Grants,
                    ProductRemoteAccountRole.Owner),
            },
            CancellationToken.None);
        var deleted = await _processor.ProcessAsync(
            Request(ProductIpcProtocol.RemoteAccountDeleteMethod) with
            {
                RemoteUsername = owner.Username,
            },
            CancellationToken.None);

        Assert.False(disabled.Success);
        Assert.Equal("remote.operation_rejected", disabled.Error?.Code);
        Assert.False(deleted.Success);
        Assert.Equal("remote.operation_rejected", deleted.Error?.Code);
        var preserved = _accounts.List().Single();
        Assert.True(preserved.Enabled);
        Assert.Equal(ProductRemoteAccountRole.Owner, preserved.Role);
    }

    [Fact]
    public async Task DiscordWebhook_IsWriteOnlyAndCanBeRemovedThroughAdministratorIpc()
    {
        var configured = await _processor.ProcessAsync(
            Request(ProductIpcProtocol.NotificationDiscordSetMethod) with
            {
                DiscordWebhook = new ProductDiscordWebhookUpdateRequest(
                    "https://discord.com/api/webhooks/12345/abcdefghijklmnopqrst"),
            },
            CancellationToken.None);
        var status = await SendAsync(ProductIpcProtocol.NotificationDiscordStatusMethod);
        var removed = await SendAsync(ProductIpcProtocol.NotificationDiscordDeleteMethod);

        Assert.True(configured.DiscordWebhookConfiguration?.Configured);
        Assert.True(status.DiscordWebhookConfiguration?.Configured);
        Assert.False(removed.DiscordWebhookConfiguration?.Configured);
        Assert.DoesNotContain(
            "abcdefghijklmnopqrst",
            System.Text.Json.JsonSerializer.Serialize(configured),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotificationPreferences_RoundTripThroughVersionedAdministratorIpc()
    {
        var requested = ProductNotificationPreferences.Default with
        {
            ServerLifecycle = false,
            ExternalThrottleSeconds = 120,
        };
        var saved = await _processor.ProcessAsync(
            Request(ProductIpcProtocol.NotificationPreferencesSetMethod) with
            {
                NotificationPreferences = requested,
            },
            CancellationToken.None);
        var loaded = await SendAsync(ProductIpcProtocol.NotificationPreferencesStatusMethod);

        Assert.True(saved.Success);
        Assert.Equal(requested, saved.NotificationPreferences);
        Assert.Equal(requested, loaded.NotificationPreferences);
    }

    private async Task<ProductIpcResponse> SendAsync(string method)
        => await _processor.ProcessAsync(Request(method), CancellationToken.None);

    private static ProductIpcRequest Request(string method)
        => new(
            ProductIpcProtocol.CurrentSchemaVersion,
            Guid.NewGuid(),
            method,
            ProductApiProtocol.MinimumSupportedVersion,
            ProductApiProtocol.CurrentVersion);

    private sealed class FakeRemoteWebSupervisor : IProductRemoteWebSupervisor
    {
        public int EnableCount { get; private set; }
        public int DisableCount { get; private set; }
        public int ReconnectCount { get; private set; }

        public ProductRemoteWebStatus Snapshot { get; private set; } = Status(false, false);

        public Task<ProductRemoteWebStatus> EnableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnableCount++;
            return Task.FromResult(Snapshot = Status(true, true));
        }

        public Task<ProductRemoteWebStatus> DisableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisableCount++;
            return Task.FromResult(Snapshot = Status(false, false));
        }

        public Task<ProductRemoteWebStatus> ReconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReconnectCount++;
            return Task.FromResult(Snapshot = Status(true, true));
        }

        private static ProductRemoteWebStatus Status(bool desired, bool running)
            => new(
                desired,
                running,
                running,
                running ? "https://example.ts.net/" : null,
                running ? "running" : "disabled",
                null,
                DateTimeOffset.UtcNow,
                null);
    }
}
