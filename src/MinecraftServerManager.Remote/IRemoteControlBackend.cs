using MinecraftServerManager.Remote.Contracts;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Remote;

/// <summary>
/// Narrow application boundary consumed by the mobile API. Implementations map
/// opaque server identifiers to desktop operations; remote input is never treated
/// as a filesystem path or operating-system command.
/// </summary>
public interface IRemoteControlBackend
{
    ValueTask<RemoteDashboardDto> GetDashboardAsync(CancellationToken cancellationToken);

    ValueTask<RemoteServerDetailDto?> GetServerAsync(string serverId, CancellationToken cancellationToken);

    ValueTask<RemoteServerAdministrationDto?> GetServerAdministrationAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<RemoteServerAdministrationDto?>(null);
    }

    ValueTask<RemoteConsolePageDto?> GetConsoleAsync(
        string serverId,
        RemoteConsoleQuery query,
        CancellationToken cancellationToken);

    ValueTask<RemotePlayerListDto?> GetPlayersAsync(string serverId, CancellationToken cancellationToken);

    ValueTask<RemoteOperationResultDto> StartServerAsync(string serverId, CancellationToken cancellationToken);

    ValueTask<RemoteOperationResultDto> StopServerAsync(string serverId, CancellationToken cancellationToken);

    ValueTask<RemoteOperationResultDto> RestartServerAsync(string serverId, CancellationToken cancellationToken);

    ValueTask<RemoteOperationResultDto> SendConsoleCommandAsync(
        string serverId,
        string command,
        CancellationToken cancellationToken);

    ValueTask<RemoteOperationResultDto> PerformPlayerActionAsync(
        string serverId,
        RemotePlayerActionRequestDto request,
        CancellationToken cancellationToken);

    ValueTask<RemoteOperationResultDto> CreateBackupAsync(string serverId, CancellationToken cancellationToken);

    ValueTask<RemoteBackupListDto?> GetBackupsAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<RemoteBackupListDto?>(null);
    }

    ValueTask<RemoteOperationResultDto> RestoreBackupAsync(
        string serverId,
        string backupId,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new RemoteOperationResultDto(
            false,
            "Backup restore requires the formal Windows Service backend."));

    ValueTask<ProductUpdateStatus> GetProductUpdateStatusAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ProductUpdateStatus(
            channel,
            ProductUpdatePhase.Disabled,
            "unavailable",
            "unavailable",
            false,
            false,
            null,
            null,
            0,
            null,
            null,
            "update.backend_unavailable",
            "Product updates require the formal Windows Service backend."));
    }

    ValueTask<RemoteOperationResultDto> CheckForProductUpdateAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new RemoteOperationResultDto(
            false,
            "Product updates require the formal Windows Service backend."));

    ValueTask<RemoteOperationResultDto> DownloadProductUpdateAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new RemoteOperationResultDto(
            false,
            "Product updates require the formal Windows Service backend."));

    ValueTask<RemoteOperationResultDto> ScheduleProductUpdateAsync(
        ProductUpdateChannel channel,
        DateTimeOffset? notBeforeUtc,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new RemoteOperationResultDto(
            false,
            "Product updates require the formal Windows Service backend."));
}
