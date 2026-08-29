using System.IO;
using System.Runtime.CompilerServices;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientInstanceSettingsEditorViewModelTests
{
    [Fact]
    public void Editor_TracksDirtyStateAndBuildsAllEditableFlags()
    {
        var instanceId = Guid.NewGuid();
        var editor = new ClientInstanceSettingsEditorViewModel(instanceId, CreateSettings());

        Assert.False(editor.IsDirty);
        Assert.False(editor.HasErrors);
        Assert.False(editor.CanSave);

        editor.Name = "Updated client";
        editor.EnableDedicatedGpu = false;
        editor.EnableDiscordPresence = false;

        Assert.True(editor.IsDirty);
        Assert.True(editor.CanSave);
        var update = editor.BuildUpdate();
        Assert.Equal("Updated client", update.Name);
        Assert.False(update.EnableDedicatedGpu);
        Assert.False(update.EnableDiscordPresence);

        editor.Name = "Minecraft test";
        editor.EnableDedicatedGpu = true;
        editor.EnableDiscordPresence = true;
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Editor_KeepsInvalidNumericTextVisibleAndBlocksSaveUntilCorrected()
    {
        var editor = new ClientInstanceSettingsEditorViewModel(Guid.NewGuid(), CreateSettings());

        editor.WindowWidthText = "not-a-number";

        Assert.True(editor.IsDirty);
        Assert.True(editor.HasErrors);
        Assert.False(editor.CanSave);
        Assert.Contains("寬度", editor.ResolutionError, StringComparison.Ordinal);
        Assert.NotEmpty(editor.GetErrors(nameof(editor.WindowWidthText)).Cast<string>());
        Assert.Throws<InvalidOperationException>(() => editor.BuildUpdate());

        editor.WindowWidthText = "1920";
        editor.MinimumMemoryMb = 8192;
        editor.MaximumMemoryMb = 4096;
        Assert.Contains("不可小於", editor.MemoryError, StringComparison.Ordinal);

        editor.MaximumMemoryMb = 8192;
        Assert.False(editor.HasErrors);
        Assert.Equal(1920, editor.BuildUpdate().WindowWidth);
    }

    [Fact]
    public void ResolutionSelection_WritesWidthAndHeightTogether()
    {
        var editor = new ClientInstanceSettingsEditorViewModel(Guid.NewGuid(), CreateSettings());
        var selected = editor.ResolutionChoices.Single(choice =>
            choice.Width == 1920 && choice.Height == 1080);

        editor.SelectedResolution = selected;

        Assert.Equal(1920, editor.WindowWidth);
        Assert.Equal(1080, editor.WindowHeight);
        Assert.Equal(selected, editor.SelectedResolution);
        var update = editor.BuildUpdate();
        Assert.Equal(1920, update.WindowWidth);
        Assert.Equal(1080, update.WindowHeight);
    }

    [Fact]
    public void ResolutionSelection_PreservesAValidNonPresetValue()
    {
        var settings = CreateSettings() with
        {
            WindowWidth = 1234,
            WindowHeight = 777,
        };
        var editor = new ClientInstanceSettingsEditorViewModel(Guid.NewGuid(), settings);

        Assert.Contains(editor.ResolutionChoices, choice =>
            choice.Width == 1234 && choice.Height == 777);
        Assert.Equal("1234 × 777", editor.SelectedResolution?.DisplayName);
        Assert.False(editor.IsDirty);
        Assert.Equal(1234, editor.BuildUpdate().WindowWidth);
        Assert.Equal(777, editor.BuildUpdate().WindowHeight);
    }

    [Fact]
    public void Editor_ExplainsInvalidNamesAndManagedJvmMemoryArguments()
    {
        var editor = new ClientInstanceSettingsEditorViewModel(Guid.NewGuid(), CreateSettings());

        editor.Name = " \r\n ";
        editor.JvmArgumentsText = "-Xmx8G";

        Assert.True(editor.HasErrors);
        Assert.Contains("1–128", editor.NameError, StringComparison.Ordinal);
        Assert.Contains("記憶體參數", editor.JvmArgumentsError, StringComparison.Ordinal);
        Assert.Contains(editor.NameError, editor.ValidationSummary, StringComparison.Ordinal);
        Assert.Contains(editor.JvmArgumentsError, editor.ValidationSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_InitialAutomaticResolutionUpdatesTheDisplayedRangeWithoutBecomingDirty()
    {
        var resolvedModes = new List<MinecraftClientMemoryMode>();
        var editor = new ClientInstanceSettingsEditorViewModel(
            Guid.NewGuid(),
            CreateSettings(),
            mode =>
            {
                resolvedModes.Add(mode);
                return new ClientMemoryRangePreview(3072, 6144);
            });

        Assert.Equal([MinecraftClientMemoryMode.Automatic], resolvedModes);
        Assert.Equal(MinecraftClientMemoryMode.Automatic, editor.MemoryMode);
        Assert.Equal(3072, editor.MinimumMemoryMb);
        Assert.Equal(6144, editor.MaximumMemoryMb);
        Assert.False(editor.IsDirty);

        var update = editor.BuildUpdate();
        Assert.Equal(MinecraftClientMemoryMode.Automatic, update.MemoryMode);
        Assert.Equal(3072, update.MinimumMemoryMb);
        Assert.Equal(6144, update.MaximumMemoryMb);
    }

    [Fact]
    public void Editor_ModeSelectionRefreshesEffectiveRangeAndUserMemoryEditsPersistAsManual()
    {
        var settings = CreateSettings() with
        {
            MemoryMode = MinecraftClientMemoryMode.Manual,
            MinimumMemoryMb = 2048,
            MaximumMemoryMb = 4096,
        };
        var editor = new ClientInstanceSettingsEditorViewModel(
            Guid.NewGuid(),
            settings,
            mode => mode switch
            {
                MinecraftClientMemoryMode.UseGlobalDefault => new ClientMemoryRangePreview(1536, 3584),
                MinecraftClientMemoryMode.Automatic => new ClientMemoryRangePreview(4096, 8192),
                _ => throw new InvalidOperationException("Manual mode must not invoke the resolver."),
            });

        editor.SelectedMemoryMode = editor.MemoryModes.Single(choice =>
            choice.Mode == MinecraftClientMemoryMode.UseGlobalDefault);
        Assert.Equal(MinecraftClientMemoryMode.UseGlobalDefault, editor.MemoryMode);
        Assert.Equal(1536, editor.MinimumMemoryMb);
        Assert.Equal(3584, editor.MaximumMemoryMb);

        editor.MinimumMemoryMb = 1792;
        Assert.Equal(MinecraftClientMemoryMode.Manual, editor.MemoryMode);

        editor.SelectedMemoryMode = editor.MemoryModes.Single(choice =>
            choice.Mode == MinecraftClientMemoryMode.Automatic);
        Assert.Equal(MinecraftClientMemoryMode.Automatic, editor.MemoryMode);
        Assert.Equal(4096, editor.MinimumMemoryMb);
        Assert.Equal(8192, editor.MaximumMemoryMb);

        editor.MaximumMemoryMb = 7168;
        var update = editor.BuildUpdate();
        Assert.Equal(MinecraftClientMemoryMode.Manual, update.MemoryMode);
        Assert.Equal(4096, update.MinimumMemoryMb);
        Assert.Equal(7168, update.MaximumMemoryMb);
    }

    [Fact]
    public void CreatePage_MemoryModeButtonsExposeTheirSelectedState()
    {
        var xaml = File.ReadAllText(GetAppSourcePath(Path.Combine("Views", "ClientWorkspaceView.xaml")));

        Assert.Contains("ClientMemoryModeButton", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"{Binding IsGlobalMemory}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"{Binding IsAutomaticMemory}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"{Binding IsManualMemory}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding Tag, RelativeSource={RelativeSource Self}}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{DynamicResource AccentDarkBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{DynamicResource AccentBrush}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_UsesAnInlineDarkUnsavedChangesPromptWithExplicitChoices()
    {
        var xaml = File.ReadAllText(GetAppSourcePath(Path.Combine("Views", "ClientWorkspaceView.xaml")));
        var viewModel = File.ReadAllText(GetAppSourcePath(Path.Combine("ViewModels", "ClientWorkspaceViewModel.cs")));

        Assert.Contains("IsClientSettingsClosePromptOpen", xaml, StringComparison.Ordinal);
        Assert.Contains("L10n.client.unsaved.heading", xaml, StringComparison.Ordinal);
        Assert.Contains("L10n.client.unsaved.save", xaml, StringComparison.Ordinal);
        Assert.Contains("L10n.client.unsaved.discard", xaml, StringComparison.Ordinal);
        Assert.Contains("CancelClientSettingsCloseCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("L10n.client.settings.discordPresence", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsChecked=\"{Binding EnableDiscordPresence}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("if (SettingsEditor?.IsDirty == true)", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox", ExtractCloseMethod(viewModel), StringComparison.Ordinal);
    }

    [Fact]
    public void DiscordPresence_RemainsSchemaCompatibleButDefaultsOffAndIsNotAdvertised()
    {
        var instance = new MinecraftClientInstance();
        var defaults = new NewMinecraftClientDefaultsSettings();
        var request = new MinecraftClientInstanceSettingsUpdate();
        var globalSettingsXaml = File.ReadAllText(
            TestRepositoryPaths.AppSource("Dialogs", "GeneralSettingsDialog.xaml"));

        Assert.False(instance.EnableDiscordPresence);
        Assert.False(defaults.EnableDiscordPresence);
        Assert.False(request.EnableDiscordPresence);
        Assert.DoesNotContain("L10n.settings.clientDiscordPresence", globalSettingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientEnableDiscordPresence", globalSettingsXaml, StringComparison.Ordinal);
    }

    private static MinecraftClientInstanceSettingsUpdate CreateSettings() => new()
    {
        Name = "Minecraft test",
        WindowWidth = 1280,
        WindowHeight = 720,
        MemoryMode = MinecraftClientMemoryMode.Automatic,
        MinimumMemoryMb = 2048,
        MaximumMemoryMb = 4096,
        EnableDedicatedGpu = true,
        EnableDiscordPresence = true,
        JvmArguments = ["-XX:+UseG1GC"],
    };

    private static string ExtractCloseMethod(string source)
    {
        const string signature = "private void CloseClientSettings()";
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        return source[start..(end < 0 ? source.Length : end)];
    }

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);
}
