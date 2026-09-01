using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml.Linq;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts.Localization;

namespace MinecraftServerManager.App.Tests;

public sealed class OnlineModpackDialogContractTests
{
    private static readonly Regex ResourceReferencePattern = new(
        @"\{(?:Static|Dynamic)Resource\s+([^\s,}]+)\}",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Dialog_UsesDarkDynamicResourcesAndContainsRequiredControls()
    {
        var document = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "OnlineModpackDialog.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var window = Assert.IsType<XElement>(document.Root);

        Assert.Equal("{StaticResource AppWindowStyle}", (string?)window.Attribute("Style"));
        Assert.Contains(
            document.Descendants().Attributes(),
            attribute => attribute.Value.Contains("{DynamicResource WindowBrush}", StringComparison.Ordinal));
        Assert.Contains(
            document.Descendants(presentation + "ListBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding Providers}");
        Assert.Contains(
            document.Descendants(presentation + "ListBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding CatalogItems}");
        Assert.Contains(
            document.Descendants(presentation + "ComboBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding SortChoices}");
        Assert.Contains(
            document.Descendants(presentation + "ComboBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding GameVersionChoices}");
        Assert.Contains(
            document.Descendants(presentation + "ListBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding LoaderChoices}");
        Assert.Contains(
            document.Descendants(presentation + "ListBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding CategoryChoices}");
        Assert.Contains(
            document.Descendants(presentation + "ListBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding Versions}");
        Assert.Contains(
            document.Descendants(presentation + "TextBox"),
            element => ((string?)element.Attribute("Text"))?.Contains("ServerName", StringComparison.Ordinal) == true);
        var progressBar = Assert.Single(document.Descendants(presentation + "ProgressBar"));
        Assert.Equal(
            "{Binding ProgressPercentage, Mode=OneWay}",
            (string?)progressBar.Attribute("Value"));
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.online.downloadInstall}");
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.online.featured}");
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text")
                       == "{Binding ProgressDetailText, Mode=OneWay}");
        Assert.Equal("OnWindowLoaded", (string?)window.Attribute("Loaded"));
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains("ServerPackStatus", StringComparison.Ordinal) == true);

        var catalogImage = Assert.Single(
            document.Descendants(presentation + "Image"),
            element => (string?)element.Attribute("Source") == "{Binding Artwork, Mode=OneWay}");
        Assert.Equal(
            "{Binding Artwork, Mode=OneWay}",
            (string?)catalogImage.Attribute("Source"));
        Assert.DoesNotContain("IconUri", document.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewImageUri", document.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_UsesResponsivePanelWithoutAFixedCardWidth()
    {
        var document = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "OnlineModpackDialog.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var catalog = Assert.Single(
            document.Descendants(presentation + "ListBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding CatalogItems}");
        var itemsPanel = Assert.Single(catalog.Elements(presentation + "ListBox.ItemsPanel"));
        var responsivePanel = Assert.Single(
            itemsPanel.Descendants(),
            element => element.Name.LocalName == "ResponsiveWrapPanel");

        Assert.Equal("260", (string?)responsivePanel.Attribute("MinItemWidth"));
        Assert.Equal("12", (string?)responsivePanel.Attribute("HorizontalSpacing"));
        Assert.Equal("12", (string?)responsivePanel.Attribute("VerticalSpacing"));
        Assert.Empty(itemsPanel.Descendants(presentation + "WrapPanel"));
        Assert.Equal("False", (string?)catalog.Attribute("ScrollViewer.CanContentScroll"));

        var cardStyle = Assert.Single(
            document.Descendants(presentation + "Style"),
            element => (string?)element.Attribute(x + "Key") == "CatalogCardItem");
        Assert.DoesNotContain(
            cardStyle.Elements(presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Width");
    }

    [Fact]
    public void FilterLists_KeepTheDarkApplicationTemplateWhileDisabled()
    {
        var document = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "OnlineModpackDialog.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = Assert.Single(
            document.Descendants(presentation + "Style"),
            element => (string?)element.Attribute(xaml + "Key") == "DarkFilterListBoxStyle");
        Assert.Equal("ListBox", (string?)style.Attribute("TargetType"));
        Assert.Equal(
            "{StaticResource ThemedListBoxStyle}",
            (string?)style.Attribute("BasedOn"));

        var setters = style.Elements(presentation + "Setter")
            .ToDictionary(
                element => (string?)element.Attribute("Property") ?? string.Empty,
                element => (string?)element.Attribute("Value") ?? string.Empty,
                StringComparer.Ordinal);
        Assert.Equal("{DynamicResource WindowBrush}", setters["Background"]);
        Assert.Equal("0", setters["BorderThickness"]);
        Assert.Equal("0", setters["Padding"]);
        Assert.Equal("Disabled", setters["ScrollViewer.HorizontalScrollBarVisibility"]);
        Assert.Equal("Disabled", setters["ScrollViewer.VerticalScrollBarVisibility"]);

        var filterSources = new[]
        {
            "{Binding Providers}",
            "{Binding LoaderChoices}",
            "{Binding CategoryChoices}"
        };
        foreach (var itemsSource in filterSources)
        {
            var list = Assert.Single(
                document.Descendants(presentation + "ListBox"),
                element => (string?)element.Attribute("ItemsSource") == itemsSource);
            Assert.Equal(
                "{StaticResource DarkFilterListBoxStyle}",
                (string?)list.Attribute("Style"));
            Assert.Null(list.Attribute("Background"));
        }

        var providerList = Assert.Single(
            document.Descendants(presentation + "ListBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding Providers}");
        Assert.Equal(
            "{Binding CanChangeProvider}",
            (string?)providerList.Attribute("IsEnabled"));
    }

    [Fact]
    public void Dialog_UsesOnlyAnOperationScopedSecureCurseForgeCredential()
    {
        var xamlPath = GetAppSourcePath(Path.Combine("Dialogs", "OnlineModpackDialog.xaml"));
        var codePath = GetAppSourcePath(Path.Combine("Dialogs", "OnlineModpackDialog.xaml.cs"));
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var credentialInput = Assert.Single(document.Descendants(presentation + "PasswordBox"));
        Assert.Equal("256", (string?)credentialInput.Attribute("MaxLength"));
        Assert.Equal(
            "OnCurseForgeApiKeyChanged",
            (string?)credentialInput.Attribute("PasswordChanged"));
        Assert.Contains("CurseForgeApiKeyBox", xaml, StringComparison.Ordinal);
        Assert.Contains("SecurePassword.Copy()", code, StringComparison.Ordinal);
        Assert.Contains("credential.MakeReadOnly()", code, StringComparison.Ordinal);
        Assert.Contains("using var credential", code, StringComparison.Ordinal);
        Assert.Contains("CurseForgeApiKeyBox.Clear()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Json", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("File.", code, StringComparison.Ordinal);

        var workflowMethods = typeof(IOnlineModpackWorkflow).GetMethods();
        Assert.All(
            workflowMethods,
            method => Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.Name?.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) == true
                             && parameter.ParameterType == typeof(string)));
        Assert.DoesNotContain(
            typeof(OnlineModpackViewModel).GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            property => property.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
                        || property.PropertyType == typeof(SecureString));
    }

    [Fact]
    public void ClosingContract_CancelsAnActiveInstallBeforeAllowingTheWindowToClose()
    {
        var code = File.ReadAllText(GetAppSourcePath(
            Path.Combine("Dialogs", "OnlineModpackDialog.xaml.cs")));

        Assert.Contains("if (_viewModel.IsInstalling)", code, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true", code, StringComparison.Ordinal);
        Assert.Contains("_viewModel.CancelCurrentOperation()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void FtbMinecraftEulaConsent_IsUncheckedBoundAndUsesGuardedOfficialLink()
    {
        var xaml = File.ReadAllText(GetAppSourcePath(
            Path.Combine("Dialogs", "OnlineModpackDialog.xaml")));
        var code = File.ReadAllText(GetAppSourcePath(
            Path.Combine("Dialogs", "OnlineModpackDialog.xaml.cs")));
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var consent = Assert.Single(
            document.Descendants(presentation + "CheckBox"),
            element => (string?)element.Attribute(x + "Name") == "MinecraftEulaAcceptanceCheckBox");
        Assert.Equal(
            "{Binding MinecraftEulaAccepted, Mode=TwoWay}",
            (string?)consent.Attribute("IsChecked"));
        Assert.NotEqual("True", (string?)consent.Attribute("IsChecked"));

        var link = Assert.Single(
            document.Descendants(presentation + "Hyperlink"),
            element => (string?)element.Attribute("NavigateUri") == "https://aka.ms/MinecraftEULA");
        Assert.Equal(
            "OnMinecraftEulaLinkRequestNavigate",
            (string?)link.Attribute("RequestNavigate"));
        Assert.Contains("MinecraftEulaLinkOpener.TryOpen(this)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationResources_AreUniqueAndEveryDialogReferenceResolves()
    {
        var application = XDocument.Load(GetAppSourcePath("App.xaml"));
        var dialog = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "OnlineModpackDialog.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var applicationResources = Assert.Single(
            application.Descendants(presentation + "Application.Resources"));
        var dialogResources = Assert.Single(
            dialog.Descendants(presentation + "Window.Resources"));
        var applicationEntries = ExplicitResourceEntries(applicationResources, xaml).ToArray();
        var dialogEntries = ExplicitResourceEntries(dialogResources, xaml).ToArray();

        Assert.Empty(applicationEntries
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key));
        Assert.Empty(dialogEntries
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key));
        Assert.Single(applicationEntries, entry => entry.Key == "WindowColor");

        var availableKeys = applicationEntries
            .Concat(dialogEntries)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        availableKeys.UnionWith(
            ProductLocalizationCatalog.Keys.Select(key => $"L10n.{key}"));
        var unresolved = dialog.Root!
            .DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .SelectMany(attribute => ResourceReferencePattern.Matches(attribute.Value))
            .Select(match => match.Groups[1].Value)
            .Where(key => !availableKeys.Contains(key))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unresolved);
        Assert.Equal(
            "BooleanToVisibilityConverter",
            Assert.Single(applicationEntries, entry => entry.Key == "BoolToVisibility")
                .Element.Name.LocalName);
        Assert.Equal(
            "StringToVisibilityConverter",
            Assert.Single(applicationEntries, entry => entry.Key == "StringToVisibility")
                .Element.Name.LocalName);
    }

    [Fact]
    public void TwoWayByDefaultTargets_OnlyWriteToPublicSettersUnlessBindingIsExplicitlyOneWay()
    {
        var document = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "OnlineModpackDialog.xaml")));
        var targets = new TwoWayDefaultTarget[]
        {
            new("TextBox", "Text", TextBox.TextProperty, typeof(TextBox)),
            new("ComboBox", "SelectedItem", Selector.SelectedItemProperty, typeof(ComboBox)),
            new("ListBox", "SelectedItem", Selector.SelectedItemProperty, typeof(ListBox)),
            new("ProgressBar", "Value", RangeBase.ValueProperty, typeof(ProgressBar))
        };
        var readOnlyBindings = new List<string>();

        foreach (var target in targets)
        {
            var metadata = Assert.IsAssignableFrom<FrameworkPropertyMetadata>(
                target.Property.GetMetadata(target.OwnerType));
            Assert.True(
                metadata.BindsTwoWayByDefault,
                $"{target.ElementName}.{target.PropertyName} 不再是 BindsTwoWayByDefault；請更新契約掃描表。");

            foreach (var element in document.Descendants()
                         .Where(element => element.Name.LocalName == target.ElementName))
            {
                var markup = (string?)element.Attribute(target.PropertyName);
                if (markup is null || !markup.StartsWith("{Binding", StringComparison.Ordinal))
                {
                    continue;
                }

                var path = ReadBindingPath(markup);
                var sourceProperty = typeof(OnlineModpackViewModel).GetProperty(
                    path,
                    BindingFlags.Instance | BindingFlags.Public);
                Assert.NotNull(sourceProperty);
                if (sourceProperty!.GetSetMethod(nonPublic: false) is not null)
                {
                    continue;
                }

                readOnlyBindings.Add(path);
                Assert.True(
                    Regex.IsMatch(
                        markup,
                        @"(?:^|,)\s*Mode\s*=\s*(?:OneWay|OneTime)(?:\s*[,}])",
                        RegexOptions.CultureInvariant),
                    $"{target.ElementName}.{target.PropertyName} 綁定唯讀 {path} 時必須明確使用 Mode=OneWay/OneTime：{markup}");
            }
        }

        Assert.Equal([nameof(OnlineModpackViewModel.ProgressPercentage)], readOnlyBindings);
    }

    [Fact]
    public void ResultAndVersionTemplates_UseOneWayBindingsForReadOnlyDisplayProperties()
    {
        var document = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "OnlineModpackDialog.xaml")));

        var templateBindings = document.Descendants()
            .Where(element => element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "DataTemplate"))
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Value.StartsWith("{Binding", StringComparison.Ordinal))
            .Select(attribute => attribute.Value)
            .ToArray();

        Assert.NotEmpty(templateBindings);
        Assert.All(
            templateBindings,
            binding => Assert.Contains("Mode=OneWay", binding, StringComparison.Ordinal));
        Assert.Contains(
            document.Descendants().Where(element => element.Name.LocalName == "Run")
                .Select(element => (string?)element.Attribute("Text")),
            value => value == "{Binding MinecraftVersion, Mode=OneWay}");
    }

