using System.Security;
using System.Windows;
using System.Windows.Controls;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class BackgroundDialogHandoffTests
{
    [Fact]
    public void CoreCreateClick_SubmitsImmutableRequestAndClosesWithoutExecutingWorkflow()
    {
        WpfStaTestHost.Run(() =>
        {
            var workflow = new CoreWorkflow();
            var viewModel = new CoreServerCreationViewModel(workflow);
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            viewModel.SelectCoreAsync(viewModel.Cores.Single()).GetAwaiter().GetResult();
            viewModel.SelectedVersion = viewModel.Versions.Single();
            viewModel.ServerName = "  Background Core  ";
            CoreServerCreationRequest? submitted = null;
            var dialog = new CoreServerCreationDialog(
                viewModel,
                request =>
                {
                    submitted = request;
                    return BackgroundJobSubmissionResult.Success();
                });
            dialog.ContentRendered += (_, _) =>
            {
                var button = Assert.IsType<Button>(dialog.FindName("CreateServerButton"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };

            var result = dialog.ShowDialog();

            Assert.True(result);
            Assert.NotNull(submitted);
            Assert.Equal("Background Core", submitted.ServerName);
            Assert.Equal(CoreWorkflow.Version, submitted.Version);
            Assert.Equal(0, workflow.CreateCount);
        });
    }

    [Fact]
    public void OnlineInstallClick_SubmitsImmutableRequestAndClosesWithoutExecutingWorkflow()
    {
        WpfStaTestHost.Run(() =>
        {
            var workflow = new OnlineWorkflow();
            var viewModel = new OnlineModpackViewModel(workflow);
            viewModel.LoadFeaturedAsync(null).GetAwaiter().GetResult();
            viewModel.SelectResultAsync(viewModel.Results.Single(), null).GetAwaiter().GetResult();
            viewModel.SelectedVersion = viewModel.Versions.Single();
            viewModel.ServerName = "  Background Pack  ";
            OnlineModpackInstallRequest? submitted = null;
            var dialog = new OnlineModpackDialog(
                viewModel,
                loadFeaturedOnOpen: false,
                request =>
                {
                    submitted = request;
                    return BackgroundJobSubmissionResult.Success();
                });
            dialog.ContentRendered += (_, _) =>
            {
                var button = Assert.Single(
                    FindVisualChildren<Button>(dialog),
                    candidate => candidate.Content as string == "下載並安裝");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };

            var result = dialog.ShowDialog();

            Assert.True(result);
            Assert.NotNull(submitted);
            Assert.Equal("Background Pack", submitted.ServerName);
            Assert.Equal(OnlineWorkflow.Version, submitted.Version);
            Assert.Equal(0, workflow.InstallCount);
        });
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
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

    private sealed class CoreWorkflow : ICoreServerCreationWorkflow
    {
        public static readonly CoreServerProduct Product = new(
            CoreServerSoftware.Paper,
            "paper",
            "Paper",
            "Test");
        public static readonly CoreServerVersion Version = new(
            "paper",
            "1.21.1-1",
            "1.21.1",
            "1.21.1",
            "1");

        public int CreateCount { get; private set; }

        public Task<IReadOnlyList<CoreServerProduct>> GetAvailableCoresAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CoreServerProduct>>([Product]);

        public Task<IReadOnlyList<CoreServerVersion>> GetVersionsAsync(
            CoreServerProduct core,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CoreServerVersion>>([Version]);

        public Task<ServerInstance> CreateAsync(
            CoreServerCreationRequest request,
            IProgress<CoreServerCreationProgress> progress,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            throw new InvalidOperationException("Background handoff must not execute inside the dialog.");
        }
    }

    private sealed class OnlineWorkflow : IOnlineModpackWorkflow
    {
        public static readonly OnlineModpackSearchResult Project = new(
            OnlineModpackProvider.Ftb,
            "project",
            "Pack",
            "Test",
            "Muhun");
        public static readonly OnlineModpackVersion Version = new(
            OnlineModpackProvider.Ftb,
            "project",
            "version",
            "1.0",
            "1.21.1",
            "Fabric",
            "release",
            DateTimeOffset.UtcNow,
            HasOfficialServerPack: true);

        public int InstallCount { get; private set; }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> GetFeaturedAsync(
            OnlineModpackProvider provider,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>([Project]);

        public Task<IReadOnlyList<OnlineModpackSearchResult>> SearchAsync(
            OnlineModpackProvider provider,
            string query,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>([Project]);

        public Task<IReadOnlyList<OnlineModpackVersion>> GetVersionsAsync(
            OnlineModpackSearchResult project,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OnlineModpackVersion>>([Version]);

        public Task<ServerInstance> InstallAsync(
            OnlineModpackInstallRequest request,
            SecureString? transientApiKey,
            IProgress<OnlineModpackInstallProgress> progress,
            CancellationToken cancellationToken)
        {
            InstallCount++;
            throw new InvalidOperationException("Background handoff must not execute inside the dialog.");
        }
    }
}
