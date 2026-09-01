using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Client;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class MainWindowProductServiceIntegrationTests
{
    [Fact]
    public async Task ServiceOwnedProperties_AutoLoadReloadAndSaveThroughApi17WithoutProjectionPathAccess()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var serviceId = Guid.NewGuid();
        var client = new StubServiceClient(serviceId)
        {
            ServerPropertiesText = "motd=service owned\nserver-port=25565\n",
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        await client.PropertiesRead.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var projected = Assert.Single(viewModel.Servers);
        await WaitUntilAsync(() => projected.ServerPropertiesText == client.ServerPropertiesText);
        Assert.Same(projected, viewModel.SelectedServer);
        Assert.Equal(client.ServerPropertiesText, projected.ServerPropertiesText);
        Assert.False(projected.CanAccessLocalFiles);
        Assert.False(File.Exists(Path.Combine(projected.DirectoryPath, "server.properties")));
        Assert.True(viewModel.SupportsProductServicePropertiesEditor);
        Assert.True(viewModel.CanEditSelectedServerProperties);
        Assert.True(viewModel.ReloadPropertiesCommand.CanExecute(null));
        Assert.True(viewModel.SavePropertiesCommand.CanExecute(null));

        client.ReplaceServerPropertiesExternally("motd=reloaded\nserver-port=25566\n");
        viewModel.ReloadPropertiesCommand.Execute(null);
        await client.PropertiesReloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => projected.ServerPropertiesText == client.ServerPropertiesText);
        Assert.Equal("motd=reloaded\nserver-port=25566\n", projected.ServerPropertiesText);
        Assert.Equal(2, client.ServerPropertiesReadCount);

        projected.ServerPropertiesText = "motd=updated\nserver-port=25570\n";
        var releaseUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PropertiesUpdateRelease = releaseUpdate;
        viewModel.SavePropertiesCommand.Execute(null);
        await client.PropertiesUpdated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsSelectedServerPropertiesOperationRunning);
        Assert.False(viewModel.CanEditSelectedServerProperties);
        Assert.False(viewModel.CanSaveSelectedServerProperties);
        Assert.False(viewModel.ReloadPropertiesCommand.CanExecute(null));
        Assert.False(viewModel.SavePropertiesCommand.CanExecute(null));

        releaseUpdate.SetResult();
        await WaitUntilAsync(() => projected.Port == 25570);
        await WaitUntilAsync(() => !viewModel.IsSelectedServerPropertiesOperationRunning);

        Assert.Equal(projected.ServerPropertiesText, client.ServerPropertiesText);
        Assert.Equal(25570, client.StoredRegistration.Port);
        Assert.Equal(2, client.ServerPropertiesReadCount);

        viewModel.StartSelectedCommand.Execute(null);
        await WaitUntilAsync(() => projected.State == ServerState.Running);
        Assert.True(viewModel.CanEditSelectedServerProperties);
        Assert.True(viewModel.CanReloadSelectedServerProperties);
        Assert.False(viewModel.CanSaveSelectedServerProperties);
        Assert.False(viewModel.SavePropertiesCommand.CanExecute(null));
        Assert.False(viewModel.CanEditSelectedInstanceConfiguration);
        Assert.False(viewModel.CanSaveSelectedInstanceConfiguration);
        Assert.False(viewModel.SaveSelectedSettingsCommand.CanExecute(null));
        Assert.True(viewModel.HasSelectedInstanceConfigurationStatus);
    }

    [Fact]
    public async Task ServiceOwnedProperties_OverlappingReloadKeepsNewestReselectionResponse()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var serviceId = Guid.NewGuid();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubServiceClient(serviceId);
        client.ServerPropertiesReadHandler = async (call, cancellationToken) =>
        {
            if (call == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                firstReturned.TrySetResult();
                return client.CreatePropertiesDocument("motd=obsolete\nserver-port=25565\n");
            }

            secondReturned.TrySetResult();
            return client.CreatePropertiesDocument("motd=current\nserver-port=25566\n");
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var projected = Assert.Single(viewModel.Servers);
        Assert.True(viewModel.IsSelectedServerPropertiesOperationRunning);
        Assert.False(viewModel.CanEditSelectedServerProperties);
        Assert.False(viewModel.CanSaveSelectedServerProperties);
        Assert.False(viewModel.ReloadPropertiesCommand.CanExecute(null));

        viewModel.SelectedServer = null;
        viewModel.SelectedServer = projected;
        await secondReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => projected.ServerPropertiesText.Contains("motd=current", StringComparison.Ordinal));

        Assert.True(viewModel.CanEditSelectedServerProperties);
        Assert.True(viewModel.CanReloadSelectedServerProperties);
        Assert.True(viewModel.CanSaveSelectedServerProperties);

        releaseFirst.SetResult();
        await firstReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.Contains("motd=current", projected.ServerPropertiesText, StringComparison.Ordinal);
        Assert.DoesNotContain("motd=obsolete", projected.ServerPropertiesText, StringComparison.Ordinal);
        Assert.True(viewModel.CanEditSelectedServerProperties);
        Assert.True(viewModel.CanSaveSelectedServerProperties);
    }

    [Fact]
    public async Task ServiceOwnedProperties_ReloadClearsReadyStateUntilFreshRevisionArrives()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var serviceId = Guid.NewGuid();
        var reloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubServiceClient(serviceId)
        {
            ServerPropertiesText = "motd=initial\nserver-port=25565\n",
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        await client.PropertiesRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var projected = Assert.Single(viewModel.Servers);
        await WaitUntilAsync(() => viewModel.CanSaveSelectedServerProperties);

        client.ServerPropertiesReadHandler = async (call, cancellationToken) =>
        {
            Assert.Equal(2, call);
            reloadStarted.TrySetResult();
            await releaseReload.Task.WaitAsync(cancellationToken);
            return client.CreatePropertiesDocument("motd=fresh\nserver-port=25566\n");
        };

        viewModel.ReloadPropertiesCommand.Execute(null);
        await reloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsSelectedServerPropertiesOperationRunning);
        Assert.False(viewModel.CanEditSelectedServerProperties);
        Assert.False(viewModel.CanSaveSelectedServerProperties);
        Assert.False(viewModel.ReloadPropertiesCommand.CanExecute(null));
        Assert.False(viewModel.SavePropertiesCommand.CanExecute(null));

        releaseReload.SetResult();
        await WaitUntilAsync(() => projected.ServerPropertiesText.Contains("motd=fresh", StringComparison.Ordinal));
        await WaitUntilAsync(() => !viewModel.IsSelectedServerPropertiesOperationRunning);

        Assert.True(viewModel.CanReloadSelectedServerProperties);
        Assert.True(viewModel.CanEditSelectedServerProperties);
        Assert.True(viewModel.CanSaveSelectedServerProperties);
        Assert.True(viewModel.ReloadPropertiesCommand.CanExecute(null));
        Assert.True(viewModel.SavePropertiesCommand.CanExecute(null));
    }

    [Fact]
    public async Task ServiceOwnedProperties_TransientReadFailureShowsInlineStatusAndKeepsReloadAvailable()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var retryReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubServiceClient(Guid.NewGuid())
        {
            ServerPropertiesText = "motd=initial\nserver-port=25565\n",
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        await client.PropertiesRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => viewModel.CanSaveSelectedServerProperties);

        client.ServerPropertiesReadHandler = (call, _) => call switch
        {
            2 => Task.FromException<ProductServerPropertiesDocument>(
                new IOException("temporary read failure")),
            _ => RetryAsync(),
        };

        viewModel.ReloadPropertiesCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsSelectedServerPropertiesOperationRunning);

        Assert.True(viewModel.HasSelectedServerPropertiesStatus);
        Assert.Contains("server.properties", viewModel.SelectedServerPropertiesStatusText, StringComparison.Ordinal);
        Assert.True(viewModel.CanReloadSelectedServerProperties);
        Assert.True(viewModel.ReloadPropertiesCommand.CanExecute(null));
        Assert.False(viewModel.CanEditSelectedServerProperties);
        Assert.False(viewModel.CanSaveSelectedServerProperties);

        viewModel.ReloadPropertiesCommand.Execute(null);
        await retryReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.HasSelectedServerPropertiesStatus);

        Assert.True(viewModel.CanEditSelectedServerProperties);
        Assert.True(viewModel.CanSaveSelectedServerProperties);

        Task<ProductServerPropertiesDocument> RetryAsync()
        {
            retryReturned.TrySetResult();
            return Task.FromResult(client.CreatePropertiesDocument(
                "motd=recovered\nserver-port=25565\n"));
        }
    }

    [Fact]
    public async Task Api16Service_RemainsConnectedButPropertiesEditorFailsClosed()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var client = new StubServiceClient(Guid.NewGuid())
        {
            MaximumApiVersion = ProductApiProtocol.MinecraftEulaConsentVersion,
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        Assert.True(viewModel.IsProductServiceConnected);
        Assert.Equal(ProductApiProtocol.MinecraftEulaConsentVersion, viewModel.ProductServiceNegotiatedApiVersion);
        Assert.False(viewModel.SupportsProductServicePropertiesEditor);
        Assert.False(viewModel.SupportsProductServiceInstanceSettings);
        Assert.False(viewModel.CanEditSelectedInstanceConfiguration);
        Assert.False(viewModel.CanSaveSelectedInstanceConfiguration);
        Assert.False(viewModel.CanEditSelectedServerProperties);
        Assert.False(viewModel.ReloadPropertiesCommand.CanExecute(null));
        Assert.False(viewModel.SavePropertiesCommand.CanExecute(null));
        Assert.True(viewModel.HasSelectedServerPropertiesStatus);
        Assert.True(viewModel.HasSelectedInstanceConfigurationStatus);
        Assert.True(viewModel.ShowProductServiceUpdateAction);
        Assert.True(viewModel.UpdateProductServiceCommand.CanExecute(null));
        Assert.False(client.PropertiesRead.Task.IsCompleted);
    }

    [Fact]
    public async Task Api16ConnectedService_UpdateActionReprobesCurrentApiAndAutomaticallyLoadsProperties()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var client = new StubServiceClient(Guid.NewGuid())
        {
            MaximumApiVersion = ProductApiProtocol.MinecraftEulaConsentVersion,
            ServerPropertiesText = "motd=loaded after update\nserver-port=25565\n",
        };
        var launcher = new StubProductServiceUpdateLauncher(() =>
        {
            client.MaximumApiVersion = ProductApiProtocol.CurrentVersion;
            return new BundledProductServiceUpdateResult(
                BundledProductServiceUpdateOutcome.Completed,
                0);
        });
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(
            paths,
            client,
            productServiceUpdateLauncher: launcher);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        Assert.True(viewModel.IsProductServiceConnected);
        Assert.True(viewModel.ShowProductServiceUpdateAction);
        Assert.False(client.PropertiesRead.Task.IsCompleted);

        await viewModel.UpdateProductServiceAsync();
        await client.PropertiesRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var projected = Assert.Single(viewModel.Servers);
        await WaitUntilAsync(() => projected.ServerPropertiesText.Contains("loaded after update", StringComparison.Ordinal));

        Assert.Equal(1, launcher.InvocationCount);
        Assert.Equal(ProductApiProtocol.CurrentVersion, viewModel.ProductServiceNegotiatedApiVersion);
        Assert.True(viewModel.SupportsProductServicePropertiesEditor);
        Assert.True(viewModel.SupportsProductServiceInstanceSettings);
        Assert.False(viewModel.ShowProductServiceUpdateAction);
        Assert.False(viewModel.HasSelectedServerPropertiesStatus);
        Assert.True(viewModel.CanEditSelectedServerProperties);
    }

    [Fact]
    public async Task Api17Service_KeepsPropertiesAvailableButInstanceSettingsRequireUpdate()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var client = new StubServiceClient(Guid.NewGuid())
        {
            MaximumApiVersion = ProductApiProtocol.ServerPropertiesEditorVersion,
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        await client.PropertiesRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => viewModel.CanEditSelectedServerProperties);

        Assert.True(viewModel.SupportsProductServicePropertiesEditor);
        Assert.False(viewModel.SupportsProductServiceInstanceSettings);
        Assert.True(viewModel.CanEditSelectedServerProperties);
        Assert.False(viewModel.CanEditSelectedInstanceConfiguration);
        Assert.False(viewModel.CanSaveSelectedInstanceConfiguration);
        Assert.False(viewModel.SaveSelectedSettingsCommand.CanExecute(null));
        Assert.True(viewModel.HasSelectedInstanceConfigurationStatus);
        Assert.True(viewModel.ShowProductServiceUpdateAction);
        Assert.True(viewModel.UpdateProductServiceCommand.CanExecute(null));
    }

    [Fact]
    public async Task Initialize_UsesOnlyServiceCatalogAndKeepsLegacySettingsReadOnly()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var legacy = new ServerInstance
        {
            Id = Guid.NewGuid(),
            Name = "Legacy only",
            DirectoryPath = Path.Combine(paths.Servers, "legacy"),
        };
        using (var store = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile))
        {
            await store.SaveAsync(new ManagerSettings { Instances = [legacy] });
        }

        var serviceId = Guid.NewGuid();
        var client = new StubServiceClient(serviceId)
        {
            AdministrationSnapshot = new ProductServerAdministrationSnapshot(
                serviceId,
                DateTimeOffset.UtcNow,
                true,
                [],
                false,
                new ProductServerJavaRuntimeSummary(true, true, 21, "21.0.8", "JRE", "Temurin", "x64")),
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        var projected = Assert.Single(viewModel.Servers);
        Assert.Equal(serviceId, projected.Id);
        Assert.True(projected.IsServiceManaged);
        Assert.False(projected.CanAccessLocalFiles);
        Assert.Equal(21, projected.Model.JavaMajorVersion);
        Assert.Equal("Java 21", projected.JavaDisplay);
        Assert.Empty(viewModel.InstalledJavaRuntimes);
        viewModel.SelectedWorkspaceTabKey = MainWindowViewModel.JavaRuntimeWorkspaceTabKey;
        await viewModel.LastAddonScan.WaitAsync(TimeSpan.FromSeconds(5));
        var runtime = Assert.Single(viewModel.InstalledJavaRuntimes);
        Assert.Equal("Java 21", runtime.MajorDisplay);
        Assert.Equal("21.0.8 · JRE · x64", runtime.ExecutablePath);
        Assert.DoesNotContain("temurin-21/bin", runtime.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.DownloadJavaCommand.CanExecute(null));
        Assert.Equal(
            ProductApiProtocol.CurrentVersion,
            viewModel.ProductServiceNegotiatedApiVersion);
        Assert.DoesNotContain(viewModel.Servers, server => server.Id == legacy.Id);
        await viewModel.ShutdownAsync();
        var stored = await ReadSettingsAsync(paths);
        Assert.Contains(stored.Instances, server => server.Id == legacy.Id);
    }

    [Fact]
    public async Task Initialize_WhenServiceUnavailable_DoesNotFallBackToLegacyProcessOwnership()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        using (var store = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile))
        {
            await store.SaveAsync(new ManagerSettings
            {
                Instances =
                [
                    new ServerInstance
                    {
                        Id = Guid.NewGuid(),
                        Name = "Must stay inactive",
                        DirectoryPath = Path.Combine(paths.Servers, "inactive"),
                    },
                ],
            });
        }
        var client = new StubServiceClient(Guid.NewGuid())
        {
            HandshakeError = new ProductServiceClientException(
                "service.connection_failed",
                "not installed"),
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        Assert.Empty(viewModel.Servers);
        Assert.False(viewModel.IsProductServiceConnected);
        Assert.Null(viewModel.ProductServiceNegotiatedApiVersion);
        Assert.Contains("保持唯讀", viewModel.ProductServiceConnectionText);
        Assert.False(viewModel.StartSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task CultureChange_RefreshesServiceConnectionTextWithoutConnectionStateChange()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        LocalizationService.Current.Initialize(
            paths.LanguageSettingsFile,
            CultureInfo.GetCultureInfo("zh-TW"));
        var client = new StubServiceClient(Guid.NewGuid())
        {
            HandshakeError = new ProductServiceClientException(
                "service.connection_failed",
                "not installed"),
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var changed = 0;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(viewModel.ProductServiceConnectionText))
            {
                changed++;
            }
        };

        LocalizationService.Current.SetCulture("en-US");

        Assert.True(changed > 0);
        Assert.Contains("read-only", viewModel.ProductServiceConnectionText, StringComparison.OrdinalIgnoreCase);
        LocalizationService.Current.SetCulture("zh-TW");
    }

    [Fact]
    public async Task LifecycleAndCommand_AreRoutedToServiceAndGuiShutdownDoesNotStopServer()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        var serviceId = Guid.NewGuid();
        var client = new StubServiceClient(serviceId);
        var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        await viewModel.StartServerForRemoteAsync(serviceId, CancellationToken.None);
        await viewModel.SendCommandForRemoteAsync(serviceId, "say hello", CancellationToken.None);
        await viewModel.RestartServerForRemoteAsync(serviceId, CancellationToken.None);
        await viewModel.ShutdownAsync();

        Assert.Equal(["start", "command:say hello", "restart"], client.Mutations);
        Assert.DoesNotContain("stop", client.Mutations);
        Assert.True(viewModel.KeepsRunningServersOnGuiExit);
    }

    [Fact]
    public async Task ServiceProjection_UsesAuthoritativeRegistrationAndSavesEditableFields()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        var serviceId = Guid.NewGuid();
        var client = new StubServiceClient(serviceId);
        await using (var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client))
        {
            await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
            var projected = Assert.Single(viewModel.Servers);

            Assert.Equal(client.StoredRegistration.MinimumMemoryMb, projected.MinimumMemoryMb);
            Assert.Equal(client.StoredRegistration.MaximumMemoryMb, projected.MaximumMemoryMb);
            Assert.Equal(client.StoredRegistration.AutoRestart, projected.AutoRestart);
            Assert.True(viewModel.SupportsProductServiceInstanceSettings);
            Assert.True(viewModel.CanEditSelectedInstanceConfiguration);
            Assert.True(viewModel.CanSaveSelectedInstanceConfiguration);
            Assert.True(viewModel.SaveSelectedSettingsCommand.CanExecute(null));
            Assert.True(viewModel.DeleteServerCommand.CanExecute(projected));
            Assert.True(viewModel.OpenSelectedFolderCommand.CanExecute(null));
            Assert.True(projected.IsControlChannelAvailable);

            projected.Name = "Edited display";
            projected.Port = 25577;
            projected.IsMemoryManual = true;
            projected.MinimumMemoryMb = 2048;
            projected.MaximumMemoryMb = 6144;
            projected.AutoRestart = false;
            projected.SeparateDiagnosticOutput = true;
            projected.EnableHangWatchdog = true;
            projected.WatchdogCheckIntervalSeconds = 45;
            projected.WatchdogProbeTimeoutSeconds = 9;
            projected.WatchdogFailureThreshold = 4;
            projected.WatchdogStartupGraceSeconds = 240;
            projected.EnableAutomaticRecoveryPoints = true;
            projected.RecoveryPointIntervalMinutes = 60;
            projected.RecoveryPointRetentionCount = 5;
            var releaseUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.RegistrationUpdateRelease = releaseUpdate;
            viewModel.SaveSelectedSettingsCommand.Execute(null);
            await client.RegistrationUpdated.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(viewModel.IsSelectedInstanceConfigurationSaveRunning);
            Assert.False(viewModel.CanEditSelectedInstanceConfiguration);
            Assert.False(viewModel.CanSaveSelectedInstanceConfiguration);
            Assert.False(viewModel.SaveSelectedSettingsCommand.CanExecute(null));

            releaseUpdate.SetResult();
            await WaitUntilAsync(() => !viewModel.IsSelectedInstanceConfigurationSaveRunning);

            Assert.Equal("Edited display", client.StoredRegistration.Name);
            Assert.Equal(25577, client.StoredRegistration.Port);
            Assert.Equal(ProductServerMemoryAllocationMode.Manual, client.StoredRegistration.MemoryAllocationMode);
            Assert.Equal(2048, client.StoredRegistration.MinimumMemoryMb);
            Assert.Equal(6144, client.StoredRegistration.MaximumMemoryMb);
            Assert.False(client.StoredRegistration.AutoRestart);
            Assert.True(client.StoredRegistration.SeparateDiagnosticOutput);
            Assert.True(client.StoredRegistration.EnableHangWatchdog);
            Assert.Equal(45, client.StoredRegistration.WatchdogCheckIntervalSeconds);
            Assert.Equal(9, client.StoredRegistration.WatchdogProbeTimeoutSeconds);
            Assert.Equal(4, client.StoredRegistration.WatchdogFailureThreshold);
            Assert.Equal(240, client.StoredRegistration.WatchdogStartupGraceSeconds);
            Assert.True(client.StoredRegistration.EnableAutomaticRecoveryPoints);
            Assert.Equal(60, client.StoredRegistration.RecoveryPointIntervalMinutes);
            Assert.Equal(5, client.StoredRegistration.RecoveryPointRetentionCount);
        }

        await using var reopened = MainWindowViewModel.CreateServiceOwned(paths, client);
        await reopened.InitializeAsync(allowInteractiveAutoImport: false);
        var roundTripped = Assert.Single(reopened.Servers);
        Assert.Equal(MemoryAllocationMode.Manual, roundTripped.MemoryAllocationMode);
        Assert.Equal(2048, roundTripped.MinimumMemoryMb);
        Assert.Equal(6144, roundTripped.MaximumMemoryMb);
        Assert.True(roundTripped.SeparateDiagnosticOutput);
        Assert.True(roundTripped.EnableHangWatchdog);
        Assert.Equal(45, roundTripped.WatchdogCheckIntervalSeconds);
        Assert.Equal(9, roundTripped.WatchdogProbeTimeoutSeconds);
        Assert.Equal(4, roundTripped.WatchdogFailureThreshold);
        Assert.Equal(240, roundTripped.WatchdogStartupGraceSeconds);
        Assert.True(roundTripped.EnableAutomaticRecoveryPoints);
        Assert.Equal(60, roundTripped.RecoveryPointIntervalMinutes);
        Assert.Equal(5, roundTripped.RecoveryPointRetentionCount);
    }

    [Fact]
    public async Task ServicePlayers_AreRefreshedThroughIpcWithoutReadingProjectionDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        var serviceId = Guid.NewGuid();
        var client = new StubServiceClient(serviceId)
        {
            PlayerNames = ["Alex", "Steve"],
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var projected = Assert.Single(viewModel.Servers);
        Assert.False(Directory.Exists(projected.DirectoryPath));

        viewModel.SelectedWorkspaceTabKey = MainWindowViewModel.PlayersWorkspaceTabKey;
        await client.PlayersListed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => projected.OnlinePlayerCount == 2);

        Assert.Equal(["Alex", "Steve"], projected.VisiblePlayers.Select(player => player.Name));
        Assert.False(Directory.Exists(projected.DirectoryPath));
        Assert.True(viewModel.CanRefreshSelectedPlayers);
        Assert.True(viewModel.RefreshPlayersCommand.CanExecute(null));
    }

    [Fact]
    public async Task ServiceSettings_ManagerDefaultPersistsCurrentGlobalRangeAndMode()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        using (var store = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile))
        {
            await store.SaveAsync(new ManagerSettings
            {
                NewServerDefaults = new NewServerDefaultsSettings
                {
                    MinimumMemoryMb = 3072,
                    MaximumMemoryMb = 7168,
                },
            });
        }

        var client = new StubServiceClient(Guid.NewGuid());
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var projected = Assert.Single(viewModel.Servers);

        projected.IsMemoryUsingDefault = true;
        viewModel.SaveSelectedSettingsCommand.Execute(null);
        await client.RegistrationUpdated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsSelectedInstanceConfigurationSaveRunning);

        Assert.Equal(ProductServerMemoryAllocationMode.UseManagerDefault, client.StoredRegistration.MemoryAllocationMode);
        Assert.Equal(3072, client.StoredRegistration.MinimumMemoryMb);
        Assert.Equal(7168, client.StoredRegistration.MaximumMemoryMb);
        Assert.Equal(MemoryAllocationMode.UseManagerDefault, projected.MemoryAllocationMode);
        Assert.Equal(3072, projected.MinimumMemoryMb);
        Assert.Equal(7168, projected.MaximumMemoryMb);
    }

    [Fact]
    public async Task ServiceSettings_EditingAndSavingRequireExactStoppedState()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(
            paths,
            new StubServiceClient(Guid.NewGuid()));
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var projected = Assert.Single(viewModel.Servers);

        projected.SetState(ServerState.Stopped);
        Assert.True(viewModel.CanEditSelectedInstanceConfiguration);
        Assert.True(viewModel.CanSaveSelectedInstanceConfiguration);
        Assert.True(viewModel.SaveSelectedSettingsCommand.CanExecute(null));

        foreach (var state in new[]
                 {
                     ServerState.Starting,
                     ServerState.Running,
                     ServerState.Stopping,
                     ServerState.Crashed,
                     ServerState.Faulted,
                 })
        {
            projected.SetState(state);
            Assert.False(viewModel.CanEditSelectedInstanceConfiguration);
            Assert.False(viewModel.CanSaveSelectedInstanceConfiguration);
            Assert.False(viewModel.SaveSelectedSettingsCommand.CanExecute(null));
            Assert.True(viewModel.HasSelectedInstanceConfigurationStatus);
        }
    }

    [Fact]
    public async Task ServiceSettings_AutomaticMemoryUsesPathFreeAdministrationMetricsAndPersistsRecommendation()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var serviceId = Guid.NewGuid();
        var addons = Enumerable.Range(0, 51)
            .Select(index => new ProductServerAddonSummary(
                ProductServerAddonKind.Mod,
                $"mod-{index:D2}.jar",
                2L * 1024 * 1024))
            .ToArray();
        var client = new StubServiceClient(serviceId)
        {
            AdministrationSnapshot = new ProductServerAdministrationSnapshot(
                serviceId,
                DateTimeOffset.UtcNow,
                true,
                addons,
                false,
                new ProductServerJavaRuntimeSummary(
                    true,
                    true,
                    21,
                    "21.0.8",
                    "JRE",
                    "Temurin",
                    "x64")),
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var projected = Assert.Single(viewModel.Servers);
        Assert.False(Directory.Exists(projected.DirectoryPath));

        projected.IsMemoryAutomatic = true;
        await client.AdministrationRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.LastAutomaticMemoryRecommendation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MemoryAllocationMode.Automatic, projected.MemoryAllocationMode);
        Assert.False(projected.IsAutomaticMemoryRecommendationRunning);
        Assert.True(projected.HasSuccessfulAutomaticMemoryRecommendation);
        Assert.InRange(projected.MinimumMemoryMb, 512, projected.MaximumMemoryMb);
        Assert.Contains("51", projected.MemoryConfigurationHint, StringComparison.Ordinal);

        var recommendedMinimum = projected.MinimumMemoryMb;
        var recommendedMaximum = projected.MaximumMemoryMb;
        viewModel.SaveSelectedSettingsCommand.Execute(null);
        await client.RegistrationUpdated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsSelectedInstanceConfigurationSaveRunning);

        Assert.Equal(ProductServerMemoryAllocationMode.Automatic, client.StoredRegistration.MemoryAllocationMode);
        Assert.Equal(recommendedMinimum, client.StoredRegistration.MinimumMemoryMb);
        Assert.Equal(recommendedMaximum, client.StoredRegistration.MaximumMemoryMb);
    }

    [Fact]
    public async Task ServiceBackupButton_UsesIpcAndRefreshesOpaqueBackupRows()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        var serviceId = Guid.NewGuid();
        var client = new StubServiceClient(serviceId);
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var projected = Assert.Single(viewModel.Servers);

        viewModel.CreateBackupCommand.Execute(null);
        await client.BackupCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => projected.Backups.Count == 1);

        var backup = Assert.Single(projected.Backups);
        Assert.Equal("backup-opaque-1", backup.BackupId);
        Assert.Equal("service-backup.zip", backup.FileName);
        Assert.DoesNotContain(temporary.Path, JsonSerializer.Serialize(backup), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServiceAppearance_IsCopiedToGuiThemeStorageAndRestoredByServerId()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var sourceBackground = Path.Combine(temporary.Path, "outside-background.png");
        var sourceIcon = Path.Combine(temporary.Path, "outside-icon.png");
        AppearanceThemeServiceTests.WriteTinyPng(sourceBackground);
        AppearanceThemeServiceTests.WriteTinyPng(sourceIcon);
        var serviceId = Guid.NewGuid();
        var client = new StubServiceClient(serviceId);

        await using (var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client))
        {
            await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
            var projected = Assert.Single(viewModel.Servers);
            projected.BackgroundImagePath = sourceBackground;
            projected.BackgroundImageOpacity = 0.42;
            projected.IconImagePath = sourceIcon;
            Assert.True(viewModel.OpenServerAppearanceCommand.CanExecute(projected));
            Assert.True(viewModel.SaveServerAppearanceCommand.CanExecute(projected));

            viewModel.SaveServerAppearanceCommand.Execute(projected);
            await WaitUntilAsync(async () =>
            {
                var current = await TryReadSettingsAsync(paths);
                return current is not null
                       && current.ServiceServerAppearances.TryGetValue(serviceId, out var preference)
                       && preference.BackgroundImagePath is not null
                       && preference.IconImagePath is not null;
            });
        }

        var stored = await ReadSettingsAsync(paths);
        var appearance = Assert.Single(stored.ServiceServerAppearances).Value;
        Assert.NotNull(appearance.BackgroundImagePath);
        Assert.NotNull(appearance.IconImagePath);
        Assert.True(SafePath.IsWithinRoot(paths.Themes, appearance.BackgroundImagePath!));
        Assert.True(SafePath.IsWithinRoot(paths.Themes, appearance.IconImagePath!));
        Assert.NotEqual(Path.GetFullPath(sourceBackground), Path.GetFullPath(appearance.BackgroundImagePath!));
        Assert.NotEqual(Path.GetFullPath(sourceIcon), Path.GetFullPath(appearance.IconImagePath!));
        Assert.True(File.Exists(appearance.BackgroundImagePath));
        Assert.True(File.Exists(appearance.IconImagePath));
        Assert.Equal(0.42, appearance.BackgroundImageOpacity, 3);

        await using var reloaded = MainWindowViewModel.CreateServiceOwned(paths, client);
        await reloaded.InitializeAsync(allowInteractiveAutoImport: false);
        var projectedAgain = Assert.Single(reloaded.Servers);
        Assert.Equal(appearance.BackgroundImagePath, projectedAgain.BackgroundImagePath);
        Assert.Equal(appearance.IconImagePath, projectedAgain.IconImagePath);
        Assert.Equal(0.42, projectedAgain.BackgroundImageOpacity, 3);
        Assert.False(projectedAgain.CanAccessLocalFiles);
    }

    [Fact]
    public async Task ServiceRemove_UnregistersStoppedRowWithoutRequestingPermanentDeletion()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        var serviceId = Guid.NewGuid();
        var client = new StubServiceClient(serviceId);
        await using var viewModel = new MainWindowViewModel(
            paths,
            new AcceptRemoval(),
            new OnlineModpackWorkflow(paths),
            onlineModpackDialogService: null,
            productServiceClient: client);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var projected = Assert.Single(viewModel.Servers);

        Assert.True(viewModel.RemoveServerCommand.CanExecute(projected));
        viewModel.RemoveServerCommand.Execute(projected);
        await client.Removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => viewModel.Servers.Count == 0);

        Assert.Empty(viewModel.Servers);
        Assert.Equal(["remove"], client.Mutations);
    }

    [Fact]
    public async Task ServiceRemove_DeletesOnlyGuiOwnedAppearanceCopiesAndPersistedPreference()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var serviceId = Guid.NewGuid();
        var ownedDirectory = Path.Combine(paths.Themes, "icons");
        Directory.CreateDirectory(ownedDirectory);
        var ownedIcon = Path.Combine(ownedDirectory, $"{serviceId:N}.owned.png");
        var unrelatedIcon = Path.Combine(ownedDirectory, $"{Guid.NewGuid():N}.unrelated.png");
        AppearanceThemeServiceTests.WriteTinyPng(ownedIcon);
        AppearanceThemeServiceTests.WriteTinyPng(unrelatedIcon);
        using (var store = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile))
        {
            await store.SaveAsync(new ManagerSettings
            {
                ServiceServerAppearances = new Dictionary<Guid, ServerAppearancePreference>
                {
                    [serviceId] = new() { IconImagePath = ownedIcon },
                },
            });
        }

        var client = new StubServiceClient(serviceId);
        await using var viewModel = new MainWindowViewModel(
            paths,
            new AcceptRemoval(),
            new OnlineModpackWorkflow(paths),
            onlineModpackDialogService: null,
            productServiceClient: client);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var projected = Assert.Single(viewModel.Servers);

        viewModel.RemoveServerCommand.Execute(projected);
        await client.Removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => viewModel.Servers.Count == 0);

        var stored = await ReadSettingsAsync(paths);
        Assert.Empty(stored.ServiceServerAppearances);
        Assert.False(File.Exists(ownedIcon));
        Assert.True(File.Exists(unrelatedIcon));
    }

    [Fact]
    public async Task ServiceMode_PermanentDeleteUsesServiceOwnedPathAndRemovesProjectionAfterCommit()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        var serviceDirectory = Path.Combine(temporary.Path, "service-data", "servers", "owned");
        Directory.CreateDirectory(serviceDirectory);
        await File.WriteAllTextAsync(Path.Combine(serviceDirectory, "world.dat"), "delete-me");
        var client = new StubServiceClient(Guid.NewGuid())
        {
            ServerDirectoryPath = serviceDirectory,
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(
            paths,
            client,
            new AcceptDeletion());
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var projected = Assert.Single(viewModel.Servers);

        Assert.True(viewModel.RemoveServerCommand.CanExecute(projected));
        Assert.True(viewModel.DeleteServerCommand.CanExecute(projected));
        await viewModel.DeleteServerPermanentlyAsync(projected);

        Assert.Empty(viewModel.Servers);
        Assert.False(Directory.Exists(serviceDirectory));
        Assert.Equal(["delete"], client.Mutations);
    }

    [Fact]
    public async Task ServiceMode_LegacyGuiRecoveryPointMutationIsHiddenAndFailClosed()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        var client = new StubServiceClient(Guid.NewGuid());
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        Assert.Single(viewModel.Servers);
        Assert.False(viewModel.CanManageLocalRecoveryPoints);
        Assert.False(viewModel.OpenRecoveryPointsFolderCommand.CanExecute(null));
        Assert.False(viewModel.RestoreRecoveryPointCommand.CanExecute(null));
    }

    [Fact]
    public async Task ServiceMode_AddonsTabUsesBoundedSnapshotWithoutRequestingDirectoryOrNetworkScan()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        var serviceDirectory = Path.Combine(temporary.Path, "service-data", "servers", "addons");
        Directory.CreateDirectory(Path.Combine(serviceDirectory, "mods"));
        var client = new StubServiceClient(Guid.NewGuid())
        {
            ServerDirectoryPath = serviceDirectory,
            AdministrationSnapshot = new ProductServerAdministrationSnapshot(
                Guid.Empty,
                DateTimeOffset.UtcNow,
                true,
                [new ProductServerAddonSummary(ProductServerAddonKind.Mod, "bounded.jar", 42)],
                false,
                new ProductServerJavaRuntimeSummary(true, true, 21, "21.0.8", "JRE", "Temurin", "x64")),
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var projected = Assert.Single(viewModel.Servers);

        Assert.True(viewModel.CanBrowseSelectedServerFiles);
        Assert.True(viewModel.CheckAddonUpdatesCommand.CanExecute(null));
        Assert.True(viewModel.OpenAddonFolderCommand.CanExecute(null));
        viewModel.SelectedWorkspaceTabKey = MainWindowViewModel.AddonsWorkspaceTabKey;
        await viewModel.LastAddonScan.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(client.AdministrationRequested.Task.IsCompletedSuccessfully);
        Assert.False(client.DirectoryRequested.Task.IsCompleted);
        Assert.Equal("bounded.jar", Assert.Single(projected.AddonUpdates).FileName);
        Assert.False(Directory.Exists(projected.DirectoryPath));
        Assert.True(Directory.Exists(Path.Combine(serviceDirectory, "mods")));
    }

    [Fact]
    public async Task ServiceMode_ModpackUpdateStagesCandidateAndLeavesLiveMutationToService()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var importsRoot = Path.Combine(temporary.Path, "service-imports");
        var candidateRoot = Path.Combine(paths.Servers, "verified-update-candidate");
        Directory.CreateDirectory(Path.Combine(candidateRoot, "mods"));
        await File.WriteAllTextAsync(Path.Combine(candidateRoot, "new-core.jar"), "new-core");
        await File.WriteAllTextAsync(Path.Combine(candidateRoot, "mods", "new-mod.jar"), "new-mod");
        var candidate = new ServerInstance
        {
            Name = "Candidate",
            DirectoryPath = candidateRoot,
            ServerJarPath = Path.Combine(candidateRoot, "new-core.jar"),
            LaunchKind = ServerLaunchKind.ExecutableJar,
            CoreType = CoreType.NeoForge,
            MinecraftVersion = "1.21.1",
            ServerArguments = ["nogui"],
            ModpackSource = ModpackSourceKind.Modrinth,
            ModpackProviderId = "builtin.modrinth",
            ModpackProjectId = "project",
            ModpackVersionId = "new-version",
            ModpackVersionName = "1.7.0",
        };
        var serviceId = Guid.NewGuid();
        var client = new StubServiceClient(serviceId, importsRoot);
        await using var viewModel = new MainWindowViewModel(
            paths,
            new AcceptRemoval(),
            new CandidateWorkflow(candidate),
            onlineModpackDialogService: null,
            productServiceClient: client,
            productServiceImportsRoot: importsRoot);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var projected = Assert.Single(viewModel.Servers);

        Assert.True(viewModel.UpdateSelectedModpackCommand.CanExecute(null));
        await viewModel.ApplyModpackUpdateAsync(
            projected,
            new OnlineModpackSearchResult(
                OnlineModpackProvider.Modrinth,
                "project",
                "Pack",
                "Summary",
                "Author"),
            new OnlineModpackVersion(
                OnlineModpackProvider.Modrinth,
                "project",
                "new-version",
                "1.7.0",
                "1.21.1",
                "NeoForge",
                "release",
                DateTimeOffset.UtcNow,
                HasOfficialServerPack: true),
            CancellationToken.None);

        Assert.NotNull(client.CommittedManifest);
        Assert.Contains(
            client.CommittedManifest!.Files,
            entry => entry.Path == "new-core.jar" && entry.Length == "new-core".Length);
        Assert.Contains(client.Mutations, mutation => mutation == "modpack-update:commit");
        Assert.Equal("new-version", client.StoredRegistration.ModpackVersionId);
        Assert.Equal("new-version", projected.Model.ModpackVersionId);
        Assert.False(Directory.Exists(candidateRoot));
        Assert.Contains("等待第一次實際啟動健康驗證", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    private static async Task<ManagerSettings> ReadSettingsAsync(ApplicationPaths paths)
    {
        using var store = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile);
        return await store.LoadAsync() ?? throw new InvalidDataException();
    }

    private static async Task<ManagerSettings?> TryReadSettingsAsync(ApplicationPaths paths)
    {
        using var store = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile);
        return await store.LoadAsync();
    }

    private sealed class StubServiceClient(Guid serverId, string? importsRoot = null) : IProductServiceClient
    {
        private ProductServerState _state = ProductServerState.Stopped;
        private bool _deleted;
        private readonly Guid _modpackUpdateId = Guid.NewGuid();
        private ProductServerModpackUpdateBeginRequest? _modpackUpdateRequest;
        private string _serverPropertiesText = "server-port=25565\n";
        private string _serverPropertiesRevision = CalculatePropertiesRevision("server-port=25565\n");
        private int _serverPropertiesReadCount;
        private ProductServerRegistration _registration = new()
        {
            Id = serverId,
            Name = "Service owned",
            ServerDirectory = "service-owned",
            JavaRuntimePath = "temurin-21/bin/java.exe",
            CoreType = "NeoForge",
            MinecraftVersion = "1.21.1",
            MinimumMemoryMb = 1536,
            MaximumMemoryMb = 5120,
            Port = 25565,
            AutoRestart = true,
            LaunchKind = ProductServerLaunchKind.ExecutableJar,
            ServerJarPath = "old-core.jar",
            ServerArguments = ["nogui"],
            ModpackProviderId = "builtin.modrinth",
            ModpackSource = ProductModpackSourceKind.Modrinth,
            ModpackProjectId = "project",
            ModpackVersionId = "old-version",
            ModpackVersionName = "1.6.0",
        };

        public ProductServiceClientException? HandshakeError { get; init; }

        public ProductApiVersion MaximumApiVersion { get; set; } = ProductApiProtocol.CurrentVersion;

        public string ServerPropertiesText
        {
            get => _serverPropertiesText;
            init
            {
                _serverPropertiesText = value;
                _serverPropertiesRevision = CalculatePropertiesRevision(value);
            }
        }

        public int ServerPropertiesReadCount => Volatile.Read(ref _serverPropertiesReadCount);

        public Func<int, CancellationToken, Task<ProductServerPropertiesDocument>>?
            ServerPropertiesReadHandler { get; set; }

        public TaskCompletionSource? PropertiesUpdateRelease { get; set; }

        public TaskCompletionSource? RegistrationUpdateRelease { get; set; }

        public List<string> Mutations { get; } = [];

        public IReadOnlyList<string> PlayerNames { get; init; } = [];

        public TaskCompletionSource PlayersListed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource BackupCreated { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DirectoryRequested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AdministrationRequested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PropertiesRead { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PropertiesReloaded { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PropertiesUpdated { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ProductServerAdministrationSnapshot? AdministrationSnapshot { get; init; }

        public ProductServerRegistration StoredRegistration => _registration;

        public string ServerDirectoryPath { get; init; } = Path.Combine(
            Path.GetTempPath(),
            "mcsv-service-owned",
            serverId.ToString("N"));

        public ProductServerModpackUpdateManifest? CommittedManifest { get; private set; }

        public TaskCompletionSource RegistrationUpdated { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Removed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProductLocalHandshakePayload> HandshakeAsync(
            CancellationToken cancellationToken = default)
            => HandshakeError is null
                ? Task.FromResult(new ProductLocalHandshakePayload(
                    new ProductHandshakeResponse(
                        "Muhun MCSV Manager",
                        "1.0.0",
                        MaximumApiVersion,
                        ProductApiProtocol.MinimumSupportedVersion,
                        Ready: true),
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow))
                : Task.FromException<ProductLocalHandshakePayload>(HandshakeError);

        public Task<IReadOnlyList<ProductServerSummary>> ListServersAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProductServerSummary>>(
                _deleted ? [] : [Summary()]);

        public Task<ProductServerStatus> GetStatusAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Status());

        public Task<ProductServerRegistration> GetRegistrationAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_registration);

        public Task<ProductConsolePage> ReadConsoleAsync(
            Guid requestedServerId,
            long afterCursor,
            int limit = 50,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ProductConsolePage(
                serverId,
                afterCursor,
                0,
                afterCursor,
                false,
                []));

        public Task<ProductServerPlayerList> ListPlayersAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(serverId, requestedServerId);
            PlayersListed.TrySetResult();
            return Task.FromResult(new ProductServerPlayerList(
                serverId,
                DateTimeOffset.UtcNow,
                PlayerNames.Select(name => new ProductServerPlayerSummary(name, DateTimeOffset.UtcNow)).ToArray()));
        }

        public Task<ProductServerMutationResult> StartAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add("start");
            _state = ProductServerState.Running;
            return Task.FromResult(new ProductServerMutationResult(serverId, true, Status()));
        }

        public Task<ProductServerMutationResult> StopAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add("stop");
            _state = ProductServerState.Stopped;
            return Task.FromResult(new ProductServerMutationResult(serverId, true, Status()));
        }

        public Task<ProductServerMutationResult> RestartAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add("restart");
            _state = ProductServerState.Running;
            return Task.FromResult(new ProductServerMutationResult(serverId, true, Status()));
        }

        public Task<ProductServerStatus> SendCommandAsync(
            Guid requestedServerId,
            string command,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add("command:" + command);
            return Task.FromResult(Status());
        }

        public Task<ProductServerStatus> RegisterAsync(
            ProductServerRegistration registration,
            CancellationToken cancellationToken = default)
        {
            _registration = registration;
            RegistrationUpdated.TrySetResult();
            return Task.FromResult(Status());
        }

        public async Task<ProductServerSettingsUpdateResult> UpdateServerSettingsAsync(
            Guid requestedServerId,
            ProductServerSettingsUpdateRequest settings,
            CancellationToken cancellationToken = default)
        {
            _registration = _registration with
            {
                Name = settings.Name,
                MinimumMemoryMb = settings.MinimumMemoryMb,
                MaximumMemoryMb = settings.MaximumMemoryMb,
                Port = settings.Port,
                AutoRestart = settings.AutoRestart,
                MemoryAllocationMode = settings.MemoryAllocationMode ?? _registration.MemoryAllocationMode,
                SeparateDiagnosticOutput = settings.SeparateDiagnosticOutput ?? _registration.SeparateDiagnosticOutput,
                EnableHangWatchdog = settings.EnableHangWatchdog ?? _registration.EnableHangWatchdog,
                WatchdogCheckIntervalSeconds = settings.WatchdogCheckIntervalSeconds ?? _registration.WatchdogCheckIntervalSeconds,
                WatchdogProbeTimeoutSeconds = settings.WatchdogProbeTimeoutSeconds ?? _registration.WatchdogProbeTimeoutSeconds,
                WatchdogFailureThreshold = settings.WatchdogFailureThreshold ?? _registration.WatchdogFailureThreshold,
                WatchdogStartupGraceSeconds = settings.WatchdogStartupGraceSeconds ?? _registration.WatchdogStartupGraceSeconds,
                EnableAutomaticRecoveryPoints = settings.EnableAutomaticRecoveryPoints ?? _registration.EnableAutomaticRecoveryPoints,
                RecoveryPointIntervalMinutes = settings.RecoveryPointIntervalMinutes ?? _registration.RecoveryPointIntervalMinutes,
                RecoveryPointRetentionCount = settings.RecoveryPointRetentionCount ?? _registration.RecoveryPointRetentionCount,
            };
            RegistrationUpdated.TrySetResult();
            if (RegistrationUpdateRelease is { } release)
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            return new ProductServerSettingsUpdateResult(
                _registration,
                Status());
        }

        public Task RemoveAsync(Guid requestedServerId, CancellationToken cancellationToken = default)
        {
            Mutations.Add("remove");
            Removed.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<ProductServerDirectoryInfo> GetServerDirectoryAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(serverId, requestedServerId);
            DirectoryRequested.TrySetResult();
            return Task.FromResult(new ProductServerDirectoryInfo(
                serverId,
                ServerDirectoryPath,
                Directory.Exists(ServerDirectoryPath)));
        }

        public Task<ProductServerAdministrationSnapshot> GetServerAdministrationAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(serverId, requestedServerId);
            AdministrationRequested.TrySetResult();
            return Task.FromResult((AdministrationSnapshot ?? new ProductServerAdministrationSnapshot(
                serverId,
                DateTimeOffset.UtcNow,
                true,
                [],
                false,
                new ProductServerJavaRuntimeSummary(false, false, null, null, string.Empty, string.Empty, string.Empty)))
                with { ServerId = serverId });
        }

        public async Task<ProductServerPropertiesDocument> ReadServerPropertiesAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(serverId, requestedServerId);
            var readCount = Interlocked.Increment(ref _serverPropertiesReadCount);
            if (readCount == 1)
            {
                PropertiesRead.TrySetResult();
            }
            else
            {
                PropertiesReloaded.TrySetResult();
            }

            return ServerPropertiesReadHandler is null
                ? CreatePropertiesDocument(_serverPropertiesText)
                : await ServerPropertiesReadHandler(readCount, cancellationToken);
        }

        public ProductServerPropertiesDocument CreatePropertiesDocument(string text)
            => new(
                serverId,
                true,
                text,
                CalculatePropertiesRevision(text));

        public void ReplaceServerPropertiesExternally(string text)
        {
            _serverPropertiesText = text;
            _serverPropertiesRevision = CalculatePropertiesRevision(text);
        }

        public async Task<ProductServerPropertiesDocument> UpdateServerPropertiesAsync(
            Guid requestedServerId,
            ProductServerPropertiesUpdateRequest update,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(serverId, requestedServerId);
            Assert.Equal(_serverPropertiesRevision, update.ExpectedRevisionSha256);
            _serverPropertiesText = update.Text;
            _serverPropertiesRevision = CalculatePropertiesRevision(update.Text);
            if (ServerPropertiesPortEditor.TryReadServerPort(update.Text, out var port))
            {
                _registration = _registration with { Port = port };
            }
            PropertiesUpdated.TrySetResult();
            if (PropertiesUpdateRelease is { } release)
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            return new ProductServerPropertiesDocument(
                serverId,
                true,
                _serverPropertiesText,
                _serverPropertiesRevision);
        }

        public Task<ProductServerDeletionResult> DeleteServerPermanentlyAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(serverId, requestedServerId);
            if (Directory.Exists(ServerDirectoryPath))
            {
                Directory.Delete(ServerDirectoryPath, recursive: true);
            }
            _deleted = true;
            Mutations.Add("delete");
            return Task.FromResult(new ProductServerDeletionResult(
                serverId,
                Deleted: true,
                DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<ProductServerBackupSummary>> ListBackupsAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProductServerBackupSummary>>(
                BackupCreated.Task.IsCompleted
                    ? [BackupSummary()]
                    : []);

        public Task<ProductServerBackupMutationResult> CreateBackupAsync(
            Guid requestedServerId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(serverId, requestedServerId);
            BackupCreated.TrySetResult();
            return Task.FromResult(new ProductServerBackupMutationResult(
                serverId,
                BackupSummary(),
                DateTimeOffset.UtcNow));
        }

        public Task<ProductServerModpackUpdateStatus> BeginModpackUpdateAsync(
            ProductServerModpackUpdateBeginRequest request,
            CancellationToken cancellationToken = default)
        {
            if (importsRoot is null)
            {
                throw new NotSupportedException();
            }

            _modpackUpdateRequest = request;
            Directory.CreateDirectory(ModpackStagingDirectory());
            Mutations.Add("modpack-update:begin");
            return Task.FromResult(ModpackStatus(ProductServerModpackUpdateState.Staging));
        }

        public async Task<ProductServerModpackUpdateStatus> CommitModpackUpdateAsync(
            Guid updateId,
            string manifestSha256,
            CancellationToken cancellationToken = default)
        {
            if (updateId != _modpackUpdateId || _modpackUpdateRequest is null)
            {
                throw new InvalidDataException("Unexpected modpack update correlation.");
            }

            var manifestPath = Path.Combine(ModpackStagingDirectory(), "manifest.v1.json");
            await using (var stream = File.OpenRead(manifestPath))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!actualHash.Equals(manifestSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Manifest digest did not match.");
                }
            }

            CommittedManifest = JsonSerializer.Deserialize<ProductServerModpackUpdateManifest>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var target = _modpackUpdateRequest.Target;
            _registration = _registration with
            {
                LaunchKind = target.LaunchKind,
                ServerJarPath = target.ServerJarPath,
                JavaArgumentFilePaths = target.JavaArgumentFilePaths,
                CoreType = target.CoreType,
                MinecraftVersion = target.MinecraftVersion,
                ServerArguments = target.ServerArguments,
                ModpackProviderId = target.ModpackProviderId,
                ModpackSource = target.ModpackSource,
                ModpackProjectId = target.ModpackProjectId,
                ModpackVersionId = target.ModpackVersionId,
                ModpackVersionName = target.ModpackVersionName,
                IsInstallerArtifact = target.IsInstallerArtifact,
            };
            Mutations.Add("modpack-update:commit");
            return ModpackStatus(ProductServerModpackUpdateState.AwaitingHealth);
        }

        public Task<ProductServerModpackUpdateStatus> GetModpackUpdateStatusAsync(
            Guid updateId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ModpackStatus(ProductServerModpackUpdateState.AwaitingHealth));

        public Task<ProductServerModpackUpdateStatus> CancelModpackUpdateAsync(
            Guid updateId,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add("modpack-update:cancel");
            return Task.FromResult(ModpackStatus(ProductServerModpackUpdateState.Cancelled));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private ProductServerSummary Summary()
            => new(
                serverId,
                _registration.Name,
                _state,
                _registration.Port,
                _registration.CoreType,
                _registration.MinecraftVersion);

        private ProductServerStatus Status()
            => new(Summary(), Guid.NewGuid(), 1234, DateTimeOffset.UtcNow, null, null, null);

        private static ProductServerBackupSummary BackupSummary()
            => new("backup-opaque-1", "service-backup.zip", 4096, DateTimeOffset.UtcNow);

        private string ModpackStagingDirectory()
            => Path.Combine(
                importsRoot ?? throw new InvalidOperationException(),
                "modpack-updates",
                _modpackUpdateId.ToString("N"));

        private ProductServerModpackUpdateStatus ModpackStatus(ProductServerModpackUpdateState state)
        {
            var files = CommittedManifest?.Files ?? [];
            var totalBytes = files.Sum(entry => entry.Length);
            return new ProductServerModpackUpdateStatus(
                _modpackUpdateId,
                serverId,
                state,
                state == ProductServerModpackUpdateState.Staging ? ModpackStagingDirectory() : null,
                totalBytes,
                totalBytes,
                files.Count,
                files.Count,
                BackupArchivePath: null,
                ErrorCode: null,
                ErrorMessage: null,
                UpdatedAtUtc: DateTimeOffset.UtcNow);
        }

        private static string CalculatePropertiesRevision(string text)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private sealed class StubProductServiceUpdateLauncher(
        Func<BundledProductServiceUpdateResult> update) : IBundledProductServiceUpdateLauncher
    {
        public int InvocationCount { get; private set; }

        public Task<BundledProductServiceUpdateResult> UpdateAsync(
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(update());
        }
    }

    private sealed class CandidateWorkflow(ServerInstance candidate) : IOnlineModpackWorkflow
    {
        public Task<IReadOnlyList<OnlineModpackSearchResult>> SearchAsync(
            OnlineModpackProvider provider,
            string query,
            System.Security.SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<OnlineModpackVersion>> GetVersionsAsync(
            OnlineModpackSearchResult project,
            System.Security.SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ServerInstance> InstallAsync(
            OnlineModpackInstallRequest request,
            System.Security.SecureString? transientApiKey,
            IProgress<OnlineModpackInstallProgress> progress,
            CancellationToken cancellationToken)
            => Task.FromResult(candidate);
    }

    private sealed class AcceptRemoval : IServerRemovalConfirmationService
    {
        public bool ConfirmRemoval(string serverName, string directoryPath) => true;
    }

    private sealed class AcceptDeletion : IServerDeletionConfirmationService
    {
        public bool ConfirmDeletion(string serverName, string directoryPath) => true;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected GUI state was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!await predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected persisted GUI state was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mcsv-service-gui-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup on Windows CI.
            }
        }
    }
}
