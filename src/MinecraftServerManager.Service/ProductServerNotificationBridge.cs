using System.Threading.Channels;
using MinecraftServerManager.Contracts.Notifications;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Data;

namespace MinecraftServerManager.Service;

public sealed record ProductServerStateNotification(
    Guid ServerId,
    Guid SessionId,
    ServerState PreviousState,
    ServerState State,
    int? ExitCode,
    string? FailureCode,
    DateTimeOffset OccurredAtUtc);

public interface IProductServerNotificationSink
{
    Task StoreAsync(
        ProductServerStateNotification notification,
        CancellationToken cancellationToken);
}

public sealed class ProductDurableServerNotificationSink(
    ProductServerRegistry registry,
    ProductNotificationPublisher publisher) : IProductServerNotificationSink
{
    public const string DiscordProviderId = "discord.primary";

    public ProductDurableServerNotificationSink(
        ProductServerRegistry registry,
        ProductSequenceStore sequences,
        NotificationOutboxStore outbox,
        ProductDiscordWebhookSettings discordSettings)
        : this(
            registry,
            new ProductNotificationPublisher(
                sequences,
                outbox,
                discordSettings,
                new ProductNotificationPreferenceStore(CreateCompatibilityLayout())))
    {
    }

    public async Task StoreAsync(
        ProductServerStateNotification notification,
        CancellationToken cancellationToken)
    {
        if (!TryMap(notification, registry, out var eventType, out var severity, out var summaryKey, out var data))
        {
            return;
        }

        await publisher.PublishAsync(
            new ProductNotificationEvent(
                eventType,
                severity,
                summaryKey,
                data,
                notification.OccurredAtUtc,
                notification.ServerId,
                notification.SessionId),
            cancellationToken).ConfigureAwait(false);
    }

    private static ProductDataLayout CreateCompatibilityLayout()
        => new(Path.Combine(
            Path.GetTempPath(),
            "MuhunMCSV-NotificationCompatibility",
            Guid.NewGuid().ToString("N")));

    private static bool TryMap(
        ProductServerStateNotification notification,
        ProductServerRegistry registry,
        out string eventType,
        out ProductEventSeverity severity,
        out string summaryKey,
        out IReadOnlyDictionary<string, string> data)
    {
        if (!registry.TryGet(notification.ServerId, out var server))
        {
            eventType = summaryKey = string.Empty;
            severity = default;
            data = new Dictionary<string, string>();
            return false;
        }

        switch (notification.State)
        {
            case ServerState.Running:
                eventType = "server.started";
                severity = ProductEventSeverity.Information;
                summaryKey = "Notification.Server.Started";
                data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["server_name"] = server.Name,
                };
                return true;

            case ServerState.Stopped:
                eventType = "server.stopped";
                severity = ProductEventSeverity.Information;
                summaryKey = "Notification.Server.Stopped";
                data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["server_name"] = server.Name,
                    ["exit_code"] = notification.ExitCode?.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
                    ["reason_code"] = notification.PreviousState == ServerState.Stopping
                        ? "manager_stop"
                        : "clean_exit",
                };
                return true;

            case ServerState.Crashed:
            case ServerState.Faulted:
                eventType = "server.crashed";
                severity = notification.State == ServerState.Faulted
                    ? ProductEventSeverity.Critical
                    : ProductEventSeverity.Error;
                summaryKey = "Notification.Server.Crashed";
                data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["server_name"] = server.Name,
                    ["exit_code"] = notification.ExitCode?.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
                    ["failure_code"] = notification.FailureCode ?? "process.exit_nonzero",
                };
                return true;

            default:
                eventType = summaryKey = string.Empty;
                severity = default;
                data = new Dictionary<string, string>();
                return false;
        }
    }
}

/// <summary>
/// Non-blocking bridge from Core events to durable storage. SQLite and provider selection run only
/// on the single background reader, never on Java's process/event callback thread.
/// </summary>
public sealed class ProductServerNotificationBridge(
    ServerProcessManager processManager,
    ProductServerRegistry registry,
    IProductServerNotificationSink sink,
    TimeProvider timeProvider,
    ILogger<ProductServerNotificationBridge> logger) : IHostedService, IDisposable
{
    public const int QueueCapacity = 512;
    private readonly Channel<ProductServerStateNotification> _queue = Channel.CreateBounded<ProductServerStateNotification>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    private readonly CancellationTokenSource _abort = new();
    private Task? _worker;
    private int _pending;
    private int _started;
    private long _dropped;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        await registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        processManager.StateChanged += OnStateChanged;
        _worker = Task.Run(ProcessAsync, CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        processManager.StateChanged -= OnStateChanged;
        _queue.Writer.TryComplete();
        if (_worker is null)
        {
            return;
        }

        await ProductHostedServiceShutdown.DrainWorkerAsync(
                _worker,
                _abort,
                cancellationToken,
                logger,
                nameof(ProductServerNotificationBridge))
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        processManager.StateChanged -= OnStateChanged;
        _queue.Writer.TryComplete();
        _abort.Cancel();
        _abort.Dispose();
    }

    private void OnStateChanged(object? sender, ServerStateChangedEventArgs args)
    {
        if (args.State is ServerState.Starting or ServerState.Stopping)
        {
            return;
        }

        var failureCode = args.Error switch
        {
            null => null,
            FileNotFoundException or DirectoryNotFoundException => "launch.file_not_found",
            UnauthorizedAccessException => "launch.access_denied",
            TimeoutException => "process.timeout",
            _ => "process.failure",
        };
        var pending = Interlocked.Increment(ref _pending);
        if (pending > QueueCapacity || !_queue.Writer.TryWrite(new ProductServerStateNotification(
                args.InstanceId,
                args.SessionId,
                args.PreviousState,
                args.State,
                args.ExitCode,
                failureCode,
                timeProvider.GetUtcNow())))
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _dropped);
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var notification in _queue.Reader.ReadAllAsync(_abort.Token).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _pending);
                var stored = false;
                for (var attempt = 0; attempt < 3 && !stored; attempt++)
                {
                    try
                    {
                        await sink.StoreAsync(notification, _abort.Token).ConfigureAwait(false);
                        stored = true;
                    }
                    catch (OperationCanceledException) when (_abort.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        if (attempt == 2)
                        {
                            logger.LogError(
                                "Failed to persist a bounded server notification after retries. Error: {ErrorType}",
                                error.GetType().Name);
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), _abort.Token)
                                .ConfigureAwait(false);
                        }
                    }
                }

                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                {
                    logger.LogWarning(
                        "Dropped {Count} server notification events because the bounded queue was full.",
                        dropped);
                }
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
        }
    }
}
