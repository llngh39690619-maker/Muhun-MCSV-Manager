using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;
using MinecraftServerManager.Data;

namespace MinecraftServerManager.Service;

public sealed class ProductIpcMessageProcessor
{
    private readonly ProductServiceState _state;
    private readonly ProductServerRuntime? _runtime;
    private readonly IProductUpdateCoordinator? _updates;
    private readonly ProductServerImportService? _imports;
    private readonly IProductRemoteWebSupervisor? _remoteWeb;
    private readonly ProductRemoteAccountStore? _remoteAccounts;
    private readonly ProductRememberedDeviceStore? _remoteDevices;
    private readonly ProductDiscordWebhookSettings? _discordWebhook;
    private readonly NotificationOutboxStore? _notificationOutbox;
    private readonly ProductServerBackupManager? _backups;
    private readonly ProductServerModpackUpdateCoordinator? _modpackUpdates;
    private readonly ProductProviderCoordinator? _providers;
    private readonly ProductPlayerPresenceTracker? _players;
    private readonly ProductNotificationPreferenceStore? _notificationPreferences;
    private readonly ProductServerAdministrationReader? _administration;
    private readonly ProductServerPropertiesManager? _properties;

    public ProductIpcMessageProcessor(ProductServiceState state)
        : this(state, runtime: null, updates: null, imports: null, remoteWeb: null, remoteAccounts: null, remoteDevices: null, discordWebhook: null, notificationOutbox: null, backups: null)
    {
    }

    public ProductIpcMessageProcessor(ProductServiceState state, ProductServerRuntime? runtime)
        : this(state, runtime, updates: null, imports: null, remoteWeb: null, remoteAccounts: null, remoteDevices: null, discordWebhook: null, notificationOutbox: null, backups: null)
    {
    }

    public ProductIpcMessageProcessor(
        ProductServiceState state,
        ProductServerRuntime? runtime,
        IProductUpdateCoordinator? updates)
        : this(state, runtime, updates, imports: null, remoteWeb: null, remoteAccounts: null, remoteDevices: null, discordWebhook: null, notificationOutbox: null, backups: null)
    {
    }

    public ProductIpcMessageProcessor(
        ProductServiceState state,
        ProductServerRuntime? runtime,
        IProductUpdateCoordinator? updates,
        ProductServerImportService? imports)
        : this(state, runtime, updates, imports, remoteWeb: null, remoteAccounts: null, remoteDevices: null, discordWebhook: null, notificationOutbox: null, backups: null)
    {
    }

    public ProductIpcMessageProcessor(
        ProductServiceState state,
        ProductServerRuntime? runtime,
        IProductUpdateCoordinator? updates,
        ProductServerImportService? imports,
        IProductRemoteWebSupervisor? remoteWeb,
        ProductRemoteAccountStore? remoteAccounts,
        ProductRememberedDeviceStore? remoteDevices)
        : this(
            state,
            runtime,
            updates,
            imports,
            remoteWeb,
            remoteAccounts,
            remoteDevices,
            discordWebhook: null,
            notificationOutbox: null,
            backups: null)
    {
    }

    public ProductIpcMessageProcessor(
        ProductServiceState state,
        ProductServerRuntime? runtime,
        IProductUpdateCoordinator? updates,
        ProductServerImportService? imports,
        IProductRemoteWebSupervisor? remoteWeb,
        ProductRemoteAccountStore? remoteAccounts,
        ProductRememberedDeviceStore? remoteDevices,
        ProductDiscordWebhookSettings? discordWebhook,
        NotificationOutboxStore? notificationOutbox)
        : this(
            state,
            runtime,
            updates,
            imports,
            remoteWeb,
            remoteAccounts,
            remoteDevices,
            discordWebhook,
            notificationOutbox,
            backups: null)
    {
    }

    public ProductIpcMessageProcessor(
        ProductServiceState state,
        ProductServerRuntime? runtime,
        IProductUpdateCoordinator? updates,
        ProductServerImportService? imports,
        IProductRemoteWebSupervisor? remoteWeb,
        ProductRemoteAccountStore? remoteAccounts,
        ProductRememberedDeviceStore? remoteDevices,
        ProductDiscordWebhookSettings? discordWebhook,
        NotificationOutboxStore? notificationOutbox,
        ProductServerBackupManager? backups,
        ProductServerModpackUpdateCoordinator? modpackUpdates = null,
        ProductProviderCoordinator? providers = null,
        ProductPlayerPresenceTracker? players = null,
        ProductNotificationPreferenceStore? notificationPreferences = null,
        ProductServerAdministrationReader? administration = null,
        ProductServerPropertiesManager? properties = null)
    {
        _state = state;
        _runtime = runtime;
        _updates = updates;
        _imports = imports;
        _remoteWeb = remoteWeb;
        _remoteAccounts = remoteAccounts;
        _remoteDevices = remoteDevices;
        _discordWebhook = discordWebhook;
        _notificationOutbox = notificationOutbox;
        _backups = backups;
        _modpackUpdates = modpackUpdates;
        _providers = providers;
        _players = players;
        _notificationPreferences = notificationPreferences;
        _administration = administration;
        _properties = properties;
    }

