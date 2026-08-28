using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class BulkServerSelectionCommandTests
{
    [Fact]
    public async Task SelectionMode_RequiresAtLeastOneCheckAndClearsEveryCheckOnExit()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
        var first = CreateServer(temporary.Path, "First");
        var second = CreateServer(temporary.Path, "Second");
        viewModel.Servers.Add(first);
        viewModel.Servers.Add(second);

        first.IsBulkSelected = true;
        Assert.False(first.IsBulkSelected);
        AssertBulkCommands(viewModel, canExecute: false);

        viewModel.ToggleBulkSelectionModeCommand.Execute(null);
        Assert.True(viewModel.IsBulkSelectionMode);
        AssertBulkCommands(viewModel, canExecute: false);

        first.IsBulkSelected = true;
        AssertBulkCommands(viewModel, canExecute: true);
        second.IsBulkSelected = true;

        viewModel.ToggleBulkSelectionModeCommand.Execute(null);

        Assert.False(viewModel.IsBulkSelectionMode);
        Assert.False(first.IsBulkSelected);
        Assert.False(second.IsBulkSelected);
        AssertBulkCommands(viewModel, canExecute: false);
    }

    [Fact]
    public async Task RemovingCheckedServer_ClearsDetachedRowAndCommandState()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
        var server = CreateServer(temporary.Path, "Removed");
        viewModel.Servers.Add(server);
        viewModel.ToggleBulkSelectionModeCommand.Execute(null);
        server.IsBulkSelected = true;
        AssertBulkCommands(viewModel, canExecute: true);

        viewModel.Servers.Remove(server);

        Assert.False(server.IsBulkSelected);
        AssertBulkCommands(viewModel, canExecute: false);

        // A detached row cannot silently re-enable commands through a stale subscription.
        server.IsBulkSelected = true;
        AssertBulkCommands(viewModel, canExecute: false);
    }

    [Fact]
    public async Task SharedOperationGate_DisablesSelectionAndBothOppositeCommands()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
        var server = CreateServer(temporary.Path, "Checked");
        viewModel.Servers.Add(server);
        viewModel.ToggleBulkSelectionModeCommand.Execute(null);
        server.IsBulkSelected = true;
        AssertBulkCommands(viewModel, canExecute: true);
        Assert.True(viewModel.ToggleBulkSelectionModeCommand.CanExecute(null));

        SetBatchOperationRunning(viewModel, value: true);
        try
        {
            AssertBulkCommands(viewModel, canExecute: false);
            Assert.False(viewModel.ToggleBulkSelectionModeCommand.CanExecute(null));
            Assert.False(viewModel.RemoveServerCommand.CanExecute(server));
            Assert.False(viewModel.DeleteServerCommand.CanExecute(server));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => viewModel.RemoveServerAsync(server));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => viewModel.DeleteServerPermanentlyAsync(server));
        }
        finally
        {
            SetBatchOperationRunning(viewModel, value: false);
        }

        AssertBulkCommands(viewModel, canExecute: true);
        Assert.True(viewModel.ToggleBulkSelectionModeCommand.CanExecute(null));
        Assert.True(viewModel.RemoveServerCommand.CanExecute(server));
        Assert.True(viewModel.DeleteServerCommand.CanExecute(server));
    }

    [Fact]
    public async Task StartChecked_ContinuesAfterPerServerFailuresAndReportsOneSummary()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
        var first = CreateServer(temporary.Path, "Installer One", isInstallerArtifact: true);
        var second = CreateServer(temporary.Path, "Installer Two", isInstallerArtifact: true);
        viewModel.Servers.Add(first);
        viewModel.Servers.Add(second);
        viewModel.ToggleBulkSelectionModeCommand.Execute(null);
        first.IsBulkSelected = true;
        second.IsBulkSelected = true;

        await InvokePrivateTaskAsync(viewModel, "StartCheckedServersAsync");

        Assert.Contains("全部啟動完成：成功 0、略過 0、失敗 2", viewModel.StatusMessage);
        Assert.Contains("Installer One", viewModel.StatusMessage);
        Assert.True(first.IsBulkSelected);
        Assert.True(second.IsBulkSelected);
        Assert.False(viewModel.IsBatchLifecycleOperationRunning);
    }

    [Fact]
    public async Task StopChecked_UsesCheckedSnapshotAndSkipsIneligibleAndUncheckedServers()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
        var running = CreateServer(temporary.Path, "Running");
        var starting = CreateServer(temporary.Path, "Starting");
        var stopped = CreateServer(temporary.Path, "Stopped");
        var selectedButUnchecked = CreateServer(temporary.Path, "Selected only");
        viewModel.Servers.Add(running);
        viewModel.Servers.Add(starting);
        viewModel.Servers.Add(stopped);
        viewModel.Servers.Add(selectedButUnchecked);
        running.SetState(ServerState.Running);
        starting.SetState(ServerState.Starting);
        selectedButUnchecked.SetState(ServerState.Running);
        viewModel.SelectedServer = selectedButUnchecked;
        viewModel.ToggleBulkSelectionModeCommand.Execute(null);
        running.IsBulkSelected = true;
        starting.IsBulkSelected = true;
        stopped.IsBulkSelected = true;

        await InvokePrivateTaskAsync(viewModel, "StopCheckedServersAsync");

        var lifecycleGates = GetPrivateField<ConcurrentDictionary<Guid, SemaphoreSlim>>(
            viewModel,
            "_lifecycleGates");
        Assert.Contains(running.Id, lifecycleGates.Keys);
        Assert.Contains(starting.Id, lifecycleGates.Keys);
        Assert.DoesNotContain(stopped.Id, lifecycleGates.Keys);
        Assert.DoesNotContain(selectedButUnchecked.Id, lifecycleGates.Keys);
        Assert.Same(selectedButUnchecked, viewModel.SelectedServer);
        Assert.Contains("略過 3", viewModel.StatusMessage);
        Assert.False(viewModel.IsBatchLifecycleOperationRunning);
    }

    [Fact]
    public async Task Shutdown_CancelsAndWaitsForTrackedBatchBeforeStopAllAndDispose()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task> operation = async cancellationToken =>
        {
            entered.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                exited.TrySetResult(true);
            }
        };

        var batchTask = InvokePrivateTask(
            viewModel,
            "RunTrackedCheckedServerBatchAsync",
            operation);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(await exited.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batchTask);
        Assert.False(viewModel.IsBatchLifecycleOperationRunning);
    }

    [Fact]
    public void BatchJavaPrerequisite_TargetsExplicitCheckedServerAndNeverCurrentDetailSelection()
    {
        var source = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "ViewModels",
            "MainWindowViewModel.cs")));
        var installHelper = ExtractPrivateMethod(
            source,
            "private async Task<string> DownloadJavaForMajorAsync(");
        var startHelper = ExtractPrivateMethod(
            source,
            "private async Task StartServerAsync(");
        var selectedDownload = ExtractPrivateMethod(
            source,
            "private async Task DownloadSelectedJavaAsync(");

        Assert.DoesNotContain("SelectedServer", installHelper, StringComparison.Ordinal);
        Assert.Contains("server.JavaExecutablePath = installedJavaPath;", startHelper, StringComparison.Ordinal);
        Assert.Contains("await SaveSettingsAsync();", startHelper, StringComparison.Ordinal);
        Assert.Contains("var targetServer = SelectedServer;", selectedDownload, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedServer.Model.JavaExecutablePath", selectedDownload, StringComparison.Ordinal);
    }

    private static void AssertBulkCommands(
        MainWindowViewModel viewModel,
        bool canExecute)
    {
        Assert.Equal(canExecute, viewModel.StartCheckedServersCommand.CanExecute(null));
        Assert.Equal(canExecute, viewModel.StopCheckedServersCommand.CanExecute(null));
    }

    private static ServerInstanceViewModel CreateServer(
        string root,
        string name,
        bool isInstallerArtifact = false)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        return new ServerInstanceViewModel(
            new ServerInstance
            {
                Name = name,
                DirectoryPath = directory,
                ServerJarPath = Path.Combine(directory, "server.jar"),
                IsInstallerArtifact = isInstallerArtifact
            },
            (_, _) => Task.CompletedTask);
    }

    private static void SetBatchOperationRunning(
        MainWindowViewModel viewModel,
        bool value)
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.IsBatchLifecycleOperationRunning))
            ?? throw new MissingMemberException(
                typeof(MainWindowViewModel).FullName,
                nameof(MainWindowViewModel.IsBatchLifecycleOperationRunning));
        property.SetValue(viewModel, value);
    }

    private static async Task InvokePrivateTaskAsync(object target, string methodName)
    {
        await InvokePrivateTask(target, methodName);
    }

    private static Task InvokePrivateTask(
        object target,
        string methodName,
        params object?[] arguments)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        return Assert.IsAssignableFrom<Task>(method.Invoke(target, arguments));
    }

    private static T GetPrivateField<T>(object target, string fieldName)
        where T : class
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        return Assert.IsType<T>(field.GetValue(target));
    }

    private static string ExtractPrivateMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method was not found: {signature}");
        var end = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        return source[start..(end < 0 ? source.Length : end)];
    }

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);
}
