using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class CoreServerCreationProgressVisualTests
{
    [Fact]
    public void ExternalBuildDetail_UsesSecondNonInteractiveDarkProgressBarInsideDialog()
    {
        WpfStaTestHost.Run(() =>
        {
            var workflow = new BlockingCreationWorkflow();
            var viewModel = new CoreServerCreationViewModel(workflow);
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            viewModel.SelectCoreAsync(Assert.Single(viewModel.Cores)).GetAwaiter().GetResult();
            Assert.True(viewModel.RequiresMinecraftEula);
            viewModel.MinecraftEulaAccepted = true;
            var dialog = new CoreServerCreationDialog(viewModel);

            try
            {
                var createTask = viewModel.CreateAsync();
                Assert.True(PumpDispatcherUntil(
                    () => viewModel.ShowDetailProgress,
                    TimeSpan.FromSeconds(5)));
                LayoutDialogContent(dialog);

                var overall = Assert.IsType<ProgressBar>(dialog.FindName("OverallProgressBar"));
                var detail = Assert.IsType<ProgressBar>(dialog.FindName("DetailProgressBar"));
                var root = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
                Assert.Equal(2, FindVisualChildren<ProgressBar>(root).Count());

                Assert.Equal(Visibility.Visible, overall.Visibility);
                Assert.Equal(48, overall.Value);
                Assert.False(overall.IsIndeterminate);
                Assert.Equal(Visibility.Visible, detail.Visibility);
                Assert.True(detail.IsIndeterminate);
                Assert.False(detail.IsHitTestVisible);
                Assert.False(detail.Focusable);
                Assert.Same(FindVisualAncestor<Border>(overall), FindVisualAncestor<Border>(detail));

                var background = Assert.IsType<SolidColorBrush>(detail.Background);
                Assert.NotEqual(Colors.White, background.Color);
                Assert.NotEqual(Color.FromRgb(0xF0, 0xF0, 0xF0), background.Color);
                Assert.Equal("Starting clone of Bukkit", viewModel.DetailText);

                viewModel.CancelCurrentOperation();
                Assert.True(PumpDispatcherUntil(
                    () => createTask.IsCompleted && !viewModel.IsBusy,
                    TimeSpan.FromSeconds(5)));
                Assert.True(createTask.IsCompletedSuccessfully);
                Assert.False(viewModel.ShowDetailProgress);
            }
            finally
            {
                viewModel.Dispose();
            }
        });
    }

    private static void LayoutDialogContent(Window dialog)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
        root.Measure(new Size(dialog.Width, dialog.Height));
        root.Arrange(new Rect(0, 0, dialog.Width, dialog.Height));
        root.UpdateLayout();
    }

    private static T? FindVisualAncestor<T>(DependencyObject child)
        where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(child);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
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

    private sealed class BlockingCreationWorkflow : ICoreServerCreationWorkflow
    {
        private static readonly CoreServerProduct Product = new(
            CoreServerSoftware.Spigot,
            "spigot",
            "Spigot",
            "Spigot BuildTools 測試核心");

        private static readonly CoreServerVersion Version = new(
            Product.CoreId,
            "1.21.4",
            "1.21.4",
            "1.21.4",
            "official-refs",
            IsRecommended: true);

        public Task<IReadOnlyList<CoreServerProduct>> GetAvailableCoresAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CoreServerProduct>>([Product]);

        public Task<IReadOnlyList<CoreServerVersion>> GetVersionsAsync(
            CoreServerProduct core,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CoreServerVersion>>([Version]);

        public async Task<ServerInstance> CreateAsync(
            CoreServerCreationRequest request,
            IProgress<CoreServerCreationProgress> progress,
            CancellationToken cancellationToken)
        {
            progress.Report(new(
                CoreServerCreationStage.Installing,
                "正在隔離環境建置 Spigot 1.21.4…",
                48,
                "Starting clone of Bukkit",
                IsDetailIndeterminate: true));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation should have interrupted the build.");
        }
    }
}
