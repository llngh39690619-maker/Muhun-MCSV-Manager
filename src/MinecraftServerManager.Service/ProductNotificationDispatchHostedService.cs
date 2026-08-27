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
            await _loop.ConfigureAwait(false);
        }

        try
        {
            await dispatcher.DispatchDueOnceAsync(
                    timeProvider.GetUtcNow(),
                    DispatchBatchSize,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
