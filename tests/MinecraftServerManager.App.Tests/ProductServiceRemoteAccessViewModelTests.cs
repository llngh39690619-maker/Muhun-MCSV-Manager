using System.IO;
using System.Windows;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Security;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.App.Tests;

public sealed class ProductServiceRemoteAccessViewModelTests
{
    [Fact]
    public async Task RuntimeLocalization_UpdatesAnOpenFormalServiceViewModel()
    {
        var languageFile = Path.Combine(
            Path.GetTempPath(),
            $"mcsv-remote-language-{Guid.NewGuid():N}.json");
        try
        {
            LocalizationService.Current.Initialize(
                languageFile,
                System.Globalization.CultureInfo.GetCultureInfo("zh-TW"));
            var client = new StubRemoteClient
            {
                Accounts = [Account("operator03", [])],
            };
            using var viewModel = new ProductServiceRemoteAccessViewModel(
                client,
                [],
                copyText: _ => { },
                openUrl: _ => { });
            await viewModel.InitializeAsync();

            Assert.Equal("可登入", viewModel.SelectedAccount?.LockoutText);
            Assert.Equal("顯示密碼", viewModel.SelectedAccount?.PinToggleText);
            Assert.Equal("檢視者", viewModel.SelectedAccount?.RoleDisplayText);

            LocalizationService.Current.SetCulture("en-US");

            Assert.Equal("Can sign in", viewModel.SelectedAccount?.LockoutText);
            Assert.Equal("Show password", viewModel.SelectedAccount?.PinToggleText);
            Assert.Equal("Viewer", viewModel.SelectedAccount?.RoleDisplayText);
            Assert.Equal(
                "Remote administration data was refreshed from Windows Service.",
                viewModel.StatusMessage);
        }
        finally
        {
            LocalizationService.Current.SetCulture("zh-TW");
            File.Delete(languageFile);
        }
    }

    [Fact]
    public async Task Initialize_ProjectsEveryFormalPermissionAndExistingServerScope()
    {
        var serverId = Guid.NewGuid();
        var client = new StubRemoteClient
        {
            Accounts =
            [
                Account(
                    "admin01",
                    [
                        new ProductPermissionGrant(
                            ProductPermissionCodes.ServiceManage,
                            ProductPermissionScope.Global),
                        new ProductPermissionGrant(
                            ProductPermissionCodes.ServerStart,
                            ProductPermissionScope.ForServer(serverId)),
                    ])
            ],
        };
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [new ProductServiceRemoteServerOption(serverId, "測試 Server")],
            copyText: _ => { },
            openUrl: _ => { });

        await viewModel.InitializeAsync();

