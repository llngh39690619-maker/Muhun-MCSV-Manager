using MinecraftServerManager.Contracts.Notifications;

namespace MinecraftServerManager.Notifications;

public enum NotificationProviderDeliveryStatus
{
    Delivered,
    Retry,
    TerminalFailure,
    DisableProvider,
}

public sealed record NotificationProviderDeliveryResult(
    NotificationProviderDeliveryStatus Status,
    string? FailureCode = null,
    TimeSpan? RetryAfter = null,
    string? ProviderGeneration = null)
{
    public static NotificationProviderDeliveryResult Delivered { get; } =
        new(NotificationProviderDeliveryStatus.Delivered);
}

/// <summary>
/// Optional generation-aware secret projection.  A provider returns the opaque generation with
/// its delivery result so a delayed rejection can never disable credentials that an operator
/// replaced while the HTTP request was in flight.
/// </summary>
public sealed record NotificationSecretSnapshot(string? Value, string Generation);

public interface IVersionedNotificationSecretResolver : INotificationSecretResolver
{
    ValueTask<NotificationSecretSnapshot?> ResolveSecretSnapshotAsync(
        string secretReference,
        CancellationToken cancellationToken);
}

public interface INotificationProviderDisableHandler
{
    Task DisableAsync(
        string providerId,
        string? providerGeneration,
        string failureCode,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);
}

public interface INotificationDeliveryProvider
{
    string ProviderId { get; }

    Task<NotificationProviderDeliveryResult> DeliverAsync(
        ProductEventEnvelope envelope,
        CancellationToken cancellationToken);
}

public interface INotificationSecretResolver
{
    ValueTask<string?> ResolveSecretAsync(string secretReference, CancellationToken cancellationToken);
}

public interface INotificationMessageRenderer
{
    ValueTask<string> RenderAsync(ProductEventEnvelope envelope, CancellationToken cancellationToken);
}
