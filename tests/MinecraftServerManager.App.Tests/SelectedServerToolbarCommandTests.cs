using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.App.Tests;

public sealed class SelectedServerToolbarCommandTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void DetailToolbar_KeepsSingleSelectedServerLifecycleCommands()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        var buttons = document.Descendants(Presentation + "Button").ToArray();

        var start = Assert.Single(buttons, button =>
            (string?)button.Attribute("Content") == "{DynamicResource L10n.main.action.start}");
        Assert.Equal("{Binding StartSelectedCommand}", (string?)start.Attribute("Command"));

        var stop = Assert.Single(buttons, button =>
            (string?)button.Attribute("Content") == "{DynamicResource L10n.main.action.stop}");
        Assert.Equal("{Binding StopSelectedCommand}", (string?)stop.Attribute("Command"));

        Assert.DoesNotContain(buttons, button =>
            (string?)button.Attribute("Content") is "啟動選取" or "停止選取" or "全部停止" or "雙控制台");
        Assert.DoesNotContain(document.Descendants().SelectMany(element => element.Attributes()), attribute =>
            attribute.Value.Contains("ToggleSplitConsoleCommand", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectedLifecycleCommands_FollowSelectionAndEveryServerState()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new MainWindowViewModel(new ApplicationPaths(temporary.Path));

        Assert.False(viewModel.StartSelectedCommand.CanExecute(null));
        Assert.False(viewModel.StopSelectedCommand.CanExecute(null));

        var server = CreateServer(temporary.Path, "Selected");
        viewModel.Servers.Add(server);
        viewModel.SelectedServer = server;

        AssertCommandState(viewModel, canStart: true, canStop: false);

        server.SetState(ServerState.Crashed);
        AssertCommandState(viewModel, canStart: true, canStop: false);

        server.SetState(ServerState.Faulted);
        AssertCommandState(viewModel, canStart: true, canStop: false);

        server.SetState(ServerState.Starting);
        AssertCommandState(viewModel, canStart: false, canStop: true);

        server.SetState(ServerState.Running);
        AssertCommandState(viewModel, canStart: false, canStop: true);

        server.SetState(ServerState.Stopping);
        AssertCommandState(viewModel, canStart: false, canStop: false);

        viewModel.SelectedServer = null;
        AssertCommandState(viewModel, canStart: false, canStop: false);
    }

    [Fact]
    public async Task SelectedLifecycleCommands_NotifyForCurrentSelectionOnly()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
        var first = CreateServer(temporary.Path, "First");
        var second = CreateServer(temporary.Path, "Second");
        viewModel.Servers.Add(first);
        viewModel.Servers.Add(second);
        viewModel.SelectedServer = first;

        var startChanges = 0;
        var stopChanges = 0;
        viewModel.StartSelectedCommand.CanExecuteChanged += (_, _) => startChanges++;
        viewModel.StopSelectedCommand.CanExecuteChanged += (_, _) => stopChanges++;

        first.SetState(ServerState.Running);
        Assert.Equal(1, startChanges);
        Assert.Equal(1, stopChanges);

        viewModel.SelectedServer = second;
        Assert.Equal(2, startChanges);
        Assert.Equal(2, stopChanges);

        first.SetState(ServerState.Faulted);
        Assert.Equal(2, startChanges);
        Assert.Equal(2, stopChanges);

        second.SetState(ServerState.Starting);
        Assert.Equal(3, startChanges);
        Assert.Equal(3, stopChanges);
    }

    [Fact]
    public async Task StopSelected_UsesSelectionSnapshotWhenAStatusObserverChangesSelection()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
        var first = CreateServer(temporary.Path, "First");
        var second = CreateServer(temporary.Path, "Second");
        viewModel.Servers.Add(first);
        viewModel.Servers.Add(second);
        viewModel.SelectedServer = first;

        viewModel.PropertyChanged += ChangeSelectionOnStatus;
        try
        {
            await InvokePrivateTaskAsync(viewModel, "StopSelectedAsync");
        }
        finally
        {
            viewModel.PropertyChanged -= ChangeSelectionOnStatus;
        }

        var lifecycleGates = GetPrivateField<ConcurrentDictionary<Guid, SemaphoreSlim>>(
            viewModel,
            "_lifecycleGates");
        Assert.Same(second, viewModel.SelectedServer);
        Assert.Contains(first.Id, lifecycleGates.Keys);
        Assert.DoesNotContain(second.Id, lifecycleGates.Keys);

        void ChangeSelectionOnStatus(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName == nameof(MainWindowViewModel.StatusMessage))
            {
                viewModel.SelectedServer = second;
            }
        }
    }

    [Fact]
    public async Task StartSelectedCommand_DisablesAndNotifiesAcrossTrackedModpackUpdate()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
        var server = CreateServer(temporary.Path, "Updating");
        server.Model.ModpackSource = ModpackSourceKind.Modrinth;
        server.Model.ModpackProjectId = "project";
        server.Model.ModpackVersionId = "old-version";
        viewModel.SelectedServer = server;
        Assert.True(viewModel.StartSelectedCommand.CanExecute(null));

        var observedCanExecute = new List<bool>();
        viewModel.StartSelectedCommand.CanExecuteChanged += (_, _) =>
            observedCanExecute.Add(viewModel.StartSelectedCommand.CanExecute(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.ApplyModpackUpdateAsync(
                server,
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
                CancellationToken.None));

        Assert.Equal([false, true], observedCanExecute);
        Assert.True(viewModel.StartSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void LifecycleCommandImplementations_NeverEnumerateTheServerCollection()
    {
        var source = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "ViewModels",
            "MainWindowViewModel.cs")));
        var startSelected = ExtractPrivateMethod(source, "private async Task StartSelectedAsync(");
        var stopSelected = ExtractPrivateMethod(source, "private async Task StopSelectedAsync(");

        Assert.Contains("SelectedServer", startSelected, StringComparison.Ordinal);
        Assert.Contains("StartServerAsync(server", startSelected, StringComparison.Ordinal);
        Assert.DoesNotContain("Servers", startSelected, StringComparison.Ordinal);
        Assert.Contains("var server = SelectedServer;", stopSelected, StringComparison.Ordinal);
        Assert.Contains("StopServerCoordinatedAsync(server.Id", stopSelected, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedServer.", stopSelected, StringComparison.Ordinal);
        Assert.DoesNotContain("Servers", stopSelected, StringComparison.Ordinal);

        Assert.Null(typeof(MainWindowViewModel).GetProperty("StartAllCommand"));
        Assert.Null(typeof(MainWindowViewModel).GetProperty("StopAllCommand"));
        Assert.Null(typeof(MainWindowViewModel).GetProperty("ToggleSplitConsoleCommand"));
        Assert.DoesNotContain("private async Task StartAllAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private async Task StopAllAsync(", source, StringComparison.Ordinal);
        Assert.Contains("StopAllServersCoordinatedAsync", source, StringComparison.Ordinal);
    }

    private static void AssertCommandState(
        MainWindowViewModel viewModel,
        bool canStart,
        bool canStop)
    {
        Assert.Equal(canStart, viewModel.StartSelectedCommand.CanExecute(null));
        Assert.Equal(canStop, viewModel.StopSelectedCommand.CanExecute(null));
    }

    private static ServerInstanceViewModel CreateServer(string root, string name)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        return new ServerInstanceViewModel(
            new ServerInstance
            {
                Name = name,
                DirectoryPath = directory,
                ServerJarPath = Path.Combine(directory, "server.jar")
            },
            (_, _) => Task.CompletedTask);
    }

    private static string ExtractPrivateMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method was not found: {signature}");
        var end = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        return source[start..(end < 0 ? source.Length : end)];
    }

    private static async Task InvokePrivateTaskAsync(object target, string methodName)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(target, null));
        await task;
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

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);
}
