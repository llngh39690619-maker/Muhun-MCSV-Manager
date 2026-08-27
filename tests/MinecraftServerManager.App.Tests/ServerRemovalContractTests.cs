using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Contracts.Localization;

namespace MinecraftServerManager.App.Tests;

public sealed class ServerRemovalContractTests
{
    [Fact]
    public void ServerList_UsesTheContextRowAsTheRemovalCommandParameter()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.DoesNotContain(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content") == "移除選取項目");

        var removeItem = Assert.Single(
            document.Descendants(presentation + "MenuItem"),
            element => (string?)element.Attribute("Header")
                       == "{DynamicResource L10n.main.context.remove}");
        Assert.Equal("{Binding Tag.RemoveServerCommand}", (string?)removeItem.Attribute("Command"));
        Assert.Equal("{Binding DataContext}", (string?)removeItem.Attribute("CommandParameter"));
        Assert.Equal(
            "{DynamicResource ThemedContextMenuItemStyle}",
            (string?)removeItem.Attribute("Style"));

        var deleteItem = Assert.Single(
            document.Descendants(presentation + "MenuItem"),
            element => (string?)element.Attribute("Header")
                       == "{DynamicResource L10n.main.context.delete}");
        Assert.Equal("{Binding Tag.DeleteServerCommand}", (string?)deleteItem.Attribute("Command"));
        Assert.Equal("{Binding DataContext}", (string?)deleteItem.Attribute("CommandParameter"));
        Assert.Equal(
            "{DynamicResource ThemedContextMenuItemStyle}",
            (string?)deleteItem.Attribute("Style"));
        Assert.Same(removeItem.Parent, deleteItem.Parent);

        var contextMenu = Assert.IsType<XElement>(removeItem.Parent);
        Assert.Equal(presentation + "ContextMenu", contextMenu.Name);
        Assert.Equal(
            "{Binding PlacementTarget, RelativeSource={RelativeSource Self}}",
            (string?)contextMenu.Attribute("DataContext"));
        Assert.Equal(
            "{DynamicResource ThemedContextMenuStyle}",
            (string?)contextMenu.Attribute("Style"));

        var placementBorder = contextMenu.Parent?.Parent;
        Assert.NotNull(placementBorder);
        Assert.Equal(presentation + "Border", placementBorder!.Name);
        Assert.Contains("AncestorType={x:Type ListBox}", (string?)placementBorder.Attribute("Tag"));
    }

