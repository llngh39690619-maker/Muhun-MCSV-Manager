using System.Collections.Concurrent;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class ModrinthModpackInstallerTests
{
    [Fact]
    public async Task InstallVerifiesOuterPackUsesBoundedParallelDownloadsAndAppliesServerOverridesLast()
    {
        using var temp = new TemporaryDirectory();
        var contents = Enumerable.Range(0, 18).ToDictionary(
            index => $"mods/mod-{index}.jar",
            index => System.Text.Encoding.UTF8.GetBytes($"mod-{index}"),
            StringComparer.Ordinal);
        contents["config/value.txt"] = "downloaded"u8.ToArray();
        var optionalPath = "mods/optional.jar";
        var optionalBytes = "optional"u8.ToArray();
        var clientPath = "mods/client-only.jar";
        var clientBytes = "client"u8.ToArray();

        var fileJson = contents.Select(pair => ModrinthModpackTestFixtures.FileJson(pair.Key, pair.Value)).ToList();
        fileJson.Add(ModrinthModpackTestFixtures.FileJson(optionalPath, optionalBytes, "optional"));
        fileJson.Add(ModrinthModpackTestFixtures.FileJson(clientPath, clientBytes, "unsupported"));
        var manifest = ModrinthModpackTestFixtures.Manifest(
            string.Join(',', fileJson), "\"minecraft\":\"1.20.1\",\"forge\":\"47.2.0\"");
        var packBytes = ModrinthModpackTestFixtures.CreateMrpack(
            manifest,
            ("overrides/config/value.txt", "base"u8.ToArray(), null),
            ("server-overrides/config/value.txt", "server"u8.ToArray(), null),
            ("client-overrides/leak.txt", "do-not-install"u8.ToArray(), null));
        var packHashes = ModrinthModpackTestFixtures.Hashes(packBytes);
        var payloads = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        payloads["https://files.test/pack"] = packBytes;
        foreach (var pair in contents)
        {
            payloads["https://files.test/" + Uri.EscapeDataString(pair.Key)] = pair.Value;
        }
        payloads["https://files.test/" + Uri.EscapeDataString(optionalPath)] = optionalBytes;
        payloads["https://files.test/" + Uri.EscapeDataString(clientPath)] = clientBytes;

        var active = 0;
        var maxActive = 0;
        var transport = new FixtureTransport(async (uri, token) =>
        {
            var now = Interlocked.Increment(ref active);
            UpdateMaximum(ref maxActive, now);
            try
            {
                if (uri.AbsolutePath != "/pack") await Task.Delay(30, token);
                return FixtureTransport.Bytes(payloads[uri.AbsoluteUri]);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var installer = new ModrinthModpackInstaller(
            new ModrinthModpackArtifactDownloader(transport, new TestUriPolicy()));
        var version = new ModrinthModpackVersion(
            "project", "api-version", "Version", "1", "release", "listed", "server_only",
            new[] { "1.20.1" }, new[] { "forge" }, DateTimeOffset.UtcNow,
            new ModrinthMrpackFile("fixture.mrpack", new Uri("https://files.test/pack"),
                packBytes.Length, packHashes.Sha512, packHashes.Sha1, true));
        var staging = Path.Combine(temp.Path, "staging");
        Directory.CreateDirectory(staging);
        var progress = new InlineProgress<ModrinthModpackInstallProgress>();

        var result = await installer.InstallAsync(version, staging, progress: progress);

        Assert.Equal("server", await File.ReadAllTextAsync(Path.Combine(staging, "config", "value.txt")));
        Assert.False(File.Exists(Path.Combine(staging, "mods", "optional.jar")));
        Assert.False(File.Exists(Path.Combine(staging, "mods", "client-only.jar")));
        Assert.False(File.Exists(Path.Combine(staging, "leak.txt")));
        Assert.Equal(ModrinthModpackLoaderKind.Forge, result.LoaderInstallRequest.Kind);
        Assert.Equal("47.2.0", result.LoaderInstallRequest.LoaderVersion);
        Assert.Equal(1, result.SkippedOptionalFiles);
        Assert.Equal(1, result.SkippedUnsupportedFiles);
        Assert.Equal(4, maxActive);
        var fileProgress = progress.Values
            .Where(value => value.Phase == "download-files")
            .ToArray();
        Assert.NotEmpty(fileProgress);
        Assert.All(fileProgress, value => Assert.Equal(4, value.EffectiveConcurrentDownloads));
        Assert.All(fileProgress, value => Assert.True(value.UsesAdaptiveConcurrency));
        Assert.Empty(Directory.GetFiles(temp.Path, ".muhun-modrinth-*.mrpack"));
    }

    [Fact]
    public async Task FirstDownloadFailureCancelsBatchAndCleansCreatedStagingContent()
    {
        using var temp = new TemporaryDirectory();
        var contents = Enumerable.Range(0, 30)
            .Select(index => ($"mods/mod-{index}.jar", System.Text.Encoding.UTF8.GetBytes($"mod-{index}")))
            .ToArray();
        var manifest = ModrinthModpackTestFixtures.Manifest(string.Join(',', contents.Select(pair =>
            ModrinthModpackTestFixtures.FileJson(pair.Item1, pair.Item2))));
        var packPath = Path.Combine(temp.Path, "fixture.mrpack");
        ModrinthModpackTestFixtures.WriteFile(
            packPath,
            ModrinthModpackTestFixtures.CreateMrpack(manifest));
        var requested = 0;
        var transport = new FixtureTransport(async (uri, token) =>
        {
            Interlocked.Increment(ref requested);
            if (uri.AbsolutePath.EndsWith("mod-0.jar", StringComparison.Ordinal))
            {
                throw new IOException("fixture first failure");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), token);
            var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            var payload = contents.Single(pair => pair.Item1 == path).Item2;
            return FixtureTransport.Bytes(payload);
        });
        var installer = new ModrinthModpackInstaller(new ModrinthModpackArtifactDownloader(
            transport,
            new TestUriPolicy()));
        var staging = Path.Combine(temp.Path, "staging");
        Directory.CreateDirectory(staging);

        var error = await Assert.ThrowsAsync<IOException>(() => installer.InstallDownloadedAsync(
            "p", "v", packPath, staging));

        Assert.Contains("fixture first failure", error.ToString(), StringComparison.Ordinal);
        Assert.InRange(requested, 1, 4);
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    [Theory]
    [InlineData(0, 0, 16, true, 1)]
    [InlineData(4, 32L * 1024 * 1024, 16, true, 1)]
    [InlineData(12, 128L * 1024 * 1024, 16, true, 2)]
    [InlineData(32, 512L * 1024 * 1024, 16, true, 4)]
    [InlineData(64, 1024L * 1024 * 1024, 16, true, 8)]
    [InlineData(128, 2048L * 1024 * 1024, 16, true, 12)]
    [InlineData(129, 2048L * 1024 * 1024, 16, true, 16)]
    [InlineData(200, 3L * 1024 * 1024 * 1024, 16, true, 16)]
    [InlineData(200, 3L * 1024 * 1024 * 1024, 6, true, 6)]
    [InlineData(3, 3L * 1024 * 1024 * 1024, 16, true, 3)]
    [InlineData(30, 1, 5, false, 5)]
    public void AdaptivePlanner_UsesVerifiedWorkloadAndHonorsBounds(
        int fileCount,
        long totalBytes,
        int hardCap,
        bool adaptive,
        int expected)
    {
        Assert.Equal(
            expected,
            ModrinthDownloadConcurrencyPlanner.Plan(fileCount, totalBytes, hardCap, adaptive));
    }

    [Fact]
    public async Task CallerCancellationCancelsAdaptiveBatchAndCleansCreatedStagingContent()
    {
        using var temp = new TemporaryDirectory();
        var contents = Enumerable.Range(0, 40)
            .Select(index => ($"mods/mod-{index}.jar", System.Text.Encoding.UTF8.GetBytes($"mod-{index}")))
            .ToArray();
        var manifest = ModrinthModpackTestFixtures.Manifest(string.Join(',', contents.Select(pair =>
            ModrinthModpackTestFixtures.FileJson(pair.Item1, pair.Item2))));
        var packPath = Path.Combine(temp.Path, "fixture.mrpack");
        ModrinthModpackTestFixtures.WriteFile(
            packPath,
            ModrinthModpackTestFixtures.CreateMrpack(manifest));
        var requested = 0;
        var firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FixtureTransport(async (_, token) =>
        {
            Interlocked.Increment(ref requested);
            firstRequest.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        var installer = new ModrinthModpackInstaller(new ModrinthModpackArtifactDownloader(
            transport,
            new TestUriPolicy()));
        var staging = Path.Combine(temp.Path, "staging");
        Directory.CreateDirectory(staging);
        using var cancellation = new CancellationTokenSource();

        var install = installer.InstallDownloadedAsync(
            "p", "v", packPath, staging, cancellationToken: cancellation.Token);
        await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        Assert.InRange(requested, 1, 8);
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task InstallerRejectsUnboundedDownloadConcurrency(int concurrency)
    {
        using var temp = new TemporaryDirectory();
        var packPath = Path.Combine(temp.Path, "fixture.mrpack");
        ModrinthModpackTestFixtures.WriteFile(
            packPath,
            ModrinthModpackTestFixtures.CreateMrpack(ModrinthModpackTestFixtures.Manifest(string.Empty)));
        var staging = Path.Combine(temp.Path, "staging");
        Directory.CreateDirectory(staging);
        var installer = new ModrinthModpackInstaller(new ModrinthModpackArtifactDownloader(
            new FixtureTransport((_, _) => throw new InvalidOperationException("must not download")),
            new TestUriPolicy()));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => installer.InstallDownloadedAsync(
            "p",
            "v",
            packPath,
            staging,
            new ModrinthModpackInstallOptions(MaxConcurrentDownloads: concurrency)));
    }

    [Fact]
    public async Task InstallOptionalFlagIncludesOptionalButNeverUnsupported()
    {
        using var temp = new TemporaryDirectory();
        var optional = "optional"u8.ToArray();
        var unsupported = "unsupported"u8.ToArray();
        var manifest = ModrinthModpackTestFixtures.Manifest(string.Join(',',
            ModrinthModpackTestFixtures.FileJson("mods/optional.jar", optional, "optional"),
            ModrinthModpackTestFixtures.FileJson("mods/client.jar", unsupported, "unsupported")));
        var packPath = Path.Combine(temp.Path, "fixture.mrpack");
        ModrinthModpackTestFixtures.WriteFile(packPath, ModrinthModpackTestFixtures.CreateMrpack(manifest));
        var payloads = new Dictionary<string, byte[]>
        {
            ["https://files.test/" + Uri.EscapeDataString("mods/optional.jar")] = optional,
            ["https://files.test/" + Uri.EscapeDataString("mods/client.jar")] = unsupported
        };
        var installer = new ModrinthModpackInstaller(new ModrinthModpackArtifactDownloader(
            new FixtureTransport((uri, _) => Task.FromResult(FixtureTransport.Bytes(payloads[uri.AbsoluteUri]))),
            new TestUriPolicy()));
        var staging = Path.Combine(temp.Path, "staging");
        Directory.CreateDirectory(staging);

        var result = await installer.InstallDownloadedAsync(
            "p", "v", packPath, staging, new ModrinthModpackInstallOptions(IncludeOptionalFiles: true));

        Assert.True(File.Exists(Path.Combine(staging, "mods", "optional.jar")));
        Assert.False(File.Exists(Path.Combine(staging, "mods", "client.jar")));
        Assert.Equal(0, result.SkippedOptionalFiles);
        Assert.Equal(1, result.SkippedUnsupportedFiles);
    }

    [Fact]
    public async Task InstallerRefusesNonEmptyCallerStaging()
    {
        using var temp = new TemporaryDirectory();
        var staging = Path.Combine(temp.Path, "staging");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "keep.txt"), "owned");
        var downloader = new ModrinthModpackArtifactDownloader(
            new FixtureTransport((_, _) => throw new InvalidOperationException("must not download")), new TestUriPolicy());
        var installer = new ModrinthModpackInstaller(downloader);
        var version = new ModrinthModpackVersion(
            "p", "v", "n", "1", "release", "listed", "server_only", [], [], DateTimeOffset.UtcNow,
            new ModrinthMrpackFile("p.mrpack", new Uri("https://files.test/pack"), 0, new string('0', 128), null, true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(version, staging));
        Assert.Equal("owned", await File.ReadAllTextAsync(Path.Combine(staging, "keep.txt")));
    }

    [Fact]
    public void InstallerRefusesReparsePointStagingRoot()
    {
        using var temp = new TemporaryDirectory();
        var target = Path.Combine(temp.Path, "real-target");
        var stagingLink = Path.Combine(temp.Path, "linked-staging");
        Directory.CreateDirectory(target);
        ReparsePointTestHelper.CreateDirectoryLink(stagingLink, target);
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                SafeModpackArchive.EnsureSafeStagingDirectory(stagingLink, requireEmpty: true));
        }
        finally
        {
            Directory.Delete(stagingLink);
        }
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly ConcurrentQueue<T> _values = new();

        public IReadOnlyList<T> Values => _values.ToArray();

        public void Report(T value) => _values.Enqueue(value);
    }
}
