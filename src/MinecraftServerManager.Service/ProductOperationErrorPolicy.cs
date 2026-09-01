using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

internal sealed record ProductPublicOperationError(
    int StatusCode,
    string Code,
    string Message);

/// <summary>
/// Converts expected runtime failures into stable public errors. Exception messages can contain
/// absolute launch paths, process arguments, account names, or other host details and therefore
/// must never cross the REST or named-pipe trust boundary.
/// </summary>
internal static class ProductOperationErrorPolicy
{
    public static bool IsExpected(Exception error) => error is
        ArgumentException or
        InvalidDataException or
        InvalidOperationException or
        KeyNotFoundException or
        FileNotFoundException or
        DirectoryNotFoundException or
        IOException or
        UnauthorizedAccessException;

    public static ProductPublicOperationError ToPublic(Exception error) => error switch
    {
        ProductServerPropertiesConflictException => new(
            StatusCodes.Status409Conflict,
            "server.properties_changed",
            "server.properties changed after it was loaded; reload before saving."),
        MinecraftEulaAcceptanceRequiredException => new(
            StatusCodes.Status409Conflict,
            "server.eula_acceptance_required",
            "Minecraft EULA acceptance must be confirmed before this server can start."),
        KeyNotFoundException => new(
            StatusCodes.Status404NotFound,
            "server.not_found",
            "The selected server is not registered."),
        FileNotFoundException or DirectoryNotFoundException => new(
            StatusCodes.Status409Conflict,
            "server.launch_path_not_found",
            "The configured server launch path was not found."),
        UnauthorizedAccessException => new(
            StatusCodes.Status403Forbidden,
            "server.path_rejected",
            "Access to the configured server path was rejected."),
        IOException => new(
            StatusCodes.Status409Conflict,
            "operation.io_failed",
            "The Service could not complete the requested filesystem operation."),
        InvalidOperationException => new(
            StatusCodes.Status409Conflict,
            "server.operation_rejected",
            "The server operation cannot be completed in its current state."),
        InvalidDataException => new(
            StatusCodes.Status422UnprocessableEntity,
            "server.data_invalid",
            "The configured server data is invalid."),
        _ => new(
            StatusCodes.Status400BadRequest,
            "request.invalid",
            "The request is invalid."),
    };
}
