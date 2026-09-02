using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.Contracts.Tests;

public sealed class ProductIpcContractTests
{
    [Fact]
    public void NotificationPreferences_RequireCanonicalSupportedCulture()
    {
        var valid = ValidRequest() with
        {
            Method = ProductIpcProtocol.NotificationPreferencesSetMethod,
            NotificationPreferences = ProductNotificationPreferences.Default with
            {
                CultureName = "en-US",
            },
        };
        var nonCanonical = valid with
        {
            NotificationPreferences = valid.NotificationPreferences! with
            {
                CultureName = "en-GB",
            },
        };

        Assert.Null(ProductIpcRequestValidator.Validate(valid));
        Assert.Equal(
            "protocol.notification_preferences_invalid",
            ProductIpcRequestValidator.Validate(nonCanonical)?.Code);
    }

    [Fact]
    public void ValidHandshakeRequest_IsAccepted()
    {
        var request = ValidRequest();

        Assert.Null(ProductIpcRequestValidator.Validate(request));
    }

    [Fact]
    public void UnsupportedMethod_FailsClosed()
    {
        var request = ValidRequest() with { Method = "server.destroy" };

        Assert.Equal("protocol.method_unsupported", ProductIpcRequestValidator.Validate(request)?.Code);
    }

    [Fact]
    public void RuntimeMethod_RequiresItsServerIdentity()
    {
        var request = ValidRequest() with { Method = ProductIpcProtocol.ServerStartMethod };

        Assert.Equal("protocol.server_id_required", ProductIpcRequestValidator.Validate(request)?.Code);
    }

    [Fact]
    public void PlayerListMethod_RequiresItsServerIdentityAndIsARegisteredProtocolMethod()
    {
        var missing = ValidRequest() with { Method = ProductIpcProtocol.ServerPlayersMethod };
        var valid = missing with { ServerId = Guid.NewGuid() };

        Assert.Equal("protocol.server_id_required", ProductIpcRequestValidator.Validate(missing)?.Code);
        Assert.Null(ProductIpcRequestValidator.Validate(valid));
    }

    [Fact]
    public void BoundedAdministrationMethod_IsRegisteredAndRequiresServerIdentity()
    {
        var missing = ValidRequest() with { Method = ProductIpcProtocol.ServerAdministrationMethod };
        var valid = missing with { ServerId = Guid.NewGuid() };

        Assert.Equal("protocol.server_id_required", ProductIpcRequestValidator.Validate(missing)?.Code);
        Assert.Null(ProductIpcRequestValidator.Validate(valid));
    }

    [Fact]
    public void RuntimeContracts_AdvanceMinorApiWithoutDroppingVersionOneClients()
    {
        Assert.Equal(new ProductApiVersion(1, 0), ProductApiProtocol.MinimumSupportedVersion);
        Assert.Equal(new ProductApiVersion(1, 6), ProductApiProtocol.MinecraftEulaConsentVersion);
        Assert.Equal(new ProductApiVersion(1, 7), ProductApiProtocol.ServerPropertiesEditorVersion);
        Assert.Equal(new ProductApiVersion(1, 8), ProductApiProtocol.ServiceInstanceSettingsVersion);
        Assert.Equal(new ProductApiVersion(1, 9), ProductApiProtocol.RuntimeStatusVersion);
        Assert.Equal(new ProductApiVersion(1, 10), ProductApiProtocol.KnownPlayerRosterVersion);
        Assert.Equal(ProductApiProtocol.KnownPlayerRosterVersion, ProductApiProtocol.CurrentVersion);
        Assert.Equal("X-MCSV-Service-Token", ProductLocalApiAuthentication.HeaderName);
    }

