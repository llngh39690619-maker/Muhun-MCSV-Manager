using MinecraftServerManager.Contracts.Notifications;

namespace MinecraftServerManager.Data.Tests;

public sealed class NotificationOutboxStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-DataTests",
        Guid.NewGuid().ToString("N"));
    private ProductDatabase _database = null!;
    private NotificationOutboxStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new ProductDatabase(Path.Combine(_directory, "product.db"));
        await _database.InitializeAsync();
        _store = new NotificationOutboxStore(_database);
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
    public async Task EnqueueLeaseComplete_PersistsDeliveryState()
    {
        var envelope = CreateEvent();
        var created = await _store.EnqueueAsync(envelope, ["discord.primary", "windows.local"]);

        Assert.Equal(2, created.Count);
        var leased = await _store.LeaseDueAsync(
            DateTimeOffset.UtcNow.AddSeconds(1),
            10,
            TimeSpan.FromMinutes(1),
            "worker:one");
        Assert.Equal(2, leased.Count);
        Assert.All(leased, item => Assert.Equal(envelope.EventId, item.Event.EventId));

        Assert.True(await _store.MarkDeliveredAsync(
            leased[0].DispatchId,
            "worker:one",
            DateTimeOffset.UtcNow));

        var records = await _store.ReadRecentAsync(10);
        Assert.Equal(2, records.Count);
        Assert.Single(records, record => record.State == NotificationDispatchState.Delivered);
        Assert.Single(records, record => record.State == NotificationDispatchState.Pending);
    }

    [Fact]
    public async Task DuplicateEventAndProvider_IsIdempotent_ButMutationIsRejected()
    {
        var envelope = CreateEvent();
        Assert.Single(await _store.EnqueueAsync(envelope, ["discord.primary"]));
        Assert.Empty(await _store.EnqueueAsync(envelope, ["discord.primary"]));

        var mutated = envelope with
        {
            Data = new Dictionary<string, string> { ["server_name"] = "Different" },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.EnqueueAsync(mutated, ["discord.primary"]));
    }

    [Fact]
    public async Task ActiveLease_IsExclusive_AndExpiredLeaseCanBeRecovered()
    {
        await _store.EnqueueAsync(CreateEvent(), ["discord.primary"]);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var first = await _store.LeaseDueAsync(now, 1, TimeSpan.FromSeconds(5), "worker:one");
        Assert.Single(first);

        Assert.Empty(await _store.LeaseDueAsync(
            now.AddSeconds(1),
            1,
            TimeSpan.FromSeconds(5),
            "worker:two"));

        var recovered = await _store.LeaseDueAsync(
            now.AddSeconds(6),
            1,
            TimeSpan.FromSeconds(5),
            "worker:two");
        Assert.Single(recovered);
        Assert.Equal(first[0].DispatchId, recovered[0].DispatchId);
        Assert.False(await _store.MarkDeliveredAsync(
            first[0].DispatchId,
            "worker:one",
            now.AddSeconds(7)));
    }

    [Fact]
    public async Task RetryBecomesTerminalAtConfiguredAttemptLimit()
    {
        await _store.EnqueueAsync(CreateEvent(), ["discord.primary"]);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var first = Assert.Single(await _store.LeaseDueAsync(
            now,
            1,
            TimeSpan.FromSeconds(5),
            "worker:one"));

        Assert.Equal(
            NotificationRetryOutcome.Scheduled,
            await _store.ScheduleRetryAsync(
                first.DispatchId,
                "worker:one",
                now.AddMinutes(1),
                "http.503",
                maximumAttempts: 2));

        var second = Assert.Single(await _store.LeaseDueAsync(
            now.AddMinutes(2),
            1,
            TimeSpan.FromSeconds(5),
            "worker:two"));
        Assert.Equal(1, second.AttemptCount);

        Assert.Equal(
            NotificationRetryOutcome.TerminalFailure,
            await _store.ScheduleRetryAsync(
                second.DispatchId,
                "worker:two",
                now.AddMinutes(3),
                "http.503",
                maximumAttempts: 2));

        var record = Assert.Single(await _store.ReadRecentAsync(10));
        Assert.Equal(NotificationDispatchState.TerminalFailure, record.State);
        Assert.Equal(2, record.AttemptCount);
        Assert.Equal("http.503", record.LastFailureCode);
    }

    [Fact]
    public async Task InvalidOrSensitiveEvent_IsRejectedBeforeDiskWrite()
    {
        var invalid = CreateEvent() with
        {
            Data = new Dictionary<string, string> { ["access_token"] = "must-not-persist" },
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.EnqueueAsync(invalid, ["discord.primary"]));
        Assert.Empty(await _store.ReadRecentAsync(10));
    }

    [Fact]
    public async Task PruneCompleted_RemovesOldDeliveredHistoryButPreservesPendingWork()
    {
        var first = CreateEvent();
        var second = CreateEvent() with
        {
            EventId = Guid.NewGuid(),
            Sequence = first.Sequence + 1,
        };
        await _store.EnqueueAsync(first, ["discord.primary"]);
        await _store.EnqueueAsync(second, ["discord.primary"]);

        var now = DateTimeOffset.UtcNow;
        var leased = await _store.LeaseDueAsync(
            now.AddSeconds(1),
            1,
            TimeSpan.FromMinutes(1),
            "worker:prune");
        Assert.True(await _store.MarkDeliveredAsync(
            Assert.Single(leased).DispatchId,
            "worker:prune",
            now.AddSeconds(2)));

        var result = await _store.PruneCompletedAsync(now.AddDays(1), 100);

        Assert.Equal(1, result.DispatchesDeleted);
        Assert.Equal(1, result.EventsDeleted);
        var remaining = Assert.Single(await _store.ReadRecentAsync(10));
        Assert.Equal(NotificationDispatchState.Pending, remaining.State);
    }

    private static ProductEventEnvelope CreateEvent()
        => new(
            ProductEventEnvelopeValidator.CurrentSchemaVersion,
            Guid.NewGuid(),
            Sequence: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow,
            "server.started",
            ProductEventSeverity.Information,
            "Notification.Server.Started",
            Guid.NewGuid(),
            Guid.NewGuid(),
            new Dictionary<string, string> { ["server_name"] = "Test Server" });
}
