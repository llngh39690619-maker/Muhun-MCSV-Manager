using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientWorkspacePresentationTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void HeaderAccountSelector_ShowsCrispHeadNameSelectionAndAddAction()
    {
        var xaml = File.ReadAllText(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));
        var header = xaml[..xaml.IndexOf("<Grid Grid.Row=\"1\">", StringComparison.Ordinal)];

        Assert.Contains("Width=\"32\" Height=\"32\" Stretch=\"Fill\"", header, StringComparison.Ordinal);
        Assert.Contains("RenderOptions.BitmapScalingMode=\"NearestNeighbor\"", header, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding AccountButtonAccessibleName}\"", header, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding AccountButtonAccessibleName}\"", header, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HeaderAccountSelector\"", header, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Accounts}\"", header, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedAccount}\"", header, StringComparison.Ordinal);
        Assert.Contains("DisplayMemberPath=\"Username\"", header, StringComparison.Ordinal);
        Assert.Contains("<RowDefinition Height=\"58\" />", header, StringComparison.Ordinal);
        Assert.Contains("Width=\"176\" Height=\"42\"", header, StringComparison.Ordinal);
        Assert.Contains("Padding=\"10,0,34,0\" FontSize=\"15\" FontWeight=\"Medium\"", header, StringComparison.Ordinal);
        Assert.Contains("VerticalContentAlignment=\"Center\"", header, StringComparison.Ordinal);
        Assert.Contains("Width=\"42\" MinWidth=\"42\" Height=\"42\"", header, StringComparison.Ordinal);
        Assert.Contains("Height=\"38\" MinWidth=\"0\" Padding=\"10,6\"", header, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding AddAccountCommand}\"", header, StringComparison.Ordinal);
        Assert.Contains("L10n.client.account.select", header, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolutionUi_UsesOneSelectorAndFullScreenInsteadOfWidthHeightInputs()
    {
        var xaml = File.ReadAllText(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));

        Assert.Equal(2, CountOccurrences(xaml, "ItemsSource=\"{Binding ResolutionChoices}\""));
        Assert.Equal(2, CountOccurrences(xaml, "SelectedItem=\"{Binding SelectedResolution}\""));
        Assert.DoesNotContain("Text=\"{Binding WindowWidth}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding WindowHeight}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding WindowWidthText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding WindowHeightText", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentCards_OpenTheDownloadCenterOnTheirOwnTypedTab()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));
        var buttons = document
            .Descendants(Presentation + "Button")
            .Where(button => string.Equals(
                (string?)button.Attribute("Command"),
                "{Binding OpenContentDownloadCommand}",
                StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(3, buttons.Length);
        Assert.Equal(
            [
                "{x:Static clientContracts:MinecraftClientContentKind.Mod}",
                "{x:Static clientContracts:MinecraftClientContentKind.ResourcePack}",
                "{x:Static clientContracts:MinecraftClientContentKind.ShaderPack}",
            ],
            buttons
                .Select(button => (string?)button.Attribute("CommandParameter") ?? string.Empty)
                .ToArray());
        Assert.All(
            buttons,
            button => Assert.Equal(
                "{StaticResource ClientContentPrimaryActionButton}",
                (string?)button.Attribute("Style")));

        var contentFolderButtons = document
            .Descendants(Presentation + "Button")
            .Where(button => (string?)button.Attribute("CommandParameter") is
                "mods" or "resourcepacks" or "shaderpacks")
            .ToArray();
        Assert.Equal(3, contentFolderButtons.Length);
        Assert.All(
            contentFolderButtons,
            button => Assert.Equal(
                "{StaticResource ClientContentActionButton}",
                (string?)button.Attribute("Style")));

        var workspaceSource = File.ReadAllText(TestRepositoryPaths.AppSource(
            "ViewModels",
            "ClientWorkspaceViewModel.cs"));
        var openStart = workspaceSource.IndexOf(
            "private async Task OpenContentDownloadAsync",
            StringComparison.Ordinal);
        Assert.True(openStart >= 0);
        var openEnd = workspaceSource.IndexOf(
            "private async Task SelectContentDownloadKindAsync",
            openStart,
            StringComparison.Ordinal);
        Assert.True(openEnd > openStart);
        var openMethod = workspaceSource[openStart..openEnd];
        var kindAssignment = openMethod.IndexOf(
            "ContentDownloadKind = kind;",
            StringComparison.Ordinal);
        var windowRequest = openMethod.IndexOf(
            "ContentDownloadCenterRequested?.Invoke",
            StringComparison.Ordinal);
        Assert.True(kindAssignment >= 0);
        Assert.True(windowRequest >= 0);
        Assert.True(kindAssignment < windowRequest);
        Assert.Contains(
            "parameter is MinecraftClientContentKind typedKind",
            workspaceSource,
            StringComparison.Ordinal);

        var mainWindowSource = File.ReadAllText(TestRepositoryPaths.AppSource(
            "ViewModels",
            "MainWindowViewModel.cs"));
        var activateStart = mainWindowSource.IndexOf(
            "private void OnContentDownloadCenterRequested",
            StringComparison.Ordinal);
        Assert.True(activateStart >= 0);
        var activateEnd = mainWindowSource.IndexOf(
            "private async Task StartSelectedAsync",
            activateStart,
            StringComparison.Ordinal);
        Assert.True(activateEnd > activateStart);
        var activateMethod = mainWindowSource[activateStart..activateEnd];
        Assert.Contains("existing.Activate();", activateMethod, StringComparison.Ordinal);
        Assert.Contains("window.Show();", activateMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void FtbCatalog_UsesDirectInstallOptionsAndKeepsOfficialAppAsFallback()
    {
        var xaml = File.ReadAllText(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));
        var source = File.ReadAllText(TestRepositoryPaths.AppSource(
            "ViewModels",
            "ClientWorkspaceViewModel.cs"));

        Assert.Contains("ShowsCatalogInstallOptions", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding IsModrinthCatalogSource, Converter={StaticResource BoolToVisibility}}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("OpenFtbFallbackCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("L10n.client.catalog.ftbFallbackAction", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenClientDiagnosticsFolderCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowsFtbInstallDiagnostic", xaml, StringComparison.Ordinal);
        Assert.Contains("L10n.client.catalog.openDiagnosticsFolder", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{DynamicResource L10n.client.catalog.openDiagnosticsFolder}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("_ftbInstaller.InstallAsync", source, StringComparison.Ordinal);
        Assert.Contains("InstallSelectedFtbPackAsync", source, StringComparison.Ordinal);
        Assert.Contains("IncludeOptionalFiles: true", source, StringComparison.Ordinal);
        Assert.Contains("_ftbInstaller.RecoverPendingPromotionsAsync", source, StringComparison.Ordinal);
        Assert.Contains("FtbAppProtocol.OfficialDownloadPage", source, StringComparison.Ordinal);
        Assert.Contains("FtbClientInstallFailurePolicy.Classify", source, StringComparison.Ordinal);
        Assert.Contains("_clientOperationDiagnosticStore.WriteFailureAsync", source, StringComparison.Ordinal);
        Assert.Contains("new ClientOperationDiagnosticStore(_paths)", source, StringComparison.Ordinal);
        Assert.Contains("progressTracker.LastStage", source, StringComparison.Ordinal);
        Assert.Contains("[\"versionId\"]", source, StringComparison.Ordinal);
        Assert.Contains("[\"gameVersion\"]", source, StringComparison.Ordinal);
        Assert.Contains("[\"javaVersion\"]", source, StringComparison.Ordinal);
        Assert.Contains("ErrorText = LocalizeFtbInstallFailure", source, StringComparison.Ordinal);
        Assert.Contains("localizationKey + \".withoutDiagnostic\"", source, StringComparison.Ordinal);

        var ftbInstallStart = source.IndexOf(
            "private async Task InstallSelectedFtbPackAsync",
            StringComparison.Ordinal);
        var ftbInstallEnd = source.IndexOf(
            "private Task OpenSelectedFtbFallbackAsync",
            ftbInstallStart,
            StringComparison.Ordinal);
        var ftbInstall = source[ftbInstallStart..ftbInstallEnd];
        Assert.Contains("ClearFtbInstallFailureState();", ftbInstall, StringComparison.Ordinal);
        Assert.Contains(
            "catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)",
            ftbInstall,
            StringComparison.Ordinal);
        Assert.Contains("_isShowingFtbInstallFailure = true;", ftbInstall, StringComparison.Ordinal);
        Assert.Contains("SelectFtbInstallFailureLocalizationKey", ftbInstall, StringComparison.Ordinal);
        Assert.DoesNotContain("ErrorText = error.Message", ftbInstall, StringComparison.Ordinal);
        Assert.DoesNotContain("ErrorText = L(\"client.vm.catalog.ftb.directFailed\")", ftbInstall, StringComparison.Ordinal);

        var runGuardedStart = source.IndexOf(
            "private async Task RunGuardedAsync",
            StringComparison.Ordinal);
        var runGuardedEnd = source.IndexOf(
            "private void ClearFtbInstallFailureState",
            runGuardedStart,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ClearFtbInstallFailureState();",
            source[runGuardedStart..runGuardedEnd],
            StringComparison.Ordinal);
        Assert.Contains(
            "_isShowingFtbInstallFailure = false;",
            source[runGuardedStart..runGuardedEnd],
            StringComparison.Ordinal);

        var diagnosticsStart = source.IndexOf(
            "private void OpenClientDiagnosticsFolder",
            StringComparison.Ordinal);
        var diagnosticsEnd = source.IndexOf(
            "private void OpenSelectedExternalInstaller",
            diagnosticsStart,
            StringComparison.Ordinal);
        var diagnostics = source[diagnosticsStart..diagnosticsEnd];
        Assert.Contains("Directory.Exists", diagnostics, StringComparison.Ordinal);
        Assert.Contains("using var process = Process.Start(start);", diagnostics, StringComparison.Ordinal);
        Assert.Contains("if (process is null)", diagnostics, StringComparison.Ordinal);
        Assert.Contains(
            "catch (Exception error) when (error is not OutOfMemoryException)",
            diagnostics,
            StringComparison.Ordinal);
        Assert.Contains("ShowClientDiagnosticsFolderError();", diagnostics, StringComparison.Ordinal);
        Assert.Contains("client.vm.catalog.ftb.diagnosticsFolderOpenFailed", diagnostics, StringComparison.Ordinal);

        var cultureChangedStart = source.IndexOf(
            "private void OnCultureChanged",
            StringComparison.Ordinal);
        var cultureChangedEnd = source.IndexOf(
            "private static string L(",
            cultureChangedStart,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (_isShowingFtbInstallFailure &&",
            source[cultureChangedStart..cultureChangedEnd],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (IsFtbCatalogSource)\r\n        {\r\n            await OpenSelectedFtbPackAsync(project)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogInstall_ExposesCancellationAndAllowsLeavingDuringBackgroundInstall()
    {
        var xaml = File.ReadAllText(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));
        var source = File.ReadAllText(TestRepositoryPaths.AppSource(
            "ViewModels",
            "ClientWorkspaceViewModel.cs"));
        var catalogStart = xaml.IndexOf(
            "Visibility=\"{Binding IsCatalogPage",
            StringComparison.Ordinal);
        var catalogEnd = xaml.IndexOf(
            "Visibility=\"{Binding IsDashboardPage",
            catalogStart,
            StringComparison.Ordinal);
        var catalog = xaml[catalogStart..catalogEnd];

        Assert.Contains("Command=\"{Binding CancelOperationCommand}\"", catalog, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding IsBusy, Converter={StaticResource BoolToVisibility}}\"",
            catalog,
            StringComparison.Ordinal);
        Assert.Contains(
            "() => !IsBusy || IsCatalogInstallRunning",
            source,
            StringComparison.Ordinal);
        Assert.Contains("CloseCatalogCommand.NotifyCanExecuteChanged();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogDetails_KeepInstallActionAndBackgroundQueueOutsideScrollableContent()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var catalogLayout = document
            .Descendants(presentation + "Grid")
            .Single(element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "CatalogPageLayout",
                StringComparison.Ordinal));
        var pageScrollViewer = catalogLayout
            .Descendants(presentation + "ScrollViewer")
            .Single(element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "CatalogPageScrollViewer",
                StringComparison.Ordinal));
        var actionBar = catalogLayout
            .Descendants(presentation + "Border")
            .Single(element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "CatalogDetailActionBar",
                StringComparison.Ordinal));
        var installButtonsInScrollContent = pageScrollViewer
            .Descendants(presentation + "Button")
            .Where(element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding InstallCatalogPackCommand}",
                StringComparison.Ordinal));
        var installTray = document
            .Descendants(presentation + "Border")
            .Single(element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "CatalogInstallTray",
                StringComparison.Ordinal));

        Assert.Equal("0", (string?)pageScrollViewer.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)actionBar.Attribute("Grid.Row"));
        Assert.Empty(installButtonsInScrollContent);
        Assert.Contains(
            actionBar.Descendants(presentation + "Button"),
            element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding InstallCatalogPackCommand}",
                StringComparison.Ordinal));
        Assert.DoesNotContain(pageScrollViewer, installTray.Ancestors());
        Assert.Equal("2", (string?)installTray.Attribute("Grid.Row"));
        Assert.Contains("{Binding CatalogInstallJobs}", installTray.ToString(), StringComparison.Ordinal);
        Assert.Contains("{Binding ToggleCatalogInstallQueueCommand}", installTray.ToString(), StringComparison.Ordinal);
        Assert.Contains("{Binding ClearCompletedCatalogInstallJobsCommand}", installTray.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "{Binding SelectedCatalogProject.FullDescription}",
            catalogLayout.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogResults_LetMouseWheelReachThePageScrollViewer()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var pageScrollViewer = document
            .Descendants(presentation + "ScrollViewer")
            .Single(element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "CatalogPageScrollViewer",
                StringComparison.Ordinal));
        var resultsList = pageScrollViewer
            .Descendants(presentation + "ListBox")
            .Single(element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "CatalogResultsList",
                StringComparison.Ordinal));
        var listTemplate = resultsList
            .Element(presentation + "ListBox.Template")?
            .Element(presentation + "ControlTemplate");

        Assert.Equal("Auto", (string?)pageScrollViewer.Attribute("VerticalScrollBarVisibility"));
        Assert.NotNull(listTemplate);
        Assert.Empty(listTemplate.Descendants(presentation + "ScrollViewer"));
        Assert.Single(listTemplate.Descendants(presentation + "ItemsPresenter"));
    }

    [Fact]
    public void InstanceIcon_UsesOwnedArtworkAndRejectsExternalOrUriPaths()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        var instanceRoot = Path.Combine(temporary.Path, "instance");
        var assets = Path.Combine(instanceRoot, ".x-mcsv", "assets");
        Directory.CreateDirectory(assets);
        var ownedIcon = Path.Combine(assets, "catalog-icon.png");
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        File.WriteAllBytes(ownedIcon, png);
        var outsideIcon = Path.Combine(temporary.Path, "outside.png");
        File.WriteAllBytes(outsideIcon, png);
        var model = new MinecraftClientInstance
        {
            DirectoryPath = instanceRoot,
            IconImagePath = outsideIcon,
            CatalogIconImagePath = ownedIcon,
            CatalogProvider = "modrinth",
            Loader = MinecraftClientLoader.Fabric,
        };

        var item = new ClientInstanceItemViewModel(model);

        Assert.Equal(Path.GetFullPath(ownedIcon), item.IconImagePath);
        Assert.Equal("M", item.CatalogSourceBadgeText);
        Assert.False(item.UsesGrassBlockFallback);
        Assert.Null(ClientInstanceItemViewModel.ResolveSafeOwnedIconPath(
            model,
            outsideIcon,
            "https://cdn.modrinth.com/data/icon.png"));
    }

    [Fact]
    public void VanillaInstance_UsesGrassBlockFallbackWhenNoOwnedImageExists()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        var model = new MinecraftClientInstance
        {
            DirectoryPath = temporary.Path,
            Loader = MinecraftClientLoader.Vanilla,
        };
        var item = new ClientInstanceItemViewModel(model);

        Assert.True(item.UsesGrassBlockFallback);
        Assert.Equal(string.Empty, item.CatalogSourceBadgeText);
        Assert.Null(item.IconImagePath);
    }

    [Fact]
    public void PlayerHead_ComposesFaceAndHatAsNativeEightByEightPixelGrid()
    {
        BitmapSource? head = null;
        WpfStaTestHost.Run(() =>
        {
            const int size = 64;
            const int stride = size * 4;
            var pixels = new byte[stride * size];
            FillSquare(pixels, stride, 8, 8, 8, blue: 0, green: 0, red: 255, alpha: 255);
            SetPixel(pixels, stride, 40, 8, blue: 255, green: 0, red: 0, alpha: 255);
            var skin = BitmapSource.Create(
                size,
                size,
                96,
                96,
                PixelFormats.Pbgra32,
                null,
                pixels,
                stride);
            skin.Freeze();

            head = ClientWorkspaceViewModel.CreatePlayerHeadTexture(skin);
        });

        Assert.NotNull(head);
        Assert.Equal(8, head.PixelWidth);
        Assert.Equal(8, head.PixelHeight);
        Assert.Equal(96d, head.DpiX);
        var output = new byte[8 * 8 * 4];
        head.CopyPixels(output, 8 * 4, 0);
        Assert.Equal([255, 0, 0, 255], output.AsSpan(0, 4).ToArray());
        var centerOffset = (4 * 8 + 4) * 4;
        Assert.Equal([0, 0, 255, 255], output.AsSpan(centerOffset, 4).ToArray());
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static void FillSquare(
        byte[] pixels,
        int stride,
        int left,
        int top,
        int size,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        for (var y = top; y < top + size; y++)
        {
            for (var x = left; x < left + size; x++)
            {
                SetPixel(pixels, stride, x, y, blue, green, red, alpha);
            }
        }
    }

    private static void SetPixel(
        byte[] pixels,
        int stride,
        int x,
        int y,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        var offset = y * stride + x * 4;
        pixels[offset] = blue;
        pixels[offset + 1] = green;
        pixels[offset + 2] = red;
        pixels[offset + 3] = alpha;
    }
}
