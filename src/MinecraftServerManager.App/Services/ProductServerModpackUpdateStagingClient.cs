using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.Client;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Copies an already-built local modpack candidate only into the exact staging capability issued
/// by the Windows Service. The Service remains the sole registry/runtime writer and independently
/// re-verifies the manifest before it mutates the live server.
/// </summary>
internal sealed class ProductServerModpackUpdateStagingClient
{
    private const int MaximumFiles = 100_000;
    private const long MaximumBytes = 1024L * 1024 * 1024 * 1024;
    private const string ManifestFileName = "manifest.v1.json";
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CancellationAttemptTimeout = TimeSpan.FromSeconds(5);
    private readonly IProductServiceClient _client;
    private readonly string _authorizedImportsRoot;
    private readonly TimeSpan _pollInterval;

    public ProductServerModpackUpdateStagingClient(
        IProductServiceClient client,
        string? authorizedImportsRoot = null,
        TimeSpan? pollInterval = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _authorizedImportsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            authorizedImportsRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Muhun",
                "MCSV",
                "imports")));
        _pollInterval = pollInterval ?? DefaultPollInterval;
        if (_pollInterval <= TimeSpan.Zero || _pollInterval > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval),
                "The modpack update polling interval is outside its safe bounds.");
        }
    }

    public async Task<ProductServerModpackUpdateStatus> UpdateAsync(
        ServerInstance candidate,
        Guid serverId,
        string expectedCurrentVersionId,
        ProductServerModpackUpdateDefinition target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(target);
        var sourceRoot = RequireSafeSourceDirectory(candidate.DirectoryPath);
        ValidateCandidate(sourceRoot, target);
        var request = new ProductServerModpackUpdateBeginRequest(
            serverId,
            expectedCurrentVersionId,
            target);
        ProductServerModpackUpdateStatus? status = null;
        try
        {
            status = await BeginAsync(request, cancellationToken).ConfigureAwait(false);
            if (status.State != ProductServerModpackUpdateState.Staging)
            {
                return await AwaitReadyAsync(status, serverId, cancellationToken)
                    .ConfigureAwait(false);
            }

            var staging = ValidateServiceStaging(status);
            RejectOverlappingTrees(sourceRoot, staging);
            var destinationRoot = Path.Combine(staging, "candidate");
            await ResetPayloadAsync(staging, destinationRoot).ConfigureAwait(false);
            var entries = await CopyCandidateAsync(
                    sourceRoot,
                    destinationRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (entries.Count == 0)
            {
                throw new InvalidDataException("A modpack update candidate cannot be empty.");
            }

            var manifest = new ProductServerModpackUpdateManifest(1, status.UpdateId, entries);
            var manifestPath = Path.Combine(staging, ManifestFileName);
            await WriteManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            var hash = await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            status = await CommitAsync(
                    status.UpdateId,
                    Convert.ToHexString(hash),
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateStatus(status, status.UpdateId, serverId);
            return await AwaitReadyAsync(status, serverId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryCancelAsync(status?.UpdateId ?? Guid.Empty).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TryCancelAsync(status?.UpdateId ?? Guid.Empty).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ProductServerModpackUpdateStatus> BeginAsync(
        ProductServerModpackUpdateBeginRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateBeginRequest(request);
        var status = await _client.BeginModpackUpdateAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ValidateStatus(status, expectedUpdateId: null, request.ServerId);
        return status;
    }

    public async Task<ProductServerModpackUpdateStatus> CommitAsync(
        Guid updateId,
        string manifestSha256,
        CancellationToken cancellationToken = default)
    {
        ValidateUpdateId(updateId);
        _ = ParseSha256(manifestSha256);
        var status = await _client.CommitModpackUpdateAsync(
                updateId,
                manifestSha256,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateStatus(status, updateId, expectedServerId: null);
        return status;
    }

    public async Task<ProductServerModpackUpdateStatus> GetStatusAsync(
        Guid updateId,
        CancellationToken cancellationToken = default)
    {
        ValidateUpdateId(updateId);
        var status = await _client.GetModpackUpdateStatusAsync(updateId, cancellationToken)
            .ConfigureAwait(false);
        ValidateStatus(status, updateId, expectedServerId: null);
        return status;
    }

    public async Task<ProductServerModpackUpdateStatus> CancelAsync(
        Guid updateId,
        CancellationToken cancellationToken = default)
    {
        ValidateUpdateId(updateId);
        var status = await _client.CancelModpackUpdateAsync(updateId, cancellationToken)
            .ConfigureAwait(false);
        ValidateStatus(status, updateId, expectedServerId: null);
        return status;
    }

    private async Task<ProductServerModpackUpdateStatus> AwaitReadyAsync(
        ProductServerModpackUpdateStatus status,
        Guid expectedServerId,
        CancellationToken cancellationToken)
    {
        while (status.State is not ProductServerModpackUpdateState.AwaitingHealth
               and not ProductServerModpackUpdateState.HealthyAwaitingStop &&
               !status.IsTerminal)
        {
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            status = await GetStatusAsync(status.UpdateId, cancellationToken).ConfigureAwait(false);
            ValidateStatus(status, status.UpdateId, expectedServerId);
        }

        return status.State switch
        {
            ProductServerModpackUpdateState.AwaitingHealth or
            ProductServerModpackUpdateState.HealthyAwaitingStop or
            ProductServerModpackUpdateState.Completed => status,
            ProductServerModpackUpdateState.Cancelled => throw new OperationCanceledException(
                "Service-owned modpack update staging was cancelled."),
            _ => throw new InvalidDataException(
                $"Service-owned modpack update failed ({status.ErrorCode ?? "modpack_update.failed"}): " +
                (status.ErrorMessage ?? "No diagnostic detail was returned.")),
        };
    }

    private async Task TryCancelAsync(Guid updateId)
    {
        if (updateId == Guid.Empty)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(CancellationAttemptTimeout);
            _ = await CancelAsync(updateId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A Service-owned commit may already have crossed its atomic boundary, or the pipe
            // may have disconnected. The durable Service journal remains authoritative.
        }
    }

    private string ValidateServiceStaging(ProductServerModpackUpdateStatus status)
    {
        if (status.State != ProductServerModpackUpdateState.Staging ||
            string.IsNullOrWhiteSpace(status.StagingDirectory))
        {
            throw new InvalidDataException("Service did not return a modpack staging capability.");
        }

        var staging = Path.GetFullPath(status.StagingDirectory);
        var updateRoot = Path.Combine(_authorizedImportsRoot, "modpack-updates");
        var parent = Path.GetDirectoryName(staging);
        if (!Path.GetFileName(staging).Equals(
                status.UpdateId.ToString("N"),
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(parent) ||
            !Path.GetFullPath(parent).Equals(updateRoot, PathComparison))
        {
            throw new InvalidDataException("Service returned an invalid modpack staging capability.");
        }

        SafePath.EnsureNoReparsePointsUnderRoot(_authorizedImportsRoot, staging);
        return staging;
    }

    private static async Task ResetPayloadAsync(string staging, string destinationRoot)
    {
        if (Directory.Exists(destinationRoot) || File.Exists(destinationRoot))
        {
            RejectReparse(destinationRoot);
            await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                    staging,
                    destinationRoot,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        Directory.CreateDirectory(destinationRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(staging, destinationRoot);
        var manifest = Path.Combine(staging, ManifestFileName);
        if (Directory.Exists(manifest))
        {
            throw new InvalidDataException("The modpack update manifest path is not a file.");
        }

        if (File.Exists(manifest))
        {
            RejectReparse(manifest);
            File.Delete(manifest);
        }
    }

    private static async Task<IReadOnlyList<ProductServerModpackUpdateManifestEntry>> CopyCandidateAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var entries = new List<ProductServerModpackUpdateManifestEntry>();
        long totalBytes = 0;
        foreach (var source in EnumerateFilesNoFollow(sourceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= MaximumFiles)
            {
                throw new InvalidDataException("Modpack update exceeds its file-count limit.");
            }

            var relative = NormalizeRelativePath(Path.GetRelativePath(sourceRoot, source));
            ValidateRelativePath(relative, "candidate file");
            var destination = SafePath.EnsureWithinRoot(destinationRoot, relative, allowRoot: false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            SafePath.EnsureNoReparsePointsUnderRoot(
                destinationRoot,
                Path.GetDirectoryName(destination)!);
            await using var lease = ProductNoFollowSourceFileReader.Open(sourceRoot, source);
            totalBytes = AddBytes(totalBytes, lease.Length);
            var copied = await CopyAndHashAsync(lease.Stream, destination, cancellationToken)
                .ConfigureAwait(false);
            if (copied.Length != lease.Length)
            {
                throw new InvalidDataException("Candidate file length changed while it was staged.");
            }

            entries.Add(new ProductServerModpackUpdateManifestEntry(
                relative,
                copied.Length,
                Convert.ToHexString(copied.Hash)));
        }

        return entries.AsReadOnly();
    }

    private static async Task<(long Length, byte[] Hash)> CopyAndHashAsync(
        Stream source,
        string destination,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length = checked(length + read);
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            return (length, hash.GetHashAndReset());
        }
        catch
        {
            try
            {
                File.Delete(destination);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
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
                    throw new InvalidDataException("Modpack candidate cannot contain a reparse point.");
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

    private static async Task WriteManifestAsync(
        string path,
        ProductServerModpackUpdateManifest manifest,
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

    private static void ValidateBeginRequest(ProductServerModpackUpdateBeginRequest request)
    {
        if (request.ServerId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty Service server id is required.", nameof(request));
        }

        ValidateText(request.ExpectedCurrentVersionId, 256, "current modpack version");
        ArgumentNullException.ThrowIfNull(request.Target);
        if (!Enum.IsDefined(request.Target.LaunchKind) || !Enum.IsDefined(request.Target.ModpackSource))
        {
            throw new InvalidDataException("The modpack update definition contains an unknown enum value.");
        }

        ValidateText(request.Target.CoreType, 64, "core type");
        ValidateText(request.Target.ModpackProjectId, 256, "modpack project id");
        ValidateText(request.Target.ModpackVersionId, 256, "target modpack version id");
        ValidateText(request.Target.ModpackVersionName, 256, "target modpack version name");
        if (request.Target.ServerArguments is null || request.Target.ServerArguments.Count > 128 ||
            request.Target.JavaArgumentFilePaths is null || request.Target.JavaArgumentFilePaths.Count > 128)
        {
            throw new InvalidDataException("The modpack update definition exceeds its argument limits.");
        }

        if (request.Target.LaunchKind == ProductServerLaunchKind.ExecutableJar)
        {
            ValidateRelativePath(request.Target.ServerJarPath, "server JAR");
            if (!Path.GetExtension(request.Target.ServerJarPath)
                    .Equals(".jar", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("An executable modpack candidate requires a JAR path.");
            }
        }
        else if (!string.IsNullOrEmpty(request.Target.ServerJarPath))
        {
            throw new InvalidDataException("A Java argument-file candidate cannot contain a server JAR path.");
        }

        foreach (var path in request.Target.JavaArgumentFilePaths)
        {
            ValidateRelativePath(path, "Java argument file");
        }

        if (request.Target.LaunchKind == ProductServerLaunchKind.JavaArgumentFiles &&
            request.Target.JavaArgumentFilePaths.Count == 0)
        {
            throw new InvalidDataException("A Java argument-file candidate requires at least one file.");
        }

        foreach (var argument in request.Target.ServerArguments)
        {
            ValidateText(argument, 2048, "server argument");
        }
    }

    private static void ValidateCandidate(
        string sourceRoot,
        ProductServerModpackUpdateDefinition target)
    {
        var probe = new ProductServerModpackUpdateBeginRequest(
            Guid.NewGuid(),
            "candidate",
            target);
        ValidateBeginRequest(probe);
        if (target.LaunchKind == ProductServerLaunchKind.ExecutableJar)
        {
            RequireCandidateFile(sourceRoot, target.ServerJarPath, "server JAR");
        }

        foreach (var path in target.JavaArgumentFilePaths)
        {
            RequireCandidateFile(sourceRoot, path, "Java argument file");
        }
    }

    private static void RequireCandidateFile(string sourceRoot, string relativePath, string label)
    {
        var path = SafePath.EnsureWithinRoot(sourceRoot, relativePath, allowRoot: false);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The candidate {label} was not found.", path);
        }

        SafePath.EnsureNoReparsePointsUnderRoot(sourceRoot, path);
    }

    private static string RequireSafeSourceDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Modpack candidate directory must be an absolute path.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("Modpack candidate directory was not found: " + root);
        }

        RejectReparse(root);
        return root;
    }

    private static void RejectOverlappingTrees(string sourceRoot, string staging)
    {
        if (SafePath.IsWithinRoot(sourceRoot, staging) || SafePath.IsWithinRoot(staging, sourceRoot))
        {
            throw new InvalidDataException("Modpack source and Service staging directories cannot overlap.");
        }
    }

    private static long AddBytes(long current, long value)
    {
        var next = checked(current + value);
        if (value < 0 || next > MaximumBytes)
        {
            throw new InvalidDataException("Modpack update exceeds its total-byte limit.");
        }

        return next;
    }

    private static void ValidateStatus(
        ProductServerModpackUpdateStatus status,
        Guid? expectedUpdateId,
        Guid? expectedServerId)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.UpdateId == Guid.Empty || status.ServerId == Guid.Empty ||
            (expectedUpdateId is { } updateId && status.UpdateId != updateId) ||
            (expectedServerId is { } serverId && status.ServerId != serverId) ||
            !Enum.IsDefined(status.State) ||
            status.TotalBytes < 0 || status.CompletedBytes < 0 ||
            status.CompletedBytes > status.TotalBytes ||
            status.TotalFiles < 0 || status.CompletedFiles < 0 ||
            status.CompletedFiles > status.TotalFiles ||
            (status.State == ProductServerModpackUpdateState.Staging) !=
            !string.IsNullOrWhiteSpace(status.StagingDirectory))
        {
            throw new InvalidDataException("Service returned an invalid modpack update status.");
        }
    }

    private static void ValidateUpdateId(Guid updateId)
    {
        if (updateId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty modpack update id is required.", nameof(updateId));
        }
    }

    private static byte[] ParseSha256(string value)
    {
        if (value is null || value.Length != 64)
        {
            throw new ArgumentException("A SHA-256 manifest hash is required.", nameof(value));
        }

        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException error)
        {
            throw new ArgumentException("A SHA-256 manifest hash is required.", nameof(value), error);
        }
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

    private static void ValidateText(string value, int maximumLength, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new InvalidDataException($"The {label} is invalid.");
        }
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Modpack source or staging path cannot be a reparse point.");
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
    };
}
