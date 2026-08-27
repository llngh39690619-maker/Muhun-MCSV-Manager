using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts.Localization;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class ReliabilitySettingsContractTests
{
    [Fact]
    public void LegacyInstanceJson_UsesSafeOptInDefaultsForReliabilityFeatures()
    {
        var instance = JsonSerializer.Deserialize<ServerInstance>("{}", new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(instance);
        Assert.False(instance.EnableHangWatchdog);
        Assert.False(instance.EnableAutomaticRecoveryPoints);
        Assert.Equal(30, instance.WatchdogCheckIntervalSeconds);
        Assert.Equal(8, instance.WatchdogProbeTimeoutSeconds);
        Assert.Equal(3, instance.WatchdogFailureThreshold);
        Assert.Equal(180, instance.WatchdogStartupGraceSeconds);
        Assert.Equal(30, instance.RecoveryPointIntervalMinutes);
        Assert.Equal(3, instance.RecoveryPointRetentionCount);
    }

    [Fact]
    public void ServerSettingsXaml_ExposesEveryReliabilitySettingAndExplainsStatusProtocol()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var elements = document.Descendants().ToArray();

        Assert.Contains(elements, element =>
            (string?)element.Attribute("IsChecked") == "{Binding SelectedServer.AutoRestart}");
        Assert.Contains(elements, element =>
            (string?)element.Attribute("IsChecked") == "{Binding SelectedServer.EnableHangWatchdog}");
        Assert.Contains(elements, element =>
            (string?)element.Attribute("IsChecked") == "{Binding SelectedServer.EnableAutomaticRecoveryPoints}");

        foreach (var property in new[]
                 {
                     "WatchdogCheckIntervalSeconds",
                     "WatchdogProbeTimeoutSeconds",
                     "WatchdogFailureThreshold",
                     "WatchdogStartupGraceSeconds",
                     "RecoveryPointIntervalMinutes",
                     "RecoveryPointRetentionCount"
                 })
        {
            Assert.Contains(
                document.Descendants(presentation + "TextBox"),
                element => ((string?)element.Attribute("Text"))?.Contains(
                    $"SelectedServer.{property}",
                    StringComparison.Ordinal) == true);
        }

        var explanatoryText = string.Join(
            "\n",
            document.Descendants(presentation + "TextBlock")
                .Select(element => (string?)element.Attribute("Text") ?? string.Empty));
        Assert.Contains(
            "{DynamicResource L10n.main.settings.watchdogHint}",
            explanatoryText,
            StringComparison.Ordinal);
        Assert.Contains(
            "{DynamicResource L10n.main.settings.autoRestartHint}",
            explanatoryText,
            StringComparison.Ordinal);
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Command") == "{Binding RestoreRecoveryPointCommand}"
                       && (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.main.backups.restoreRecoveryPoint}"
                       && (string?)element.Attribute("Visibility")
                       == "{Binding CanManageLocalRecoveryPoints, Converter={StaticResource BoolToVisibility}}");

        var zhTw = ProductLocalizationCatalog.GetDocument("zh-TW").Strings;
        Assert.Contains("Minecraft 狀態協定", zhTw["main.settings.watchdogHint"], StringComparison.Ordinal);
        Assert.Contains("不傳送 list", zhTw["main.settings.watchdogHint"], StringComparison.Ordinal);
        Assert.Contains("最多自動重啟 3 次", zhTw["main.settings.autoRestartHint"], StringComparison.Ordinal);
        Assert.Contains("健康恢復點", zhTw["main.backups.restoreRecoveryPoint"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingLegacySettings_AdvancesSchemaAndPersistsReliabilityValues()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(temporary.Path, "schema migration");
        var serverRoot = Path.Combine(applicationRoot, "servers", "test");
        Directory.CreateDirectory(serverRoot);
        await File.WriteAllBytesAsync(Path.Combine(serverRoot, "server.jar"), []);
        var instance = new ServerInstance
        {
            Name = "Reliability",
            DirectoryPath = serverRoot,
            ServerJarPath = Path.Combine(serverRoot, "server.jar"),
            EnableHangWatchdog = true,
            WatchdogFailureThreshold = 4,
            EnableAutomaticRecoveryPoints = true,
            RecoveryPointRetentionCount = 5,
        };
        var settingsPath = Path.Combine(applicationRoot, "manager.json");
        Directory.CreateDirectory(applicationRoot);
        using (var store = new JsonSettingsStore<ManagerSettings>(settingsPath))
        {
            await store.SaveAsync(new ManagerSettings
            {
                SchemaVersion = 1,
                Instances = [instance]
            });
        }

        await using (var viewModel = new MainWindowViewModel(
                         new ApplicationPaths(applicationRoot)))
        {
            await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
            await viewModel.ShutdownAsync();
        }

        using var persistedStore = new JsonSettingsStore<ManagerSettings>(settingsPath);
        var persisted = Assert.IsType<ManagerSettings>(await persistedStore.LoadAsync());
        Assert.True(persisted.SchemaVersion >= 4);
        var saved = Assert.Single(persisted.Instances);
        Assert.True(saved.EnableHangWatchdog);
        Assert.Equal(4, saved.WatchdogFailureThreshold);
        Assert.True(saved.EnableAutomaticRecoveryPoints);
        Assert.Equal(5, saved.RecoveryPointRetentionCount);
    }

    [Fact]
    public void LifecycleCoordination_NeverHoldsBackupGateWhileAwaitingCoreTransition()
    {
        var source = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "ViewModels",
            "MainWindowViewModel.cs")));

        foreach (var signature in new[]
                 {
                     "private async Task<bool> StopServerCoordinatedAsync(",
                     "private async Task<Guid> StartProcessCoordinatedAsync(",
                     "private async Task<bool> TryStartProcessCoordinatedAsync(",
                     "private async Task<ServerStopResult> StopServerDetailedCoordinatedAsync(",
                 })
        {
            var method = ExtractPrivateMethod(source, signature);
            Assert.Contains("EnterLifecycleTransitionAsync", method, StringComparison.Ordinal);
            Assert.Contains("WaitForBackupIdleAsync", method, StringComparison.Ordinal);
            Assert.DoesNotContain("_backupGates", method, StringComparison.Ordinal);

            var backupBarrier = method.IndexOf("WaitForBackupIdleAsync", StringComparison.Ordinal);
            var coreTransition = method.IndexOf("_processManager.", StringComparison.Ordinal);
            Assert.True(
                backupBarrier >= 0 && coreTransition > backupBarrier,
                $"{signature} must pass the backup-idle barrier before entering Core.");
        }

        var prepareStart = ExtractPrivateMethod(
            source,
            "private async Task PrepareServerStartOnUiAsync(");
        Assert.DoesNotContain("_backupGates", prepareStart, StringComparison.Ordinal);

        var manualBackup = ExtractPrivateMethod(source, "private async Task CreateBackupAsync(");
        var recoveryBackup = ExtractPrivateMethod(
            source,
            "private async Task CreateHealthyRecoveryPointAsync(");
        Assert.Contains("_lifecycleTransitions.ContainsKey", manualBackup, StringComparison.Ordinal);
        Assert.Contains("_lifecycleTransitions.ContainsKey", recoveryBackup, StringComparison.Ordinal);
    }

    [Fact]
    public void ReliabilityRecovery_UsesCoordinatedGuardedStartAndTruthfulStartFailurePolicy()
    {
        var source = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "ViewModels",
            "MainWindowViewModel.cs")));
        var watchdogRecovery = ExtractPrivateMethod(
            source,
            "private async Task RecoverUnresponsiveServerAsync(");
        Assert.Contains("TryStartProcessCoordinatedAsync", watchdogRecovery, StringComparison.Ordinal);
        Assert.Contains("_manualStopEpochs", watchdogRecovery, StringComparison.Ordinal);
        Assert.Contains("CanRestartStoppedSession", watchdogRecovery, StringComparison.Ordinal);

        var reliabilityState = ExtractPrivateMethod(
            source,
            "private void RegisterReliabilityStateChange(");
        Assert.Contains("_sessionsThatReachedRunning", reliabilityState, StringComparison.Ordinal);
        Assert.Contains("new CrashRestartDecision(", reliabilityState, StringComparison.Ordinal);
        Assert.Contains("false,", reliabilityState, StringComparison.Ordinal);
        Assert.Contains("main.vm.console.javaFailedBeforeRunning", reliabilityState, StringComparison.Ordinal);
        Assert.Contains(
            "不會自動重試",
            ProductLocalizationCatalog.GetDocument("zh-TW")
                .Strings["main.vm.console.javaFailedBeforeRunning"],
            StringComparison.Ordinal);

        var removal = ExtractPrivateMethod(source, "internal async Task RemoveServerAsync(");
        Assert.Contains("RunServerListMutationAsync", removal, StringComparison.Ordinal);
        var removalCore = ExtractPrivateMethod(source, "private async Task RemoveServerCoreAsync(");
        Assert.Contains("InvalidateAutomaticRestartIntent", removalCore, StringComparison.Ordinal);
        Assert.Contains("StopServerCoordinatedAsync", removalCore, StringComparison.Ordinal);
    }

    private static string ExtractPrivateMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method was not found: {signature}");
        var nextPrivate = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        var nextInternal = source.IndexOf("\n    internal ", start + signature.Length, StringComparison.Ordinal);
        var candidates = new[] { nextPrivate, nextInternal }.Where(index => index >= 0).ToArray();
        var end = candidates.Length == 0 ? source.Length : candidates.Min();
        return source[start..end];
    }

    private static string GetAppSourcePath(
        string relativePath,
        [CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            "..",
            "src",
            "MinecraftServerManager.App",
            relativePath));
}
