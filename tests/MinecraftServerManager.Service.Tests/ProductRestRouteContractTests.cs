using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Security;
using MinecraftServerManager.Service;
using Microsoft.AspNetCore.Routing;

namespace MinecraftServerManager.Service.Tests;

[Collection(ProductServiceHostCollection.Name)]
public sealed class ProductRestRouteContractTests
{
    [Fact]
    public async Task Service_ExposesCompleteVersionedServerRuntimeRouteSurface()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        await using var application = ProductServiceApplication.Build(
        [
            $"--{ProductServiceOptions.SectionName}:DataRoot={layout.Root}",
            $"--{ProductServiceOptions.SectionName}:Port=39051",
            UniqueIpcPipeArgument(),
        ]);

        var routes = ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToHashSet(StringComparer.Ordinal);
        var root = $"{ProductApiProtocol.RestBasePath}/servers";

        Assert.Contains(root, routes);
        Assert.Contains($"{root}/{{serverId:guid}}", routes);
        Assert.Contains($"{root}/{{serverId:guid}}/console", routes);
        Assert.Contains($"{root}/{{serverId:guid}}/start", routes);
        Assert.Contains($"{root}/{{serverId:guid}}/stop", routes);
        Assert.Contains($"{root}/{{serverId:guid}}/restart", routes);
        Assert.Contains($"{root}/{{serverId:guid}}/command", routes);
        Assert.DoesNotContain($"{root}/{{serverId:guid}}/directory", routes);
        Assert.DoesNotContain($"{root}/{{serverId:guid}}/delete", routes);
        var notifications = $"{ProductApiProtocol.RestBasePath}/notifications";
        Assert.Contains($"{notifications}/settings/discord", routes);
        Assert.Contains($"{notifications}/history", routes);
        Assert.Contains($"{notifications}/preferences", routes);
        var accounts = $"{ProductApiProtocol.RestBasePath}/remote-accounts";
        Assert.Contains(accounts, routes);
        Assert.Contains($"{accounts}/{{username}}/authorization", routes);
        Assert.Contains($"{accounts}/{{username}}/pin", routes);
        Assert.Contains($"{accounts}/{{username}}/pin/reveal", routes);
        Assert.Contains($"{ProductApiProtocol.RestBasePath}/remote-devices", routes);
        var remoteAccess = $"{ProductApiProtocol.RestBasePath}/remote-access";
        Assert.Contains($"{remoteAccess}/status", routes);
        Assert.Contains($"{remoteAccess}/start", routes);
        Assert.Contains($"{remoteAccess}/stop", routes);
        Assert.Contains($"{remoteAccess}/reconnect", routes);
        var providers = $"{ProductApiProtocol.RestBasePath}/providers";
        Assert.Contains(providers, routes);
        Assert.Contains($"{providers}/install", routes);
        Assert.Contains($"{providers}/{{providerId}}/enabled", routes);
        Assert.Contains($"{providers}/{{providerId}}/health", routes);
        Assert.Contains($"{providers}/{{providerId}}", routes);
        Assert.Contains($"{providers}/publishers", routes);
        Assert.Contains($"{providers}/publishers/{{publisherId}}", routes);
        Assert.Contains($"{ProductApiProtocol.RestBasePath}/system/activation-ready", routes);
    }

    [Fact]
    public async Task ActivationReady_IsTokenProtectedAndBindsExactInstallationIdentity()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var port = GetAvailableLoopbackPort();
        await using var application = ProductServiceApplication.Build(
        [
            $"--{ProductServiceOptions.SectionName}:DataRoot={layout.Root}",
            $"--{ProductServiceOptions.SectionName}:Port={port}",
            UniqueIpcPipeArgument(),
        ]);
        await application.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var route = $"{ProductApiProtocol.RestBasePath}/system/activation-ready";
            using var missing = await client.GetAsync(route);
            using var rejectedRequest = new HttpRequestMessage(HttpMethod.Get, route);
            rejectedRequest.Headers.Add(ProductLocalApiAuthentication.HeaderName, new string('0', 64));
            using var rejected = await client.SendAsync(rejectedRequest);
            using var acceptedRequest = new HttpRequestMessage(HttpMethod.Get, route);
            acceptedRequest.Headers.Add(
                ProductLocalApiAuthentication.HeaderName,
                File.ReadAllText(Path.Combine(layout.Secrets, ProductLocalApiAuthenticator.FileName)).Trim());
            using var accepted = await client.SendAsync(acceptedRequest);
            var ready = await accepted.Content.ReadFromJsonAsync<ProductActivationReadyResponse>();
            var storedIdentity = Guid.Parse(File.ReadAllText(Path.Combine(
                layout.Data,
                ProductInstallationIdentityStore.FileName)).Trim());

            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.NotNull(ready);
            Assert.True(ready.Ready);
            Assert.Equal("ready", ready.Status);
            Assert.Equal(ProductServiceApplication.ProductName, ready.Product);
            Assert.Equal(ProductServiceApplication.ProductVersion, ready.Version);
            Assert.Equal(storedIdentity, ready.InstallationId);
            Assert.NotEqual(default, ready.StartedAtUtc);
        }
        finally
        {
            await application.StopAsync();
        }
    }

    [Fact]
    public async Task LoopbackAccountApi_ManagesMultipleAccountsWithoutReturningPinsInLists()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var port = GetAvailableLoopbackPort();
        await using var application = ProductServiceApplication.Build(
        [
            $"--{ProductServiceOptions.SectionName}:DataRoot={layout.Root}",
            $"--{ProductServiceOptions.SectionName}:Port={port}",
            UniqueIpcPipeArgument(),
        ]);
        await application.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var accountsRoute = $"{ProductApiProtocol.RestBasePath}/remote-accounts";
            using var unauthenticated = await client.GetAsync(accountsRoute);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

            client.DefaultRequestHeaders.Add(
                ProductLocalApiAuthentication.HeaderName,
                File.ReadAllText(Path.Combine(
                    layout.Secrets,
                    ProductLocalApiAuthenticator.FileName)).Trim());
            var serverId = Guid.NewGuid();
            var grant = new ProductPermissionGrant(
                ProductPermissionCodes.ServerRead,
                ProductPermissionScope.ForServer(serverId));
            using var first = await client.PostAsJsonAsync(
                accountsRoute,
                new ProductCreateRemoteAccountRequest(
                    "manager1",
                    "mcsv-local-approved-account",
                    null,
                    "482913",
                    [grant]));
            using var second = await client.PostAsJsonAsync(
                accountsRoute,
                new ProductCreateRemoteAccountRequest(
                    "partner2",
                    "mcsv-local-approved-account",
                    "partner@gmail.com",
                    "741852",
                    [grant]));

            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
            Assert.DoesNotContain("482913", await first.Content.ReadAsStringAsync());
            Assert.DoesNotContain("741852", await second.Content.ReadAsStringAsync());

            var list = await client.GetFromJsonAsync<ProductRemoteAccountSummary[]>(accountsRoute);
            Assert.Equal(2, list?.Length);
            Assert.Equal(
                ProductRemoteAccountRole.Owner,
                list?.Single(account => account.Username == "manager1").Role);
            Assert.Equal(
                ProductRemoteAccountRole.Viewer,
                list?.Single(account => account.Username == "partner2").Role);
            Assert.DoesNotContain("482913", await client.GetStringAsync(accountsRoute));
            Assert.DoesNotContain("741852", await client.GetStringAsync(accountsRoute));

            using var reveal = await client.PostAsync(
                $"{accountsRoute}/manager1/pin/reveal",
                content: null);
            var revealed = await reveal.Content.ReadFromJsonAsync<ProductRevealRemoteAccountPinResponse>();
            Assert.Equal("482913", revealed?.Pin);

            using var changed = await client.PutAsJsonAsync(
                $"{accountsRoute}/partner2/authorization",
                new ProductUpdateRemoteAccountAuthorizationRequest(
                    Enabled: false,
                    Grants: []));
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            var changedAccount = await changed.Content.ReadFromJsonAsync<ProductRemoteAccountSummary>();
            Assert.NotNull(changedAccount);
            Assert.False(changedAccount.Enabled);

            using var invalid = await client.PostAsJsonAsync(
                accountsRoute,
                new ProductCreateRemoteAccountRequest(
                    "invalid3",
                    "mcsv-local-approved-account",
                    null,
                    "12",
                    []));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.DoesNotContain("12", await invalid.Content.ReadAsStringAsync());

            using var removed = await client.DeleteAsync($"{accountsRoute}/partner2");
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            Assert.Single(await client.GetFromJsonAsync<ProductRemoteAccountSummary[]>(accountsRoute) ?? []);
            using var lastOwnerRemoval = await client.DeleteAsync($"{accountsRoute}/manager1");
            Assert.Equal(HttpStatusCode.Conflict, lastOwnerRemoval.StatusCode);
            Assert.Single(await client.GetFromJsonAsync<ProductRemoteAccountSummary[]>(accountsRoute) ?? []);
            Assert.Equal(
                "[]",
                await client.GetStringAsync($"{ProductApiProtocol.RestBasePath}/remote-devices"));
        }
        finally
        {
            await application.StopAsync();
        }
    }

    [Fact]
    public async Task LoopbackRuntimeApi_RejectsMissingAndInvalidServiceCapability()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var port = GetAvailableLoopbackPort();
        await using var application = ProductServiceApplication.Build(
        [
            $"--{ProductServiceOptions.SectionName}:DataRoot={layout.Root}",
            $"--{ProductServiceOptions.SectionName}:Port={port}",
            UniqueIpcPipeArgument(),
        ]);
        await application.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var route = $"{ProductApiProtocol.RestBasePath}/servers";
            var discordRoute = $"{ProductApiProtocol.RestBasePath}/notifications/settings/discord";
            var remoteStatusRoute = $"{ProductApiProtocol.RestBasePath}/remote-access/status";

            using var missing = await client.GetAsync(route);
            using var missingNotifications = await client.GetAsync(discordRoute);
            using var missingRemoteStatus = await client.GetAsync(remoteStatusRoute);
            using var missingRemoteStart = await client.PostAsync(
                $"{ProductApiProtocol.RestBasePath}/remote-access/start",
                content: null);
            using var missingRemoteStop = await client.PostAsync(
                $"{ProductApiProtocol.RestBasePath}/remote-access/stop",
                content: null);
            using var missingRemoteReconnect = await client.PostAsync(
                $"{ProductApiProtocol.RestBasePath}/remote-access/reconnect",
                content: null);
            using var rejectedRequest = new HttpRequestMessage(HttpMethod.Get, route);
            rejectedRequest.Headers.Add(
                ProductLocalApiAuthentication.HeaderName,
                new string('0', 64));
            using var rejected = await client.SendAsync(rejectedRequest);
            using var acceptedRequest = new HttpRequestMessage(HttpMethod.Get, route);
            acceptedRequest.Headers.Add(
                ProductLocalApiAuthentication.HeaderName,
                File.ReadAllText(Path.Combine(
                    layout.Secrets,
                    ProductLocalApiAuthenticator.FileName)).Trim());
            using var accepted = await client.SendAsync(acceptedRequest);

            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, missingNotifications.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, missingRemoteStatus.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, missingRemoteStart.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, missingRemoteStop.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, missingRemoteReconnect.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Equal("[]", await accepted.Content.ReadAsStringAsync());

            var token = File.ReadAllText(Path.Combine(
                layout.Secrets,
                ProductLocalApiAuthenticator.FileName)).Trim();
            client.DefaultRequestHeaders.Add(ProductLocalApiAuthentication.HeaderName, token);
            using var remoteStatus = await client.GetAsync(remoteStatusRoute);
            Assert.Equal(HttpStatusCode.OK, remoteStatus.StatusCode);
            Assert.DoesNotContain("tailscale.exe", await remoteStatus.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            var registration = ProductServerRegistryTests.Registration();
            using var registered = await client.PutAsJsonAsync(
                $"{route}/{registration.Id:D}",
                registration);
            using var listed = await client.GetAsync(route);

            Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            Assert.Contains(registration.Id.ToString("D"), await listed.Content.ReadAsStringAsync());
            Assert.True(File.Exists(Path.Combine(layout.Data, ProductServerRegistry.FileName)));
            Assert.True(File.Exists(Path.Combine(layout.Data, "product.v1.db")));

            using var initiallyDisabled = await client.GetAsync(discordRoute);
            using var invalid = await client.PutAsJsonAsync(
                discordRoute,
                new ProductDiscordWebhookUpdateRequest(
                    "https://attacker.invalid/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz_ABCDE12345"));
            using var configured = await client.PutAsJsonAsync(
                discordRoute,
                new ProductDiscordWebhookUpdateRequest(ProductNotificationSecretsTests.ValidWebhook));
            using var current = await client.GetAsync(discordRoute);
            var configuredBody = await configured.Content.ReadAsStringAsync();
            var currentBody = await current.Content.ReadAsStringAsync();

            Assert.Equal(
                "{\"configured\":false,\"enabled\":false}",
                await initiallyDisabled.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.DoesNotContain("attacker.invalid", await invalid.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
            Assert.Equal("{\"configured\":true,\"enabled\":true}", configuredBody);
            Assert.Equal(configuredBody, currentBody);
            Assert.DoesNotContain("discord.com", configuredBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ABCDE12345", configuredBody, StringComparison.Ordinal);
            var secretFile = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(layout.Secrets, "vault"),
                "*.secret"));
            Assert.DoesNotContain(
                "ABCDE12345",
                Encoding.UTF8.GetString(await File.ReadAllBytesAsync(secretFile)),
                StringComparison.Ordinal);

            using var history = await client.GetAsync(
                $"{ProductApiProtocol.RestBasePath}/notifications/history?limit=10");
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            Assert.Equal("[]", await history.Content.ReadAsStringAsync());

            var preferencesRoute = $"{ProductApiProtocol.RestBasePath}/notifications/preferences";
            var requestedPreferences = ProductNotificationPreferences.Default with
            {
                BackupOperations = false,
                ExternalThrottleSeconds = 90,
            };
            using var savedPreferences = await client.PutAsJsonAsync(
                preferencesRoute,
                requestedPreferences);
            Assert.Equal(HttpStatusCode.OK, savedPreferences.StatusCode);
            Assert.Equal(
                requestedPreferences,
                await client.GetFromJsonAsync<ProductNotificationPreferences>(preferencesRoute));

            using var deleted = await client.DeleteAsync(discordRoute);
            Assert.Equal(
                "{\"configured\":false,\"enabled\":false}",
                await deleted.Content.ReadAsStringAsync());
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(layout.Secrets, "vault"), "*.secret"));
        }
        finally
        {
            await application.StopAsync();
        }
    }

    private static int GetAvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string UniqueIpcPipeArgument()
        => $"--{ProductServiceOptions.SectionName}:IpcPipeName=muhun.mcsv.test.{Guid.NewGuid():N}";
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProductServiceHostCollection
{
    public const string Name = "Product service host";
}
