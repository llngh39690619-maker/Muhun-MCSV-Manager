using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.Contracts.Localization;

/// <summary>
/// Versioned product-localization contract shared by the desktop and embedded Web client.
/// Keys are stable API surface: existing keys may not be renamed or repurposed inside a v1 catalog.
/// </summary>
public static partial class ProductLocalizationCatalog
{
    public const int SchemaVersion = 1;
    public const string FallbackCulture = "zh-TW";
    public const string EnglishCulture = "en-US";

    public static IReadOnlyList<string> SupportedCultures { get; } =
        [FallbackCulture, EnglishCulture];

    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly(
    [
        "common.apply", "common.cancel", "common.close", "common.confirm", "common.delete",
        "common.refresh", "common.reload", "common.restart", "common.save", "common.search",
        "common.send", "common.start", "common.stop", "common.unknown", "common.open", "common.ok", "common.reconnect",
        "tray.open", "tray.exit",
        "language.label", "language.zh-TW", "language.en-US",
        "app.singleInstance.title", "app.singleInstance.message",
        "app.startupFailed.title", "app.startupFailed.message",
        "app.brand.mark", "app.brand.name",
        "main.window.title", "main.serverList", "main.runningCount", "main.select", "main.startSelected",
        "main.workspace.server", "main.workspace.client",
        "main.stopSelected", "main.remoteManagement", "main.mobileRemote", "main.webConsole", "main.openDataDirectory",
        "main.onlineModpack", "main.importServer", "main.createCore", "main.noServers",
        "main.noServersHint", "main.tab.console", "main.tab.diagnostics", "main.tab.players",
        "main.tab.settings", "main.tab.addons", "main.tab.java", "main.tab.backups",
        "main.action.openFolder", "main.action.start", "main.action.stop", "main.action.restart",
        "main.action.refresh", "main.action.saveSettings", "main.action.createBackup",
        "main.metric.cpu", "main.metric.memory", "main.metric.port", "main.metric.java",
        "main.metric.uptime", "main.status.running", "main.status.stopped", "main.status.starting",
        "main.status.stopping", "main.status.crashed", "main.settingsTooltip", "main.jobsTooltip",
        "main.service.updateAction", "main.service.updateTooltip",
        "main.console.commandTooltip", "main.diagnostics.empty",
        "main.bulkSelection.tooltip", "main.bulkSelection.automation", "main.bulkSelection.serverAutomation",
        "main.startSelected.tooltip", "main.startSelected.automation",
        "main.stopSelected.tooltip", "main.stopSelected.automation",
        "main.remoteManagement.tooltip", "main.remoteManagement.automation",
        "main.mobileRemote.tooltip", "main.mobileRemote.automation",
        "main.webConsole.tooltip", "main.webConsole.automation",
        "main.context.appearance", "main.context.remove", "main.context.delete",
        "main.players.heading", "main.players.description", "main.players.showKnown",
        "main.players.reloadLocal", "main.players.actionsHeading", "main.players.actionsHint",
        "main.players.name", "main.players.kick", "main.players.ban", "main.players.pardon",
        "main.players.op", "main.players.deop", "main.players.whitelist",
        "main.players.whitelistAdd", "main.players.whitelistRemove",
        "main.players.whitelistOn", "main.players.whitelistOff",
        "main.settings.startupHeading", "main.settings.displayName", "main.settings.launchTarget",
        "main.settings.javaExecutable", "main.memory.heading", "main.memory.unspecified",
        "main.memory.automatic", "main.memory.manual", "main.memory.recalculate",
        "main.memory.minimum", "main.memory.maximum", "main.settings.port", "main.settings.portHint",
        "main.settings.autoRestart", "main.settings.autoRestartHint",
        "main.settings.separateDiagnostics", "main.settings.separateDiagnosticsHint",
        "main.settings.watchdog", "main.settings.watchdogHint", "main.settings.watchdogInterval",
        "main.settings.watchdogTimeout", "main.settings.watchdogFailures",
        "main.settings.watchdogStartupGrace", "main.settings.recoveryPoints",
        "main.settings.recoveryPointsHint", "main.settings.recoveryInterval",
        "main.settings.recoveryRetention", "main.properties.heading", "main.properties.hint",
        "main.properties.reload", "main.properties.save", "main.addons.heading", "main.addons.hint",
        "main.addons.check", "main.addons.openFolder", "main.modpackUpdate.heading",
        "main.modpackUpdate.hint", "main.modpackUpdate.openBackups", "main.modpackUpdate.start",
        "main.addons.currentVersion", "main.addons.latestCompatible", "main.java.heading",
        "main.java.hint", "main.java.downloadTemurin", "main.java.scanInstalled",
        "main.backups.heading", "main.backups.openFolder", "main.backups.openRecoveryPoints",
        "main.backups.restoreRecoveryPoint", "main.backups.openCrashReports", "main.backups.restore",
        "main.close.compatibilityRunning.title", "main.close.compatibilityRunning.message",
        "main.close.backgroundJobs.title", "main.close.backgroundJobs.message", "main.close.failedTitle",
        "addon.version.unrecognized", "addon.project", "addon.update.available", "addon.update.none",
        "player.state.online", "player.state.offline", "player.role.whitelist", "player.role.banned",
        "player.role.regular", "javaRuntime.installed",
        "server.oneDrive.performanceWarning", "server.core.custom", "server.version.unknown",
        "server.detail.serviceManaged", "server.detail.local", "server.state.starting",
        "server.state.running", "server.state.stopping", "server.state.crashed", "server.state.faulted",
        "server.state.stopped", "server.java.unspecified", "server.memory.executableHint",
        "server.memory.argumentFileHint", "server.console.lines", "server.diagnostics.summary",
        "server.diagnostics.header", "server.players.summaryKnown", "server.players.summaryOnline",
        "server.players.emptyKnown", "server.players.emptyOnline", "server.modpack.source",
        "server.modpack.unlinked", "server.command.sendFailed", "server.memory.systemLoading",
        "server.memory.systemAvailable", "server.memory.autoEstimating", "server.memory.autoComplete",
        "server.memory.autoCompleteConstrained", "server.memory.autoFailed", "server.uptime.daysHours",
        "settings.window.title", "settings.heading", "settings.description", "settings.display",
        "settings.theme", "settings.windowSize", "settings.width", "settings.height", "settings.fontSize",
        "settings.language", "settings.languageHint", "settings.newServerDefaults", "settings.newServerHint",
        "settings.globalMemory", "settings.globalMemoryHint", "settings.minimumMemory",
        "settings.maximumMemory", "settings.separateDiagnostics", "settings.autoRestart",
        "settings.hangWatchdog", "settings.recoveryPoints", "settings.unsaved.title",
        "settings.newClientDefaults", "settings.newClientHint", "settings.clientMemoryMode",
        "settings.clientMemoryAutomatic", "settings.clientMemoryManual", "settings.clientResolution",
        "settings.clientFullScreen", "settings.clientQuickLaunch", "settings.clientHideLauncher",
        "settings.clientShowLog", "settings.clientDedicatedGpu", "settings.clientDiscordPresence",
        "settings.unsaved.heading", "settings.validation.window", "settings.validation.memory",
        "settings.update", "settings.updateHint", "settings.updateChannel", "settings.updateStatus",
        "settings.updateCheck", "settings.updateDownload", "settings.updateApply",
        "settings.windowSize.recommended", "settings.windowSize.custom", "settings.memory.system",
        "settings.memory.fallbackSuffix", "settings.memory.defaultAllocated",
        "settings.validation.width", "settings.validation.height", "settings.validation.font",
        "settings.validation.minimumMemory", "settings.validation.maximumMemory",
        "settings.validation.clientMinimumMemory", "settings.validation.clientMaximumMemory",
        "settings.validation.clientWindowWidth", "settings.validation.clientWindowHeight",
        "settings.update.status.unread", "settings.update.status.serviceRequired",
        "settings.update.status.channelChanged", "settings.update.status.refreshed",
        "settings.update.status.checked", "settings.update.status.downloaded",
        "settings.update.status.scheduled", "settings.update.status.operationFailed",
        "settings.update.candidate.none", "settings.update.candidate.value",
        "settings.update.versions.match", "settings.update.versions.mismatch",
        "settings.update.status.summary", "settings.update.errorCode",
        "settings.update.phase.disabled", "settings.update.phase.idle", "settings.update.phase.checking",
        "settings.update.phase.available", "settings.update.phase.downloading", "settings.update.phase.ready",
        "settings.update.phase.scheduled", "settings.update.phase.applying",
        "settings.update.phase.rollingBack", "settings.update.phase.failed", "settings.update.phase.unknown",
        "settings.update.channel.stable", "settings.update.channel.beta",
        "theme.ashenJade.name", "theme.ashenJade.description", "theme.blackGold.name",
        "theme.blackGold.description", "theme.ashenSteel.name", "theme.ashenSteel.description",
        "theme.bloodMoon.name", "theme.bloodMoon.description",
        "online.window.title", "online.heading", "online.description", "online.searchPack",
        "online.search", "online.featured", "online.source", "online.sort", "online.gameVersion",
        "online.loader", "online.category", "online.resultLimit", "online.maxResults", "online.availableVersions",
        "online.serverName", "online.minecraftEula.accept", "online.minecraftEula.link",
        "online.downloadInstall", "online.officialKey", "online.author",
        "online.provider.curseForgeTransientHint", "online.curseForgeApiKey",
        "online.curseForgeApiKeyPrivacy",
        "remote.window.title", "remote.heading", "remote.description", "remote.connectionMethod",
        "remote.connectionStatus", "remote.mobileUrl", "remote.localPort", "remote.accounts",
        "remote.addAccount", "remote.username", "remote.pin", "remote.confirmPin", "remote.createAccount",
        "remote.permissions", "remote.permission.start", "remote.permission.stop",
        "remote.permission.restart", "remote.permission.command", "remote.permission.players",
        "remote.permission.backup", "remote.reconnect", "remote.closeWeb", "remote.copyUrl",
        "remote.service.description", "remote.service.lifecycle", "remote.service.configuration",
        "remote.service.publicUrl", "remote.service.lastUpdated", "remote.service.enableWeb",
        "remote.service.securityBoundary", "remote.service.emailOptional",
        "remote.service.initialFullPermission", "remote.service.initialFullPermissionHint",
        "remote.service.accountEnabled", "remote.service.selectAccount",
        "remote.service.allowSignIn", "remote.service.role", "remote.service.role.owner",
        "remote.service.role.admin", "remote.service.role.operator", "remote.service.role.viewer",
        "remote.service.currentPin", "remote.service.resetPin",
        "remote.service.grantAll", "remote.service.clearAll", "remote.service.saveAuthorization",
        "remote.service.deleteAccount", "remote.service.allPermissions", "remote.service.category",
        "remote.service.highRisk", "remote.service.globalGrant", "remote.service.devices",
        "remote.service.devicesHint", "remote.service.lastUsed", "remote.service.idleExpiry",
        "remote.service.revokeDevice", "remote.service.localAccount", "remote.service.lockedUntil",
        "remote.service.canSignIn", "remote.service.showPin", "remote.service.hidePin",
        "remote.service.grantLimit", "remote.service.loading", "remote.service.unknown",
        "remote.service.enabled", "remote.service.disabled", "remote.service.hostRunning",
        "remote.service.hostStopped", "remote.service.funnelConnected",
        "remote.service.funnelDisconnected", "remote.service.retryAt",
        "remote.service.refreshed", "remote.service.started", "remote.service.stopped",
        "remote.service.reconnected", "remote.service.usernameInvalid",
        "remote.service.pinMismatch", "remote.service.accountCreated",
        "remote.service.selectAccountError", "remote.service.authorizationSaved",
        "remote.service.invalidRevealedPin", "remote.service.pinRevealed",
        "remote.service.pinReset", "remote.service.deleteAccountConfirm",
        "remote.service.accountDeleted", "remote.service.grantAllPending",
        "remote.service.clearAllPending", "remote.service.revokeDeviceConfirm",
        "remote.service.deviceRevoked", "remote.service.urlCopied",
        "remote.service.urlCopyFailed", "remote.service.urlOpened",
        "remote.service.urlOpenFailed", "remote.service.emailInvalid",
        "remote.service.error.accessDenied", "remote.service.error.accountNotFound",
        "remote.service.error.deviceNotFound", "remote.service.error.unavailable",
        "remote.service.error.rejected",
        "remote.console.window.title", "remote.console.heading", "remote.console.description",
        "remote.console.lifecycleHint", "remote.console.publicUrl", "remote.console.versionLabel",
        "remote.console.uptimeLabel", "remote.console.copyUrl", "remote.console.openBrowser",
        "remote.console.stopWeb", "remote.console.reconnect", "remote.console.state.disabled",
        "remote.console.state.connecting", "remote.console.state.connected", "remote.console.state.stopping",
        "remote.console.state.reconnecting", "remote.console.state.error",
        "remote.console.state.closedForRun", "remote.console.duration.days",
        "remote.legacy.account.createdLocal", "remote.legacy.account.createdServiceUnavailable",
        "remote.legacy.account.createdTailscale", "remote.legacy.account.deleted",
        "remote.legacy.account.deleteLocalConfirm", "remote.legacy.account.deleteMissing",
        "remote.legacy.account.deleteTailscaleConfirm", "remote.legacy.account.deleteTitle",
        "remote.legacy.account.local", "remote.legacy.account.permissionsMissing",
        "remote.legacy.account.permissionsSaved", "remote.legacy.account.pinHidden",
        "remote.legacy.account.pinReset", "remote.legacy.account.pinRevealed",
        "remote.legacy.account.pinUnavailable", "remote.legacy.account.refreshFailed",
        "remote.legacy.account.resetMissing", "remote.legacy.account.verifiedIdentity",
        "remote.legacy.accounts.emptyLocal", "remote.legacy.accounts.emptyTailscale",
        "remote.legacy.accounts.summary", "remote.legacy.cloudflared.downloading",
        "remote.legacy.cloudflared.installed", "remote.legacy.cloudflared.installFailed",
        "remote.legacy.cloudflared.installHint", "remote.legacy.cloudflared.installing",
        "remote.legacy.cloudflared.pickerFilter", "remote.legacy.cloudflared.pickerTitle",
        "remote.legacy.cloudflared.preparing", "remote.legacy.cloudflared.trustIncomplete",
        "remote.legacy.cloudflared.vaultProblem", "remote.legacy.cloudflared.verified",
        "remote.legacy.cloudflared.verifiedWithoutReceipt", "remote.legacy.devices.allRevoked",
        "remote.legacy.devices.alreadyRevoked", "remote.legacy.devices.empty",
        "remote.legacy.devices.revokeConfirm", "remote.legacy.devices.revoked",
        "remote.legacy.devices.revokeTitle", "remote.legacy.devices.sessionsRevoked",
        "remote.legacy.devices.summary", "remote.legacy.gmail.codeSent",
        "remote.legacy.gmail.expired", "remote.legacy.gmail.mustMatch",
        "remote.legacy.gmail.notSent", "remote.legacy.gmail.verified",
        "remote.legacy.mode.funnelDescription", "remote.legacy.mode.namedDescription",
        "remote.legacy.mode.quickDescription", "remote.legacy.mode.tailscaleDescription",
        "remote.legacy.namedTunnelLabel", "remote.legacy.notStored",
        "remote.legacy.page.cloudflareDownload", "remote.legacy.page.googleAppPasswords",
        "remote.legacy.page.openFailed", "remote.legacy.page.tailscaleDownload",
        "remote.legacy.page.tailscaleFunnel", "remote.legacy.page.tailscaleHttps",
        "remote.legacy.provider.closedForRun", "remote.legacy.provider.connected",
        "remote.legacy.provider.connecting", "remote.legacy.provider.reconnecting",
        "remote.legacy.provider.state", "remote.legacy.provider.waiting",
        "remote.legacy.securityStore.protected", "remote.legacy.smtp.authenticationFailed",
        "remote.legacy.smtp.deleteConfirm", "remote.legacy.smtp.deleted",
        "remote.legacy.smtp.deleteTitle", "remote.legacy.smtp.saved",
        "remote.legacy.state.closedForRun", "remote.legacy.state.connected",
        "remote.legacy.state.connecting", "remote.legacy.state.disconnected",
        "remote.legacy.state.notInstalled", "remote.legacy.state.reconnecting",
        "remote.legacy.state.waiting", "remote.legacy.status.applyFailed",
        "remote.legacy.status.clipboardFailed", "remote.legacy.status.copied",
        "remote.legacy.status.refreshFailed", "remote.legacy.status.refreshRecovery",
        "remote.legacy.status.stopFailed", "remote.legacy.tailscale.certificateGuidance",
        "remote.legacy.tailscale.funnelCertificateGuidance", "remote.legacy.token.deleted",
        "remote.legacy.token.deleteFailed", "remote.legacy.token.notStored",
        "remote.legacy.token.replaced", "remote.legacy.token.saved",
        "remote.legacy.token.saveFailed", "remote.legacy.token.stopFailed",
        "remote.legacy.token.stored", "remote.legacy.token.unchanged",
        "remote.legacy.ui.accountSettings", "remote.legacy.ui.accountSettingsAria",
        "remote.legacy.ui.allowedGmail", "remote.legacy.ui.allowedGmailAria",
        "remote.legacy.ui.allowedGmailHint", "remote.legacy.ui.autoConnectHint",
        "remote.legacy.ui.chooseFile", "remote.legacy.ui.cloudflaredPath",
        "remote.legacy.ui.confirmNewPinAria", "remote.legacy.ui.confirmPinAria",
        "remote.legacy.ui.credentialRules", "remote.legacy.ui.deleteAccount",
        "remote.legacy.ui.deleteSmtp", "remote.legacy.ui.deleteToken",
        "remote.legacy.ui.deviceExpiryLabel", "remote.legacy.ui.deviceLastUsedLabel",
        "remote.legacy.ui.deviceUsernameLabel", "remote.legacy.ui.entryLabel",
        "remote.legacy.ui.funnelAuth", "remote.legacy.ui.funnelBoundary",
        "remote.legacy.ui.funnelDescription", "remote.legacy.ui.funnelHeading",
        "remote.legacy.ui.funnelLocalUrl", "remote.legacy.ui.funnelLocalUrlAria",
        "remote.legacy.ui.initialPermissions", "remote.legacy.ui.installCloudflared",
        "remote.legacy.ui.installCloudflaredAria", "remote.legacy.ui.localPortAria",
        "remote.legacy.ui.mobileHint", "remote.legacy.ui.mode.funnel",
        "remote.legacy.ui.mode.funnelAria", "remote.legacy.ui.mode.named",
        "remote.legacy.ui.mode.namedAria", "remote.legacy.ui.mode.quick",
        "remote.legacy.ui.mode.tailscale", "remote.legacy.ui.namedDescription",
        "remote.legacy.ui.namedHeading", "remote.legacy.ui.namedLocalUrl",
        "remote.legacy.ui.namedLocalUrlAria", "remote.legacy.ui.namedPublicUrl",
        "remote.legacy.ui.namedPublicUrlAria", "remote.legacy.ui.namedPublicUrlHint",
        "remote.legacy.ui.namedTrustWarning", "remote.legacy.ui.newPinAria",
        "remote.legacy.ui.newToken", "remote.legacy.ui.openCloudflaredDownload",
        "remote.legacy.ui.openFunnelDocs", "remote.legacy.ui.openGoogleAppPasswords",
        "remote.legacy.ui.openTailscaleDownload", "remote.legacy.ui.openTailscaleHttps",
        "remote.legacy.ui.permissionsHint", "remote.legacy.ui.pinAria",
        "remote.legacy.ui.quickWarning", "remote.legacy.ui.recipientGmail",
        "remote.legacy.ui.recipientGmailAria", "remote.legacy.ui.refreshDevices",
        "remote.legacy.ui.registrationDescription", "remote.legacy.ui.registrationHeading",
        "remote.legacy.ui.rememberedDevices", "remote.legacy.ui.resetAccountPin",
        "remote.legacy.ui.resetPinHeading", "remote.legacy.ui.revokeDevice",
        "remote.legacy.ui.savedSenderLabel", "remote.legacy.ui.savePermissions",
        "remote.legacy.ui.saveSmtp", "remote.legacy.ui.saveToken",
        "remote.legacy.ui.securityBoundary", "remote.legacy.ui.sendCode",
        "remote.legacy.ui.serviceLabel", "remote.legacy.ui.signOutAll",
        "remote.legacy.ui.smtpDescription", "remote.legacy.ui.smtpHeading",
        "remote.legacy.ui.smtpPassword", "remote.legacy.ui.smtpPasswordAria",
        "remote.legacy.ui.smtpSender", "remote.legacy.ui.smtpSenderAria",
        "remote.legacy.ui.tokenAria", "remote.legacy.ui.tokenHint",
        "remote.legacy.ui.usernameAria", "remote.legacy.ui.verificationCode",
        "remote.legacy.ui.verificationCodeAria", "remote.legacy.ui.verifyCode",
        "web.document.title", "web.skip", "web.brand", "web.connection.connecting",
        "web.connection.connected", "web.connection.error", "web.connection.offline",
        "web.connection.reconnecting", "web.signOut", "web.dismiss", "web.loading.title",
        "web.loading.hint", "web.auth.title", "web.auth.description", "web.auth.unavailable",
        "web.auth.username", "web.auth.usernameHint", "web.auth.pin", "web.auth.pinHint",
        "web.auth.remember", "web.auth.rememberHint", "web.auth.rememberUnavailable",
        "web.auth.securityLoading", "web.auth.login", "web.auth.loggingIn", "web.auth.privacy",
        "web.install.title", "web.install.instructions", "web.install.warning", "web.install.standalone",
        "web.dashboard.eyebrow", "web.dashboard.title", "web.dashboard.running",
        "web.dashboard.allServers", "web.dashboard.onlinePlayers", "web.dashboard.memory",
        "web.dashboard.servers", "web.dashboard.noData", "web.dashboard.showAll",
        "web.update.title", "web.update.hint", "web.update.channel", "web.update.status",
        "web.update.stable", "web.update.beta", "web.update.check", "web.update.download",
        "web.update.apply", "web.update.unavailable",
        "web.servers.eyebrow", "web.servers.title", "web.servers.search", "web.servers.back",
        "web.server.power", "web.server.overview", "web.server.console", "web.server.players",
        "web.server.environment",
        "web.server.readOnly", "web.server.manualBackup", "web.server.manualBackupHint",
        "web.server.createBackup", "web.server.remoteHelp", "web.server.remoteHelpText",
        "web.console.messages", "web.console.ordinary", "web.console.diagnostics",
        "web.console.notRead", "web.console.reload", "web.console.output", "web.console.command",
        "web.console.commandPlaceholder", "web.players.list", "web.players.action",
        "web.players.actionHint", "web.players.name", "web.players.namePlaceholder",
        "web.players.actionLabel", "web.players.reason", "web.players.reasonPlaceholder",
        "web.players.submit", "web.environment.title", "web.environment.hint",
        "web.environment.refresh", "web.environment.loading", "web.environment.loadFailed",
        "web.environment.java", "web.environment.javaStatus", "web.environment.javaAvailable",
        "web.environment.javaUnavailable", "web.environment.javaVersion", "web.environment.javaMajor",
        "web.environment.javaVendor", "web.environment.javaKind", "web.environment.javaArchitecture",
        "web.environment.addons", "web.environment.addonsCount", "web.environment.addonsEmpty",
        "web.environment.addonsUnavailable", "web.environment.addonsTruncated",
        "web.environment.mod", "web.environment.plugin", "web.environment.unknown",
        "web.player.kick", "web.player.ban", "web.player.pardon",
        "web.player.op", "web.player.deop", "web.player.whitelistAdd", "web.player.whitelistRemove",
        "web.player.whitelistOn", "web.player.whitelistOff", "web.nav.primary", "web.nav.dashboard",
        "web.nav.servers", "web.confirm.title", "web.noScript", "web.state.stopped",
        "web.state.starting", "web.state.running", "web.state.stopping", "web.state.crashed",
        "web.state.faulted", "web.state.unknown", "web.server.unnamed", "web.server.versionUnknown",
        "web.time.daysHours", "web.time.hoursMinutes", "web.time.minutes", "web.status.aria",
        "web.auth.lockedUntil", "web.error.default", "web.error.unauthorized",
        "web.error.forbidden", "web.error.conflict", "web.error.rateLimitedUntil",
        "web.error.rateLimited", "web.error.unreachable", "web.empty.search",
        "web.empty.servers", "web.empty.console", "web.empty.diagnostics", "web.empty.players",
        "web.dashboard.updatedAt", "web.dashboard.updatedNow", "web.players.count",
        "web.players.online", "web.players.offline", "web.players.banned", "web.players.selected",
        "web.action.pending", "web.action.sent", "web.connection.retryIn",
        "web.language.select", "web.auth.offline", "web.auth.securityNotReady",
        "web.auth.usernameInvalid", "web.auth.pinInvalid", "web.auth.incomplete",
        "web.auth.rememberFailed", "web.auth.loginRemembered", "web.auth.loginSuccess",
        "web.auth.loginFailed", "web.auth.signOutTitle", "web.auth.signOutMessage",
        "web.auth.signOutSuccess", "web.auth.signOutFailed", "web.device.apple",
        "web.device.mobile", "web.device.standalone", "web.device.browser",
        "web.error.blockedPath", "web.error.csrfUnavailable", "web.error.noResponse",
        "web.error.refreshAuth", "web.error.loadAuth", "web.error.refreshPermissions",
        "web.error.refreshData", "web.error.offlineMutation", "web.error.localizationUnavailable",
        "web.api.deviceNameInvalid", "web.api.deviceCapacityExceeded",
        "web.api.credentialsFormatInvalid", "web.api.credentialsInvalid", "web.api.rememberUnavailable",
        "web.api.ingressIdentityRequired", "web.api.auditDeviceEnrollUnavailable",
        "web.api.auditDeviceRefreshUnavailable", "web.api.auditSessionUnavailable",
        "web.api.consoleQueryInvalid", "web.api.updateChannelInvalid", "web.api.updateRequestInvalid",
        "web.api.commandInvalid", "web.api.idempotencyKeyRequired", "web.api.auditOperationUnavailable",
        "web.api.idempotencyConflict", "web.api.idempotencyCapacity", "web.api.idempotencyNoResult",
        "web.api.signInRequired", "web.api.forbidden", "web.api.backupConfirmationRequired",
        "web.api.serverIdInvalid", "web.api.backupIdInvalid", "web.api.commandRequired",
        "web.api.commandSingleLine", "web.api.commandTooLong", "web.api.playerActionInvalid",
        "web.api.whitelistPlayerForbidden", "web.api.playerNameInvalid", "web.api.playerReasonSingleLine",
        "web.api.playerReasonUnsupported", "web.api.requestInvalid", "web.api.rateLimited",
        "web.api.remoteUnavailable", "web.api.operationFailed",
        "web.server.cardAria",
        "web.server.playerSuffix", "web.server.openDetails", "web.server.stopPlayersNotice",
        "web.server.actionConfirm", "web.server.actionConfirmTitle", "web.server.actionFailed",
        "web.console.summary", "web.console.more", "web.console.commandInvalid",
        "web.console.commandSent", "web.console.readFailed", "web.console.sendFailed",
        "web.player.unknown", "web.player.uuidUnknown", "web.player.selectAria",
        "web.player.whitelistInvalid", "web.player.nameInvalid", "web.player.targetServer",
        "web.player.actionConfirm", "web.player.actionFailed", "web.player.noNameRequired",
        "web.backup.confirmTitle", "web.backup.confirmMessage", "web.backup.created",
        "web.backup.failed", "web.backup.listTitle", "web.backup.listHint", "web.backup.refresh",
        "web.backup.loading", "web.backup.empty", "web.backup.loadFailed", "web.backup.createdAt",
        "web.backup.size", "web.backup.restore", "web.backup.restoreConfirmTitle",
        "web.backup.restoreConfirmMessage", "web.backup.restoreSecondTitle",
        "web.backup.restoreSecondMessage", "web.backup.restoring", "web.backup.restored",
        "web.backup.restoreFailed", "web.backup.requiresStopped", "web.backup.readUnavailable",
        "web.connection.offlineNotice", "web.connection.offlineAuthNotice",
        "web.connection.authRecoveryFailed", "web.connection.reconnectFailed",
        "web.connection.serviceUnavailable", "web.players.readFailed",
        "notification.heading", "notification.history", "notification.settings",
        "notification.provider.discord", "notification.event.serverStarted",
        "notification.event.serverStopped", "notification.event.serverCrashed",
        "notification.event.serverStartFailed", "notification.event.playerJoined",
        "notification.event.playerLeft", "notification.event.backupCompleted",
        "notification.event.updateCompleted", "notification.event.updateFailed",
        "notification.event.securityAlert", "notification.status.queued",
        "notification.status.sent", "notification.status.failed", "notification.status.disabled",
        "notification.window.title", "notification.description", "notification.discord.configured",
        "notification.discord.notConfigured", "notification.discord.hint", "notification.discord.store",
        "notification.discord.remove", "notification.history.recent", "notification.history.hint",
        "notification.column.provider", "notification.column.state", "notification.column.attempts",
        "notification.column.nextAttempt", "notification.column.result",
        "notification.result.deliveredAt", "notification.result.pending", "notification.result.failureCode",
        "notification.status.loading", "notification.status.configured", "notification.status.removed",
        "notification.status.refreshed", "notification.status.operationFailed",
        "notification.discord.disabled", "notification.subscriptions.title",
        "notification.subscriptions.serverLifecycle", "notification.subscriptions.backups",
        "notification.subscriptions.modpacks", "notification.subscriptions.productUpdates",
        "notification.subscriptions.providerHealth", "notification.subscriptions.throttle",
        "notification.subscriptions.throttleHint", "notification.subscriptions.save",
        "notification.status.preferencesSaved",
        "notification.message.defaultServerName", "notification.message.serverStarted",
        "notification.message.serverStopped", "notification.message.serverCrashed",
        "notification.message.backupCompleted", "notification.message.backupRestored",
        "notification.message.backupFailed", "notification.message.modpackUpdateCompleted",
        "notification.message.modpackUpdateRolledBack", "notification.message.modpackUpdateFailed",
        "notification.message.productUpdateAvailable", "notification.message.productUpdateCompleted",
        "notification.message.productUpdateRolledBack", "notification.message.productUpdateFailed",
        "notification.message.providerDisabled", "notification.message.unknown",
        "settings.notification.description", "settings.notification.manage",
        "password.reveal", "password.hide",
        "appearance.window.title", "appearance.heading", "appearance.description", "appearance.colors",
        "appearance.color.window", "appearance.color.panel", "appearance.color.raised", "appearance.color.border",
        "appearance.color.accent", "appearance.color.accentDark", "appearance.color.text", "appearance.color.muted",
        "appearance.aria.windowColor", "appearance.aria.panelColor", "appearance.aria.raisedColor",
        "appearance.aria.borderColor", "appearance.aria.accentColor", "appearance.aria.accentDarkColor",
        "appearance.aria.textColor", "appearance.aria.mutedColor", "appearance.aria.patternColor",
        "appearance.pattern", "appearance.pattern.style", "appearance.pattern.color", "appearance.pattern.opacity",
        "appearance.image", "appearance.image.choose", "appearance.image.opacity", "appearance.image.hint",
        "appearance.aria.chooseImage", "appearance.aria.clearImage", "appearance.reset", "appearance.preview",
        "jobs.window.title", "jobs.heading", "jobs.cleanupHint", "jobs.cancelAll", "jobs.clearFinished",
        "core.window.title", "core.heading", "core.description", "core.available", "core.versions", "core.search",
        "core.eula.accept", "core.eula.link",
        "core.recommended", "core.published", "core.serverName", "core.create", "core.aria.available",
        "core.aria.search", "core.aria.versions", "core.aria.detailProgress", "core.aria.cancelOrClose",
        "deleteServer.window.title", "deleteServer.heading", "deleteServer.description", "deleteServer.warning",
        "deleteServer.confirm", "deleteServer.aria.cancel", "deleteServer.aria.confirm",
        "removeServer.window.title", "removeServer.heading", "removeServer.description", "removeServer.preserved",
        "removeServer.aria.cancel", "removeServer.aria.confirm",
        "importChoice.window.title", "importChoice.heading", "importChoice.description", "importChoice.folder",
        "importChoice.folder.description", "importChoice.folder.aria", "importChoice.jar",
        "importChoice.jar.description", "importChoice.jar.aria", "importChoice.cancel.aria",
        "importJar.window.title", "importJar.heading", "importJar.description", "importJar.source",
        "importJar.detectedCore", "importJar.confidence", "importJar.javaVersion", "importJar.evidence",
        "importJar.createIsolated",
        "importFolder.window.title", "importFolder.heading", "importFolder.folder", "importFolder.modpack",
        "importFolder.coreMinecraft", "importFolder.host", "importFolder.launchSource",
        "importFolder.javaArguments", "importFolder.memorySource", "importFolder.evidence", "importFolder.add",
        "modpackUpdate.window.title", "modpackUpdate.heading", "modpackUpdate.current", "modpackUpdate.target",
        "modpackUpdate.method", "modpackUpdate.description", "modpackUpdate.preserved", "modpackUpdate.warning",
        "modpackUpdate.acknowledge", "modpackUpdate.confirm",
        "modpackUpdate.curseForgeCredential.title", "modpackUpdate.curseForgeCredential.heading",
        "modpackUpdate.curseForgeCredential.description", "modpackUpdate.curseForgeCredential.required",
        "online.results.aria", "online.authorPrefix", "online.cancelOrClose.aria",
        "paper.window.title", "paper.heading", "paper.description", "paper.searchHint", "paper.stableBuild",
        "paper.downloadCreate",
        "serverAppearance.window.title", "serverAppearance.headingSuffix", "serverAppearance.description",
        "serverAppearance.background", "serverAppearance.background.empty", "serverAppearance.opacity",
        "serverAppearance.background.choose", "serverAppearance.background.clear", "serverAppearance.icon",
        "serverAppearance.icon.empty", "serverAppearance.icon.choose", "serverAppearance.applyOpacity",
        "common.yes", "common.no", "common.unexpectedError", "common.errorWithDetail",
        "common.operationFailed", "common.incompleteData", "common.notSelected", "common.invalidDefaultResult",
        "appearance.dialog.chooseImage", "appearance.dialog.imageFilter", "appearance.pattern.none",
        "appearance.pattern.dots", "appearance.pattern.grid", "appearance.pattern.diagonal",
        "appearance.status.initial", "appearance.status.noImage", "appearance.status.loaded",
        "appearance.status.unchanged", "appearance.status.previewed", "appearance.status.saved",
        "appearance.status.saveFailed", "appearance.status.resetPreview", "appearance.status.removed",
        "appearance.status.reverted",
        "jobs.schedulingProfile", "jobs.summary.active", "jobs.summary.finished", "jobs.summary.empty",
        "jobs.activity.empty", "jobs.state.queued", "jobs.state.running", "jobs.state.finalizing",
        "jobs.state.cancelling", "jobs.state.completed", "jobs.state.failed", "jobs.state.cancelled",
        "jobs.kind.core", "jobs.kind.modpack", "jobs.time.processing", "jobs.time.finished",
        "jobs.time.started", "jobs.time.enqueued", "jobs.status.queuedPosition", "jobs.status.preparing",
        "jobs.status.processing", "jobs.status.finalizing", "jobs.status.completed", "jobs.status.failed",
        "jobs.status.cancelled", "jobs.status.cancellingCleanup", "jobs.error.shuttingDown",
        "jobs.error.duplicateServer", "jobs.error.alreadyQueued", "jobs.error.reserveFolder",
        "jobs.error.folderBusy", "jobs.error.queueClosed", "jobs.error.addCore", "jobs.error.addCoreDetail",
        "jobs.error.addModpack", "jobs.error.addModpackDetail", "jobs.activity.createCore",
        "jobs.activity.installModpack",
        "core.cancelOperation", "core.status.preparingCatalog", "core.status.chooseCore", "core.error.noCores",
        "core.progress.readingCores", "core.status.noCores", "core.status.loadedCores",
        "core.status.coresCancelled", "core.error.readCores", "core.progress.readVersions",
        "core.status.noVersionsFor", "core.status.loadedVersions", "core.status.versionsCancelled",
        "core.status.versionsCancelledRetry", "core.error.readVersions", "core.status.versionsFailedNoFake",
        "core.progress.preparingCreate", "core.status.created", "core.status.createCancelled",
        "core.error.createFailed", "core.validation.selectCoreVersion", "core.validation.versionMismatch",
        "core.validation.serverName", "core.validation.eulaRequired", "core.progress.cancellingCreate", "core.progress.cancellingRead",
        "core.status.backgroundReadingVersions", "core.status.noVersionData", "core.status.noVersionMatch",
        "core.status.updateFailedCache", "core.status.updateFailedNoCache",
        "core.status.backgroundUpdatingVersions", "core.status.noVersionsNoFake",
        "core.status.showingTrustedCache", "core.status.waitingVersions",
        "core.stage.preparing", "core.stage.resolvingVersion", "core.stage.preparingDirectory",
        "core.stage.downloading", "core.stage.verifying", "core.stage.installing",
        "core.stage.detectingServer", "core.stage.finalizing", "core.stage.creating",
        "core.status.readingLocalCatalog", "core.status.backgroundCancelled",
        "core.error.localCatalogFailed", "core.status.catalogUnavailable",
        "core.status.backgroundUpdateCancelled", "core.status.backgroundUpdateIncomplete",
        "core.error.backgroundUpdateFailed", "core.build.unspecified", "core.release.unavailable",
        "core.catalog.bootstrap.fresh", "core.catalog.bootstrap.stale", "core.catalog.bootstrap.baseline",
        "core.catalog.cacheRejectedSuffix", "core.catalog.cooldown.failed",
        "core.catalog.cooldown.succeeded", "core.catalog.noSources",
        "core.catalog.cacheWriteFailedSuffix", "core.catalog.updated",
        "core.catalog.failedWithCache", "core.catalog.failed", "core.catalog.progress",
        "core.catalog.completed", "core.catalog.completedWithFailures",
        "core.catalog.error.productLimit",
        "importJar.customUnrecognized", "importJar.warning.lowConfidence", "importJar.warning.installer",
        "importJar.warning.java", "importJar.status.ready",
        "importFolder.host.windows", "importFolder.host.linux", "importFolder.host.unsupported",
        "importFolder.script.generated", "importFolder.java.required", "importFolder.memory.maximum",
        "importFolder.memory.arguments", "importFolder.intro.autoDetected", "importFolder.intro.selected",
        "importFolder.warning.directJava", "importFolder.warning.port",
        "modpackUpdate.validation.selectVersion", "modpackUpdate.validation.acknowledge",
        "paper.validation.selectVersion",
        "online.downloadCount", "online.downloadCountUnavailable", "online.updatedDate",
        "online.updatedDateUnavailable", "online.artwork.loading", "online.artwork.fallback",
        "online.status.initial", "online.heading.featured", "online.sort.relevance", "online.sort.downloads",
        "online.sort.updated", "online.sort.newest", "online.filter.allVersions", "online.filter.allLoaders",
        "online.validation.provider", "online.status.preparingFeatured", "online.provider.ftbHint",
        "online.provider.queryHint", "online.provider.curseForgeUnavailable", "online.validation.noServerPack",
        "online.validation.minecraftEulaRequired",
        "online.cancelOperation", "online.status.loadingFeatured", "online.status.noFeatured",
        "online.status.loadedFeatured", "online.status.featuredCancelled", "online.error.featuredFailed",
        "online.validation.searchQuery", "online.heading.searchResults", "online.status.searching",
        "online.status.noResults", "online.status.resultsFound", "online.status.searchCancelled",
        "online.error.searchFailed", "online.status.loadingVersions", "online.status.noVersions",
        "online.status.loadedVersions", "online.status.versionsCancelled", "online.error.versionsFailed",
        "online.stage.preparing", "online.stage.metadata", "online.stage.downloading",
        "online.stage.verifying", "online.stage.extracting", "online.stage.loader",
        "online.stage.detectingServer", "online.stage.finalizing", "online.stage.installing",
        "online.status.installed", "online.status.installCancelled", "online.error.installFailed",
        "online.validation.selectPackVersion", "online.validation.versionMismatch",
        "online.status.cancellingInstall", "online.status.cancellingOperation",
        "online.filter.allCategories", "online.category.adventure", "online.category.challenging",
        "online.category.combat", "online.category.kitchenSink", "online.category.lightweight",
        "online.category.magic", "online.category.multiplayer", "online.category.optimization",
        "online.category.quests", "online.category.technology", "online.status.filtersChanged",
        "online.status.curseForgeKeyRequired",
        "online.workflow.ftb.downloadInstaller", "online.workflow.ftb.downloadVerifyInstaller",
        "online.workflow.ftb.installerVerified", "online.workflow.ftb.detecting",
        "online.workflow.curse.resolvingServerPack", "online.workflow.curse.downloadingServerPack",
        "online.workflow.extractingServerPack", "online.workflow.extractingFiles",
        "online.workflow.detectingTrustedLaunch", "online.workflow.curse.downloadingClientManifest",
        "online.workflow.curse.downloadingClientMetadata", "online.workflow.curse.readingManifest",
        "online.workflow.quiltUnsupported", "online.workflow.preparingJava", "online.workflow.resolvingJava",
        "online.workflow.installingLoader", "online.workflow.curse.loaderInstalling",
        "online.workflow.loaderInstalledDetecting", "online.workflow.curse.launchNotFound",
        "online.workflow.serverPackFinalizing", "online.workflow.error.modrinthProjectMismatch",
        "online.workflow.modrinth.loaderInstalling", "online.workflow.modrinth.launchNotFound",
        "online.workflow.modrinth.finalizing", "online.workflow.error.packPathMap",
        "online.workflow.error.jarPathMap", "online.workflow.error.folderPathMap",
        "online.workflow.error.jarDetectionMissing", "online.workflow.ftb.pack",
        "online.workflow.error.curseLoader", "online.workflow.error.curseCategory",
        "online.workflow.error.invalidIdentifier", "online.workflow.unknownLoader",
        "online.workflow.error.modrinthLoader", "online.workflow.loader.downloadVanilla",
        "online.workflow.loader.downloadFabric", "online.workflow.loader.downloadForge",
        "online.workflow.loader.downloadNeoForge", "online.workflow.loader.runInstaller",
        "online.workflow.loader.validateOutput", "online.workflow.loader.mergeOutput",
        "online.workflow.loader.runIsolatedInstaller", "online.workflow.loader.validateOfficialOutput",
        "online.workflow.loader.mergeOfficialOutput", "online.workflow.modrinth.downloadPack",
        "online.workflow.modrinth.downloadFiles", "online.workflow.modrinth.inspect",
        "online.workflow.modrinth.overrides", "online.workflow.modrinth.serverOverrides",
        "online.workflow.modrinth.downloading", "online.workflow.modrinth.extracting",
        "online.workflow.concurrency.auto", "online.workflow.concurrency.fixed",
        "online.workflow.downloadDetail", "online.workflow.error.sourceMismatch",
        "online.workflow.error.noServerPack", "online.workflow.error.serverNameLength",
        "online.workflow.error.provenanceMismatch", "online.workflow.error.provenanceUnavailable",
        "online.workflow.error.providerUnavailable", "online.workflow.error.curseApiKeyRequired",
        "online.workflow.error.curseApiKeyEmpty", "online.workflow.error.unsafeStagingCleanup",
        "online.workflow.error.artworkReparsePoint", "online.workflow.error.javaRuntime",
        "online.validation.offsetNonNegative", "online.validation.limitRange", "online.validation.rangeMaximum",
        "online.validation.invalidText", "online.validation.advancedFiltersUnsupported",
        "online.version.serverPackAvailable", "online.version.serverPackUnavailable",
        "online.workflow.ftb.error", "online.workflow.ftb.installing", "online.workflow.ftb.rateDetail",
        "online.workflow.ftb.estimatingDetail", "online.workflow.ftb.downloadProgress",
        "online.workflow.duration.calculating", "online.workflow.duration.hoursMinutes",
        "online.workflow.duration.minutesSeconds", "online.workflow.duration.seconds",
        "provider.window.title", "provider.heading", "provider.description", "provider.installed",
        "provider.select", "provider.id", "provider.version", "provider.publisher",
        "provider.enabledHealth", "provider.capabilities", "provider.lastError", "provider.enable",
        "provider.disable", "provider.healthCheck", "provider.uninstall", "provider.publishers",
        "provider.publisher.removeSelected", "provider.publisher.pinHeading", "provider.publisher.pinHint",
        "provider.publisherId", "provider.publicKeyPem", "provider.publisher.pin", "provider.install.tab",
        "provider.install.inboxHeading", "provider.install.inboxHint", "provider.install.fileName",
        "provider.install.sha256", "provider.install.providerId", "provider.install.version",
        "provider.install.publisherId", "provider.install.signatureHeading", "provider.install.signatureHint",
        "provider.install.algorithm", "provider.install.signatureVersion", "provider.install.signatureBase64",
        "provider.install.allowDowngrade", "provider.install.verifyInstall", "provider.status.loading",
        "provider.status.refreshed", "provider.status.enabled", "provider.status.disabled",
        "provider.status.healthSucceeded", "provider.status.healthFailed", "provider.confirm.uninstall",
        "provider.status.uninstalled", "provider.status.publisherPinned", "provider.confirm.removePublisher",
        "provider.status.publisherRemoved", "provider.status.installed", "provider.error.invalidRegistry",
        "provider.error.invalidPublishers", "provider.error.rejected", "provider.error.serviceRequired",
        "provider.state.enabled", "provider.state.disabled", "provider.health.disabled", "provider.health.stopped",
        "provider.health.starting", "provider.health.healthy", "provider.health.degraded",
        "provider.health.failed", "provider.health.unknown", "settings.provider.manage",
        "service.status.connected", "service.status.connecting", "service.status.retrying",
        "service.status.notInstalled", "service.status.stopped", "service.status.unavailable",
        "service.status.accessDenied", "service.status.incompatible", "service.status.versionMismatch", "service.status.faulted",
        "service.status.tooltip", "service.readOnly.settings", "service.readOnly.addons",
        "service.readOnly.java", "service.readOnly.backups", "service.readOnly.backupOperation",
        "service.migration.pending", "service.registry.empty",
        .. MainWindowViewModelLocalization.Keys,
        .. ClientWorkspaceLocalization.Keys
    ]);