    [Fact]
    public void ConfirmationDialog_IsDarkTwoButtonConfirmationWithoutTextInput()
    {
        var document = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "RemoveServerConfirmationDialog.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var window = Assert.IsType<XElement>(document.Root);
        Assert.Equal("{StaticResource AppWindowStyle}", (string?)window.Attribute("Style"));
        Assert.Equal("{DynamicResource WindowBrush}", (string?)window.Attribute("Background"));
        var root = Assert.Single(window.Elements(presentation + "Grid"));
        Assert.Equal("DialogRoot", root.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value);
        Assert.Equal("{DynamicResource WindowBrush}", (string?)root.Attribute("Background"));
        Assert.Empty(document.Descendants(presentation + "TextBox"));

        var buttons = document.Descendants(presentation + "Button").ToArray();
        Assert.Contains(buttons, element => (string?)element.Attribute("Content") == "{DynamicResource L10n.common.confirm}");
        Assert.Contains(buttons, element => (string?)element.Attribute("Content") == "{DynamicResource L10n.common.cancel}");
        Assert.Equal(2, buttons.Length);
        Assert.Contains(document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{DynamicResource L10n.removeServer.preserved}");
        Assert.Contains(
            "資料夾與所有檔案都會完整保留",
            ProductLocalizationCatalog.GetDocument("zh-TW").Strings["removeServer.preserved"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void PermanentDeletionDialog_IsDarkTwoButtonConfirmationWithoutTextInput()
    {
        var document = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "DeleteServerConfirmationDialog.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var window = Assert.IsType<XElement>(document.Root);
        Assert.Equal("{StaticResource AppWindowStyle}", (string?)window.Attribute("Style"));
        Assert.Equal("{DynamicResource WindowBrush}", (string?)window.Attribute("Background"));
        var root = Assert.Single(window.Elements(presentation + "Grid"));
        Assert.Equal("DialogRoot", root.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value);
        Assert.Equal("{DynamicResource WindowBrush}", (string?)root.Attribute("Background"));
        Assert.Empty(document.Descendants(presentation + "TextBox"));

        var buttons = document.Descendants(presentation + "Button").ToArray();
        Assert.Contains(buttons, element => (string?)element.Attribute("Content") == "{DynamicResource L10n.deleteServer.confirm}");
        Assert.Contains(buttons, element => (string?)element.Attribute("Content") == "{DynamicResource L10n.common.cancel}");
        Assert.Equal(2, buttons.Length);
        Assert.Contains(document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{DynamicResource L10n.deleteServer.warning}");
        Assert.Contains(
            "永久刪除，且無法復原",
            ProductLocalizationCatalog.GetDocument("zh-TW").Strings["deleteServer.warning"],
            StringComparison.Ordinal);
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding DirectoryPath}");
    }

    [Fact]
    public async Task RemovingANonSelectedContextRow_RemovesOnlyThatRowAndPreservesItsFolder()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "portable app 中文");
        var alphaDirectory = CreateServerDirectory(applicationRoot, "Alpha Server");
        var betaDirectory = CreateServerDirectory(applicationRoot, "Beta Server");
        var alphaModel = CreateServer("Alpha", alphaDirectory);
        var betaModel = CreateServer("Beta", betaDirectory);
        await WriteSettingsAsync(applicationRoot, alphaModel, betaModel);

        var confirmation = new FakeRemovalConfirmationService(true);
        await using var viewModel = new MainWindowViewModel(
            new ApplicationPaths(applicationRoot),
            confirmation);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var selected = Assert.Single(viewModel.Servers, server => server.Id == alphaModel.Id);
        var contextRow = Assert.Single(viewModel.Servers, server => server.Id == betaModel.Id);
        Assert.Same(selected, viewModel.SelectedServer);

        await viewModel.RemoveServerAsync(contextRow);

        Assert.Single(confirmation.Requests);
        Assert.Equal(("Beta", betaDirectory), confirmation.Requests[0]);
        Assert.Single(viewModel.Servers);
        Assert.Same(selected, viewModel.SelectedServer);
        Assert.DoesNotContain(viewModel.Servers, server => server.Id == betaModel.Id);
        Assert.True(Directory.Exists(betaDirectory));
        Assert.True(File.Exists(Path.Combine(betaDirectory, "server.jar")));

        using var persistedStore = new JsonSettingsStore<ManagerSettings>(
            Path.Combine(applicationRoot, "manager.json"));
        var persisted = await persistedStore.LoadAsync();
        var remaining = Assert.Single(Assert.IsType<ManagerSettings>(persisted).Instances);
        Assert.Equal(alphaModel.Id, remaining.Id);
    }

    [Fact]
    public async Task RunningContextRow_IsRejectedBeforeConfirmationAndNothingIsDeleted()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "running rejection");
        var serverDirectory = CreateServerDirectory(applicationRoot, "Running Server");
        var model = CreateServer("Running", serverDirectory);
        await WriteSettingsAsync(applicationRoot, model);

        var confirmation = new FakeRemovalConfirmationService(true);
        await using var viewModel = new MainWindowViewModel(
            new ApplicationPaths(applicationRoot),
            confirmation);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var server = Assert.Single(viewModel.Servers);
        server.SetState(ServerState.Running);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.RemoveServerAsync(server));

