using System.Net;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Stable, privacy-safe failure categories for managed Minecraft downloads.
/// </summary>
public enum MinecraftClientDownloadFailureKind
{
    Unknown = 0,
    NetworkUnavailable = 1,
    Timeout = 2,
    HttpStatus = 3,
    SizeMismatch = 4,
    Sha1Mismatch = 5,
    Sha256Mismatch = 6,
    InvalidResponse = 7,
}

/// <summary>
/// Describes a failed managed download without exposing a request path, query, token, or local
/// file name. The original exception is retained only for trusted diagnostics.
/// </summary>
public sealed class MinecraftClientDownloadException : IOException
{
    public MinecraftClientDownloadException(
        int attemptCount,
        string? host,
        HttpStatusCode? httpStatusCode,
        MinecraftClientDownloadFailureKind failureKind,
        string stage,
        Exception innerException)
        : base(CreateMessage(attemptCount, failureKind, stage), innerException)
    {
        if (attemptCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount));
        }

        AttemptCount = attemptCount;
        Host = NormalizeHost(host);
        HttpStatusCode = httpStatusCode;
        FailureKind = failureKind;
        Stage = NormalizeStage(stage);
    }

    public int AttemptCount { get; }

    /// <summary>
    /// DNS host only. This never contains a URI path, query, fragment, or user information.
    /// </summary>
    public string? Host { get; }

    public HttpStatusCode? HttpStatusCode { get; }

    public MinecraftClientDownloadFailureKind FailureKind { get; }

    /// <summary>
    /// Stable machine-readable phase such as <c>game-file</c> or <c>launcher-metadata</c>.
    /// </summary>
    public string Stage { get; }

    private static string CreateMessage(
        int attemptCount,
        MinecraftClientDownloadFailureKind failureKind,
        string stage) =>
        $"Minecraft client download stage '{NormalizeStage(stage)}' failed after " +
        $"{Math.Max(1, attemptCount)} attempt(s) ({failureKind}).";

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