    private static readonly IReadOnlyDictionary<string, int> ParameterCounts =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["app.startupFailed.message"] = 1,
            ["addon.project"] = 1,
            ["online.author"] = 1,
            ["settings.windowSize.recommended"] = 1,
            ["server.detail.serviceManaged"] = 2,
            ["server.detail.local"] = 3,
            ["server.console.lines"] = 1,
            ["server.diagnostics.summary"] = 2,
            ["server.diagnostics.header"] = 1,
            ["server.players.summaryKnown"] = 2,
            ["server.players.summaryOnline"] = 1,
            ["server.modpack.source"] = 2,
            ["server.command.sendFailed"] = 1,
            ["server.memory.systemAvailable"] = 2,
            ["server.memory.autoComplete"] = 5,
            ["server.memory.autoCompleteConstrained"] = 5,
            ["server.memory.autoFailed"] = 1,
            ["server.uptime.daysHours"] = 2,
            ["settings.memory.system"] = 3,
            ["settings.memory.defaultAllocated"] = 2,
            ["settings.update.status.operationFailed"] = 1,
            ["settings.update.candidate.value"] = 1,
            ["settings.update.status.summary"] = 4,
            ["settings.update.errorCode"] = 1,
            ["web.time.daysHours"] = 2,
            ["web.time.hoursMinutes"] = 2,
            ["web.time.minutes"] = 1,
            ["web.status.aria"] = 1,
            ["web.auth.lockedUntil"] = 1,
            ["web.error.rateLimitedUntil"] = 1,
            ["web.dashboard.updatedAt"] = 1,
            ["web.players.count"] = 2,
            ["web.environment.addonsCount"] = 1,
            ["web.players.selected"] = 1,
            ["web.action.pending"] = 1,
            ["web.action.sent"] = 1,
            ["web.connection.retryIn"] = 1,
            ["web.auth.rememberFailed"] = 1,
            ["web.api.commandTooLong"] = 1,
            ["web.server.cardAria"] = 2,
            ["web.server.playerSuffix"] = 1,
            ["web.server.stopPlayersNotice"] = 1,
            ["web.server.actionConfirm"] = 3,
            ["web.server.actionConfirmTitle"] = 1,
            ["web.server.actionFailed"] = 1,
            ["web.console.summary"] = 2,
            ["web.player.selectAria"] = 1,
            ["web.player.actionConfirm"] = 2,
            ["web.player.actionFailed"] = 1,
            ["web.backup.confirmMessage"] = 1,
            ["web.backup.restoreConfirmMessage"] = 1,
            ["web.backup.restoreSecondMessage"] = 1,
            ["web.connection.offlineNotice"] = 0,
            ["service.status.retrying"] = 1,
            ["service.status.versionMismatch"] = 2,
            ["service.status.faulted"] = 1,
            ["service.status.tooltip"] = 1,
            ["service.migration.pending"] = 1,
            ["notification.result.deliveredAt"] = 1,
            ["notification.result.failureCode"] = 1,
            ["notification.status.operationFailed"] = 1,
            ["notification.message.serverStarted"] = 1,
            ["notification.message.serverStopped"] = 1,
            ["notification.message.serverCrashed"] = 1,
            ["notification.message.backupCompleted"] = 1,
            ["notification.message.backupRestored"] = 1,
            ["notification.message.backupFailed"] = 1,
            ["notification.message.modpackUpdateCompleted"] = 1,
            ["notification.message.modpackUpdateRolledBack"] = 1,
            ["notification.message.modpackUpdateFailed"] = 1,
            ["notification.message.unknown"] = 1,
            ["remote.service.lockedUntil"] = 1,
            ["remote.service.retryAt"] = 1,
            ["remote.service.accountCreated"] = 1,
            ["remote.service.authorizationSaved"] = 1,
            ["remote.service.pinReset"] = 1,
            ["remote.service.deleteAccountConfirm"] = 1,
            ["remote.service.accountDeleted"] = 1,
            ["remote.service.revokeDeviceConfirm"] = 2,
            ["remote.service.urlCopyFailed"] = 1,
            ["remote.service.urlOpenFailed"] = 1,
            ["remote.service.error.rejected"] = 1,
            ["remote.console.duration.days"] = 2,
            ["remote.legacy.account.createdServiceUnavailable"] = 1,
            ["remote.legacy.account.deleted"] = 1,
            ["remote.legacy.account.deleteLocalConfirm"] = 1,
            ["remote.legacy.account.deleteTailscaleConfirm"] = 1,
            ["remote.legacy.account.permissionsSaved"] = 1,
            ["remote.legacy.account.pinReset"] = 1,
            ["remote.legacy.account.pinRevealed"] = 1,
            ["remote.legacy.account.refreshFailed"] = 1,
            ["remote.legacy.account.verifiedIdentity"] = 2,
            ["remote.legacy.accounts.summary"] = 1,
            ["remote.legacy.cloudflared.downloading"] = 1,
            ["remote.legacy.cloudflared.installFailed"] = 1,
            ["remote.legacy.cloudflared.verified"] = 1,
            ["remote.legacy.cloudflared.verifiedWithoutReceipt"] = 1,
            ["remote.legacy.devices.allRevoked"] = 1,
            ["remote.legacy.devices.revokeConfirm"] = 1,
            ["remote.legacy.devices.revoked"] = 1,
            ["remote.legacy.devices.summary"] = 1,
            ["remote.legacy.gmail.codeSent"] = 2,
            ["remote.legacy.gmail.verified"] = 1,
            ["remote.legacy.page.openFailed"] = 2,
            ["remote.legacy.provider.closedForRun"] = 1,
            ["remote.legacy.provider.connected"] = 1,
            ["remote.legacy.provider.connecting"] = 1,
            ["remote.legacy.provider.reconnecting"] = 1,
            ["remote.legacy.provider.state"] = 2,
            ["remote.legacy.provider.waiting"] = 1,
            ["remote.legacy.status.clipboardFailed"] = 1,
            ["remote.legacy.status.refreshFailed"] = 1,
            ["remote.legacy.token.deleteFailed"] = 1,
            ["remote.legacy.token.saveFailed"] = 1,
            ["remote.legacy.token.stopFailed"] = 1,
            ["appearance.status.loaded"] = 1,
            ["common.errorWithDetail"] = 2,
            ["common.operationFailed"] = 1,
            ["jobs.schedulingProfile"] = 2,
            ["jobs.summary.active"] = 1,
            ["jobs.summary.finished"] = 1,
            ["jobs.time.finished"] = 2,
            ["jobs.time.started"] = 1,
            ["jobs.time.enqueued"] = 1,
            ["jobs.error.duplicateServer"] = 1,
            ["jobs.error.alreadyQueued"] = 1,
            ["jobs.error.reserveFolder"] = 1,
            ["jobs.error.addCoreDetail"] = 1,
            ["jobs.error.addModpackDetail"] = 1,
            ["jobs.activity.createCore"] = 2,
            ["jobs.activity.installModpack"] = 2,
            ["core.status.loadedCores"] = 1,
            ["core.progress.readVersions"] = 1,
            ["core.status.noVersionsFor"] = 1,
            ["core.status.loadedVersions"] = 2,
            ["core.status.created"] = 1,
            ["core.status.backgroundReadingVersions"] = 1,
            ["core.status.noVersionData"] = 1,
            ["core.status.noVersionMatch"] = 1,
            ["core.status.updateFailedCache"] = 1,
            ["core.status.updateFailedNoCache"] = 1,
            ["core.status.backgroundUpdatingVersions"] = 1,
            ["core.status.noVersionsNoFake"] = 1,
            ["core.status.waitingVersions"] = 1,
            ["core.error.localCatalogFailed"] = 1,
            ["core.error.backgroundUpdateFailed"] = 1,
            ["core.catalog.cacheRejectedSuffix"] = 1,
            ["core.catalog.updated"] = 3,
            ["core.catalog.failedWithCache"] = 2,
            ["core.catalog.failed"] = 2,
            ["core.catalog.progress"] = 3,
            ["core.catalog.completed"] = 1,
            ["core.catalog.completedWithFailures"] = 1,
            ["importFolder.java.required"] = 1,
            ["importFolder.memory.maximum"] = 1,
            ["online.downloadCount"] = 1,
            ["online.updatedDate"] = 1,
            ["online.maxResults"] = 1,
            ["online.heading.featured"] = 1,
            ["online.status.preparingFeatured"] = 1,
            ["online.status.loadingFeatured"] = 1,
            ["online.status.noFeatured"] = 1,
            ["online.status.loadedFeatured"] = 2,
            ["online.heading.searchResults"] = 1,
            ["online.status.searching"] = 1,
            ["online.status.resultsFound"] = 1,
            ["online.status.loadingVersions"] = 1,
            ["online.status.loadedVersions"] = 1,
            ["online.status.installed"] = 1,
            ["online.workflow.extractingFiles"] = 2,
            ["online.workflow.preparingJava"] = 1,
            ["online.workflow.resolvingJava"] = 1,
            ["online.workflow.installingLoader"] = 1,
            ["online.workflow.error.invalidIdentifier"] = 1,
            ["online.workflow.loader.runInstaller"] = 1,
            ["online.workflow.loader.runIsolatedInstaller"] = 1,
            ["online.workflow.downloadDetail"] = 4,
            ["online.workflow.error.javaRuntime"] = 1,
            ["online.validation.invalidText"] = 1,
            ["online.workflow.ftb.rateDetail"] = 3,
            ["online.workflow.ftb.estimatingDetail"] = 1,
            ["online.workflow.ftb.downloadProgress"] = 3,
            ["online.workflow.duration.hoursMinutes"] = 2,
            ["online.workflow.duration.minutesSeconds"] = 2,
            ["online.workflow.duration.seconds"] = 1,
            ["provider.status.enabled"] = 1,
            ["provider.status.disabled"] = 1,
            ["provider.status.healthSucceeded"] = 1,
            ["provider.status.healthFailed"] = 1,
            ["provider.confirm.uninstall"] = 2,
            ["provider.status.uninstalled"] = 1,
            ["provider.status.publisherPinned"] = 1,
            ["provider.confirm.removePublisher"] = 1,
            ["provider.status.publisherRemoved"] = 1,
            ["provider.status.installed"] = 2,
            ["provider.error.rejected"] = 1,
        });

    private static readonly Lazy<IReadOnlyDictionary<string, ProductLocalizationDocument>> Documents =
        new(LoadAndValidateDocuments, LazyThreadSafetyMode.ExecutionAndPublication);

    public static ProductLocalizationDocument GetDocument(string? cultureName)
    {
        var normalized = NormalizeCulture(cultureName);
        return Documents.Value[normalized];
    }

    public static ReadOnlyMemory<byte> GetJsonUtf8(string? cultureName) =>
        GetDocument(cultureName).JsonUtf8;

    /// <summary>
    /// Normalizes a supported BCP-47 language tag. Invalid and unsupported tags are rejected;
    /// callers that only need a safe value should use <see cref="NormalizeCulture"/>.
    /// </summary>
    public static bool TryNormalizeCulture(string? cultureName, out string normalizedCulture)
    {
        normalizedCulture = FallbackCulture;
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return false;
        }

        var candidate = cultureName.Trim();
        if (!SupportedBcp47TagRegex().IsMatch(candidate))
        {
            return false;
        }

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(candidate);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }

        if (culture.Name.Length == 0)
        {
            return false;
        }

        normalizedCulture = culture.TwoLetterISOLanguageName switch
        {
            "en" => EnglishCulture,
            "zh" => FallbackCulture,
            _ => FallbackCulture,
        };
        return culture.TwoLetterISOLanguageName is "en" or "zh";
    }

    public static string NormalizeCulture(string? cultureName)
        => TryNormalizeCulture(cultureName, out var normalized)
            ? normalized
            : FallbackCulture;

    public static int GetParameterCount(string key) =>
        ParameterCounts.TryGetValue(key, out var count)
            ? count
            : MainWindowViewModelLocalization.Keys.Contains(key, StringComparer.Ordinal)
                ? MainWindowViewModelLocalization.GetParameterCount(key)
                : ClientWorkspaceLocalization.GetParameterCount(key);

    public static string Format(string? cultureName, string key, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(arguments);
        var document = GetDocument(cultureName);
        if (!document.Strings.TryGetValue(key, out var value))
        {
            throw new KeyNotFoundException($"Unknown localization key: {key}.");
        }

        var expected = GetParameterCount(key);
        if (arguments.Length != expected)
        {
            throw new FormatException(
                $"Localization key '{key}' requires {expected} parameters, but {arguments.Length} were supplied.");
        }

        return expected == 0
            ? value
            : string.Format(CultureInfo.GetCultureInfo(document.Culture), value, arguments);
    }

    private static IReadOnlyDictionary<string, ProductLocalizationDocument> LoadAndValidateDocuments()
    {
        var expectedKeys = Keys.ToHashSet(StringComparer.Ordinal);
        if (expectedKeys.Count != Keys.Count)
        {
            throw new InvalidOperationException("The product localization key contract contains duplicates.");
        }

        var documents = new Dictionary<string, ProductLocalizationDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in SupportedCultures)
        {
            var json = LoadEmbeddedJson(culture);
            var model = JsonSerializer.Deserialize<ProductLocalizationJson>(json.Span)
                        ?? throw new InvalidOperationException($"Localization catalog {culture} is empty.");
            if (model.SchemaVersion != SchemaVersion || !culture.Equals(model.Culture, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Localization catalog metadata is invalid: {culture}.");
            }

            foreach (var (key, value) in MainWindowViewModelLocalization.GetStrings(culture))
            {
                if (!model.Strings.TryAdd(key, value))
                {
                    throw new InvalidOperationException(
                        $"Localization key is duplicated by the MainWindow ViewModel extension: {key}.");
                }
            }

            foreach (var (key, value) in ClientWorkspaceLocalization.GetStrings(culture))
            {
                if (!model.Strings.TryAdd(key, value))
                {
                    throw new InvalidOperationException(
                        $"Localization key is duplicated by the client workspace extension: {key}.");
                }
            }

            var actualKeys = model.Strings.Keys.ToHashSet(StringComparer.Ordinal);
            var missing = expectedKeys.Except(actualKeys, StringComparer.Ordinal).Order().ToArray();
            var orphaned = actualKeys.Except(expectedKeys, StringComparer.Ordinal).Order().ToArray();
            if (missing.Length > 0 || orphaned.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Localization key mismatch for {culture}. Missing=[{string.Join(',', missing)}], orphaned=[{string.Join(',', orphaned)}].");
            }

            foreach (var (key, value) in model.Strings)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException($"Localization value is empty: {culture}/{key}.");
                }

                ValidatePlaceholders(culture, key, value, GetParameterCount(key));
            }

            var mergedJson = JsonSerializer.SerializeToUtf8Bytes(model);
            documents[culture] = new ProductLocalizationDocument(
                model.SchemaVersion,
                model.Culture,
                new ReadOnlyDictionary<string, string>(model.Strings),
                mergedJson);
        }

        return new ReadOnlyDictionary<string, ProductLocalizationDocument>(documents);
    }

    private static ReadOnlyMemory<byte> LoadEmbeddedJson(string culture)
    {
        var assembly = typeof(ProductLocalizationCatalog).Assembly;
        var suffix = $".Localization.{culture}.v{SchemaVersion}.json";
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded localization catalog is missing: {culture}.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Unable to open localization catalog: {culture}.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        if (memory.Length is <= 0 or > 512 * 1024)
        {
            throw new InvalidOperationException($"Localization catalog has an unsafe size: {culture}.");
        }

        return memory.ToArray();
    }

    private static void ValidatePlaceholders(string culture, string key, string value, int expectedCount)
    {
        try
        {
            _ = CompositeFormat.Parse(value);
        }
        catch (FormatException error)
        {
            throw new InvalidOperationException(
                $"Localization value is not a valid composite format: {culture}/{key}.",
                error);
        }

        var indexes = CompositeFormatPlaceholderRegex().Matches(value)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .ToArray();
        var expected = Enumerable.Range(0, expectedCount).ToArray();
        if (!indexes.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Localization placeholder contract mismatch: {culture}/{key}; expected {expectedCount} positional parameters.");
        }
    }

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:,[^}:]+)?(?:\:[^}]+)?\}(?!\})", RegexOptions.CultureInvariant)]
    private static partial Regex CompositeFormatPlaceholderRegex();

    [GeneratedRegex(@"\A[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SupportedBcp47TagRegex();

    private sealed class ProductLocalizationJson
    {
        public int SchemaVersion { get; init; }
        public string Culture { get; init; } = string.Empty;
        public Dictionary<string, string> Strings { get; init; } = new(StringComparer.Ordinal);
    }
}

public sealed record ProductLocalizationDocument(
    int SchemaVersion,
    string Culture,
    IReadOnlyDictionary<string, string> Strings,
    ReadOnlyMemory<byte> JsonUtf8);