    /// <summary>Compatibility helper for the non-I/O handshake foundation tests.</summary>
    public ProductIpcResponse Process(ProductIpcRequest? request)
        => ProcessAsync(request, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<ProductIpcResponse> ProcessAsync(
        ProductIpcRequest? request,
        CancellationToken cancellationToken)
    {
        var validationError = ProductIpcRequestValidator.Validate(request);
        if (validationError is not null)
        {
            return Failure(request?.RequestId ?? Guid.Empty, validationError);
        }

        var negotiation = ProductApiProtocol.Negotiate(
            request!.ClientMinimumApiVersion,
            request.ClientMaximumApiVersion);
        if (!negotiation.IsCompatible)
        {
            var code = negotiation.Status == ProductApiNegotiationStatus.ClientTooOld
                ? "protocol.client_too_old"
                : "protocol.client_too_new";
            return Failure(
                request.RequestId,
                new ProductIpcError(code, "Client and Service API versions are incompatible."));
        }

        if (!_state.IsReady || _state.InstallationId == Guid.Empty)
        {
            return Failure(
                request.RequestId,
                new ProductIpcError("service.not_ready", "Muhun MCSV Service is not ready."));
        }

        if (request.Method == ProductIpcProtocol.HandshakeMethod)
        {
            return Handshake(request, negotiation.SelectedVersion!.Value);
        }

        if (negotiation.SelectedVersion!.Value.CompareTo(new ProductApiVersion(1, 1)) < 0)
        {
            return Failure(
                request.RequestId,
                new ProductIpcError(
                    "protocol.method_version_unsupported",
                    "Server runtime methods require API version 1.1 or newer."));
        }

        if (request.AcceptMinecraftEula is not null
            && negotiation.SelectedVersion.Value.CompareTo(
                ProductApiProtocol.MinecraftEulaConsentVersion) < 0)
        {
            return Failure(
                request.RequestId,
                new ProductIpcError(
                    "protocol.field_version_unsupported",
                    "Minecraft EULA confirmation requires API version 1.6 or newer."));
        }

        if (ProductServerInstanceSettingsContract.HasAnyServiceInstanceSetting(request.ServerSettings)
            && negotiation.SelectedVersion.Value.CompareTo(
                ProductApiProtocol.ServiceInstanceSettingsVersion) < 0)
        {
            return Failure(
                request.RequestId,
                new ProductIpcError(
                    "protocol.field_version_unsupported",
                    "Service-owned instance settings require API version 1.8 or newer."));
        }

        var isUpdateMethod = request.Method is
            ProductIpcProtocol.UpdateStatusMethod or
            ProductIpcProtocol.UpdateCheckMethod or
            ProductIpcProtocol.UpdateDownloadMethod or
            ProductIpcProtocol.UpdateScheduleMethod;
        if (isUpdateMethod)
        {
            if (negotiation.SelectedVersion.Value.CompareTo(new ProductApiVersion(1, 2)) < 0)
            {
                return Failure(
                    request.RequestId,
                    new ProductIpcError(
                        "protocol.method_version_unsupported",
                        "Product update methods require API version 1.2 or newer."));
            }

            if (_updates is null)
            {
                return Failure(
                    request.RequestId,
                    new ProductIpcError("service.update_unavailable", "Product update service is unavailable."));
            }

            try
            {
                return await ProcessUpdateAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (ProductOperationErrorPolicy.IsExpected(error))
            {
                var publicError = ProductOperationErrorPolicy.ToPublic(error);
                return Failure(
                    request.RequestId,
                    new ProductIpcError(publicError.Code, publicError.Message));
            }
        }

        var isRemoteMethod = request.Method.StartsWith("remote.", StringComparison.Ordinal);
        if (isRemoteMethod)
        {
            if (negotiation.SelectedVersion.Value.CompareTo(new ProductApiVersion(1, 2)) < 0)
            {
                return Failure(
                    request.RequestId,
                    new ProductIpcError(
                        "protocol.method_version_unsupported",
                        "Remote management methods require API version 1.2 or newer."));
            }

            if (_remoteWeb is null || _remoteAccounts is null || _remoteDevices is null)
            {
                return Failure(
                    request.RequestId,
                    new ProductIpcError(
                        "service.remote_unavailable",
                        "Remote management is unavailable."));
            }

            try
            {
                return await ProcessRemoteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (ProductOperationErrorPolicy.IsExpected(error))
            {
                return Failure(request.RequestId, ToRemoteError(error));
            }
        }

        var isNotificationMethod = request.Method.StartsWith("notification.", StringComparison.Ordinal);
        if (isNotificationMethod)
        {
            if (negotiation.SelectedVersion.Value.CompareTo(new ProductApiVersion(1, 2)) < 0)
            {
                return Failure(
                    request.RequestId,
                    new ProductIpcError(
                        "protocol.method_version_unsupported",
                        "Notification management methods require API version 1.2 or newer."));
            }

            if (_discordWebhook is null || _notificationOutbox is null)
            {
                return Failure(
                    request.RequestId,
                    new ProductIpcError(
                        "service.notifications_unavailable",
                        "Notification management is unavailable."));
            }

            try
            {
                return await ProcessNotificationAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (ProductOperationErrorPolicy.IsExpected(error))
            {
                return Failure(request.RequestId, ToNotificationError(error));
            }
        }

        var isModpackUpdateMethod = request.Method is
            ProductIpcProtocol.ServerModpackUpdateBeginMethod or
            ProductIpcProtocol.ServerModpackUpdateCommitMethod or
            ProductIpcProtocol.ServerModpackUpdateStatusMethod or
            ProductIpcProtocol.ServerModpackUpdateCancelMethod;
        if (isModpackUpdateMethod)
        {
            if (negotiation.SelectedVersion.Value.CompareTo(new ProductApiVersion(1, 3)) < 0)
            {
                return Failure(
                    request.RequestId,
                    new ProductIpcError(
                        "protocol.method_version_unsupported",
                        "Server modpack update methods require API version 1.3 or newer."));
            }

            if (_modpackUpdates is null)
            {
                return Failure(
                    request.RequestId,
                    new ProductIpcError(
                        "service.modpack_update_unavailable",
                        "Server modpack update service is unavailable."));
            }

            try
            {
                return await ProcessModpackUpdateAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (ProductOperationErrorPolicy.IsExpected(error))
            {
                return Failure(request.RequestId, ToModpackUpdateError(error));
            }
        }

        var isProviderMethod = request.Method.StartsWith("provider.", StringComparison.Ordinal);
        if (isProviderMethod)
        {
            if (negotiation.SelectedVersion.Value.CompareTo(new ProductApiVersion(1, 4)) < 0)
            {
                return Failure(
                    request.RequestId,
                    new ProductIpcError(
                        "protocol.method_version_unsupported",
                        "Provider management methods require API version 1.4 or newer."));
            }

            if (_providers is null)
            {
                return Failure(
                    request.RequestId,
                    new ProductIpcError(
                        "service.provider_unavailable",
                        "Provider management is unavailable."));
            }

            try
            {
                return await ProcessProviderAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (ProductProviderErrorPolicy.IsExpected(error))
            {
                var publicError = ProductProviderErrorPolicy.ToPublic(error);
                return Failure(
                    request.RequestId,
                    new ProductIpcError(publicError.Code, publicError.Message));
            }
        }

        var isServerAdministrationMethod = request.Method is
            ProductIpcProtocol.ServerRegistrationMethod or
            ProductIpcProtocol.ServerSettingsUpdateMethod or
            ProductIpcProtocol.ServerBackupListMethod or
            ProductIpcProtocol.ServerBackupCreateMethod or
            ProductIpcProtocol.ServerBackupRestoreMethod;
        if (isServerAdministrationMethod &&
            negotiation.SelectedVersion.Value.CompareTo(new ProductApiVersion(1, 3)) < 0)
        {
            return Failure(
                request.RequestId,
                new ProductIpcError(
                    "protocol.method_version_unsupported",
                    "Server administration methods require API version 1.3 or newer."));
        }

        var isLocalFileAdministrationMethod = request.Method is
            ProductIpcProtocol.ServerDirectoryMethod or
            ProductIpcProtocol.ServerAdministrationMethod or
            ProductIpcProtocol.ServerDeleteMethod;
        if (isLocalFileAdministrationMethod &&
            negotiation.SelectedVersion.Value.CompareTo(new ProductApiVersion(1, 5)) < 0)
        {
            return Failure(
                request.RequestId,
                new ProductIpcError(
                    "protocol.method_version_unsupported",
                    "Local server file administration requires API version 1.5 or newer."));
        }

        var isServerPropertiesMethod = request.Method is
            ProductIpcProtocol.ServerPropertiesReadMethod or
            ProductIpcProtocol.ServerPropertiesUpdateMethod;
        if (isServerPropertiesMethod &&
            negotiation.SelectedVersion.Value.CompareTo(
                ProductApiProtocol.ServerPropertiesEditorVersion) < 0)
        {
            return Failure(
                request.RequestId,
                new ProductIpcError(
                    "protocol.method_version_unsupported",
                    "Service-owned server.properties editing requires API version 1.7 or newer."));
        }

        if (_runtime is null)
        {
            return Failure(
                request.RequestId,
                new ProductIpcError("service.runtime_unavailable", "Server runtime is unavailable."));
        }

        if (request.Method == ProductIpcProtocol.ServerAdministrationMethod && _administration is null)
        {
            return Failure(
                request.RequestId,
                new ProductIpcError(
                    "service.administration_unavailable",
                    "Bounded server administration inspection is unavailable."));
        }

        if (isServerPropertiesMethod && _properties is null)
        {
            return Failure(
                request.RequestId,
                new ProductIpcError(
                    "service.properties_unavailable",
                    "Service-owned server.properties editing is unavailable."));
        }

        if (request.Method == ProductIpcProtocol.ServerPlayersMethod && _players is null)
        {
            return Failure(
                request.RequestId,
                new ProductIpcError(
                    "service.players_unavailable",
                    "Service player presence tracking is unavailable."));
        }

        var isBackupMethod = request.Method is
            ProductIpcProtocol.ServerBackupListMethod or
            ProductIpcProtocol.ServerBackupCreateMethod or
            ProductIpcProtocol.ServerBackupRestoreMethod;
        if (isBackupMethod && _backups is null)
        {
            return Failure(
                request.RequestId,
                new ProductIpcError(
                    "service.backup_unavailable",
                    "Service-owned backup management is unavailable."));
        }

        var isImportMethod = request.Method is
            ProductIpcProtocol.ServerImportBeginMethod or
            ProductIpcProtocol.ServerImportCommitMethod or
            ProductIpcProtocol.ServerImportStatusMethod or
            ProductIpcProtocol.ServerImportCancelMethod;
        if (isImportMethod && _imports is null)
        {
            return Failure(
                request.RequestId,
                new ProductIpcError("service.import_unavailable", "Server import service is unavailable."));
        }

        try
        {
            return request.Method switch
            {
                ProductIpcProtocol.ServerListMethod => List(request),
                ProductIpcProtocol.ServerStatusListMethod => ListStatuses(request),
                ProductIpcProtocol.ServerStatusMethod => Success(request.RequestId) with
                {
                    Server = _runtime.GetStatus(request.ServerId!.Value),
                },
                ProductIpcProtocol.ServerRegistrationMethod => Success(request.RequestId) with
                {
                    Registration = _runtime.GetRegistration(request.ServerId!.Value),
                },
                ProductIpcProtocol.ServerSettingsUpdateMethod => await UpdateSettingsAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false),
                ProductIpcProtocol.ServerRegisterMethod => await RegisterAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                ProductIpcProtocol.ServerRemoveMethod => await RemoveAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                ProductIpcProtocol.ServerDirectoryMethod => Success(request.RequestId) with
                {
                    ServerDirectory = _runtime.GetDirectoryInfo(request.ServerId!.Value),
                },
                ProductIpcProtocol.ServerAdministrationMethod => Success(request.RequestId) with
                {
                    ServerAdministration = _administration!.Capture(request.ServerId!.Value),
                },
                ProductIpcProtocol.ServerPropertiesReadMethod => Success(request.RequestId) with
                {
                    ServerProperties = await _properties!.ReadAsync(
                            request.ServerId!.Value,
                            cancellationToken)
                        .ConfigureAwait(false),
                },
                ProductIpcProtocol.ServerPropertiesUpdateMethod => Success(request.RequestId) with
                {
                    ServerProperties = await _properties!.SaveAsync(
                            request.ServerId!.Value,
                            request.ServerPropertiesUpdate!,
                            cancellationToken)
                        .ConfigureAwait(false),
                },
                ProductIpcProtocol.ServerDeleteMethod => Success(request.RequestId) with
                {
                    ServerDeletion = await _runtime.DeletePermanentlyAsync(
                            request.ServerId!.Value,
                            cancellationToken)
                        .ConfigureAwait(false),
                },
                ProductIpcProtocol.ServerStartMethod => Success(request.RequestId) with
                {
                    Mutation = await _runtime.StartAsync(
                            request.ServerId!.Value,
                            request.AcceptMinecraftEula == true,
                            cancellationToken)
                        .ConfigureAwait(false),
                },
                ProductIpcProtocol.ServerStopMethod => Success(request.RequestId) with
                {
                    Mutation = await _runtime.StopAsync(request.ServerId!.Value, cancellationToken)
                        .ConfigureAwait(false),
                },
                ProductIpcProtocol.ServerRestartMethod => Success(request.RequestId) with
                {
                    Mutation = await _runtime.RestartAsync(
                            request.ServerId!.Value,
                            request.AcceptMinecraftEula == true,
                            cancellationToken)
                        .ConfigureAwait(false),
                },
                ProductIpcProtocol.ServerConsoleMethod => Success(request.RequestId) with
                {
                    Console = _runtime.ReadConsole(
                        request.ServerId!.Value,
                        request.ConsoleCursor ?? 0,
                        request.ConsoleLimit ?? 50),
                },
                ProductIpcProtocol.ServerPlayersMethod => Success(request.RequestId) with
                {
                    Players = await GetPlayersAsync(
                            request.ServerId!.Value,
                            negotiation.SelectedVersion.Value,
                            cancellationToken)
                        .ConfigureAwait(false),
                },
                ProductIpcProtocol.ServerCommandMethod => await SendCommandAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                ProductIpcProtocol.ServerBackupListMethod => Success(request.RequestId) with
                {
                    BackupPage = _backups!.List(
                        request.ServerId!.Value,
                        request.ListOffset ?? 0,
                        request.ListLimit ?? 50),
                },
                ProductIpcProtocol.ServerBackupCreateMethod => Success(request.RequestId) with
                {
                    BackupMutation = await _backups!.CreateAsync(
                            request.ServerId!.Value,
                            cancellationToken)
                        .ConfigureAwait(false),
                },
                ProductIpcProtocol.ServerBackupRestoreMethod => Success(request.RequestId) with
                {
                    BackupRestore = await _backups!.RestoreAsync(
                            request.ServerId!.Value,
                            request.BackupId!,
                            cancellationToken)
                        .ConfigureAwait(false),
                },
                ProductIpcProtocol.ServerImportBeginMethod => Success(request.RequestId) with
                {
                    Import = await _imports!.BeginAsync(request.ImportBegin!, cancellationToken)
                        .ConfigureAwait(false),
                },
                ProductIpcProtocol.ServerImportCommitMethod => Success(request.RequestId) with
                {
                    Import = await _imports!.CommitAsync(
                            request.ImportId!.Value,
                            request.ManifestSha256!,
                            cancellationToken)
                        .ConfigureAwait(false),
                },
                ProductIpcProtocol.ServerImportStatusMethod => Success(request.RequestId) with
                {
                    Import = _imports!.GetStatus(request.ImportId!.Value),
                },
                ProductIpcProtocol.ServerImportCancelMethod => Success(request.RequestId) with
                {
                    Import = await _imports!.CancelAsync(request.ImportId!.Value, cancellationToken)
                        .ConfigureAwait(false),
                },
                _ => Failure(
                    request.RequestId,
                    new ProductIpcError("protocol.method_unsupported", "IPC method is unsupported.")),
            };
        }
        catch (Exception error) when (ProductOperationErrorPolicy.IsExpected(error))
        {
            var publicError = ProductOperationErrorPolicy.ToPublic(error);
            return Failure(
                request.RequestId,
                new ProductIpcError(publicError.Code, publicError.Message));
        }
    }

    private async Task<ProductServerPlayerList> GetPlayersAsync(
        Guid serverId,
        ProductApiVersion negotiatedApiVersion,
        CancellationToken cancellationToken)
    {
        // GetStatus validates that the id belongs to the Service registry before player metadata
        // is projected. The IPC page remains below the 64 KiB frame limit even at its maximum.
        _ = _runtime!.GetStatus(serverId);
        var onlinePlayers = _players!.GetPlayers(serverId)
            .Where(player => player.Online
                             && player.Name.Length is > 0 and <= 64
                             && !player.Name.Any(char.IsControl))
            .Take(ProductServerPlayerContract.MaximumOnlinePlayers)
            .Select(player => new ProductServerPlayerSummary(player.Name, player.LastSeenUtc))
            .ToArray();
        var response = new ProductServerPlayerList(
            serverId,
            DateTimeOffset.UtcNow,
            onlinePlayers);
        if (negotiatedApiVersion.CompareTo(ProductApiProtocol.KnownPlayerRosterVersion) < 0)
        {
            return response;
        }

        var knownPlayers = await _players.GetKnownPlayersAsync(serverId, cancellationToken)
            .ConfigureAwait(false);
        return response with
        {
            KnownPlayers = knownPlayers
                .Where(player => player.Name.Length is > 0 and <= 16 &&
                                 !player.Name.Any(char.IsControl))
                .Take(ProductServerPlayerContract.MaximumKnownPlayers)
                .Select(static player => new ProductKnownPlayerSummary(
                    player.Name,
                    player.Uuid,
                    player.Online,
                    player.Operator,
                    player.Whitelisted,
                    player.Banned,
                    player.LastSeenUtc))
                .ToArray(),
        };
    }

    private async Task<ProductIpcResponse> ProcessUpdateAsync(
        ProductIpcRequest request,
        CancellationToken cancellationToken)
    {
        var channel = request.UpdateChannel!.Value;
        var result = request.Method switch
        {
            ProductIpcProtocol.UpdateStatusMethod => new ProductUpdateOperationResult(
                true,
                _updates!.GetStatus(channel)),
            ProductIpcProtocol.UpdateCheckMethod => await _updates!.CheckAsync(channel, cancellationToken)
                .ConfigureAwait(false),
            ProductIpcProtocol.UpdateDownloadMethod => await _updates!.DownloadAsync(channel, cancellationToken)
                .ConfigureAwait(false),
            ProductIpcProtocol.UpdateScheduleMethod => await _updates!.ScheduleAsync(
                    channel,
                    request.UpdateNotBeforeUtc,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unsupported product update IPC method."),
        };
        return Success(request.RequestId) with { Update = result };
    }

    private async Task<ProductIpcResponse> ProcessProviderAsync(
        ProductIpcRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case ProductIpcProtocol.ProviderListMethod:
                return ListProviders(request);
            case ProductIpcProtocol.ProviderPublisherListMethod:
                return ListProviderPublishers(request);
            case ProductIpcProtocol.ProviderSetEnabledMethod:
                return Success(request.RequestId) with
                {
                    Provider = await _providers!.SetEnabledAsync(
                            request.ProviderId!,
                            request.ProviderEnabled!.Value,
                            cancellationToken)
                        .ConfigureAwait(false),
                };
            case ProductIpcProtocol.ProviderHealthMethod:
            {
                var result = await _providers!.CheckHealthAsync(
                        request.ProviderId!,
                        cancellationToken)
                    .ConfigureAwait(false);
                var success = string.Equals(
                    result.Status,
                    ProductProviderRpcProtocol.SuccessStatus,
                    StringComparison.Ordinal) && result.Error is null;
                return Success(request.RequestId) with
                {
                    ProviderHealth = new ProductProviderHealthCheckResult(
                        request.ProviderId!,
                        success,
                        success ? null : result.Error?.Code ?? "provider.health_failed"),
                };
            }
            case ProductIpcProtocol.ProviderUninstallMethod:
                return await _providers!.UninstallAsync(request.ProviderId!, cancellationToken)
                    .ConfigureAwait(false)
                    ? Success(request.RequestId)
                    : Failure(
                        request.RequestId,
                        new ProductIpcError(
                            "provider.not_found",
                            "The selected provider is not registered."));
            case ProductIpcProtocol.ProviderInstallMethod:
                return Success(request.RequestId) with
                {
                    Provider = await _providers!.InstallFromInboxAsync(
                            request.ProviderInstall!,
                            cancellationToken)
                        .ConfigureAwait(false),
                };
            case ProductIpcProtocol.ProviderPublisherPinMethod:
                return Success(request.RequestId) with
                {
                    ProviderPublisher = await _providers!.PinPublisherAsync(
                            request.ProviderPublisherPin!,
                            cancellationToken)
                        .ConfigureAwait(false),
                };
            case ProductIpcProtocol.ProviderPublisherRemoveMethod:
                return await _providers!.RemovePublisherAsync(
                        request.ProviderPublisherId!,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ? Success(request.RequestId)
                    : Failure(
                        request.RequestId,
                        new ProductIpcError(
                            "provider.publisher_not_found",
                            "The selected provider publisher is not pinned."));
            default:
                return Failure(
                    request.RequestId,
                    new ProductIpcError("protocol.method_unsupported", "IPC method is unsupported."));
        }
    }

    private ProductIpcResponse ListProviders(ProductIpcRequest request)
    {
        var values = _providers!.List();
        var offset = request.ListOffset ?? 0;
        var limit = Math.Min(request.ListLimit ?? 20, 20);
        if (offset > values.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ListOffset));
        }

        var page = values.Skip(offset).Take(limit).ToArray();
        var next = checked(offset + page.Length);
        return Success(request.RequestId) with
        {
            ProviderPage = new ProductProviderPage(offset, next, next < values.Count, page),
        };
    }

    private ProductIpcResponse ListProviderPublishers(ProductIpcRequest request)
    {
        var values = _providers!.ListTrustedPublishers();
        var offset = request.ListOffset ?? 0;
        var limit = Math.Min(request.ListLimit ?? 20, 20);
        if (offset > values.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ListOffset));
        }

        var page = values.Skip(offset).Take(limit).ToArray();
        var next = checked(offset + page.Length);
        return Success(request.RequestId) with
        {
            ProviderPublisherPage = new ProductTrustedProviderPublisherPage(
                offset,
                next,
                next < values.Count,
                page),
        };
    }

    private async Task<ProductIpcResponse> ProcessModpackUpdateAsync(
        ProductIpcRequest request,
        CancellationToken cancellationToken)
    {
        var status = request.Method switch
        {
            ProductIpcProtocol.ServerModpackUpdateBeginMethod =>
                await _modpackUpdates!.BeginAsync(request.ModpackUpdateBegin!, cancellationToken)
                    .ConfigureAwait(false),
            ProductIpcProtocol.ServerModpackUpdateCommitMethod =>
                await _modpackUpdates!.CommitAsync(
                        request.ModpackUpdateId!.Value,
                        request.ManifestSha256!,
                        cancellationToken)
                    .ConfigureAwait(false),
            ProductIpcProtocol.ServerModpackUpdateStatusMethod =>
                _modpackUpdates!.GetStatus(request.ModpackUpdateId!.Value),
            ProductIpcProtocol.ServerModpackUpdateCancelMethod =>
                await _modpackUpdates!.CancelAsync(
                        request.ModpackUpdateId!.Value,
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unsupported modpack update IPC method."),
        };
        return Success(request.RequestId) with { ModpackUpdate = status };
    }

    private async Task<ProductIpcResponse> ProcessRemoteAsync(
        ProductIpcRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case ProductIpcProtocol.RemoteAccessStatusMethod:
                return Success(request.RequestId) with
                {
                    RemoteAccess = ToRemoteAccessStatus(_remoteWeb!.Snapshot),
                };
            case ProductIpcProtocol.RemoteAccessStartMethod:
                return Success(request.RequestId) with
                {
                    RemoteAccess = ToRemoteAccessStatus(
                        await _remoteWeb!.EnableAsync(cancellationToken).ConfigureAwait(false)),
                };
            case ProductIpcProtocol.RemoteAccessStopMethod:
                return Success(request.RequestId) with
                {
                    RemoteAccess = ToRemoteAccessStatus(
                        await _remoteWeb!.DisableAsync(cancellationToken).ConfigureAwait(false)),
                };
            case ProductIpcProtocol.RemoteAccessReconnectMethod:
                return Success(request.RequestId) with
                {
                    RemoteAccess = ToRemoteAccessStatus(
                        await _remoteWeb!.ReconnectAsync(cancellationToken).ConfigureAwait(false)),
                };
            case ProductIpcProtocol.RemoteAccountListMethod:
                return ListRemoteAccounts(request);
            case ProductIpcProtocol.RemoteAccountCreateMethod:
            {
                var create = request.RemoteAccountCreate!;
                var account = await _remoteAccounts!.CreateAsync(
                        create.Username,
                        create.CredentialSubject,
                        create.Email,
                        create.Pin,
                        create.Grants,
                        cancellationToken,
                        create.Role)
                    .ConfigureAwait(false);
                return Success(request.RequestId) with { RemoteAccount = ToRemoteAccountSummary(account) };
            }
            case ProductIpcProtocol.RemoteAccountAuthorizationUpdateMethod:
            {
                var authorization = request.RemoteAccountAuthorization!;
                var account = await _remoteAccounts!.UpdateAuthorizationAsync(
                        request.RemoteUsername!,
                        authorization.Enabled,
                        authorization.Grants,
                        cancellationToken,
                        authorization.Role)
                    .ConfigureAwait(false);
                return Success(request.RequestId) with { RemoteAccount = ToRemoteAccountSummary(account) };
            }
            case ProductIpcProtocol.RemoteAccountPinUpdateMethod:
            {
                var account = await _remoteAccounts!.UpdatePinAsync(
                        request.RemoteUsername!,
                        request.RemoteAccountPin!.Pin,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Success(request.RequestId) with { RemoteAccount = ToRemoteAccountSummary(account) };
            }
            case ProductIpcProtocol.RemoteAccountPinRevealMethod:
                return Success(request.RequestId) with
                {
                    RemotePin = new ProductRevealRemoteAccountPinResponse(
                        await _remoteAccounts!.RevealPinAsync(
                                request.RemoteUsername!,
                                cancellationToken)
                            .ConfigureAwait(false)),
                };
            case ProductIpcProtocol.RemoteAccountDeleteMethod:
                await _remoteAccounts!.DeleteAsync(request.RemoteUsername!, cancellationToken)
                    .ConfigureAwait(false);
                return Success(request.RequestId);
            case ProductIpcProtocol.RemoteDeviceListMethod:
                return ListRemoteDevices(request);
            case ProductIpcProtocol.RemoteDeviceRevokeMethod:
                return _remoteDevices!.Revoke(request.RemoteDeviceId!.Value)
                    ? Success(request.RequestId)
                    : Failure(
                        request.RequestId,
                        new ProductIpcError(
                            "remote.device_not_found",
                            "The remembered device was not found."));
            default:
                return Failure(
                    request.RequestId,
                    new ProductIpcError("protocol.method_unsupported", "IPC method is unsupported."));
        }
    }

    private async Task<ProductIpcResponse> ProcessNotificationAsync(
        ProductIpcRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case ProductIpcProtocol.NotificationDiscordStatusMethod:
                return Success(request.RequestId) with
                {
                    DiscordWebhookConfiguration = await _discordWebhook!.GetAsync(cancellationToken)
                        .ConfigureAwait(false),
                };
            case ProductIpcProtocol.NotificationDiscordSetMethod:
                return Success(request.RequestId) with
                {
                    DiscordWebhookConfiguration = await _discordWebhook!.SetAsync(
                            request.DiscordWebhook!.WebhookUrl,
                            cancellationToken)
                        .ConfigureAwait(false),
                };
            case ProductIpcProtocol.NotificationDiscordDeleteMethod:
                return Success(request.RequestId) with
                {
                    DiscordWebhookConfiguration = await _discordWebhook!.DeleteAsync(cancellationToken)
                        .ConfigureAwait(false),
                };
            case ProductIpcProtocol.NotificationHistoryMethod:
            {
                var offset = request.ListOffset ?? 0;
                var limit = request.ListLimit ?? 50;
                var maximum = Math.Min(500, checked(offset + limit + 1));
                var records = await _notificationOutbox!.ReadRecentAsync(maximum, cancellationToken)
                    .ConfigureAwait(false);
                var page = records
                    .Skip(offset)
                    .Take(limit)
                    .Select(record => new ProductNotificationDeliverySummary(
                        record.DispatchId,
                        record.EventId,
                        record.ProviderId,
                        record.State.ToString(),
                        record.AttemptCount,
                        record.NextAttemptAtUtc,
                        record.LastFailureCode,
                        record.DeliveredAtUtc))
                    .ToArray();
                var next = checked(offset + page.Length);
                return Success(request.RequestId) with
                {
                    NotificationPage = new ProductNotificationDeliveryPage(
                        offset,
                        next,
                        records.Count > next,
                        page),
                };
            }
            case ProductIpcProtocol.NotificationPreferencesStatusMethod:
                if (_notificationPreferences is null)
                {
                    throw new InvalidOperationException("Notification preference management is unavailable.");
                }

                return Success(request.RequestId) with
                {
                    NotificationPreferences = await _notificationPreferences.GetAsync(cancellationToken)
                        .ConfigureAwait(false),
                };
            case ProductIpcProtocol.NotificationPreferencesSetMethod:
                if (_notificationPreferences is null)
                {
                    throw new InvalidOperationException("Notification preference management is unavailable.");
                }

                return Success(request.RequestId) with
                {
                    NotificationPreferences = await _notificationPreferences.SetAsync(
                            request.NotificationPreferences!,
                            cancellationToken)
                        .ConfigureAwait(false),
                };
            default:
                return Failure(
                    request.RequestId,
                    new ProductIpcError("protocol.method_unsupported", "IPC method is unsupported."));
        }
    }

    private ProductIpcResponse ListRemoteAccounts(ProductIpcRequest request)
    {
        var accounts = _remoteAccounts!.List();
        var offset = request.ListOffset ?? 0;
        if (offset > accounts.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ListOffset));
        }

        // A single account can carry up to 256 scoped grants. One row per frame guarantees that
        // even a maximum-size account remains below the 64 KiB named-pipe frame limit.
        var page = accounts.Skip(offset).Take(1).Select(ToRemoteAccountSummary).ToArray();
        var next = checked(offset + page.Length);
        return Success(request.RequestId) with
        {
            RemoteAccountPage = new ProductRemoteAccountPage(offset, next, next < accounts.Count, page),
        };
    }

