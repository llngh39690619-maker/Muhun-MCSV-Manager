using System.Security.Cryptography;
using MinecraftServerManager.ProviderHost;

namespace MinecraftServerManager.Service;

internal static class ProductProviderErrorPolicy
{
    public static bool IsExpected(Exception error) => error is
        ArgumentException or
        InvalidDataException or
        InvalidOperationException or
        KeyNotFoundException or
        FileNotFoundException or
        DirectoryNotFoundException or
        UnauthorizedAccessException or
        CryptographicException or
        ProviderPolicyException or
        ProviderRpcTimeoutException or
        ProviderRpcProtocolException or
        ProviderProcessCrashedException;

    public static ProductPublicOperationError ToPublic(Exception error) => error switch
    {
        KeyNotFoundException => new(
            StatusCodes.Status404NotFound,
            "provider.not_found",
            "The selected provider is not registered."),
        FileNotFoundException or DirectoryNotFoundException => new(
            StatusCodes.Status404NotFound,
            "provider.package_not_found",
            "The provider package was not found in the Service inbox."),
        UnauthorizedAccessException => new(
            StatusCodes.Status403Forbidden,
            "provider.path_rejected",
            "The provider package path was rejected."),
        CryptographicException => new(
            StatusCodes.Status422UnprocessableEntity,
            "provider.signature_rejected",
            "Provider package signature or digest verification failed."),
        ProviderPolicyException => new(
            StatusCodes.Status403Forbidden,
            "provider.policy_rejected",
            "The provider operation is outside its declared capability policy."),
        ProviderRpcTimeoutException => new(
            StatusCodes.Status504GatewayTimeout,
            "provider.timeout",
            "The provider did not complete within its bounded timeout."),
        ProviderRpcProtocolException or ProviderProcessCrashedException => new(
            StatusCodes.Status502BadGateway,
            "provider.execution_failed",
            "The isolated provider process failed its protocol or health check."),
        InvalidOperationException => new(
            StatusCodes.Status409Conflict,
            "provider.operation_rejected",
            "The provider operation cannot be completed in its current state."),
        InvalidDataException => new(
            StatusCodes.Status422UnprocessableEntity,
            "provider.data_invalid",
            "Provider package or trust data is invalid."),
        _ => new(
            StatusCodes.Status400BadRequest,
            "provider.request_invalid",
            "The provider request is invalid."),
    };
}
