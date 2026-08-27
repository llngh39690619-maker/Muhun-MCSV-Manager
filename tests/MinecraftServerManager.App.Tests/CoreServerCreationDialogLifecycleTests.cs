using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class CoreServerCreationDialogLifecycleTests
{
    [Fact]
    public void ShowAndFirstLayout_LoadsEmptyCatalogWithoutBindingWriteBackOrCrash()
    {
        WpfStaTestHost.Run(() =>
        {
            var viewModel = new CoreServerCreationViewModel(new EmptyWorkflow());
            var dialog = new CoreServerCreationDialog(viewModel);
            var contentRendered = false;
            var timedOut = false;
            var timeout = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                timedOut = true;
                if (dialog.IsVisible)
                {
                    dialog.Close();
                }
            };
            dialog.ContentRendered += (_, _) =>
            {
                contentRendered = true;
                dialog.Close();
            };

            timeout.Start();
            var result = dialog.ShowDialog();
            timeout.Stop();

            Assert.False(timedOut);
            Assert.True(contentRendered);
            Assert.False(result);
            Assert.False(dialog.IsVisible);
        });
    }

    [Fact]
    public void CloseDuringCatalogLoad_IsBlockedUntilCancellationCompletes()
    {
        WpfStaTestHost.Run(() =>
        {
            var workflow = new BlockingCatalogWorkflow();
            var viewModel = new CoreServerCreationViewModel(workflow);
            var dialog = new CoreServerCreationDialog(viewModel);
            var closeWasBlocked = false;
            var timedOut = false;
            var timeout = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            var elapsed = TimeSpan.Zero;
            timeout.Tick += (_, _) =>
            {
                elapsed += timeout.Interval;
                if (workflow.CancellationObserved.Task.IsCompleted && !viewModel.IsBusy)
                {
                    timeout.Stop();
                    dialog.Close();
                    return;
                }

                if (elapsed >= TimeSpan.FromSeconds(5))
                {
                    timeout.Stop();
                    timedOut = true;
                    viewModel.CancelCurrentOperation();
                }
            };
            dialog.Loaded += (_, _) =>
            {
                Assert.True(viewModel.IsBusy);
                dialog.Close();
                closeWasBlocked = dialog.IsVisible;
            };

            timeout.Start();
            var result = dialog.ShowDialog();
            timeout.Stop();

            Assert.False(timedOut);
            Assert.True(closeWasBlocked);
            Assert.True(workflow.CancellationObserved.Task.IsCompletedSuccessfully);
            Assert.False(result);
        });
    }

    [Fact]
    public void SelectCore_WithActualVersion_CompletesVersionItemLayoutWithoutBindingWriteBackOrCrash()
    {
        WpfStaTestHost.Run(() =>
        {
            var workflow = new SingleVersionWorkflow();
            var viewModel = new CoreServerCreationViewModel(workflow);
            var dialog = new CoreServerCreationDialog(viewModel);
            var coreList = Assert.IsType<ListBox>(dialog.FindName("CoreList"));
            var versionItemWasLaidOut = false;
            var timedOut = false;
            var elapsed = TimeSpan.Zero;
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(25)
            };
            timer.Tick += (_, _) =>
            {
                elapsed += timer.Interval;
                if (coreList.SelectedItem is null && viewModel.Cores.Count == 1)
                {
                    coreList.SelectedItem = viewModel.Cores[0];
                }

                if (viewModel.Versions.Count == 1)
                {
                    dialog.UpdateLayout();
                    var versionList = Assert.Single(
                        FindVisualChildren<ListBox>(dialog),
                        list => ReferenceEquals(list.ItemsSource, viewModel.Versions));
                    versionList.UpdateLayout();
                    Assert.NotNull(versionList.ItemContainerGenerator.ContainerFromIndex(0));
                    versionItemWasLaidOut = true;
                    timer.Stop();
                    dialog.Close();
                    return;
                }

                if (elapsed < TimeSpan.FromSeconds(5))
                {
                    return;
                }

                timedOut = true;
                timer.Stop();
                dialog.Close();
            };

            timer.Start();
            var result = dialog.ShowDialog();
            timer.Stop();

            Assert.False(timedOut);
            Assert.True(versionItemWasLaidOut);
            Assert.False(result);
            Assert.Equal(1, workflow.VersionRequestCount);
        });
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

    private sealed class EmptyWorkflow : ICoreServerCreationWorkflow
    {
        public Task<IReadOnlyList<CoreServerProduct>> GetAvailableCoresAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CoreServerProduct>>([]);

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

    private sealed class SingleVersionWorkflow : ICoreServerCreationWorkflow
    {
        private static readonly CoreServerProduct Product = new(
            CoreServerSoftware.Paper,
            "paper",
            "Paper",
            "Paper 測試核心");

        public int VersionRequestCount { get; private set; }

        public Task<IReadOnlyList<CoreServerProduct>> GetAvailableCoresAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CoreServerProduct>>([Product]);

        public Task<IReadOnlyList<CoreServerVersion>> GetVersionsAsync(
            CoreServerProduct core,
            CancellationToken cancellationToken)
        {
            VersionRequestCount++;
            Assert.Equal(Product, core);
            return Task.FromResult<IReadOnlyList<CoreServerVersion>>(
            [
                new(
                    Product.CoreId,
                    "1.21.11-42",
                    "Paper 1.21.11 build 42",
                    "1.21.11",
                    "42",
                    DateTimeOffset.Parse("2026-08-17T00:00:00Z"),
                    IsRecommended: true)
            ]);
        }

        public Task<ServerInstance> CreateAsync(
            CoreServerCreationRequest request,
            IProgress<CoreServerCreationProgress> progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class BlockingCatalogWorkflow : ICoreServerCreationWorkflow
    {
        public TaskCompletionSource<bool> CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<CoreServerProduct>> GetAvailableCoresAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved.TrySetResult(true);
                }
            }
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
}
