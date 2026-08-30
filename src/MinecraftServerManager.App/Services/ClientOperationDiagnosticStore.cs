using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient;

namespace MinecraftServerManager.App.Services;

internal sealed record ClientOperationDiagnosticWriteRequest(
    string Operation,
    string Stage,
    string FailureCode,
    Exception Exception,
    IReadOnlyDictionary<string, string?> Context);

internal sealed record ClientOperationDiagnosticReference(
    string DiagnosticId,
    string FilePath);

internal sealed partial class ClientOperationDiagnosticStore
{
    private const int SchemaVersion = 3;
    private const int MaximumPayloadBytes = 64 * 1024;
    private const int MaximumExceptionCount = 12;
    private const int MaximumContextEntries = 16;
    private const int DefaultMaximumRetainedFiles = 50;
    private static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromDays(30);

    private static readonly HashSet<string> NumericContextKeys = new(StringComparer.Ordinal)
    {
        "packId",
        "packVersionId",
        "versionId",
        "completedFiles",
        "totalFiles",
        "attempt",
        "maximumAttempts"
    };

    private static readonly HashSet<string> BooleanContextKeys = new(StringComparer.Ordinal)
    {
        "rollbackAttempted",
        "rollbackSucceeded",
        "recoveryRequired"
    };

    private static readonly HashSet<string> TokenContextKeys = new(StringComparer.Ordinal)
    {
        "minecraftVersion",
        "gameVersion",
        "loader",
        "loaderVersion",
        "javaVersion",
        "provider"
    };

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly int _maximumRetainedFiles;
    private readonly TimeSpan _maximumAge;
    private readonly string _trustedRoot;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ClientOperationDiagnosticStore(string directoryPath)
        : this(
            ResolveTrustedParent(directoryPath),
            directoryPath,
            static () => DateTimeOffset.UtcNow,
            DefaultMaximumRetainedFiles,
            DefaultMaximumAge)
    {
    }

    public ClientOperationDiagnosticStore(ApplicationPaths paths)
        : this(
            ResolveApplicationRoot(paths),
            ResolveApplicationDirectory(paths),
            static () => DateTimeOffset.UtcNow,
            DefaultMaximumRetainedFiles,
            DefaultMaximumAge)
    {
    }

    internal ClientOperationDiagnosticStore(
        string directoryPath,
        Func<DateTimeOffset> utcNow,
        int maximumRetainedFiles,
        TimeSpan maximumAge)
        : this(
            ResolveTrustedParent(directoryPath),
            directoryPath,
            utcNow,
            maximumRetainedFiles,
            maximumAge)
    {
    }

