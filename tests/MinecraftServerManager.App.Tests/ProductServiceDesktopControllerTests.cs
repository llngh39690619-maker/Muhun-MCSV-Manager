using MinecraftServerManager.App.Services;
using MinecraftServerManager.Client;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.App.Tests;

public sealed class ProductServiceDesktopControllerTests
{
    [Fact]
    public async Task Refresh_UsesServiceListStatusAndMonotonicConsoleCursor()
    {
        var serverId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var client = new StubClient(serverId, sessionId);
        await using var controller = new ProductServiceDesktopController(client);

        var first = await controller.RefreshAsync();
        var second = await controller.RefreshAsync();

        Assert.True(first.Connection.IsConnected);
        Assert.Equal([0L, 1L], client.ConsoleRequests);
        Assert.Equal("first", Assert.Single(first.Servers).Console.Entries.Single().Text);
        Assert.Equal("second", Assert.Single(second.Servers).Console.Entries.Single().Text);
    }

    [Fact]
    public async Task Refresh_WhenServiceUnavailable_ReturnsNoLegacyProjection()
    {
        var client = new StubClient(Guid.NewGuid(), Guid.NewGuid())
        {
            HandshakeError = new ProductServiceClientException(
                "service.connection_failed",
                "pipe is not available"),
        };
        await using var controller = new ProductServiceDesktopController(client);

        var snapshot = await controller.RefreshAsync();

        Assert.Equal(ProductServiceConnectionState.Unavailable, snapshot.Connection.State);
        Assert.Empty(snapshot.Servers);
        Assert.Equal(0, client.ListCalls);
    }

    [Fact]
    public async Task Mutations_AreSentOnlyToServiceClient()
    {
        var serverId = Guid.NewGuid();
        var client = new StubClient(serverId, Guid.NewGuid());
        await using var controller = new ProductServiceDesktopController(client);

        await controller.StartAsync(serverId);
        await controller.SendCommandAsync(serverId, "say service-owned");
        await controller.RestartAsync(serverId);
        await controller.StopAsync(serverId);

        Assert.Equal(
            ["start", "command:say service-owned", "restart", "stop"],
            client.Mutations);
    }

    [Fact]
    public async Task Refresh_RejectsInvalidBulkStatusWithoutPublishingPartialData()
    {
        var client = new StubClient(Guid.NewGuid(), Guid.NewGuid())
        {
            ReturnInvalidBulkStatus = true,
        };
        await using var controller = new ProductServiceDesktopController(client);

        var snapshot = await controller.RefreshAsync();

        Assert.Equal(ProductServiceConnectionState.Faulted, snapshot.Connection.State);
        Assert.Equal("service.refresh_failed", snapshot.Connection.Code);
        Assert.Empty(snapshot.Servers);
    }

    [Fact]
    public async Task AdministrationAndBackupOperations_AreForwardedOnlyToServiceClient()
    {
        var serverId = Guid.NewGuid();
        var client = new StubClient(serverId, Guid.NewGuid());
        await using var controller = new ProductServiceDesktopController(client);
        var registration = await controller.GetRegistrationAsync(serverId);

        await controller.UpdateRegistrationAsync(registration with { Name = "edited" });
        var backups = await controller.ListBackupsAsync(serverId);
        await controller.CreateBackupAsync(serverId);
        await controller.RestoreBackupAsync(serverId, backups[0].BackupId);
        await controller.RemoveAsync(serverId);

        Assert.Equal(
            ["register:edited", "backup:create", "backup:restore", "remove"],
            client.Mutations);
    }

    [Fact]
    public async Task PlayerPresence_IsReadOnlyAndForwardedOnlyToServiceClient()
    {
        var serverId = Guid.NewGuid();
        var client = new StubClient(serverId, Guid.NewGuid());
        await using var controller = new ProductServiceDesktopController(client);

        var players = await controller.ListPlayersAsync(serverId);

        Assert.Equal("PlayerOne", Assert.Single(players.Players).Name);
        Assert.Equal(["players:list"], client.Mutations);
    }

