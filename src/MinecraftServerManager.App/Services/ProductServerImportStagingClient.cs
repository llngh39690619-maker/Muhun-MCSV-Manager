using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.Client;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

internal sealed class ProductServerImportStagingClient
{
    private const int MaximumFiles = 100_000;
    private const long MaximumBytes = 1024L * 1024 * 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultNoProgressTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DefaultResumeRequiredTimeout = TimeSpan.FromSeconds(30);
    private readonly IProductServiceClient _client;
    private readonly string _authorizedImportsRoot;
    private readonly TimeSpan _noProgressTimeout;
    private readonly TimeSpan _resumeRequiredTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<DateTimeOffset> _utcNow;

    public ProductServerImportStagingClient(
        IProductServiceClient client,
        string? authorizedImportsRoot = null,
        TimeSpan? noProgressTimeout = null,
        TimeSpan? resumeRequiredTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _authorizedImportsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            authorizedImportsRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Muhun",
                "MCSV",
                "imports")));
        _noProgressTimeout = noProgressTimeout ?? DefaultNoProgressTimeout;
        _resumeRequiredTimeout = resumeRequiredTimeout ?? DefaultResumeRequiredTimeout;
        if (_noProgressTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(noProgressTimeout));
        }
        if (_resumeRequiredTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(resumeRequiredTimeout));
        }

        _delayAsync = delayAsync ?? ((delay, cancellationToken) =>
            Task.Delay(delay, cancellationToken));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ProductServerImportStatus> ImportAsync(
        ServerInstance instance,
        string? migrationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var sourceRoot = RequireSafeSourceDirectory(instance.DirectoryPath);
        var javaExecutable = ResolveJavaExecutable(instance);
        var runtimeRoot = ResolveRuntimeRoot(javaExecutable);
        var definition = CreateDefinition(instance, sourceRoot, runtimeRoot, javaExecutable);
        var status = await _client.BeginImportAsync(
                new ProductServerImportBeginRequest(definition, migrationKey),
                cancellationToken)
            .ConfigureAwait(false);
        if (status.State == ProductServerImportState.Completed)
        {
            return status;
        }

        if (status.State != ProductServerImportState.Staging ||
            string.IsNullOrWhiteSpace(status.StagingDirectory))
        {
            return await AwaitTerminalAsync(status, cancellationToken).ConfigureAwait(false);
        }

        var staging = ValidateServiceStaging(status);
        try
        {
            var serverPayload = Path.Combine(staging, "payload", "server");
            var runtimePayload = Path.Combine(staging, "payload", "runtime");
            await ResetPayloadAsync(staging, serverPayload, runtimePayload).ConfigureAwait(false);
            var entries = new List<ProductServerImportManifestEntry>();
            long totalBytes = 0;
            await CopyTreeAsync(
                    sourceRoot,
                    serverPayload,
                    "server",
                    entries,
                    value => AddImportBytes(ref totalBytes, value),
                    cancellationToken)
                .ConfigureAwait(false);
            await CopyTreeAsync(
                    runtimeRoot,
                    runtimePayload,
                    "runtime",
                    entries,
                    value => AddImportBytes(ref totalBytes, value),
                    cancellationToken)
                .ConfigureAwait(false);
            if (entries.Count == 0 || entries.Count > MaximumFiles || totalBytes > MaximumBytes)
            {
                throw new InvalidDataException("Service import payload exceeds its bounded manifest limits.");
            }

            var manifest = new ProductServerImportManifest(1, status.ImportId, entries);
            var manifestPath = Path.Combine(staging, "manifest.v1.json");
            await WriteManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            var hash = await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            status = await _client.CommitImportAsync(
                    status.ImportId,
                    Convert.ToHexString(hash),
                    cancellationToken)
                .ConfigureAwait(false);
            return await AwaitTerminalAsync(status, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryCancelAsync(status.ImportId).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TryCancelAsync(status.ImportId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ProductServerImportStatus> AwaitTerminalAsync(
        ProductServerImportStatus status,
        CancellationToken cancellationToken)
    {
        var lastObserved = ImportProgressObservation.From(status);
        var lastProgressAt = _utcNow();
        DateTimeOffset? resumeRequiredSince = null;
        while (!status.IsTerminal)
        {
            var now = _utcNow();
            if (IsResumeRequired(status))
            {
                resumeRequiredSince ??= now;
                if (ElapsedSince(resumeRequiredSince.Value, now) >= _resumeRequiredTimeout)
                {
                    throw new ProductServiceClientException(
                        "import.resume_required",
                        "The Windows Service could not finish registering the imported server " +
                        "(import.resume_required). Restart Muhun MCSV Service and retry; the " +
                        "promoted server data was preserved.");
                }
            }
            else
            {
                resumeRequiredSince = null;
            }

            if (ElapsedSince(lastProgressAt, now) >= _noProgressTimeout)
            {
                throw new ProductServiceClientException(
                    "import.stalled",
                    $"The Windows Service import made no observable progress while it was " +
                    $"{status.State} for {FormatDuration(_noProgressTimeout)}. The background " +
                    "job was stopped instead of waiting forever; retry after checking or " +
                    "restarting Muhun MCSV Service.");
            }

            var delay = PollInterval;
            delay = MinPositive(delay, _noProgressTimeout - ElapsedSince(lastProgressAt, now));
            if (resumeRequiredSince is { } resumeStarted)
            {
                delay = MinPositive(
                    delay,
                    _resumeRequiredTimeout - ElapsedSince(resumeStarted, now));
            }

            await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
            var next = await _client.GetImportStatusAsync(status.ImportId, cancellationToken)
                .ConfigureAwait(false);
            var observed = ImportProgressObservation.From(next);
            if (observed.HasProgressBeyond(lastObserved))
            {
                lastObserved = observed;
                lastProgressAt = _utcNow();
            }

            status = next;
        }

        return status.State switch
        {
            ProductServerImportState.Completed => status,
            ProductServerImportState.Cancelled => throw new OperationCanceledException(
                "Service-owned server import was cancelled."),
            _ => throw new InvalidDataException(
                $"Service-owned server import failed ({status.ErrorCode ?? "import.failed"}): " +
                (status.ErrorMessage ?? "No diagnostic detail was returned.")),
        };
    }

    private static bool IsResumeRequired(ProductServerImportStatus status)
        => string.Equals(
            status.ErrorCode,
            "import.resume_required",
            StringComparison.OrdinalIgnoreCase);

    private static TimeSpan ElapsedSince(DateTimeOffset startedAt, DateTimeOffset now)
        => now > startedAt ? now - startedAt : TimeSpan.Zero;

    private static TimeSpan MinPositive(TimeSpan first, TimeSpan second)
    {
        if (second <= TimeSpan.Zero)
        {
            return TimeSpan.FromTicks(1);
        }

        return first <= second ? first : second;
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes:0.#} minutes"
            : $"{Math.Max(1, Math.Ceiling(duration.TotalSeconds)):0} seconds";

    private readonly record struct ImportProgressObservation(
        ProductServerImportState State,
        long CompletedBytes,
        int CompletedFiles,
        DateTimeOffset UpdatedAtUtc)
    {
        public static ImportProgressObservation From(ProductServerImportStatus status)
            => new(
                status.State,
                status.CompletedBytes,
                status.CompletedFiles,
                status.UpdatedAtUtc);

        public bool HasProgressBeyond(ImportProgressObservation previous)
            => State != previous.State
               || CompletedBytes > previous.CompletedBytes
               || CompletedFiles > previous.CompletedFiles
               || UpdatedAtUtc > previous.UpdatedAtUtc;
    }

    private async Task TryCancelAsync(Guid importId)
    {
        if (importId == Guid.Empty)
        {
            return;
        }

        try
        {
            _ = await _client.CancelImportAsync(importId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Promotion may already have crossed its atomic boundary, or the Service may have
            // disconnected. Its durable journal completes or resumes independently.
        }
    }

    private static ProductServerImportDefinition CreateDefinition(
        ServerInstance instance,
        string sourceRoot,
        string runtimeRoot,
        string javaExecutable)
    {
        var jar = instance.LaunchKind == ServerLaunchKind.ExecutableJar
            ? MapServerRelativePath(sourceRoot, instance.ServerJarPath, "server JAR")
            : string.IsNullOrWhiteSpace(instance.ServerJarPath)
                ? "server.jar"
                : MapServerRelativePath(sourceRoot, instance.ServerJarPath, "server JAR");
        var argumentFiles = instance.JavaArgumentFilePaths
            .Select(path => MapServerRelativePath(sourceRoot, path, "Java argument file"))
            .ToArray();
        var javaRelative = NormalizeRelativePath(Path.GetRelativePath(runtimeRoot, javaExecutable));
        ValidateRelativePath(javaRelative, "Java executable");
        return new ProductServerImportDefinition
        {
            ServerId = instance.Id,
            Name = instance.Name,
            LaunchKind = (ProductServerLaunchKind)instance.LaunchKind,
            ServerJarPath = jar,
            JavaArgumentFilePaths = argumentFiles,
            JavaExecutablePath = javaRelative,
            CoreType = instance.CoreType.ToString(),
            MinecraftVersion = instance.MinecraftVersion,
            MinimumMemoryMb = instance.MinimumMemoryMb,
            MaximumMemoryMb = instance.MaximumMemoryMb,
            JvmArguments = instance.JvmArguments.ToArray(),
            ServerArguments = instance.ServerArguments.ToArray(),
            StopCommand = instance.StopCommand,
            Port = instance.Port,
            AutoRestart = instance.AutoRestart,
            ModpackProviderId = instance.ModpackProviderId,
            ModpackSource = (ProductModpackSourceKind)instance.ModpackSource,
            ModpackProjectId = instance.ModpackProjectId,
            ModpackVersionId = instance.ModpackVersionId,
            ModpackVersionName = instance.ModpackVersionName,
            IsInstallerArtifact = instance.IsInstallerArtifact,
        };
    }

    private static string MapServerRelativePath(string root, string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"The {label} path is missing.");
        }

        var full = Path.IsPathFullyQualified(value)
            ? Path.GetFullPath(value)
            : SafePath.EnsureWithinRoot(root, value, allowRoot: false);
        if (!SafePath.IsWithinRoot(root, full))
        {
            throw new InvalidDataException($"The {label} path is outside the server directory.");
        }

        SafePath.EnsureNoReparsePointsUnderRoot(root, full);
        var relative = NormalizeRelativePath(Path.GetRelativePath(root, full));
        ValidateRelativePath(relative, label);
        return relative;
    }

    private static string ResolveJavaExecutable(ServerInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.JavaExecutablePath))
        {
            throw new FileNotFoundException(
                "A concrete managed or bundled Java executable is required before Service migration.");
        }

        var path = Path.GetFullPath(instance.JavaExecutablePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected Java executable was not found.", path);
        }

        RejectReparse(path);
        return path;
    }

    private static string ResolveRuntimeRoot(string javaExecutable)
    {
        var bin = Directory.GetParent(javaExecutable)
                  ?? throw new InvalidDataException("Java executable has no parent directory.");
        var home = bin.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)
            ? bin.Parent?.FullName
            : bin.FullName;
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
        {
            throw new DirectoryNotFoundException("Java runtime root was not found.");
        }

        return RequireSafeSourceDirectory(home);
    }

    private static string RequireSafeSourceDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Import source directory must be an absolute path.");
        }

        var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("Import source directory was not found: " + root);
        }

        RejectReparse(root);
        return root;
    }

    private string ValidateServiceStaging(ProductServerImportStatus status)
    {
        var staging = Path.GetFullPath(status.StagingDirectory!);
        var parent = Path.GetDirectoryName(staging);
        if (!Path.GetFileName(staging).Equals(status.ImportId.ToString("N"), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(parent) ||
            !Path.GetFullPath(parent).Equals(
                _authorizedImportsRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Service returned an invalid import staging capability.");
        }

        SafePath.EnsureNoReparsePointsUnderRoot(_authorizedImportsRoot, staging);
        return staging;
    }

    private static async Task ResetPayloadAsync(
        string staging,
        string serverPayload,
        string runtimePayload)
    {
        foreach (var path in new[] { serverPayload, runtimePayload })
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                        staging,
                        path,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            Directory.CreateDirectory(path);
            SafePath.EnsureNoReparsePointsUnderRoot(staging, path);
        }

        var manifest = Path.Combine(staging, "manifest.v1.json");
        if (File.Exists(manifest))
        {
            RejectReparse(manifest);
            File.Delete(manifest);
        }
    }

    private static async Task CopyTreeAsync(
        string sourceRoot,
        string destinationRoot,
        string manifestPrefix,
        List<ProductServerImportManifestEntry> entries,
        Action<long> addBytes,
        CancellationToken cancellationToken)
    {
        foreach (var source in EnumerateFilesNoFollow(sourceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= MaximumFiles)
            {
                throw new InvalidDataException("Service import exceeds its file-count limit.");
            }

            var relative = NormalizeRelativePath(Path.GetRelativePath(sourceRoot, source));
            ValidateRelativePath(relative, "payload file");
            var destination = SafePath.EnsureWithinRoot(destinationRoot, relative, allowRoot: false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            SafePath.EnsureNoReparsePointsUnderRoot(destinationRoot, Path.GetDirectoryName(destination)!);
            var sourceInfo = new FileInfo(source);
            addBytes(sourceInfo.Length);
            await CopyFileAsync(source, destination, cancellationToken).ConfigureAwait(false);
            var hash = await HashFileAsync(destination, cancellationToken).ConfigureAwait(false);
            entries.Add(new ProductServerImportManifestEntry(
                $"{manifestPrefix}/{relative}",
                sourceInfo.Length,
                Convert.ToHexString(hash)));
        }
    }

    private static void AddImportBytes(ref long totalBytes, long value)
    {
        var next = checked(totalBytes + value);
        if (next > MaximumBytes)
        {
            throw new InvalidDataException("Service import exceeds its total-byte limit.");
        }

        totalBytes = next;
    }

    private static IEnumerable<string> EnumerateFilesNoFollow(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectReparse(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Import source cannot contain a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private static async Task CopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task WriteManifestAsync(
        string path,
        ProductServerImportManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             32 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateRelativePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Contains('\\') ||
            Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException($"The {label} relative path is invalid.");
        }

        foreach (var segment in path.Split('/'))
        {
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException($"The {label} relative path is unsafe.");
            }
        }
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Import source or staging path cannot be a reparse point.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
    };
}
