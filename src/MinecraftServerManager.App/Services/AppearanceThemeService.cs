using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Validates, owns and applies application-wide theme resources. It never keeps the source image
/// open and only persists images copied below the manager's themes directory.
/// </summary>
public sealed class AppearanceThemeService
{
    public const double MaximumPatternOpacity = 0.35;
    public const double MaximumBackgroundImageOpacity = 0.40;

    private readonly string _themesRoot;
    private readonly string _backgroundRoot;

    public AppearanceThemeService(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _themesRoot = Path.GetFullPath(paths.Themes);
        _backgroundRoot = Path.GetFullPath(Path.Combine(_themesRoot, "application-backgrounds"));
    }

    public string BackgroundRoot => _backgroundRoot;

    /// <summary>Copies a validated user-selected image to a manager-owned, randomized path.</summary>
    public string ImportBackgroundImage(string sourcePath)
    {
        ThemeImageAssetValidator.ValidateBackground(sourcePath);
        Directory.CreateDirectory(_themesRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(_themesRoot, _themesRoot);
        Directory.CreateDirectory(_backgroundRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(_themesRoot, _backgroundRoot);

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var destination = SafePath.CombineUnderRoot(
            _backgroundRoot,
            $"window-{Guid.NewGuid():N}{extension}");

        try
        {
            File.Copy(Path.GetFullPath(sourcePath), destination, overwrite: false);
            SafePath.EnsureNoReparsePointsUnderRoot(_themesRoot, destination);
            ThemeImageAssetValidator.ValidateBackground(destination);
            return destination;
        }
        catch
        {
            TryDeleteManagedBackground(destination);
            throw;
        }
    }

    /// <summary>Deletes only a regular file owned by this service; external paths are ignored.</summary>
    public bool TryDeleteManagedBackground(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var candidate = Path.GetFullPath(path);
            if (!SafePath.IsWithinRoot(_backgroundRoot, candidate)
                || PathsEqual(_backgroundRoot, candidate)
                || !File.Exists(candidate))
            {
                return false;
            }

            SafePath.EnsureNoReparsePointsUnderRoot(_themesRoot, candidate);
            File.Delete(candidate);
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Returns a fully valid copy or throws without changing application resources.</summary>
    public ApplicationAppearanceSettings ValidateAndNormalize(ApplicationAppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Copy();
        var errors = new List<string>();

        normalized.WindowColor = NormalizeColor(settings.WindowColor, "視窗底色", errors);
        normalized.PanelColor = NormalizeColor(settings.PanelColor, "面板底色", errors);
        normalized.PanelRaisedColor = NormalizeColor(settings.PanelRaisedColor, "浮動面板底色", errors);
        normalized.BorderColor = NormalizeColor(settings.BorderColor, "邊框色", errors);
        normalized.AccentColor = NormalizeColor(settings.AccentColor, "強調色", errors);
        normalized.AccentDarkColor = NormalizeColor(settings.AccentDarkColor, "強調底色", errors);
        normalized.TextColor = NormalizeColor(settings.TextColor, "文字色", errors);
        normalized.MutedTextColor = NormalizeColor(settings.MutedTextColor, "次要文字色", errors);
        normalized.PatternColor = NormalizeColor(settings.PatternColor, "圖案色", errors);

        if (!Enum.IsDefined(settings.Pattern))
        {
            errors.Add("背景圖案不是支援的選項。");
        }

        if (!IsOpacityValid(settings.PatternOpacity, MaximumPatternOpacity))
        {
            errors.Add($"圖案透明度必須介於 0 與 {MaximumPatternOpacity:0.##}。");
        }

        if (!IsOpacityValid(settings.BackgroundImageOpacity, MaximumBackgroundImageOpacity))
        {
            errors.Add($"背景圖片透明度必須介於 0 與 {MaximumBackgroundImageOpacity:0.##}。");
        }

        normalized.BackgroundImagePath = NormalizeAndValidateManagedBackground(
            settings.BackgroundImagePath,
            errors);

        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        return normalized;
    }

    /// <summary>
    /// Repairs untrusted/corrupt persisted values field-by-field. Invalid colors return to the
    /// built-in dark palette and an unavailable or external image is removed from the result.
    /// </summary>
    public ApplicationAppearanceSettings Repair(ApplicationAppearanceSettings? settings)
    {
        settings ??= new ApplicationAppearanceSettings();
        var repaired = settings.Copy();

        repaired.WindowColor = NormalizeColorOrDefault(settings.WindowColor, ApplicationAppearanceSettings.DefaultWindowColor);
        repaired.PanelColor = NormalizeColorOrDefault(settings.PanelColor, ApplicationAppearanceSettings.DefaultPanelColor);
        repaired.PanelRaisedColor = NormalizeColorOrDefault(settings.PanelRaisedColor, ApplicationAppearanceSettings.DefaultPanelRaisedColor);
        repaired.BorderColor = NormalizeColorOrDefault(settings.BorderColor, ApplicationAppearanceSettings.DefaultBorderColor);
        repaired.AccentColor = NormalizeColorOrDefault(settings.AccentColor, ApplicationAppearanceSettings.DefaultAccentColor);
        repaired.AccentDarkColor = NormalizeColorOrDefault(settings.AccentDarkColor, ApplicationAppearanceSettings.DefaultAccentDarkColor);
        repaired.TextColor = NormalizeColorOrDefault(settings.TextColor, ApplicationAppearanceSettings.DefaultTextColor);
        repaired.MutedTextColor = NormalizeColorOrDefault(settings.MutedTextColor, ApplicationAppearanceSettings.DefaultMutedTextColor);
        repaired.PatternColor = NormalizeColorOrDefault(settings.PatternColor, ApplicationAppearanceSettings.DefaultPatternColor);
        repaired.Pattern = Enum.IsDefined(settings.Pattern) ? settings.Pattern : AppearancePattern.None;
        repaired.PatternOpacity = IsOpacityValid(settings.PatternOpacity, MaximumPatternOpacity)
            ? settings.PatternOpacity
            : ApplicationAppearanceSettings.DefaultPatternOpacity;
        repaired.BackgroundImageOpacity = IsOpacityValid(settings.BackgroundImageOpacity, MaximumBackgroundImageOpacity)
            ? settings.BackgroundImageOpacity
            : ApplicationAppearanceSettings.DefaultBackgroundImageOpacity;

        var backgroundErrors = new List<string>();
        repaired.BackgroundImagePath = NormalizeAndValidateManagedBackground(
            settings.BackgroundImagePath,
            backgroundErrors);
        return repaired;
    }

    /// <summary>
    /// Replaces theme resources atomically after validation. XAML should consume these keys using
    /// DynamicResource so already-open windows participate in live preview.
    /// </summary>
    public ApplicationAppearanceSettings Apply(
        ResourceDictionary resources,
        ApplicationAppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var normalized = ValidateAndNormalize(settings);

        var window = ParseColor(normalized.WindowColor);
        var panel = ParseColor(normalized.PanelColor);
        var panelRaised = ParseColor(normalized.PanelRaisedColor);
        var border = ParseColor(normalized.BorderColor);
        var accent = ParseColor(normalized.AccentColor);
        var accentDark = ParseColor(normalized.AccentDarkColor);
        var text = ParseColor(normalized.TextColor);
        var mutedText = ParseColor(normalized.MutedTextColor);
        var pattern = ParseColor(normalized.PatternColor);
        var patternBrush = CreatePatternBrush(
            normalized.Pattern,
            pattern,
            normalized.PatternOpacity);
        var backgroundImageBrush = CreateBackgroundImageBrush(
            normalized.BackgroundImagePath,
            normalized.BackgroundImageOpacity);

        SetColorAndBrush(resources, ThemeResourceKeys.WindowColor, ThemeResourceKeys.WindowBrush, window);
        SetColorAndBrush(resources, ThemeResourceKeys.PanelColor, ThemeResourceKeys.PanelBrush, panel);
        SetColorAndBrush(resources, ThemeResourceKeys.PanelRaisedColor, ThemeResourceKeys.PanelRaisedBrush, panelRaised);
        SetColorAndBrush(resources, ThemeResourceKeys.BorderColor, ThemeResourceKeys.BorderBrush, border);
        SetColorAndBrush(resources, ThemeResourceKeys.AccentColor, ThemeResourceKeys.AccentBrush, accent);
        SetColorAndBrush(resources, ThemeResourceKeys.AccentDarkColor, ThemeResourceKeys.AccentDarkBrush, accentDark);
        SetColorAndBrush(resources, ThemeResourceKeys.TextColor, ThemeResourceKeys.TextBrush, text);
        SetColorAndBrush(resources, ThemeResourceKeys.MutedTextColor, ThemeResourceKeys.MutedTextBrush, mutedText);

        resources[ThemeResourceKeys.WindowPatternBrush] = patternBrush;
        resources[ThemeResourceKeys.WindowBackgroundImageBrush] = backgroundImageBrush;
        return normalized;
    }

    private string? NormalizeAndValidateManagedBackground(string? path, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                errors.Add("背景圖片必須是管理器 themes 內的完整路徑。");
                return null;
            }

            var candidate = Path.GetFullPath(path);
            if (!SafePath.IsWithinRoot(_backgroundRoot, candidate) || PathsEqual(_backgroundRoot, candidate))
            {
                errors.Add("背景圖片不在管理器允許的 themes 資料夾內。");
                return null;
            }

            SafePath.EnsureNoReparsePointsUnderRoot(_themesRoot, candidate);
            ThemeImageAssetValidator.ValidateBackground(candidate);
            return candidate;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            errors.Add($"背景圖片無法使用：{exception.Message}");
            return null;
        }
    }

    private static Brush CreatePatternBrush(AppearancePattern pattern, Color color, double opacity)
    {
        if (pattern == AppearancePattern.None || opacity <= 0)
        {
            return Brushes.Transparent;
        }

        const double tileSize = 18;
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(
            Brushes.Transparent,
            null,
            new RectangleGeometry(new Rect(0, 0, tileSize, tileSize))));

        var brush = Freeze(new SolidColorBrush(color));
        var pen = Freeze(new Pen(brush, 1));
        switch (pattern)
        {
            case AppearancePattern.Dots:
                drawing.Children.Add(new GeometryDrawing(
                    brush,
                    null,
                    new EllipseGeometry(new Point(tileSize / 2, tileSize / 2), 1.15, 1.15)));
                break;
            case AppearancePattern.Grid:
                drawing.Children.Add(new GeometryDrawing(
                    null,
                    pen,
                    new GeometryGroup
                    {
                        Children =
                        {
                            new LineGeometry(new Point(0.5, 0), new Point(0.5, tileSize)),
                            new LineGeometry(new Point(0, 0.5), new Point(tileSize, 0.5))
                        }
                    }));
                break;
            case AppearancePattern.Diagonal:
                drawing.Children.Add(new GeometryDrawing(
                    null,
                    pen,
                    new LineGeometry(new Point(-0.5, tileSize + 0.5), new Point(tileSize + 0.5, -0.5))));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pattern));
        }

        drawing.Freeze();
        return Freeze(new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, tileSize, tileSize),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, tileSize, tileSize),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            Opacity = opacity
        });
    }

    private static Brush CreateBackgroundImageBrush(string? path, double opacity)
    {
        if (string.IsNullOrWhiteSpace(path) || opacity <= 0)
        {
            return Brushes.Transparent;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return Freeze(new ImageBrush(frame)
        {
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
            Opacity = opacity
        });
    }

    private static string NormalizeColor(string? value, string label, ICollection<string> errors)
    {
        if (TryNormalizeColor(value, out var normalized)) return normalized;
        errors.Add($"{label}必須使用 #RRGGBB 格式。");
        return value?.Trim() ?? string.Empty;
    }

    private static string NormalizeColorOrDefault(string? value, string defaultValue)
        => TryNormalizeColor(value, out var normalized) ? normalized : defaultValue;

    private static bool TryNormalizeColor(string? value, out string normalized)
    {
        var candidate = value?.Trim();
        if (candidate is null || candidate.Length != 7 || candidate[0] != '#')
        {
            normalized = string.Empty;
            return false;
        }

        for (var index = 1; index < candidate.Length; index++)
        {
            if (!Uri.IsHexDigit(candidate[index]))
            {
                normalized = string.Empty;
                return false;
            }
        }

        normalized = candidate.ToUpperInvariant();
        return true;
    }

    private static Color ParseColor(string value) => Color.FromRgb(
        byte.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static bool IsOpacityValid(double value, double maximum)
        => double.IsFinite(value) && value >= 0 && value <= maximum;

    private static void SetColorAndBrush(
        ResourceDictionary resources,
        string colorKey,
        string brushKey,
        Color color)
    {
        resources[colorKey] = color;
        resources[brushKey] = Freeze(new SolidColorBrush(color));
    }

    private static T Freeze<T>(T value) where T : Freezable
    {
        if (value.CanFreeze) value.Freeze();
        return value;
    }

    private static bool PathsEqual(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            comparison);
    }
}