    [Fact]
    public async Task ProviderAdministration_IsForwardedOnlyAcrossServiceClientBoundary()
    {
        var client = new StubClient(Guid.NewGuid(), Guid.NewGuid());
        await using var controller = new ProductServiceDesktopController(client);

        var providers = await controller.ListProvidersAsync();
        var publishers = await controller.ListTrustedProviderPublishersAsync();
        await controller.SetProviderEnabledAsync(providers[0].Id, false);
        await controller.CheckProviderHealthAsync(providers[0].Id);
        await controller.UninstallProviderAsync(providers[0].Id);
        await controller.PinProviderPublisherAsync(
            new ProductPinProviderPublisherRequest("muhun.new", "PUBLIC KEY"));
        await controller.RemoveProviderPublisherAsync(publishers[0].PublisherId);
        await controller.InstallProviderFromInboxAsync(new ProductProviderInstallFromInboxRequest(
            "provider.mcsvp",
            new string('a', 64),
            "muhun.catalog",
            "1.0.0",
            "muhun.publisher",
            new ProductProviderDetachedSignature(
                "muhun.publisher",
                "ECDSA-P256-SHA256",
                Convert.ToBase64String([1, 2, 3, 4]),
                1)));

        Assert.Equal(
            [
                "provider:disable", "provider:health", "provider:uninstall",
                "publisher:pin", "publisher:remove", "provider:install",
            ],
            client.ProviderMutations);
    }

    private sealed class StubClient(Guid serverId, Guid sessionId) : IProductServiceClient
    {
        private ProductServerRegistration _registration = Registration(serverId);
        private readonly ProductServerBackupSummary _backup = new(
            new string('a', 64),
            "backup.zip",
            100,
            DateTimeOffset.UtcNow);
        public ProductServiceClientException? HandshakeError { get; init; }

        public bool ReturnInvalidBulkStatus { get; init; }

        public int ListCalls { get; private set; }

        public List<long> ConsoleRequests { get; } = [];

        public List<string> Mutations { get; } = [];

        public List<string> ProviderMutations { get; } = [];

        public Task<ProductLocalHandshakePayload> HandshakeAsync(
            CancellationToken cancellationToken = default)
        {
            if (HandshakeError is not null)
            {
                return Task.FromException<ProductLocalHandshakePayload>(HandshakeError);
            }

            return Task.FromResult(new ProductLocalHandshakePayload(
                new ProductHandshakeResponse(
                    "Muhun MCSV Manager",
                    "1.0.0",
                    ProductApiProtocol.CurrentVersion,
                    ProductApiProtocol.MinimumSupportedVersion,
                    Ready: true),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<ProductServerSummary>> ListServersAsync(
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<ProductServerSummary>>([Summary(serverId)]);
        }

        public Task<ProductServerStatus> GetStatusAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Status(requestedServerId));
        }

        public Task<ProductServerRegistration> GetRegistrationAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_registration);
        }

        public Task<IReadOnlyList<ProductServerStatus>> ListStatusesAsync(
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            var actualId = ReturnInvalidBulkStatus ? Guid.Empty : serverId;
            return Task.FromResult<IReadOnlyList<ProductServerStatus>>([Status(actualId)]);
        }

        public Task<ProductConsolePage> ReadConsoleAsync(
            Guid requestedServerId,
            long afterCursor,
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            ConsoleRequests.Add(afterCursor);
            var next = afterCursor + 1;
            return Task.FromResult(new ProductConsolePage(
                requestedServerId,
                afterCursor,
                next,
                next,
                HistoryGap: false,
                [new ProductConsoleEntry(
                    next,
                    sessionId,
                    DateTimeOffset.UtcNow,
                    afterCursor == 0 ? "first" : "second",
                    ProductConsoleStream.StandardOutput,
                    ProductConsoleSeverity.Information,
                    null,
                    false,
                false)]));
        }

