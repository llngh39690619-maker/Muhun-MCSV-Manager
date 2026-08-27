using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.Contracts;

public static class ProductIpcProtocol
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFrameBytes = 64 * 1024;
    public const string HandshakeMethod = "system.handshake";
    public const string ServerListMethod = "server.list";
    public const string ServerStatusListMethod = "server.status.list";
    public const string ServerStatusMethod = "server.status";
    public const string ServerRegistrationMethod = "server.registration";
    public const string ServerSettingsUpdateMethod = "server.settings.update";
    public const string ServerRegisterMethod = "server.register";
    public const string ServerRemoveMethod = "server.remove";
    public const string ServerDirectoryMethod = "server.directory";
    public const string ServerAdministrationMethod = "server.administration";
    public const string ServerDeleteMethod = "server.delete";
    public const string ServerStartMethod = "server.start";
    public const string ServerStopMethod = "server.stop";
    public const string ServerRestartMethod = "server.restart";
    public const string ServerConsoleMethod = "server.console";
    public const string ServerPlayersMethod = "server.players.list";
    public const string ServerCommandMethod = "server.command";
    public const string ServerBackupListMethod = "server.backup.list";
    public const string ServerBackupCreateMethod = "server.backup.create";
    public const string ServerBackupRestoreMethod = "server.backup.restore";
    public const string ServerImportBeginMethod = "server.import.begin";
    public const string ServerImportCommitMethod = "server.import.commit";
    public const string ServerImportStatusMethod = "server.import.status";
    public const string ServerImportCancelMethod = "server.import.cancel";
    public const string ServerModpackUpdateBeginMethod = "server.modpackUpdate.begin";
    public const string ServerModpackUpdateCommitMethod = "server.modpackUpdate.commit";
    public const string ServerModpackUpdateStatusMethod = "server.modpackUpdate.status";
    public const string ServerModpackUpdateCancelMethod = "server.modpackUpdate.cancel";
    public const string UpdateStatusMethod = "update.status";
    public const string UpdateCheckMethod = "update.check";
    public const string UpdateDownloadMethod = "update.download";
    public const string UpdateScheduleMethod = "update.schedule";
    public const string RemoteAccessStatusMethod = "remote.access.status";
    public const string RemoteAccessStartMethod = "remote.access.start";
    public const string RemoteAccessStopMethod = "remote.access.stop";
    public const string RemoteAccessReconnectMethod = "remote.access.reconnect";
    public const string RemoteAccountListMethod = "remote.account.list";
    public const string RemoteAccountCreateMethod = "remote.account.create";
    public const string RemoteAccountAuthorizationUpdateMethod = "remote.account.authorization.update";
    public const string RemoteAccountPinUpdateMethod = "remote.account.pin.update";
    public const string RemoteAccountPinRevealMethod = "remote.account.pin.reveal";
    public const string RemoteAccountDeleteMethod = "remote.account.delete";
    public const string RemoteDeviceListMethod = "remote.device.list";
    public const string RemoteDeviceRevokeMethod = "remote.device.revoke";
    public const string NotificationDiscordStatusMethod = "notification.discord.status";
    public const string NotificationDiscordSetMethod = "notification.discord.set";
    public const string NotificationDiscordDeleteMethod = "notification.discord.delete";
    public const string NotificationHistoryMethod = "notification.history";
    public const string NotificationPreferencesStatusMethod = "notification.preferences.status";
    public const string NotificationPreferencesSetMethod = "notification.preferences.set";
    public const string ProviderListMethod = "provider.list";
    public const string ProviderSetEnabledMethod = "provider.enabled.set";
    public const string ProviderHealthMethod = "provider.health";
    public const string ProviderUninstallMethod = "provider.uninstall";
    public const string ProviderInstallMethod = "provider.install";
    public const string ProviderPublisherListMethod = "provider.publisher.list";
    public const string ProviderPublisherPinMethod = "provider.publisher.pin";
    public const string ProviderPublisherRemoveMethod = "provider.publisher.remove";
}