    [Fact]
    public void KnownPlayerRoster_RoundTripsNullCapabilityAndStaysWithinIpcFrameBound()
    {
        var captured = DateTimeOffset.Parse("2026-09-02T12:34:56.789+00:00");
        var online = Enumerable.Range(0, ProductServerPlayerContract.MaximumOnlinePlayers)
            .Select(index => new ProductServerPlayerSummary(
                $"Player{index:D9}",
                captured))
            .ToArray();
        var known = Enumerable.Range(0, ProductServerPlayerContract.MaximumKnownPlayers)
            .Select(index => new ProductKnownPlayerSummary(
                $"Known{index:D10}",
                Guid.Parse("f84c6a79-0a4e-45c3-b682-16ba4a8c4d50"),
                Online: index % 2 == 0,
                Operator: true,
                Whitelisted: true,
                Banned: true,
                LastSeenUtc: captured))
            .ToArray();
        var response = new ProductIpcResponse(
            ProductIpcProtocol.CurrentSchemaVersion,
            Guid.NewGuid(),
            true,
            Handshake: null,
            Error: null)
        {
            Players = new ProductServerPlayerList(Guid.NewGuid(), captured, online)
            {
                KnownPlayers = known,
            },
        };

        var payload = JsonSerializer.SerializeToUtf8Bytes(response);
        var roundTrip = JsonSerializer.Deserialize<ProductIpcResponse>(payload);

        Assert.True(payload.Length <= ProductIpcProtocol.MaximumFrameBytes);
        Assert.Equal(known, roundTrip!.Players!.KnownPlayers);
        Assert.Null(new ProductServerPlayerList(Guid.NewGuid(), captured, []).KnownPlayers);
    }

