using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.Client;

/// <summary>
/// Narrow local-control boundary used by the desktop GUI. Implementations must treat the Windows
/// Service as the sole owner of Java processes; callers never fall back to an in-process runtime
/// after one of these operations fails.
/// </summary>
public interface IProductServiceClient : IAsyncDisposable
{
    Task<ProductLocalHandshakePayload> HandshakeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductServerSummary>> ListServersAsync(
        CancellationToken cancellationToken = default);

    async Task<IReadOnlyList<ProductServerStatus>> ListStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        var summaries = await ListServersAsync(cancellationToken).ConfigureAwait(false);
        var statuses = new List<ProductServerStatus>(summaries.Count);
        foreach (var summary in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statuses.Add(await GetStatusAsync(summary.Id, cancellationToken).ConfigureAwait(false));
        }

        return statuses.AsReadOnly();
    }

    Task<ProductServerStatus> GetStatusAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task<ProductServerRegistration> GetRegistrationAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerRegistration>(
            new NotSupportedException("This client does not support registration inspection."));

    Task<ProductServerStatus> RegisterAsync(
        ProductServerRegistration registration,
        CancellationToken cancellationToken = default);

    Task<ProductServerSettingsUpdateResult> UpdateServerSettingsAsync(
        Guid serverId,
        ProductServerSettingsUpdateRequest settings,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerSettingsUpdateResult>(
            new NotSupportedException("This client does not support safe server settings updates."));

    Task RemoveAsync(Guid serverId, CancellationToken cancellationToken = default);

    Task<ProductServerDirectoryInfo> GetServerDirectoryAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerDirectoryInfo>(
            new NotSupportedException("This client does not support Service-owned directory inspection."));

    Task<ProductServerAdministrationSnapshot> GetServerAdministrationAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerAdministrationSnapshot>(
            new NotSupportedException("This client does not support bounded Service-owned server administration snapshots."));

    Task<ProductServerDeletionResult> DeleteServerPermanentlyAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerDeletionResult>(
            new NotSupportedException("This client does not support Service-owned permanent deletion."));

    Task<IReadOnlyList<ProductServerBackupSummary>> ListBackupsAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<ProductServerBackupSummary>>(
            new NotSupportedException("This client does not support Service-owned backups."));

    Task<ProductServerBackupMutationResult> CreateBackupAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerBackupMutationResult>(
            new NotSupportedException("This client does not support Service-owned backups."));

    Task<ProductServerBackupRestoreResult> RestoreBackupAsync(
        Guid serverId,
        string backupId,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerBackupRestoreResult>(
            new NotSupportedException("This client does not support Service-owned backups."));

    Task<ProductServerMutationResult> StartAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task<ProductServerMutationResult> StopAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task<ProductServerMutationResult> RestartAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task<ProductConsolePage> ReadConsoleAsync(
        Guid serverId,
        long afterCursor,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<ProductServerPlayerList> ListPlayersAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerPlayerList>(
            new NotSupportedException("This client does not support Service player presence."));

    Task<ProductServerStatus> SendCommandAsync(
        Guid serverId,
        string command,
        CancellationToken cancellationToken = default);

    Task<ProductUpdateStatus> GetUpdateStatusAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductUpdateStatus>(
            new NotSupportedException("This local Service client does not support product updates."));

    Task<ProductUpdateOperationResult> CheckForUpdateAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductUpdateOperationResult>(
            new NotSupportedException("This local Service client does not support product updates."));

    Task<ProductUpdateOperationResult> DownloadUpdateAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductUpdateOperationResult>(
            new NotSupportedException("This local Service client does not support product updates."));

    Task<ProductUpdateOperationResult> ScheduleUpdateAsync(
        ProductUpdateChannel channel,
        DateTimeOffset? notBeforeUtc = null,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductUpdateOperationResult>(
            new NotSupportedException("This local Service client does not support product updates."));

    Task<ProductServerImportStatus> BeginImportAsync(
        ProductServerImportBeginRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerImportStatus>(
            new NotSupportedException("This client does not support Service-owned imports."));

    Task<ProductServerImportStatus> CommitImportAsync(
        Guid importId,
        string manifestSha256,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerImportStatus>(
            new NotSupportedException("This client does not support Service-owned imports."));

    Task<ProductServerImportStatus> GetImportStatusAsync(
        Guid importId,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerImportStatus>(
            new NotSupportedException("This client does not support Service-owned imports."));

    Task<ProductServerImportStatus> CancelImportAsync(
        Guid importId,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerImportStatus>(
            new NotSupportedException("This client does not support Service-owned imports."));

    Task<ProductServerModpackUpdateStatus> BeginModpackUpdateAsync(
        ProductServerModpackUpdateBeginRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerModpackUpdateStatus>(
            new NotSupportedException("This client does not support Service-owned modpack updates."));

    Task<ProductServerModpackUpdateStatus> CommitModpackUpdateAsync(
        Guid updateId,
        string manifestSha256,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerModpackUpdateStatus>(
            new NotSupportedException("This client does not support Service-owned modpack updates."));

    Task<ProductServerModpackUpdateStatus> GetModpackUpdateStatusAsync(
        Guid updateId,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerModpackUpdateStatus>(
            new NotSupportedException("This client does not support Service-owned modpack updates."));

    Task<ProductServerModpackUpdateStatus> CancelModpackUpdateAsync(
        Guid updateId,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductServerModpackUpdateStatus>(
            new NotSupportedException("This client does not support Service-owned modpack updates."));

    Task<ProductRemoteAccessStatus> GetRemoteAccessStatusAsync(
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductRemoteAccessStatus>(
            new NotSupportedException("This client does not support remote-access management."));

    Task<ProductRemoteAccessStatus> StartRemoteAccessAsync(
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductRemoteAccessStatus>(
            new NotSupportedException("This client does not support remote-access management."));

    Task<ProductRemoteAccessStatus> StopRemoteAccessAsync(
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductRemoteAccessStatus>(
            new NotSupportedException("This client does not support remote-access management."));

    Task<ProductRemoteAccessStatus> ReconnectRemoteAccessAsync(
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductRemoteAccessStatus>(
            new NotSupportedException("This client does not support remote-access management."));

    Task<IReadOnlyList<ProductRemoteAccountSummary>> ListRemoteAccountsAsync(
        CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<ProductRemoteAccountSummary>>(
            new NotSupportedException("This client does not support remote-account management."));

    Task<ProductRemoteAccountSummary> CreateRemoteAccountAsync(
        ProductCreateRemoteAccountRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductRemoteAccountSummary>(
            new NotSupportedException("This client does not support remote-account management."));

    Task<ProductRemoteAccountSummary> UpdateRemoteAccountAuthorizationAsync(
        string username,
        ProductUpdateRemoteAccountAuthorizationRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductRemoteAccountSummary>(
            new NotSupportedException("This client does not support remote-account management."));

    Task<ProductRemoteAccountSummary> UpdateRemoteAccountPinAsync(
        string username,
        ProductUpdateRemoteAccountPinRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductRemoteAccountSummary>(
            new NotSupportedException("This client does not support remote-account management."));

    Task<ProductRevealRemoteAccountPinResponse> RevealRemoteAccountPinAsync(
        string username,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductRevealRemoteAccountPinResponse>(
            new NotSupportedException("This client does not support remote-account management."));

    Task DeleteRemoteAccountAsync(
        string username,
        CancellationToken cancellationToken = default)
        => Task.FromException(
            new NotSupportedException("This client does not support remote-account management."));

    Task<IReadOnlyList<ProductRememberedDeviceSummary>> ListRemoteDevicesAsync(
        CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<ProductRememberedDeviceSummary>>(
            new NotSupportedException("This client does not support remembered-device management."));

    Task RevokeRemoteDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
        => Task.FromException(
            new NotSupportedException("This client does not support remembered-device management."));

    Task<ProductDiscordWebhookConfiguration> GetDiscordWebhookConfigurationAsync(
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductDiscordWebhookConfiguration>(
            new NotSupportedException("This client does not support notification management."));

    Task<ProductDiscordWebhookConfiguration> SetDiscordWebhookAsync(
        string webhookUrl,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductDiscordWebhookConfiguration>(
            new NotSupportedException("This client does not support notification management."));

    Task<ProductDiscordWebhookConfiguration> DeleteDiscordWebhookAsync(
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductDiscordWebhookConfiguration>(
            new NotSupportedException("This client does not support notification management."));

    Task<IReadOnlyList<ProductNotificationDeliverySummary>> ListNotificationHistoryAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<ProductNotificationDeliverySummary>>(
            new NotSupportedException("This client does not support notification management."));

    Task<ProductNotificationPreferences> GetNotificationPreferencesAsync(
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductNotificationPreferences>(
            new NotSupportedException("This client does not support notification management."));

    Task<ProductNotificationPreferences> SetNotificationPreferencesAsync(
        ProductNotificationPreferences preferences,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductNotificationPreferences>(
            new NotSupportedException("This client does not support notification management."));

    Task<IReadOnlyList<ProductProviderSummary>> ListProvidersAsync(
        CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<ProductProviderSummary>>(
            new NotSupportedException("This client does not support provider management."));

    Task<IReadOnlyList<ProductTrustedProviderPublisherSummary>> ListProviderPublishersAsync(
        CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<ProductTrustedProviderPublisherSummary>>(
            new NotSupportedException("This client does not support provider management."));

    Task<ProductProviderSummary> SetProviderEnabledAsync(
        string providerId,
        bool enabled,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductProviderSummary>(
            new NotSupportedException("This client does not support provider management."));

    Task<ProductProviderHealthCheckResult> CheckProviderHealthAsync(
        string providerId,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductProviderHealthCheckResult>(
            new NotSupportedException("This client does not support provider management."));

    Task UninstallProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default)
        => Task.FromException(
            new NotSupportedException("This client does not support provider management."));

    Task<ProductTrustedProviderPublisherSummary> PinProviderPublisherAsync(
        ProductPinProviderPublisherRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductTrustedProviderPublisherSummary>(
            new NotSupportedException("This client does not support provider management."));

    Task RemoveProviderPublisherAsync(
        string publisherId,
        CancellationToken cancellationToken = default)
        => Task.FromException(
            new NotSupportedException("This client does not support provider management."));

    Task<ProductProviderSummary> InstallProviderFromInboxAsync(
        ProductProviderInstallFromInboxRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromException<ProductProviderSummary>(
            new NotSupportedException("This client does not support provider management."));
}
