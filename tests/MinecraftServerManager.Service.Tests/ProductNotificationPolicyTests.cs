using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Notifications;
using MinecraftServerManager.Data;
using MinecraftServerManager.Notifications;
using MinecraftServerManager.Service;
using MinecraftServerManager.Updater;
using System.Text.Json;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductNotificationPolicyTests : IAsyncLifetime
{
    private readonly ProductDataLayout _layout = ProductServerRegistryTests.CreateLayout();
    private ProductDatabase _database = null!;
    private NotificationOutboxStore _outbox = null!;
    private MemoryProductSecretVault _vault = null!;
    private ProductDiscordWebhookSettings _settings = null!;

    public async Task InitializeAsync()
    {
        _database = new ProductDatabase(Path.Combine(_layout.Data, "product.v1.db"));
        await _database.InitializeAsync();
        _outbox = new NotificationOutboxStore(_database);
        _vault = new MemoryProductSecretVault();
        _settings = new ProductDiscordWebhookSettings(
            _vault,
            new ProductNotificationSecretResolver(_vault));
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_layout.Root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task DiscordRejection_DisablesExactGenerationDurably_AndEmitsSanitizedLocalEvent()
    {
        await _settings.SetAsync(ProductNotificationSecretsTests.ValidWebhook);
        var resolver = new ProductNotificationSecretResolver(_vault);
        var snapshot = await resolver.ResolveSecretSnapshotAsync(
            ProductNotificationSecretResolver.DiscordWebhookSecretReference,
            default);
        Assert.NotNull(snapshot);
        await _outbox.EnqueueAsync(
            Event("server.started", Guid.NewGuid(), DateTimeOffset.UtcNow),
            [ProductDurableServerNotificationSink.DiscordProviderId]);
        var preferences = new ProductNotificationPreferenceStore(_layout);
        var publisher = new ProductNotificationPublisher(
            new ProductSequenceStore(_database),
            _outbox,
            _settings,
            preferences);
        var handler = new ProductNotificationProviderDisableHandler(_settings, publisher);
        var dispatcher = new NotificationDispatcher(
            _outbox,
            [new RejectingDiscordProvider(snapshot!.Generation)],
            "service:rejection-test",
            handler);

        await dispatcher.DispatchDueOnceAsync(DateTimeOffset.UtcNow.AddSeconds(1));

        var configured = await _settings.GetAsync();
        Assert.True(configured.Configured);
        Assert.False(configured.Enabled);
        var records = await _outbox.ReadRecentAsync(10);
        Assert.Contains(records, item =>
            item.ProviderId == ProductDurableServerNotificationSink.DiscordProviderId &&
            item.State == NotificationDispatchState.TerminalFailure &&
            item.LastFailureCode == "discord.webhook_rejected");
        Assert.Contains(records, item => item.ProviderId == ProductLocalHistoryNotificationProvider.Id);

        var payload = await ReadEventPayloadAsync("provider.disabled");
        Assert.Contains("discord.primary", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("discord.com", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ABCDE12345", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot.Generation, payload, StringComparison.Ordinal);

        var restarted = new ProductDiscordWebhookSettings(
            _vault,
            new ProductNotificationSecretResolver(_vault));
        Assert.False((await restarted.GetAsync()).Enabled);
    }

    [Fact]
    public async Task RestartAfterGenerationDisable_RecoversOneLocalEvent_AndCannotDisableReplacement()
    {
        await _settings.SetAsync(ProductNotificationSecretsTests.ValidWebhook);
        var resolver = new ProductNotificationSecretResolver(_vault);
        var rejected = await resolver.ResolveSecretSnapshotAsync(
            ProductNotificationSecretResolver.DiscordWebhookSecretReference,
            default);
        Assert.NotNull(rejected);

        // Simulate a Service crash after the durable credential transition but before the local
        // provider.disabled event was inserted or the leased dispatch was made terminal.
        Assert.True(await _settings.DisableGenerationAsync(rejected!.Generation));
        var restartedSettings = new ProductDiscordWebhookSettings(
            _vault,
            new ProductNotificationSecretResolver(_vault));
        var restartedPublisher = new ProductNotificationPublisher(
            new ProductSequenceStore(_database),
            _outbox,
            restartedSettings,
            new ProductNotificationPreferenceStore(_layout));
        var restartedHandler = new ProductNotificationProviderDisableHandler(
            restartedSettings,
            restartedPublisher);
        var occurredAt = DateTimeOffset.UtcNow;

        await restartedHandler.DisableAsync(
            ProductDurableServerNotificationSink.DiscordProviderId,
            rejected.Generation,
            "discord.webhook_rejected",
            occurredAt,
            default);
        await restartedHandler.DisableAsync(
            ProductDurableServerNotificationSink.DiscordProviderId,
            rejected.Generation,
            "discord.webhook_invalid",
            occurredAt,
            default);

        Assert.Single(await _outbox.ReadRecentAsync(10));
        Assert.False((await restartedSettings.GetAsync()).Enabled);

        await restartedPublisher.PublishAsync(new ProductNotificationEvent(
            "server.started",
            ProductEventSeverity.Information,
            "Notification.Server.Started",
            new Dictionary<string, string> { ["server_name"] = "While disabled" },
            occurredAt.AddSeconds(1),
            Guid.NewGuid()));
        var disabledRecords = await _outbox.ReadRecentAsync(10);
        Assert.Equal(2, disabledRecords.Count);
        Assert.DoesNotContain(disabledRecords, record =>
            record.ProviderId == ProductDurableServerNotificationSink.DiscordProviderId);

        await restartedSettings.SetAsync(ProductNotificationSecretsTests.ValidWebhook);
        var replacement = await resolver.ResolveSecretSnapshotAsync(
            ProductNotificationSecretResolver.DiscordWebhookSecretReference,
            default);
        Assert.NotNull(replacement);
        Assert.NotEqual(rejected.Generation, replacement!.Generation);

        await restartedHandler.DisableAsync(
            ProductDurableServerNotificationSink.DiscordProviderId,
            rejected.Generation,
            "discord.webhook_rejected",
            occurredAt,
            default);

        Assert.True((await restartedSettings.GetAsync()).Enabled);
        await restartedPublisher.PublishAsync(new ProductNotificationEvent(
            "server.started",
            ProductEventSeverity.Information,
            "Notification.Server.Started",
            new Dictionary<string, string> { ["server_name"] = "Replacement generation" },
            occurredAt.AddSeconds(2),
            Guid.NewGuid()));
        var enabledRecords = await _outbox.ReadRecentAsync(10);
        Assert.Equal(4, enabledRecords.Count);
        Assert.Single(enabledRecords, record =>
            record.ProviderId == ProductDurableServerNotificationSink.DiscordProviderId);
    }

    [Fact]
    public async Task ExternalThrottle_IsAtomicAndSurvivesStoreRestart_LocalHistoryRemainsComplete()
    {
        await _settings.SetAsync(ProductNotificationSecretsTests.ValidWebhook);
        var store = new ProductNotificationPreferenceStore(_layout);
        await store.SetAsync(ProductNotificationPreferences.Default with
        {
            ExternalThrottleSeconds = 60,
        });
        var publisher = new ProductNotificationPublisher(
            new ProductSequenceStore(_database),
            _outbox,
            _settings,
            store);
        var serverId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => publisher.PublishAsync(
            new ProductNotificationEvent(
                "server.started",
                ProductEventSeverity.Information,
                "Notification.Server.Started",
                new Dictionary<string, string> { ["server_name"] = $"Server {index}" },
                now.AddMilliseconds(index),
                serverId))));

        var restartedPublisher = new ProductNotificationPublisher(
            new ProductSequenceStore(_database),
            _outbox,
            _settings,
            new ProductNotificationPreferenceStore(_layout));
        await restartedPublisher.PublishAsync(new ProductNotificationEvent(
            "server.started",
            ProductEventSeverity.Information,
            "Notification.Server.Started",
            new Dictionary<string, string> { ["server_name"] = "After restart" },
            now.AddSeconds(30),
            serverId));

        var records = await _outbox.ReadRecentAsync(100);
        Assert.Equal(21, records.Count(item => item.ProviderId == ProductLocalHistoryNotificationProvider.Id));
        Assert.Single(records, item =>
            item.ProviderId == ProductDurableServerNotificationSink.DiscordProviderId);
    }

    [Fact]
    public async Task DisabledSubscription_SuppressesOnlyExternalProvider()
    {
        await _settings.SetAsync(ProductNotificationSecretsTests.ValidWebhook);
        var store = new ProductNotificationPreferenceStore(_layout);
        await store.SetAsync(ProductNotificationPreferences.Default with { BackupOperations = false });
        var publisher = new ProductNotificationPublisher(
            new ProductSequenceStore(_database),
            _outbox,
            _settings,
            store);

        await publisher.PublishAsync(new ProductNotificationEvent(
            "backup.completed",
            ProductEventSeverity.Information,
            "Notification.Backup.Completed",
            new Dictionary<string, string>
            {
                ["server_name"] = "Test",
                ["backup_id"] = new string('a', 64),
                ["size_bytes"] = "42",
                ["duration_ms"] = "12",
            },
            DateTimeOffset.UtcNow,
            Guid.NewGuid()));

        var record = Assert.Single(await _outbox.ReadRecentAsync(10));
        Assert.Equal(ProductLocalHistoryNotificationProvider.Id, record.ProviderId);
    }

    [Fact]
    public async Task StableUpdaterOutcome_IsIdempotentAcrossPublisherRestart()
    {
        var operationId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        var notification = new ProductNotificationEvent(
            "product.update.completed",
            ProductEventSeverity.Information,
            "Notification.ProductUpdate.Completed",
            new Dictionary<string, string>
            {
                ["channel"] = "stable",
                ["previous_version"] = "1.0.0",
                ["target_version"] = "1.0.1",
            },
            occurredAt,
            CorrelationId: operationId,
            StableEventId: operationId,
            StableSequence: ProductNotificationPublisher.CreateStableSequence(operationId));
        var first = new ProductNotificationPublisher(
            new ProductSequenceStore(_database),
            _outbox,
            _settings,
            new ProductNotificationPreferenceStore(_layout));
        await first.PublishAsync(notification);
        var restarted = new ProductNotificationPublisher(
            new ProductSequenceStore(_database),
            _outbox,
            _settings,
            new ProductNotificationPreferenceStore(_layout));

        await restarted.PublishAsync(notification);

        Assert.Single(await _outbox.ReadRecentAsync(10));
    }

    [Theory]
    [InlineData(ProductUpdateActivationState.Committed, "product.update.completed")]
    [InlineData(ProductUpdateActivationState.RolledBack, "product.update.rolled-back")]
    [InlineData(ProductUpdateActivationState.RecoveryFailed, "product.update.failed")]
    public async Task UpdaterJournal_ProjectsEveryTerminalOutcomeIntoVersionedEvent(
        ProductUpdateActivationState state,
        string expectedType)
    {
        _layout.EnsureCreated();
        var operationId = Guid.NewGuid();
        var journal = new ProductUpdateActivationJournal(
            1,
            operationId,
            "1.0.0",
            "1.0.1",
            state,
            DateTimeOffset.UtcNow,
            state == ProductUpdateActivationState.Committed ? null : "update.health_failed");
        var path = Path.Combine(
            _layout.Updates,
            ProductDomainNotificationBridge.ActivationJournalFileName);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(journal, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var projected = ProductDomainNotificationBridge.ReadActivationOutcome(
            path,
            NullLogger.Instance);

        Assert.NotNull(projected);
        Assert.Equal(expectedType, projected!.Type);
        Assert.Equal(operationId, projected.StableEventId);
        Assert.Equal(operationId, projected.CorrelationId);
        Assert.True(projected.StableSequence > 0);

        var publisher = new ProductNotificationPublisher(
            new ProductSequenceStore(_database),
            _outbox,
            _settings,
            new ProductNotificationPreferenceStore(_layout));
        await publisher.PublishAsync(projected);
        var record = Assert.Single(await _outbox.ReadRecentAsync(10));
        Assert.Equal(ProductLocalHistoryNotificationProvider.Id, record.ProviderId);
        await AssertVersionedEventAsync(expectedType);
    }

    [Theory]
    [InlineData(ProductServerModpackUpdateState.Completed, "modpack.update.completed")]
    [InlineData(ProductServerModpackUpdateState.RolledBack, "modpack.update.rolled-back")]
    [InlineData(ProductServerModpackUpdateState.Failed, "modpack.update.failed")]
    public async Task ModpackTerminalState_ProjectsEveryRequiredOutcomeIntoVersionedEvent(
        ProductServerModpackUpdateState state,
        string expectedType)
    {
        var serverId = Guid.NewGuid();
        var updateId = Guid.NewGuid();
        var registration = ProductServerRegistryTests.Registration(serverId) with
        {
            ModpackVersionId = "version-170",
            ModpackVersionName = "1.7.0",
        };
        var status = new ProductServerModpackUpdateStatus(
            updateId,
            serverId,
            state,
            null,
            100,
            100,
            1,
            1,
            null,
            state == ProductServerModpackUpdateState.Completed
                ? null
                : "modpack_update.health_failed",
            null,
            DateTimeOffset.UtcNow);

        var projected = ProductDomainNotificationBridge.CreateModpackNotification(
            status,
            registration);

        Assert.NotNull(projected);
        Assert.Equal(expectedType, projected!.Type);
        Assert.Equal(updateId, projected.StableEventId);
        Assert.Equal(updateId, projected.CorrelationId);
        Assert.Equal(serverId, projected.ServerId);

        var publisher = new ProductNotificationPublisher(
            new ProductSequenceStore(_database),
            _outbox,
            _settings,
            new ProductNotificationPreferenceStore(_layout));
        await publisher.PublishAsync(projected);
        Assert.Single(await _outbox.ReadRecentAsync(10));
        await AssertVersionedEventAsync(expectedType);
    }

    [Fact]
    public async Task ProductUpdateStatus_ProjectsAvailabilityAndFailureIntoVersionedEvents()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var checking = UpdateStatus(ProductUpdatePhase.Checking);
        var available = UpdateStatus(ProductUpdatePhase.Available, "1.0.1");
        var applying = UpdateStatus(ProductUpdatePhase.Applying, "1.0.1");
        var failed = UpdateStatus(
            ProductUpdatePhase.Failed,
            "1.0.1",
            "update.activation_rejected");
        var availability = ProductDomainNotificationBridge.CreateProductUpdateNotification(
            new ProductUpdateStatusChangedEventArgs(checking, available, occurredAt));
        var failure = ProductDomainNotificationBridge.CreateProductUpdateNotification(
            new ProductUpdateStatusChangedEventArgs(applying, failed, occurredAt.AddSeconds(1)));

        Assert.Equal("product.update.available", Assert.IsType<ProductNotificationEvent>(availability).Type);
        Assert.Equal("product.update.failed", Assert.IsType<ProductNotificationEvent>(failure).Type);

        var publisher = new ProductNotificationPublisher(
            new ProductSequenceStore(_database),
            _outbox,
            _settings,
            new ProductNotificationPreferenceStore(_layout));
        await publisher.PublishAsync(availability!);
        await publisher.PublishAsync(failure!);

        var records = await _outbox.ReadRecentAsync(10);
        Assert.Equal(2, records.Count);
        await AssertVersionedEventAsync("product.update.available");
        await AssertVersionedEventAsync("product.update.failed");
    }

    private async Task AssertVersionedEventAsync(string eventType)
    {
        using var document = JsonDocument.Parse(await ReadEventPayloadAsync(eventType));
        Assert.Equal(
            ProductEventEnvelopeValidator.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(eventType, document.RootElement.GetProperty("type").GetString());
    }

    private async Task<string> ReadEventPayloadAsync(string eventType)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _database.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT payload_json FROM notification_events WHERE event_type = $event_type LIMIT 1;";
        command.Parameters.AddWithValue("$event_type", eventType);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static ProductEventEnvelope Event(
        string type,
        Guid serverId,
        DateTimeOffset occurredAtUtc)
        => new(
            ProductEventEnvelopeValidator.CurrentSchemaVersion,
            Guid.NewGuid(),
            1,
            occurredAtUtc,
            type,
            ProductEventSeverity.Information,
            "Notification.Server.Started",
            serverId,
            null,
            new Dictionary<string, string> { ["server_name"] = "Test" });

    private static ProductUpdateStatus UpdateStatus(
        ProductUpdatePhase phase,
        string? availableVersion = null,
        string? errorCode = null)
        => new(
            ProductUpdateChannel.Stable,
            phase,
            "1.0.0",
            "1.0.0",
            true,
            true,
            availableVersion,
            availableVersion is null ? null : 1024,
            0,
            DateTimeOffset.UtcNow,
            null,
            errorCode,
            null);

    private sealed class RejectingDiscordProvider(string generation) : INotificationDeliveryProvider
    {
        public string ProviderId => ProductDurableServerNotificationSink.DiscordProviderId;

        public Task<NotificationProviderDeliveryResult> DeliverAsync(
            ProductEventEnvelope envelope,
            CancellationToken cancellationToken)
            => Task.FromResult(new NotificationProviderDeliveryResult(
                NotificationProviderDeliveryStatus.DisableProvider,
                "discord.webhook_rejected",
                ProviderGeneration: generation));
    }
}