        public Task<ProductServerPlayerList> ListPlayersAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add("players:list");
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProductServerPlayerList(
                requestedServerId,
                now,
                [new ProductServerPlayerSummary("PlayerOne", now)]));
        }

        public Task<ProductServerMutationResult> StartAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add("start");
            return Task.FromResult(Mutation(requestedServerId));
        }

        public Task<ProductServerMutationResult> StopAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add("stop");
            return Task.FromResult(Mutation(requestedServerId));
        }

        public Task<ProductServerMutationResult> RestartAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add("restart");
            return Task.FromResult(Mutation(requestedServerId));
        }

        public Task<ProductServerStatus> SendCommandAsync(
            Guid requestedServerId,
            string command,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add("command:" + command);
            return Task.FromResult(Status(requestedServerId));
        }

        public Task<ProductServerStatus> RegisterAsync(
            ProductServerRegistration registration,
            CancellationToken cancellationToken = default)
        {
            _registration = registration;
            Mutations.Add("register:" + registration.Name);
            return Task.FromResult(Status(registration.Id));
        }

        public Task<ProductServerSettingsUpdateResult> UpdateServerSettingsAsync(
            Guid requestedServerId,
            ProductServerSettingsUpdateRequest settings,
            CancellationToken cancellationToken = default)
        {
            _registration = _registration with
            {
                Name = settings.Name,
                MinimumMemoryMb = settings.MinimumMemoryMb,
                MaximumMemoryMb = settings.MaximumMemoryMb,
                Port = settings.Port,
                AutoRestart = settings.AutoRestart,
            };
            Mutations.Add("register:" + settings.Name);
            return Task.FromResult(new ProductServerSettingsUpdateResult(
                _registration,
                Status(requestedServerId)));
        }

        public Task RemoveAsync(Guid requestedServerId, CancellationToken cancellationToken = default)
        {
            Mutations.Add("remove");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProductServerBackupSummary>> ListBackupsAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProductServerBackupSummary>>([_backup]);

        public Task<ProductServerBackupMutationResult> CreateBackupAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add("backup:create");
            return Task.FromResult(new ProductServerBackupMutationResult(
                requestedServerId,
                _backup,
                DateTimeOffset.UtcNow));
        }

        public Task<ProductServerBackupRestoreResult> RestoreBackupAsync(
            Guid requestedServerId,
            string backupId,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add("backup:restore");
            return Task.FromResult(new ProductServerBackupRestoreResult(
                requestedServerId,
                backupId,
                DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<ProductProviderSummary>> ListProvidersAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProductProviderSummary>>([Provider()]);

        public Task<IReadOnlyList<ProductTrustedProviderPublisherSummary>> ListProviderPublishersAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProductTrustedProviderPublisherSummary>>(
                [new("muhun.publisher", new string('a', 64), DateTimeOffset.UtcNow)]);

        public Task<ProductProviderSummary> SetProviderEnabledAsync(
            string providerId,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            ProviderMutations.Add(enabled ? "provider:enable" : "provider:disable");
            return Task.FromResult(Provider() with { Enabled = enabled });
        }

        public Task<ProductProviderHealthCheckResult> CheckProviderHealthAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            ProviderMutations.Add("provider:health");
            return Task.FromResult(new ProductProviderHealthCheckResult(providerId, true, null));
        }

        public Task UninstallProviderAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            ProviderMutations.Add("provider:uninstall");
            return Task.CompletedTask;
        }

        public Task<ProductTrustedProviderPublisherSummary> PinProviderPublisherAsync(
            ProductPinProviderPublisherRequest request,
            CancellationToken cancellationToken = default)
        {
            ProviderMutations.Add("publisher:pin");
            return Task.FromResult(new ProductTrustedProviderPublisherSummary(
                request.PublisherId,
                new string('b', 64),
                DateTimeOffset.UtcNow));
        }

        public Task RemoveProviderPublisherAsync(
            string publisherId,
            CancellationToken cancellationToken = default)
        {
            ProviderMutations.Add("publisher:remove");
            return Task.CompletedTask;
        }

        public Task<ProductProviderSummary> InstallProviderFromInboxAsync(
            ProductProviderInstallFromInboxRequest request,
            CancellationToken cancellationToken = default)
        {
            ProviderMutations.Add("provider:install");
            return Task.FromResult(Provider());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static ProductServerSummary Summary(Guid id)
            => new(id, "Service Server", ProductServerState.Running, 25565, "NeoForge", "1.21.1");

        private static ProductServerStatus Status(Guid id)
            => new(
                Summary(id),
                Guid.NewGuid(),
                1234,
                DateTimeOffset.UtcNow,
                null,
                new ProductServerResourceSample(
                    DateTimeOffset.UtcNow,
                    1.5,
                    512 * 1024 * 1024,
                    600 * 1024 * 1024,
                    TimeSpan.FromMinutes(1)),
                null);

        private static ProductServerRegistration Registration(Guid id)
            => new()
            {
                Id = id,
                Name = "Service Server",
                ServerDirectory = "service-server",
                JavaRuntimePath = "temurin-21/bin/java.exe",
                CoreType = "NeoForge",
                MinecraftVersion = "1.21.1",
                Port = 25565,
            };

        private static ProductServerMutationResult Mutation(Guid id)
            => new(id, true, Status(id));

        private static ProductProviderSummary Provider()
            => new(
                "muhun.catalog",
                "Muhun catalogue",
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
    }
}
