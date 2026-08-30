using System.Buffers;
using System.Net.Http;
using System.Security.Cryptography;
using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Keeps CmlLib's public parallel installer pipeline while replacing its direct-to-destination
/// download with a verified adjacent temporary file and atomic promotion.
/// </summary>
internal sealed class AtomicRetryingParallelGameInstaller : ParallelGameInstaller, IGameInstaller
{
    private const int BufferSize = 128 * 1024;
    private const string ForceUnknownHashDownload = "x-mcsv-force-positive-file";
    internal const long MaximumGameFileBytes = 2L * 1024 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly CmlDownloadReliabilityOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Action? _beforePromotionForTesting;

    public AtomicRetryingParallelGameInstaller(
        HttpClient httpClient,
        CmlDownloadReliabilityOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action? beforePromotionForTesting = null)
        : this(
            httpClient,
            (options ?? CmlDownloadReliabilityOptions.Default).Validate(),
            delayAsync ?? Task.Delay,
            beforePromotionForTesting,
            optionsValidated: true)
    {
    }

    private AtomicRetryingParallelGameInstaller(
        HttpClient httpClient,
        CmlDownloadReliabilityOptions options,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Action? beforePromotionForTesting,
        bool optionsValidated)
        : base(
            options.MaximumConcurrentChecks,
            options.MaximumConcurrentDownloads,
            options.BoundedCapacity,
            httpClient ?? throw new ArgumentNullException(nameof(httpClient)))
    {
        _ = optionsValidated;
        _httpClient = httpClient;
        _options = options;
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        _beforePromotionForTesting = beforePromotionForTesting;
        CheckFileSize = true;
        CheckFileChecksum = true;
    }

    public new ValueTask Install(
        IEnumerable<GameFile> gameFiles,
        IProgress<InstallerProgressChangedEventArgs>? fileProgress,
        IProgress<ByteProgress>? byteProgress,
        CancellationToken cancellationToken) =>
        base.Install(
            NormalizeLegacyHashes(gameFiles),
            fileProgress,
            byteProgress,
            cancellationToken);

    ValueTask IGameInstaller.Install(
        IEnumerable<GameFile> gameFiles,
        IProgress<InstallerProgressChangedEventArgs>? fileProgress,
        IProgress<ByteProgress>? byteProgress,
        CancellationToken cancellationToken) =>
        Install(gameFiles, fileProgress, byteProgress, cancellationToken);

    protected override async Task Download(
        GameFile file,
        IProgress<ByteProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(file.Path) || string.IsNullOrWhiteSpace(file.Url))
        {
            return;
        }

        if (file.Size > MaximumGameFileBytes)
        {
            throw CreateFailure(
                1,
                TryGetHost(file.Url),
                new DownloadPolicyViolationException());
        }

        var destinationPath = Path.GetFullPath(file.Path);
        var parent = Path.GetDirectoryName(destinationPath) ??
            throw new IOException("The Minecraft download destination has no parent directory.");
        Directory.CreateDirectory(parent);
        var host = TryGetHost(file.Url);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= _options.MaximumFileAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPath = Path.Combine(
                parent,
                $".x-mcsv-{Guid.NewGuid():N}.partial");
            try
            {
                await DownloadOnceAsync(file, temporaryPath, progress, cancellationToken)
                    .ConfigureAwait(false);
                _beforePromotionForTesting?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, destinationPath, overwrite: true);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ExceptionGraphSafety.RethrowOutOfMemory(exception);
                cancellationToken.ThrowIfCancellationRequested();
                lastError = exception;
                if (attempt >= _options.MaximumFileAttempts ||
                    !CmlDownloadRetryPolicy.IsRetryable(exception, cancellationToken))
                {
                    throw CreateFailure(attempt, host, exception);
                }

                await _delayAsync(_options.GetDelayAfterAttempt(attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }

        throw CreateFailure(_options.MaximumFileAttempts, host, lastError ?? new IOException());
    }

