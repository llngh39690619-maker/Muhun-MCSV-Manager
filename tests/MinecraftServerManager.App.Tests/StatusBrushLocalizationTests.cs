using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class StatusBrushLocalizationTests
{
    [Fact]
    public void Converter_UsesStableServerStateInsteadOfLocalizedText()
    {
        var converter = new StatusTextToBrushConverter();
        var culture = CultureInfo.GetCultureInfo("en-US");

        var running = Assert.IsType<SolidColorBrush>(converter.Convert(
            ServerState.Running,
            typeof(Brush),
            null!,
            culture));
        Assert.True(running.IsFrozen);
        Assert.Equal(
            Color.FromRgb(84, 226, 140),
            running.Color);
        Assert.Equal(
            Color.FromRgb(255, 190, 92),
            Assert.IsType<SolidColorBrush>(converter.Convert(
                ServerState.Stopping,
                typeof(Brush),
                null!,
                culture)).Color);
        Assert.Equal(
            Color.FromRgb(255, 104, 104),
            Assert.IsType<SolidColorBrush>(converter.Convert(
                ServerState.Faulted,
                typeof(Brush),
                null!,
                culture)).Color);
    }

    [Fact]
    public void MainWindow_StatusColorsBindStableStateWhileLabelsBindStateText()
    {
        var source = File.ReadAllText(GetSourcePath("MainWindow.xaml"));

        Assert.DoesNotContain(
            "StateText, Converter={StaticResource StatusBrush}",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "State, Converter={StaticResource StatusBrush}",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding StateText}\"", source, StringComparison.Ordinal);
    }

    private static string GetSourcePath(string fileName)
        => TestRepositoryPaths.AppSource(fileName);
}
