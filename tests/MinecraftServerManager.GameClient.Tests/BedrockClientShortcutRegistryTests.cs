using MinecraftServerManager.GameClient;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class BedrockClientShortcutRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-bedrock-shortcut-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ShortcutContract_HasOnlySafeMetadataAndClosedChannels()
    {
        Assert.Equal(
            new[] { "Channel", "CreatedAtUtc", "DisplayName", "Id" },
            typeof(BedrockClientShortcut)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(
            new[] { MinecraftBedrockChannel.Stable, MinecraftBedrockChannel.Preview },
            Enum.GetValues<MinecraftBedrockChannel>());
    }

    [Fact]
    public async Task AddAndLoad_RoundTripSafeFieldsAndTrimDisplayName()
    {
        var path = Path.Combine(_root, "bedrock-shortcuts.v1.json");
        var createdAt = new DateTimeOffset(2026, 8, 30, 10, 11, 12, TimeSpan.FromHours(8));
        var input = new BedrockClientShortcut
        {
            DisplayName = "  Preview on this PC  ",
            Channel = MinecraftBedrockChannel.Preview,
            CreatedAtUtc = createdAt,
        };

        BedrockClientShortcut added;
        using (var registry = new BedrockClientShortcutRegistry(path))
        {
            added = await registry.AddAsync(input);
        }

        using var reopened = new BedrockClientShortcutRegistry(path);
        var document = await reopened.LoadAsync();
        var loaded = Assert.Single(document.Shortcuts);
        Assert.NotSame(input, added);
        Assert.Equal(input.Id, loaded.Id);
        Assert.Equal("Preview on this PC", added.DisplayName);
        Assert.Equal("Preview on this PC", loaded.DisplayName);
        Assert.Equal(MinecraftBedrockChannel.Preview, loaded.Channel);
        Assert.Equal(createdAt.ToUniversalTime(), loaded.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAtUtc.Offset);
        Assert.Equal(BedrockClientShortcutRegistryDocument.CurrentSchemaVersion, document.SchemaVersion);

        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("directoryPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gameVersion", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    public async Task Add_RejectsBlankOrControlCharacterDisplayName(string displayName)
    {
        using var registry = CreateRegistry("invalid-name.json");

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.AddAsync(new()
        {
            DisplayName = displayName,
        }));
    }

    [Fact]
    public async Task Add_RejectsDisplayNameLongerThan128CharactersAfterTrim()
    {
        using var registry = CreateRegistry("long-name.json");

        var accepted = await registry.AddAsync(new BedrockClientShortcut
        {
            DisplayName = $"  {new string('a', 128)}  ",
        });
        Assert.Equal(128, accepted.DisplayName.Length);

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.AddAsync(new()
        {
            DisplayName = new string('a', 129),
        }));
    }

    [Fact]
    public async Task Save_RejectsUnknownChannelAndDuplicateIds()
    {
        using var invalidChannelRegistry = CreateRegistry("invalid-channel.json");
        await Assert.ThrowsAsync<InvalidDataException>(() => invalidChannelRegistry.SaveAsync(new()
        {
            Shortcuts =
            [
                new BedrockClientShortcut
                {
                    DisplayName = "Unknown",
                    Channel = (MinecraftBedrockChannel)int.MaxValue,
                },
            ],
        }));

        var duplicateId = Guid.NewGuid();
        using var duplicateRegistry = CreateRegistry("duplicate-id.json");
        await Assert.ThrowsAsync<InvalidDataException>(() => duplicateRegistry.SaveAsync(new()
        {
            Shortcuts =
            [
                new BedrockClientShortcut { Id = duplicateId, DisplayName = "First" },
                new BedrockClientShortcut { Id = duplicateId, DisplayName = "Second" },
            ],
        }));
    }

    [Fact]
    public async Task Remove_RemovesOnlyRegistryEntryAndLeavesUnrelatedFilesUntouched()
    {
        var registryPath = Path.Combine(_root, "registry", "bedrock-shortcuts.json");
        var unrelatedDirectory = Path.Combine(_root, "minecraft-installation");
        var unrelatedFile = Path.Combine(unrelatedDirectory, "keep-me.dat");
        Directory.CreateDirectory(unrelatedDirectory);
        var sentinel = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89 };
        await File.WriteAllBytesAsync(unrelatedFile, sentinel);

        using var registry = new BedrockClientShortcutRegistry(registryPath);
        var first = await registry.AddAsync(new() { DisplayName = "Stable" });
        var second = await registry.AddAsync(new()
        {
            DisplayName = "Preview",
            Channel = MinecraftBedrockChannel.Preview,
        });

        var removed = await registry.RemoveAsync(first.Id);

        Assert.Equal(first.Id, removed.Id);
        Assert.Equal(second.Id, Assert.Single((await registry.LoadAsync()).Shortcuts).Id);
        Assert.True(Directory.Exists(unrelatedDirectory));
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(unrelatedFile));
        Assert.True(File.Exists(registryPath));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(registryPath)!,
            $".{Path.GetFileName(registryPath)}.*.tmp"));
    }

    [Fact]
    public async Task AddAndRemove_RejectDuplicateOrMissingIdsWithoutReplacingRegistry()
    {
        var path = Path.Combine(_root, "identity.json");
        using var registry = new BedrockClientShortcutRegistry(path);
        var shortcut = await registry.AddAsync(new() { DisplayName = "Stable" });

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.AddAsync(new()
        {
            Id = shortcut.Id,
            DisplayName = "Duplicate",
        }));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            registry.RemoveAsync(Guid.NewGuid()));

        var persisted = Assert.Single((await registry.LoadAsync()).Shortcuts);
        Assert.Equal(shortcut.Id, persisted.Id);
        Assert.Equal("Stable", persisted.DisplayName);
    }

    private BedrockClientShortcutRegistry CreateRegistry(string fileName) =>
        new(Path.Combine(_root, fileName));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
