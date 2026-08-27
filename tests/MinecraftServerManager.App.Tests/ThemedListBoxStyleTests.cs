using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Controls;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class ThemedListBoxStyleTests
{
    private const string ThemedListBoxStyleReference = "{StaticResource ThemedListBoxStyle}";
    private const string BusyEnabledBinding = "{Binding IsInputEnabled}";
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void CoreAndOnlineBusyLists_UseSharedThemeTemplateContract()
    {
        var coreDialog = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "CoreServerCreationDialog.xaml")));
        var onlineDialog = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "OnlineModpackDialog.xaml")));

        AssertBusyListContract(coreDialog, "{Binding Cores}", "Core 可用核心", canContentScroll: true);
        AssertBusyListContract(coreDialog, "{Binding Versions}", "Core 實際版本", canContentScroll: true);
        AssertBusyListContract(
            onlineDialog,
            "{Binding CatalogItems}",
            "線上模組包結果卡片",
            canContentScroll: false,
            expectedItemsPanel: "ResponsiveWrapPanel");
        AssertBusyListContract(onlineDialog, "{Binding Versions}", "線上模組包版本", canContentScroll: true);

        var application = XDocument.Load(GetAppSourcePath("App.xaml"));
        var style = Assert.Single(
            application.Descendants(Presentation + "Style"),
            element => (string?)element.Attribute(Xaml + "Key") == "ThemedListBoxStyle");

        Assert.Equal("{x:Type ListBox}", (string?)style.Attribute("TargetType"));
        AssertSetter(style, "Background", "{DynamicResource WindowBrush}");
        AssertSetter(style, "BorderBrush", "{DynamicResource BorderBrush}");
        AssertSetter(style, "ScrollViewer.CanContentScroll", "True");
        AssertSetter(style, "VirtualizingPanel.IsVirtualizing", "True");
        AssertSetter(style, "VirtualizingPanel.VirtualizationMode", "Recycling");
        Assert.Single(style.Descendants(Presentation + "VirtualizingStackPanel"));

        var template = Assert.Single(style.Descendants(Presentation + "ControlTemplate"));
        var listBorder = Assert.Single(
            template.Descendants(Presentation + "Border"),
            element => (string?)element.Attribute(Xaml + "Name") == "ListBorder");
        Assert.Equal("{TemplateBinding Background}", (string?)listBorder.Attribute("Background"));
        Assert.Equal("{TemplateBinding BorderBrush}", (string?)listBorder.Attribute("BorderBrush"));
        Assert.Equal("{TemplateBinding BorderThickness}", (string?)listBorder.Attribute("BorderThickness"));

        var scrollViewer = Assert.Single(
            template.Descendants(Presentation + "ScrollViewer"),
            element => (string?)element.Attribute(Xaml + "Name") == "PART_ScrollViewer");
        Assert.Equal(
            "{TemplateBinding ScrollViewer.CanContentScroll}",
            (string?)scrollViewer.Attribute("CanContentScroll"));
        Assert.Single(template.Descendants(Presentation + "ItemsPresenter"));

        Assert.DoesNotContain(
            template.Descendants(Presentation + "Trigger"),
            trigger => string.Equals(
                           (string?)trigger.Attribute("Property"),
                           "IsEnabled",
                           StringComparison.Ordinal)
                       && string.Equals(
                           (string?)trigger.Attribute("Value"),
                           "False",
                           StringComparison.OrdinalIgnoreCase));

        var templateMarkup = template.ToString(SaveOptions.DisableFormatting);
        Assert.DoesNotContain("SystemColors.ControlBrush", templateMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#FFFFFFFF", templateMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#FFFFFF", templateMarkup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoreAndOnlineBusyLists_KeepWindowBrushWhenActuallyDisabled()
    {
        WpfStaTestHost.Run(() =>
        {
            ProbeBusyCoreDialog();
            ProbeBusyOnlineDialog();
        });
    }

    private static void ProbeBusyCoreDialog()
    {
        var workflow = new BlockingCoreCatalogWorkflow();
        var viewModel = new CoreServerCreationViewModel(workflow);
        var dialog = new CoreServerCreationDialog(viewModel);

        try
        {
            var loadTask = viewModel.InitializeAsync();
            LayoutDialogContent(dialog);

            Assert.True(workflow.Started.Task.IsCompletedSuccessfully);
            Assert.True(viewModel.IsBusy);
            AssertBusyListVisuals(
                dialog,
                [viewModel.Cores, viewModel.Versions],
                "Core");

            viewModel.CancelCurrentOperation();
            Assert.True(PumpDispatcherUntil(
                () => loadTask.IsCompleted && !viewModel.IsBusy,
                TimeSpan.FromSeconds(5)));
            Assert.True(loadTask.IsCompletedSuccessfully);
        }
        finally
        {
            CloseAfterCancelling(dialog, viewModel.IsBusy, viewModel.CancelCurrentOperation, viewModel.Dispose);
        }
    }

    private static void ProbeBusyOnlineDialog()
    {
        var workflow = new BlockingFeaturedWorkflow();
        var viewModel = new OnlineModpackViewModel(workflow);
        var dialog = new OnlineModpackDialog(viewModel);

        try
        {
            var loadTask = viewModel.LoadFeaturedAsync(transientApiKey: null);
            LayoutDialogContent(dialog);

            Assert.True(workflow.Started.Task.IsCompletedSuccessfully);
            Assert.True(viewModel.IsBusy);
            AssertBusyListVisuals(
                dialog,
                [viewModel.CatalogItems, viewModel.Versions],
                "Online");

            viewModel.CancelCurrentOperation();
            Assert.True(PumpDispatcherUntil(
                () => loadTask.IsCompleted && !viewModel.IsBusy,
                TimeSpan.FromSeconds(5)));
            Assert.True(loadTask.IsCompletedSuccessfully);
        }
        finally
        {
            CloseAfterCancelling(dialog, viewModel.IsBusy, viewModel.CancelCurrentOperation, viewModel.Dispose);
        }
    }

    private static void AssertBusyListVisuals(
        Window dialog,
        IReadOnlyList<object> expectedItemsSources,
        string label)
    {
        var visualRoot = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
        var lists = FindVisualChildren<ListBox>(visualRoot)
            .Where(list => expectedItemsSources.Any(source => ReferenceEquals(source, list.ItemsSource)))
            .ToArray();
        Assert.Equal(expectedItemsSources.Count, lists.Length);

        var expectedWindowBrush = Assert.IsType<SolidColorBrush>(
            Application.Current.TryFindResource("WindowBrush"));
        var expectedBorderBrush = Assert.IsType<SolidColorBrush>(
            Application.Current.TryFindResource("BorderBrush"));
        var expectedStyle = Assert.IsType<Style>(
            Application.Current.TryFindResource("ThemedListBoxStyle"));

        foreach (var list in lists)
        {
            Assert.False(list.IsEnabled);
            Assert.Same(expectedStyle, list.Style);
            list.ApplyTemplate();
            list.UpdateLayout();

            var listBorder = Assert.IsType<Border>(list.Template.FindName("ListBorder", list));
            var actualBackground = Assert.IsType<SolidColorBrush>(listBorder.Background);
            var actualBorder = Assert.IsType<SolidColorBrush>(listBorder.BorderBrush);

            Assert.Equal(expectedWindowBrush.Color, actualBackground.Color);
            Assert.Equal(expectedBorderBrush.Color, actualBorder.Color);
            Assert.NotEqual(Colors.White, actualBackground.Color);
            Assert.NotEqual(Color.FromRgb(0xF0, 0xF0, 0xF0), actualBackground.Color);
            var responsivePanel = FindVisualChildren<ResponsiveWrapPanel>(list).SingleOrDefault();
            if (responsivePanel is not null)
            {
                Assert.False(ScrollViewer.GetCanContentScroll(list));
                continue;
            }

            Assert.True(
                VirtualizingPanel.GetIsVirtualizing(list),
                $"{label} busy ListBox 必須保留 UI virtualization。");
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
            Assert.True(ScrollViewer.GetCanContentScroll(list));
        }
    }

    private static void LayoutDialogContent(Window dialog)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
        root.Measure(new Size(dialog.Width, dialog.Height));
        root.Arrange(new Rect(0, 0, dialog.Width, dialog.Height));
        root.UpdateLayout();
    }

    private static void AssertBusyListContract(
        XDocument document,
        string itemsSource,
        string label,
        bool canContentScroll,
        string? expectedItemsPanel = null)
    {
        var list = Assert.Single(
            document.Descendants(Presentation + "ListBox"),
            element => (string?)element.Attribute("ItemsSource") == itemsSource);
        Assert.True(
            string.Equals(
                BusyEnabledBinding,
                (string?)list.Attribute("IsEnabled"),
                StringComparison.Ordinal),
            $"{label} 必須在 busy 時透過 IsInputEnabled 保留真正的 disabled 語意。");
        Assert.True(
            string.Equals(
                ThemedListBoxStyleReference,
                (string?)list.Attribute("Style"),
                StringComparison.Ordinal),
            $"{label} 必須套用共用 ThemedListBoxStyle，避免 WPF disabled 白底模板。");

        var localCanContentScroll = (string?)list.Attribute("ScrollViewer.CanContentScroll");
        if (canContentScroll)
        {
            Assert.True(
                localCanContentScroll is null or "True",
                $"{label} 必須保留共用樣式的邏輯捲動。");
        }
        else
        {
            Assert.Equal("False", localCanContentScroll);
        }

        if (expectedItemsPanel is not null)
        {
            var itemsPanel = Assert.Single(list.Elements(Presentation + "ListBox.ItemsPanel"));
            Assert.Single(
                itemsPanel.Descendants(),
                element => element.Name.LocalName == expectedItemsPanel);
        }
    }

    private static void AssertSetter(XElement style, string property, string value)
    {
        Assert.Contains(
            style.Elements(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == property
                      && (string?)setter.Attribute("Value") == value);
    }

    private static void CloseAfterCancelling(
        Window dialog,
        bool isBusy,
        Action cancel,
        Action dispose)
    {
        if (isBusy)
        {
            cancel();
            if (!PumpDispatcherUntil(
                    () => dialog.DataContext switch
                    {
                        CoreServerCreationViewModel core => !core.IsBusy,
                        OnlineModpackViewModel online => !online.IsBusy,
                        _ => true
                    },
                    TimeSpan.FromSeconds(2)))
            {
                dispose();
            }
        }

        if (dialog.IsVisible)
        {
            dialog.Close();
            return;
        }

        dispose();
    }

    private static bool PumpDispatcherUntil(Func<bool> predicate, TimeSpan timeout)
    {
        if (predicate())
        {
            return true;
        }

        var deadline = DateTime.UtcNow + timeout;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        timer.Tick += (_, _) =>
        {
            if (!predicate() && DateTime.UtcNow < deadline)
            {
                return;
            }

            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return predicate();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
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

    private sealed class BlockingCoreCatalogWorkflow : ICoreServerCreationWorkflow
    {
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<CoreServerProduct>> GetAvailableCoresAsync(
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public Task<IReadOnlyList<CoreServerVersion>> GetVersionsAsync(
            CoreServerProduct core,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ServerInstance> CreateAsync(
            CoreServerCreationRequest request,
            IProgress<CoreServerCreationProgress> progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class BlockingFeaturedWorkflow : IOnlineModpackWorkflow
    {
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<OnlineModpackSearchResult>> GetFeaturedAsync(
            OnlineModpackProvider provider,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> SearchAsync(
            OnlineModpackProvider provider,
            string query,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<OnlineModpackVersion>> GetVersionsAsync(
            OnlineModpackSearchResult project,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ServerInstance> InstallAsync(
            OnlineModpackInstallRequest request,
            SecureString? transientApiKey,
            IProgress<OnlineModpackInstallProgress> progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
