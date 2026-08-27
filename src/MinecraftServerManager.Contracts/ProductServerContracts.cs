namespace MinecraftServerManager.Contracts;

/// <summary>
/// Public, provider-neutral launch modes understood by the Service API. Paths in a registration
/// are always relative to the product-owned Servers or Runtimes directory; clients cannot make
/// the Service execute an arbitrary path.
/// </summary>
public enum ProductServerLaunchKind
{
    ExecutableJar = 0,
    JavaArgumentFiles = 1,
}

public enum ProductServerState
{
    Stopped = 0,
    Starting,
    Running,
    Stopping,
    Crashed,
    Faulted,
}

public enum ProductConsoleStream
{
    StandardOutput = 0,
    StandardError,
    System,
}

public enum ProductConsoleSeverity
{
    Unclassified = 0,
    Information,
    Warning,
    Error,
    Fatal,
}

/// <summary>Verified catalog provenance retained by the Service for future in-place updates.</summary>
public enum ProductModpackSourceKind
{
    None = 0,
    Ftb,
    Modrinth,
    CurseForge,
}

/// <summary>
/// Durable Service-owned server definition. ServerDirectory is relative to ProductDataLayout's
/// Servers directory and JavaRuntimePath is relative to its Runtimes directory.
/// </summary>
public sealed record ProductServerRegistration
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string ServerDirectory { get; init; } = string.Empty;

    public string JavaRuntimePath { get; init; } = string.Empty;

    public ProductServerLaunchKind LaunchKind { get; init; }

    public string ServerJarPath { get; init; } = "server.jar";

    public IReadOnlyList<string> JavaArgumentFilePaths { get; init; } = [];

    public string CoreType { get; init; } = "Unknown";

    public string? MinecraftVersion { get; init; }

    public int MinimumMemoryMb { get; init; } = 1024;

    public int MaximumMemoryMb { get; init; } = 4096;

    public IReadOnlyList<string> JvmArguments { get; init; } = [];

    public IReadOnlyList<string> ServerArguments { get; init; } = ["nogui"];

    public string? StopCommand { get; init; }

    public int Port { get; init; } = 25565;

    public bool AutoRestart { get; init; }

    public string? ModpackProviderId { get; init; }

    public ProductModpackSourceKind ModpackSource { get; init; }

    public string? ModpackProjectId { get; init; }

    public string? ModpackVersionId { get; init; }

    public string? ModpackVersionName { get; init; }

    public bool IsInstallerArtifact { get; init; }
}

public sealed record ProductServerSummary(
    Guid Id,
    string Name,
    ProductServerState State,
    int Port,
    string CoreType,
    string? MinecraftVersion);

/// <summary>
/// Local-administrator-only projection of a Service-owned server directory. This payload is
/// available only over the ACL-protected named pipe and is never exposed by the Web API.
/// </summary>
public sealed record ProductServerDirectoryInfo(
    Guid ServerId,
    string DirectoryPath,
    bool Exists);

/// <summary>Kind of an add-on discovered in one fixed, Service-owned server subdirectory.</summary>
public enum ProductServerAddonKind
{
    Mod = 0,
    Plugin,
}

/// <summary>
/// Path-free metadata for one top-level JAR in a Service-owned mods or plugins directory.
/// FileName is a display-only leaf name and can never contain a directory component.
/// </summary>
public sealed record ProductServerAddonSummary(
    ProductServerAddonKind Kind,
    string FileName,
    long SizeBytes);

/// <summary>
/// Allowlisted Java release metadata. The managed executable path and runtime home are
/// deliberately absent from this contract because this projection may reach the remote Web UI.
/// </summary>
public sealed record ProductServerJavaRuntimeSummary(
    bool Configured,
    bool Available,
    int? MajorVersion,
    string? Version,
    string RuntimeKind,
    string Vendor,
    string Architecture);

/// <summary>
/// Bounded, path-free administration projection captured by the Windows Service. This is the
/// only add-on/Java shape that may be adapted to the remote API.
/// </summary>
public sealed record ProductServerAdministrationSnapshot(
    Guid ServerId,
    DateTimeOffset CapturedAtUtc,
    bool AddonsAvailable,
    IReadOnlyList<ProductServerAddonSummary> Addons,
    bool AddonsTruncated,
    ProductServerJavaRuntimeSummary Java);

public static class ProductServerAdministrationContract
{
    public const int MaximumListedAddons = 200;
    public const int MaximumScannedEntries = 1024;
    public const int MaximumAddonFileNameCharacters = 160;
    public const int MaximumJavaMetadataCharacters = 64;
    public const int MaximumJavaReleaseFileBytes = 64 * 1024;
}

public sealed record ProductServerDeletionResult(
    Guid ServerId,
    bool Deleted,
    DateTimeOffset CompletedAtUtc);

