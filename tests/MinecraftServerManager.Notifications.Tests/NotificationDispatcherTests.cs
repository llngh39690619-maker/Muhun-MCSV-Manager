using MinecraftServerManager.Contracts.Notifications;
using MinecraftServerManager.Data;

namespace MinecraftServerManager.Notifications.Tests;

public sealed class NotificationDispatcherTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-NotificationTests",
        Guid.NewGuid().ToString("N"));
    private NotificationOutboxStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var database = new ProductDatabase(Path.Combine(_directory, "product.db"));
        await database.InitializeAsync();
        _store = new NotificationOutboxStore(database);
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task DeliveredProvider_CompletesDurableOutboxItem()
    {
        await _store.EnqueueAsync(CreateEvent(), ["test.provider"]);
        var provider = new FakeProvider(NotificationProviderDeliveryResult.Delivered);
        var dispatcher = new NotificationDispatcher(_store, [provider], "dispatcher:test");

        Assert.Equal(1, await dispatcher.DispatchDueOnceAsync(DateTimeOffset.UtcNow.AddSeconds(1)));

        var record = Assert.Single(await _store.ReadRecentAsync(10));
        Assert.Equal(NotificationDispatchState.Delivered, record.State);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task ProviderException_IsScheduledWithoutLeakingExceptionText()
    {
        await _store.EnqueueAsync(CreateEvent(), ["test.provider"]);
        var dispatcher = new NotificationDispatcher(
            _store,
            [new ThrowingProvider()],
            "dispatcher:test");

        await dispatcher.DispatchDueOnceAsync(DateTimeOffset.UtcNow.AddSeconds(1));

        var record = Assert.Single(await _store.ReadRecentAsync(10));
        Assert.Equal(NotificationDispatchState.Pending, record.State);
        Assert.Equal("provider.unhandled_failure", record.LastFailureCode);
        Assert.DoesNotContain("super-secret", record.LastFailureCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisableProvider_CommitsDurableControllerBeforeTerminalOutboxState()
    {
        await _store.EnqueueAsync(CreateEvent(), ["test.provider"]);
        var controller = new CapturingDisableHandler();
        var dispatcher = new NotificationDispatcher(
            _store,
            [new FakeProvider(new NotificationProviderDeliveryResult(
                NotificationProviderDeliveryStatus.DisableProvider,
                "provider.rejected",
                ProviderGeneration: "generation-1"))],
            "dispatcher:test",
            controller);

        await dispatcher.DispatchDueOnceAsync(DateTimeOffset.UtcNow.AddSeconds(1));

        var disabled = Assert.Single(controller.Calls);
        Assert.Equal("test.provider", disabled.ProviderId);
        Assert.Equal("generation-1", disabled.Generation);
        var record = Assert.Single(await _store.ReadRecentAsync(10));
        Assert.Equal(NotificationDispatchState.TerminalFailure, record.State);
    }

    private static ProductEventEnvelope CreateEvent()
        => new(
            1,
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            "server.started",
            ProductEventSeverity.Information,
            "Notification.Server.Started",
            Guid.NewGuid(),
            null,
            new Dictionary<string, string> { ["server_name"] = "Test" });

    private sealed class FakeProvider(NotificationProviderDeliveryResult result)
        : INotificationDeliveryProvider
    {
        public string ProviderId => "test.provider";
        public int CallCount { get; private set; }

        public Task<NotificationProviderDeliveryResult> DeliverAsync(
            ProductEventEnvelope envelope,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingProvider : INotificationDeliveryProvider
    {
        public string ProviderId => "test.provider";

        public Task<NotificationProviderDeliveryResult> DeliverAsync(
            ProductEventEnvelope envelope,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("super-secret implementation detail");
    }

    private sealed class CapturingDisableHandler : INotificationProviderDisableHandler
    {
        public List<(string ProviderId, string? Generation)> Calls { get; } = [];

        public Task DisableAsync(
            string providerId,
            string? providerGeneration,
            string failureCode,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            Calls.Add((providerId, providerGeneration));
            return Task.CompletedTask;
        }
    }
}
