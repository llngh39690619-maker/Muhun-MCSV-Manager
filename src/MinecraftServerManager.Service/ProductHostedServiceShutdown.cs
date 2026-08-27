namespace MinecraftServerManager.Service;

/// <summary>
/// Gives best-effort hosted-service queues a short, independent drain window. Exhausting the
/// host's shutdown token is a request to abandon that best-effort work, not a reason to crash the
/// Windows Service while it is already stopping.
/// </summary>
internal static class ProductHostedServiceShutdown
{
    internal static readonly TimeSpan NotificationDrainTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan AbortObservationTimeout = TimeSpan.FromSeconds(1);

    internal static async Task DrainWorkerAsync(
        Task worker,
        CancellationTokenSource abort,
        CancellationToken hostCancellationToken,
        ILogger logger,
        string workerName,
        TimeSpan? drainTimeout = null,
        TimeSpan? abortObservationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(abort);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerName);

        var effectiveDrainTimeout = drainTimeout ?? NotificationDrainTimeout;
        var effectiveAbortTimeout = abortObservationTimeout ?? AbortObservationTimeout;
        ValidateTimeout(effectiveDrainTimeout, nameof(drainTimeout));
        ValidateTimeout(effectiveAbortTimeout, nameof(abortObservationTimeout));

        try
        {
            await worker.WaitAsync(effectiveDrainTimeout, hostCancellationToken).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (hostCancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Aborting {WorkerName} because the Service shutdown deadline was exhausted.",
                workerName);
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "Aborting {WorkerName} after its bounded notification drain window elapsed.",
                workerName);
        }

        abort.Cancel();
        try
        {
            await worker.WaitAsync(effectiveAbortTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested)
        {
        }
        catch (TimeoutException)
        {
            // Do not let a dependency which ignored cancellation hold the Windows Service open.
            // Observe a later fault so it cannot become an unobserved task exception.
            _ = worker.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            logger.LogWarning(
                "{WorkerName} did not observe cancellation within the bounded abort window.",
                workerName);
        }
    }

    private static void ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
