namespace MinecraftServerManager.Contracts;

public enum ProductServerModpackUpdateState
{
    Staging = 0,
    Queued,
    Verifying,
    BackingUp,
    Applying,
    AwaitingHealth,
    HealthyAwaitingStop,
    RollingBack,
    Completed,
    RolledBack,
    Cancelled,
    Failed,
}

/// <summary>
/// Target launch/provenance metadata. Paths are relative to the candidate payload; neither the
/// GUI nor Web API can make the Service open an arbitrary source directory.
/// </summary>
public sealed record ProductServerModpackUpdateDefinition
{
    public ProductServerLaunchKind LaunchKind { get; init; }

    public string ServerJarPath { get; init; } = "server.jar";

    public IReadOnlyList<string> JavaArgumentFilePaths { get; init; } = [];

    public string CoreType { get; init; } = "Unknown";

    public string? MinecraftVersion { get; init; }

    public IReadOnlyList<string> ServerArguments { get; init; } = ["nogui"];

    public string? ModpackProviderId { get; init; }

    public ProductModpackSourceKind ModpackSource { get; init; }

    public string ModpackProjectId { get; init; } = string.Empty;

    public string ModpackVersionId { get; init; } = string.Empty;

    public string ModpackVersionName { get; init; } = string.Empty;

    public bool IsInstallerArtifact { get; init; }
}

public sealed record ProductServerModpackUpdateBeginRequest(
    Guid ServerId,
    string ExpectedCurrentVersionId,
    ProductServerModpackUpdateDefinition Target);

public sealed record ProductServerModpackUpdateManifestEntry(
    string Path,
    long Length,
    string Sha256);

public sealed record ProductServerModpackUpdateManifest(
    int SchemaVersion,
    Guid UpdateId,
    IReadOnlyList<ProductServerModpackUpdateManifestEntry> Files);

public sealed record ProductServerModpackUpdateStatus(
    Guid UpdateId,
    Guid ServerId,
    ProductServerModpackUpdateState State,
    string? StagingDirectory,
    long TotalBytes,
    long CompletedBytes,
    int TotalFiles,
    int CompletedFiles,
    string? BackupArchivePath,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsTerminal => State is ProductServerModpackUpdateState.Completed
        or ProductServerModpackUpdateState.RolledBack
        or ProductServerModpackUpdateState.Cancelled
        or ProductServerModpackUpdateState.Failed;
}
