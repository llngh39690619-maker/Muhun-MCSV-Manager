using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using MinecraftServerManager.GameClient;

namespace MinecraftServerManager.App.Services;

internal sealed record FtbClientInstallFailureClassification(
    string FailureCode,
    string LocalizationKey)
{
    public bool IsRetryable { get; init; }
    public int? HttpStatusCode { get; init; }
    public string? HttpRequestError { get; init; }
    public string ExceptionType { get; init; } = typeof(Exception).FullName!;
    public int? AttemptCount { get; init; }
    public string? RemoteHost { get; init; }
    public string? DownloadStage { get; init; }
    public string? DownloadFailureKind { get; init; }
    public string? LoaderStage { get; init; }
    public string? TransactionStage { get; init; }
}

internal static class FtbClientInstallFailurePolicy
{
    internal const string NetworkTimeout = "network_timeout";
    internal const string NetworkUnavailable = "network_unavailable";
    internal const string HttpRejected = "http_rejected";
    internal const string DiskFull = "disk_full";
    internal const string AccessDenied = "access_denied";
    internal const string IntegrityFailed = "integrity_failed";
    internal const string LoaderFailed = "loader_failed";
    internal const string GamePayloadFailed = "game_payload_failed";
    internal const string JavaFailed = "java_failed";
    internal const string RollbackIncomplete = "rollback_incomplete";
    internal const string RecoveryRequired = "recovery_required";
    internal const string Unknown = "unknown";

    private const int MaximumExceptionCount = 12;
    private const int DiskFullHResult = unchecked((int)0x80070070);
    private const int HandleDiskFullHResult = unchecked((int)0x80070027);
    private const int AccessDeniedHResult = unchecked((int)0x80070005);

    public static FtbClientInstallFailureClassification Classify(
        Exception exception,
        string lastStage)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var exceptions = EnumerateExceptionGraph(exception).ToArray();
        var normalizedStage = NormalizeStage(lastStage);
        var managedDownload = exceptions.OfType<MinecraftClientDownloadException>().FirstOrDefault();
        var loaderProcess = exceptions.OfType<MinecraftClientLoaderProcessException>().FirstOrDefault();
        var transactionFailure = exceptions.FirstOrDefault(static candidate =>
            candidate is FtbClientInstallRecoveryRequiredException
                or FtbClientInstallRollbackIncompleteException);
        if (transactionFailure is FtbClientInstallRecoveryRequiredException)
        {
            return Create(
                RecoveryRequired,
                "client.vm.catalog.ftb.failure.recovery",
                transactionFailure,
                managedDownload: managedDownload,
                loaderProcess: loaderProcess);
        }

        if (transactionFailure is FtbClientInstallRollbackIncompleteException)
        {
            return Create(
                RollbackIncomplete,
                "client.vm.catalog.ftb.failure.rollback",
                transactionFailure,
                managedDownload: managedDownload,
                loaderProcess: loaderProcess);
        }

        if (loaderProcess is not null)
        {
            return Create(
                LoaderFailed,
                "client.vm.catalog.ftb.failure.loader",
                loaderProcess,
                managedDownload: managedDownload,
                loaderProcess: loaderProcess);
        }

        if (managedDownload is not null)
        {
            var managedClassification = ClassifyManagedDownload(managedDownload, normalizedStage);
            if (managedClassification is not null)
            {
                return managedClassification;
            }
        }

        var diskFull = exceptions.FirstOrDefault(IsDiskFull);
        if (diskFull is not null)
        {
            return Create(
                DiskFull,
                "client.vm.catalog.ftb.failure.storage",
                diskFull,
                managedDownload: managedDownload);
        }

        var accessDenied = exceptions.FirstOrDefault(IsAccessDenied);
        if (accessDenied is not null)
        {
            return Create(
                AccessDenied,
                "client.vm.catalog.ftb.failure.storage",
                accessDenied,
                managedDownload: managedDownload);
        }

        var integrity = exceptions.FirstOrDefault(IsIntegrityFailure);
        if (integrity is not null)
        {
            return Create(
                IntegrityFailed,
                "client.vm.catalog.ftb.failure.integrity",
                integrity,
                managedDownload: managedDownload);
        }

        var timeout = exceptions.FirstOrDefault(IsNetworkTimeout);
        if (timeout is not null)
        {
            return Create(
                NetworkTimeout,
                "client.vm.catalog.ftb.failure.timeout",
                timeout,
                isRetryable: true,
                managedDownload: managedDownload);
        }

        var rejected = exceptions
            .OfType<HttpRequestException>()
            .FirstOrDefault(http => http.StatusCode is not null);
        if (rejected is not null)
        {
            var status = (int)rejected.StatusCode!.Value;
            return Create(
                HttpRejected,
                "client.vm.catalog.ftb.failure.network",
                rejected,
                isRetryable: status == 429 || status >= 500,
                managedDownload: managedDownload);
        }

