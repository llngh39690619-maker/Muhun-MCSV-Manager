using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MinecraftServerManager.App.Tests;

public sealed class ThemeXamlContractTests
{
    [Fact]
    public void InteractiveTemplateBackgrounds_UseDynamicThemeResources()
    {
        var document = XDocument.Load(GetAppXamlPath());
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var setters = document
            .Descendants(presentation + "Setter")
            .Select(element => new
            {
                TargetName = (string?)element.Attribute("TargetName"),
                Property = (string?)element.Attribute("Property"),
                Value = (string?)element.Attribute("Value")
            })
            .ToArray();

        Assert.Contains(setters, setter =>
            setter.TargetName == "ButtonBorder"
            && setter.Property == "Background"
            && setter.Value == "{DynamicResource PanelRaisedBrush}");
        Assert.Contains(setters, setter =>
            setter.TargetName == "TabBorder"
            && setter.Property == "Background"
            && setter.Value == "{DynamicResource PanelRaisedBrush}");
        Assert.Contains(setters, setter =>
            setter.TargetName == "TabBorder"
            && setter.Property == "Background"
            && setter.Value == "{DynamicResource AccentDarkBrush}");
        Assert.Contains(setters, setter =>
            setter.TargetName == "ItemBorder"
            && setter.Property == "Background"
            && setter.Value == "{DynamicResource PanelRaisedBrush}");
        Assert.Contains(setters, setter =>
            setter.TargetName == "ItemBorder"
            && setter.Property == "Background"
            && setter.Value == "{DynamicResource AccentDarkBrush}");

        Assert.DoesNotContain(setters, setter =>
            setter.TargetName is "ButtonBorder" or "TabBorder" or "ItemBorder"
            && setter.Property == "Background"
            && setter.Value?.StartsWith('#') == true);
    }

    private static string GetAppXamlPath([CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            "..",
            "src",
            "MinecraftServerManager.App",
            "App.xaml"));
}
