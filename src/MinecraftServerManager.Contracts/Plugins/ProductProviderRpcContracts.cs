using System.Text.Json;

namespace MinecraftServerManager.Contracts.Plugins;

/// <summary>
/// Stable, process-boundary protocol constants. Provider traffic is carried in bounded,
/// little-endian length-prefixed UTF-8 JSON frames; it is never interpreted as executable UI.
/// </summary>
public static class ProductProviderRpcProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumFrameBytes = 256 * 1024;
    public const int MaximumRequestIdLength = 64;
    public const int MaximumBrokerHttpRequestBodyBytes = 64 * 1024;
    public const int MaximumBrokerHttpResponseBodyBytes = 128 * 1024;
    public const int MaximumBrokerHttpHeaders = 24;

    public const string RequestMessageType = "request";
    public const string ResponseMessageType = "response";
    public const string CancelMessageType = "cancel";
    public const string BrokerHttpRequestMessageType = "broker.http.request";
    public const string BrokerHttpResponseMessageType = "broker.http.response";

    public const string SuccessStatus = "success";
    public const string ErrorStatus = "error";
}

public static class ProductProviderOperations
{
    public const string HealthGet = "provider.health.get";
    public const string ConfigurationRead = "provider.configuration.read";
    public const string StateWrite = "provider.state.write";
    public const string NotificationDeliver = "notification.deliver";
    public const string ModpackCatalogSearch = "modpack.catalog.search";
    public const string ServerCoreCatalogSearch = "server-core.catalog.search";
    public const string RuntimeCatalogSearch = "runtime.catalog.search";
    public const string TunnelConnect = "tunnel.connect";
}

public sealed record ProductProviderRpcRequest(
    int ProtocolVersion,
    string MessageType,
    string RequestId,
    string Operation,
    string? NetworkTarget,
    JsonElement Payload);

public sealed record ProductProviderRpcCancellation(
    int ProtocolVersion,
    string MessageType,
    string RequestId);

public sealed record ProductProviderRpcError(
    string Code,
    string Message,
    bool Retryable = false);

public sealed record ProductProviderRpcResponse(
    int ProtocolVersion,
    string MessageType,
    string RequestId,
    string Status,
    JsonElement? Result,
    ProductProviderRpcError? Error);

/// <summary>
/// A provider cannot create network sockets. It may instead ask the trusted host to perform one
/// bounded HTTPS request. The host revalidates the exact manifest host, method, headers, sizes,
/// DNS results, redirect policy, and deadline before sending anything.
/// </summary>
public sealed record ProductProviderBrokerHttpRequest(
    int ProtocolVersion,
    string MessageType,
    string RequestId,
    string BrokerRequestId,
    string Method,
    string Uri,
    IReadOnlyDictionary<string, string> Headers,
    string? BodyBase64);

public sealed record ProductProviderBrokerHttpResponse(
    int ProtocolVersion,
    string MessageType,
    string RequestId,
    string BrokerRequestId,
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    string? BodyBase64,
    ProductProviderRpcError? Error);
