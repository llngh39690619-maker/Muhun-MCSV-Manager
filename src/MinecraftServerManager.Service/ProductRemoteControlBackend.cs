using MinecraftServerManager.Contracts;
using MinecraftServerManager.Remote;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.Service;

/// <summary>
/// Remote Web adapter backed only by the Service-owned runtime. No WPF dispatcher, desktop
/// lifetime, or caller-provided filesystem path participates in a remote request.
/// </summary>
public sealed class ProductRemoteControlBackend(
    ProductServerRuntime runtime,
    ProductServerRegistry registry,
    ProductPlayerPresenceTracker playerTracker,
    ProductServerBackupManager backupManager,
    IProductUpdateCoordinator? updates = null,
    ProductServerAdministrationReader? administrationReader = null) : IRemoteControlBackend, IDisposable
{

    public ValueTask<RemoteDashboardDto> GetDashboardAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var servers = runtime.List()
            .Select(summary => MapSummary(runtime.GetStatus(summary.Id)))
            .ToArray();
        return ValueTask.FromResult(new RemoteDashboardDto(DateTimeOffset.UtcNow, servers));
    }

    public ValueTask<RemoteServerDetailDto?> GetServerAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseServerId(serverId, out var id) || !registry.TryGet(id, out _))
        {
            return ValueTask.FromResult<RemoteServerDetailDto?>(null);
        }

        var java = administrationReader?.CaptureJava(id, cancellationToken);
        var detail = new RemoteServerDetailDto(
            MapSummary(runtime.GetStatus(id)),
            JavaVersion: CreateJavaDisplayName(java),
            SupportsPlayerManagement: true,
            SupportsBackups: true,
            HasDiagnosticConsole: true);
        return ValueTask.FromResult<RemoteServerDetailDto?>(detail);
    }

    public ValueTask<RemoteServerAdministrationDto?> GetServerAdministrationAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseServerId(serverId, out var id) || !registry.TryGet(id, out _) ||
            administrationReader?.Capture(id, cancellationToken) is not { } snapshot)
        {
            return ValueTask.FromResult<RemoteServerAdministrationDto?>(null);
        }

        var response = new RemoteServerAdministrationDto(
            snapshot.CapturedAtUtc,
            snapshot.AddonsAvailable,
            snapshot.Addons.Select(static addon => new RemoteServerAddonDto(
                addon.Kind == ProductServerAddonKind.Plugin
                    ? RemoteServerAddonKind.Plugin
                    : RemoteServerAddonKind.Mod,
                addon.FileName,
                addon.SizeBytes)).ToArray(),
            snapshot.AddonsTruncated,
            MapJava(snapshot.Java));
        return ValueTask.FromResult<RemoteServerAdministrationDto?>(response);
    }

    public ValueTask<RemoteConsolePageDto?> GetConsoleAsync(
        string serverId,
        RemoteConsoleQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseServerId(serverId, out var id) || !registry.TryGet(id, out _))
        {
            return ValueTask.FromResult<RemoteConsolePageDto?>(null);
        }

        var requested = Math.Clamp(query.Limit, 1, 500);
        var cursor = Math.Max(query.After ?? 0, 0);
        var lines = new List<RemoteConsoleLineDto>(requested);
        var underlyingHasMore = false;
        for (var pageNumber = 0; pageNumber < 10 && lines.Count < requested; pageNumber++)
        {
            var page = runtime.ReadConsole(id, cursor, ProductConsoleJournal.MaximumPageSize);
            foreach (var entry in page.Entries)
            {
                cursor = entry.Cursor;
                if (MatchesStream(entry, query.Stream))
                {
                    lines.Add(MapConsoleLine(entry));
                    if (lines.Count == requested)
                    {
                        break;
                    }
                }
            }

            underlyingHasMore = page.Entries.Count == ProductConsoleJournal.MaximumPageSize;
            if (!underlyingHasMore)
            {
                cursor = page.NextCursor;
                break;
            }
        }

        return ValueTask.FromResult<RemoteConsolePageDto?>(new RemoteConsolePageDto(
            lines,
            cursor,
            underlyingHasMore || lines.Count == requested));
    }

    public ValueTask<RemotePlayerListDto?> GetPlayersAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseServerId(serverId, out var id) || !registry.TryGet(id, out _))
        {
            return ValueTask.FromResult<RemotePlayerListDto?>(null);
        }

        // Remote clients poll this endpoint frequently. Keep that path on the in-memory online
        // snapshot; durable registries are loaded only by the explicit desktop IPC request.
        return ValueTask.FromResult<RemotePlayerListDto?>(new RemotePlayerListDto(
            DateTimeOffset.UtcNow,
            playerTracker.GetPlayers(id)));
    }

    public ValueTask<RemoteOperationResultDto> StartServerAsync(
        string serverId,
        CancellationToken cancellationToken)
        => RunServerMutationAsync(serverId, runtime.StartAsync, cancellationToken);

    public ValueTask<RemoteOperationResultDto> StopServerAsync(
        string serverId,
        CancellationToken cancellationToken)
        => RunServerMutationAsync(serverId, runtime.StopAsync, cancellationToken);

    public ValueTask<RemoteOperationResultDto> RestartServerAsync(
        string serverId,
        CancellationToken cancellationToken)
        => RunServerMutationAsync(serverId, runtime.RestartAsync, cancellationToken);

    public async ValueTask<RemoteOperationResultDto> SendConsoleCommandAsync(
        string serverId,
        string command,
        CancellationToken cancellationToken)
    {
        if (!TryParseServerId(serverId, out var id) || !registry.TryGet(id, out _))
        {
            return NotFound();
        }

        try
        {
            await runtime.SendCommandAsync(id, command, cancellationToken).ConfigureAwait(false);
            return Accepted();
        }
        catch (Exception error) when (IsSafeOperationFailure(error, cancellationToken))
        {
            return Rejected(error);
        }
    }

    public ValueTask<RemoteOperationResultDto> PerformPlayerActionAsync(
        string serverId,
        RemotePlayerActionRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!RemoteInputValidator.TryValidatePlayerAction(request, out _))
        {
            return ValueTask.FromResult(new RemoteOperationResultDto(false, "The player action is invalid."));
        }

        return SendConsoleCommandAsync(serverId, CreatePlayerCommand(request), cancellationToken);
    }

    public async ValueTask<RemoteOperationResultDto> CreateBackupAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        if (!TryParseServerId(serverId, out var id) || !registry.TryGet(id, out _))
        {
            return NotFound();
        }

        try
        {
            await backupManager.CreateAsync(id, cancellationToken)
                .ConfigureAwait(false);
            return Accepted();
        }
        catch (Exception error) when (IsSafeOperationFailure(error, cancellationToken))
        {
            return Rejected(error);
        }
    }

    public ValueTask<RemoteBackupListDto?> GetBackupsAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseServerId(serverId, out var id) || !registry.TryGet(id, out _))
        {
            return ValueTask.FromResult<RemoteBackupListDto?>(null);
        }

        var summaries = new List<RemoteBackupSummaryDto>(
            RemoteBackupRestoreContract.MaximumListedBackups);
        var offset = 0;
        var hasMore = false;
        while (summaries.Count < RemoteBackupRestoreContract.MaximumListedBackups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = RemoteBackupRestoreContract.MaximumListedBackups - summaries.Count;
            var page = backupManager.List(
                id,
                offset,
                Math.Min(ProductServerBackupManager.MaximumPageSize, remaining));
            summaries.AddRange(page.Backups.Select(MapBackup));
            hasMore = page.HasMore;
            if (!page.HasMore)
            {
                break;
            }

            if (page.NextOffset <= offset)
            {
                throw new InvalidDataException("The Service-owned backup catalog did not advance.");
            }

            offset = page.NextOffset;
        }

        return ValueTask.FromResult<RemoteBackupListDto?>(new RemoteBackupListDto(
            DateTimeOffset.UtcNow,
            summaries,
            hasMore));
    }

    public async ValueTask<RemoteOperationResultDto> RestoreBackupAsync(
        string serverId,
        string backupId,
        CancellationToken cancellationToken)
    {
        if (!TryParseServerId(serverId, out var id) || !registry.TryGet(id, out _))
        {
            return NotFound();
        }

        try
        {
            await backupManager.RestoreAsync(id, backupId, cancellationToken)
                .ConfigureAwait(false);
            return Accepted();
        }
        catch (Exception error) when (IsSafeOperationFailure(error, cancellationToken))
        {
            return Rejected(error);
        }
    }

    public ValueTask<ProductUpdateStatus> GetProductUpdateStatusAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (updates is null)
        {
            throw new InvalidOperationException("Product update coordinator is unavailable.");
        }

        return ValueTask.FromResult(updates.GetStatus(channel));
    }

    public ValueTask<RemoteOperationResultDto> CheckForProductUpdateAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken)
        => RunUpdateMutationAsync(
            token => updates!.CheckAsync(channel, token),
            cancellationToken);

    public ValueTask<RemoteOperationResultDto> DownloadProductUpdateAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken)
        => RunUpdateMutationAsync(
            token => updates!.DownloadAsync(channel, token),
            cancellationToken);

    public ValueTask<RemoteOperationResultDto> ScheduleProductUpdateAsync(
        ProductUpdateChannel channel,
        DateTimeOffset? notBeforeUtc,
        CancellationToken cancellationToken)
        => RunUpdateMutationAsync(
            token => updates!.ScheduleAsync(channel, notBeforeUtc, token),
            cancellationToken);

    public void Dispose()
    {
        // Dependencies are DI-owned singletons; this adapter has no independent resources.
    }

    internal static string CreatePlayerCommand(RemotePlayerActionRequestDto request)
    {
        var player = request.PlayerName;
        return request.Action switch
        {
            RemotePlayerActionKind.Kick => WithOptionalReason($"kick {player}", request.Reason),
            RemotePlayerActionKind.Ban => WithOptionalReason($"ban {player}", request.Reason),
            RemotePlayerActionKind.Pardon => $"pardon {player}",
            RemotePlayerActionKind.Op => $"op {player}",
            RemotePlayerActionKind.Deop => $"deop {player}",
            RemotePlayerActionKind.WhitelistAdd => $"whitelist add {player}",
            RemotePlayerActionKind.WhitelistRemove => $"whitelist remove {player}",
            RemotePlayerActionKind.WhitelistOn => "whitelist on",
            RemotePlayerActionKind.WhitelistOff => "whitelist off",
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    private async ValueTask<RemoteOperationResultDto> RunServerMutationAsync(
        string serverId,
        Func<Guid, CancellationToken, Task<ProductServerMutationResult>> operation,
        CancellationToken cancellationToken)
    {
        if (!TryParseServerId(serverId, out var id) || !registry.TryGet(id, out _))
        {
            return NotFound();
        }

        try
        {
            await operation(id, cancellationToken).ConfigureAwait(false);
            return Accepted();
        }
        catch (Exception error) when (IsSafeOperationFailure(error, cancellationToken))
        {
            return Rejected(error);
        }
    }

    private async ValueTask<RemoteOperationResultDto> RunUpdateMutationAsync(
        Func<CancellationToken, Task<ProductUpdateOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        if (updates is null)
        {
            return new RemoteOperationResultDto(false, "Product update coordinator is unavailable.");
        }

        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            return new RemoteOperationResultDto(
                result.Accepted,
                result.Status.Message ?? (result.Accepted ? "Operation accepted." : "Operation rejected."),
                result.OperationId);
        }
        catch (Exception error) when (IsSafeOperationFailure(error, cancellationToken))
        {
            return Rejected(error);
        }
    }

    private RemoteServerSummaryDto MapSummary(ProductServerStatus status)
    {
        var players = playerTracker.GetPlayers(status.Server.Id);
        return new RemoteServerSummaryDto(
            status.Server.Id.ToString("D"),
            status.Server.Name,
            status.Server.CoreType,
            status.Server.MinecraftVersion ?? string.Empty,
            MapState(status.Server.State),
            status.Server.State == ProductServerState.Running,
            players.Count,
            MaximumPlayers: null,
            status.Resource?.CpuPercent,
            status.Resource?.WorkingSetBytes,
            status.Server.Port,
            status.Resource is { } resource ? (long)resource.Uptime.TotalSeconds : null);
    }

    private static RemoteConsoleLineDto MapConsoleLine(ProductConsoleEntry entry)
        => new(
            entry.Cursor,
            entry.Timestamp,
            MapSeverity(entry.Severity),
            IsDiagnostic(entry) ? RemoteConsoleStream.Diagnostic : RemoteConsoleStream.Ordinary,
            entry.Text);

    internal static RemoteBackupSummaryDto MapBackup(ProductServerBackupSummary backup)
        => new(
            backup.BackupId.ToLowerInvariant(),
            CreateSafeBackupDisplayName(backup.FileName, backup.CreatedAtUtc),
            backup.ArchiveBytes,
            backup.CreatedAtUtc);

    private static RemoteServerJavaRuntimeDto MapJava(ProductServerJavaRuntimeSummary java)
        => new(
            java.Configured,
            java.Available,
            java.MajorVersion,
            java.Version,
            java.RuntimeKind.ToUpperInvariant() switch
            {
                "JDK" => RemoteJavaRuntimeKind.Jdk,
                "JRE" => RemoteJavaRuntimeKind.Jre,
                _ => RemoteJavaRuntimeKind.Unknown,
            },
            java.Vendor,
            java.Architecture.ToLowerInvariant() switch
            {
                "x64" => RemoteJavaArchitecture.X64,
                "arm64" => RemoteJavaArchitecture.Arm64,
                "x86" => RemoteJavaArchitecture.X86,
                _ => RemoteJavaArchitecture.Unknown,
            });

    private static string? CreateJavaDisplayName(ProductServerJavaRuntimeSummary? java)
    {
        if (java is null || !java.Available)
        {
            return null;
        }

        return java.MajorVersion is { } major
            ? $"Java {major}"
            : string.IsNullOrWhiteSpace(java.Version)
                ? "Java"
                : $"Java {java.Version}";
    }

    private static string CreateSafeBackupDisplayName(string? value, DateTimeOffset createdAtUtc)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length is < 1 or > RemoteBackupRestoreContract.MaximumDisplayNameCharacters ||
            candidate.StartsWith(".", StringComparison.Ordinal) ||
            candidate.Contains("..", StringComparison.Ordinal) ||
            candidate.Any(character => char.IsControl(character) || character is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*'))
        {
            return $"backup-{createdAtUtc.ToUniversalTime():yyyyMMdd-HHmmss}.zip";
        }

        return candidate;
    }

    private static bool MatchesStream(ProductConsoleEntry entry, RemoteConsoleStream stream)
        => stream switch
        {
            RemoteConsoleStream.Ordinary => !IsDiagnostic(entry),
            RemoteConsoleStream.Diagnostic => IsDiagnostic(entry),
            _ => true,
        };

    private static bool IsDiagnostic(ProductConsoleEntry entry)
        => entry.DiagnosticId is not null ||
           entry.Severity is ProductConsoleSeverity.Warning or ProductConsoleSeverity.Error or ProductConsoleSeverity.Fatal;

    private static RemoteConsoleSeverity MapSeverity(ProductConsoleSeverity severity) => severity switch
    {
        ProductConsoleSeverity.Warning => RemoteConsoleSeverity.Warning,
        ProductConsoleSeverity.Error or ProductConsoleSeverity.Fatal => RemoteConsoleSeverity.Error,
        _ => RemoteConsoleSeverity.Information,
    };

    private static RemoteServerState MapState(ProductServerState state) => state switch
    {
        ProductServerState.Starting => RemoteServerState.Starting,
        ProductServerState.Running => RemoteServerState.Running,
        ProductServerState.Stopping => RemoteServerState.Stopping,
        ProductServerState.Crashed or ProductServerState.Faulted => RemoteServerState.Failed,
        _ => RemoteServerState.Stopped,
    };

    private static string WithOptionalReason(string command, string? reason)
        => string.IsNullOrWhiteSpace(reason) ? command : $"{command} {reason}";

    private static bool TryParseServerId(string value, out Guid serverId)
        => Guid.TryParseExact(value, "D", out serverId) && serverId != Guid.Empty;

    private static bool IsSafeOperationFailure(Exception error, CancellationToken cancellationToken)
        => error is not OutOfMemoryException &&
           !(error is OperationCanceledException && cancellationToken.IsCancellationRequested);

    private static RemoteOperationResultDto Accepted()
        => new(true, "Operation accepted.", Guid.NewGuid().ToString("D"));

    private static RemoteOperationResultDto NotFound()
        => new(false, "The selected server was not found.");

    private static RemoteOperationResultDto Rejected(Exception error)
    {
        var publicError = ProductOperationErrorPolicy.ToPublic(
            ProductOperationErrorPolicy.IsExpected(error)
                ? error
                : new InvalidOperationException());
        return new RemoteOperationResultDto(false, publicError.Message);
    }
}
