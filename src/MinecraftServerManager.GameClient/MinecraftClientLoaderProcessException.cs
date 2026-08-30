namespace MinecraftServerManager.GameClient;

/// <summary>
/// Indicates that a verified Minecraft loader installer failed while running as a local Java
/// process. The public properties and outer message are safe for user-facing diagnostics.
/// </summary>
public sealed class MinecraftClientLoaderProcessException : InvalidOperationException
{
    internal MinecraftClientLoaderProcessException(
        string stage,
        string? host,
        Exception innerException)
        : base("The verified Minecraft client loader installation did not complete successfully.", innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
        Stage = NormalizeStage(stage);
        Host = NormalizeHost(host);
    }

    /// <summary>A safe machine-readable process stage; it never contains a path or URI.</summary>
    public string Stage { get; }

    /// <summary>Official Maven DNS host only; no URI path, query, or user information.</summary>
    public string? Host { get; }

    private static string NormalizeStage(string? stage) =>
        !string.IsNullOrWhiteSpace(stage) &&
        stage.Length <= 48 &&
        stage.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? stage
            : "unknown";

    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
        {
            return null;
        }

        return Uri.CheckHostName(host) == UriHostNameType.Unknown ? null : host;
    }
}
