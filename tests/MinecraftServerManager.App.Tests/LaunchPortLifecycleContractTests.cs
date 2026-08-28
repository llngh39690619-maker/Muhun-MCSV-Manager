using System.IO;
using System.Runtime.CompilerServices;

namespace MinecraftServerManager.App.Tests;

public sealed class LaunchPortLifecycleContractTests
{
    [Fact]
    public void PrepareStart_AlwaysAllocatesFromMinecraftDefaultPortAtLaunchTime()
    {
        var source = ReadMainWindowViewModelSource();
        var prepareStart = ExtractPrivateMethod(
            source,
            "private async Task PrepareServerStartOnUiAsync(");

        Assert.Equal(1, CountOccurrences(prepareStart, "AssignAvailablePortAsync("));
        Assert.Contains(
            "requestedPort: ServerPortAllocator.DefaultPreferredPort",
            prepareStart,
            StringComparison.Ordinal);
        Assert.Contains("reserveForLaunch: true", prepareStart, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingStoppedConfiguration_DoesNotPerformLaunchTimePortAllocation()
    {
        var source = ReadMainWindowViewModelSource();
        var saveSettings = ExtractPrivateMethod(
            source,
            "private async Task SaveSelectedSettingsAsync(");
        var saveProperties = ExtractPrivateMethod(
            source,
            "private async Task SavePropertiesAsync(");

        Assert.DoesNotContain("AssignAvailablePortAsync(", saveSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("AssignAvailablePortAsync(", saveProperties, StringComparison.Ordinal);
        Assert.Contains("SaveSettingsAsync", saveSettings, StringComparison.Ordinal);
        Assert.Contains("SaveDocumentAsync", saveProperties, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingWhileStarting_IsRejectedBeforeEitherPersistenceEntryPointWrites()
    {
        var source = ReadMainWindowViewModelSource();
        var saveSettings = ExtractPrivateMethod(
            source,
            "private async Task SaveSelectedSettingsAsync(");
        var saveProperties = ExtractPrivateMethod(
            source,
            "private async Task SavePropertiesAsync(");

        AssertStartingGuardPrecedesWrite(saveSettings, "PersistConfiguredPortAsync(");
        AssertStartingGuardPrecedesWrite(saveProperties, "SaveDocumentAsync(");
    }

    [Fact]
    public void CoordinatedStarts_ReleaseOnlyFailedOrCancelledReservations()
    {
        var source = ReadMainWindowViewModelSource();
        var start = ExtractPrivateMethod(
            source,
            "private async Task<Guid> StartProcessCoordinatedAsync(");
        var tryStart = ExtractPrivateMethod(
            source,
            "private async Task<bool> TryStartProcessCoordinatedAsync(");

        AssertFailureOnlyPendingCleanup(start, expectedCleanupCount: 1);
        AssertFailureOnlyPendingCleanup(tryStart, expectedCleanupCount: 2);

        var canStartGuard = SliceBetween(
            tryStart,
            "if (!canStart())",
            "await _processManager.StartAsync(");
        Assert.Contains(
            "ReleasePendingLaunchPort(instance.Id);",
            canStartGuard,
            StringComparison.Ordinal);
        Assert.Contains("return false;", canStartGuard, StringComparison.Ordinal);
    }

    [Fact]
    public void StateEvents_PromoteAndReleaseTheExactLaunchSessionReservation()
    {
        var source = ReadMainWindowViewModelSource();
        var stateChanged = ExtractPrivateMethod(
            source,
            "private void OnServerStateChanged(");

        Assert.Contains(
            "_sessionLaunchPorts[sessionKey] = launchedPort;",
            stateChanged,
            StringComparison.Ordinal);
        Assert.Contains(
            "_pendingLaunchPortSessions[e.InstanceId] = e.SessionId;",
            stateChanged,
            StringComparison.Ordinal);
        Assert.Contains(
            "var activePort = _sessionLaunchPorts.TryGetValue(sessionKey",
            stateChanged,
            StringComparison.Ordinal);
        Assert.Contains("server.MarkPortAsActive(activePort);", stateChanged, StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(
                stateChanged,
                "ReleasePendingLaunchPort(server.Id, e.SessionId);"));
        Assert.DoesNotContain("_pendingLaunchPorts.TryRemove", stateChanged, StringComparison.Ordinal);
    }

    [Fact]
    public void Watchdog_ProbesThePortBoundToTheRunningSession()
    {
        var source = ReadMainWindowViewModelSource();
        var watchdog = ExtractPrivateMethod(
            source,
            "private async Task RunWatchdogSessionAsync(");

        Assert.Contains("var key = (instanceId, sessionId);", watchdog, StringComparison.Ordinal);
        Assert.Contains(
            "_sessionLaunchPorts.TryGetValue(key, out var sessionPort)",
            watchdog,
            StringComparison.Ordinal);

        var probeCall = SliceBetween(
            watchdog,
            "_minecraftStatusProbe.ProbeAsync(",
            ".ConfigureAwait(false)");
        Assert.Contains("activePort", probeCall, StringComparison.Ordinal);
        Assert.DoesNotContain("instance.Port", probeCall, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessManager_PreparedStartAbortReleasesTheUncommittedReservation()
    {
        var source = ReadMainWindowViewModelSource();
        var optionsStart = source.IndexOf(
            "_processManager = new ServerProcessManager(new ServerProcessManagerOptions",
            StringComparison.Ordinal);
        Assert.True(optionsStart >= 0, "The process-manager options initializer was not found.");
        var optionsEnd = source.IndexOf("});", optionsStart, StringComparison.Ordinal);
        Assert.True(optionsEnd > optionsStart, "The process-manager options initializer is incomplete.");
        var options = source[optionsStart..optionsEnd];

        Assert.Contains("PreparedStartAborted =", options, StringComparison.Ordinal);
        Assert.Contains("ReleasePendingLaunchPort", options, StringComparison.Ordinal);
    }

    private static void AssertStartingGuardPrecedesWrite(string method, string writeCall)
    {
        var guard = method.IndexOf("server.State == ServerState.Starting", StringComparison.Ordinal);
        var rejection = method.IndexOf("throw new InvalidOperationException", guard, StringComparison.Ordinal);
        var write = method.IndexOf(writeCall, StringComparison.Ordinal);

        Assert.True(guard >= 0, "The save entry point must explicitly reject Starting state.");
        Assert.True(rejection > guard, "The Starting-state guard must reject the save.");
        Assert.True(write > rejection, "The Starting-state rejection must occur before persistence.");
    }

    private static void AssertFailureOnlyPendingCleanup(
        string coordinatedStart,
        int expectedCleanupCount)
    {
        var processStart = coordinatedStart.IndexOf(
            "_processManager.StartAsync(",
            StringComparison.Ordinal);
        Assert.True(processStart >= 0, "The coordinated start must invoke the process manager.");

        var catchBlock = coordinatedStart.IndexOf(
            "catch",
            processStart,
            StringComparison.Ordinal);
        var cleanup = coordinatedStart.IndexOf(
            "ReleasePendingLaunchPort(instance.Id);",
            processStart,
            StringComparison.Ordinal);
        var finallyBlock = coordinatedStart.IndexOf(
            "finally",
            processStart,
            StringComparison.Ordinal);

        Assert.True(catchBlock > processStart, "A failed launch must have an explicit cleanup path.");
        Assert.True(
            cleanup > catchBlock && cleanup < finallyBlock,
            "A failed launch must release its reservation before entering the unconditional finally block.");
        Assert.DoesNotContain(
            "ReleasePendingLaunchPort",
            coordinatedStart[finallyBlock..],
            StringComparison.Ordinal);
        Assert.Equal(
            expectedCleanupCount,
            CountOccurrences(coordinatedStart, "ReleasePendingLaunchPort(instance.Id);"));
    }

    private static string SliceBetween(string source, string startToken, string endToken)
    {
        var start = source.IndexOf(startToken, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start token was not found: {startToken}");
        var end = source.IndexOf(endToken, start + startToken.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End token was not found after start token: {endToken}");
        return source[start..end];
    }

    private static string ReadMainWindowViewModelSource()
        => File.ReadAllText(TestRepositoryPaths.AppSource(
            "ViewModels",
            "MainWindowViewModel.cs"));

    private static string ExtractPrivateMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method was not found: {signature}");
        var end = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        return source[start..(end < 0 ? source.Length : end)];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
