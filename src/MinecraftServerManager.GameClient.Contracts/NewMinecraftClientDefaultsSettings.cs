namespace MinecraftServerManager.GameClient.Contracts;

/// <summary>Defaults copied to client instances created after these settings are saved.</summary>
public sealed class NewMinecraftClientDefaultsSettings
{
    public MinecraftClientMemoryMode MemoryMode { get; set; } = MinecraftClientMemoryMode.Automatic;

    public int MinimumMemoryMb { get; set; } = 2048;

    public int MaximumMemoryMb { get; set; } = 4096;

    public int WindowWidth { get; set; } = 1280;

    public int WindowHeight { get; set; } = 720;

    public bool FullScreen { get; set; }

    public bool EnableQuickLaunch { get; set; }

    public bool HideLauncherAfterGameStarts { get; set; } = true;

    public bool ShowGameLog { get; set; }

    public bool EnableDedicatedGpu { get; set; } = true;

    public bool EnableDiscordPresence { get; set; }

    public NewMinecraftClientDefaultsSettings Copy() => new()
    {
        MemoryMode = MemoryMode,
        MinimumMemoryMb = MinimumMemoryMb,
        MaximumMemoryMb = MaximumMemoryMb,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        FullScreen = FullScreen,
        EnableQuickLaunch = EnableQuickLaunch,
        HideLauncherAfterGameStarts = HideLauncherAfterGameStarts,
        ShowGameLog = ShowGameLog,
        EnableDedicatedGpu = EnableDedicatedGpu,
        EnableDiscordPresence = EnableDiscordPresence,
    };
}
