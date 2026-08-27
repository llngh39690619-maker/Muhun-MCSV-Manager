using System.IO;
using System.Diagnostics;
using System.Text.Json;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class TailscaleServeServiceTests
{
    private const string ExecutablePath = @"C:\Test\Tailscale\tailscale.exe";
    private const string DnsName = "manager-node.example.ts.net";
    private const int LocalPort = 41873;
    private const string Target = "http://127.0.0.1:41873";
    private const string CandidateOrigin = "https://manager-node.example.ts.net:8443";
    private const string FunnelCandidateOrigin = "https://manager-node.example.ts.net";

    [Fact]
    public void ExecutableLocator_PrefersExactProgramFilesInstallOverPath()
    {
        var examinedPaths = new List<string>();
        var locator = new TailscaleExecutableLocator(
            () => @"C:\UserControlledPath",
            path =>
            {
                examinedPaths.Add(path);
                return string.Equals(
                    path,
                    TailscaleExecutableLocator.ProgramFilesExecutablePath,
                    StringComparison.OrdinalIgnoreCase);
            });

        var result = locator.FindExecutable();

        Assert.Equal(TailscaleExecutableLocator.ProgramFilesExecutablePath, result);
        Assert.Equal([TailscaleExecutableLocator.ProgramFilesExecutablePath], examinedPaths);
    }

    [Fact]
    public void ExecutableLocator_UsesAbsolutePathEntryWhenFixedInstallIsMissing()
    {
        var examinedPaths = new List<string>();
        var pathCandidate = @"C:\Tools\Tailscale\tailscale.exe";
        var locator = new TailscaleExecutableLocator(
            () => string.Join(Path.PathSeparator, @"C:\Tools\Tailscale", "relative-entry"),
            path =>
            {
                examinedPaths.Add(path);
                return string.Equals(path, pathCandidate, StringComparison.OrdinalIgnoreCase);
            });

        var result = locator.FindExecutable();

        Assert.Equal(pathCandidate, result);
        Assert.Equal(
            [TailscaleExecutableLocator.ProgramFilesExecutablePath, pathCandidate],
            examinedPaths);
    }

    [Fact]
    public void ForegroundStartInfo_IsHiddenShellFreeAndPreservesExactArguments()
    {
        string[] arguments =
        [
            "serve",
            "--yes",
            "--https=8443",
            Target
        ];

        var startInfo = SystemTailscaleCommandRunner.CreateStartInfo(ExecutablePath, arguments);

        Assert.Equal(ExecutablePath, startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(arguments, startInfo.ArgumentList);
    }

    [Fact]
    public void OutputCapture_IsStrictlyBounded()
    {
        var capture = new BoundedTextCapture(8);

        capture.Append("123456".ToCharArray(), 6);
        capture.Append("789ABC".ToCharArray(), 6);

        Assert.Equal("12345678", capture.Snapshot());
    }

    [Fact]
    public async Task KillOnCloseJob_ClosingOwnedJobTerminatesExactChildPid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pingPath = Path.Combine(Environment.SystemDirectory, "ping.exe");
        Assert.True(File.Exists(pingPath), $"Missing harmless Windows test child: {pingPath}");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pingPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            }
        };
        process.StartInfo.ArgumentList.Add("127.0.0.1");
        process.StartInfo.ArgumentList.Add("-t");
        SafeJobHandle? job = null;
        var childPid = 0;
        try
        {
            job = WindowsKillOnCloseJob.Create();
            Assert.True(process.Start());
            childPid = process.Id;
            WindowsKillOnCloseJob.Assign(job, process);
            Assert.False(process.HasExited);

            job.Dispose();
            job = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);

            Assert.True(process.HasExited);
            Assert.Equal(childPid, process.Id);
        }
        finally
        {
            job?.Dispose();
            try
            {
                if (childPid != 0 && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [Fact]
    public async Task GetStatus_ProvidesValidatedCandidateUrlBeforeServeIsConfigured()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus(dnsName: DnsName + ".")),
            Success("{}"));
        var process = new FakeForegroundProcess();
        await using var service = CreateService(runner, process);

        var result = await service.GetStatusAsync(LocalPort);

        Assert.True(result.IsInstalled);
        Assert.True(result.IsBackendRunning);
        Assert.Equal(DnsName, result.DnsName);
        Assert.Equal($"https://{DnsName}:8443/", result.CandidateUrl?.AbsoluteUri);
        Assert.Null(result.Url);
        Assert.False(result.IsConfigured);
        Assert.False(result.HasHttpsPortConflict);
        Assert.Equal(["status", "--json"], runner.Calls[0].Arguments);
        Assert.Equal(["serve", "status", "--json"], runner.Calls[1].Arguments);
    }

    [Fact]
    public async Task Funnel_GetStatus_UsesFixedDefaultHttpsOriginAndFunnelStatus()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus(dnsName: DnsName + ".")),
            Success("{}"));
        await using var service = CreateFunnelService(runner, new FakeForegroundProcess());

        var result = await service.GetStatusAsync(LocalPort);

        Assert.Equal($"https://{DnsName}/", result.CandidateUrl?.AbsoluteUri);
        Assert.Equal(["status", "--json"], runner.Calls[0].Arguments);
        Assert.Equal(["funnel", "status", "--json"], runner.Calls[1].Arguments);
    }

    [Fact]
    public async Task Funnel_Enable_OwnsOnlyExactForeground443RouteWithoutBackgroundMode()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(ForegroundConfig(Target, allowFunnel: true, httpsPort: 443)));
        var process = new FakeForegroundProcess(standardOutput: FunnelCandidateOrigin);
        var factory = new FakeForegroundProcessFactory(process);
        await using var service = CreateFunnelService(runner, factory);

        var result = await service.EnableAsync(LocalPort);

        Assert.True(result.Succeeded);
        Assert.True(result.Status.IsOwnedByThisService);
        Assert.Equal($"https://{DnsName}/", result.Status.Url?.AbsoluteUri);
        Assert.Equal(
            ["funnel", "--yes", "--https=443", Target],
            Assert.Single(factory.Calls).Arguments);
        Assert.Equal(["funnel", "status", "--json"], runner.Calls[2].Arguments);
        Assert.DoesNotContain("--bg", factory.Calls[0].Arguments);
        Assert.DoesNotContain("reset", factory.Calls[0].Arguments);
    }

    [Fact]
    public async Task Funnel_UnexpectedForegroundExitPublishesTransientNetworkFault()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(ForegroundConfig(Target, allowFunnel: true, httpsPort: 443)));
        var process = new FakeForegroundProcess(
            standardOutput: FunnelCandidateOrigin,
            standardError: "failed to connect to tailscaled backend: network disconnected");
        await using var service = CreateFunnelService(runner, process);
        var observed = new TaskCompletionSource<TailscaleRouteProcessExitedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.ForegroundProcessExited += (_, args) => observed.TrySetResult(args);
        Assert.True((await service.EnableAsync(LocalPort)).Succeeded);

        process.ExitUnexpectedly(17);

        var fault = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(process.ProcessId, fault.ProcessId);
        Assert.Equal(17, fault.ExitCode);
        Assert.True(fault.AutoRetryRecommended);
        Assert.Contains("network disconnected", fault.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Funnel_UnexpectedNonNetworkExitDoesNotRecommendAutomaticRetry()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(ForegroundConfig(Target, allowFunnel: true, httpsPort: 443)));
        var process = new FakeForegroundProcess(
            standardOutput: FunnelCandidateOrigin,
            standardError: "foreground process was terminated by an administrator");
        await using var service = CreateFunnelService(runner, process);
        var observed = new TaskCompletionSource<TailscaleRouteProcessExitedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.ForegroundProcessExited += (_, args) => observed.TrySetResult(args);
        Assert.True((await service.EnableAsync(LocalPort)).Succeeded);

        process.ExitUnexpectedly(9);

        var fault = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(fault.AutoRetryRecommended);
    }

    [Fact]
    public async Task Funnel_ExistingOrIncomplete443RouteIsConflictAndIsNeverOverwritten()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success(PersistentConfig(Target, allowFunnel: false, httpsPort: 443)));
        var factory = new FakeForegroundProcessFactory(new FakeForegroundProcess());
        await using var service = CreateFunnelService(runner, factory);

        var result = await service.EnableAsync(LocalPort);

        Assert.False(result.Succeeded);
        Assert.True(result.Status.HasHttpsPortConflict);
        Assert.Empty(factory.Calls);
    }

    [Fact]
    public async Task Funnel_Disable_KillsOwnedForegroundProcessAndRequiresRouteAbsence()
    {
        var exact = ForegroundConfig(Target, allowFunnel: true, httpsPort: 443);
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(exact),
            Success(NodeStatus()),
            Success(exact),
            Success(NodeStatus()),
            Success("{}"));
        var process = new FakeForegroundProcess(standardOutput: FunnelCandidateOrigin);
        await using var service = CreateFunnelService(runner, process);
        Assert.True((await service.EnableAsync(LocalPort)).Succeeded);

        var disabled = await service.DisableAsync(LocalPort);

        Assert.True(disabled.Succeeded);
        Assert.Equal(1, process.KillCount);
        Assert.False(disabled.Status.IsConfigured);
        Assert.All(
            runner.Calls.Where(call => call.Arguments.Count >= 2 && call.Arguments[1] == "status"),
            call => Assert.Equal("funnel", call.Arguments[0]));
    }

    [Fact]
    public async Task Funnel_NormalDisableDoesNotPublishUnexpectedExit()
    {
        var exact = ForegroundConfig(Target, allowFunnel: true, httpsPort: 443);
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(exact),
            Success(NodeStatus()),
            Success(exact),
            Success(NodeStatus()),
            Success("{}"));
        var process = new FakeForegroundProcess(standardOutput: FunnelCandidateOrigin);
        await using var service = CreateFunnelService(runner, process);
        var unexpected = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.ForegroundProcessExited += (_, _) => unexpected.TrySetResult(true);
        Assert.True((await service.EnableAsync(LocalPort)).Succeeded);

        var disabled = await service.DisableAsync(LocalPort);
        var completed = await Task.WhenAny(unexpected.Task, Task.Delay(150));

        Assert.True(disabled.Succeeded);
        Assert.NotSame(unexpected.Task, completed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetStatus_NullOrEmptyCertDomainsReportsHttpsEnablementRequired(
        bool useNull)
    {
        var certDomains = useNull ? null : Array.Empty<string>();
        var runner = new ScriptedRunner(
            Success(NodeStatusWithCertDomains(certDomains)));
        var factory = new FakeForegroundProcessFactory(new FakeForegroundProcess());
        await using var service = CreateService(runner, factory, new FakeDelay());

        var result = await service.GetStatusAsync(LocalPort);

        Assert.True(result.IsInstalled);
        Assert.True(result.IsBackendRunning);
        Assert.True(result.RequiresHttpsCertificateEnablement);
        Assert.NotNull(result.Error);
        Assert.Contains("HTTPS", result.Error);
        Assert.Contains("授權", result.Error);
        Assert.False(result.IsConfigured);
        Assert.False(result.HasHttpsPortConflict);
        Assert.Null(result.Url);
        Assert.Single(runner.Calls);
        Assert.Equal(["status", "--json"], runner.Calls[0].Arguments);
        Assert.Empty(factory.Calls);
    }

    [Fact]
    public async Task GetStatus_CertDomainsWithoutCurrentDnsNameReportsHttpsEnablementRequired()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatusWithCertDomains(["old-node.example.ts.net."])));
        var factory = new FakeForegroundProcessFactory(new FakeForegroundProcess());
        await using var service = CreateService(runner, factory, new FakeDelay());

        var result = await service.GetStatusAsync(LocalPort);

        Assert.True(result.IsInstalled);
        Assert.True(result.IsBackendRunning);
        Assert.True(result.RequiresHttpsCertificateEnablement);
        Assert.Contains("HTTPS", result.Error);
        Assert.False(result.IsConfigured);
        Assert.Null(result.Url);
        Assert.Single(runner.Calls);
        Assert.Equal(["status", "--json"], runner.Calls[0].Arguments);
        Assert.Empty(factory.Calls);
    }

    [Fact]
    public async Task Enable_HttpsNotEnabledFailsBeforeStartingForegroundWithoutGenericTimeout()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatusWithCertDomains(null)));
        var factory = new FakeForegroundProcessFactory(new FakeForegroundProcess());
        await using var service = CreateService(runner, factory, new FakeDelay());

        var result = await service.EnableAsync(LocalPort);

        Assert.False(result.Succeeded);
        Assert.False(result.Changed);
        Assert.True(result.Status.RequiresHttpsCertificateEnablement);
        Assert.Contains("Enable HTTPS", result.Error);
        Assert.DoesNotContain("整體時限", result.Error);
        Assert.Single(runner.Calls);
        Assert.Empty(factory.Calls);
    }

    [Fact]
    public async Task GetStatus_MissingCertDomainsFailsClosedInsteadOfAssumingHttpsIsEnabled()
    {
        var runner = new ScriptedRunner(
            Success(JsonSerializer.Serialize(new
            {
                BackendState = "Running",
                Self = new { DNSName = DnsName }
            })));
        await using var service = CreateService(runner, new FakeForegroundProcess());

        var result = await service.GetStatusAsync(LocalPort);

        Assert.NotNull(result.Error);
        Assert.Contains("CertDomains", result.Error);
        Assert.False(result.RequiresHttpsCertificateEnablement);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task GetStatus_RejectsNonTailscaleDnsForCandidateOrigin()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus(dnsName: "attacker.example.com")),
            Success("{}"));
        await using var service = CreateService(runner, new FakeForegroundProcess());

        var result = await service.GetStatusAsync(LocalPort);

        Assert.True(result.IsBackendRunning);
        Assert.Null(result.CandidateUrl);
        Assert.Null(result.Url);
    }

    [Fact]
    public async Task GetStatus_OverallDeadlineStopsBlockedCommandOnce()
    {
        var runner = ScriptedRunner.BlockingAfter();
        await using var service = CreateService(
            runner,
            new FakeForegroundProcessFactory(new FakeForegroundProcess()),
            new FakeDelay(),
            startupProbeAttempts: 100,
            operationTimeout: TimeSpan.FromMilliseconds(50));

        var result = await service.GetStatusAsync(LocalPort)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(result.Error);
        Assert.Contains("整體時限", result.Error);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task GetStatus_CallerCancellationIsNotConvertedToTimeoutStatus()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new ScriptedRunner();
        await using var service = CreateService(runner, new FakeForegroundProcess());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetStatusAsync(LocalPort, cancellation.Token));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task GetStatus_ServicesVirtualIp8443IsNotNodePortConflict()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success(ServicesConfig(Target)));
        await using var service = CreateService(
            runner,
            new FakeForegroundProcessFactory(new FakeForegroundProcess()),
            new FakeDelay());

        var result = await service.GetStatusAsync(LocalPort);

        Assert.Null(result.Error);
        Assert.False(result.IsConfigured);
        Assert.False(result.HasHttpsPortConflict);
    }

    [Fact]
    public async Task GetStatus_UnknownForegroundShapeFailsClosed()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success(DirectForegroundShape(Target)));
        await using var service = CreateService(
            runner,
            new FakeForegroundProcessFactory(new FakeForegroundProcess()),
            new FakeDelay());

        var result = await service.GetStatusAsync(LocalPort);

        Assert.NotNull(result.Error);
        Assert.False(result.IsConfigured);
        Assert.False(result.IsOwnedByThisService);
    }

    [Fact]
    public async Task GetStatus_UnknownRootShapeFailsClosed()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{\"FutureRoutes\":[]}"));
        await using var service = CreateService(
            runner,
            new FakeForegroundProcessFactory(new FakeForegroundProcess()),
            new FakeDelay());

        var result = await service.GetStatusAsync(LocalPort);

        Assert.NotNull(result.Error);
        Assert.Contains("不支援欄位", result.Error);
    }

    [Fact]
    public async Task Enable_StartsExactForegroundCommandAndConfirmsNestedSession()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(ForegroundConfig(Target)));
        var process = new FakeForegroundProcess();
        var factory = new FakeForegroundProcessFactory(process);
        await using var service = CreateService(runner, factory, new FakeDelay());

        var result = await service.EnableAsync(LocalPort);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.True(result.Status.IsConfigured);
        Assert.True(result.Status.IsOwnedByThisService);
        Assert.False(result.Status.HasHttpsPortConflict);
        Assert.Equal(result.Status.CandidateUrl, result.Status.Url);
        var start = Assert.Single(factory.Calls);
        Assert.Equal(
            ["serve", "--yes", "--https=8443", Target],
            start.Arguments);
        Assert.False(process.HasExited);
        Assert.Equal(0, process.KillCount);
        AssertNoPersistentMutationCommands(runner, factory);
    }

    [Fact]
    public async Task Enable_PollsUntilExactForegroundSessionAppears()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success("{}"),
            Success(ForegroundConfig(Target)));
        var process = new FakeForegroundProcess();
        var delay = new FakeDelay();
        await using var service = CreateService(
            runner,
            new FakeForegroundProcessFactory(process),
            delay,
            startupProbeAttempts: 3);

        var result = await service.EnableAsync(LocalPort);

        Assert.True(result.Succeeded);
        Assert.Equal(1, delay.CallCount);
        Assert.Equal(4, runner.Calls.Count);
    }

    [Fact]
    public async Task Enable_OverallDeadlineDoesNotMultiplyByProbeAttemptsAndCleansChild()
    {
        var runner = ScriptedRunner.BlockingAfter(
            Success(NodeStatus()),
            Success("{}"));
        var process = new FakeForegroundProcess(waitResults: [false]);
        await using var service = CreateService(
            runner,
            new FakeForegroundProcessFactory(process),
            new FakeDelay(),
            startupProbeAttempts: 100,
            operationTimeout: TimeSpan.FromMilliseconds(50));

        var result = await service.EnableAsync(LocalPort)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Succeeded);
        Assert.Contains("整體時限", result.Error);
        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task Enable_RequiresChildSuccessMarkerBeforeBindingForegroundSession()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(ForegroundConfig(Target)));
        var process = new FakeForegroundProcess(
            standardOutput: string.Empty,
            waitResults: [false]);
        await using var service = CreateService(
            runner,
            new FakeForegroundProcessFactory(process),
            new FakeDelay(),
            startupProbeAttempts: 1);

        var result = await service.EnableAsync(LocalPort);

        Assert.False(result.Succeeded);
        Assert.False(result.Status.IsOwnedByThisService);
        Assert.Equal(1, process.KillCount);
    }

    [Fact]
    public async Task Enable_AllowsExplicitFalseFunnelEntryAndStillOwnsPrivateSession()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(ForegroundConfig(Target, allowFunnel: false)));
        await using var service = CreateService(runner, new FakeForegroundProcess());

        var result = await service.EnableAsync(LocalPort);

        Assert.True(result.Succeeded);
        Assert.True(result.Status.IsOwnedByThisService);
        Assert.False(result.Status.HasHttpsPortConflict);
    }

    [Fact]
    public async Task Enable_PreservesServicesVirtualIpConfigurationAlongsideOwnedNodeRoute()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success(ServicesConfig("http://127.0.0.1:50000")),
            Success(ServicesAndForegroundConfig(
                "http://127.0.0.1:50000",
                Target)));
        await using var service = CreateService(runner, new FakeForegroundProcess());

        var result = await service.EnableAsync(LocalPort);

        Assert.True(result.Succeeded);
        Assert.True(result.Status.IsOwnedByThisService);
        Assert.False(result.Status.HasHttpsPortConflict);
    }

    [Fact]
    public async Task GetStatus_DoesNotTransferOwnershipToDifferentForegroundSession()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(ForegroundConfig(Target, sessionId: "session-owned")),
            Success(NodeStatus()),
            Success(ForegroundConfig(Target, sessionId: "session-other")));
        await using var service = CreateService(runner, new FakeForegroundProcess());
        Assert.True((await service.EnableAsync(LocalPort)).Succeeded);

        var result = await service.GetStatusAsync(LocalPort);

        Assert.True(result.IsConfigured);
        Assert.False(result.IsOwnedByThisService);
        Assert.True(result.HasHttpsPortConflict);
    }

    [Fact]
    public async Task Enable_RefusesExactButUnownedPersistentConfiguration()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success(PersistentConfig(Target)));
        var factory = new FakeForegroundProcessFactory(new FakeForegroundProcess());
        await using var service = CreateService(runner, factory, new FakeDelay());

        var result = await service.EnableAsync(LocalPort);

        Assert.False(result.Succeeded);
        Assert.True(result.Status.IsConfigured);
        Assert.False(result.Status.IsOwnedByThisService);
        Assert.True(result.Status.HasHttpsPortConflict);
        Assert.Empty(factory.Calls);
    }

    [Fact]
    public async Task Enable_RefusesExistingForegroundSessionEvenWhenTargetMatches()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success(ForegroundConfig(Target)));
        var factory = new FakeForegroundProcessFactory(new FakeForegroundProcess());
        await using var service = CreateService(runner, factory, new FakeDelay());

        var result = await service.EnableAsync(LocalPort);

        Assert.False(result.Succeeded);
        Assert.True(result.Status.IsConfigured);
        Assert.True(result.Status.HasHttpsPortConflict);
        Assert.Empty(factory.Calls);
    }

    [Fact]
    public async Task Enable_RefusesFunnelConfigurationWithoutStartingChild()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success(PersistentConfig(Target, allowFunnel: true)));
        var factory = new FakeForegroundProcessFactory(new FakeForegroundProcess());
        await using var service = CreateService(runner, factory, new FakeDelay());

        var result = await service.EnableAsync(LocalPort);

        Assert.False(result.Succeeded);
        Assert.True(result.Status.HasHttpsPortConflict);
        Assert.Empty(factory.Calls);
        AssertNoPersistentMutationCommands(runner, factory);
    }

    [Fact]
    public async Task Enable_StopsOwnedChildWhenAnother8443SessionAppearsDuringProbe()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(TwoForegroundSessions(Target, "http://127.0.0.1:59999")));
        var process = new FakeForegroundProcess(waitResults: [false]);
        var factory = new FakeForegroundProcessFactory(process);
        await using var service = CreateService(runner, factory, new FakeDelay());

        var result = await service.EnableAsync(LocalPort);

        Assert.False(result.Succeeded);
        Assert.False(result.Status.IsOwnedByThisService);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
        AssertNoPersistentMutationCommands(runner, factory);
    }

    [Fact]
    public async Task Enable_StopsOwnedChildWhenTopLevel8443AlsoAppearsDuringProbe()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(TopLevelAndForegroundConfig(Target, "http://127.0.0.1:59999")));
        var process = new FakeForegroundProcess(waitResults: [false]);
        var factory = new FakeForegroundProcessFactory(process);
        await using var service = CreateService(runner, factory, new FakeDelay());

        var result = await service.EnableAsync(LocalPort);

        Assert.False(result.Succeeded);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
        AssertNoPersistentMutationCommands(runner, factory);
    }

    [Fact]
    public async Task Enable_RequiresConfigToBeDirectForegroundSessionValue()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(IndirectForegroundConfig(Target)));
        var process = new FakeForegroundProcess(waitResults: [false]);
        var factory = new FakeForegroundProcessFactory(process);
        await using var service = CreateService(runner, factory, new FakeDelay());

        var result = await service.EnableAsync(LocalPort);

        Assert.False(result.Succeeded);
        Assert.Equal(1, process.KillCount);
        AssertNoPersistentMutationCommands(runner, factory);
    }

    [Fact]
    public async Task Enable_ReportsBoundedDiagnosticsWhenForegroundChildExitsEarly()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"));
        var process = new FakeForegroundProcess(
            hasExited: true,
            exitCode: 23,
            standardError: "serve failed");
        var factory = new FakeForegroundProcessFactory(process);
        await using var service = CreateService(runner, factory, new FakeDelay());

        var result = await service.EnableAsync(LocalPort);

        Assert.False(result.Succeeded);
        Assert.Contains("PID", result.Error);
        Assert.Contains("ExitCode=23", result.Error);
        Assert.Contains("serve failed", result.Error);
        Assert.Equal(0, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task Enable_DoesNotSucceedWhenChildExitsDuringExactStatusProbe()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(ForegroundConfig(Target)));
        var process = new FakeForegroundProcess(exitOnHasExitedRead: 2);
        var factory = new FakeForegroundProcessFactory(process);
        await using var service = CreateService(runner, factory, new FakeDelay());

        var result = await service.EnableAsync(LocalPort);

        Assert.False(result.Succeeded);
        Assert.False(result.Status.IsOwnedByThisService);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task Disable_WaitsThenKillsOnlyOwnedForegroundProcess()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(ForegroundConfig(Target)),
            Success(NodeStatus()),
            Success("{}"));
        var process = new FakeForegroundProcess(waitResults: [false]);
        var factory = new FakeForegroundProcessFactory(process);
        await using var service = CreateService(runner, factory, new FakeDelay());
        Assert.True((await service.EnableAsync(LocalPort)).Succeeded);

        var result = await service.DisableAsync(LocalPort);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
        Assert.False(result.Status.IsConfigured);
        AssertNoPersistentMutationCommands(runner, factory);
    }

    [Fact]
    public async Task Disable_PollsUntilOwnedForegroundRouteDisappears()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(ForegroundConfig(Target)),
            Success(NodeStatus()),
            Success(ForegroundConfig(Target)),
            Success(NodeStatus()),
            Success("{}"));
        var process = new FakeForegroundProcess(waitResults: [false]);
        var factory = new FakeForegroundProcessFactory(process);
        var delay = new FakeDelay();
        await using var service = CreateService(
            runner,
            factory,
            delay,
            startupProbeAttempts: 3);
        Assert.True((await service.EnableAsync(LocalPort)).Succeeded);

        var result = await service.DisableAsync(LocalPort);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.Equal(1, delay.CallCount);
        Assert.False(result.Status.HasHttpsPortConflict);
    }

    [Fact]
    public async Task Disable_OverallDeadlineDoesNotMultiplyAndNeverReportsSuccess()
    {
        var runner = ScriptedRunner.BlockingAfter(
            Success(NodeStatus()),
            Success("{}"),
            Success(ForegroundConfig(Target)));
        var process = new FakeForegroundProcess(waitResults: [false]);
        await using var service = CreateService(
            runner,
            new FakeForegroundProcessFactory(process),
            new FakeDelay(),
            startupProbeAttempts: 100,
            operationTimeout: TimeSpan.FromMilliseconds(50));
        Assert.True((await service.EnableAsync(LocalPort)).Succeeded);

        var result = await service.DisableAsync(LocalPort)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Succeeded);
        Assert.True(result.Changed);
        Assert.Contains("整體時限", result.Error);
        Assert.Equal(4, runner.Calls.Count);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task Disable_FailsWhenHttps8443StillExistsAfterPolling()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(ForegroundConfig(Target)),
            Success(NodeStatus()),
            Success(ForegroundConfig(Target)),
            Success(NodeStatus()),
            Success(ForegroundConfig(Target)));
        var process = new FakeForegroundProcess(waitResults: [false]);
        var factory = new FakeForegroundProcessFactory(process);
        await using var service = CreateService(
            runner,
            factory,
            new FakeDelay(),
            startupProbeAttempts: 2);
        Assert.True((await service.EnableAsync(LocalPort)).Succeeded);

        var result = await service.DisableAsync(LocalPort);

        Assert.False(result.Succeeded);
        Assert.True(result.Changed);
        Assert.True(result.Status.HasHttpsPortConflict);
        Assert.Contains("stale route", result.Error);
        AssertNoPersistentMutationCommands(runner, factory);
    }

    [Fact]
    public async Task Disable_WithNoOwnedProcessNeverTouchesUnownedConfiguration()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success(PersistentConfig(Target)));
        var factory = new FakeForegroundProcessFactory(new FakeForegroundProcess());
        await using var service = CreateService(runner, factory, new FakeDelay());

        var result = await service.DisableAsync(LocalPort);

        Assert.False(result.Succeeded);
        Assert.False(result.Changed);
        Assert.True(result.Status.HasHttpsPortConflict);
        Assert.Empty(factory.Calls);
        AssertNoPersistentMutationCommands(runner, factory);
    }

    [Fact]
    public async Task Dispose_KillsOwnedForegroundProcessAndIsIdempotent()
    {
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success(ForegroundConfig(Target)));
        var process = new FakeForegroundProcess(waitResults: [false]);
        var factory = new FakeForegroundProcessFactory(process);
        var service = CreateService(runner, factory, new FakeDelay());
        var unexpected = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.ForegroundProcessExited += (_, _) => unexpected.TrySetResult(true);
        Assert.True((await service.EnableAsync(LocalPort)).Succeeded);

        await service.DisposeAsync();
        await service.DisposeAsync();
        var completed = await Task.WhenAny(unexpected.Task, Task.Delay(150));

        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
        Assert.NotSame(unexpected.Task, completed);
        AssertNoPersistentMutationCommands(runner, factory);
    }

    [Fact]
    public async Task CancellationDuringStartupAlwaysKillsOwnedChild()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new ScriptedRunner(
            Success(NodeStatus()),
            Success("{}"),
            Success("{}"));
        var process = new FakeForegroundProcess(waitResults: [false]);
        var factory = new FakeForegroundProcessFactory(process);
        var delay = new CancelingDelay(cancellation);
        await using var service = CreateService(
            runner,
            factory,
            delay,
            startupProbeAttempts: 3);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.EnableAsync(LocalPort, cancellation.Token));

        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
        AssertNoPersistentMutationCommands(runner, factory);
    }

    [Fact]
    public async Task InvalidPort_IsRejectedBeforeDiscoveryOrProcessStart()
    {
        var locator = new FakeLocator(ExecutablePath);
        var runner = new ScriptedRunner();
        var factory = new FakeForegroundProcessFactory(new FakeForegroundProcess());
        await using var service = new TailscaleServeService(
            locator,
            runner,
            factory,
            new FakeDelay());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.EnableAsync(0));

        Assert.Equal(0, locator.CallCount);
        Assert.Empty(runner.Calls);
        Assert.Empty(factory.Calls);
    }

    [Fact]
    public async Task MissingExecutable_ReturnsUnavailableWithoutExecutingAnything()
    {
        var locator = new FakeLocator(null);
        var runner = new ScriptedRunner();
        var factory = new FakeForegroundProcessFactory(new FakeForegroundProcess());
        await using var service = new TailscaleServeService(
            locator,
            runner,
            factory,
            new FakeDelay());

        var result = await service.GetStatusAsync(LocalPort);

        Assert.False(result.IsInstalled);
        Assert.NotNull(result.Error);
        Assert.Equal(1, locator.CallCount);
        Assert.Empty(runner.Calls);
        Assert.Empty(factory.Calls);
    }

    private static TailscaleServeService CreateService(
        ScriptedRunner runner,
        FakeForegroundProcess process)
        => CreateService(
            runner,
            new FakeForegroundProcessFactory(process),
            new FakeDelay());

    private static TailscaleServeService CreateService(
        ScriptedRunner runner,
        FakeForegroundProcessFactory factory,
        ITailscaleDelay delay,
        int startupProbeAttempts = 3,
        TimeSpan? operationTimeout = null)
        => new(
            new FakeLocator(ExecutablePath),
            runner,
            factory,
            delay,
            commandTimeout: TimeSpan.FromMilliseconds(275),
            startupProbeAttempts: startupProbeAttempts,
            startupProbeInterval: TimeSpan.FromMilliseconds(1),
            operationTimeout: operationTimeout);

    private static TailscaleFunnelService CreateFunnelService(
        ScriptedRunner runner,
        FakeForegroundProcessFactory factory)
        => new(
            new FakeLocator(ExecutablePath),
            runner,
            factory,
            new FakeDelay(),
            commandTimeout: TimeSpan.FromMilliseconds(275),
            startupProbeAttempts: 3,
            startupProbeInterval: TimeSpan.FromMilliseconds(1));

    private static TailscaleFunnelService CreateFunnelService(
        ScriptedRunner runner,
        FakeForegroundProcess process)
        => CreateFunnelService(runner, new FakeForegroundProcessFactory(process));

    private static void AssertNoPersistentMutationCommands(
        ScriptedRunner runner,
        FakeForegroundProcessFactory factory)
    {
        var allArguments = runner.Calls.SelectMany(call => call.Arguments)
            .Concat(factory.Calls.SelectMany(call => call.Arguments))
            .ToArray();
        Assert.DoesNotContain("--bg", allArguments);
        Assert.DoesNotContain("off", allArguments);
        Assert.DoesNotContain("reset", allArguments);
        Assert.DoesNotContain(
            allArguments,
            argument => argument.Contains("funnel", StringComparison.OrdinalIgnoreCase));
    }

    private static TailscaleCommandResult Success(string standardOutput = "")
        => new(0, standardOutput, string.Empty);

    private static string NodeStatus(
        string backendState = "Running",
        string dnsName = DnsName)
        => NodeStatusWithCertDomains(
            [dnsName.Trim().TrimEnd('.')],
            backendState,
            dnsName);

    private static string NodeStatusWithCertDomains(
        IReadOnlyList<string>? certDomains,
        string backendState = "Running",
        string dnsName = DnsName)
        => JsonSerializer.Serialize(new
        {
            BackendState = backendState,
            CertDomains = certDomains,
            Self = new
            {
                DNSName = dnsName
            }
        });

    private static string PersistentConfig(
        string target,
        bool? allowFunnel = null,
        int httpsPort = TailscaleServeService.ServeHttpsPort)
        => JsonSerializer.Serialize(CreateNodeConfig(target, allowFunnel, httpsPort));

    private static string ServicesConfig(string target)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Services"] = new Dictionary<string, object?>
            {
                ["svc:web"] = CreateNodeConfig(target)
            }
        });

    private static string ServicesAndForegroundConfig(
        string serviceTarget,
        string foregroundTarget)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Services"] = new Dictionary<string, object?>
            {
                ["svc:web"] = CreateNodeConfig(serviceTarget)
            },
            ["Foreground"] = new Dictionary<string, object?>
            {
                ["session-owned"] = CreateNodeConfig(foregroundTarget)
            }
        });

    private static string DirectForegroundShape(string target)
    {
        var direct = CreateNodeConfig(target);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Foreground"] = direct
        });
    }

    private static string ForegroundConfig(
        string target,
        bool? allowFunnel = null,
        string sessionId = "session-owned",
        int httpsPort = TailscaleServeService.ServeHttpsPort)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Foreground"] = new Dictionary<string, object?>
            {
                [sessionId] = CreateNodeConfig(target, allowFunnel, httpsPort)
            }
        });

    private static string TwoForegroundSessions(string firstTarget, string secondTarget)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Foreground"] = new Dictionary<string, object?>
            {
                ["session-owned"] = CreateNodeConfig(firstTarget),
                ["session-user"] = CreateNodeConfig(secondTarget)
            }
        });

    private static string TopLevelAndForegroundConfig(
        string foregroundTarget,
        string topLevelTarget)
    {
        var root = CreateNodeConfig(topLevelTarget);
        root["Foreground"] = new Dictionary<string, object?>
        {
            ["session-owned"] = CreateNodeConfig(foregroundTarget)
        };
        return JsonSerializer.Serialize(root);
    }

    private static string IndirectForegroundConfig(string target)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Foreground"] = new Dictionary<string, object?>
            {
                ["session-owned"] = new Dictionary<string, object?>
                {
                    ["UnexpectedWrapper"] = CreateNodeConfig(target)
                }
            }
        });

    private static Dictionary<string, object?> CreateNodeConfig(
        string target,
        bool? allowFunnel = null,
        int httpsPort = TailscaleServeService.ServeHttpsPort)
    {
        var hostPort = $"{DnsName}:{httpsPort}";
        var config = new Dictionary<string, object?>
        {
            ["TCP"] = new Dictionary<string, object?>
            {
                [httpsPort.ToString()] = new
                {
                    HTTPS = true
                }
            },
            ["Web"] = new Dictionary<string, object?>
            {
                [hostPort] = new
                {
                    Handlers = new Dictionary<string, object?>
                    {
                        ["/"] = new
                        {
                            Proxy = target
                        }
                    }
                }
            }
        };
        if (allowFunnel is { } allow)
        {
            config["AllowFunnel"] = new Dictionary<string, object?>
            {
                [hostPort] = allow
            };
        }

        return config;
    }

    private sealed class FakeLocator(string? executablePath) : ITailscaleExecutableLocator
    {
        public int CallCount { get; private set; }

        public string? FindExecutable()
        {
            CallCount++;
            return executablePath;
        }
    }

    private sealed class ScriptedRunner : ITailscaleCommandRunner
    {
        private readonly Queue<TailscaleCommandResult> _results;
        private readonly bool _blockWhenExhausted;

        public ScriptedRunner(params TailscaleCommandResult[] results)
            : this(false, results)
        {
        }

        private ScriptedRunner(
            bool blockWhenExhausted,
            params TailscaleCommandResult[] results)
        {
            _blockWhenExhausted = blockWhenExhausted;
            _results = new Queue<TailscaleCommandResult>(results);
        }

        public static ScriptedRunner BlockingAfter(params TailscaleCommandResult[] results)
            => new(true, results);

        public List<CommandCall> Calls { get; } = [];

        public async Task<TailscaleCommandResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new CommandCall(executablePath, [.. arguments], timeout));
            if (_results.Count == 0)
            {
                if (_blockWhenExhausted)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                throw new InvalidOperationException("No scripted Tailscale result remains.");
            }

            return _results.Dequeue();
        }
    }

    private sealed class FakeForegroundProcessFactory(FakeForegroundProcess process)
        : ITailscaleForegroundProcessFactory
    {
        public List<ForegroundStartCall> Calls { get; } = [];

        public Task<ITailscaleForegroundProcess> StartAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new ForegroundStartCall(executablePath, [.. arguments]));
            return Task.FromResult<ITailscaleForegroundProcess>(process);
        }
    }

    private sealed class FakeForegroundProcess : ITailscaleForegroundProcess
    {
        private readonly Queue<bool> _waitResults;
        private readonly TaskCompletionSource<bool> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hasExited;

        public FakeForegroundProcess(
            bool hasExited = false,
            int? exitCode = null,
            string standardOutput = CandidateOrigin,
            string standardError = "",
            IEnumerable<bool>? waitResults = null,
            int? exitOnHasExitedRead = null)
        {
            _hasExited = hasExited;
            ExitCode = exitCode;
            StandardOutput = standardOutput;
            StandardError = standardError;
            _waitResults = new Queue<bool>(waitResults ?? []);
            ExitOnHasExitedRead = exitOnHasExitedRead;
            if (hasExited)
            {
                _completion.TrySetResult(true);
            }
        }

        public int ProcessId { get; } = 4242;
        public bool HasExited
        {
            get
            {
                HasExitedReadCount++;
                if (ExitOnHasExitedRead is { } read && HasExitedReadCount >= read)
                {
                    _hasExited = true;
                    ExitCode ??= 0;
                    _completion.TrySetResult(true);
                }

                return _hasExited;
            }
        }
        public int? ExitCode { get; private set; }
        public string StandardOutput { get; }
        public string StandardError { get; }
        public Task Completion => _completion.Task;
        public int KillCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int HasExitedReadCount { get; private set; }
        public int? ExitOnHasExitedRead { get; }
        public List<TimeSpan> WaitTimeouts { get; } = [];

        public Task<bool> WaitForExitAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitTimeouts.Add(timeout);
            var result = _waitResults.Count > 0 && _waitResults.Dequeue();
            if (result)
            {
                _hasExited = true;
                ExitCode ??= 0;
                _completion.TrySetResult(true);
            }

            return Task.FromResult(result);
        }

        public void KillEntireProcessTree()
        {
            KillCount++;
            _hasExited = true;
            ExitCode ??= -1;
            _completion.TrySetResult(true);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _hasExited = true;
            _completion.TrySetResult(true);
            return ValueTask.CompletedTask;
        }

        public void ExitUnexpectedly(int exitCode = 1)
        {
            ExitCode = exitCode;
            _hasExited = true;
            _completion.TrySetResult(true);
        }
    }

    private sealed class FakeDelay : ITailscaleDelay
    {
        public int CallCount { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CancelingDelay(CancellationTokenSource cancellation)
        : ITailscaleDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellationToken);
        }
    }

    private sealed record CommandCall(
        string ExecutablePath,
        IReadOnlyList<string> Arguments,
        TimeSpan Timeout);

    private sealed record ForegroundStartCall(
        string ExecutablePath,
        IReadOnlyList<string> Arguments);
}
