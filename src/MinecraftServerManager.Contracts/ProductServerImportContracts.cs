namespace MinecraftServerManager.Contracts;

/// <summary>
/// Service-owned import states. A client may write only while Staging; every later transition is
/// performed by the Windows Service from its private journal.
/// </summary>
public enum ProductServerImportState
{
    Staging = 0,
    Queued,
    Verifying,
    Copying,
    Promoting,
    Registering,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>
/// Launch metadata for content placed in an import staging payload. All paths are relative to the
/// server or runtime payload; this contract intentionally cannot name a source or final path.
/// </summary>
public sealed record ProductServerImportDefinition
{
    public Guid ServerId { get; init; }

    public string Name { get; init; } = string.Empty;

    public ProductServerLaunchKind LaunchKind { get; init; }

    public string ServerJarPath { get; init; } = "server.jar";

    public IReadOnlyList<string> JavaArgumentFilePaths { get; init; } = [];

    public string JavaExecutablePath { get; init; } = string.Empty;

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

/// <summary>
/// A stable GUI-generated key makes legacy migration idempotent across application restarts.
/// It contains no filesystem path and is interpreted only as an opaque receipt key.
/// </summary>
public sealed record ProductServerImportBeginRequest(
    ProductServerImportDefinition Server,
    string? MigrationKey = null);

public sealed record ProductServerImportManifestEntry(
    string Path,
    long Length,
    string Sha256);

/// <summary>
/// Written atomically by the GUI into the exact staging directory returned by Begin. Paths start
/// with server/ or runtime/ and are checked against the actual no-follow tree by the Service.
/// </summary>
public sealed record ProductServerImportManifest(
    int SchemaVersion,
    Guid ImportId,
    IReadOnlyList<ProductServerImportManifestEntry> Files);

public sealed record ProductServerImportStatus(
    Guid ImportId,
    Guid ServerId,
    ProductServerImportState State,
    string? StagingDirectory,
    long TotalBytes,
    long CompletedBytes,
    int TotalFiles,
    int CompletedFiles,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsTerminal => State is ProductServerImportState.Completed
        or ProductServerImportState.Cancelled
        or ProductServerImportState.Failed;
}
