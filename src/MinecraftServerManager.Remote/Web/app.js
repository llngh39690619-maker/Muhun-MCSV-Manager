"use strict";

(() => {
    const API_ROOT = "/api/v1";
    const POLL_INTERVALS = Object.freeze({
        dashboard: 5000,
        server: 4000,
        console: 2000,
        players: 5000
    });
    const MAX_CONSOLE_LINES = 300;
    const CONSOLE_PAGE_SIZE = 200;
    const DEFAULT_LOGIN_RETRY_SECONDS = 60;
    const MAX_LOGIN_RETRY_SECONDS = 24 * 60 * 60;
    const DEVICE_REFRESH_REQUEST_KEY = "mcsv-device-refresh-request-v1";
    const RECOVERY_BACKOFF_BASE_MS = 1000;
    const RECOVERY_BACKOFF_MAX_MS = 30000;
    const RECOVERY_BACKOFF_MAX_EXPONENT = 5;
    const LOCALIZATION_SCHEMA_VERSION = 1;
    const FALLBACK_CULTURE = "zh-TW";
    const SUPPORTED_CULTURES = Object.freeze([FALLBACK_CULTURE, "en-US"]);
    const LANGUAGE_STORAGE_KEY = "mcsv-language-v1";
    const BACKUP_RESTORE_CONFIRMATION = "RESTORE STOPPED SERVER BACKUP";
    const MAX_BACKUP_ENTRIES = 200;
    const MAX_ADMINISTRATION_ADDONS = 200;
    const MAX_ADDON_FILE_NAME_CHARACTERS = 160;
    const MAX_JAVA_METADATA_CHARACTERS = 64;

    const state = {
        authenticated: false,
        authStatusReady: false,
        credentialRegistered: null,
        rememberedDevice: false,
        supportsRememberedDevices: false,
        deviceRestoreAttempted: false,
        csrfToken: "",
        antiforgeryToken: "",
        login: "",
        username: "",
        permissions: {
            startServer: false,
            stopServer: false,
            restartServer: false,
            sendConsoleCommand: false,
            managePlayers: false,
            createBackup: false
        },
        permissionGrants: null,
        updateStatus: null,
        currentView: "dashboard",
        currentServerId: null,
        currentServer: null,
        serverTab: "overview",
        consoleStream: "ordinary",
        servers: [],
        serverListsSignature: "",
        players: [],
        playersSignature: "",
        backups: [],
        backupServerId: null,
        backupsLoaded: false,
        backupError: false,
        administration: null,
        administrationServerId: null,
        administrationLoaded: false,
        administrationError: false,
        consoleStreams: new Map(),
        pollTimer: 0,
        connectionFailureCount: 0,
        toastTimer: 0,
        loginRetryUntil: 0,
        loginRetryTimer: 0,
        readControllers: new Set(),
        mutationControllers: new Set(),
        readsInFlight: new Map(),
        mutationsInFlight: new Map(),
        culture: FALLBACK_CULTURE,
        strings: Object.create(null)
    };

    class ApiError extends Error {
        constructor(message, status = 0, code = "request_failed", details = null, retryAfterSeconds = null) {
            super(message);
            this.name = "ApiError";
            this.status = status;
            this.code = code;
            this.details = details;
            this.retryAfterSeconds = retryAfterSeconds;
        }
    }

    const $ = (selector, root = document) => root.querySelector(selector);
    const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

    function normalizeCulture(value) {
        try {
            const language = new Intl.Locale(String(value || "").trim()).language.toLowerCase();
            if (language === "en") return "en-US";
            if (language === "zh") return FALLBACK_CULTURE;
        } catch {
            // Invalid and unsupported BCP-47 tags always fall back to Traditional Chinese.
        }
        return FALLBACK_CULTURE;
    }

    function preferredCulture() {
        const requestedCulture = new URL(window.location.href).searchParams.get("culture");
        if (requestedCulture !== null) {
            return normalizeCulture(requestedCulture);
        }
        try {
            const saved = window.localStorage.getItem(LANGUAGE_STORAGE_KEY);
            if (SUPPORTED_CULTURES.includes(saved)) {
                return saved;
            }
        } catch {
            // Storage may be unavailable in private browsing; browser languages remain usable.
        }
        for (const language of navigator.languages || [navigator.language]) {
            const normalized = normalizeCulture(language);
            if (normalized === "en-US" || String(language || "").toLowerCase().startsWith("zh")) {
                return normalized;
            }
        }
        return FALLBACK_CULTURE;
    }

    function formatLocalized(template, argumentsList) {
        const escapedOpen = "\u0000";
        const escapedClose = "\u0001";
        return template
            .replaceAll("{{", escapedOpen)
            .replaceAll("}}", escapedClose)
            .replace(/\{([0-9]+)(?:,[^}:]+)?(?::[^}]+)?\}/gu, (match, rawIndex) => {
                const index = Number(rawIndex);
                return index < argumentsList.length ? String(argumentsList[index] ?? "") : match;
            })
            .replaceAll(escapedOpen, "{")
            .replaceAll(escapedClose, "}");
    }

    function t(key, ...argumentsList) {
        const value = state.strings[key];
        if (typeof value !== "string" || value.length === 0) {
            return key;
        }
        return formatLocalized(value, argumentsList);
    }

    function applyLocalizationToDocument() {
        document.documentElement.lang = state.culture;
        for (const node of $$('[data-i18n]')) {
            node.textContent = t(node.dataset.i18n);
        }
        for (const node of $$('[data-i18n-placeholder]')) {
            node.setAttribute("placeholder", t(node.dataset.i18nPlaceholder));
        }
        for (const node of $$('[data-i18n-aria-label]')) {
            node.setAttribute("aria-label", t(node.dataset.i18nAriaLabel));
        }
        const selector = $("#language-selector");
        if (selector) selector.value = state.culture;
        const location = new URL(window.location.href);
        location.searchParams.set("culture", state.culture);
        window.history.replaceState(window.history.state, "", location);
        const manifest = $('link[rel="manifest"]');
        if (manifest) {
            manifest.href = `/manifest.webmanifest?culture=${encodeURIComponent(state.culture)}`;
        }
        document.title = t("web.document.title");
    }

    async function loadLocalization(requestedCulture, persist = false) {
        const culture = normalizeCulture(requestedCulture);
        const response = await fetch(`/localization/${encodeURIComponent(culture)}.json`, {
            method: "GET",
            credentials: "same-origin",
            cache: "no-store",
            redirect: "error",
            headers: { Accept: "application/json" }
        });
        if (!response.ok || !(response.headers.get("content-type") || "").includes("json")) {
            throw new TypeError("Localization catalog is unavailable.");
        }
        const payload = await response.json();
        const schemaVersion = pick(payload, "schemaVersion");
        const payloadCulture = normalizeCulture(pick(payload, "culture"));
        const strings = pick(payload, "strings");
        if (schemaVersion !== LOCALIZATION_SCHEMA_VERSION || payloadCulture !== culture ||
            !strings || typeof strings !== "object" || Array.isArray(strings)) {
            throw new TypeError("Localization catalog is invalid.");
        }
        const requiredKeys = ["web.document.title", "web.auth.login", "web.error.default", "web.state.unknown"];
        if (requiredKeys.some(key => typeof strings[key] !== "string" || strings[key].length === 0)) {
            throw new TypeError("Localization catalog is incomplete.");
        }
        state.culture = culture;
        state.strings = Object.freeze({ ...strings });
        if (persist) {
            try {
                window.localStorage.setItem(LANGUAGE_STORAGE_KEY, culture);
            } catch {
                // Language still applies for this session when storage is unavailable.
            }
        }
        applyLocalizationToDocument();
    }

    function pick(source, ...keys) {
        if (!source || typeof source !== "object") {
            return undefined;
        }

        for (const key of keys) {
            if (Object.prototype.hasOwnProperty.call(source, key)) {
                return source[key];
            }

            const pascal = `${key.charAt(0).toUpperCase()}${key.slice(1)}`;
            if (Object.prototype.hasOwnProperty.call(source, pascal)) {
                return source[pascal];
            }
        }

        return undefined;
    }

    function arrayFrom(source, ...keys) {
        if (Array.isArray(source)) {
            return source;
        }

        for (const key of keys) {
            const candidate = pick(source, key);
            if (Array.isArray(candidate)) {
                return candidate;
            }
        }

        return [];
    }

    function text(value, fallback = "—") {
        if (value === null || value === undefined || value === "") {
            return fallback;
        }
        return String(value);
    }

    function element(tagName, className, content) {
        const node = document.createElement(tagName);
        if (className) {
            node.className = className;
        }
        if (content !== undefined && content !== null) {
            node.textContent = String(content);
        }
        return node;
    }

    function normalizeState(rawState) {
        const numericStates = ["stopped", "starting", "running", "stopping", "crashed", "faulted"];
        if (Number.isInteger(rawState) && rawState >= 0 && rawState < numericStates.length) {
            return numericStates[rawState];
        }

        const normalized = String(rawState ?? "unknown").trim().toLowerCase();
        const aliases = {
            stop: "stopped",
            start: "starting",
            run: "running",
            crash: "crashed",
            fault: "faulted",
            failed: "faulted",
            error: "faulted"
        };
        return aliases[normalized] || normalized || "unknown";
    }

    function stateLabel(rawState) {
        const normalized = normalizeState(rawState);
        const known = ["stopped", "starting", "running", "stopping", "crashed", "faulted", "unknown"];
        return known.includes(normalized)
            ? t(`web.state.${normalized}`)
            : text(rawState, t("web.state.unknown"));
    }

    function serverId(server) {
        return text(pick(server, "id", "serverId"), "");
    }

    function serverName(server) {
        return text(pick(server, "name"), t("web.server.unnamed"));
    }

    function isServerRunning(server) {
        const explicit = pick(server, "running", "isRunning");
        return typeof explicit === "boolean" ? explicit : normalizeState(pick(server, "state")) === "running";
    }

    function finiteNumber(value) {
        if (value === null || value === undefined || value === "") {
            return null;
        }
        const number = Number(value);
        return Number.isFinite(number) ? number : null;
    }

    function formatPercent(value) {
        const number = finiteNumber(value);
        return number === null ? "—" : `${number.toFixed(1)}%`;
    }

    function formatBytes(value) {
        let bytes = finiteNumber(value);
        if (bytes === null || bytes < 0) {
            return "—";
        }
        if (bytes === 0) {
            return "0 B";
        }

        const units = ["B", "KB", "MB", "GB", "TB"];
        const unitIndex = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
        bytes /= 1024 ** unitIndex;
        const precision = unitIndex < 2 || bytes >= 100 ? 0 : 1;
        return `${bytes.toFixed(precision)} ${units[unitIndex]}`;
    }

    function formatUptime(value) {
        const parsed = finiteNumber(value);
        if (parsed === null) {
            return "—";
        }
        const seconds = Math.max(0, parsed);
        const days = Math.floor(seconds / 86400);
        const hours = Math.floor((seconds % 86400) / 3600);
        const minutes = Math.floor((seconds % 3600) / 60);
        if (days > 0) {
            return t("web.time.daysHours", days, hours);
        }
        if (hours > 0) {
            return t("web.time.hoursMinutes", hours, minutes);
        }
        return t("web.time.minutes", minutes);
    }

    function formatTime(value, includeDate = false) {
        if (!value) {
            return "—";
        }
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return text(value);
        }
        const options = includeDate
            ? { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false }
            : { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false };
        return new Intl.DateTimeFormat(state.culture, options).format(date);
    }

    function setConnectionStatus(mode, label) {
        const status = $("#connection-status");
        status.className = `status-pill is-${mode}`;
        $("#connection-status-text").textContent = label;
        status.setAttribute("aria-label", t("web.status.aria", label));
    }

    function showAlert(message) {
        $("#alert-message").textContent = message;
        $("#alert-banner").hidden = false;
    }

    function dismissAlert() {
        $("#alert-banner").hidden = true;
        $("#alert-message").textContent = "";
    }

    function showToast(message) {
        const toast = $("#toast");
        window.clearTimeout(state.toastTimer);
        toast.textContent = message;
        toast.hidden = false;
        state.toastTimer = window.setTimeout(() => {
            toast.hidden = true;
        }, 4200);
    }

    function parseRetryAfterSeconds(value) {
        const normalized = String(value ?? "").trim();
        if (!normalized) {
            return null;
        }

        if (/^[0-9]+$/u.test(normalized)) {
            const seconds = Number(normalized);
            return Number.isSafeInteger(seconds) ? Math.max(0, seconds) : null;
        }

        const retryAt = Date.parse(normalized);
        return Number.isFinite(retryAt)
            ? Math.max(0, Math.ceil((retryAt - Date.now()) / 1000))
            : null;
    }

    function formatLoginRetryTime(retryUntil) {
        return new Intl.DateTimeFormat(state.culture, {
            hour: "2-digit",
            minute: "2-digit",
            second: "2-digit",
            hour12: false
        }).format(new Date(retryUntil));
    }

    function loginRetryMessage() {
        return t("web.auth.lockedUntil", formatLoginRetryTime(state.loginRetryUntil));
    }

    function remainingLoginRetrySeconds() {
        if (!state.loginRetryUntil) {
            return 0;
        }
        if (state.loginRetryUntil <= Date.now()) {
            window.clearTimeout(state.loginRetryTimer);
            state.loginRetryTimer = 0;
            state.loginRetryUntil = 0;
            return 0;
        }
        return Math.max(1, Math.ceil((state.loginRetryUntil - Date.now()) / 1000));
    }

    function clearLoginRetry({ clearMessage = true } = {}) {
        window.clearTimeout(state.loginRetryTimer);
        state.loginRetryTimer = 0;
        state.loginRetryUntil = 0;
        if (clearMessage) {
            const errorNode = $("#login-error");
            if (errorNode?.dataset.errorKind === "retry-lock") {
                errorNode.textContent = "";
                errorNode.hidden = true;
                delete errorNode.dataset.errorKind;
            }
        }
    }

    function suspendLoginRetryTimer() {
        window.clearTimeout(state.loginRetryTimer);
        state.loginRetryTimer = 0;
    }

    function finishLoginRetry() {
        state.loginRetryTimer = 0;
        if (remainingLoginRetrySeconds() > 0) {
            scheduleLoginRetryTimer();
            return;
        }
        clearLoginRetry();
        updateLoginAvailability();
    }

    function scheduleLoginRetryTimer() {
        window.clearTimeout(state.loginRetryTimer);
        const remainingMilliseconds = Math.max(0, state.loginRetryUntil - Date.now());
        state.loginRetryTimer = window.setTimeout(finishLoginRetry, remainingMilliseconds + 50);
    }

    function startLoginRetry(retryAfterSeconds) {
        const requestedSeconds = Number.isFinite(retryAfterSeconds)
            ? Math.ceil(retryAfterSeconds)
            : DEFAULT_LOGIN_RETRY_SECONDS;
        const boundedSeconds = Math.min(
            MAX_LOGIN_RETRY_SECONDS,
            Math.max(1, requestedSeconds));
        state.loginRetryUntil = Math.max(
            state.loginRetryUntil,
            Date.now() + boundedSeconds * 1000);
        scheduleLoginRetryTimer();
        updateLoginAvailability();
        return loginRetryMessage();
    }

    function userFacingError(error, fallback = t("web.error.default")) {
        if (error instanceof ApiError) {
            if (error.status === 401) return t("web.error.unauthorized");
            if (error.status === 403) return t("web.error.forbidden");
            if (error.status === 409) return error.message || t("web.error.conflict");
            if (error.status === 429 && Number.isFinite(error.retryAfterSeconds)) {
                const retryAt = Date.now() + Math.max(1, error.retryAfterSeconds) * 1000;
                return t("web.error.rateLimitedUntil", formatLoginRetryTime(retryAt));
            }
            if (error.status === 429) return t("web.error.rateLimited");
            return error.message || fallback;
        }
        if (error instanceof TypeError) {
            return t("web.error.unreachable");
        }
        return error?.message || fallback;
    }

    function handleRequestError(error, context) {
        if (error?.name === "AbortError") {
            return;
        }

        if (error instanceof ApiError && error.status === 401) {
            if (!state.readsInFlight.has("auth-status")) {
                resetSessionState();
                showAuthenticationView();
                void refreshAuthenticationForLogin(t("web.error.refreshAuth"));
            }
        } else if (error instanceof ApiError && error.status === 403 && state.authenticated) {
            void refreshPermissionsAfterForbidden();
        }

        const message = `${context}：${userFacingError(error)}`;
        showAlert(message);
        setConnectionStatus(
            navigator.onLine ? "error" : "offline",
            navigator.onLine ? t("web.connection.error") : t("web.connection.offline"));
    }

    function abortReadRequests() {
        for (const controller of state.readControllers) {
            controller.abort();
        }
        state.readControllers.clear();
    }

    async function apiRequest(path, options = {}) {
        if (!path.startsWith(`${API_ROOT}/`)) {
            throw new Error(t("web.error.blockedPath"));
        }

        const method = options.method || "GET";
        const mutation = options.mutation === true || method !== "GET";
        const controller = new AbortController();
        const controllerSet = mutation ? state.mutationControllers : state.readControllers;
        controllerSet.add(controller);

        const headers = new Headers({ Accept: "application/json" });
        headers.set("X-MCSV-Culture", state.culture);
        if (options.body !== undefined) {
            headers.set("Content-Type", "application/json");
        }
        if (mutation) {
            const csrfToken = options.csrfToken || state.csrfToken;
            if (!csrfToken) {
                controllerSet.delete(controller);
                throw new ApiError(t("web.error.csrfUnavailable"), 403, "csrf_unavailable");
            }
            headers.set("X-MCSV-CSRF", csrfToken);
            headers.set("Idempotency-Key", options.idempotencyKey || crypto.randomUUID());
        }

        try {
            const requestBody = options.body === undefined ? undefined : JSON.stringify(options.body);
            let response;
            for (let attempt = 0; attempt < 2; attempt += 1) {
                try {
                    response = await fetch(path, {
                        method,
                        headers,
                        credentials: "same-origin",
                        cache: "no-store",
                        redirect: "error",
                        signal: controller.signal,
                        body: requestBody
                    });
                    break;
                } catch (error) {
                    const canSafelyRetry = mutation
                        && options.retryable !== false
                        && attempt === 0
                        && error instanceof TypeError
                        && navigator.onLine
                        && !controller.signal.aborted;
                    if (!canSafelyRetry) {
                        throw error;
                    }
                    // The server binds this unchanged Idempotency-Key to the exact route/body,
                    // so a lost response can be retried once without repeating the operation.
                    await new Promise(resolve => window.setTimeout(resolve, 300));
                }
            }
            if (!response) {
                throw new TypeError(t("web.error.noResponse"));
            }

            const contentType = response.headers.get("content-type") || "";
            let payload = null;
            if (response.status !== 204) {
                if (contentType.includes("json")) {
                    payload = await response.json();
                } else {
                    const responseText = await response.text();
                    payload = responseText ? { detail: responseText } : null;
                }
            }

            if (!response.ok) {
                const message = text(pick(payload, "detail", "message", "title"), `HTTP ${response.status}`);
                const code = text(pick(payload, "code"), "request_failed");
                const retryAfterSeconds = parseRetryAfterSeconds(response.headers.get("Retry-After"));
                throw new ApiError(message, response.status, code, payload, retryAfterSeconds);
            }

            setConnectionStatus("online", t("web.connection.connected"));
            state.connectionFailureCount = 0;
            return payload;
        } finally {
            controllerSet.delete(controller);
        }
    }

    function runReadOnce(key, request) {
        if (state.readsInFlight.has(key)) {
            return state.readsInFlight.get(key);
        }
        const pending = Promise.resolve()
            .then(request)
            .finally(() => state.readsInFlight.delete(key));
        state.readsInFlight.set(key, pending);
        return pending;
    }

    async function runMutationOnce(key, request) {
        if (state.mutationsInFlight.has(key)) {
            showToast(t("web.action.pending", t("common.unknown")));
            return null;
        }

        const pending = Promise.resolve()
            .then(request)
            .finally(() => {
                state.mutationsInFlight.delete(key);
                updateActionAvailability();
            });
        state.mutationsInFlight.set(key, pending);
        updateActionAvailability();
        return pending;
    }

    function showLoadingView() {
        $("#loading-view").hidden = false;
        $("#auth-view").hidden = true;
        $("#app-shell").hidden = true;
        $("#bottom-navigation").hidden = true;
        $("#signout-button").hidden = true;
    }

    function showAuthenticationView(status = null) {
        window.clearTimeout(state.pollTimer);
        abortReadRequests();
        $("#loading-view").hidden = true;
        $("#auth-view").hidden = false;
        $("#app-shell").hidden = true;
        $("#bottom-navigation").hidden = true;
        $("#signout-button").hidden = true;

        const credentialRegistered = pick(status, "credentialRegistered");
        if (typeof credentialRegistered === "boolean") {
            state.credentialRegistered = credentialRegistered;
        }
        updateLoginAvailability();
        if (!$("#login-form").hidden && state.authStatusReady) {
            const usernameInput = $("#login-username");
            (usernameInput.value ? $("#login-pin") : usernameInput).focus();
        }
    }

    function showApplication() {
        clearLoginRetry();
        $("#login-pin").value = "";
        $("#loading-view").hidden = true;
        $("#auth-view").hidden = true;
        $("#app-shell").hidden = false;
        $("#bottom-navigation").hidden = false;
        $("#signout-button").hidden = false;
        updateActionAvailability();
        dismissAlert();
        navigate(state.currentView === "server" && state.currentServerId ? "server" : "dashboard", { replace: true });
    }

    async function loadAuthStatus() {
        state.authStatusReady = false;
        updateLoginAvailability();
        return runReadOnce("auth-status", async () => {
            let status = await apiRequest(`${API_ROOT}/auth/status`);
            applyAuthStatus(status);
            if (!state.authenticated && !state.deviceRestoreAttempted) {
                state.deviceRestoreAttempted = true;
                const restored = await restoreRememberedDevice();
                if (restored) {
                    status = restored;
                    applyAuthStatus(status);
                }
            }

            state.authStatusReady = true;
            if (state.login) {
                $("#host-subtitle").textContent = state.login;
            }

            if (state.authenticated) {
                showApplication();
            } else {
                showAuthenticationView(status);
            }
            return status;
        });
    }

    async function refreshPermissionsAfterForbidden() {
        try {
            await runReadOnce("authorization-refresh", async () => {
                const status = await apiRequest(`${API_ROOT}/auth/status`);
                applyAuthStatus(status);
                if (!state.authenticated) {
                    resetSessionState();
                    showAuthenticationView(status);
                    return;
                }
                updateActionAvailability();
                if ((state.serverTab === "console" && !permissionsForServer().readConsole) ||
                    (state.serverTab === "players" && !permissionsForServer().readPlayers)) {
                    selectServerTab("overview", false);
                }
            });
        } catch (refreshError) {
            if (refreshError instanceof ApiError && refreshError.status === 401) {
                resetSessionState();
                showAuthenticationView();
            }
        }
    }

    function applyAuthStatus(status) {
        state.authenticated = pick(status, "authenticated") === true;
        state.csrfToken = text(pick(status, "csrfToken"), "");
        state.login = text(pick(status, "login"), "");
        state.username = text(pick(status, "username"), state.username);
        state.rememberedDevice = pick(status, "rememberedDevice") === true;
        state.supportsRememberedDevices = pick(status, "supportsRememberedDevices") === true;
        const antiforgeryToken = text(pick(status, "antiforgeryToken"), "");
        if (antiforgeryToken) {
            state.antiforgeryToken = antiforgeryToken;
        }
        applyPermissions(status);
        const credentialRegistered = pick(status, "credentialRegistered");
        state.credentialRegistered = typeof credentialRegistered === "boolean"
            ? credentialRegistered
            : null;
        updateRememberedDeviceAvailability();
    }

    function updateRememberedDeviceAvailability() {
        const option = $("#remember-device-option");
        const input = $("#remember-device");
        const unavailable = $("#remember-device-unavailable");
        option.hidden = !state.supportsRememberedDevices;
        unavailable.hidden = state.supportsRememberedDevices;
        input.disabled = !state.supportsRememberedDevices;
        if (!state.supportsRememberedDevices) {
            input.checked = false;
        }
    }

    function getOrCreateDeviceRefreshRequestId() {
        try {
            const stored = window.localStorage.getItem(DEVICE_REFRESH_REQUEST_KEY);
            if (/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(stored ?? "")) {
                return stored;
            }
            const created = crypto.randomUUID();
            window.localStorage.setItem(DEVICE_REFRESH_REQUEST_KEY, created);
            return created;
        } catch {
            return crypto.randomUUID();
        }
    }

    function clearDeviceRefreshRequestId() {
        try {
            window.localStorage.removeItem(DEVICE_REFRESH_REQUEST_KEY);
        } catch {
            // Storage may be unavailable in private browsing. The HttpOnly device cookie
            // remains the only bearer credential and is never exposed to this script.
        }
    }

    async function restoreRememberedDevice() {
        if (!state.supportsRememberedDevices || !state.csrfToken || !navigator.onLine) {
            return null;
        }

        try {
            const restored = await runMutationOnce("auth:restore-device", () => apiRequest(`${API_ROOT}/auth/devices/refresh`, {
                method: "POST",
                mutation: true,
                retryable: false,
                body: { requestId: getOrCreateDeviceRefreshRequestId() }
            }));
            if (restored) {
                clearDeviceRefreshRequestId();
            }
            return restored;
        } catch (error) {
            if (error instanceof ApiError && error.status === 401) {
                clearDeviceRefreshRequestId();
                return null;
            }
            state.deviceRestoreAttempted = false;
            throw error;
        }
    }

    function deviceDisplayName() {
        const standalone = window.matchMedia?.("(display-mode: standalone)").matches === true
            || window.navigator.standalone === true;
        const appleMobile = /iPhone|iPad|iPod/u.test(navigator.userAgent)
            || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
        return `${appleMobile ? t("web.device.apple") : t("web.device.mobile")} · ${standalone ? t("web.device.standalone") : t("web.device.browser")}`;
    }

    function applyPermissions(status) {
        const permissions = pick(status, "permissions");
        state.permissions = {
            startServer: pick(permissions, "canStartServer") === true,
            stopServer: pick(permissions, "canStopServer") === true,
            restartServer: pick(permissions, "canRestartServer") === true,
            sendConsoleCommand: pick(permissions, "canSendConsoleCommand") === true,
            managePlayers: pick(permissions, "canManagePlayers") === true,
            createBackup: pick(permissions, "canCreateBackup") === true
        };
        const grants = pick(status, "permissionGrants");
        state.permissionGrants = Array.isArray(grants)
            ? grants.filter(grant => grant && typeof pick(grant, "permissionCode") === "string")
            : null;
    }

    function hasServerPermission(permissionCode, serverId, legacyPermission) {
        if (!Array.isArray(state.permissionGrants)) {
            return ["readServer", "readConsole", "readPlayers"].includes(legacyPermission)
                ? state.authenticated === true
                : state.permissions[legacyPermission] === true;
        }
        const canonicalServerId = value => text(value, "")
            .replace(/[{}-]/gu, "")
            .toLowerCase();
        const target = canonicalServerId(serverId);
        return state.permissionGrants.some(grant => {
            if (text(pick(grant, "permissionCode"), "") !== permissionCode) {
                return false;
            }
            const scope = text(pick(grant, "scope"), "").toLowerCase();
            if (scope === "global") {
                return true;
            }
            return scope === "server" &&
                target.length > 0 &&
                canonicalServerId(pick(grant, "serverId")) === target;
        });
    }

    function hasGlobalPermission(permissionCode) {
        if (!Array.isArray(state.permissionGrants)) {
            return false;
        }
        return state.permissionGrants.some(grant =>
            text(pick(grant, "permissionCode"), "") === permissionCode &&
            text(pick(grant, "scope"), "").toLowerCase() === "global");
    }

    function permissionsForServer(serverId = state.currentServerId) {
        return {
            readServer: hasServerPermission("server.read", serverId, "readServer"),
            readConsole: hasServerPermission("console.read", serverId, "readConsole"),
            readPlayers: hasServerPermission("player.read", serverId, "readPlayers"),
            startServer: hasServerPermission("server.start", serverId, "startServer"),
            stopServer: hasServerPermission("server.stop", serverId, "stopServer"),
            restartServer: hasServerPermission("server.restart", serverId, "restartServer"),
            sendConsoleCommand: hasServerPermission("console.write", serverId, "sendConsoleCommand"),
            managePlayers: hasServerPermission("player.manage", serverId, "managePlayers"),
            readBackups: hasServerPermission("backup.read", serverId, "readBackups"),
            createBackup: hasServerPermission("backup.create", serverId, "createBackup"),
            restoreBackup: hasServerPermission("backup.restore", serverId, "restoreBackup")
        };
    }

    function updateLoginAvailability() {
        const form = $("#login-form");
        const unavailable = $("#credential-unavailable");
        const readiness = $("#login-readiness");
        const button = $("#login-button");
        const hasCredential = state.credentialRegistered !== false;
        const pending = state.mutationsInFlight.has("auth:login");
        const loginLocked = remainingLoginRetrySeconds() > 0;
        const ready = hasCredential && state.authStatusReady && Boolean(state.csrfToken) && navigator.onLine && !loginLocked;

        form.hidden = !hasCredential;
        unavailable.hidden = hasCredential;
        readiness.hidden = ready;
        if (!ready) {
            readiness.textContent = loginLocked
                ? loginRetryMessage()
                : navigator.onLine
                    ? t("web.auth.securityLoading")
                    : t("web.auth.offline");
        }
        button.disabled = !ready || pending;
        button.textContent = pending ? t("web.auth.loggingIn") : t("web.auth.login");
    }

    async function refreshAuthenticationForLogin(context) {
        try {
            return await loadAuthStatus();
        } catch (error) {
            if (error?.name === "AbortError") {
                return null;
            }
            showAuthenticationView();
            showAlert(`${context}：${userFacingError(error)}`);
            setConnectionStatus(
                navigator.onLine ? "error" : "offline",
                navigator.onLine ? t("web.connection.error") : t("web.connection.offline"));
            if (navigator.onLine) {
                scheduleAuthenticationRecovery(context);
            }
            return null;
        }
    }

    async function login(event) {
        event.preventDefault();
        const usernameInput = $("#login-username");
        const pinInput = $("#login-pin");
        const errorNode = $("#login-error");
        errorNode.hidden = true;
        delete errorNode.dataset.errorKind;
        const username = usernameInput.value.trim();
        const pin = pinInput.value;

        if (remainingLoginRetrySeconds() > 0) {
            errorNode.textContent = loginRetryMessage();
            errorNode.dataset.errorKind = "retry-lock";
            errorNode.hidden = false;
            updateLoginAvailability();
            return;
        }

        if (!state.authStatusReady || !state.csrfToken) {
            errorNode.textContent = t("web.auth.securityNotReady");
            errorNode.hidden = false;
            await refreshAuthenticationForLogin(t("web.error.loadAuth"));
            return;
        }

        if (!/^[A-Za-z][A-Za-z0-9]{5,31}$/u.test(username)) {
            errorNode.textContent = t("web.auth.usernameInvalid");
            errorNode.hidden = false;
            usernameInput.focus();
            return;
        }

        if (!/^[0-9]{4,12}$/u.test(pin)) {
            errorNode.textContent = t("web.auth.pinInvalid");
            errorNode.hidden = false;
            pinInput.focus();
            return;
        }

        try {
            const status = await runMutationOnce("auth:login", () => apiRequest(`${API_ROOT}/auth/login`, {
                method: "POST",
                mutation: true,
                retryable: false,
                body: { username, pin }
            }));
            if (!status) {
                return;
            }
            state.authenticated = pick(status, "authenticated") === true;
            state.csrfToken = text(pick(status, "csrfToken"), state.csrfToken);
            state.login = text(pick(status, "login"), state.login);
            state.username = text(pick(status, "username"), username);
            applyPermissions(status);
            pinInput.value = "";
            if (!state.authenticated) {
                throw new ApiError(t("web.auth.incomplete"), 401, "login_failed");
            }

            let remembered = false;
            let enrollmentErrorMessage = "";
            if (state.supportsRememberedDevices && $("#remember-device").checked) {
                try {
                    const enrolled = await runMutationOnce("auth:enroll-device", () => apiRequest(`${API_ROOT}/auth/devices/enroll`, {
                        method: "POST",
                        mutation: true,
                        retryable: false,
                        body: { deviceName: deviceDisplayName() }
                    }));
                    if (enrolled) {
                        applyAuthStatus(enrolled);
                        remembered = state.rememberedDevice;
                    }
                } catch (enrollError) {
                    enrollmentErrorMessage = t("web.auth.rememberFailed", userFacingError(enrollError));
                }
            }
            showApplication();
            if (enrollmentErrorMessage) {
                showAlert(enrollmentErrorMessage);
            }
            showToast(remembered ? t("web.auth.loginRemembered") : t("web.auth.loginSuccess"));
        } catch (error) {
            if (error instanceof ApiError && error.status === 401) {
                state.authStatusReady = false;
                state.csrfToken = "";
                updateLoginAvailability();
                await refreshAuthenticationForLogin(t("web.error.refreshAuth"));
            }
            if (error instanceof ApiError && error.status === 429) {
                errorNode.textContent = startLoginRetry(error.retryAfterSeconds);
                errorNode.dataset.errorKind = "retry-lock";
            } else {
                errorNode.textContent = userFacingError(error, t("web.auth.loginFailed"));
            }
            errorNode.hidden = false;
            if (!$("#login-form").hidden) {
                pinInput.select();
            }
        } finally {
            updateLoginAvailability();
        }
    }

    async function signOut() {
        const confirmed = await confirmAction(
            t("web.auth.signOutTitle"),
            t("web.auth.signOutMessage"),
            t("web.signOut"));
        if (!confirmed) {
            return;
        }

        try {
            await runMutationOnce("auth:signout", () => apiRequest(`${API_ROOT}/auth/signout`, {
                method: "POST",
                mutation: true,
                csrfToken: state.antiforgeryToken,
                body: {}
            }));
        } catch (error) {
            if (!(error instanceof ApiError && error.status === 401)) {
                handleRequestError(error, t("web.auth.signOutFailed"));
                return;
            }
        }

        resetSessionState({ suppressDeviceRestore: true });
        showAuthenticationView();
        await refreshAuthenticationForLogin(t("web.error.refreshAuth"));
        showToast(t("web.auth.signOutSuccess"));
    }

    function resetSessionState({ suppressDeviceRestore = false } = {}) {
        clearLoginRetry();
        state.authenticated = false;
        state.authStatusReady = false;
        state.credentialRegistered = null;
        state.rememberedDevice = false;
        state.supportsRememberedDevices = false;
        state.deviceRestoreAttempted = suppressDeviceRestore;
        state.csrfToken = "";
        state.antiforgeryToken = "";
        state.username = "";
        applyPermissions(null);
        state.currentServerId = null;
        state.currentServer = null;
        state.servers = [];
        state.serverListsSignature = "";
        state.players = [];
        state.playersSignature = "";
        state.backups = [];
        state.backupServerId = null;
        state.backupsLoaded = false;
        state.backupError = false;
        state.administration = null;
        state.administrationServerId = null;
        state.administrationLoaded = false;
        state.administrationError = false;
        state.consoleStreams.clear();
        state.updateStatus = null;
        $("#product-update-card").hidden = true;
        updateRememberedDeviceAvailability();
        window.clearTimeout(state.pollTimer);
        abortReadRequests();
        updateLoginAvailability();
    }

    function navigate(route, options = {}) {
        if (!state.authenticated) {
            return;
        }

        if (route === "server" && !state.currentServerId) {
            route = "servers";
        }
        state.currentView = route;
        for (const view of $$(".page-view")) {
            view.hidden = view.id !== `${route}-view`;
        }
        for (const button of $$("#bottom-navigation [data-route]")) {
            const activeRoute = route === "server" ? "servers" : route;
            if (button.dataset.route === activeRoute) {
                button.setAttribute("aria-current", "page");
            } else {
                button.removeAttribute("aria-current");
            }
        }

        if (!options.replace) {
            window.scrollTo({ top: 0, behavior: "instant" });
        }
        refreshCurrentView();
        schedulePoll();
    }

    function openServer(id) {
        if (!id) {
            return;
        }
        state.currentServerId = id;
        state.currentServer = state.servers.find(server => serverId(server) === id) || null;
        state.players = [];
        state.playersSignature = "";
        state.backups = [];
        state.backupServerId = null;
        state.backupsLoaded = false;
        state.backupError = false;
        state.administration = null;
        state.administrationServerId = null;
        state.administrationLoaded = false;
        state.administrationError = false;
        state.serverTab = "overview";
        selectServerTab("overview", false);
        renderServerDetail();
        navigate("server");
    }

    function createServerCard(server) {
        const id = serverId(server);
        const name = serverName(server);
        const rawState = pick(server, "state");
        const normalizedState = normalizeState(rawState);
        const core = text(pick(server, "core", "coreType"), "Custom");
        const version = text(pick(server, "version", "minecraftVersion"), t("web.server.versionUnknown"));

        const button = element("button", "server-card");
        button.type = "button";
        button.dataset.serverId = id;
        button.disabled = !id;
        button.setAttribute("aria-label", t("web.server.cardAria", name, stateLabel(rawState)));
        button.addEventListener("click", () => openServer(id));

        button.append(element("span", "server-initial", core.charAt(0).toUpperCase() || "S"));
        const copy = element("span", "server-card-copy");
        copy.append(element("strong", "", name));
        const playerCount = finiteNumber(pick(server, "playerCount"));
        const playerSuffix = playerCount !== null ? t("web.server.playerSuffix", playerCount) : "";
        copy.append(element("small", "", `${core} · ${version}${playerSuffix}`));
        button.append(copy);
        button.append(element("span", `server-card-state state-${normalizedState}`, stateLabel(rawState)));
        return button;
    }

    function renderServerLists() {
        const search = $("#server-search").value.trim().toLocaleLowerCase(state.culture);
        const signature = JSON.stringify({
            search,
            servers: state.servers.map(server => [
                serverId(server),
                serverName(server),
                pick(server, "core", "coreType"),
                pick(server, "version", "minecraftVersion"),
                normalizeState(pick(server, "state")),
                pick(server, "playerCount")
            ])
        });
        if (signature === state.serverListsSignature) {
            return;
        }
        state.serverListsSignature = signature;
        const filtered = state.servers.filter(server => {
            const searchable = [
                serverName(server),
                pick(server, "core", "coreType"),
                pick(server, "version", "minecraftVersion"),
                stateLabel(pick(server, "state"))
            ].map(value => text(value, "").toLocaleLowerCase(state.culture)).join(" ");
            return !search || searchable.includes(search);
        });

        renderServerList($("#all-server-list"), filtered, search ? t("web.empty.search") : t("web.empty.servers"));
        renderServerList($("#dashboard-server-list"), state.servers.slice(0, 6), t("web.empty.servers"));
    }

    function renderServerList(container, servers, emptyMessage) {
        const focusedServerId = container.contains(document.activeElement)
            ? document.activeElement?.dataset?.serverId
            : null;
        const fragment = document.createDocumentFragment();
        if (servers.length === 0) {
            fragment.append(element("p", "empty-state", emptyMessage));
        } else {
            for (const server of servers) {
                fragment.append(createServerCard(server));
            }
        }
        container.replaceChildren(fragment);
        if (focusedServerId) {
            const matchingButton = [...container.querySelectorAll("[data-server-id]")]
                .find(button => button.dataset.serverId === focusedServerId);
            matchingButton?.focus({ preventScroll: true });
        }
    }

    function renderDashboard(payload) {
        state.servers = arrayFrom(payload, "servers");
        const running = state.servers.filter(isServerRunning);
        const players = state.servers.reduce((sum, server) => {
            const count = finiteNumber(pick(server, "playerCount"));
            return sum + (count ?? 0);
        }, 0);
        const memory = running.reduce((sum, server) => {
            const bytes = finiteNumber(pick(server, "memoryBytes", "workingSetBytes"));
            return sum + (bytes ?? 0);
        }, 0);

        $("#running-count").textContent = `${running.length} / ${state.servers.length}`;
        $("#server-count").textContent = String(state.servers.length);
        $("#online-player-count").textContent = String(players);
        $("#total-memory").textContent = formatBytes(memory);
        const generatedAt = pick(payload, "generatedAtUtc", "generatedAt");
        $("#dashboard-updated-at").textContent = generatedAt
            ? t("web.dashboard.updatedAt", formatTime(generatedAt, true))
            : t("web.dashboard.updatedNow");
        renderServerLists();
    }

    function renderProductUpdate(status) {
        state.updateStatus = status && typeof status === "object" ? status : null;
        const phase = text(pick(state.updateStatus, "phase"), "Disabled");
        const current = text(pick(state.updateStatus, "currentServiceVersion"), "—");
        const candidate = text(pick(state.updateStatus, "availableVersion"), "—");
        const message = text(pick(state.updateStatus, "message", "errorCode"), "");
        const consistency = pick(state.updateStatus, "installedVersionsMatch") === false
            ? "GUI / Service mismatch"
            : "GUI / Service aligned";
        $("#product-update-status").textContent =
            `${phase} · ${current} → ${candidate} · ${consistency}${message ? ` · ${message}` : ""}`;
        updateActionAvailability();
    }

    async function loadProductUpdateStatus() {
        const channel = $("#product-update-channel").value;
        return runReadOnce(`product-update:${channel}`, async () => {
            const status = await apiRequest(`${API_ROOT}/updates/${encodeURIComponent(channel)}`);
            renderProductUpdate(status);
            return status;
        });
    }

    async function performProductUpdate(action) {
        if (!hasGlobalPermission("update.manage") ||
            !["check", "download", "schedule"].includes(action)) {
            return;
        }
        if (action === "schedule") {
            const confirmed = await confirmAction(
                t("web.update.title"),
                t("web.update.hint"),
                t("web.update.apply"),
                false);
            if (!confirmed) return;
        }
        const channel = $("#product-update-channel").value;
        const key = `product-update:${channel}:${action}`;
        try {
            const result = await runMutationOnce(key, () => apiRequest(
                `${API_ROOT}/updates/${encodeURIComponent(channel)}/${action}`,
                {
                    method: "POST",
                    mutation: true,
                    body: action === "schedule"
                        ? { notBeforeUtc: new Date().toISOString() }
                        : {}
                }));
            if (!result) return;
            renderProductUpdate(pick(result, "status") || result);
            const resultStatus = pick(result, "status");
            showToast(text(
                pick(resultStatus, "message"),
                t(`web.update.${action === "schedule" ? "apply" : action}`)));
        } catch (error) {
            handleRequestError(error, t("web.update.title"));
        }
    }

    async function loadDashboard() {
        return runReadOnce("dashboard", async () => {
            const payload = await apiRequest(`${API_ROOT}/dashboard`);
            renderDashboard(payload);
            const canManageUpdates = hasGlobalPermission("update.manage");
            $("#product-update-card").hidden = !canManageUpdates;
            if (canManageUpdates) {
                await loadProductUpdateStatus();
            } else {
                state.updateStatus = null;
            }
            dismissAlert();
            return payload;
        });
    }

    function renderServerDetail() {
        const server = state.currentServer;
        if (!server) {
            return;
        }

        const rawState = pick(server, "state");
        const normalizedState = normalizeState(rawState);
        const name = serverName(server);
        const core = text(pick(server, "core", "coreType"), "Custom");
        const version = text(pick(server, "version", "minecraftVersion"), t("web.server.versionUnknown"));
        const playerCount = finiteNumber(pick(server, "playerCount"));
        const maxPlayers = finiteNumber(pick(server, "maxPlayers", "maximumPlayers"));

        $("#server-name").textContent = name;
        document.title = `${name} · ${t("web.brand")}`;
        $("#server-core").textContent = `${core} · ${version}`;
        const stateNode = $("#server-state");
        stateNode.textContent = stateLabel(rawState);
        stateNode.className = `server-state state-${normalizedState}`;
        $("#server-cpu").textContent = isServerRunning(server) ? formatPercent(pick(server, "cpuPercent")) : "—";
        $("#server-memory").textContent = isServerRunning(server) ? formatBytes(pick(server, "memoryBytes", "workingSetBytes")) : "—";
        $("#server-players").textContent = playerCount !== null
            ? (maxPlayers !== null ? `${playerCount} / ${maxPlayers}` : String(playerCount))
            : "—";
        $("#server-uptime").textContent = isServerRunning(server) ? formatUptime(pick(server, "uptimeSeconds")) : "—";
        $("#server-port").textContent = text(pick(server, "port"));
        $("#server-java").textContent = text(pick(server, "javaVersion", "java"));
        updateActionAvailability();
    }

    async function loadServerDetail() {
        const id = state.currentServerId;
        if (!id) {
            return null;
        }
        return runReadOnce(`server:${id}`, async () => {
            const payload = await apiRequest(`${API_ROOT}/servers/${encodeURIComponent(id)}`);
            if (state.currentServerId !== id) {
                return payload;
            }
            const nestedServer = pick(payload, "server");
            const detail = nestedServer && typeof nestedServer === "object"
                ? {
                    ...nestedServer,
                    javaVersion: pick(payload, "javaVersion"),
                    supportsPlayerManagement: pick(payload, "supportsPlayerManagement"),
                    supportsBackups: pick(payload, "supportsBackups"),
                    hasDiagnosticConsole: pick(payload, "hasDiagnosticConsole")
                }
                : payload;
            state.currentServer = detail;
            const index = state.servers.findIndex(server => serverId(server) === id);
            if (index >= 0) {
                state.servers[index] = { ...state.servers[index], ...detail };
            }
            renderServerDetail();
            dismissAlert();
            return detail;
        });
    }

    function renderBackups() {
        const id = state.currentServerId;
        const scopedPermissions = permissionsForServer(id);
        const card = $("#backup-catalog-card");
        const list = $("#backup-list");
        const status = $("#backup-list-status");
        card.hidden = !scopedPermissions.readBackups;
        list.replaceChildren();
        if (!scopedPermissions.readBackups) {
            status.hidden = true;
            return;
        }

        const pending = Boolean(id && state.mutationsInFlight.has(`server:${id}:backup-restore`));
        const fullyStopped = normalizeState(pick(state.currentServer, "state")) === "stopped";
        $("#refresh-backups-button").disabled = !id || state.readsInFlight.has(`backups:${id}`) || pending;
        if (pending) {
            status.textContent = t("web.backup.restoring");
            status.hidden = false;
        } else if (state.backupError) {
            status.textContent = t("web.backup.readUnavailable");
            status.hidden = false;
        } else if (!state.backupsLoaded || state.backupServerId !== id) {
            status.textContent = t("web.backup.loading");
            status.hidden = false;
        } else if (state.backups.length === 0) {
            status.textContent = t("web.backup.empty");
            status.hidden = false;
        } else {
            status.textContent = "";
            status.hidden = true;
        }

        for (const backup of state.backups) {
            const backupId = text(pick(backup, "backupId"), "").toLowerCase();
            if (!/^[a-f0-9]{64}$/u.test(backupId)) {
                continue;
            }

            const entry = element("article", "backup-entry");
            entry.setAttribute("role", "listitem");
            const copy = element("div", "backup-entry-copy");
            copy.append(element("strong", "", text(pick(backup, "displayName"), "Backup")));
            const metadata = element("div", "backup-entry-meta");
            metadata.append(element("span", "", `${t("web.backup.createdAt")}：${formatTime(pick(backup, "createdAtUtc"), true)}`));
            metadata.append(element("span", "", `${t("web.backup.size")}：${formatBytes(pick(backup, "archiveBytes"))}`));
            copy.append(metadata);
            entry.append(copy);

            if (scopedPermissions.restoreBackup) {
                const restore = element("button", "danger-button", t("web.backup.restore"));
                restore.type = "button";
                restore.dataset.backupRestoreId = backupId;
                restore.disabled = pending || !fullyStopped;
                if (!fullyStopped) {
                    restore.title = t("web.backup.requiresStopped");
                }
                restore.addEventListener("click", () => restoreBackup(backup));
                entry.append(restore);
            }

            list.append(entry);
        }
    }

    async function loadBackups() {
        const id = state.currentServerId;
        if (!id || !permissionsForServer(id).readBackups) {
            state.backups = [];
            state.backupServerId = id;
            state.backupsLoaded = false;
            state.backupError = false;
            renderBackups();
            return null;
        }

        state.backupError = false;
        renderBackups();
        try {
            return await runReadOnce(`backups:${id}`, async () => {
                const payload = await apiRequest(`${API_ROOT}/servers/${encodeURIComponent(id)}/backups`);
                if (state.currentServerId !== id) {
                    return payload;
                }
                const backups = arrayFrom(payload, "backups")
                    .slice(0, MAX_BACKUP_ENTRIES)
                    .filter(backup => /^[a-f0-9]{64}$/iu.test(text(pick(backup, "backupId"), "")));
                state.backups = backups;
                state.backupServerId = id;
                state.backupsLoaded = true;
                state.backupError = false;
                renderBackups();
                return payload;
            });
        } catch (error) {
            if (state.currentServerId === id && error?.name !== "AbortError") {
                state.backups = [];
                state.backupServerId = id;
                state.backupsLoaded = true;
                state.backupError = true;
                renderBackups();
            }
            throw error;
        }
    }

    function updateActionAvailability() {
        updateLoginAvailability();
        const server = state.currentServer;
        const id = state.currentServerId;
        const scopedPermissions = permissionsForServer(id);
        const normalized = normalizeState(pick(server, "state"));
        const knownState = ["stopped", "starting", "running", "stopping", "crashed", "faulted"].includes(normalized);
        for (const button of $$('[data-server-action]')) {
            const action = button.dataset.serverAction;
            const permitted = action === "start"
                ? scopedPermissions.startServer
                : action === "stop"
                    ? scopedPermissions.stopServer
                    : scopedPermissions.restartServer;
            const pending = Boolean(id && state.mutationsInFlight.has(`server:${id}:${action}`));
            let allowed = true;
            if (knownState) {
                if (action === "start") allowed = ["stopped", "crashed", "faulted"].includes(normalized);
                if (action === "stop") allowed = ["starting", "running"].includes(normalized);
                if (action === "restart") allowed = normalized === "running";
            }
            button.hidden = !permitted;
            button.disabled = pending || !allowed || !permitted;
            button.setAttribute("aria-busy", pending ? "true" : "false");
        }

        const anyLifecyclePermission = scopedPermissions.startServer ||
            scopedPermissions.stopServer || scopedPermissions.restartServer;
        $("#server-action-bar").hidden = !anyLifecyclePermission;

        const backupPending = Boolean(id && state.mutationsInFlight.has(`server:${id}:backup`));
        const supportsBackups = pick(server, "supportsBackups") !== false;
        const supportsPlayerManagement = pick(server, "supportsPlayerManagement") !== false;
        $("#create-backup-button").hidden = !scopedPermissions.createBackup;
        $("#create-backup-button").disabled = backupPending || !id || normalized !== "stopped" || !supportsBackups || !scopedPermissions.createBackup;
        $("#create-backup-button").title = normalized === "stopped" ? "" : t("web.backup.requiresStopped");
        const restorePending = Boolean(id && state.mutationsInFlight.has(`server:${id}:backup-restore`));
        $("#backup-catalog-card").hidden = !scopedPermissions.readBackups;
        $("#refresh-backups-button").disabled = !id || restorePending || state.readsInFlight.has(`backups:${id}`);
        const fullyStopped = normalized === "stopped";
        for (const button of $$('[data-backup-restore-id]')) {
            button.disabled = restorePending || !fullyStopped || !scopedPermissions.restoreBackup;
            button.title = fullyStopped ? "" : t("web.backup.requiresStopped");
        }
        $("#command-form").hidden = !scopedPermissions.sendConsoleCommand;
        $("#send-command-button").disabled = !id || !scopedPermissions.sendConsoleCommand || !["starting", "running"].includes(normalized) || state.mutationsInFlight.has(`server:${id}:command`);
        $("#player-action-form").hidden = !scopedPermissions.managePlayers;
        $("#submit-player-action-button").disabled = !id || !scopedPermissions.managePlayers || normalized !== "running" || !supportsPlayerManagement || state.mutationsInFlight.has(`server:${id}:player-action`);
        $("#console-tab").hidden = !scopedPermissions.readConsole;
        $("#players-tab").hidden = !scopedPermissions.readPlayers;
        const anyMutationPermission = anyLifecyclePermission || scopedPermissions.createBackup || scopedPermissions.restoreBackup ||
            scopedPermissions.sendConsoleCommand || scopedPermissions.managePlayers;
        $("#permission-notice").hidden = anyMutationPermission;

        const updateCard = $("#product-update-card");
        if (updateCard) {
            const canManageUpdates = hasGlobalPermission("update.manage");
            updateCard.hidden = !canManageUpdates;
            const phase = text(pick(state.updateStatus, "phase"), "").toLowerCase();
            const channel = $("#product-update-channel").value;
            const pendingPrefix = `product-update:${channel}:`;
            const pending = [...state.mutationsInFlight.keys()].some(key => key.startsWith(pendingPrefix));
            $("#product-update-channel").disabled = !canManageUpdates || pending;
            $("#product-update-check").disabled = !canManageUpdates || pending;
            $("#product-update-download").disabled = !canManageUpdates || pending || phase !== "available";
            $("#product-update-apply").disabled = !canManageUpdates || pending || phase !== "ready";
        }
    }

    async function performServerAction(action) {
        const id = state.currentServerId;
        if (!id || !["start", "stop", "restart"].includes(action)) {
            return;
        }

        const actionLabels = {
            start: t("common.start"),
            stop: t("common.stop"),
            restart: t("common.restart")
        };
        const name = serverName(state.currentServer);
        if (action !== "start") {
            const playerCount = Number(pick(state.currentServer, "playerCount"));
            const playerNotice = Number.isFinite(playerCount) && playerCount > 0
                ? t("web.server.stopPlayersNotice", playerCount)
                : "";
            const confirmed = await confirmAction(
                `${actionLabels[action]} ${name}`,
                t("web.server.actionConfirm", playerNotice, actionLabels[action], name),
                t("web.server.actionConfirmTitle", actionLabels[action])
            );
            if (!confirmed) {
                return;
            }
        }

        const key = `server:${id}:${action}`;
        try {
            const result = await runMutationOnce(key, () => apiRequest(`${API_ROOT}/servers/${encodeURIComponent(id)}/actions/${action}`, {
                method: "POST",
                mutation: true,
                body: {}
            }));
            if (!result) {
                return;
            }
            showToast(text(pick(result, "message"), t("web.action.sent", actionLabels[action])));
            await loadServerDetail();
        } catch (error) {
            handleRequestError(error, t("web.server.actionFailed", actionLabels[action]));
        }
    }

    function consoleKey(id = state.currentServerId, stream = state.consoleStream) {
        return `${id || ""}:${stream}`;
    }

    function getConsoleBuffer(id = state.currentServerId, stream = state.consoleStream) {
        const key = consoleKey(id, stream);
        if (!state.consoleStreams.has(key)) {
            state.consoleStreams.set(key, { lines: [], cursor: null, hasMore: false, rendered: false });
        }
        return state.consoleStreams.get(key);
    }

    function normalizeConsoleLine(raw, fallbackSequence) {
        return {
            sequence: Number(pick(raw, "sequence")) || fallbackSequence,
            timestampUtc: pick(raw, "timestampUtc", "timestamp"),
            severity: text(pick(raw, "severity"), "info").toLowerCase(),
            stream: text(pick(raw, "stream"), "out").toLowerCase(),
            text: text(pick(raw, "text", "message"), "")
        };
    }

    function renderConsole(forceBottom = false) {
        const log = $("#console-log");
        const buffer = getConsoleBuffer();
        const wasNearBottom = log.scrollHeight - log.scrollTop - log.clientHeight < 80;
        const fragment = document.createDocumentFragment();
        if (buffer.lines.length === 0) {
            fragment.append(element(
                "p",
                "console-empty",
                state.consoleStream === "diagnostic" ? t("web.empty.diagnostics") : t("web.empty.console")));
        } else {
            for (const line of buffer.lines) {
                const severity = ["warning", "warn", "error", "fatal"].includes(line.severity)
                    ? (line.severity === "warn" ? "warning" : line.severity)
                    : "info";
                const row = element("div", `console-line severity-${severity}`);
                row.append(element("span", "console-time", formatTime(line.timestampUtc)));
                const streamLabel = line.stream === "diagnostic" ? "DIAG" : "OUT";
                const severityLabel = severity === "warning" ? "WARN" : severity === "info" ? streamLabel : "ERR";
                row.append(element("span", "console-severity", severityLabel));
                row.append(element("span", "console-text", line.text));
                fragment.append(row);
            }
        }
        log.replaceChildren(fragment);
        $("#console-summary").textContent = t(
            "web.console.summary",
            buffer.lines.length,
            state.consoleStream === "diagnostic" ? t("web.console.diagnostics") : t("web.console.ordinary"));
        const hint = $("#console-more-hint");
        hint.hidden = !buffer.hasMore;
        hint.textContent = buffer.hasMore ? t("web.console.more") : "";
        if (forceBottom || wasNearBottom) {
            log.scrollTop = log.scrollHeight;
        }
        buffer.rendered = true;
    }

    async function loadConsole(options = {}) {
        const id = state.currentServerId;
        const stream = state.consoleStream;
        if (!id || state.currentView !== "server" || state.serverTab !== "console" ||
            !permissionsForServer(id).readConsole) {
            return null;
        }

        const buffer = getConsoleBuffer(id, stream);
        if (options.reset) {
            buffer.lines = [];
            buffer.cursor = null;
            buffer.hasMore = false;
            buffer.rendered = false;
            renderConsole();
        }
        const query = new URLSearchParams({ stream, limit: String(CONSOLE_PAGE_SIZE) });
        if (buffer.cursor !== null && buffer.cursor !== undefined && buffer.cursor !== "") {
            query.set("after", String(buffer.cursor));
        }

        return runReadOnce(`console:${id}:${stream}`, async () => {
            const payload = await apiRequest(`${API_ROOT}/servers/${encodeURIComponent(id)}/console?${query}`);
            if (state.currentServerId !== id || state.consoleStream !== stream) {
                return payload;
            }

            const incoming = arrayFrom(payload, "lines").map((line, index) => normalizeConsoleLine(line, Date.now() + index));
            const knownSequences = new Set(buffer.lines.map(line => line.sequence));
            const added = incoming.filter(line => !knownSequences.has(line.sequence));
            if (options.reset) {
                buffer.lines = incoming;
            } else if (added.length > 0) {
                buffer.lines.push(...added);
            }
            if (buffer.lines.length > MAX_CONSOLE_LINES) {
                buffer.lines.splice(0, buffer.lines.length - MAX_CONSOLE_LINES);
            }

            const nextCursor = pick(payload, "nextCursor");
            if (nextCursor !== null && nextCursor !== undefined) {
                buffer.cursor = nextCursor;
            } else if (incoming.length > 0) {
                buffer.cursor = Math.max(...incoming.map(line => line.sequence));
            }
            buffer.hasMore = pick(payload, "hasMore") === true;
            if (options.reset || added.length > 0 || !buffer.rendered) {
                renderConsole(options.reset === true);
            }
            dismissAlert();
            return payload;
        });
    }

    async function sendConsoleCommand(event) {
        event.preventDefault();
        const id = state.currentServerId;
        const input = $("#command-input");
        const errorNode = $("#command-error");
        errorNode.hidden = true;
        const command = input.value.trim();

        if (!id || command.length === 0 || command.length > 512 || /[\r\n\0]/u.test(command)) {
            errorNode.textContent = t("web.console.commandInvalid");
            errorNode.hidden = false;
            input.focus();
            return;
        }

        try {
            const result = await runMutationOnce(`server:${id}:command`, () => apiRequest(`${API_ROOT}/servers/${encodeURIComponent(id)}/console/commands`, {
                method: "POST",
                mutation: true,
                body: { command }
            }));
            if (!result) {
                return;
            }
            input.value = "";
            showToast(text(pick(result, "message"), t("web.console.commandSent")));
            window.setTimeout(() => loadConsole().catch(error => handleRequestError(error, t("web.console.readFailed"))), 350);
        } catch (error) {
            if (error instanceof ApiError && error.status === 401) {
                handleRequestError(error, t("web.console.sendFailed"));
                return;
            }
            errorNode.textContent = userFacingError(error, t("web.console.sendFailed"));
            errorNode.hidden = false;
        }
    }

    function renderPlayers() {
        const container = $("#player-list");
        const focusedPlayer = container.contains(document.activeElement)
            ? document.activeElement?.dataset?.playerName
            : null;
        const fragment = document.createDocumentFragment();
        if (state.players.length === 0) {
            fragment.append(element("p", "empty-state", t("web.empty.players")));
        } else {
            const sorted = [...state.players].sort((left, right) => {
                const onlineDifference = Number(pick(right, "online", "isOnline") === true) - Number(pick(left, "online", "isOnline") === true);
                return onlineDifference || text(pick(left, "name"), "").localeCompare(text(pick(right, "name"), ""), state.culture);
            });
            for (const player of sorted) {
                const name = text(pick(player, "name"), t("web.player.unknown"));
                const row = element("button", "player-row");
                row.type = "button";
                row.dataset.playerName = name;
                row.setAttribute("aria-label", t("web.player.selectAria", name));
                row.addEventListener("click", () => {
                    $("#player-name-input").value = name;
                    $("#player-name-input").focus();
                    showToast(t("web.players.selected", name));
                });

                const copy = element("span", "player-copy");
                copy.append(element("strong", "", name));
                copy.append(element("small", "", text(pick(player, "uuid"), t("web.player.uuidUnknown"))));
                row.append(copy);

                const badges = element("span", "player-badges");
                const online = pick(player, "online", "isOnline") === true;
                badges.append(element(
                    "span",
                    `mini-badge${online ? " is-online" : ""}`,
                    online ? t("web.players.online") : t("web.players.offline")));
                if (pick(player, "operator", "isOperator") === true) {
                    badges.append(element("span", "mini-badge", "OP"));
                }
                if (pick(player, "banned", "isBanned") === true) {
                    badges.append(element("span", "mini-badge is-banned", t("web.players.banned")));
                }
                row.append(badges);
                fragment.append(row);
            }
        }
        container.replaceChildren(fragment);
        if (focusedPlayer) {
            const matchingPlayer = [...container.querySelectorAll("[data-player-name]")]
                .find(button => button.dataset.playerName === focusedPlayer);
            matchingPlayer?.focus({ preventScroll: true });
        }
        const onlineCount = state.players.filter(player => pick(player, "online", "isOnline") === true).length;
        $("#players-summary").textContent = t("web.players.count", onlineCount, state.players.length);
    }

    async function loadPlayers() {
        const id = state.currentServerId;
        if (!id || state.currentView !== "server" || state.serverTab !== "players" ||
            !permissionsForServer(id).readPlayers) {
            return null;
        }
        return runReadOnce(`players:${id}`, async () => {
            const payload = await apiRequest(`${API_ROOT}/servers/${encodeURIComponent(id)}/players`);
            if (state.currentServerId !== id) {
                return payload;
            }
            const players = arrayFrom(payload, "players");
            const signature = JSON.stringify(players.map(player => [
                pick(player, "name"),
                pick(player, "uuid"),
                pick(player, "online", "isOnline"),
                pick(player, "operator", "isOperator"),
                pick(player, "banned", "isBanned"),
                pick(player, "lastSeenUtc")
            ]));
            state.players = players;
            if (signature !== state.playersSignature) {
                state.playersSignature = signature;
                renderPlayers();
            }
            dismissAlert();
            return payload;
        });
    }

    function safeAdministrationText(value, maximumLength, fallback = "—") {
        const candidate = text(value, "").trim();
        if (candidate.length === 0 || candidate.length > maximumLength ||
            /[\u0000-\u001f\u007f/\\:<>"|?*]/u.test(candidate)) {
            return fallback;
        }
        return candidate;
    }

    function renderEnvironment() {
        const payload = state.administrationServerId === state.currentServerId
            ? state.administration
            : null;
        const status = $("#environment-status");
        const list = $("#environment-addon-list");
        const hint = $("#environment-addon-hint");
        list.replaceChildren();

        if (state.administrationError) {
            status.textContent = t("web.environment.loadFailed");
            status.hidden = false;
        } else if (!state.administrationLoaded || !payload) {
            status.textContent = t("web.environment.loading");
            status.hidden = false;
        } else {
            status.textContent = "";
            status.hidden = true;
        }

        const java = pick(payload, "java") || {};
        const javaAvailable = pick(java, "available") === true;
        const major = finiteNumber(pick(java, "majorVersion"));
        const version = safeAdministrationText(
            pick(java, "version"),
            MAX_JAVA_METADATA_CHARACTERS);
        const runtimeKind = safeAdministrationText(
            pick(java, "runtimeKind"),
            MAX_JAVA_METADATA_CHARACTERS,
            t("web.environment.unknown"));
        const vendor = safeAdministrationText(
            pick(java, "vendor"),
            MAX_JAVA_METADATA_CHARACTERS,
            t("web.environment.unknown"));
        const architecture = safeAdministrationText(
            pick(java, "architecture"),
            MAX_JAVA_METADATA_CHARACTERS,
            t("web.environment.unknown"));
        $("#environment-java-status").textContent = javaAvailable
            ? t("web.environment.javaAvailable")
            : t("web.environment.javaUnavailable");
        $("#environment-java-major").textContent = major !== null && major >= 1 && major <= 99
            ? String(Math.trunc(major))
            : "—";
        $("#environment-java-version").textContent = version;
        $("#environment-java-kind").textContent = runtimeKind;
        $("#environment-java-vendor").textContent = vendor;
        $("#environment-java-architecture").textContent = architecture;

        const addonsAvailable = pick(payload, "addonsAvailable") === true;
        const addons = arrayFrom(payload, "addons")
            .slice(0, MAX_ADMINISTRATION_ADDONS)
            .map(addon => {
                const fileName = safeAdministrationText(
                    pick(addon, "fileName"),
                    MAX_ADDON_FILE_NAME_CHARACTERS,
                    "");
                const sizeBytes = finiteNumber(pick(addon, "sizeBytes"));
                const kind = text(pick(addon, "kind"), "").toLowerCase();
                return fileName && sizeBytes !== null && sizeBytes >= 0 && ["mod", "plugin"].includes(kind)
                    ? { fileName, sizeBytes, kind }
                    : null;
            })
            .filter(Boolean);
        $("#environment-addon-summary").textContent = addonsAvailable
            ? t("web.environment.addonsCount", addons.length)
            : t("web.environment.addonsUnavailable");

        if (addonsAvailable && addons.length === 0) {
            list.append(element("p", "empty-state", t("web.environment.addonsEmpty")));
        } else {
            for (const addon of addons) {
                const row = element("div", "environment-addon-row");
                row.setAttribute("role", "listitem");
                row.append(element(
                    "span",
                    "mini-badge",
                    addon.kind === "plugin"
                        ? t("web.environment.plugin")
                        : t("web.environment.mod")));
                row.append(element("strong", "environment-addon-name", addon.fileName));
                row.append(element("span", "environment-addon-size", formatBytes(addon.sizeBytes)));
                list.append(row);
            }
        }

        hint.textContent = pick(payload, "addonsTruncated") === true
            ? t("web.environment.addonsTruncated")
            : "";
        hint.hidden = hint.textContent.length === 0;
        $("#refresh-environment-button").disabled =
            !state.currentServerId || state.readsInFlight.has(`administration:${state.currentServerId}`);

        if (javaAvailable) {
            $("#server-java").textContent = major !== null
                ? `Java ${Math.trunc(major)}`
                : version === "—" ? "Java" : `Java ${version}`;
        }
    }

    async function loadEnvironment() {
        const id = state.currentServerId;
        if (!id || state.currentView !== "server" || state.serverTab !== "environment" ||
            !permissionsForServer(id).readServer) {
            return null;
        }

        state.administrationError = false;
        renderEnvironment();
        try {
            return await runReadOnce(`administration:${id}`, async () => {
                const payload = await apiRequest(`${API_ROOT}/servers/${encodeURIComponent(id)}/administration`);
                if (state.currentServerId !== id) {
                    return payload;
                }
                state.administration = payload;
                state.administrationServerId = id;
                state.administrationLoaded = true;
                state.administrationError = false;
                renderEnvironment();
                dismissAlert();
                return payload;
            });
        } catch (error) {
            if (state.currentServerId === id && error?.name !== "AbortError") {
                state.administration = null;
                state.administrationServerId = id;
                state.administrationLoaded = false;
                state.administrationError = true;
                renderEnvironment();
            }
            throw error;
        }
    }

    async function performPlayerAction(event) {
        event.preventDefault();
        const id = state.currentServerId;
        const errorNode = $("#player-action-error");
        errorNode.hidden = true;
        const playerName = $("#player-name-input").value.trim();
        const action = $("#player-action-select").value;
        const reason = $("#player-reason-input").value.trim();
        const validActions = ["kick", "ban", "pardon", "op", "deop", "whitelistAdd", "whitelistRemove", "whitelistOn", "whitelistOff"];
        const playerlessActions = ["whitelistOn", "whitelistOff"];
        const namePattern = /^[A-Za-z0-9_]{1,16}$/u;

        if (!id || !validActions.includes(action) || (!playerlessActions.includes(action) && !namePattern.test(playerName)) || /[\r\n\0]/u.test(reason)) {
            errorNode.textContent = playerlessActions.includes(action)
                ? t("web.player.whitelistInvalid")
                : t("web.player.nameInvalid");
            errorNode.hidden = false;
            return;
        }

        const labels = {
            kick: t("web.player.kick"),
            ban: t("web.player.ban"),
            pardon: t("web.player.pardon"),
            op: t("web.player.op"),
            deop: t("web.player.deop"),
            whitelistAdd: t("web.player.whitelistAdd"),
            whitelistRemove: t("web.player.whitelistRemove"),
            whitelistOn: t("web.player.whitelistOn"),
            whitelistOff: t("web.player.whitelistOff")
        };
        const targetDescription = playerlessActions.includes(action) ? t("web.player.targetServer") : playerName;
        const confirmed = await confirmAction(
            `${labels[action]}${playerlessActions.includes(action) ? "" : ` ${playerName}`}`,
            t("web.player.actionConfirm", targetDescription, labels[action]),
            t("web.confirm.title")
        );
        if (!confirmed) {
            return;
        }

        try {
            const body = { playerName: playerlessActions.includes(action) ? null : playerName, action };
            if (reason && ["kick", "ban"].includes(action)) {
                body.reason = reason;
            }
            const result = await runMutationOnce(`server:${id}:player-action`, () => apiRequest(`${API_ROOT}/servers/${encodeURIComponent(id)}/player-actions`, {
                method: "POST",
                mutation: true,
                body
            }));
            if (!result) {
                return;
            }
            showToast(text(pick(result, "message"), t("web.action.sent", labels[action])));
            await loadPlayers();
        } catch (error) {
            if (error instanceof ApiError && error.status === 401) {
                handleRequestError(error, t("web.player.actionFailed", labels[action]));
                return;
            }
            errorNode.textContent = userFacingError(error, t("web.player.actionFailed", labels[action]));
            errorNode.hidden = false;
        }
    }

    function updatePlayerActionForm() {
        const action = $("#player-action-select").value;
        const playerless = action === "whitelistOn" || action === "whitelistOff";
        const supportsReason = action === "kick" || action === "ban";
        const nameInput = $("#player-name-input");
        const reasonInput = $("#player-reason-input");
        nameInput.disabled = playerless;
        nameInput.required = !playerless;
        if (playerless) {
            nameInput.value = "";
            nameInput.placeholder = t("web.player.noNameRequired");
        } else {
            nameInput.placeholder = t("web.players.namePlaceholder");
        }
        reasonInput.disabled = !supportsReason;
        if (!supportsReason) {
            reasonInput.value = "";
        }
    }

    async function createBackup() {
        const id = state.currentServerId;
        if (!id) {
            return;
        }
        const confirmed = await confirmAction(
            t("web.backup.confirmTitle"),
            t("web.backup.confirmMessage", serverName(state.currentServer)),
            t("web.server.createBackup"),
            false);
        if (!confirmed) {
            return;
        }

        try {
            const result = await runMutationOnce(`server:${id}:backup`, () => apiRequest(`${API_ROOT}/servers/${encodeURIComponent(id)}/backups`, {
                method: "POST",
                mutation: true,
                body: {}
            }));
            if (!result) {
                return;
            }
            if (pick(result, "accepted") !== true) {
                throw new ApiError(text(pick(result, "message"), t("web.backup.failed")), 409);
            }
            showToast(text(pick(result, "message"), t("web.backup.created")));
            if (permissionsForServer(id).readBackups) {
                void loadBackups().catch(error => handleRequestError(error, t("web.backup.loadFailed")));
            }
        } catch (error) {
            handleRequestError(error, t("web.backup.failed"));
        }
    }

    async function restoreBackup(backup) {
        const id = state.currentServerId;
        const backupId = text(pick(backup, "backupId"), "").toLowerCase();
        if (!id || !/^[a-f0-9]{64}$/u.test(backupId) || !permissionsForServer(id).restoreBackup) {
            return;
        }
        if (normalizeState(pick(state.currentServer, "state")) !== "stopped") {
            showAlert(t("web.backup.requiresStopped"));
            return;
        }

        const displayName = text(pick(backup, "displayName"), "Backup");
        const firstConfirmation = await confirmAction(
            t("web.backup.restoreConfirmTitle"),
            t("web.backup.restoreConfirmMessage", displayName),
            t("web.backup.restore"));
        if (!firstConfirmation) {
            return;
        }
        const secondConfirmation = await confirmAction(
            t("web.backup.restoreSecondTitle"),
            t("web.backup.restoreSecondMessage", serverName(state.currentServer)),
            t("web.backup.restore"));
        if (!secondConfirmation) {
            return;
        }

        try {
            const restoreTask = runMutationOnce(`server:${id}:backup-restore`, () => apiRequest(
                `${API_ROOT}/servers/${encodeURIComponent(id)}/backups/${encodeURIComponent(backupId)}/restore`,
                {
                    method: "POST",
                    mutation: true,
                    body: { confirmation: BACKUP_RESTORE_CONFIRMATION }
                }));
            renderBackups();
            const result = await restoreTask;
            if (!result) {
                return;
            }
            if (pick(result, "accepted") !== true) {
                throw new ApiError(text(pick(result, "message"), t("web.backup.restoreFailed")), 409);
            }
            showToast(t("web.backup.restored"));
            await Promise.all([loadServerDetail(), loadBackups()]);
        } catch (error) {
            handleRequestError(error, t("web.backup.restoreFailed"));
        } finally {
            renderBackups();
        }
    }

    function selectServerTab(tabName, fetchNow = true) {
        if (!["overview", "console", "players", "environment"].includes(tabName)) {
            return;
        }
        const scopedPermissions = permissionsForServer();
        if ((tabName === "console" && !scopedPermissions.readConsole) ||
            (tabName === "players" && !scopedPermissions.readPlayers)) {
            tabName = "overview";
        }
        state.serverTab = tabName;
        for (const button of $$("#server-tabs [data-server-tab]")) {
            const selected = button.dataset.serverTab === tabName;
            button.setAttribute("aria-selected", String(selected));
            button.tabIndex = selected ? 0 : -1;
        }
        for (const panel of $$("#server-view > .tab-panel")) {
            panel.hidden = panel.id !== `${tabName}-panel`;
        }
        if (fetchNow) {
            if (tabName === "overview" && permissionsForServer().readBackups &&
                (!state.backupsLoaded || state.backupServerId !== state.currentServerId)) {
                loadBackups().catch(error => handleRequestError(error, t("web.backup.loadFailed")));
            }
            if (tabName === "console") loadConsole().catch(error => handleRequestError(error, t("web.console.readFailed")));
            if (tabName === "players") loadPlayers().catch(error => handleRequestError(error, t("web.players.readFailed")));
            if (tabName === "environment") loadEnvironment().catch(error => handleRequestError(error, t("web.environment.loadFailed")));
            schedulePoll();
        }
    }

    function selectConsoleStream(stream) {
        if (!["ordinary", "diagnostic"].includes(stream)) {
            return;
        }
        state.consoleStream = stream;
        for (const button of $$("#console-stream-tabs [data-console-stream]")) {
            const selected = button.dataset.consoleStream === stream;
            button.setAttribute("aria-selected", String(selected));
            button.tabIndex = selected ? 0 : -1;
        }
        $("#console-stream-panel").setAttribute("aria-labelledby", stream === "ordinary" ? "ordinary-console-tab" : "diagnostic-console-tab");
        renderConsole(true);
        loadConsole().catch(error => handleRequestError(error, t("web.console.readFailed")));
        schedulePoll();
    }

    function supportTabKeyboardNavigation(tabList) {
        tabList.addEventListener("keydown", event => {
            if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) {
                return;
            }
            const tabs = $$('[role="tab"]', tabList);
            const currentIndex = tabs.indexOf(document.activeElement);
            if (currentIndex < 0) {
                return;
            }
            event.preventDefault();
            let nextIndex = currentIndex;
            if (event.key === "ArrowLeft") nextIndex = (currentIndex - 1 + tabs.length) % tabs.length;
            if (event.key === "ArrowRight") nextIndex = (currentIndex + 1) % tabs.length;
            if (event.key === "Home") nextIndex = 0;
            if (event.key === "End") nextIndex = tabs.length - 1;
            tabs[nextIndex].focus();
            tabs[nextIndex].click();
        });
    }

    function confirmAction(title, message, acceptLabel, destructive = true) {
        const dialog = $("#confirm-dialog");
        $("#confirm-title").textContent = title;
        $("#confirm-message").textContent = message;
        const acceptButton = $("#confirm-accept-button");
        acceptButton.textContent = acceptLabel;
        acceptButton.className = destructive ? "danger-button" : "primary-button";

        return new Promise(resolve => {
            const closeHandler = () => {
                dialog.removeEventListener("close", closeHandler);
                resolve(dialog.returnValue === "confirm");
            };
            dialog.addEventListener("close", closeHandler);
            dialog.showModal();
        });
    }

    async function refreshCurrentView() {
        if (!state.authenticated || document.visibilityState === "hidden" || !navigator.onLine) {
            return false;
        }
        try {
            if (state.currentView === "dashboard" || state.currentView === "servers") {
                await loadDashboard();
                return true;
            }
            if (state.currentView === "server") {
                const requests = [loadServerDetail()];
                if (state.serverTab === "overview" && permissionsForServer().readBackups &&
                    (!state.backupsLoaded || state.backupServerId !== state.currentServerId)) {
                    requests.push(loadBackups());
                }
                if (state.serverTab === "console") requests.push(loadConsole());
                if (state.serverTab === "players") requests.push(loadPlayers());
                if (state.serverTab === "environment" &&
                    (!state.administrationLoaded || state.administrationServerId !== state.currentServerId)) {
                    requests.push(loadEnvironment());
                }
                const results = await Promise.allSettled(requests);
                const failure = results.find(result => result.status === "rejected" && result.reason?.name !== "AbortError");
                if (failure) {
                    throw failure.reason;
                }
            }
            return true;
        } catch (error) {
            handleRequestError(error, t("web.error.refreshData"));
            return false;
        }
    }

    function currentPollInterval() {
        if (state.currentView !== "server") {
            return POLL_INTERVALS.dashboard;
        }
        if (state.serverTab === "console") {
            return POLL_INTERVALS.console;
        }
        if (state.serverTab === "players") {
            return POLL_INTERVALS.players;
        }
        return POLL_INTERVALS.server;
    }

    function recordConnectionFailure() {
        state.connectionFailureCount = Math.min(state.connectionFailureCount + 1, 32);
        const exponent = Math.min(
            Math.max(0, state.connectionFailureCount - 1),
            RECOVERY_BACKOFF_MAX_EXPONENT);
        const delay = Math.min(
            RECOVERY_BACKOFF_MAX_MS,
            RECOVERY_BACKOFF_BASE_MS * (2 ** exponent));
        if (navigator.onLine) {
            setConnectionStatus("connecting", t("web.connection.retryIn", Math.ceil(delay / 1000)));
        }
        return delay;
    }

    function schedulePoll(delay = currentPollInterval()) {
        window.clearTimeout(state.pollTimer);
        if (!state.authenticated || document.visibilityState === "hidden" || !navigator.onLine) {
            return;
        }
        state.pollTimer = window.setTimeout(async () => {
            const succeeded = await refreshCurrentView();
            if (!state.authenticated || document.visibilityState === "hidden" || !navigator.onLine) {
                return;
            }
            schedulePoll(succeeded ? currentPollInterval() : recordConnectionFailure());
        }, delay);
    }

    function scheduleAuthenticationRecovery(context, delay = null) {
        window.clearTimeout(state.pollTimer);
        if (state.authenticated || document.visibilityState === "hidden" || !navigator.onLine) {
            return;
        }
        const retryDelay = delay ?? recordConnectionFailure();
        state.pollTimer = window.setTimeout(async () => {
            try {
                await loadAuthStatus();
            } catch (error) {
                handleRequestError(error, context);
                showAuthenticationView();
                scheduleAuthenticationRecovery(context);
            }
        }, retryDelay);
    }

    function wireEvents() {
        $("#language-selector").addEventListener("change", async event => {
            const selector = event.currentTarget;
            const previousCulture = state.culture;
            selector.disabled = true;
            try {
                await loadLocalization(selector.value, true);
                configureInstalledExperience();
                updateLoginAvailability();
                updatePlayerActionForm();
                state.serverListsSignature = "";
                if (state.authenticated) {
                    renderServerLists();
                    renderServerDetail();
                    renderBackups();
                    if (state.serverTab === "console") renderConsole();
                    if (state.serverTab === "players") renderPlayers();
                    if (state.serverTab === "environment") renderEnvironment();
                }
            } catch {
                selector.value = previousCulture;
                showAlert(t("web.error.localizationUnavailable"));
            } finally {
                selector.disabled = false;
            }
        });
        $("#login-form").addEventListener("submit", login);
        $("#signout-button").addEventListener("click", signOut);
        $("#dismiss-alert-button").addEventListener("click", dismissAlert);
        $("#show-all-servers-button").addEventListener("click", () => navigate("servers"));
        $("#back-to-servers-button").addEventListener("click", () => navigate("servers"));
        $("#server-search").addEventListener("input", renderServerLists);
        $("#command-form").addEventListener("submit", sendConsoleCommand);
        $("#player-action-form").addEventListener("submit", performPlayerAction);
        $("#player-action-select").addEventListener("change", updatePlayerActionForm);
        $("#create-backup-button").addEventListener("click", createBackup);
        $("#refresh-backups-button").addEventListener("click", () => loadBackups().catch(error => handleRequestError(error, t("web.backup.loadFailed"))));
        $("#refresh-console-button").addEventListener("click", () => loadConsole({ reset: true }).catch(error => handleRequestError(error, t("web.console.readFailed"))));
        $("#refresh-players-button").addEventListener("click", () => loadPlayers().catch(error => handleRequestError(error, t("web.players.readFailed"))));
        $("#refresh-environment-button").addEventListener("click", () => loadEnvironment().catch(error => handleRequestError(error, t("web.environment.loadFailed"))));
        $("#product-update-channel").addEventListener("change", () => {
            state.updateStatus = null;
            $("#product-update-status").textContent = t("web.update.unavailable");
            updateActionAvailability();
            loadProductUpdateStatus().catch(error => handleRequestError(error, t("web.update.title")));
        });
        $("#product-update-check").addEventListener("click", () => performProductUpdate("check"));
        $("#product-update-download").addEventListener("click", () => performProductUpdate("download"));
        $("#product-update-apply").addEventListener("click", () => performProductUpdate("schedule"));

        for (const button of $$("[data-refresh]")) {
            button.addEventListener("click", () => {
                refreshCurrentView();
                schedulePoll();
            });
        }
        for (const button of $$("#bottom-navigation [data-route]")) {
            button.addEventListener("click", () => navigate(button.dataset.route));
        }
        for (const button of $$('[data-server-action]')) {
            button.addEventListener("click", () => performServerAction(button.dataset.serverAction));
        }
        for (const button of $$("#server-tabs [data-server-tab]")) {
            button.addEventListener("click", () => selectServerTab(button.dataset.serverTab));
        }
        for (const button of $$("#console-stream-tabs [data-console-stream]")) {
            button.addEventListener("click", () => selectConsoleStream(button.dataset.consoleStream));
        }

        supportTabKeyboardNavigation($("#server-tabs"));
        supportTabKeyboardNavigation($("#console-stream-tabs"));

        document.addEventListener("visibilitychange", () => {
            if (document.visibilityState === "hidden") {
                window.clearTimeout(state.pollTimer);
                abortReadRequests();
                return;
            }
            if (state.authenticated) {
                schedulePoll(0);
            } else {
                scheduleAuthenticationRecovery(t("web.connection.authRecoveryFailed"), 0);
            }
        });

        window.addEventListener("offline", () => {
            window.clearTimeout(state.pollTimer);
            abortReadRequests();
            state.connectionFailureCount = 0;
            updateLoginAvailability();
            setConnectionStatus("offline", t("web.connection.offline"));
            showAlert(t("web.connection.offlineNotice"));
        });

        window.addEventListener("online", () => {
            updateLoginAvailability();
            setConnectionStatus("connecting", t("web.connection.reconnecting"));
            dismissAlert();
            state.connectionFailureCount = 0;
            if (state.authenticated) {
                schedulePoll(0);
            } else {
                scheduleAuthenticationRecovery(t("web.connection.reconnectFailed"), 0);
            }
        });

        window.addEventListener("pagehide", suspendLoginRetryTimer);
        window.addEventListener("pageshow", () => {
            if (remainingLoginRetrySeconds() > 0) {
                scheduleLoginRetryTimer();
            }
            updateLoginAvailability();
        });
    }

    function configureInstalledExperience() {
        const standalone = window.matchMedia?.("(display-mode: standalone)").matches === true
            || window.navigator.standalone === true;
        document.documentElement.classList.toggle("standalone", standalone);
        const installStatus = $("#install-status");
        if (standalone && installStatus) {
            installStatus.textContent = t("web.install.standalone");
        }
    }

    async function registerServiceWorker() {
        if (!("serviceWorker" in navigator) || !window.isSecureContext) {
            return;
        }

        try {
            await navigator.serviceWorker.register("/service-worker.js", {
                scope: "/",
                updateViaCache: "none"
            });
        } catch {
            // PWA installation is optional. A registration failure must never block the
            // authenticated web console or cause a control operation to be retried.
        }
    }

    async function initialize() {
        try {
            await loadLocalization(preferredCulture());
        } catch {
            if (preferredCulture() !== FALLBACK_CULTURE) {
                try {
                    await loadLocalization(FALLBACK_CULTURE);
                } catch {
                    // The static Traditional-Chinese document remains readable. The normal API
                    // connection error below will report the unavailable host without executing
                    // or queuing any control operation.
                }
            }
        }
        configureInstalledExperience();
        wireEvents();
        updatePlayerActionForm();
        showLoadingView();
        setConnectionStatus(
            navigator.onLine ? "connecting" : "offline",
            navigator.onLine ? t("web.connection.connecting") : t("web.connection.offline"));
        if (!navigator.onLine) {
            showAuthenticationView();
            showAlert(t("web.connection.offlineAuthNotice"));
            return;
        }
        try {
            await loadAuthStatus();
        } catch (error) {
            handleRequestError(error, t("web.connection.serviceUnavailable"));
            showAuthenticationView();
            scheduleAuthenticationRecovery(t("web.connection.serviceUnavailable"));
        }
    }

    initialize();
    void registerServiceWorker();
})();
