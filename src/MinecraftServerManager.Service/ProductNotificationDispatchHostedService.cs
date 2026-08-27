using MinecraftServerManager.Data;
using MinecraftServerManager.Notifications;

namespace MinecraftServerManager.Service;

public sealed class ProductNotificationDispatchHostedService(
    ProductDatabaseInitializer databaseInitializer,
    NotificationDispatcher dispatcher,
    TimeProvider timeProvider,
    ILogger<ProductNotificationDispatchHostedService> logger) : IHostedService, IDisposable
{
    public const int DispatchBatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan FinalDispatchTimeout = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _loopCancellation = new();
    private Task? _loop;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await databaseInitializer.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        _loop = Task.Run(DispatchLoopAsync, CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _loopCancellation.Cancel();
        if (_loop is not null)
        {
            await ProductHostedServiceShutdown.DrainWorkerAsync(
                    _loop,
                    _loopCancellation,
                    cancellationToken,
                    logger,
                    nameof(ProductNotificationDispatchHostedService))
                .ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Skipped the final notification dispatch pass because the Service shutdown deadline was exhausted.");
            return;
        }

        using var finalDispatchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        finalDispatchCancellation.CancelAfter(FinalDispatchTimeout);
        try
        {
            await dispatcher.DispatchDueOnceAsync(
                    timeProvider.GetUtcNow(),
                    DispatchBatchSize,
                    finalDispatchCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (finalDispatchCancellation.IsCancellationRequested)
        {
            logger.LogWarning(
                "The final notification dispatch pass was abandoned at its bounded shutdown deadline.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning(
                "Final notification dispatch pass failed safely. Error: {ErrorType}",
                error.GetType().Name);
        }
    }

    public void Dispose()
    {
        _loopCancellation.Cancel();
        _loopCancellation.Dispose();
    }

    private async Task DispatchLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            while (!_loopCancellation.IsCancellationRequested)
            {
                try
                {
                    await dispatcher.DispatchDueOnceAsync(
                            timeProvider.GetUtcNow(),
                            DispatchBatchSize,
                            _loopCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_loopCancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    logger.LogWarning(
                        "Notification dispatcher pass failed safely. Error: {ErrorType}",
                        error.GetType().Name);
                }

                if (!await timer.WaitForNextTickAsync(_loopCancellation.Token).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_loopCancellation.IsCancellationRequested)
        {
        }
    }
}