public sealed record ProductIpcRequest(
    int SchemaVersion,
    Guid RequestId,
    string Method,
    ProductApiVersion ClientMinimumApiVersion,
    ProductApiVersion ClientMaximumApiVersion)
{
    public Guid? ServerId { get; init; }

    public long? ConsoleCursor { get; init; }

    public int? ConsoleLimit { get; init; }

    public string? Command { get; init; }

    public ProductServerRegistration? Server { get; init; }

    public ProductServerSettingsUpdateRequest? ServerSettings { get; init; }

    public Guid? ImportId { get; init; }

    public ProductServerImportBeginRequest? ImportBegin { get; init; }

    public Guid? ModpackUpdateId { get; init; }

    public ProductServerModpackUpdateBeginRequest? ModpackUpdateBegin { get; init; }

    public string? ManifestSha256 { get; init; }

    public int? ListOffset { get; init; }

    public int? ListLimit { get; init; }

    public ProductUpdateChannel? UpdateChannel { get; init; }

    public DateTimeOffset? UpdateNotBeforeUtc { get; init; }

    public string? RemoteUsername { get; init; }

    public Guid? RemoteDeviceId { get; init; }

    public ProductCreateRemoteAccountRequest? RemoteAccountCreate { get; init; }

    public ProductUpdateRemoteAccountAuthorizationRequest? RemoteAccountAuthorization { get; init; }

    public ProductUpdateRemoteAccountPinRequest? RemoteAccountPin { get; init; }

    public ProductDiscordWebhookUpdateRequest? DiscordWebhook { get; init; }

    public ProductNotificationPreferences? NotificationPreferences { get; init; }

    public string? BackupId { get; init; }

    public string? ProviderId { get; init; }

    public bool? ProviderEnabled { get; init; }

    public string? ProviderPublisherId { get; init; }

    public ProductPinProviderPublisherRequest? ProviderPublisherPin { get; init; }

    public ProductProviderInstallFromInboxRequest? ProviderInstall { get; init; }
}

public sealed record ProductLocalHandshakePayload(
    ProductHandshakeResponse Protocol,
    Guid InstallationId,
    DateTimeOffset StartedAtUtc);

public sealed record ProductIpcError(string Code, string Message);

public sealed record ProductIpcResponse(
    int SchemaVersion,
    Guid RequestId,
    bool Success,
    ProductLocalHandshakePayload? Handshake,
    ProductIpcError? Error)
{
    public ProductServerListPage? ServerPage { get; init; }

    public ProductServerStatusPage? ServerStatusPage { get; init; }

    public ProductServerStatus? Server { get; init; }

    public ProductServerRegistration? Registration { get; init; }

    public ProductServerDirectoryInfo? ServerDirectory { get; init; }

    public ProductServerAdministrationSnapshot? ServerAdministration { get; init; }

    public ProductServerDeletionResult? ServerDeletion { get; init; }

    public ProductConsolePage? Console { get; init; }

    public ProductServerPlayerList? Players { get; init; }

    public ProductServerMutationResult? Mutation { get; init; }

    public ProductServerImportStatus? Import { get; init; }

    public ProductServerModpackUpdateStatus? ModpackUpdate { get; init; }

    public ProductUpdateOperationResult? Update { get; init; }

    public ProductRemoteAccessStatus? RemoteAccess { get; init; }

    public ProductRemoteAccountPage? RemoteAccountPage { get; init; }

    public ProductRemoteAccountSummary? RemoteAccount { get; init; }

    public ProductRevealRemoteAccountPinResponse? RemotePin { get; init; }

    public ProductRememberedDevicePage? RemoteDevicePage { get; init; }

    public ProductDiscordWebhookConfiguration? DiscordWebhookConfiguration { get; init; }

    public ProductNotificationDeliveryPage? NotificationPage { get; init; }

    public ProductNotificationPreferences? NotificationPreferences { get; init; }

    public ProductServerBackupPage? BackupPage { get; init; }

    public ProductServerBackupMutationResult? BackupMutation { get; init; }

    public ProductServerBackupRestoreResult? BackupRestore { get; init; }

    public ProductProviderPage? ProviderPage { get; init; }

    public ProductProviderSummary? Provider { get; init; }

    public ProductProviderHealthCheckResult? ProviderHealth { get; init; }

    public ProductTrustedProviderPublisherPage? ProviderPublisherPage { get; init; }

    public ProductTrustedProviderPublisherSummary? ProviderPublisher { get; init; }
}

