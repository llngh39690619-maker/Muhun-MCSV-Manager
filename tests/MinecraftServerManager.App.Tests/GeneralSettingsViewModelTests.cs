using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class GeneralSettingsViewModelTests
{
    [Fact]
    public async Task FormalUpdateStatus_EnablesOnlyTheNextVerifiedOperation()
    {
        var client = new FixedUpdateClient(new ProductUpdateStatus(
            ProductUpdateChannel.Stable,
            ProductUpdatePhase.Available,
            "1.0.0",
            "1.0.0",
            true,
            true,
            "1.1.0",
            1024,
            0,
            DateTimeOffset.UtcNow,
            null,
            null,
            "Signed candidate available."));
        var viewModel = new GeneralSettingsViewModel(
            new ManagerUiSettings(),
            new NewServerDefaultsSettings(),
            static (_, _, _) => Task.CompletedTask,
            updateClient: client);

        await viewModel.RefreshUpdateAsync();

        Assert.Contains("1.1.0", viewModel.UpdateStatusText, StringComparison.Ordinal);
        Assert.True(viewModel.DownloadUpdateCommand.CanExecute(null));
        Assert.False(viewModel.ScheduleUpdateCommand.CanExecute(null));
    }

    [Fact]
    public void FormalUpdateCommands_AreDisabledWithoutWindowsServiceOwnership()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.RefreshUpdateCommand.CanExecute(null));
        Assert.False(viewModel.CheckUpdateCommand.CanExecute(null));
        Assert.False(viewModel.DownloadUpdateCommand.CanExecute(null));
        Assert.False(viewModel.ScheduleUpdateCommand.CanExecute(null));
    }

    [Fact]
    public void ServiceManagementCards_InvokeOnlyTheirExplicitCallbacks()
    {
        var notificationOpened = 0;
        var providersOpened = 0;
        var viewModel = new GeneralSettingsViewModel(
            new ManagerUiSettings(),
            new NewServerDefaultsSettings(),
            static (_, _, _) => Task.CompletedTask,
            openNotificationSettings: () => notificationOpened++,
            openProviderManagement: () => providersOpened++);

        Assert.True(viewModel.OpenNotificationSettingsCommand.CanExecute(null));
        Assert.True(viewModel.OpenProviderManagementCommand.CanExecute(null));
        viewModel.OpenNotificationSettingsCommand.Execute(null);
        viewModel.OpenProviderManagementCommand.Execute(null);

        Assert.Equal(1, notificationOpened);
        Assert.Equal(1, providersOpened);
    }

    [Fact]
    public void Themes_ExposeFourCuratedPresetsIncludingBlackGold()
    {
        var viewModel = CreateViewModel();

        Assert.Collection(
            viewModel.Themes,
            theme => Assert.Equal("ashen-jade", theme.Id),
            theme => Assert.Equal("black-gold-embers", theme.Id),
            theme => Assert.Equal("ashen-steel", theme.Id),
            theme => Assert.Equal("blood-moon", theme.Id));
        Assert.Equal(4, viewModel.Themes.Select(theme => theme.Id).Distinct().Count());

        var blackGold = Assert.Single(
            viewModel.Themes,
            theme => theme.Id == "black-gold-embers");
        Assert.Equal("黑金餘燼", blackGold.DisplayName);
        Assert.Equal("#090806", blackGold.Appearance.WindowColor);
        Assert.Equal("#D4AF37", blackGold.Appearance.AccentColor);
        Assert.Equal("#F4ECD8", blackGold.Appearance.TextColor);
    }

    [Fact]
    public void CultureChange_RefreshesThemeOptionsMemoryValidationAndUpdateStatusInPlace()
    {
        LocalizationService.Current.SetCulture("zh-TW");
        var viewModel = CreateViewModel();
        viewModel.SelectedTheme = viewModel.Themes.Single(theme =>
            theme.Id == "black-gold-embers");
        viewModel.WindowWidth = 1000;

        Assert.Equal("黑金餘燼", viewModel.SelectedTheme.DisplayName);
        Assert.Contains("系統可用記憶體", viewModel.SystemMemoryDisplay, StringComparison.Ordinal);
        Assert.Contains("視窗寬度", viewModel.ValidationMessage, StringComparison.Ordinal);

        try
        {
            LocalizationService.Current.SetCulture("en-US");

            Assert.Equal("black-gold-embers", viewModel.SelectedTheme.Id);
            Assert.Equal("Black-Gold Embers", viewModel.SelectedTheme.DisplayName);
            Assert.Contains("Available system memory", viewModel.SystemMemoryDisplay, StringComparison.Ordinal);
            Assert.Contains("Window width", viewModel.ValidationMessage, StringComparison.Ordinal);
            Assert.Contains("Windows Service", viewModel.UpdateStatusText, StringComparison.Ordinal);
            Assert.Equal("Custom", viewModel.WindowSizeOptions.Single(choice => choice.IsCustom).DisplayName);
            Assert.Equal("Stable", viewModel.UpdateChannelOptions.Single(
                option => option.Channel == ProductUpdateChannel.Stable).DisplayName);
        }
        finally
        {
            LocalizationService.Current.SetCulture("zh-TW");
        }
    }

    [Theory]
    [InlineData("width", 1119, true)]
    [InlineData("width", 1120, false)]
    [InlineData("width", 7680, false)]
    [InlineData("width", 7681, true)]
    [InlineData("height", 699, true)]
    [InlineData("height", 700, false)]
    [InlineData("height", 4320, false)]
    [InlineData("height", 4321, true)]
    [InlineData("font", 10.9, true)]
    [InlineData("font", 11, false)]
    [InlineData("font", 20, false)]
    [InlineData("font", 20.1, true)]
    public void WindowAndFontBoundaries_AreValidated(
        string property,
        double value,
        bool expectedError)
    {
        var viewModel = CreateViewModel();

        switch (property)
        {
            case "width":
                viewModel.WindowWidth = value;
                break;
            case "height":
                viewModel.WindowHeight = value;
                break;
            case "font":
                viewModel.FontSize = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(property));
        }

        Assert.Equal(expectedError, viewModel.HasValidationError);
    }

    [Fact]
    public void GlobalMemoryDefaults_AreOneFixedRangeAndSnapTo256Mb()
    {
        var viewModel = CreateViewModel(defaultMemoryMode: MemoryAllocationMode.Automatic);

        viewModel.DefaultMinimumMemoryMb = 3333;

        Assert.Equal(3328, viewModel.DefaultMinimumMemoryMb);
        viewModel.DefaultMaximumMemoryMb = 5001;

        Assert.Equal(5120, viewModel.DefaultMaximumMemoryMb);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.DoesNotContain(
            typeof(GeneralSettingsViewModel).GetProperties(),
            property => property.Name is "DefaultMemoryMode" or "DefaultMemoryModes");
    }

    [Fact]
    public void DisplayEdits_PreviewThemeFontAndWindowImmediately_AndCanRestoreBaseline()
    {
        var previews = new List<GeneralSettingsPreview>();
        var restoreCount = 0;
        var viewModel = new GeneralSettingsViewModel(
            new ManagerUiSettings(),
            new NewServerDefaultsSettings(),
            static (_, _, _) => Task.CompletedTask,
            new FixedMemoryProbe(),
            previews.Add,
            () => restoreCount++);
        Assert.False(viewModel.HasUnsavedChanges);

        viewModel.SelectedTheme = viewModel.Themes.Single(
            theme => theme.Id == "black-gold-embers");
        Assert.False(previews[^1].ResizeMainWindow);
        viewModel.SelectedWindowSize = GeneralSettingsViewModel.WindowSizes.Single(
            choice => choice.DisplayName == "1280 × 720");
        var sizePreview = previews[^1];
        Assert.True(sizePreview.ResizeMainWindow);
        Assert.Equal(1280, sizePreview.UserInterface.WindowWidth);
        Assert.Equal(720, sizePreview.UserInterface.WindowHeight);
        viewModel.FontSize = 15.5;

        var preview = Assert.IsType<GeneralSettingsPreview>(previews[^1]);
        Assert.Equal("black-gold-embers", preview.UserInterface.ThemePresetId);
        Assert.Equal(1280, preview.UserInterface.WindowWidth);
        Assert.Equal(720, preview.UserInterface.WindowHeight);
        Assert.Equal(15.5, preview.UserInterface.FontSize);
        Assert.Equal("#090806", preview.Appearance.WindowColor);
        Assert.Equal("#D4AF37", preview.Appearance.AccentColor);
        Assert.False(preview.ResizeMainWindow);
        Assert.True(viewModel.HasUnsavedChanges);

        viewModel.RestorePreview();

        Assert.Equal(1, restoreCount);
    }

    [Fact]
    public void InvalidIntermediateCustomSize_DoesNotResizeMainWindow()
    {
        GeneralSettingsPreview? preview = null;
        var viewModel = new GeneralSettingsViewModel(
            new ManagerUiSettings(),
            new NewServerDefaultsSettings(),
            static (_, _, _) => Task.CompletedTask,
            new FixedMemoryProbe(),
            value => preview = value);
        viewModel.SelectedWindowSize = GeneralSettingsViewModel.WindowSizes.Single(
            choice => choice.IsCustom);

        viewModel.WindowWidth = 1;

        Assert.NotNull(preview);
        Assert.False(preview.ResizeMainWindow);
        Assert.True(viewModel.HasValidationError);
    }

    [Fact]
    public async Task SaveCommand_ProducesIndependentUiThemeAndFutureServerDefaults()
    {
        var existingDefaults = new NewServerDefaultsSettings
        {
            MemoryMode = MemoryAllocationMode.Automatic,
            MinimumMemoryMb = 2048,
            MaximumMemoryMb = 4096,
        };
        var completion = new TaskCompletionSource<SavedSettings>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new GeneralSettingsViewModel(
            new ManagerUiSettings(),
            existingDefaults,
            (ui, defaults, appearance) =>
            {
                completion.TrySetResult(new SavedSettings(ui, defaults, appearance));
                return Task.CompletedTask;
            });
        viewModel.SelectedTheme = viewModel.Themes.Single(
            theme => theme.Id == "black-gold-embers");
        viewModel.SelectedWindowSize = GeneralSettingsViewModel.WindowSizes.Single(
            choice => choice.IsCustom);
        viewModel.WindowWidth = 1444.4;
        viewModel.WindowHeight = 811.6;
        viewModel.FontSize = 15.5;
        viewModel.DefaultMinimumMemoryMb = 3072;
        viewModel.DefaultMaximumMemoryMb = 7168;
        viewModel.SeparateDiagnosticOutput = false;
        viewModel.AutoRestart = true;
        viewModel.EnableHangWatchdog = true;
        viewModel.EnableAutomaticRecoveryPoints = true;

        viewModel.ApplyCommand.Execute(null);
        var saved = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("black-gold-embers", saved.Ui.ThemePresetId);
        Assert.Equal(1444, saved.Ui.WindowWidth);
        Assert.Equal(812, saved.Ui.WindowHeight);
        Assert.Equal(15.5, saved.Ui.FontSize);
        Assert.Equal(MemoryAllocationMode.Manual, saved.Defaults.MemoryMode);
        Assert.Equal(3072, saved.Defaults.MinimumMemoryMb);
        Assert.Equal(7168, saved.Defaults.MaximumMemoryMb);
        Assert.False(saved.Defaults.SeparateDiagnosticOutput);
        Assert.True(saved.Defaults.AutoRestart);
        Assert.True(saved.Defaults.EnableHangWatchdog);
        Assert.True(saved.Defaults.EnableAutomaticRecoveryPoints);
        Assert.Equal("#D4AF37", saved.Appearance.AccentColor);
        Assert.NotSame(viewModel.SelectedTheme.Appearance, saved.Appearance);

        Assert.Equal(MemoryAllocationMode.Automatic, existingDefaults.MemoryMode);
        Assert.Equal(2048, existingDefaults.MinimumMemoryMb);
        Assert.Equal(4096, existingDefaults.MaximumMemoryMb);
    }

    private static GeneralSettingsViewModel CreateViewModel(
        MemoryAllocationMode defaultMemoryMode = MemoryAllocationMode.Automatic)
        => new(
            new ManagerUiSettings(),
            new NewServerDefaultsSettings { MemoryMode = defaultMemoryMode },
            static (_, _, _) => Task.CompletedTask,
            new FixedMemoryProbe());

    private sealed class FixedMemoryProbe : ISystemMemoryProbe
    {
        private const long Gibibyte = 1024L * 1024L * 1024L;

        public SystemMemorySnapshot GetSnapshot() => new(
            64 * Gibibyte,
            48 * Gibibyte);
    }

    private sealed class FixedUpdateClient(ProductUpdateStatus status) : IProductUpdateClient
    {
        public Task<ProductUpdateStatus> GetUpdateStatusAsync(
            ProductUpdateChannel channel,
            CancellationToken cancellationToken = default)
            => Task.FromResult(status with { Channel = channel });

        public Task<ProductUpdateOperationResult> CheckForUpdateAsync(
            ProductUpdateChannel channel,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ProductUpdateOperationResult(true, status with { Channel = channel }));

        public Task<ProductUpdateOperationResult> DownloadUpdateAsync(
            ProductUpdateChannel channel,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ProductUpdateOperationResult(true, status with { Channel = channel }));

        public Task<ProductUpdateOperationResult> ScheduleUpdateAsync(
            ProductUpdateChannel channel,
            DateTimeOffset? notBeforeUtc = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ProductUpdateOperationResult(true, status with { Channel = channel }));
    }

    private sealed record SavedSettings(
        ManagerUiSettings Ui,
        NewServerDefaultsSettings Defaults,
        ApplicationAppearanceSettings Appearance);
}
