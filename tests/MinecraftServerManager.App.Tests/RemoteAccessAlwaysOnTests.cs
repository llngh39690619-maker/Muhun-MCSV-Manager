using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using System.Xml.Linq;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Remote;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class RemoteAccessAlwaysOnTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Apply_AlwaysPersistsEnabledAutoStartIntent()
    {
        WpfStaTestHost.Run(() =>
        {
            RemoteControlSettings? persisted = null;
            var coordinator = new RemoteAccessCoordinator(new NeverUsedRemoteBackend());
            try
            {
                using var viewModel = new RemoteAccessSettingsViewModel(
                    CreateValidSettings(enabled: false),
                    coordinator,
                    settings =>
                    {
                        persisted = settings.Copy();
                        return Task.FromException(
                            new InvalidOperationException("stop before network start"));
                    },
                    Dispatcher.CurrentDispatcher);

                Assert.True(viewModel.CanApply);
                Assert.True(viewModel.ApplyCommand.CanExecute(null));

                viewModel.ApplyCommand.Execute(null);
                PumpDispatcherUntil(() => !viewModel.IsBusy && viewModel.HasError);

                Assert.NotNull(persisted);
                Assert.True(persisted.Enabled);
                Assert.True(viewModel.Enabled);
                Assert.Contains("無法套用", viewModel.StatusMessage, StringComparison.Ordinal);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void ApplyAfterApplicationShutdown_DoesNotPersistOrStartTunnel()
    {
        WpfStaTestHost.Run(() =>
        {
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"mcsv-remote-shutdown-race-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            var cloudflaredPath = Path.Combine(temporaryDirectory, "cloudflared.exe");
            File.WriteAllBytes(cloudflaredPath, []);
            var persistCount = 0;
            var tunnelFactoryCalls = 0;
            using var applicationStopping = new CancellationTokenSource();
            applicationStopping.Cancel();
            var coordinator = new RemoteAccessCoordinator(
                new NeverUsedRemoteBackend(),
                quickTunnelFactory: _ =>
                {
                    tunnelFactoryCalls++;
                    throw new InvalidOperationException("A shutdown application must not restart Web.");
                });
            try
            {
                using var viewModel = new RemoteAccessSettingsViewModel(
                    new RemoteControlSettings
                    {
                        Enabled = true,
                        AccessMode = RemoteAccessMode.CloudflareQuickTunnel,
                        CloudflaredExecutablePath = cloudflaredPath,
                    },
                    coordinator,
                    _ =>
                    {
                        persistCount++;
                        return Task.CompletedTask;
                    },
                    Dispatcher.CurrentDispatcher,
                    new RemoteAccessSessionState(),
                    applicationStopping.Token);

                Assert.True(viewModel.ApplyCommand.CanExecute(null));
                viewModel.ApplyCommand.Execute(null);
                PumpDispatcherUntil(() => !viewModel.IsBusy && viewModel.HasError);

                Assert.Equal(0, persistCount);
                Assert.Equal(0, tunnelFactoryCalls);
                Assert.Contains("無法套用", viewModel.StatusMessage, StringComparison.Ordinal);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        });
    }

    [Fact]
    public void CloseWeb_IsRuntimeOnlyAndKeepsNextLaunchEnabled()
    {
        WpfStaTestHost.Run(() =>
        {
            var persistCount = 0;
            var coordinator = new RemoteAccessCoordinator(new NeverUsedRemoteBackend());
            try
            {
                using var viewModel = new RemoteAccessSettingsViewModel(
                    CreateValidSettings(enabled: true),
                    coordinator,
                    _ =>
                    {
                        persistCount++;
                        return Task.CompletedTask;
                    },
                    Dispatcher.CurrentDispatcher);

                Assert.True(viewModel.StopCommand.CanExecute(null));
                viewModel.StopCommand.Execute(null);
                PumpDispatcherUntil(() =>
                    !viewModel.IsBusy
                    && viewModel.StatusMessage.Contains("已停止", StringComparison.Ordinal));

                Assert.Equal(0, persistCount);
                Assert.True(viewModel.Enabled);
                Assert.False(viewModel.IsRunning);
                Assert.Equal("本次已關閉", viewModel.RemoteServiceStatusText);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void CoordinatorMaintenanceStop_DoesNotInventManualCloseIntent()
    {
        WpfStaTestHost.Run(() =>
        {
            var sessionState = new RemoteAccessSessionState();
            var coordinator = new RemoteAccessCoordinator(new NeverUsedRemoteBackend());
            try
            {
                using var viewModel = new RemoteAccessSettingsViewModel(
                    CreateValidSettings(enabled: true),
                    coordinator,
                    _ => Task.CompletedTask,
                    Dispatcher.CurrentDispatcher,
                    sessionState);

                coordinator.StopAsync(disableOwnedServe: true).GetAwaiter().GetResult();
                PumpDispatcherUntil(() =>
                    viewModel.StatusMessage.Contains("已停止", StringComparison.Ordinal));

                Assert.False(sessionState.IsStoppedForCurrentRun);
                Assert.Equal("等待重新連線", viewModel.RemoteServiceStatusText);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void ClosingSettingsWindow_OnlyRaisesCloseRequest()
    {
        WpfStaTestHost.Run(() =>
        {
            var persistCount = 0;
            var closeCount = 0;
            var coordinator = new RemoteAccessCoordinator(new NeverUsedRemoteBackend());
            try
            {
                using var viewModel = new RemoteAccessSettingsViewModel(
                    CreateValidSettings(enabled: true),
                    coordinator,
                    _ =>
                    {
                        persistCount++;
                        return Task.CompletedTask;
                    },
                    Dispatcher.CurrentDispatcher);
                viewModel.CloseRequested += (_, _) => closeCount++;

                viewModel.CloseCommand.Execute(null);

                Assert.Equal(1, closeCount);
                Assert.Equal(0, persistCount);
                Assert.True(viewModel.Enabled);
                Assert.Equal("等待重新連線", viewModel.RemoteServiceStatusText);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void AccountMaintenance_DoesNotReopenWebAfterRuntimeClose()
    {
        WpfStaTestHost.Run(() =>
        {
            var tunnelFactoryCalls = 0;
            var coordinator = new RemoteAccessCoordinator(
                new NeverUsedRemoteBackend(),
                quickTunnelFactory: _ =>
                {
                    tunnelFactoryCalls++;
                    throw new InvalidOperationException("Web must remain closed for this run.");
                });
            try
            {
                using var viewModel = new RemoteAccessSettingsViewModel(
                    new RemoteControlSettings
                    {
                        Enabled = true,
                        AccessMode = RemoteAccessMode.CloudflareQuickTunnel,
                        CloudflaredExecutablePath = @"C:\Tools\cloudflared.exe",
                    },
                    coordinator,
                    _ => Task.CompletedTask,
                    Dispatcher.CurrentDispatcher);

                viewModel.StopCommand.Execute(null);
                PumpDispatcherUntil(() =>
                    !viewModel.IsBusy
                    && viewModel.RemoteServiceStatusText == "本次已關閉");

                viewModel.RemoteUsername = "account1";
                viewModel.RemotePin = "12345678";
                viewModel.ConfirmedRemotePin = "12345678";
                Assert.True(viewModel.RegisterAccountCommand.CanExecute(null));
                viewModel.RegisterAccountCommand.Execute(null);
                PumpDispatcherUntil(() =>
                    !viewModel.IsBusy && viewModel.AccountRows.Count == 1);

                Assert.Equal(0, tunnelFactoryCalls);
                Assert.True(viewModel.Enabled);
                Assert.False(viewModel.IsRunning);
                Assert.Equal("本次已關閉", viewModel.RemoteServiceStatusText);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void WebConsoleClose_RemainsStoppedAcrossReopenedSettingsAndAccountRegistration()
    {
        WpfStaTestHost.Run(() =>
        {
            var tunnelFactoryCalls = 0;
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"mcsv-shared-remote-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            var cloudflaredPath = Path.Combine(temporaryDirectory, "cloudflared.exe");
            File.WriteAllBytes(cloudflaredPath, []);
            var sessionState = new RemoteAccessSessionState();
            var coordinator = new RemoteAccessCoordinator(
                new NeverUsedRemoteBackend(),
                quickTunnelFactory: _ =>
                {
                    tunnelFactoryCalls++;
                    throw new InvalidOperationException("Only an explicit reconnect may start Web.");
                });
            try
            {
                // This is the process-local action performed by the Web console's Close Web
                // delegate before the settings dialog is opened or reopened.
                sessionState.MarkStoppedForCurrentRun();
                using (var firstDialog = new RemoteAccessSettingsViewModel(
                           new RemoteControlSettings
                           {
                               Enabled = true,
                               AccessMode = RemoteAccessMode.CloudflareQuickTunnel,
                               CloudflaredExecutablePath = cloudflaredPath,
                           },
                           coordinator,
                           _ => Task.CompletedTask,
                           Dispatcher.CurrentDispatcher,
                           sessionState))
                {
                    Assert.Equal("本次已關閉", firstDialog.RemoteServiceStatusText);
                }

                using var reopenedDialog = new RemoteAccessSettingsViewModel(
                    new RemoteControlSettings
                    {
                        Enabled = true,
                        AccessMode = RemoteAccessMode.CloudflareQuickTunnel,
                        CloudflaredExecutablePath = cloudflaredPath,
                    },
                    coordinator,
                    _ => Task.CompletedTask,
                    Dispatcher.CurrentDispatcher,
                    sessionState);
                reopenedDialog.RemoteUsername = "account2";
                reopenedDialog.RemotePin = "12345678";
                reopenedDialog.ConfirmedRemotePin = "12345678";

                reopenedDialog.RegisterAccountCommand.Execute(null);
                PumpDispatcherUntil(() =>
                    !reopenedDialog.IsBusy && reopenedDialog.AccountRows.Count == 1);

                Assert.True(sessionState.IsStoppedForCurrentRun);
                Assert.Equal("本次已關閉", reopenedDialog.RemoteServiceStatusText);
                Assert.Equal(0, tunnelFactoryCalls);

                reopenedDialog.ApplyCommand.Execute(null);
                PumpDispatcherUntil(() => !reopenedDialog.IsBusy && reopenedDialog.HasError);

                Assert.False(sessionState.IsStoppedForCurrentRun);
                Assert.Equal(1, tunnelFactoryCalls);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        });
    }

    [Fact]
    public void WebConsole_ReconnectStartsDirectlyAndCloseRemainsAvailableWhenStopped()
    {
        WpfStaTestHost.Run(() =>
        {
            var startCount = 0;
            var stopCount = 0;
            var coordinator = new RemoteAccessCoordinator(new NeverUsedRemoteBackend());
            try
            {
                using var viewModel = new RemoteWebConsoleViewModel(
                    coordinator,
                    () =>
                    {
                        startCount++;
                        return Task.CompletedTask;
                    },
                    () =>
                    {
                        stopCount++;
                        return Task.CompletedTask;
                    },
                    Dispatcher.CurrentDispatcher);

                Assert.Equal("本次已關閉", viewModel.StateText);
                Assert.True(viewModel.StopCommand.CanExecute(null));

                viewModel.ReconnectCommand.Execute(null);
                Assert.Equal(1, startCount);
                Assert.Equal(0, stopCount);

                viewModel.StopCommand.Execute(null);
                Assert.Equal(1, stopCount);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void RemoteDialogs_ExposeAlwaysOnCopyAndOnlyExplicitRuntimeClose()
    {
        var settingsDialog = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "RemoteAccessDialog.xaml")));
        var webConsole = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "RemoteWebConsoleDialog.xaml")));

        Assert.DoesNotContain(
            settingsDialog.Descendants(Presentation + "CheckBox"),
            element => ((string?)element.Attribute("IsChecked"))?.Contains(
                           "Enabled",
                           StringComparison.Ordinal) == true);
        Assert.Contains(
            settingsDialog.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text")
                       == "{DynamicResource L10n.remote.legacy.ui.autoConnectHint}");
        AssertButton(
            settingsDialog,
            "{DynamicResource L10n.remote.closeWeb}",
            "{Binding StopCommand}");
        AssertButton(
            settingsDialog,
            "{DynamicResource L10n.remote.reconnect}",
            "{Binding ApplyCommand}");
        AssertButton(
            webConsole,
            "{DynamicResource L10n.remote.console.stopWeb}",
            "{Binding StopCommand}");
        AssertButton(
            webConsole,
            "{DynamicResource L10n.remote.console.reconnect}",
            "{Binding ReconnectCommand}");
        Assert.Contains(
            webConsole.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text")
                       == "{DynamicResource L10n.remote.console.lifecycleHint}");
    }

    private static RemoteControlSettings CreateValidSettings(bool enabled) => new()
    {
        Enabled = enabled,
        AccessMode = RemoteAccessMode.Tailscale,
        AllowedLogin = "owner@gmail.com",
        LocalPort = RemoteControlSettings.DefaultLocalPort,
    };

    private static void AssertButton(XDocument document, string content, string command)
        => Assert.Contains(
            document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content") == content
                       && (string?)element.Attribute("Command") == command);

    private static void PumpDispatcherUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("WPF dispatcher condition timed out.");
            }

            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                () => frame.Continue = false,
                DispatcherPriority.Background);
            Dispatcher.PushFrame(frame);
        }
    }

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);

    private sealed class NeverUsedRemoteBackend : IRemoteControlBackend
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
