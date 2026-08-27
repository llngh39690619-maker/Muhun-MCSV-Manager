using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class OnlineModpackDialogLifecycleTests
{
    [Fact]
    public void ShowAndFirstLayout_DoesNotWriteBackToReadOnlyProgressProperty()
    {
        WpfStaTestHost.Run(() =>
        {
            var viewModel = new OnlineModpackViewModel(new NoOpWorkflow());
            var dialog = new OnlineModpackDialog(viewModel);
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
                dialog.Close();
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
    public void FeaturedResultAndVersion_FirstLayoutDoesNotWriteBackToReadOnlyRecords()
    {
        WpfStaTestHost.Run(() =>
        {
            var workflow = new LayoutWorkflow();
            var viewModel = new OnlineModpackViewModel(workflow);
            viewModel.LoadFeaturedAsync(null).GetAwaiter().GetResult();
            var featured = Assert.Single(viewModel.Results);
            viewModel.SelectResultAsync(featured, null).GetAwaiter().GetResult();
            Assert.Single(viewModel.Versions);

            var dialog = new OnlineModpackDialog(viewModel, loadFeaturedOnOpen: false);
            var itemContainersWereLaidOut = false;
            var timedOut = false;
            Exception? layoutError = null;
            var elapsed = TimeSpan.Zero;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) =>
            {
                elapsed += timer.Interval;
                try
                {
                    dialog.UpdateLayout();
                    var lists = FindVisualChildren<ListBox>(dialog).ToArray();
                    var resultList = lists.SingleOrDefault(
                        list => ReferenceEquals(list.ItemsSource, viewModel.CatalogItems));
                    var versionList = lists.SingleOrDefault(
                        list => ReferenceEquals(list.ItemsSource, viewModel.Versions));
                    resultList?.UpdateLayout();
                    versionList?.UpdateLayout();
                    if (resultList?.ItemContainerGenerator.ContainerFromIndex(0) is not null
                        && versionList?.ItemContainerGenerator.ContainerFromIndex(0) is not null)
                    {
                        itemContainersWereLaidOut = true;
                        timer.Stop();
                        dialog.Close();
                        return;
                    }
                }
                catch (Exception exception)
                {
                    layoutError = exception;
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
            Assert.True(itemContainersWereLaidOut);
            Assert.Null(layoutError);
            Assert.False(result);
        });
    }

    [Fact]
    public void RapidCriteriaChanges_CoalesceIntoOneAutomaticBrowseWithTheFinalSearchAndFilters()
    {
        WpfStaTestHost.Run(() =>
        {
            var workflow = new RecordingBrowseWorkflow();
            var viewModel = new OnlineModpackViewModel(workflow)
            {
                SearchQuery = "  retained search  "
            };
            viewModel.SearchAsync(null).GetAwaiter().GetResult();
            workflow.Requests.Clear();
            workflow.CredentialLengths.Clear();
            var dialog = new OnlineModpackDialog(
                viewModel,
                loadFeaturedOnOpen: false,
                backgroundSubmitter: null,
                catalogRefreshDebounce: TimeSpan.FromMilliseconds(25));
            var elapsed = TimeSpan.Zero;
            TimeSpan? firstRequestAt = null;
            var timedOut = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            dialog.Loaded += (_, _) =>
            {
                viewModel.SelectedProvider = Assert.Single(
                    viewModel.Providers,
                    item => item.Provider == OnlineModpackProvider.Modrinth);
                viewModel.SelectedSort = Assert.Single(
                    viewModel.SortChoices,
                    item => item.Sort == OnlineModpackSort.RecentlyUpdated);
                viewModel.SelectedGameVersion = Assert.Single(
                    viewModel.GameVersionChoices,
                    item => item.Key == "1.20.1");
                viewModel.SelectedLoader = Assert.Single(
                    viewModel.LoaderChoices,
                    item => item.Key == "neoforge");
                viewModel.SelectedCategory = Assert.Single(
                    viewModel.CategoryChoices,
                    item => item.Key == "magic");
                viewModel.SelectedResultLimit = 60;
            };
            timer.Tick += (_, _) =>
            {
                elapsed += timer.Interval;
                if (workflow.Requests.Count > 0)
                {
                    firstRequestAt ??= elapsed;
                    if (elapsed - firstRequestAt >= TimeSpan.FromMilliseconds(100))
                    {
                        timer.Stop();
                        dialog.Close();
                        return;
                    }
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
            Assert.False(result);
            var request = Assert.Single(workflow.Requests);
            Assert.Equal(OnlineModpackProvider.Modrinth, request.Provider);
            Assert.Equal("retained search", request.Query);
            Assert.Equal(OnlineModpackSort.RecentlyUpdated, request.Sort);
            Assert.Equal("1.20.1", request.GameVersion);
            Assert.Equal("neoforge", request.Loader);
            Assert.Equal("magic", request.SourceCategory);
            Assert.Equal(60, request.Limit);
        });
    }

    [Fact]
    public void ManualFeaturedAndSearchButtons_ControlTheModeUsedByLaterAutomaticRefreshes()
    {
        WpfStaTestHost.Run(() =>
        {
            var workflow = new RecordingBrowseWorkflow();
            var viewModel = new OnlineModpackViewModel(workflow)
            {
                SearchQuery = "  retained search  "
            };
            var dialog = new OnlineModpackDialog(
                viewModel,
                loadFeaturedOnOpen: false,
                backgroundSubmitter: null,
                catalogRefreshDebounce: TimeSpan.FromMilliseconds(20));
            var elapsed = TimeSpan.Zero;
            TimeSpan? fourthRequestAt = null;
            var switchedToSearch = false;
            var timedOut = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            dialog.Loaded += (_, _) =>
            {
                var featuredButton = Assert.IsType<Button>(dialog.FindName("FeaturedButton"));
                featuredButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                viewModel.SelectedProvider = Assert.Single(
                    viewModel.Providers,
                    item => item.Provider == OnlineModpackProvider.Modrinth);
            };
            timer.Tick += (_, _) =>
            {
                elapsed += timer.Interval;
                if (!switchedToSearch && workflow.Requests.Count == 2)
                {
                    switchedToSearch = true;
                    var searchButton = Assert.IsType<Button>(dialog.FindName("SearchButton"));
                    searchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    viewModel.SelectedLoader = Assert.Single(
                        viewModel.LoaderChoices,
                        item => item.Key == "neoforge");
                }

                if (workflow.Requests.Count == 4)
                {
                    fourthRequestAt ??= elapsed;
                    if (elapsed - fourthRequestAt >= TimeSpan.FromMilliseconds(80))
                    {
                        timer.Stop();
                        dialog.Close();
                        return;
                    }
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
            Assert.False(result);
            Assert.True(switchedToSearch);
            Assert.Equal(4, workflow.Requests.Count);
            Assert.Equal(
                [string.Empty, string.Empty, "retained search", "retained search"],
                workflow.Requests.Select(static request => request.Query));
            Assert.Equal(
                [
                    OnlineModpackProvider.Ftb,
                    OnlineModpackProvider.Modrinth,
                    OnlineModpackProvider.Modrinth,
                    OnlineModpackProvider.Modrinth
                ],
                workflow.Requests.Select(static request => request.Provider));
            Assert.Equal(OnlineModpackBrowseMode.Search, viewModel.BrowseMode);
        });
    }

    [Fact]
    public void InitialLayout_LoadsTheDefaultCatalogExactlyOnce()
    {
        WpfStaTestHost.Run(() =>
        {
            var workflow = new RecordingBrowseWorkflow();
            var viewModel = new OnlineModpackViewModel(workflow);
            var dialog = new OnlineModpackDialog(
                viewModel,
                loadFeaturedOnOpen: true,
                backgroundSubmitter: null,
                catalogRefreshDebounce: TimeSpan.FromMilliseconds(20));
            var elapsed = TimeSpan.Zero;
            var timedOut = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                elapsed += timer.Interval;
                if (workflow.Requests.Count > 0 && elapsed >= TimeSpan.FromMilliseconds(120))
                {
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
            Assert.False(result);
            var request = Assert.Single(workflow.Requests);
            Assert.Equal(OnlineModpackProvider.Ftb, request.Provider);
            Assert.Equal(string.Empty, request.Query);
        });
    }

    [Fact]
    public void ProviderChange_CancelsAnInFlightBrowseAndPublishesOnlyTheReplacement()
    {
        WpfStaTestHost.Run(() =>
        {
            var workflow = new SupersedingBrowseWorkflow();
            var viewModel = new OnlineModpackViewModel(workflow);
            var dialog = new OnlineModpackDialog(
                viewModel,
                loadFeaturedOnOpen: false,
                backgroundSubmitter: null,
                catalogRefreshDebounce: TimeSpan.FromMilliseconds(20));
            var switchedToFtb = false;
            var elapsed = TimeSpan.Zero;
            var timedOut = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            dialog.Loaded += (_, _) =>
            {
                viewModel.SelectedProvider = Assert.Single(
                    viewModel.Providers,
                    item => item.Provider == OnlineModpackProvider.Modrinth);
            };
            timer.Tick += (_, _) =>
            {
                elapsed += timer.Interval;
                if (!switchedToFtb && workflow.Requests.Count == 1)
                {
                    switchedToFtb = true;
                    viewModel.SelectedProvider = Assert.Single(
                        viewModel.Providers,
                        item => item.Provider == OnlineModpackProvider.Ftb);
                }

                if (switchedToFtb && workflow.Requests.Count == 2 && viewModel.Results.Count == 1)
                {
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
            Assert.False(result);
            Assert.True(switchedToFtb);
            Assert.True(workflow.FirstCancellation.IsCancellationRequested);
            Assert.Equal(
                [OnlineModpackProvider.Modrinth, OnlineModpackProvider.Ftb],
                workflow.Requests.Select(static request => request.Provider));
            Assert.Equal(OnlineModpackProvider.Ftb, Assert.Single(viewModel.Results).Provider);
        });
    }

    [Fact]
    public void CurseForgeSelection_DoesNotBrowseWithAnEmptyKeyAndLoadsAfterKeyEntry()
    {
        WpfStaTestHost.Run(() =>
        {
            var workflow = new RecordingBrowseWorkflow();
            var viewModel = new OnlineModpackViewModel(workflow);
            var dialog = new OnlineModpackDialog(
                viewModel,
                loadFeaturedOnOpen: false,
                backgroundSubmitter: null,
                catalogRefreshDebounce: TimeSpan.FromMilliseconds(20));
            var elapsed = TimeSpan.Zero;
            var enteredCredential = false;
            var noRequestWasSentWithoutCredential = false;
            var timedOut = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            dialog.Loaded += (_, _) =>
            {
                viewModel.SelectedProvider = Assert.Single(
                    viewModel.Providers,
                    item => item.Provider == OnlineModpackProvider.CurseForge);
            };
            timer.Tick += (_, _) =>
            {
                elapsed += timer.Interval;
                if (!enteredCredential && elapsed >= TimeSpan.FromMilliseconds(120))
                {
                    noRequestWasSentWithoutCredential = workflow.Requests.Count == 0;
                    enteredCredential = true;
                    var passwordBox = Assert.IsType<PasswordBox>(
                        dialog.FindName("CurseForgeApiKeyBox"));
                    passwordBox.Password = "secret";
                }

                if (enteredCredential && workflow.Requests.Count == 1)
                {
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
            Assert.False(result);
            Assert.True(noRequestWasSentWithoutCredential);
            var request = Assert.Single(workflow.Requests);
            Assert.Equal(OnlineModpackProvider.CurseForge, request.Provider);
            Assert.Equal(6, Assert.Single(workflow.CredentialLengths));
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

    [Fact]
    public void CloseWhileLoadedFeaturedIsPending_CancelsWithoutHangOrLateResultPollution()
    {
        WpfStaTestHost.Run(() =>
        {
            var workflow = new PendingFeaturedWorkflow();
            var viewModel = new OnlineModpackViewModel(workflow);
            var dialog = new OnlineModpackDialog(viewModel);
            var loadedCloseWasRequested = false;
            var timedOut = false;
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                timedOut = true;
                if (dialog.IsVisible)
                {
                    dialog.Close();
                }
            };
            dialog.Loaded += (_, _) =>
            {
                Assert.NotNull(workflow.FeaturedCall);
                Assert.True(viewModel.IsBusy);
                loadedCloseWasRequested = true;
                dialog.Close();
            };

            timeout.Start();
            var result = dialog.ShowDialog();
            timeout.Stop();

            Assert.False(timedOut);
            Assert.True(loadedCloseWasRequested);
            Assert.False(result);
            Assert.False(dialog.IsVisible);
            Assert.True(workflow.FeaturedCancellation.IsCancellationRequested);
            Assert.False(viewModel.IsBusy);
            Assert.Empty(viewModel.Results);

            workflow.CompleteAfterCancellation();
            Assert.True(SpinWait.SpinUntil(
                () => workflow.FeaturedCall!.IsCompleted,
                TimeSpan.FromSeconds(2)));
            DrainDispatcher();

            Assert.False(viewModel.IsBusy);
            Assert.Empty(viewModel.Results);
        });
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class NoOpWorkflow : IOnlineModpackWorkflow
    {
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

    private sealed class LayoutWorkflow : IOnlineModpackWorkflow
    {
        private readonly OnlineModpackSearchResult _project = new(
            OnlineModpackProvider.Ftb,
            "134",
            "FTB Skies 2: Aero",
            "Minecraft 1.21.1",
            "Feed The Beast");

        public Task<IReadOnlyList<OnlineModpackSearchResult>> GetFeaturedAsync(
            OnlineModpackProvider provider,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>([_project]);

        public Task<IReadOnlyList<OnlineModpackSearchResult>> SearchAsync(
            OnlineModpackProvider provider,
            string query,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>([_project]);

        public Task<IReadOnlyList<OnlineModpackVersion>> GetVersionsAsync(
            OnlineModpackSearchResult project,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OnlineModpackVersion>>(
            [
                new(
                    OnlineModpackProvider.Ftb,
                    project.ProjectId,
                    "100001",
                    "1.6.1",
                    "1.21.1",
                    "NeoForge 21.1.248",
                    "release",
                    DateTimeOffset.UtcNow,
                    HasOfficialServerPack: true)
            ]);

        public Task<ServerInstance> InstallAsync(
            OnlineModpackInstallRequest request,
            SecureString? transientApiKey,
            IProgress<OnlineModpackInstallProgress> progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingBrowseWorkflow : IOnlineModpackWorkflow
    {
        public List<OnlineModpackBrowseRequest> Requests { get; } = [];

        public List<int?> CredentialLengths { get; } = [];

        public Task<IReadOnlyList<OnlineModpackSearchResult>> BrowseAsync(
            OnlineModpackBrowseRequest request,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            CredentialLengths.Add(transientApiKey?.Length);
            return Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>([]);
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

    private sealed class SupersedingBrowseWorkflow : IOnlineModpackWorkflow
    {
        public List<OnlineModpackBrowseRequest> Requests { get; } = [];

        public CancellationToken FirstCancellation { get; private set; }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> BrowseAsync(
            OnlineModpackBrowseRequest request,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                FirstCancellation = cancellationToken;
                return WaitUntilCancelledAsync(cancellationToken);
            }

            return Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>(
            [
                new(
                    request.Provider,
                    "replacement",
                    "Replacement",
                    "Latest browse result",
                    "Test")
            ]);
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

        private static async Task<IReadOnlyList<OnlineModpackSearchResult>> WaitUntilCancelledAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }

    private sealed class PendingFeaturedWorkflow : IOnlineModpackWorkflow
    {
        private readonly TaskCompletionSource<IReadOnlyList<OnlineModpackSearchResult>> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<OnlineModpackSearchResult>>? FeaturedCall { get; private set; }

        public CancellationToken FeaturedCancellation { get; private set; }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> GetFeaturedAsync(
            OnlineModpackProvider provider,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            FeaturedCancellation = cancellationToken;
            return FeaturedCall = _completion.Task;
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

        public void CompleteAfterCancellation()
            => _completion.SetResult(
            [
                new(
                    OnlineModpackProvider.Ftb,
                    "late-featured",
                    "遲到的 FTB 推薦",
                    "不得污染已關閉的對話框",
                    "Feed The Beast")
            ]);
    }
}
