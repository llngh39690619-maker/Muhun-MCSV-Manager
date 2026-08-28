using System.Net;
using System.Text;
using MinecraftServerManager.GameClient;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class MojangReleaseCatalogTests
{
    [Fact]
    public async Task StableCatalog_ExcludesEveryNonReleaseTypeAndSortsNewestFirst()
    {
        const string json = """
            {
              "latest": { "release": "1.21.8", "snapshot": "25w35a" },
              "versions": [
                { "id": "25w35a", "type": "snapshot", "url": "https://piston-meta.mojang.com/v1/packages/a/snapshot.json", "sha1": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "releaseTime": "2025-08-27T12:00:00Z", "complianceLevel": 1 },
                { "id": "1.0", "type": "release", "url": "https://piston-meta.mojang.com/v1/packages/b/1.0.json", "sha1": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "releaseTime": "2011-11-18T14:00:00Z", "complianceLevel": 0 },
                { "id": "1.21.8-rc1", "type": "snapshot", "url": "https://piston-meta.mojang.com/v1/packages/c/rc.json", "sha1": "cccccccccccccccccccccccccccccccccccccccc", "releaseTime": "2025-07-10T12:00:00Z", "complianceLevel": 1 },
                { "id": "1.21.8", "type": "release", "url": "https://piston-meta.mojang.com/v1/packages/d/1.21.8.json", "sha1": "dddddddddddddddddddddddddddddddddddddddd", "releaseTime": "2025-07-17T12:00:00Z", "complianceLevel": 1 },
                { "id": "b1.7.3", "type": "old_beta", "url": "https://piston-meta.mojang.com/v1/packages/e/beta.json", "sha1": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "releaseTime": "2011-07-08T12:00:00Z", "complianceLevel": 0 }
              ]
            }
            """;

        using var client = new HttpClient(new StubHandler(json));
        var catalog = new MojangReleaseCatalog(client);

        var result = await catalog.GetStableReleasesAsync();

        Assert.Equal("1.21.8", result.LatestReleaseId);
        Assert.Equal(["1.21.8", "1.0"], result.Releases.Select(release => release.Id));
        Assert.All(result.Releases, release =>
            Assert.Equal("piston-meta.mojang.com", release.MetadataUri.Host));
    }

    [Fact]
    public async Task StableCatalog_RejectsLatestReleaseMissingFromReleaseEntries()
    {
        const string json = """
            {
              "latest": { "release": "1.21.8" },
              "versions": [
                { "id": "1.21.8", "type": "snapshot", "url": "https://piston-meta.mojang.com/v1/packages/a/value.json", "sha1": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "releaseTime": "2025-07-17T12:00:00Z", "complianceLevel": 1 }
              ]
            }
            """;

        using var client = new HttpClient(new StubHandler(json));
        var catalog = new MojangReleaseCatalog(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.GetStableReleasesAsync());
    }

    [Fact]
    public async Task StableCatalog_RejectsMetadataOutsideOfficialHost()
    {
        const string json = """
            {
              "latest": { "release": "1.21.8" },
              "versions": [
                { "id": "1.21.8", "type": "release", "url": "https://example.invalid/value.json", "sha1": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "releaseTime": "2025-07-17T12:00:00Z", "complianceLevel": 1 }
              ]
            }
            """;

        using var client = new HttpClient(new StubHandler(json));
        var catalog = new MojangReleaseCatalog(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.GetStableReleasesAsync());
    }

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(responseBody);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(bytes),
            };
            response.Content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(response);
        }
    }
}
