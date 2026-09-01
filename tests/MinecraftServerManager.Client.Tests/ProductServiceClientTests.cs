using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;
using MinecraftServerManager.Contracts.Security;

namespace MinecraftServerManager.Client.Tests;

public sealed class ProductServiceClientTests
{
    [Fact]
    public async Task Handshake_UsesBoundedCorrelatedFrame()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var installationId = Guid.NewGuid();
        var server = RunServerOnceAsync(pipeName, request => new ProductIpcResponse(
            1,
            request.RequestId,
            true,
            new ProductLocalHandshakePayload(
                new ProductHandshakeResponse(
                    "Muhun MCSV Manager",
                    "1.0.0",
                    ProductApiProtocol.CurrentVersion,
                    ProductApiProtocol.MinimumSupportedVersion,
                    true),
                installationId,
                DateTimeOffset.UtcNow),
            null));
        await using var client = new ProductServiceClient(pipeName);

        var result = await client.HandshakeAsync();

        Assert.Equal(installationId, result.InstallationId);
        await server;
    }

    [Fact]
    public async Task ConfirmedMinecraftEulaStart_RequiresApi16AndRoundTripsOneShotFlag()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerStartMethod, request.Method);
            Assert.Equal(serverId, request.ServerId);
            Assert.Equal(
                ProductApiProtocol.MinecraftEulaConsentVersion,
                request.ClientMinimumApiVersion);
            Assert.Equal(ProductApiProtocol.CurrentVersion, request.ClientMaximumApiVersion);
            Assert.True(request.AcceptMinecraftEula is true);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                Mutation = new ProductServerMutationResult(
                    serverId,
                    true,
                    CreateStatus(serverId, "Paper")),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var result = await client.StartAsync(serverId, acceptMinecraftEula: true);

        Assert.Equal(serverId, result.ServerId);
        Assert.True(result.Changed);
        await server;
    }

    [Fact]
    public async Task UnconfirmedMinecraftEulaStart_RemainsCompatibleAndOmitsOneShotFlag()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductApiProtocol.MinimumSupportedVersion, request.ClientMinimumApiVersion);
            Assert.Null(request.AcceptMinecraftEula);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                Mutation = new ProductServerMutationResult(
                    serverId,
                    false,
                    CreateStatus(serverId, "Paper")),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var result = await client.StartAsync(serverId);

        Assert.False(result.Changed);
        await server;
    }

    [Fact]
    public async Task ServiceError_PreservesStableCode()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var server = RunServerOnceAsync(pipeName, request => new ProductIpcResponse(
            1,
            request.RequestId,
            false,
            null,
            new ProductIpcError("service.not_ready", "Service is not ready.")));
        await using var client = new ProductServiceClient(pipeName);

        var exception = await Assert.ThrowsAsync<ProductServiceClientException>(
            () => client.HandshakeAsync());

        Assert.Equal("service.not_ready", exception.Code);
        await server;
    }

    [Fact]
    public async Task MismatchedRequestIdentifier_IsRejected()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var server = RunServerOnceAsync(pipeName, _ => new ProductIpcResponse(
            1,
            Guid.NewGuid(),
            true,
            new ProductLocalHandshakePayload(
                new ProductHandshakeResponse(
                    "Muhun MCSV Manager",
                    "1.0.0",
                    ProductApiProtocol.CurrentVersion,
                    ProductApiProtocol.MinimumSupportedVersion,
                    true),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow),
            null));
        await using var client = new ProductServiceClient(pipeName);

        var exception = await Assert.ThrowsAsync<ProductServiceClientException>(
            () => client.HandshakeAsync());

        Assert.Equal("service.connection_failed", exception.Code);
        await server;
    }

    [Fact]
    public async Task OversizedResponse_IsRejectedBeforeAllocation()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var server = Task.Run(async () =>
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync();
            _ = await ReadRequestAsync(pipe);
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, ProductIpcProtocol.MaximumFrameBytes + 1);
            await pipe.WriteAsync(header);
        });
        await using var client = new ProductServiceClient(pipeName);

        var exception = await Assert.ThrowsAsync<ProductServiceClientException>(
            () => client.HandshakeAsync());

        Assert.Equal("service.connection_failed", exception.Code);
        await server;
    }

    [Fact]
    public async Task ListStatuses_UsesOneBoundedStatusPageInsteadOfPerServerRequests()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerStatusListMethod, request.Method);
            Assert.Equal(0, request.ListOffset);
            Assert.Equal(50, request.ListLimit);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                ServerStatusPage = new ProductServerStatusPage(
                    0,
                    2,
                    false,
                    [CreateStatus(firstId, "A"), CreateStatus(secondId, "B")]),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var statuses = await client.ListStatusesAsync();

        Assert.Equal([firstId, secondId], statuses.Select(value => value.Server.Id));
        await server;
    }

    [Theory]
    [InlineData("major-too-large")]
    [InlineData("available-not-configured")]
    [InlineData("empty-required-metadata")]
    public async Task GetStatus_RejectsUnsafeJavaRuntimeMetadata(string invalidCase)
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var java = invalidCase switch
        {
            "major-too-large" => new ProductServerJavaRuntimeSummary(
                true, true, 100, "100", "JDK", "Vendor", "x64"),
            "available-not-configured" => new ProductServerJavaRuntimeSummary(
                false, true, 21, "21", "JDK", "Vendor", "x64"),
            "empty-required-metadata" => new ProductServerJavaRuntimeSummary(
                true, true, 21, "21", "", "Vendor", "x64"),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase)),
        };
        var server = RunServerOnceAsync(pipeName, request => new ProductIpcResponse(
            1,
            request.RequestId,
            true,
            null,
            null)
        {
            Server = CreateStatus(serverId, "Unsafe Java") with { Java = java },
        });
        await using var client = new ProductServiceClient(pipeName);

        var error = await Assert.ThrowsAsync<ProductServiceClientException>(
            () => client.GetStatusAsync(serverId));

        Assert.Equal("protocol.payload_invalid", error.Code);
        await server;
    }

    [Fact]
    public async Task PlayerList_UsesPathFreeBoundedServiceProjection()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var captured = DateTimeOffset.UtcNow;
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerPlayersMethod, request.Method);
            Assert.Equal(serverId, request.ServerId);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                Players = new ProductServerPlayerList(
                    serverId,
                    captured,
                    [new ProductServerPlayerSummary("PlayerOne", captured)]),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var result = await client.ListPlayersAsync(serverId);

        Assert.Equal(serverId, result.ServerId);
        Assert.Equal("PlayerOne", Assert.Single(result.Players).Name);
        await server;
    }

    [Fact]
    public async Task PlayerList_RejectsCrossServerProjection()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var server = RunServerOnceAsync(pipeName, request => new ProductIpcResponse(
            1, request.RequestId, true, null, null)
        {
            Players = new ProductServerPlayerList(Guid.NewGuid(), DateTimeOffset.UtcNow, []),
        });
        await using var client = new ProductServiceClient(pipeName);

        var error = await Assert.ThrowsAsync<ProductServiceClientException>(
            () => client.ListPlayersAsync(serverId));

        Assert.Equal("protocol.payload_invalid", error.Code);
        await server;
    }

    [Fact]
    public async Task RemoteAccessStatus_UsesServiceOwnedIpcMethod()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.RemoteAccessStatusMethod, request.Method);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                RemoteAccess = new ProductRemoteAccessStatus(
                    true,
                    true,
                    true,
                    "https://example.ts.net/",
                    "running",
                    null,
                    DateTimeOffset.UtcNow,
                    null),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var status = await client.GetRemoteAccessStatusAsync();

        Assert.True(status.FunnelRunning);
        Assert.Equal("https://example.ts.net/", status.PublicUrl);
        await server;
    }

    [Fact]
    public async Task RemoteAccountList_PaginatesOneMaximumGrantAccountPerFrame()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var server = RunServerAsync(pipeName, 2, request =>
        {
            Assert.Equal(ProductIpcProtocol.RemoteAccountListMethod, request.Method);
            Assert.Equal(1, request.ListLimit);
            var offset = request.ListOffset!.Value;
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                RemoteAccountPage = new ProductRemoteAccountPage(
                    offset,
                    offset + 1,
                    offset == 0,
                    [Account(
                        offset == 0 ? "operator01" : "operator02",
                        offset == 0 ? ProductRemoteAccountRole.Admin : ProductRemoteAccountRole.Operator)]),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var accounts = await client.ListRemoteAccountsAsync();

        Assert.Equal(["operator01", "operator02"], accounts.Select(account => account.Username));
        Assert.Equal(
            [ProductRemoteAccountRole.Admin, ProductRemoteAccountRole.Operator],
            accounts.Select(account => account.Role));
        await server;
    }

    [Fact]
    public async Task DiscordWebhookStatus_ReturnsOnlyConfiguredState()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.NotificationDiscordStatusMethod, request.Method);
            Assert.Null(request.DiscordWebhook);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                DiscordWebhookConfiguration = new ProductDiscordWebhookConfiguration(true),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var configuration = await client.GetDiscordWebhookConfigurationAsync();

        Assert.True(configuration.Configured);
        await server;
    }

    [Fact]
    public async Task NotificationPreferences_SetUsesVersionedBoundedPolicy()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var requested = ProductNotificationPreferences.Default with
        {
            ProductUpdates = false,
            ExternalThrottleSeconds = 45,
        };
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.NotificationPreferencesSetMethod, request.Method);
            Assert.Equal(requested, request.NotificationPreferences);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                NotificationPreferences = requested,
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        Assert.Equal(requested, await client.SetNotificationPreferencesAsync(requested));
        await server;
    }

    [Fact]
    public async Task GetRegistration_ReturnsRelativeServiceOwnedDefinition()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerRegistrationMethod, request.Method);
            Assert.Equal(serverId, request.ServerId);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                Registration = Registration(serverId),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var registration = await client.GetRegistrationAsync(serverId);

        Assert.Equal(serverId, registration.Id);
        Assert.False(Path.IsPathFullyQualified(registration.ServerDirectory));
        await server;
    }

    [Fact]
    public async Task ServerDirectory_UsesServerIdentityAndValidatesAbsoluteLocalPath()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var directoryPath = Path.Combine(Path.GetTempPath(), $"managed-{serverId:N}");
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerDirectoryMethod, request.Method);
            Assert.Equal(serverId, request.ServerId);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                ServerDirectory = new ProductServerDirectoryInfo(serverId, directoryPath, true),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var result = await client.GetServerDirectoryAsync(serverId);

        Assert.Equal(serverId, result.ServerId);
        Assert.Equal(directoryPath, result.DirectoryPath);
        Assert.True(result.Exists);
        await server;
    }

    [Fact]
    public async Task ServerAdministration_UsesServerIdentityAndReturnsOnlyBoundedMetadata()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var capturedAt = DateTimeOffset.UtcNow;
        var snapshot = new ProductServerAdministrationSnapshot(
            serverId,
            capturedAt,
            true,
            [new ProductServerAddonSummary(ProductServerAddonKind.Mod, "safe.jar", 42)],
            false,
            new ProductServerJavaRuntimeSummary(true, true, 21, "21.0.8", "JRE", "Temurin", "x64"));
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerAdministrationMethod, request.Method);
            Assert.Equal(serverId, request.ServerId);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                ServerAdministration = snapshot,
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var result = await client.GetServerAdministrationAsync(serverId);

        Assert.Equal(snapshot.ServerId, result.ServerId);
        Assert.Equal(snapshot.CapturedAtUtc, result.CapturedAtUtc);
        Assert.Equal(snapshot.Java, result.Java);
        Assert.Equal("safe.jar", Assert.Single(result.Addons).FileName);
        await server;
    }

    [Fact]
    public async Task ServerPropertiesRead_RequiresApi17AndValidatesBoundedDocument()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var text = "server-port=25565\n";
        var revision = ProductServerPropertiesContract.CalculateRevision(text);
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerPropertiesReadMethod, request.Method);
            Assert.Equal(serverId, request.ServerId);
            Assert.Equal(ProductApiProtocol.ServerPropertiesEditorVersion, request.ClientMinimumApiVersion);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                ServerProperties = new ProductServerPropertiesDocument(
                    serverId,
                    true,
                    text,
                    revision),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var result = await client.ReadServerPropertiesAsync(serverId);

        Assert.Equal("server-port=25565\n", result.Text);
        Assert.Equal(revision, result.RevisionSha256);
        await server;
    }

    [Fact]
    public async Task ServerPropertiesUpdate_SendsRevisionAndRejectsCrossServerResponse()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var update = new ProductServerPropertiesUpdateRequest(
            "server-port=25570\n",
            new string('b', 64));
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerPropertiesUpdateMethod, request.Method);
            Assert.Equal(ProductApiProtocol.ServerPropertiesEditorVersion, request.ClientMinimumApiVersion);
            Assert.Equal(update, request.ServerPropertiesUpdate);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                ServerProperties = new ProductServerPropertiesDocument(
                    Guid.NewGuid(),
                    true,
                    update.Text,
                    new string('c', 64)),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var error = await Assert.ThrowsAsync<ProductServiceClientException>(() =>
            client.UpdateServerPropertiesAsync(serverId, update));

        Assert.Equal("protocol.payload_invalid", error.Code);
        await server;
    }

    [Fact]
    public async Task ServerPropertiesRead_NullTextIsRejectedAsInvalidPayload()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var server = RunServerOnceAsync(pipeName, request =>
            new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                ServerProperties = new ProductServerPropertiesDocument(
                    serverId,
                    true,
                    null!,
                    new string('a', 64)),
            });
        await using var client = new ProductServiceClient(pipeName);

        var error = await Assert.ThrowsAsync<ProductServiceClientException>(() =>
            client.ReadServerPropertiesAsync(serverId));

        Assert.Equal("protocol.payload_invalid", error.Code);
        await server;
    }

    [Fact]
    public async Task ServerPropertiesRead_DigestMismatchIsRejectedAsInvalidPayload()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var server = RunServerOnceAsync(pipeName, request =>
            new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                ServerProperties = new ProductServerPropertiesDocument(
                    serverId,
                    true,
                    "server-port=25565\n",
                    new string('a', 64)),
            });
        await using var client = new ProductServiceClient(pipeName);

        var error = await Assert.ThrowsAsync<ProductServiceClientException>(() =>
            client.ReadServerPropertiesAsync(serverId));

        Assert.Equal("protocol.payload_invalid", error.Code);
        await server;
    }

    [Fact]
    public async Task PermanentDelete_UsesServerIdentityAndValidatesCommittedResult()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var completedAtUtc = DateTimeOffset.UtcNow;
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerDeleteMethod, request.Method);
            Assert.Equal(serverId, request.ServerId);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                ServerDeletion = new ProductServerDeletionResult(serverId, true, completedAtUtc),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var result = await client.DeleteServerPermanentlyAsync(serverId);

        Assert.Equal(serverId, result.ServerId);
        Assert.True(result.Deleted);
        Assert.Equal(completedAtUtc, result.CompletedAtUtc);
        await server;
    }

    [Fact]
    public async Task UpdateServerSettings_NeverSendsLaunchPathsOrFullRegistration()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var stored = Registration(serverId) with
        {
            Name = "Edited",
            Port = 25570,
            MinimumMemoryMb = 2048,
            MaximumMemoryMb = 4096,
            AutoRestart = true,
        };
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerSettingsUpdateMethod, request.Method);
            Assert.Equal(serverId, request.ServerId);
            Assert.Equal(ProductApiProtocol.MinimumSupportedVersion, request.ClientMinimumApiVersion);
            Assert.Equal(ProductApiProtocol.CurrentVersion, request.ClientMaximumApiVersion);
            Assert.Null(request.Server);
            Assert.Equal("Edited", request.ServerSettings!.Name);
            Assert.Null(request.ServerSettings.MemoryAllocationMode);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                Registration = stored,
                Server = CreateStatus(serverId, stored.Name) with
                {
                    Server = new ProductServerSummary(
                        serverId,
                        stored.Name,
                        ProductServerState.Stopped,
                        stored.Port,
                        stored.CoreType,
                        stored.MinecraftVersion),
                },
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var result = await client.UpdateServerSettingsAsync(
            serverId,
            new ProductServerSettingsUpdateRequest("Edited", 2048, 4096, 25570, true));

        Assert.Equal(stored.Name, result.Registration.Name);
        Assert.Equal(stored.ServerDirectory, result.Registration.ServerDirectory);
        await server;
    }

    [Fact]
    public async Task CompleteServiceInstanceSettings_RequireApi18AndRoundTripPayload()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var settings = CompleteServiceInstanceSettings();
        var stored = Registration(serverId) with
        {
            Name = settings.Name,
            Port = settings.Port,
            MinimumMemoryMb = settings.MinimumMemoryMb,
            MaximumMemoryMb = settings.MaximumMemoryMb,
            AutoRestart = settings.AutoRestart,
            MemoryAllocationMode = settings.MemoryAllocationMode!.Value,
            SeparateDiagnosticOutput = settings.SeparateDiagnosticOutput!.Value,
            EnableHangWatchdog = settings.EnableHangWatchdog!.Value,
            WatchdogCheckIntervalSeconds = settings.WatchdogCheckIntervalSeconds!.Value,
            WatchdogProbeTimeoutSeconds = settings.WatchdogProbeTimeoutSeconds!.Value,
            WatchdogFailureThreshold = settings.WatchdogFailureThreshold!.Value,
            WatchdogStartupGraceSeconds = settings.WatchdogStartupGraceSeconds!.Value,
            EnableAutomaticRecoveryPoints = settings.EnableAutomaticRecoveryPoints!.Value,
            RecoveryPointIntervalMinutes = settings.RecoveryPointIntervalMinutes!.Value,
            RecoveryPointRetentionCount = settings.RecoveryPointRetentionCount!.Value,
        };
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerSettingsUpdateMethod, request.Method);
            Assert.Equal(ProductApiProtocol.ServiceInstanceSettingsVersion, request.ClientMinimumApiVersion);
            Assert.Equal(ProductApiProtocol.CurrentVersion, request.ClientMaximumApiVersion);
            Assert.Equal(settings, request.ServerSettings);
            return SettingsUpdateResponse(request.RequestId, stored);
        });
        await using var client = new ProductServiceClient(pipeName);

        var result = await client.UpdateServerSettingsAsync(serverId, settings);

        Assert.Equal(ProductServerMemoryAllocationMode.Automatic, result.Registration.MemoryAllocationMode);
        Assert.True(result.Registration.EnableHangWatchdog);
        Assert.True(result.Registration.EnableAutomaticRecoveryPoints);
        await server;
    }

    [Fact]
    public async Task CompleteServiceInstanceSettings_AreRejectedByApi17InsteadOfSilentlyIgnored()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var api17 = new ProductApiVersion(1, 7);
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerSettingsUpdateMethod, request.Method);
            Assert.True(request.ClientMinimumApiVersion.CompareTo(api17) > 0);
            return new ProductIpcResponse(
                1,
                request.RequestId,
                false,
                null,
                new ProductIpcError(
                    "protocol.api_version_incompatible",
                    "The requested API capability is newer than this Service."));
        });
        await using var client = new ProductServiceClient(pipeName);

        var error = await Assert.ThrowsAsync<ProductServiceClientException>(
            () => client.UpdateServerSettingsAsync(serverId, CompleteServiceInstanceSettings()));

        Assert.Equal("protocol.api_version_incompatible", error.Code);
        await server;
    }

    [Fact]
    public async Task BackupList_UsesBoundedOpaquePages()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var first = new ProductServerBackupSummary(
            new string('a', 64),
            "first.zip",
            100,
            DateTimeOffset.UtcNow);
        var second = new ProductServerBackupSummary(
            new string('b', 64),
            "second.zip",
            200,
            DateTimeOffset.UtcNow);
        var server = RunServerAsync(pipeName, 2, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerBackupListMethod, request.Method);
            Assert.Equal(serverId, request.ServerId);
            Assert.Equal(50, request.ListLimit);
            var offset = request.ListOffset!.Value;
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                BackupPage = new ProductServerBackupPage(
                    serverId,
                    offset,
                    offset + 1,
                    offset == 0,
                    [offset == 0 ? first : second]),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var backups = await client.ListBackupsAsync(serverId);

        Assert.Equal([first.BackupId, second.BackupId], backups.Select(item => item.BackupId));
        await server;
    }

    [Fact]
    public async Task RestoreBackup_SendsOnlyOpaqueIdAndChecksServerCorrelation()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var backupId = new string('c', 64);
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerBackupRestoreMethod, request.Method);
            Assert.Equal(serverId, request.ServerId);
            Assert.Equal(backupId, request.BackupId);
            Assert.Null(request.Server);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                BackupRestore = new ProductServerBackupRestoreResult(
                    serverId,
                    backupId,
                    DateTimeOffset.UtcNow),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var result = await client.RestoreBackupAsync(serverId, backupId);

        Assert.Equal(backupId, result.BackupId);
        await server;
    }

    [Fact]
    public async Task CreateBackup_SendsOnlyServerIdentityAndValidatesOpaqueResult()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var backup = new ProductServerBackupSummary(
            new string('d', 64),
            "created.zip",
            512,
            DateTimeOffset.UtcNow);
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ServerBackupCreateMethod, request.Method);
            Assert.Equal(serverId, request.ServerId);
            Assert.Null(request.BackupId);
            Assert.Null(request.Server);
            Assert.Null(request.ServerSettings);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                BackupMutation = new ProductServerBackupMutationResult(
                    serverId,
                    backup,
                    DateTimeOffset.UtcNow),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var result = await client.CreateBackupAsync(serverId);

        Assert.Equal(serverId, result.ServerId);
        Assert.Equal(backup.BackupId, result.Backup.BackupId);
        await server;
    }

    [Fact]
    public async Task ModpackUpdateMethods_MapOnlyCapabilityIdDefinitionAndManifestHash()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var serverId = Guid.NewGuid();
        var updateId = Guid.NewGuid();
        var hash = new string('A', 64);
        var begin = new ProductServerModpackUpdateBeginRequest(
            serverId,
            "v1",
            new ProductServerModpackUpdateDefinition
            {
                LaunchKind = ProductServerLaunchKind.ExecutableJar,
                ServerJarPath = "server.jar",
                CoreType = "NeoForge",
                MinecraftVersion = "1.21.1",
                ModpackSource = ProductModpackSourceKind.Modrinth,
                ModpackProjectId = "project",
                ModpackVersionId = "v2",
                ModpackVersionName = "2.0",
            });
        var call = 0;
        var server = RunServerAsync(pipeName, 4, request =>
        {
            switch (call++)
            {
                case 0:
                    Assert.Equal(ProductIpcProtocol.ServerModpackUpdateBeginMethod, request.Method);
                    Assert.Equal(serverId, request.ModpackUpdateBegin?.ServerId);
                    Assert.Equal("v1", request.ModpackUpdateBegin?.ExpectedCurrentVersionId);
                    Assert.Equal("v2", request.ModpackUpdateBegin?.Target.ModpackVersionId);
                    Assert.Equal("server.jar", request.ModpackUpdateBegin?.Target.ServerJarPath);
                    Assert.Null(request.ModpackUpdateId);
                    Assert.Null(request.ManifestSha256);
                    break;
                case 1:
                    Assert.Equal(ProductIpcProtocol.ServerModpackUpdateCommitMethod, request.Method);
                    Assert.Equal(updateId, request.ModpackUpdateId);
                    Assert.Equal(hash, request.ManifestSha256);
                    Assert.Null(request.ModpackUpdateBegin);
                    break;
                case 2:
                    Assert.Equal(ProductIpcProtocol.ServerModpackUpdateStatusMethod, request.Method);
                    Assert.Equal(updateId, request.ModpackUpdateId);
                    Assert.Null(request.ManifestSha256);
                    break;
                case 3:
                    Assert.Equal(ProductIpcProtocol.ServerModpackUpdateCancelMethod, request.Method);
                    Assert.Equal(updateId, request.ModpackUpdateId);
                    Assert.Null(request.ManifestSha256);
                    break;
            }

            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                ModpackUpdate = new ProductServerModpackUpdateStatus(
                    updateId,
                    serverId,
                    ProductServerModpackUpdateState.Staging,
                    null,
                    0,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        Assert.Equal(updateId, (await client.BeginModpackUpdateAsync(begin)).UpdateId);
        Assert.Equal(updateId, (await client.CommitModpackUpdateAsync(updateId, hash)).UpdateId);
        Assert.Equal(updateId, (await client.GetModpackUpdateStatusAsync(updateId)).UpdateId);
        Assert.Equal(updateId, (await client.CancelModpackUpdateAsync(updateId)).UpdateId);
        await server;
    }

    [Fact]
    public async Task ProviderList_PaginatesBoundedSummariesWithoutExecutableOrHostPaths()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var server = RunServerAsync(pipeName, 2, request =>
        {
            Assert.Equal(ProductIpcProtocol.ProviderListMethod, request.Method);
            Assert.Equal(20, request.ListLimit);
            Assert.Null(request.ProviderId);
            Assert.Null(request.ProviderInstall);
            var offset = request.ListOffset!.Value;
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                ProviderPage = new ProductProviderPage(
                    offset,
                    offset + 1,
                    offset == 0,
                    [Provider(offset == 0 ? "muhun.catalog" : "muhun.tunnel")]),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var providers = await client.ListProvidersAsync();

        Assert.Equal(["muhun.catalog", "muhun.tunnel"], providers.Select(value => value.Id));
        await server;
    }

    [Fact]
    public async Task ProviderHealth_RejectsCrossProviderOrUnboundedErrorProjection()
    {
        var pipeName = $"muhun-test-{Guid.NewGuid():N}";
        var server = RunServerOnceAsync(pipeName, request =>
        {
            Assert.Equal(ProductIpcProtocol.ProviderHealthMethod, request.Method);
            Assert.Equal("muhun.catalog", request.ProviderId);
            return new ProductIpcResponse(1, request.RequestId, true, null, null)
            {
                ProviderHealth = new ProductProviderHealthCheckResult(
                    "muhun.attacker",
                    false,
                    new string('x', 512)),
            };
        });
        await using var client = new ProductServiceClient(pipeName);

        var error = await Assert.ThrowsAsync<ProductServiceClientException>(
            () => client.CheckProviderHealthAsync("muhun.catalog"));

        Assert.Equal("protocol.payload_invalid", error.Code);
        await server;
    }

    [Fact]
    public async Task ProviderInstall_RejectsArbitraryPathBeforeOpeningPipe()
    {
        await using var client = new ProductServiceClient($"muhun-test-{Guid.NewGuid():N}");
        var request = new ProductProviderInstallFromInboxRequest(
            "..\\provider.mcsvp",
            new string('a', 64),
            "muhun.catalog",
            "1.0.0",
            "muhun.publisher",
            new ProductProviderDetachedSignature(
                "muhun.publisher",
                "ECDSA-P256-SHA256",
                Convert.ToBase64String([1, 2, 3, 4]),
                1));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.InstallProviderFromInboxAsync(request));
    }

    [Theory]
    [InlineData(ProductIpcProtocol.ServerStopMethod)]
    [InlineData(ProductIpcProtocol.ServerRestartMethod)]
    [InlineData(ProductIpcProtocol.ServerBackupCreateMethod)]
    [InlineData(ProductIpcProtocol.ServerBackupRestoreMethod)]
    [InlineData(ProductIpcProtocol.ServerImportCommitMethod)]
    [InlineData(ProductIpcProtocol.ServerModpackUpdateCommitMethod)]
    [InlineData(ProductIpcProtocol.ServerDeleteMethod)]
    [InlineData(ProductIpcProtocol.UpdateDownloadMethod)]
    [InlineData(ProductIpcProtocol.ProviderInstallMethod)]
    public void LongMutations_DoNotUseLegacyTenSecondClientDeadline(string method)
        => Assert.True(ProductServiceClient.GetRequestTimeout(method) >= TimeSpan.FromMinutes(30));

    [Fact]
    public void ReadOnlyStatus_RetainsShortClientDeadline()
        => Assert.Equal(
            TimeSpan.FromSeconds(10),
            ProductServiceClient.GetRequestTimeout(ProductIpcProtocol.ServerStatusMethod));

    [Fact]
    public void ServerPropertiesUpdate_UsesMutationClientDeadline()
        => Assert.Equal(
            TimeSpan.FromMinutes(2),
            ProductServiceClient.GetRequestTimeout(ProductIpcProtocol.ServerPropertiesUpdateMethod));

    private static Task RunServerOnceAsync(
        string pipeName,
        Func<ProductIpcRequest, ProductIpcResponse> responseFactory)
        => Task.Run(async () =>
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync();
            var request = await ReadRequestAsync(pipe);
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                responseFactory(request),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await pipe.WriteAsync(header);
            await pipe.WriteAsync(payload);
            await pipe.FlushAsync();
        });

    private static Task RunServerAsync(
        string pipeName,
        int requestCount,
        Func<ProductIpcRequest, ProductIpcResponse> responseFactory)
        => Task.Run(async () =>
        {
            for (var index = 0; index < requestCount; index++)
            {
                await using var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync();
                var request = await ReadRequestAsync(pipe);
                var payload = JsonSerializer.SerializeToUtf8Bytes(
                    responseFactory(request),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var header = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await pipe.WriteAsync(header);
                await pipe.WriteAsync(payload);
                await pipe.FlushAsync();
            }
        });

    private static async Task<ProductIpcRequest> ReadRequestAsync(Stream stream)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload);
        return JsonSerializer.Deserialize<ProductIpcRequest>(
                   payload,
                   new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidDataException();
    }

    private static ProductServerStatus CreateStatus(Guid id, string name)
    {
        var summary = new ProductServerSummary(
            id,
            name,
            ProductServerState.Stopped,
            25565,
            "Paper",
            "1.21.1");
        return new ProductServerStatus(summary, null, null, null, null, null, null);
    }

    private static ProductRemoteAccountSummary Account(
        string username,
        ProductRemoteAccountRole role = ProductRemoteAccountRole.Viewer)
        => new(
            username,
            "mcsv-local-approved-account",
            null,
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            [new ProductPermissionGrant(ProductPermissionCodes.UserRead, ProductPermissionScope.Global)],
            role);

    private static ProductProviderSummary Provider(string id)
        => new(
            id,
            $"Provider {id}",
            "1.0.0",
            "muhun.publisher",
            true,
            ProductProviderHealthState.Healthy,
            [ProductProviderCapabilities.ModpackCatalog],
            [ProductProviderPermissions.Http],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            0,
            null);

    private static ProductServerSettingsUpdateRequest CompleteServiceInstanceSettings()
        => new("Edited", 3072, 6144, 25570, true)
        {
            MemoryAllocationMode = ProductServerMemoryAllocationMode.Automatic,
            SeparateDiagnosticOutput = false,
            EnableHangWatchdog = true,
            WatchdogCheckIntervalSeconds = 30,
            WatchdogProbeTimeoutSeconds = 8,
            WatchdogFailureThreshold = 3,
            WatchdogStartupGraceSeconds = 180,
            EnableAutomaticRecoveryPoints = true,
            RecoveryPointIntervalMinutes = 30,
            RecoveryPointRetentionCount = 3,
        };

    private static ProductIpcResponse SettingsUpdateResponse(
        Guid requestId,
        ProductServerRegistration registration)
        => new(1, requestId, true, null, null)
        {
            Registration = registration,
            Server = new ProductServerStatus(
                new ProductServerSummary(
                    registration.Id,
                    registration.Name,
                    ProductServerState.Stopped,
                    registration.Port,
                    registration.CoreType,
                    registration.MinecraftVersion),
                null,
                null,
                null,
                null,
                null,
                null),
        };

    private static ProductServerRegistration Registration(Guid id) => new()
    {
        Id = id,
        Name = "Managed",
        ServerDirectory = "managed-server",
        JavaRuntimePath = "java/bin/java.exe",
        LaunchKind = ProductServerLaunchKind.ExecutableJar,
        ServerJarPath = "server.jar",
        CoreType = "Paper",
        MinimumMemoryMb = 1024,
        MaximumMemoryMb = 2048,
        Port = 25565,
    };
}
