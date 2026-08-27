namespace MinecraftServerManager.Core.Models;

/// <summary>Persisted desktop layout and theme selection.</summary>
public sealed class ManagerUiSettings
{
    // Persist the real normal-window size even when Windows exposes a work area smaller than the
    // design minimum (for example, a 1080p laptop at a high DPI scale). MainWindow applies the
    // monitor-specific usable minimum when it restores the value.
    public const double MinimumPersistedWindowWidth = 1;
    public const double MinimumPersistedWindowHeight = 1;
    public const double MaximumPersistedWindowWidth = 7680;
    public const double MaximumPersistedWindowHeight = 4320;
    public const double DefaultWindowWidth = 1480;
    public const double DefaultWindowHeight = 900;
    public const double DefaultFontSize = 13;

    public string ThemePresetId { get; set; } = "ashen-jade";

    public double WindowWidth { get; set; } = DefaultWindowWidth;

    public double WindowHeight { get; set; } = DefaultWindowHeight;

    public double FontSize { get; set; } = DefaultFontSize;

    public ManagerUiSettings Copy() => new()
    {
        ThemePresetId = ThemePresetId,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        FontSize = FontSize,
    };
}