    [Fact]
    public void RuntimeStatus_JsonRoundTripPreservesPathFreeJavaAndListenerState()
    {
        var summary = new ProductServerSummary(
            Guid.NewGuid(),
            "Status contract",
            ProductServerState.Running,
            25566,
            "Paper",
            "1.21.1");
        var expected = new ProductServerStatus(
            summary,
            Guid.NewGuid(),
            123,
            DateTimeOffset.UtcNow,
            null,
            null,
            null)
        {
            Java = new ProductServerJavaRuntimeSummary(
                true, true, 21, "21.0.8+9", "JDK", "Eclipse Adoptium", "x64"),
            PortListening = true,
        };

        var json = JsonSerializer.Serialize(expected);
        var actual = JsonSerializer.Deserialize<ProductServerStatus>(json);

        Assert.NotNull(actual);
        Assert.Equal(expected.Java, actual.Java);
        Assert.True(actual.PortListening);
        Assert.DoesNotContain("javaRuntimePath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServiceInstanceSettings_RequireOneCompleteValidSnapshot()
    {
        var valid = ValidRequest() with
        {
            Method = ProductIpcProtocol.ServerSettingsUpdateMethod,
            ServerId = Guid.NewGuid(),
            ServerSettings = CompleteServiceInstanceSettings(),
        };
        var partial = valid with
        {
            ServerSettings = new ProductServerSettingsUpdateRequest(
                "Server",
                1024,
                4096,
                25565,
                false)
            {
                MemoryAllocationMode = ProductServerMemoryAllocationMode.Automatic,
            },
        };
        var invalidMode = valid with
        {
            ServerSettings = CompleteServiceInstanceSettings() with
            {
                MemoryAllocationMode = (ProductServerMemoryAllocationMode)99,
            },
        };
        var invalidTimeout = valid with
        {
            ServerSettings = CompleteServiceInstanceSettings() with
            {
                WatchdogProbeTimeoutSeconds = 30,
                WatchdogCheckIntervalSeconds = 30,
            },
        };

        Assert.Null(ProductIpcRequestValidator.Validate(valid));
        Assert.Equal("protocol.server_settings_invalid", ProductIpcRequestValidator.Validate(partial)?.Code);
        Assert.Equal("protocol.server_settings_invalid", ProductIpcRequestValidator.Validate(invalidMode)?.Code);
        Assert.Equal("protocol.server_settings_invalid", ProductIpcRequestValidator.Validate(invalidTimeout)?.Code);
    }

    [Fact]
    public void ServerPropertiesMethods_RequireIdentityAndBoundedUpdate()
    {
        var missingIdentity = ValidRequest() with
        {
            Method = ProductIpcProtocol.ServerPropertiesReadMethod,
        };
        var missingUpdate = ValidRequest() with
        {
            Method = ProductIpcProtocol.ServerPropertiesUpdateMethod,
            ServerId = Guid.NewGuid(),
        };
        var valid = missingUpdate with
        {
            ServerPropertiesUpdate = new ProductServerPropertiesUpdateRequest(
                "server-port=25570\n",
                ProductServerPropertiesContract.MissingRevision),
        };
        var invalid = valid with
        {
            ServerPropertiesUpdate = new ProductServerPropertiesUpdateRequest(
                new string('x', ProductServerPropertiesContract.MaximumTextCharacters + 1),
                ProductServerPropertiesContract.MissingRevision),
        };

        Assert.Equal("protocol.server_id_required", ProductIpcRequestValidator.Validate(missingIdentity)?.Code);
        Assert.Equal("protocol.server_properties_required", ProductIpcRequestValidator.Validate(missingUpdate)?.Code);
        Assert.Null(ProductIpcRequestValidator.Validate(valid));
        Assert.Equal("protocol.server_properties_invalid", ProductIpcRequestValidator.Validate(invalid)?.Code);
    }

    [Theory]
    [InlineData("測", ProductServerPropertiesContract.MaximumTextCharacters)]
    [InlineData("\\", ProductServerPropertiesContract.MaximumTextCharacters)]
    [InlineData("😀", ProductServerPropertiesContract.MaximumTextCharacters / 2)]
    public void MaximumServerPropertiesPayload_FitsAuthenticatedPipeFrame(
        string unit,
        int repeatCount)
    {
        var serverId = Guid.NewGuid();
        var text = string.Concat(Enumerable.Repeat(unit, repeatCount));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = ValidRequest() with
        {
            Method = ProductIpcProtocol.ServerPropertiesUpdateMethod,
            ServerId = serverId,
            ClientMinimumApiVersion = ProductApiProtocol.ServerPropertiesEditorVersion,
            ServerPropertiesUpdate = new ProductServerPropertiesUpdateRequest(
                text,
                ProductServerPropertiesContract.MissingRevision),
        };
        var response = new ProductIpcResponse(
            ProductIpcProtocol.CurrentSchemaVersion,
            request.RequestId,
            true,
            null,
            null)
        {
            ServerProperties = new ProductServerPropertiesDocument(
                serverId,
                true,
                text,
                new string('a', 64)),
        };

        Assert.Null(ProductIpcRequestValidator.Validate(request));
        Assert.True(
            JsonSerializer.SerializeToUtf8Bytes(request, options).Length <=
            ProductIpcProtocol.MaximumFrameBytes);
        Assert.True(
            JsonSerializer.SerializeToUtf8Bytes(response, options).Length <=
            ProductIpcProtocol.MaximumFrameBytes);
    }

    [Fact]
    public void MinecraftEulaConfirmation_RoundTripsAndLegacyPayloadDefaultsToNull()
    {
        var serverId = Guid.NewGuid();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = ValidRequest() with
        {
            Method = ProductIpcProtocol.ServerStartMethod,
            ServerId = serverId,
            ClientMinimumApiVersion = ProductApiProtocol.MinecraftEulaConsentVersion,
            AcceptMinecraftEula = true,
        };

        var roundTripped = JsonSerializer.Deserialize<ProductIpcRequest>(
            JsonSerializer.SerializeToUtf8Bytes(request, options),
            options);

        Assert.NotNull(roundTripped);
        Assert.Equal(ProductApiProtocol.MinecraftEulaConsentVersion, roundTripped.ClientMinimumApiVersion);
        Assert.True(roundTripped.AcceptMinecraftEula is true);
        Assert.Null(ProductIpcRequestValidator.Validate(roundTripped));

        var legacyPayload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = ProductIpcProtocol.CurrentSchemaVersion,
                requestId = Guid.NewGuid(),
                method = ProductIpcProtocol.ServerStartMethod,
                clientMinimumApiVersion = ProductApiProtocol.MinimumSupportedVersion,
                clientMaximumApiVersion = new ProductApiVersion(1, 5),
                serverId,
            },
            options);
        var legacyRequest = JsonSerializer.Deserialize<ProductIpcRequest>(legacyPayload, options);

        Assert.NotNull(legacyRequest);
        Assert.Null(legacyRequest.AcceptMinecraftEula);
        Assert.Null(ProductIpcRequestValidator.Validate(legacyRequest));
    }

