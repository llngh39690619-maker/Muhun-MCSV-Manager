using MinecraftServerManager.Contracts.Localization;

namespace MinecraftServerManager.Contracts;

public sealed record ProductDiscordWebhookUpdateRequest(string WebhookUrl);

public sealed record ProductDiscordWebhookConfiguration(bool Configured, bool Enabled = true);

/// <summary>
/// Versioned Service-owned policy. Local history is intentionally not configurable; these
/// switches and the bounded coalescing interval apply only to external providers.
/// </summary>
public sealed record ProductNotificationPreferences(
    int SchemaVersion,
    bool ServerLifecycle,
    bool BackupOperations,
    bool ModpackUpdates,
    bool ProductUpdates,
    bool ProviderHealth,
    int ExternalThrottleSeconds)
{
    public const int CurrentSchemaVersion = 1;
    public const int MinimumThrottleSeconds = 0;
    public const int MaximumThrottleSeconds = 3_600;

    /// <summary>
    /// Canonical culture used to render Service-owned external notifications. Keeping this on the
    /// durable notification policy makes delivery deterministic even when the GUI is not running.
    /// The initializer preserves compatibility with schema-v1 files written before culture was
    /// recorded explicitly.
    /// </summary>
    public string CultureName { get; init; } = ProductLocalizationCatalog.FallbackCulture;

    public static ProductNotificationPreferences Default { get; } = new(
        CurrentSchemaVersion,
        ServerLifecycle: true,
        BackupOperations: true,
        ModpackUpdates: true,
        ProductUpdates: true,
        ProviderHealth: true,
        ExternalThrottleSeconds: 30);
}

public static class ProductNotificationPreferencesValidator
{
    public static void ValidateAndThrow(ProductNotificationPreferences? preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.SchemaVersion != ProductNotificationPreferences.CurrentSchemaVersion)
        {
            throw new ArgumentException("Notification preference schema is unsupported.", nameof(preferences));
        }

        if (preferences.ExternalThrottleSeconds is
            < ProductNotificationPreferences.MinimumThrottleSeconds or
            > ProductNotificationPreferences.MaximumThrottleSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preferences),
                "External notification throttle must be between 0 and 3600 seconds.");
        }

        // A missing value is accepted only as the schema-v1 compatibility shape and is repaired
        // by the Service store to the fallback culture before persistence. Any supplied value
        // must already be canonical so aliases cannot create ambiguous durable policy.
        if (!string.IsNullOrWhiteSpace(preferences.CultureName)
            && (!ProductLocalizationCatalog.TryNormalizeCulture(
                preferences.CultureName,
                out var normalizedCulture)
            || !string.Equals(
                preferences.CultureName,
                normalizedCulture,
                StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "External notification culture must be a supported canonical product culture.",
                nameof(preferences));
        }
    }
}

public sealed record ProductNotificationDeliverySummary(
    Guid DispatchId,
    Guid EventId,
    string ProviderId,
    string State,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    string? LastFailureCode,
    DateTimeOffset? DeliveredAtUtc);

public sealed record ProductNotificationDeliveryPage(
    int Offset,
    int NextOffset,
    bool HasMore,
    IReadOnlyList<ProductNotificationDeliverySummary> Deliveries);
