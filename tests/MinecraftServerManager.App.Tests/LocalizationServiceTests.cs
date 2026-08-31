using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.Services.Localization;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Client;
using MinecraftServerManager.Contracts.Localization;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Remote;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class LocalizationServiceTests
{
    [Fact]
    public void Initialize_CorruptState_RepairsAtomicallyAndFallsBackSafely()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "language.json");
        File.WriteAllText(path, "{ definitely not json");

        LocalizationService.Current.Initialize(path, CultureInfo.GetCultureInfo("fr-FR"));

        Assert.Equal("zh-TW", LocalizationService.Current.CultureName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("zh-TW", document.RootElement.GetProperty("Culture").GetString());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void SetCulture_CanonicalizesAndPersistsWithoutRestart()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "language.json");
        LocalizationService.Current.Initialize(path, CultureInfo.GetCultureInfo("zh-TW"));

        LocalizationService.Current.SetCulture("en-GB");

        Assert.Equal("en-US", LocalizationService.Current.CultureName);
        Assert.Equal("Close", LocalizationService.Current.Get("common.close"));
        Assert.Equal("By MCSV", LocalizationService.Current.Get("online.author", "MCSV"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("en-US", document.RootElement.GetProperty("Culture").GetString());
        LocalizationService.Current.SetCulture("zh-TW");
    }

    [Fact]
    public void Initialize_SupportedAlias_IsPersistedInCanonicalBcp47Form()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "language.json");
        File.WriteAllText(path, "{\"Culture\":\"en-GB\"}");

        LocalizationService.Current.Initialize(path);

        Assert.Equal("en-US", LocalizationService.Current.CultureName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("en-US", document.RootElement.GetProperty("Culture").GetString());
        LocalizationService.Current.SetCulture("zh-TW");
    }

    [Fact]
    public void ApplyResources_UpdatesEveryVersionedDynamicResource()
    {
        using var directory = TemporaryDirectory.Create();
        LocalizationService.Current.Initialize(
            Path.Combine(directory.Path, "language.json"),
            CultureInfo.GetCultureInfo("en-US"));
        var resources = new ResourceDictionary();

        LocalizationService.Current.ApplyResources(resources);

        Assert.Equal("X MCSV", resources["L10n.main.window.title"]);
        Assert.StartsWith(
            "Windows Service is connected",
            Assert.IsType<string>(resources["L10n.service.status.connected"]),
            StringComparison.Ordinal);
        Assert.Equal(
            MinecraftServerManager.Contracts.Localization.ProductLocalizationCatalog.Keys.Count,
            resources.Keys.Cast<object>().Count(key => key is string text && text.StartsWith("L10n.", StringComparison.Ordinal)));
        LocalizationService.Current.SetCulture("zh-TW");
    }

    [Fact]
    public void ProductServiceStatusLocalizer_UsesCatalogAndNeverEchoesUnsafeDetails()
    {
        using var directory = TemporaryDirectory.Create();
        LocalizationService.Current.Initialize(
            Path.Combine(directory.Path, "language.json"),
            CultureInfo.GetCultureInfo("en-US"));

        Assert.Contains(
            "keep running",
            ProductServiceStatusLocalizer.Format(ProductServiceConnectionState.Connected, null),
            StringComparison.OrdinalIgnoreCase);
        var faulted = ProductServiceStatusLocalizer.Format(
            ProductServiceConnectionState.Faulted,
            "C:\\secret\\service-token.txt");
        Assert.Contains("service.unknown", faulted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", faulted, StringComparison.OrdinalIgnoreCase);
        LocalizationService.Current.SetCulture("zh-TW");
    }

    [Fact]
    public void AppXaml_LocalizationReferencesResolveToVersionedCatalogKeys()
    {
        var appRoot = GetAppSourcePath();
        var pattern = new Regex(
            @"\{(?:Static|Dynamic)Resource\s+L10n\.([^\s,}]+)\}",
            RegexOptions.CultureInvariant);
        var referencedKeys = Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(path => pattern.Matches(File.ReadAllText(path)))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(referencedKeys);
        Assert.All(referencedKeys, key => Assert.Contains(key, ProductLocalizationCatalog.Keys));
        Assert.Contains("main.window.title", referencedKeys);
        Assert.Contains("service.status.tooltip", ProductLocalizationCatalog.Keys);
        Assert.Contains("service.readOnly.backupOperation", ProductLocalizationCatalog.Keys);
    }

    [Fact]
    public void VersionedDocuments_HaveExactCatalogParity()
    {
        var expected = ProductLocalizationCatalog.Keys.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(2, ProductLocalizationCatalog.SupportedCultures.Count);
        foreach (var culture in ProductLocalizationCatalog.SupportedCultures)
        {
            var document = ProductLocalizationCatalog.GetDocument(culture);
            Assert.Equal(ProductLocalizationCatalog.SchemaVersion, document.SchemaVersion);
            Assert.Equal(culture, document.Culture);
            Assert.True(expected.SetEquals(document.Strings.Keys));
            Assert.All(expected, key => Assert.False(string.IsNullOrWhiteSpace(document.Strings[key])));
        }
    }

    [Fact]
    public void MainWindowViewModel_UserFacingTextUsesVersionedLocalizationKeys()
    {
        var path = Path.Combine(GetAppSourcePath(), "ViewModels", "MainWindowViewModel.cs");
        var source = File.ReadAllText(path);
        var chinese = new Regex(@"[\u3400-\u9fff]", RegexOptions.CultureInvariant);
        var referencedKeys = Regex.Matches(
                source,
                "\"(main\\.vm\\.[^\"]+)\"",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotMatch(chinese, source);
        Assert.True(referencedKeys.Count >= 300, $"Expected the formal MainWindow vocabulary, found {referencedKeys.Count} keys.");
        Assert.All(referencedKeys, key => Assert.Contains(key, ProductLocalizationCatalog.Keys));
        Assert.All(ProductLocalizationCatalog.SupportedCultures, culture =>
        {
            var document = ProductLocalizationCatalog.GetDocument(culture);
            Assert.All(referencedKeys, key =>
                Assert.True(document.Strings.ContainsKey(key), $"{culture} is missing {key}."));
        });
    }

    [Fact]
    public async Task MainWindowViewModel_CultureChangeRefreshesVisibleComputedAndTokenizedStatus()
    {
        using var directory = TemporaryDirectory.Create();
        LocalizationService.Current.Initialize(
            Path.Combine(directory.Path, "language.json"),
            CultureInfo.GetCultureInfo("zh-TW"));
        await using var viewModel = new MainWindowViewModel(new ApplicationPaths(directory.Path));

        try
        {
            Assert.Equal("準備就緒", viewModel.StatusMessage);
            Assert.Equal("0 個", viewModel.ServerCountText);
            Assert.Equal("執行中 0 / 0", viewModel.RunningSummary);
            Assert.Equal("沒有進行中的下載或建立工作", viewModel.BackgroundJobActivity);

            LocalizationService.Current.SetCulture("en-US");

            Assert.Equal("Ready", viewModel.StatusMessage);
            Assert.Equal("0", viewModel.ServerCountText);
            Assert.Equal("Running 0 / 0", viewModel.RunningSummary);
            Assert.Equal("No download or creation jobs are active", viewModel.BackgroundJobActivity);
        }
        finally
        {
            LocalizationService.Current.SetCulture("zh-TW");
        }
    }

    [Fact]
    public void FormalDialogs_ContainNoUnlocalizedUserFacingChinese()
    {
        var appRoot = GetAppSourcePath();
        var files = FormalLocalizationSourceFiles
            .Select(relativePath => Path.Combine(appRoot, relativePath))
            .ToArray();
        var chinese = new Regex(@"[\u3400-\u9fff]", RegexOptions.CultureInvariant);

        Assert.All(files, path =>
        {
            Assert.True(File.Exists(path), $"Formal localization source is missing: {path}");
            Assert.DoesNotMatch(chinese, File.ReadAllText(path));
        });
    }

    [Fact]
    public void MainWindowXaml_UserFacingAttributesUseBindingsOrVersionedResources()
    {
        var path = Path.Combine(GetAppSourcePath(), "MainWindow.xaml");
        var source = File.ReadAllText(path);
        var userFacingAttribute = new Regex(
            @"\b(?:Title|Text|Content|Header|ToolTip|AutomationProperties\.Name)=""([^""]*)""",
            RegexOptions.CultureInvariant);
        var permittedFormalLiterals = new HashSet<string>(StringComparer.Ordinal)
        {
            "  •  ",
            "MUHUN",
            "MCSV Manager",
            "⚙",
            "!",
            "◆",
            "☰",
        };

        var unresolved = userFacingAttribute.Matches(source)
            .Select(match => match.Groups[1].Value)
            .Where(value =>
                !value.StartsWith("{Binding ", StringComparison.Ordinal) &&
                !value.StartsWith("{DynamicResource L10n.", StringComparison.Ordinal) &&
                !permittedFormalLiterals.Contains(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unresolved.Length == 0,
            $"MainWindow.xaml contains unlocalized user-facing attributes: {string.Join(" | ", unresolved)}");
    }

    [Fact]
    public void MainWindowXaml_LocalizationReferencesResolveAcrossEveryVersionedDocument()
    {
        var path = Path.Combine(GetAppSourcePath(), "MainWindow.xaml");
        var pattern = new Regex(
            @"\{DynamicResource\s+L10n\.([^\s,}]+)\}",
            RegexOptions.CultureInvariant);
        var referencedKeys = pattern.Matches(File.ReadAllText(path))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("main.console.commandTooltip", referencedKeys);
        Assert.Contains("main.players.actionsHeading", referencedKeys);
        Assert.Contains("main.settings.watchdogHint", referencedKeys);
        Assert.Contains("main.modpackUpdate.start", referencedKeys);
        Assert.Contains("main.backups.restore", referencedKeys);
        Assert.All(referencedKeys, key => Assert.Contains(key, ProductLocalizationCatalog.Keys));
        Assert.All(ProductLocalizationCatalog.SupportedCultures, culture =>
        {
            var document = ProductLocalizationCatalog.GetDocument(culture);
            Assert.All(referencedKeys, key =>
            {
                Assert.True(document.Strings.TryGetValue(key, out var value), $"{culture} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{culture}/{key} is empty.");
            });
        });
    }

    [Fact]
    public void FormalDialogBaml_LoadsAndDynamicResourcesSwitchInPlace()
    {
        WpfStaTestHost.Run(() =>
        {
            using var directory = TemporaryDirectory.Create();
            LocalizationService.Current.Initialize(
                Path.Combine(directory.Path, "language.json"),
                CultureInfo.GetCultureInfo("en-US"));
            var dialog = new DeleteServerConfirmationDialog("Formal Test", @"C:\formal-test");

            try
            {
                dialog.Show();
                dialog.UpdateLayout();
                Assert.Equal("Delete server completely", dialog.Title);
                Assert.Equal("Cancel", Assert.IsType<Button>(dialog.FindName("CancelButton")).Content);

                LocalizationService.Current.SetCulture("zh-TW");
                dialog.UpdateLayout();
                Assert.Equal("完全刪除 Server", dialog.Title);
                Assert.Equal("取消", Assert.IsType<Button>(dialog.FindName("CancelButton")).Content);
            }
            finally
            {
                dialog.Close();
                LocalizationService.Current.SetCulture("zh-TW");
            }
        });
    }

    [Fact]
    public void RemoteAccessDialogBaml_DynamicResourcesSwitchInPlace()
    {
        WpfStaTestHost.Run(() =>
        {
            using var directory = TemporaryDirectory.Create();
            LocalizationService.Current.Initialize(
                Path.Combine(directory.Path, "language.json"),
                CultureInfo.GetCultureInfo("en-US"));
            var coordinator = new RemoteAccessCoordinator(new LocalizationRemoteBackend());
            using var viewModel = CreateRemoteSettingsViewModel(coordinator);
            var dialog = new RemoteAccessDialog(viewModel);

            try
            {
                dialog.Show();
                dialog.UpdateLayout();
                var stopButton = Assert.Single(
                    FindVisualChildren<Button>(dialog),
                    button => ReferenceEquals(button.Command, viewModel.StopCommand));
                var englishTitle = LocalizationService.Current.Get("remote.window.title");
                var englishStop = LocalizationService.Current.Get("remote.closeWeb");
                Assert.Equal(englishTitle, dialog.Title);
                Assert.Equal(englishStop, stopButton.Content);

                LocalizationService.Current.SetCulture("zh-TW");
                dialog.UpdateLayout();

                Assert.Equal(LocalizationService.Current.Get("remote.window.title"), dialog.Title);
                Assert.Equal(LocalizationService.Current.Get("remote.closeWeb"), stopButton.Content);
                Assert.NotEqual(englishTitle, dialog.Title);
                Assert.NotEqual(englishStop, stopButton.Content);
            }
            finally
            {
                if (dialog.IsVisible) dialog.Close();
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                LocalizationService.Current.SetCulture("zh-TW");
            }
        });
    }

    [Fact]
    public void RemoteWebConsoleDialogBaml_DynamicResourcesSwitchInPlace()
    {
        WpfStaTestHost.Run(() =>
        {
            using var directory = TemporaryDirectory.Create();
            LocalizationService.Current.Initialize(
                Path.Combine(directory.Path, "language.json"),
                CultureInfo.GetCultureInfo("en-US"));
            var coordinator = new RemoteAccessCoordinator(new LocalizationRemoteBackend());
            using var viewModel = new RemoteWebConsoleViewModel(
                coordinator,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                Dispatcher.CurrentDispatcher);
            var dialog = new RemoteWebConsoleDialog(viewModel);

            try
            {
                dialog.Show();
                dialog.UpdateLayout();
                var stopButton = Assert.Single(
                    FindVisualChildren<Button>(dialog),
                    button => ReferenceEquals(button.Command, viewModel.StopCommand));
                var englishTitle = LocalizationService.Current.Get("remote.console.window.title");
                var englishStop = LocalizationService.Current.Get("remote.console.stopWeb");
                Assert.Equal(englishTitle, dialog.Title);
                Assert.Equal(englishStop, stopButton.Content);

                LocalizationService.Current.SetCulture("zh-TW");
                dialog.UpdateLayout();

                Assert.Equal(LocalizationService.Current.Get("remote.console.window.title"), dialog.Title);
                Assert.Equal(LocalizationService.Current.Get("remote.console.stopWeb"), stopButton.Content);
                Assert.NotEqual(englishTitle, dialog.Title);
                Assert.NotEqual(englishStop, stopButton.Content);
            }
            finally
            {
                if (dialog.IsVisible) dialog.Close();
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                LocalizationService.Current.SetCulture("zh-TW");
            }
        });
    }

    [Fact]
    public void RemoteViewModels_CultureChangeRefreshesComputedTextAndTokensInPlace()
    {
        WpfStaTestHost.Run(() =>
        {
            using var directory = TemporaryDirectory.Create();
            LocalizationService.Current.Initialize(
                Path.Combine(directory.Path, "language.json"),
                CultureInfo.GetCultureInfo("zh-TW"));
            var coordinator = new RemoteAccessCoordinator(new LocalizationRemoteBackend());
            using var settings = CreateRemoteSettingsViewModel(coordinator);
            using var console = new RemoteWebConsoleViewModel(
                coordinator,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                Dispatcher.CurrentDispatcher);
            var account = new RemoteAccountRowViewModel(
                new RemoteApprovedAccount(
                    "account1",
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    RemoteWebPermission.All,
                    HasRecoverablePin: true),
                showTailscaleIdentity: false,
                () => "12345678");

            try
            {
                account.TogglePinVisibilityCommand.Execute(null);
                var traditional = new[]
                {
                    settings.AccessModeDescription,
                    settings.ProvisioningStatus,
                    settings.CloudflareNamedTunnelTokenStatus,
                    settings.CloudflaredInstallStatus,
                    account.PinDisplayText,
                    console.StateText,
                };

                LocalizationService.Current.SetCulture("en-US");

                var english = new[]
                {
                    settings.AccessModeDescription,
                    settings.ProvisioningStatus,
                    settings.CloudflareNamedTunnelTokenStatus,
                    settings.CloudflaredInstallStatus,
                    account.PinDisplayText,
                    console.StateText,
                };
                Assert.Equal(
                    LocalizationService.Current.Get("remote.legacy.mode.tailscaleDescription"),
                    english[0]);
                Assert.Equal(LocalizationService.Current.Get("remote.legacy.gmail.notSent"), english[1]);
                Assert.Equal(LocalizationService.Current.Get("remote.legacy.token.notStored"), english[2]);
                Assert.Equal(LocalizationService.Current.Get("remote.legacy.cloudflared.installHint"), english[3]);
                Assert.Equal(
                    LocalizationService.Current.Get("remote.legacy.account.pinRevealed", "12345678"),
                    english[4]);
                Assert.Equal(LocalizationService.Current.Get("remote.console.state.closedForRun"), english[5]);
                Assert.All(
                    traditional.Zip(english),
                    pair => Assert.NotEqual(pair.First, pair.Second));
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                LocalizationService.Current.SetCulture("zh-TW");
            }
        });
    }

    private static IReadOnlyList<string> FormalLocalizationSourceFiles { get; } =
    [
        @"MainWindow.xaml",
        @"Controls\RevealPasswordBox.xaml",
        @"Dialogs\AppearanceSettingsDialog.xaml",
        @"Dialogs\BackgroundJobsWindow.xaml",
        @"Dialogs\ClientContentDownloadCenterWindow.xaml",
        @"Dialogs\CoreServerCreationDialog.xaml",
        @"Dialogs\DeleteServerConfirmationDialog.xaml",
        @"Dialogs\ExistingServerImportChoiceDialog.xaml",
        @"Dialogs\ImportServerDialog.xaml",
        @"Dialogs\ImportServerFolderDialog.xaml",
        @"Dialogs\ModpackUpdateSelectionDialog.xaml",
        @"Dialogs\OnlineModpackDialog.xaml",
        @"Dialogs\PaperVersionDialog.xaml",
        @"Dialogs\ProductProviderManagementDialog.xaml",
        @"Dialogs\RemoveServerConfirmationDialog.xaml",
        @"Dialogs\RemoteAccessDialog.xaml",
        @"Dialogs\RemoteWebConsoleDialog.xaml",
        @"Dialogs\ServerAppearanceSettingsDialog.xaml",
        @"Dialogs\AppearanceSettingsDialog.xaml.cs",
        @"Dialogs\ClientContentDownloadCenterWindow.xaml.cs",
        @"Dialogs\CoreServerCreationDialog.xaml.cs",
        @"Dialogs\DarkMessageDialog.xaml.cs",
        @"Dialogs\ImportServerDialog.xaml.cs",
        @"Dialogs\ImportServerFolderDialog.xaml.cs",
        @"Dialogs\ModpackUpdateSelectionDialog.xaml.cs",
        @"Dialogs\OnlineModpackDialog.xaml.cs",
        @"Dialogs\PaperVersionDialog.xaml.cs",
        @"Services\BackgroundServerJobCoordinator.cs",
        @"Services\BackgroundServerJobDialogServices.cs",
        @"Services\CoreServerCreationWorkflow.Catalog.cs",
        @"Services\FtbInstallerProgressFormatter.cs",
        @"Services\ICoreServerCreationWorkflow.cs",
        @"Services\IOnlineModpackWorkflow.cs",
        @"Services\OnlineModpackWorkflow.cs",
        @"Services\ThemePresetCatalog.cs",
        @"ViewModels\AppearanceSettingsViewModel.cs",
        @"ViewModels\BackgroundServerJobViewModel.cs",
        @"ViewModels\CoreServerCreationViewModel.cs",
        @"ViewModels\GeneralSettingsViewModel.cs",
        @"ViewModels\OnlineModpackViewModel.cs",
        @"ViewModels\ProductProviderManagementViewModel.cs",
        @"ViewModels\RemoteAccessSettingsViewModel.cs",
        @"ViewModels\RemoteAccountRowViewModel.cs",
        @"ViewModels\RemoteWebConsoleViewModel.cs",
        @"ViewModels\ServerInstanceViewModel.cs",
    ];

    private static RemoteAccessSettingsViewModel CreateRemoteSettingsViewModel(
        RemoteAccessCoordinator coordinator)
        => new(
            new RemoteControlSettings
            {
                AccessMode = RemoteAccessMode.Tailscale,
                AllowedLogin = "owner@gmail.com",
                LocalPort = RemoteControlSettings.DefaultLocalPort,
            },
            coordinator,
            _ => Task.CompletedTask,
            Dispatcher.CurrentDispatcher);

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
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

    private static string GetAppSourcePath()
        => TestRepositoryPaths.AppSource();

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mcsv-localization-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class LocalizationRemoteBackend : IRemoteControlBackend
    {
        public ValueTask<RemoteDashboardDto> GetDashboardAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<RemoteServerDetailDto?> GetServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<RemoteConsolePageDto?> GetConsoleAsync(
            string serverId,
            RemoteConsoleQuery query,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<RemotePlayerListDto?> GetPlayersAsync(
            string serverId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<RemoteOperationResultDto> StartServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<RemoteOperationResultDto> StopServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<RemoteOperationResultDto> RestartServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<RemoteOperationResultDto> SendConsoleCommandAsync(
            string serverId,
            string command,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<RemoteOperationResultDto> PerformPlayerActionAsync(
            string serverId,
            RemotePlayerActionRequestDto request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<RemoteOperationResultDto> CreateBackupAsync(
            string serverId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