public static class ProductIpcRequestValidator
{
    public static ProductIpcError? Validate(ProductIpcRequest? request)
    {
        if (request is null)
        {
            return new ProductIpcError("protocol.request_required", "IPC request is required.");
        }

        if (request.SchemaVersion != ProductIpcProtocol.CurrentSchemaVersion)
        {
            return new ProductIpcError("protocol.schema_unsupported", "IPC schema version is unsupported.");
        }

        if (request.RequestId == Guid.Empty)
        {
            return new ProductIpcError("protocol.request_id_invalid", "IPC request id must not be empty.");
        }

        if (!KnownMethods.Contains(request.Method))
        {
            return new ProductIpcError("protocol.method_unsupported", "IPC method is unsupported.");
        }

        if (request.ClientMinimumApiVersion.Major < 1 ||
            request.ClientMaximumApiVersion.Major < 1 ||
            request.ClientMinimumApiVersion.Minor < 0 ||
            request.ClientMaximumApiVersion.Minor < 0 ||
            request.ClientMinimumApiVersion.CompareTo(request.ClientMaximumApiVersion) > 0)
        {
            return new ProductIpcError("protocol.version_range_invalid", "Client API version range is invalid.");
        }

        var requiresServerId = request.Method is
            ProductIpcProtocol.ServerStatusMethod or
            ProductIpcProtocol.ServerRegistrationMethod or
            ProductIpcProtocol.ServerSettingsUpdateMethod or
            ProductIpcProtocol.ServerRemoveMethod or
            ProductIpcProtocol.ServerDirectoryMethod or
            ProductIpcProtocol.ServerAdministrationMethod or
            ProductIpcProtocol.ServerDeleteMethod or
            ProductIpcProtocol.ServerStartMethod or
            ProductIpcProtocol.ServerStopMethod or
            ProductIpcProtocol.ServerRestartMethod or
            ProductIpcProtocol.ServerConsoleMethod or
            ProductIpcProtocol.ServerPlayersMethod or
            ProductIpcProtocol.ServerCommandMethod or
            ProductIpcProtocol.ServerBackupListMethod or
            ProductIpcProtocol.ServerBackupCreateMethod or
            ProductIpcProtocol.ServerBackupRestoreMethod;
        if (requiresServerId && request.ServerId.GetValueOrDefault() == Guid.Empty)
        {
            return new ProductIpcError("protocol.server_id_required", "A non-empty server id is required.");
        }

        if (request.Method == ProductIpcProtocol.ServerRegisterMethod && request.Server is null)
        {
            return new ProductIpcError("protocol.server_required", "A server registration is required.");
        }

        if (request.Method == ProductIpcProtocol.ServerSettingsUpdateMethod &&
            request.ServerSettings is null)
        {
            return new ProductIpcError(
                "protocol.server_settings_required",
                "A server settings update is required.");
        }

        if (request.ServerSettings is { } settings &&
            (string.IsNullOrWhiteSpace(settings.Name) || settings.Name.Length > 128 ||
             settings.Name.Any(character => char.IsControl(character)) ||
             settings.MinimumMemoryMb is < 128 or > 1_048_576 ||
             settings.MaximumMemoryMb < settings.MinimumMemoryMb ||
             settings.MaximumMemoryMb > 1_048_576 ||
             settings.Port is < 1 or > 65535))
        {
            return new ProductIpcError(
                "protocol.server_settings_invalid",
                "The server settings update is invalid.");
        }

        if (request.Method == ProductIpcProtocol.ServerImportBeginMethod && request.ImportBegin is null)
        {
            return new ProductIpcError("protocol.import_begin_required", "An import definition is required.");
        }

        var requiresImportId = request.Method is
            ProductIpcProtocol.ServerImportCommitMethod or
            ProductIpcProtocol.ServerImportStatusMethod or
            ProductIpcProtocol.ServerImportCancelMethod;
        if (requiresImportId && request.ImportId.GetValueOrDefault() == Guid.Empty)
        {
            return new ProductIpcError("protocol.import_id_required", "A non-empty import id is required.");
        }

        if (request.Method == ProductIpcProtocol.ServerImportCommitMethod &&
            (request.ManifestSha256 is null ||
             request.ManifestSha256.Length != 64 ||
             request.ManifestSha256.Any(character => !Uri.IsHexDigit(character))))
        {
            return new ProductIpcError("protocol.import_manifest_hash_invalid", "A SHA-256 manifest hash is required.");
        }

        if (request.Method == ProductIpcProtocol.ServerModpackUpdateBeginMethod &&
            request.ModpackUpdateBegin is null)
        {
            return new ProductIpcError(
                "protocol.modpack_update_begin_required",
                "A modpack update definition is required.");
        }

        var requiresModpackUpdateId = request.Method is
            ProductIpcProtocol.ServerModpackUpdateCommitMethod or
            ProductIpcProtocol.ServerModpackUpdateStatusMethod or
            ProductIpcProtocol.ServerModpackUpdateCancelMethod;
        if (requiresModpackUpdateId && request.ModpackUpdateId.GetValueOrDefault() == Guid.Empty)
        {
            return new ProductIpcError(
                "protocol.modpack_update_id_required",
                "A non-empty modpack update id is required.");
        }

        if (request.Method == ProductIpcProtocol.ServerModpackUpdateCommitMethod &&
            (request.ManifestSha256 is null ||
             request.ManifestSha256.Length != 64 ||
             request.ManifestSha256.Any(character => !Uri.IsHexDigit(character))))
        {
            return new ProductIpcError(
                "protocol.modpack_update_manifest_hash_invalid",
                "A SHA-256 modpack update manifest hash is required.");
        }

        if (request.Method == ProductIpcProtocol.ServerCommandMethod &&
            string.IsNullOrWhiteSpace(request.Command))
        {
            return new ProductIpcError("protocol.command_required", "A server command is required.");
        }

        if (request.Method == ProductIpcProtocol.ServerBackupRestoreMethod &&
            (string.IsNullOrWhiteSpace(request.BackupId) || request.BackupId.Length != 64 ||
             request.BackupId.Any(character => !Uri.IsHexDigit(character))))
        {
            return new ProductIpcError(
                "protocol.backup_id_invalid",
                "A valid opaque backup id is required.");
        }

        if (request.ConsoleCursor is < 0 || request.ConsoleLimit is < 1 or > 50)
        {
            return new ProductIpcError("protocol.console_range_invalid", "Console cursor or limit is invalid.");
        }

        if (request.ListOffset is < 0 || request.ListLimit is < 1 or > 50)
        {
            return new ProductIpcError("protocol.list_range_invalid", "Server list offset or limit is invalid.");
        }

        var isUpdateMethod = request.Method is
            ProductIpcProtocol.UpdateStatusMethod or
            ProductIpcProtocol.UpdateCheckMethod or
            ProductIpcProtocol.UpdateDownloadMethod or
            ProductIpcProtocol.UpdateScheduleMethod;
        if (isUpdateMethod &&
            (request.UpdateChannel is null || !Enum.IsDefined(request.UpdateChannel.Value)))
        {
            return new ProductIpcError("protocol.update_channel_invalid", "A supported update channel is required.");
        }

        if (request.Method != ProductIpcProtocol.UpdateScheduleMethod && request.UpdateNotBeforeUtc is not null)
        {
            return new ProductIpcError(
                "protocol.update_schedule_unexpected",
                "An update schedule is only valid for the schedule method.");
        }

        if (request.UpdateNotBeforeUtc is { } notBefore && notBefore.Offset != TimeSpan.Zero)
        {
            return new ProductIpcError(
                "protocol.update_schedule_invalid",
                "Update schedule time must use UTC.");
        }

        var requiresRemoteUsername = request.Method is
            ProductIpcProtocol.RemoteAccountAuthorizationUpdateMethod or
            ProductIpcProtocol.RemoteAccountPinUpdateMethod or
            ProductIpcProtocol.RemoteAccountPinRevealMethod or
            ProductIpcProtocol.RemoteAccountDeleteMethod;
        if (requiresRemoteUsername &&
            (string.IsNullOrWhiteSpace(request.RemoteUsername) || request.RemoteUsername.Length > 32))
        {
            return new ProductIpcError(
                "protocol.remote_username_required",
                "A valid remote account username is required.");
        }

        if (request.Method == ProductIpcProtocol.RemoteAccountCreateMethod &&
            request.RemoteAccountCreate is null)
        {
            return new ProductIpcError(
                "protocol.remote_account_required",
                "A remote account definition is required.");
        }

        if (request.Method == ProductIpcProtocol.RemoteAccountAuthorizationUpdateMethod &&
            request.RemoteAccountAuthorization is null)
        {
            return new ProductIpcError(
                "protocol.remote_authorization_required",
                "A remote account authorization definition is required.");
        }

        if (request.Method == ProductIpcProtocol.RemoteAccountPinUpdateMethod &&
            request.RemoteAccountPin is null)
        {
            return new ProductIpcError(
                "protocol.remote_pin_required",
                "A remote account PIN definition is required.");
        }

        if (request.Method == ProductIpcProtocol.RemoteDeviceRevokeMethod &&
            request.RemoteDeviceId.GetValueOrDefault() == Guid.Empty)
        {
            return new ProductIpcError(
                "protocol.remote_device_id_required",
                "A non-empty remembered-device id is required.");
        }

        var isRemoteAccountList = request.Method == ProductIpcProtocol.RemoteAccountListMethod;
        if (isRemoteAccountList && request.ListLimit is > 1)
        {
            return new ProductIpcError(
                "protocol.remote_account_page_invalid",
                "Remote account pages contain at most one account.");
        }

        if (request.Method == ProductIpcProtocol.NotificationDiscordSetMethod &&
            request.DiscordWebhook is null)
        {
            return new ProductIpcError(
                "protocol.discord_webhook_required",
                "A Discord webhook definition is required.");
        }

        if (request.Method == ProductIpcProtocol.NotificationHistoryMethod &&
            request.ListOffset is > 499)
        {
            return new ProductIpcError(
                "protocol.notification_page_invalid",
                "Notification history offset exceeds the retained management window.");
        }

        if (request.Method == ProductIpcProtocol.NotificationPreferencesSetMethod)
        {
            try
            {
                ProductNotificationPreferencesValidator.ValidateAndThrow(
                    request.NotificationPreferences);
            }
            catch (Exception error) when (error is ArgumentException)
            {
                return new ProductIpcError(
                    "protocol.notification_preferences_invalid",
                    "A valid versioned notification preference definition is required.");
            }
        }
        else if (request.NotificationPreferences is not null)
        {
            return new ProductIpcError(
                "protocol.notification_preferences_unexpected",
                "Notification preferences are not valid for this method.");
        }

        var requiresProviderId = request.Method is
            ProductIpcProtocol.ProviderSetEnabledMethod or
            ProductIpcProtocol.ProviderHealthMethod or
            ProductIpcProtocol.ProviderUninstallMethod;
        if (requiresProviderId && !IsSafeProviderIdentifier(request.ProviderId))
        {
            return new ProductIpcError(
                "protocol.provider_id_required",
                "A valid provider id is required.");
        }

        if (!requiresProviderId && request.ProviderId is not null)
        {
            return new ProductIpcError(
                "protocol.provider_id_unexpected",
                "A provider id is not valid for this method.");
        }

        if (request.Method == ProductIpcProtocol.ProviderSetEnabledMethod &&
            request.ProviderEnabled is null)
        {
            return new ProductIpcError(
                "protocol.provider_enabled_required",
                "A provider enabled state is required.");
        }

        if (request.Method != ProductIpcProtocol.ProviderSetEnabledMethod &&
            request.ProviderEnabled is not null)
        {
            return new ProductIpcError(
                "protocol.provider_enabled_unexpected",
                "A provider enabled state is not valid for this method.");
        }

        if (request.Method == ProductIpcProtocol.ProviderPublisherPinMethod &&
            (request.ProviderPublisherPin is not { } pin ||
             !IsSafeProviderIdentifier(pin.PublisherId) ||
             string.IsNullOrWhiteSpace(pin.PublicKeyPem) ||
             pin.PublicKeyPem.Length > 16 * 1024 ||
             pin.PublicKeyPem.Any(character => character == '\0') ||
             pin.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal)))
        {
            return new ProductIpcError(
                "protocol.provider_publisher_pin_invalid",
                "A bounded provider publisher public key is required.");
        }

