using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteWebSecurityContractTests
{
    [Fact]
    public void CredentialLogin_IsNonRetryableAndUsesSameOriginCsrfMutationContract()
    {
        var script = ReadWebAsset("app.js");

        Assert.Contains("const API_ROOT = \"/api/v1\";", script, StringComparison.Ordinal);
        Assert.Contains("if (!path.startsWith(`${API_ROOT}/`))", script, StringComparison.Ordinal);
        Assert.Contains("const csrfToken = options.csrfToken || state.csrfToken;", script, StringComparison.Ordinal);
        Assert.Contains("headers.set(\"X-MCSV-CSRF\", csrfToken)", script, StringComparison.Ordinal);
        Assert.Contains("credentials: \"same-origin\"", script, StringComparison.Ordinal);
        Assert.Contains("options.retryable !== false", script, StringComparison.Ordinal);
        var loginCall = script.IndexOf("runMutationOnce(\"auth:login\"", StringComparison.Ordinal);
        Assert.True(loginCall >= 0);
        var loginBlock = script.Substring(loginCall, Math.Min(600, script.Length - loginCall));
        Assert.Contains("`${API_ROOT}/auth/login`", loginBlock, StringComparison.Ordinal);
        Assert.Contains("method: \"POST\"", loginBlock, StringComparison.Ordinal);
        Assert.Contains("mutation: true", loginBlock, StringComparison.Ordinal);
        Assert.Contains("retryable: false", loginBlock, StringComparison.Ordinal);
        Assert.Contains("body: { username, pin }", loginBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("auth:pair", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/auth/pair", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UnauthenticatedMobilePage_OffersLoginOnlyWithAsciiDigitPin()
    {
        var markup = ReadWebAsset("index.html");
        var script = ReadWebAsset("app.js");

        Assert.Contains("id=\"login-form\"", markup, StringComparison.Ordinal);
        Assert.Contains(
            "id=\"login-form\" method=\"post\" action=\"/api/v1/auth/login\"",
            markup,
            StringComparison.Ordinal);
        Assert.Contains("id=\"login-username\"", markup, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"username\"", markup, StringComparison.Ordinal);
        Assert.Contains("pattern=\"[A-Za-z][A-Za-z0-9]{5,31}\"", markup, StringComparison.Ordinal);
        Assert.Contains("minlength=\"6\" maxlength=\"32\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"login-pin\"", markup, StringComparison.Ordinal);
        Assert.Contains("inputmode=\"numeric\"", markup, StringComparison.Ordinal);
        Assert.Contains("pattern=\"[0-9]{4,12}\"", markup, StringComparison.Ordinal);
        Assert.Contains("minlength=\"4\" maxlength=\"12\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"login-button\"", markup, StringComparison.Ordinal);
        Assert.Contains("type=\"submit\" disabled", markup, StringComparison.Ordinal);
        Assert.Contains("請先在電腦版新增帳號", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("pair-form", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("pairing-code", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("安全配對", markup, StringComparison.Ordinal);
        Assert.Contains("!/^[A-Za-z][A-Za-z0-9]{5,31}$/u.test(username)", script, StringComparison.Ordinal);
        Assert.Contains("const pin = pinInput.value;", script, StringComparison.Ordinal);
        Assert.Contains("!/^[0-9]{4,12}$/u.test(pin)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginRemainsDisabledUntilAuthStatusRefreshesCsrf()
    {
        var script = ReadWebAsset("app.js");

        Assert.Contains("const ready = hasCredential && state.authStatusReady && Boolean(state.csrfToken) && navigator.onLine", script, StringComparison.Ordinal);
        Assert.Contains("button.disabled = !ready || pending", script, StringComparison.Ordinal);
        Assert.Contains("state.credentialRegistered !== false", script, StringComparison.Ordinal);

        var signOut = script.IndexOf("async function signOut()", StringComparison.Ordinal);
        Assert.True(signOut >= 0);
        var signOutBlock = script.Substring(signOut, Math.Min(1800, script.Length - signOut));
        var reset = signOutBlock.IndexOf(
            "resetSessionState({ suppressDeviceRestore: true });",
            StringComparison.Ordinal);
        var reload = signOutBlock.IndexOf("await refreshAuthenticationForLogin", StringComparison.Ordinal);
        Assert.True(reset >= 0 && reload > reset);

        var unauthorized = script.IndexOf("error instanceof ApiError && error.status === 401", StringComparison.Ordinal);
        Assert.True(unauthorized >= 0);
        var unauthorizedBlock = script.Substring(unauthorized, Math.Min(500, script.Length - unauthorized));
        Assert.Contains("refreshAuthenticationForLogin", unauthorizedBlock, StringComparison.Ordinal);
        Assert.Contains("handleRequestError(error, t(\"web.console.sendFailed\"))", script, StringComparison.Ordinal);
        Assert.Contains("handleRequestError(error, t(\"web.player.actionFailed\", labels[action]))", script, StringComparison.Ordinal);

        var loginCatch = script.IndexOf("errorNode.textContent = userFacingError(error, t(\"web.auth.loginFailed\"))", StringComparison.Ordinal);
        Assert.True(loginCatch >= 0);
        var loginCatchPrefix = script.Substring(Math.Max(0, loginCatch - 500), Math.Min(500, loginCatch));
        Assert.Contains("await refreshAuthenticationForLogin", loginCatchPrefix, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginRateLimit_PreservesRetryAfterAndUsesOneBoundedDisposableTimer()
    {
        var script = ReadWebAsset("app.js");

        Assert.Contains(
            "this.retryAfterSeconds = retryAfterSeconds;",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "parseRetryAfterSeconds(response.headers.get(\"Retry-After\"))",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "throw new ApiError(message, response.status, code, payload, retryAfterSeconds);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "const loginLocked = remainingLoginRetrySeconds() > 0;",
            script,
            StringComparison.Ordinal);
        Assert.Contains("&& !loginLocked", script, StringComparison.Ordinal);
        Assert.Contains(
            "errorNode.textContent = startLoginRetry(error.retryAfterSeconds);",
            script,
            StringComparison.Ordinal);
        Assert.Contains("t(\"web.auth.lockedUntil\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "window.clearTimeout(state.loginRetryTimer);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.loginRetryTimer = window.setTimeout(finishLoginRetry",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "window.addEventListener(\"pagehide\", suspendLoginRetryTimer);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "const MAX_LOGIN_RETRY_SECONDS = 24 * 60 * 60;",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MutationControls_DefaultHiddenAndFollowServerIssuedPermissions()
    {
        var script = ReadWebAsset("app.js");

        Assert.Contains("function applyPermissions(status)", script, StringComparison.Ordinal);
        foreach (var permission in new[]
                 {
                     "canStartServer",
                     "canStopServer",
                     "canRestartServer",
                     "canSendConsoleCommand",
                     "canManagePlayers",
                     "canCreateBackup"
                 })
        {
            Assert.Contains($"pick(permissions, \"{permission}\") === true", script, StringComparison.Ordinal);
        }

        Assert.Contains("button.hidden = !permitted", script, StringComparison.Ordinal);
        Assert.Contains("function permissionsForServer(serverId = state.currentServerId)", script, StringComparison.Ordinal);
        Assert.Contains(".replace(/[{}-]/gu, \"\")", script, StringComparison.Ordinal);
        Assert.Contains("hasServerPermission(\"server.start\"", script, StringComparison.Ordinal);
        Assert.Contains("hasServerPermission(\"console.write\"", script, StringComparison.Ordinal);
        Assert.Contains("hasServerPermission(\"player.manage\"", script, StringComparison.Ordinal);
        Assert.Contains("hasServerPermission(\"backup.create\"", script, StringComparison.Ordinal);
        Assert.Contains("$(\"#create-backup-button\").hidden = !scopedPermissions.createBackup", script, StringComparison.Ordinal);
        Assert.Contains("$(\"#command-form\").hidden = !scopedPermissions.sendConsoleCommand", script, StringComparison.Ordinal);
        Assert.Contains("$(\"#player-action-form\").hidden = !scopedPermissions.managePlayers", script, StringComparison.Ordinal);
        Assert.Contains("$(\"#console-tab\").hidden = !scopedPermissions.readConsole", script, StringComparison.Ordinal);
        Assert.Contains("$(\"#players-tab\").hidden = !scopedPermissions.readPlayers", script, StringComparison.Ordinal);
        Assert.Contains("else if (error instanceof ApiError && error.status === 403 && state.authenticated)", script, StringComparison.Ordinal);
        Assert.Contains("void refreshPermissionsAfterForbidden();", script, StringComparison.Ordinal);
        Assert.Contains("const status = await apiRequest(`${API_ROOT}/auth/status`);", script, StringComparison.Ordinal);
        Assert.Contains("id=\"permission-notice\"", ReadWebAsset("index.html"), StringComparison.Ordinal);
        Assert.Contains("applyPermissions(null);", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceOwnedBackupUi_UsesScopedReadRestoreAndDoubleConfirmedIdempotentMutation()
    {
        var markup = ReadWebAsset("index.html");
        var script = ReadWebAsset("app.js");
        var style = ReadWebAsset("app.css");

        Assert.Contains("id=\"backup-catalog-card\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"backup-list\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"refresh-backups-button\"", markup, StringComparison.Ordinal);
        Assert.Contains("role=\"status\" aria-live=\"polite\"", markup, StringComparison.Ordinal);
        Assert.Contains("hasServerPermission(\"backup.read\"", script, StringComparison.Ordinal);
        Assert.Contains("hasServerPermission(\"backup.restore\"", script, StringComparison.Ordinal);
        Assert.Contains("const MAX_BACKUP_ENTRIES = 200;", script, StringComparison.Ordinal);
        Assert.Contains("slice(0, MAX_BACKUP_ENTRIES)", script, StringComparison.Ordinal);
        Assert.Contains("/^[a-f0-9]{64}$/u.test(backupId)", script, StringComparison.Ordinal);
        Assert.Contains("const BACKUP_RESTORE_CONFIRMATION = \"RESTORE STOPPED SERVER BACKUP\";", script, StringComparison.Ordinal);
        Assert.Contains("t(\"web.backup.restoreConfirmTitle\")", script, StringComparison.Ordinal);
        Assert.Contains("t(\"web.backup.restoreSecondTitle\")", script, StringComparison.Ordinal);
        Assert.Contains("body: { confirmation: BACKUP_RESTORE_CONFIRMATION }", script, StringComparison.Ordinal);
        Assert.Contains("mutation: true", script, StringComparison.Ordinal);
        Assert.Contains("normalizeState(pick(state.currentServer, \"state\")) !== \"stopped\"", script, StringComparison.Ordinal);
        Assert.Contains("copy.append(element(\"strong\", \"\", text(pick(backup, \"displayName\")", script, StringComparison.Ordinal);
        Assert.Contains(".backup-entry", style, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr) auto", style, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 620px)", style, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceOwnedEnvironmentUi_IsReadOnlyBoundedAndNeverRequestsAPath()
    {
        var markup = ReadWebAsset("index.html");
        var script = ReadWebAsset("app.js");
        var style = ReadWebAsset("app.css");

        Assert.Contains("id=\"environment-tab\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"environment-panel\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"environment-addon-list\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"environment-java-version\"", markup, StringComparison.Ordinal);
        Assert.Contains("const MAX_ADMINISTRATION_ADDONS = 200;", script, StringComparison.Ordinal);
        Assert.Contains("const MAX_ADDON_FILE_NAME_CHARACTERS = 160;", script, StringComparison.Ordinal);
        Assert.Contains("`${API_ROOT}/servers/${encodeURIComponent(id)}/administration`", script, StringComparison.Ordinal);
        Assert.Contains("!permissionsForServer(id).readServer", script, StringComparison.Ordinal);
        Assert.Contains("/[\\u0000-\\u001f\\u007f/\\\\:<>\"|?*]/u.test(candidate)", script, StringComparison.Ordinal);
        Assert.Contains(".slice(0, MAX_ADMINISTRATION_ADDONS)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("directoryPath", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javaRuntimePath", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serverDirectory", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".environment-addon-row", style, StringComparison.Ordinal);
        Assert.Contains(".environment-grid", style, StringComparison.Ordinal);
    }

    [Fact]
    public void RememberedDevice_UsesHttpOnlyCookieEndpointsWithoutPersistingCredentialsInScriptStorage()
    {
        var markup = ReadWebAsset("index.html");
        var script = ReadWebAsset("app.js");

        Assert.Contains("id=\"remember-device\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"remember-device-option\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"remember-device-option\" class=\"remember-device-option\" for=\"remember-device\" hidden", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"remember-device-unavailable\"", markup, StringComparison.Ordinal);
        Assert.Contains("保持登入需要固定網址", markup, StringComparison.Ordinal);
        Assert.Contains("type=\"checkbox\" checked", markup, StringComparison.Ordinal);
        Assert.Contains("不保存數字密碼", markup, StringComparison.Ordinal);
        Assert.Contains("`${API_ROOT}/auth/devices/enroll`", script, StringComparison.Ordinal);
        Assert.Contains("`${API_ROOT}/auth/devices/refresh`", script, StringComparison.Ordinal);
        Assert.Contains("state.supportsRememberedDevices = pick(status, \"supportsRememberedDevices\") === true;", script, StringComparison.Ordinal);
        Assert.Contains("option.hidden = !state.supportsRememberedDevices;", script, StringComparison.Ordinal);
        Assert.Contains("unavailable.hidden = state.supportsRememberedDevices;", script, StringComparison.Ordinal);
        Assert.Contains("if (!state.supportsRememberedDevices || !state.csrfToken || !navigator.onLine)", script, StringComparison.Ordinal);
        Assert.Contains("if (state.supportsRememberedDevices && $(\"#remember-device\").checked)", script, StringComparison.Ordinal);
        Assert.Contains("csrfToken: state.antiforgeryToken", script, StringComparison.Ordinal);
        Assert.Contains("retryable: false", script, StringComparison.Ordinal);
        Assert.Contains("body: { requestId: getOrCreateDeviceRefreshRequestId() }", script, StringComparison.Ordinal);
        Assert.Contains("mcsv-device-refresh-request-v1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage.setItem(\"username", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localStorage.setItem(\"pin", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localStorage.setItem(\"token", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("document.cookie", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FixedOriginPolling_RecoversWithBoundedBackoffWithoutQueueingMutations()
    {
        var script = ReadWebAsset("app.js");

        Assert.Contains("const RECOVERY_BACKOFF_BASE_MS = 1000;", script, StringComparison.Ordinal);
        Assert.Contains("const RECOVERY_BACKOFF_MAX_MS = 30000;", script, StringComparison.Ordinal);
        Assert.Contains("const RECOVERY_BACKOFF_MAX_EXPONENT = 5;", script, StringComparison.Ordinal);
        Assert.Contains("function recordConnectionFailure()", script, StringComparison.Ordinal);
        Assert.Contains("RECOVERY_BACKOFF_BASE_MS * (2 ** exponent)", script, StringComparison.Ordinal);
        Assert.Contains("const delay = Math.min(", script, StringComparison.Ordinal);
        Assert.Contains("RECOVERY_BACKOFF_MAX_MS,", script, StringComparison.Ordinal);
        Assert.Contains("succeeded ? currentPollInterval() : recordConnectionFailure()", script, StringComparison.Ordinal);
        Assert.Contains("function scheduleAuthenticationRecovery(context, delay = null)", script, StringComparison.Ordinal);
        Assert.Contains("scheduleAuthenticationRecovery(context);", script, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener(\"offline\"", script, StringComparison.Ordinal);
        Assert.Contains("abortReadRequests();", script, StringComparison.Ordinal);
        Assert.Contains("showAlert(t(\"web.connection.offlineNotice\"))", script, StringComparison.Ordinal);

        Assert.Contains("if (state.mutationsInFlight.has(key))", script, StringComparison.Ordinal);
        Assert.Contains("showToast(t(\"web.action.pending\"", script, StringComparison.Ordinal);
        Assert.Contains("headers.set(\"Idempotency-Key\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("BackgroundSync", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mutationQueue", script, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadWebAsset(string fileName)
    {
        var assembly = typeof(RemoteControlHost).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith($".Web.{fileName}", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
