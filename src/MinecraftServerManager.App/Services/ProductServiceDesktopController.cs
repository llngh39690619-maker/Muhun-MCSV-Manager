using System.Collections.Concurrent;
using MinecraftServerManager.Client;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.App.Services;

internal sealed record ProductServiceServerProjection(
    ProductServerSummary Summary,
    ProductServerStatus Status,
    ProductServerRegistration Registration,
    bool RegistrationChanged,
    ProductConsolePage Console,
    bool ReplaceConsole);

internal sealed record ProductServiceDesktopSnapshot(
    ProductServiceConnectionResult Connection,
    IReadOnlyList<ProductServiceServerProjection> Servers,
    bool IsComplete = true);

public interface IProductUpdateClient
{
    Task<ProductUpdateStatus> GetUpdateStatusAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken = default);

    Task<ProductUpdateOperationResult> CheckForUpdateAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken = default);

    Task<ProductUpdateOperationResult> DownloadUpdateAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken = default);

    Task<ProductUpdateOperationResult> ScheduleUpdateAsync(
        ProductUpdateChannel channel,
        DateTimeOffset? notBeforeUtc = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Desktop-only projection of the administrator IPC surface for the Service-owned remote host.
/// Keeping this interface smaller than <see cref="IProductServiceClient"/> makes the dialog
/// independently testable and, more importantly, prevents it from ever constructing a Web host.
/// </summary>
internal interface IProductRemoteManagementClient
{
    Task<ProductRemoteAccessStatus> GetRemoteAccessStatusAsync(
        CancellationToken cancellationToken = default);

    Task<ProductRemoteAccessStatus> StartRemoteAccessAsync(
        CancellationToken cancellationToken = default);

    Task<ProductRemoteAccessStatus> StopRemoteAccessAsync(
        CancellationToken cancellationToken = default);

    Task<ProductRemoteAccessStatus> ReconnectRemoteAccessAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductRemoteAccountSummary>> ListRemoteAccountsAsync(
        CancellationToken cancellationToken = default);

    Task<ProductRemoteAccountSummary> CreateRemoteAccountAsync(
        ProductCreateRemoteAccountRequest request,
        CancellationToken cancellationToken = default);

    Task<ProductRemoteAccountSummary> UpdateRemoteAccountAuthorizationAsync(
        string username,
        ProductUpdateRemoteAccountAuthorizationRequest request,
        CancellationToken cancellationToken = default);

    Task<ProductRemoteAccountSummary> UpdateRemoteAccountPinAsync(
        string username,
        ProductUpdateRemoteAccountPinRequest request,
        CancellationToken cancellationToken = default);

    Task<ProductRevealRemoteAccountPinResponse> RevealRemoteAccountPinAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task DeleteRemoteAccountAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductRememberedDeviceSummary>> ListRemoteDevicesAsync(
        CancellationToken cancellationToken = default);

    Task RevokeRemoteDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Desktop-side Service session. It owns only IPC cursors and never owns a Java process or a
/// server directory. A failed Service call is returned as an unavailable snapshot; there is no
/// in-process fallback and therefore no split-brain process ownership.
/// </summary>
internal sealed class ProductServiceDesktopController :
    IAsyncDisposable,
    IProductUpdateClient,
    IProductRemoteManagementClient,
    IProductNotificationManagementClient,
    IProductProviderManagementClient
{
    private const int ConsolePageSize = 50;
    private readonly IProductServiceClient _client;
    private readonly ProductServerImportStagingClient _imports;
    private readonly ProductServerModpackUpdateStagingClient _modpackUpdates;
    private readonly ConcurrentDictionary<Guid, long> _consoleCursors = [];
    private readonly ConcurrentDictionary<Guid, ProductServerRegistration> _registrations = [];
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _disposed;

    public ProductServiceDesktopController(
        IProductServiceClient client,
        string? authorizedImportsRoot = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _imports = new ProductServerImportStagingClient(_client);
        _modpackUpdates = new ProductServerModpackUpdateStagingClient(
            _client,
            authorizedImportsRoot);
    }

    public async Task<ProductServiceDesktopSnapshot> RefreshAsync(
        CancellationToken cancellationToken = default)
        => await RefreshCoreAsync(consoleServerIds: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Production polling reads status pages for every row but console data only for the visible
    /// server. This keeps polling cost nearly constant when hundreds of servers are registered.
    /// Passing null means no console is visible; the compatibility RefreshAsync overload keeps
    /// reading all consoles for existing diagnostics and tests.
    /// </summary>
    public async Task<ProductServiceDesktopSnapshot> RefreshFocusedAsync(
        Guid? consoleServerId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlySet<Guid> ids = consoleServerId is { } id && id != Guid.Empty
            ? new HashSet<Guid> { id }
            : new HashSet<Guid>();
        return await RefreshCoreAsync(ids, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes one newly committed server without re-listing and re-projecting every existing
    /// registration.  The returned snapshot is explicitly partial so the UI never treats absent
    /// rows as deleted.
    /// </summary>
    public async Task<ProductServiceDesktopSnapshot> RefreshServerAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(serverId));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await ProductServiceConnectionProbe.ProbeAsync(_client, cancellationToken)
                .ConfigureAwait(false);
            if (!connection.IsConnected)
            {
                return new ProductServiceDesktopSnapshot(connection, [], IsComplete: false);
            }

            try
            {
                var status = await _client.GetStatusAsync(serverId, cancellationToken)
                    .ConfigureAwait(false);
                var registration = await _client.GetRegistrationAsync(serverId, cancellationToken)
                    .ConfigureAwait(false);
                ValidateRegistration(registration, status.Server);
                _registrations[serverId] = registration;

                var requestedCursor = _consoleCursors.GetValueOrDefault(serverId);
                var console = await _client.ReadConsoleAsync(
                        serverId,
                        requestedCursor,
                        ConsolePageSize,
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateProjection(status.Server, status, console, requestedCursor);
                _consoleCursors[serverId] = console.NextCursor;

                ProductServiceServerProjection[] projection =
                [
                    new(
                        status.Server,
                        status,
                        registration,
                        true,
                        console,
                        console.HistoryGap),
                ];
                return new ProductServiceDesktopSnapshot(
                    connection,
                    projection,
                    IsComplete: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _ = error;
                var failedConnection = await ProductServiceConnectionProbe
                    .ProbeAsync(_client, cancellationToken)
                    .ConfigureAwait(false);
                if (failedConnection.IsConnected)
                {
                    failedConnection = new ProductServiceConnectionResult(
                        ProductServiceConnectionState.Faulted,
                        "service.refresh_failed",
                        null);
                }

                return new ProductServiceDesktopSnapshot(
                    failedConnection,
                    [],
                    IsComplete: false);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<ProductServiceDesktopSnapshot> RefreshCoreAsync(
        IReadOnlySet<Guid>? consoleServerIds,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await ProductServiceConnectionProbe.ProbeAsync(_client, cancellationToken)
                .ConfigureAwait(false);
            if (!connection.IsConnected)
            {
                return new ProductServiceDesktopSnapshot(connection, []);
            }

            try
            {
                var statuses = await _client.ListStatusesAsync(cancellationToken).ConfigureAwait(false);
                if (statuses.Count > 256 ||
                    statuses.Any(status => status.Server.Id == Guid.Empty) ||
                    statuses.Select(status => status.Server.Id).Distinct().Count() != statuses.Count)
                {
                    throw new InvalidDataException("Service returned an invalid or duplicate status page.");
                }

                var liveIds = statuses.Select(server => server.Server.Id).ToHashSet();
                foreach (var staleId in _consoleCursors.Keys.Where(id => !liveIds.Contains(id)).ToArray())
                {
                    _consoleCursors.TryRemove(staleId, out _);
                }
                foreach (var staleId in _registrations.Keys.Where(id => !liveIds.Contains(id)).ToArray())
                {
                    _registrations.TryRemove(staleId, out _);
                }

                var projections = new List<ProductServiceServerProjection>(statuses.Count);
                foreach (var status in statuses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var summary = status.Server;
                    var registrationChanged = false;
                    if (!_registrations.TryGetValue(summary.Id, out var registration) ||
                        !RegistrationMatchesSummary(registration, summary))
                    {
                        registration = await _client.GetRegistrationAsync(summary.Id, cancellationToken)
                            .ConfigureAwait(false);
                        ValidateRegistration(registration, summary);
                        _registrations[summary.Id] = registration;
                        registrationChanged = true;
                    }
                    var requestedCursor = _consoleCursors.GetValueOrDefault(summary.Id);
                    var shouldReadConsole = consoleServerIds is null
                                            || consoleServerIds.Contains(summary.Id);
                    var console = shouldReadConsole
                        ? await _client.ReadConsoleAsync(
                                summary.Id,
                                requestedCursor,
                                ConsolePageSize,
                                cancellationToken)
                            .ConfigureAwait(false)
                        : new ProductConsolePage(
                            summary.Id,
                            requestedCursor,
                            requestedCursor,
                            requestedCursor,
                            HistoryGap: false,
                            Entries: []);
                    ValidateProjection(summary, status, console, requestedCursor);
                    if (shouldReadConsole)
                    {
                        _consoleCursors[summary.Id] = console.NextCursor;
                    }
                    projections.Add(new ProductServiceServerProjection(
                        status.Server,
                        status,
                        registration,
                        registrationChanged,
                        console,
                        console.HistoryGap));
                }

                return new ProductServiceDesktopSnapshot(connection, projections.AsReadOnly());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _ = error;
                var failedConnection = await ProductServiceConnectionProbe
                    .ProbeAsync(_client, cancellationToken)
                    .ConfigureAwait(false);
                if (failedConnection.IsConnected)
                {
                    failedConnection = new ProductServiceConnectionResult(
                        ProductServiceConnectionState.Faulted,
                        "service.refresh_failed",
                        null);
                }

                return new ProductServiceDesktopSnapshot(failedConnection, []);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public Task<ProductServerMutationResult> StartAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => _client.StartAsync(serverId, cancellationToken);

    public Task<ProductServerMutationResult> StartAsync(
        Guid serverId,
        bool acceptMinecraftEula,
        CancellationToken cancellationToken = default)
        => _client.StartAsync(serverId, acceptMinecraftEula, cancellationToken);

    public Task<ProductServerMutationResult> StopAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => _client.StopAsync(serverId, cancellationToken);

    public Task<ProductServerMutationResult> RestartAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => _client.RestartAsync(serverId, cancellationToken);

    public Task<ProductServerMutationResult> RestartAsync(
        Guid serverId,
        bool acceptMinecraftEula,
        CancellationToken cancellationToken = default)
        => _client.RestartAsync(serverId, acceptMinecraftEula, cancellationToken);

    public Task<ProductServerStatus> SendCommandAsync(
        Guid serverId,
        string command,
        CancellationToken cancellationToken = default)
        => _client.SendCommandAsync(serverId, command, cancellationToken);

    public async Task<ProductServerRegistration> GetRegistrationAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var registration = await _client.GetRegistrationAsync(serverId, cancellationToken)
            .ConfigureAwait(false);
        if (registration.Id != serverId)
        {
            throw new InvalidDataException("Service returned a cross-server registration.");
        }

        _registrations[serverId] = registration;
        return registration;
    }

    public async Task<ProductServerSettingsUpdateResult> UpdateRegistrationAsync(
        ProductServerRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var result = await _client.UpdateServerSettingsAsync(
                registration.Id,
                new ProductServerSettingsUpdateRequest(
                    registration.Name,
                    registration.MinimumMemoryMb,
                    registration.MaximumMemoryMb,
                    registration.Port,
                    registration.AutoRestart)
                {
                    MemoryAllocationMode = registration.MemoryAllocationMode,
                    SeparateDiagnosticOutput = registration.SeparateDiagnosticOutput,
                    EnableHangWatchdog = registration.EnableHangWatchdog,
                    WatchdogCheckIntervalSeconds = registration.WatchdogCheckIntervalSeconds,
                    WatchdogProbeTimeoutSeconds = registration.WatchdogProbeTimeoutSeconds,
                    WatchdogFailureThreshold = registration.WatchdogFailureThreshold,
                    WatchdogStartupGraceSeconds = registration.WatchdogStartupGraceSeconds,
                    EnableAutomaticRecoveryPoints = registration.EnableAutomaticRecoveryPoints,
                    RecoveryPointIntervalMinutes = registration.RecoveryPointIntervalMinutes,
                    RecoveryPointRetentionCount = registration.RecoveryPointRetentionCount,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Status.Server.Id != registration.Id ||
            result.Registration.Id != registration.Id)
        {
            throw new InvalidDataException("Service returned a cross-server registration update.");
        }

        _registrations[registration.Id] = result.Registration;
        return result;
    }

    public async Task RemoveAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        await _client.RemoveAsync(serverId, cancellationToken).ConfigureAwait(false);
        _consoleCursors.TryRemove(serverId, out _);
        _registrations.TryRemove(serverId, out _);
    }

    public Task<ProductServerDirectoryInfo> GetServerDirectoryAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => _client.GetServerDirectoryAsync(serverId, cancellationToken);

    public Task<ProductServerAdministrationSnapshot> GetServerAdministrationAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => _client.GetServerAdministrationAsync(serverId, cancellationToken);

    public Task<ProductServerPropertiesDocument> ReadServerPropertiesAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => _client.ReadServerPropertiesAsync(serverId, cancellationToken);

    public Task<ProductServerPropertiesDocument> UpdateServerPropertiesAsync(
        Guid serverId,
        ProductServerPropertiesUpdateRequest update,
        CancellationToken cancellationToken = default)
        => _client.UpdateServerPropertiesAsync(serverId, update, cancellationToken);

    public async Task<ProductServerDeletionResult> DeleteServerPermanentlyAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.DeleteServerPermanentlyAsync(serverId, cancellationToken)
            .ConfigureAwait(false);
        if (result.ServerId != serverId || !result.Deleted)
        {
            throw new InvalidDataException("Service returned a cross-server deletion result.");
        }

        _consoleCursors.TryRemove(serverId, out _);
        _registrations.TryRemove(serverId, out _);
        return result;
    }

    public Task<IReadOnlyList<ProductServerBackupSummary>> ListBackupsAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => _client.ListBackupsAsync(serverId, cancellationToken);

    public Task<ProductServerPlayerList> ListPlayersAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => _client.ListPlayersAsync(serverId, cancellationToken);

    public Task<ProductServerBackupMutationResult> CreateBackupAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => _client.CreateBackupAsync(serverId, cancellationToken);

    public Task<ProductServerBackupRestoreResult> RestoreBackupAsync(
        Guid serverId,
        string backupId,
        CancellationToken cancellationToken = default)
        => _client.RestoreBackupAsync(serverId, backupId, cancellationToken);

    public Task<ProductServerImportStatus> ImportAsync(
        MinecraftServerManager.Core.Models.ServerInstance instance,
        string? migrationKey,
        CancellationToken cancellationToken = default)
        => _imports.ImportAsync(instance, migrationKey, cancellationToken);

    public Task<ProductServerModpackUpdateStatus> UpdateModpackAsync(
        MinecraftServerManager.Core.Models.ServerInstance candidate,
        Guid serverId,
        string expectedCurrentVersionId,
        ProductServerModpackUpdateDefinition target,
        CancellationToken cancellationToken = default)
        => _modpackUpdates.UpdateAsync(
            candidate,
            serverId,
            expectedCurrentVersionId,
            target,
            cancellationToken);

    public Task<ProductServerModpackUpdateStatus> BeginModpackUpdateAsync(
        ProductServerModpackUpdateBeginRequest request,
        CancellationToken cancellationToken = default)
        => _modpackUpdates.BeginAsync(request, cancellationToken);

    public Task<ProductServerModpackUpdateStatus> CommitModpackUpdateAsync(
        Guid updateId,
        string manifestSha256,
        CancellationToken cancellationToken = default)
        => _modpackUpdates.CommitAsync(updateId, manifestSha256, cancellationToken);

    public Task<ProductServerModpackUpdateStatus> GetModpackUpdateStatusAsync(
        Guid updateId,
        CancellationToken cancellationToken = default)
        => _modpackUpdates.GetStatusAsync(updateId, cancellationToken);

    public Task<ProductServerModpackUpdateStatus> CancelModpackUpdateAsync(
        Guid updateId,
        CancellationToken cancellationToken = default)
        => _modpackUpdates.CancelAsync(updateId, cancellationToken);

    public Task<ProductUpdateStatus> GetUpdateStatusAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken = default)
        => _client.GetUpdateStatusAsync(channel, cancellationToken);

    public Task<ProductUpdateOperationResult> CheckForUpdateAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken = default)
        => _client.CheckForUpdateAsync(channel, cancellationToken);

    public Task<ProductUpdateOperationResult> DownloadUpdateAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken = default)
        => _client.DownloadUpdateAsync(channel, cancellationToken);

    public Task<ProductUpdateOperationResult> ScheduleUpdateAsync(
        ProductUpdateChannel channel,
        DateTimeOffset? notBeforeUtc = null,
        CancellationToken cancellationToken = default)
        => _client.ScheduleUpdateAsync(channel, notBeforeUtc, cancellationToken);

    public Task<ProductRemoteAccessStatus> GetRemoteAccessStatusAsync(
        CancellationToken cancellationToken = default)
        => _client.GetRemoteAccessStatusAsync(cancellationToken);

    public Task<ProductRemoteAccessStatus> StartRemoteAccessAsync(
        CancellationToken cancellationToken = default)
        => _client.StartRemoteAccessAsync(cancellationToken);

    public Task<ProductRemoteAccessStatus> StopRemoteAccessAsync(
        CancellationToken cancellationToken = default)
        => _client.StopRemoteAccessAsync(cancellationToken);

    public Task<ProductRemoteAccessStatus> ReconnectRemoteAccessAsync(
        CancellationToken cancellationToken = default)
        => _client.ReconnectRemoteAccessAsync(cancellationToken);

    public Task<IReadOnlyList<ProductRemoteAccountSummary>> ListRemoteAccountsAsync(
        CancellationToken cancellationToken = default)
        => _client.ListRemoteAccountsAsync(cancellationToken);

    public Task<ProductRemoteAccountSummary> CreateRemoteAccountAsync(
        ProductCreateRemoteAccountRequest request,
        CancellationToken cancellationToken = default)
        => _client.CreateRemoteAccountAsync(request, cancellationToken);

    public Task<ProductRemoteAccountSummary> UpdateRemoteAccountAuthorizationAsync(
        string username,
        ProductUpdateRemoteAccountAuthorizationRequest request,
        CancellationToken cancellationToken = default)
        => _client.UpdateRemoteAccountAuthorizationAsync(username, request, cancellationToken);

    public Task<ProductRemoteAccountSummary> UpdateRemoteAccountPinAsync(
        string username,
        ProductUpdateRemoteAccountPinRequest request,
        CancellationToken cancellationToken = default)
        => _client.UpdateRemoteAccountPinAsync(username, request, cancellationToken);

    public Task<ProductRevealRemoteAccountPinResponse> RevealRemoteAccountPinAsync(
        string username,
        CancellationToken cancellationToken = default)
        => _client.RevealRemoteAccountPinAsync(username, cancellationToken);

    public Task DeleteRemoteAccountAsync(
        string username,
        CancellationToken cancellationToken = default)
        => _client.DeleteRemoteAccountAsync(username, cancellationToken);

    public Task<IReadOnlyList<ProductRememberedDeviceSummary>> ListRemoteDevicesAsync(
        CancellationToken cancellationToken = default)
        => _client.ListRemoteDevicesAsync(cancellationToken);

    public Task RevokeRemoteDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
        => _client.RevokeRemoteDeviceAsync(deviceId, cancellationToken);

    public Task<ProductDiscordWebhookConfiguration> GetDiscordWebhookConfigurationAsync(
        CancellationToken cancellationToken = default)
        => _client.GetDiscordWebhookConfigurationAsync(cancellationToken);

    public Task<ProductDiscordWebhookConfiguration> SetDiscordWebhookAsync(
        string webhookUrl,
        CancellationToken cancellationToken = default)
        => _client.SetDiscordWebhookAsync(webhookUrl, cancellationToken);

    public Task<ProductDiscordWebhookConfiguration> DeleteDiscordWebhookAsync(
        CancellationToken cancellationToken = default)
        => _client.DeleteDiscordWebhookAsync(cancellationToken);

    public Task<IReadOnlyList<ProductNotificationDeliverySummary>> ListNotificationHistoryAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
        => _client.ListNotificationHistoryAsync(maximumCount, cancellationToken);

    public Task<ProductNotificationPreferences> GetNotificationPreferencesAsync(
        CancellationToken cancellationToken = default)
        => _client.GetNotificationPreferencesAsync(cancellationToken);

    public Task<ProductNotificationPreferences> SetNotificationPreferencesAsync(
        ProductNotificationPreferences preferences,
        CancellationToken cancellationToken = default)
        => _client.SetNotificationPreferencesAsync(preferences, cancellationToken);

    public Task<IReadOnlyList<ProductProviderSummary>> ListProvidersAsync(
        CancellationToken cancellationToken = default)
        => _client.ListProvidersAsync(cancellationToken);

    public Task<IReadOnlyList<ProductTrustedProviderPublisherSummary>> ListTrustedProviderPublishersAsync(
        CancellationToken cancellationToken = default)
        => _client.ListProviderPublishersAsync(cancellationToken);

    public Task<ProductProviderSummary> SetProviderEnabledAsync(
        string providerId,
        bool enabled,
        CancellationToken cancellationToken = default)
        => _client.SetProviderEnabledAsync(providerId, enabled, cancellationToken);

    public Task<ProductProviderHealthCheckResult> CheckProviderHealthAsync(
        string providerId,
        CancellationToken cancellationToken = default)
        => _client.CheckProviderHealthAsync(providerId, cancellationToken);

    public Task UninstallProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default)
        => _client.UninstallProviderAsync(providerId, cancellationToken);

    public Task<ProductTrustedProviderPublisherSummary> PinProviderPublisherAsync(
        ProductPinProviderPublisherRequest request,
        CancellationToken cancellationToken = default)
        => _client.PinProviderPublisherAsync(request, cancellationToken);

    public Task RemoveProviderPublisherAsync(
        string publisherId,
        CancellationToken cancellationToken = default)
        => _client.RemoveProviderPublisherAsync(publisherId, cancellationToken);

    public Task<ProductProviderSummary> InstallProviderFromInboxAsync(
        ProductProviderInstallFromInboxRequest request,
        CancellationToken cancellationToken = default)
        => _client.InstallProviderFromInboxAsync(request, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // A caller cancels and awaits its polling loop before disposal. Waiting for a final
        // in-flight refresh here closes the ownership boundary without disposing the semaphore
        // while another continuation can still release it.
        await _refreshGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _refreshGate.Release();
        await _client.DisposeAsync().ConfigureAwait(false);
        _refreshGate.Dispose();
    }

    private static void ValidateProjection(
        ProductServerSummary listed,
        ProductServerStatus status,
        ProductConsolePage console,
        long requestedCursor)
    {
        if (listed.Id == Guid.Empty || status.Server.Id != listed.Id || console.ServerId != listed.Id)
        {
            throw new InvalidDataException("Service returned a cross-server projection.");
        }

        if (status.Java is { } java && !IsSafeJavaStatus(java))
        {
            throw new InvalidDataException("Service returned invalid Java runtime status metadata.");
        }

        if (console.RequestedAfterCursor != requestedCursor ||
            console.OldestAvailableCursor < 0 ||
            console.NextCursor < 0 ||
            console.Entries.Count > ConsolePageSize ||
            console.Entries.Any(entry => entry.Cursor <= 0 || entry.Cursor > console.NextCursor) ||
            !console.Entries.Select(entry => entry.Cursor).SequenceEqual(
                console.Entries.Select(entry => entry.Cursor).Order()))
        {
            throw new InvalidDataException("Service returned an invalid console cursor page.");
        }
    }

    private static bool IsSafeJavaStatus(ProductServerJavaRuntimeSummary java)
        => (!java.Available || java.Configured) &&
           java.MajorVersion is null or >= 1 and <= 99 &&
           IsSafeOptionalJavaMetadata(java.Version) &&
           IsSafeRequiredJavaMetadata(java.RuntimeKind) &&
           IsSafeRequiredJavaMetadata(java.Vendor) &&
           IsSafeRequiredJavaMetadata(java.Architecture);

    private static bool IsSafeOptionalJavaMetadata(string? value)
        => value is null || IsSafeRequiredJavaMetadata(value);

    private static bool IsSafeRequiredJavaMetadata(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length <= ProductServerAdministrationContract.MaximumJavaMetadataCharacters &&
           !value.Any(char.IsControl);

    private static bool RegistrationMatchesSummary(
        ProductServerRegistration registration,
        ProductServerSummary summary)
        => registration.Id == summary.Id
           && string.Equals(registration.Name, summary.Name, StringComparison.Ordinal)
           && registration.Port == summary.Port
           && string.Equals(registration.CoreType, summary.CoreType, StringComparison.Ordinal)
           && string.Equals(
               registration.MinecraftVersion,
               summary.MinecraftVersion,
               StringComparison.Ordinal);

    private static void ValidateRegistration(
        ProductServerRegistration registration,
        ProductServerSummary summary)
    {
        if (!RegistrationMatchesSummary(registration, summary) ||
            registration.MinimumMemoryMb < 128 ||
            registration.MaximumMemoryMb < registration.MinimumMemoryMb ||
            registration.JavaArgumentFilePaths.Count > 128 ||
            registration.JvmArguments.Count > 128 ||
            registration.ServerArguments.Count > 128)
        {
            throw new InvalidDataException("Service returned an invalid server registration.");
        }
    }
}
