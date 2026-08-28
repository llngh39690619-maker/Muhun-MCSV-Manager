using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class MinecraftClientInstanceSettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-client-settings-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UpdateAsync_UpdatesOnlyEditableSettingsAndPersistsAtomically()
    {
        var oldJava = CreateFile(Path.Combine(_root, "java-17", "bin", "java.exe"));
        var newJava = CreateFile(Path.Combine(_root, "java-21", "bin", "java.exe"));
        var icon = CreateFile(Path.Combine(_root, "icons", "custom.png"), [1, 2, 3]);
        var original = CreateInstance("original", oldJava);
        using var registry = await CreateRegistryAsync(original);
        var service = new MinecraftClientInstanceSettingsService(
            registry,
            new FixedJavaProbe(17));
        var suppliedArguments = new List<string>
        {
            " -XX:+UseG1GC ",
            "-Dexample.value=hello world",
        };
        var request = CreateSettings() with
        {
            Name = "  Updated client  ",
            IconImagePath = icon,
            WindowWidth = 2560,
            WindowHeight = 1440,
            FullScreen = true,
            EnableQuickLaunch = true,
            HideLauncherAfterGameStarts = false,
            ShowGameLog = true,
            EnableDedicatedGpu = true,
            EnableDiscordPresence = true,
            MemoryMode = MinecraftClientMemoryMode.Manual,
            MinimumMemoryMb = 4096,
            MaximumMemoryMb = 8192,
            JavaExecutablePath = newJava,
            JvmArguments = suppliedArguments,
        };

        var result = await service.UpdateAsync(original.Id, request);
        suppliedArguments.Add("-Dmust.not.persist=true");
        result.JvmArguments.Add("-Dreturned.copy=true");
        result.EnvironmentVariables["MUTATED"] = "true";

        var stored = Assert.Single((await registry.LoadAsync()).Instances);
        AssertInstallationIdentityPreserved(original, stored);
        Assert.Equal("Updated client", stored.Name);
        Assert.NotNull(stored.IconImagePath);
        Assert.StartsWith(
            Path.Combine(Path.GetFullPath(original.DirectoryPath), ".x-mcsv", "assets")
            + Path.DirectorySeparatorChar,
            Path.GetFullPath(stored.IconImagePath),
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("custom-icon-", Path.GetFileName(stored.IconImagePath), StringComparison.OrdinalIgnoreCase);
        File.Delete(icon);
        Assert.True(File.Exists(stored.IconImagePath));
        Assert.Equal(2560, stored.WindowWidth);
        Assert.Equal(1440, stored.WindowHeight);
        Assert.True(stored.FullScreen);
        Assert.True(stored.EnableQuickLaunch);
        Assert.False(stored.HideLauncherAfterGameStarts);
        Assert.True(stored.ShowGameLog);
        Assert.Equal(MinecraftClientMemoryMode.Manual, stored.MemoryMode);
        Assert.Equal(4096, stored.MinimumMemoryMb);
        Assert.Equal(8192, stored.MaximumMemoryMb);
        Assert.Equal(Path.GetFullPath(newJava), stored.JavaExecutablePath);
        Assert.Equal(17, stored.JavaMajorVersion);
        Assert.Equal(["-XX:+UseG1GC", "-Dexample.value=hello world"], stored.JvmArguments);
        Assert.DoesNotContain(stored.JvmArguments, value => value.Contains("persist", StringComparison.Ordinal));
        Assert.DoesNotContain("MUTATED", stored.EnvironmentVariables.Keys);
        Assert.True(stored.EnableDedicatedGpu);
        Assert.True(stored.EnableDiscordPresence);
        Assert.Equal("kept", stored.EnvironmentVariables["PRESERVED"]);
    }

    [Fact]
    public async Task UpdateAsync_RejectsAnInstanceOutsideTheConfiguredOwnedRootBeforeCopyingIcon()
    {
        var ownedRoot = Path.Combine(_root, "owned-instances");
        Directory.CreateDirectory(ownedRoot);
        var icon = CreateFile(Path.Combine(_root, "icons", "outside-root.png"), [1, 2, 3]);
        var instance = CreateInstance("outside", javaExecutablePath: null);
        using var registry = await CreateRegistryAsync(instance);
        var service = new MinecraftClientInstanceSettingsService(
            registry,
            new FixedJavaProbe(17),
            ownedRoot);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateAsync(
            instance.Id,
            CreateSettings() with { IconImagePath = icon }));

        Assert.Empty(Directory.EnumerateFiles(instance.DirectoryPath, "custom-icon-*", SearchOption.AllDirectories));
        Assert.Null(Assert.Single((await registry.LoadAsync()).Instances).IconImagePath);
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsOnlyEditableValuesAndACollectionCopy()
    {
        var java = CreateFile(Path.Combine(_root, "runtime", "bin", "java.exe"));
        var instance = CreateInstance("read", java);
        instance.EnableQuickLaunch = true;
        instance.JvmArguments = ["-XX:+UseZGC"];
        using var registry = await CreateRegistryAsync(instance);
        var service = new MinecraftClientInstanceSettingsService(registry);

        var settings = await service.GetSettingsAsync(instance.Id);

        Assert.Equal(instance.Name, settings.Name);
        Assert.True(settings.EnableQuickLaunch);
        Assert.Equal(instance.EnableDedicatedGpu, settings.EnableDedicatedGpu);
        Assert.Equal(instance.EnableDiscordPresence, settings.EnableDiscordPresence);
        Assert.Equal(instance.JavaExecutablePath, settings.JavaExecutablePath);
        Assert.Equal(["-XX:+UseZGC"], settings.JvmArguments);
        Assert.Null(typeof(MinecraftClientInstanceSettingsUpdate).GetProperty("Id"));
        Assert.Null(typeof(MinecraftClientInstanceSettingsUpdate).GetProperty("DirectoryPath"));
        Assert.Null(typeof(MinecraftClientInstanceSettingsUpdate).GetProperty("GameVersion"));
        Assert.NotSame(instance.JvmArguments, settings.JvmArguments);
    }

    [Fact]
    public async Task ValidateJavaExecutableAsync_ProbesACompatibleSelectionWithoutPersistingIt()
    {
        var java = CreateFile(Path.Combine(_root, "java-99-misleading", "bin", "java.exe"));
        var instance = CreateInstance("validate Java", javaExecutablePath: null);
        using var registry = await CreateRegistryAsync(instance);
        var probe = new RecordingJavaProbe(17);
        var service = new MinecraftClientInstanceSettingsService(registry, probe);

        var detectedMajor = await service.ValidateJavaExecutableAsync(instance.Id, java);

        Assert.Equal(17, detectedMajor);
        Assert.Equal(Path.GetFullPath(java), probe.LastPath);
        var stored = Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Null(stored.JavaExecutablePath);
        Assert.Null(stored.JavaMajorVersion);
    }

    [Theory]
    [InlineData(MinecraftClientMemoryMode.UseGlobalDefault)]
    [InlineData(MinecraftClientMemoryMode.Automatic)]
    [InlineData(MinecraftClientMemoryMode.Manual)]
    public async Task UpdateAsync_AcceptsEverySupportedMemoryPolicy(MinecraftClientMemoryMode mode)
    {
        var instance = CreateInstance(mode.ToString(), javaExecutablePath: null);
        using var registry = await CreateRegistryAsync(instance);
        var service = new MinecraftClientInstanceSettingsService(registry);

        var result = await service.UpdateAsync(
            instance.Id,
            CreateSettings() with { MemoryMode = mode });

        Assert.Equal(mode, result.MemoryMode);
        Assert.Equal(mode, Assert.Single((await registry.LoadAsync()).Instances).MemoryMode);
    }

    [Fact]
    public async Task UpdateAsync_SameJavaPathIsReprobedAndPersistsDetectedMajorVersion()
    {
        var java = CreateFile(Path.Combine(_root, "runtime", "bin", "java.exe"));
        var instance = CreateInstance("same Java", java);
        using var registry = await CreateRegistryAsync(instance);
        var service = new MinecraftClientInstanceSettingsService(
            registry,
            new FixedJavaProbe(17));

        var result = await service.UpdateAsync(
            instance.Id,
            CreateSettings() with { JavaExecutablePath = java.ToUpperInvariant() });

        Assert.Equal(17, result.JavaMajorVersion);
    }

    [Fact]
    public async Task UpdateAsync_RejectsAJavaMajorThatDoesNotMatchTheMinecraftVersion()
    {
        var java = CreateFile(Path.Combine(_root, "misleading-java-17-folder", "bin", "java.exe"));
        var instance = CreateInstance("wrong Java", javaExecutablePath: null);
        using var registry = await CreateRegistryAsync(instance);
        var service = new MinecraftClientInstanceSettingsService(
            registry,
            new FixedJavaProbe(21));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.UpdateAsync(
            instance.Id,
            CreateSettings() with { JavaExecutablePath = java }));

        Assert.Contains("Minecraft 1.20.1 requires Java 17", error.Message, StringComparison.Ordinal);
        Assert.Contains("reports Java 21", error.Message, StringComparison.Ordinal);
        var stored = Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Null(stored.JavaExecutablePath);
        Assert.Null(stored.JavaMajorVersion);
    }

    [Fact]
    public async Task UpdateAsync_ClearingJavaAndIconClearsOnlyThoseOverrides()
    {
        var java = CreateFile(Path.Combine(_root, "runtime", "bin", "java.exe"));
        var icon = CreateFile(Path.Combine(_root, "icons", "old.ico"), [1]);
        var instance = CreateInstance("clear", java);
        instance.IconImagePath = icon;
        using var registry = await CreateRegistryAsync(instance);
        var service = new MinecraftClientInstanceSettingsService(registry);

        var result = await service.UpdateAsync(
            instance.Id,
            CreateSettings() with
            {
                IconImagePath = "   ",
                JavaExecutablePath = null,
            });

        Assert.Null(result.IconImagePath);
        Assert.Null(result.JavaExecutablePath);
        Assert.Null(result.JavaMajorVersion);
        Assert.Equal(instance.CatalogIconImagePath, result.CatalogIconImagePath);
        Assert.Equal(instance.CatalogPreviewImagePath, result.CatalogPreviewImagePath);
    }

    [Fact]
    public async Task UpdateAsync_RejectsInvalidSettingsWithoutPersistingPartialChanges()
    {
        var instance = CreateInstance("unchanged", javaExecutablePath: null);
        using var registry = await CreateRegistryAsync(instance);
        var service = new MinecraftClientInstanceSettingsService(registry);
        var wrongExecutable = CreateFile(Path.Combine(_root, "runtime", "bin", "not-java.exe"));
        var textIcon = CreateFile(Path.Combine(_root, "icons", "icon.txt"), [1]);
        var oversizedArgument = "-D" + new string('a', 2_047);
        var invalidCases = new MinecraftClientInstanceSettingsUpdate[]
        {
            CreateSettings() with { Name = " \r\n " },
            CreateSettings() with { Name = new string('n', 129) },
            CreateSettings() with { WindowWidth = 639 },
            CreateSettings() with { WindowHeight = 16_385 },
            CreateSettings() with { MemoryMode = (MinecraftClientMemoryMode)999 },
            CreateSettings() with { MinimumMemoryMb = 511 },
            CreateSettings() with { MinimumMemoryMb = 8192, MaximumMemoryMb = 4096 },
            CreateSettings() with { JvmArguments = null! },
            CreateSettings() with { JvmArguments = ["not-a-jvm-option"] },
            CreateSettings() with { JvmArguments = ["-Dline=one\ntwo"] },
            CreateSettings() with { JvmArguments = ["-Xmx16G"] },
            CreateSettings() with { JvmArguments = ["-XX:MaxRAMPercentage=95"] },
            CreateSettings() with
            {
                JvmArguments = Enumerable.Repeat(oversizedArgument, 33).ToArray(),
            },
            CreateSettings() with { JavaExecutablePath = "java.exe" },
            CreateSettings() with { JavaExecutablePath = wrongExecutable },
            CreateSettings() with
            {
                JavaExecutablePath = Path.Combine(_root, "missing", "java.exe"),
            },
            CreateSettings() with { IconImagePath = textIcon },
            CreateSettings() with { IconImagePath = Path.Combine(_root, "missing.png") },
        };

        foreach (var invalid in invalidCases)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => service.UpdateAsync(instance.Id, invalid));
            var stored = Assert.Single((await registry.LoadAsync()).Instances);
            Assert.Equal("unchanged", stored.Name);
            Assert.False(stored.EnableQuickLaunch);
        }
    }

    [Fact]
    public async Task UpdateAsync_RejectsAnOversizedIcon()
    {
        var icon = CreateFile(
            Path.Combine(_root, "icons", "oversized.png"),
            size: MinecraftClientInstanceSettingsService.MaximumIconFileBytes + 1L);
        var instance = CreateInstance("icon", javaExecutablePath: null);
        using var registry = await CreateRegistryAsync(instance);
        var service = new MinecraftClientInstanceSettingsService(registry);

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(
            instance.Id,
            CreateSettings() with { IconImagePath = icon }));
    }

    [Fact]
    public async Task UpdateAsync_MissingInstanceDoesNotCreateANewRecord()
    {
        var instance = CreateInstance("existing", javaExecutablePath: null);
        using var registry = await CreateRegistryAsync(instance);
        var service = new MinecraftClientInstanceSettingsService(registry);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateAsync(Guid.NewGuid(), CreateSettings()));

        var stored = Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Equal(instance.Id, stored.Id);
    }

    [Fact]
    public async Task UpdateAsync_RejectsChangesWhileAProcessIdentityIsActive()
    {
        var java = CreateFile(Path.Combine(_root, "runtime", "bin", "java.exe"));
        var instance = CreateInstance("running", java);
        MinecraftClientProcessRecoveryService.RecordIdentity(
            instance,
            new MinecraftClientProcessIdentity(
                12_345,
                new DateTimeOffset(2026, 8, 28, 3, 4, 5, TimeSpan.Zero),
                java));
        using var registry = await CreateRegistryAsync(instance);
        var service = new MinecraftClientInstanceSettingsService(registry);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(
            instance.Id,
            CreateSettings() with { Name = "must not change" }));

        Assert.Equal("running", Assert.Single((await registry.LoadAsync()).Instances).Name);
    }

    [Fact]
    public async Task UpdateAsync_CancellationBeforeValidationDoesNotPersist()
    {
        var instance = CreateInstance("not cancelled", javaExecutablePath: null);
        using var registry = await CreateRegistryAsync(instance);
        var service = new MinecraftClientInstanceSettingsService(registry);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UpdateAsync(
            instance.Id,
            CreateSettings() with { Name = "cancelled" },
            cancellation.Token));

        Assert.Equal("not cancelled", Assert.Single((await registry.LoadAsync()).Instances).Name);
    }

    [Fact]
    public async Task UpdateAsync_ConcurrentServiceInstancesDoNotLoseIndependentUpdates()
    {
        var first = CreateInstance("first", javaExecutablePath: null);
        var second = CreateInstance("second", javaExecutablePath: null);
        using var registry = await CreateRegistryAsync(first, second);
        var firstService = new MinecraftClientInstanceSettingsService(registry);
        var secondService = new MinecraftClientInstanceSettingsService(registry);

        await Task.WhenAll(
            firstService.UpdateAsync(
                first.Id,
                CreateSettings() with { Name = "first updated", EnableQuickLaunch = true }),
            secondService.UpdateAsync(
                second.Id,
                CreateSettings() with { Name = "second updated", ShowGameLog = true }));

        var stored = (await registry.LoadAsync()).Instances.ToDictionary(item => item.Id);
        Assert.Equal("first updated", stored[first.Id].Name);
        Assert.True(stored[first.Id].EnableQuickLaunch);
        Assert.Equal("second updated", stored[second.Id].Name);
        Assert.True(stored[second.Id].ShowGameLog);
    }

    [Fact]
    public async Task RegistryUpdateAsync_CallbackFailureLeavesTheAtomicFileUnchanged()
    {
        var instance = CreateInstance("before", javaExecutablePath: null);
        using var registry = await CreateRegistryAsync(instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.UpdateAsync<int>(document =>
        {
            Assert.Single(document.Instances).Name = "must not persist";
            throw new InvalidOperationException("simulated update failure");
        }));

        Assert.Equal("before", Assert.Single((await registry.LoadAsync()).Instances).Name);
    }

    private async Task<MinecraftClientRegistry> CreateRegistryAsync(
        params MinecraftClientInstance[] instances)
    {
        var registry = new MinecraftClientRegistry(Path.Combine(_root, "registry.json"));
        try
        {
            await registry.SaveAsync(new MinecraftClientRegistryDocument
            {
                Instances = [.. instances],
            });
            return registry;
        }
        catch
        {
            registry.Dispose();
            throw;
        }
    }

    private MinecraftClientInstance CreateInstance(string name, string? javaExecutablePath)
    {
        var directory = Path.Combine(_root, "instances", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new MinecraftClientInstance
        {
            Id = Guid.NewGuid(),
            Name = name,
            Edition = MinecraftClientEdition.Java,
            DirectoryPath = directory,
            GameVersion = "1.20.1",
            InstalledVersionId = "fabric-loader-0.16.14-1.20.1",
            Loader = MinecraftClientLoader.Fabric,
            LoaderVersion = "0.16.14",
            LoaderInstallKind = MinecraftClientLoaderInstallKind.Managed,
            JavaMajorVersion = javaExecutablePath is null ? null : 17,
            JavaExecutablePath = javaExecutablePath,
            MemoryMode = MinecraftClientMemoryMode.Automatic,
            MinimumMemoryMb = 2048,
            MaximumMemoryMb = 4096,
            WindowWidth = 1280,
            WindowHeight = 720,
            EnableQuickLaunch = false,
            HideLauncherAfterGameStarts = true,
            ShowGameLog = false,
            EnableDedicatedGpu = false,
            EnableDiscordPresence = false,
            JvmArguments = ["-Doriginal=true"],
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PRESERVED"] = "kept",
            },
            AccountId = "account-reference",
            BackgroundImagePath = Path.Combine(_root, "backgrounds", "preserved.png"),
            BackgroundImageOpacity = 0.42,
            IconImagePath = null,
            CatalogIconImagePath = Path.Combine(_root, "catalog", "icon.png"),
            CatalogPreviewImagePath = Path.Combine(_root, "catalog", "preview.png"),
            LastPlayedAtUtc = new DateTimeOffset(2026, 8, 27, 1, 2, 3, TimeSpan.Zero),
            TotalPlayTimeSeconds = 12_345,
            CreatedAtUtc = new DateTimeOffset(2026, 8, 1, 4, 5, 6, TimeSpan.Zero),
        };
    }

    private static MinecraftClientInstanceSettingsUpdate CreateSettings() => new()
    {
        Name = "settings",
        WindowWidth = 1920,
        WindowHeight = 1080,
        FullScreen = false,
        EnableQuickLaunch = false,
        HideLauncherAfterGameStarts = true,
        ShowGameLog = false,
        EnableDedicatedGpu = false,
        EnableDiscordPresence = false,
        MemoryMode = MinecraftClientMemoryMode.Automatic,
        MinimumMemoryMb = 2048,
        MaximumMemoryMb = 4096,
        JavaExecutablePath = null,
        JvmArguments = [],
    };

    private static void AssertInstallationIdentityPreserved(
        MinecraftClientInstance expected,
        MinecraftClientInstance actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Edition, actual.Edition);
        Assert.Equal(expected.DirectoryPath, actual.DirectoryPath);
        Assert.Equal(expected.GameVersion, actual.GameVersion);
        Assert.Equal(expected.InstalledVersionId, actual.InstalledVersionId);
        Assert.Equal(expected.Loader, actual.Loader);
        Assert.Equal(expected.LoaderVersion, actual.LoaderVersion);
        Assert.Equal(expected.LoaderInstallKind, actual.LoaderInstallKind);
        Assert.Equal(expected.AccountId, actual.AccountId);
        Assert.Equal(expected.BackgroundImagePath, actual.BackgroundImagePath);
        Assert.Equal(expected.BackgroundImageOpacity, actual.BackgroundImageOpacity);
        Assert.Equal(expected.CatalogIconImagePath, actual.CatalogIconImagePath);
        Assert.Equal(expected.CatalogPreviewImagePath, actual.CatalogPreviewImagePath);
        Assert.Equal(expected.LastPlayedAtUtc, actual.LastPlayedAtUtc);
        Assert.Equal(expected.TotalPlayTimeSeconds, actual.TotalPlayTimeSeconds);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
    }

    private sealed class FixedJavaProbe(int majorVersion) : IMinecraftClientJavaExecutableProbe
    {
        public Task<int> ProbeMajorVersionAsync(
            string javaExecutablePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(majorVersion);
        }
    }

    private sealed class RecordingJavaProbe(int majorVersion) : IMinecraftClientJavaExecutableProbe
    {
        public string? LastPath { get; private set; }

        public Task<int> ProbeMajorVersionAsync(
            string javaExecutablePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPath = javaExecutablePath;
            return Task.FromResult(majorVersion);
        }
    }

    private static string CreateFile(string path, byte[]? content = null, long? size = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (size is not null)
        {
            stream.SetLength(size.Value);
        }
        else if (content is not null)
        {
            stream.Write(content);
        }

        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
