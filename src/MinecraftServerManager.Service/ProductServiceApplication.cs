using System.Net;
using System.Reflection;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Data;
using MinecraftServerManager.Notifications;
using MinecraftServerManager.ProviderHost;
using MinecraftServerManager.Remote;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace MinecraftServerManager.Service;

public static class ProductServiceApplication
{
    public const string ProductName = "Muhun MCSV Manager";
    public static string ProductVersion { get; } =
        typeof(ProductServiceApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+', 2)[0]
        ?? typeof(ProductServiceApplication).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Host.UseWindowsService(options =>
        {
            options.ServiceName = ProductServiceOptions.WindowsServiceName;
        });

        var serviceOptions = ReadOptions(builder.Configuration);
        ProductServiceOptionsValidator.ValidateAndThrow(serviceOptions);
        if (!WindowsServiceHelpers.IsWindowsService() && string.IsNullOrWhiteSpace(serviceOptions.DataRoot))
        {
            throw new InvalidOperationException(
                "Console execution of this foundation build requires an explicit Mcsv:Service:DataRoot. " +
                "The Windows Service installer will provision the production data root and ACL.");
        }

        var layout = ProductDataLayout.FromOptions(serviceOptions);

        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = 64 * 1024;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            server.Listen(IPAddress.Loopback, serviceOptions.Port, listen =>
            {
                // The public HTTPS tunnel terminates TLS and proxies to this loopback-only
                // endpoint. HTTP/1.1 avoids advertising h2 without local TLS/ALPN.
                listen.Protocols = HttpProtocols.Http1;
            });
        });

        builder.Services.AddSingleton(serviceOptions);
        builder.Services.AddSingleton(serviceOptions.Updates);
        builder.Services.AddSingleton(layout);
        builder.Services.AddSingleton<ProductInstallationIdentityStore>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ProductServiceState>();
        builder.Services.AddSingleton(provider =>
        {
            var productLayout = provider.GetRequiredService<ProductDataLayout>();
            return new ProductDatabase(Path.Combine(productLayout.Data, "product.v1.db"));
        });
        builder.Services.AddSingleton<ProductDatabaseInitializer>();
        builder.Services.AddSingleton<NotificationOutboxStore>();
        builder.Services.AddSingleton<ProductSequenceStore>();
        builder.Services.AddSingleton<ProductSecurityAuditStore>();
        builder.Services.AddSingleton<ProductLocalIpcAuditPolicy>();
        builder.Services.AddSingleton<ProductServerRegistry>();
        builder.Services.AddSingleton<ProductDesiredRunIntentStore>();
        builder.Services.AddSingleton<ProductLocalApiAuthenticator>();
        builder.Services.AddSingleton(provider => new ProviderHostLayout(
            provider.GetRequiredService<ProductDataLayout>().Plugins));
        builder.Services.AddSingleton<ProviderRegistry>();
        builder.Services.AddSingleton<ProductProviderPublisherTrustStore>();
        builder.Services.AddSingleton<IProviderPackageTrustVerifier>(provider =>
            provider.GetRequiredService<ProductProviderPublisherTrustStore>());
        builder.Services.AddSingleton<ProviderPackageInstaller>();
        builder.Services.AddSingleton<ProviderPackageUninstaller>();
        builder.Services.AddSingleton<IProviderProcessFactory, ProviderProcessFactory>();
        builder.Services.AddSingleton<IProviderHttpBroker, ProviderHttpBroker>();
        builder.Services.AddSingleton<ProviderInvocationHost>();
        builder.Services.AddSingleton(new ProductBuiltinProviderDeploymentOptions(
            Required: WindowsServiceHelpers.IsWindowsService()));
        builder.Services.AddSingleton<ProductBuiltinProviderBootstrapper>();
        builder.Services.AddSingleton<ProductProviderCoordinator>();
        builder.Services.AddSingleton<IProductSecretVault>(provider =>
        {
            var productLayout = provider.GetRequiredService<ProductDataLayout>();
            var installationId = provider.GetRequiredService<ProductInstallationIdentityStore>()
                .GetOrCreate();
            return new WindowsDpapiSecretVault(
                Path.Combine(productLayout.Secrets, "vault"),
                installationId);
        });
        builder.Services.AddSingleton<ProductNotificationSecretResolver>();
        builder.Services.AddSingleton<INotificationSecretResolver>(provider =>
            provider.GetRequiredService<ProductNotificationSecretResolver>());
        builder.Services.AddSingleton<ProductDiscordWebhookSettings>();
        builder.Services.AddSingleton<ProductNotificationPreferenceStore>();
        builder.Services.AddSingleton<ProductNotificationPublisher>();
        builder.Services.AddSingleton<ProductNotificationProviderDisableHandler>();
        builder.Services.AddSingleton<INotificationProviderDisableHandler>(provider =>
            provider.GetRequiredService<ProductNotificationProviderDisableHandler>());
        builder.Services.AddSingleton<ProductRemoteAccountStore>();
        builder.Services.AddSingleton<ProductRememberedDeviceStore>();
        builder.Services.AddSingleton<ProductRemoteCredentialStore>();
        builder.Services.AddSingleton<ProductRemoteSecurityAuditSink>();
        builder.Services.AddSingleton<ProductRemoteWebIntentStore>();
        builder.Services.AddSingleton<IProductTailscaleExecutableLocator, ProductTailscaleExecutableLocator>();
        builder.Services.AddSingleton<IProductTailscaleProcessRunner, ProductTailscaleProcessRunner>();
        builder.Services.AddSingleton<IProductTailscalePlatform, ProductTailscalePlatform>();
        builder.Services.AddSingleton<IProductRemoteWebHostFactory, ProductRemoteWebHostFactory>();
        builder.Services.AddSingleton<INotificationMessageRenderer, ProductNotificationMessageRenderer>();
        builder.Services.AddSingleton<INotificationDeliveryProvider, ProductLocalHistoryNotificationProvider>();
        builder.Services.AddSingleton<INotificationDeliveryProvider>(provider =>
            new DiscordWebhookProvider(
                ProductDurableServerNotificationSink.DiscordProviderId,
                ProductNotificationSecretResolver.DiscordWebhookSecretReference,
                provider.GetRequiredService<INotificationSecretResolver>(),
                provider.GetRequiredService<INotificationMessageRenderer>()));
        builder.Services.AddSingleton<ProductServerRestartBlocker>();
        builder.Services.AddSingleton<ServerPropertiesPortService>();
        builder.Services.AddSingleton<ProductServerPortCoordinator>();
        builder.Services.AddSingleton<ProductServerDirectoryLeaseProvider>();
        builder.Services.AddSingleton(provider =>
        {
            var registry = provider.GetRequiredService<ProductServerRegistry>();
            var restartBlocker = provider.GetRequiredService<ProductServerRestartBlocker>();
            var portCoordinator = provider.GetRequiredService<ProductServerPortCoordinator>();
            var directoryLeaseProvider = provider.GetRequiredService<ProductServerDirectoryLeaseProvider>();
            var manager = new ServerProcessManager(new ServerProcessManagerOptions
            {
                MaximumRetainedConsoleLines = ProductServerRuntime.CoreRetainedConsoleLinesPerServer,
                ShouldAutoRestartAsync = (serverId, _) => Task.FromResult(
                    registry.TryGet(serverId, out var server) &&
                    server.AutoRestart &&
                    !restartBlocker.IsBlocked(serverId)),
                AcquireDirectoryLease = directoryLeaseProvider.Acquire,
                RefreshAutoRestartSnapshotAsync = (snapshot, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!registry.TryGet(snapshot.Id, out var latest))
                    {
                        throw new InvalidOperationException(
                            "Automatic restart was cancelled because the server registration no longer exists.");
                    }

                    ProductServerRuntime.ApplyRegistrationLaunchSnapshot(snapshot, latest, layout);
                    return Task.CompletedTask;
                },
                PrepareStartAsync = portCoordinator.PrepareStartAsync,
                PreparedStartAborted = portCoordinator.PreparedStartAborted,
            });
            manager.StateChanged += portCoordinator.ObserveStateChanged;
            return manager;
        });
        builder.Services.AddSingleton<ProductServerRuntime>();
        builder.Services.AddSingleton<ProductServerImportService>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<IMinecraftStatusProbe, MinecraftStatusProbe>();
        builder.Services.AddSingleton<ProductServerModpackUpdateCoordinator>();
        builder.Services.AddSingleton<ProductServerBackupManager>();
        builder.Services.AddSingleton<ProductPlayerPresenceTracker>();
        builder.Services.AddSingleton<ProductServerAdministrationReader>();
        builder.Services.AddSingleton<ProductRemoteControlBackend>();
        builder.Services.AddSingleton<IProductServerNotificationSink>(provider =>
            new ProductDurableServerNotificationSink(
                provider.GetRequiredService<ProductServerRegistry>(),
                provider.GetRequiredService<ProductNotificationPublisher>()));
        builder.Services.AddSingleton(provider =>
        {
            var installationId = provider.GetRequiredService<ProductInstallationIdentityStore>()
                .GetOrCreate();
            return new NotificationDispatcher(
                provider.GetRequiredService<NotificationOutboxStore>(),
                provider.GetServices<INotificationDeliveryProvider>(),
                $"service:{installationId:N}:{Environment.ProcessId}",
                provider.GetRequiredService<INotificationProviderDisableHandler>());
        });
        builder.Services.AddSingleton<ProductIpcMessageProcessor>();
        builder.Services.AddSingleton<ProductRemoteWebSupervisor>();
        builder.Services.AddSingleton<IProductRemoteWebSupervisor>(provider =>
            provider.GetRequiredService<ProductRemoteWebSupervisor>());
        builder.Services.AddSingleton<IProductUpdateActivationLauncher, ProductUpdateActivationLauncher>();
        builder.Services.AddSingleton<ProductUpdateCoordinator>(provider =>
        {
            var updateOptions = provider.GetRequiredService<ProductUpdateOptions>();
            IProductUpdateTransport? transport = updateOptions.AllowedFeedHosts.Count > 0 &&
                                                (!string.IsNullOrWhiteSpace(updateOptions.StableManifestUrl) ||
                                                 !string.IsNullOrWhiteSpace(updateOptions.BetaManifestUrl))
                ? new ProductUpdateHttpTransport(updateOptions.AllowedFeedHosts)
                : null;
            return new ProductUpdateCoordinator(
                updateOptions,
                provider.GetRequiredService<ProductServiceOptions>(),
                provider.GetRequiredService<ProductDataLayout>(),
                provider.GetRequiredService<ProductInstallationIdentityStore>(),
                provider.GetRequiredService<IProductUpdateActivationLauncher>(),
                provider.GetRequiredService<TimeProvider>(),
                transport);
        });
        builder.Services.AddSingleton<IProductUpdateCoordinator>(provider =>
            provider.GetRequiredService<ProductUpdateCoordinator>());
        // Registration order is intentional. During shutdown hosted services stop in reverse:
        // IPC closes, Worker stops Java while the bridge is still subscribed, the bridge drains
        // into SQLite, then the dispatcher performs its final bounded delivery pass.
        builder.Services.AddHostedService<ProductNotificationDispatchHostedService>();
        builder.Services.AddHostedService<ProductServerNotificationBridge>();
        builder.Services.AddHostedService<ProductDomainNotificationBridge>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<ProductPlayerPresenceTracker>());
        builder.Services.AddHostedService<ProductServiceWorker>();
        builder.Services.AddHostedService<ProductDesiredServerRecoveryHostedService>();
        builder.Services.AddHostedService<ProductUpdateSchedulerHostedService>();
        builder.Services.AddHostedService<ProductUpdateArtifactRetentionHostedService>();
        builder.Services.AddHostedService<ProductRetentionHostedService>();
        builder.Services.AddHostedService<ProductIpcHostedService>();
        // Starts after the registry/database Worker is ready and stops first, so public ingress
        // is removed before IPC, Java runtimes, or the Service-owned Web host are torn down.
        builder.Services.AddHostedService(provider => provider.GetRequiredService<ProductRemoteWebSupervisor>());

        var application = builder.Build();
        ConfigurePipeline(application);
        return application;
    }

    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var application = Build(args);
        await application.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static ProductServiceOptions ReadOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(ProductServiceOptions.SectionName);
        var updates = section.GetSection(nameof(ProductServiceOptions.Updates));
        return new ProductServiceOptions
        {
            Port = section.GetValue<int?>(nameof(ProductServiceOptions.Port))
                   ?? ProductServiceOptions.DefaultPort,
            DataRoot = section[nameof(ProductServiceOptions.DataRoot)],
            IpcPipeName = section[nameof(ProductServiceOptions.IpcPipeName)]
                          ?? ProductApiProtocol.IpcPackage,
            EnableRemoteWebInConsole = section.GetValue<bool?>(
                                           nameof(ProductServiceOptions.EnableRemoteWebInConsole))
                                       ?? false,
            Updates = new ProductUpdateOptions
            {
                StableManifestUrl = updates[nameof(ProductUpdateOptions.StableManifestUrl)],
                BetaManifestUrl = updates[nameof(ProductUpdateOptions.BetaManifestUrl)],
                AllowedFeedHosts = updates
                    .GetSection(nameof(ProductUpdateOptions.AllowedFeedHosts))
                    .GetChildren()
                    .Select(child => child.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray(),
                PublicKeyDocumentPath = updates[nameof(ProductUpdateOptions.PublicKeyDocumentPath)],
            },
        };
    }

    private static void ConfigurePipeline(WebApplication application)
    {
        application.Use(async (context, next) =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
            await next(context).ConfigureAwait(false);
        });

        application.UseWhen(
            context => context.Request.Path.StartsWithSegments(
                           $"{ProductApiProtocol.RestBasePath}/servers") ||
                       context.Request.Path.StartsWithSegments(
                           $"{ProductApiProtocol.RestBasePath}/notifications") ||
                       context.Request.Path.StartsWithSegments(
                           $"{ProductApiProtocol.RestBasePath}/remote-accounts") ||
                       context.Request.Path.StartsWithSegments(
                           $"{ProductApiProtocol.RestBasePath}/remote-devices") ||
                       context.Request.Path.StartsWithSegments(
                           $"{ProductApiProtocol.RestBasePath}/remote-access") ||
                       context.Request.Path.StartsWithSegments(
                           $"{ProductApiProtocol.RestBasePath}/providers") ||
                       context.Request.Path.StartsWithSegments(
                           $"{ProductApiProtocol.RestBasePath}/updates") ||
                       context.Request.Path.Equals(
                           $"{ProductApiProtocol.RestBasePath}/system/activation-ready",
                           StringComparison.Ordinal),
            branch => branch.Use(async (context, next) =>
            {
                var authenticator = context.RequestServices
                    .GetRequiredService<ProductLocalApiAuthenticator>();
                var values = context.Request.Headers[ProductLocalApiAuthentication.HeaderName];
                var supplied = values.Count == 1 ? values[0] : null;
                var result = authenticator.Authenticate(supplied);
                if (result == ProductLocalApiAuthenticationResult.Authenticated)
                {
                    await next(context).ConfigureAwait(false);
                    return;
                }

                context.Response.StatusCode = result == ProductLocalApiAuthenticationResult.Missing
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status403Forbidden;
                if (result == ProductLocalApiAuthenticationResult.Missing)
                {
                    context.Response.Headers.WWWAuthenticate = "MCSV-Service-Token";
                }

                await context.Response.WriteAsJsonAsync(new
                {
                    type = "about:blank",
                    title = result == ProductLocalApiAuthenticationResult.Missing
                        ? "Authentication required"
                        : "Authentication rejected",
                    status = context.Response.StatusCode,
                    code = result == ProductLocalApiAuthenticationResult.Missing
                        ? "service.authentication_required"
                        : "service.authentication_rejected",
                }).ConfigureAwait(false);
            }));

        application.MapGet(
            $"{ProductApiProtocol.RestBasePath}/system/health/live",
            () => Results.Json(new
            {
                status = "live",
                product = ProductName,
                version = ProductVersion,
            }));

        application.MapGet(
            $"{ProductApiProtocol.RestBasePath}/system/handshake",
            (ProductServiceState state) => Results.Json(
                new ProductHandshakeResponse(
                    ProductName,
                    ProductVersion,
                    ProductApiProtocol.CurrentVersion,
                    ProductApiProtocol.MinimumSupportedVersion,
                    state.IsReady)));

        application.MapGet(
            $"{ProductApiProtocol.RestBasePath}/system/activation-ready",
            (ProductServiceState state) => Results.Json(
                new ProductActivationReadyResponse(
                    state.IsReady ? "ready" : "starting",
                    ProductName,
                    ProductVersion,
                    state.InstallationId,
                    state.StartedAtUtc,
                    state.IsReady)));

        var serversPath = $"{ProductApiProtocol.RestBasePath}/servers";
        application.MapGet(
            serversPath,
            (ProductServerRuntime runtime) => Results.Json(runtime.List()));
        application.MapGet(
            $"{serversPath}/{{serverId:guid}}",
            (Guid serverId, ProductServerRuntime runtime) => Execute(
                () => Results.Json(runtime.GetStatus(serverId))));
        application.MapGet(
            $"{serversPath}/{{serverId:guid}}/console",
            (Guid serverId, long? after, int? limit, ProductServerRuntime runtime) => Execute(
                () => Results.Json(runtime.ReadConsole(serverId, after ?? 0, limit ?? 50))));
        application.MapPut(
            $"{serversPath}/{{serverId:guid}}",
            async (Guid serverId,
                   ProductServerRegistration registration,
                   ProductServerRuntime runtime,
                   CancellationToken cancellationToken) => await ExecuteAsync(async () =>
            {
                if (registration.Id != serverId)
                {
                    return Results.BadRequest(new
                    {
                        code = "server.id_mismatch",
                        message = "Route and registration server ids must match.",
                    });
                }

                await runtime.UpsertAsync(registration, cancellationToken).ConfigureAwait(false);
                return Results.Json(runtime.GetStatus(serverId));
            }).ConfigureAwait(false));
        application.MapDelete(
            $"{serversPath}/{{serverId:guid}}",
            async (Guid serverId,
                   ProductServerRuntime runtime,
                   CancellationToken cancellationToken) => await ExecuteAsync(async () =>
            {
                var removed = await runtime.RemoveAsync(serverId, cancellationToken).ConfigureAwait(false);
                return removed
                    ? Results.NoContent()
                    : Results.NotFound(new { code = "server.not_found" });
            }).ConfigureAwait(false));
        application.MapPost(
            $"{serversPath}/{{serverId:guid}}/start",
            (Guid serverId, ProductServerRuntime runtime, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Json(
                    await runtime.StartAsync(serverId, cancellationToken).ConfigureAwait(false))));
        application.MapPost(
            $"{serversPath}/{{serverId:guid}}/stop",
            (Guid serverId, ProductServerRuntime runtime, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Json(
                    await runtime.StopAsync(serverId, cancellationToken).ConfigureAwait(false))));
        application.MapPost(
            $"{serversPath}/{{serverId:guid}}/restart",
            (Guid serverId, ProductServerRuntime runtime, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Json(
                    await runtime.RestartAsync(serverId, cancellationToken).ConfigureAwait(false))));
        application.MapPost(
            $"{serversPath}/{{serverId:guid}}/command",
            (Guid serverId,
             ProductServerCommandRequest request,
             ProductServerRuntime runtime,
             CancellationToken cancellationToken) => ExecuteAsync(async () =>
            {
                await runtime.SendCommandAsync(serverId, request.Command, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Accepted(value: runtime.GetStatus(serverId));
            }));

        var notificationsPath = $"{ProductApiProtocol.RestBasePath}/notifications";
        var discordSettingsPath = $"{notificationsPath}/settings/discord";
        application.MapGet(
            discordSettingsPath,
            (ProductDiscordWebhookSettings settings, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Json(
                    await settings.GetAsync(cancellationToken).ConfigureAwait(false))));
        application.MapPut(
            discordSettingsPath,
            (ProductDiscordWebhookUpdateRequest request,
             ProductDiscordWebhookSettings settings,
             CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Json(
                await settings.SetAsync(request.WebhookUrl, cancellationToken).ConfigureAwait(false))));
        application.MapDelete(
            discordSettingsPath,
            (ProductDiscordWebhookSettings settings, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Json(
                    await settings.DeleteAsync(cancellationToken).ConfigureAwait(false))));
        application.MapGet(
            $"{notificationsPath}/history",
            (int? limit,
             NotificationOutboxStore outbox,
             CancellationToken cancellationToken) => ExecuteAsync(async () =>
            {
                var maximumCount = limit ?? 100;
                if (maximumCount is < 1 or > 100)
                {
                    return Results.BadRequest(new
                    {
                        code = "notification.history_limit_invalid",
                        message = "Notification history limit must be between 1 and 100.",
                    });
                }

                return Results.Json(
                    await outbox.ReadRecentAsync(maximumCount, cancellationToken).ConfigureAwait(false));
            }));
        var notificationPreferencesPath = $"{notificationsPath}/preferences";
        application.MapGet(
            notificationPreferencesPath,
            (ProductNotificationPreferenceStore preferences,
             CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Json(
                await preferences.GetAsync(cancellationToken).ConfigureAwait(false))));
        application.MapPut(
            notificationPreferencesPath,
            (ProductNotificationPreferences request,
             ProductNotificationPreferenceStore preferences,
             CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Json(
                await preferences.SetAsync(request, cancellationToken).ConfigureAwait(false))));

        var remoteAccountsPath = $"{ProductApiProtocol.RestBasePath}/remote-accounts";
        application.MapGet(
            remoteAccountsPath,
            (ProductRemoteAccountStore accounts) => Results.Json(
                accounts.List().Select(ToRemoteAccountSummary).ToArray()));
        application.MapPost(
            remoteAccountsPath,
            (ProductCreateRemoteAccountRequest request,
             ProductRemoteAccountStore accounts,
             CancellationToken cancellationToken) => ExecuteAsync(async () =>
            {
                var created = await accounts.CreateAsync(
                        request.Username,
                        request.CredentialSubject,
                        request.Email,
                        request.Pin,
                        request.Grants,
                        cancellationToken,
                        request.Role)
                    .ConfigureAwait(false);
                return Results.Created(
                    $"{remoteAccountsPath}/{Uri.EscapeDataString(created.Username)}",
                    ToRemoteAccountSummary(created));
            }));
        application.MapPut(
            $"{remoteAccountsPath}/{{username}}/authorization",
            (string username,
             ProductUpdateRemoteAccountAuthorizationRequest request,
             ProductRemoteAccountStore accounts,
             CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Json(
                ToRemoteAccountSummary(await accounts.UpdateAuthorizationAsync(
                        username,
                        request.Enabled,
                        request.Grants,
                        cancellationToken,
                        request.Role)
                    .ConfigureAwait(false)))));
        application.MapPut(
            $"{remoteAccountsPath}/{{username}}/pin",
            (string username,
             ProductUpdateRemoteAccountPinRequest request,
             ProductRemoteAccountStore accounts,
             CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Json(
                ToRemoteAccountSummary(await accounts.UpdatePinAsync(
                        username,
                        request.Pin,
                        cancellationToken)
                    .ConfigureAwait(false)))));
        application.MapPost(
            $"{remoteAccountsPath}/{{username}}/pin/reveal",
            (string username,
             ProductRemoteAccountStore accounts,
             CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Json(
                new ProductRevealRemoteAccountPinResponse(
                    await accounts.RevealPinAsync(username, cancellationToken)
                        .ConfigureAwait(false)))));
        application.MapDelete(
            $"{remoteAccountsPath}/{{username}}",
            (string username,
             ProductRemoteAccountStore accounts,
             CancellationToken cancellationToken) => ExecuteAsync(async () =>
            {
                await accounts.DeleteAsync(username, cancellationToken).ConfigureAwait(false);
                return Results.NoContent();
            }));

        var remoteDevicesPath = $"{ProductApiProtocol.RestBasePath}/remote-devices";
        application.MapGet(
            remoteDevicesPath,
            (ProductRememberedDeviceStore devices) => Results.Json(
                devices.List().Select(ToRememberedDeviceSummary).ToArray()));
        application.MapDelete(
            $"{remoteDevicesPath}/{{deviceId:guid}}",
            (Guid deviceId, ProductRememberedDeviceStore devices) =>
                devices.Revoke(deviceId)
                    ? Results.NoContent()
                    : Results.NotFound(new { code = "remote.device_not_found" }));

        var remoteAccessPath = $"{ProductApiProtocol.RestBasePath}/remote-access";
        application.MapGet(
            $"{remoteAccessPath}/status",
            (ProductRemoteWebSupervisor supervisor) => Results.Json(supervisor.Snapshot));
        application.MapPost(
            $"{remoteAccessPath}/start",
            (ProductRemoteWebSupervisor supervisor, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Json(
                    await supervisor.EnableAsync(cancellationToken).ConfigureAwait(false))));
        application.MapPost(
            $"{remoteAccessPath}/stop",
            (ProductRemoteWebSupervisor supervisor, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Json(
                    await supervisor.DisableAsync(cancellationToken).ConfigureAwait(false))));
        application.MapPost(
            $"{remoteAccessPath}/reconnect",
            (ProductRemoteWebSupervisor supervisor, CancellationToken cancellationToken) =>
                ExecuteAsync(async () => Results.Json(
                    await supervisor.ReconnectAsync(cancellationToken).ConfigureAwait(false))));

        var providersPath = $"{ProductApiProtocol.RestBasePath}/providers";
        application.MapGet(
            providersPath,
            (ProductProviderCoordinator providers) => ExecuteProvider(
                () => Results.Json(providers.List())));
        application.MapPost(
            $"{providersPath}/install",
            (ProductProviderInstallFromInboxRequest request,
             ProductProviderCoordinator providers,
             CancellationToken cancellationToken) => ExecuteProviderAsync(async () =>
            {
                var installed = await providers.InstallFromInboxAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Created(
                    $"{providersPath}/{Uri.EscapeDataString(installed.Id)}",
                    installed);
            }));
        application.MapPut(
            $"{providersPath}/{{providerId}}/enabled",
            (string providerId,
             ProductProviderEnableRequest request,
             ProductProviderCoordinator providers,
             CancellationToken cancellationToken) => ExecuteProviderAsync(async () => Results.Json(
                await providers.SetEnabledAsync(providerId, request.Enabled, cancellationToken)
                    .ConfigureAwait(false))));
        application.MapPost(
            $"{providersPath}/{{providerId}}/health",
            (string providerId,
             ProductProviderCoordinator providers,
             CancellationToken cancellationToken) => ExecuteProviderAsync(async () => Results.Json(
                await providers.CheckHealthAsync(providerId, cancellationToken).ConfigureAwait(false))));
        application.MapDelete(
            $"{providersPath}/{{providerId}}",
            (string providerId,
             ProductProviderCoordinator providers,
             CancellationToken cancellationToken) => ExecuteProviderAsync(async () =>
                await providers.UninstallAsync(providerId, cancellationToken).ConfigureAwait(false)
                    ? Results.NoContent()
                    : Results.NotFound(new { code = "provider.not_found" })));

        var publisherTrustPath = $"{providersPath}/publishers";
        application.MapGet(
            publisherTrustPath,
            (ProductProviderCoordinator providers) => ExecuteProvider(
                () => Results.Json(providers.ListTrustedPublishers())));
        application.MapPut(
            $"{publisherTrustPath}/{{publisherId}}",
            (string publisherId,
             ProductPinProviderPublisherRequest request,
             ProductProviderCoordinator providers,
             CancellationToken cancellationToken) => ExecuteProviderAsync(async () =>
            {
                if (!string.Equals(publisherId, request.PublisherId, StringComparison.Ordinal))
                {
                    return Results.BadRequest(new { code = "provider.publisher_id_mismatch" });
                }

                return Results.Json(
                    await providers.PinPublisherAsync(request, cancellationToken).ConfigureAwait(false));
            }));
        application.MapDelete(
            $"{publisherTrustPath}/{{publisherId}}",
            (string publisherId,
             ProductProviderCoordinator providers,
             CancellationToken cancellationToken) => ExecuteProviderAsync(async () =>
                await providers.RemovePublisherAsync(publisherId, cancellationToken).ConfigureAwait(false)
                    ? Results.NoContent()
                    : Results.NotFound(new { code = "provider.publisher_not_found" })));

        var updatesPath = $"{ProductApiProtocol.RestBasePath}/updates";
        application.MapGet(
            $"{updatesPath}/{{channel}}",
            (string channel, IProductUpdateCoordinator updates) =>
                TryParseUpdateChannel(channel, out var parsed)
                    ? Results.Json(updates.GetStatus(parsed))
                    : Results.BadRequest(new { code = "update.channel_invalid" }));
        application.MapPost(
            $"{updatesPath}/{{channel}}/check",
            (string channel,
             IProductUpdateCoordinator updates,
             CancellationToken cancellationToken) =>
                TryParseUpdateChannel(channel, out var parsed)
                    ? ExecuteAsync(async () => Results.Json(
                        await updates.CheckAsync(parsed, cancellationToken).ConfigureAwait(false)))
                    : Task.FromResult<IResult>(Results.BadRequest(new { code = "update.channel_invalid" })));
        application.MapPost(
            $"{updatesPath}/{{channel}}/download",
            (string channel,
             IProductUpdateCoordinator updates,
             CancellationToken cancellationToken) =>
                TryParseUpdateChannel(channel, out var parsed)
                    ? ExecuteAsync(async () => Results.Json(
                        await updates.DownloadAsync(parsed, cancellationToken).ConfigureAwait(false)))
                    : Task.FromResult<IResult>(Results.BadRequest(new { code = "update.channel_invalid" })));
        application.MapPost(
            $"{updatesPath}/{{channel}}/schedule",
            (string channel,
             ProductUpdateScheduleApiRequest request,
             IProductUpdateCoordinator updates,
             CancellationToken cancellationToken) =>
                TryParseUpdateChannel(channel, out var parsed)
                    ? ExecuteAsync(async () => Results.Json(
                        await updates.ScheduleAsync(
                                parsed,
                                request.NotBeforeUtc,
                                cancellationToken)
                            .ConfigureAwait(false)))
                    : Task.FromResult<IResult>(Results.BadRequest(new { code = "update.channel_invalid" })));

        application.MapFallback(() => Results.NotFound(new
        {
            type = "about:blank",
            title = "Not Found",
            status = StatusCodes.Status404NotFound,
            code = "route.not_found",
        }));
    }

    private static IResult Execute(Func<IResult> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception error) when (ProductOperationErrorPolicy.IsExpected(error))
        {
            return OperationError(error);
        }
    }

    private static IResult ExecuteProvider(Func<IResult> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception error) when (ProductProviderErrorPolicy.IsExpected(error))
        {
            return ProviderOperationError(error);
        }
    }

    private static async Task<IResult> ExecuteProviderAsync(Func<Task<IResult>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception error) when (ProductProviderErrorPolicy.IsExpected(error))
        {
            return ProviderOperationError(error);
        }
    }

    private static IResult ProviderOperationError(Exception error)
    {
        var publicError = ProductProviderErrorPolicy.ToPublic(error);
        return Results.Json(
            new
            {
                type = "about:blank",
                title = "Provider operation failed",
                status = publicError.StatusCode,
                code = publicError.Code,
                detail = publicError.Message,
            },
            statusCode: publicError.StatusCode);
    }

    private static bool TryParseUpdateChannel(string value, out ProductUpdateChannel channel)
        => Enum.TryParse(value, ignoreCase: true, out channel) && Enum.IsDefined(channel);

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception error) when (ProductOperationErrorPolicy.IsExpected(error))
        {
            return OperationError(error);
        }
    }

    private static IResult OperationError(Exception error)
    {
        var publicError = ProductOperationErrorPolicy.ToPublic(error);
        return Results.Json(
            new
            {
                type = "about:blank",
                title = "Operation failed",
                status = publicError.StatusCode,
                code = publicError.Code,
                detail = publicError.Message,
            },
            statusCode: publicError.StatusCode);
    }

    private static ProductRemoteAccountSummary ToRemoteAccountSummary(
        ProductRemoteAccountInfo account)
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
}

internal sealed record ProductUpdateScheduleApiRequest(DateTimeOffset? NotBeforeUtc);
