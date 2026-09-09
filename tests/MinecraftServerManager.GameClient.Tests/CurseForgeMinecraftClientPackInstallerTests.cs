using System.IO.Compression;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class CurseForgeMinecraftClientPackInstallerTests : IDisposable
{
    private const string ApiKey = "operation-only-test-key";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-curseforge-client-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _instances;
    private readonly string _staging;
    private readonly string _registryPath;

    public CurseForgeMinecraftClientPackInstallerTests()
    {
        _instances = Path.Combine(_root, "instances");
        _staging = Path.Combine(_root, "staging");
        _registryPath = Path.Combine(_root, "registry.json");
        Directory.CreateDirectory(_instances);
        Directory.CreateDirectory(_staging);
    }

    [Fact]
    public void InstallRequest_DefaultsToFourConcurrentDownloads()
    {
        Assert.Equal(4, Request().MaximumConcurrentDownloads);
    }

    [Fact]
    public async Task InstallAsync_VerifiesExactPackAndDependenciesThenPublishesRunnableClient()
    {
        var fixture = CreateFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var apiClient = CreateApiClient(fixture, out var apiHandler);
        using var downloadClient = CreateDownloadClient(fixture, out var downloadHandler);
        var installer = CreateInstaller(registry, payload, apiClient, downloadClient);
        using var credential = Credential();

        var result = await installer.InstallAsync(Request(), credential, javaExecutablePath: null);

        var finalRoot = Path.Combine(_instances, result.Instance.Id.ToString("N"));
        Assert.Equal(finalRoot, result.Instance.DirectoryPath);
        Assert.Equal("1.12.2", result.Instance.GameVersion);
        Assert.Equal(MinecraftClientLoader.Forge, result.Instance.Loader);
        Assert.Equal("14.23.5.2860", result.Instance.LoaderVersion);
        Assert.Equal("fake-profile", result.Instance.InstalledVersionId);
        Assert.Equal("curseforge", result.Instance.CatalogProvider);
        Assert.Equal("100", result.Instance.CatalogProjectId);
        Assert.Equal("101", result.Instance.CatalogVersionId);
        Assert.Equal("media.forgecdn.net", result.Instance.CatalogIconUri?.Host);
        Assert.Equal("RLCraft", result.PackName);
        Assert.Equal("v2.9.3", result.PackVersion);
        Assert.Equal(fixture.RequiredBytes, await File.ReadAllBytesAsync(
            Path.Combine(finalRoot, "mods", "required-mod.jar")));
        Assert.False(File.Exists(Path.Combine(finalRoot, "mods", "optional-mod.jar")));
        Assert.Equal("safe override", await File.ReadAllTextAsync(
            Path.Combine(finalRoot, "config", "rlcraft.cfg")));
        Assert.Contains("mods/required-mod.jar", result.InstalledPaths);
        Assert.Contains("config/rlcraft.cfg", result.InstalledPaths);
        Assert.Equal(1, result.InstalledContentFiles);
        Assert.Equal(1, result.SkippedOptionalFiles);
        Assert.Single(payload.Requests);
        Assert.Equal("1.12.2", payload.Requests[0].GameVersion);
        Assert.Equal(MinecraftClientLoader.Forge, payload.Requests[0].Loader);
        Assert.Equal("14.23.5.2860", payload.Requests[0].LoaderVersion);
        Assert.Equal(result.Instance.Id, Assert.Single((await registry.LoadAsync()).Instances).Id);
        Assert.DoesNotContain(ApiKey, await File.ReadAllTextAsync(_registryPath), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
        Assert.True(apiHandler.CallCount >= 7);
        Assert.All(apiHandler.SeenApiKeys, key => Assert.Equal(ApiKey, key));
        Assert.True(downloadHandler.CallCount >= 2);
        Assert.False(downloadHandler.SawApiKey);
    }

    [Fact]
    public async Task InstallAsync_DependencyHashFailureRollsBackPayloadRegistryAndStaging()
    {
        var fixture = CreateFixture() with { RequiredHashOverride = new string('0', 40) };
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var apiClient = CreateApiClient(fixture, out _);
        using var downloadClient = CreateDownloadClient(fixture, out _);
        var installer = CreateInstaller(registry, payload, apiClient, downloadClient);
        var request = Request();
        using var credential = Credential();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(request, credential, javaExecutablePath: null));

        Assert.Single(payload.Requests);
        Assert.False(Directory.Exists(Path.Combine(_instances, request.InstanceId.ToString("N"))));
        Assert.Empty((await registry.LoadAsync()).Instances);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
    }

    [Fact]
    public async Task InstallAsync_ManifestMinecraftVersionMismatchRollsBackBeforePayloadInstallation()
    {
        var fixture = CreateFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var apiClient = CreateApiClient(fixture, out _);
        using var downloadClient = CreateDownloadClient(fixture, out _);
        var installer = CreateInstaller(registry, payload, apiClient, downloadClient);
        var request = Request() with { ExpectedMinecraftVersion = "1.20.1" };
        using var credential = Credential();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(request, credential, javaExecutablePath: null));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Empty(payload.Requests);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_UnsafeExpectedMinecraftVersionFailsBeforeMutation()
    {
        var fixture = CreateFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var apiClient = CreateApiClient(fixture, out var apiHandler);
        using var downloadClient = CreateDownloadClient(fixture, out var downloadHandler);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            apiClient,
            downloadClient);
        var request = Request() with { ExpectedMinecraftVersion = "../1.12.2" };
        using var credential = Credential();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(request, credential, javaExecutablePath: null));

        Assert.Equal(0, apiHandler.CallCount);
        Assert.Equal(0, downloadHandler.CallCount);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_RootDistributionUnavailablePreservesOfficialPageFallbackSignal()
    {
        var fixture = CreateFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var apiClient = CreateApiClient(fixture, out _, new HashSet<int> { 100 });
        using var downloadClient = CreateDownloadClient(fixture, out _);
        var installer = CreateInstaller(registry, payload, apiClient, downloadClient);
        using var credential = Credential();

        var exception = await Assert.ThrowsAsync<CurseForgeServerPackException>(() =>
            installer.InstallAsync(Request(), credential, javaExecutablePath: null));

        Assert.Equal(CurseForgeServerPackResolutionStatus.DistributionUnavailable, exception.Status);
        Assert.Empty(payload.Requests);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_DependencyDistributionUnavailableIsOrdinaryFailureAndRollsBack()
    {
        var fixture = CreateFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var apiClient = CreateApiClient(fixture, out _, new HashSet<int> { 200 });
        using var downloadClient = CreateDownloadClient(fixture, out _);
        var installer = CreateInstaller(registry, payload, apiClient, downloadClient);
        using var credential = Credential();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(Request(), credential, javaExecutablePath: null));

        Assert.Contains("200/201", exception.Message, StringComparison.Ordinal);
        var providerException = Assert.IsType<CurseForgeServerPackException>(exception.InnerException);
        Assert.Equal(CurseForgeServerPackResolutionStatus.DistributionUnavailable, providerException.Status);
        Assert.Single(payload.Requests);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_DependencyWithoutDownloadUrlIsOrdinaryFailureAndRollsBack()
    {
        var fixture = CreateFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var apiClient = CreateApiClient(
            fixture,
            out _,
            unavailableDownloadUrls: new HashSet<(int ModId, int FileId)> { (200, 201) });
        using var downloadClient = CreateDownloadClient(fixture, out _);
        var installer = CreateInstaller(registry, payload, apiClient, downloadClient);
        using var credential = Credential();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(Request(), credential, javaExecutablePath: null));

        Assert.Contains("200/201", exception.Message, StringComparison.Ordinal);
        var providerException = Assert.IsType<CurseForgeServerPackException>(exception.InnerException);
        Assert.Equal(CurseForgeServerPackResolutionStatus.DistributionUnavailable, providerException.Status);
        Assert.Single(payload.Requests);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_ReferencedContentByteLimitRollsBackWithoutPublishingInstance()
    {
        var fixture = CreateFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var apiClient = CreateApiClient(fixture, out _);
        using var downloadClient = CreateDownloadClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            payload,
            apiClient,
            downloadClient,
            maximumReferencedContentBytes: fixture.RequiredBytes.Length - 1);
        using var credential = Credential();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(Request(), credential, javaExecutablePath: null));

        Assert.Contains("byte limit", exception.Message, StringComparison.Ordinal);
        Assert.Single(payload.Requests);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_RegistryConflictDeletesAlreadyPromotedPayload()
    {
        var fixture = CreateFixture();
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
                    DirectoryPath = Path.Combine(_root, "existing"),
                    GameVersion = "1.12.2",
                    InstalledVersionId = "existing-profile",
                },
            ],
        });
        using var apiClient = CreateApiClient(fixture, out _);
        using var downloadClient = CreateDownloadClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            apiClient,
            downloadClient);
        using var credential = Credential();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(request, credential, javaExecutablePath: null));

        Assert.False(Directory.Exists(Path.Combine(_instances, request.InstanceId.ToString("N"))));
        Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
    }

    [Fact]
    public async Task InstallAsync_EmptyCredentialFailsBeforeAnyApiOrFilesystemMutation()
    {
        var fixture = CreateFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var apiClient = CreateApiClient(fixture, out var apiHandler);
        using var downloadClient = CreateDownloadClient(fixture, out var downloadHandler);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            apiClient,
            downloadClient);
        using var credential = new SecureString();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            installer.InstallAsync(Request(), credential, javaExecutablePath: null));

        Assert.Equal(0, apiHandler.CallCount);
        Assert.Equal(0, downloadHandler.CallCount);
        Assert.Empty((await registry.LoadAsync()).Instances);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
    }

    private CurseForgeMinecraftClientPackInstaller CreateInstaller(
        MinecraftClientRegistry registry,
        IMinecraftClientPayloadInstaller payload,
        HttpClient apiClient,
        HttpClient downloadClient,
        long maximumReferencedContentBytes = 32L * 1024 * 1024 * 1024) => new(
        _instances,
        _staging,
        registry,
        new FakeReleaseCatalog("1.12.2"),
        payload,
        new CurseForgeModpackProvider(apiClient, downloadClient, "XMCSV.Tests/1.0"),
        new CurseForgeModpackManifestInspector(),
        maximumReferencedContentBytes);

    private static CurseForgeClientPackInstallRequest Request() => new(
        Guid.NewGuid(),
        "RLCraft v2.9.3",
        100,
        101,
        MinecraftClientMemoryMode.UseGlobalDefault,
        2_048,
        6_144,
        1_280,
        720,
        FullScreen: false,
        JavaMajorVersion: 8,
        ExpectedMinecraftVersion: "1.12.2");

    private static SecureString Credential()
    {
        var result = new SecureString();
        foreach (var character in ApiKey)
        {
            result.AppendChar(character);
        }

        result.MakeReadOnly();
        return result;
    }

    private static Fixture CreateFixture()
    {
        var required = Encoding.UTF8.GetBytes("required mod payload");
        var optional = Encoding.UTF8.GetBytes("optional mod payload");
        var package = CreatePackage();
        return new Fixture(package, required, optional);
    }

    private static byte[] CreatePackage()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            const string manifest = """
                {
                  "minecraft": {
                    "version": "1.12.2",
                    "modLoaders": [
                      { "id": "forge-14.23.5.2860", "primary": true }
                    ]
                  },
                  "manifestType": "minecraftModpack",
                  "manifestVersion": 1,
                  "name": "RLCraft",
                  "version": "v2.9.3",
                  "author": "Shivaxi",
                  "files": [
                    { "projectID": 200, "fileID": 201, "required": true },
                    { "projectID": 300, "fileID": 301, "required": false }
                  ],
                  "overrides": "overrides"
                }
                """;
            WriteEntry(archive, "manifest.json", Encoding.UTF8.GetBytes(manifest));
            WriteEntry(archive, "overrides/config/rlcraft.cfg", Encoding.UTF8.GetBytes("safe override"));
        }

        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static HttpClient CreateApiClient(
        Fixture fixture,
        out ApiHandler handler,
        IReadOnlySet<int>? restrictedProjects = null,
        IReadOnlySet<(int ModId, int FileId)>? unavailableDownloadUrls = null)
    {
        handler = new ApiHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (TryParseDownloadPath(path, out var downloadModId, out var downloadFileId))
            {
                if (unavailableDownloadUrls?.Contains((downloadModId, downloadFileId)) == true)
                {
                    return JsonResponse(request, """{ "data": null }""");
                }

                var artifact = fixture.Resolve(downloadModId, downloadFileId);
                return JsonResponse(
                    request,
                    $$"""{ "data": "https://edge.forgecdn.net/files/{{downloadModId}}/{{downloadFileId}}/{{artifact.FileName}}" }""");
            }

            if (TryParseFilePath(path, out var fileModId, out var fileId))
            {
                var artifact = fixture.Resolve(fileModId, fileId);
                var hash = artifact.HashOverride ?? Convert.ToHexString(SHA1.HashData(artifact.Bytes));
                return JsonResponse(
                    request,
                    $$"""
                    {
                      "data": {
                        "id": {{fileId}}, "gameId": 432, "modId": {{fileModId}},
                        "isAvailable": true,
                        "displayName": "{{artifact.FileName}}", "fileName": "{{artifact.FileName}}",
                        "releaseType": 1, "fileStatus": 10,
                        "hashes": [{ "value": "{{hash}}", "algo": 1 }],
                        "fileLength": {{artifact.Bytes.Length}},
                        "fileDate": "2026-09-09T00:00:00Z", "gameVersions": ["1.12.2", "Forge"],
                        "isServerPack": false, "serverPackFileId": null
                      }
                    }
                    """);
            }

            if (TryParseProjectPath(path, out var modId))
            {
                var allowModDistribution = restrictedProjects is null || !restrictedProjects.Contains(modId);
                return JsonResponse(
                    request,
                    $$"""
                    {
                      "data": {
                        "id": {{modId}}, "gameId": 432, "classId": 4471,
                        "slug": "project-{{modId}}", "name": "{{(modId == 100 ? "RLCraft" : $"Dependency {modId}")}}",
                        "summary": "Test fixture", "authors": [{ "name": "Author" }],
                        "links": { "websiteUrl": "https://www.curseforge.com/minecraft/mc-mods/project-{{modId}}" },
                        "logo": { "thumbnailUrl": "https://media.forgecdn.net/avatars/{{modId}}/icon.png" },
                        "screenshots": [{ "thumbnailUrl": "https://media.forgecdn.net/screenshots/{{modId}}.jpg" }],
                        "downloadCount": 1, "dateModified": "2026-09-09T00:00:00Z",
                        "isAvailable": true, "allowModDistribution": {{allowModDistribution.ToString().ToLowerInvariant()}}
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        });
        return new HttpClient(handler);
    }

    private static HttpClient CreateDownloadClient(Fixture fixture, out DownloadHandler handler)
    {
        handler = new DownloadHandler(request =>
        {
            var segments = request.RequestUri!.AbsolutePath.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);
            var modId = int.Parse(segments[^3]);
            var fileId = int.Parse(segments[^2]);
            var artifact = fixture.Resolve(modId, fileId);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(artifact.Bytes),
            };
        });
        return new HttpClient(handler);
    }

    private static HttpResponseMessage JsonResponse(HttpRequestMessage request, string json) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static bool TryParseProjectPath(string path, out int modId)
    {
        modId = 0;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["v1", "mods", var mod] && int.TryParse(mod, out modId);
    }

    private static bool TryParseFilePath(string path, out int modId, out int fileId)
    {
        modId = 0;
        fileId = 0;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["v1", "mods", var mod, "files", var file] &&
               int.TryParse(mod, out modId) && int.TryParse(file, out fileId);
    }

    private static bool TryParseDownloadPath(string path, out int modId, out int fileId)
    {
        modId = 0;
        fileId = 0;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["v1", "mods", var mod, "files", var file, "download-url"] &&
               int.TryParse(mod, out modId) && int.TryParse(file, out fileId);
    }

    private async Task AssertNoInstallationAsync(MinecraftClientRegistry registry)
    {
        Assert.Empty((await registry.LoadAsync()).Instances);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_instances));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
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

    private sealed record Fixture(
        byte[] PackageBytes,
        byte[] RequiredBytes,
        byte[] OptionalBytes,
        string? RequiredHashOverride = null)
    {
        public Artifact Resolve(int modId, int fileId) => (modId, fileId) switch
        {
            (100, 101) => new Artifact("rlcraft-v2.9.3.zip", PackageBytes),
            (200, 201) => new Artifact("required-mod.jar", RequiredBytes, RequiredHashOverride),
            (300, 301) => new Artifact("optional-mod.jar", OptionalBytes),
            _ => throw new InvalidOperationException($"Unexpected artifact {modId}/{fileId}."),
        };
    }

    private sealed record Artifact(string FileName, byte[] Bytes, string? HashOverride = null);

    private sealed class FakeReleaseCatalog(params string[] versions) : IMinecraftReleaseCatalog
    {
        public Task<MinecraftReleaseCatalogSnapshot> GetStableReleasesAsync(
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.Parse("2026-09-09T00:00:00Z");
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

    private sealed class ApiHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public List<string> SeenApiKeys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            SeenApiKeys.Add(Assert.Single(request.Headers.GetValues("x-api-key")));
            return Task.FromResult(responder(request));
        }
    }

    private sealed class DownloadHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        private int _callCount;
        private int _sawApiKey;

        public int CallCount => Volatile.Read(ref _callCount);

        public bool SawApiKey => Volatile.Read(ref _sawApiKey) != 0;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            if (request.Headers.Contains("x-api-key"))
            {
                Interlocked.Exchange(ref _sawApiKey, 1);
            }

            return Task.FromResult(responder(request));
        }
    }
}
