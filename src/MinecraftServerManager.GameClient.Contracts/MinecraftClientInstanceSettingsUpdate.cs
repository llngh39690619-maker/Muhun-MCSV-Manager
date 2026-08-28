namespace MinecraftServerManager.GameClient.Contracts;

/// <summary>
/// User-editable settings for an installed client instance. Installation identity, catalog
/// metadata, ownership, play history and the instance directory are deliberately absent.
/// </summary>
public sealed record MinecraftClientInstanceSettingsUpdate
{
    public string Name { get; init; } = "Minecraft";

    public string? IconImagePath { get; init; }

    public int WindowWidth { get; init; } = 1280;

    public int WindowHeight { get; init; } = 720;

    public bool FullScreen { get; init; }

    public bool EnableQuickLaunch { get; init; }

    public bool HideLauncherAfterGameStarts { get; init; } = true;

    public bool ShowGameLog { get; init; }

    public bool EnableDedicatedGpu { get; init; } = true;

    // Reserved for forward-compatible persistence. No control is exposed until an actual,
    // privacy-reviewed Discord lifecycle exists.
    public bool EnableDiscordPresence { get; init; }

    public MinecraftClientMemoryMode MemoryMode { get; init; } =
        MinecraftClientMemoryMode.UseGlobalDefault;

    public int MinimumMemoryMb { get; init; } = 1024;

    public int MaximumMemoryMb { get; init; } = 4096;

    /// <summary>Absolute path to java.exe/javaw.exe, or null to use automatic runtime selection.</summary>
    public string? JavaExecutablePath { get; init; }

    public IReadOnlyList<string> JvmArguments { get; init; } = [];
}
