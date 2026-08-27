using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.Remote;

/// <summary>
/// Owns an embedded Kestrel instance that is reachable only through loopback.
/// Tailscale Serve is expected to terminate public HTTPS and proxy to this host.
/// </summary>
public sealed class RemoteControlHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly RemoteAuthCoordinator _auth;
    private readonly RemoteIdempotencyStore _idempotency;
    private readonly RemoteHostAccessGate _accessGate;
    private readonly TimeSpan _mutationShutdownDrainTimeout;
    private int _disposed;

    private RemoteControlHost(
        WebApplication application,
        RemoteAuthCoordinator auth,
        RemoteIdempotencyStore idempotency,
        RemoteHostAccessGate accessGate,
        int port,
        TimeSpan mutationShutdownDrainTimeout)
    {
        _application = application;
        _auth = auth;
        _idempotency = idempotency;
        _accessGate = accessGate;
        _mutationShutdownDrainTimeout = mutationShutdownDrainTimeout;
        LocalEndpoint = new Uri($"http://127.0.0.1:{port}", UriKind.Absolute);
    }

    public Uri LocalEndpoint { get; }

    public void RevokeAllSessions()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _auth.RevokeAll();
        _idempotency.Clear();
    }

    /// <summary>
    /// Permanently turns this listener into a deny-all guard. The socket deliberately remains
    /// bound until the owning ingress route is confirmed absent, preventing an unknown stale
    /// proxy from being redirected to a different local service on the same port.
    /// </summary>
    public void EnterFailClosedMode()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _accessGate.Quiesce();
        _auth.RevokeAll();
        _idempotency.Clear();
    }

    public static async Task<RemoteControlHost> StartAsync(
        IRemoteControlBackend backend,
        RemoteControlOptions options,
        IRemoteCredentialStore? credentialStore = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default,
        IRemoteSecurityAuditSink? securityAuditSink = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(options);
        RemoteControlOptionsValidator.ValidateAndThrow(options);
        if (options.RequireDurableSecurityAudit && securityAuditSink is null)
        {
            throw new InvalidOperationException(
                "A durable remote security-audit adapter is required by this host configuration.");
        }

        var sessions = new RemoteSessionStore(options, timeProvider);
        credentialStore ??= DenyAllRemoteCredentialStore.Instance;
        securityAuditSink ??= NullRemoteSecurityAuditSink.Instance;
        var auth = new RemoteAuthCoordinator(
            sessions,
            credentialStore,
            credentialStore as IRemoteRememberedDeviceStore);
        var idempotency = new RemoteIdempotencyStore(options, timeProvider);
        var accessGate = new RemoteHostAccessGate();
        var allowedLogins = options.CreateAllowedLoginSet();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(RemoteControlHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory
        });

        // This embedded security boundary is configured exclusively from the
        // strongly typed options above. Environment variables or appsettings must
        // never be able to add another Kestrel listener.
        builder.Configuration.Sources.Clear();

        // Remote-control requests and secrets must never be written to console,
        // debug, event log, or a default provider by this library.
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            kestrel.Limits.MaxRequestBodySize = 16 * 1024;
            kestrel.Limits.MaxRequestHeaderCount = 64;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.MaxConcurrentConnections = 64;
            kestrel.Limits.MaxConcurrentUpgradedConnections = 8;
            kestrel.ListenLocalhost(options.Port);
        });

        builder.Services.Configure<JsonOptions>(json =>
        {
            json.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(backend);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(credentialStore);
        builder.Services.AddSingleton(auth);
        builder.Services.AddSingleton(idempotency);
        builder.Services.AddSingleton<IRemoteSecurityAuditSink>(securityAuditSink);
        builder.Services.AddSingleton<IReadOnlySet<string>>(allowedLogins);
        builder.Services.AddAntiforgery(antiforgery =>
        {
            antiforgery.HeaderName = RemoteControlOptions.CsrfHeaderName;
            antiforgery.Cookie.Name = "__Host-MCSV-Auth-CSRF";
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.Path = "/";
        });
        builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
            forwarded.ForwardLimit = 1;
            forwarded.KnownProxies.Clear();
            forwarded.KnownProxies.Add(IPAddress.Loopback);
            forwarded.KnownProxies.Add(IPAddress.IPv6Loopback);
        });
        ConfigureRateLimiting(builder.Services, options, securityAuditSink);

        var application = builder.Build();
        ConfigurePipeline(application, options, accessGate);
        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            EnsureLoopbackOnly(application);
            return new RemoteControlHost(
                application,
                auth,
                idempotency,
                accessGate,
                options.Port,
                options.MutationShutdownDrainTimeout);
        }
        catch
        {
            auth.RevokeAll();
            idempotency.Clear();
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _accessGate.Quiesce();
        _auth.RevokeAll();
        _application.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        try
        {
            await _application.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = await _idempotency.DrainAsync(
                    _mutationShutdownDrainTimeout,
                    CancellationToken.None)
                .ConfigureAwait(false);
            _idempotency.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _accessGate.Quiesce();
            _auth.RevokeAll();
            _application.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            try
            {
                await _application.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                _ = await _idempotency.DrainAsync(
                        _mutationShutdownDrainTimeout,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                _idempotency.Clear();
            }
        }

        await _application.DisposeAsync().ConfigureAwait(false);
    }

    private static void ConfigureRateLimiting(
        IServiceCollection services,
        RemoteControlOptions options,
        IRemoteSecurityAuditSink securityAuditSink)
    {
        var allowedLogins = options.CreateAllowedLoginSet();
        services.AddRateLimiter(rateLimiter =>
        {
            rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rateLimiter.OnRejected = async (rejected, cancellationToken) =>
            {
                _ = RemoteApi.TryWriteAudit(
                    securityAuditSink,
                    RemoteApi.CreateAuditEvent(
                        RemoteSecurityAuditAction.RateLimitRejected,
                        RemoteSecurityAuditOutcome.Rejected,
                        null,
                        null,
                        null,
                        "rate_limit_rejected"));
                if (rejected.Lease.TryGetMetadata(
                        MetadataName.RetryAfter,
                        out var retryAfter))
                {
                    rejected.HttpContext.Response.Headers.RetryAfter =
                        Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                }

                await RemoteApi.WriteProblemAsync(
                        rejected.HttpContext,
                        StatusCodes.Status429TooManyRequests,
                        RemoteApi.Localize(rejected.HttpContext, "web.api.rateLimited"))
                    .ConfigureAwait(false);
            };
            var publicIngressGlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                options.IsPublicInternetIngress
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        "public-ingress-global",
                        _ => CreateFixedWindowOptions(options.GlobalRequestsPerMinute))
                    : RateLimitPartition.GetNoLimiter("tailscale-no-aggregate-limiter"));
            var perClientGlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    $"global:{GetLoginPartition(context, options, allowedLogins)}",
                    _ => CreateFixedWindowOptions(options.GlobalRequestsPerMinute)));
            // Public ingress needs a bounded aggregate limiter before creating or touching a
            // client partition. Funnel has no trusted client-address header, and public callers
            // can forge arbitrary headers, so identity is never used to evade this ceiling.
            rateLimiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                publicIngressGlobalLimiter,
                perClientGlobalLimiter);

            rateLimiter.AddPolicy("authentication", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    GetLoginPartition(context, options, allowedLogins),
                    _ => CreateFixedWindowOptions(options.LoginAttemptsPerMinute)));
            rateLimiter.AddPolicy("read", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    GetLoginPartition(context, options, allowedLogins),
                    _ => CreateFixedWindowOptions(options.ReadRequestsPerMinute)));
            rateLimiter.AddPolicy("mutation", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    GetLoginPartition(context, options, allowedLogins),
                    _ => CreateFixedWindowOptions(options.MutationRequestsPerMinute)));
        });
    }

    private static FixedWindowRateLimiterOptions CreateFixedWindowOptions(int permitLimit) => new()
    {
        AutoReplenishment = true,
        PermitLimit = permitLimit,
        QueueLimit = 0,
        Window = TimeSpan.FromMinutes(1)
    };

    internal static string GetLoginPartition(
        HttpContext context,
        RemoteControlOptions options,
        IReadOnlySet<string> allowedLogins)
    {
        if (options.IngressMode == RemoteIngressMode.TailscaleFunnel)
        {
            // Funnel is an Internet ingress. Its request headers and proxy peer do not prove a
            // public client's identity or address, so all callers share one bounded partition.
            return "public-funnel";
        }

        if (options.IsCloudflareTunnel)
        {
            return TryGetTrustedQuickTunnelClientAddress(context, out var address)
                ? $"public-quick-tunnel:{address}"
                : "public-quick-tunnel:unattributed";
        }

        if (RemoteIdentity.TryGetAllowedLogin(context.Request.Headers, allowedLogins, out var login))
        {
            return login.ToLowerInvariant();
        }

        return "unauthenticated";
    }

    private static bool TryGetTrustedQuickTunnelClientAddress(
        HttpContext context,
        out string canonicalAddress)
    {
        canonicalAddress = string.Empty;
        var proxyAddress = context.Connection.RemoteIpAddress;
        if (proxyAddress?.IsIPv4MappedToIPv6 == true)
        {
            proxyAddress = proxyAddress.MapToIPv4();
        }

        if (proxyAddress is null || !IPAddress.IsLoopback(proxyAddress))
        {
            // CF-Connecting-IP is client-controlled unless the immediate peer is the
            // loopback-only cloudflared process owned by this desktop application.
            return false;
        }

        if (!context.Request.Headers.TryGetValue(
                RemoteControlOptions.CloudflareConnectingIpHeaderName,
                out var values) ||
            values.Count != 1)
        {
            return false;
        }

        var value = values[0]?.Trim();
        if (string.IsNullOrEmpty(value) ||
            value.Length > 64 ||
            value.Contains('%') ||
            !IPAddress.TryParse(value, out var address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        canonicalAddress = address.ToString();
        return true;
    }

    private static void ConfigurePipeline(
        WebApplication application,
        RemoteControlOptions options,
        RemoteHostAccessGate accessGate)
    {
        // Only a loopback peer is trusted to supply these proxy headers. This lets
        // Tailscale Serve communicate the external HTTPS origin without trusting
        // forwarded headers from any LAN or Internet client.
        application.UseForwardedHeaders();
        application.Use(RemoteApi.ApplySecurityHeadersAsync);
        application.Use(async (context, next) =>
        {
            if (accessGate.IsQuiesced)
            {
                context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";
                await RemoteApi.WriteProblemAsync(
                        context,
                        StatusCodes.Status503ServiceUnavailable,
                        RemoteApi.Localize(context, "web.api.remoteUnavailable"))
                    .ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        application.UseExceptionHandler(exceptionApplication =>
        {
            exceptionApplication.Run(context => RemoteApi.WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                RemoteApi.Localize(context, "web.api.operationFailed")));
        });
        application.UseRouting();
        application.Use((context, next) => RemoteApi.DisableApiCachingAsync(context, next));
        application.UseRateLimiter();
        application.Use((context, next) => RemoteApi.RequireIngressIdentityAsync(context, next));

        RemoteApi.MapEndpoints(application, options);
        RemoteWebAssets.MapEndpoints(application);
    }

    private static void EnsureLoopbackOnly(WebApplication application)
    {
        var addresses = application.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?
            .Addresses;
        if (addresses is null || addresses.Count == 0)
        {
            throw new InvalidOperationException("Kestrel did not report a loopback listener.");
        }

        foreach (var address in addresses)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                !(string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                  IPAddress.TryParse(uri.Host, out var ipAddress) && IPAddress.IsLoopback(ipAddress)))
            {
                throw new InvalidOperationException("Remote control refused a non-loopback listener.");
            }
        }
    }

    private sealed class DenyAllRemoteCredentialStore : IRemoteCredentialStore
    {
        public static DenyAllRemoteCredentialStore Instance { get; } = new();

        public bool HasCredentialForLogin(string tailscaleLogin) => false;

        public RemoteCredentialAuthenticationResult Authenticate(
            string tailscaleLogin,
            string username,
            string pin)
            => new(RemoteCredentialAuthenticationStatus.InvalidCredentials);
    }

    private sealed class RemoteHostAccessGate
    {
        private int _quiesced;

        public bool IsQuiesced => Volatile.Read(ref _quiesced) != 0;

        public void Quiesce() => Interlocked.Exchange(ref _quiesced, 1);
    }
}
