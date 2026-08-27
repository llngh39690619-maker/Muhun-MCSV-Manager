using System.IO;
using System.Windows;
using System.Windows.Media;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class AppearanceSettingsViewModelTests
{
    [Fact]
    public void PatternOptions_AreAvailableThroughTheViewModelBindingSource()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var service = AppearanceThemeServiceTests.CreateService(directory.Path);
        var viewModel = new AppearanceSettingsViewModel(
            service,
            new ResourceDictionary(),
            new ApplicationAppearanceSettings(),
            _ => Task.CompletedTask);

        Assert.Equal(4, viewModel.PatternOptions.Count);
        Assert.Equal(
            [AppearancePattern.None, AppearancePattern.Dots, AppearancePattern.Grid, AppearancePattern.Diagonal],
            viewModel.PatternOptions.Select(option => option.Value));
    }

    [Fact]
    public void PreviewThenCancel_RestoresOpeningThemeWithoutPersisting()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var service = AppearanceThemeServiceTests.CreateService(directory.Path);
        var resources = new ResourceDictionary();
        var initial = new ApplicationAppearanceSettings();
        service.Apply(resources, initial);
        var persistCalls = 0;
        var viewModel = new AppearanceSettingsViewModel(
            service,
            resources,
            initial,
            _ =>
            {
                persistCalls++;
                return Task.CompletedTask;
            });
        viewModel.AccentColor = "#112233";
        viewModel.Pattern = AppearancePattern.Dots;

        Assert.True(viewModel.Preview());
        Assert.Equal(
            Color.FromRgb(17, 34, 51),
            Assert.IsType<SolidColorBrush>(resources[ThemeResourceKeys.AccentBrush]).Color);

        viewModel.Cancel();

        Assert.Equal(
            Color.FromRgb(89, 217, 142),
            Assert.IsType<SolidColorBrush>(resources[ThemeResourceKeys.AccentBrush]).Color);
        Assert.Equal(0, persistCalls);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task SaveAsync_NormalizesPersistsAndAppliesTheme()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var service = AppearanceThemeServiceTests.CreateService(directory.Path);
        var resources = new ResourceDictionary();
        service.Apply(resources, new ApplicationAppearanceSettings());
        ApplicationAppearanceSettings? persisted = null;
        var savedRaised = false;
        var viewModel = new AppearanceSettingsViewModel(
            service,
            resources,
            new ApplicationAppearanceSettings(),
            settings =>
            {
                persisted = settings;
                return Task.CompletedTask;
            });
        viewModel.Saved += (_, _) => savedRaised = true;
        viewModel.WindowColor = "#0a0b0c";
        viewModel.Pattern = AppearancePattern.Diagonal;

        var result = await viewModel.SaveAsync();

        Assert.True(result);
        Assert.True(savedRaised);
        Assert.NotNull(persisted);
        Assert.Equal("#0A0B0C", persisted.WindowColor);
        Assert.Equal(AppearancePattern.Diagonal, persisted.Pattern);
        Assert.Equal(
            Color.FromRgb(10, 11, 12),
            Assert.IsType<SolidColorBrush>(resources[ThemeResourceKeys.WindowBrush]).Color);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void InvalidColor_DisablesPreviewAndExplainsExpectedFormat()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var service = AppearanceThemeServiceTests.CreateService(directory.Path);
        var viewModel = new AppearanceSettingsViewModel(
            service,
            new ResourceDictionary(),
            new ApplicationAppearanceSettings(),
            _ => Task.CompletedTask);

        viewModel.TextColor = "white";

        Assert.False(viewModel.IsValid);
        Assert.True(viewModel.HasValidationError);
        Assert.Contains("#RRGGBB", viewModel.ValidationMessage, StringComparison.Ordinal);
        Assert.False(viewModel.PreviewCommand.CanExecute(null));
        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void ResetToDefaults_PreviewsButDoesNotPersist()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var service = AppearanceThemeServiceTests.CreateService(directory.Path);
        var resources = new ResourceDictionary();
        var current = new ApplicationAppearanceSettings { AccentColor = "#112233" };
        service.Apply(resources, current);
        var persistCalls = 0;
        var viewModel = new AppearanceSettingsViewModel(
            service,
            resources,
            current,
            _ =>
            {
                persistCalls++;
                return Task.CompletedTask;
            });

        viewModel.ResetToDefaults();

        Assert.Equal(ApplicationAppearanceSettings.DefaultAccentColor, viewModel.AccentColor);
        Assert.True(viewModel.IsPreviewApplied);
        Assert.True(viewModel.IsDirty);
        Assert.Equal(0, persistCalls);
    }

    [Fact]
    public void InvalidImageImport_IsContainedAndLeavesCandidateValid()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var service = AppearanceThemeServiceTests.CreateService(directory.Path);
        var fakeImage = Path.Combine(directory.Path, "bad.png");
        File.WriteAllText(fakeImage, "not an image");
        var viewModel = new AppearanceSettingsViewModel(
            service,
            new ResourceDictionary(),
            new ApplicationAppearanceSettings(),
            _ => Task.CompletedTask);

        var imported = viewModel.TryImportBackgroundImage(fakeImage);

        Assert.False(imported);
        Assert.True(viewModel.IsValid);
        Assert.True(viewModel.HasValidationError);
        Assert.False(viewModel.HasBackgroundImage);
        Assert.NotEmpty(viewModel.ValidationMessage);
    }

    [Fact]
    public async Task PersistFailure_LeavesAReversiblePreviewAndDoesNotRaiseSaved()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var service = AppearanceThemeServiceTests.CreateService(directory.Path);
        var resources = new ResourceDictionary();
        var initial = new ApplicationAppearanceSettings();
        service.Apply(resources, initial);
        var savedRaised = false;
        var viewModel = new AppearanceSettingsViewModel(
            service,
            resources,
            initial,
            _ => throw new IOException("disk full"));
        viewModel.Saved += (_, _) => savedRaised = true;
        viewModel.AccentColor = "#112233";

        var result = await viewModel.SaveAsync();

        Assert.False(result);
        Assert.False(savedRaised);
        Assert.True(viewModel.IsPreviewApplied);
        Assert.Equal(
            Color.FromRgb(17, 34, 51),
            Assert.IsType<SolidColorBrush>(resources[ThemeResourceKeys.AccentBrush]).Color);

        viewModel.Cancel();

        Assert.Equal(
            Color.FromRgb(89, 217, 142),
            Assert.IsType<SolidColorBrush>(resources[ThemeResourceKeys.AccentBrush]).Color);
    }

    [Fact]
    public void Cancel_DeletesAnUncommittedManagedBackgroundCopy()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var service = AppearanceThemeServiceTests.CreateService(directory.Path);
        var source = Path.Combine(directory.Path, "source.png");
        AppearanceThemeServiceTests.WriteTinyPng(source);
        var viewModel = new AppearanceSettingsViewModel(
            service,
            new ResourceDictionary(),
            new ApplicationAppearanceSettings(),
            _ => Task.CompletedTask);

        Assert.True(viewModel.TryImportBackgroundImage(source));
        var managed = viewModel.BackgroundImagePath;
        Assert.NotNull(managed);
        Assert.True(File.Exists(managed));

        viewModel.Cancel();

        Assert.False(File.Exists(managed));
        Assert.False(viewModel.HasBackgroundImage);
    }

    [Fact]
    public void Cancel_MissingOriginalManagedBackground_SafelyRestoresOriginalColors()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var service = AppearanceThemeServiceTests.CreateService(directory.Path);
        var source = Path.Combine(directory.Path, "source.png");
        AppearanceThemeServiceTests.WriteTinyPng(source);
        var managed = service.ImportBackgroundImage(source);
        var resources = new ResourceDictionary();
        var initial = new ApplicationAppearanceSettings
        {
            AccentColor = "#112233",
            BackgroundImagePath = managed
        };
        service.Apply(resources, initial);
        var viewModel = new AppearanceSettingsViewModel(
            service,
            resources,
            initial,
            _ => Task.CompletedTask);
        viewModel.AccentColor = "#AABBCC";
        Assert.True(viewModel.Preview());
        File.Delete(managed);

        var exception = Record.Exception(viewModel.Cancel);

        Assert.Null(exception);
        Assert.Equal("#112233", viewModel.AccentColor);
        Assert.False(viewModel.HasBackgroundImage);
        Assert.False(viewModel.IsDirty);
        Assert.Equal(
            Color.FromRgb(17, 34, 51),
            Assert.IsType<SolidColorBrush>(resources[ThemeResourceKeys.AccentBrush]).Color);
        Assert.Same(Brushes.Transparent, resources[ThemeResourceKeys.WindowBackgroundImageBrush]);
    }
}
