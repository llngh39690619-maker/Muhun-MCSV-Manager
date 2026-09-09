using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.App.Tests;

public sealed class CurseForgeMetadataPreviewWorkflowTests
{
    [Theory]
    [InlineData("RLCraft", "2.9.3", "1.12.2", "forge-14.23.5.2860", "Forge")]
    [InlineData("SkyFactory 4", "4.2.4", "1.12.2", "forge-14.23.5.2860", "Forge")]
    [InlineData("The Pixelmon Modpack", "9.1.0", "1.16.5", "forge-36.2.34", "Forge")]
    [InlineData("Unrelated pack", "1.0", "1.20.1", "fabric-0.16.9", "Fabric")]
    public async Task MissingApiLoaderIsResolvedFromExactManifest(
        string name, string packVersion, string minecraft, string loaderId, string expected)
    {
        using var fixture = new Fixture();
        fixture.Packs[(100, 200)] = CreateArchive(name, packVersion, minecraft, loaderId);
        var project = Project(100, name);
        var versions = await fixture.Workflow.GetVersionsAsync(project, fixture.Credential, default);
        var initial = Assert.Single(versions);
        Assert.Empty(initial.Loader); // The real failure shape: omitted modLoader and no file label.
        Assert.Equal(0, fixture.Downloads);

        var resolved = await fixture.Workflow.ResolveVersionMetadataAsync(project, initial, fixture.Credential);

        Assert.Equal(expected, resolved.Loader);
        Assert.Equal(minecraft, resolved.MinecraftVersion);
        Assert.Equal(initial.ProjectId, resolved.ProjectId);
        Assert.Equal(initial.VersionId, resolved.VersionId);
        Assert.Empty(initial.Loader);
        Assert.True(fixture.Downloads > 0);
    }

    [Fact]
    public async Task CacheKeepsExactProjectFileAndFingerprintSeparate()
    {
        using var fixture = new Fixture();
        fixture.Packs[(100, 200)] = CreateArchive("A", "1", "1.20.1", "forge-47.2.0");
        fixture.Packs[(101, 200)] = CreateArchive("B", "1", "1.20.1", "fabric-0.16.9");
        var a = Version(100, 200, "1.20.1") with { MetadataFingerprint = "first" };
        var first = await fixture.Workflow.ResolveVersionMetadataAsync(Project(100), a, fixture.Credential);
        var count = fixture.Downloads;
        var cached = await fixture.Workflow.ResolveVersionMetadataAsync(Project(100), a, fixture.Credential);
        Assert.Equal(first, cached);
        Assert.Equal(count, fixture.Downloads);
        var otherProject = await fixture.Workflow.ResolveVersionMetadataAsync(Project(101),
            Version(101, 200, "1.20.1"), fixture.Credential);
        Assert.Equal("Fabric", otherProject.Loader);
        fixture.Packs[(100, 200)] = CreateArchive("A", "2", "1.20.1", "quilt-0.26.4");
        var changed = await fixture.Workflow.ResolveVersionMetadataAsync(Project(100),
            a with { MetadataFingerprint = "second" }, fixture.Credential);
        Assert.Equal("Quilt", changed.Loader);
    }

