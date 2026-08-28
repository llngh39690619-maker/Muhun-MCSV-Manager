using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MinecraftServerManager.App.Tests;

public sealed class DarkWindowSurfaceContractTests
{
    [Fact]
    public void GlobalWindowStyle_EnablesNativeDarkSurfaceBeforeFirstFrame()
    {
        var document = XDocument.Load(GetAppSourcePath("App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = document.Descendants(presentation + "Style")
            .Single(element => (string?)element.Attribute(x + "Key") == "AppWindowStyle");
        var setters = style.Elements(presentation + "Setter")
            .ToDictionary(
                element => (string?)element.Attribute("Property") ?? string.Empty,
                element => (string?)element.Attribute("Value") ?? string.Empty,
                StringComparer.Ordinal);

        Assert.Equal("True", setters["infra:DarkWindowSurface.IsEnabled"]);
        Assert.Equal("True", setters["UseLayoutRounding"]);
        Assert.Equal("True", setters["SnapsToDevicePixels"]);
    }

    [Fact]
    public void NativeDarkSurface_CoversCompositorNonClientAndEraseBackgroundPaths()
    {
        var source = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "Infrastructure",
            "DarkWindowSurface.cs")));

        Assert.Contains("CompositionTarget.BackgroundColor", source, StringComparison.Ordinal);
        Assert.Contains("DwmUseImmersiveDarkMode", source, StringComparison.Ordinal);
        Assert.Contains("DwmCaptionColor", source, StringComparison.Ordinal);
        Assert.Contains("WmEraseBackground", source, StringComparison.Ordinal);
        Assert.Contains("FillRect", source, StringComparison.Ordinal);
    }

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);
}
