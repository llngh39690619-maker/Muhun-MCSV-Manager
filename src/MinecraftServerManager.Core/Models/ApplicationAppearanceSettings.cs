namespace MinecraftServerManager.Core.Models;

/// <summary>
/// Persisted application-wide appearance. Values are strings so the settings file stays portable;
/// the WPF layer validates and converts them before applying resources.
/// </summary>
public sealed class ApplicationAppearanceSettings
{
    public const string DefaultWindowColor = "#101318";
    public const string DefaultPanelColor = "#171B22";
    public const string DefaultPanelRaisedColor = "#1E242D";
    public const string DefaultBorderColor = "#2B3340";
    public const string DefaultAccentColor = "#59D98E";
    public const string DefaultAccentDarkColor = "#1D6C45";
    public const string DefaultTextColor = "#F2F5F8";
    public const string DefaultMutedTextColor = "#9DA8B8";
    public const string DefaultPatternColor = "#59D98E";
    public const double DefaultPatternOpacity = 0.08;
    public const double DefaultBackgroundImageOpacity = 0.16;

    public string WindowColor { get; set; } = DefaultWindowColor;
    public string PanelColor { get; set; } = DefaultPanelColor;
    public string PanelRaisedColor { get; set; } = DefaultPanelRaisedColor;
    public string BorderColor { get; set; } = DefaultBorderColor;
    public string AccentColor { get; set; } = DefaultAccentColor;
    public string AccentDarkColor { get; set; } = DefaultAccentDarkColor;
    public string TextColor { get; set; } = DefaultTextColor;
    public string MutedTextColor { get; set; } = DefaultMutedTextColor;
    public AppearancePattern Pattern { get; set; }
    public string PatternColor { get; set; } = DefaultPatternColor;
    public double PatternOpacity { get; set; } = DefaultPatternOpacity;
    public string? BackgroundImagePath { get; set; }
    public double BackgroundImageOpacity { get; set; } = DefaultBackgroundImageOpacity;

    public ApplicationAppearanceSettings Copy() => new()
    {
        WindowColor = WindowColor,
        PanelColor = PanelColor,
        PanelRaisedColor = PanelRaisedColor,
        BorderColor = BorderColor,
        AccentColor = AccentColor,
        AccentDarkColor = AccentDarkColor,
        TextColor = TextColor,
        MutedTextColor = MutedTextColor,
        Pattern = Pattern,
        PatternColor = PatternColor,
        PatternOpacity = PatternOpacity,
        BackgroundImagePath = BackgroundImagePath,
        BackgroundImageOpacity = BackgroundImageOpacity
    };
}
