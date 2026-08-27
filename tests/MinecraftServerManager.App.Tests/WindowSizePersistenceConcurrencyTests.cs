using System.IO;
using System.Text.Json;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class WindowSizePersistenceConcurrencyTests
{
    [Fact]
    public async Task FailedGeneralSettingsTransaction_RollsBackBeforeQueuedResizeSnapshot()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        var initialSettings = new ManagerSettings
        {
            Appearance = new ApplicationAppearanceSettings
            {
                WindowColor = "#111111",
            },
            UserInterface = new ManagerUiSettings
            {
                WindowWidth = 1480,
                WindowHeight = 900,
            },
        };
        var store = new ControlledSettingsStore(paths.SettingsFile, initialSettings);
        await using var viewModel = new MainWindowViewModel(
            paths,
            new ServerRemovalConfirmationService(),
            new OnlineModpackWorkflow(paths),
            onlineModpackDialogService: null,
            settingsStore: store);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        store.ArmDelayedFailure();

        var failedGeneralSave = viewModel.PersistGeneralSettingsValuesAsync(
            new ManagerUiSettings
            {
                ThemePresetId = "ashen-jade",
                WindowWidth = 1600,
                WindowHeight = 900,
                FontSize = 14,
            },
            new NewServerDefaultsSettings(),
            new ApplicationAppearanceSettings
            {
                WindowColor = "#222222",
            });
        await store.DelayedSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var queuedResize = viewModel.PersistNormalWindowSizeAsync(900, 600);
        Assert.False(queuedResize.IsCompleted);

        store.ReleaseDelayedSave.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => failedGeneralSave);
        await queuedResize;

        Assert.Equal(2, store.ControlledSnapshots.Count);
        Assert.Equal("#222222", store.ControlledSnapshots[0].Appearance.WindowColor);
        Assert.Equal(1600, store.ControlledSnapshots[0].UserInterface.WindowWidth);
        Assert.Equal("#111111", store.ControlledSnapshots[1].Appearance.WindowColor);
        Assert.Equal(900, store.ControlledSnapshots[1].UserInterface.WindowWidth);
        Assert.Equal(600, store.ControlledSnapshots[1].UserInterface.WindowHeight);
        Assert.Equal("#111111", store.LastSuccessfulSnapshot!.Appearance.WindowColor);
    }

    [Fact]
    public async Task DelayedFailedSave_CannotRollBackOrOverwriteNewerWindowSize()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        var store = new ControlledSettingsStore(paths.SettingsFile, new ManagerSettings());
        await using var viewModel = new MainWindowViewModel(
            paths,
            new ServerRemovalConfirmationService(),
            new OnlineModpackWorkflow(paths),
            onlineModpackDialogService: null,
            settingsStore: store);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        store.ArmDelayedFailure();

        var olderSave = viewModel.PersistNormalWindowSizeAsync(1280, 720);
        await store.DelayedSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newerSave = viewModel.PersistNormalWindowSizeAsync(1600, 900);

        Assert.False(newerSave.IsCompleted);
        store.ReleaseDelayedSave.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => olderSave);
        await newerSave;

        Assert.Equal(2, store.ControlledSnapshots.Count);
        Assert.Equal(1280, store.ControlledSnapshots[0].UserInterface.WindowWidth);
        Assert.Equal(720, store.ControlledSnapshots[0].UserInterface.WindowHeight);
        Assert.Equal(1600, store.ControlledSnapshots[1].UserInterface.WindowWidth);
        Assert.Equal(900, store.ControlledSnapshots[1].UserInterface.WindowHeight);
        Assert.Equal(1600, store.LastSuccessfulSnapshot!.UserInterface.WindowWidth);
        Assert.Equal(900, store.LastSuccessfulSnapshot.UserInterface.WindowHeight);

        // The first asynchronous writer received a detached snapshot. The later in-memory
        // mutation cannot retroactively change what that writer observed.
        Assert.Equal(1280, store.ControlledSnapshots[0].UserInterface.WindowWidth);
    }

    private sealed class ControlledSettingsStore(
        string filePath,
        ManagerSettings initialSettings) : IJsonSettingsStore<ManagerSettings>
    {
        private bool _armed;
        private int _controlledSaveCount;

        public string FilePath { get; } = filePath;

        public TaskCompletionSource DelayedSaveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDelayedSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ManagerSettings> ControlledSnapshots { get; } = [];

        public ManagerSettings? LastSuccessfulSnapshot { get; private set; }

        public Task<ManagerSettings?> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ManagerSettings?>(Clone(initialSettings));
        }

        public async Task SaveAsync(
            ManagerSettings settings,
            CancellationToken cancellationToken = default)
        {
            var snapshot = Clone(settings);
            if (!_armed)
            {
                LastSuccessfulSnapshot = snapshot;
                return;
            }

            var saveNumber = Interlocked.Increment(ref _controlledSaveCount);
            lock (ControlledSnapshots)
            {
                ControlledSnapshots.Add(snapshot);
            }

            if (saveNumber == 1)
            {
                DelayedSaveEntered.TrySetResult();
                await ReleaseDelayedSave.Task.WaitAsync(cancellationToken);
                throw new IOException("Simulated delayed settings write failure.");
            }

            LastSuccessfulSnapshot = snapshot;
        }

        public void ArmDelayedFailure()
        {
            _armed = true;
            LastSuccessfulSnapshot = null;
        }

        private static ManagerSettings Clone(ManagerSettings value)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(value);
            return JsonSerializer.Deserialize<ManagerSettings>(payload)!;
        }
    }
}
