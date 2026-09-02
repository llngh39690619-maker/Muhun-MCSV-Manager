using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace MinecraftServerManager.GameClient;

internal sealed record CmlDownloadReliabilityOptions
{
    internal static CmlDownloadReliabilityOptions Default { get; } = new();

    public int MaximumFileAttempts { get; init; } = 4;

    public int MaximumPhaseAttempts { get; init; } = 4;

    public int MaximumConcurrentChecks { get; init; } = 4;

    public int MaximumConcurrentDownloads { get; init; } = 4;

    public int BoundedCapacity { get; init; } = 512;

    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(7)];

    public CmlDownloadReliabilityOptions Validate()
    {
        if (MaximumFileAttempts is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileAttempts));
        }

        if (MaximumPhaseAttempts is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPhaseAttempts));
        }

        if (MaximumConcurrentChecks is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentChecks));
        }

        if (MaximumConcurrentDownloads is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentDownloads));
        }

        if (BoundedCapacity is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(BoundedCapacity));
        }

        if (RetryDelays is null || RetryDelays.Count == 0 ||
            RetryDelays.Any(delay => delay < TimeSpan.Zero || delay > TimeSpan.FromMinutes(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelays));
        }

        return this;
    }

    public TimeSpan GetDelayAfterAttempt(int attempt) =>
        RetryDelays[Math.Min(Math.Max(attempt - 1, 0), RetryDelays.Count - 1)];
}

internal static class CmlDownloadRetryPolicy
{
    public static bool IsRetryable(Exception exception, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (callerToken.IsCancellationRequested || exception is MinecraftClientDownloadException)
        {
            return false;
        }

        if (exception is AggregateException aggregate)
        {
            var leaves = aggregate.Flatten().InnerExceptions;
            return leaves.Count > 0 && leaves.All(inner => IsRetryable(inner, callerToken));
        }

        if (exception is DownloadedFileValidationException)
        {
            return true;
        }

        if (exception is HttpRequestException http)
        {
            return http.StatusCode is null || IsTransientStatus(http.StatusCode.Value);
        }

        if (exception is TimeoutException or TaskCanceledException or HttpIOException or SocketException)
        {
            return true;
        }

        if (exception is JsonException or InvalidDataException)
        {
            return true;
        }

        if (exception is IOException io && io.InnerException is not null)
        {
            return IsRetryable(io.InnerException, callerToken);
        }

        return false;
    }

    public static MinecraftClientDownloadFailureKind GetFailureKind(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions
                .Select(GetFailureKind)
                .FirstOrDefault(kind => kind != MinecraftClientDownloadFailureKind.Unknown);
        }

        return exception switch
        {
            DownloadedFileValidationException validation => validation.FailureKind,
            MinecraftClientDownloadException download => download.FailureKind,
            HttpRequestException { StatusCode: not null } =>
                MinecraftClientDownloadFailureKind.HttpStatus,
            HttpRequestException or HttpIOException or SocketException =>
                MinecraftClientDownloadFailureKind.NetworkUnavailable,
            TimeoutException or TaskCanceledException =>
                MinecraftClientDownloadFailureKind.Timeout,
            JsonException or InvalidDataException =>
                MinecraftClientDownloadFailureKind.InvalidResponse,
            IOException io when io.InnerException is { } inner => GetFailureKind(inner),
            _ => MinecraftClientDownloadFailureKind.Unknown,
        };
    }

    public static HttpStatusCode? GetHttpStatusCode(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is MinecraftClientDownloadException download)
        {
            return download.HttpStatusCode;
        }

        if (exception is HttpRequestException http)
        {
            return http.StatusCode;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions
                .Select(GetHttpStatusCode)
                .FirstOrDefault(status => status is not null);
        }

        return exception.InnerException is null ? null : GetHttpStatusCode(exception.InnerException);
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or (HttpStatusCode)429 ||
        (int)statusCode >= 500;
}

internal sealed class DownloadedFileValidationException(
    MinecraftClientDownloadFailureKind failureKind)
    : IOException("The downloaded Minecraft file failed integrity verification.")
{
    public MinecraftClientDownloadFailureKind FailureKind { get; } = failureKind;
}

internal sealed class DownloadPolicyViolationException()
    : IOException("The Minecraft download exceeded its configured safety policy.");
