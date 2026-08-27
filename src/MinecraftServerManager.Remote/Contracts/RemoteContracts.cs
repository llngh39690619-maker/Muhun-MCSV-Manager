using System.Text.Json.Serialization;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Remote.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<RemoteServerState>))]
public enum RemoteServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter<RemoteConsoleSeverity>))]
public enum RemoteConsoleSeverity
{
    Information,
    Warning,
    Error
}

[JsonConverter(typeof(JsonStringEnumConverter<RemoteConsoleStream>))]
public enum RemoteConsoleStream
{
    All,
    Ordinary,
    Diagnostic
}

[JsonConverter(typeof(JsonStringEnumConverter<RemotePlayerActionKind>))]
public enum RemotePlayerActionKind
{
    Kick,
    Ban,
    Pardon,
    Op,
    Deop,
    WhitelistAdd,
    WhitelistRemove,
    WhitelistOn,
    WhitelistOff
}

public sealed record RemoteServerSummaryDto(
    string Id,
    string Name,
    string Core,
    string Version,
    RemoteServerState State,
    bool Running,
    int PlayerCount,
    int? MaximumPlayers,
    double? CpuPercent,
    long? MemoryBytes,
    int? Port,
    long? UptimeSeconds);

public sealed record RemoteDashboardDto(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<RemoteServerSummaryDto> Servers);

public sealed record RemoteServerDetailDto(
    RemoteServerSummaryDto Server,
    string? JavaVersion,
    bool SupportsPlayerManagement,
    bool SupportsBackups,
    bool HasDiagnosticConsole);

[JsonConverter(typeof(JsonStringEnumConverter<RemoteServerAddonKind>))]
public enum RemoteServerAddonKind
{
    Mod,
    Plugin,
}

[JsonConverter(typeof(JsonStringEnumConverter<RemoteJavaRuntimeKind>))]
public enum RemoteJavaRuntimeKind
{
    Unknown,
    Jre,
    Jdk,
}

[JsonConverter(typeof(JsonStringEnumConverter<RemoteJavaArchitecture>))]
public enum RemoteJavaArchitecture
{
    Unknown,
    X64,
    Arm64,
    X86,
}

/// <summary>A path-free, bounded add-on label intended only for display.</summary>
public sealed record RemoteServerAddonDto(
    RemoteServerAddonKind Kind,
    string FileName,
    long SizeBytes);

/// <summary>Allowlisted Java metadata; executable and installation paths are never included.</summary>
public sealed record RemoteServerJavaRuntimeDto(
    bool Configured,
    bool Available,
    int? MajorVersion,
    string? Version,
    RemoteJavaRuntimeKind RuntimeKind,
    string Vendor,
    RemoteJavaArchitecture Architecture);

/// <summary>Bounded Web projection of Service-owned add-on and Java runtime information.</summary>
public sealed record RemoteServerAdministrationDto(
    DateTimeOffset GeneratedAtUtc,
    bool AddonsAvailable,
    IReadOnlyList<RemoteServerAddonDto> Addons,
    bool AddonsTruncated,
    RemoteServerJavaRuntimeDto Java);

public static class RemoteServerAdministrationContract
{
    public const int MaximumListedAddons = ProductServerAdministrationContract.MaximumListedAddons;
    public const int MaximumProcessedAddons = ProductServerAdministrationContract.MaximumScannedEntries;
    public const int MaximumAddonFileNameCharacters = ProductServerAdministrationContract.MaximumAddonFileNameCharacters;
    public const int MaximumJavaMetadataCharacters = ProductServerAdministrationContract.MaximumJavaMetadataCharacters;
}

public sealed record RemoteConsoleLineDto(
    long Sequence,
    DateTimeOffset TimestampUtc,
    RemoteConsoleSeverity Severity,
    RemoteConsoleStream Stream,
    string Text);

public sealed record RemoteConsolePageDto(
    IReadOnlyList<RemoteConsoleLineDto> Lines,
    long? NextCursor,
    bool HasMore);

public sealed record RemotePlayerDto(
    string Name,
    Guid? Uuid,
    bool Online,
    bool Operator,
    bool Banned,
    DateTimeOffset? LastSeenUtc);

public sealed record RemotePlayerListDto(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<RemotePlayerDto> Players);

public sealed record RemoteCredentialLoginRequestDto(string Username, string Pin);

public sealed record RemoteEmptyMutationRequestDto;

public sealed record RemoteRememberedDeviceEnrollmentRequestDto(string DeviceName);

public sealed record RemoteRememberedDeviceRefreshRequestDto(Guid RequestId);

public sealed record RemoteWebPermissionSetDto(
    bool CanStartServer,
    bool CanStopServer,
    bool CanRestartServer,
    bool CanSendConsoleCommand,
    bool CanManagePlayers,
    bool CanCreateBackup);

[JsonConverter(typeof(JsonStringEnumConverter<RemotePermissionScopeKind>))]
public enum RemotePermissionScopeKind
{
    Global,
    Server
}

public sealed record RemotePermissionGrantDto(
    string PermissionCode,
    RemotePermissionScopeKind Scope,
    Guid? ServerId);

public sealed record RemoteAuthStatusDto(
    bool Authenticated,
    string? Login,
    string? CsrfToken,
    DateTimeOffset? SessionExpiresAtUtc,
    bool CredentialRegistered = false,
    string? Username = null,
    RemoteWebPermissionSetDto? Permissions = null,
    bool RememberedDevice = false,
    DateTimeOffset? RememberedDeviceExpiresAtUtc = null,
    bool SupportsRememberedDevices = false,
    string? AntiforgeryToken = null,
    IReadOnlyList<RemotePermissionGrantDto>? PermissionGrants = null);

public sealed record RemoteCommandRequestDto(string Command);

public sealed record RemotePlayerActionRequestDto(
    string? PlayerName,
    RemotePlayerActionKind Action,
    string? Reason);

public sealed record RemoteOperationResultDto(
    bool Accepted,
    string Message,
    string? OperationId = null);

/// <summary>
/// Path-free metadata for one Service-owned backup. BackupId is an opaque identifier and
/// DisplayName is a bounded label intended only for display; neither value is a filesystem path.
/// </summary>
public sealed record RemoteBackupSummaryDto(
    string BackupId,
    string DisplayName,
    long ArchiveBytes,
    DateTimeOffset CreatedAtUtc);

/// <summary>A bounded Web projection of the Service-owned backup catalog.</summary>
public sealed record RemoteBackupListDto(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<RemoteBackupSummaryDto> Backups,
    bool HasMore);

/// <summary>
/// Explicit acknowledgement required in addition to the authenticated, authorized, CSRF-
/// protected and idempotent restore request.
/// </summary>
public sealed record RemoteBackupRestoreRequestDto(string Confirmation);

public static class RemoteBackupRestoreContract
{
    public const int MaximumListedBackups = 200;
    public const int MaximumDisplayNameCharacters = 160;
    public const string RequiredConfirmation = "RESTORE STOPPED SERVER BACKUP";
}

public sealed record RemoteConsoleQuery(
    RemoteConsoleStream Stream,
    long? After,
    int Limit);

public sealed record RemoteProductUpdateScheduleRequestDto(DateTimeOffset? NotBeforeUtc);
