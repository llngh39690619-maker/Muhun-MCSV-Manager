using System.Diagnostics;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost;

/// <summary>
/// Starts one isolated provider process per invocation. A crash, timeout, cancellation, or protocol
/// violation terminates that process tree and updates durable health without affecting the caller.
/// </summary>
public sealed class ProviderInvocationHost(
    ProviderRegistry registry,
    IProviderProcessFactory processFactory,
    IProviderHttpBroker? httpBroker = null)
{
    public const int CircuitBreakerFailureThreshold = 3;

    public async Task<ProductProviderRpcResponse> InvokeAsync(
        string providerId,
        ProviderInvocationRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(request);
        if (!registry.TryGet(providerId, out var registration))
        {
            throw new KeyNotFoundException("Provider is not registered.");
        }

        ProviderInvocationPolicy.EnsureAllowed(registration, request);
        var maximumTimeout = ProviderInvocationPolicy.GetMaximumTimeout(request.Operation);
        if (timeout <= TimeSpan.Zero || timeout > maximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                $"Provider operation timeout must not exceed {maximumTimeout.TotalSeconds:0} seconds.");
        }

        await registry.ReportHealthAsync(
                providerId,
                ProviderHealthStatus.Starting,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var elapsed = Stopwatch.StartNew();
        try
        {
            var process = await processFactory.StartAsync(registration, operationSource.Token)
                .ConfigureAwait(false);
            await using var session = new ProviderRpcSession(
                process,
                registration: registration,
                httpBroker: httpBroker ?? new ProviderHttpBroker());
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new ProviderRpcTimeoutException("Provider startup exceeded its operation timeout.");
            }

            var response = await session.InvokeAsync(request, remaining, operationSource.Token)
                .ConfigureAwait(false);
            await registry.ReportHealthAsync(
                    providerId,
                    ProviderHealthStatus.Healthy,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var error = new ProviderRpcTimeoutException("Provider invocation exceeded its total timeout.");
            await ReportFailureBestEffortAsync(providerId, error).ConfigureAwait(false);
            throw error;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReportStoppedBestEffortAsync(providerId).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            await ReportFailureBestEffortAsync(providerId, error).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReportFailureBestEffortAsync(string providerId, Exception error)
    {
        try
        {
            await registry.ReportHealthAsync(
                    providerId,
                    ProviderHealthStatus.Failed,
                    DescribeFailure(error),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (registry.TryGet(providerId, out var failed) &&
                failed.ConsecutiveFailures >= CircuitBreakerFailureThreshold)
            {
                await registry.SetEnabledAsync(providerId, false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception registryError) when (
            registryError is IOException or UnauthorizedAccessException or InvalidOperationException or
            KeyNotFoundException)
        {
            // The original provider failure remains the operation result.
        }
    }

    private async Task ReportStoppedBestEffortAsync(string providerId)
    {
        try
        {
            await registry.ReportHealthAsync(
                    providerId,
                    ProviderHealthStatus.Stopped,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    private static string DescribeFailure(Exception error) => error switch
    {
        ProviderRpcTimeoutException => "Provider request timed out.",
        ProviderProcessCrashedException => "Provider process exited unexpectedly.",
        ProviderRpcProtocolException => "Provider violated the RPC protocol.",
        System.Security.Cryptography.CryptographicException => "Provider package integrity verification failed.",
        _ => "Provider invocation failed (" + error.GetType().Name + ").",
    };
}
