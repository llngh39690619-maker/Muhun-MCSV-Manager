using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Security;
using System.Reflection;
using System.Text.Json;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App;

public partial class App : Application
{
    private static readonly TimeSpan ExitRemoteCleanupTimeout = TimeSpan.FromSeconds(10);
    private SingleInstanceGuard? _singleInstanceGuard;
    private MainWindowViewModel? _mainViewModel;

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (MainWindow is MinecraftServerManager.App.MainWindow applicationWindow)
            {
                applicationWindow.PrepareForApplicationShutdown();
            }

            StopRemoteAccessForProcessExit();
        }
        finally
        {
            _singleInstanceGuard?.Dispose();
            _singleInstanceGuard = null;
            base.OnExit(e);
        }
    }

    internal static MainWindow CreateMainWindow(
        MainWindowViewModel viewModel,
        bool enableSystemTray,
        Func<IMainWindowTrayIcon>? trayIconFactory = null)
    {
        if (!enableSystemTray)
        {
            return new MainWindow(viewModel);
        }

        IMainWindowTrayIcon trayIcon;
        try
        {
            trayIcon = trayIconFactory is null
                ? new MainWindowTrayIcon()
                : trayIconFactory() ?? DisabledMainWindowTrayIcon.Instance;
        }
        catch (Exception)
        {
            trayIcon = DisabledMainWindowTrayIcon.Instance;
        }

        return new MainWindow(viewModel, trayIcon);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var smokeTest = e.Args.Any(argument => argument.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
        var closeTest = e.Args.Any(argument => argument.Equals("--close-test", StringComparison.OrdinalIgnoreCase));
        var onlineDialogSmokeTest = e.Args.Any(argument =>
            argument.Equals("--online-dialog-smoke-test", StringComparison.OrdinalIgnoreCase));
        var coreDialogSmokeTest = e.Args.Any(argument =>
            argument.Equals("--core-dialog-smoke-test", StringComparison.OrdinalIgnoreCase));
        var remoteDialogSmokeTest = e.Args.Any(argument =>
            argument.Equals("--remote-dialog-smoke-test", StringComparison.OrdinalIgnoreCase));
        var remoteAccountSmokeTest = e.Args.Any(argument =>
            argument.Equals("--remote-account-smoke-test", StringComparison.OrdinalIgnoreCase));
        var renderArgumentIndex = Array.FindIndex(e.Args, argument => argument.Equals("--render-preview", StringComparison.OrdinalIgnoreCase));
        var renderPreviewPath = renderArgumentIndex >= 0 && renderArgumentIndex + 1 < e.Args.Length
            ? Path.GetFullPath(e.Args[renderArgumentIndex + 1])
            : null;
        var renderClientArgumentIndex = Array.FindIndex(
            e.Args,
            argument => argument.Equals("--render-client-preview", StringComparison.OrdinalIgnoreCase));
        var renderClientPreviewPath = renderClientArgumentIndex >= 0 && renderClientArgumentIndex + 1 < e.Args.Length
            ? Path.GetFullPath(e.Args[renderClientArgumentIndex + 1])
            : null;
        var renderClientCatalogArgumentIndex = Array.FindIndex(
            e.Args,
            argument => argument.Equals("--render-client-catalog-preview", StringComparison.OrdinalIgnoreCase));
        var renderClientCatalogPreviewPath = renderClientCatalogArgumentIndex >= 0 &&
                                             renderClientCatalogArgumentIndex + 1 < e.Args.Length
            ? Path.GetFullPath(e.Args[renderClientCatalogArgumentIndex + 1])
            : null;
        var detectPackIndex = Array.FindIndex(e.Args, argument => argument.Equals("--detect-server-pack", StringComparison.OrdinalIgnoreCase));
        var detectPackPath = detectPackIndex >= 0 && detectPackIndex + 2 < e.Args.Length
            ? Path.GetFullPath(e.Args[detectPackIndex + 1])
            : null;
        var detectPackOutputPath = detectPackIndex >= 0 && detectPackIndex + 2 < e.Args.Length
            ? Path.GetFullPath(e.Args[detectPackIndex + 2])
            : null;
        var singleInstanceHoldIndex = Array.FindIndex(
            e.Args,
            argument => argument.Equals("--single-instance-hold-test", StringComparison.OrdinalIgnoreCase));
        var singleInstanceReadyPath = singleInstanceHoldIndex >= 0 && singleInstanceHoldIndex + 2 < e.Args.Length
            ? Path.GetFullPath(e.Args[singleInstanceHoldIndex + 1])
            : null;
        var singleInstanceReleasePath = singleInstanceHoldIndex >= 0 && singleInstanceHoldIndex + 2 < e.Args.Length
            ? Path.GetFullPath(e.Args[singleInstanceHoldIndex + 2])
            : null;
        var diagnosticMode = smokeTest
            || closeTest
            || onlineDialogSmokeTest
            || coreDialogSmokeTest
            || remoteDialogSmokeTest
            || remoteAccountSmokeTest
            || renderPreviewPath is not null
            || renderClientPreviewPath is not null
            || renderClientCatalogPreviewPath is not null
            || detectPackPath is not null
            || singleInstanceReadyPath is not null;
        PrimaryDisplayWindowPlacement.SuppressActivationForNewWindows = diagnosticMode;
        MainWindow? applicationWindow = null;
        ProductGuiActivationAcknowledgementRequest? activationAcknowledgementRequest = null;

        try
        {
            ProductGuiActivationAcknowledgement.TryParseRequest(
                e.Args,
                out activationAcknowledgementRequest);

            // Diagnostic commands remain self-contained for deterministic release QA. Interactive
            // launches must be the active GUI in an installer-owned A/B slot; all mutable data is
            // then bound to that selected install root with no LocalAppData/ProgramData fallback.
            var paths = diagnosticMode
                ? new ApplicationPaths(AppContext.BaseDirectory)
                : ApplicationPaths.CreateForCurrentInstallation();
            LocalizationService.Current.Initialize(paths.LanguageSettingsFile);
            _singleInstanceGuard = SingleInstanceGuard.TryAcquire(
                diagnosticMode ? AppContext.BaseDirectory : paths.Root);
            if (_singleInstanceGuard is null)
            {
                if (!diagnosticMode)
                {
                    DarkMessageBox.ShowStartupPrompt(
                        L("app.singleInstance.message"),
                        L("app.singleInstance.title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                Shutdown(3);
                return;
            }

            if (singleInstanceReadyPath is not null && singleInstanceReleasePath is not null)
            {
                var readyDirectory = Path.GetDirectoryName(singleInstanceReadyPath)
                    ?? throw new InvalidOperationException("單一實例診斷 ready 路徑沒有父資料夾。");
                Directory.CreateDirectory(readyDirectory);
                await File.WriteAllTextAsync(singleInstanceReadyPath, "ready");
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!File.Exists(singleInstanceReleasePath))
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new TimeoutException("單一實例跨程序診斷等候 release 逾時。");
                    }

                    await Task.Delay(25);
                }

                Shutdown(0);
                return;
            }

            if (detectPackPath is not null && detectPackOutputPath is not null)
            {
                var detector = new ServerPackDetector();
                var detection = await detector.DetectAsync(detectPackPath);
                Directory.CreateDirectory(Path.GetDirectoryName(detectPackOutputPath)!);
                await File.WriteAllTextAsync(
                    detectPackOutputPath,
                    JsonSerializer.Serialize(detection, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(detection.IsRecognized && detection.IsRunnable ? 0 : 2);
                return;
            }

            var viewModel = diagnosticMode
                ? new MainWindowViewModel(paths)
                : MainWindowViewModel.CreateServiceOwned(paths);
            _mainViewModel = viewModel;
            applicationWindow = CreateMainWindow(
                viewModel,
                enableSystemTray: !diagnosticMode);
            MainWindow = applicationWindow;
            if (!diagnosticMode)
            {
                applicationWindow.Show();
            }
            await viewModel.InitializeAsync(allowInteractiveAutoImport: !diagnosticMode);
            if (activationAcknowledgementRequest is not null)
            {
                if (!viewModel.IsProductServiceRuntime ||
                    !viewModel.IsProductServiceConnected ||
                    viewModel.ProductServiceNegotiatedApiVersion is not { } negotiatedApiVersion)
                {
                    throw new InvalidOperationException(
                        "GUI activation cannot be acknowledged until the Service is ready and API-compatible.");
                }

                await ProductGuiActivationAcknowledgement.SendReadyAsync(
                    activationAcknowledgementRequest,
                    GetRunningProductVersion(),
                    serviceReady: true,
                    negotiatedApiVersion);
            }

            if (onlineDialogSmokeTest)
            {
                var onlineSmokeViewModel = new OnlineModpackViewModel(new OnlineDialogSmokeWorkflow());
                await onlineSmokeViewModel.LoadFeaturedAsync(transientApiKey: null);
                var result = onlineSmokeViewModel.Results.Single();
                await onlineSmokeViewModel.SelectResultAsync(result, transientApiKey: null);
                if (onlineSmokeViewModel.Versions.Count != 1)
                {
                    throw new InvalidOperationException("線上模組包診斷未建立唯一的版本資料列。");
                }

                var dialog = new OnlineModpackDialog(onlineSmokeViewModel, loadFeaturedOnOpen: false);
                dialog.ContentRendered += (_, _) =>
                {
                    dialog.UpdateLayout();
                    EnsureListItemMaterialized(dialog, onlineSmokeViewModel.CatalogItems, "線上模組包結果卡片");
                    EnsureListItemMaterialized(dialog, onlineSmokeViewModel.Versions, "線上模組包版本");
                    dialog.Close();
                };
                dialog.ShowDialog();
            }

            if (coreDialogSmokeTest)
            {
                var coreSmokeViewModel = new CoreServerCreationViewModel(new CoreDialogSmokeWorkflow());
                await coreSmokeViewModel.InitializeAsync();
                var core = coreSmokeViewModel.Cores.Single();
                await coreSmokeViewModel.SelectCoreAsync(core);
                if (coreSmokeViewModel.Versions.Count != 1)
                {
                    throw new InvalidOperationException("核心建立器診斷未建立唯一的版本資料列。");
                }

                var dialog = new CoreServerCreationDialog(coreSmokeViewModel);
                dialog.ContentRendered += (_, _) =>
                {
                    dialog.UpdateLayout();
                    EnsureListItemMaterialized(dialog, coreSmokeViewModel.Versions, "核心版本");
                    dialog.Close();
                };
                dialog.ShowDialog();
            }

            if (remoteDialogSmokeTest)
            {
                if (!viewModel.OpenRemoteAccessCommand.CanExecute(null))
                {
                    throw new InvalidOperationException("手機遠端設定命令尚未就緒。");
                }

                // Owner assignment requires a Window that has been shown at least once. Keep
                // it hidden during diagnostics so a guarded dialog error cannot block CI with
                // an interactive MessageBox.
                applicationWindow.Show();
                applicationWindow.Hide();

                // Exercise the same command path used by the real main-window button. Runtime
                // WPF binding validation occurs only when the non-modal window is first shown.
                viewModel.OpenRemoteAccessCommand.Execute(null);
                var dialog = Windows
                    .OfType<RemoteAccessDialog>()
                    .SingleOrDefault(candidate => candidate.IsVisible)
                    ?? throw new InvalidOperationException(
                        $"手機遠端設定視窗未成功顯示。{viewModel.StatusMessage}");
                dialog.UpdateLayout();
                if (!dialog.IsLoaded || !dialog.IsVisible)
                {
                    throw new InvalidOperationException("手機遠端設定視窗未完成首次佈局。");
                }

                dialog.Close();
                if (dialog.IsVisible)
                {
                    throw new InvalidOperationException("手機遠端設定視窗未成功關閉。");
                }
            }

            if (remoteAccountSmokeTest)
            {
                if (!viewModel.OpenRemoteAccessCommand.CanExecute(null))
                {
                    throw new InvalidOperationException("手機遠端設定命令尚未就緒。");
                }

                applicationWindow.Show();
                applicationWindow.Hide();
                viewModel.OpenRemoteAccessCommand.Execute(null);
                var dialog = Windows
                    .OfType<RemoteAccessDialog>()
                    .SingleOrDefault(candidate => candidate.IsVisible)
                    ?? throw new InvalidOperationException(
                        $"手機遠端設定視窗未成功顯示。{viewModel.StatusMessage}");
                dialog.UpdateLayout();
                if (!dialog.IsLoaded || !dialog.IsVisible)
                {
                    throw new InvalidOperationException("手機遠端設定視窗未完成首次佈局。");
                }

                if (dialog.DataContext is not RemoteAccessSettingsViewModel remoteViewModel)
                {
                    throw new InvalidOperationException("手機遠端設定視窗沒有正確的資料模型。");
                }

                remoteViewModel.AccessMode = RemoteAccessMode.CloudflareQuickTunnel;
                remoteViewModel.RemoteUsername = "smoke01";
                remoteViewModel.RemotePin = "98765432";
                remoteViewModel.ConfirmedRemotePin = "98765432";
                if (!remoteViewModel.RegisterAccountCommand.CanExecute(null))
                {
                    throw new InvalidOperationException("Quick Tunnel 測試帳號尚不能建立。");
                }

                remoteViewModel.RegisterAccountCommand.Execute(null);
                var accountDeadline = DateTime.UtcNow.AddSeconds(15);
                while (remoteViewModel.IsBusy ||
                       !remoteViewModel.AccountRows.Any(row => row.Username == "smoke01"))
                {
                    if (remoteViewModel.HasProvisioningError)
                    {
                        throw new InvalidOperationException(
                            $"Quick Tunnel 測試帳號建立失敗：{remoteViewModel.ProvisioningError}");
                    }

                    if (DateTime.UtcNow >= accountDeadline)
                    {
                        throw new TimeoutException("Quick Tunnel 測試帳號建立與畫面更新逾時。");
                    }

                    await Task.Delay(25);
                }

                var account = remoteViewModel.AccountRows.Single(row => row.Username == "smoke01");
                if (account.IdentityText != "本機帳號" ||
                    account.PinDisplayText != "密碼：••••••••" ||
                    !account.TogglePinVisibilityCommand.CanExecute(null))
                {
                    throw new InvalidOperationException("Quick Tunnel 測試帳號的安全顯示內容不正確。");
                }

                account.TogglePinVisibilityCommand.Execute(null);
                if (account.PinDisplayText != "密碼：98765432")
                {
                    throw new InvalidOperationException("Quick Tunnel 測試帳號無法在本機顯示密碼。");
                }
                account.HideRevealedPin();
                if (account.PinDisplayText != "密碼：••••••••")
                {
                    throw new InvalidOperationException("Quick Tunnel 測試帳號未重新遮蔽密碼。");
                }

                dialog.Close();
                if (dialog.IsVisible)
                {
                    throw new InvalidOperationException("手機遠端設定視窗未成功關閉。");
                }
            }

            if (renderPreviewPath is not null)
            {
                RenderPreview(applicationWindow, renderPreviewPath);
            }

            if (renderClientPreviewPath is not null)
            {
                await viewModel.ShowClientWorkspaceForDiagnosticsAsync();
                RenderPreview(applicationWindow, renderClientPreviewPath);
            }

            if (renderClientCatalogPreviewPath is not null)
            {
                await viewModel.ShowClientCatalogForDiagnosticsAsync();
                await WaitForThumbnailRenderingAsync(applicationWindow);
                RenderPreview(applicationWindow, renderClientCatalogPreviewPath);
            }

            if (closeTest)
            {
                applicationWindow.Close();
                return;
            }

            if (diagnosticMode)
            {
                await viewModel.ShutdownAsync();
                applicationWindow.PrepareForApplicationShutdown();
                applicationWindow.Close();
                Shutdown(0);
            }
        }
        catch (Exception exception)
        {
            applicationWindow?.PrepareForApplicationShutdown();
            if (_mainViewModel is { } failedViewModel)
            {
                try
                {
                    // Initialization may fail before the normal Closing path exists. Dispose does
                    // not rewrite manager.json, but it does revoke and stop every remote resource.
                    await failedViewModel.DisposeAsync();
                }
                catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException)
                {
                    _ = cleanupError;
                    // Preserve the startup exception; OnExit still performs the idempotent,
                    // bounded remote-only fallback below.
                }
            }
            if (diagnosticMode)
            {
                await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "smoke-test-error.txt"), exception.ToString());
                Shutdown(-1);
                return;
            }

            DarkMessageBox.ShowStartupPrompt(
                L("app.startupFailed.message", exception.Message),
                L("app.startupFailed.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static string L(string key, params object?[] arguments)
        => LocalizationService.Current.Get(key, arguments);

    private static string GetRunningProductVersion()
    {
        var assembly = typeof(App).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2, StringSplitOptions.TrimEntries)[0];
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        var version = assembly.GetName().Version;
        if (version is not null)
        {
            return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }

        throw new InvalidOperationException("GUI assembly version metadata is unavailable.");
    }

    private void StopRemoteAccessForProcessExit()
    {
        var viewModel = _mainViewModel;
        if (viewModel is null)
        {
            return;
        }

        try
        {
            var cleanup = viewModel.EnsureRemoteAccessStoppedForApplicationExitAsync();
            if (cleanup.IsCompleted)
            {
                _ = cleanup.Exception;
                return;
            }

            // The remote-only cleanup path never awaits the dispatcher. A bounded worker wait
            // gives owned tunnels time to exit without letting a broken external command deadlock
            // WPF's synchronous OnExit callback.
            _ = Task.Run(async () => await cleanup.ConfigureAwait(false))
                .Wait(ExitRemoteCleanupTimeout);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _ = error;
            // The process is already exiting. Normal Closing/initialization-failure paths awaited
            // the same idempotent task; this final fallback must never prevent process teardown.
        }
    }

    private static void RenderPreview(Window window, string destinationPath)
    {
        const int width = 1480;
        const int height = 900;
        var root = PreparePreviewLayout(window, width, height);

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static async Task WaitForThumbnailRenderingAsync(Window window)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var root = PreparePreviewLayout(window, width: 1480, height: 900);
        await window.Dispatcher.InvokeAsync(
            window.UpdateLayout,
            DispatcherPriority.Loaded,
            timeout.Token);
        var thumbnailImages = FindVisualChildren<Image>(root)
            .Where(image => !string.IsNullOrWhiteSpace(LocalImageThumbnail.GetSourcePath(image)))
            .ToArray();
        if (thumbnailImages.Length == 0)
        {
            throw new InvalidOperationException(
                "Client catalog diagnostic did not materialize any artwork image controls.");
        }

        await Task.WhenAll(thumbnailImages.Select(
            image => LocalImageThumbnail.LoadForDiagnosticsAsync(image, timeout.Token)));
        var decodedCount = thumbnailImages.Count(static image => image.Source is not null);
        if (decodedCount == 0)
        {
            throw new InvalidOperationException(
                "Client catalog diagnostic could not decode any downloaded artwork.");
        }

        await LocalImageThumbnail.WaitForPendingLoadsAsync(timeout.Token);
        await window.Dispatcher.InvokeAsync(
            window.UpdateLayout,
            DispatcherPriority.Render,
            timeout.Token);
    }

    private static FrameworkElement PreparePreviewLayout(Window window, int width, int height)
    {
        if (window.Content is not FrameworkElement root)
        {
            throw new InvalidOperationException("主視窗沒有可渲染的內容。");
        }

        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        return root;
    }

    private static void EnsureListItemMaterialized(
        DependencyObject root,
        object expectedItemsSource,
        string label)
    {
        var list = FindVisualChildren<ListBox>(root)
            .SingleOrDefault(candidate => ReferenceEquals(candidate.ItemsSource, expectedItemsSource))
            ?? throw new InvalidOperationException($"找不到 {label} 清單。");
        list.UpdateLayout();
        if (list.Items.Count == 0 || list.ItemContainerGenerator.ContainerFromIndex(0) is null)
        {
            throw new InvalidOperationException($"{label} 的第一個 WPF 項目容器未建立。");
        }
    }

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

    private sealed class CoreDialogSmokeWorkflow : ICoreServerCreationWorkflow
    {
        private static readonly CoreServerProduct Product = new(
            CoreServerSoftware.Paper,
            "paper-smoke",
            "Paper",
            "WPF 核心版本列診斷");

        public Task<IReadOnlyList<CoreServerProduct>> GetAvailableCoresAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CoreServerProduct>>([Product]);

        public Task<IReadOnlyList<CoreServerVersion>> GetVersionsAsync(
            CoreServerProduct core,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CoreServerVersion>>(
            [
                new(
                    Product.CoreId,
                    "1.21.11-smoke",
                    "Paper 1.21.11 診斷版本",
                    "1.21.11",
                    "smoke",
                    DateTimeOffset.Parse("2026-08-17T00:00:00Z"),
                    IsRecommended: true)
            ]);

        public Task<ServerInstance> CreateAsync(
            CoreServerCreationRequest request,
            IProgress<CoreServerCreationProgress> progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("診斷模式不會建立 Server。");
    }

    private sealed class OnlineDialogSmokeWorkflow : IOnlineModpackWorkflow
    {
        private static readonly OnlineModpackSearchResult Project = new(
            OnlineModpackProvider.Ftb,
            "smoke-project",
            "FTB 診斷模組包",
            "WPF 推薦結果與版本列診斷",
            "Feed The Beast");

        public Task<IReadOnlyList<OnlineModpackSearchResult>> GetFeaturedAsync(
            OnlineModpackProvider provider,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>([Project]);

        public Task<IReadOnlyList<OnlineModpackSearchResult>> SearchAsync(
            OnlineModpackProvider provider,
            string query,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>([Project]);

        public Task<IReadOnlyList<OnlineModpackVersion>> GetVersionsAsync(
            OnlineModpackSearchResult project,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OnlineModpackVersion>>(
            [
                new(
                    OnlineModpackProvider.Ftb,
                    Project.ProjectId,
                    "smoke-version",
                    "1.0.0 診斷版本",
                    "1.21.1",
                    "NeoForge 21.1.248",
                    "release",
                    DateTimeOffset.Parse("2026-08-17T00:00:00Z"),
                    HasOfficialServerPack: true)
            ]);

        public Task<ServerInstance> InstallAsync(
            OnlineModpackInstallRequest request,
            SecureString? transientApiKey,
            IProgress<OnlineModpackInstallProgress> progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("診斷模式不會安裝模組包。");
    }
}
