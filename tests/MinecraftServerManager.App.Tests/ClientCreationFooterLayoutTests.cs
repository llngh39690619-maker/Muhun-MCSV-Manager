using System.Xml.Linq;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientCreationFooterLayoutTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void CreatePage_KeepsActionsAndProgressOutsideTheScrollableForm()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));

        var createPage = FindNamedElement(document, "Grid", "CreatePageLayout");
        var rows = createPage
            .Element(Presentation + "Grid.RowDefinitions")!
            .Elements(Presentation + "RowDefinition")
            .Select(row => row.Attribute("Height")!.Value)
            .ToArray();

        Assert.Equal(["*", "Auto", "Auto"], rows);

        var scrollViewer = FindNamedDirectChild(createPage, "ScrollViewer", "CreateFormScrollViewer");
        var actionBar = FindNamedDirectChild(createPage, "Border", "CreateActionBar");
        var progressBar = FindNamedDirectChild(createPage, "ProgressBar", "CreateInstallProgressBar");

        Assert.Equal("0", (string?)scrollViewer.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)actionBar.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)progressBar.Attribute("Grid.Row"));
        Assert.Empty(scrollViewer.Descendants(Presentation + "ProgressBar"));
        Assert.Empty(actionBar.Descendants(Presentation + "ProgressBar"));
        Assert.Contains(
            actionBar.Descendants(Presentation + "Button"),
            button => string.Equals(
                (string?)button.Attribute("Command"),
                "{Binding CreateInstanceCommand}",
                StringComparison.Ordinal));
    }

    [Fact]
    public void FixedCreateFooter_RemainsResponsiveAndExposesProgressToAutomation()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));

        var actionBar = FindNamedElement(document, "Border", "CreateActionBar");
        var progressBar = FindNamedElement(document, "ProgressBar", "CreateInstallProgressBar");

        Assert.NotNull(actionBar.Descendants(Presentation + "WrapPanel").SingleOrDefault());
        Assert.Equal("{Binding ProgressValue, Mode=OneWay}", (string?)progressBar.Attribute("Value"));
        Assert.Equal("{Binding StatusText}", (string?)progressBar.Attribute("AutomationProperties.Name"));
        Assert.Equal("8", (string?)progressBar.Attribute("Height"));
    }

    private static XElement FindNamedElement(XDocument document, string localName, string name) =>
        document
            .Descendants(Presentation + localName)
            .Single(element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                name,
                StringComparison.Ordinal));

    private static XElement FindNamedDirectChild(XElement parent, string localName, string name) =>
        parent
            .Elements(Presentation + localName)
            .Single(element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                name,
                StringComparison.Ordinal));
}