        var unavailable = exceptions.FirstOrDefault(IsNetworkUnavailable);
        if (unavailable is not null)
        {
            return Create(
                NetworkUnavailable,
                "client.vm.catalog.ftb.failure.network",
                unavailable,
                isRetryable: true,
                managedDownload: managedDownload);
        }

        var effectiveStage = managedDownload is null
            ? normalizedStage
            : NormalizeStage(managedDownload.Stage) is { Length: > 0 } managedStage
                && !managedStage.Equals("unknown", StringComparison.Ordinal)
                    ? managedStage
                    : normalizedStage;
        var invalidData = exceptions.FirstOrDefault(static candidate => candidate is InvalidDataException);
        if (invalidData is not null && IsContentDownloadStage(effectiveStage))
        {
            return Create(
                IntegrityFailed,
                "client.vm.catalog.ftb.failure.integrity",
                invalidData,
                managedDownload: managedDownload);
        }

        if (IsJavaStage(effectiveStage))
        {
            return Create(
                JavaFailed,
                "client.vm.catalog.ftb.failure.java",
                managedDownload ?? exception,
                managedDownload: managedDownload);
        }

        if (IsLoaderStage(effectiveStage))
        {
            return Create(
                LoaderFailed,
                "client.vm.catalog.ftb.failure.loader",
                managedDownload ?? exception,
                managedDownload: managedDownload);
        }

        if (IsGamePayloadStage(effectiveStage))
        {
            return Create(
                GamePayloadFailed,
                "client.vm.catalog.ftb.failure.compatibility",
                managedDownload ?? exception,
                managedDownload: managedDownload);
        }