    private ClientOperationDiagnosticStore(
        string trustedRoot,
        string directoryPath,
        Func<DateTimeOffset> utcNow,
        int maximumRetainedFiles,
        TimeSpan maximumAge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(utcNow);
        if (maximumRetainedFiles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedFiles));
        }

        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        _trustedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        DirectoryPath = Path.TrimEndingDirectorySeparator(SafePath.EnsureWithinRoot(
            _trustedRoot,
            Path.GetFullPath(directoryPath),
            allowRoot: false));
        _utcNow = utcNow;
        _maximumRetainedFiles = maximumRetainedFiles;
        _maximumAge = maximumAge;
    }

    public string DirectoryPath { get; }

    public async Task<ClientOperationDiagnosticReference?> WriteFailureAsync(
        ClientOperationDiagnosticWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Exception);
            ArgumentNullException.ThrowIfNull(request.Context);

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var directoryLease = EnsureDiagnosticDirectoryAndAcquireLease();

                var occurredAt = _utcNow().ToUniversalTime();
                var diagnosticId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                var fileName = $"{occurredAt:yyyyMMdd-HHmmss}-{diagnosticId}.json";
                var destination = SafePath.CombineUnderRoot(DirectoryPath, fileName);
                var partial = SafePath.CombineUnderRoot(
                    DirectoryPath,
                    $".{fileName}.{Guid.NewGuid():N}.partial");

                var payload = CreatePayload(request, diagnosticId, occurredAt);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
                if (bytes.Length > MaximumPayloadBytes)
                {
                    return null;
                }

                try
                {
                    await using (var stream = new FileStream(
                                     partial,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     16 * 1024,
                                     FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        stream.Flush(flushToDisk: true);
                    }

                    SafePath.EnsureNoReparsePointsUnderRoot(DirectoryPath, partial);
                    File.Move(partial, destination, overwrite: false);
                    SafePath.EnsureNoReparsePointsUnderRoot(DirectoryPath, destination);
                }
                catch
                {
                    TryDeleteRegularFile(partial);
                    throw;
                }

                try
                {
                    PruneRetainedFiles(occurredAt, destination);
                }
                catch
                {
                    // The diagnostic is already durable. Retention is best effort and must not
                    // turn a successful report into a second application failure.
                }

                return new ClientOperationDiagnosticReference(diagnosticId, destination);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch
        {
            // This method is called while another exception is already being handled. Logging
            // must never obscure or replace the actual installation failure.
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private object CreatePayload(
        ClientOperationDiagnosticWriteRequest request,
        string diagnosticId,
        DateTimeOffset occurredAt)
    {
        var policyClassification = FtbClientInstallFailurePolicy.Classify(
            request.Exception,
            request.Stage);
        var failureCode = FtbClientInstallFailurePolicy.IsKnownFailureCode(request.FailureCode)
            ? request.FailureCode
            : policyClassification.FailureCode;
        var exceptions = CreateSafeExceptionChain(request.Exception);
        var context = CreateSafeContext(request.Context);

        return new
        {
            SchemaVersion,
            DiagnosticId = diagnosticId,
            OccurredAtUtc = occurredAt,
            Operation = NormalizeCode(request.Operation, "ftb_client_install"),
            Stage = NormalizeCode(request.Stage, "unknown"),
            Failure = new
            {
                Code = failureCode,
                policyClassification.IsRetryable,
                policyClassification.HttpStatusCode,
                policyClassification.HttpRequestError,
                policyClassification.RemoteHost,
                policyClassification.ExceptionType,
                policyClassification.AttemptCount,
                policyClassification.DownloadStage,
                policyClassification.DownloadFailureKind,
                policyClassification.LoaderStage,
                policyClassification.TransactionStage,
                ExceptionChain = exceptions
            },
            Context = context
        };
    }

    private static IReadOnlyList<object> CreateSafeExceptionChain(Exception exception)
        => FtbClientInstallFailurePolicy.DescribeExceptionGraph(exception)
            .Take(MaximumExceptionCount)
            .Select(item =>
            {
                var http = item.Exception as HttpRequestException;
                var managedDownload = item.Exception as MinecraftClientDownloadException;
                return (object)new
                {
                    item.Depth,
                    Type = BoundTypeName(item.Exception.GetType()),
                    item.Exception.HResult,
                    HttpStatusCode = managedDownload?.HttpStatusCode is { } managedStatus
                        ? (int?)managedStatus
                        : http?.StatusCode is null
                            ? null
                            : (int?)http.StatusCode.Value,
                    HttpRequestError = http?.HttpRequestError.ToString(),
                    AttemptCount = managedDownload?.AttemptCount
                };
            })
            .ToArray();

    private static SortedDictionary<string, string?> CreateSafeContext(
        IReadOnlyDictionary<string, string?> source)
    {
        var result = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in source.Take(MaximumContextEntries * 4))
        {
            if (result.Count >= MaximumContextEntries)
            {
                break;
            }

            string? normalized = null;
            if (NumericContextKeys.Contains(pair.Key)
                && long.TryParse(
                    pair.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number)
                && number >= 0)
            {
                normalized = number.ToString(CultureInfo.InvariantCulture);
            }
            else if (BooleanContextKeys.Contains(pair.Key)
                     && bool.TryParse(pair.Value, out var boolean))
            {
                normalized = boolean ? "true" : "false";
            }
            else if (TokenContextKeys.Contains(pair.Key))
            {
                normalized = NormalizeOptionalToken(pair.Value);
            }
            else if (pair.Key.Equals("httpStatusCode", StringComparison.Ordinal)
                     && int.TryParse(
                         pair.Value,
                         NumberStyles.None,
                         CultureInfo.InvariantCulture,
                         out var status)
                     && status is >= 100 and <= 599)
            {
                normalized = status.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                continue;
            }

            if (normalized is not null)
            {
                result[pair.Key] = normalized;
            }
        }

        return result;
    }

    private IDisposable EnsureDiagnosticDirectoryAndAcquireLease()
    {
        EnsureRegularDirectory(_trustedRoot);
        var relativePath = Path.GetRelativePath(_trustedRoot, DirectoryPath);
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        IDisposable currentLease = EmptyDisposable.Instance;
        var current = _trustedRoot;
        try
        {
            foreach (var segment in segments)
            {
                current = SafePath.CombineUnderRoot(current, segment);
                if (!Directory.Exists(current))
                {
                    if (File.Exists(current))
                    {
                        throw new IOException("A diagnostic directory component is not a directory.");
                    }

                    Directory.CreateDirectory(current);
                }

                EnsureRegularDirectory(current);
                if (OperatingSystem.IsWindows())
                {
                    var extendedLease = SafePath.AcquireNoReparseDirectoryChainLease(
                        _trustedRoot,
                        current);
                    currentLease.Dispose();
                    currentLease = extendedLease;
                }
                else
                {
                    SafePath.EnsureNoReparsePointsUnderRoot(_trustedRoot, current);
                }
            }

            if (!Path.GetFullPath(current).Equals(
                    DirectoryPath,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Diagnostic directory escaped its trusted root.");
            }

            return currentLease;
        }
        catch
        {
            currentLease.Dispose();
            throw;
        }
    }

    private void PruneRetainedFiles(DateTimeOffset occurredAt, string currentPath)
    {
        var threshold = occurredAt.UtcDateTime - _maximumAge;
        var currentFullPath = Path.GetFullPath(currentPath);
        var files = new DirectoryInfo(DirectoryPath)
            .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
            .Where(IsRegularFile)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var file in files)
        {
            if (Path.GetFullPath(file.FullName).Equals(
                    currentFullPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (file.LastWriteTimeUtc < threshold)
            {
                TryDeleteRegularFile(file.FullName);
            }
        }

        var retainedCandidates = new DirectoryInfo(DirectoryPath)
            .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
            .Where(IsRegularFile)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToArray();

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentFullPath };
        foreach (var file in retainedCandidates)
        {
            if (keep.Count >= _maximumRetainedFiles)
            {
                break;
            }

            keep.Add(Path.GetFullPath(file.FullName));
        }

        foreach (var file in retainedCandidates)
        {
            if (!keep.Contains(Path.GetFullPath(file.FullName)))
            {
                TryDeleteRegularFile(file.FullName);
            }
        }
    }

    private static bool IsRegularFile(FileInfo file)
    {
        try
        {
            return file.Exists
                   && !file.Attributes.HasFlag(FileAttributes.Directory)
                   && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                   && file.Length <= MaximumPayloadBytes;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteRegularFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.Directory)
                && !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void EnsureRegularDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException("Diagnostic paths cannot contain reparse points.");
        }
    }

    private static string ResolveTrustedParent(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        return Directory.GetParent(Path.GetFullPath(directoryPath))?.FullName
               ?? throw new ArgumentException(
                   "The diagnostic directory must have an explicit trusted parent.",
                   nameof(directoryPath));
    }

    private static string ResolveApplicationRoot(ApplicationPaths? paths)
        => paths?.Root ?? throw new ArgumentNullException(nameof(paths));

    private static string ResolveApplicationDirectory(ApplicationPaths? paths)
        => Path.Combine(
            paths?.Logs ?? throw new ArgumentNullException(nameof(paths)),
            "client-operations",
            "ftb-install");

    private static string NormalizeCode(string? value, string fallback)
    {
        var normalized = NormalizeOptionalToken(value);
        return normalized ?? fallback;
    }

    private static string? NormalizeOptionalToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return SafeTokenPattern().IsMatch(trimmed) && !JwtPattern().IsMatch(trimmed)
            ? trimmed
            : null;
    }

    private static string BoundTypeName(Type type)
    {
        var value = type.FullName ?? type.Name;
        return value.Length <= 192 ? value : value[..192];
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+\\-]{0,63}$", RegexOptions.NonBacktracking)]
    private static partial Regex SafeTokenPattern();

    [GeneratedRegex(
        "^[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}$",
        RegexOptions.NonBacktracking)]
    private static partial Regex JwtPattern();

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose()
        {
        }
    }
}
