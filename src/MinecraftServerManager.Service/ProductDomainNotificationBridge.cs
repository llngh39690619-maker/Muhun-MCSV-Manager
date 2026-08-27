using System.Text.Json;
using System.Threading.Channels;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Notifications;
using MinecraftServerManager.Updater;

namespace MinecraftServerManager.Service;

/// <summary>
/// Converts Service-owned backup-adjacent transaction outcomes and product updater state into the
/// common durable notification path without blocking their filesystem/process callbacks.
/// </summary>
public sealed class ProductDomainNotificationBridge(
    ProductServerModpackUpdateCoordinator modpackUpdates,
    ProductUpdateCoordinator productUpdates,
    ProductServerRegistry registry,
    ProductNotificationPublisher publisher,
    ProductDataLayout layout,
    ILogger<ProductDomainNotificationBridge> logger) : IHostedService, IDisposable
{
    internal const int QueueCapacity = 256;
    internal const string ActivationJournalFileName = "activation-journal.v1.json";
    private readonly Channel<ProductNotificationEvent> _queue = Channel.CreateBounded<ProductNotificationEvent>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    private readonly CancellationTokenSource _abort = new();
    private Task? _worker;
    private int _started;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        modpackUpdates.TerminalStatePersisted += OnModpackTerminal;
        productUpdates.StatusChanged += OnProductUpdateStatusChanged;
        _worker = Task.Run(ProcessAsync, CancellationToken.None);
        var priorOutcome = ReadPriorActivationOutcome();
        if (priorOutcome is not null && !_queue.Writer.TryWrite(priorOutcome))
        {
            logger.LogWarning("Product update outcome notification queue is full at startup.");
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        _queue.Writer.TryComplete();
        if (_worker is null) return;
        await ProductHostedServiceShutdown.DrainWorkerAsync(
                _worker,
                _abort,
                cancellationToken,
                logger,
                nameof(ProductDomainNotificationBridge))
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        Unsubscribe();
        _queue.Writer.TryComplete();
        _abort.Cancel();
        _abort.Dispose();
    }

    private void OnModpackTerminal(object? sender, ProductServerModpackUpdateStatus status)
    {
        if (status.State == ProductServerModpackUpdateState.Cancelled ||
            !registry.TryGet(status.ServerId, out var registration))
        {
            return;
        }

        Enqueue(CreateModpackNotification(status, registration));
    }

    internal static ProductNotificationEvent? CreateModpackNotification(
        ProductServerModpackUpdateStatus status,
        ProductServerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(registration);
        if (status.ServerId != registration.Id || !status.IsTerminal ||
            status.State == ProductServerModpackUpdateState.Cancelled)
        {
            return null;
        }

        var currentVersion = registration.ModpackVersionName ??
                             registration.ModpackVersionId ??
                             "unknown";
        return status.State switch
        {
            ProductServerModpackUpdateState.Completed => new ProductNotificationEvent(
                "modpack.update.completed",
                ProductEventSeverity.Information,
                "Notification.ModpackUpdate.Completed",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["server_name"] = registration.Name,
                    ["previous_version"] = "unknown",
                    ["target_version"] = currentVersion,
                },
                status.UpdatedAtUtc,
                status.ServerId,
                status.UpdateId,
                status.UpdateId,
                ProductNotificationPublisher.CreateStableSequence(status.UpdateId)),
            ProductServerModpackUpdateState.RolledBack => new ProductNotificationEvent(
                "modpack.update.rolled-back",
                ProductEventSeverity.Error,
                "Notification.ModpackUpdate.RolledBack",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["server_name"] = registration.Name,
                    ["restored_version"] = currentVersion,
                    ["target_version"] = "unknown",
                    ["failure_code"] = SafeFailure(status.ErrorCode, "modpack_update.rolled_back"),
                },
                status.UpdatedAtUtc,
                status.ServerId,
                status.UpdateId,
                status.UpdateId,
                ProductNotificationPublisher.CreateStableSequence(status.UpdateId)),
            ProductServerModpackUpdateState.Failed => new ProductNotificationEvent(
                "modpack.update.failed",
                ProductEventSeverity.Error,
                "Notification.ModpackUpdate.Failed",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["server_name"] = registration.Name,
                    ["target_version"] = currentVersion,
                    ["failure_code"] = SafeFailure(status.ErrorCode, "modpack_update.failed"),
                },
                status.UpdatedAtUtc,
                status.ServerId,
                status.UpdateId,
                status.UpdateId,
                ProductNotificationPublisher.CreateStableSequence(status.UpdateId)),
            _ => null,
        };
    }

    private void OnProductUpdateStatusChanged(object? sender, ProductUpdateStatusChangedEventArgs args)
    {
        Enqueue(CreateProductUpdateNotification(args));
    }

    internal static ProductNotificationEvent? CreateProductUpdateNotification(
        ProductUpdateStatusChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        ProductNotificationEvent? notification = null;
        if (args.Current.Phase == ProductUpdatePhase.Available &&
            (args.Previous.Phase != ProductUpdatePhase.Available ||
             !string.Equals(
                 args.Previous.AvailableVersion,
                 args.Current.AvailableVersion,
                 StringComparison.Ordinal)))
        {
            notification = new ProductNotificationEvent(
                "product.update.available",
                ProductEventSeverity.Information,
                "Notification.ProductUpdate.Available",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["channel"] = ChannelName(args.Current.Channel),
                    ["previous_version"] = args.Current.CurrentServiceVersion,
                    ["target_version"] = args.Current.AvailableVersion ?? "unknown",
                },
                args.OccurredAtUtc);
        }
        else if (args.Current.Phase == ProductUpdatePhase.Failed &&
                 (args.Previous.Phase != ProductUpdatePhase.Failed ||
                  !string.Equals(args.Previous.ErrorCode, args.Current.ErrorCode, StringComparison.Ordinal)))
        {
            notification = new ProductNotificationEvent(
                "product.update.failed",
                ProductEventSeverity.Error,
                "Notification.ProductUpdate.Failed",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["channel"] = ChannelName(args.Current.Channel),
                    ["target_version"] = args.Current.AvailableVersion ?? "unknown",
                    ["failure_code"] = SafeFailure(args.Current.ErrorCode, "update.failed"),
                },
                args.OccurredAtUtc);
        }

        return notification;
    }

    private ProductNotificationEvent? ReadPriorActivationOutcome()
    {
        var path = ResolveActivationJournalPath();
        return path is null ? null : ReadActivationOutcome(path, logger);
    }

    /// <summary>
    /// Reads the final updater journal without following links or accepting unbounded input. The
    /// updater owns this journal; the Service only projects terminal outcomes into its outbox.
    /// </summary>
    internal static ProductNotificationEvent? ReadActivationOutcome(string path, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            RejectReparseTraversal(path);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4_096);
            if (stream.Length is < 2 or > 16 * 1024)
            {
                throw new InvalidDataException("Product update activation journal size is invalid.");
            }

            var journal = JsonSerializer.Deserialize<ProductUpdateActivationJournal>(
                stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
                }) ?? throw new InvalidDataException("Product update activation journal is empty.");
            if (journal.SchemaVersion != 1 || journal.OperationId == Guid.Empty ||
                journal.UpdatedAtUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(journal.State))
            {
                throw new InvalidDataException("Product update activation journal is invalid.");
            }

            var common = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["channel"] = "unknown",
                ["target_version"] = journal.TargetVersion,
            };
            var stableSequence = ProductNotificationPublisher.CreateStableSequence(journal.OperationId);
            return journal.State switch
            {
                ProductUpdateActivationState.Committed => new ProductNotificationEvent(
                    "product.update.completed",
                    ProductEventSeverity.Information,
                    "Notification.ProductUpdate.Completed",
                    new Dictionary<string, string>(common, StringComparer.Ordinal)
                    {
                        ["previous_version"] = journal.PreviousVersion,
                    },
                    journal.UpdatedAtUtc,
                    CorrelationId: journal.OperationId,
                    StableEventId: journal.OperationId,
                    StableSequence: stableSequence),
                ProductUpdateActivationState.RolledBack => new ProductNotificationEvent(
                    "product.update.rolled-back",
                    ProductEventSeverity.Critical,
                    "Notification.ProductUpdate.RolledBack",
                    new Dictionary<string, string>(common, StringComparer.Ordinal)
                    {
                        ["restored_version"] = journal.PreviousVersion,
                        ["failure_code"] = SafeFailure(journal.FailureCode, "update.activation_rolled_back"),
                    },
                    journal.UpdatedAtUtc,
                    CorrelationId: journal.OperationId,
                    StableEventId: journal.OperationId,
                    StableSequence: stableSequence),
                ProductUpdateActivationState.RecoveryFailed => new ProductNotificationEvent(
                    "product.update.failed",
                    ProductEventSeverity.Critical,
                    "Notification.ProductUpdate.Failed",
                    new Dictionary<string, string>(common, StringComparer.Ordinal)
                    {
                        ["failure_code"] = SafeFailure(journal.FailureCode, "update.recovery_failed"),
                    },
                    journal.UpdatedAtUtc,
                    CorrelationId: journal.OperationId,
                    StableEventId: journal.OperationId,
                    StableSequence: stableSequence),
                _ => null,
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      InvalidDataException or JsonException)
        {
            logger.LogWarning(
                "Unable to project the bounded product update outcome journal. Error: {ErrorType}",
                error.GetType().Name);
            return null;
        }
    }

    private string? ResolveActivationJournalPath()
    {
        // Formal installs execute the Service from
        // <install-root>/versions/<version>/service-win-x64. The updater's authoritative journal
        // is under that same install root, not under the mutable download cache in DataRoot.
        var payloadRoot = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var versionRoot = payloadRoot.Parent;
        var versionsRoot = versionRoot?.Parent;
        var installRoot = versionsRoot?.Parent;
        if (string.Equals(payloadRoot.Name, "service-win-x64", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(versionsRoot?.Name, "versions", StringComparison.OrdinalIgnoreCase) &&
            installRoot is not null)
        {
            var formalPath = Path.Combine(
                installRoot.FullName,
                ProductUpdateActivator.ActivationStateDirectoryName,
                ActivationJournalFileName);
            if (File.Exists(formalPath))
            {
                return formalPath;
            }
        }

        // Backward-compatible Service-owned location. It also keeps non-installed/test hosts from
        // guessing an install root outside the configured protected data layout.
        var dataPath = Path.Combine(layout.Updates, ActivationJournalFileName);
        return File.Exists(dataPath) ? dataPath : null;
    }

    private static void RejectReparseTraversal(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Product update activation journal is unavailable.", fullPath);
        }

        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Product update activation journal cannot be a reparse point.");
        }

        for (var current = new DirectoryInfo(Path.GetDirectoryName(fullPath)!);
             current is not null;
             current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Product update activation journal cannot traverse a reparse point.");
            }
        }
    }

    private void Enqueue(ProductNotificationEvent? notification)
    {
        if (notification is not null && !_queue.Writer.TryWrite(notification))
        {
            logger.LogWarning("A domain notification was dropped because its bounded queue was full.");
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var notification in _queue.Reader.ReadAllAsync(_abort.Token).ConfigureAwait(false))
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        await publisher.PublishAsync(notification, _abort.Token).ConfigureAwait(false);
                        break;
                    }
                    catch (OperationCanceledException) when (_abort.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                    {
                        if (attempt == 2)
                        {
                            logger.LogError(
                                "Failed to persist a domain notification after bounded retries. Error: {ErrorType}",
                                error.GetType().Name);
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), _abort.Token)
                                .ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
        }
    }

    private void Unsubscribe()
    {
        modpackUpdates.TerminalStatePersisted -= OnModpackTerminal;
        productUpdates.StatusChanged -= OnProductUpdateStatusChanged;
    }

    private static string ChannelName(ProductUpdateChannel channel)
        => channel == ProductUpdateChannel.Stable ? "stable" : "beta";

    private static string SafeFailure(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
           value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            ? fallback
            : value;
}
