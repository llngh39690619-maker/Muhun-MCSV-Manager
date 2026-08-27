using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Services;

public sealed record ThemePreset(
    string Id,
    string DisplayName,
    string Description,
    ApplicationAppearanceSettings Appearance);

/// <summary>Curated, high-contrast dark palettes used by the manager settings window.</summary>
public static class ThemePresetCatalog
{
    public const string DefaultId = "ashen-jade";

    public static IReadOnlyList<ThemePreset> All =>
    [
        Create(
            DefaultId,
            "theme.ashenJade.name",
            "theme.ashenJade.description",
            "#101318", "#171B22", "#1E242D", "#2B3340",
            "#59D98E", "#1D6C45", "#F2F5F8", "#9DA8B8"),
        Create(
            "black-gold-embers",
            "theme.blackGold.name",
            "theme.blackGold.description",
            "#090806", "#11100D", "#1A1710", "#554623",
            "#D4AF37", "#5A4412", "#F4ECD8", "#A89C82"),
        Create(
            "ashen-steel",
            "theme.ashenSteel.name",
            "theme.ashenSteel.description",
            "#0C0E11", "#15191E", "#20262D", "#39434D",
            "#A7BBC8", "#465A66", "#EDF2F5", "#97A4AE"),
        Create(
            "blood-moon",
            "theme.bloodMoon.name",
            "theme.bloodMoon.description",
            "#0D090A", "#191113", "#241719", "#553034",
            "#D76A55", "#6C271F", "#F4E8E2", "#B39B94"),
    ];

    public static ThemePreset GetOrDefault(string? id)
    {
        var themes = All;
        return themes.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? themes[0];
    }

    private static ThemePreset Create(
        string id,
        string displayNameKey,
        string descriptionKey,
        string window,
        string panel,
        string raised,
        string border,
        string accent,
        string accentDark,
        string text,
        string muted)
        => new(
            id,
            LocalizationService.Current.Get(displayNameKey),
            LocalizationService.Current.Get(descriptionKey),
            new ApplicationAppearanceSettings
            {
                WindowColor = window,
                PanelColor = panel,
                PanelRaisedColor = raised,
                BorderColor = border,
                AccentColor = accent,
                AccentDarkColor = accentDark,
                TextColor = text,
                MutedTextColor = muted,
                Pattern = AppearancePattern.None,
                PatternColor = accent,
                PatternOpacity = 0,
                BackgroundImagePath = null,
                BackgroundImageOpacity = 0,
            });
}
