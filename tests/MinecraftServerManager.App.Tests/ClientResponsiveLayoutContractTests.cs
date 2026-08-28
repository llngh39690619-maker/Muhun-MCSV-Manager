using System.IO;
using System.Xml.Linq;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientResponsiveLayoutContractTests
{
    [Fact]
    public void ClientWorkspace_UsesTheTestedControlsPanelForEveryResponsiveSurface()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));
        XNamespace controls = "clr-namespace:MinecraftServerManager.App.Controls";

        var panels = document.Descendants(controls + "ResponsiveWrapPanel").ToArray();

        Assert.Equal(6, panels.Length);
        Assert.All(panels, panel => Assert.NotNull(panel.Attribute("MinItemWidth")));
        Assert.All(panels, panel => Assert.Null(panel.Attribute("MinimumItemWidth")));
        Assert.False(File.Exists(TestRepositoryPaths.AppSource(
            "Infrastructure",
            "ResponsiveWrapPanel.cs")));
    }

    [Fact]
    public void CatalogPreview_FailsClosedUnlessDownloadedArtworkIsDecodedBeforeCapture()
    {
        var app = File.ReadAllText(TestRepositoryPaths.AppSource("App.xaml.cs"));
        var thumbnail = File.ReadAllText(TestRepositoryPaths.AppSource(
            "Infrastructure",
            "LocalImageThumbnail.cs"));

        Assert.Contains("WaitForThumbnailRenderingAsync(applicationWindow)", app, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource(TimeSpan.FromSeconds(15))", app, StringComparison.Ordinal);
        Assert.Contains("LoadForDiagnosticsAsync(image, timeout.Token)", app, StringComparison.Ordinal);
        Assert.Contains("decodedCount == 0", app, StringComparison.Ordinal);
        Assert.Contains("image.Source = source", thumbnail, StringComparison.Ordinal);

        var waitIndex = app.IndexOf(
            "await WaitForThumbnailRenderingAsync(applicationWindow)",
            StringComparison.Ordinal);
        var renderIndex = app.IndexOf(
            "RenderPreview(applicationWindow, renderClientCatalogPreviewPath)",
            waitIndex,
            StringComparison.Ordinal);
        Assert.True(waitIndex >= 0);
        Assert.True(renderIndex > waitIndex);
    }
}
