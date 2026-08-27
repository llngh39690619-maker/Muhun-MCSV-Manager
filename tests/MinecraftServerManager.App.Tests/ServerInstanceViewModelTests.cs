using System.IO;
using System.Collections.Specialized;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class ServerInstanceViewModelTests
{
    [Theory]
    [InlineData(MemoryAllocationMode.Legacy, false, false, true)]
    [InlineData(MemoryAllocationMode.UseManagerDefault, true, false, false)]
    [InlineData(MemoryAllocationMode.Automatic, false, true, false)]
    [InlineData(MemoryAllocationMode.Manual, false, false, true)]
    public void MemoryAllocationMode_ProjectsLegacyDefaultAutomaticAndManualSelectors(
        MemoryAllocationMode mode,
        bool expectedDefault,
        bool expectedAutomatic,
        bool expectedManual)
    {
        var model = CreateModel();
        model.MemoryAllocationMode = mode;
        var viewModel = CreateViewModel(model);

        Assert.Equal(mode, viewModel.MemoryAllocationMode);
        Assert.Equal(expectedDefault, viewModel.IsMemoryUsingDefault);
        Assert.Equal(expectedAutomatic, viewModel.IsMemoryAutomatic);
        Assert.Equal(expectedManual, viewModel.IsMemoryManual);
    }

    [Fact]
    public void MemoryModeSelectors_WriteExplicitNonLegacyModes()
    {
        var model = CreateModel();
        model.MemoryAllocationMode = MemoryAllocationMode.Legacy;
        var viewModel = CreateViewModel(model);

        viewModel.IsMemoryUsingDefault = true;
        Assert.Equal(MemoryAllocationMode.UseManagerDefault, model.MemoryAllocationMode);
        viewModel.IsMemoryAutomatic = true;
        Assert.Equal(MemoryAllocationMode.Automatic, model.MemoryAllocationMode);
        viewModel.IsMemoryManual = true;
        Assert.Equal(MemoryAllocationMode.Manual, model.MemoryAllocationMode);
    }

    [Fact]
    public void MovingEitherMemorySlider_SnapsValuesAndSwitchesToManual()
    {
        var model = CreateModel();
        model.MemoryAllocationMode = MemoryAllocationMode.Automatic;
        model.MinimumMemoryMb = 2048;
        model.MaximumMemoryMb = 4096;
        var requestedModes = new List<MemoryAllocationMode>();
        var viewModel = CreateViewModel(
            model,
            (_, mode) => requestedModes.Add(mode));

        viewModel.MinimumMemorySliderMb = 3333;

        Assert.Equal(3328, model.MinimumMemoryMb);
        Assert.Equal(MemoryAllocationMode.Manual, model.MemoryAllocationMode);
        Assert.Equal([MemoryAllocationMode.Manual], requestedModes);

        viewModel.MemoryAllocationMode = MemoryAllocationMode.UseManagerDefault;
        viewModel.MaximumMemorySliderMb = 5001;

        Assert.Equal(5120, model.MaximumMemoryMb);
        Assert.Equal(MemoryAllocationMode.Manual, model.MemoryAllocationMode);
        Assert.Equal(
            [
                MemoryAllocationMode.Manual,
                MemoryAllocationMode.UseManagerDefault,
                MemoryAllocationMode.Manual,
            ],
            requestedModes);
    }

    [Fact]
    public void SelectingOrReclickingAutomatic_RequestsImmediateBackgroundRecommendation()
    {
        var requestedModes = new List<MemoryAllocationMode>();
        var viewModel = CreateViewModel(
            memoryModeRequested: (_, mode) => requestedModes.Add(mode));

        viewModel.IsMemoryAutomatic = true;
        viewModel.RecalculateAutomaticMemoryCommand.Execute(null);

        Assert.Equal(MemoryAllocationMode.Automatic, viewModel.MemoryAllocationMode);
        Assert.Equal(
            [MemoryAllocationMode.Automatic, MemoryAllocationMode.Automatic],
            requestedModes);
    }

    [Fact]
    public void AutomaticRecommendation_UpdatesNumbersWithoutSwitchingToManual()
    {
        var model = CreateModel();
        model.MemoryAllocationMode = MemoryAllocationMode.Automatic;
        model.MinimumMemoryMb = 512;
        model.MaximumMemoryMb = 1024;
        var viewModel = CreateViewModel(model);
        var recommendation = new MemoryRecommendation(
            MinimumMemoryMb: 3072,
            MaximumMemoryMb: 6144,
            AddonJarCount: 51,
            AddonJarBytes: 123456,
            ReservedSystemMemoryMb: 4096,
            SafeAllocationCeilingMb: 8192,
            WasConstrainedBySystemMemory: false,
            Explanation: "偵測到 51 個頂層模組/插件 JAR。");

        viewModel.BeginAutomaticMemoryRecommendation();
        Assert.True(viewModel.IsAutomaticMemoryRecommendationRunning);
        Assert.Contains("正在背景掃描", viewModel.MemoryConfigurationHint, StringComparison.Ordinal);

        viewModel.ApplyAutomaticMemoryRecommendation(recommendation);

        Assert.False(viewModel.IsAutomaticMemoryRecommendationRunning);
        Assert.Equal(MemoryAllocationMode.Automatic, model.MemoryAllocationMode);
        Assert.Equal(3072, model.MinimumMemoryMb);
        Assert.Equal(6144, model.MaximumMemoryMb);
        Assert.Equal(8192, viewModel.MemorySliderMaximumMb);
        Assert.Contains("偵測到 51 個", viewModel.MemoryConfigurationHint, StringComparison.Ordinal);
        Assert.Contains("3,072–6,144 MB", viewModel.MemoryConfigurationHint, StringComparison.Ordinal);
    }

    [Fact]
    public void CultureChange_RefreshesVisibleServerStatusAndTokenizedMemoryTextInPlace()
    {
        LocalizationService.Current.SetCulture("zh-TW");
        var model = CreateModel();
        model.CoreType = CoreType.Unknown;
        model.MinecraftVersion = string.Empty;
        model.MemoryAllocationMode = MemoryAllocationMode.Automatic;
        model.ModpackSource = ModpackSourceKind.None;
        var viewModel = CreateViewModel(model);
        viewModel.SetState(ServerState.Running);
        viewModel.UpdateMetrics(1, 1024, TimeSpan.FromDays(2) + TimeSpan.FromHours(3));
        viewModel.SetSystemMemoryDisplay(8L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024);
        viewModel.BeginAutomaticMemoryRecommendation();

        Assert.Equal("執行中", viewModel.StateText);
        Assert.Contains("正在背景掃描", viewModel.MemoryConfigurationHint, StringComparison.Ordinal);

        try
        {
            LocalizationService.Current.SetCulture("en-US");

            Assert.Equal("Running", viewModel.StateText);
            Assert.Equal("Custom", viewModel.CoreTypeText);
            Assert.Equal("Version unknown", viewModel.MinecraftVersionDisplay);
            Assert.Contains("Performance notice", viewModel.OneDrivePerformanceWarning, StringComparison.Ordinal);
            Assert.Contains("Currently available system memory", viewModel.SystemMemoryDisplay, StringComparison.Ordinal);
            Assert.Contains("Scanning top-level", viewModel.MemoryConfigurationHint, StringComparison.Ordinal);
            Assert.Equal("No players are online", viewModel.EmptyPlayerListText);
            Assert.Equal("No online modpack source linked", viewModel.ModpackSourceDisplay);
            Assert.Equal("2d 03:00", viewModel.UptimeDisplay);
        }
        finally
        {
            LocalizationService.Current.SetCulture("zh-TW");
        }
    }

    [Fact]
    public void BackgroundOpacity_ClampsZeroToOneAndRoundTripsPercent()
    {
        var model = CreateModel();
        var viewModel = CreateViewModel(model);

        viewModel.BackgroundImageOpacity = -1;
        Assert.Equal(0, model.BackgroundImageOpacity);
        Assert.Equal(0, viewModel.BackgroundImageOpacityPercent);

        viewModel.BackgroundImageOpacityPercent = 150;
        Assert.Equal(1, model.BackgroundImageOpacity);
        Assert.Equal(100, viewModel.BackgroundImageOpacityPercent);

        viewModel.BackgroundImageOpacityPercent = 42.5;
        Assert.Equal(0.425, model.BackgroundImageOpacity, precision: 3);
        Assert.Equal(42.5, viewModel.BackgroundImageOpacityPercent, precision: 3);

        viewModel.BackgroundImageOpacity = double.NaN;
        Assert.Equal(0.25, model.BackgroundImageOpacity, precision: 3);
    }

    [Fact]
    public void IconPath_IsIndependentAndHasNoOpacitySetting()
    {
        var model = CreateModel();
        model.BackgroundImageOpacity = 0.42;
        var viewModel = CreateViewModel(model);

        viewModel.IconImagePath = "themes/icons/custom.png";

        Assert.Equal("themes/icons/custom.png", model.IconImagePath);
        Assert.Equal(0.42, model.BackgroundImageOpacity, precision: 3);
        Assert.DoesNotContain(
            typeof(ServerInstanceViewModel).GetProperties(),
            property => property.Name.Contains("Icon", StringComparison.OrdinalIgnoreCase)
                        && property.Name.Contains("Opacity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EffectiveIcon_UserOverrideWinsThenFallsBackToCatalogAndCoreInitial()
    {
        var model = CreateModel();
        model.IconImagePath = "themes/icons/user.png";
        model.CatalogIconImagePath = "cache/modpack-artwork/icons/catalog.png";
        model.CatalogPreviewImagePath = "cache/modpack-artwork/previews/catalog.png";
        model.ModpackProviderId = "modrinth";
        var viewModel = CreateViewModel(model);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal(model.IconImagePath, viewModel.EffectiveIconImagePath);
        Assert.Equal(model.CatalogPreviewImagePath, viewModel.CatalogPreviewImagePath);
        Assert.Equal("modrinth", viewModel.ModpackProviderId);

        viewModel.IconImagePath = null;

        Assert.Equal(model.CatalogIconImagePath, viewModel.EffectiveIconImagePath);
        Assert.Contains(nameof(ServerInstanceViewModel.EffectiveIconImagePath), changed);

        changed.Clear();
        viewModel.CatalogIconImagePath = null;

        Assert.Null(viewModel.EffectiveIconImagePath);
        Assert.Contains(nameof(ServerInstanceViewModel.EffectiveIconImagePath), changed);
        Assert.Equal("P", viewModel.CoreInitial);
    }

    [Fact]
    public void EditingConfiguredPort_DoesNotChangeTheRunningSessionPort()
    {
        var viewModel = CreateViewModel();
        viewModel.MarkPortAsActive(25565);

        viewModel.Port = 25566;

        Assert.Equal(25566, viewModel.Port);
        Assert.Equal(25565, viewModel.ActivePort);
    }

    [Theory]
    [InlineData(ServerState.Starting)]
    [InlineData(ServerState.Running)]
    [InlineData(ServerState.Stopping)]
    public void NonTerminalState_PreservesTheSessionPort(ServerState state)
    {
        var viewModel = CreateViewModel();
        viewModel.MarkPortAsActive(25565);

        viewModel.SetState(state);

        Assert.Equal(25565, viewModel.ActivePort);
    }

    [Theory]
    [InlineData(ServerState.Stopped)]
    [InlineData(ServerState.Crashed)]
    [InlineData(ServerState.Faulted)]
    public void TerminalState_ReleasesTheSessionPort(ServerState state)
    {
        var viewModel = CreateViewModel();
        viewModel.MarkPortAsActive(25565);

        viewModel.SetState(state);

        Assert.Null(viewModel.ActivePort);
    }

    [Fact]
    public void EventOnlyPlayer_IsRemovedFromAllCollectionsWhenLeaving()
    {
        var viewModel = CreateViewModel();

        viewModel.UpdatePlayerPresence("TransientUser", isOnline: true);

        Assert.Single(viewModel.Players);
        Assert.Single(viewModel.VisiblePlayers);
        Assert.True(viewModel.Players[0].IsOnline);

        viewModel.UpdatePlayerPresence("TransientUser", isOnline: false);

        Assert.Empty(viewModel.Players);
        Assert.Empty(viewModel.VisiblePlayers);
        Assert.Equal("0 位線上", viewModel.PlayerSummary);
    }

    [Fact]
    public void RegistryPlayer_RemainsKnownButHiddenAfterLeaving()
    {
        var viewModel = CreateViewModel();
        viewModel.ReplacePlayers([
            new PlayerStatusRecord("KnownUser", "uuid", false, true, false, false)
        ]);

        viewModel.UpdatePlayerPresence("KnownUser", isOnline: true);
        viewModel.UpdatePlayerPresence("KnownUser", isOnline: false);

        var known = Assert.Single(viewModel.Players);
        Assert.False(known.IsOnline);
        Assert.Empty(viewModel.VisiblePlayers);

        viewModel.ShowKnownPlayers = true;
        Assert.Same(known, Assert.Single(viewModel.VisiblePlayers));
    }

    [Fact]
    public void DuplicatePresenceEvents_AreIdempotent()
    {
        var viewModel = CreateViewModel();

        viewModel.UpdatePlayerPresence("RepeatUser", isOnline: true);
        viewModel.UpdatePlayerPresence("RepeatUser", isOnline: true);

        Assert.Single(viewModel.Players);
        Assert.Single(viewModel.VisiblePlayers);

        viewModel.UpdatePlayerPresence("RepeatUser", isOnline: false);
        viewModel.UpdatePlayerPresence("RepeatUser", isOnline: false);

        Assert.Empty(viewModel.Players);
        Assert.Empty(viewModel.VisiblePlayers);
    }

    [Fact]
    public void SessionReset_RemovesEventOnlyRowsButKeepsRegistryPlayers()
    {
        var viewModel = CreateViewModel();
        viewModel.ReplacePlayers([
            new PlayerStatusRecord("KnownUser", "uuid", false, false, false, false)
        ]);
        viewModel.UpdatePlayerPresence("KnownUser", isOnline: true);
        viewModel.UpdatePlayerPresence("TransientUser", isOnline: true);

        viewModel.UpdateOnlinePlayers([]);

        var known = Assert.Single(viewModel.Players);
        Assert.Equal("KnownUser", known.Name);
        Assert.False(known.IsOnline);
        Assert.Empty(viewModel.VisiblePlayers);
    }

    [Fact]
    public void EventOnlyRoster_IsBoundedAndFullyClearsAfterLeaves()
    {
        var viewModel = CreateViewModel();
        var names = Enumerable.Range(0, 5_000)
            .Select(index => $"P{index:D4}")
            .ToArray();

        foreach (var name in names)
        {
            viewModel.UpdatePlayerPresence(name, isOnline: true);
        }

        Assert.Equal(4_096, viewModel.Players.Count);
        Assert.Equal(4_096, viewModel.VisiblePlayers.Count);

        foreach (var name in names)
        {
            viewModel.UpdatePlayerPresence(name, isOnline: false);
        }

        Assert.Empty(viewModel.Players);
        Assert.Empty(viewModel.VisiblePlayers);
    }

    [Fact]
    public void LargeRegistryAndKnownPlayerToggle_PublishOneResetPerProjection()
    {
        var viewModel = CreateViewModel();
        viewModel.ShowKnownPlayers = true;
        var playerEvents = new List<NotifyCollectionChangedEventArgs>();
        var visibleEvents = new List<NotifyCollectionChangedEventArgs>();
        viewModel.Players.CollectionChanged += (_, args) => playerEvents.Add(args);
        viewModel.VisiblePlayers.CollectionChanged += (_, args) => visibleEvents.Add(args);
        var records = Enumerable.Range(0, 4_096)
            .Select(index => new PlayerStatusRecord(
                $"P{index:D4}",
                $"uuid-{index}",
                false,
                false,
                false,
                false))
            .ToArray();

        viewModel.ReplacePlayers(records);

        Assert.Equal(4_096, viewModel.Players.Count);
        Assert.Equal(4_096, viewModel.VisiblePlayers.Count);
        Assert.Collection(playerEvents, change =>
            Assert.Equal(NotifyCollectionChangedAction.Reset, change.Action));
        Assert.Collection(visibleEvents, change =>
            Assert.Equal(NotifyCollectionChangedAction.Reset, change.Action));

        visibleEvents.Clear();
        viewModel.ShowKnownPlayers = false;
        Assert.Empty(viewModel.VisiblePlayers);
        Assert.Collection(visibleEvents, change =>
            Assert.Equal(NotifyCollectionChangedAction.Reset, change.Action));

        visibleEvents.Clear();
        viewModel.ShowKnownPlayers = true;
        Assert.Equal(4_096, viewModel.VisiblePlayers.Count);
        Assert.Collection(visibleEvents, change =>
            Assert.Equal(NotifyCollectionChangedAction.Reset, change.Action));
    }

    [Fact]
    public void RepeatedIdenticalOnlineSnapshot_DoesNotRepublishOrRetouchRows()
    {
        var viewModel = CreateViewModel();
        viewModel.UpdateOnlinePlayers(["StableUser"]);
        var player = Assert.Single(viewModel.Players);
        var playerPropertyChanges = 0;
        var playerCollectionChanges = 0;
        var visibleCollectionChanges = 0;
        player.PropertyChanged += (_, _) => playerPropertyChanges++;
        viewModel.Players.CollectionChanged += (_, _) => playerCollectionChanges++;
        viewModel.VisiblePlayers.CollectionChanged += (_, _) => visibleCollectionChanges++;

        viewModel.UpdateOnlinePlayers(["StableUser"]);

        Assert.Equal(0, playerPropertyChanges);
        Assert.Equal(0, playerCollectionChanges);
        Assert.Equal(0, visibleCollectionChanges);
    }

    [Fact]
    public void AppendConsoleBatch_RetainsOnlyLatestTwoThousandLines()
    {
        var viewModel = CreateViewModel();
        var timestamp = DateTimeOffset.UtcNow;

        viewModel.AppendConsoleBatch(Enumerable.Range(0, 10_000)
            .Select(index => new ConsoleLine(timestamp, $"line-{index}")));

        Assert.Equal(2_000, viewModel.ConsoleLines.Count);
        Assert.Equal("line-8000", viewModel.ConsoleLines[0].Text);
        Assert.Equal("line-9999", viewModel.ConsoleLines[^1].Text);
    }

    private static ServerInstanceViewModel CreateViewModel(
        ServerInstance? model = null,
        Action<ServerInstanceViewModel, MemoryAllocationMode>? memoryModeRequested = null) => new(
            model ?? CreateModel(),
            static (_, _) => Task.CompletedTask,
            memoryModeRequested: memoryModeRequested);

    private static ServerInstance CreateModel() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test Server",
        DirectoryPath = Path.GetTempPath(),
        CoreType = CoreType.Paper
    };
}
