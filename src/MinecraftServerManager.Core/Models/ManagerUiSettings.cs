namespace MinecraftServerManager.Core.Models;

/// <summary>Persisted desktop layout and theme selection.</summary>
public sealed class ManagerUiSettings
{
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
