using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Xml.Linq;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Tests;

public sealed class CoreServerCreationDialogContractTests
{
    [Fact]
    public void Dialog_UsesDarkDynamicResourcesAndContainsRequiredControls()
    {
        var document = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "CoreServerCreationDialog.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var window = Assert.IsType<XElement>(document.Root);

        Assert.Equal("{StaticResource AppWindowStyle}", (string?)window.Attribute("Style"));
        Assert.Contains(
            document.Descendants().Attributes(),
            attribute => attribute.Value.Contains("{DynamicResource WindowBrush}", StringComparison.Ordinal));
        Assert.Contains(
            document.Descendants().Attributes(),
            attribute => attribute.Value.Contains("{DynamicResource PanelBrush}", StringComparison.Ordinal));
        Assert.Contains(
            document.Descendants(presentation + "ListBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding Cores}");
        Assert.Contains(
            document.Descendants(presentation + "ListBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding Versions}");
        Assert.Contains(
            document.Descendants(presentation + "TextBox"),
            element => ((string?)element.Attribute("Text"))?.Contains(
                nameof(CoreServerCreationViewModel.VersionSearchQuery),
                StringComparison.Ordinal) == true);
        Assert.Contains(
            document.Descendants(presentation + "TextBox"),
            element => ((string?)element.Attribute("Text"))?.Contains(
                nameof(CoreServerCreationViewModel.ServerName),
                StringComparison.Ordinal) == true);
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains(
                nameof(CoreServerCreationViewModel.VersionStateText),
                StringComparison.Ordinal) == true);
        var progressBars = document.Descendants(presentation + "ProgressBar").ToArray();
        Assert.Equal(2, progressBars.Length);
        var progressBar = Assert.Single(
            progressBars,
            element => (string?)element.Attribute(xaml + "Name") == "OverallProgressBar");
        Assert.Equal(
            "{Binding ProgressPercentage, Mode=OneWay}",
            (string?)progressBar.Attribute("Value"));
        var detailProgressBar = Assert.Single(
            progressBars,
            element => (string?)element.Attribute(xaml + "Name") == "DetailProgressBar");
        Assert.Equal(
            "{Binding IsDetailIndeterminate}",
            (string?)detailProgressBar.Attribute("IsIndeterminate"));
        Assert.Equal("False", (string?)detailProgressBar.Attribute("IsHitTestVisible"));
        Assert.Equal("False", (string?)detailProgressBar.Attribute("Focusable"));
        var detailPanel = Assert.IsType<XElement>(detailProgressBar.Parent);
        Assert.Equal(
            "{Binding ShowDetailProgress, Converter={StaticResource BoolToVisibility}}",
            (string?)detailPanel.Attribute("Visibility"));
        Assert.Equal("False", (string?)detailPanel.Attribute("IsHitTestVisible"));
        Assert.Same(
            progressBar.Ancestors(presentation + "Border").First(),
            detailProgressBar.Ancestors(presentation + "Border").First());
        Assert.Single(window.DescendantsAndSelf(presentation + "Window"));
        Assert.Empty(window.Descendants(presentation + "Popup"));
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content") == "{DynamicResource L10n.core.create}");
    }

    [Fact]
    public void WorkflowSurface_ProvidesCatalogVersionsProgressCancellationAndCreatedServer()
    {
        var methods = typeof(ICoreServerCreationWorkflow).GetMethods();

        Assert.Equal(
            ["CreateAsync", "GetAvailableCoresAsync", "GetVersionsAsync"],
            methods.Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.All(
            methods,
            method => Assert.Contains(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(CancellationToken)));
        var create = Assert.Single(methods, method => method.Name == "CreateAsync");
        Assert.Contains(
            create.GetParameters(),
            parameter => parameter.ParameterType == typeof(IProgress<CoreServerCreationProgress>));
        Assert.Equal(typeof(Task<MinecraftServerManager.Core.Models.ServerInstance>), create.ReturnType);
    }

    [Fact]
    public void ClosingContract_BlocksWindowCloseAndCancelsEveryActiveOperation()
    {
        var code = File.ReadAllText(GetAppSourcePath(
            Path.Combine("Dialogs", "CoreServerCreationDialog.xaml.cs")));

        Assert.Contains("_viewModel.IsBusy", code, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true", code, StringComparison.Ordinal);
        Assert.Contains("_viewModel.CancelCurrentOperation()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoWayByDefaultTargets_OnlyWriteToPublicSettersUnlessExplicitlyOneWay()
    {
        var document = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "CoreServerCreationDialog.xaml")));
        var targets = new TwoWayDefaultTarget[]
        {
            new(
                "TextBox",
                "Text",
                TextBox.TextProperty,
                typeof(TextBox),
                typeof(CoreServerCreationViewModel)),
            new(
                "ListBox",
                "SelectedItem",
                Selector.SelectedItemProperty,
                typeof(ListBox),
                typeof(CoreServerCreationViewModel)),
            new(
                "ProgressBar",
                "Value",
                RangeBase.ValueProperty,
                typeof(ProgressBar),
                typeof(CoreServerCreationViewModel)),
            new(
                "Run",
                "Text",
                Run.TextProperty,
                typeof(Run),
                typeof(CoreServerVersion))
        };
        var readOnlyBindings = new List<string>();

        foreach (var target in targets)
        {
            var metadata = Assert.IsAssignableFrom<FrameworkPropertyMetadata>(
                target.Property.GetMetadata(target.OwnerType));
            Assert.True(metadata.BindsTwoWayByDefault);

            foreach (var element in document.Descendants()
                         .Where(element => element.Name.LocalName == target.ElementName))
            {
                var markup = (string?)element.Attribute(target.PropertyName);
                if (markup is null || !markup.StartsWith("{Binding", StringComparison.Ordinal))
                {
                    continue;
                }

                var path = ReadBindingPath(markup);
                var sourceProperty = target.SourceType.GetProperty(
                    path,
                    BindingFlags.Instance | BindingFlags.Public);
                Assert.NotNull(sourceProperty);
                if (sourceProperty!.GetSetMethod(nonPublic: false) is not null)
                {
                    continue;
                }

                readOnlyBindings.Add(path);
                Assert.Matches(
                    new Regex(
                        @"(?:^|,)\s*Mode\s*=\s*(?:OneWay|OneTime)(?:\s*[,}])",
                        RegexOptions.CultureInvariant),
                    markup);
            }
        }

        Assert.Equal(
            [
                nameof(CoreServerCreationViewModel.ProgressPercentage),
                nameof(CoreServerVersion.BuildDisplay),
                nameof(CoreServerVersion.ReleaseDateDisplay)
            ],
            readOnlyBindings);
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

    private static string ReadBindingPath(string markup)
    {
        var body = markup["{Binding".Length..].TrimStart();
        if (body.EndsWith('}'))
        {
            body = body[..^1];
        }

        var first = body.Split(',', 2)[0].Trim();
        const string pathPrefix = "Path=";
        return first.StartsWith(pathPrefix, StringComparison.Ordinal)
            ? first[pathPrefix.Length..].Trim()
            : first;
    }

    private sealed record TwoWayDefaultTarget(
        string ElementName,
        string PropertyName,
        DependencyProperty Property,
        Type OwnerType,
        Type SourceType);
}
