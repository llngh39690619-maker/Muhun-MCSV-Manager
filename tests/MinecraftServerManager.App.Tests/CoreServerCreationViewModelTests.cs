using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class CoreServerCreationViewModelTests
{
    [Fact]
    public void SoftwareVocabulary_CoversEveryRequestedCoreFamily()
    {
        Assert.Equal(
        [
            CoreServerSoftware.Paper,
            CoreServerSoftware.Spigot,
            CoreServerSoftware.CraftBukkit,
            CoreServerSoftware.Forge,
            CoreServerSoftware.NeoForge,
            CoreServerSoftware.Fabric,
            CoreServerSoftware.Mohist,
            CoreServerSoftware.Arclight,
            CoreServerSoftware.Velocity,
            CoreServerSoftware.Vanilla,
            CoreServerSoftware.CatServer,
            CoreServerSoftware.Akarin
        ],
            Enum.GetValues<CoreServerSoftware>());
    }

    [Fact]
    public async Task InitializeAsync_ExposesOnlyUsableProductsReturnedByWorkflow()
    {
        var paper = Product(CoreServerSoftware.Paper, "paper", "Paper");
        var catServer = Product(CoreServerSoftware.CatServer, "catserver", "CatServer");
        var workflow = new FakeWorkflow
        {
            CoreResults =
            [
                paper,
                catServer,
                Product(CoreServerSoftware.Akarin, "", "Not usable"),
                paper with { DisplayName = "Duplicate must not appear" }
            ]
        };
        var viewModel = new CoreServerCreationViewModel(workflow);

        await viewModel.InitializeAsync();

        Assert.Equal([paper, catServer], viewModel.Cores);
        Assert.DoesNotContain(
            Enum.GetValues<CoreServerSoftware>(),
            software => viewModel.Cores.Any(item => item.Software == software)
                        && software is not CoreServerSoftware.Paper and not CoreServerSoftware.CatServer);
        Assert.False(viewModel.IsBusy);
        Assert.Contains("2 種", viewModel.StageText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewModel.CoreCatalogStateText);
    }

    [Fact]
    public async Task EmptyVersionResult_IsExplicitAndNeverFabricatesFallbackVersion()
    {
        var core = Product(CoreServerSoftware.Akarin, "akarin", "Akarin");
        var workflow = new FakeWorkflow { CoreResults = [core], VersionResults = [] };
        var viewModel = new CoreServerCreationViewModel(workflow);
        await viewModel.InitializeAsync();

        await viewModel.SelectCoreAsync(Assert.Single(viewModel.Cores));

        Assert.Empty(viewModel.Versions);
        Assert.Null(viewModel.SelectedVersion);
        Assert.False(viewModel.CanCreate);
        Assert.Contains("沒有可建立的實際版本", viewModel.VersionStateText, StringComparison.Ordinal);
        Assert.Contains("不會顯示假版本", viewModel.VersionStateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectCoreAndSearch_FilterOnlyMatchingActualVersions()
    {
        var core = Product(CoreServerSoftware.Vanilla, "vanilla", "原版");
        var oldVersion = Version(core, "1.20.6", "1.20.6", "release");
        var currentVersion = Version(core, "26.2", "26.2", "stable-26.2");
        var wrongCore = currentVersion with { CoreId = "paper", VersionId = "wrong" };
        var workflow = new FakeWorkflow
        {
            CoreResults = [core],
            VersionResults = [oldVersion, currentVersion, wrongCore]
        };
        var viewModel = new CoreServerCreationViewModel(workflow);
        await viewModel.InitializeAsync();
        await viewModel.SelectCoreAsync(core);

        Assert.Equal([oldVersion, currentVersion], viewModel.Versions);
        viewModel.VersionSearchQuery = "26.2";

        Assert.Same(currentVersion, Assert.Single(viewModel.Versions));
        Assert.Same(currentVersion, viewModel.SelectedVersion);
        Assert.Equal(string.Empty, viewModel.VersionStateText);

        viewModel.VersionSearchQuery = "does-not-exist";
        Assert.Empty(viewModel.Versions);
        Assert.Null(viewModel.SelectedVersion);
        Assert.Contains("沒有符合", viewModel.VersionStateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingVersion_GeneratesCoreAndMinecraftVersionServerName()
    {
        var core = Product(CoreServerSoftware.Paper, "paper", "Paper");
        var version = Version(core, "build-100", "1.21.11", "100");
        var workflow = new FakeWorkflow { CoreResults = [core], VersionResults = [version] };
        var viewModel = new CoreServerCreationViewModel(workflow);

        await viewModel.InitializeAsync();
        await viewModel.SelectCoreAsync(core);

        Assert.Same(version, viewModel.SelectedVersion);
        Assert.Equal("Paper-1.21.11", viewModel.ServerName);
    }

    [Fact]
    public async Task ManualServerName_IsPreservedWhenSelectedVersionChanges()
    {
        var core = Product(CoreServerSoftware.Velocity, "velocity", "Velocity");
        var first = Version(core, "4.0.0", "4.0.0", "stable");
        var second = Version(core, "3.4.0", "3.4.0", "stable");
        var workflow = new FakeWorkflow
        {
            CoreResults = [core],
            VersionResults = [first, second]
        };
        var viewModel = new CoreServerCreationViewModel(workflow);
        await viewModel.InitializeAsync();
        await viewModel.SelectCoreAsync(core);
        viewModel.ServerName = "我的 Proxy";

        viewModel.SelectedVersion = second;

        Assert.Equal("我的 Proxy", viewModel.ServerName);
    }

    [Fact]
    public async Task UntouchedAutomaticServerName_TracksSelectedVersion()
    {
        var core = Product(CoreServerSoftware.Velocity, "velocity", "Velocity");
        var first = Version(core, "4.0.0", "4.0.0", "stable");
        var second = Version(core, "3.4.0", "3.4.0", "stable");
        var workflow = new FakeWorkflow
        {
            CoreResults = [core],
            VersionResults = [first, second]
        };
        var viewModel = new CoreServerCreationViewModel(workflow);
        await viewModel.InitializeAsync();
        await viewModel.SelectCoreAsync(core);

        viewModel.SelectedVersion = second;

        Assert.Equal("Velocity-3.4.0", viewModel.ServerName);
    }

    [Fact]
    public async Task CreateAsync_ReportsProgressAndReturnsCreatedServer()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            var core = Product(CoreServerSoftware.Arclight, "arclight", "Arclight");
            var version = Version(core, "1.0", "1.20.1", "build-100");
            var created = new ServerInstance
            {
                Name = "My Arclight",
                DirectoryPath = "C:\\servers\\arclight",
                ServerJarPath = "server.jar"
            };
            var workflow = new FakeWorkflow
            {
                CoreResults = [core],
                VersionResults = [version],
                CreatedServer = created,
                ProgressToReport = new(
                    CoreServerCreationStage.Verifying,
                    "正在驗證下載內容…",
                    48)
            };
            var viewModel = await ReadyForCreationAsync(workflow, "My Arclight");
            var createdRaised = false;
            viewModel.Created += (_, _) => createdRaised = true;

            await viewModel.CreateAsync();

            Assert.True(createdRaised);
            Assert.Same(created, viewModel.CreatedServer);
            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.IsCreating);
            Assert.Equal(100, viewModel.ProgressPercentage);
            Assert.False(viewModel.IsProgressIndeterminate);
            Assert.Equal("My Arclight", workflow.LastCreateRequest?.ServerName);
            Assert.Same(core, workflow.LastCreateRequest?.Core);
            Assert.Same(version, workflow.LastCreateRequest?.Version);
            Assert.True(workflow.LastCreateRequest?.MinecraftEulaAccepted);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task CreateAsync_DetailProgressPreservesMeasuredOverallStageAndClearsOnCancel()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            var core = Product(CoreServerSoftware.Spigot, "spigot", "Spigot");
            var workflow = new FakeWorkflow
            {
                CoreResults = [core],
                VersionResults = [Version(core, "1", "1.21.4", "official-refs")],
                ProgressToReport = new(
                    CoreServerCreationStage.Installing,
                    "正在隔離環境建置 Spigot 1.21.4…",
                    48,
                    "  Starting clone of Bukkit  ",
                    IsDetailIndeterminate: true),
                BlockCreateUntilCancelled = true
            };
            var viewModel = await ReadyForCreationAsync(workflow, "Spigot-1.21.4");

            var operation = viewModel.CreateAsync();
            await workflow.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(viewModel.IsBusy);
            Assert.Equal("正在隔離環境建置 Spigot 1.21.4…", viewModel.StageText);
            Assert.Equal(48, viewModel.ProgressPercentage);
            Assert.False(viewModel.IsProgressIndeterminate);
            Assert.True(viewModel.ShowProgressPercentage);
            Assert.Equal("Starting clone of Bukkit", viewModel.DetailText);
            Assert.True(viewModel.IsDetailIndeterminate);
            Assert.True(viewModel.ShowDetailProgress);

            viewModel.CancelCurrentOperation();

            Assert.Equal(string.Empty, viewModel.DetailText);
            Assert.False(viewModel.IsDetailIndeterminate);
            Assert.False(viewModel.ShowDetailProgress);
            await operation;
            Assert.False(viewModel.IsBusy);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task CancelCurrentOperation_CancelsCreationAndDoesNotPublishServer()
    {
        var core = Product(CoreServerSoftware.CatServer, "catserver", "CatServer");
        var workflow = new FakeWorkflow
        {
            CoreResults = [core],
            VersionResults = [Version(core, "1", "1.12.2", "release")],
            BlockCreateUntilCancelled = true
        };
        var viewModel = await ReadyForCreationAsync(workflow, "Cancel Me");

        var operation = viewModel.CreateAsync();
        await workflow.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsCreating);
        Assert.Equal(CoreServerCreationOperationState.Creating, viewModel.OperationState);

        viewModel.CancelCurrentOperation();

        Assert.True(
            viewModel.OperationState is CoreServerCreationOperationState.Cancelling
                or CoreServerCreationOperationState.Idle,
            $"取消後應停留在取消中，或在工作流程同步完成取消時直接回到閒置；實際為 {viewModel.OperationState}。");
        // Without a WPF synchronization context the cancellation continuation may finish between
        // two property reads here. The terminal assertions below are the stable contract; the UI
        // itself executes both transitions serially on its dispatcher.
        await operation;

        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsCreating);
        Assert.Equal(CoreServerCreationOperationState.Idle, viewModel.OperationState);
        Assert.Null(viewModel.CreatedServer);
        Assert.Contains("建立已取消", viewModel.StageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposedViewModel_IgnoresLateCatalogCompletionFromOldGeneration()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<CoreServerProduct>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new FakeWorkflow { CoreTask = completion.Task };
        var viewModel = new CoreServerCreationViewModel(workflow);

        var operation = viewModel.InitializeAsync();
        viewModel.Dispose();
        completion.SetResult([Product(CoreServerSoftware.Paper, "paper", "Paper")]);
        await operation;

        Assert.Empty(viewModel.Cores);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task IndeterminateCatalogLoad_DoesNotPresentInitialZeroAsMeasuredProgress()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<CoreServerProduct>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new FakeWorkflow { CoreTask = completion.Task };
        var viewModel = new CoreServerCreationViewModel(workflow);

        var operation = viewModel.InitializeAsync();

        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.IsProgressIndeterminate);
        Assert.False(viewModel.ShowProgressPercentage);

        completion.SetResult([Product(CoreServerSoftware.Paper, "paper", "Paper")]);
        await operation;

        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.ShowProgressPercentage);
    }

    private static async Task<CoreServerCreationViewModel> ReadyForCreationAsync(
        FakeWorkflow workflow,
        string serverName)
    {
        var viewModel = new CoreServerCreationViewModel(workflow);
        await viewModel.InitializeAsync();
        await viewModel.SelectCoreAsync(Assert.Single(viewModel.Cores));
        viewModel.ServerName = serverName;
        if (viewModel.RequiresMinecraftEula)
        {
            viewModel.MinecraftEulaAccepted = true;
        }
        Assert.True(viewModel.CanCreate);
        return viewModel;
    }

    private static CoreServerProduct Product(
        CoreServerSoftware software,
        string id,
        string displayName)
        => new(software, id, displayName, $"{displayName} description");

    private static CoreServerVersion Version(
        CoreServerProduct core,
        string id,
        string minecraftVersion,
        string build)
        => new(core.CoreId, id, id, minecraftVersion, build, DateTimeOffset.UtcNow);

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class FakeWorkflow : ICoreServerCreationWorkflow
    {
        public IReadOnlyList<CoreServerProduct> CoreResults { get; init; } = [];
        public IReadOnlyList<CoreServerVersion> VersionResults { get; init; } = [];
        public Task<IReadOnlyList<CoreServerProduct>>? CoreTask { get; init; }
        public ServerInstance CreatedServer { get; init; } = new();
        public CoreServerCreationProgress? ProgressToReport { get; init; }
        public bool BlockCreateUntilCancelled { get; init; }
        public TaskCompletionSource<bool> CreateStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public CoreServerCreationRequest? LastCreateRequest { get; private set; }

        public Task<IReadOnlyList<CoreServerProduct>> GetAvailableCoresAsync(
            CancellationToken cancellationToken)
            => CoreTask ?? Task.FromResult(CoreResults);

        public Task<IReadOnlyList<CoreServerVersion>> GetVersionsAsync(
            CoreServerProduct core,
            CancellationToken cancellationToken)
            => Task.FromResult(VersionResults);

        public async Task<ServerInstance> CreateAsync(
            CoreServerCreationRequest request,
            IProgress<CoreServerCreationProgress> progress,
            CancellationToken cancellationToken)
        {
            LastCreateRequest = request;
            CreateStarted.TrySetResult(true);
            if (ProgressToReport is not null)
            {
                progress.Report(ProgressToReport);
            }

            if (BlockCreateUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return CreatedServer;
        }
    }
}
