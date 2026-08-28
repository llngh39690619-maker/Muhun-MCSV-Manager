using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts.Localization;
using MinecraftServerManager.GameClient;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientWorkspaceLocalizationTests
{
    [Fact]
    public void ClientWorkspaceXaml_UsesOnlyVersionedDynamicLocalizationResources()
    {
        var xaml = File.ReadAllText(GetAppSourcePath(Path.Combine("Views", "ClientWorkspaceView.xaml")));
        Assert.DoesNotMatch(new Regex(@"[\u3400-\u9fff]", RegexOptions.CultureInvariant), xaml);

        var keys = Regex.Matches(xaml, @"L10n\.(client\.[A-Za-z0-9.]+)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Contains(key, ProductLocalizationCatalog.Keys));
    }

    [Fact]
    public void ClientComputedText_RefreshesImmediatelyWhenCultureChanges()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        LocalizationService.Current.Initialize(
            Path.Combine(directory.Path, "language.json"),
            CultureInfo.GetCultureInfo("zh-TW"));
        var instance = new ClientInstanceItemViewModel(new MinecraftClientInstance
        {
            Id = Guid.NewGuid(),
            Name = "Localization test",
            GameVersion = "1.21.1",
            DirectoryPath = directory.Path,
            TotalPlayTimeSeconds = 5_400,
        })
        {
            State = MinecraftClientInstanceState.Running,
        };
        var editor = new ClientInstanceSettingsEditorViewModel(Guid.NewGuid(), new MinecraftClientInstanceSettingsUpdate
        {
            Name = "Localization test",
            WindowWidth = 1280,
            WindowHeight = 720,
            MemoryMode = MinecraftClientMemoryMode.Automatic,
            MinimumMemoryMb = 2048,
            MaximumMemoryMb = 4096,
        });
        editor.WindowWidthText = "invalid";
        var catalogVersion = new ClientCatalogVersionItemViewModel(new FtbClientCatalogVersion(
            1,
            2,
            "Stable",
            "1.21.1",
            null,
            null,
            DateTimeOffset.UtcNow));
        var changedProperties = new List<string?>();
        catalogVersion.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        try
        {
            Assert.Equal("遊戲中", instance.StatusText);
            Assert.Contains("小時", instance.PlayTimeText, StringComparison.Ordinal);
            Assert.Equal("自動（依內容調整）", editor.MemoryModes[1].Name);
            Assert.Contains("寬度", editor.ResolutionError, StringComparison.Ordinal);
            Assert.EndsWith("未知載入器", catalogVersion.Name, StringComparison.Ordinal);

            LocalizationService.Current.SetCulture("en-US");

            Assert.Equal("Playing", instance.StatusText);
            Assert.Contains("hours", instance.PlayTimeText, StringComparison.Ordinal);
            Assert.Equal("Automatic (adjust to content)", editor.MemoryModes[1].Name);
            Assert.Contains("Width", editor.ResolutionError, StringComparison.Ordinal);
            Assert.EndsWith("Unknown loader", catalogVersion.Name, StringComparison.Ordinal);
            Assert.Contains(nameof(ClientCatalogVersionItemViewModel.Name), changedProperties);
        }
        finally
        {
            LocalizationService.Current.SetCulture("zh-TW");
        }
    }

    [Fact]
    public void FtbMissingMetadata_IsLocalizedOnlyAtTheAppPresentationBoundary()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        LocalizationService.Current.Initialize(
            Path.Combine(directory.Path, "language.json"),
            CultureInfo.GetCultureInfo("zh-TW"));
        var version = new FtbClientCatalogVersion(
            7,
            70,
            "Stable",
            string.Empty,
            null,
            null,
            DateTimeOffset.UtcNow);
        var project = new ClientModpackProjectItemViewModel(new FtbClientCatalogProject(
            7,
            "Metadata-free pack",
            string.Empty,
            0,
            DateTimeOffset.UtcNow,
            null,
            null,
            [version]));
        var versionItem = new ClientCatalogVersionItemViewModel(version);
        var changedProperties = new List<string?>();
        project.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        try
        {
            Assert.Equal("Minecraft 未知版本 · 未知載入器", project.Description);
            Assert.Equal("未知版本", project.GameVersionText);
            Assert.Equal("Stable · Minecraft 未知版本 · 未知載入器", versionItem.Name);

            LocalizationService.Current.SetCulture("en-US");

            Assert.Equal("Minecraft Unknown version · Unknown loader", project.Description);
            Assert.Equal("Unknown version", project.GameVersionText);
            Assert.Equal("Stable · Minecraft Unknown version · Unknown loader", versionItem.Name);
            Assert.Contains(nameof(ClientModpackProjectItemViewModel.Description), changedProperties);
            Assert.Contains(nameof(ClientModpackProjectItemViewModel.GameVersionText), changedProperties);
        }
        finally
        {
            LocalizationService.Current.SetCulture("zh-TW");
        }
    }

    [Fact]
    public void FtbValidationFailures_AreLocalizedBeforeTheyReachErrorText()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        LocalizationService.Current.Initialize(
            Path.Combine(directory.Path, "language.json"),
            CultureInfo.GetCultureInfo("zh-TW"));
        var errors = new[]
        {
            Record.Exception(() => new FtbClientCatalogRequest(Limit: 101).Validate()),
            Record.Exception(() => new FtbClientCatalogRequest(Query: new string('x', 201)).Validate()),
            Record.Exception(() => new FtbClientCatalogRequest(GameVersion: new string('1', 65)).Validate()),
            Record.Exception(() => FtbAppProtocol.CreateInstallUri(0)),
        };
        Assert.All(errors, Assert.NotNull);

        try
        {
            Assert.Equal(
                [
                    "FTB 顯示數量必須介於 1 到 100。",
                    "FTB 搜尋文字不得超過 200 個字元。",
                    "Minecraft 版本文字不得超過 64 個字元。",
                    "FTB 模組包 ID 必須是正整數。",
                ],
                errors.Select(error =>
                    ClientWorkspaceViewModel.LocalizeFtbValidationFailure(error!)));

            LocalizationService.Current.SetCulture("en-US");

            Assert.Equal(
                [
                    "FTB result limit must be between 1 and 100.",
                    "FTB search text cannot exceed 200 characters.",
                    "Minecraft version text cannot exceed 64 characters.",
                    "FTB pack ID must be a positive integer.",
                ],
                errors.Select(error =>
                    ClientWorkspaceViewModel.LocalizeFtbValidationFailure(error!)));
        }
        finally
        {
            LocalizationService.Current.SetCulture("zh-TW");
        }
    }

    [Fact]
    public void ClientWorkspaceProgress_MapsProviderStagesToLocalizedProductText()
    {
        var source = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "ViewModels",
            "ClientWorkspaceViewModel.cs")));

        Assert.DoesNotContain("StatusText = value.Message", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentStatusText = value.Message", source, StringComparison.Ordinal);
        Assert.Contains("LocalizeClientInstallProgress(value)", source, StringComparison.Ordinal);
        Assert.Contains("LocalizeModrinthProgress(value)", source, StringComparison.Ordinal);
        Assert.Contains("LocalizeContentProgress(value)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("XMCSV/1.0.8", source, StringComparison.Ordinal);
        Assert.Contains("AssemblyInformationalVersionAttribute", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientAccountLifecycle_ExposesLocalizedRemoveAndSignOutAllActions()
    {
        var xaml = File.ReadAllText(GetAppSourcePath(Path.Combine("Views", "ClientWorkspaceView.xaml")));
        var source = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "ViewModels",
            "ClientWorkspaceViewModel.cs")));

        Assert.Contains("L10n.client.account.remove", xaml, StringComparison.Ordinal);
        Assert.Contains("L10n.client.account.signOutAll", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RemoveSelectedAccountCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SignOutAllAccountsCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("_authenticationService.SignOutAsync(account.Id)", source, StringComparison.Ordinal);
        Assert.Contains("_authenticationService.SignOutAllAsync()", source, StringComparison.Ordinal);

        foreach (var key in new[]
                 {
                     "client.account.remove",
                     "client.account.signOutAll",
                     "client.vm.status.accountRemoved",
                     "client.vm.status.accountsSignedOut",
                     "client.vm.validation.account",
                 })
        {
            Assert.Contains(key, ProductLocalizationCatalog.Keys);
            Assert.False(string.IsNullOrWhiteSpace(
                ProductLocalizationCatalog.GetDocument("en-US").Strings[key]));
            Assert.False(string.IsNullOrWhiteSpace(
                ProductLocalizationCatalog.GetDocument("zh-TW").Strings[key]));
        }
    }

    [Fact]
    public void ClientInstanceDeletion_UsesDarkConfirmationLocalizedDangerActionAndDoubleRunningGuard()
    {
        var xaml = File.ReadAllText(GetAppSourcePath(Path.Combine("Views", "ClientWorkspaceView.xaml")));
        var source = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "ViewModels",
            "ClientWorkspaceViewModel.cs")));

        Assert.Contains("L10n.client.action.delete", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DangerButton}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DeleteClientInstanceCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("var answer = DarkMessageBox.Show(", source, StringComparison.Ordinal);
        Assert.Contains("MessageBoxResult.No", source, StringComparison.Ordinal);
        Assert.Contains("CanDeleteSelectedInstance", source, StringComparison.Ordinal);
        Assert.Contains("_runningSessions.ContainsKey(instance.Id)", source, StringComparison.Ordinal);
        Assert.Contains("_processRecoveryService.IsMatchingProcessActive(stored)", source, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(source, @"await EnsureInstanceDeletionAllowedAsync\(instance,").Count);
        Assert.Contains("await _instanceManager.DeleteAsync(instance.Id, CancellationToken.None)", source, StringComparison.Ordinal);

        foreach (var key in new[]
                 {
                     "client.action.delete",
                     "client.delete.confirm",
                     "client.delete.title",
                     "client.vm.status.instanceDeleting",
                     "client.vm.status.instanceDeleted",
                     "client.vm.validation.deleteWhileRunning",
                 })
        {
            Assert.Contains(key, ProductLocalizationCatalog.Keys);
            Assert.False(string.IsNullOrWhiteSpace(
                ProductLocalizationCatalog.GetDocument("en-US").Strings[key]));
            Assert.False(string.IsNullOrWhiteSpace(
                ProductLocalizationCatalog.GetDocument("zh-TW").Strings[key]));
        }
    }

    [Fact]
    public void ClientFormalSources_ContainNoDirectHanUserInterfaceText()
    {
        var han = new Regex(@"[\u3400-\u9fff]", RegexOptions.CultureInvariant);
        var files = new[]
        {
            Path.Combine("ViewModels", "ClientWorkspaceViewModel.cs"),
            Path.Combine("ViewModels", "ClientInstanceItemViewModel.cs"),
            Path.Combine("ViewModels", "ClientInstanceSettingsEditorViewModel.cs"),
            Path.Combine("ViewModels", "ClientContentItemViewModel.cs"),
            Path.Combine("ViewModels", "ClientModpackProjectItemViewModel.cs"),
            Path.Combine("ViewModels", "ClientLoaderChoiceViewModel.cs"),
        };
        Assert.All(files, file => Assert.DoesNotMatch(han, File.ReadAllText(GetAppSourcePath(file))));
    }

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);
}
