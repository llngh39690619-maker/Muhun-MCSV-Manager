using MinecraftServerManager.Contracts.Notifications;
using MinecraftServerManager.Data;
using MinecraftServerManager.Notifications;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductNotificationOutboxIntegrationTests
{
    [Fact]
    public async Task NoWebhook_StillCreatesAndCompletesDurableLocalHistory()
    {
        var fixture = await CreateFixtureAsync();
        var server = ProductServerRegistryTests.Registration();
        await fixture.Registry.UpsertAsync(server);
        var sink = new ProductDurableServerNotificationSink(
            fixture.Registry,
            new ProductSequenceStore(fixture.Database),
            fixture.Outbox,
            fixture.Settings);

        await sink.StoreAsync(
            new ProductServerStateNotification(
                server.Id,
                Guid.NewGuid(),
                MinecraftServerManager.Core.Models.ServerState.Starting,
                MinecraftServerManager.Core.Models.ServerState.Running,
                null,
                null,
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        var pending = Assert.Single(await fixture.Outbox.ReadRecentAsync(10));
        Assert.Equal(ProductLocalHistoryNotificationProvider.Id, pending.ProviderId);
        Assert.Equal(NotificationDispatchState.Pending, pending.State);

        var dispatcher = new NotificationDispatcher(
            fixture.Outbox,
            [new ProductLocalHistoryNotificationProvider()],
            "service:test-local-history");
        Assert.Equal(1, await dispatcher.DispatchDueOnceAsync(DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.Equal(
            NotificationDispatchState.Delivered,
            Assert.Single(await fixture.Outbox.ReadRecentAsync(10)).State);
    }

    [Fact]
    public async Task PendingRetry_IsDeliveredByANewDispatcherAfterRestart()
    {
        var fixture = await CreateFixtureAsync();
        await fixture.Outbox.EnqueueAsync(CreateEvent(1), ["test.provider"]);
        var now = DateTimeOffset.UtcNow;
        var first = new NotificationDispatcher(
            fixture.Outbox,
            [new FixedProvider(new NotificationProviderDeliveryResult(
                NotificationProviderDeliveryStatus.Retry,
                "test.transient",
                TimeSpan.FromSeconds(1)))],
            "service:first-process");

        await first.DispatchDueOnceAsync(now.AddMilliseconds(1));

        var pending = Assert.Single(await fixture.Outbox.ReadRecentAsync(10));
        Assert.Equal(NotificationDispatchState.Pending, pending.State);
        Assert.Equal(1, pending.AttemptCount);

        var restarted = new NotificationDispatcher(
            new NotificationOutboxStore(new ProductDatabase(fixture.Database.DatabasePath)),
            [new FixedProvider(NotificationProviderDeliveryResult.Delivered)],
            "service:restarted-process");
        await restarted.DispatchDueOnceAsync(now.AddSeconds(2));

        Assert.Equal(
            NotificationDispatchState.Delivered,
            Assert.Single(await fixture.Outbox.ReadRecentAsync(10)).State);
    }

    internal static ProductEventEnvelope CreateEvent(long sequence) => new(
        ProductEventEnvelopeValidator.CurrentSchemaVersion,
        Guid.NewGuid(),
        sequence,
        DateTimeOffset.UtcNow,
        "server.started",
        ProductEventSeverity.Information,
        "Notification.Server.Started",
        Guid.NewGuid(),
        Guid.NewGuid(),
        new Dictionary<string, string> { ["server_name"] = "Test Server" });

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var database = new ProductDatabase(Path.Combine(layout.Data, "product.v1.db"));
        await database.InitializeAsync();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        var vault = new MemoryProductSecretVault();
        return new Fixture(
            database,
            new NotificationOutboxStore(database),
            registry,
            new ProductDiscordWebhookSettings(vault, new ProductNotificationSecretResolver(vault)));
    }

    private sealed record Fixture(
        ProductDatabase Database,
        NotificationOutboxStore Outbox,
        ProductServerRegistry Registry,
        ProductDiscordWebhookSettings Settings);

    private sealed class FixedProvider(NotificationProviderDeliveryResult result)
        : INotificationDeliveryProvider
    {
        public string ProviderId => "test.provider";

        public Task<NotificationProviderDeliveryResult> DeliverAsync(
            ProductEventEnvelope envelope,
            CancellationToken cancellationToken)
            => Task.FromResult(result);
    }
}
