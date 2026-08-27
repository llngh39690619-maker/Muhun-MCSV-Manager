using System.Diagnostics;
using System.Text.Json;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductDesiredRunIntentStoreTests
{
    [Fact]
    public async Task Intent_IsAtomicBoundedSortedAndDurableAcrossInstances()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var firstId = Guid.Parse("f0000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var store = new ProductDesiredRunIntentStore(layout);

        await store.SetDesiredAsync(firstId, true);
        await store.SetDesiredAsync(secondId, true);
        await store.SetDesiredAsync(firstId, true);

        Assert.Equal([secondId, firstId], store.GetDesiredServerIds());
        var path = Path.Combine(layout.Operations, ProductDesiredRunIntentStore.FileName);
        Assert.InRange(new FileInfo(path).Length, 2, ProductDesiredRunIntentStore.MaximumFileBytes);
        Assert.Empty(Directory.EnumerateFiles(layout.Operations, "*.tmp"));

        var recreated = new ProductDesiredRunIntentStore(layout);
        await recreated.LoadAsync();
        Assert.Equal([secondId, firstId], recreated.GetDesiredServerIds());

        await recreated.SetDesiredAsync(secondId, false);
        var third = new ProductDesiredRunIntentStore(layout);
        await third.LoadAsync();
        Assert.Equal([firstId], third.GetDesiredServerIds());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"schemaVersion\":2,\"serverIds\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"serverIds\":[\"00000000-0000-0000-0000-000000000000\"]}")]
    [InlineData("{\"schemaVersion\":1,\"serverIds\":[],\"futureCommand\":\"start-all\"}")]
    public async Task CorruptFutureOrAmbiguousIntent_FailsClosedWithoutReplacingSource(string payload)
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        layout.EnsureCreated();
        var path = Path.Combine(layout.Operations, ProductDesiredRunIntentStore.FileName);
        await File.WriteAllTextAsync(path, payload);
        var original = await File.ReadAllBytesAsync(path);
        var store = new ProductDesiredRunIntentStore(layout);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Throws<InvalidOperationException>(() => store.GetDesiredServerIds());
    }

    [Fact]
    public async Task DuplicateAndOverLimitCollections_AreRejectedFailClosed()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        layout.EnsureCreated();
        var path = Path.Combine(layout.Operations, ProductDesiredRunIntentStore.FileName);
        var duplicate = Guid.NewGuid();
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                serverIds = new[] { duplicate, duplicate },
            }));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ProductDesiredRunIntentStore(layout).LoadAsync());

        var tooMany = Enumerable.Range(0, ProductDesiredRunIntentStore.MaximumEntries + 1)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(new { schemaVersion = 1, serverIds = tooMany }));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ProductDesiredRunIntentStore(layout).LoadAsync());
    }

    [Fact]
    public async Task OversizedIntent_IsRejectedBeforeJsonParsing()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        layout.EnsureCreated();
        var path = Path.Combine(layout.Operations, ProductDesiredRunIntentStore.FileName);
        await File.WriteAllBytesAsync(
            path,
            Enumerable.Repeat((byte)' ', ProductDesiredRunIntentStore.MaximumFileBytes + 1).ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ProductDesiredRunIntentStore(layout).LoadAsync());
    }

    [Fact]
    public async Task ReparsePointOperationsDirectory_IsRejected()
    {
        var parent = Path.Combine(Path.GetTempPath(), "muhun-desired-link-test", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "product");
        var outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var operations = Path.Combine(root, "operations");
        CreateDirectoryJunction(operations, outside);
        try
        {
            var store = new ProductDesiredRunIntentStore(new ProductDataLayout(root));

            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        }
        finally
        {
            if (Directory.Exists(operations))
            {
                Directory.Delete(operations);
            }

            var cleanupRoot = Path.GetFullPath(
                    Path.Combine(Path.GetTempPath(), "muhun-desired-link-test"))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupTarget = Path.GetFullPath(parent);
            if (!cleanupTarget.StartsWith(cleanupRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Test cleanup target escaped its dedicated root.");
            }

            Directory.Delete(cleanupTarget, recursive: true);
        }
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", linkPath, targetPath },
        }) ?? throw new InvalidOperationException("Could not create test junction.");
        process.WaitForExit();
        if (process.ExitCode != 0 ||
            !File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Could not create test reparse point.");
        }
    }
}
