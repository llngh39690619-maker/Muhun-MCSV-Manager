using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MinecraftServerManager.App.Controls;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts.Localization;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.App.Tests;

public sealed class DirectGuiLocalizationTests
{
    private static readonly IReadOnlyList<string> DirectGuiKeys =
    [
        "app.singleInstance.title",
        "app.singleInstance.message",
        "app.startupFailed.title",
        "app.startupFailed.message",
        "main.close.compatibilityRunning.title",
        "main.close.compatibilityRunning.message",
        "main.close.backgroundJobs.title",
        "main.close.backgroundJobs.message",
        "main.close.failedTitle",
        "password.reveal",
        "password.hide",
        "addon.version.unrecognized",
        "addon.project",
        "addon.update.available",
        "addon.update.none",
        "player.state.online",
        "player.state.offline",
        "player.role.whitelist",
        "player.role.banned",
        "player.role.regular",
        "javaRuntime.installed",
    ];

    [Fact]
    public void DirectGuiKeys_HaveExactVersionedResourcesAndFormatContracts()
    {
        Assert.All(DirectGuiKeys, key => Assert.Contains(key, ProductLocalizationCatalog.Keys));
        Assert.All(ProductLocalizationCatalog.SupportedCultures, culture =>
        {
            var strings = ProductLocalizationCatalog.GetDocument(culture).Strings;
            Assert.All(DirectGuiKeys, key =>
            {
                Assert.True(strings.TryGetValue(key, out var value), $"{culture} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{culture}/{key} is empty.");
            });
        });

        Assert.Equal(1, ProductLocalizationCatalog.GetParameterCount("app.startupFailed.message"));
        Assert.Equal(1, ProductLocalizationCatalog.GetParameterCount("addon.project"));
        Assert.Contains(
            "startup detail",
            ProductLocalizationCatalog.Format("en-US", "app.startupFailed.message", "startup detail"),
            StringComparison.Ordinal);
        Assert.Equal(
            "Project: example-project",
            ProductLocalizationCatalog.Format("en-US", "addon.project", "example-project"));
    }

