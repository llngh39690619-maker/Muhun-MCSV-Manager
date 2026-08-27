using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Notifications;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

/// <summary>
/// Owns backup paths and restore commits inside the Windows Service trust boundary. Public callers
/// exchange opaque ids only; a client-supplied path can therefore never become a read or restore
/// target. Every mutation is serialized with the server runtime and requires a fully stopped
/// process so the resulting archive and directory swap are point-in-time operations.
/// </summary>
public sealed class ProductServerBackupManager(
    ProductDataLayout layout,
    ProductServerRuntime runtime,
    BackupService backupService,
    ProductNotificationPublisher? notifications = null,
    TimeProvider? timeProvider = null)
{
    public const int MaximumBackupsPerServer = 2_000;
    public const int MaximumPageSize = 50;
    private readonly BackupRestoreService _restoreService = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public ProductServerBackupPage List(Guid serverId, int offset, int limit)
    {
        ValidatePage(offset, limit);
        _ = runtime.GetRegistration(serverId);
        var backups = EnumerateBackups(serverId);
        if (offset > backups.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var page = backups.Skip(offset).Take(limit).ToArray();
        var next = checked(offset + page.Length);
        return new ProductServerBackupPage(
            serverId,
            offset,
            next,
            next < backups.Count,
            page);
    }

    public async Task<ProductServerBackupMutationResult> CreateAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var registration = runtime.GetRegistration(serverId);
        var startedAtUtc = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var gate = _gates.GetOrAdd(serverId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await runtime.ExecuteExplicitlyStoppedMutationAsync(
                    serverId,
                    async (registration, token) =>
                    {
                        var source = ResolveServerDirectory(registration);
                        await using var lease = ServerDirectoryLease.Acquire(source);
                        var destination = ResolveBackupDirectory(serverId, create: true);
                        var result = await backupService.CreateBackupAsync(
                                source,
                                registration.Name,
                                new BackupOptions { DestinationDirectory = destination },
                                progress: null,
                                token)
                            .ConfigureAwait(false);
                        var summary = ToSummary(serverId, new FileInfo(result.ArchivePath));
                        return new ProductServerBackupMutationResult(
                            serverId,
                            summary,
                            result.CompletedAtUtc);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await PublishCompletedAsync(registration, result, startedAtUtc).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            await PublishFailedAsync(registration, "unavailable", error).ConfigureAwait(false);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ProductServerBackupRestoreResult> RestoreAsync(
        Guid serverId,
        string backupId,
        CancellationToken cancellationToken = default)
    {
        ValidateBackupId(backupId);
        var registration = runtime.GetRegistration(serverId);
        var startedAtUtc = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var gate = _gates.GetOrAdd(serverId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await runtime.ExecuteExplicitlyStoppedMutationAsync(
                    serverId,
                    (registration, token) => RestoreStoppedAsync(
                        registration,
                        backupId,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            await PublishRestoredAsync(registration, result, startedAtUtc).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            await PublishFailedAsync(registration, backupId, error).ConfigureAwait(false);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ProductServerBackupRestoreResult> RestoreStoppedAsync(
        ProductServerRegistration registration,
        string backupId,
        CancellationToken cancellationToken)
    {
        var selected = EnumerateBackups(registration.Id)
            .SingleOrDefault(item =>
                string.Equals(item.BackupId, backupId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("The selected Service-owned backup was not found.");
        var backupDirectory = ResolveBackupDirectory(registration.Id, create: false);
        var archivePath = SafePath.EnsureWithinRoot(
            backupDirectory,
            Path.Combine(backupDirectory, selected.FileName),
            allowRoot: false);
        SafePath.EnsureNoReparsePointsUnderRoot(backupDirectory, archivePath);

        var source = ResolveServerDirectory(registration);
        var sourceParent = Directory.GetParent(source)?.FullName
            ?? throw new InvalidDataException("The registered server directory has no parent.");
        if (!PathsEqual(sourceParent, layout.Servers))
        {
            // Registrations may contain nested paths. A restore sibling must remain inside the
            // same owned parent so the final Directory.Move is a same-volume atomic rename.
            SafePath.EnsureNoReparsePointsUnderRoot(layout.Servers, sourceParent);
        }

        await using (var lease = ServerDirectoryLease.Acquire(source))
        {
            // Retain the current state as another immutable ZIP before replacing anything. This
            // also proves that the complete source tree can be read without traversing a link.
            await backupService.CreateBackupAsync(
                    source,
                    registration.Name,
                    new BackupOptions
                    {
                        DestinationDirectory = backupDirectory,
                        ArchiveFileName = $"pre-restore-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip",
                    },
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var stagingName = $".{Path.GetFileName(source)}.restore-{Guid.NewGuid():N}";
        var staging = SafePath.EnsureWithinRoot(
            layout.Servers,
            Path.Combine(sourceParent, stagingName),
            allowRoot: false);
        var rollbackDirectory = SafePath.EnsureWithinRoot(
            backupDirectory,
            Path.Combine(
                backupDirectory,
                $".restore-rollback-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"),
            allowRoot: false);

        try
        {
            await _restoreService.RestoreAsync(
                    archivePath,
                    staging,
                    new BackupRestoreOptions { TrustedDestinationRoot = layout.Servers },
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateRestoredLaunchFiles(registration, staging);
        }
        catch
        {
            await DeleteServiceOwnedTreeBestEffortAsync(layout.Servers, staging)
                .ConfigureAwait(false);
            throw;
        }

        var oldMoved = false;
        var replacementCommitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafePath.EnsureNoReparsePointsUnderRoot(layout.Servers, source);
            SafePath.EnsureTreeContainsNoReparsePoints(staging);
            Directory.Move(source, rollbackDirectory);
            oldMoved = true;
            Directory.Move(staging, source);
            replacementCommitted = true;
        }
        catch
        {
            if (oldMoved && !replacementCommitted &&
                !Directory.Exists(source) && Directory.Exists(rollbackDirectory))
            {
                Directory.Move(rollbackDirectory, source);
            }

            await DeleteServiceOwnedTreeBestEffortAsync(layout.Servers, staging)
                .ConfigureAwait(false);

            throw;
        }

        // The pre-restore ZIP above is the durable rollback artifact. Cleanup is no-follow and
        // bounded; a transient cleanup failure deliberately leaves the dot-prefixed directory
        // for an administrator rather than jeopardizing the successful restore commit.
        await DeleteServiceOwnedTreeBestEffortAsync(backupDirectory, rollbackDirectory)
            .ConfigureAwait(false);

        return new ProductServerBackupRestoreResult(
            registration.Id,
            selected.BackupId,
            DateTimeOffset.UtcNow);
    }

    private IReadOnlyList<ProductServerBackupSummary> EnumerateBackups(Guid serverId)
    {
        var directory = ResolveBackupDirectory(serverId, create: false);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        SafePath.EnsureNoReparsePointsUnderRoot(layout.Backups, directory);
        var files = Directory.EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)
            .Take(MaximumBackupsPerServer + 1)
            .Select(path => new FileInfo(path))
            .ToArray();
        if (files.Length > MaximumBackupsPerServer)
        {
            throw new InvalidDataException("The server backup catalog exceeds its safety limit.");
        }

        foreach (var file in files)
        {
            SafePath.EnsureNoReparsePointsUnderRoot(directory, file.FullName);
            if (file.Name.Length is < 5 or > 240 || file.Length < 1)
            {
                throw new InvalidDataException("The Service-owned backup catalog is invalid.");
            }
        }

        return files
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => ToSummary(serverId, file))
            .ToArray();
    }

    private string ResolveServerDirectory(ProductServerRegistration registration)
    {
        var source = ProductServerRegistrationValidator.ResolveOwnedPath(
            layout.Servers,
            registration.ServerDirectory,
            allowRoot: false);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("The Service-owned server directory was not found.");
        }

        return SafePath.EnsureNoReparsePointsUnderRoot(layout.Servers, source);
    }

    private string ResolveBackupDirectory(Guid serverId, bool create)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(serverId));
        }

        if (create)
        {
            Directory.CreateDirectory(layout.Backups);
        }

        var directory = SafePath.EnsureWithinRoot(
            layout.Backups,
            Path.Combine(layout.Backups, serverId.ToString("D")),
            allowRoot: false);
        if (create)
        {
            Directory.CreateDirectory(directory);
            SafePath.EnsureNoReparsePointsUnderRoot(layout.Backups, directory);
        }

        return directory;
    }

    private static ProductServerBackupSummary ToSummary(Guid serverId, FileInfo file)
        => new(
            CreateBackupId(serverId, file.Name),
            file.Name,
            file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));

    private async Task PublishCompletedAsync(
        ProductServerRegistration registration,
        ProductServerBackupMutationResult result,
        DateTimeOffset startedAtUtc)
    {
        if (notifications is null) return;
        await PublishBestEffortAsync(new ProductNotificationEvent(
            "backup.completed",
            ProductEventSeverity.Information,
            "Notification.Backup.Completed",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["server_name"] = registration.Name,
                ["backup_id"] = result.Backup.BackupId,
                ["size_bytes"] = result.Backup.ArchiveBytes.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["duration_ms"] = DurationMilliseconds(startedAtUtc),
            },
            result.CompletedAtUtc,
            registration.Id)).ConfigureAwait(false);
    }

    private async Task PublishRestoredAsync(
        ProductServerRegistration registration,
        ProductServerBackupRestoreResult result,
        DateTimeOffset startedAtUtc)
    {
        if (notifications is null) return;
        await PublishBestEffortAsync(new ProductNotificationEvent(
            "backup.restored",
            ProductEventSeverity.Warning,
            "Notification.Backup.Restored",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["server_name"] = registration.Name,
                ["backup_id"] = result.BackupId,
                ["duration_ms"] = DurationMilliseconds(startedAtUtc),
            },
            result.CompletedAtUtc,
            registration.Id)).ConfigureAwait(false);
    }

    private async Task PublishFailedAsync(
        ProductServerRegistration registration,
        string backupId,
        Exception error)
    {
        if (notifications is null) return;
        await PublishBestEffortAsync(new ProductNotificationEvent(
            "backup.failed",
            ProductEventSeverity.Error,
            "Notification.Backup.Failed",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["server_name"] = registration.Name,
                ["backup_id"] = backupId,
                ["failure_code"] = MapFailureCode(error),
            },
            (timeProvider ?? TimeProvider.System).GetUtcNow(),
            registration.Id)).ConfigureAwait(false);
    }

    private async Task PublishBestEffortAsync(ProductNotificationEvent notification)
    {
        try
        {
            await notifications!.PublishAsync(notification, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // The primary filesystem result remains authoritative; a notification storage outage
            // must never make a committed backup or restore appear safe to retry.
        }
    }

    private string DurationMilliseconds(DateTimeOffset startedAtUtc)
        => Math.Max(
                0,
                ((timeProvider ?? TimeProvider.System).GetUtcNow() - startedAtUtc).TotalMilliseconds)
            .ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    private static string MapFailureCode(Exception error) => error switch
    {
        UnauthorizedAccessException => "backup.access_denied",
        DirectoryNotFoundException or FileNotFoundException => "backup.not_found",
        InvalidDataException => "backup.invalid_data",
        IOException => "backup.io_failed",
        InvalidOperationException => "backup.precondition_failed",
        _ => "backup.failed",
    };

    private static string CreateBackupId(Guid serverId, string fileName)
    {
        var material = Encoding.UTF8.GetBytes($"{serverId:D}\n{fileName}");
        return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }

    private static void ValidateBackupId(string backupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        if (backupId.Length != 64 || backupId.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Backup id is invalid.", nameof(backupId));
        }
    }

    private static void ValidatePage(int offset, int limit)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (limit is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
    }

    private static void ValidateRestoredLaunchFiles(
        ProductServerRegistration registration,
        string restoredRoot)
    {
        SafePath.EnsureTreeContainsNoReparsePoints(restoredRoot);
        if (registration.LaunchKind == ProductServerLaunchKind.ExecutableJar)
        {
            var jar = SafePath.EnsureWithinRoot(
                restoredRoot,
                Path.Combine(restoredRoot, registration.ServerJarPath),
                allowRoot: false);
            if (!File.Exists(jar))
            {
                throw new InvalidDataException("The backup does not contain the configured server JAR.");
            }

            return;
        }

        foreach (var relativePath in registration.JavaArgumentFilePaths)
        {
            var argumentFile = SafePath.EnsureWithinRoot(
                restoredRoot,
                Path.Combine(restoredRoot, relativePath),
                allowRoot: false);
            if (!File.Exists(argumentFile))
            {
                throw new InvalidDataException(
                    "The backup does not contain every configured Java argument file.");
            }
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task DeleteServiceOwnedTreeBestEffortAsync(string root, string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                    root,
                    path,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A successful commit must not be reported as failed because old-tree cleanup was
            // temporarily blocked. On pre-commit failure this can leave only the exact hidden
            // Service-created staging directory, never an operator-provided path.
            _ = error;
        }
    }
}
