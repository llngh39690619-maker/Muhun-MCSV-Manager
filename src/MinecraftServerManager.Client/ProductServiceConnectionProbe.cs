using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Client;

public enum ProductServiceConnectionState
{
    Connected = 0,
    Unavailable,
    AccessDenied,
    NotReady,
    Incompatible,
    Faulted,
}

public sealed record ProductServiceConnectionResult(
    ProductServiceConnectionState State,
    string Code,
    ProductLocalHandshakePayload? Handshake)
{
    public bool IsConnected => State == ProductServiceConnectionState.Connected;
}

/// <summary>
/// Converts local IPC failures into a small, non-sensitive state machine suitable for a GUI.
/// Exception messages and machine paths are deliberately not copied into the result.
/// </summary>
public static class ProductServiceConnectionProbe
{
    public static async Task<ProductServiceConnectionResult> ProbeAsync(
        IProductServiceClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            var handshake = await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            if (!handshake.Protocol.Ready)
            {
                return Result(ProductServiceConnectionState.NotReady, "service.not_ready");
            }

            if (!HasCompatibleVersion(handshake.Protocol))
            {
                return Result(ProductServiceConnectionState.Incompatible, "protocol.version_incompatible");
            }

            return new ProductServiceConnectionResult(
                ProductServiceConnectionState.Connected,
                "service.connected",
                handshake);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProductServiceClientException error)
        {
            return Result(Classify(error.Code), NormalizeCode(error.Code));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _ = error;
            return Result(ProductServiceConnectionState.Faulted, "service.probe_failed");
        }
    }

    private static bool HasCompatibleVersion(ProductHandshakeResponse protocol)
    {
        var clientMinimum = ProductApiProtocol.MinimumSupportedVersion;
        var clientMaximum = ProductApiProtocol.CurrentVersion;
        var serviceMinimum = protocol.MinimumApiVersion;
        var serviceMaximum = protocol.ApiVersion;
        return serviceMinimum.CompareTo(serviceMaximum) <= 0
               && clientMinimum.Major == serviceMinimum.Major
               && clientMaximum.Major == serviceMaximum.Major
               && clientMinimum.CompareTo(serviceMaximum) <= 0
               && serviceMinimum.CompareTo(clientMaximum) <= 0;
    }

    private static ProductServiceConnectionState Classify(string? code)
    {
        var normalized = NormalizeCode(code);
        if (normalized is "service.timeout" or "service.connection_failed")
        {
            return ProductServiceConnectionState.Unavailable;
        }

        if (normalized == "service.access_denied")
        {
            return ProductServiceConnectionState.AccessDenied;
        }

        if (normalized == "service.not_ready")
        {
            return ProductServiceConnectionState.NotReady;
        }

        if (normalized.StartsWith("protocol.", StringComparison.Ordinal))
        {
            return ProductServiceConnectionState.Incompatible;
        }

        return ProductServiceConnectionState.Faulted;
    }

    private static string NormalizeCode(string? code)
        => string.IsNullOrWhiteSpace(code) || code.Length > 96
            ? "service.unknown_error"
            : code.Trim();

    private static ProductServiceConnectionResult Result(
        ProductServiceConnectionState state,
        string code)
        => new(state, code, null);
}
