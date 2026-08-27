using MinecraftServerManager.Data;

namespace MinecraftServerManager.Notifications;

public sealed class NotificationDispatcher
{
    private const int MaximumAttempts = 8;
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(15);
    private readonly NotificationOutboxStore _store;
    private readonly IReadOnlyDictionary<string, INotificationDeliveryProvider> _providers;
    private readonly INotificationProviderDisableHandler? _disableHandler;
    private readonly string _workerId;

    public NotificationDispatcher(
        NotificationOutboxStore store,
        IEnumerable<INotificationDeliveryProvider> providers,
        string workerId,
        INotificationProviderDisableHandler? disableHandler = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        _workerId = workerId;
        _disableHandler = disableHandler;

        var providerMap = new Dictionary<string, INotificationDeliveryProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!providerMap.TryAdd(provider.ProviderId, provider))
            {
                throw new ArgumentException("Duplicate notification provider id.", nameof(providers));
            }
        }

        _providers = providerMap;
    }

    public async Task<int> DispatchDueOnceAsync(
        DateTimeOffset nowUtc,
        int maximumCount = 20,
        CancellationToken cancellationToken = default)
    {
        var leased = await _store.LeaseDueAsync(
            nowUtc,
            maximumCount,
            TimeSpan.FromMinutes(1),
            _workerId,
            cancellationToken).ConfigureAwait(false);

        foreach (var item in leased)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_providers.TryGetValue(item.ProviderId, out var provider))
            {
                await _store.ScheduleRetryAsync(
                    item.DispatchId,
                    _workerId,
                    nowUtc,
                    "provider.unavailable",
                    maximumAttempts: 1,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            NotificationProviderDeliveryResult result;
            try
            {
                result = await provider.DeliverAsync(item.Event, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                result = new NotificationProviderDeliveryResult(
                    NotificationProviderDeliveryStatus.Retry,
                    "provider.unhandled_failure");
            }

            await CompleteAsync(item, result, nowUtc, cancellationToken).ConfigureAwait(false);
        }

        return leased.Count;
    }

    private async Task CompleteAsync(
        LeasedNotification item,
        NotificationProviderDeliveryResult result,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        switch (result.Status)
        {
            case NotificationProviderDeliveryStatus.Delivered:
                await _store.MarkDeliveredAsync(
                    item.DispatchId,
                    _workerId,
                    nowUtc,
                    cancellationToken).ConfigureAwait(false);
                return;

            case NotificationProviderDeliveryStatus.Retry:
            {
                var retryAfter = result.RetryAfter ?? CalculateBackoff(item.AttemptCount);
                retryAfter = retryAfter < TimeSpan.FromSeconds(1)
                    ? TimeSpan.FromSeconds(1)
                    : retryAfter > MaximumBackoff
                        ? MaximumBackoff
                        : retryAfter;
                await _store.ScheduleRetryAsync(
                    item.DispatchId,
                    _workerId,
                    nowUtc.Add(retryAfter),
                    NormalizeFailureCode(result.FailureCode, "provider.transient_failure"),
                    MaximumAttempts,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            case NotificationProviderDeliveryStatus.TerminalFailure:
                await _store.ScheduleRetryAsync(
                    item.DispatchId,
                    _workerId,
                    nowUtc,
                    NormalizeFailureCode(result.FailureCode, "provider.terminal_failure"),
                    item.AttemptCount + 1,
                    cancellationToken).ConfigureAwait(false);
                return;

            case NotificationProviderDeliveryStatus.DisableProvider:
            {
                var failureCode = NormalizeFailureCode(
                    result.FailureCode,
                    "provider.disabled");
                if (_disableHandler is not null)
                {
                    try
                    {
                        await _disableHandler.DisableAsync(
                                item.ProviderId,
                                result.ProviderGeneration,
                                failureCode,
                                item.Event.OccurredAtUtc,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        await _store.ScheduleRetryAsync(
                                item.DispatchId,
                                _workerId,
                                nowUtc.Add(TimeSpan.FromSeconds(5)),
                                "provider.disable_state_unavailable",
                                MaximumAttempts,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }
                }

                await _store.ScheduleRetryAsync(
                        item.DispatchId,
                        _workerId,
                        nowUtc,
                        failureCode,
                        item.AttemptCount + 1,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            default:
                throw new InvalidOperationException("Unknown notification delivery status.");
        }
    }

    private static TimeSpan CalculateBackoff(int attemptCount)
    {
        var exponent = Math.Clamp(attemptCount, 0, 7);
        return TimeSpan.FromSeconds(5 * (1 << exponent));
    }

    private static string NormalizeFailureCode(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