    [Fact]
    public async Task MinecraftMismatchDoesNotPoisonCacheAndCanRetry()
    {
        using var fixture = new Fixture();
        var version = Version(100, 200, "1.12.2");
        fixture.Packs[(100, 200)] = CreateArchive("A", "1", "1.20.1", "forge-47.2.0");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Workflow.ResolveVersionMetadataAsync(Project(100), version, fixture.Credential));
        fixture.Packs[(100, 200)] = CreateArchive("A", "1", "1.12.2", "forge-14.23.5.2860");
        var retried = await fixture.Workflow.ResolveVersionMetadataAsync(Project(100), version, fixture.Credential);
        Assert.Equal("Forge", retried.Loader);
        Assert.Equal("1.12.2", retried.MinecraftVersion);
    }

    [Fact]
    public async Task FailedRequestIsNotCached()
    {
        using var fixture = new Fixture();
        fixture.Packs[(100, 200)] = CreateArchive("A", "1", "1.20.1", "forge-47.2.0");
        fixture.FailDownload = true;
        var version = Version(100, 200, "1.20.1");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.Workflow.ResolveVersionMetadataAsync(Project(100), version, fixture.Credential));
        fixture.FailDownload = false;
        var retried = await fixture.Workflow.ResolveVersionMetadataAsync(Project(100), version, fixture.Credential);
        Assert.Equal("Forge", retried.Loader);
    }

    [Fact]
    public async Task AlreadyKnownLoaderDoesNotReadCdn()
    {
        using var fixture = new Fixture();
        var version = Version(100, 200, "1.20.1") with { Loader = "Forge" };
        Assert.Same(version, await fixture.Workflow.ResolveVersionMetadataAsync(Project(100), version));
        Assert.Equal(0, fixture.Downloads);
    }

    [Fact]
    public async Task CancelledAndCrossProjectRequestsDoNotReadCdn()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Workflow.ResolveVersionMetadataAsync(Project(100), Version(100, 200, "1.20.1"),
                fixture.Credential, new CancellationToken(true)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Workflow.ResolveVersionMetadataAsync(Project(101), Version(100, 200, "1.20.1"), fixture.Credential));
        Assert.Equal(0, fixture.Downloads);
    }

    private static OnlineModpackSearchResult Project(int id, string name = "Fixture") =>
        new(OnlineModpackProvider.CurseForge, id.ToString(), name, "", "");

    private static OnlineModpackVersion Version(int project, int file, string minecraft) =>
        new(OnlineModpackProvider.CurseForge, project.ToString(), file.ToString(), "Fixture v1", minecraft,
            "", "release", DateTimeOffset.UnixEpoch, false);

    private static byte[] CreateArchive(string name, string version, string minecraft, string loader)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
        {
            writer.Write(JsonSerializer.Serialize(new
            {
                manifestType = "minecraftModpack", manifestVersion = 1, name, version,
                minecraft = new { version = minecraft, modLoaders = new[] { new { id = loader, primary = true } } },
                files = Array.Empty<object>(), overrides = "overrides"
            }));
        }
        return output.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly AppearanceThemeServiceTests.TestDirectory _directory = new();
        private readonly HttpClient _api;
        private readonly HttpClient _cdn;
        public Dictionary<(int Project, int File), byte[]> Packs { get; } = [];
        public OnlineModpackWorkflow Workflow { get; }
        public SecureString Credential { get; } = new();
        public int Downloads;
        public bool FailDownload;

        public Fixture()
        {
            foreach (var c in "fixture-key") Credential.AppendChar(c);
            Credential.MakeReadOnly();
            _api = new HttpClient(new Handler(request =>
            {
                Assert.Equal("api.curseforge.com", request.RequestUri!.Host);
                Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("x-api-key")));
                var parts = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var project = int.Parse(parts[2]);
                var file = parts.Length > 4 ? int.Parse(parts[4]) : 200;
                var archive = Packs[(project, file)];
                if (parts.Length == 3)
                    return Json(new { data = new { id = project, gameId = 432, classId = 4471,
                        slug = "fixture", name = "Fixture", isAvailable = true, allowModDistribution = true,
                        latestFilesIndexes = new[] { new { fileId = file, gameVersion = Minecraft(archive) } } } });
                if (parts.Length == 6)
                    return Json(new { data = $"https://edge.forgecdn.net/{project}/{file}/pack.zip" });
                var data = new { id = file, modId = project, gameId = 432, displayName = "Fixture v1",
                    fileName = "pack.zip", fileLength = archive.Length, isAvailable = true,
                    isServerPack = false, releaseType = 1, fileStatus = 4,
                    gameVersions = new[] { Minecraft(archive) }, hashes = Array.Empty<object>() };
                return parts.Length == 4 ? Json(new { data = new[] { data } }) : Json(new { data });
            }));
            _cdn = new HttpClient(new Handler(request =>
            {
                Interlocked.Increment(ref Downloads);
                Assert.False(request.Headers.Contains("x-api-key"));
                if (FailDownload) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var parts = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(Packs[(int.Parse(parts[0]), int.Parse(parts[1]))]) };
            }));
            Workflow = new OnlineModpackWorkflow(new ApplicationPaths(_directory.Path), null, null, null, null,
                new CurseForgeModpackProvider(_api, _cdn, "McsvTests/1.0"));
        }

        private static string Minecraft(byte[] bytes)
        {
            using var zip = new ZipArchive(new MemoryStream(bytes));
            using var doc = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
            return doc.RootElement.GetProperty("minecraft").GetProperty("version").GetString()!;
        }
        private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
        public void Dispose() { Workflow.Dispose(); Credential.Dispose(); _api.Dispose(); _cdn.Dispose(); _directory.Dispose(); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = respond(request); response.RequestMessage = request; return Task.FromResult(response);
        }
    }
}
