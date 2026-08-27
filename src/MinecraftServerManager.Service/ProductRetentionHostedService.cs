using MinecraftServerManager.Data;

namespace MinecraftServerManager.Service;

/// <summary>
/// Keeps durable history bounded for an always-on Service. Pending notification deliveries are
/// never pruned; failures are logged and retried at the next bounded maintenance interval.
/// </summary>
public sealed class ProductRetentionHostedService(
    NotificationOutboxStore notifications,
    ProductSecurityAuditStore securityAudit,
    TimeProvider timeProvider,
    ILogger<ProductRetentionHostedService> logger) : BackgroundService
{
    internal static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(24);
    internal static readonly TimeSpan NotificationRetention = TimeSpan.FromDays(90);
    internal static readonly TimeSpan SecurityAuditRetention = TimeSpan.FromDays(365);
    internal const int MaximumCompletedNotifications = 50_000;
    internal const int MaximumSecurityAuditRecords = 250_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(MaintenanceInterval, timeProvider, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            var notificationResult = await notifications.PruneCompletedAsync(
                    now - NotificationRetention,
                    MaximumCompletedNotifications,
                    cancellationToken)
                .ConfigureAwait(false);
            var auditDeleted = await securityAudit.PruneAsync(
                    now - SecurityAuditRetention,
                    MaximumSecurityAuditRecords,
                    cancellationToken)
                .ConfigureAwait(false);
            if (notificationResult.DispatchesDeleted != 0 ||
                notificationResult.EventsDeleted != 0 ||
                auditDeleted != 0)
            {
                logger.LogInformation(
                    "Durable retention removed {DispatchCount} notification dispatches, " +
                    "{EventCount} event payloads, and {AuditCount} audit records.",
                    notificationResult.DispatchesDeleted,
                    notificationResult.EventsDeleted,
                    auditDeleted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning(error, "Durable retention maintenance failed and will be retried.");
        }
    }
}