    [Fact]
    public void MinecraftEulaConfirmation_IsRejectedOutsideStartOrRestart()
    {
        var request = ValidRequest() with { AcceptMinecraftEula = false };

        Assert.Equal(
            "protocol.eula_confirmation_not_allowed",
            ProductIpcRequestValidator.Validate(request)?.Code);
    }

    [Fact]
    public void DefaultApiVersion_FailsClosed()
    {
        var request = ValidRequest() with { ClientMinimumApiVersion = default };

        Assert.Equal("protocol.version_range_invalid", ProductIpcRequestValidator.Validate(request)?.Code);
    }

    [Fact]
    public void ImportCommitRequiresCapabilityIdAndExactSha256()
    {
        var missingId = ValidRequest() with
        {
            Method = ProductIpcProtocol.ServerImportCommitMethod,
            ManifestSha256 = new string('A', 64),
        };
        var invalidHash = missingId with
        {
            ImportId = Guid.NewGuid(),
            ManifestSha256 = "not-a-sha256",
        };

        Assert.Equal("protocol.import_id_required", ProductIpcRequestValidator.Validate(missingId)?.Code);
        Assert.Equal(
            "protocol.import_manifest_hash_invalid",
            ProductIpcRequestValidator.Validate(invalidHash)?.Code);
    }

    [Fact]
    public void ServiceAdministrationMethods_RequireServerAndOpaqueBackupIdentity()
    {
        var missingRegistrationServer = ValidRequest() with
        {
            Method = ProductIpcProtocol.ServerRegistrationMethod,
        };
        var missingBackupServer = ValidRequest() with
        {
            Method = ProductIpcProtocol.ServerBackupListMethod,
        };
        var missingSettings = ValidRequest() with
        {
            Method = ProductIpcProtocol.ServerSettingsUpdateMethod,
            ServerId = Guid.NewGuid(),
        };
        var invalidRestoreId = ValidRequest() with
        {
            Method = ProductIpcProtocol.ServerBackupRestoreMethod,
            ServerId = Guid.NewGuid(),
            BackupId = "../outside.zip",
        };
        var shortOpaqueRestoreId = invalidRestoreId with
        {
            BackupId = new string('a', 63),
        };
        var validRestore = invalidRestoreId with
        {
            BackupId = new string('a', 64),
        };

        Assert.Equal(
            "protocol.server_id_required",
            ProductIpcRequestValidator.Validate(missingRegistrationServer)?.Code);
        Assert.Equal(
            "protocol.server_id_required",
            ProductIpcRequestValidator.Validate(missingBackupServer)?.Code);
        Assert.Equal(
            "protocol.backup_id_invalid",
            ProductIpcRequestValidator.Validate(invalidRestoreId)?.Code);
        Assert.Equal(
            "protocol.backup_id_invalid",
            ProductIpcRequestValidator.Validate(shortOpaqueRestoreId)?.Code);
        Assert.Equal(
            "protocol.server_settings_required",
            ProductIpcRequestValidator.Validate(missingSettings)?.Code);
        Assert.Null(ProductIpcRequestValidator.Validate(validRestore));
    }

