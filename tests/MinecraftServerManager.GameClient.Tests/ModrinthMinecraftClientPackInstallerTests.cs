using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class ModrinthMinecraftClientPackInstallerTests : IDisposable
{
    private static readonly Uri PackUri = new(
        "https://cdn.modrinth.com/data/PackGood1/versions/StableV1/good.mrpack");
    private static readonly Uri ClientModUri = new(
        "https://cdn.modrinth.com/data/ClientMod1/versions/Version1/client.jar");
    private static readonly Uri OptionalModUri = new(
        "https://cdn.modrinth.com/data/Optional1/versions/Version1/optional.jar");
    private static readonly Uri ServerModUri = new(
        "https://cdn.modrinth.com/data/ServerMod1/versions/Version1/server.jar");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-modrinth-client-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _instances;
    private readonly string _staging;
    private readonly string _registryPath;

    public ModrinthMinecraftClientPackInstallerTests()
    {
        _instances = Path.Combine(_root, "instances");
        _staging = Path.Combine(_root, "staging");
        _registryPath = Path.Combine(_root, "registry.json");
        Directory.CreateDirectory(_instances);
        Directory.CreateDirectory(_staging);
    }

    [Fact]
    public async Task InstallAsync_CreatesRunnableClientAndAppliesOnlyClientLayers()
    {
        var fixture = CreatePackFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var artifactClient = CreateArtifactClient(fixture);
        var installer = CreateInstaller(registry, payload, fixture, artifactClient);
        var cachedIcon = Path.Combine(_root, "catalog-icon.png");
        await File.WriteAllBytesAsync(
            cachedIcon,
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        var cachedPreview = Path.Combine(_root, "catalog-preview.jpg");
        await File.WriteAllBytesAsync(cachedPreview, [0xff, 0xd8, 0xff, 0xd9]);
        var request = Request() with
        {
            JavaMajorVersion = 21,
            CatalogIconImagePath = cachedIcon,
            CatalogPreviewImagePath = cachedPreview,
        };

        var result = await installer.InstallAsync(request, javaExecutablePath: null);

        var finalRoot = Path.Combine(_instances, request.InstanceId.ToString("N"));
        Assert.Equal(finalRoot, result.Instance.DirectoryPath);
        Assert.True(File.Exists(Path.Combine(finalRoot, "versions", "fake-profile", "installed.txt")));
        Assert.Equal(fixture.ClientModBytes, await File.ReadAllBytesAsync(
            Path.Combine(finalRoot, "mods", "client.jar")));
        Assert.False(File.Exists(Path.Combine(finalRoot, "mods", "server.jar")));
        Assert.False(File.Exists(Path.Combine(finalRoot, "mods", "optional.jar")));
        Assert.Equal("client", await File.ReadAllTextAsync(
            Path.Combine(finalRoot, "config", "layer.txt")));
        Assert.False(File.Exists(Path.Combine(finalRoot, "config", "server-only.txt")));
        Assert.Equal(MinecraftClientLoader.Fabric, result.Instance.Loader);
        Assert.Equal("0.16.9", result.Instance.LoaderVersion);
        Assert.Equal("fake-profile", result.Instance.InstalledVersionId);
        Assert.Equal("modrinth", result.Instance.CatalogProvider);
        Assert.Equal("PackGood1", result.Instance.CatalogProjectId);
        Assert.Equal("StableV1", result.Instance.CatalogVersionId);
        Assert.Equal("cdn.modrinth.com", result.Instance.CatalogIconUri?.Host);
        Assert.Equal(21, result.Instance.JavaMajorVersion);
        Assert.Equal(
            Path.Combine(finalRoot, ".x-mcsv", "assets", "catalog-icon.png"),
            result.Instance.CatalogIconImagePath);
        Assert.Equal(
            Path.Combine(finalRoot, ".x-mcsv", "assets", "catalog-preview.jpg"),
            result.Instance.CatalogPreviewImagePath);
        File.Delete(cachedIcon);
        File.Delete(cachedPreview);
        Assert.True(File.Exists(result.Instance.CatalogIconImagePath));
        Assert.True(File.Exists(result.Instance.CatalogPreviewImagePath));
        Assert.Equal(1, result.InstalledContentFiles);
        Assert.Equal(1, result.SkippedUnsupportedFiles);
        Assert.Equal(1, result.SkippedOptionalFiles);
        Assert.Contains("mods/client.jar", result.InstalledPaths);
        Assert.Contains("config/layer.txt", result.InstalledPaths);
        Assert.Single(payload.Requests);
        Assert.Equal("1.21.1", payload.Requests[0].GameVersion);
        Assert.Equal("0.16.9", payload.Requests[0].LoaderVersion);
        var stored = Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Equal(result.Instance.Id, stored.Id);
        Assert.Equal(21, stored.JavaMajorVersion);
        Assert.Equal(result.Instance.CatalogIconImagePath, stored.CatalogIconImagePath);
        Assert.Equal(result.Instance.CatalogPreviewImagePath, stored.CatalogPreviewImagePath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
    }

    [Fact]
    public async Task InstallAsync_CanIncludeClientOptionalFilesExplicitly()
    {
        var fixture = CreatePackFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifactClient = CreateArtifactClient(fixture);
        var installer = CreateInstaller(registry, new FakePayloadInstaller(), fixture, artifactClient);
        var request = Request() with { IncludeOptionalFiles = true };

        var result = await installer.InstallAsync(request, javaExecutablePath: null);

        Assert.Equal(2, result.InstalledContentFiles);
        Assert.Equal(0, result.SkippedOptionalFiles);
        Assert.True(File.Exists(Path.Combine(result.Instance.DirectoryPath, "mods", "optional.jar")));
    }

    [Fact]
    public async Task InstallAsync_RejectsPackageHashMismatchAndRollsBackEverything()
    {
        var fixture = CreatePackFixture() with { PackSha512 = new string('0', 128) };
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifactClient = CreateArtifactClient(fixture);
        var installer = CreateInstaller(registry, new FakePayloadInstaller(), fixture, artifactClient);

        await Assert.ThrowsAsync<IOException>(
            () => installer.InstallAsync(Request(), javaExecutablePath: null));

        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_RejectsApiDeclaredOversizedPackageBeforeAnyDownload()
    {
        var fixture = CreatePackFixture();
        fixture = fixture with
        {
            Version = fixture.Version with
            {
                MrpackFile = fixture.Version.MrpackFile with
                {
                    Size = 8L * 1024 * 1024 * 1024 + 1,
                },
            },
        };
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifactClient = CreateArtifactClient(fixture, out var handler);
        var installer = CreateInstaller(registry, new FakePayloadInstaller(), fixture, artifactClient);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.Equal(0, handler.CallCount);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_RejectsRedirectsWithoutFollowingThem()
    {
        var fixture = CreatePackFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        var handler = new ArtifactHandler(request =>
        {
            if (request.RequestUri == PackUri)
            {
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    RequestMessage = request,
                    Headers = { Location = PackUri },
                };
            }

            throw new InvalidOperationException("No content request should occur after redirect rejection.");
        });
        using var artifactClient = new HttpClient(handler);
        var installer = CreateInstaller(registry, new FakePayloadInstaller(), fixture, artifactClient);

        await Assert.ThrowsAsync<IOException>(
            () => installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.Equal(1, handler.CallCount);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_RemoteContentHashFailureRemovesInstalledGameStaging()
    {
        var fixture = CreatePackFixture(remoteClientHashOverride: new string('f', 128));
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifactClient = CreateArtifactClient(fixture);
        var payload = new FakePayloadInstaller();
        var installer = CreateInstaller(registry, payload, fixture, artifactClient);

        await Assert.ThrowsAsync<IOException>(
            () => installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.Single(payload.Requests);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_PayloadFailureNeverDownloadsContentOrRegistersInstance()
    {
        var fixture = CreatePackFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifactClient = CreateArtifactClient(fixture, out var handler);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(new InvalidOperationException("payload failed")),
            fixture,
            artifactClient);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.Equal(1, handler.CallCount);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_CancellationCleansOperationDirectory()
    {
        var fixture = CreatePackFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        var handler = new ArtifactHandler(async (request, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return BytesResponse(request, fixture.PackBytes);
        });
        using var artifactClient = new HttpClient(handler);
        var installer = CreateInstaller(registry, new FakePayloadInstaller(), fixture, artifactClient);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(
            Request(),
            javaExecutablePath: null,
            cancellationToken: cancellation.Token));

        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_RegistryConflictDeletesPromotedDirectory()
    {
        var fixture = CreatePackFixture();
        var request = Request();
        using var registry = new MinecraftClientRegistry(_registryPath);
        await registry.SaveAsync(new MinecraftClientRegistryDocument
        {
            Instances =
            [
                new MinecraftClientInstance
                {
                    Id = request.InstanceId,
                    Name = "Existing",
                    DirectoryPath = Path.Combine(_root, "other-instance"),
                    GameVersion = "1.21.1",
                    InstalledVersionId = "1.21.1",
                },
            ],
        });
        using var artifactClient = CreateArtifactClient(fixture);
        var installer = CreateInstaller(registry, new FakePayloadInstaller(), fixture, artifactClient);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => installer.InstallAsync(request, javaExecutablePath: null));

        Assert.False(Directory.Exists(Path.Combine(_instances, request.InstanceId.ToString("N"))));
        Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
    }

    [Fact]
    public async Task InstallAsync_RejectsManifestMetadataMismatchBeforeGameInstall()
    {
        var fixture = CreatePackFixture();
        var mismatchedVersion = fixture.Version with { GameVersions = ["1.20.1"] };
        fixture = fixture with { Version = mismatchedVersion };
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifactClient = CreateArtifactClient(fixture);
        var payload = new FakePayloadInstaller();
        var installer = CreateInstaller(registry, payload, fixture, artifactClient);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.Empty(payload.Requests);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_RejectsProtectedLauncherOverrides()
    {
        var fixture = CreatePackFixture(extraArchiveEntries:
            [("client-overrides/versions/evil/profile.json", Encoding.UTF8.GetBytes("{}"))]);
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifactClient = CreateArtifactClient(fixture);
        var payload = new FakePayloadInstaller();
        var installer = CreateInstaller(registry, payload, fixture, artifactClient);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.Empty(payload.Requests);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task SafeArchiveClientInspectionUsesClientEnvAndExposesClientOverrides()
    {
        var fixture = CreatePackFixture();
        var path = Path.Combine(_root, "fixture.mrpack");
        await File.WriteAllBytesAsync(path, fixture.PackBytes);

        var plan = await SafeModpackArchive.InspectClientAsync(
            path,
            new OfficialModrinthClientDownloadUriPolicy());

        Assert.Contains(plan.Files, file => file.Path == "mods/client.jar");
        Assert.Contains(plan.Files, file => file.Path == "mods/optional.jar" && file.IsOptional);
        Assert.DoesNotContain(plan.Files, file => file.Path == "mods/server.jar");
        Assert.Single(plan.Overrides);
        Assert.Single(plan.ServerOverrides);
        Assert.Single(plan.ClientOverrides);
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

    private ModrinthMinecraftClientPackInstaller CreateInstaller(
        MinecraftClientRegistry registry,
        IMinecraftClientPayloadInstaller payload,
        PackFixture fixture,
        HttpClient artifactClient) => new(
        _instances,
        _staging,
        registry,
        new FakeReleaseCatalog("1.21.1"),
        payload,
        new FakeCatalog(fixture.Project, fixture.EffectiveVersion),
        artifactClient);

    private static HttpClient CreateArtifactClient(PackFixture fixture)
        => CreateArtifactClient(fixture, out _);

    private static HttpClient CreateArtifactClient(
        PackFixture fixture,
        out ArtifactHandler handler)
    {
        handler = new ArtifactHandler(request => request.RequestUri switch
        {
            var uri when uri == PackUri => BytesResponse(request, fixture.PackBytes),
            var uri when uri == ClientModUri => BytesResponse(request, fixture.ClientModBytes),
            var uri when uri == OptionalModUri => BytesResponse(request, fixture.OptionalModBytes),
            var uri when uri == ServerModUri => BytesResponse(request, fixture.ServerModBytes),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request },
        });
        return new HttpClient(handler);
    }

    private static HttpResponseMessage BytesResponse(HttpRequestMessage request, byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentLength = bytes.Length;
        return response;
    }

    private static PackFixture CreatePackFixture(
        string? remoteClientHashOverride = null,
        IReadOnlyList<(string Path, byte[] Bytes)>? extraArchiveEntries = null)
    {
        var client = Encoding.UTF8.GetBytes("verified-client-mod");
        var optional = Encoding.UTF8.GetBytes("verified-optional-mod");
        var server = Encoding.UTF8.GetBytes("server-only-mod");
        var manifest = JsonNode.Parse(FixtureText("modrinth-client-index.json"))!.AsObject();
        manifest["files"] = new JsonArray(
            FileNode(
                "mods/client.jar",
                ClientModUri,
                client,
                "required",
                "unsupported",
                remoteClientHashOverride),
            FileNode("mods/optional.jar", OptionalModUri, optional, "optional", "unsupported"),
            FileNode("mods/server.jar", ServerModUri, server, "unsupported", "required"));

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "modrinth.index.json", Encoding.UTF8.GetBytes(
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = false })));
            AddEntry(archive, "overrides/config/layer.txt", Encoding.UTF8.GetBytes("common"));
            AddEntry(archive, "client-overrides/config/layer.txt", Encoding.UTF8.GetBytes("client"));
            AddEntry(archive, "server-overrides/config/server-only.txt", Encoding.UTF8.GetBytes("server"));
            foreach (var entry in extraArchiveEntries ?? [])
            {
                AddEntry(archive, entry.Path, entry.Bytes);
            }
        }

        var pack = output.ToArray();
        var file = new ModrinthClientMrpackFile(
            "good.mrpack",
            PackUri,
            pack.LongLength,
            Sha512(pack),
            Sha1(pack),
            true);
        var version = new ModrinthClientModpackVersion(
            "PackGood1",
            "StableV1",
            "Stable Pack",
            "1.0.0",
            "client_and_server",
            ["1.21.1"],
            ["fabric"],
            DateTimeOffset.Parse("2026-08-20T12:00:00Z"),
            1000,
            file);
        var project = new ModrinthClientModpackProject(
            "PackGood1",
            "good-pack",
            "Good Client Pack",
            "Fixture",
            "TestAuthor",
            new Uri("https://cdn.modrinth.com/data/PackGood1/icon.png"),
            new Uri("https://cdn.modrinth.com/data/PackGood1/images/featured.png"),
            [],
            ["1.21.1"],
            ["fabric"],
            ["client_and_server"],
            5000,
            120,
            DateTimeOffset.Parse("2026-08-20T12:00:00Z"));
        return new PackFixture(
            pack,
            Sha512(pack),
            client,
            optional,
            server,
            project,
            version);
    }

    private static JsonObject FileNode(
        string path,
        Uri uri,
        byte[] bytes,
        string client,
        string server,
        string? sha512Override = null) => new()
    {
        ["path"] = path,
        ["hashes"] = new JsonObject
        {
            ["sha512"] = sha512Override ?? Sha512(bytes),
            ["sha1"] = Sha1(bytes),
        },
        ["env"] = new JsonObject
        {
            ["client"] = client,
            ["server"] = server,
        },
        ["downloads"] = new JsonArray(uri.AbsoluteUri),
        ["fileSize"] = bytes.LongLength,
    };

    private static void AddEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static string FixtureText(string fixtureName)
    {
        var assembly = typeof(ModrinthMinecraftClientPackInstallerTests).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(fixtureName, StringComparison.OrdinalIgnoreCase));
        using var input = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Fixture '{fixtureName}' is unavailable.");
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private async Task AssertNoInstallationAsync(MinecraftClientRegistry registry)
    {
        Assert.Empty((await registry.LoadAsync()).Instances);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_instances));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
    }

    private static string Sha512(byte[] bytes) => Convert.ToHexString(
        SHA512.HashData(bytes)).ToLowerInvariant();

    private static string Sha1(byte[] bytes) => Convert.ToHexString(
        SHA1.HashData(bytes)).ToLowerInvariant();

    private static ModrinthClientPackInstallRequest Request() => new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        "Good Client Pack",
        "PackGood1",
        "StableV1",
        MinecraftClientMemoryMode.Automatic,
        2048,
        6144,
        1280,
        720,
        false);

    private sealed record PackFixture(
        byte[] PackBytes,
        string PackSha512,
        byte[] ClientModBytes,
        byte[] OptionalModBytes,
        byte[] ServerModBytes,
        ModrinthClientModpackProject Project,
        ModrinthClientModpackVersion Version)
    {
        public ModrinthClientModpackVersion EffectiveVersion => Version with
        {
            MrpackFile = Version.MrpackFile with { Sha512 = PackSha512 },
        };
    }

    private sealed class FakeCatalog(
        ModrinthClientModpackProject project,
        ModrinthClientModpackVersion version) : IModrinthClientModpackCatalog
    {
        public Task<ModrinthClientModpackProject> GetProjectAsync(
            string projectId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(project);

        public Task<ModrinthClientModpackVersion> GetStableVersionAsync(
            string versionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(version);
        }

        public Task<IReadOnlyList<ModrinthClientModpackVersion>> GetStableVersionsAsync(
            string projectId,
            string? gameVersion = null,
            MinecraftClientLoader? loader = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ModrinthClientModpackVersion>>([version]);

        public Task<ModrinthClientModpackSearchPage> SearchAsync(
            ModrinthClientModpackSearchRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ModrinthClientModpackSearchPage([project], 0, 1, 1));

        public Task<ModrinthClientModpackSearchPage> GetPopularAsync(
            ModrinthClientModpackSearchRequest request,
            CancellationToken cancellationToken = default)
            => SearchAsync(request, cancellationToken);
    }

    private sealed class FakeReleaseCatalog(params string[] versions) : IMinecraftReleaseCatalog
    {
        public Task<MinecraftReleaseCatalogSnapshot> GetStableReleasesAsync(
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.Parse("2026-08-28T00:00:00Z");
            return Task.FromResult(new MinecraftReleaseCatalogSnapshot(
                versions[0],
                now,
                versions.Select(version => new MinecraftReleaseInfo(
                    version,
                    now,
                    new Uri($"https://piston-meta.mojang.com/v1/packages/a/{version}.json"),
                    new string('a', 40),
                    1)).ToArray()));
        }
    }

    private sealed class FakePayloadInstaller(Exception? failure = null) : IMinecraftClientPayloadInstaller
    {
        public List<MinecraftClientInstallRequest> Requests { get; } = [];

        public async Task<string> InstallAsync(
            MinecraftClientInstallRequest request,
            string stagingDirectory,
            string? javaExecutablePath,
            IProgress<MinecraftClientInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (failure is not null)
            {
                throw failure;
            }

            var marker = Path.Combine(stagingDirectory, "versions", "fake-profile", "installed.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, "installed", cancellationToken);
            return "fake-profile";
        }
    }

    private sealed class ArtifactHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _factory;
        private int _callCount;

        public ArtifactHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
            : this((request, _) => Task.FromResult(factory(request)))
        {
        }

        public ArtifactHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> factory)
        {
            _factory = factory;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return _factory(request, cancellationToken);
        }
    }
}
