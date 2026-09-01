using System.IO.Pipes;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.Client;

public sealed class ProductServiceClient : IProductServiceClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MutationRequestTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LongMutationRequestTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ProviderRequestTimeout = TimeSpan.FromSeconds(20);
    private const int MaximumBackupsPerServer = 2_000;
    private readonly string _pipeName;
    private readonly SemaphoreSlim _concurrency = new(4, 4);
    private int _disposed;

    public ProductServiceClient(string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? ProductApiProtocol.IpcPackage
            : pipeName;
        if (_pipeName.Length > 128 || _pipeName.IndexOfAny(['\\', '/']) >= 0)
        {
            throw new ArgumentException("Pipe name is invalid.", nameof(pipeName));
        }
    }

    public async Task<ProductLocalHandshakePayload> HandshakeAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(CreateRequest(ProductIpcProtocol.HandshakeMethod), cancellationToken)
            .ConfigureAwait(false);
        return response.Handshake
            ?? throw new ProductServiceClientException(
                "protocol.payload_missing",
                "Service handshake response did not include its payload.");
    }

    public async Task<IReadOnlyList<ProductServerSummary>> ListServersAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<ProductServerSummary>();
        var seenIds = new HashSet<Guid>();
        var offset = 0;
        while (true)
        {
            var response = await SendAsync(
                CreateRequest(ProductIpcProtocol.ServerListMethod) with
                {
                    ListOffset = offset,
                    ListLimit = 50,
                },
                cancellationToken).ConfigureAwait(false);
            var page = response.ServerPage
                ?? throw new ProductServiceClientException(
                    "protocol.payload_missing",
                    "Service list response did not include a page.");
            if (page.Offset != offset || page.NextOffset < offset || page.Servers.Count > 50 ||
                page.Servers.Any(item => item.Id == Guid.Empty || !seenIds.Add(item.Id)))
            {
                throw new ProductServiceClientException(
                    "protocol.page_invalid",
                    "Service returned an invalid or duplicate server page.");
            }

            result.AddRange(page.Servers);
            if (result.Count > 256)
            {
                throw new ProductServiceClientException(
                    "protocol.page_limit_exceeded",
                    "Service returned more servers than the client limit.");
            }

            if (!page.HasMore)
            {
                return result.AsReadOnly();
            }

            if (page.NextOffset <= offset)
            {
                throw new ProductServiceClientException(
                    "protocol.page_stalled",
                    "Service list pagination did not advance.");
            }

            offset = page.NextOffset;
        }
    }

    public async Task<IReadOnlyList<ProductServerStatus>> ListStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<ProductServerStatus>();
        var seenIds = new HashSet<Guid>();
        var offset = 0;
        while (true)
        {
            var response = await SendAsync(
                CreateRequest(ProductIpcProtocol.ServerStatusListMethod) with
                {
                    ListOffset = offset,
                    ListLimit = 50,
                },
                cancellationToken).ConfigureAwait(false);
            var page = response.ServerStatusPage
                ?? throw new ProductServiceClientException(
                    "protocol.payload_missing",
                    "Service status-list response did not include a page.");
            if (page.Offset != offset || page.NextOffset < offset || page.Servers.Count > 50 ||
                page.Servers.Any(item => item.Server.Id == Guid.Empty || !seenIds.Add(item.Server.Id)))
            {
                throw new ProductServiceClientException(
                    "protocol.page_invalid",
                    "Service returned an invalid or duplicate status page.");
            }

            result.AddRange(page.Servers);
            if (result.Count > 256)
            {
                throw new ProductServiceClientException(
                    "protocol.page_limit_exceeded",
                    "Service returned more statuses than the client limit.");
            }

            if (!page.HasMore)
            {
                return result.AsReadOnly();
            }

            if (page.NextOffset <= offset)
            {
                throw new ProductServiceClientException(
                    "protocol.page_stalled",
                    "Service status pagination did not advance.");
            }

            offset = page.NextOffset;
        }
    }

    public async Task<ProductServerStatus> GetStatusAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => RequireServer((await SendAsync(
            CreateServerRequest(ProductIpcProtocol.ServerStatusMethod, serverId),
            cancellationToken).ConfigureAwait(false)).Server);

    public async Task<ProductServerRegistration> GetRegistrationAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
                CreateServerRequest(ProductIpcProtocol.ServerRegistrationMethod, serverId),
                cancellationToken)
            .ConfigureAwait(false);
        var registration = response.Registration
            ?? throw MissingPayload("server registration");
        ValidateRegistrationPayload(registration, serverId);
        return registration;
    }

    public async Task<ProductServerStatus> RegisterAsync(
        ProductServerRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var response = await SendAsync(
            CreateRequest(ProductIpcProtocol.ServerRegisterMethod) with { Server = registration },
            cancellationToken).ConfigureAwait(false);
        return RequireServer(response.Server);
    }

    public async Task<ProductServerSettingsUpdateResult> UpdateServerSettingsAsync(
        Guid serverId,
        ProductServerSettingsUpdateRequest settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var response = await SendAsync(
                CreateServerRequest(ProductIpcProtocol.ServerSettingsUpdateMethod, serverId) with
                {
                    ServerSettings = settings,
                },
                cancellationToken)
            .ConfigureAwait(false);
        var registration = response.Registration ?? throw MissingPayload("updated server registration");
        var status = RequireServer(response.Server);
        ValidateRegistrationPayload(registration, serverId);
        if (status.Server.Id != serverId ||
            !string.Equals(status.Server.Name, registration.Name, StringComparison.Ordinal) ||
            status.Server.Port != registration.Port)
        {
            throw new ProductServiceClientException(
                "protocol.payload_invalid",
                "Service returned an inconsistent server settings update result.");
        }

        return new ProductServerSettingsUpdateResult(registration, status);
    }

    public async Task RemoveAsync(Guid serverId, CancellationToken cancellationToken = default)
        => _ = await SendAsync(
            CreateServerRequest(ProductIpcProtocol.ServerRemoveMethod, serverId),
            cancellationToken).ConfigureAwait(false);

    public async Task<ProductServerDirectoryInfo> GetServerDirectoryAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
                CreateServerRequest(ProductIpcProtocol.ServerDirectoryMethod, serverId),
                cancellationToken)
            .ConfigureAwait(false);
        var directory = response.ServerDirectory
            ?? throw MissingPayload("Service-owned server directory");
        if (directory.ServerId != serverId ||
            string.IsNullOrWhiteSpace(directory.DirectoryPath) ||
            !Path.IsPathFullyQualified(directory.DirectoryPath) ||
            directory.DirectoryPath.Length > 32_767 ||
            directory.DirectoryPath.Any(char.IsControl))
        {
            throw new ProductServiceClientException(
                "protocol.payload_invalid",
                "Service returned an invalid server directory payload.");
        }

        return directory;
    }

    public async Task<ProductServerAdministrationSnapshot> GetServerAdministrationAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
                CreateServerRequest(ProductIpcProtocol.ServerAdministrationMethod, serverId),
                cancellationToken)
            .ConfigureAwait(false);
        var snapshot = response.ServerAdministration
            ?? throw MissingPayload("bounded server administration snapshot");
        if (snapshot.ServerId != serverId ||
            snapshot.CapturedAtUtc == default ||
            snapshot.CapturedAtUtc.Offset != TimeSpan.Zero ||
            snapshot.Addons is null ||
            snapshot.Addons.Count > ProductServerAdministrationContract.MaximumListedAddons ||
            snapshot.Addons.Any(static addon =>
                !Enum.IsDefined(addon.Kind) ||
                addon.SizeBytes < 0 ||
                string.IsNullOrWhiteSpace(addon.FileName) ||
                addon.FileName.Length > ProductServerAdministrationContract.MaximumAddonFileNameCharacters ||
                addon.FileName.Any(char.IsControl) ||
                !string.Equals(addon.FileName, Path.GetFileName(addon.FileName), StringComparison.Ordinal)) ||
            !IsSafeJavaSummary(snapshot.Java))
        {
            throw new ProductServiceClientException(
                "protocol.payload_invalid",
                "Service returned an invalid bounded server administration snapshot.");
        }

        return snapshot;
    }

    private static bool IsSafeJavaSummary(ProductServerJavaRuntimeSummary? java)
        => java is not null &&
           new[] { java.Version, java.RuntimeKind, java.Vendor, java.Architecture }
               .Where(static value => value is not null)
               .All(static value =>
                   value!.Length <= ProductServerAdministrationContract.MaximumJavaMetadataCharacters &&
                   !value.Any(char.IsControl));

    public async Task<ProductServerDeletionResult> DeleteServerPermanentlyAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
                CreateServerRequest(ProductIpcProtocol.ServerDeleteMethod, serverId),
                cancellationToken)
            .ConfigureAwait(false);
        var result = response.ServerDeletion
            ?? throw MissingPayload("Service-owned server deletion result");
        if (result.ServerId != serverId || !result.Deleted ||
            result.CompletedAtUtc == default || result.CompletedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ProductServiceClientException(
                "protocol.payload_invalid",
                "Service returned an invalid server deletion result.");
        }

        return result;
    }

    public async Task<IReadOnlyList<ProductServerBackupSummary>> ListBackupsAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var backups = new List<ProductServerBackupSummary>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (true)
        {
            var response = await SendAsync(
                    CreateServerRequest(ProductIpcProtocol.ServerBackupListMethod, serverId) with
                    {
                        ListOffset = offset,
                        ListLimit = 50,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var page = response.BackupPage ?? throw MissingPayload("server backup page");
            if (page.ServerId != serverId || page.Offset != offset || page.NextOffset < offset ||
                page.Backups.Count > 50 ||
                page.Backups.Any(backup =>
                    !IsValidBackupSummary(backup) ||
                    !ids.Add(backup.BackupId)))
            {
                throw InvalidPage("server backup");
            }

            backups.AddRange(page.Backups);
            if (backups.Count > MaximumBackupsPerServer)
            {
                throw new ProductServiceClientException(
                    "protocol.page_limit_exceeded",
                    "Service returned more backups than the client limit.");
            }

            if (!page.HasMore)
            {
                return backups.AsReadOnly();
            }

            if (page.NextOffset <= offset)
            {
                throw InvalidPage("server backup");
            }

            offset = page.NextOffset;
        }
    }

    public async Task<ProductServerBackupMutationResult> CreateBackupAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
                CreateServerRequest(ProductIpcProtocol.ServerBackupCreateMethod, serverId),
                cancellationToken)
            .ConfigureAwait(false);
        var result = response.BackupMutation ?? throw MissingPayload("backup mutation");
        if (result.ServerId != serverId || !IsValidBackupSummary(result.Backup))
        {
            throw new ProductServiceClientException(
                "protocol.payload_invalid",
                "Service returned an invalid backup mutation result.");
        }

        return result;
    }

    public async Task<ProductServerBackupRestoreResult> RestoreBackupAsync(
        Guid serverId,
        string backupId,
        CancellationToken cancellationToken = default)
    {
        ValidateBackupId(backupId);
        var response = await SendAsync(
                CreateServerRequest(ProductIpcProtocol.ServerBackupRestoreMethod, serverId) with
                {
                    BackupId = backupId,
                },
                cancellationToken)
            .ConfigureAwait(false);
        var result = response.BackupRestore ?? throw MissingPayload("backup restore result");
        if (result.ServerId != serverId ||
            !string.Equals(result.BackupId, backupId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProductServiceClientException(
                "protocol.payload_invalid",
                "Service returned a cross-server backup restore result.");
        }

        return result;
    }

    public Task<ProductServerMutationResult> StartAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => MutateAsync(ProductIpcProtocol.ServerStartMethod, serverId, cancellationToken);

    public Task<ProductServerMutationResult> StartAsync(
        Guid serverId,
        bool acceptMinecraftEula,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            ProductIpcProtocol.ServerStartMethod,
            serverId,
            cancellationToken,
            acceptMinecraftEula);

    public Task<ProductServerMutationResult> StopAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => MutateAsync(ProductIpcProtocol.ServerStopMethod, serverId, cancellationToken);

    public Task<ProductServerMutationResult> RestartAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => MutateAsync(ProductIpcProtocol.ServerRestartMethod, serverId, cancellationToken);

    public Task<ProductServerMutationResult> RestartAsync(
        Guid serverId,
        bool acceptMinecraftEula,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            ProductIpcProtocol.ServerRestartMethod,
            serverId,
            cancellationToken,
            acceptMinecraftEula);

    public async Task<ProductConsolePage> ReadConsoleAsync(
        Guid serverId,
        long afterCursor,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            CreateServerRequest(ProductIpcProtocol.ServerConsoleMethod, serverId) with
            {
                ConsoleCursor = afterCursor,
                ConsoleLimit = limit,
            },
            cancellationToken).ConfigureAwait(false);
        return response.Console
            ?? throw new ProductServiceClientException(
                "protocol.payload_missing",
                "Service console response did not include a page.");
    }

    public async Task<ProductServerPlayerList> ListPlayersAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            CreateServerRequest(ProductIpcProtocol.ServerPlayersMethod, serverId),
            cancellationToken).ConfigureAwait(false);
        var players = response.Players
            ?? throw new ProductServiceClientException(
                "protocol.payload_missing",
                "Service player response did not include a player list.");
        if (players.ServerId != serverId || players.Players.Count > 256 ||
            players.Players.Any(player => player.Name.Length is < 1 or > 64
                                          || player.Name.Any(char.IsControl)))
        {
            throw new ProductServiceClientException(
                "protocol.payload_invalid",
                "Service player response was invalid.");
        }

        return players;
    }

    public async Task<ProductServerStatus> SendCommandAsync(
        Guid serverId,
        string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var response = await SendAsync(
            CreateServerRequest(ProductIpcProtocol.ServerCommandMethod, serverId) with
            {
                Command = command,
            },
            cancellationToken).ConfigureAwait(false);
        return RequireServer(response.Server);
    }

    public async Task<ProductUpdateStatus> GetUpdateStatusAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken = default)
        => (await SendUpdateAsync(
                ProductIpcProtocol.UpdateStatusMethod,
                channel,
                notBeforeUtc: null,
                cancellationToken)
            .ConfigureAwait(false)).Status;

    public Task<ProductUpdateOperationResult> CheckForUpdateAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken = default)
        => SendUpdateAsync(
            ProductIpcProtocol.UpdateCheckMethod,
            channel,
            notBeforeUtc: null,
            cancellationToken);

    public Task<ProductUpdateOperationResult> DownloadUpdateAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken = default)
        => SendUpdateAsync(
            ProductIpcProtocol.UpdateDownloadMethod,
            channel,
            notBeforeUtc: null,
            cancellationToken);

    public Task<ProductUpdateOperationResult> ScheduleUpdateAsync(
        ProductUpdateChannel channel,
        DateTimeOffset? notBeforeUtc = null,
        CancellationToken cancellationToken = default)
        => SendUpdateAsync(
            ProductIpcProtocol.UpdateScheduleMethod,
            channel,
            notBeforeUtc,
            cancellationToken);

    public async Task<ProductServerImportStatus> BeginImportAsync(
        ProductServerImportBeginRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendAsync(
            CreateRequest(ProductIpcProtocol.ServerImportBeginMethod) with { ImportBegin = request },
            cancellationToken).ConfigureAwait(false);
        return RequireImport(response.Import);
    }

    public async Task<ProductServerImportStatus> CommitImportAsync(
        Guid importId,
        string manifestSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestSha256);
        var response = await SendAsync(
            CreateImportRequest(ProductIpcProtocol.ServerImportCommitMethod, importId) with
            {
                ManifestSha256 = manifestSha256,
            },
            cancellationToken).ConfigureAwait(false);
        return RequireImport(response.Import);
    }

    public async Task<ProductServerImportStatus> GetImportStatusAsync(
        Guid importId,
        CancellationToken cancellationToken = default)
        => RequireImport((await SendAsync(
            CreateImportRequest(ProductIpcProtocol.ServerImportStatusMethod, importId),
            cancellationToken).ConfigureAwait(false)).Import);

    public async Task<ProductServerImportStatus> CancelImportAsync(
        Guid importId,
        CancellationToken cancellationToken = default)
        => RequireImport((await SendAsync(
            CreateImportRequest(ProductIpcProtocol.ServerImportCancelMethod, importId),
            cancellationToken).ConfigureAwait(false)).Import);

    public async Task<ProductServerModpackUpdateStatus> BeginModpackUpdateAsync(
        ProductServerModpackUpdateBeginRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendAsync(
            CreateRequest(ProductIpcProtocol.ServerModpackUpdateBeginMethod) with
            {
                ModpackUpdateBegin = request,
            },
            cancellationToken).ConfigureAwait(false);
        return RequireModpackUpdate(response.ModpackUpdate);
    }

    public async Task<ProductServerModpackUpdateStatus> CommitModpackUpdateAsync(
        Guid updateId,
        string manifestSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestSha256);
        var response = await SendAsync(
            CreateModpackUpdateRequest(ProductIpcProtocol.ServerModpackUpdateCommitMethod, updateId) with
            {
                ManifestSha256 = manifestSha256,
            },
            cancellationToken).ConfigureAwait(false);
        return RequireModpackUpdate(response.ModpackUpdate);
    }

    public async Task<ProductServerModpackUpdateStatus> GetModpackUpdateStatusAsync(
        Guid updateId,
        CancellationToken cancellationToken = default)
        => RequireModpackUpdate((await SendAsync(
            CreateModpackUpdateRequest(ProductIpcProtocol.ServerModpackUpdateStatusMethod, updateId),
            cancellationToken).ConfigureAwait(false)).ModpackUpdate);

    public async Task<ProductServerModpackUpdateStatus> CancelModpackUpdateAsync(
        Guid updateId,
        CancellationToken cancellationToken = default)
        => RequireModpackUpdate((await SendAsync(
            CreateModpackUpdateRequest(ProductIpcProtocol.ServerModpackUpdateCancelMethod, updateId),
            cancellationToken).ConfigureAwait(false)).ModpackUpdate);

    public Task<ProductRemoteAccessStatus> GetRemoteAccessStatusAsync(
        CancellationToken cancellationToken = default)
        => SendRemoteAccessAsync(ProductIpcProtocol.RemoteAccessStatusMethod, cancellationToken);

    public Task<ProductRemoteAccessStatus> StartRemoteAccessAsync(
        CancellationToken cancellationToken = default)
        => SendRemoteAccessAsync(ProductIpcProtocol.RemoteAccessStartMethod, cancellationToken);

    public Task<ProductRemoteAccessStatus> StopRemoteAccessAsync(
        CancellationToken cancellationToken = default)
        => SendRemoteAccessAsync(ProductIpcProtocol.RemoteAccessStopMethod, cancellationToken);

    public Task<ProductRemoteAccessStatus> ReconnectRemoteAccessAsync(
        CancellationToken cancellationToken = default)
        => SendRemoteAccessAsync(ProductIpcProtocol.RemoteAccessReconnectMethod, cancellationToken);

    public async Task<IReadOnlyList<ProductRemoteAccountSummary>> ListRemoteAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        var accounts = new List<ProductRemoteAccountSummary>();
        var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (true)
        {
            var response = await SendAsync(
                    CreateRequest(ProductIpcProtocol.RemoteAccountListMethod) with
                    {
                        ListOffset = offset,
                        ListLimit = 1,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var page = response.RemoteAccountPage ?? throw MissingPayload("remote account page");
            if (page.Offset != offset || page.NextOffset < offset || page.Accounts.Count > 1 ||
                page.Accounts.Any(account =>
                    string.IsNullOrWhiteSpace(account.Username) || !usernames.Add(account.Username)))
            {
                throw InvalidPage("remote account");
            }

            accounts.AddRange(page.Accounts);
            if (accounts.Count > 32)
            {
                throw new ProductServiceClientException(
                    "protocol.page_limit_exceeded",
                    "Service returned more remote accounts than the client limit.");
            }

            if (!page.HasMore)
            {
                return accounts.AsReadOnly();
            }

            if (page.NextOffset <= offset)
            {
                throw InvalidPage("remote account");
            }

            offset = page.NextOffset;
        }
    }

    public async Task<ProductRemoteAccountSummary> CreateRemoteAccountAsync(
        ProductCreateRemoteAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendAsync(
                CreateRequest(ProductIpcProtocol.RemoteAccountCreateMethod) with
                {
                    RemoteAccountCreate = request,
                },
                cancellationToken)
            .ConfigureAwait(false);
        return response.RemoteAccount ?? throw MissingPayload("remote account");
    }

    public async Task<ProductRemoteAccountSummary> UpdateRemoteAccountAuthorizationAsync(
        string username,
        ProductUpdateRemoteAccountAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendAsync(
                CreateRemoteAccountRequest(
                    ProductIpcProtocol.RemoteAccountAuthorizationUpdateMethod,
                    username) with
                {
                    RemoteAccountAuthorization = request,
                },
                cancellationToken)
            .ConfigureAwait(false);
        return response.RemoteAccount ?? throw MissingPayload("remote account");
    }

    public async Task<ProductRemoteAccountSummary> UpdateRemoteAccountPinAsync(
        string username,
        ProductUpdateRemoteAccountPinRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendAsync(
                CreateRemoteAccountRequest(ProductIpcProtocol.RemoteAccountPinUpdateMethod, username) with
                {
                    RemoteAccountPin = request,
                },
                cancellationToken)
            .ConfigureAwait(false);
        return response.RemoteAccount ?? throw MissingPayload("remote account");
    }

    public async Task<ProductRevealRemoteAccountPinResponse> RevealRemoteAccountPinAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
                CreateRemoteAccountRequest(ProductIpcProtocol.RemoteAccountPinRevealMethod, username),
                cancellationToken)
            .ConfigureAwait(false);
        return response.RemotePin ?? throw MissingPayload("remote account PIN");
    }

    public async Task DeleteRemoteAccountAsync(
        string username,
        CancellationToken cancellationToken = default)
        => _ = await SendAsync(
                CreateRemoteAccountRequest(ProductIpcProtocol.RemoteAccountDeleteMethod, username),
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ProductRememberedDeviceSummary>> ListRemoteDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = new List<ProductRememberedDeviceSummary>();
        var ids = new HashSet<Guid>();
        var offset = 0;
        while (true)
        {
            var response = await SendAsync(
                    CreateRequest(ProductIpcProtocol.RemoteDeviceListMethod) with
                    {
                        ListOffset = offset,
                        ListLimit = 50,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var page = response.RemoteDevicePage ?? throw MissingPayload("remembered-device page");
            if (page.Offset != offset || page.NextOffset < offset || page.Devices.Count > 50 ||
                page.Devices.Any(device => device.DeviceId == Guid.Empty || !ids.Add(device.DeviceId)))
            {
                throw InvalidPage("remembered-device");
            }

            devices.AddRange(page.Devices);
            if (devices.Count > 64)
            {
                throw new ProductServiceClientException(
                    "protocol.page_limit_exceeded",
                    "Service returned more remembered devices than the client limit.");
            }

            if (!page.HasMore)
            {
                return devices.AsReadOnly();
            }

            if (page.NextOffset <= offset)
            {
                throw InvalidPage("remembered-device");
            }

            offset = page.NextOffset;
        }
    }

    public async Task RevokeRemoteDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device id must not be empty.", nameof(deviceId));
        }

        _ = await SendAsync(
                CreateRequest(ProductIpcProtocol.RemoteDeviceRevokeMethod) with
                {
                    RemoteDeviceId = deviceId,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ProductDiscordWebhookConfiguration> GetDiscordWebhookConfigurationAsync(
        CancellationToken cancellationToken = default)
        => RequireDiscordConfiguration((await SendAsync(
                CreateRequest(ProductIpcProtocol.NotificationDiscordStatusMethod),
                cancellationToken)
            .ConfigureAwait(false)).DiscordWebhookConfiguration);

    public async Task<ProductDiscordWebhookConfiguration> SetDiscordWebhookAsync(
        string webhookUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookUrl);
        var response = await SendAsync(
                CreateRequest(ProductIpcProtocol.NotificationDiscordSetMethod) with
                {
                    DiscordWebhook = new ProductDiscordWebhookUpdateRequest(webhookUrl),
                },
                cancellationToken)
            .ConfigureAwait(false);
        return RequireDiscordConfiguration(response.DiscordWebhookConfiguration);
    }

    public async Task<ProductDiscordWebhookConfiguration> DeleteDiscordWebhookAsync(
        CancellationToken cancellationToken = default)
        => RequireDiscordConfiguration((await SendAsync(
                CreateRequest(ProductIpcProtocol.NotificationDiscordDeleteMethod),
                cancellationToken)
            .ConfigureAwait(false)).DiscordWebhookConfiguration);

    public async Task<IReadOnlyList<ProductNotificationDeliverySummary>> ListNotificationHistoryAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var result = new List<ProductNotificationDeliverySummary>(maximumCount);
        var ids = new HashSet<Guid>();
        var offset = 0;
        while (result.Count < maximumCount)
        {
            var limit = Math.Min(50, maximumCount - result.Count);
            var response = await SendAsync(
                    CreateRequest(ProductIpcProtocol.NotificationHistoryMethod) with
                    {
                        ListOffset = offset,
                        ListLimit = limit,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var page = response.NotificationPage ?? throw MissingPayload("notification page");
            if (page.Offset != offset || page.NextOffset < offset || page.Deliveries.Count > limit ||
                page.Deliveries.Any(delivery =>
                    delivery.DispatchId == Guid.Empty ||
                    delivery.EventId == Guid.Empty ||
                    !ids.Add(delivery.DispatchId)))
            {
                throw InvalidPage("notification");
            }

            result.AddRange(page.Deliveries);
            if (!page.HasMore || result.Count >= maximumCount)
            {
                return result.AsReadOnly();
            }

            if (page.NextOffset <= offset)
            {
                throw InvalidPage("notification");
            }

            offset = page.NextOffset;
        }

        return result.AsReadOnly();
    }

    public async Task<ProductNotificationPreferences> GetNotificationPreferencesAsync(
        CancellationToken cancellationToken = default)
        => (await SendAsync(
                CreateRequest(ProductIpcProtocol.NotificationPreferencesStatusMethod),
                cancellationToken)
            .ConfigureAwait(false)).NotificationPreferences
           ?? throw MissingPayload("notification preferences");

    public async Task<ProductNotificationPreferences> SetNotificationPreferencesAsync(
        ProductNotificationPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ProductNotificationPreferencesValidator.ValidateAndThrow(preferences);
        return (await SendAsync(
                CreateRequest(ProductIpcProtocol.NotificationPreferencesSetMethod) with
                {
                    NotificationPreferences = preferences,
                },
                cancellationToken)
            .ConfigureAwait(false)).NotificationPreferences
               ?? throw MissingPayload("notification preferences");
    }

    public async Task<IReadOnlyList<ProductProviderSummary>> ListProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<ProductProviderSummary>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (true)
        {
            var response = await SendAsync(
                    CreateRequest(ProductIpcProtocol.ProviderListMethod) with
                    {
                        ListOffset = offset,
                        ListLimit = 20,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var page = response.ProviderPage ?? throw MissingPayload("provider page");
            if (page.Offset != offset || page.NextOffset < offset || page.Providers.Count > 20 ||
                page.Providers.Any(provider =>
                    !IsValidProvider(provider) || !ids.Add(provider.Id)))
            {
                throw InvalidPage("provider");
            }

            result.AddRange(page.Providers);
            if (result.Count > 128)
            {
                throw new ProductServiceClientException(
                    "protocol.page_limit_exceeded",
                    "Service returned more providers than the client limit.");
            }

            if (!page.HasMore) return result.AsReadOnly();
            if (page.NextOffset <= offset) throw InvalidPage("provider");
            offset = page.NextOffset;
        }
    }

    public async Task<IReadOnlyList<ProductTrustedProviderPublisherSummary>> ListProviderPublishersAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<ProductTrustedProviderPublisherSummary>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (true)
        {
            var response = await SendAsync(
                    CreateRequest(ProductIpcProtocol.ProviderPublisherListMethod) with
                    {
                        ListOffset = offset,
                        ListLimit = 20,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var page = response.ProviderPublisherPage ?? throw MissingPayload("provider publisher page");
            if (page.Offset != offset || page.NextOffset < offset || page.Publishers.Count > 20 ||
                page.Publishers.Any(publisher =>
                    !IsSafeProviderIdentifier(publisher.PublisherId) ||
                    publisher.PublicKeySha256.Length != 64 ||
                    publisher.PublicKeySha256.Any(character => !Uri.IsHexDigit(character)) ||
                    !ids.Add(publisher.PublisherId)))
            {
                throw InvalidPage("provider publisher");
            }

            result.AddRange(page.Publishers);
            if (result.Count > 128)
            {
                throw new ProductServiceClientException(
                    "protocol.page_limit_exceeded",
                    "Service returned more provider publishers than the client limit.");
            }

            if (!page.HasMore) return result.AsReadOnly();
            if (page.NextOffset <= offset) throw InvalidPage("provider publisher");
            offset = page.NextOffset;
        }
    }

    public async Task<ProductProviderSummary> SetProviderEnabledAsync(
        string providerId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
                CreateProviderRequest(ProductIpcProtocol.ProviderSetEnabledMethod, providerId) with
                {
                    ProviderEnabled = enabled,
                },
                cancellationToken)
            .ConfigureAwait(false);
        return RequireProvider(response.Provider, providerId);
    }

    public async Task<ProductProviderHealthCheckResult> CheckProviderHealthAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
                CreateProviderRequest(ProductIpcProtocol.ProviderHealthMethod, providerId),
                cancellationToken)
            .ConfigureAwait(false);
        var result = response.ProviderHealth ?? throw MissingPayload("provider health result");
        if (!string.Equals(result.ProviderId, providerId, StringComparison.Ordinal) ||
            (result.Success && result.ErrorCode is not null) ||
            (!result.Success &&
             (string.IsNullOrWhiteSpace(result.ErrorCode) || result.ErrorCode.Length > 128 ||
              result.ErrorCode.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))))
        {
            throw new ProductServiceClientException(
                "protocol.payload_invalid",
                "Service returned an invalid provider health result.");
        }

        return result;
    }

    public async Task UninstallProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default)
        => _ = await SendAsync(
                CreateProviderRequest(ProductIpcProtocol.ProviderUninstallMethod, providerId),
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<ProductTrustedProviderPublisherSummary> PinProviderPublisherAsync(
        ProductPinProviderPublisherRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendAsync(
                CreateRequest(ProductIpcProtocol.ProviderPublisherPinMethod) with
                {
                    ProviderPublisherPin = request,
                },
                cancellationToken)
            .ConfigureAwait(false);
        var publisher = response.ProviderPublisher ?? throw MissingPayload("provider publisher");
        if (!string.Equals(publisher.PublisherId, request.PublisherId, StringComparison.Ordinal) ||
            !IsValidProviderPublisher(publisher))
        {
            throw new ProductServiceClientException(
                "protocol.payload_invalid",
                "Service returned an invalid provider publisher summary.");
        }

        return publisher;
    }

    public async Task RemoveProviderPublisherAsync(
        string publisherId,
        CancellationToken cancellationToken = default)
    {
        ValidateProviderIdentifier(publisherId, nameof(publisherId));
        _ = await SendAsync(
                CreateRequest(ProductIpcProtocol.ProviderPublisherRemoveMethod) with
                {
                    ProviderPublisherId = publisherId,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ProductProviderSummary> InstallProviderFromInboxAsync(
        ProductProviderInstallFromInboxRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ipcRequest = CreateRequest(ProductIpcProtocol.ProviderInstallMethod) with
        {
            ProviderInstall = request,
        };
        if (ProductIpcRequestValidator.Validate(ipcRequest) is { } validation)
        {
            throw new ArgumentException(validation.Message, nameof(request));
        }

        var response = await SendAsync(ipcRequest, cancellationToken).ConfigureAwait(false);
        return RequireProvider(response.Provider, request.ExpectedProviderId);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private async Task<ProductServerMutationResult> MutateAsync(
        string method,
        Guid serverId,
        CancellationToken cancellationToken,
        bool acceptMinecraftEula = false)
    {
        var request = CreateServerRequest(method, serverId) with
        {
            AcceptMinecraftEula = acceptMinecraftEula ? true : null,
        };
        if (acceptMinecraftEula)
        {
            // Advertise the operation's real minimum instead of allowing an older Service to
            // negotiate 1.5 and silently ignore an unknown JSON field.
            request = request with
            {
                ClientMinimumApiVersion = ProductApiProtocol.MinecraftEulaConsentVersion,
            };
        }

        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Mutation
            ?? throw new ProductServiceClientException(
                "protocol.payload_missing",
                "Service mutation response did not include a result.");
    }

    private async Task<ProductUpdateOperationResult> SendUpdateAsync(
        string method,
        ProductUpdateChannel channel,
        DateTimeOffset? notBeforeUtc,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        if (notBeforeUtc is { Offset: var offset } && offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Update schedule must use UTC.", nameof(notBeforeUtc));
        }

        var response = await SendAsync(
            CreateRequest(method) with
            {
                UpdateChannel = channel,
                UpdateNotBeforeUtc = notBeforeUtc,
            },
            cancellationToken).ConfigureAwait(false);
        return response.Update
            ?? throw new ProductServiceClientException(
                "protocol.payload_missing",
                "Service update response did not include its payload.");
    }

    private async Task<ProductRemoteAccessStatus> SendRemoteAccessAsync(
        string method,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(CreateRequest(method), cancellationToken).ConfigureAwait(false);
        return response.RemoteAccess ?? throw MissingPayload("remote-access status");
    }

    private async Task<ProductIpcResponse> SendAsync(
        ProductIpcRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(GetRequestTimeout(request.Method));
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            try
            {
                await pipe.ConnectAsync(ConnectTimeout, deadline.Token).ConfigureAwait(false);
                await ProductIpcClientFrameCodec.WriteRequestAsync(pipe, request, deadline.Token)
                    .ConfigureAwait(false);
                var response = await ProductIpcClientFrameCodec.ReadResponseAsync(
                        pipe,
                        request.RequestId,
                        deadline.Token)
                    .ConfigureAwait(false);
                if (!response.Success)
                {
                    throw new ProductServiceClientException(
                        response.Error!.Code,
                        response.Error.Message);
                }

                return response;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProductServiceClientException(
                    "service.timeout",
                    "Muhun MCSV Service did not respond before the local deadline.",
                    exception);
            }
            catch (TimeoutException exception)
            {
                throw new ProductServiceClientException(
                    "service.timeout",
                    "Muhun MCSV Service did not accept the local connection before the deadline.",
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new ProductServiceClientException(
                    "service.access_denied",
                    "The current Windows account is not authorized to control Muhun MCSV Service.",
                    exception);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                throw new ProductServiceClientException(
                    "service.connection_failed",
                    "Muhun MCSV Service IPC connection failed.",
                    exception);
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private static ProductIpcRequest CreateRequest(string method)
        => new(
            ProductIpcProtocol.CurrentSchemaVersion,
            Guid.NewGuid(),
            method,
            ProductApiProtocol.MinimumSupportedVersion,
            ProductApiProtocol.CurrentVersion);

    private static ProductIpcRequest CreateServerRequest(string method, Guid serverId)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(serverId));
        }

        return CreateRequest(method) with { ServerId = serverId };
    }

    private static ProductIpcRequest CreateImportRequest(string method, Guid importId)
    {
        if (importId == Guid.Empty)
        {
            throw new ArgumentException("Import id must not be empty.", nameof(importId));
        }

        return CreateRequest(method) with { ImportId = importId };
    }

    private static ProductIpcRequest CreateModpackUpdateRequest(string method, Guid updateId)
    {
        if (updateId == Guid.Empty)
        {
            throw new ArgumentException("Modpack update id must not be empty.", nameof(updateId));
        }

        return CreateRequest(method) with { ModpackUpdateId = updateId };
    }

    private static ProductIpcRequest CreateRemoteAccountRequest(string method, string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        return CreateRequest(method) with { RemoteUsername = username };
    }

    private static ProductIpcRequest CreateProviderRequest(string method, string providerId)
    {
        ValidateProviderIdentifier(providerId, nameof(providerId));
        return CreateRequest(method) with { ProviderId = providerId };
    }

    private static void ValidateProviderIdentifier(string? value, string parameterName)
    {
        if (!IsSafeProviderIdentifier(value))
        {
            throw new ArgumentException("Provider identifier is invalid.", parameterName);
        }
    }

    private static bool IsSafeProviderIdentifier(string? value)
        => value is { Length: >= 3 and <= 96 }
           && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9'
               or '.' or '_' or '-');

    internal static TimeSpan GetRequestTimeout(string method)
    {
        if (method is
            ProductIpcProtocol.ServerStopMethod or
            ProductIpcProtocol.ServerRestartMethod or
            ProductIpcProtocol.ServerDeleteMethod or
            ProductIpcProtocol.ServerBackupCreateMethod or
            ProductIpcProtocol.ServerBackupRestoreMethod or
            ProductIpcProtocol.ServerImportCommitMethod or
            ProductIpcProtocol.ServerModpackUpdateCommitMethod or
            ProductIpcProtocol.UpdateDownloadMethod or
            ProductIpcProtocol.RemoteAccessStartMethod or
            ProductIpcProtocol.RemoteAccessStopMethod or
            ProductIpcProtocol.RemoteAccessReconnectMethod or
            ProductIpcProtocol.ProviderInstallMethod or
            ProductIpcProtocol.ProviderUninstallMethod)
        {
            return LongMutationRequestTimeout;
        }

        if (method is
            ProductIpcProtocol.ServerSettingsUpdateMethod or
            ProductIpcProtocol.ServerRegisterMethod or
            ProductIpcProtocol.ServerRemoveMethod or
            ProductIpcProtocol.ServerStartMethod or
            ProductIpcProtocol.ServerCommandMethod or
            ProductIpcProtocol.ServerImportBeginMethod or
            ProductIpcProtocol.ServerImportCancelMethod or
            ProductIpcProtocol.ServerModpackUpdateBeginMethod or
            ProductIpcProtocol.ServerModpackUpdateCancelMethod or
            ProductIpcProtocol.UpdateCheckMethod or
            ProductIpcProtocol.UpdateScheduleMethod or
            ProductIpcProtocol.RemoteAccountCreateMethod or
            ProductIpcProtocol.RemoteAccountAuthorizationUpdateMethod or
            ProductIpcProtocol.RemoteAccountPinUpdateMethod or
            ProductIpcProtocol.RemoteAccountPinRevealMethod or
            ProductIpcProtocol.RemoteAccountDeleteMethod or
            ProductIpcProtocol.RemoteDeviceRevokeMethod or
            ProductIpcProtocol.NotificationDiscordSetMethod or
            ProductIpcProtocol.NotificationDiscordDeleteMethod or
            ProductIpcProtocol.NotificationPreferencesSetMethod or
            ProductIpcProtocol.ProviderSetEnabledMethod or
            ProductIpcProtocol.ProviderHealthMethod or
            ProductIpcProtocol.ProviderPublisherPinMethod or
            ProductIpcProtocol.ProviderPublisherRemoveMethod)
        {
            return MutationRequestTimeout;
        }

        return method.StartsWith("provider.", StringComparison.Ordinal)
            ? ProviderRequestTimeout
            : RequestTimeout;
    }

    private static void ValidateBackupId(string backupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        if (backupId.Length != 64 || backupId.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Backup id is invalid.", nameof(backupId));
        }
    }

    private static void ValidateRegistrationPayload(
        ProductServerRegistration registration,
        Guid expectedServerId)
    {
        if (registration.Id != expectedServerId ||
            string.IsNullOrWhiteSpace(registration.Name) || registration.Name.Length > 128 ||
            !IsSafeRelativePath(registration.ServerDirectory) ||
            !IsSafeRelativePath(registration.JavaRuntimePath) ||
            !IsSafeRelativePath(registration.ServerJarPath) ||
            registration.MinimumMemoryMb is < 128 or > 1_048_576 ||
            registration.MaximumMemoryMb < registration.MinimumMemoryMb ||
            registration.MaximumMemoryMb > 1_048_576 ||
            registration.Port is < 1 or > 65535 ||
            registration.JavaArgumentFilePaths.Count > 128 ||
            registration.JvmArguments.Count > 128 ||
            registration.ServerArguments.Count > 128)
        {
            throw new ProductServiceClientException(
                "protocol.payload_invalid",
                "Service returned an invalid server registration.");
        }
    }

    private static bool IsSafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            Path.IsPathRooted(value) || Path.IsPathFullyQualified(value) ||
            value.Any(char.IsControl))
        {
            return false;
        }

        return !value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    private static bool IsValidBackupSummary(ProductServerBackupSummary backup)
        => backup is not null
           && backup.BackupId.Length == 64
           && backup.BackupId.All(Uri.IsHexDigit)
           && backup.ArchiveBytes > 0
           && !string.IsNullOrWhiteSpace(backup.FileName)
           && backup.FileName.Length <= 240
           && string.Equals(Path.GetFileName(backup.FileName), backup.FileName, StringComparison.Ordinal)
           && !Path.IsPathFullyQualified(backup.FileName)
           && !backup.FileName.Any(char.IsControl);

    private static ProductServiceClientException MissingPayload(string payload)
        => new(
            "protocol.payload_missing",
            $"Service response did not include its {payload}.");

    private static ProductServiceClientException InvalidPage(string resource)
        => new(
            "protocol.page_invalid",
            $"Service returned an invalid or duplicate {resource} page.");

    private static ProductServerStatus RequireServer(ProductServerStatus? server)
        => server ?? throw new ProductServiceClientException(
            "protocol.payload_missing",
            "Service response did not include server status.");

    private static ProductServerImportStatus RequireImport(ProductServerImportStatus? import)
        => import ?? throw new ProductServiceClientException(
            "protocol.payload_missing",
            "Service response did not include import status.");

    private static ProductServerModpackUpdateStatus RequireModpackUpdate(
        ProductServerModpackUpdateStatus? update)
        => update ?? throw new ProductServiceClientException(
            "protocol.payload_missing",
            "Service response did not include modpack update status.");

    private static ProductDiscordWebhookConfiguration RequireDiscordConfiguration(
        ProductDiscordWebhookConfiguration? configuration)
        => configuration ?? throw MissingPayload("Discord webhook configuration");

    private static ProductProviderSummary RequireProvider(
        ProductProviderSummary? provider,
        string expectedProviderId)
    {
        if (provider is null ||
            !string.Equals(provider.Id, expectedProviderId, StringComparison.Ordinal) ||
            !IsValidProvider(provider))
        {
            throw new ProductServiceClientException(
                "protocol.payload_invalid",
                "Service returned an invalid provider summary.");
        }

        return provider;
    }

    private static bool IsValidProvider(ProductProviderSummary? provider)
        => provider is not null
           && IsSafeProviderIdentifier(provider.Id)
           && !string.IsNullOrWhiteSpace(provider.DisplayName)
           && provider.DisplayName.Length <= 128
           && !provider.DisplayName.Any(char.IsControl)
           && !string.IsNullOrWhiteSpace(provider.Version)
           && provider.Version.Length <= 96
           && !provider.Version.Any(char.IsControl)
           && IsSafeProviderIdentifier(provider.PublisherId)
           && Enum.IsDefined(provider.Health)
           && provider.Capabilities is not null
           && provider.Permissions is not null
           && provider.Capabilities.Count <= 64
           && provider.Permissions.Count <= 64
           && provider.Capabilities.All(IsValidProviderToken)
           && provider.Permissions.All(IsValidProviderToken)
           && provider.Capabilities.Distinct(StringComparer.Ordinal).Count() == provider.Capabilities.Count
           && provider.Permissions.Distinct(StringComparer.Ordinal).Count() == provider.Permissions.Count
           && provider.ConsecutiveFailures >= 0
           && (provider.LastError is null ||
               (provider.LastError.Length <= 512 && !provider.LastError.Any(char.IsControl)));

    private static bool IsValidProviderPublisher(ProductTrustedProviderPublisherSummary? publisher)
        => publisher is not null
           && IsSafeProviderIdentifier(publisher.PublisherId)
           && publisher.PublicKeySha256.Length == 64
           && publisher.PublicKeySha256.All(Uri.IsHexDigit);

    private static bool IsValidProviderToken(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 96
           && !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));
}
