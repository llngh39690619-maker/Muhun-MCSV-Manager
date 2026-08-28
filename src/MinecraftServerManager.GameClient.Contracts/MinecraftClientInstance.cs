namespace MinecraftServerManager.GameClient.Contracts;

/// <summary>Serializable settings for one isolated Minecraft client instance.</summary>
public sealed class MinecraftClientInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Minecraft";

    public MinecraftClientEdition Edition { get; set; } = MinecraftClientEdition.Java;

    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>Mojang release id. Snapshots, experimental versions and old alpha/beta are rejected.</summary>
    public string GameVersion { get; set; } = string.Empty;

    /// <summary>
    /// The concrete launch profile written by the managed installer. For Vanilla this is the
    /// Mojang release id; loader-backed instances normally use a loader-specific profile id.
    /// </summary>
    public string InstalledVersionId { get; set; } = string.Empty;

    public MinecraftClientLoader Loader { get; set; } = MinecraftClientLoader.Vanilla;

    public string? LoaderVersion { get; set; }

    public MinecraftClientLoaderInstallKind LoaderInstallKind { get; set; } =
        MinecraftClientLoaderInstallKind.Managed;

    public int? JavaMajorVersion { get; set; }

    public string? JavaExecutablePath { get; set; }

    public MinecraftClientMemoryMode MemoryMode { get; set; } =
        MinecraftClientMemoryMode.UseGlobalDefault;

    public int MinimumMemoryMb { get; set; } = 1024;

    public int MaximumMemoryMb { get; set; } = 4096;

    public int WindowWidth { get; set; } = 1280;

    public int WindowHeight { get; set; } = 720;

    public bool FullScreen { get; set; }

    /// <summary>Skips non-essential launcher UI when the user explicitly requests a fast launch.</summary>
    public bool EnableQuickLaunch { get; set; }

    public bool HideLauncherAfterGameStarts { get; set; } = true;

    public bool ShowGameLog { get; set; }

    public bool EnableDedicatedGpu { get; set; } = true;

    // Retained in the schema for forward compatibility, but defaults off and is intentionally
    // hidden until Discord Presence has a complete opt-in lifecycle and privacy contract.
    public bool EnableDiscordPresence { get; set; }

    public List<string> JvmArguments { get; set; } = [];

    public Dictionary<string, string> EnvironmentVariables { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Non-secret local account identifier. Tokens are stored in the protected vault.</summary>
    public string? AccountId { get; set; }

    public string? BackgroundImagePath { get; set; }

    public double BackgroundImageOpacity { get; set; } = 0.2;

    public string? IconImagePath { get; set; }

    public string? CatalogIconImagePath { get; set; }

    public string? CatalogPreviewImagePath { get; set; }

    /// <summary>Optional public catalog provenance. It never contains an API credential.</summary>
    public string? CatalogProvider { get; set; }

    public string? CatalogProjectId { get; set; }

    public string? CatalogVersionId { get; set; }

    public Uri? CatalogIconUri { get; set; }

    public Uri? CatalogPreviewUri { get; set; }

    public DateTimeOffset? LastPlayedAtUtc { get; set; }

    public long TotalPlayTimeSeconds { get; set; }

    /// <summary>
    /// Operating-system process id recorded only while this instance is running. The id is never
    /// trusted by itself: recovery also requires the exact process start time and Java executable
    /// path below to match the live process.
    /// </summary>
    public int? ActiveProcessId { get; set; }

    public DateTimeOffset? ActiveProcessStartedAtUtc { get; set; }

    public string? ActiveProcessExecutablePath { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MinecraftClientRegistryDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<MinecraftClientInstance> Instances { get; set; } = [];
}