        if (request.Method != ProductIpcProtocol.ProviderPublisherPinMethod &&
            request.ProviderPublisherPin is not null)
        {
            return new ProductIpcError(
                "protocol.provider_publisher_pin_unexpected",
                "A provider publisher key is not valid for this method.");
        }

        if (request.Method == ProductIpcProtocol.ProviderPublisherRemoveMethod &&
            !IsSafeProviderIdentifier(request.ProviderPublisherId))
        {
            return new ProductIpcError(
                "protocol.provider_publisher_id_required",
                "A valid provider publisher id is required.");
        }

        if (request.Method != ProductIpcProtocol.ProviderPublisherRemoveMethod &&
            request.ProviderPublisherId is not null)
        {
            return new ProductIpcError(
                "protocol.provider_publisher_id_unexpected",
                "A provider publisher id is not valid for this method.");
        }

        if (request.Method == ProductIpcProtocol.ProviderInstallMethod &&
            !IsValidProviderInstall(request.ProviderInstall))
        {
            return new ProductIpcError(
                "protocol.provider_install_invalid",
                "A bounded signed provider inbox package definition is required.");
        }

        if (request.Method != ProductIpcProtocol.ProviderInstallMethod &&
            request.ProviderInstall is not null)
        {
            return new ProductIpcError(
                "protocol.provider_install_unexpected",
                "A provider install definition is not valid for this method.");
        }