    [Fact]
    public void DirectGuiSources_UseLocalizationAndKeepDiagnosticOnlyAppTextOutOfScope()
    {
        var app = File.ReadAllText(GetAppSourcePath("App.xaml.cs"));
        Assert.Contains("L(\"app.singleInstance.message\")", app, StringComparison.Ordinal);
        Assert.Contains("L(\"app.singleInstance.title\")", app, StringComparison.Ordinal);
        Assert.Contains("L(\"app.startupFailed.message\", exception.Message)", app, StringComparison.Ordinal);
        Assert.Contains("L(\"app.startupFailed.title\")", app, StringComparison.Ordinal);
        Assert.DoesNotContain("相同資料夾中的 Muhun MCSV Manager 已在執行", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Muhun MCSV Manager 無法初始化：", app, StringComparison.Ordinal);

        var formalFiles = new[]
        {
            "MainWindow.xaml.cs",
            Path.Combine("Controls", "RevealPasswordBox.xaml.cs"),
            Path.Combine("ViewModels", "AddonUpdateViewModel.cs"),
            Path.Combine("ViewModels", "PlayerEntryViewModel.cs"),
            Path.Combine("ViewModels", "JavaRuntimeItemViewModel.cs"),
        };
        var chinese = new Regex(@"[\u3400-\u9fff]", RegexOptions.CultureInvariant);
        var localizationReference = new Regex(
            @"""((?:main\.close|password|addon|player|javaRuntime)\.[^""]+)""",
            RegexOptions.CultureInvariant);

        foreach (var relativePath in formalFiles)
        {
            var source = File.ReadAllText(GetAppSourcePath(relativePath));
            Assert.DoesNotMatch(chinese, source);
            var referencedKeys = localizationReference.Matches(source)
                .Select(match => match.Groups[1].Value)
                .ToArray();
            Assert.NotEmpty(referencedKeys);
            Assert.All(referencedKeys, key => Assert.Contains(key, ProductLocalizationCatalog.Keys));
        }
    }

    [Fact]
    public void ComputedRows_RefreshImmediatelyWhenCultureChanges()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        LocalizationService.Current.Initialize(
            Path.Combine(directory.Path, "language.json"),
            CultureInfo.GetCultureInfo("zh-TW"));
        var addon = new AddonUpdateViewModel(new AddonUpdateInfo(
            @"C:\server\mods\example.jar",
            "example.jar",
            "sha512",
            IsRecognized: false,
            ProjectId: null,
            CurrentVersion: null,
            LatestVersion: null,
            IsUpdateAvailable: false,
            DownloadUri: null,
            DownloadFileName: null,
            DownloadSha512: null,
            DownloadSize: null,
            Message: "provider detail"));
        var player = new PlayerEntryViewModel(new PlayerStatusRecord(
            "Alex",
            null,
            IsOnline: true,
            IsOperator: false,
            IsWhitelisted: true,
            IsBanned: true));
        var runtime = new JavaRuntimeItemViewModel(new JavaRuntimeInfo
        {
            JavaExecutablePath = @"C:\Java\bin\java.exe",
            MajorVersion = 21,
            Vendor = string.Empty,
        });
        var changed = new HashSet<string>(StringComparer.Ordinal);
        Track(addon, "addon", changed);
        Track(player, "player", changed);
        Track(runtime, "runtime", changed);

        try
        {
            Assert.Equal("無法辨識", addon.CurrentDisplay);
            Assert.Equal("專案：—", addon.ProjectDisplay);
            Assert.Equal("無更新", addon.UpdateLabel);
            Assert.Equal("線上", player.OnlineText);
            Assert.Equal("白名單 · 已封禁", player.RoleText);
            Assert.Equal("已安裝 Runtime", runtime.VendorDisplay);

            LocalizationService.Current.SetCulture("en-US");

            Assert.Equal("Unrecognized", addon.CurrentDisplay);
            Assert.Equal("Project: —", addon.ProjectDisplay);
            Assert.Equal("No update", addon.UpdateLabel);
            Assert.Equal("Online", player.OnlineText);
            Assert.Equal("Whitelisted · Banned", player.RoleText);
            Assert.Equal("Installed runtime", runtime.VendorDisplay);
            Assert.Contains("addon.CurrentDisplay", changed);
            Assert.Contains("addon.ProjectDisplay", changed);
            Assert.Contains("addon.UpdateLabel", changed);
            Assert.Contains("player.OnlineText", changed);
            Assert.Contains("player.RoleText", changed);
            Assert.Contains("runtime.VendorDisplay", changed);
        }
        finally
        {
            LocalizationService.Current.SetCulture("zh-TW");
        }
    }

    [Fact]
    public void RevealPasswordButton_UsesLiveLocalizedTooltipAndAutomationName()
    {
        WpfStaTestHost.Run(() =>
        {
            using var directory = new AppearanceThemeServiceTests.TestDirectory();
            LocalizationService.Current.Initialize(
                Path.Combine(directory.Path, "language.json"),
                CultureInfo.GetCultureInfo("zh-TW"));
            var control = new RevealPasswordBox { Password = "12345678" };
            var window = new Window { Content = control };

            try
            {
                window.Show();
                window.UpdateLayout();
                var button = Assert.IsType<Button>(control.FindName("RevealButton"));
                Assert.Equal("顯示密碼", button.ToolTip);
                Assert.Equal("顯示密碼", AutomationProperties.GetName(button));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("隱藏密碼", button.ToolTip);
                Assert.Equal("隱藏密碼", AutomationProperties.GetName(button));

                LocalizationService.Current.SetCulture("en-US");
                window.UpdateLayout();
                Assert.Equal("Hide password", button.ToolTip);
                Assert.Equal("Hide password", AutomationProperties.GetName(button));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("Show password", button.ToolTip);
                Assert.Equal("Show password", AutomationProperties.GetName(button));
            }
            finally
            {
                window.Close();
                LocalizationService.Current.SetCulture("zh-TW");
            }
        });
    }

    private static void Track(
        INotifyPropertyChanged source,
        string prefix,
        ISet<string> changed)
        => source.PropertyChanged += (_, args) =>
        {
            if (!string.IsNullOrEmpty(args.PropertyName))
            {
                changed.Add($"{prefix}.{args.PropertyName}");
            }
        };

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
