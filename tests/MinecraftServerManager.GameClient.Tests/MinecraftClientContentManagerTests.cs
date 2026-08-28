using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class MinecraftClientContentManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-client-content-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _instance;
    private readonly string _sources;

    public MinecraftClientContentManagerTests()
    {
        _instance = Path.Combine(_root, "instance");
        _sources = Path.Combine(_root, "sources");
        Directory.CreateDirectory(_instance);
        Directory.CreateDirectory(_sources);
    }

    [Fact]
    public async Task ImportAsync_SupportsEveryManagedContentKindAndListsMetadata()
    {
        var mod = CreateFile("sample.jar", 7);
        var resourcePack = CreateFile("resources.zip", 11);
        var shaderPack = CreateDirectory("shader", ("shaders/main.glsl", 13));
        var save = CreateDirectory("My World", ("level.dat", 17), ("region/r.0.0.mca", 19));
        var screenshot = CreateFile("capture.png", 23);
        using var manager = new MinecraftClientContentManager(_instance);

        await manager.ImportAsync(Request(MinecraftClientContentKind.Mod, mod));
        await manager.ImportAsync(Request(MinecraftClientContentKind.ResourcePack, resourcePack));
        await manager.ImportAsync(Request(MinecraftClientContentKind.ShaderPack, shaderPack));
        await manager.ImportAsync(Request(MinecraftClientContentKind.Save, save));
        await manager.ImportAsync(Request(MinecraftClientContentKind.Screenshot, screenshot));

        var modEntry = Assert.Single((await manager.ListAsync(MinecraftClientContentKind.Mod)).Entries);
        Assert.Equal(7, modEntry.SizeBytes);
        Assert.Equal(1, modEntry.FileCount);
        Assert.True(modEntry.IsSafe);
        Assert.False(modEntry.IsDirectory);

        var saveEntry = Assert.Single((await manager.ListAsync(MinecraftClientContentKind.Save)).Entries);
        Assert.Equal(36, saveEntry.SizeBytes);
        Assert.Equal(2, saveEntry.FileCount);
        Assert.True(saveEntry.IsDirectory);
        Assert.True(File.Exists(Path.Combine(_instance, "saves", "My World", "level.dat")));
    }

    [Fact]
    public async Task ImportAsync_RejectsUnsupportedExtensionsWithoutCreatingVisibleContent()
    {
        var source = CreateFile("not-a-mod.txt", 5);
        using var manager = new MinecraftClientContentManager(_instance);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.ImportAsync(Request(MinecraftClientContentKind.Mod, source)));

        Assert.Empty((await manager.ListAsync(MinecraftClientContentKind.Mod)).Entries);
    }

    [Fact]
    public async Task ImportAsync_IsBatchAtomicWhenOneDestinationConflicts()
    {
        var first = CreateFile("first.jar", 5);
        var duplicate = CreateFile("duplicate.jar", 7);
        Directory.CreateDirectory(Path.Combine(_instance, "mods"));
        await File.WriteAllBytesAsync(Path.Combine(_instance, "mods", "duplicate.jar"), new byte[3]);
        using var manager = new MinecraftClientContentManager(_instance);

        await Assert.ThrowsAsync<IOException>(() => manager.ImportAsync(
            new MinecraftClientContentImportRequest(
                MinecraftClientContentKind.Mod,
                [first, duplicate])));

        Assert.False(File.Exists(Path.Combine(_instance, "mods", "first.jar")));
        Assert.Equal(3, new FileInfo(Path.Combine(_instance, "mods", "duplicate.jar")).Length);
    }

    [Fact]
    public async Task ImportAsync_PreCancelledOperationLeavesNoContentOrStagingPayload()
    {
        var source = CreateFile("cancelled.jar", 1_024);
        using var manager = new MinecraftClientContentManager(_instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ImportAsync(
                Request(MinecraftClientContentKind.Mod, source),
                cancellationToken: cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_instance, "mods", "cancelled.jar")));
    }

    [Fact]
    public async Task ImportAsync_CancellationAfterProgressCleansPartialStagingContent()
    {
        var source = CreateFile("cancel-during-copy.jar", 256 * 1024);
        using var manager = new MinecraftClientContentManager(_instance);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<MinecraftClientContentProgress>(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ImportAsync(
            Request(MinecraftClientContentKind.Mod, source),
            progress,
            cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_instance, "mods", "cancel-during-copy.jar")));
        var staging = Path.Combine(_instance, ".x-mcsv-content", "staging");
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    [Fact]
    public async Task ImportAsync_RejectsSourcesInsideTheManagedInstance()
    {
        var source = Path.Combine(_instance, "inside.jar");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        using var manager = new MinecraftClientContentManager(_instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => manager.ImportAsync(Request(MinecraftClientContentKind.Mod, source)));
    }

    [Fact]
    public async Task ImportAsync_EnforcesFileSizeAndEntryCountBounds()
    {
        var large = CreateFile("large.jar", 9);
        var directory = CreateDirectory(
            "world",
            ("one.dat", 1),
            ("two.dat", 1),
            ("three.dat", 1));
        var limits = new MinecraftClientContentLimits
        {
            MaximumSingleFileBytes = 8,
            MaximumImportBytes = 32,
            MaximumImportFiles = 3,
        };
        using var manager = new MinecraftClientContentManager(_instance, limits);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.ImportAsync(Request(MinecraftClientContentKind.Mod, large)));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.ImportAsync(Request(MinecraftClientContentKind.Save, directory)));
        Assert.Empty((await manager.ListAsync(MinecraftClientContentKind.Mod)).Entries);
        Assert.Empty((await manager.ListAsync(MinecraftClientContentKind.Save)).Entries);
    }

    [Fact]
    public async Task SetEnabledAsync_MovesContentBetweenIsolatedRoots()
    {
        var source = CreateFile("toggle.jar", 3);
        using var manager = new MinecraftClientContentManager(_instance);
        var imported = Assert.Single((await manager.ImportAsync(
            Request(MinecraftClientContentKind.Mod, source))).ImportedEntries);

        var disabled = await manager.SetEnabledAsync(imported.Key, enabled: false);

        Assert.Equal(MinecraftClientContentState.Disabled, disabled.Key.State);
        Assert.False(File.Exists(Path.Combine(_instance, "mods", "toggle.jar")));
        Assert.True(File.Exists(Path.Combine(
            _instance,
            ".x-mcsv-content",
            "disabled",
            "mods",
            "toggle.jar")));

        var enabled = await manager.SetEnabledAsync(disabled.Key, enabled: true);
        Assert.Equal(MinecraftClientContentState.Enabled, enabled.Key.State);
        Assert.True(File.Exists(Path.Combine(_instance, "mods", "toggle.jar")));
    }

    [Fact]
    public async Task RemoveAsync_DefaultsToRecoverableRecycleAndRestorePreservesState()
    {
        var source = CreateFile("recover.jar", 5);
        using var manager = new MinecraftClientContentManager(_instance);
        var imported = Assert.Single((await manager.ImportAsync(
            Request(MinecraftClientContentKind.Mod, source))).ImportedEntries);
        var disabled = await manager.SetEnabledAsync(imported.Key, enabled: false);

        var recycled = Assert.IsType<MinecraftClientContentEntry>(
            await manager.RemoveAsync(disabled.Key));

        Assert.Equal(MinecraftClientContentState.Recycled, recycled.Key.State);
        Assert.NotNull(recycled.Key.RecycleId);
        Assert.False(File.Exists(Path.Combine(
            _instance,
            ".x-mcsv-content",
            "disabled",
            "mods",
            "recover.jar")));
        Assert.Single(await manager.ListRecycleBinAsync(MinecraftClientContentKind.Mod));

        var restored = await manager.RestoreAsync(recycled.Key);
        Assert.Equal(MinecraftClientContentState.Disabled, restored.Key.State);
        Assert.True(File.Exists(Path.Combine(
            _instance,
            ".x-mcsv-content",
            "disabled",
            "mods",
            "recover.jar")));
        Assert.Empty(await manager.ListRecycleBinAsync());
    }

    [Fact]
    public async Task RestoreAsync_RejectsConflictsAndKeepsRecyclePayloadRecoverable()
    {
        var source = CreateFile("conflict.jar", 5);
        using var manager = new MinecraftClientContentManager(_instance);
        var imported = Assert.Single((await manager.ImportAsync(
            Request(MinecraftClientContentKind.Mod, source))).ImportedEntries);
        var recycled = Assert.IsType<MinecraftClientContentEntry>(
            await manager.RemoveAsync(imported.Key));
        await File.WriteAllBytesAsync(Path.Combine(_instance, "mods", "conflict.jar"), new byte[9]);

        await Assert.ThrowsAsync<IOException>(() => manager.RestoreAsync(recycled.Key));

        Assert.Equal(9, new FileInfo(Path.Combine(_instance, "mods", "conflict.jar")).Length);
        Assert.Single(await manager.ListRecycleBinAsync());
    }

    [Fact]
    public async Task RemoveAsync_OnlyDeletesPermanentlyWhenExplicitlyRequested()
    {
        var source = CreateFile("delete.jar", 5);
        using var manager = new MinecraftClientContentManager(_instance);
        var imported = Assert.Single((await manager.ImportAsync(
            Request(MinecraftClientContentKind.Mod, source))).ImportedEntries);

        Assert.Null(await manager.RemoveAsync(imported.Key, permanently: true));

        Assert.False(File.Exists(Path.Combine(_instance, "mods", "delete.jar")));
        Assert.Empty(await manager.ListRecycleBinAsync());
    }

    [Fact]
    public async Task PurgeRecycleBinAsync_CanFilterByContentKind()
    {
        using var manager = new MinecraftClientContentManager(_instance);
        var mod = Assert.Single((await manager.ImportAsync(Request(
            MinecraftClientContentKind.Mod,
            CreateFile("purge.jar", 2)))).ImportedEntries);
        var screenshot = Assert.Single((await manager.ImportAsync(Request(
            MinecraftClientContentKind.Screenshot,
            CreateFile("keep.png", 2)))).ImportedEntries);
        await manager.RemoveAsync(mod.Key);
        await manager.RemoveAsync(screenshot.Key);

        Assert.Equal(1, await manager.PurgeRecycleBinAsync(MinecraftClientContentKind.Mod));

        var remaining = Assert.Single(await manager.ListRecycleBinAsync());
        Assert.Equal(MinecraftClientContentKind.Screenshot, remaining.Key.Kind);
    }

    [Fact]
    public async Task Mutations_RejectForgedTraversalKeys()
    {
        using var manager = new MinecraftClientContentManager(_instance);
        var key = new MinecraftClientContentItemKey(
            MinecraftClientContentKind.Mod,
            MinecraftClientContentState.Enabled,
            "..\\outside.jar");

        await Assert.ThrowsAsync<ArgumentException>(() => manager.SetEnabledAsync(key, enabled: false));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.RemoveAsync(key));
    }

    [Fact]
    public async Task ListAsync_EnforcesCategoryItemLimit()
    {
        Directory.CreateDirectory(Path.Combine(_instance, "screenshots"));
        for (var index = 0; index < 4; index++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(_instance, "screenshots", $"{index}.png"),
                [1]);
        }

        using var manager = new MinecraftClientContentManager(
            _instance,
            new MinecraftClientContentLimits { MaximumItemsPerCategory = 3 });

        var snapshot = await manager.ListAsync(MinecraftClientContentKind.Screenshot);

        Assert.Equal(3, snapshot.Entries.Count);
        Assert.True(snapshot.ItemLimitReached);
    }

    [Fact]
    public async Task ListAsync_UsesOneBoundedInspectionBudgetAcrossAllItems()
    {
        var saves = Path.Combine(_instance, "saves");
        for (var index = 0; index < 4; index++)
        {
            var world = Path.Combine(saves, $"World {index}");
            Directory.CreateDirectory(world);
            await File.WriteAllBytesAsync(Path.Combine(world, "level.dat"), new byte[4]);
        }

        using var manager = new MinecraftClientContentManager(
            _instance,
            new MinecraftClientContentLimits
            {
                MaximumSnapshotInspectionEntries = 4,
                MaximumSnapshotInspectionBytes = 5,
            });

        var snapshot = await manager.ListAsync(MinecraftClientContentKind.Save);

        Assert.True(snapshot.ItemLimitReached);
        Assert.InRange(snapshot.Entries.Count, 1, 2);
        Assert.Contains(snapshot.Entries, entry => entry.InspectionTruncated);
        Assert.True(snapshot.Entries.Sum(entry => entry.FileCount) <= 2);
    }

    [Fact]
    public async Task ListRecycleBinAsync_BoundsCandidatesBeforeSorting()
    {
        using var manager = new MinecraftClientContentManager(
            _instance,
            new MinecraftClientContentLimits
            {
                MaximumRecycleCandidates = 2,
                MaximumItemsPerCategory = 10,
            });
        for (var index = 0; index < 5; index++)
        {
            var imported = Assert.Single((await manager.ImportAsync(Request(
                MinecraftClientContentKind.Mod,
                CreateFile($"recycle-{index}.jar", 2)))).ImportedEntries);
            await manager.RemoveAsync(imported.Key);
        }

        var entries = await manager.ListRecycleBinAsync(MinecraftClientContentKind.Mod);

        Assert.InRange(entries.Count, 1, 2);
    }

    [Fact]
    public async Task ImportAsync_RejectsReparsePointsWithoutFollowingThem()
    {
        var external = CreateDirectory("external", ("secret.jar", 3));
        var link = Path.Combine(_sources, "linked-world");
        try
        {
            Directory.CreateSymbolicLink(link, external);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        using var manager = new MinecraftClientContentManager(_instance);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => manager.ImportAsync(Request(MinecraftClientContentKind.Save, link)));
        Assert.Empty((await manager.ListAsync(MinecraftClientContentKind.Save)).Entries);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string CreateFile(string name, int bytes)
    {
        var path = Path.Combine(_sources, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private string CreateDirectory(string name, params (string RelativePath, int Bytes)[] files)
    {
        var path = Path.Combine(_sources, name);
        Directory.CreateDirectory(path);
        foreach (var file in files)
        {
            var filePath = Path.Combine(path, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllBytes(filePath, new byte[file.Bytes]);
        }

        return path;
    }

    private static MinecraftClientContentImportRequest Request(
        MinecraftClientContentKind kind,
        params string[] sources) => new(kind, sources);

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