        var account = Assert.Single(viewModel.Accounts);
        Assert.Equal(ProductPermissionCatalog.All.Count, account.Permissions.Count);
        Assert.True(account.Permissions.Single(permission =>
            permission.Code == ProductPermissionCodes.ServiceManage).IsGlobalGranted);
        var start = account.Permissions.Single(permission =>
            permission.Code == ProductPermissionCodes.ServerStart);
        Assert.True(Assert.Single(start.Servers).IsGranted);
        Assert.Equal(
            account.Account.Grants.OrderBy(grant => grant.PermissionCode),
            account.BuildGrants().OrderBy(grant => grant.PermissionCode));
    }

    [Fact]
    public async Task RuntimeCommands_MutateOnlyServiceClientAndKeepPublicUrlActionsExplicit()
    {
        var copied = string.Empty;
        var opened = string.Empty;
        var client = new StubRemoteClient();
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: value => copied = value,
            openUrl: value => opened = value,
            persistentFunnel: new StubPersistentFunnel());
        await viewModel.InitializeAsync();

        Assert.True(viewModel.StartCommand.CanExecute(null));
        viewModel.StartCommand.Execute(null);
        await client.StartObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.True(viewModel.RemoteStatus?.DesiredEnabled);
        Assert.True(viewModel.CopyUrlCommand.CanExecute(null));
        viewModel.CopyUrlCommand.Execute(null);
        viewModel.OpenUrlCommand.Execute(null);
        Assert.Equal("https://x-mcsv.tail123.ts.net/", copied);
        Assert.Equal(copied, opened);
        Assert.Equal(1, client.StartCalls);
    }

    [Fact]
    public async Task Reconnect_ConfiguresPersistentRouteInStrictChallengeCommitOrder()
    {
        var events = new List<string>();
        var configured = RemoteStatus(
            desiredEnabled: true,
            hostRunning: true,
            funnelRunning: false,
            publicUrl: "https://x-mcsv.tail123.ts.net/",
            state: "configured_cached",
            errorCode: null) with
        {
            RouteConfigured = true,
            RouteStatusCached = true,
            RouteLastVerifiedAtUtc = DateTimeOffset.UtcNow,
        };
        var client = new StubRemoteClient
        {
            Events = events,
            ReconnectRemoteStatus = RemoteStatus(
                desiredEnabled: true,
                hostRunning: false,
                funnelRunning: false,
                publicUrl: null,
                state: "awaiting_interactive_route",
                errorCode: "tailscale.interactive_route_required"),
            RouteCommitStatus = configured,
        };
        var persistentFunnel = new StubPersistentFunnel { Events = events };
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            launchTailscale: () => throw new InvalidOperationException("A ready active-user route must not launch recovery."),
            persistentFunnel: persistentFunnel);
        await viewModel.InitializeAsync();

        viewModel.ReconnectCommand.Execute(null);
        await client.ReconnectObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.Equal(
            [
                "client.reconnect",
                "funnel.identity",
                "client.route.prepare",
                "funnel.ensure",
                "client.route.commit",
            ],
            events);
        Assert.False(viewModel.HasError);
        Assert.True(viewModel.HasPublicUrl);
        Assert.True(viewModel.RemoteStatus?.HostRunning);
        Assert.False(viewModel.RemoteStatus?.FunnelRunning);
        Assert.True(viewModel.RemoteStatus?.RouteConfigured);
        Assert.True(viewModel.RemoteStatus?.RouteStatusCached);
        Assert.Equal(1, client.RoutePrepareCalls);
        Assert.Equal(1, client.RouteCommitCalls);
        Assert.False(persistentFunnel.LastAllowExistingVerifiedRoute);
    }

    [Fact]
    public async Task Reconnect_PublicUrlWithoutReceiptNeverAuthorizesAnExistingRoute()
    {
        var client = new StubRemoteClient
        {
            ReconnectRemoteStatus = RemoteStatus(
                desiredEnabled: true,
                hostRunning: true,
                funnelRunning: false,
                publicUrl: "https://x-mcsv.tail123.ts.net/",
                state: "awaiting_route_verification",
                errorCode: null),
        };
        var persistentFunnel = new StubPersistentFunnel
        {
            EnsureResult = new ProductPersistentFunnelOperationResult(
                false,
                false,
                ProductPersistentFunnelDisposition.Conflict,
                null,
                "tailscale.funnel_unowned_exact_route"),
        };
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            persistentFunnel: persistentFunnel);
        await viewModel.InitializeAsync();

        viewModel.ReconnectCommand.Execute(null);
        await client.ReconnectObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.False(persistentFunnel.LastAllowExistingVerifiedRoute);
        Assert.Equal(0, client.RouteCommitCalls);
        Assert.True(viewModel.HasError);
        Assert.Contains(
            "tailscale.funnel_unowned_exact_route",
            viewModel.StatusMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_ResetFailureNeverCommitsRemovalOrDropsCachedHostState()
    {
        var events = new List<string>();
        var configured = RemoteStatus(
            desiredEnabled: true,
            hostRunning: true,
            funnelRunning: false,
            publicUrl: "https://x-mcsv.tail123.ts.net/",
            state: "configured_cached",
            errorCode: null) with
        {
            RouteConfigured = true,
            RouteStatusCached = true,
            RouteLastVerifiedAtUtc = DateTimeOffset.UtcNow,
        };
        var client = new StubRemoteClient
        {
            Events = events,
            InitialRemoteStatus = configured,
        };
        var persistentFunnel = new StubPersistentFunnel
        {
            Events = events,
            RemoveResult = new ProductPersistentFunnelOperationResult(
                false,
                false,
                ProductPersistentFunnelDisposition.ExactTarget,
                null,
                "tailscale.funnel_reset_failed"),
        };
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            persistentFunnel: persistentFunnel);
        await viewModel.InitializeAsync();

        viewModel.StopCommand.Execute(null);
        await WaitUntilAsync(() => client.RouteRemovalPrepareCalls == 1 && !viewModel.IsBusy);

        Assert.Equal(
            ["client.route-removal.prepare", "funnel.remove"],
            events);
        Assert.Equal(0, client.RouteRemovalCommitCalls);
        Assert.Null(client.LastRouteRemovalRecoveryPublicUrl);
        Assert.True(viewModel.HasError);
        Assert.True(viewModel.RemoteStatus?.DesiredEnabled);
        Assert.True(viewModel.RemoteStatus?.HostRunning);
        Assert.True(viewModel.RemoteStatus?.RouteConfigured);
    }

    [Fact]
    public async Task Stop_VerifiedAbsenceCommitsOnlyAfterPersistentRouteRemoval()
    {
        var events = new List<string>();
        var client = new StubRemoteClient
        {
            Events = events,
            InitialRemoteStatus = RemoteStatus(
                desiredEnabled: true,
                hostRunning: true,
                funnelRunning: false,
                publicUrl: "https://x-mcsv.tail123.ts.net/",
                state: "configured_cached",
                errorCode: null) with
            {
                RouteConfigured = true,
                RouteStatusCached = true,
                RouteLastVerifiedAtUtc = DateTimeOffset.UtcNow,
            },
        };
        var persistentFunnel = new StubPersistentFunnel { Events = events };
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            persistentFunnel: persistentFunnel);
        await viewModel.InitializeAsync();

        viewModel.StopCommand.Execute(null);
        await WaitUntilAsync(() => client.RouteRemovalCommitCalls == 1 && !viewModel.IsBusy);

        Assert.Equal(
            [
                "client.route-removal.prepare",
                "funnel.remove",
                "client.route-removal.commit",
            ],
            events);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.RemoteStatus?.DesiredEnabled);
        Assert.False(viewModel.RemoteStatus?.HostRunning);
        Assert.False(viewModel.RemoteStatus?.RouteConfigured);
        Assert.Null(client.LastRouteRemovalRecoveryPublicUrl);
    }

    [Fact]
    public async Task Stop_InvalidServiceChallengeNeverTouchesTailscaleOrCommitsRemoval()
    {
        var client = new StubRemoteClient
        {
            InitialRemoteStatus = RemoteStatus(
                desiredEnabled: true,
                hostRunning: true,
                funnelRunning: false,
                publicUrl: "https://x-mcsv.tail123.ts.net/",
                state: "configured_cached",
                errorCode: null) with
            {
                RouteConfigured = true,
                RouteStatusCached = true,
                RouteLastVerifiedAtUtc = DateTimeOffset.UtcNow,
            },
            RouteRemovalChallenge = new ProductRemoteAccessRouteChallenge(
                Guid.NewGuid(),
                "https://x-mcsv.attacker.example/",
                DateTimeOffset.UtcNow.AddMinutes(1),
                42_871),
        };
        var persistentFunnel = new StubPersistentFunnel();
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            persistentFunnel: persistentFunnel);
        await viewModel.InitializeAsync();

        viewModel.StopCommand.Execute(null);
        await WaitUntilAsync(() => client.RouteRemovalPrepareCalls == 1 && !viewModel.IsBusy);

        Assert.Equal(0, persistentFunnel.RemoveCalls);
        Assert.Equal(0, client.RouteRemovalCommitCalls);
        Assert.True(viewModel.HasError);
    }

    [Fact]
    public async Task Stop_CorruptReceiptRecoversCanonicalActiveUserOriginBeforeRemoval()
    {
        var events = new List<string>();
        var client = new StubRemoteClient
        {
            Events = events,
            InitialRemoteStatus = RemoteStatus(
                desiredEnabled: true,
                hostRunning: true,
                funnelRunning: false,
                publicUrl: null,
                state: "blocked",
                errorCode: "remote.route_receipt_invalid"),
        };
        var persistentFunnel = new StubPersistentFunnel { Events = events };
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            persistentFunnel: persistentFunnel);
        await viewModel.InitializeAsync();

        viewModel.StopCommand.Execute(null);
        await WaitUntilAsync(() => client.RouteRemovalCommitCalls == 1 && !viewModel.IsBusy);

        Assert.Equal(
            [
                "funnel.identity",
                "client.route-removal.prepare",
                "funnel.remove",
                "client.route-removal.commit",
            ],
            events);
        Assert.Equal(
            StubPersistentFunnel.ReadyIdentity.PublicOrigin?.AbsoluteUri,
            client.LastRouteRemovalRecoveryPublicUrl);
        Assert.Equal(0, client.StopCalls);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Stop_CorruptReceiptWithUnverifiedIdentityNeverPreparesOrCommitsRemoval()
    {
        var events = new List<string>();
        var client = new StubRemoteClient
        {
            Events = events,
            InitialRemoteStatus = RemoteStatus(
                desiredEnabled: true,
                hostRunning: true,
                funnelRunning: false,
                publicUrl: null,
                state: "blocked",
                errorCode: "remote.route_receipt_invalid"),
        };
        var persistentFunnel = new StubPersistentFunnel
        {
            Events = events,
            Identity = new ProductTailscaleIdentityResult(
                false,
                null,
                "tailscale.status_schema_invalid"),
        };
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            persistentFunnel: persistentFunnel);
        await viewModel.InitializeAsync();

        viewModel.StopCommand.Execute(null);
        await WaitUntilAsync(() => events.Count == 1 && !viewModel.IsBusy);

        Assert.Equal(["funnel.identity"], events);
        Assert.Equal(0, client.RouteRemovalPrepareCalls);
        Assert.Equal(0, persistentFunnel.RemoveCalls);
        Assert.Equal(0, client.RouteRemovalCommitCalls);
        Assert.True(viewModel.HasError);
    }

    [Fact]
    public async Task Stop_CorruptReceiptWithUnverifiableRouteNeverCommitsRemoval()
    {
        var events = new List<string>();
        var client = new StubRemoteClient
        {
            Events = events,
            InitialRemoteStatus = RemoteStatus(
                desiredEnabled: true,
                hostRunning: true,
                funnelRunning: false,
                publicUrl: null,
                state: "blocked",
                errorCode: "remote.route_receipt_invalid"),
        };
        var persistentFunnel = new StubPersistentFunnel
        {
            Events = events,
            RemoveResult = new ProductPersistentFunnelOperationResult(
                false,
                false,
                ProductPersistentFunnelDisposition.Indeterminate,
                null,
                "tailscale.funnel_status_schema_invalid"),
        };
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            persistentFunnel: persistentFunnel);
        await viewModel.InitializeAsync();

        viewModel.StopCommand.Execute(null);
        await WaitUntilAsync(() => persistentFunnel.RemoveCalls == 1 && !viewModel.IsBusy);

        Assert.Equal(
            ["funnel.identity", "client.route-removal.prepare", "funnel.remove"],
            events);
        Assert.Equal(0, client.RouteRemovalCommitCalls);
        Assert.True(viewModel.HasError);
    }

    [Fact]
    public async Task Reconnect_WhenSignedOut_LaunchesTailscaleAndDoesNotReportFalseSuccess()
    {
        var launchCalls = 0;
        var client = new StubRemoteClient
        {
            InitialRemoteStatus = RemoteStatus(
                desiredEnabled: true,
                hostRunning: false,
                funnelRunning: false,
                publicUrl: null,
                state: "unavailable",
                errorCode: "tailscale.backend_not_running"),
            ReconnectRemoteStatus = RemoteStatus(
                desiredEnabled: true,
                hostRunning: false,
                funnelRunning: false,
                publicUrl: null,
                state: "unavailable",
                errorCode: "tailscale.backend_not_running"),
        };
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            launchTailscale: () =>
            {
                launchCalls++;
                return true;
            },
            persistentFunnel: new StubPersistentFunnel
            {
                Identity = new ProductTailscaleIdentityResult(
                    false,
                    null,
                    "tailscale.backend_not_running"),
            },
            delayAsync: (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            tailscaleRecoveryPollInterval: TimeSpan.Zero,
            tailscaleRecoveryAttempts: 1);
        await viewModel.InitializeAsync();

        viewModel.ReconnectCommand.Execute(null);
        await client.ReconnectObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.True(viewModel.RemoteStatus?.DesiredEnabled);
        Assert.False(viewModel.RemoteStatus?.HostRunning);
        Assert.False(viewModel.RemoteStatus?.FunnelRunning);
        Assert.False(viewModel.HasPublicUrl);
        Assert.True(viewModel.HasError);
        Assert.True(viewModel.IsTailscaleRecoveryActive);
        Assert.Equal(1, launchCalls);
        Assert.Contains("tailscale", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("已要求", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("固定 HTTPS 網址已建立", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconnect_AfterTailscaleSignIn_RetriesAndShowsFixedXMcsvUrl()
    {
        var signedOut = RemoteStatus(
            desiredEnabled: true,
            hostRunning: false,
            funnelRunning: false,
            publicUrl: null,
            state: "unavailable",
            errorCode: "tailscale.backend_not_running");
        var waiting = RemoteStatus(
            desiredEnabled: true,
            hostRunning: false,
            funnelRunning: false,
            publicUrl: null,
            state: "waiting",
            errorCode: null);
        var running = RemoteStatus(
            desiredEnabled: true,
            hostRunning: true,
            funnelRunning: true,
            publicUrl: "https://x-mcsv.tail123.ts.net/",
            state: "running",
            errorCode: null);
        var client = new StubRemoteClient
        {
            InitialRemoteStatus = signedOut,
            ReconnectRemoteStatus = running,
        };
        client.StatusResponses.Enqueue(signedOut);
        client.StatusResponses.Enqueue(waiting);
        client.ReconnectResponses.Enqueue(signedOut);
        client.ReconnectResponses.Enqueue(running);
        var persistentFunnel = new StubPersistentFunnel();
        persistentFunnel.IdentityResponses.Enqueue(new ProductTailscaleIdentityResult(
            false,
            null,
            "tailscale.backend_not_running"));
        persistentFunnel.IdentityResponses.Enqueue(StubPersistentFunnel.ReadyIdentity);
        var launchCalls = 0;
        var delayCalls = 0;
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            launchTailscale: () =>
            {
                launchCalls++;
                return true;
            },
            persistentFunnel: persistentFunnel,
            delayAsync: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            tailscaleRecoveryPollInterval: TimeSpan.Zero,
            tailscaleRecoveryAttempts: 2);
        await viewModel.InitializeAsync();

        viewModel.ReconnectCommand.Execute(null);
        await client.ReconnectObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => client.ReconnectCalls >= 2);
        await viewModel.TailscaleRecoveryTask.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy && !viewModel.IsTailscaleRecoveryActive);

        Assert.Equal(1, launchCalls);
        Assert.Equal(1, delayCalls);
        Assert.Equal(2, client.ReconnectCalls);
        Assert.True(viewModel.RemoteStatus?.HostRunning);
        Assert.False(viewModel.RemoteStatus?.FunnelRunning);
        Assert.True(viewModel.RemoteStatus?.RouteConfigured);
        Assert.True(viewModel.HasPublicUrl);
        Assert.Equal("https://x-mcsv.tail123.ts.net/", viewModel.PublicUrl);
        Assert.False(viewModel.HasError);
        Assert.Contains(viewModel.PublicUrl, viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_WhenDesiredRuntimeIsUnavailable_ShowsDiagnosticInsteadOfGenericSuccess()
    {
        var client = new StubRemoteClient
        {
            InitialRemoteStatus = RemoteStatus(
                desiredEnabled: true,
                hostRunning: false,
                funnelRunning: false,
                publicUrl: null,
                state: "unavailable",
                errorCode: "tailscale.backend_not_running"),
        };
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            launchTailscale: () => throw new InvalidOperationException("Refresh must not launch Tailscale."));

        await viewModel.InitializeAsync();

        Assert.True(viewModel.HasError);
        Assert.True(viewModel.HasLifecycleDiagnostic);
        Assert.Contains("Tailscale", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(
            LocalizationService.Current.Get("remote.service.refreshed"),
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task Start_WhenRuntimeRemainsUnavailable_DoesNotReportFalseSuccess()
    {
        var unavailable = RemoteStatus(
            desiredEnabled: true,
            hostRunning: false,
            funnelRunning: false,
            publicUrl: null,
            state: "unavailable",
            errorCode: "tailscale.not_installed");
        var client = new StubRemoteClient
        {
            StartRemoteStatus = unavailable,
        };
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            launchTailscale: () => throw new InvalidOperationException("Start must not launch an absent client."),
            persistentFunnel: new StubPersistentFunnel
            {
                Identity = new ProductTailscaleIdentityResult(
                    false,
                    null,
                    "tailscale.not_installed"),
            });
        await viewModel.InitializeAsync();

        viewModel.StartCommand.Execute(null);
        await client.StartObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.True(viewModel.HasError);
        Assert.False(viewModel.HasPublicUrl);
        Assert.Contains("Tailscale", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(
            LocalizationService.Current.Get("remote.service.started"),
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task Reconnect_SchemaInvalid_DoesNotLaunchInteractiveTailscale()
    {
        var malformed = RemoteStatus(
            desiredEnabled: true,
            hostRunning: false,
            funnelRunning: false,
            publicUrl: null,
            state: "unavailable",
            errorCode: "tailscale.status_schema_invalid");
        var client = new StubRemoteClient
        {
            InitialRemoteStatus = malformed,
            ReconnectRemoteStatus = malformed,
        };
        var launchCalls = 0;
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            launchTailscale: () =>
            {
                launchCalls++;
                return true;
            },
            persistentFunnel: new StubPersistentFunnel
            {
                Identity = new ProductTailscaleIdentityResult(
                    false,
                    null,
                    "tailscale.status_schema_invalid"),
            });
        await viewModel.InitializeAsync();

        viewModel.ReconnectCommand.Execute(null);
        await client.ReconnectObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.Equal(0, launchCalls);
        Assert.False(viewModel.IsTailscaleRecoveryActive);
        Assert.True(viewModel.HasError);
        Assert.Contains("tailscale.status_schema_invalid", viewModel.StatusMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "tailscale.localapi_identity_conflict",
        "remote.service.tailscale.localApiIdentityConflict")]
    [InlineData(
        "tailscale.localapi_access_denied",
        "remote.service.tailscale.localApiAccessDenied")]
    public async Task Reconnect_LocalApiIdentityFailure_ShowsDedicatedActionWithoutFalseRecovery(
        string errorCode,
        string localizationKey)
    {
        var unavailable = RemoteStatus(
            desiredEnabled: true,
            hostRunning: false,
            funnelRunning: false,
            publicUrl: null,
            state: "unavailable",
            errorCode: errorCode);
        var client = new StubRemoteClient
        {
            InitialRemoteStatus = unavailable,
            ReconnectRemoteStatus = unavailable,
        };
        var launchCalls = 0;
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            launchTailscale: () =>
            {
                launchCalls++;
                return true;
            },
            persistentFunnel: new StubPersistentFunnel
            {
                Identity = new ProductTailscaleIdentityResult(false, null, errorCode),
            });
        await viewModel.InitializeAsync();

        viewModel.ReconnectCommand.Execute(null);
        await client.ReconnectObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.Equal(0, launchCalls);
        Assert.False(viewModel.IsTailscaleRecoveryActive);
        Assert.True(viewModel.HasError);
        Assert.False(viewModel.HasPublicUrl);
        Assert.Equal(
            LocalizationService.Current.Get(localizationKey, errorCode),
            viewModel.StatusMessage);
        Assert.NotEqual(
            LocalizationService.Current.Get("remote.service.reconnected"),
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task Reconnect_HostnameUnavailable_ExplainsRequiredXMcsvName()
    {
        var unavailable = RemoteStatus(
            desiredEnabled: true,
            hostRunning: false,
            funnelRunning: false,
            publicUrl: null,
            state: "blocked",
            errorCode: "tailscale.hostname_unavailable");
        var client = new StubRemoteClient
        {
            InitialRemoteStatus = unavailable,
            ReconnectRemoteStatus = unavailable,
        };
        var launchCalls = 0;
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { },
            launchTailscale: () =>
            {
                launchCalls++;
                return true;
            },
            persistentFunnel: new StubPersistentFunnel
            {
                Identity = new ProductTailscaleIdentityResult(
                    false,
                    null,
                    "tailscale.hostname_unavailable"),
            });
        await viewModel.InitializeAsync();

        viewModel.ReconnectCommand.Execute(null);
        await client.ReconnectObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.Equal(0, launchCalls);
        Assert.True(viewModel.HasError);
        Assert.Contains("x-mcsv", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tailscale.hostname_unavailable", viewModel.StatusMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AccountEditor_SendsEnabledStateAndGlobalOrPerServerGrants()
    {
        var serverId = Guid.NewGuid();
        var client = new StubRemoteClient
        {
            Accounts = [Account("operator01", [])],
        };
        using var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [new ProductServiceRemoteServerOption(serverId, "生存服")],
            copyText: _ => { },
            openUrl: _ => { });
        await viewModel.InitializeAsync();
        var account = Assert.IsType<ProductServiceRemoteAccountViewModel>(viewModel.SelectedAccount);
        account.Enabled = false;
        account.Role = (ProductRemoteAccountRole)99;
        Assert.Equal(ProductRemoteAccountRole.Viewer, account.Role);
        account.Role = ProductRemoteAccountRole.Operator;
        account.Permissions.Single(permission =>
            permission.Code == ProductPermissionCodes.AuditRead).IsGlobalGranted = true;
        Assert.Single(account.Permissions.Single(permission =>
            permission.Code == ProductPermissionCodes.ConsoleWrite).Servers).IsGranted = true;

        viewModel.SaveAuthorizationCommand.Execute(null);
        var request = await client.AuthorizationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.False(request.Enabled);
        Assert.Equal(ProductRemoteAccountRole.Operator, request.Role);
        Assert.Contains(request.Grants, grant =>
            grant.PermissionCode == ProductPermissionCodes.AuditRead
            && grant.Scope == ProductPermissionScope.Global);
        Assert.Contains(request.Grants, grant =>
            grant.PermissionCode == ProductPermissionCodes.ConsoleWrite
            && grant.Scope.ServerId == serverId);
    }

    [Fact]
    public async Task RevealPin_IsEphemeralAndDisposeDoesNotStopServiceHost()
    {
        var client = new StubRemoteClient
        {
            Accounts = [Account("operator02", [])],
        };
        var viewModel = new ProductServiceRemoteAccessViewModel(
            client,
            [],
            copyText: _ => { },
            openUrl: _ => { });
        await viewModel.InitializeAsync();

        viewModel.TogglePinVisibilityCommand.Execute(null);
        await client.PinRevealObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);
        Assert.True(viewModel.SelectedAccount?.IsPinRevealed);
        Assert.Equal("12345678", viewModel.SelectedAccount?.PinDisplayText);

        viewModel.Dispose();

        Assert.False(viewModel.SelectedAccount?.IsPinRevealed);
        Assert.Equal(0, client.StopCalls);
    }

    [Fact]
    public void MainWindowServiceBranch_PrecedesLegacyCoordinatorConstruction()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource(
            "ViewModels",
            "MainWindowViewModel.cs"));
        var start = source.IndexOf(
            "private async Task<string?> InitializeRemoteAccessAsync(",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private void OpenRemoteAccess()",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var method = source[start..end];
        var serviceBranch = method.IndexOf("if (_productServiceController is not null)", StringComparison.Ordinal);
        var legacyConstruction = method.IndexOf("new RemoteAccessCoordinator(", StringComparison.Ordinal);
        Assert.True(serviceBranch >= 0 && legacyConstruction > serviceBranch);
        Assert.Contains("Never construct the legacy", method, StringComparison.Ordinal);
    }

    [Fact]
    public void FormalServiceDialog_LoadsBamlOnDarkApplicationSurface()
    {
        WpfStaTestHost.Run(() =>
        {
            using var viewModel = new ProductServiceRemoteAccessViewModel(
                new StubRemoteClient(),
                [],
                copyText: _ => { },
                openUrl: _ => { });
            var dialog = new ProductServiceRemoteAccessDialog(viewModel);
            var rendered = false;
            dialog.ContentRendered += (_, _) =>
            {
                dialog.UpdateLayout();
                rendered = true;
                Assert.Equal(
                    Application.Current.Resources["WindowBrush"],
                    dialog.Background);
                dialog.Close();
            };

            dialog.ShowDialog();

            Assert.True(rendered);
            Assert.False(dialog.IsVisible);
        });
    }

    private static ProductRemoteAccountSummary Account(
        string username,
        IReadOnlyList<ProductPermissionGrant> grants,
        ProductRemoteAccountRole role = ProductRemoteAccountRole.Viewer)
        => new(
            username,
            RemoteControlOptions.PublicTunnelCredentialSubject,
            null,
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            grants,
            role);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("View model operation did not settle.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class StubRemoteClient : IProductRemoteManagementClient
    {
        public IReadOnlyList<ProductRemoteAccountSummary> Accounts { get; set; } = [];
        public ProductRemoteAccessStatus InitialRemoteStatus { get; set; } = Status(false);
        public ProductRemoteAccessStatus StartRemoteStatus { get; set; } = Status(true);
        public ProductRemoteAccessStatus ReconnectRemoteStatus { get; set; } = Status(true);
        public ProductRemoteAccessStatus RouteCommitStatus { get; set; } = Status(true);
        public ProductRemoteAccessStatus RouteRemovalCommitStatus { get; set; } = Status(false);
        public ProductRemoteAccessRouteChallenge? RouteRemovalChallenge { get; set; }
        public Queue<ProductRemoteAccessStatus> StatusResponses { get; } = [];
        public Queue<ProductRemoteAccessStatus> ReconnectResponses { get; } = [];
        public List<string>? Events { get; set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int ReconnectCalls { get; private set; }
        public int RoutePrepareCalls { get; private set; }
        public int RouteCommitCalls { get; private set; }
        public int RouteRemovalPrepareCalls { get; private set; }
        public int RouteRemovalCommitCalls { get; private set; }
        public string? LastRouteRemovalRecoveryPublicUrl { get; private set; }
        public TaskCompletionSource StartObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReconnectObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProductUpdateRemoteAccountAuthorizationRequest>
            AuthorizationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PinRevealObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProductRemoteAccessStatus> GetRemoteAccessStatusAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(StatusResponses.Count > 0
                ? StatusResponses.Dequeue()
                : InitialRemoteStatus);

        public Task<ProductRemoteAccessStatus> StartRemoteAccessAsync(
            CancellationToken cancellationToken = default)
        {
            Events?.Add("client.start");
            StartCalls++;
            StartObserved.TrySetResult();
            return Task.FromResult(StartRemoteStatus);
        }

        public Task<ProductRemoteAccessStatus> StopRemoteAccessAsync(
            CancellationToken cancellationToken = default)
        {
            Events?.Add("client.stop");
            StopCalls++;
            return Task.FromResult(Status(false));
        }

        public Task<ProductRemoteAccessStatus> ReconnectRemoteAccessAsync(
            CancellationToken cancellationToken = default)
        {
            Events?.Add("client.reconnect");
            ReconnectCalls++;
            ReconnectObserved.TrySetResult();
            return Task.FromResult(ReconnectResponses.Count > 0
                ? ReconnectResponses.Dequeue()
                : ReconnectRemoteStatus);
        }

        public Task<ProductRemoteAccessRouteChallenge> PrepareRemoteAccessRouteAsync(
            string publicUrl,
            CancellationToken cancellationToken = default)
        {
            Events?.Add("client.route.prepare");
            RoutePrepareCalls++;
            return Task.FromResult(new ProductRemoteAccessRouteChallenge(
                Guid.NewGuid(),
                publicUrl,
                DateTimeOffset.UtcNow.AddMinutes(1),
                42_871));
        }

        public Task<ProductRemoteAccessStatus> CommitRemoteAccessRouteAsync(
            Guid operationId,
            DateTimeOffset verifiedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Events?.Add("client.route.commit");
            RouteCommitCalls++;
            return Task.FromResult(RouteCommitStatus);
        }

        public Task<ProductRemoteAccessRouteChallenge> PrepareRemoteAccessRouteRemovalAsync(
            string? publicUrl,
            CancellationToken cancellationToken = default)
        {
            Events?.Add("client.route-removal.prepare");
            RouteRemovalPrepareCalls++;
            LastRouteRemovalRecoveryPublicUrl = publicUrl;
            return Task.FromResult(RouteRemovalChallenge ?? new ProductRemoteAccessRouteChallenge(
                Guid.NewGuid(),
                "https://x-mcsv.tail123.ts.net/",
                DateTimeOffset.UtcNow.AddMinutes(1),
                42_871));
        }

        public Task<ProductRemoteAccessStatus> CommitRemoteAccessRouteRemovalAsync(
            Guid operationId,
            DateTimeOffset verifiedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Events?.Add("client.route-removal.commit");
            RouteRemovalCommitCalls++;
            return Task.FromResult(RouteRemovalCommitStatus);
        }

        public Task<IReadOnlyList<ProductRemoteAccountSummary>> ListRemoteAccountsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(Accounts);

        public Task<ProductRemoteAccountSummary> CreateRemoteAccountAsync(
            ProductCreateRemoteAccountRequest request,
            CancellationToken cancellationToken = default)
        {
            var account = Account(request.Username, request.Grants);
            account = account with { Role = request.Role ?? ProductRemoteAccountRole.Viewer };
            Accounts = [.. Accounts, account];
            return Task.FromResult(account);
        }

        public Task<ProductRemoteAccountSummary> UpdateRemoteAccountAuthorizationAsync(
            string username,
            ProductUpdateRemoteAccountAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            AuthorizationObserved.TrySetResult(request);
            var original = Accounts.Single(account => account.Username == username);
            var updated = original with
            {
                Enabled = request.Enabled,
                Grants = request.Grants,
                Role = request.Role ?? original.Role,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            Accounts = Accounts.Select(account => account.Username == username ? updated : account).ToArray();
            return Task.FromResult(updated);
        }

        public Task<ProductRemoteAccountSummary> UpdateRemoteAccountPinAsync(
            string username,
            ProductUpdateRemoteAccountPinRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Accounts.Single(account => account.Username == username));

        public Task<ProductRevealRemoteAccountPinResponse> RevealRemoteAccountPinAsync(
            string username,
            CancellationToken cancellationToken = default)
        {
            PinRevealObserved.TrySetResult();
            return Task.FromResult(new ProductRevealRemoteAccountPinResponse("12345678"));
        }

        public Task DeleteRemoteAccountAsync(
            string username,
            CancellationToken cancellationToken = default)
        {
            Accounts = Accounts.Where(account => account.Username != username).ToArray();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProductRememberedDeviceSummary>> ListRemoteDevicesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProductRememberedDeviceSummary>>([]);

        public Task RevokeRemoteDeviceAsync(
            Guid deviceId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private static ProductRemoteAccessStatus Status(bool enabled)
            => new(
                enabled,
                enabled,
                false,
                enabled ? "https://x-mcsv.tail123.ts.net/" : null,
                enabled ? "running" : "disabled",
                null,
                DateTimeOffset.UtcNow,
                null)
            {
                RouteConfigured = enabled,
                RouteStatusCached = enabled,
                RouteLastVerifiedAtUtc = enabled ? DateTimeOffset.UtcNow : null,
            };
    }

    private sealed class StubPersistentFunnel : IProductTailscalePersistentFunnelService
    {
        public static ProductTailscaleIdentityResult ReadyIdentity { get; } = new(
            true,
            new Uri("https://x-mcsv.tail123.ts.net/", UriKind.Absolute),
            null);

        public ProductTailscaleIdentityResult Identity { get; set; } = ReadyIdentity;
        public Queue<ProductTailscaleIdentityResult> IdentityResponses { get; } = [];
        public ProductPersistentFunnelOperationResult EnsureResult { get; set; } = new(
            true,
            true,
            ProductPersistentFunnelDisposition.ExactTarget,
            DateTimeOffset.UtcNow,
            null);
        public ProductPersistentFunnelOperationResult RemoveResult { get; set; } = new(
            true,
            true,
            ProductPersistentFunnelDisposition.Absent,
            DateTimeOffset.UtcNow,
            null);
        public int EnsureCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public bool? LastAllowExistingVerifiedRoute { get; private set; }
        public List<string>? Events { get; set; }

        public Task<ProductTailscaleIdentityResult> EnsureProductIdentityAsync(
            CancellationToken cancellationToken = default)
        {
            Events?.Add("funnel.identity");
            return Task.FromResult(IdentityResponses.Count > 0
                ? IdentityResponses.Dequeue()
                : Identity);
        }

        public Task<ProductPersistentFunnelOperationResult> EnsureRouteAsync(
            Uri expectedPublicOrigin,
            int localPort,
            bool allowExistingVerifiedRoute,
            CancellationToken cancellationToken = default)
        {
            Events?.Add("funnel.ensure");
            EnsureCalls++;
            LastAllowExistingVerifiedRoute = allowExistingVerifiedRoute;
            return Task.FromResult(EnsureResult);
        }

        public Task<ProductPersistentFunnelOperationResult> RemoveRouteAsync(
            Uri expectedPublicOrigin,
            int localPort,
            CancellationToken cancellationToken = default)
        {
            Events?.Add("funnel.remove");
            RemoveCalls++;
            return Task.FromResult(RemoveResult);
        }
    }

    private static ProductRemoteAccessStatus RemoteStatus(
        bool desiredEnabled,
        bool hostRunning,
        bool funnelRunning,
        string? publicUrl,
        string state,
        string? errorCode)
        => new(
            desiredEnabled,
            hostRunning,
            funnelRunning,
            publicUrl,
            state,
            errorCode,
            DateTimeOffset.UtcNow,
            null);
}
