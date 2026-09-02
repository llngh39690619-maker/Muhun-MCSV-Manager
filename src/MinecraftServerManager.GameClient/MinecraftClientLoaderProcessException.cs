namespace MinecraftServerManager.GameClient;

/// <summary>
/// A bounded, privacy-safe classification of a verified loader child-process failure.
/// Values describe only a failure category and never contain process output, paths, or URIs.
/// </summary>
public enum MinecraftClientLoaderProcessFailureKind
{
    Unknown,
    StartFailure,
    ProcessExit,
    Timeout,
    AccessDenied,
    HttpNotFound,
    Network,
    Integrity,
    LauncherProfile,
    NativeCrash
}

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
        : this(
            stage,
            host,
            FindChildProcessFailure(innerException)?.ExitCode,
            FindChildProcessFailure(innerException)?.FailureKind
                ?? MinecraftClientLoaderProcessFailureKind.Unknown,
            innerException)
    {
    }

    internal MinecraftClientLoaderProcessException(
        string stage,
        string? host,
        int? exitCode,
        MinecraftClientLoaderProcessFailureKind failureKind,
        Exception innerException)
        : base("The verified Minecraft client loader installation did not complete successfully.", innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
        Stage = NormalizeStage(stage);
        Host = NormalizeHost(host);
        ExitCode = exitCode;
        FailureKind = Enum.IsDefined(failureKind)
            ? failureKind
            : MinecraftClientLoaderProcessFailureKind.Unknown;
    }

    /// <summary>A safe machine-readable process stage; it never contains a path or URI.</summary>
    public string Stage { get; }

    /// <summary>Official Maven DNS host only; no URI path, query, or user information.</summary>
    public string? Host { get; }

    /// <summary>The local Java process exit code, when the process reached a normal exit.</summary>
    public int? ExitCode { get; }

    /// <summary>A fixed privacy-safe failure category derived from bounded process diagnostics.</summary>
    public MinecraftClientLoaderProcessFailureKind FailureKind { get; }

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

    private static MinecraftClientLoaderChildProcessException? FindChildProcessFailure(
        Exception exception)
    {
        var pending = new Queue<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Enqueue(exception);
        while (pending.Count > 0 && visited.Count < 12)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is MinecraftClientLoaderChildProcessException processFailure)
            {
                return processFailure;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Take(12))
                {
                    pending.Enqueue(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Enqueue(inner);
            }
        }

        return null;
    }
}

internal sealed class MinecraftClientLoaderChildProcessException : InvalidOperationException
{
    public MinecraftClientLoaderChildProcessException(
        int? exitCode,
        MinecraftClientLoaderProcessFailureKind failureKind,
        Exception? innerException = null)
        : base("The verified loader child process failed.", innerException)
    {
        ExitCode = exitCode;
        FailureKind = Enum.IsDefined(failureKind)
            ? failureKind
            : MinecraftClientLoaderProcessFailureKind.Unknown;
    }

    public int? ExitCode { get; }

    public MinecraftClientLoaderProcessFailureKind FailureKind { get; }
}