public sealed record ProductServerListPage(
    int Offset,
    int NextOffset,
    bool HasMore,
    IReadOnlyList<ProductServerSummary> Servers);

/// <summary>Bounded status page used by the desktop poller to avoid one IPC roundtrip per server.</summary>
public sealed record ProductServerStatusPage(
    int Offset,
    int NextOffset,
    bool HasMore,
    IReadOnlyList<ProductServerStatus> Servers);

public sealed record ProductServerResourceSample(
    DateTimeOffset Timestamp,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    TimeSpan Uptime);

public sealed record ProductServerStatus(
    ProductServerSummary Server,
    Guid? SessionId,
    int? ProcessId,
    DateTimeOffset? StartedAtUtc,
    int? LastExitCode,
    ProductServerResourceSample? Resource,
    string? LastError);

public sealed record ProductConsoleEntry(
    long Cursor,
    Guid SessionId,
    DateTimeOffset Timestamp,
    string Text,
    ProductConsoleStream Stream,
    ProductConsoleSeverity Severity,
    Guid? DiagnosticId,
    bool IsDiagnosticContinuation,
    bool TextTruncated);

/// <summary>
/// Cursor page for bounded console polling. HistoryGap is true when the requested cursor predates
/// the oldest retained line; clients should replace rather than append in that case.
/// </summary>
public sealed record ProductConsolePage(
    Guid ServerId,
    long RequestedAfterCursor,
    long OldestAvailableCursor,
    long NextCursor,
    bool HistoryGap,
    IReadOnlyList<ProductConsoleEntry> Entries);

/// <summary>
/// A bounded, path-free player projection derived from the Service console event stream. The
/// Service never performs a blocking query on the desktop refresh path.
/// </summary>
public sealed record ProductServerPlayerSummary(
    string Name,
    DateTimeOffset? LastSeenUtc);

public sealed record ProductServerPlayerList(
    Guid ServerId,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ProductServerPlayerSummary> Players);

public sealed record ProductServerMutationResult(
    Guid ServerId,
    bool Changed,
    ProductServerStatus Status);

/// <summary>
/// Editable subset of a durable registration. Launch paths, arguments, core identity, and
/// catalog provenance are intentionally absent so a desktop settings form cannot redirect a
/// Service-owned server id to another managed tree or executable.
/// </summary>
public sealed record ProductServerSettingsUpdateRequest(
    string Name,
    int MinimumMemoryMb,
    int MaximumMemoryMb,
    int Port,
    bool AutoRestart);

public sealed record ProductServerSettingsUpdateResult(
    ProductServerRegistration Registration,
    ProductServerStatus Status);

public sealed record ProductServerCommandRequest(string Command);

/// <summary>
/// Public metadata for a Service-owned backup. <see cref="BackupId"/> is an opaque identifier
/// resolved by the Service; neither local nor Web clients ever submit or receive a filesystem
/// path for backup operations.
/// </summary>
public sealed record ProductServerBackupSummary(
    string BackupId,
    string FileName,
    long ArchiveBytes,
    DateTimeOffset CreatedAtUtc);

/// <summary>Bounded page of backups belonging to exactly one registered server.</summary>
public sealed record ProductServerBackupPage(
    Guid ServerId,
    int Offset,
    int NextOffset,
    bool HasMore,
    IReadOnlyList<ProductServerBackupSummary> Backups);

public sealed record ProductServerBackupMutationResult(
    Guid ServerId,
    ProductServerBackupSummary Backup,
    DateTimeOffset CompletedAtUtc);

public sealed record ProductServerBackupRestoreResult(
    Guid ServerId,
    string BackupId,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Authentication boundary for the loopback REST adapter. The token is generated and retained by
/// the Windows Service and is never returned by any HTTP endpoint.
/// </summary>
public static class ProductLocalApiAuthentication
{
    public const string HeaderName = "X-MCSV-Service-Token";
    public const string TokenRelativePath = "secrets/service-rest-token.v1";
    public const string InstallationIdentityRelativePath = "data/installation-id.v1";
    public const int MaximumCredentialFileBytes = 128;
}

public static class ProductLocalIpcAccess
{
    public const string InstallerOperatorSidRelativePath = "data/installer-operator-sid.v1";
    public const int MaximumSidFileBytes = 192;
}

/// <summary>
/// Authenticated, exact activation boundary used by the installer and signed A/B updater. A
/// process being alive is intentionally insufficient: callers bind readiness to the immutable
/// installation identity and the exact product version they are activating.
/// </summary>
public sealed record ProductActivationReadyResponse(
    string Status,
    string Product,
    string Version,
    Guid InstallationId,
    DateTimeOffset StartedAtUtc,
    bool Ready);
