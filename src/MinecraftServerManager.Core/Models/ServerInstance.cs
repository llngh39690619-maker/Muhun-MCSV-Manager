namespace MinecraftServerManager.Core.Models;

/// <summary>Serializable settings for one isolated Minecraft server instance.</summary>
public sealed class ServerInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Minecraft Server";

    public string DirectoryPath { get; set; } = string.Empty;

    public string ServerJarPath { get; set; } = "server.jar";

    /// <summary>
    /// Defaults to the original JAR launch mode so existing manager.json files remain compatible.
    /// </summary>
    public ServerLaunchKind LaunchKind { get; set; } = ServerLaunchKind.ExecutableJar;

    /// <summary>
    /// Ordered Java argument-file paths, relative to <see cref="DirectoryPath"/> and without the
    /// leading <c>@</c>. Used only when <see cref="LaunchKind"/> is JavaArgumentFiles.
    /// </summary>
    public List<string> JavaArgumentFilePaths { get; set; } = [];

    /// <summary>The launch script inspected during import. The manager never executes it.</summary>
    public string? SourceLaunchScriptPath { get; set; }

    public CoreType CoreType { get; set; } = CoreType.Unknown;

    public string? MinecraftVersion { get; set; }

    public int? JavaMajorVersion { get; set; }

    public string? JavaExecutablePath { get; set; }

    public int MinimumMemoryMb { get; set; } = 1024;

    public int MaximumMemoryMb { get; set; } = 4096;

    /// <summary>
    /// Missing values from older manager.json files deserialize as Legacy, preserving their exact
    /// launch behavior. Newly registered servers explicitly receive the manager default mode.
    /// </summary>
    public MemoryAllocationMode MemoryAllocationMode { get; set; } = MemoryAllocationMode.Legacy;

    public List<string> JvmArguments { get; set; } = [];

    public List<string> ServerArguments { get; set; } = ["nogui"];

    /// <summary>
    /// Optional command written to standard input for a graceful shutdown. A missing value keeps
    /// backward compatibility by using <c>ServerProcessManagerOptions.StopCommand</c> (normally
    /// <c>stop</c>). Proxies such as Velocity can override this with <c>shutdown</c>.
    /// </summary>
    public string? StopCommand { get; set; }

    public int Port { get; set; } = 25565;

    public bool AutoRestart { get; set; }

    /// <summary>
    /// Whether clients should remove classified warning/error/fatal lines from the ordinary
    /// console and present them in a diagnostic view. Missing and explicit null values are treated
    /// as disabled by consumers so older manager.json files keep their original behavior.
    /// </summary>
    public bool? SeparateDiagnosticOutput { get; set; }

    /// <summary>
    /// Enables passive status-protocol health checks for a process that is still running but no
    /// longer answering Minecraft server-list pings. Disabled by default for existing instances.
    /// </summary>
    public bool EnableHangWatchdog { get; set; }

    public int WatchdogCheckIntervalSeconds { get; set; } = 30;

    public int WatchdogProbeTimeoutSeconds { get; set; } = 8;

    public int WatchdogFailureThreshold { get; set; } = 3;

    public int WatchdogStartupGraceSeconds { get; set; } = 180;

    /// <summary>
    /// Enables periodic, bounded ZIP recovery points while the server is healthy. These are not
    /// described as exact pre-crash snapshots because a future crash cannot be predicted.
    /// </summary>
    public bool EnableAutomaticRecoveryPoints { get; set; }

    public int RecoveryPointIntervalMinutes { get; set; } = 30;

    public int RecoveryPointRetentionCount { get; set; } = 3;

    /// <summary>Optional per-instance background image used by the desktop manager.</summary>
    public string? BackgroundImagePath { get; set; }

    /// <summary>Per-instance background opacity from 0 through 1. Icons always render at 100%.</summary>
    public double BackgroundImageOpacity { get; set; } = 0.25;

    /// <summary>
    /// Optional user-selected image shown on the instance card. This is an explicit override and
    /// must always take precedence over catalog artwork.
    /// </summary>
    public string? IconImagePath { get; set; }

    /// <summary>
    /// Manager-owned small artwork obtained from a verified modpack catalog. It is kept separate
    /// from <see cref="IconImagePath"/> so refreshing a catalog can never overwrite a user's icon.
    /// </summary>
    public string? CatalogIconImagePath { get; set; }

    /// <summary>
    /// Manager-owned wide preview artwork obtained from a verified modpack catalog.
    /// </summary>
    public string? CatalogPreviewImagePath { get; set; }

    /// <summary>
    /// Stable, provider-neutral catalog identifier such as <c>modrinth</c>, <c>curseforge</c>, or
    /// <c>ftb</c>. The string form permits future providers without changing this persisted model.
    /// </summary>
    public string? ModpackProviderId { get; set; }

    /// <summary>Catalog provenance is present only for packs installed from a verified source.</summary>
    public ModpackSourceKind ModpackSource { get; set; }

    public string? ModpackProjectId { get; set; }

    public string? ModpackVersionId { get; set; }

    public string? ModpackVersionName { get; set; }

    /// <summary>
    /// True when the imported artifact is an installer that must not be launched as a server JAR.
    /// </summary>
    public bool IsInstallerArtifact { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
