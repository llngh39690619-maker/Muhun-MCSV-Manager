using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using MinecraftServerManager.Remote.Contracts;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Localization;
using MinecraftServerManager.Contracts.Security;
using System.Security.Cryptography;

namespace MinecraftServerManager.Remote;

internal static class RemoteApi
{
    internal static readonly object LoginItemKey = new();

    public static async Task ApplySecurityHeadersAsync(HttpContext context, RequestDelegate next)
    {
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] =
            "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; connect-src 'self'; img-src 'self' data:; script-src 'self'; style-src 'self'; worker-src 'self'; manifest-src 'self'";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Strict-Transport-Security"] = "max-age=31536000";
        await next(context).ConfigureAwait(false);
    }

    public static async Task RequireIngressIdentityAsync(HttpContext context, RequestDelegate next)
    {
        var options = context.RequestServices.GetRequiredService<RemoteControlOptions>();
        var allowedLogins = context.RequestServices.GetRequiredService<IReadOnlySet<string>>();
        if (options.IsPublicInternetIngress)
        {
            // Public ingress never carries an MCSV-verifiable end-user identity. This includes
            // Funnel: its Tailscale identity headers describe tailnet traffic, not arbitrary
            // Internet clients. Never trust any supplied identity or proxy header here.
            context.Items[LoginItemKey] = RemoteControlOptions.PublicTunnelCredentialSubject;
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!RemoteIdentity.TryGetAllowedLogin(context.Request.Headers, allowedLogins, out var login))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status401Unauthorized,
                Localize(context, "web.api.ingressIdentityRequired")).ConfigureAwait(false);
            return;
        }

        context.Items[LoginItemKey] = login;
        await next(context).ConfigureAwait(false);
    }

    public static async Task DisableApiCachingAsync(HttpContext context, RequestDelegate next)
    {
        // The mobile page and API evolve together inside the signed executable. Do not let a
        // browser retain an older privileged script against a newer API, and never cache data.
        context.Response.Headers.CacheControl = "no-store, max-age=0";
        context.Response.Headers.Pragma = "no-cache";

        await next(context).ConfigureAwait(false);
    }

    public static void MapEndpoints(WebApplication application, RemoteControlOptions options)
    {
        var api = application.MapGroup("/api/v1");

        api.MapGet("/auth/status", GetAuthStatusAsync)
            .RequireRateLimiting("read");
        api.MapPost("/auth/login", (HttpContext context, RemoteCredentialLoginRequestDto request) =>
                LoginAsync(context, request, options))
            .RequireRateLimiting("authentication");
        api.MapPost("/auth/devices/enroll", (HttpContext context, RemoteRememberedDeviceEnrollmentRequestDto request) =>
                EnrollRememberedDevice(context, request, options))
            .RequireRateLimiting("authentication");
        api.MapPost("/auth/devices/refresh", (HttpContext context, RemoteRememberedDeviceRefreshRequestDto request) =>
                RefreshRememberedDeviceAsync(context, request, options))
            .RequireRateLimiting("authentication");
        api.MapPost("/auth/signout", (HttpContext context, RemoteEmptyMutationRequestDto _) =>
                SignOutAsync(context, options))
            .RequireRateLimiting("mutation");

        api.MapGet("/dashboard", GetDashboardAsync)
            .RequireRateLimiting("read");
        api.MapGet("/servers/{serverId}", GetServerAsync)
            .RequireRateLimiting("read");
        api.MapGet("/servers/{serverId}/administration", GetServerAdministrationAsync)
            .RequireRateLimiting("read");
        api.MapGet("/servers/{serverId}/console", (HttpContext context, string serverId) =>
                GetConsoleAsync(context, serverId, options))
            .RequireRateLimiting("read");
        api.MapGet("/servers/{serverId}/players", GetPlayersAsync)
            .RequireRateLimiting("read");
        api.MapGet("/servers/{serverId}/backups", GetBackupsAsync)
            .RequireRateLimiting("read");

        api.MapPost("/servers/{serverId}/actions/{action}", (
                HttpContext context,
                string serverId,
                string action,
                RemoteEmptyMutationRequestDto _) =>
                PerformLifecycleActionAsync(context, serverId, action, options))
            .RequireRateLimiting("mutation");
        api.MapPost("/servers/{serverId}/console/commands", (HttpContext context, string serverId, RemoteCommandRequestDto request) =>
                SendCommandAsync(context, serverId, request, options))
            .RequireRateLimiting("mutation");
        api.MapPost("/servers/{serverId}/player-actions", (HttpContext context, string serverId, RemotePlayerActionRequestDto request) =>
                PerformPlayerActionAsync(context, serverId, request, options))
            .RequireRateLimiting("mutation");
        api.MapPost("/servers/{serverId}/backups", (
                HttpContext context,
                string serverId,
                RemoteEmptyMutationRequestDto _) =>
                PerformServerMutationAsync(
                    context,
                    serverId,
                    ProductPermissionCodes.BackupCreate,
                    static id => RemoteMutationSignature.CreateBackup(id),
                    static (backend, id, token) => backend.CreateBackupAsync(id, token),
                    options))
            .RequireRateLimiting("mutation");
        api.MapPost("/servers/{serverId}/backups/{backupId}/restore", (
                HttpContext context,
                string serverId,
                string backupId,
                RemoteBackupRestoreRequestDto request) =>
                RestoreBackupAsync(context, serverId, backupId, request, options))
            .RequireRateLimiting("mutation");

        api.MapGet("/updates/{channel}", GetProductUpdateStatusAsync)
            .RequireRateLimiting("read");
        api.MapPost("/updates/{channel}/check", (
                HttpContext context,
                string channel,
                RemoteEmptyMutationRequestDto _) =>
                PerformProductUpdateMutationAsync(context, channel, "check", null, options))
            .RequireRateLimiting("mutation");
        api.MapPost("/updates/{channel}/download", (
                HttpContext context,
                string channel,
                RemoteEmptyMutationRequestDto _) =>
                PerformProductUpdateMutationAsync(context, channel, "download", null, options))
            .RequireRateLimiting("mutation");
        api.MapPost("/updates/{channel}/schedule", (
                HttpContext context,
                string channel,
                RemoteProductUpdateScheduleRequestDto request) =>
                PerformProductUpdateMutationAsync(
                    context,
                    channel,
                    "schedule",
                    request?.NotBeforeUtc,
                    options))
            .RequireRateLimiting("mutation");

        application.Map("/api/{**unmatchedApiPath}", static () => Results.NotFound());
    }

    private static IResult EnrollRememberedDevice(
        HttpContext context,
        RemoteRememberedDeviceEnrollmentRequestDto request,
        RemoteControlOptions options)
    {
        var rejection = ValidateMutation(context, options, out var session);
        if (rejection is not null)
        {
            return rejection;
        }
        var validatedSession = session!;

        if (!SupportsRememberedDevices(options))
        {
            return RememberedDevicesUnavailable(context);
        }

        var label = request?.DeviceName?.Trim() ?? string.Empty;
        if (label.Length is < 1 or > 64 || label.Any(char.IsControl))
        {
            return LocalizedBadRequest(context, "web.api.deviceNameInvalid");
        }

        if (!context.Request.Cookies.TryGetValue(options.SessionCookieName, out var sessionToken) ||
            !context.Request.Headers.TryGetValue(RemoteControlOptions.CsrfHeaderName, out var csrfValues) ||
            csrfValues.Count != 1)
        {
            return Unauthorized(context);
        }

        if (!TryWriteAudit(
                context.RequestServices.GetRequiredService<IRemoteSecurityAuditSink>(),
                CreateAuditEvent(
                    RemoteSecurityAuditAction.RememberedDeviceEnroll,
                    RemoteSecurityAuditOutcome.Accepted,
                    validatedSession.Username,
                    null,
                    null,
                    "enrollment_authorized")))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: Localize(context, "web.api.auditDeviceEnrollUnavailable"));
        }

        IssuedRemoteRememberedDevice issued;
        var authCoordinator = context.RequestServices
            .GetRequiredService<RemoteAuthCoordinator>();
        try
        {
            var authorization = authCoordinator.TryEnrollRememberedDevice(
                    sessionToken,
                    GetLogin(context),
                    csrfValues[0],
                    label,
                    out issued);
            if (authorization != RemoteMutationAuthorizationStatus.Accepted)
            {
                return Unauthorized(context);
            }
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: Localize(context, "web.api.deviceCapacityExceeded"));
        }

        AppendRememberedDeviceCookie(context, issued, options);
        return Results.Json(new RemoteAuthStatusDto(
            true,
            GetDisplayLogin(options, validatedSession.Login),
            validatedSession.CsrfToken,
            validatedSession.ExpiresAtUtc,
            true,
            issued.Device.Username,
            ToPermissionSet(validatedSession.Permissions, validatedSession.Authorization),
            true,
            issued.Device.AbsoluteExpiresAtUtc,
            SupportsRememberedDevices: true,
            PermissionGrants: ToPermissionGrants(validatedSession.Authorization)));
    }

    private static async Task<IResult> RefreshRememberedDeviceAsync(
        HttpContext context,
        RemoteRememberedDeviceRefreshRequestDto request,
        RemoteControlOptions options)
    {
        if (!RemoteRequestSecurity.HasExactMutationOrigin(context.Request, options.PublicOrigin))
        {
            return Forbidden(context);
        }

        try
        {
            await context.RequestServices.GetRequiredService<IAntiforgery>()
                .ValidateRequestAsync(context)
                .ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Forbidden(context);
        }

        if (!SupportsRememberedDevices(options))
        {
            DeleteRememberedDeviceCookie(context, options);
            return RememberedDevicesUnavailable(context);
        }

        if (request is null || request.RequestId == Guid.Empty ||
            !context.Request.Cookies.TryGetValue(options.RememberedDeviceCookieName, out var token))
        {
            DeleteRememberedDeviceCookie(context, options);
            return Unauthorized(context);
        }

        if (!TryWriteAudit(
                context.RequestServices.GetRequiredService<IRemoteSecurityAuditSink>(),
                CreateAuditEvent(
                    RemoteSecurityAuditAction.RememberedDeviceRefresh,
                    RemoteSecurityAuditOutcome.Accepted,
                    null,
                    null,
                    null,
                    "refresh_authorized")))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: Localize(context, "web.api.auditDeviceRefreshUnavailable"));
        }

        var result = context.RequestServices.GetRequiredService<RemoteAuthCoordinator>()
            .TryRefreshRememberedDevice(
                GetLogin(context),
                token,
                request.RequestId,
                out var session);
        if (result.Status != RemoteRememberedDeviceRefreshStatus.Success ||
            string.IsNullOrWhiteSpace(result.ReplacementToken) ||
            result.Device is null ||
            string.IsNullOrWhiteSpace(result.Username))
        {
            DeleteRememberedDeviceCookie(context, options);
            return Unauthorized(context);
        }

        if (session is null)
        {
            // A sign-out or desktop revocation raced this refresh after the durable
            // credential rotated. Never put that replacement back into the browser:
            // the completed revocation owns the final state.
            DeleteRememberedDeviceCookie(context, options);
            return Unauthorized(context);
        }

        AppendRememberedDeviceCookie(context, result, options);
        AppendSessionCookie(context, session, options);
        return Results.Json(new RemoteAuthStatusDto(
            true,
            GetDisplayLogin(options, session.Login),
            session.CsrfToken,
            session.ExpiresAtUtc,
            true,
            result.Username,
            ToPermissionSet(session.Permissions, session.Authorization),
            true,
            result.Device.AbsoluteExpiresAtUtc,
            SupportsRememberedDevices: true,
            PermissionGrants: ToPermissionGrants(session.Authorization)));
    }

    private static IResult GetAuthStatusAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        RemoteControlOptions options)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        var supportsRememberedDevices = SupportsRememberedDevices(options);
        if (TryAuthenticate(context, out var session, out var authorization))
        {
            return Results.Json(new RemoteAuthStatusDto(
                true,
                GetDisplayLogin(options, session.Login),
                session.CsrfToken,
                session.ExpiresAtUtc,
                true,
                session.Username,
                ToPermissionSet(session.Permissions, authorization),
                SupportsRememberedDevices: supportsRememberedDevices,
                AntiforgeryToken: tokens.RequestToken,
                PermissionGrants: ToPermissionGrants(authorization)));
        }

        var login = GetLogin(context);
        var credentialRegistered = context.RequestServices
            .GetRequiredService<RemoteAuthCoordinator>()
            .HasCredentialForLogin(login);
        return Results.Json(new RemoteAuthStatusDto(
            false,
            options.IsPublicInternetIngress ? string.Empty : login,
            tokens.RequestToken,
            null,
            credentialRegistered,
            SupportsRememberedDevices: supportsRememberedDevices,
            AntiforgeryToken: tokens.RequestToken));
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        RemoteCredentialLoginRequestDto request,
        RemoteControlOptions options)
    {
        if (!RemoteRequestSecurity.HasExactMutationOrigin(context.Request, options.PublicOrigin))
        {
            return Forbidden(context);
        }

        try
        {
            await context.RequestServices.GetRequiredService<IAntiforgery>()
                .ValidateRequestAsync(context)
                .ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Forbidden(context);
        }

        if (request is null ||
            !RemoteCredentialRules.TryNormalizeUsername(request.Username, out var normalizedUsername) ||
            !RemoteCredentialRules.IsValidPin(request.Pin))
        {
            return LocalizedBadRequest(context, "web.api.credentialsFormatInvalid");
        }

        var auth = context.RequestServices.GetRequiredService<RemoteAuthCoordinator>();
        var authentication = auth.TryLogin(
            request.Username,
            request.Pin,
            GetLogin(context),
            out var session);
        if (authentication.Status != RemoteCredentialAuthenticationStatus.Success)
        {
            _ = TryWriteAudit(
                context.RequestServices.GetRequiredService<IRemoteSecurityAuditSink>(),
                CreateAuditEvent(
                    RemoteSecurityAuditAction.CredentialLogin,
                    RemoteSecurityAuditOutcome.Rejected,
                    normalizedUsername,
                    null,
                    null,
                    "invalid_credentials"));
            // Do not reveal whether a normalized username exists or is persistently locked.
            // Edge/global request limiting still returns 429 independently, but credential
            // verification always exposes one response shape to the remote caller.
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: Localize(context, "web.api.credentialsInvalid"));
        }

        if (!TryWriteAudit(
                context.RequestServices.GetRequiredService<IRemoteSecurityAuditSink>(),
                CreateAuditEvent(
                    RemoteSecurityAuditAction.CredentialLogin,
                    RemoteSecurityAuditOutcome.Accepted,
                    session.Username,
                    null,
                    null,
                    "login_accepted")))
        {
            _ = auth.Revoke(session.SessionToken);
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: Localize(context, "web.api.auditSessionUnavailable"));
        }

        AppendSessionCookie(context, session, options);
        return Results.Json(new RemoteAuthStatusDto(
            true,
            GetDisplayLogin(options, session.Login),
            session.CsrfToken,
            session.ExpiresAtUtc,
            true,
            session.Username,
            ToPermissionSet(session.Permissions, session.Authorization),
            SupportsRememberedDevices: SupportsRememberedDevices(options),
            PermissionGrants: ToPermissionGrants(session.Authorization)));
    }

    private static async Task<IResult> SignOutAsync(
        HttpContext context,
        RemoteControlOptions options)
    {
        if (!RemoteRequestSecurity.HasExactMutationOrigin(context.Request, options.PublicOrigin))
        {
            return Forbidden(context);
        }

        try
        {
            await context.RequestServices.GetRequiredService<IAntiforgery>()
                .ValidateRequestAsync(context)
                .ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Forbidden(context);
        }

        context.Request.Cookies.TryGetValue(options.SessionCookieName, out var sessionToken);
        context.Request.Cookies.TryGetValue(
            options.RememberedDeviceCookieName,
            out var deviceToken);

        // Durable device revocation happens before session invalidation. If the protected store
        // is temporarily unwritable, the exception handler returns a retryable failure while the
        // browser retains both cookies and its independent antiforgery token.
        context.RequestServices.GetRequiredService<RemoteAuthCoordinator>()
            .SignOut(GetLogin(context), sessionToken, deviceToken);
        _ = TryWriteAudit(
            context.RequestServices.GetRequiredService<IRemoteSecurityAuditSink>(),
            CreateAuditEvent(
                RemoteSecurityAuditAction.SessionSignOut,
                RemoteSecurityAuditOutcome.Accepted,
                null,
                null,
                null,
                "session_signed_out"));

        context.Response.Cookies.Delete(options.SessionCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
        DeleteRememberedDeviceCookie(context, options);
        return Results.NoContent();
    }

    private static async Task<IResult> GetDashboardAsync(
        HttpContext context,
        IRemoteControlBackend backend,
        CancellationToken cancellationToken)
    {
        if (!TryAuthenticate(context, out var session, out var authorization))
        {
            return Unauthorized(context);
        }

        var dashboard = await backend.GetDashboardAsync(cancellationToken).ConfigureAwait(false);
        var visibleServers = (dashboard.Servers ?? [])
            .Where(server => IsPermissionGranted(
                session,
                authorization,
                ProductPermissionCodes.ServerRead,
                TryParseProductServerId(server.Id)))
            .ToArray();
        return Results.Json(dashboard with { Servers = visibleServers });
    }

    private static async Task<IResult> GetServerAsync(
        HttpContext context,
        string serverId,
        IRemoteControlBackend backend,
        CancellationToken cancellationToken)
    {
        var rejection = ValidateRead(context, serverId, ProductPermissionCodes.ServerRead);
        if (rejection is not null)
        {
            return rejection;
        }

        var server = await backend.GetServerAsync(serverId, cancellationToken).ConfigureAwait(false);
        return server is null ? Results.NotFound() : Results.Json(server);
    }

    private static async Task<IResult> GetServerAdministrationAsync(
        HttpContext context,
        string serverId,
        IRemoteControlBackend backend,
        CancellationToken cancellationToken)
    {
        // Add-on and Java metadata are still server data. Re-check server.read and its exact
        // server scope on every request instead of relying on dashboard visibility or UI state.
        var rejection = ValidateRead(context, serverId, ProductPermissionCodes.ServerRead);
        if (rejection is not null)
        {
            return rejection;
        }

        var snapshot = await backend
            .GetServerAdministrationAsync(serverId, cancellationToken)
            .ConfigureAwait(false);
        return snapshot is null
            ? Results.NotFound()
            : Results.Json(BoundServerAdministration(snapshot));
    }

    private static async Task<IResult> GetConsoleAsync(
        HttpContext context,
        string serverId,
        RemoteControlOptions options)
    {
        var rejection = ValidateRead(context, serverId, ProductPermissionCodes.ConsoleRead);
        if (rejection is not null)
        {
            return rejection;
        }

        if (!TryParseConsoleQuery(context.Request.Query, options, out var query))
        {
            return LocalizedBadRequest(context, "web.api.consoleQueryInvalid");
        }

        var backend = context.RequestServices.GetRequiredService<IRemoteControlBackend>();
        var page = await backend.GetConsoleAsync(serverId, query, context.RequestAborted).ConfigureAwait(false);
        return page is null
            ? Results.NotFound()
            : Results.Json(BoundConsolePage(page, query, options.MaximumConsoleLineCharacters));
    }

    private static async Task<IResult> GetPlayersAsync(
        HttpContext context,
        string serverId,
        IRemoteControlBackend backend,
        CancellationToken cancellationToken)
    {
        var rejection = ValidateRead(context, serverId, ProductPermissionCodes.PlayerRead);
        if (rejection is not null)
        {
            return rejection;
        }

        var players = await backend.GetPlayersAsync(serverId, cancellationToken).ConfigureAwait(false);
        return players is null ? Results.NotFound() : Results.Json(players);
    }

    private static async Task<IResult> GetBackupsAsync(
        HttpContext context,
        string serverId,
        IRemoteControlBackend backend,
        CancellationToken cancellationToken)
    {
        var rejection = ValidateRead(context, serverId, ProductPermissionCodes.BackupRead);
        if (rejection is not null)
        {
            return rejection;
        }

        var backups = await backend.GetBackupsAsync(serverId, cancellationToken).ConfigureAwait(false);
        return backups is null
            ? Results.NotFound()
            : Results.Json(BoundBackupList(backups));
    }

    private static async Task<IResult> GetProductUpdateStatusAsync(
        HttpContext context,
        string channel,
        IRemoteControlBackend backend,
        CancellationToken cancellationToken)
    {
        var rejection = ValidateGlobalRead(context, ProductPermissionCodes.UpdateManage);
        if (rejection is not null)
        {
            return rejection;
        }

        if (!TryParseUpdateChannel(channel, out var parsed))
        {
            return LocalizedBadRequest(context, "web.api.updateChannelInvalid");
        }

        return Results.Json(
            await backend.GetProductUpdateStatusAsync(parsed, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> PerformProductUpdateMutationAsync(
        HttpContext context,
        string channel,
        string action,
        DateTimeOffset? notBeforeUtc,
        RemoteControlOptions options)
    {
        var rejection = ValidateMutation(context, options, out _);
        if (rejection is not null)
        {
            return rejection;
        }

        if (!TryParseUpdateChannel(channel, out var parsed) ||
            action is not ("check" or "download" or "schedule") ||
            (action != "schedule" && notBeforeUtc is not null) ||
            (notBeforeUtc is { Offset: var offset } && offset != TimeSpan.Zero))
        {
            WriteRejectedMutationAudit(context, "invalid_product_update", ProductPermissionCodes.UpdateManage);
            return LocalizedBadRequest(context, "web.api.updateRequestInvalid");
        }

        var backend = context.RequestServices.GetRequiredService<IRemoteControlBackend>();
        var signature = RemoteMutationSignature.CreateProductUpdate(action, channel.ToLowerInvariant(), notBeforeUtc);
        try
        {
            return await ExecuteIdempotentMutationAsync(
                    context,
                    signature,
                    ProductPermissionCodes.UpdateManage,
                    targetServerId: null,
                    action switch
                    {
                        "check" => token => backend.CheckForProductUpdateAsync(parsed, token),
                        "download" => token => backend.DownloadProductUpdateAsync(parsed, token),
                        "schedule" => token => backend.ScheduleProductUpdateAsync(parsed, notBeforeUtc, token),
                        _ => throw new InvalidOperationException("Unsupported product update action."),
                    })
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static async Task<IResult> PerformServerMutationAsync(
        HttpContext context,
        string serverId,
        string requiredPermissionCode,
        Func<string, byte[]> createSignature,
        Func<IRemoteControlBackend, string, CancellationToken, ValueTask<RemoteOperationResultDto>> operation,
        RemoteControlOptions options)
    {
        var rejection = ValidateMutationWithServer(context, serverId, options, out _);
        if (rejection is not null)
        {
            return rejection;
        }

        var backend = context.RequestServices.GetRequiredService<IRemoteControlBackend>();
        var signature = createSignature(serverId);
        try
        {
            return await ExecuteIdempotentMutationAsync(
                    context,
                    signature,
                    requiredPermissionCode,
                    TryParseProductServerId(serverId),
                    token => operation(backend, serverId, token))
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static Task<IResult> PerformLifecycleActionAsync(
        HttpContext context,
        string serverId,
        string action,
        RemoteControlOptions options)
    {
        return action switch
        {
            "start" => PerformServerMutationAsync(
                context,
                serverId,
                ProductPermissionCodes.ServerStart,
                id => RemoteMutationSignature.CreateLifecycle(id, "start"),
                static (backend, id, token) => backend.StartServerAsync(id, token),
                options),
            "stop" => PerformServerMutationAsync(
                context,
                serverId,
                ProductPermissionCodes.ServerStop,
                id => RemoteMutationSignature.CreateLifecycle(id, "stop"),
                static (backend, id, token) => backend.StopServerAsync(id, token),
                options),
            "restart" => PerformServerMutationAsync(
                context,
                serverId,
                ProductPermissionCodes.ServerRestart,
                id => RemoteMutationSignature.CreateLifecycle(id, "restart"),
                static (backend, id, token) => backend.RestartServerAsync(id, token),
                options),
            _ => Task.FromResult<IResult>(Results.NotFound())
        };
    }

    private static async Task<IResult> SendCommandAsync(
        HttpContext context,
        string serverId,
        RemoteCommandRequestDto request,
        RemoteControlOptions options)
    {
        var rejection = ValidateMutationWithServer(context, serverId, options, out _);
        if (rejection is not null)
        {
            return rejection;
        }

        if (request is null)
        {
            WriteRejectedMutationAudit(
                context,
                "invalid_command",
                ProductPermissionCodes.ConsoleWrite,
                TryParseProductServerId(serverId));
            return LocalizedBadRequest(context, "web.api.commandInvalid");
        }

        if (!RemoteInputValidator.TryValidateCommand(
                request.Command,
                options.MaximumCommandLength,
                out var error))
        {
            WriteRejectedMutationAudit(
                context,
                "invalid_command",
                ProductPermissionCodes.ConsoleWrite,
                TryParseProductServerId(serverId));
            return LocalizedValidationBadRequest(context, error, options.MaximumCommandLength);
        }

        var backend = context.RequestServices.GetRequiredService<IRemoteControlBackend>();
        var signature = RemoteMutationSignature.CreateCommand(serverId, request.Command);
        try
        {
            return await ExecuteIdempotentMutationAsync(
                    context,
                    signature,
                    ProductPermissionCodes.ConsoleWrite,
                    TryParseProductServerId(serverId),
                    token => backend.SendConsoleCommandAsync(serverId, request.Command, token))
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static async Task<IResult> PerformPlayerActionAsync(
        HttpContext context,
        string serverId,
        RemotePlayerActionRequestDto request,
        RemoteControlOptions options)
    {
        var rejection = ValidateMutationWithServer(context, serverId, options, out _);
        if (rejection is not null)
        {
            return rejection;
        }

        if (!RemoteInputValidator.TryValidatePlayerAction(request, out var error))
        {
            WriteRejectedMutationAudit(
                context,
                "invalid_player_action",
                ProductPermissionCodes.PlayerManage,
                TryParseProductServerId(serverId));
            return LocalizedValidationBadRequest(context, error);
        }

        var backend = context.RequestServices.GetRequiredService<IRemoteControlBackend>();
        var signature = RemoteMutationSignature.CreatePlayerAction(serverId, request);
        try
        {
            return await ExecuteIdempotentMutationAsync(
                    context,
                    signature,
                    ProductPermissionCodes.PlayerManage,
                    TryParseProductServerId(serverId),
                    token => backend.PerformPlayerActionAsync(serverId, request, token))
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static async Task<IResult> RestoreBackupAsync(
        HttpContext context,
        string serverId,
        string backupId,
        RemoteBackupRestoreRequestDto request,
        RemoteControlOptions options)
    {
        var rejection = ValidateMutationWithServer(context, serverId, options, out _);
        if (rejection is not null)
        {
            return rejection;
        }

        if (!RemoteInputValidator.TryValidateBackupId(backupId, out var error) ||
            request is null ||
            !string.Equals(
                request.Confirmation,
                RemoteBackupRestoreContract.RequiredConfirmation,
                StringComparison.Ordinal))
        {
            WriteRejectedMutationAudit(
                context,
                "invalid_backup_restore",
                ProductPermissionCodes.BackupRestore,
                TryParseProductServerId(serverId));
            return error.Length == 0
                ? LocalizedBadRequest(context, "web.api.backupConfirmationRequired")
                : LocalizedValidationBadRequest(context, error);
        }

        var backend = context.RequestServices.GetRequiredService<IRemoteControlBackend>();
        var signature = RemoteMutationSignature.CreateBackupRestore(
            serverId,
            backupId,
            request.Confirmation);
        try
        {
            return await ExecuteIdempotentMutationAsync(
                    context,
                    signature,
                    ProductPermissionCodes.BackupRestore,
                    TryParseProductServerId(serverId),
                    token => backend.RestoreBackupAsync(serverId, backupId, token))
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static async Task<IResult> ExecuteIdempotentMutationAsync(
        HttpContext context,
        ReadOnlyMemory<byte> requestSignature,
        string requiredPermissionCode,
        Guid? targetServerId,
        Func<CancellationToken, ValueTask<RemoteOperationResultDto>> operation)
    {
        if (!TryGetIdempotencyKey(context, out var idempotencyKey))
        {
            WriteRejectedMutationAudit(
                context,
                "invalid_idempotency_key",
                requiredPermissionCode,
                targetServerId);
            return LocalizedBadRequest(context, "web.api.idempotencyKeyRequired");
        }

        var services = context.RequestServices;
        var options = services.GetRequiredService<RemoteControlOptions>();
        if (!context.Request.Cookies.TryGetValue(options.SessionCookieName, out var sessionToken) ||
            !context.Request.Headers.TryGetValue(RemoteControlOptions.CsrfHeaderName, out var csrfValues) ||
            csrfValues.Count != 1)
        {
            return Unauthorized(context);
        }

        var store = services.GetRequiredService<RemoteIdempotencyStore>();
        var auth = services.GetRequiredService<RemoteAuthCoordinator>();
        var auditSink = services.GetRequiredService<IRemoteSecurityAuditSink>();
        var auditWriteFailed = false;
        var authorization = auth.TryAcceptMutation(
                sessionToken,
                GetLogin(context),
                csrfValues[0],
                requiredPermissionCode,
                targetServerId,
                acceptance =>
                {
                    var auditEvent = CreateAuditEvent(
                        RemoteSecurityAuditAction.ServerMutation,
                        RemoteSecurityAuditOutcome.Accepted,
                        acceptance.Username,
                        acceptance.PermissionCode,
                        acceptance.ServerId,
                        "authorization_accepted",
                        idempotencyKey);
                    if (!TryWriteAudit(auditSink, auditEvent))
                    {
                        auditWriteFailed = true;
                        return null!;
                    }

                    return store.ExecuteAsync(
                        acceptance.SessionId,
                        idempotencyKey,
                        requestSignature,
                        operation,
                        options.OperationCancellationToken,
                        context.RequestAborted);
                },
                out Task<RemoteIdempotencyExecution> executionTask);
        if (authorization != RemoteAuthorizationStatus.Granted)
        {
            _ = TryWriteAudit(
                auditSink,
                CreateAuditEvent(
                    RemoteSecurityAuditAction.ServerMutation,
                    RemoteSecurityAuditOutcome.Rejected,
                    null,
                    requiredPermissionCode,
                    targetServerId,
                    authorization == RemoteAuthorizationStatus.Forbidden
                        ? "permission_denied"
                        : "session_invalid",
                    idempotencyKey));
            return authorization == RemoteAuthorizationStatus.Forbidden
                ? Forbidden(context)
                : Unauthorized(context);
        }

        if (auditWriteFailed)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: Localize(context, "web.api.auditOperationUnavailable"));
        }

        var execution = await executionTask.ConfigureAwait(false);

        return execution.Outcome switch
        {
            RemoteIdempotencyOutcome.Completed when execution.Result is not null =>
                Results.Json(execution.Result),
            RemoteIdempotencyOutcome.Conflict => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: Localize(context, "web.api.idempotencyConflict")),
            RemoteIdempotencyOutcome.CapacityExceeded => Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: Localize(context, "web.api.idempotencyCapacity")),
            _ => Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: Localize(context, "web.api.idempotencyNoResult"))
        };
    }

    private static IResult? ValidateRead(
        HttpContext context,
        string serverId,
        string requiredPermissionCode)
    {
        if (!RemoteInputValidator.TryValidateServerId(serverId, out var error))
        {
            return LocalizedValidationBadRequest(context, error);
        }

        var options = context.RequestServices.GetRequiredService<RemoteControlOptions>();
        if (!context.Request.Cookies.TryGetValue(options.SessionCookieName, out var token))
        {
            return Unauthorized(context);
        }

        var authorization = context.RequestServices
            .GetRequiredService<RemoteAuthCoordinator>()
            .TryAuthorizeRequest(
                token,
                GetLogin(context),
                requiredPermissionCode,
                TryParseProductServerId(serverId),
                out _,
                out _);
        return authorization switch
        {
            RemoteAuthorizationStatus.Granted => null,
            RemoteAuthorizationStatus.Forbidden => Forbidden(context),
            _ => Unauthorized(context),
        };
    }

    private static IResult? ValidateGlobalRead(HttpContext context, string requiredPermissionCode)
    {
        var options = context.RequestServices.GetRequiredService<RemoteControlOptions>();
        if (!context.Request.Cookies.TryGetValue(options.SessionCookieName, out var token))
        {
            return Unauthorized(context);
        }

        var authorization = context.RequestServices
            .GetRequiredService<RemoteAuthCoordinator>()
            .TryAuthorizeRequest(
                token,
                GetLogin(context),
                requiredPermissionCode,
                targetServerId: null,
                out _,
                out _);
        return authorization switch
        {
            RemoteAuthorizationStatus.Granted => null,
            RemoteAuthorizationStatus.Forbidden => Forbidden(context),
            _ => Unauthorized(context),
        };
    }

    private static IResult? ValidateMutationWithServer(
        HttpContext context,
        string serverId,
        RemoteControlOptions options,
        out ValidatedRemoteSession? session)
    {
        var rejection = ValidateMutation(context, options, out session);
        if (rejection is not null)
        {
            return rejection;
        }

        if (!RemoteInputValidator.TryValidateServerId(serverId, out var error))
        {
            WriteRejectedMutationAudit(
                context,
                "invalid_server_id",
                username: session?.Username);
            session = null;
            return LocalizedValidationBadRequest(context, error);
        }

        return null;
    }

    private static bool TryGetIdempotencyKey(HttpContext context, out Guid key)
    {
        key = Guid.Empty;
        return context.Request.Headers.TryGetValue(
                   RemoteControlOptions.IdempotencyHeaderName,
                   out var values) &&
               values.Count == 1 &&
               RemoteInputValidator.TryParseIdempotencyKey(values[0], out key);
    }

    private static IResult? ValidateMutation(
        HttpContext context,
        RemoteControlOptions options,
        out ValidatedRemoteSession? session)
    {
        session = null;
        if (!TryAuthenticate(context, out var authenticatedSession, out _))
        {
            WriteRejectedMutationAudit(context, "session_invalid");
            return Unauthorized(context);
        }

        if (!RemoteRequestSecurity.HasExactMutationOrigin(context.Request, options.PublicOrigin))
        {
            WriteRejectedMutationAudit(
                context,
                "origin_invalid",
                username: authenticatedSession.Username);
            return Forbidden(context);
        }

        if (!context.Request.Headers.TryGetValue(RemoteControlOptions.CsrfHeaderName, out var csrfValues) ||
            csrfValues.Count != 1 ||
            !RemoteSessionStore.CsrfMatches(authenticatedSession, csrfValues[0]))
        {
            WriteRejectedMutationAudit(
                context,
                "csrf_invalid",
                username: authenticatedSession.Username);
            return Forbidden(context);
        }

        session = authenticatedSession;
        return null;
    }

    private static bool TryAuthenticate(
        HttpContext context,
        out ValidatedRemoteSession session,
        out RemoteAuthorizationSnapshot? authorization)
    {
        session = default!;
        authorization = null;
        var options = context.RequestServices.GetRequiredService<RemoteControlOptions>();
        if (!context.Request.Cookies.TryGetValue(options.SessionCookieName, out var token))
        {
            return false;
        }

        return context.RequestServices.GetRequiredService<RemoteAuthCoordinator>()
            .TryValidateSession(token, GetLogin(context), out session, out authorization);
    }

    private static Guid? TryParseProductServerId(string? serverId)
        => Guid.TryParse(serverId, out var parsed) && parsed != Guid.Empty ? parsed : null;

    private static bool TryParseUpdateChannel(string? value, out ProductUpdateChannel channel)
        => Enum.TryParse(value, ignoreCase: true, out channel) && Enum.IsDefined(channel);

    private static bool IsPermissionGranted(
        ValidatedRemoteSession session,
        RemoteAuthorizationSnapshot? authorization,
        string permissionCode,
        Guid? targetServerId)
    {
        if (authorization is null)
        {
            return RemoteLegacyPermissionMapping.IsGranted(session.Permissions, permissionCode);
        }

        return targetServerId is { } id &&
               ProductAuthorization.Evaluate(
                   authorization.Grants,
                   permissionCode,
                   id) == ProductAuthorizationDecision.Granted;
    }

    private static string GetLogin(HttpContext context)
        => (string)context.Items[LoginItemKey]!;

    private static string GetDisplayLogin(RemoteControlOptions options, string login)
        => options.IsPublicInternetIngress ? string.Empty : login;

    private static bool SupportsRememberedDevices(RemoteControlOptions options)
        => options.SupportsRememberedDevices;

    private static IResult RememberedDevicesUnavailable(HttpContext context)
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: Localize(context, "web.api.rememberUnavailable"));

    private static RemoteWebPermissionSetDto ToPermissionSet(
        RemoteWebPermission permissions,
        RemoteAuthorizationSnapshot? authorization)
        => authorization is not null
            ? new(false, false, false, false, false, false)
            : new(
                permissions.HasFlag(RemoteWebPermission.StartServer),
                permissions.HasFlag(RemoteWebPermission.StopServer),
                permissions.HasFlag(RemoteWebPermission.RestartServer),
                permissions.HasFlag(RemoteWebPermission.SendConsoleCommand),
                permissions.HasFlag(RemoteWebPermission.ManagePlayers),
                permissions.HasFlag(RemoteWebPermission.CreateBackup));

    private static IReadOnlyList<RemotePermissionGrantDto>? ToPermissionGrants(
        RemoteAuthorizationSnapshot? authorization)
        => authorization?.Grants
            .Select(grant => new RemotePermissionGrantDto(
                grant.PermissionCode,
                grant.Scope.Kind == ProductPermissionScopeKind.Global
                    ? RemotePermissionScopeKind.Global
                    : RemotePermissionScopeKind.Server,
                grant.Scope.ServerId))
            .ToArray();

    internal static RemoteSecurityAuditEvent CreateAuditEvent(
        RemoteSecurityAuditAction action,
        RemoteSecurityAuditOutcome outcome,
        string? username,
        string? permissionCode,
        Guid? serverId,
        string reasonCode,
        Guid? correlationId = null)
        => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            action,
            outcome,
            username,
            permissionCode,
            serverId,
            reasonCode,
            correlationId);

    internal static bool TryWriteAudit(
        IRemoteSecurityAuditSink sink,
        RemoteSecurityAuditEvent auditEvent)
    {
        if (!RemoteSecurityAuditEventValidator.IsValid(auditEvent))
        {
            return false;
        }

        try
        {
            return sink.TryWrite(auditEvent);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteRejectedMutationAudit(
        HttpContext context,
        string reasonCode,
        string? permissionCode = null,
        Guid? serverId = null,
        string? username = null)
    {
        var sink = context.RequestServices.GetRequiredService<IRemoteSecurityAuditSink>();
        _ = TryWriteAudit(
            sink,
            CreateAuditEvent(
                RemoteSecurityAuditAction.ServerMutation,
                RemoteSecurityAuditOutcome.Rejected,
                username,
                permissionCode,
                serverId,
                reasonCode));
    }

    private static void AppendSessionCookie(
        HttpContext context,
        IssuedRemoteSession session,
        RemoteControlOptions options)
    {
        context.Response.Cookies.Append(options.SessionCookieName, session.SessionToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = session.ExpiresAtUtc,
            IsEssential = true
        });
    }

    private static void AppendRememberedDeviceCookie(
        HttpContext context,
        IssuedRemoteRememberedDevice issued,
        RemoteControlOptions options)
        => AppendRememberedDeviceCookie(
            context,
            issued.Token,
            issued.Device.AbsoluteExpiresAtUtc,
            options);

    private static void AppendRememberedDeviceCookie(
        HttpContext context,
        RemoteRememberedDeviceRefreshResult refreshed,
        RemoteControlOptions options)
        => AppendRememberedDeviceCookie(
            context,
            refreshed.ReplacementToken!,
            refreshed.Device!.AbsoluteExpiresAtUtc,
            options);

    private static void AppendRememberedDeviceCookie(
        HttpContext context,
        string token,
        DateTimeOffset expiresAtUtc,
        RemoteControlOptions options)
    {
        context.Response.Cookies.Append(options.RememberedDeviceCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expiresAtUtc,
            IsEssential = true
        });
    }

    private static void DeleteRememberedDeviceCookie(
        HttpContext context,
        RemoteControlOptions options)
    {
        context.Response.Cookies.Delete(options.RememberedDeviceCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
    }

    private static bool TryParseConsoleQuery(
        IQueryCollection values,
        RemoteControlOptions options,
        out RemoteConsoleQuery query)
    {
        query = default!;
        var stream = RemoteConsoleStream.All;
        if (values.TryGetValue("stream", out var streamValues) &&
            (streamValues.Count != 1 || !Enum.TryParse(streamValues[0], true, out stream)))
        {
            return false;
        }

        long? after = null;
        if (values.TryGetValue("after", out var afterValues))
        {
            if (afterValues.Count != 1 || !long.TryParse(afterValues[0], out var parsedAfter) || parsedAfter < 0)
            {
                return false;
            }

            after = parsedAfter;
        }

        var limit = options.DefaultConsolePageSize;
        if (values.TryGetValue("limit", out var limitValues) &&
            (limitValues.Count != 1 || !int.TryParse(limitValues[0], out limit) ||
             limit is < 1 || limit > options.MaximumConsolePageSize))
        {
            return false;
        }

        query = new RemoteConsoleQuery(stream, after, limit);
        return true;
    }

    private static RemoteConsolePageDto BoundConsolePage(
        RemoteConsolePageDto page,
        RemoteConsoleQuery query,
        int maximumLineCharacters)
    {
        var source = page.Lines ?? [];
        var wasOverPageLimit = source.Count > query.Limit;
        var lines = source
            .Take(query.Limit)
            .Select(line => line.Text is { Length: var length } && length > maximumLineCharacters
                ? line with { Text = line.Text[..(maximumLineCharacters - 1)] + "…" }
                : line)
            .ToArray();
        var nextCursor = lines.Length == 0 ? page.NextCursor ?? query.After : lines[^1].Sequence;
        return new RemoteConsolePageDto(lines, nextCursor, page.HasMore || wasOverPageLimit);
    }

    internal static RemoteBackupListDto BoundBackupList(RemoteBackupListDto page)
    {
        var source = (page.Backups ?? [])
            .Take(RemoteBackupRestoreContract.MaximumListedBackups + 1)
            .ToArray();
        var backups = source
            .Take(RemoteBackupRestoreContract.MaximumListedBackups)
            .Where(backup => backup is not null &&
                             RemoteInputValidator.TryValidateBackupId(backup.BackupId, out _) &&
                             backup.ArchiveBytes > 0)
            .Select(backup => backup with
            {
                BackupId = backup.BackupId.ToLowerInvariant(),
                DisplayName = CreateSafeBackupDisplayName(backup.DisplayName, backup.CreatedAtUtc),
            })
            .ToArray();
        return new RemoteBackupListDto(
            page.GeneratedAtUtc,
            backups,
            page.HasMore || source.Length > RemoteBackupRestoreContract.MaximumListedBackups);
    }

    internal static RemoteServerAdministrationDto BoundServerAdministration(
        RemoteServerAdministrationDto snapshot)
    {
        var source = (snapshot.Addons ?? [])
            .Take(RemoteServerAdministrationContract.MaximumProcessedAddons + 1)
            .ToArray();
        var addons = new List<RemoteServerAddonDto>(
            RemoteServerAdministrationContract.MaximumListedAddons);
        foreach (var addon in source.Take(RemoteServerAdministrationContract.MaximumProcessedAddons))
        {
            if (addon is null || addons.Count >= RemoteServerAdministrationContract.MaximumListedAddons ||
                !Enum.IsDefined(addon.Kind) || addon.SizeBytes < 0 ||
                !TryCreateSafeAddonFileName(addon.FileName, out var fileName))
            {
                continue;
            }

            addons.Add(addon with { FileName = fileName });
        }

        var java = snapshot.Java ?? new RemoteServerJavaRuntimeDto(
            Configured: false,
            Available: false,
            MajorVersion: null,
            Version: null,
            RemoteJavaRuntimeKind.Unknown,
            "Managed Java",
            RemoteJavaArchitecture.Unknown);
        java = java with
        {
            MajorVersion = java.MajorVersion is >= 1 and <= 99 ? java.MajorVersion : null,
            Version = BoundJavaVersion(java.Version),
            RuntimeKind = Enum.IsDefined(java.RuntimeKind)
                ? java.RuntimeKind
                : RemoteJavaRuntimeKind.Unknown,
            Vendor = BoundJavaVendor(java.Vendor),
            Architecture = Enum.IsDefined(java.Architecture)
                ? java.Architecture
                : RemoteJavaArchitecture.Unknown,
        };

        return new RemoteServerAdministrationDto(
            snapshot.GeneratedAtUtc,
            snapshot.AddonsAvailable,
            addons,
            snapshot.AddonsTruncated ||
            source.Length > RemoteServerAdministrationContract.MaximumListedAddons,
            java);
    }

    private static bool TryCreateSafeAddonFileName(string? value, out string fileName)
    {
        fileName = string.Empty;
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length is < 1 or > RemoteServerAdministrationContract.MaximumAddonFileNameCharacters ||
            candidate is "." or ".." ||
            candidate.Any(character => char.IsControl(character) ||
                                       character is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*'))
        {
            return false;
        }

        fileName = candidate;
        return true;
    }

    private static string? BoundJavaVersion(string? value)
    {
        var candidate = value?.Trim();
        return string.IsNullOrWhiteSpace(candidate) ||
               candidate.Length > RemoteServerAdministrationContract.MaximumJavaMetadataCharacters ||
               !char.IsAsciiDigit(candidate[0]) ||
               candidate.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '+' or '-' or '_'))
            ? null
            : candidate;
    }

    private static string BoundJavaVendor(string? value)
    {
        var candidate = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (candidate.Contains("adoptium", StringComparison.Ordinal) ||
            candidate.Contains("temurin", StringComparison.Ordinal)) return "Eclipse Adoptium";
        if (candidate.Contains("microsoft", StringComparison.Ordinal)) return "Microsoft";
        if (candidate.Contains("oracle", StringComparison.Ordinal)) return "Oracle";
        if (candidate.Contains("amazon", StringComparison.Ordinal) ||
            candidate.Contains("corretto", StringComparison.Ordinal)) return "Amazon Corretto";
        if (candidate.Contains("azul", StringComparison.Ordinal) ||
            candidate.Contains("zulu", StringComparison.Ordinal)) return "Azul";
        if (candidate.Contains("bellsoft", StringComparison.Ordinal) ||
            candidate.Contains("liberica", StringComparison.Ordinal)) return "BellSoft";
        if (candidate.Contains("red hat", StringComparison.Ordinal)) return "Red Hat";
        if (candidate.Contains("sap", StringComparison.Ordinal)) return "SAP";
        if (candidate.Contains("ibm", StringComparison.Ordinal) ||
            candidate.Contains("semeru", StringComparison.Ordinal)) return "IBM Semeru";
        return "Managed Java";
    }

    internal static string CreateSafeBackupDisplayName(string? value, DateTimeOffset createdAtUtc)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length is < 1 or > RemoteBackupRestoreContract.MaximumDisplayNameCharacters ||
            candidate.StartsWith(".", StringComparison.Ordinal) ||
            candidate.Contains("..", StringComparison.Ordinal) ||
            candidate.Any(character => char.IsControl(character) || character is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*'))
        {
            return $"backup-{createdAtUtc.ToUniversalTime():yyyyMMdd-HHmmss}.zip";
        }

        return candidate;
    }

    private static IResult Unauthorized(HttpContext context) => Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: Localize(context, "web.api.signInRequired"));

    private static IResult Forbidden(HttpContext context) => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: Localize(context, "web.api.forbidden"));

    private static IResult LocalizedBadRequest(HttpContext context, string key) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: Localize(context, key));

    private static IResult LocalizedValidationBadRequest(
        HttpContext context,
        string validationMessage,
        int maximumCommandLength = 0)
    {
        var key = validationMessage switch
        {
            "Server identifier is invalid." => "web.api.serverIdInvalid",
            "Backup identifier is invalid." => "web.api.backupIdInvalid",
            "Command is required." => "web.api.commandRequired",
            "Command must contain exactly one text line." => "web.api.commandSingleLine",
            "Player action is invalid." => "web.api.playerActionInvalid",
            "This whitelist action must not include a player name." => "web.api.whitelistPlayerForbidden",
            "Player name is invalid." => "web.api.playerNameInvalid",
            "Reason must be a single line of at most 160 characters." => "web.api.playerReasonSingleLine",
            "A reason is accepted only for kick or ban actions." => "web.api.playerReasonUnsupported",
            _ when maximumCommandLength > 0 && validationMessage.StartsWith(
                "Command must not exceed ",
                StringComparison.Ordinal) => "web.api.commandTooLong",
            _ => "web.api.requestInvalid",
        };
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: key == "web.api.commandTooLong"
                ? Localize(context, key, maximumCommandLength)
                : Localize(context, key));
    }

    internal static string Localize(HttpContext context, string key, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(context);
        var requestedCulture = context.Request.Headers.TryGetValue(
            RemoteControlOptions.CultureHeaderName,
            out var values) && values.Count == 1
                ? values[0]
                : null;
        return ProductLocalizationCatalog.Format(
            ProductLocalizationCatalog.NormalizeCulture(requestedCulture),
            key,
            arguments);
    }

    internal static async Task WriteProblemAsync(HttpContext context, int statusCode, string title)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title,
            status = statusCode
        }).ConfigureAwait(false);
    }
}
