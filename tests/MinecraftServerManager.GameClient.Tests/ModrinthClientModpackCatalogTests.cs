using System.Net;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class ModrinthClientModpackCatalogTests
{
    [Fact]
    public async Task SearchAsync_UsesClientFacetsAndReturnsOnlyClientCompatiblePreviewMetadata()
    {
        var handler = FixtureHandler("modrinth-client-search.json");
        using var client = new HttpClient(handler);
        var catalog = new ModrinthClientModpackCatalog(client, "X-MCSV-Tests/1.0");

        var page = await catalog.SearchAsync(new ModrinthClientModpackSearchRequest(
            "adventure",
            "1.21.1",
            MinecraftClientLoader.Fabric,
            "adventure",
            ModrinthClientModpackSort.Updated,
            0,
            20));

        var project = Assert.Single(page.Projects);
        Assert.Equal("PackGood1", project.ProjectId);
        Assert.Equal("cdn.modrinth.com", Assert.IsType<Uri>(project.IconUri).Host);
        Assert.Equal("cdn.modrinth.com", Assert.IsType<Uri>(project.FeaturedImageUri).Host);
        Assert.Single(project.GalleryImageUris);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("api.modrinth.com", request.Host);
        var query = Uri.UnescapeDataString(request.Query);
        Assert.Contains("project_type:modpack", query, StringComparison.Ordinal);
        Assert.Contains("environment:client_only", query, StringComparison.Ordinal);
        Assert.Contains("versions:1.21.1", query, StringComparison.Ordinal);
        Assert.Contains("categories:fabric", query, StringComparison.Ordinal);
        Assert.Contains("categories:adventure", query, StringComparison.Ordinal);
        Assert.Contains("index=updated", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPopularAsync_ForcesDownloadsSortWithoutAFreeTextQuery()
    {
        var handler = FixtureHandler("modrinth-client-search.json");
        using var client = new HttpClient(handler);
        var catalog = new ModrinthClientModpackCatalog(client, "X-MCSV-Tests/1.0");

        await catalog.GetPopularAsync(new ModrinthClientModpackSearchRequest(Query: "ignored"));

        var query = Uri.UnescapeDataString(Assert.Single(handler.Requests).Query);
        Assert.Contains("index=downloads", query, StringComparison.Ordinal);
        Assert.Contains("query=&", query, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStableVersionsAsync_ExcludesBetaServerOnlyAndUnofficialArtifacts()
    {
        var handler = FixtureHandler("modrinth-client-versions.json");
        using var client = new HttpClient(handler);
        var catalog = new ModrinthClientModpackCatalog(client, "X-MCSV-Tests/1.0");

        var versions = await catalog.GetStableVersionsAsync(
            "PackGood1",
            "1.21.1",
            MinecraftClientLoader.Fabric);

        var stable = Assert.Single(versions);
        Assert.Equal("StableV1", stable.VersionId);
        Assert.Equal("cdn.modrinth.com", stable.MrpackFile.DownloadUri.Host);
        Assert.Equal(128, stable.MrpackFile.Sha512.Length);
        var query = Uri.UnescapeDataString(Assert.Single(handler.Requests).Query);
        Assert.Contains("game_versions=[\"1.21.1\"]", query, StringComparison.Ordinal);
        Assert.Contains("loaders=[\"fabric\"]", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetProjectAsync_ReturnsOnlyOfficialCdnImages()
    {
        var handler = FixtureHandler("modrinth-client-project.json");
        using var client = new HttpClient(handler);
        var catalog = new ModrinthClientModpackCatalog(client, "X-MCSV-Tests/1.0");

        var project = await catalog.GetProjectAsync("PackGood1");

        Assert.Equal("Good Client Pack", project.Title);
        Assert.Equal("# Complete overview\nThis is the full project description.", project.FullDescription);
        Assert.Single(project.GalleryImageUris);
        Assert.All(project.GalleryImageUris, uri => Assert.Equal("cdn.modrinth.com", uri.Host));
    }

    [Fact]
    public async Task GetStableVersionAsync_ReturnsASelectableReleaseAndRejectsBeta()
    {
        using var versionsDocument = JsonDocument.Parse(FixtureBytes("modrinth-client-versions.json"));
        var release = Encoding.UTF8.GetBytes(versionsDocument.RootElement[0].GetRawText());
        var beta = Encoding.UTF8.GetBytes(versionsDocument.RootElement[1].GetRawText());
        var handler = new StubHandler((request, _) => Task.FromResult(Success(
            request.RequestUri!.AbsolutePath.EndsWith("/StableV1", StringComparison.Ordinal)
                ? release
                : beta,
            request)));
        using var client = new HttpClient(handler);
        var catalog = new ModrinthClientModpackCatalog(client, "X-MCSV-Tests/1.0");

        var stable = await catalog.GetStableVersionAsync("StableV1");
        Assert.Equal("StableV1", stable.VersionId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.GetStableVersionAsync("BetaV1"));
    }

    [Fact]
    public async Task ApiRedirectIsRejectedEvenWhenItEndsAtTheOfficialHost()
    {
        var bytes = FixtureBytes("modrinth-client-search.json");
        var handler = new StubHandler((request, _) =>
        {
            var response = Success(bytes, request);
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.modrinth.com/v2/search?redirected=true");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var catalog = new ModrinthClientModpackCatalog(client, "X-MCSV-Tests/1.0");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.SearchAsync(new ModrinthClientModpackSearchRequest()));
    }

    [Fact]
    public async Task ApiDeclaredOversizeAndCancellationAreRejectedWithoutPartialResults()
    {
        var bytes = FixtureBytes("modrinth-client-search.json");
        var oversized = new StubHandler((request, _) =>
        {
            var response = Success(bytes, request);
            response.Content.Headers.ContentLength = 16L * 1024 * 1024 + 1;
            return Task.FromResult(response);
        });
        using (var client = new HttpClient(oversized))
        {
            var catalog = new ModrinthClientModpackCatalog(client, "X-MCSV-Tests/1.0");
            await Assert.ThrowsAsync<InvalidDataException>(
                () => catalog.SearchAsync(new ModrinthClientModpackSearchRequest()));
        }

        var cancelled = FixtureHandler("modrinth-client-search.json");
        using var cancelledClient = new HttpClient(cancelled);
        var cancelledCatalog = new ModrinthClientModpackCatalog(
            cancelledClient,
            "X-MCSV-Tests/1.0");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelledCatalog.SearchAsync(
                new ModrinthClientModpackSearchRequest(),
                cancellation.Token));
    }

    [Fact]
    public async Task SearchInputBoundsRejectInvalidFacetsBeforeNetworkAccess()
    {
        var handler = FixtureHandler("modrinth-client-search.json");
        using var client = new HttpClient(handler);
        var catalog = new ModrinthClientModpackCatalog(client, "X-MCSV-Tests/1.0");

        await Assert.ThrowsAsync<ArgumentException>(() => catalog.SearchAsync(
            new ModrinthClientModpackSearchRequest(GameVersion: "../1.21.1")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => catalog.SearchAsync(
            new ModrinthClientModpackSearchRequest(Limit: 101)));
        Assert.Empty(handler.Requests);
    }

    private static StubHandler FixtureHandler(string fixtureName)
    {
        var bytes = FixtureBytes(fixtureName);
        return new StubHandler((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Success(bytes, request));
        });
    }

    private static byte[] FixtureBytes(string fixtureName)
    {
        var assembly = typeof(ModrinthClientModpackCatalogTests).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(fixtureName, StringComparison.OrdinalIgnoreCase));
        using var input = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Fixture '{fixtureName}' was not embedded.");
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static HttpResponseMessage Success(byte[] bytes, HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentLength = bytes.Length;
        return response;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        private readonly List<Uri> _requests = [];

        public IReadOnlyList<Uri> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requests.Add(request.RequestUri!);
            return responseFactory(request, cancellationToken);
        }
    }
}