    [Fact]
    public void ModpackUpdateMethods_RequireDefinitionCapabilityAndExactManifestHash()
    {
        var missingDefinition = ValidRequest() with
        {
            Method = ProductIpcProtocol.ServerModpackUpdateBeginMethod,
        };
        var missingId = ValidRequest() with
        {
            Method = ProductIpcProtocol.ServerModpackUpdateStatusMethod,
        };
        var badCommitHash = ValidRequest() with
        {
            Method = ProductIpcProtocol.ServerModpackUpdateCommitMethod,
            ModpackUpdateId = Guid.NewGuid(),
            ManifestSha256 = "not-a-sha256",
        };
        var validCommit = badCommitHash with
        {
            ManifestSha256 = new string('A', 64),
        };

        Assert.Equal(
            "protocol.modpack_update_begin_required",
            ProductIpcRequestValidator.Validate(missingDefinition)?.Code);
        Assert.Equal(
            "protocol.modpack_update_id_required",
            ProductIpcRequestValidator.Validate(missingId)?.Code);
        Assert.Equal(
            "protocol.modpack_update_manifest_hash_invalid",
            ProductIpcRequestValidator.Validate(badCommitHash)?.Code);
        Assert.Null(ProductIpcRequestValidator.Validate(validCommit));
    }

    [Fact]
    public void ProviderMethods_RequireSafeIdentifiersAndMethodSpecificPayloads()
    {
        var missingId = ValidRequest() with
        {
            Method = ProductIpcProtocol.ProviderHealthMethod,
        };
        var missingEnabled = ValidRequest() with
        {
            Method = ProductIpcProtocol.ProviderSetEnabledMethod,
            ProviderId = "muhun.catalog",
        };
        var traversal = ValidProviderInstall() with { InboxFileName = "..\\provider.mcsvp" };
        var badInstall = ValidRequest() with
        {
            Method = ProductIpcProtocol.ProviderInstallMethod,
            ProviderInstall = traversal,
        };
        var unrelatedSecret = ValidRequest() with
        {
            ProviderInstall = ValidProviderInstall(),
        };

        Assert.Equal("protocol.provider_id_required", ProductIpcRequestValidator.Validate(missingId)?.Code);
        Assert.Equal(
            "protocol.provider_enabled_required",
            ProductIpcRequestValidator.Validate(missingEnabled)?.Code);
        Assert.Equal("protocol.provider_install_invalid", ProductIpcRequestValidator.Validate(badInstall)?.Code);
        Assert.Equal(
            "protocol.provider_install_unexpected",
            ProductIpcRequestValidator.Validate(unrelatedSecret)?.Code);
    }

    [Fact]
    public void ProviderPublisherPin_RejectsPrivateKeysAtIpcBoundary()
    {
        var request = ValidRequest() with
        {
            Method = ProductIpcProtocol.ProviderPublisherPinMethod,
            ProviderPublisherPin = new ProductPinProviderPublisherRequest(
                "muhun.publisher",
                "-----BEGIN PRIVATE KEY-----\nnot-a-public-key\n-----END PRIVATE KEY-----"),
        };

        Assert.Equal(
            "protocol.provider_publisher_pin_invalid",
            ProductIpcRequestValidator.Validate(request)?.Code);
    }

    private static ProductProviderInstallFromInboxRequest ValidProviderInstall() => new(
        "provider.mcsvp",
        new string('a', 64),
        "muhun.provider",
        "1.0.0",
        "muhun.publisher",
        new ProductProviderDetachedSignature(
            "muhun.publisher",
            "ECDSA-P256-SHA256",
            Convert.ToBase64String([1, 2, 3, 4]),
            1));

    private static ProductServerSettingsUpdateRequest CompleteServiceInstanceSettings() => new(
        "Server",
        1024,
        4096,
        25565,
        false)
    {
        MemoryAllocationMode = ProductServerMemoryAllocationMode.Automatic,
        SeparateDiagnosticOutput = true,
        EnableHangWatchdog = true,
        WatchdogCheckIntervalSeconds = 45,
        WatchdogProbeTimeoutSeconds = 9,
        WatchdogFailureThreshold = 4,
        WatchdogStartupGraceSeconds = 240,
        EnableAutomaticRecoveryPoints = true,
        RecoveryPointIntervalMinutes = 60,
        RecoveryPointRetentionCount = 5,
    };

    private static ProductIpcRequest ValidRequest() => new(
        ProductIpcProtocol.CurrentSchemaVersion,
        Guid.NewGuid(),
        ProductIpcProtocol.HandshakeMethod,
        ProductApiProtocol.MinimumSupportedVersion,
        ProductApiProtocol.CurrentVersion);
}