    private ProductIpcResponse ListRemoteDevices(ProductIpcRequest request)
    {
        var devices = _remoteDevices!.List();
        var offset = request.ListOffset ?? 0;
        var limit = request.ListLimit ?? 50;
        if (offset > devices.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ListOffset));
        }

        var page = devices.Skip(offset).Take(limit).Select(ToRememberedDeviceSummary).ToArray();
        var next = checked(offset + page.Length);
        return Success(request.RequestId) with
        {
            RemoteDevicePage = new ProductRememberedDevicePage(offset, next, next < devices.Count, page),
        };
    }

    private static ProductRemoteAccessStatus ToRemoteAccessStatus(ProductRemoteWebStatus status)
        => new(
            status.DesiredEnabled,
            status.HostRunning,
            status.FunnelRunning,
            status.PublicUrl,
            status.State,
            status.ErrorCode,
            status.UpdatedAtUtc,
            status.NextRetryAtUtc);

    private static ProductRemoteAccountSummary ToRemoteAccountSummary(ProductRemoteAccountInfo account)
        => new(
            account.Username,
            account.CredentialSubject,
            account.Email,
            account.Enabled,
            account.CreatedAtUtc,
            account.UpdatedAtUtc,
            account.LockedUntilUtc,
            account.Grants,
            account.Role);

    private static ProductRememberedDeviceSummary ToRememberedDeviceSummary(
        ProductRememberedDeviceInfo device)
        => new(
            device.DeviceId,
            device.Username,
            device.Label,
            device.CreatedAtUtc,
            device.LastUsedAtUtc,
            device.IdleExpiresAtUtc,
            device.AbsoluteExpiresAtUtc,
            device.Status.ToString(),
            device.RevokedAtUtc,
            device.RevocationReason);

    private static ProductIpcError ToRemoteError(Exception error) => error switch
    {
        KeyNotFoundException => new(
            "remote.account_not_found",
            "The remote account was not found."),
        UnauthorizedAccessException => new(
            "remote.access_denied",
            "The remote management operation was denied."),
        IOException => new(
            "remote.storage_failed",
            "The remote management data could not be persisted."),
        InvalidOperationException => new(
            "remote.operation_rejected",
            "The remote management operation cannot be completed in its current state."),
        _ => new(
            "remote.request_invalid",
            "The remote management request is invalid."),
    };

    private static ProductIpcError ToNotificationError(Exception error) => error switch
    {
        UnauthorizedAccessException => new(
            "notification.access_denied",
            "Notification settings could not be accessed."),
        IOException => new(
            "notification.storage_failed",
            "Notification settings could not be persisted."),
        _ => new(
            "notification.request_invalid",
            "The notification management request is invalid."),
    };

    private static ProductIpcError ToModpackUpdateError(Exception error) => error switch
    {
        KeyNotFoundException => new(
            "modpack_update.not_found",
            "The modpack update transaction was not found."),
        UnauthorizedAccessException => new(
            "modpack_update.access_denied",
            "The modpack update storage boundary rejected the operation."),
        InvalidDataException => new(
            "modpack_update.integrity_failed",
            "The staged modpack update failed integrity validation."),
        IOException => new(
            "modpack_update.io_failed",
            "The Service could not complete the modpack update filesystem operation."),
        InvalidOperationException => new(
            "modpack_update.precondition_failed",
            "The modpack update cannot be completed in its current state."),
        _ => new(
            "modpack_update.request_invalid",
            "The modpack update request is invalid."),
    };

    public static ProductIpcResponse Failure(Guid requestId, ProductIpcError error)
        => new(
            ProductIpcProtocol.CurrentSchemaVersion,
            requestId,
            Success: false,
            Handshake: null,
            error);

    private ProductIpcResponse Handshake(ProductIpcRequest request, ProductApiVersion selectedVersion)
    {
        var publicHandshake = new ProductHandshakeResponse(
            ProductServiceApplication.ProductName,
            ProductServiceApplication.ProductVersion,
            selectedVersion,
            ProductApiProtocol.MinimumSupportedVersion,
            Ready: true);
        return new ProductIpcResponse(
            ProductIpcProtocol.CurrentSchemaVersion,
            request.RequestId,
            Success: true,
            new ProductLocalHandshakePayload(
                publicHandshake,
                _state.InstallationId,
                _state.StartedAtUtc),
            Error: null);
    }

    private static ProductIpcResponse Success(Guid requestId) => new(
        ProductIpcProtocol.CurrentSchemaVersion,
        requestId,
        Success: true,
        Handshake: null,
        Error: null);

    private async Task<ProductIpcResponse> RegisterAsync(
        ProductIpcRequest request,
        CancellationToken cancellationToken)
    {
        await _runtime!.UpsertAsync(request.Server!, cancellationToken).ConfigureAwait(false);
        return Success(request.RequestId) with
        {
            Server = _runtime.GetStatus(request.Server!.Id),
        };
    }

    private async Task<ProductIpcResponse> UpdateSettingsAsync(
        ProductIpcRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _runtime!.UpdateSettingsAsync(
                request.ServerId!.Value,
                request.ServerSettings!,
                cancellationToken)
            .ConfigureAwait(false);
        return Success(request.RequestId) with
        {
            Registration = result.Registration,
            Server = result.Status,
        };
    }

    private ProductIpcResponse List(ProductIpcRequest request)
    {
        var servers = _runtime!.List();
        var offset = request.ListOffset ?? 0;
        var limit = request.ListLimit ?? 50;
        if (offset > servers.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ListOffset));
        }

        var page = servers.Skip(offset).Take(limit).ToArray();
        var next = checked(offset + page.Length);
        return Success(request.RequestId) with
        {
            ServerPage = new ProductServerListPage(offset, next, next < servers.Count, page),
        };
    }

    private ProductIpcResponse ListStatuses(ProductIpcRequest request)
    {
        var servers = _runtime!.List();
        var offset = request.ListOffset ?? 0;
        var limit = request.ListLimit ?? 50;
        if (offset > servers.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ListOffset));
        }

        var values = servers
            .Skip(offset)
            .Take(limit)
            .Select(server => _runtime.GetStatus(server.Id))
            .ToArray();
        var nextOffset = checked(offset + values.Length);
        return Success(request.RequestId) with
        {
            ServerStatusPage = new ProductServerStatusPage(
                offset,
                nextOffset,
                nextOffset < servers.Count,
                values),
        };
    }

    private async Task<ProductIpcResponse> RemoveAsync(
        ProductIpcRequest request,
        CancellationToken cancellationToken)
    {
        var removed = await _runtime!.RemoveAsync(request.ServerId!.Value, cancellationToken)
            .ConfigureAwait(false);
        return removed
            ? Success(request.RequestId)
            : Failure(
                request.RequestId,
                new ProductIpcError("server.not_found", "The selected server is not registered."));
    }

    private async Task<ProductIpcResponse> SendCommandAsync(
        ProductIpcRequest request,
        CancellationToken cancellationToken)
    {
        await _runtime!.SendCommandAsync(
                request.ServerId!.Value,
                request.Command!,
                cancellationToken)
            .ConfigureAwait(false);
        return Success(request.RequestId) with
        {
            Server = _runtime.GetStatus(request.ServerId.Value),
        };
    }

}
