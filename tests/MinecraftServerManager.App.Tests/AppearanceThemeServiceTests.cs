using System.IO;
using System.Windows;
using System.Windows.Media;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class AppearanceThemeServiceTests
{
    [Fact]
    public void Apply_ValidGridTheme_ReplacesDynamicResources()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);
        var resources = new ResourceDictionary();
        var settings = new ApplicationAppearanceSettings
        {
            WindowColor = "#010203",
            AccentColor = "#abcdef",
            Pattern = AppearancePattern.Grid,
            PatternOpacity = 0.2
        };

        var applied = service.Apply(resources, settings);

        Assert.Equal("#ABCDEF", applied.AccentColor);
        Assert.Equal(Color.FromRgb(1, 2, 3), Assert.IsType<Color>(resources[ThemeResourceKeys.WindowColor]));
        Assert.Equal(
            Color.FromRgb(171, 205, 239),
            Assert.IsType<SolidColorBrush>(resources[ThemeResourceKeys.AccentBrush]).Color);
        var pattern = Assert.IsType<DrawingBrush>(resources[ThemeResourceKeys.WindowPatternBrush]);
        Assert.Equal(0.2, pattern.Opacity, precision: 3);
        Assert.True(pattern.IsFrozen);
    }

    [Fact]
    public void Apply_InvalidColor_DoesNotPartiallyModifyResources()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);
        var resources = new ResourceDictionary
        {
            [ThemeResourceKeys.WindowColor] = Color.FromRgb(9, 9, 9)
        };
        var invalid = new ApplicationAppearanceSettings { AccentColor = "white" };

        var exception = Assert.Throws<InvalidDataException>(() => service.Apply(resources, invalid));

        Assert.Contains("#RRGGBB", exception.Message, StringComparison.Ordinal);
        Assert.Equal(Color.FromRgb(9, 9, 9), Assert.IsType<Color>(resources[ThemeResourceKeys.WindowColor]));
        Assert.False(resources.Contains(ThemeResourceKeys.AccentBrush));
    }

    [Fact]
    public void ValidateAndNormalize_RejectsNonFiniteAndExcessiveOpacity()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);

        var invalidPattern = new ApplicationAppearanceSettings { PatternOpacity = double.NaN };
        var invalidImage = new ApplicationAppearanceSettings { BackgroundImageOpacity = 0.41 };

        Assert.Throws<InvalidDataException>(() => service.ValidateAndNormalize(invalidPattern));
        Assert.Throws<InvalidDataException>(() => service.ValidateAndNormalize(invalidImage));
    }

    [Fact]
    public void Repair_CorruptPersistedValues_ReturnsSafeDarkDefaults()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);
        var externalImage = Path.Combine(directory.Path, "outside.png");
        WriteTinyPng(externalImage);
        var corrupt = new ApplicationAppearanceSettings
        {
            WindowColor = "transparent",
            Pattern = (AppearancePattern)999,
            PatternOpacity = 100,
            BackgroundImagePath = externalImage,
            BackgroundImageOpacity = double.PositiveInfinity
        };

        var repaired = service.Repair(corrupt);

        Assert.Equal(ApplicationAppearanceSettings.DefaultWindowColor, repaired.WindowColor);
        Assert.Equal(AppearancePattern.None, repaired.Pattern);
        Assert.Equal(ApplicationAppearanceSettings.DefaultPatternOpacity, repaired.PatternOpacity);
        Assert.Null(repaired.BackgroundImagePath);
        Assert.Equal(ApplicationAppearanceSettings.DefaultBackgroundImageOpacity, repaired.BackgroundImageOpacity);
        service.ValidateAndNormalize(repaired);
    }

    [Fact]
    public void ImportBackgroundImage_ValidPng_CopiesToOwnedPathAndLoadsWithoutFileLock()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);
        var source = Path.Combine(directory.Path, "使用者圖片.png");
        WriteTinyPng(source);

        var managed = service.ImportBackgroundImage(source);
        var settings = new ApplicationAppearanceSettings
        {
            BackgroundImagePath = managed,
            BackgroundImageOpacity = 0.2
        };
        var resources = new ResourceDictionary();

        service.Apply(resources, settings);

        Assert.StartsWith(service.BackgroundRoot, managed, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<ImageBrush>(resources[ThemeResourceKeys.WindowBackgroundImageBrush]);
        Assert.True(service.TryDeleteManagedBackground(managed));
        Assert.False(File.Exists(managed));
    }

    [Fact]
    public void ImportBackgroundImage_FakePng_IsRejectedWithoutManagedCopy()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);
        var source = Path.Combine(directory.Path, "fake.png");
        File.WriteAllText(source, "not an image");

        Assert.Throws<InvalidDataException>(() => service.ImportBackgroundImage(source));
        Assert.False(Directory.Exists(service.BackgroundRoot));
    }

    [Fact]
    public void ValidateAndNormalize_ExternalImagePath_IsRejected()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);
        var externalImage = Path.Combine(directory.Path, "external.png");
        WriteTinyPng(externalImage);

        var settings = new ApplicationAppearanceSettings { BackgroundImagePath = externalImage };

        var exception = Assert.Throws<InvalidDataException>(() => service.ValidateAndNormalize(settings));
        Assert.Contains("themes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    internal static AppearanceThemeService CreateService(string root)
        => new(new ApplicationPaths(Path.Combine(root, "app")));

    internal static void WriteTinyPng(string path)
    {
        const string tinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        File.WriteAllBytes(path, Convert.FromBase64String(tinyPng));
    }

    internal sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"msm-appearance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A failed test may intentionally keep a decoder/file open. The randomized path
                // is isolated in the system temporary directory and cannot affect another test.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the original assertion failure.
            }
        }
    }
}
