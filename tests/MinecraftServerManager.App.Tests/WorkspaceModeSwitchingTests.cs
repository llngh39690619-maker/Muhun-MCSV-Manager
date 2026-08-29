using System.Xml.Linq;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Tests;

public sealed class WorkspaceModeSwitchingTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Views =
        "clr-namespace:MinecraftServerManager.App.Views";

    [Fact]
    public void MainWindow_ClientSurfaceVisibilityReadsTheMainViewModelBeforeReplacingItsDataContext()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource("MainWindow.xaml"));
        var clientSurface = Assert.Single(document.Descendants(Views + "ClientWorkspaceView"));

        Assert.Equal("{Binding ClientWorkspace}", (string?)clientSurface.Attribute("DataContext"));
        Assert.Equal(
            "{Binding DataContext.IsClientWorkspace, RelativeSource={RelativeSource AncestorType={x:Type Window}}, Converter={StaticResource BoolToVisibility}}",
            NormalizeMarkupExtension((string?)clientSurface.Attribute("Visibility")));

        var serverSurface = Assert.Single(
            document.Descendants(Presentation + "Grid"),
            element => (string?)element.Attribute("Visibility") ==
                       "{Binding IsServerWorkspace, Converter={StaticResource BoolToVisibility}}");
        Assert.NotNull(serverSurface);

        var serverBackground = Assert.Single(
            document.Descendants(Presentation + "Image"),
            element => (string?)element.Attribute("Source") ==
                       "{Binding SelectedServer.BackgroundImagePath}");
        Assert.Equal(
            "{Binding IsServerWorkspace, Converter={StaticResource BoolToVisibility}}",
            (string?)serverBackground.Attribute("Visibility"));
    }

    [Fact]
    public async Task WorkspaceCommands_KeepServerAndClientSurfacesMutuallyExclusive()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new MainWindowViewModel(new ApplicationPaths(temporary.Path));

        Assert.True(viewModel.IsServerWorkspace);
        Assert.False(viewModel.IsClientWorkspace);

        viewModel.ShowClientWorkspaceCommand.Execute(null);

        Assert.True(viewModel.IsClientWorkspace);
        Assert.False(viewModel.IsServerWorkspace);

        viewModel.ShowServerWorkspaceCommand.Execute(null);

        Assert.False(viewModel.IsClientWorkspace);
        Assert.True(viewModel.IsServerWorkspace);
    }

    private static string? NormalizeMarkupExtension(string? value)
        => value is null
            ? null
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
