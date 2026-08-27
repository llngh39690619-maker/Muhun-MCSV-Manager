using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MinecraftServerManager.App.Tests;

public sealed class SettingsAppearanceXamlContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void MainGear_OpensGeneralSettingsAndAppearanceWorkspaceTabIsRemoved()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        var gear = Assert.Single(
            document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content") == "⚙");

        Assert.Equal("{Binding OpenSettingsCommand}", (string?)gear.Attribute("Command"));
        Assert.DoesNotContain(
            document.Descendants(Presentation + "TabItem"),
            tab => string.Equals(
                       (string?)tab.Attribute("Tag"),
                       "Appearance",
                       StringComparison.OrdinalIgnoreCase)
                   || string.Equals(
                       (string?)tab.Attribute("Header"),
                       "外觀",
                       StringComparison.Ordinal));
    }

    [Fact]
    public void ServerRowContextMenu_PassesTheRightClickedRowToAppearanceCommand()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        var item = Assert.Single(
            document.Descendants(Presentation + "MenuItem"),
            element => (string?)element.Attribute("Header")
                       == "{DynamicResource L10n.main.context.appearance}");

        Assert.Equal(
            "{Binding Tag.OpenServerAppearanceCommand}",
            (string?)item.Attribute("Command"));
        Assert.Equal("{Binding DataContext}", (string?)item.Attribute("CommandParameter"));

        var contextMenu = Assert.IsType<XElement>(item.Parent);
        Assert.Equal(Presentation + "ContextMenu", contextMenu.Name);
        Assert.Equal(
            "{Binding PlacementTarget, RelativeSource={RelativeSource Self}}",
            (string?)contextMenu.Attribute("DataContext"));
        var contextMenuProperty = Assert.IsType<XElement>(contextMenu.Parent);
        var exactRow = Assert.IsType<XElement>(contextMenuProperty.Parent);
        Assert.Equal(Presentation + "Border", exactRow.Name);
        Assert.Equal(
            "{Binding DataContext, RelativeSource={RelativeSource AncestorType={x:Type ListBox}}}",
            (string?)exactRow.Attribute("Tag"));
    }

    [Fact]
    public void BackgroundOpacityIsBoundAndIconRemainsAtOneHundredPercent()
    {
        var mainWindow = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        var selectedBackground = Assert.Single(
            mainWindow.Descendants(Presentation + "Image"),
            image => (string?)image.Attribute("Source") ==
                     "{Binding SelectedServer.BackgroundImagePath}");
        Assert.Equal(
            "{Binding SelectedServer.BackgroundImageOpacity}",
            (string?)selectedBackground.Attribute("Opacity"));
        var listIcon = Assert.Single(
            mainWindow.Descendants(Presentation + "Image"),
            image => (string?)image.Attribute("Source") == "{Binding EffectiveIconImagePath}");
        Assert.Equal("1", (string?)listIcon.Attribute("Opacity"));
        Assert.Equal(
            "{Binding EffectiveIconImagePath, Converter={StaticResource StringToVisibility}}",
            (string?)listIcon.Attribute("Visibility"));

        var coreInitial = Assert.Single(
            mainWindow.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding CoreInitial}");
        Assert.Equal(
            "{Binding EffectiveIconImagePath, Converter={StaticResource EmptyStringToVisibility}}",
            (string?)coreInitial.Attribute("Visibility"));

        var dialog = XDocument.Load(GetAppSourcePath(Path.Combine(
            "Dialogs",
            "ServerAppearanceSettingsDialog.xaml")));
        var opacitySlider = Assert.Single(
            dialog.Descendants(Presentation + "Slider"),
            slider => (string?)slider.Attribute("Value") ==
                      "{Binding SelectedServer.BackgroundImageOpacityPercent}");
        Assert.Equal("0", (string?)opacitySlider.Attribute("Minimum"));
        Assert.Equal("100", (string?)opacitySlider.Attribute("Maximum"));

        var icon = Assert.Single(
            dialog.Descendants(Presentation + "Image"),
            image => (string?)image.Attribute("Source") ==
                     "{Binding SelectedServer.IconImagePath}");
        Assert.Equal("1", (string?)icon.Attribute("Opacity"));
    }

    private static string GetAppSourcePath(
        string relativePath,
        [CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            "..",
            "src",
            "MinecraftServerManager.App",
            relativePath));
}
