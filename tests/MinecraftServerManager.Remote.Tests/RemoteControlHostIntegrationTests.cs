using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using MinecraftServerManager.Contracts.Localization;
using MinecraftServerManager.Contracts.Security;
using MinecraftServerManager.Remote;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteControlHostIntegrationTests
{
    [Fact]
    public async Task LocalizationEndpoint_ServesVersionedCatalogAndFailsUnknownLanguageToTraditionalChinese()
    {
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new TestCredentialStore());
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "owner@gmail.com");

        foreach (var (path, expectedCulture) in new[]
                 {
                     ("localization/zh-TW.json", "zh-TW"),
                     ("localization/en-US.json", "en-US"),
                     ("localization/not-supported.json", "zh-TW"),
                 })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(expectedCulture, Assert.Single(response.Content.Headers.ContentLanguage));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            Assert.Equal(ProductLocalizationCatalog.SchemaVersion, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(expectedCulture, document.RootElement.GetProperty("Culture").GetString());
            Assert.Equal(
                ProductLocalizationCatalog.Keys.Count,
                document.RootElement.GetProperty("Strings").EnumerateObject().Count());
        }
    }

    [Theory]
    [InlineData("manifest.webmanifest?culture=zh-TW", "zh-TW", "application/manifest+json")]
    [InlineData("manifest.webmanifest?culture=en-US", "en-US", "application/manifest+json")]
    [InlineData("manifest.webmanifest?culture=not-supported", "zh-TW", "application/manifest+json")]
    [InlineData("offline.html?culture=zh-TW", "zh-TW", "text/html")]
    [InlineData("offline.html?culture=en-US", "en-US", "text/html")]
    [InlineData("offline.html?culture=not-supported", "zh-TW", "text/html")]
    public async Task LocalizedPwaAssets_UseRequestedCultureAndSafelyFallback(
        string path,
        string expectedCulture,
        string expectedMediaType)
    {
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new TestCredentialStore());
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "owner@gmail.com");

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedMediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedCulture, Assert.Single(response.Content.Headers.ContentLanguage));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        var body = await response.Content.ReadAsStringAsync();
        if (expectedMediaType == "application/manifest+json")
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal(expectedCulture, document.RootElement.GetProperty("lang").GetString());
            Assert.Equal($"/?culture={expectedCulture}", document.RootElement.GetProperty("start_url").GetString());
        }
        else
        {
            Assert.Contains($"<html lang=\"{expectedCulture}\">", body, StringComparison.Ordinal);
            Assert.Contains("default-src 'none'; style-src 'self'", body, StringComparison.Ordinal);
            Assert.DoesNotContain("https://", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http://", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task PwaAssets_AreServedFromTheSignedAssemblyWithNoStoreSecurityHeaders()
    {
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new TestCredentialStore());
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "owner@gmail.com");

        var assets = new (string Path, string MediaType)[]
        {
            ("manifest.webmanifest", "application/manifest+json"),
            ("service-worker.js", "text/javascript"),
            ("offline.html", "text/html"),
            ("offline.css", "text/css"),
            ("icon-180.png", "image/png"),
            ("icon-192.png", "image/png"),
            ("icon-512.png", "image/png"),
            ("icon-maskable-512.png", "image/png")
        };

        foreach (var asset in assets)
        {
            using var response = await client.GetAsync(asset.Path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(asset.MediaType, response.Content.Headers.ContentType?.MediaType);
            Assert.Contains(
                "no-store",
                response.Headers.CacheControl?.ToString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "worker-src 'self'",
                response.Headers.GetValues("Content-Security-Policy").Single(),
                StringComparison.Ordinal);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0);
            if (asset.Path == "service-worker.js")
            {
                Assert.Equal("/", response.Headers.GetValues("Service-Worker-Allowed").Single());
            }
        }

        using var unknown = await client.GetAsync("icons/not-allowed.png");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task RememberedDevice_RestoresAfterHostRestart_RotatesCookieAndCanBeRevokedBySignOut()
    {
        var credentialStore = new RememberingCredentialStore();
        var firstOptions = TestOptions.Create(port: ReserveLoopbackPort());
        string deviceCookie;

        await using (var firstHost = await RemoteControlHost.StartAsync(
                         new FakeBackend(),
                         firstOptions,
                         credentialStore))
        {
            using var firstClient = CreateClient(
                firstHost.LocalEndpoint,
                firstOptions.PublicOrigin.Authority,
                "owner@gmail.com");
            var login = await LoginAsync(firstClient, firstOptions);
            Assert.True(login.Status.SupportsRememberedDevices);

            using (var missingCsrf = new HttpRequestMessage(
                       HttpMethod.Post,
                       "api/v1/auth/devices/enroll"))
            {
                missingCsrf.Headers.TryAddWithoutValidation(
                    "Origin",
                    firstOptions.PublicOrigin.GetLeftPart(UriPartial.Authority));
                missingCsrf.Headers.TryAddWithoutValidation("Cookie", login.SessionCookie);
                missingCsrf.Content = JsonContent.Create(
                    new RemoteRememberedDeviceEnrollmentRequestDto("iPhone"));
                using var rejected = await firstClient.SendAsync(missingCsrf);
                Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            }

            using var enroll = CreateMutationRequest(
                HttpMethod.Post,
                "api/v1/auth/devices/enroll",
                firstOptions,
                login.SessionCookie,
                login.Status.CsrfToken!,
                new RemoteRememberedDeviceEnrollmentRequestDto("iPhone · 主畫面 App"));
            using var enrolledResponse = await firstClient.SendAsync(enroll);
            Assert.Equal(HttpStatusCode.OK, enrolledResponse.StatusCode);
            var enrolled = await enrolledResponse.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
            Assert.NotNull(enrolled);
            Assert.True(enrolled.Authenticated);
            Assert.True(enrolled.RememberedDevice);
            Assert.True(enrolled.SupportsRememberedDevices);
            Assert.NotNull(enrolled.RememberedDeviceExpiresAtUtc);

            deviceCookie = GetCookie(
                enrolledResponse,
                RemoteControlOptions.DefaultRememberedDeviceCookieName);
            var setCookie = enrolledResponse.Headers.GetValues("Set-Cookie")
                .Single(value => value.StartsWith(
                    RemoteControlOptions.DefaultRememberedDeviceCookieName + "=",
                    StringComparison.Ordinal));
            Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
        }

        var secondOptions = TestOptions.Create(port: ReserveLoopbackPort());
        await using var secondHost = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            secondOptions,
            credentialStore);
        using var secondClient = CreateClient(
            secondHost.LocalEndpoint,
            secondOptions.PublicOrigin.Authority,
            "owner@gmail.com");

        using var bootstrapResponse = await secondClient.GetAsync("api/v1/auth/status");
        var bootstrap = await bootstrapResponse.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.NotNull(bootstrap);
        Assert.False(bootstrap.Authenticated);
        Assert.True(bootstrap.SupportsRememberedDevices);
        Assert.NotNull(bootstrap.AntiforgeryToken);
        var bootstrapCookie = GetCookie(bootstrapResponse, "__Host-MCSV-Auth-CSRF");

        using (var missingAntiforgery = new HttpRequestMessage(
                   HttpMethod.Post,
                   "api/v1/auth/devices/refresh"))
        {
            missingAntiforgery.Headers.TryAddWithoutValidation(
                "Origin",
                secondOptions.PublicOrigin.GetLeftPart(UriPartial.Authority));
            missingAntiforgery.Headers.TryAddWithoutValidation("Cookie", deviceCookie);
            missingAntiforgery.Content = JsonContent.Create(
                new RemoteRememberedDeviceRefreshRequestDto(Guid.NewGuid()));
            using var rejected = await secondClient.SendAsync(missingAntiforgery);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        }

        using var refresh = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/devices/refresh");
        refresh.Headers.TryAddWithoutValidation(
            "Origin",
            secondOptions.PublicOrigin.GetLeftPart(UriPartial.Authority));
        refresh.Headers.TryAddWithoutValidation(RemoteControlOptions.CsrfHeaderName, bootstrap.CsrfToken);
        refresh.Headers.TryAddWithoutValidation("Cookie", $"{bootstrapCookie}; {deviceCookie}");
        refresh.Content = JsonContent.Create(new RemoteRememberedDeviceRefreshRequestDto(Guid.NewGuid()));
        using var refreshedResponse = await secondClient.SendAsync(refresh);
        Assert.Equal(HttpStatusCode.OK, refreshedResponse.StatusCode);
        var refreshed = await refreshedResponse.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.NotNull(refreshed);
        Assert.True(refreshed.Authenticated);
        Assert.True(refreshed.RememberedDevice);
        Assert.True(refreshed.SupportsRememberedDevices);
        Assert.Equal("account1", refreshed.Username);
        var rotatedDeviceCookie = GetCookie(
            refreshedResponse,
            RemoteControlOptions.DefaultRememberedDeviceCookieName);
        Assert.NotEqual(deviceCookie, rotatedDeviceCookie);
        var sessionCookie = GetCookie(
            refreshedResponse,
            RemoteControlOptions.DefaultSessionCookieName);

        using var signOut = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/signout");
        signOut.Headers.TryAddWithoutValidation(
            "Origin",
            secondOptions.PublicOrigin.GetLeftPart(UriPartial.Authority));
        signOut.Headers.TryAddWithoutValidation(
            RemoteControlOptions.CsrfHeaderName,
            bootstrap.AntiforgeryToken);
        signOut.Headers.TryAddWithoutValidation(
            "Cookie",
            $"{bootstrapCookie}; {sessionCookie}; {rotatedDeviceCookie}");
        signOut.Content = JsonContent.Create(new RemoteEmptyMutationRequestDto());
        using var signOutResponse = await secondClient.SendAsync(signOut);
        Assert.Equal(HttpStatusCode.NoContent, signOutResponse.StatusCode);
        Assert.Contains(
            signOutResponse.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                         RemoteControlOptions.DefaultRememberedDeviceCookieName + "=",
                         StringComparison.Ordinal) &&
                     value.Contains("expires=", StringComparison.OrdinalIgnoreCase));

        using var afterSignOutBootstrapResponse = await secondClient.GetAsync("api/v1/auth/status");
        var afterSignOutBootstrap = await afterSignOutBootstrapResponse.Content
            .ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.NotNull(afterSignOutBootstrap);
        var afterSignOutBootstrapCookie = GetCookie(
            afterSignOutBootstrapResponse,
            "__Host-MCSV-Auth-CSRF");
        using var replay = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/devices/refresh");
        replay.Headers.TryAddWithoutValidation(
            "Origin",
            secondOptions.PublicOrigin.GetLeftPart(UriPartial.Authority));
        replay.Headers.TryAddWithoutValidation(
            RemoteControlOptions.CsrfHeaderName,
            afterSignOutBootstrap.CsrfToken);
        replay.Headers.TryAddWithoutValidation(
            "Cookie",
            $"{afterSignOutBootstrapCookie}; {rotatedDeviceCookie}");
        replay.Content = JsonContent.Create(new RemoteRememberedDeviceRefreshRequestDto(Guid.NewGuid()));
        using var replayResponse = await secondClient.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
    }

    [Fact]
    public async Task SignOut_RacingRememberedDeviceRefresh_LeavesNoLiveSessionOrDevice()
    {
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        var store = new BlockingRefreshCredentialStore();
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            store);
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "owner@gmail.com");
        var login = await LoginAsync(client, options);
        var deviceCookie = await EnrollRememberedDeviceAsync(
            client,
            options,
            login.Status,
            login.SessionCookie);

        using var refresh = new HttpRequestMessage(
            HttpMethod.Post,
            "api/v1/auth/devices/refresh");
        refresh.Headers.TryAddWithoutValidation(
            "Origin",
            options.PublicOrigin.GetLeftPart(UriPartial.Authority));
        refresh.Headers.TryAddWithoutValidation(
            RemoteControlOptions.CsrfHeaderName,
            login.Status.AntiforgeryToken);
        refresh.Headers.TryAddWithoutValidation(
            "Cookie",
            $"{login.SessionCookie}; {deviceCookie}");
        refresh.Content = JsonContent.Create(
            new RemoteRememberedDeviceRefreshRequestDto(Guid.NewGuid()));
        var refreshTask = client.SendAsync(refresh);

        try
        {
            await store.RefreshRotated.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var signOut = new HttpRequestMessage(
                HttpMethod.Post,
                "api/v1/auth/signout");
            signOut.Headers.TryAddWithoutValidation(
                "Origin",
                options.PublicOrigin.GetLeftPart(UriPartial.Authority));
            signOut.Headers.TryAddWithoutValidation(
                RemoteControlOptions.CsrfHeaderName,
                login.Status.AntiforgeryToken);
            signOut.Headers.TryAddWithoutValidation(
                "Cookie",
                $"{login.SessionCookie}; {deviceCookie}");
            signOut.Content = JsonContent.Create(new RemoteEmptyMutationRequestDto());
            using var signOutResponse = await client.SendAsync(signOut);

            Assert.Equal(HttpStatusCode.NoContent, signOutResponse.StatusCode);
            AssertCookieDeleted(
                signOutResponse,
                RemoteControlOptions.DefaultSessionCookieName);
            AssertCookieDeleted(
                signOutResponse,
                RemoteControlOptions.DefaultRememberedDeviceCookieName);
            await store.RevokeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            store.ReleaseRefresh();
        }

        using var refreshResponse = await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
        AssertCookieDeleted(
            refreshResponse,
            RemoteControlOptions.DefaultRememberedDeviceCookieName);
        Assert.DoesNotContain(
            refreshResponse.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                         RemoteControlOptions.DefaultSessionCookieName + "=",
                         StringComparison.Ordinal) &&
                     !value.StartsWith(
                         RemoteControlOptions.DefaultSessionCookieName + "=;",
                         StringComparison.Ordinal));
        Assert.Equal(
            RemoteRememberedDeviceStatus.Revoked,
            Assert.Single(store.GetRememberedDevices()).Status);

        using var dashboard = new HttpRequestMessage(HttpMethod.Get, "api/v1/dashboard");
        dashboard.Headers.TryAddWithoutValidation("Cookie", login.SessionCookie);
        using var dashboardResponse = await client.SendAsync(dashboard);
        Assert.Equal(HttpStatusCode.Unauthorized, dashboardResponse.StatusCode);
    }

    [Fact]
    public async Task CredentialLoginAndAuthorizedMutation_EnforceTheCompleteSecurityBoundary()
    {
        var port = ReserveLoopbackPort();
        var options = TestOptions.Create(port: port);
        var backend = new FakeBackend();
        await using var host = await RemoteControlHost.StartAsync(backend, options, new TestCredentialStore());
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(host.LocalEndpoint.Host)));
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, "owner@gmail.com");

        using (var pageResponse = await client.GetAsync(string.Empty))
        {
            Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
            Assert.Equal("text/html", pageResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains("no-store", pageResponse.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Muhun MCSV 遠端控制", await pageResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var statusResponse = await client.GetAsync("api/v1/auth/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        AssertSecurityHeaders(statusResponse);
        Assert.False(statusResponse.Headers.Contains("Access-Control-Allow-Origin"));
        var initialStatus = await statusResponse.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.NotNull(initialStatus);
        Assert.False(initialStatus.Authenticated);
        Assert.Equal("owner@gmail.com", initialStatus.Login);
        Assert.True(initialStatus.CredentialRegistered);
        Assert.NotNull(initialStatus.CsrfToken);
        var bootstrapCookie = GetCookie(statusResponse, "__Host-MCSV-Auth-CSRF");

        // An Origin match without the bootstrap cookie/header is still rejected,
        // and valid credentials remain usable after that rejection.
        using (var missingCsrf = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login"))
        {
            missingCsrf.Headers.TryAddWithoutValidation("Origin", options.PublicOrigin.GetLeftPart(UriPartial.Authority));
            missingCsrf.Headers.TryAddWithoutValidation(RemoteControlOptions.CultureHeaderName, "en-US");
            missingCsrf.Content = JsonContent.Create(
                new RemoteCredentialLoginRequestDto("account1", "12345678"));
            using var rejected = await client.SendAsync(missingCsrf);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.Equal(
                ProductLocalizationCatalog.Format("en-US", "web.api.forbidden"),
                problem.RootElement.GetProperty("title").GetString());
        }

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login");
        loginRequest.Headers.TryAddWithoutValidation("Origin", options.PublicOrigin.GetLeftPart(UriPartial.Authority));
        loginRequest.Headers.TryAddWithoutValidation(RemoteControlOptions.CsrfHeaderName, initialStatus.CsrfToken);
        loginRequest.Headers.TryAddWithoutValidation("Cookie", bootstrapCookie);
        loginRequest.Content = JsonContent.Create(
            new RemoteCredentialLoginRequestDto("account1", "12345678"));

        using var loginResponse = await client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var signedInStatus = await loginResponse.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.NotNull(signedInStatus);
        Assert.True(signedInStatus.Authenticated);
        Assert.Equal("owner@gmail.com", signedInStatus.Login);
        Assert.Equal("account1", signedInStatus.Username);
        Assert.NotNull(signedInStatus.CsrfToken);

        var sessionCookie = GetCookie(loginResponse, RemoteControlOptions.DefaultSessionCookieName);
        var rawSessionSetCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(RemoteControlOptions.DefaultSessionCookieName + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", rawSessionSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", rawSessionSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", rawSessionSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", rawSessionSetCookie, StringComparison.OrdinalIgnoreCase);

        using (var dashboardRequest = new HttpRequestMessage(HttpMethod.Get, "api/v1/dashboard"))
        {
            dashboardRequest.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            using var dashboardResponse = await client.SendAsync(dashboardRequest);
            Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);
        }

        using (var consoleRequest = new HttpRequestMessage(
                   HttpMethod.Get,
                   "api/v1/servers/server-01/console?stream=ordinary&limit=2"))
        {
            consoleRequest.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            using var consoleResponse = await client.SendAsync(consoleRequest);
            Assert.Equal(HttpStatusCode.OK, consoleResponse.StatusCode);
            var page = await consoleResponse.Content.ReadFromJsonAsync<RemoteConsolePageDto>();
            Assert.NotNull(page);
            Assert.Equal(2, page.Lines.Count);
            Assert.All(page.Lines, line => Assert.Equal(options.MaximumConsoleLineCharacters, line.Text.Length));
            Assert.True(page.HasMore);
        }

        using (var missingMutationCsrf = new HttpRequestMessage(
                   HttpMethod.Post,
                   "api/v1/servers/server-01/actions/start"))
        {
            missingMutationCsrf.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            missingMutationCsrf.Headers.TryAddWithoutValidation(
                "Origin",
                options.PublicOrigin.GetLeftPart(UriPartial.Authority));
            missingMutationCsrf.Content = JsonContent.Create(new RemoteEmptyMutationRequestDto());
            using var rejected = await client.SendAsync(missingMutationCsrf);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            Assert.Equal(0, backend.StartCount);
        }

        var startIdempotencyKey = Guid.NewGuid().ToString("D");
        using (var startRequest = CreateMutationRequest(
                   HttpMethod.Post,
                   "api/v1/servers/server-01/actions/start",
                   options,
                   sessionCookie,
                    signedInStatus.CsrfToken!,
                   new RemoteEmptyMutationRequestDto(),
                   startIdempotencyKey))
        {
            using var startResponse = await client.SendAsync(startRequest);
            Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
            Assert.Equal(1, backend.StartCount);
        }

        using (var replayRequest = CreateMutationRequest(
                   HttpMethod.Post,
                   "api/v1/servers/server-01/actions/start",
                   options,
                   sessionCookie,
                    signedInStatus.CsrfToken!,
                   new RemoteEmptyMutationRequestDto(),
                   startIdempotencyKey))
        {
            using var replayResponse = await client.SendAsync(replayRequest);
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
            Assert.Equal(1, backend.StartCount);
        }

        using (var conflictingRequest = CreateMutationRequest(
                   HttpMethod.Post,
                   "api/v1/servers/server-01/console/commands",
                   options,
                   sessionCookie,
                    signedInStatus.CsrfToken!,
                   new RemoteCommandRequestDto("list"),
                   startIdempotencyKey))
        {
            using var conflictResponse = await client.SendAsync(conflictingRequest);
            Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
            Assert.Equal(0, backend.CommandCount);
        }

        using (var missingIdempotencyKey = new HttpRequestMessage(
                   HttpMethod.Post,
                   "api/v1/servers/server-01/actions/stop"))
        {
            missingIdempotencyKey.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            missingIdempotencyKey.Headers.TryAddWithoutValidation(
                "Origin",
                options.PublicOrigin.GetLeftPart(UriPartial.Authority));
            missingIdempotencyKey.Headers.TryAddWithoutValidation(
                RemoteControlOptions.CsrfHeaderName,
                signedInStatus.CsrfToken!);
            missingIdempotencyKey.Content = JsonContent.Create(new RemoteEmptyMutationRequestDto());
            using var response = await client.SendAsync(missingIdempotencyKey);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var injectedCommand = CreateMutationRequest(
                   HttpMethod.Post,
                   "api/v1/servers/server-01/console/commands",
                   options,
                   sessionCookie,
                    signedInStatus.CsrfToken!,
                   new RemoteCommandRequestDto("say hello\nstop")))
        {
            using var rejected = await client.SendAsync(injectedCommand);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(0, backend.CommandCount);
        }

        using (var unexpectedPathField = CreateMutationRequest(
                   HttpMethod.Post,
                   "api/v1/servers/server-01/console/commands",
                   options,
                   sessionCookie,
                    signedInStatus.CsrfToken!,
                   new { command = "list", path = "C:\\private\\server" }))
        {
            using var rejected = await client.SendAsync(unexpectedPathField);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(0, backend.CommandCount);
        }

        using (var removedIdleWakeRoute = new HttpRequestMessage(HttpMethod.Post, "api/v1/servers/server-01/wake"))
        {
            removedIdleWakeRoute.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            using var response = await client.SendAsync(removedIdleWakeRoute);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task RevokeAllSessions_InvalidatesExistingSessionButKeepsCredentialLogin()
    {
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new TestCredentialStore());
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, "owner@gmail.com");
        var (_, sessionCookie) = await LoginAsync(client, options);
        host.RevokeAllSessions();

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/dashboard");
        request.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var (signedInAgain, _) = await LoginAsync(client, options);
        Assert.True(signedInAgain.Authenticated);
    }

    [Fact]
    public async Task EnterFailClosedMode_PermanentlyRejectsStatusLoginAndAuthenticatedRequests()
    {
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new TestCredentialStore());
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "owner@gmail.com");
        var (_, sessionCookie) = await LoginAsync(client, options);

        host.EnterFailClosedMode();

        using (var status = await client.GetAsync("api/v1/auth/status"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status.StatusCode);
            Assert.Contains(
                "no-store",
                status.Headers.CacheControl?.ToString() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }

        using (var login = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login"))
        {
            login.Content = JsonContent.Create(
                new RemoteCredentialLoginRequestDto("account1", "12345678"));
            using var response = await client.SendAsync(login);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        using (var dashboard = new HttpRequestMessage(HttpMethod.Get, "api/v1/dashboard"))
        {
            dashboard.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            using var response = await client.SendAsync(dashboard);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }

    [Fact]
    public async Task SignOut_DeletesCookieInvalidatesSessionAndAllowsFreshLogin()
    {
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new TestCredentialStore());
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, "owner@gmail.com");
        var (signedIn, sessionCookie) = await LoginAsync(client, options);

        using (var signOut = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/signout"))
        {
            signOut.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            signOut.Headers.TryAddWithoutValidation(
                "Origin",
                options.PublicOrigin.GetLeftPart(UriPartial.Authority));
            signOut.Headers.TryAddWithoutValidation(
                RemoteControlOptions.CsrfHeaderName,
                signedIn.AntiforgeryToken);
            signOut.Content = JsonContent.Create(new RemoteEmptyMutationRequestDto());
            using var response = await client.SendAsync(signOut);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var deletion = Assert.Single(
                response.Headers.GetValues("Set-Cookie"),
                value => value.StartsWith(
                    RemoteControlOptions.DefaultSessionCookieName + "=",
                    StringComparison.Ordinal));
            Assert.Contains("expires=", deletion, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/", deletion, StringComparison.OrdinalIgnoreCase);
        }

        using (var dashboard = new HttpRequestMessage(HttpMethod.Get, "api/v1/dashboard"))
        {
            dashboard.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            using var rejected = await client.SendAsync(dashboard);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        var (signedInAgain, _) = await LoginAsync(client, options);
        Assert.True(signedInAgain.Authenticated);
    }

    [Fact]
    public async Task ExpiredSession_IsRejectedAndAuthStatusReturnsFreshBootstrapToken()
    {
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 22, 6, 0, 0, TimeSpan.Zero));
        var options = TestOptions.Create(
            port: ReserveLoopbackPort(),
            sessionLifetime: TimeSpan.FromMinutes(15));
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new TestCredentialStore(),
            time);
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, "owner@gmail.com");
        var (_, sessionCookie) = await LoginAsync(client, options);
        time.Advance(TimeSpan.FromMinutes(15));

        using (var dashboard = new HttpRequestMessage(HttpMethod.Get, "api/v1/dashboard"))
        {
            dashboard.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            using var rejected = await client.SendAsync(dashboard);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "api/v1/auth/status");
        statusRequest.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
        using var statusResponse = await client.SendAsync(statusRequest);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await statusResponse.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.NotNull(status);
        Assert.False(status.Authenticated);
        Assert.NotNull(status.CsrfToken);
    }

    [Fact]
    public async Task SignOut_WithExpiredSession_StillRevokesRememberedDeviceAndDeletesBothCookies()
    {
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 22, 7, 0, 0, TimeSpan.Zero));
        var options = TestOptions.Create(
            port: ReserveLoopbackPort(),
            sessionLifetime: TimeSpan.FromMinutes(15));
        var store = new RememberingCredentialStore();
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            store,
            time);
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "owner@gmail.com");
        var login = await LoginAsync(client, options);
        var deviceCookie = await EnrollRememberedDeviceAsync(
            client,
            options,
            login.Status,
            login.SessionCookie);
        time.Advance(TimeSpan.FromMinutes(15));

        using var signOut = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/signout");
        signOut.Headers.TryAddWithoutValidation(
            "Origin",
            options.PublicOrigin.GetLeftPart(UriPartial.Authority));
        signOut.Headers.TryAddWithoutValidation(
            RemoteControlOptions.CsrfHeaderName,
            login.Status.AntiforgeryToken);
        signOut.Headers.TryAddWithoutValidation(
            "Cookie",
            $"{login.SessionCookie}; {deviceCookie}");
        signOut.Content = JsonContent.Create(new RemoteEmptyMutationRequestDto());
        using var signOutResponse = await client.SendAsync(signOut);

        Assert.Equal(HttpStatusCode.NoContent, signOutResponse.StatusCode);
        AssertCookieDeleted(signOutResponse, RemoteControlOptions.DefaultSessionCookieName);
        AssertCookieDeleted(
            signOutResponse,
            RemoteControlOptions.DefaultRememberedDeviceCookieName);
        Assert.Equal(
            RemoteRememberedDeviceStatus.Revoked,
            Assert.Single(store.GetRememberedDevices()).Status);

        using var replay = new HttpRequestMessage(
            HttpMethod.Post,
            "api/v1/auth/devices/refresh");
        replay.Headers.TryAddWithoutValidation(
            "Origin",
            options.PublicOrigin.GetLeftPart(UriPartial.Authority));
        replay.Headers.TryAddWithoutValidation(
            RemoteControlOptions.CsrfHeaderName,
            login.Status.AntiforgeryToken);
        replay.Headers.TryAddWithoutValidation(
            "Cookie",
            $"{login.SessionCookie}; {deviceCookie}");
        replay.Content = JsonContent.Create(
            new RemoteRememberedDeviceRefreshRequestDto(Guid.NewGuid()));
        using var replayResponse = await client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
    }

    [Fact]
    public async Task SignOut_WhenDeviceStoreWriteFails_RetainsCookiesAndSessionForRetry()
    {
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        var store = new FailOnceRevocationCredentialStore();
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            store);
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "owner@gmail.com");
        var login = await LoginAsync(client, options);
        var deviceCookie = await EnrollRememberedDeviceAsync(
            client,
            options,
            login.Status,
            login.SessionCookie);

        using (var firstSignOut = CreateSignOutRequest(
                   options,
                   login.Status.AntiforgeryToken!,
                   $"{login.SessionCookie}; {deviceCookie}"))
        using (var failed = await client.SendAsync(firstSignOut))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            if (failed.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                Assert.DoesNotContain(
                    setCookies,
                    value => value.StartsWith(
                        RemoteControlOptions.DefaultSessionCookieName + "=",
                        StringComparison.Ordinal));
                Assert.DoesNotContain(
                    setCookies,
                    value => value.StartsWith(
                        RemoteControlOptions.DefaultRememberedDeviceCookieName + "=",
                        StringComparison.Ordinal));
            }
        }

        using (var dashboard = new HttpRequestMessage(HttpMethod.Get, "api/v1/dashboard"))
        {
            dashboard.Headers.TryAddWithoutValidation("Cookie", login.SessionCookie);
            using var stillAuthenticated = await client.SendAsync(dashboard);
            Assert.Equal(HttpStatusCode.OK, stillAuthenticated.StatusCode);
        }

        using var retry = CreateSignOutRequest(
            options,
            login.Status.AntiforgeryToken!,
            $"{login.SessionCookie}; {deviceCookie}");
        using var retried = await client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
        AssertCookieDeleted(retried, RemoteControlOptions.DefaultSessionCookieName);
        AssertCookieDeleted(
            retried,
            RemoteControlOptions.DefaultRememberedDeviceCookieName);
        Assert.Equal(2, store.RevokeAttempts);
        Assert.Equal(
            RemoteRememberedDeviceStatus.Revoked,
            Assert.Single(store.GetRememberedDevices()).Status);
    }

    [Fact]
    public async Task NoApprovedCredential_IsReportedAndRemovedPairingRouteStaysNotFound()
    {
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        await using var host = await RemoteControlHost.StartAsync(new FakeBackend(), options);
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, "owner@gmail.com");

        using (var response = await client.GetAsync("api/v1/auth/status"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var status = await response.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
            Assert.NotNull(status);
            Assert.False(status.Authenticated);
            Assert.False(status.CredentialRegistered);
        }

        using var removedPairing = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/pair")
        {
            Content = JsonContent.Create(new { code = "12345678" })
        };
        using var removedResponse = await client.SendAsync(removedPairing);
        Assert.Equal(HttpStatusCode.NotFound, removedResponse.StatusCode);
    }

    [Fact]
    public async Task CredentialLockout_HasSameRemoteShapeAsInvalidCredentials()
    {
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new LockedCredentialStore());
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, "owner@gmail.com");
        using var statusResponse = await client.GetAsync("api/v1/auth/status");
        var status = await statusResponse.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.NotNull(status);
        var bootstrapCookie = GetCookie(statusResponse, "__Host-MCSV-Auth-CSRF");

        using var login = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login");
        login.Headers.TryAddWithoutValidation(
            "Origin",
            options.PublicOrigin.GetLeftPart(UriPartial.Authority));
        login.Headers.TryAddWithoutValidation(RemoteControlOptions.CsrfHeaderName, status.CsrfToken);
        login.Headers.TryAddWithoutValidation("Cookie", bootstrapCookie);
        login.Content = JsonContent.Create(new RemoteCredentialLoginRequestDto("account1", "12345678"));
        using var response = await client.SendAsync(login);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.RetryAfter);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("帳號或密碼不正確", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingOrSimilarTailscaleIdentity_IsRejectedBeforeApiData()
    {
        var port = ReserveLoopbackPort();
        var options = TestOptions.Create(port: port);
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new TestCredentialStore());

        using (var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, login: null))
        using (var response = await client.GetAsync("api/v1/auth/status"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(
                "no-store",
                response.Headers.CacheControl?.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }

        using (var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, login: null))
        using (var response = await client.GetAsync(string.Empty))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var client = CreateClient(
                   host.LocalEndpoint,
                   options.PublicOrigin.Authority,
                   "owner@gmail.com.attacker.example"))
        using (var response = await client.GetAsync("api/v1/auth/status"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task UnapprovedTailnetTraffic_CannotConsumeApprovedLoginGlobalQuota()
    {
        var options = TestOptions.Create(
            port: ReserveLoopbackPort(),
            globalRequestsPerMinute: 30);
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new TestCredentialStore());
        using var unapproved = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "other@gmail.com");

        for (var request = 0; request < 30; request++)
        {
            using var response = await unapproved.GetAsync("api/v1/auth/status");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var limited = await unapproved.GetAsync("api/v1/auth/status"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Contains(
                "no-store",
                limited.Headers.CacheControl?.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }

        using var approved = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "OWNER@GMAIL.COM");
        using var approvedResponse = await approved.GetAsync("api/v1/auth/status");
        Assert.Equal(HttpStatusCode.OK, approvedResponse.StatusCode);
    }

    [Fact]
    public async Task LoginAttempts_AreRateLimitedWithoutAQueue()
    {
        var port = ReserveLoopbackPort();
        var options = TestOptions.Create(port: port, loginAttemptsPerMinute: 1);
        var audit = new RecordingAuditSink();
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new TestCredentialStore(),
            securityAuditSink: audit);
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, "owner@gmail.com");

        static HttpRequestMessage CreateAttempt(RemoteControlOptions settings)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login");
            request.Headers.TryAddWithoutValidation("Origin", settings.PublicOrigin.GetLeftPart(UriPartial.Authority));
            request.Content = JsonContent.Create(
                new RemoteCredentialLoginRequestDto("account1", "00000000"));
            return request;
        }

        using (var firstRequest = CreateAttempt(options))
        using (var firstResponse = await client.SendAsync(firstRequest))
        {
            Assert.Equal(HttpStatusCode.Forbidden, firstResponse.StatusCode);
        }

        using (var secondRequest = CreateAttempt(options))
        using (var secondResponse = await client.SendAsync(secondRequest))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
        }
        Assert.Contains(
            audit.Events,
            auditEvent => auditEvent.Action == RemoteSecurityAuditAction.RateLimitRejected &&
                          auditEvent.Outcome == RemoteSecurityAuditOutcome.Rejected);
    }

    [Fact]
    public async Task QuickTunnel_LoginRateLimit_IsPartitionedByTrustedCloudflareClientAddress()
    {
        var options = CreateQuickTunnelOptions(
            port: ReserveLoopbackPort(),
            loginAttemptsPerMinute: 1);
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new PermissionCredentialStore(RemoteWebPermission.All));
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, login: null);

        using (var firstClientAttempt = CreateQuickTunnelLoginAttempt(options, "203.0.113.10"))
        using (var firstClientResponse = await client.SendAsync(firstClientAttempt))
        {
            Assert.Equal(HttpStatusCode.Forbidden, firstClientResponse.StatusCode);
        }

        using (var repeatedClientAttempt = CreateQuickTunnelLoginAttempt(options, "203.0.113.10"))
        using (var repeatedClientResponse = await client.SendAsync(repeatedClientAttempt))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, repeatedClientResponse.StatusCode);
        }

        using (var otherClientAttempt = CreateQuickTunnelLoginAttempt(options, "198.51.100.20"))
        using (var otherClientResponse = await client.SendAsync(otherClientAttempt))
        {
            Assert.Equal(HttpStatusCode.Forbidden, otherClientResponse.StatusCode);
        }
    }

    [Fact]
    public async Task QuickTunnel_RefreshRateLimit_IsPartitionedByTrustedCloudflareClientAddress()
    {
        var options = CreateQuickTunnelOptions(
            port: ReserveLoopbackPort(),
            loginAttemptsPerMinute: 1);
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new PermissionCredentialStore(RemoteWebPermission.All));
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, login: null);

        using (var firstClientAttempt = CreateQuickTunnelRefreshAttempt(options, "203.0.113.10"))
        using (var firstClientResponse = await client.SendAsync(firstClientAttempt))
        {
            Assert.Equal(HttpStatusCode.Forbidden, firstClientResponse.StatusCode);
        }

        using (var repeatedClientAttempt = CreateQuickTunnelRefreshAttempt(options, "203.0.113.10"))
        using (var repeatedClientResponse = await client.SendAsync(repeatedClientAttempt))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, repeatedClientResponse.StatusCode);
        }

        using (var otherClientAttempt = CreateQuickTunnelRefreshAttempt(options, "198.51.100.20"))
        using (var otherClientResponse = await client.SendAsync(otherClientAttempt))
        {
            Assert.Equal(HttpStatusCode.Forbidden, otherClientResponse.StatusCode);
        }
    }

    [Fact]
    public async Task QuickTunnel_InvalidMissingAndAmbiguousClientHeadersShareFailSafePartition()
    {
        var options = CreateQuickTunnelOptions(
            port: ReserveLoopbackPort(),
            loginAttemptsPerMinute: 1);
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new PermissionCredentialStore(RemoteWebPermission.All));
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, login: null);

        using (var invalidAttempt = CreateQuickTunnelLoginAttempt(options, "not-an-ip"))
        using (var invalidResponse = await client.SendAsync(invalidAttempt))
        {
            Assert.Equal(HttpStatusCode.Forbidden, invalidResponse.StatusCode);
        }

        using (var missingAttempt = CreateQuickTunnelLoginAttempt(options))
        using (var missingResponse = await client.SendAsync(missingAttempt))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, missingResponse.StatusCode);
        }

        using (var ambiguousAttempt = CreateQuickTunnelLoginAttempt(
                   options,
                   "203.0.113.10",
                   "198.51.100.20"))
        using (var ambiguousResponse = await client.SendAsync(ambiguousAttempt))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, ambiguousResponse.StatusCode);
        }
    }

    [Fact]
    public async Task QuickTunnel_DistinctClientPartitionsRemainBoundedByAggregateGlobalLimit()
    {
        var options = CreateQuickTunnelOptions(
            port: ReserveLoopbackPort(),
            globalRequestsPerMinute: 30);
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            new PermissionCredentialStore(RemoteWebPermission.All));
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, login: null);

        for (var requestNumber = 1; requestNumber <= 30; requestNumber++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/auth/status");
            request.Headers.TryAddWithoutValidation(
                RemoteControlOptions.CloudflareConnectingIpHeaderName,
                $"198.51.100.{requestNumber}");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var limitedRequest = new HttpRequestMessage(HttpMethod.Get, "api/v1/auth/status");
        limitedRequest.Headers.TryAddWithoutValidation(
            RemoteControlOptions.CloudflareConnectingIpHeaderName,
            "203.0.113.200");
        using var limitedResponse = await client.SendAsync(limitedRequest);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
    }

    [Fact]
    public async Task ClientAbort_DoesNotCancelRestart_AndSameKeyReplaysCompletedResult()
    {
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        var backend = new BlockingRestartBackend();
        await using var host = await RemoteControlHost.StartAsync(
            backend,
            options,
            new TestCredentialStore());
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, "owner@gmail.com");
        var (paired, sessionCookie) = await LoginAsync(client, options);
        var key = Guid.NewGuid().ToString("D");

        using var disconnectedRequest = CreateMutationRequest(
            HttpMethod.Post,
            "api/v1/servers/server-01/actions/restart",
            options,
            sessionCookie,
            paired.CsrfToken!,
            new RemoteEmptyMutationRequestDto(),
            key);
        using var disconnected = new CancellationTokenSource();
        var disconnectedWait = client.SendAsync(disconnectedRequest, disconnected.Token);
        await backend.RestartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        disconnected.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnectedWait);
        Assert.False(backend.ObservedCancellationToken.IsCancellationRequested);
        Assert.Equal(1, backend.RestartCount);

        backend.CompleteRestart();
        using var replayRequest = CreateMutationRequest(
            HttpMethod.Post,
            "api/v1/servers/server-01/actions/restart",
            options,
            sessionCookie,
            paired.CsrfToken!,
            new RemoteEmptyMutationRequestDto(),
            key);
        using var replayResponse = await client.SendAsync(replayRequest);

        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var result = await replayResponse.Content.ReadFromJsonAsync<RemoteOperationResultDto>();
        Assert.Equal("restart completed", result?.Message);
        Assert.Equal(1, backend.RestartCount);
    }

    [Fact]
    public async Task HostStop_CancelsAndDrainsMutationDetachedByClientAbort()
    {
        using var applicationStopping = new CancellationTokenSource();
        var options = TestOptions.Create(
            port: ReserveLoopbackPort(),
            operationCancellationToken: applicationStopping.Token);
        var backend = new ShutdownDrainBackend();
        await using var host = await RemoteControlHost.StartAsync(
            backend,
            options,
            new TestCredentialStore());
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, "owner@gmail.com");
        var (paired, sessionCookie) = await LoginAsync(client, options);

        using var restartRequest = CreateMutationRequest(
            HttpMethod.Post,
            "api/v1/servers/server-01/actions/restart",
            options,
            sessionCookie,
            paired.CsrfToken!,
            new RemoteEmptyMutationRequestDto());
        using var disconnected = new CancellationTokenSource();
        var disconnectedWait = client.SendAsync(restartRequest, disconnected.Token);
        await backend.RestartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        disconnected.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnectedWait);

        applicationStopping.Cancel();
        var stop = host.StopAsync();
        await backend.ApplicationStoppingObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stop.IsCompleted);

        backend.AllowCleanupToFinish();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(backend.CleanupCompleted);
    }

    [Fact]
    public async Task HostStopWithoutApplicationShutdown_DoesNotCancelAcceptedMutation()
    {
        using var applicationStopping = new CancellationTokenSource();
        var options = TestOptions.Create(
            port: ReserveLoopbackPort(),
            operationCancellationToken: applicationStopping.Token,
            mutationShutdownDrainTimeout: TimeSpan.FromMilliseconds(100));
        var backend = new BlockingRestartBackend();
        await using var host = await RemoteControlHost.StartAsync(
            backend,
            options,
            new TestCredentialStore());
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, "owner@gmail.com");
        var (paired, sessionCookie) = await LoginAsync(client, options);

        using var restartRequest = CreateMutationRequest(
            HttpMethod.Post,
            "api/v1/servers/server-01/actions/restart",
            options,
            sessionCookie,
            paired.CsrfToken!,
            new RemoteEmptyMutationRequestDto());
        using var disconnected = new CancellationTokenSource();
        var disconnectedWait = client.SendAsync(restartRequest, disconnected.Token);
        await backend.RestartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        disconnected.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnectedWait);

        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(applicationStopping.IsCancellationRequested);
        Assert.False(backend.ObservedCancellationToken.IsCancellationRequested);

        backend.CompleteRestart();
        await backend.RestartFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, backend.RestartCount);
    }

    [Fact]
    public async Task QuickTunnel_ReportsRememberedDevicesUnsupported_AndRejectsEnrollAndRefresh()
    {
        var options = CreateQuickTunnelOptions(ReserveLoopbackPort());
        var store = new QuickRememberedCredentialStore();
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            store);
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "attacker-controlled@example.com");

        using (var statusResponse = await client.GetAsync("api/v1/auth/status"))
        {
            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            var anonymous = await statusResponse.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
            Assert.NotNull(anonymous);
            Assert.True(anonymous.CredentialRegistered);
            Assert.False(anonymous.SupportsRememberedDevices);
            Assert.NotNull(anonymous.AntiforgeryToken);
        }

        var login = await LoginAsync(client, options);
        Assert.False(login.Status.SupportsRememberedDevices);

        using (var enroll = CreateMutationRequest(
                   HttpMethod.Post,
                   "api/v1/auth/devices/enroll",
                   options,
                   login.SessionCookie,
                   login.Status.CsrfToken!,
                   new RemoteRememberedDeviceEnrollmentRequestDto("Quick Tunnel phone")))
        using (var enrollResponse = await client.SendAsync(enroll))
        {
            Assert.Equal(HttpStatusCode.Conflict, enrollResponse.StatusCode);
        }

        using var refresh = new HttpRequestMessage(
            HttpMethod.Post,
            "api/v1/auth/devices/refresh");
        refresh.Headers.TryAddWithoutValidation(
            "Origin",
            options.PublicOrigin.GetLeftPart(UriPartial.Authority));
        refresh.Headers.TryAddWithoutValidation(
            RemoteControlOptions.CsrfHeaderName,
            login.Status.AntiforgeryToken);
        refresh.Headers.TryAddWithoutValidation(
            "Cookie",
            $"{login.SessionCookie}; {options.RememberedDeviceCookieName}=unsupported-device-token");
        refresh.Content = JsonContent.Create(
            new RemoteRememberedDeviceRefreshRequestDto(Guid.NewGuid()));
        using var refreshResponse = await client.SendAsync(refresh);

        Assert.Equal(HttpStatusCode.Conflict, refreshResponse.StatusCode);
        AssertCookieDeleted(refreshResponse, options.RememberedDeviceCookieName);
        Assert.Equal(0, store.IssueCalls);
        Assert.Equal(0, store.RefreshCalls);
    }

    [Fact]
    public async Task NamedTunnel_IgnoresIngressIdentityAndSupportsRememberedDeviceEnrollment()
    {
        var options = new RemoteControlOptions
        {
            Port = ReserveLoopbackPort(),
            PublicOrigin = new Uri("https://mcsv.example.com/"),
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.CloudflareNamedTunnel
        };
        var store = new RememberingCredentialStore(
            RemoteControlOptions.PublicTunnelCredentialSubject);
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            store);
        // Public Cloudflare headers are attacker-controlled unless Cloudflare Access is a
        // separately enforced boundary. Named Tunnel authentication must use only MCSV's
        // fixed local credential namespace.
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "attacker@gmail.com");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Cf-Access-Authenticated-User-Email",
            "forged@example.com");

        var login = await LoginAsync(client, options);

        Assert.Equal(string.Empty, login.Status.Login);
        Assert.True(login.Status.SupportsRememberedDevices);
        var deviceCookie = await EnrollRememberedDeviceAsync(
            client,
            options,
            login.Status,
            login.SessionCookie);
        Assert.StartsWith(
            options.RememberedDeviceCookieName + "=",
            deviceCookie,
            StringComparison.Ordinal);
        Assert.Single(store.GetRememberedDevices());
    }

    [Fact]
    public async Task Funnel_IgnoresForgedIdentityHeadersAndSupportsRememberedDeviceEnrollment()
    {
        var options = new RemoteControlOptions
        {
            Port = ReserveLoopbackPort(),
            PublicOrigin = new Uri("https://manager-node.example.ts.net/"),
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.TailscaleFunnel
        };
        var store = new RememberingCredentialStore(
            RemoteControlOptions.PublicTunnelCredentialSubject);
        await using var host = await RemoteControlHost.StartAsync(
            new FakeBackend(),
            options,
            store);
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "forged@gmail.com");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            RemoteControlOptions.CloudflareConnectingIpHeaderName,
            "203.0.113.9");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Cf-Access-Authenticated-User-Email",
            "forged@example.com");

        var login = await LoginAsync(client, options);

        Assert.Equal(string.Empty, login.Status.Login);
        Assert.True(login.Status.SupportsRememberedDevices);
        var deviceCookie = await EnrollRememberedDeviceAsync(
            client,
            options,
            login.Status,
            login.SessionCookie);
        Assert.StartsWith(
            options.RememberedDeviceCookieName + "=",
            deviceCookie,
            StringComparison.Ordinal);
        Assert.Single(store.GetRememberedDevices());
    }

    [Fact]
    public async Task QuickTunnel_IgnoresIdentityHeaders_AndEnforcesEveryMutationPermission()
    {
        var options = new RemoteControlOptions
        {
            Port = ReserveLoopbackPort(),
            PublicOrigin = new Uri("https://quiet-lake-abc123.trycloudflare.com/"),
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.CloudflareQuickTunnel
        };
        var backend = new FakeBackend();
        await using var host = await RemoteControlHost.StartAsync(
            backend,
            options,
            new PermissionCredentialStore(RemoteWebPermission.StartServer));
        // This header is attacker-controlled on a public Quick Tunnel and must be ignored.
        using var client = CreateClient(host.LocalEndpoint, options.PublicOrigin.Authority, "attacker@gmail.com");
        var login = await LoginAsync(client, options);

        Assert.Equal(string.Empty, login.Status.Login);
        Assert.True(login.Status.Permissions?.CanStartServer);
        Assert.False(login.Status.Permissions?.CanStopServer);
        Assert.False(login.Status.Permissions?.CanRestartServer);
        Assert.False(login.Status.Permissions?.CanSendConsoleCommand);
        Assert.False(login.Status.Permissions?.CanManagePlayers);
        Assert.False(login.Status.Permissions?.CanCreateBackup);

        using (var dashboard = new HttpRequestMessage(HttpMethod.Get, "api/v1/dashboard"))
        {
            dashboard.Headers.TryAddWithoutValidation("Cookie", login.SessionCookie);
            using var response = await client.SendAsync(dashboard);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var request = CreateMutationRequest(
                   HttpMethod.Post,
                   "api/v1/servers/server-01/actions/start",
                   options,
                   login.SessionCookie,
                   login.Status.CsrfToken!,
                   new RemoteEmptyMutationRequestDto()))
        {
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(1, backend.StartCount);

        var forbiddenMutations = new (string Path, object Body)[]
        {
            ("api/v1/servers/server-01/actions/stop", new RemoteEmptyMutationRequestDto()),
            ("api/v1/servers/server-01/actions/restart", new RemoteEmptyMutationRequestDto()),
            ("api/v1/servers/server-01/console/commands", new RemoteCommandRequestDto("say denied")),
            ("api/v1/servers/server-01/player-actions", new RemotePlayerActionRequestDto("Steve", RemotePlayerActionKind.Kick, null)),
            ("api/v1/servers/server-01/backups", new RemoteEmptyMutationRequestDto())
        };
        foreach (var mutation in forbiddenMutations)
        {
            using var request = CreateMutationRequest(
                HttpMethod.Post,
                mutation.Path,
                options,
                login.SessionCookie,
                login.Status.CsrfToken!,
                mutation.Body);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(0, backend.CommandCount);
    }

    [Fact]
    public async Task FormalAuthorization_FiltersDashboardEnforcesServerScopeAndAuditsMutation()
    {
        var serverId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var hiddenServerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        var backend = new ScopedBackend(serverId, hiddenServerId);
        var credentials = new FormalPermissionCredentialStore(
            "stamp-1",
            [
                new ProductPermissionGrant(
                    ProductPermissionCodes.ServerRead,
                    ProductPermissionScope.ForServer(serverId)),
                new ProductPermissionGrant(
                    ProductPermissionCodes.ServerStart,
                    ProductPermissionScope.ForServer(serverId))
            ]);
        var audit = new RecordingAuditSink();
        await using var host = await RemoteControlHost.StartAsync(
            backend,
            options,
            credentials,
            securityAuditSink: audit);
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "owner@gmail.com");
        var login = await LoginAsync(client, options);

        Assert.NotNull(login.Status.PermissionGrants);
        Assert.Contains(
            login.Status.PermissionGrants,
            grant => grant.PermissionCode == ProductPermissionCodes.ServerStart &&
                     grant.Scope == RemotePermissionScopeKind.Server &&
                     grant.ServerId == serverId);

        using (var dashboard = new HttpRequestMessage(HttpMethod.Get, "api/v1/dashboard"))
        {
            dashboard.Headers.TryAddWithoutValidation("Cookie", login.SessionCookie);
            using var response = await client.SendAsync(dashboard);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<RemoteDashboardDto>();
            Assert.NotNull(payload);
            Assert.Collection(payload.Servers, server => Assert.Equal(serverId.ToString("N"), server.Id));
        }

        using (var start = CreateMutationRequest(
                   HttpMethod.Post,
                   $"api/v1/servers/{serverId}/actions/start",
                   options,
                   login.SessionCookie,
                   login.Status.CsrfToken!,
                   new RemoteEmptyMutationRequestDto()))
        {
            using var response = await client.SendAsync(start);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(1, backend.StartCount);
        Assert.Contains(
            audit.Events,
            auditEvent => auditEvent.Action == RemoteSecurityAuditAction.ServerMutation &&
                          auditEvent.Outcome == RemoteSecurityAuditOutcome.Accepted &&
                          auditEvent.PermissionCode == ProductPermissionCodes.ServerStart &&
                          auditEvent.ServerId == serverId);

        using (var forbidden = CreateMutationRequest(
                   HttpMethod.Post,
                   $"api/v1/servers/{hiddenServerId}/actions/start",
                   options,
                   login.SessionCookie,
                   login.Status.CsrfToken!,
                   new RemoteEmptyMutationRequestDto()))
        {
            using var response = await client.SendAsync(forbidden);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(1, backend.StartCount);
    }

    [Fact]
    public async Task ServerAdministration_RechecksServerReadScopeOnEveryRequest()
    {
        var serverId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var hiddenServerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        var backend = new ScopedBackend(serverId, hiddenServerId);
        var credentials = new FormalPermissionCredentialStore(
            "stamp-administration-scope",
            [new ProductPermissionGrant(
                ProductPermissionCodes.ServerRead,
                ProductPermissionScope.ForServer(serverId))]);
        await using var host = await RemoteControlHost.StartAsync(backend, options, credentials);
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "owner@gmail.com");
        var login = await LoginAsync(client, options);

        using (var visible = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"api/v1/servers/{serverId:D}/administration"))
        {
            visible.Headers.TryAddWithoutValidation("Cookie", login.SessionCookie);
            using var response = await client.SendAsync(visible);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<RemoteServerAdministrationDto>();
            Assert.NotNull(payload);
            Assert.Equal("visible-mod.jar", Assert.Single(payload.Addons).FileName);
        }

        using (var hidden = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"api/v1/servers/{hiddenServerId:D}/administration"))
        {
            hidden.Headers.TryAddWithoutValidation("Cookie", login.SessionCookie);
            using var response = await client.SendAsync(hidden);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal(1, backend.AdministrationReadCount);
    }

    [Fact]
    public async Task ServerAdministration_BoundsAndRemovesPathLikeBackendMetadata()
    {
        var serverId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        var backend = new FakeBackend
        {
            AdministrationResult = new RemoteServerAdministrationDto(
                DateTimeOffset.UtcNow,
                true,
                Enumerable.Range(0, 210)
                    .Select(index => new RemoteServerAddonDto(
                        index % 2 == 0 ? RemoteServerAddonKind.Mod : RemoteServerAddonKind.Plugin,
                        index == 0 ? @"C:\service\servers\secret.jar" : $"addon-{index:D3}.jar",
                        index))
                    .ToArray(),
                false,
                new RemoteServerJavaRuntimeDto(
                    true,
                    true,
                    999,
                    @"C:\private\java.exe",
                    (RemoteJavaRuntimeKind)999,
                    @"C:\private\vendor",
                    (RemoteJavaArchitecture)999))
        };
        var credentials = new FormalPermissionCredentialStore(
            "stamp-administration-bounds",
            [new ProductPermissionGrant(
                ProductPermissionCodes.ServerRead,
                ProductPermissionScope.ForServer(serverId))]);
        await using var host = await RemoteControlHost.StartAsync(backend, options, credentials);
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "owner@gmail.com");
        var login = await LoginAsync(client, options);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/v1/servers/{serverId:D}/administration");
        request.Headers.TryAddWithoutValidation("Cookie", login.SessionCookie);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<RemoteServerAdministrationDto>();
        Assert.NotNull(payload);
        Assert.Equal(RemoteServerAdministrationContract.MaximumListedAddons, payload.Addons.Count);
        Assert.True(payload.AddonsTruncated);
        Assert.All(payload.Addons, addon =>
        {
            Assert.DoesNotContain("\\", addon.FileName, StringComparison.Ordinal);
            Assert.DoesNotContain("/", addon.FileName, StringComparison.Ordinal);
            Assert.DoesNotContain(":", addon.FileName, StringComparison.Ordinal);
            Assert.True(addon.FileName.Length <= RemoteServerAdministrationContract.MaximumAddonFileNameCharacters);
        });
        Assert.Null(payload.Java.MajorVersion);
        Assert.Null(payload.Java.Version);
        Assert.Equal(RemoteJavaRuntimeKind.Unknown, payload.Java.RuntimeKind);
        Assert.Equal("Managed Java", payload.Java.Vendor);
        Assert.Equal(RemoteJavaArchitecture.Unknown, payload.Java.Architecture);
        var serialized = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, backend.AdministrationReadCount);
    }

    [Fact]
    public async Task BackupCatalog_RequiresBackupReadAndReturnsAtMostTwoHundredPathFreeEntries()
    {
        var serverId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        var backend = new FakeBackend
        {
            BackupListResult = new RemoteBackupListDto(
                DateTimeOffset.UtcNow,
                Enumerable.Range(1, 205)
                    .Select(index => new RemoteBackupSummaryDto(
                        index.ToString("x64"),
                        index == 1 ? @"C:\service\backups\world.zip" : $"backup-{index}.zip",
                        index,
                        DateTimeOffset.UtcNow.AddMinutes(-index)))
                    .ToArray(),
                false)
        };
        var credentials = new FormalPermissionCredentialStore(
            "stamp-backup-read",
            [new ProductPermissionGrant(
                ProductPermissionCodes.BackupRead,
                ProductPermissionScope.ForServer(serverId))]);
        await using var host = await RemoteControlHost.StartAsync(backend, options, credentials);
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "owner@gmail.com");
        var login = await LoginAsync(client, options);

        using (var request = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"api/v1/servers/{serverId:D}/backups"))
        {
            request.Headers.TryAddWithoutValidation("Cookie", login.SessionCookie);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<RemoteBackupListDto>();
            Assert.NotNull(payload);
            Assert.Equal(RemoteBackupRestoreContract.MaximumListedBackups, payload.Backups.Count);
            Assert.True(payload.HasMore);
            Assert.All(payload.Backups, backup =>
            {
                Assert.Matches("^[a-f0-9]{64}$", backup.BackupId);
                Assert.DoesNotContain("\\", backup.DisplayName, StringComparison.Ordinal);
                Assert.DoesNotContain("/", backup.DisplayName, StringComparison.Ordinal);
                Assert.DoesNotContain(":", backup.DisplayName, StringComparison.Ordinal);
            });
            Assert.StartsWith("backup-", payload.Backups[0].DisplayName, StringComparison.Ordinal);
        }
        Assert.Equal(1, backend.BackupListCount);

        using (var forbiddenRestore = CreateMutationRequest(
                   HttpMethod.Post,
                   $"api/v1/servers/{serverId:D}/backups/{new string('a', 64)}/restore",
                   options,
                   login.SessionCookie,
                   login.Status.CsrfToken!,
                   new RemoteBackupRestoreRequestDto(RemoteBackupRestoreContract.RequiredConfirmation)))
        {
            using var response = await client.SendAsync(forbiddenRestore);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(0, backend.BackupRestoreCount);
    }

    [Fact]
    public async Task BackupRestore_RequiresCsrfConfirmationAndPermissionAndIsIdempotentWithoutAuditPaths()
    {
        var serverId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var backupId = new string('a', 64);
        var secondBackupId = new string('b', 64);
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        var backend = new FakeBackend();
        var credentials = new FormalPermissionCredentialStore(
            "stamp-backup-restore",
            [new ProductPermissionGrant(
                ProductPermissionCodes.BackupRestore,
                ProductPermissionScope.ForServer(serverId))]);
        var audit = new RecordingAuditSink();
        await using var host = await RemoteControlHost.StartAsync(
            backend,
            options,
            credentials,
            securityAuditSink: audit);
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "owner@gmail.com");
        var login = await LoginAsync(client, options);

        using (var missingCsrf = new HttpRequestMessage(
                   HttpMethod.Post,
                   $"api/v1/servers/{serverId:D}/backups/{backupId}/restore"))
        {
            missingCsrf.Headers.TryAddWithoutValidation("Cookie", login.SessionCookie);
            missingCsrf.Headers.TryAddWithoutValidation(
                "Origin",
                options.PublicOrigin.GetLeftPart(UriPartial.Authority));
            missingCsrf.Headers.TryAddWithoutValidation(
                RemoteControlOptions.IdempotencyHeaderName,
                Guid.NewGuid().ToString("D"));
            missingCsrf.Content = JsonContent.Create(
                new RemoteBackupRestoreRequestDto(RemoteBackupRestoreContract.RequiredConfirmation));
            using var response = await client.SendAsync(missingCsrf);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var missingConfirmation = CreateMutationRequest(
                   HttpMethod.Post,
                   $"api/v1/servers/{serverId:D}/backups/{backupId}/restore",
                   options,
                   login.SessionCookie,
                   login.Status.CsrfToken!,
                   new RemoteBackupRestoreRequestDto("restore")))
        {
            using var response = await client.SendAsync(missingConfirmation);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(0, backend.BackupRestoreCount);

        var idempotencyKey = Guid.NewGuid().ToString("D");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var restore = CreateMutationRequest(
                HttpMethod.Post,
                $"api/v1/servers/{serverId:D}/backups/{backupId}/restore",
                options,
                login.SessionCookie,
                login.Status.CsrfToken!,
                new RemoteBackupRestoreRequestDto(RemoteBackupRestoreContract.RequiredConfirmation),
                idempotencyKey);
            using var response = await client.SendAsync(restore);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(1, backend.BackupRestoreCount);
        Assert.Equal(backupId, backend.LastRestoredBackupId);

        using (var conflictingRestore = CreateMutationRequest(
                   HttpMethod.Post,
                   $"api/v1/servers/{serverId:D}/backups/{secondBackupId}/restore",
                   options,
                   login.SessionCookie,
                   login.Status.CsrfToken!,
                   new RemoteBackupRestoreRequestDto(RemoteBackupRestoreContract.RequiredConfirmation),
                   idempotencyKey))
        {
            using var response = await client.SendAsync(conflictingRestore);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        Assert.Equal(1, backend.BackupRestoreCount);

        var accepted = audit.Events
            .Where(auditEvent => auditEvent.Action == RemoteSecurityAuditAction.ServerMutation &&
                                 auditEvent.Outcome == RemoteSecurityAuditOutcome.Accepted &&
                                 auditEvent.PermissionCode == ProductPermissionCodes.BackupRestore)
            .ToArray();
        Assert.NotEmpty(accepted);
        foreach (var auditEvent in accepted)
        {
            var serializedAudit = JsonSerializer.Serialize(auditEvent);
            Assert.DoesNotContain(backupId, serializedAudit, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("backup.zip", serializedAudit, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\", serializedAudit, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RequiredDurableAudit_RejectsHostStartupWithoutAdapter()
    {
        var options = new RemoteControlOptions
        {
            Port = ReserveLoopbackPort(),
            PublicOrigin = new Uri("https://mcsv-test.example.ts.net"),
            AllowedGoogleLogins = ["owner@gmail.com"],
            RequireDurableSecurityAudit = true
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RemoteControlHost.StartAsync(
                new FakeBackend(),
                options,
                new TestCredentialStore()));
    }

    [Fact]
    public async Task AuditWriteFailure_FailsClosedBeforeBackendMutationRegistration()
    {
        var options = TestOptions.Create(port: ReserveLoopbackPort());
        var backend = new FakeBackend();
        await using var host = await RemoteControlHost.StartAsync(
            backend,
            options,
            new TestCredentialStore(),
            securityAuditSink: new RejectMutationAuditSink());
        using var client = CreateClient(
            host.LocalEndpoint,
            options.PublicOrigin.Authority,
            "owner@gmail.com");
        var login = await LoginAsync(client, options);

        using var request = CreateMutationRequest(
            HttpMethod.Post,
            "api/v1/servers/server-01/actions/start",
            options,
            login.SessionCookie,
            login.Status.CsrfToken!,
            new RemoteEmptyMutationRequestDto());
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, backend.StartCount);
    }

    private static async Task<(RemoteAuthStatusDto Status, string SessionCookie)> LoginAsync(
        HttpClient client,
        RemoteControlOptions options)
    {
        using var statusResponse = await client.GetAsync("api/v1/auth/status");
        var status = await statusResponse.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.NotNull(status);
        Assert.True(status.CredentialRegistered);
        Assert.NotNull(status.AntiforgeryToken);
        var bootstrapCookie = GetCookie(statusResponse, "__Host-MCSV-Auth-CSRF");

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login");
        request.Headers.TryAddWithoutValidation(
            "Origin",
            options.PublicOrigin.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation(RemoteControlOptions.CsrfHeaderName, status.CsrfToken);
        request.Headers.TryAddWithoutValidation("Cookie", bootstrapCookie);
        request.Content = JsonContent.Create(
            new RemoteCredentialLoginRequestDto("account1", "12345678"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var signedIn = await response.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.NotNull(signedIn);
        Assert.True(signedIn.Authenticated);
        signedIn = signedIn with { AntiforgeryToken = status.AntiforgeryToken };
        return (
            signedIn,
            $"{bootstrapCookie}; {GetCookie(response, RemoteControlOptions.DefaultSessionCookieName)}");
    }

    private static async Task<string> EnrollRememberedDeviceAsync(
        HttpClient client,
        RemoteControlOptions options,
        RemoteAuthStatusDto signedIn,
        string sessionCookie)
    {
        using var enroll = CreateMutationRequest(
            HttpMethod.Post,
            "api/v1/auth/devices/enroll",
            options,
            sessionCookie,
            signedIn.CsrfToken!,
            new RemoteRememberedDeviceEnrollmentRequestDto("Integration test phone"));
        using var response = await client.SendAsync(enroll);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.NotNull(status);
        Assert.True(status.Authenticated);
        Assert.True(status.RememberedDevice);
        Assert.True(status.SupportsRememberedDevices);
        return GetCookie(response, options.RememberedDeviceCookieName);
    }

    private static HttpRequestMessage CreateSignOutRequest(
        RemoteControlOptions options,
        string antiforgeryToken,
        string cookies)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/signout");
        request.Headers.TryAddWithoutValidation(
            "Origin",
            options.PublicOrigin.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation(
            RemoteControlOptions.CsrfHeaderName,
            antiforgeryToken);
        request.Headers.TryAddWithoutValidation("Cookie", cookies);
        request.Content = JsonContent.Create(new RemoteEmptyMutationRequestDto());
        return request;
    }

    private static HttpClient CreateClient(Uri endpoint, string host, string? login)
    {
        var client = new HttpClient
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Host = host;
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        if (login is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                RemoteControlOptions.TailscaleLoginHeaderName,
                login);
        }

        return client;
    }

    private static RemoteControlOptions CreateQuickTunnelOptions(
        int port,
        int loginAttemptsPerMinute = 5,
        int globalRequestsPerMinute = 600)
        => new()
        {
            Port = port,
            PublicOrigin = new Uri("https://quiet-lake-abc123.trycloudflare.com/"),
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.CloudflareQuickTunnel,
            LoginAttemptsPerMinute = loginAttemptsPerMinute,
            GlobalRequestsPerMinute = globalRequestsPerMinute
        };

    private static HttpRequestMessage CreateQuickTunnelLoginAttempt(
        RemoteControlOptions options,
        params string[] connectingAddresses)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login");
        request.Headers.TryAddWithoutValidation(
            "Origin",
            options.PublicOrigin.GetLeftPart(UriPartial.Authority));
        foreach (var address in connectingAddresses)
        {
            request.Headers.TryAddWithoutValidation(
                RemoteControlOptions.CloudflareConnectingIpHeaderName,
                address);
        }

        request.Content = JsonContent.Create(
            new RemoteCredentialLoginRequestDto("account1", "00000000"));
        return request;
    }

    private static HttpRequestMessage CreateQuickTunnelRefreshAttempt(
        RemoteControlOptions options,
        params string[] connectingAddresses)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/devices/refresh");
        request.Headers.TryAddWithoutValidation(
            "Origin",
            options.PublicOrigin.GetLeftPart(UriPartial.Authority));
        foreach (var address in connectingAddresses)
        {
            request.Headers.TryAddWithoutValidation(
                RemoteControlOptions.CloudflareConnectingIpHeaderName,
                address);
        }

        request.Content = JsonContent.Create(
            new RemoteRememberedDeviceRefreshRequestDto(Guid.NewGuid()));
        return request;
    }

    private static HttpRequestMessage CreateMutationRequest(
        HttpMethod method,
        string path,
        RemoteControlOptions options,
        string sessionCookie,
        string csrfToken,
        object? body = null,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
        request.Headers.TryAddWithoutValidation("Origin", options.PublicOrigin.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation(RemoteControlOptions.CsrfHeaderName, csrfToken);
        request.Headers.TryAddWithoutValidation(
            RemoteControlOptions.IdempotencyHeaderName,
            idempotencyKey ?? Guid.NewGuid().ToString("D"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static string GetCookie(HttpResponseMessage response, string cookieName)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(cookieName + "=", StringComparison.Ordinal));
        return setCookie.Split(';', 2)[0];
    }

    private static void AssertCookieDeleted(
        HttpResponseMessage response,
        string cookieName)
    {
        var deletion = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(cookieName + "=", StringComparison.Ordinal));
        Assert.Contains("expires=", deletion, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", deletion, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        Assert.True(response.Headers.Contains("Referrer-Policy"));
        Assert.True(response.Headers.Contains("Permissions-Policy"));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static int ReserveLoopbackPort()
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

    private sealed class TestCredentialStore : IRemoteCredentialStore
    {
        public bool HasCredentialForLogin(string tailscaleLogin)
            => string.Equals(tailscaleLogin, "owner@gmail.com", StringComparison.OrdinalIgnoreCase);

        public RemoteCredentialAuthenticationResult Authenticate(
            string tailscaleLogin,
            string username,
            string pin)
            => HasCredentialForLogin(tailscaleLogin) && username == "account1" && pin == "12345678"
                ? new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.Success,
                    "account1")
                : new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.InvalidCredentials);
    }

    private sealed class FormalPermissionCredentialStore(
        string securityStamp,
        IReadOnlyList<ProductPermissionGrant> grants)
        : IRemoteCredentialStore, IRemoteAuthorizationStore
    {
        public bool HasCredentialForLogin(string subject)
            => string.Equals(subject, "owner@gmail.com", StringComparison.OrdinalIgnoreCase);

        public RemoteCredentialAuthenticationResult Authenticate(
            string subject,
            string username,
            string pin)
            => HasCredentialForLogin(subject) && username == "account1" && pin == "12345678"
                ? new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.Success,
                    "account1")
                : new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.InvalidCredentials);

        public bool TryGetAuthorization(
            string credentialSubject,
            string username,
            out RemoteAuthorizationSnapshot snapshot)
        {
            snapshot = new RemoteAuthorizationSnapshot(securityStamp, grants);
            return HasCredentialForLogin(credentialSubject) && username == "account1";
        }
    }

    private sealed class RecordingAuditSink : IRemoteSecurityAuditSink
    {
        private readonly object _gate = new();
        private readonly List<RemoteSecurityAuditEvent> _events = [];

        public IReadOnlyList<RemoteSecurityAuditEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

        public bool TryWrite(RemoteSecurityAuditEvent auditEvent)
        {
            lock (_gate)
            {
                _events.Add(auditEvent);
                return true;
            }
        }
    }

    private sealed class RejectMutationAuditSink : IRemoteSecurityAuditSink
    {
        public bool TryWrite(RemoteSecurityAuditEvent auditEvent)
            => auditEvent.Action != RemoteSecurityAuditAction.ServerMutation;
    }

    private sealed class PermissionCredentialStore(RemoteWebPermission permissions)
        : IRemoteCredentialStore
    {
        public bool HasCredentialForLogin(string subject)
            => string.Equals(
                subject,
                RemoteControlOptions.QuickTunnelCredentialSubject,
                StringComparison.Ordinal);

        public RemoteCredentialAuthenticationResult Authenticate(
            string subject,
            string username,
            string pin)
            => HasCredentialForLogin(subject) && username == "account1" && pin == "12345678"
                ? new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.Success,
                    "account1",
                    Permissions: permissions)
                : new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.InvalidCredentials);
    }

    private sealed class QuickRememberedCredentialStore
        : IRemoteCredentialStore, IRemoteRememberedDeviceStore
    {
        public int IssueCalls { get; private set; }
        public int RefreshCalls { get; private set; }

        public bool HasCredentialForLogin(string subject)
            => string.Equals(
                subject,
                RemoteControlOptions.QuickTunnelCredentialSubject,
                StringComparison.Ordinal);

        public RemoteCredentialAuthenticationResult Authenticate(
            string subject,
            string username,
            string pin)
            => HasCredentialForLogin(subject) && username == "account1" && pin == "12345678"
                ? new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.Success,
                    "account1")
                : new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.InvalidCredentials);

        public IssuedRemoteRememberedDevice IssueRememberedDevice(
            string login,
            string username,
            string label)
        {
            IssueCalls++;
            throw new InvalidOperationException("Quick Tunnel must reject before device enrollment.");
        }

        public RemoteRememberedDeviceRefreshResult RefreshRememberedDevice(
            string login,
            string token,
            Guid requestId)
        {
            RefreshCalls++;
            return new RemoteRememberedDeviceRefreshResult(
                RemoteRememberedDeviceRefreshStatus.Invalid);
        }

        public IReadOnlyList<RemoteRememberedDeviceInfo> GetRememberedDevices() => [];
        public bool RevokeRememberedDevice(string login, string token) => false;
        public bool RevokeRememberedDevice(Guid deviceId) => false;
        public int RevokeRememberedDevicesForAccount(string username) => 0;
        public int RevokeAllRememberedDevices() => 0;
    }

    private sealed class RememberingCredentialStore : IRemoteCredentialStore, IRemoteRememberedDeviceStore
    {
        private readonly object _gate = new();
        private readonly string _credentialSubject;
        private RemoteRememberedDeviceInfo? _device;
        private string? _currentToken;
        private string? _previousToken;
        private Guid _lastRefreshRequestId;

        public RememberingCredentialStore(string credentialSubject = "owner@gmail.com")
        {
            _credentialSubject = credentialSubject;
        }

        public bool HasCredentialForLogin(string login)
            => string.Equals(login, _credentialSubject, StringComparison.OrdinalIgnoreCase);

        public RemoteCredentialAuthenticationResult Authenticate(
            string login,
            string username,
            string pin)
            => HasCredentialForLogin(login) && username == "account1" && pin == "12345678"
                ? new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.Success,
                    "account1")
                : new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.InvalidCredentials);

        public IssuedRemoteRememberedDevice IssueRememberedDevice(
            string login,
            string username,
            string label)
        {
            if (!HasCredentialForLogin(login) || username != "account1")
            {
                throw new InvalidOperationException("Unknown account.");
            }

            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                _device = new RemoteRememberedDeviceInfo(
                    Guid.NewGuid(),
                    "account1",
                    label,
                    now,
                    now,
                    now.AddDays(90),
                    now.AddDays(365),
                    RemoteRememberedDeviceStatus.Active);
                _currentToken = CreateToken();
                _previousToken = null;
                _lastRefreshRequestId = Guid.Empty;
                return new IssuedRemoteRememberedDevice(
                    _currentToken,
                    _device,
                    RemoteWebPermission.All);
            }
        }

        public RemoteRememberedDeviceRefreshResult RefreshRememberedDevice(
            string login,
            string token,
            Guid requestId)
        {
            lock (_gate)
            {
                if (!HasCredentialForLogin(login) || _device is null ||
                    _device.Status != RemoteRememberedDeviceStatus.Active)
                {
                    return new RemoteRememberedDeviceRefreshResult(
                        RemoteRememberedDeviceRefreshStatus.Invalid);
                }

                if (token == _previousToken && requestId == _lastRefreshRequestId)
                {
                    return Success();
                }

                if (token != _currentToken)
                {
                    return new RemoteRememberedDeviceRefreshResult(
                        RemoteRememberedDeviceRefreshStatus.Invalid);
                }

                _previousToken = _currentToken;
                _currentToken = CreateToken();
                _lastRefreshRequestId = requestId;
                var now = DateTimeOffset.UtcNow;
                _device = _device with
                {
                    LastUsedAtUtc = now,
                    IdleExpiresAtUtc = now.AddDays(90)
                };
                return Success();
            }
        }

        public IReadOnlyList<RemoteRememberedDeviceInfo> GetRememberedDevices()
        {
            lock (_gate)
            {
                return _device is null ? [] : [_device];
            }
        }

        public bool RevokeRememberedDevice(string login, string token)
        {
            lock (_gate)
            {
                // The production store authenticates an older generation for sign-out as
                // well. Possession of that token can already trigger replay revocation, and
                // accepting it here closes the refresh-rotation/sign-out race.
                if (!HasCredentialForLogin(login) ||
                    token != _currentToken && token != _previousToken ||
                    _device is null)
                {
                    return false;
                }

                Revoke("signout");
                return true;
            }
        }

        public bool RevokeRememberedDevice(Guid deviceId)
        {
            lock (_gate)
            {
                if (_device?.DeviceId != deviceId) return false;
                Revoke("desktop");
                return true;
            }
        }

        public int RevokeRememberedDevicesForAccount(string username)
        {
            lock (_gate)
            {
                if (_device?.Username != username ||
                    _device.Status != RemoteRememberedDeviceStatus.Active) return 0;
                Revoke("account");
                return 1;
            }
        }

        public int RevokeAllRememberedDevices()
        {
            lock (_gate)
            {
                if (_device is null || _device.Status != RemoteRememberedDeviceStatus.Active) return 0;
                Revoke("all");
                return 1;
            }
        }

        private RemoteRememberedDeviceRefreshResult Success()
            => new(
                RemoteRememberedDeviceRefreshStatus.Success,
                _currentToken,
                _device,
                "account1",
                RemoteWebPermission.All);

        private void Revoke(string reason)
        {
            var now = DateTimeOffset.UtcNow;
            _device = _device! with
            {
                Status = RemoteRememberedDeviceStatus.Revoked,
                RevokedAtUtc = now,
                RevocationReason = reason
            };
            _currentToken = null;
            _previousToken = null;
        }

        private static string CreateToken()
            => $"test.{Guid.NewGuid():N}.{Guid.NewGuid():N}";
    }

    private sealed class BlockingRefreshCredentialStore
        : IRemoteCredentialStore, IRemoteRememberedDeviceStore
    {
        private readonly RememberingCredentialStore _inner = new();
        private readonly TaskCompletionSource _releaseRefresh =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RefreshRotated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RevokeAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseRefresh() => _releaseRefresh.TrySetResult();

        public bool HasCredentialForLogin(string login)
            => _inner.HasCredentialForLogin(login);

        public RemoteCredentialAuthenticationResult Authenticate(
            string login,
            string username,
            string pin)
            => _inner.Authenticate(login, username, pin);

        public IssuedRemoteRememberedDevice IssueRememberedDevice(
            string login,
            string username,
            string label)
            => _inner.IssueRememberedDevice(login, username, label);

        public RemoteRememberedDeviceRefreshResult RefreshRememberedDevice(
            string login,
            string token,
            Guid requestId)
        {
            var result = _inner.RefreshRememberedDevice(login, token, requestId);
            RefreshRotated.TrySetResult();
            _releaseRefresh.Task.GetAwaiter().GetResult();
            return result;
        }

        public IReadOnlyList<RemoteRememberedDeviceInfo> GetRememberedDevices()
            => _inner.GetRememberedDevices();

        public bool RevokeRememberedDevice(string login, string token)
        {
            RevokeAttempted.TrySetResult();
            return _inner.RevokeRememberedDevice(login, token);
        }

        public bool RevokeRememberedDevice(Guid deviceId)
            => _inner.RevokeRememberedDevice(deviceId);

        public int RevokeRememberedDevicesForAccount(string username)
            => _inner.RevokeRememberedDevicesForAccount(username);

        public int RevokeAllRememberedDevices()
            => _inner.RevokeAllRememberedDevices();
    }

    private sealed class FailOnceRevocationCredentialStore
        : IRemoteCredentialStore, IRemoteRememberedDeviceStore
    {
        private readonly RememberingCredentialStore _inner = new();

        public int RevokeAttempts { get; private set; }

        public bool HasCredentialForLogin(string login)
            => _inner.HasCredentialForLogin(login);

        public RemoteCredentialAuthenticationResult Authenticate(
            string login,
            string username,
            string pin)
            => _inner.Authenticate(login, username, pin);

        public IssuedRemoteRememberedDevice IssueRememberedDevice(
            string login,
            string username,
            string label)
            => _inner.IssueRememberedDevice(login, username, label);

        public RemoteRememberedDeviceRefreshResult RefreshRememberedDevice(
            string login,
            string token,
            Guid requestId)
            => _inner.RefreshRememberedDevice(login, token, requestId);

        public IReadOnlyList<RemoteRememberedDeviceInfo> GetRememberedDevices()
            => _inner.GetRememberedDevices();

        public bool RevokeRememberedDevice(string login, string token)
        {
            RevokeAttempts++;
            if (RevokeAttempts == 1)
            {
                throw new IOException("Simulated protected-store write failure.");
            }

            return _inner.RevokeRememberedDevice(login, token);
        }

        public bool RevokeRememberedDevice(Guid deviceId)
            => _inner.RevokeRememberedDevice(deviceId);

        public int RevokeRememberedDevicesForAccount(string username)
            => _inner.RevokeRememberedDevicesForAccount(username);

        public int RevokeAllRememberedDevices()
            => _inner.RevokeAllRememberedDevices();
    }

    private sealed class LockedCredentialStore : IRemoteCredentialStore
    {
        public bool HasCredentialForLogin(string tailscaleLogin)
            => string.Equals(tailscaleLogin, "owner@gmail.com", StringComparison.OrdinalIgnoreCase);

        public RemoteCredentialAuthenticationResult Authenticate(
            string tailscaleLogin,
            string username,
            string pin)
            => new(
                RemoteCredentialAuthenticationStatus.LockedOut,
                LockedUntilUtc: DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private sealed class ScopedBackend(Guid visibleServerId, Guid hiddenServerId)
        : IRemoteControlBackend
    {
        private readonly RemoteServerSummaryDto _visible = Create(visibleServerId, "Visible");
        private readonly RemoteServerSummaryDto _hidden = Create(hiddenServerId, "Hidden");

        public int StartCount { get; private set; }

        public int AdministrationReadCount { get; private set; }

        public ValueTask<RemoteDashboardDto> GetDashboardAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new RemoteDashboardDto(
                DateTimeOffset.UtcNow,
                [_visible, _hidden]));

        public ValueTask<RemoteServerDetailDto?> GetServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RemoteServerDetailDto?>(
                serverId == _visible.Id
                    ? new RemoteServerDetailDto(_visible, "Java 25", true, true, true)
                    : serverId == _hidden.Id
                        ? new RemoteServerDetailDto(_hidden, "Java 25", true, true, true)
                        : null);

        public ValueTask<RemoteServerAdministrationDto?> GetServerAdministrationAsync(
            string serverId,
            CancellationToken cancellationToken)
        {
            AdministrationReadCount++;
            return ValueTask.FromResult<RemoteServerAdministrationDto?>(
                Guid.TryParse(serverId, out var parsed) &&
                (parsed == visibleServerId || parsed == hiddenServerId)
                    ? new RemoteServerAdministrationDto(
                        DateTimeOffset.UtcNow,
                        true,
                        [new RemoteServerAddonDto(
                            RemoteServerAddonKind.Mod,
                            parsed == visibleServerId ? "visible-mod.jar" : "hidden-mod.jar",
                            1)],
                        false,
                        new RemoteServerJavaRuntimeDto(
                            true,
                            true,
                            21,
                            "21.0.8",
                            RemoteJavaRuntimeKind.Jre,
                            "Eclipse Temurin",
                            RemoteJavaArchitecture.X64))
                    : null);
        }

        public ValueTask<RemoteConsolePageDto?> GetConsoleAsync(
            string serverId,
            RemoteConsoleQuery query,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RemoteConsolePageDto?>(
                new RemoteConsolePageDto([], null, false));

        public ValueTask<RemotePlayerListDto?> GetPlayersAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RemotePlayerListDto?>(
                new RemotePlayerListDto(DateTimeOffset.UtcNow, []));

        public ValueTask<RemoteOperationResultDto> StartServerAsync(
            string serverId,
            CancellationToken cancellationToken)
        {
            StartCount++;
            return ValueTask.FromResult(new RemoteOperationResultDto(true, "accepted"));
        }

        public ValueTask<RemoteOperationResultDto> StopServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RemoteOperationResultDto(true, "accepted"));

        public ValueTask<RemoteOperationResultDto> RestartServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RemoteOperationResultDto(true, "accepted"));

        public ValueTask<RemoteOperationResultDto> SendConsoleCommandAsync(
            string serverId,
            string command,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RemoteOperationResultDto(true, "accepted"));

        public ValueTask<RemoteOperationResultDto> PerformPlayerActionAsync(
            string serverId,
            RemotePlayerActionRequestDto request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RemoteOperationResultDto(true, "accepted"));

        public ValueTask<RemoteOperationResultDto> CreateBackupAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RemoteOperationResultDto(true, "accepted"));

        private static RemoteServerSummaryDto Create(Guid id, string name)
            => new(
                id.ToString("N"),
                name,
                "Forge",
                "26.2",
                RemoteServerState.Stopped,
                false,
                0,
                20,
                0,
                0,
                25565,
                null);
    }

    private class FakeBackend : IRemoteControlBackend
    {
        private readonly RemoteServerSummaryDto _server = new(
            "server-01",
            "Test Server",
            "Forge",
            "26.2",
            RemoteServerState.Stopped,
            false,
            0,
            20,
            0,
            0,
            25565,
            null);

        public int StartCount { get; private set; }

        public int CommandCount { get; private set; }

        public int BackupListCount { get; private set; }

        public int BackupRestoreCount { get; private set; }

        public int AdministrationReadCount { get; private set; }

        public string? LastRestoredBackupId { get; private set; }

        public RemoteBackupListDto? BackupListResult { get; init; } = new(
            DateTimeOffset.UtcNow,
            [],
            false);

        public RemoteServerAdministrationDto? AdministrationResult { get; init; } = new(
            DateTimeOffset.UtcNow,
            true,
            [],
            false,
            new RemoteServerJavaRuntimeDto(
                true,
                true,
                25,
                "25.0.1",
                RemoteJavaRuntimeKind.Jre,
                "Eclipse Temurin",
                RemoteJavaArchitecture.X64));

        public ValueTask<RemoteDashboardDto> GetDashboardAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new RemoteDashboardDto(DateTimeOffset.UtcNow, [_server]));

        public ValueTask<RemoteServerDetailDto?> GetServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RemoteServerDetailDto?>(
                serverId == _server.Id
                    ? new RemoteServerDetailDto(_server, "Java 25", true, true, true)
                    : null);

        public ValueTask<RemoteServerAdministrationDto?> GetServerAdministrationAsync(
            string serverId,
            CancellationToken cancellationToken)
        {
            AdministrationReadCount++;
            return ValueTask.FromResult(
                serverId == _server.Id || Guid.TryParse(serverId, out _)
                    ? AdministrationResult
                    : null);
        }

        public ValueTask<RemoteConsolePageDto?> GetConsoleAsync(
            string serverId,
            RemoteConsoleQuery query,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RemoteConsolePageDto?>(
                serverId == _server.Id
                    ? new RemoteConsolePageDto(
                        Enumerable.Range(1, query.Limit + 1)
                            .Select(sequence => new RemoteConsoleLineDto(
                                sequence,
                                DateTimeOffset.UtcNow,
                                RemoteConsoleSeverity.Information,
                                RemoteConsoleStream.Ordinary,
                                new string('x', 5000)))
                            .ToArray(),
                        query.Limit + 1,
                        false)
                    : null);

        public ValueTask<RemotePlayerListDto?> GetPlayersAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RemotePlayerListDto?>(
                serverId == _server.Id
                    ? new RemotePlayerListDto(DateTimeOffset.UtcNow, [])
                    : null);

        public ValueTask<RemoteOperationResultDto> StartServerAsync(
            string serverId,
            CancellationToken cancellationToken)
        {
            StartCount++;
            return ValueTask.FromResult(new RemoteOperationResultDto(true, "accepted"));
        }

        public ValueTask<RemoteOperationResultDto> StopServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RemoteOperationResultDto(true, "accepted"));

        public virtual ValueTask<RemoteOperationResultDto> RestartServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RemoteOperationResultDto(true, "accepted"));

        public ValueTask<RemoteOperationResultDto> SendConsoleCommandAsync(
            string serverId,
            string command,
            CancellationToken cancellationToken)
        {
            CommandCount++;
            return ValueTask.FromResult(new RemoteOperationResultDto(true, "accepted"));
        }

        public ValueTask<RemoteOperationResultDto> PerformPlayerActionAsync(
            string serverId,
            RemotePlayerActionRequestDto request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RemoteOperationResultDto(true, "accepted"));

        public ValueTask<RemoteOperationResultDto> CreateBackupAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RemoteOperationResultDto(true, "accepted", "backup-01"));

        public ValueTask<RemoteBackupListDto?> GetBackupsAsync(
            string serverId,
            CancellationToken cancellationToken)
        {
            BackupListCount++;
            return ValueTask.FromResult(BackupListResult);
        }

        public ValueTask<RemoteOperationResultDto> RestoreBackupAsync(
            string serverId,
            string backupId,
            CancellationToken cancellationToken)
        {
            BackupRestoreCount++;
            LastRestoredBackupId = backupId;
            return ValueTask.FromResult(new RemoteOperationResultDto(true, "restored", "restore-01"));
        }
    }

    private sealed class BlockingRestartBackend : FakeBackend
    {
        private readonly TaskCompletionSource<RemoteOperationResultDto> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RestartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RestartFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RestartCount { get; private set; }

        public CancellationToken ObservedCancellationToken { get; private set; }

        public override async ValueTask<RemoteOperationResultDto> RestartServerAsync(
            string serverId,
            CancellationToken cancellationToken)
        {
            RestartCount++;
            ObservedCancellationToken = cancellationToken;
            RestartEntered.TrySetResult();
            try
            {
                return await _completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                RestartFinished.TrySetResult();
            }
        }

        public void CompleteRestart()
            => _completion.TrySetResult(new RemoteOperationResultDto(true, "restart completed"));
    }

    private sealed class ShutdownDrainBackend : FakeBackend
    {
        private readonly TaskCompletionSource _cleanupRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RestartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ApplicationStoppingObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CleanupCompleted { get; private set; }

        public override async ValueTask<RemoteOperationResultDto> RestartServerAsync(
            string serverId,
            CancellationToken cancellationToken)
        {
            RestartEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new RemoteOperationResultDto(true, "unexpected");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ApplicationStoppingObserved.TrySetResult();
                await _cleanupRelease.Task;
                CleanupCompleted = true;
                throw;
            }
        }

        public void AllowCleanupToFinish() => _cleanupRelease.TrySetResult();
    }
}