    [Fact]
    public void ProductionFtbCatalogClient_DisablesAutomaticRedirects()
    {
        var code = File.ReadAllText(GetAppSourcePath(
            Path.Combine("Services", "OnlineModpackWorkflow.cs")));
        var start = code.IndexOf("_ftbCatalogClient = new HttpClient", StringComparison.Ordinal);
        var end = code.IndexOf("_modrinthApiClient = new HttpClient", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var composition = code[start..end];
        Assert.Contains("new SocketsHttpHandler", composition, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", composition, StringComparison.Ordinal);
        Assert.True(
            code.IndexOf("_ftbCatalog = ftbCatalog ??", StringComparison.Ordinal)
            > code.IndexOf("AllowAutoRedirect = false", start, StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionCurseApiClient_DisablesAutomaticRedirectBeforeApiKeyUse()
    {
        var code = File.ReadAllText(GetAppSourcePath(
            Path.Combine("Services", "OnlineModpackWorkflow.cs")));
        var start = code.IndexOf("_curseApiClient = new HttpClient", StringComparison.Ordinal);
        var end = code.IndexOf("_ftbCatalog =", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var composition = code[start..end];
        Assert.Contains("new SocketsHttpHandler", composition, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", composition, StringComparison.Ordinal);
        Assert.True(
            code.IndexOf("_curseForge = curseForge ??", StringComparison.Ordinal)
            > code.IndexOf("AllowAutoRedirect = false", start, StringComparison.Ordinal));
    }

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);

    private static IEnumerable<(string Key, XElement Element)> ExplicitResourceEntries(
        XElement resources,
        XNamespace xaml)
        => resources.Elements()
            .Select(element => (Key: (string?)element.Attribute(xaml + "Key"), Element: element))
            .Where(entry => entry.Key is not null)
            .Select(entry => (entry.Key!, entry.Element));

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
        Type OwnerType);

}