        return null;
    }

    private static bool IsSafeProviderIdentifier(string? value)
    {
        if (value is null || value.Length is < 3 or > 96 ||
            value[0] is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') ||
            value[^1] is not (>= 'a' and <= 'z') and not (>= '0' and <= '9'))
        {
            return false;
        }

        return value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9'
            or '.' or '_' or '-');
    }

    private static bool IsValidProviderInstall(ProductProviderInstallFromInboxRequest? request)
    {
        if (request is null || request.Signature is null ||
            request.InboxFileName.Length is < 7 or > 180 ||
            !request.InboxFileName.EndsWith(".mcsvp", StringComparison.OrdinalIgnoreCase) ||
            request.InboxFileName.Any(character => char.IsControl(character) ||
                character is '/' or '\\' or ':') ||
            !string.Equals(Path.GetFileName(request.InboxFileName), request.InboxFileName, StringComparison.Ordinal) ||
            request.ExpectedSha256.Length != 64 ||
            request.ExpectedSha256.Any(character => !Uri.IsHexDigit(character)) ||
            !IsSafeProviderIdentifier(request.ExpectedProviderId) ||
            !IsSafeProviderIdentifier(request.ExpectedPublisherId) ||
            request.ExpectedVersion.Length is < 1 or > 96 ||
            request.ExpectedVersion.Any(char.IsControl) ||
            request.Signature.PublisherId != request.ExpectedPublisherId ||
            request.Signature.Algorithm.Length is < 1 or > 64 ||
            request.Signature.Algorithm.Any(char.IsControl) ||
            request.Signature.SignatureBase64.Length is < 4 or > 16 * 1024 ||
            request.Signature.FormatVersion is < 1 or > 16)
        {
            return false;
        }

        try
        {
            var decoded = Convert.FromBase64String(request.Signature.SignatureBase64);
            return decoded.Length is > 0 and <= 12 * 1024 &&
                   Convert.ToBase64String(decoded) == request.Signature.SignatureBase64;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static readonly HashSet<string> KnownMethods = new(StringComparer.Ordinal)
    {
        ProductIpcProtocol.HandshakeMethod,
        ProductIpcProtocol.ServerListMethod,
        ProductIpcProtocol.ServerStatusListMethod,
        ProductIpcProtocol.ServerStatusMethod,
        ProductIpcProtocol.ServerRegistrationMethod,
        ProductIpcProtocol.ServerSettingsUpdateMethod,
        ProductIpcProtocol.ServerRegisterMethod,
        ProductIpcProtocol.ServerRemoveMethod,
        ProductIpcProtocol.ServerDirectoryMethod,
        ProductIpcProtocol.ServerAdministrationMethod,
        ProductIpcProtocol.ServerDeleteMethod,
        ProductIpcProtocol.ServerStartMethod,
        ProductIpcProtocol.ServerStopMethod,
        ProductIpcProtocol.ServerRestartMethod,
        ProductIpcProtocol.ServerConsoleMethod,
        ProductIpcProtocol.ServerPlayersMethod,
        ProductIpcProtocol.ServerCommandMethod,
        ProductIpcProtocol.ServerBackupListMethod,
        ProductIpcProtocol.ServerBackupCreateMethod,
        ProductIpcProtocol.ServerBackupRestoreMethod,
        ProductIpcProtocol.ServerImportBeginMethod,
        ProductIpcProtocol.ServerImportCommitMethod,
        ProductIpcProtocol.ServerImportStatusMethod,
        ProductIpcProtocol.ServerImportCancelMethod,
        ProductIpcProtocol.ServerModpackUpdateBeginMethod,
        ProductIpcProtocol.ServerModpackUpdateCommitMethod,
        ProductIpcProtocol.ServerModpackUpdateStatusMethod,
        ProductIpcProtocol.ServerModpackUpdateCancelMethod,
        ProductIpcProtocol.UpdateStatusMethod,
        ProductIpcProtocol.UpdateCheckMethod,
        ProductIpcProtocol.UpdateDownloadMethod,
        ProductIpcProtocol.UpdateScheduleMethod,
        ProductIpcProtocol.RemoteAccessStatusMethod,
        ProductIpcProtocol.RemoteAccessStartMethod,
        ProductIpcProtocol.RemoteAccessStopMethod,
        ProductIpcProtocol.RemoteAccessReconnectMethod,
        ProductIpcProtocol.RemoteAccountListMethod,
        ProductIpcProtocol.RemoteAccountCreateMethod,
        ProductIpcProtocol.RemoteAccountAuthorizationUpdateMethod,
        ProductIpcProtocol.RemoteAccountPinUpdateMethod,
        ProductIpcProtocol.RemoteAccountPinRevealMethod,
        ProductIpcProtocol.RemoteAccountDeleteMethod,
        ProductIpcProtocol.RemoteDeviceListMethod,
        ProductIpcProtocol.RemoteDeviceRevokeMethod,
        ProductIpcProtocol.NotificationDiscordStatusMethod,
        ProductIpcProtocol.NotificationDiscordSetMethod,
        ProductIpcProtocol.NotificationDiscordDeleteMethod,
        ProductIpcProtocol.NotificationHistoryMethod,
        ProductIpcProtocol.NotificationPreferencesStatusMethod,
        ProductIpcProtocol.NotificationPreferencesSetMethod,
        ProductIpcProtocol.ProviderListMethod,
        ProductIpcProtocol.ProviderSetEnabledMethod,
        ProductIpcProtocol.ProviderHealthMethod,
        ProductIpcProtocol.ProviderUninstallMethod,
        ProductIpcProtocol.ProviderInstallMethod,
        ProductIpcProtocol.ProviderPublisherListMethod,
        ProductIpcProtocol.ProviderPublisherPinMethod,
        ProductIpcProtocol.ProviderPublisherRemoveMethod,
    };
}
