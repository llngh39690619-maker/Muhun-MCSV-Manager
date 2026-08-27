using System.Buffers.Binary;
using System.Security.Cryptography;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Notifications;
using MinecraftServerManager.Data;
using MinecraftServerManager.Notifications;

namespace MinecraftServerManager.Service;

public sealed record ProductNotificationEvent(
    string Type,
    ProductEventSeverity Severity,
    string SummaryKey,
    IReadOnlyDictionary<string, string> Data,
    DateTimeOffset OccurredAtUtc,
    Guid? ServerId = null,
    Guid? CorrelationId = null,
    Guid? StableEventId = null,
    long? StableSequence = null,
    bool LocalOnly = false);

/// <summary>
/// One Service-owned path for all domain notifications. Every valid event first enters immutable
/// local history. External providers are then selected from the durable subscription policy and
/// a persisted, bounded same-type coalescing ledger.
/// </summary>
public sealed class ProductNotificationPublisher(
    ProductSequenceStore sequences,
    NotificationOutboxStore outbox,
    ProductDiscordWebhookSettings discordSettings,
    ProductNotificationPreferenceStore preferences)
{
    public const string SequenceName = "notification.events";

    public async Task PublishAsync(
        ProductNotificationEvent notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var sequence = notification.StableSequence ??
                       await sequences.NextAsync(SequenceName, cancellationToken).ConfigureAwait(false);
        var envelope = new ProductEventEnvelope(
            ProductEventEnvelopeValidator.CurrentSchemaVersion,
            notification.StableEventId ?? Guid.NewGuid(),
            sequence,
            notification.OccurredAtUtc,
            notification.Type,
            notification.Severity,
            notification.SummaryKey,
            notification.ServerId,
            notification.CorrelationId,
            notification.Data);

        var validation = ProductEventEnvelopeValidator.Validate(envelope);
        if (!validation.IsValid)
        {
            throw new ArgumentException(string.Join(" ", validation.Errors), nameof(notification));
        }

        // This write happens before reading any optional external-provider state. A corrupt or
        // unavailable provider policy can therefore never create a hole in local history.
        await outbox.EnqueueAsync(
                envelope,
                [ProductLocalHistoryNotificationProvider.Id],
                cancellationToken)
            .ConfigureAwait(false);
        if (notification.LocalOnly)
        {
            return;
        }

        try
        {
            var policy = await preferences.GetAsync(cancellationToken).ConfigureAwait(false);
            if (!IsSubscribed(policy, notification.Type))
            {
                return;
            }

            var discord = await discordSettings.GetAsync(cancellationToken).ConfigureAwait(false);
            if (!discord.Configured || !discord.Enabled)
            {
                return;
            }

            var throttleKey = CreateThrottleKey(notification.Type, notification.ServerId);
            if (!await preferences.TryClaimExternalDeliveryAsync(
                    throttleKey,
                    notification.OccurredAtUtc,
                    TimeSpan.FromSeconds(policy.ExternalThrottleSeconds),
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await outbox.EnqueueAsync(
                    envelope,
                    [ProductDurableServerNotificationSink.DiscordProviderId],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // Local history above is authoritative. Optional external-provider state must never
            // make a domain operation fail or cause the caller to retry and duplicate the event.
        }
    }

    public static long CreateStableSequence(Guid eventId)
    {
        Span<byte> input = stackalloc byte[16];
        eventId.TryWriteBytes(input);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        var value = BinaryPrimitives.ReadInt64BigEndian(hash[..8]) & long.MaxValue;
        return value == 0 ? 1 : value;
    }

    internal static bool IsSubscribed(ProductNotificationPreferences policy, string eventType)
        => eventType switch
        {
            "server.started" or "server.stopped" or "server.crashed" => policy.ServerLifecycle,
            "backup.completed" or "backup.failed" or "backup.restored" => policy.BackupOperations,
            "modpack.update.completed" or "modpack.update.failed" or
                "modpack.update.rolled-back" => policy.ModpackUpdates,
            "product.update.available" or "product.update.completed" or
                "product.update.failed" or "product.update.rolled-back" => policy.ProductUpdates,
            "provider.disabled" or "notification.delivery.failed" => policy.ProviderHealth,
            _ => false,
        };

    private static string CreateThrottleKey(string eventType, Guid? serverId)
        => $"{eventType}.{serverId?.ToString("N") ?? "global"}";
}

public sealed class ProductNotificationProviderDisableHandler(
    ProductDiscordWebhookSettings discordSettings,
    ProductNotificationPublisher publisher) : INotificationProviderDisableHandler
{
    public async Task DisableAsync(
        string providerId,
        string? providerGeneration,
        string failureCode,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                providerId,
                ProductDurableServerNotificationSink.DiscordProviderId,
                StringComparison.Ordinal))
        {
            return;
        }

        var disabledNow = await discordSettings
            .DisableGenerationAsync(providerGeneration, cancellationToken)
            .ConfigureAwait(false);
        if (!disabledNow && !await discordSettings
                .IsGenerationDisabledAsync(providerGeneration, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        var eventId = CreateDisabledEventId(providerId, providerGeneration!);

        await publisher.PublishAsync(
                new ProductNotificationEvent(
                    "provider.disabled",
                    ProductEventSeverity.Error,
                    "Notification.Provider.Disabled",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["provider_id"] = providerId,
                        // A stable public reason keeps retry/idempotency payloads identical even
                        // when a disabled resolver later reports "invalid" instead of the
                        // original HTTP rejection. Detailed codes remain on the dispatch row.
                        ["reason_code"] = "provider.rejected",
                    },
                    occurredAtUtc,
                    CorrelationId: eventId,
                    StableEventId: eventId,
                    StableSequence: ProductNotificationPublisher.CreateStableSequence(eventId),
                    LocalOnly: true),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static Guid CreateDisabledEventId(string providerId, string generation)
    {
        var material = System.Text.Encoding.UTF8.GetBytes($"provider-disabled\n{providerId}\n{generation}");
        var hash = SHA256.HashData(material);
        return new Guid(hash.AsSpan(0, 16));
    }
}