        return Create(Unknown, "client.vm.catalog.ftb.failure.unknown", exception);
    }

    internal static bool IsKnownFailureCode(string? failureCode)
        => failureCode is NetworkTimeout
            or NetworkUnavailable
            or HttpRejected
            or DiskFull
            or AccessDenied
            or IntegrityFailed
            or LoaderFailed
            or GamePayloadFailed
            or JavaFailed
            or RollbackIncomplete
            or RecoveryRequired
            or Unknown;

    internal static IReadOnlyList<(Exception Exception, int Depth)> DescribeExceptionGraph(
        Exception exception)
        => EnumerateExceptionGraphWithDepth(exception).ToArray();

    private static FtbClientInstallFailureClassification Create(
        string code,
        string localizationKey,
        Exception exception,
        bool isRetryable = false,
        MinecraftClientDownloadException? managedDownload = null,
        MinecraftClientLoaderProcessException? loaderProcess = null)
    {
        var http = exception as HttpRequestException;
        managedDownload ??= exception as MinecraftClientDownloadException;
        loaderProcess ??= exception as MinecraftClientLoaderProcessException;
        return new FtbClientInstallFailureClassification(code, localizationKey)
        {
            IsRetryable = isRetryable,
            HttpStatusCode = managedDownload?.HttpStatusCode is { } managedStatus
                ? (int)managedStatus
                : http?.StatusCode is null
                    ? null
                    : (int)http.StatusCode.Value,
            HttpRequestError = http?.HttpRequestError.ToString(),
            ExceptionType = BoundTypeName(exception.GetType()),
            AttemptCount = managedDownload?.AttemptCount,
            RemoteHost = loaderProcess?.Host ?? managedDownload?.Host,
            DownloadStage = PreserveSafeToken(managedDownload?.Stage),
            DownloadFailureKind = managedDownload?.FailureKind.ToString(),
            LoaderStage = PreserveSafeToken(loaderProcess?.Stage),
            TransactionStage = PreserveSafeToken(exception switch
            {
                FtbClientInstallRecoveryRequiredException recovery => recovery.Stage,
                FtbClientInstallRollbackIncompleteException rollback => rollback.Stage,
                _ => null
            })
        };
    }

    private static FtbClientInstallFailureClassification? ClassifyManagedDownload(
        MinecraftClientDownloadException exception,
        string outerStage)
    {
        var downloadStage = NormalizeStage(exception.Stage);
        var effectiveStage = downloadStage.Equals("unknown", StringComparison.Ordinal)
            ? outerStage
            : downloadStage;
        return exception.FailureKind switch
        {
            MinecraftClientDownloadFailureKind.Timeout => Create(
                NetworkTimeout,
                "client.vm.catalog.ftb.failure.timeout",
                exception,
                isRetryable: true),
            MinecraftClientDownloadFailureKind.NetworkUnavailable => Create(
                NetworkUnavailable,
                "client.vm.catalog.ftb.failure.network",
                exception,
                isRetryable: true),
            MinecraftClientDownloadFailureKind.HttpStatus
                when exception.HttpStatusCode is HttpStatusCode.RequestTimeout
                    or HttpStatusCode.GatewayTimeout => Create(
                        NetworkTimeout,
                        "client.vm.catalog.ftb.failure.timeout",
                        exception,
                        isRetryable: true),
            MinecraftClientDownloadFailureKind.HttpStatus => Create(
                HttpRejected,
                "client.vm.catalog.ftb.failure.network",
                exception,
                isRetryable: exception.HttpStatusCode is { } status
                    && (status == (HttpStatusCode)429 || (int)status >= 500)),
            MinecraftClientDownloadFailureKind.SizeMismatch
                or MinecraftClientDownloadFailureKind.Sha1Mismatch
                or MinecraftClientDownloadFailureKind.Sha256Mismatch => Create(
                    IntegrityFailed,
                    "client.vm.catalog.ftb.failure.integrity",
                    exception),
            MinecraftClientDownloadFailureKind.InvalidResponse
                when IsContentDownloadStage(effectiveStage) => Create(
                        IntegrityFailed,
                        "client.vm.catalog.ftb.failure.integrity",
                        exception),
            MinecraftClientDownloadFailureKind.InvalidResponse
                when IsLoaderStage(effectiveStage) => Create(
                    LoaderFailed,
                    "client.vm.catalog.ftb.failure.loader",
                    exception),
            MinecraftClientDownloadFailureKind.InvalidResponse
                when IsGamePayloadStage(effectiveStage) => Create(
                    GamePayloadFailed,
                    "client.vm.catalog.ftb.failure.compatibility",
                    exception),
            _ => null
        };
    }

    private static IEnumerable<Exception> EnumerateExceptionGraph(Exception root)
        => EnumerateExceptionGraphWithDepth(root).Select(item => item.Exception);

    private static IEnumerable<(Exception Exception, int Depth)> EnumerateExceptionGraphWithDepth(
        Exception root)
    {
        var pending = new Queue<(Exception Exception, int Depth)>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Enqueue((root, 0));

        while (pending.Count > 0 && visited.Count < MaximumExceptionCount)
        {
            var current = pending.Dequeue();
            if (current.Depth >= MaximumExceptionCount || !visited.Add(current.Exception))
            {
                continue;
            }

            yield return current;

            if (current.Exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Take(MaximumExceptionCount))
                {
                    pending.Enqueue((inner, current.Depth + 1));
                }
            }
            else if (current.Exception.InnerException is { } inner)
            {
                pending.Enqueue((inner, current.Depth + 1));
            }
        }
    }

    private static bool IsDiskFull(Exception exception)
        => exception is IOException
           && exception.HResult is DiskFullHResult or HandleDiskFullHResult;

    private static bool IsAccessDenied(Exception exception)
        => exception is UnauthorizedAccessException or SecurityException
           || exception.HResult == AccessDeniedHResult;

    private static bool IsIntegrityFailure(Exception exception)
    {
        if (exception is CryptographicException)
        {
            return true;
        }

        var typeName = exception.GetType().Name;
        return typeName.Contains("Checksum", StringComparison.OrdinalIgnoreCase)
               || typeName.Contains("Integrity", StringComparison.OrdinalIgnoreCase)
               || typeName.Contains("HashMismatch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsContentDownloadStage(string stage)
        => stage.Equals("download_content", StringComparison.Ordinal)
           || stage.Equals("game_file", StringComparison.Ordinal)
           || stage.Contains("content_download", StringComparison.Ordinal);

    private static bool IsJavaStage(string stage)
        => stage.Contains("prepare_java", StringComparison.Ordinal)
           || stage.StartsWith("java_", StringComparison.Ordinal)
           || stage.EndsWith("_java", StringComparison.Ordinal);

    private static bool IsLoaderStage(string stage)
        => stage.Contains("loader", StringComparison.Ordinal)
           || stage.Contains("neoforge", StringComparison.Ordinal)
           || stage.Contains("forge", StringComparison.Ordinal)
           || stage.Contains("fabric", StringComparison.Ordinal)
           || stage.Contains("quilt", StringComparison.Ordinal);

    private static bool IsGamePayloadStage(string stage)
        => stage.Contains("payload", StringComparison.Ordinal)
           || stage.Contains("minecraft", StringComparison.Ordinal)
           || stage.Contains("prerequisite", StringComparison.Ordinal)
           || stage.Contains("install_game", StringComparison.Ordinal)
           || stage.Contains("game_file", StringComparison.Ordinal)
           || stage.Contains("launcher_metadata", StringComparison.Ordinal)
           || stage.Equals("metadata", StringComparison.Ordinal)
           || stage.EndsWith("_metadata", StringComparison.Ordinal);

    private static bool IsNetworkTimeout(Exception exception)
        => exception is TimeoutException or TaskCanceledException
           || exception is HttpRequestException
           {
               StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout
           };

    private static bool IsNetworkUnavailable(Exception exception)
        => exception is SocketException
           || exception is HttpRequestException { StatusCode: null };

    private static string NormalizeStage(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return string.Empty;
        }

        return new string(stage
                .Trim()
                .ToLowerInvariant()
                .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')
                .ToArray())
            .Trim('_');
    }

    private static string? PreserveSafeToken(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 64
           && value.All(character =>
               char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value
            : null;

    private static string BoundTypeName(Type type)
    {
        var name = type.FullName ?? type.Name;
        return name.Length <= 192 ? name : name[..192];
    }
}
