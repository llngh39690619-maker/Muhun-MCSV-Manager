using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientWorkspacePresentationTests
{
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
        Assert.Contains("_ftbInstaller.InstallAsync", source, StringComparison.Ordinal);
        Assert.Contains("InstallSelectedFtbPackAsync", source, StringComparison.Ordinal);
        Assert.Contains("IncludeOptionalFiles: true", source, StringComparison.Ordinal);
        Assert.Contains("_ftbInstaller.RecoverPendingPromotionsAsync", source, StringComparison.Ordinal);
        Assert.Contains("FtbAppProtocol.OfficialDownloadPage", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (IsFtbCatalogSource)\r\n        {\r\n            await OpenSelectedFtbPackAsync(project)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogInstall_ExposesCancellationAndPreventsClosingWhileBusy()
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
            "CloseCatalogCommand = new RelayCommand(ShowSelectedInstance, () => !IsBusy);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("CloseCatalogCommand.NotifyCanExecuteChanged();", source, StringComparison.Ordinal);
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
