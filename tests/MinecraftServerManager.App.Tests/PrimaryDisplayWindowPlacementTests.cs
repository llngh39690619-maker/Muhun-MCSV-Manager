using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Xml.Linq;
using MinecraftServerManager.App.Infrastructure;
using FormsScreen = System.Windows.Forms.Screen;

namespace MinecraftServerManager.App.Tests;

public sealed class PrimaryDisplayWindowPlacementTests
{
    [Fact]
    public void WpfTestHost_UsesPrivateNonInteractiveDesktop()
    {
        Assert.True(WpfStaTestHost.IsIsolatedDesktop);
    }

    [Fact]
    public void EveryProductWindow_UsesPlacementStyleAndDeterministicStartupLocation()
    {
        var windowFiles = Directory
            .EnumerateFiles(TestRepositoryPaths.AppSource(), "*.xaml", SearchOption.AllDirectories)
            .Select(static path => (Path: path, Document: XDocument.Load(path)))
            .Where(static candidate => candidate.Document.Root?.Name.LocalName == "Window")
            .ToArray();

        Assert.NotEmpty(windowFiles);
        foreach (var (path, document) in windowFiles)
        {
            var root = Assert.IsType<XElement>(document.Root);
            Assert.Equal(
                "{StaticResource AppWindowStyle}",
                (string?)root.Attribute("Style"));
            Assert.Contains(
                (string?)root.Attribute("WindowStartupLocation"),
                new[] { "CenterScreen", "CenterOwner" });
        }

        var application = XDocument.Load(TestRepositoryPaths.AppSource("App.xaml"));
        Assert.Contains(
            application.Descendants().Where(static element => element.Name.LocalName == "Setter"),
            static setter =>
                (string?)setter.Attribute("Property") ==
                "infra:PrimaryDisplayWindowPlacement.IsEnabled"
                && (string?)setter.Attribute("Value") == "True");
    }

    [Fact]
    public void CenteredBounds_UsesAnchorCenterAndClampsToNegativeCoordinateWorkArea()
    {
        var workArea = new Rect(-1920, 0, 1920, 1040);
        var owner = new Rect(-1700, 100, 1200, 800);

        var result = PrimaryDisplayWindowPlacement.CalculateCenteredBounds(
            new Rect(0, 0, 800, 600),
            owner,
            workArea);

        Assert.Equal(new Rect(-1500, 200, 800, 600), result);
    }

    [Fact]
    public void CenteredBounds_OversizedWindowPinsToWorkAreaOrigin()
    {
        var workArea = new Rect(1920, -200, 1280, 720);

        var result = PrimaryDisplayWindowPlacement.CalculateCenteredBounds(
            new Rect(0, 0, 1600, 900),
            workArea,
            workArea);

        Assert.Equal(new Rect(1920, -200, 1600, 900), result);
    }

    [Fact]
    public void ClampBounds_MovesRemovedMonitorCoordinatesBackIntoVisibleWorkArea()
    {
        var result = PrimaryDisplayWindowPlacement.ClampBoundsToWorkArea(
            new Rect(3900, 1300, 900, 700),
            new Rect(0, 0, 1920, 1040));

        Assert.Equal(new Rect(1020, 340, 900, 700), result);
    }

    [Theory]
    [InlineData(false, WindowState.Normal, false)]
    [InlineData(true, WindowState.Minimized, false)]
    [InlineData(true, WindowState.Maximized, false)]
    [InlineData(true, WindowState.Normal, true)]
    public void DisplayChangeClamp_RunsOnlyForLoadedNormalWindow(
        bool isLoaded,
        WindowState windowState,
        bool expected)
    {
        Assert.Equal(
            expected,
            PrimaryDisplayWindowPlacement.ShouldClampAfterDisplayChange(isLoaded, windowState));
    }

    [Fact]
    public void ProductWindowStyle_EnablesPlacementAndSuppressesTestActivation()
    {
        WpfStaTestHost.Run(() =>
        {
            Assert.True(WpfStaTestHost.IsIsolatedDesktop);
            var window = new Window
            {
                Style = (Style)Application.Current.Resources["AppWindowStyle"],
            };

            Assert.True(PrimaryDisplayWindowPlacement.GetIsEnabled(window));
            Assert.False(window.ShowActivated);
            Assert.False(
                PrimaryDisplayWindowPlacement.IsDisplaySettingsSubscribedForTesting(window));
        });
    }

    [Fact]
    public void DisplaySettingsSubscription_ExistsOnlyForLiveNativeWindow()
    {
        WpfStaTestHost.Run(() =>
        {
            var window = new Window
            {
                Width = 480,
                Height = 320,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                Style = (Style)Application.Current.Resources["AppWindowStyle"],
            };

            Assert.False(
                PrimaryDisplayWindowPlacement.IsDisplaySettingsSubscribedForTesting(window));
            try
            {
                window.Show();
                Assert.True(
                    PrimaryDisplayWindowPlacement.IsDisplaySettingsSubscribedForTesting(window));
            }
            finally
            {
                window.Close();
            }

            Assert.False(
                PrimaryDisplayWindowPlacement.IsDisplaySettingsSubscribedForTesting(window));
        });
    }

    [Fact]
    public void CenterScreenWindow_IsCreatedOnWindowsPrimaryDisplayWithoutActivation()
    {
        WpfStaTestHost.Run(() =>
        {
            var primary = FormsScreen.PrimaryScreen;
            Assert.NotNull(primary);

            var window = new Window
            {
                Width = 480,
                Height = 320,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                Style = (Style)Application.Current.Resources["AppWindowStyle"],
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new WindowInteropHelper(window).Handle;

                Assert.NotEqual(IntPtr.Zero, handle);
                Assert.Equal(primary!.DeviceName, FormsScreen.FromHandle(handle).DeviceName);
                Assert.False(window.IsActive);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CenterOwnerWindow_FollowsOwnerDisplayAndCenterWithoutActivation()
    {
        WpfStaTestHost.Run(() =>
        {
            var style = (Style)Application.Current.Resources["AppWindowStyle"];
            var owner = new Window
            {
                Width = 720,
                Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                Style = style,
            };
            Window? child = null;

            try
            {
                owner.Show();
                child = new Window
                {
                    Width = 360,
                    Height = 240,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ShowInTaskbar = false,
                    Style = style,
                    Owner = owner,
                };
                child.Show();
                owner.UpdateLayout();
                child.UpdateLayout();

                var ownerHandle = new WindowInteropHelper(owner).Handle;
                var childHandle = new WindowInteropHelper(child).Handle;
                Assert.Equal(
                    FormsScreen.FromHandle(ownerHandle).DeviceName,
                    FormsScreen.FromHandle(childHandle).DeviceName);
                Assert.InRange(
                    Math.Abs(
                        (owner.Left + (owner.ActualWidth / 2d))
                        - (child.Left + (child.ActualWidth / 2d))),
                    0,
                    2);
                Assert.InRange(
                    Math.Abs(
                        (owner.Top + (owner.ActualHeight / 2d))
                        - (child.Top + (child.ActualHeight / 2d))),
                    0,
                    2);
                Assert.False(owner.IsActive);
                Assert.False(child.IsActive);
            }
            finally
            {
                child?.Close();
                owner.Close();
            }
        });
    }
}