        Assert.Contains("停止", exception.Message, StringComparison.Ordinal);
        Assert.Empty(confirmation.Requests);
        Assert.Single(viewModel.Servers);
        Assert.True(Directory.Exists(serverDirectory));
        Assert.True(File.Exists(Path.Combine(serverDirectory, "server.jar")));
    }

    [Fact]
    public async Task CancellingConfirmation_LeavesManagementListAndFolderUntouched()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "cancel removal");
        var serverDirectory = CreateServerDirectory(applicationRoot, "Keep Server");
        var model = CreateServer("Keep", serverDirectory);
        await WriteSettingsAsync(applicationRoot, model);

        var confirmation = new FakeRemovalConfirmationService(false);
        await using var viewModel = new MainWindowViewModel(
            new ApplicationPaths(applicationRoot),
            confirmation);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        await viewModel.RemoveServerAsync(Assert.Single(viewModel.Servers));

        Assert.Single(confirmation.Requests);
        Assert.Single(viewModel.Servers);
        Assert.True(Directory.Exists(serverDirectory));
        Assert.True(File.Exists(Path.Combine(serverDirectory, "server.jar")));
    }

    [Fact]
    public async Task PermanentlyDeletingANonSelectedImportedServer_DeletesExactFolderAndPersistsOtherRow()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "permanent deletion");
        var alphaDirectory = CreateServerDirectory(applicationRoot, "Alpha Server");
        var betaDirectory = CreateServerDirectory(applicationRoot, "Beta Server");
        File.WriteAllText(Path.Combine(betaDirectory, "world.dat"), "world");
        var alphaModel = CreateServer("Alpha", alphaDirectory);
        var betaModel = CreateServer("Beta", betaDirectory);
        await WriteSettingsAsync(applicationRoot, alphaModel, betaModel);

        var deletion = new FakeDeletionConfirmationService(true);
        await using var viewModel = new MainWindowViewModel(
            new ApplicationPaths(applicationRoot),
            new FakeRemovalConfirmationService(true),
            deletion);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var selected = Assert.Single(viewModel.Servers, server => server.Id == alphaModel.Id);
        var contextRow = Assert.Single(viewModel.Servers, server => server.Id == betaModel.Id);
        Assert.Same(selected, viewModel.SelectedServer);

        await viewModel.DeleteServerPermanentlyAsync(contextRow);

        Assert.Equal(("Beta", betaDirectory), Assert.Single(deletion.Requests));
        Assert.Single(viewModel.Servers);
        Assert.Same(selected, viewModel.SelectedServer);
        Assert.True(Directory.Exists(alphaDirectory));
        Assert.False(Directory.Exists(betaDirectory));

        using var persistedStore = new JsonSettingsStore<ManagerSettings>(
            Path.Combine(applicationRoot, "manager.json"));
        var persisted = Assert.IsType<ManagerSettings>(await persistedStore.LoadAsync());
        Assert.Equal(alphaModel.Id, Assert.Single(persisted.Instances).Id);
    }

    [Fact]
    public async Task CancellingPermanentDeletion_LeavesRowFolderAndSettingsUntouched()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "cancel permanent deletion");
        var serverDirectory = CreateServerDirectory(applicationRoot, "Keep Server");
        var model = CreateServer("Keep", serverDirectory);
        await WriteSettingsAsync(applicationRoot, model);

        var deletion = new FakeDeletionConfirmationService(false);
        await using var viewModel = new MainWindowViewModel(
            new ApplicationPaths(applicationRoot),
            new FakeRemovalConfirmationService(true),
            deletion);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        await viewModel.DeleteServerPermanentlyAsync(Assert.Single(viewModel.Servers));

        Assert.Single(deletion.Requests);
        Assert.Single(viewModel.Servers);
        Assert.True(Directory.Exists(serverDirectory));
        Assert.True(File.Exists(Path.Combine(serverDirectory, "server.jar")));
        using var persistedStore = new JsonSettingsStore<ManagerSettings>(
            Path.Combine(applicationRoot, "manager.json"));
        Assert.Single(Assert.IsType<ManagerSettings>(await persistedStore.LoadAsync()).Instances);
    }

    [Fact]
    public async Task PermanentDeletion_RejectsApplicationRootAndKeepsTheRow()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "dangerous root");
        Directory.CreateDirectory(applicationRoot);
        var model = CreateServer("Dangerous", applicationRoot);
        await WriteSettingsAsync(applicationRoot, model);

        await using var viewModel = new MainWindowViewModel(
            new ApplicationPaths(applicationRoot),
            new FakeRemovalConfirmationService(true),
            new FakeDeletionConfirmationService(true));
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var server = Assert.Single(viewModel.Servers);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => viewModel.DeleteServerPermanentlyAsync(server));

        Assert.Contains("重要根目錄", exception.Message, StringComparison.Ordinal);
        Assert.Single(viewModel.Servers);
        Assert.True(Directory.Exists(applicationRoot));
        Assert.True(File.Exists(Path.Combine(applicationRoot, "manager.json")));
    }

    [Fact]
    public void PermanentDeletionValidator_RejectsDriveRootAndAnAncestorOfTheAppRoot()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "app", "data");
        Directory.CreateDirectory(applicationRoot);
        var service = new ServerDirectoryDeletionService(new ApplicationPaths(applicationRoot));
        var driveRoot = Path.GetPathRoot(applicationRoot)!;
        var appAncestor = Directory.GetParent(applicationRoot)!.FullName;

        Assert.Throws<UnauthorizedAccessException>(
            () => service.ValidateDeletionTarget(driveRoot, []));
        Assert.Throws<UnauthorizedAccessException>(
            () => service.ValidateDeletionTarget(appAncestor, []));

        if (OperatingSystem.IsWindows())
        {
            var extendedAlias = @"\\?\" + applicationRoot;
            var exception = Assert.Throws<UnauthorizedAccessException>(
                () => service.ValidateDeletionTarget(extendedAlias, []));
            Assert.Contains("device path", exception.Message, StringComparison.OrdinalIgnoreCase);

            var forwardSlashAlias = "//?/" + applicationRoot.Replace('\\', '/');
            Assert.Throws<UnauthorizedAccessException>(
                () => service.ValidateDeletionTarget(forwardSlashAlias, []));

            var operatingSystemRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            }.Where(static path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
            Assert.All(
                operatingSystemRoots,
                path => Assert.Throws<UnauthorizedAccessException>(
                    () => service.ValidateDeletionTarget(path, [])));

            var forbiddenDescendants = new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32",
                    "drivers"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Windows Defender")
            }.Where(static path => Directory.Exists(path));
            Assert.All(
                forbiddenDescendants,
                path => Assert.Throws<UnauthorizedAccessException>(
                    () => service.ValidateDeletionTarget(path, [])));
        }
    }

    [Fact]
    public async Task PermanentDeletion_RejectsAReparsePointRootAndPreservesItsTarget()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "reparse root app");
        var outside = Path.Combine(directory.Path, "outside target");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        File.WriteAllText(marker, "keep");
        File.WriteAllBytes(Path.Combine(outside, "server.jar"), [0x50, 0x4B, 0x03, 0x04]);
        var linkedServer = Path.Combine(applicationRoot, "external servers", "Linked Server");
        Directory.CreateDirectory(Path.GetDirectoryName(linkedServer)!);
        CreateDirectoryJunction(linkedServer, outside);
        var model = CreateServer("Linked", linkedServer);
        await WriteSettingsAsync(applicationRoot, model);

        try
        {
            await using var viewModel = new MainWindowViewModel(
                new ApplicationPaths(applicationRoot),
                new FakeRemovalConfirmationService(true),
                new FakeDeletionConfirmationService(true));
            await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
            var server = Assert.Single(viewModel.Servers);

            var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => viewModel.DeleteServerPermanentlyAsync(server));

            Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(viewModel.Servers);
            Assert.True(Directory.Exists(linkedServer));
            Assert.True(File.Exists(marker));
        }
        finally
        {
            if (Directory.Exists(linkedServer))
            {
                Directory.Delete(linkedServer, recursive: false);
            }
        }
    }

    [Fact]
    public void PermanentDeletionValidator_RejectsBothDirectionsOfManagedPathOverlap()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "overlap app");
        var parentServer = Path.Combine(directory.Path, "managed", "Parent Server");
        var nestedServer = Path.Combine(parentServer, "world");
        Directory.CreateDirectory(nestedServer);
        var service = new ServerDirectoryDeletionService(new ApplicationPaths(applicationRoot));

        Assert.Throws<UnauthorizedAccessException>(() =>
            service.ValidateDeletionTarget(nestedServer, [parentServer]));
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.ValidateDeletionTarget(parentServer, [nestedServer]));
    }

    [Fact]
    public async Task PermanentDeletion_DoesNotFollowANestedLinkOutsideTheServer()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "outside sentinel app");
        var serverDirectory = CreateServerDirectory(applicationRoot, "Linked Content Server");
        var outside = Path.Combine(directory.Path, "outside sentinel");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        File.WriteAllText(marker, "keep");
        CreateDirectoryJunction(Path.Combine(serverDirectory, "redirect"), outside);
        var model = CreateServer("Linked Content", serverDirectory);
        await WriteSettingsAsync(applicationRoot, model);

        await using var viewModel = new MainWindowViewModel(
            new ApplicationPaths(applicationRoot),
            new FakeRemovalConfirmationService(true),
            new FakeDeletionConfirmationService(true));
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        await viewModel.DeleteServerPermanentlyAsync(Assert.Single(viewModel.Servers));

        Assert.Empty(viewModel.Servers);
        Assert.False(Directory.Exists(serverDirectory));
        Assert.True(File.Exists(marker));
        Assert.Equal("keep", File.ReadAllText(marker));
    }

    [Fact]
    public async Task PermanentDeletion_RejectsARedirectingIntermediateDirectory()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "redirecting path app");
        var outside = Path.Combine(directory.Path, "redirect destination");
        var physicalServer = Path.Combine(outside, "Server");
        Directory.CreateDirectory(physicalServer);
        var marker = Path.Combine(physicalServer, "keep.txt");
        File.WriteAllText(marker, "keep");
        File.WriteAllBytes(Path.Combine(physicalServer, "server.jar"), [0x50, 0x4B, 0x03, 0x04]);
        var importRoot = Path.Combine(directory.Path, "import root");
        Directory.CreateDirectory(importRoot);
        var redirect = Path.Combine(importRoot, "redirect");
        CreateDirectoryJunction(redirect, outside);
        var linkedServer = Path.Combine(redirect, "Server");
        var model = CreateServer("Redirected", linkedServer);
        await WriteSettingsAsync(applicationRoot, model);

        try
        {
            await using var viewModel = new MainWindowViewModel(
                new ApplicationPaths(applicationRoot),
                new FakeRemovalConfirmationService(true),
                new FakeDeletionConfirmationService(true));
            await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
            var server = Assert.Single(viewModel.Servers);

            var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => viewModel.DeleteServerPermanentlyAsync(server));

            Assert.Contains("redirecting directory", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(viewModel.Servers);
            Assert.True(File.Exists(marker));
            Assert.True(Directory.Exists(physicalServer));
        }
        finally
        {
            if (Directory.Exists(redirect))
            {
                Directory.Delete(redirect, recursive: false);
            }
        }
    }

    [Fact]
    public async Task PermanentDeletion_WaitsForTheBackupBarrierBeforeDeleting()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "backup barrier deletion");
        var serverDirectory = CreateServerDirectory(applicationRoot, "Barrier Server");
        var model = CreateServer("Barrier", serverDirectory);
        await WriteSettingsAsync(applicationRoot, model);

        await using var viewModel = new MainWindowViewModel(
            new ApplicationPaths(applicationRoot),
            new FakeRemovalConfirmationService(true),
            new FakeDeletionConfirmationService(true));
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var server = Assert.Single(viewModel.Servers);
        var field = typeof(MainWindowViewModel).GetField(
            "_backupGates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var backupGates = Assert.IsType<System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim>>(
            field?.GetValue(viewModel));
        var backupGate = backupGates.GetOrAdd(server.Id, static _ => new SemaphoreSlim(1, 1));
        await backupGate.WaitAsync();
        Task deletionTask = Task.CompletedTask;
        try
        {
            deletionTask = viewModel.DeleteServerPermanentlyAsync(server);
            await Task.Delay(100);

            Assert.False(deletionTask.IsCompleted);
            Assert.Single(viewModel.Servers);
            Assert.True(Directory.Exists(serverDirectory));
        }
        finally
        {
            backupGate.Release();
        }

        await deletionTask;
        Assert.Empty(viewModel.Servers);
        Assert.False(Directory.Exists(serverDirectory));
    }

    [Fact]
    public async Task PermanentDeletion_FromRunningUiState_UsesCoordinatedStopAndDeletesTheServer()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "running permanent deletion");
        var serverDirectory = CreateServerDirectory(applicationRoot, "Running Server");
        var model = CreateServer("Running", serverDirectory);
        await WriteSettingsAsync(applicationRoot, model);

        await using var viewModel = new MainWindowViewModel(
            new ApplicationPaths(applicationRoot),
            new FakeRemovalConfirmationService(true),
            new FakeDeletionConfirmationService(true));
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var server = Assert.Single(viewModel.Servers);
        server.SetState(ServerState.Running);

        await viewModel.DeleteServerPermanentlyAsync(server);

        Assert.Empty(viewModel.Servers);
        Assert.False(Directory.Exists(serverDirectory));
        Assert.Contains("已完全刪除", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PermanentDeletion_RejectsDirectoryIdentitySwapDuringConfirmation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var applicationRoot = Path.Combine(directory.Path, "identity swap app");
        var serverDirectory = CreateServerDirectory(applicationRoot, "Confirmed Server");
        var movedOriginal = Path.Combine(applicationRoot, "original moved");
        var replacementMarker = Path.Combine(serverDirectory, "replacement.txt");
        var model = CreateServer("Confirmed", serverDirectory);
        await WriteSettingsAsync(applicationRoot, model);
        var confirmation = new FakeDeletionConfirmationService(
            true,
            () =>
            {
                Directory.Move(serverDirectory, movedOriginal);
                Directory.CreateDirectory(serverDirectory);
                File.WriteAllText(replacementMarker, "replacement");
            });

        await using var viewModel = new MainWindowViewModel(
            new ApplicationPaths(applicationRoot),
            new FakeRemovalConfirmationService(true),
            confirmation);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        var exception = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
            viewModel.DeleteServerPermanentlyAsync(Assert.Single(viewModel.Servers)));

        Assert.Contains("identity changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(viewModel.Servers);
        Assert.True(File.Exists(replacementMarker));
        Assert.True(File.Exists(Path.Combine(movedOriginal, "server.jar")));
    }

    private static ServerInstance CreateServer(string name, string directoryPath)
        => new()
        {
            Name = name,
            DirectoryPath = directoryPath,
            ServerJarPath = Path.Combine(directoryPath, "server.jar"),
            CoreType = CoreType.Paper,
            MinecraftVersion = "1.21.1"
        };

    private static string CreateServerDirectory(string applicationRoot, string name)
    {
        var path = Path.Combine(applicationRoot, "external servers", name);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "server.jar"), [0x50, 0x4B, 0x03, 0x04]);
        return path;
    }

    private static async Task WriteSettingsAsync(
        string applicationRoot,
        params ServerInstance[] instances)
    {
        Directory.CreateDirectory(applicationRoot);
        using var store = new JsonSettingsStore<ManagerSettings>(
            Path.Combine(applicationRoot, "manager.json"));
        await store.SaveAsync(new ManagerSettings { Instances = [.. instances] });
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not create the test junction.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Could not create the test junction: {standardError}{standardOutput}");
        Assert.True(File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint));
    }

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

    private sealed class FakeRemovalConfirmationService(bool result)
        : IServerRemovalConfirmationService
    {
        public List<(string ServerName, string DirectoryPath)> Requests { get; } = [];

        public bool ConfirmRemoval(string serverName, string directoryPath)
        {
            Requests.Add((serverName, directoryPath));
            return result;
        }
    }

    private sealed class FakeDeletionConfirmationService(bool result, Action? onConfirm = null)
        : IServerDeletionConfirmationService
    {
        public List<(string ServerName, string DirectoryPath)> Requests { get; } = [];

        public bool ConfirmDeletion(string serverName, string directoryPath)
        {
            Requests.Add((serverName, directoryPath));
            onConfirm?.Invoke();
            return result;
        }
    }
}
