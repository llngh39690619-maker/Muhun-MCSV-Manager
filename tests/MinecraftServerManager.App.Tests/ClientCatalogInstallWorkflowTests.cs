using System.Globalization;
using System.IO;
using System.Net.Http;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientCatalogInstallWorkflowTests
{
    [Fact]
    public async Task SelectingAndClosingProject_TransitionsBetweenResultsAndDetails()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        InitializeLocalization(directory.Path);
        await using var viewModel = CreateViewModel(directory.Path);
        var (project, _) = CreateCatalogSelection();

        Assert.True(viewModel.IsCatalogBrowseView);
        Assert.False(viewModel.IsCatalogDetailOpen);

        viewModel.SelectedCatalogProject = project;

        Assert.True(viewModel.IsCatalogDetailOpen);
        Assert.False(viewModel.IsCatalogBrowseView);
        Assert.Same(project, viewModel.SelectedCatalogProject);
        Assert.Single(viewModel.CatalogVersions);
        Assert.NotNull(viewModel.SelectedCatalogVersion);
        Assert.True(viewModel.CloseCatalogDetailsCommand.CanExecute(null));

        viewModel.CloseCatalogDetailsCommand.Execute(null);

        Assert.False(viewModel.IsCatalogDetailOpen);
        Assert.True(viewModel.IsCatalogBrowseView);
        Assert.Null(viewModel.SelectedCatalogProject);
        Assert.Null(viewModel.SelectedCatalogVersion);
        Assert.Empty(viewModel.CatalogVersions);
        Assert.False(viewModel.CloseCatalogDetailsCommand.CanExecute(null));
    }

    [Fact]
    public async Task InstallJobs_RemainVisibleInBackgroundAndEveryTerminalStateEndsRunningState()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        InitializeLocalization(directory.Path);
        await using var viewModel = CreateViewModel(directory.Path);
        var (project, version) = CreateCatalogSelection();

        var completed = viewModel.StartCatalogInstallJob(project, version);
        completed.Report("download", "Downloading", 0.4d);

        Assert.True(viewModel.HasCatalogInstallJobs);
        Assert.True(viewModel.IsCatalogInstallQueueExpanded);
        Assert.True(viewModel.IsCatalogInstallRunning);
        Assert.Same(completed, viewModel.ActiveCatalogInstallJob);
        Assert.Equal(0.4d, viewModel.CatalogInstallQueueProgressValue);

        completed.MarkCompleted("Completed");
        Assert.False(viewModel.IsCatalogInstallRunning);
        viewModel.FinishCatalogInstallJob(completed);
        Assert.Null(viewModel.ActiveCatalogInstallJob);

        var failed = viewModel.StartCatalogInstallJob(project, version);
        failed.MarkFailed("Failed");
        Assert.False(viewModel.IsCatalogInstallRunning);
        viewModel.FinishCatalogInstallJob(failed);

        var canceled = viewModel.StartCatalogInstallJob(project, version);
        canceled.MarkCanceled("Canceled");
        Assert.False(viewModel.IsCatalogInstallRunning);
        viewModel.FinishCatalogInstallJob(canceled);

        Assert.True(viewModel.HasCompletedCatalogInstallJobs);
        Assert.True(viewModel.ClearCompletedCatalogInstallJobsCommand.CanExecute(null));

        viewModel.ClearCompletedCatalogInstallJobsCommand.Execute(null);

        Assert.Empty(viewModel.CatalogInstallJobs);
        Assert.False(viewModel.HasCatalogInstallJobs);
        Assert.False(viewModel.IsCatalogInstallQueueExpanded);
        Assert.False(viewModel.IsCatalogInstallRunning);
    }

    [Fact]
    public void InstallJob_OnlyReachesOneAfterSuccessfulCompletionAndIgnoresLateProgress()
    {
        var job = new ClientCatalogInstallJobViewModel(
            Guid.NewGuid(),
            "FTB Pack",
            "Stable",
            "FTB",
            "Queued");

        job.Report("install-game", "Game payload reported complete", 1d);

        Assert.True(job.IsRunning);
        Assert.True(job.ProgressValue < 1d);

        job.MarkFailed(
            "Failed at install-game; diagnostic: diag-123",
            "install-game",
            "diag-123");
        var failedProgress = job.ProgressValue;
        job.Report("complete", "Late success callback", 1d);

        Assert.True(job.IsFailed);
        Assert.Equal("install-game", job.FailedStage);
        Assert.Equal("diag-123", job.FailureDiagnosticId);
        Assert.Equal(failedProgress, job.ProgressValue);
        Assert.Equal("Failed at install-game; diagnostic: diag-123", job.StatusText);
        Assert.DoesNotContain(job.Activities, item => item.StatusText == "Late success callback");

        var completed = new ClientCatalogInstallJobViewModel(
            Guid.NewGuid(),
            "FTB Pack",
            "Stable",
            "FTB",
            "Queued");
        completed.Report("finalize", "Finalizing", 1d);
        Assert.True(completed.ProgressValue < 1d);

        completed.MarkCompleted("Completed");

        Assert.Equal(ClientCatalogInstallJobState.Completed, completed.State);
        Assert.Equal(1d, completed.ProgressValue);
    }

    [Theory]
    [InlineData("install-game", 0d, 0.10d)]
    [InlineData("install-game", 1d, 0.60d)]
    [InlineData("download-content", 0d, 0.60d)]
    [InlineData("download-content", 1d, 0.95d)]
    [InlineData("finalize", 1d, 0.97d)]
    public void FtbProgress_IsSegmentWeightedAndNeverReportsCompletion(
        string stage,
        double phaseFraction,
        double expected)
    {
        var actual = ClientWorkspaceViewModel.ResolveFtbCatalogInstallProgress(
            new FtbClientPackInstallProgress(stage, stage, Fraction: phaseFraction));

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value);
        Assert.True(actual.Value < 1d);
    }

    [Fact]
    public async Task InstallJobCollectionReset_DetachesRemovedJobObservers()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        InitializeLocalization(directory.Path);
        await using var viewModel = CreateViewModel(directory.Path);
        var (project, version) = CreateCatalogSelection();
        var job = viewModel.StartCatalogInstallJob(project, version);
        var queueSummaryChanges = 0;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(ClientWorkspaceViewModel.CatalogInstallQueueSummary))
            {
                queueSummaryChanges++;
            }
        };

        viewModel.CatalogInstallJobs.Clear();
        queueSummaryChanges = 0;

        job.Report("download", "Detached update", 0.5d);

        Assert.Equal(0, queueSummaryChanges);
        Assert.False(viewModel.IsCatalogInstallRunning);
        Assert.Null(viewModel.ActiveCatalogInstallJob);
    }

    [Fact]
    public async Task CultureChange_RefreshesPersistentQueueTextAndTerminalStatus()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        InitializeLocalization(directory.Path);
        await using var viewModel = CreateViewModel(directory.Path);
        var (project, version) = CreateCatalogSelection();
        var job = viewModel.StartCatalogInstallJob(project, version);
        job.MarkCompleted(LocalizationService.Current.Get("client.vm.catalog.jobs.completed"));
        viewModel.FinishCatalogInstallJob(job);

        try
        {
            Assert.Equal("收合下載清單", viewModel.CatalogInstallQueueToggleText);
            Assert.Equal("模組包已完成下載與建立。", job.StatusText);

            LocalizationService.Current.SetCulture("en-US");

            Assert.Equal("Collapse download list", viewModel.CatalogInstallQueueToggleText);
            Assert.Equal("The modpack was downloaded and created.", job.StatusText);
            Assert.Equal(job.StatusText, viewModel.CatalogInstallQueueSummary);
            Assert.All(job.Activities, activity =>
                Assert.Equal("The modpack was downloaded and created.", activity.StatusText));
        }
        finally
        {
            LocalizationService.Current.SetCulture("zh-TW");
        }
    }

    [Fact]
    public async Task OptionalProjectDetailsFailure_DoesNotDiscardInstallableVersions()
    {
        var versionsTask = Task.FromResult<IReadOnlyList<string>>(["1.21.1"]);
        var detailsTask = ClientWorkspaceViewModel.LoadOptionalCatalogDetailsAsync<string>(
            static _ => Task.FromException<string>(new HttpRequestException("details unavailable")),
            CancellationToken.None);

        await Task.WhenAll(detailsTask, versionsTask);

        Assert.Null(await detailsTask);
        Assert.Equal("1.21.1", Assert.Single(await versionsTask));
    }

    [Fact]
    public async Task BackgroundCompletion_DoesNotStealSelectionAfterLeavingCatalog()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        InitializeLocalization(directory.Path);
        await using var viewModel = CreateViewModel(directory.Path);
        var (project, _) = CreateCatalogSelection();
        var current = new ClientInstanceItemViewModel(new MinecraftClientInstance
        {
            Id = Guid.NewGuid(),
            Name = "Current instance",
            GameVersion = "1.21.1",
            DirectoryPath = Path.Combine(directory.Path, "current"),
        });
        var installed = new ClientInstanceItemViewModel(new MinecraftClientInstance
        {
            Id = Guid.NewGuid(),
            Name = "Installed in background",
            GameVersion = "1.21.1",
            DirectoryPath = Path.Combine(directory.Path, "installed"),
        });
        viewModel.SelectedInstance = current;
        viewModel.SelectedCatalogProject = project;

        viewModel.AddInstalledCatalogInstance(installed, project);

        Assert.Same(current, viewModel.SelectedInstance);
        Assert.Same(installed, Assert.Single(viewModel.Instances));
    }

    private static ClientWorkspaceViewModel CreateViewModel(string rootPath) =>
        new(
            new ApplicationPaths(rootPath),
            static () => new NewMinecraftClientDefaultsSettings());

    private static void InitializeLocalization(string rootPath) =>
        LocalizationService.Current.Initialize(
            Path.Combine(rootPath, "language.json"),
            CultureInfo.GetCultureInfo("zh-TW"));

    private static (ClientModpackProjectItemViewModel Project, ClientCatalogVersionItemViewModel Version)
        CreateCatalogSelection()
    {
        var version = new FtbClientCatalogVersion(
            17,
            170,
            "Stable",
            "1.21.1",
            "NeoForge",
            "21.1.1",
            DateTimeOffset.UtcNow,
            "21");
        var project = new ClientModpackProjectItemViewModel(new FtbClientCatalogProject(
            17,
            "Background pack",
            "A complete test description.",
            42,
            DateTimeOffset.UtcNow,
            null,
            null,
            [version]));
        return (project, new ClientCatalogVersionItemViewModel(version));
    }
}