    private async Task DownloadOnceAsync(
        GameFile file,
        string temporaryPath,
        IProgress<ByteProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
                file.Url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumGameFileBytes)
        {
            throw new DownloadPolicyViolationException();
        }

        var expectedProgressLength = file.Size > 0
            ? file.Size
            : Math.Max(0, response.Content.Headers.ContentLength ?? 0);
        var declaredLength = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        long totalRead = 0;
        await using (var destination = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    var read = await source.ReadAsync(
                            buffer.AsMemory(0, BufferSize),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    totalRead = checked(totalRead + read);
                    if (totalRead > MaximumGameFileBytes)
                    {
                        throw new DownloadPolicyViolationException();
                    }
                    await destination.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken)
                        .ConfigureAwait(false);
                    progress?.Report(new ByteProgress(expectedProgressLength, totalRead));
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        if (declaredLength is { } declared && declared != totalRead)
        {
            throw new DownloadedFileValidationException(
                MinecraftClientDownloadFailureKind.SizeMismatch);
        }

        await ValidateTemporaryFileAsync(file, temporaryPath, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ValidateTemporaryFileAsync(
        GameFile file,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        var actualLength = new FileInfo(temporaryPath).Length;
        if (file.Size > 0 && actualLength != file.Size)
        {
            throw new DownloadedFileValidationException(
                MinecraftClientDownloadFailureKind.SizeMismatch);
        }

        if (!HasUsableSha1(file.Hash))
        {
            if (file.Size <= 0 && actualLength <= 0)
            {
                throw new DownloadedFileValidationException(
                    MinecraftClientDownloadFailureKind.SizeMismatch);
            }

            return;
        }

        var expectedHashText = file.Hash!;
        if (expectedHashText.Length != SHA1.HashSizeInBytes * 2 ||
            !expectedHashText.All(Uri.IsHexDigit))
        {
            throw new DownloadedFileValidationException(
                MinecraftClientDownloadFailureKind.Sha1Mismatch);
        }

        await using var input = new FileStream(
            temporaryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA1.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        var expected = Convert.FromHexString(expectedHashText);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new DownloadedFileValidationException(
                    MinecraftClientDownloadFailureKind.Sha1Mismatch);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static MinecraftClientDownloadException CreateFailure(
        int attemptCount,
        string? host,
        Exception exception) =>
        new(
            attemptCount,
            host,
            CmlDownloadRetryPolicy.GetHttpStatusCode(exception),
            CmlDownloadRetryPolicy.GetFailureKind(exception),
            "game-file",
            exception);

    private static string? TryGetHost(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.IdnHost : null;

    private static IEnumerable<GameFile> NormalizeLegacyHashes(IEnumerable<GameFile> gameFiles)
    {
        ArgumentNullException.ThrowIfNull(gameFiles);
        foreach (var file in gameFiles)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (HasUsableSha1(file.Hash))
            {
                yield return file with { Hash = file.Hash!.ToLowerInvariant() };
                continue;
            }

            // CmlLib uses "0" (and occasionally an empty hash) as a legacy force-download
            // sentinel. Preserve that behavior without asking its direct-write downloader to
            // handle the file: an internal impossible hash makes NeedUpdate return true, while
            // Download treats it as unavailable metadata and validates size/positive length.
            yield return file with { Hash = ForceUnknownHashDownload };
        }
    }

    private static bool HasUsableSha1(string? hash) =>
        !string.IsNullOrWhiteSpace(hash) &&
        !hash.Equals("0", StringComparison.Ordinal) &&
        !hash.Equals(ForceUnknownHashDownload, StringComparison.Ordinal);

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ExceptionGraphSafety.RethrowOutOfMemory(exception);
            // The primary download failure is more useful. The staging directory owner performs
            // a second bounded cleanup when the complete install is rolled back.
        }
    }
}
