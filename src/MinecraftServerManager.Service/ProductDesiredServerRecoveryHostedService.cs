using MinecraftServerManager.Data;

namespace MinecraftServerManager.Service;

/// <summary>
/// Reconciles the durable desired-run set after the Service registry and product database are
/// ready. Recovery runs off the host startup path and isolates every server attempt.
/// </summary>
internal sealed class ProductDesiredServerRecoveryHostedService(
    ProductDesiredRunIntentStore desiredRunIntent,
    ProductServerRuntime runtime,
    ProductSecurityAuditStore auditStore,
    ProductServiceState serviceState,
    TimeProvider timeProvider,
    ILogger<ProductDesiredServerRecoveryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Guarantee that IHostedService.StartAsync never performs file I/O or launches Java on
        // the Windows Service startup thread.
        await Task.Yield();
        while (!serviceState.IsReady)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, stoppingToken)
                .ConfigureAwait(false);
        }

        IReadOnlyList<Guid> desiredServerIds;
        try
        {
            await desiredRunIntent.LoadAsync(stoppingToken).ConfigureAwait(false);
            desiredServerIds = desiredRunIntent.GetDesiredServerIds();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            TryAudit(null, "failed", "desired_intent_invalid", Guid.NewGuid());
            logger.LogError(
                "Desired-run intent was rejected; automatic server recovery remains fail-closed ({FailureType}).",
                error.GetType().Name);
            return;
        }

        foreach (var serverId in desiredServerIds)
        {
            stoppingToken.ThrowIfCancellationRequested();
            var correlationId = Guid.NewGuid();
            if (!TryAudit(serverId, "accepted", "desired_restore_attempt", correlationId))
            {
                logger.LogError(
                    "Server {ServerId} was not restored because its durable audit attempt could not be recorded.",
                    serverId);
                continue;
            }

            try
            {
                var result = await runtime.RestoreIfDesiredAsync(serverId, stoppingToken)
                    .ConfigureAwait(false);
                switch (result)
                {
                    case ProductDesiredServerRestoreResult.Restored:
                        TryAudit(serverId, "succeeded", "desired_restore_succeeded", correlationId);
                        logger.LogInformation("Restored desired server {ServerId}.", serverId);
                        break;
                    case ProductDesiredServerRestoreResult.AlreadyRunning:
                        TryAudit(serverId, "succeeded", "desired_already_running", correlationId);
                        break;
                    case ProductDesiredServerRestoreResult.NotDesired:
                        TryAudit(serverId, "skipped", "desired_changed_during_restore", correlationId);
                        break;
                    case ProductDesiredServerRestoreResult.MissingRegistration:
                        await desiredRunIntent.SetDesiredAsync(serverId, false, stoppingToken)
                            .ConfigureAwait(false);
                        TryAudit(serverId, "skipped", "desired_server_missing", correlationId);
                        break;
                    default:
                        TryAudit(serverId, "failed", "desired_restore_unknown", correlationId);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                TryAudit(serverId, "failed", "desired_restore_failed", correlationId);
                logger.LogWarning(
                    "Desired server {ServerId} could not be restored ({FailureType}); remaining servers will continue.",
                    serverId,
                    error.GetType().Name);
            }
        }
    }

    private bool TryAudit(
        Guid? serverId,
        string outcome,
        string reason,
        Guid correlationId)
        => auditStore.TryAppend(new ProductSecurityAuditEntry(
            Guid.NewGuid(),
            timeProvider.GetUtcNow().ToUniversalTime(),
            "server.restore",
            outcome,
            Username: null,
            PermissionCode: null,
            serverId,
            reason,
            correlationId));

}
