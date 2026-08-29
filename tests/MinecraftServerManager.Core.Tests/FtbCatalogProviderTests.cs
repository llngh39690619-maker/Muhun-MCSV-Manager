using System.Collections.Concurrent;
using System.Net;
using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class FtbCatalogProviderTests
{
    [Fact]
    public void TimestampNormalizer_AcceptsSecondsAndMillisecondsButRejectsUnsafeEpochs()
    {
        var expected = DateTimeOffset.Parse("2026-08-20T03:04:05Z");

        Assert.Equal(expected, FtbTimestampNormalizer.NormalizeUtc(expected.ToUnixTimeSeconds()));
        Assert.Equal(expected, FtbTimestampNormalizer.NormalizeUtc(expected.ToUnixTimeMilliseconds()));
        Assert.Null(FtbTimestampNormalizer.NormalizeUtc(null));
        Assert.Null(FtbTimestampNormalizer.NormalizeUtc(0));
        Assert.Null(FtbTimestampNormalizer.NormalizeUtc(1));
        Assert.Null(FtbTimestampNormalizer.NormalizeUtc(99_999_999_999));
        Assert.Null(FtbTimestampNormalizer.NormalizeUtc(
            DateTimeOffset.Parse("2200-01-01T00:00:00Z").ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task GetPackAsync_NormalizesSecondsToCanonicalMilliseconds()
    {
        var updated = DateTimeOffset.Parse("2026-08-20T03:04:05Z");
        using var client = new HttpClient(new FtbCatalogStubHandler(_ => JsonResponse(
            $$"""
            {
              "status": "success", "id": 134, "name": "Pack",
              "versions": [{ "id": 1, "name": "1.0", "type": "release",
                "updated": {{updated.ToUnixTimeSeconds()}}, "targets": [] }]
            }
            """)));
        var provider = new FtbCatalogProvider(client, "Muhun-MCSV-Manager.Tests/1.0");

        var pack = await provider.GetPackAsync(134);

        Assert.Equal(updated.ToUnixTimeMilliseconds(), Assert.Single(pack.Versions).Updated);
    }

    [Fact]
    public async Task SearchAsync_HydratesFtbPacks_IgnoresCurseForgeAndSelectsHighestReleaseId()
    {
        var requests = new ConcurrentBag<Uri>();
        using var client = new HttpClient(new FtbCatalogStubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/modpacks/public/modpack/search/8" => JsonResponse(
                    """
                    {
                      "status": "success",
                      "packs": [134, 129, 134],
                      "curseforge": [1611302, 999999],
                      "count": 4
                    }
                    """),
                "/v1/modpacks/public/modpack/134" => JsonResponse(
                    """
                    {
                      "status": "success", "id": 134,
                      "name": "FTB 天空：Aero", "slug": "ftb-skies-2-aero", "private": false,
                      "synopsis": "天空生存模組包", "installs": 456789,
                      "art": [
                        { "url": "https://cdn.feed-the-beast.com/blob/icon.webp", "type": "square", "width": 512, "height": 512,
                          "mirrors": [
                            "https://cdn.feed-the-beast.com/blob/icon.webp",
                            "https://cdn.feed-the-beast.com/blob/icon-mirror.webp",
                            "http://cdn.feed-the-beast.com/blob/unsafe.webp",
                            "https://localhost/private.webp"
                          ] },
                        { "url": "https://cdn.feed-the-beast.com/blob/splash.webp", "type": "splash", "width": 1920, "height": 1080,
                          "mirrors": [{ "url": "https://mirror.example.test/splash.webp" }] },
                        { "url": "https://unapproved.example.test/screenshot.png", "type": "screenshot", "width": 1920, "height": 1080 }
                      ],
                      "versions": [
                        { "id": 100, "name": "1.0.0", "type": "release", "updated": 10,
                          "targets": [
                            { "type": "game", "name": "minecraft", "version": "1.21.1" },
                            { "type": "modloader", "name": "neoforge", "version": "21.1.1" },
                            { "type": "runtime", "name": "java", "version": "21.0.9" }
                          ] },
                        { "id": 102, "name": "1.1.1-bad", "type": "archived", "targets": [] },
                        { "id": 101, "name": "1.1.0", "type": "release", "targets": [] }
                      ]
                    }
                    """),
                "/v1/modpacks/public/modpack/129" => JsonResponse(
                    """
                    {
                      "status": "success", "id": 129,
                      "name": "FTB Skies 2", "slug": "ftb-skies-2", "private": false,
                      "versions": [{ "id": 200, "name": "2.0.0", "type": "release", "targets": [] }]
                    }
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }));
        var provider = new FtbCatalogProvider(client, "Muhun-MCSV-Manager.Tests/1.0");

        var result = await provider.SearchAsync("天空 pack");

        Assert.Equal([134, 129], result.Packs.Select(pack => pack.Id));
        var aero = result.Packs[0];
        Assert.Equal("FTB 天空：Aero", aero.Name);
        Assert.Equal("天空生存模組包", aero.Synopsis);
        Assert.Equal(456789, aero.InstallCount);
        Assert.Equal("https://cdn.feed-the-beast.com/blob/icon.webp", aero.IconUri?.AbsoluteUri);
        Assert.Equal("https://cdn.feed-the-beast.com/blob/splash.webp", aero.PreviewImageUri?.AbsoluteUri);
        Assert.Equal(3, aero.Artwork?.Count);
        Assert.Equal(
            [
                "https://cdn.feed-the-beast.com/blob/icon.webp",
                "https://cdn.feed-the-beast.com/blob/icon-mirror.webp",
                "https://cdn.feed-the-beast.com/blob/splash.webp",
                "https://mirror.example.test/splash.webp",
                "https://unapproved.example.test/screenshot.png"
            ],
            aero.IconUriCandidates.Select(static uri => uri.AbsoluteUri));
        Assert.Equal(
            [
                "https://cdn.feed-the-beast.com/blob/splash.webp",
                "https://mirror.example.test/splash.webp",
                "https://unapproved.example.test/screenshot.png",
                "https://cdn.feed-the-beast.com/blob/icon.webp",
                "https://cdn.feed-the-beast.com/blob/icon-mirror.webp"
            ],
            aero.PreviewImageUriCandidates.Select(static uri => uri.AbsoluteUri));
        Assert.Equal([102, 101, 100], aero.Versions.Select(version => version.Id));
        Assert.Equal(101, aero.LatestRelease?.Id);
        var firstRelease = aero.Versions.Single(version => version.Id == 100);
        Assert.Equal("1.21.1", firstRelease.MinecraftVersion);
        Assert.Equal("neoforge", firstRelease.ModLoaderName);
        Assert.Equal("21.1.1", firstRelease.ModLoaderVersion);
        Assert.Equal("21.0.9", firstRelease.JavaVersion);
        Assert.DoesNotContain(requests, uri => uri.AbsolutePath.EndsWith("/1611302", StringComparison.Ordinal));
        var search = Assert.Single(requests, uri => uri.AbsolutePath.EndsWith("/search/8", StringComparison.Ordinal));
        Assert.Contains("term=%E5%A4%A9%E7%A9%BA%20pack", search.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPackAsync_RejectsMismatchedResponseId()
    {
        using var client = new HttpClient(new FtbCatalogStubHandler(_ => JsonResponse(
            """{ "status": "success", "id": 999, "name": "wrong", "versions": [] }""")));
        var provider = new FtbCatalogProvider(client, "Muhun-MCSV-Manager.Tests/1.0");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetPackAsync(134));

        Assert.Contains("ID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_RejectsRedirectToUnapprovedHost()
    {
        using var client = new HttpClient(new FtbCatalogStubHandler(_ =>
        {
            var response = JsonResponse("""{ "status": "success", "packs": [] }""");
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://attacker.example/ftb-results");
            return response;
        }));
        var provider = new FtbCatalogProvider(client, "Muhun-MCSV-Manager.Tests/1.0");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => provider.SearchAsync("Aero"));

        Assert.Contains("未核准", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_WhenApiRoundsResultCountUp_HydratesOnlyRequestedLimit()
    {
        var requests = new ConcurrentBag<Uri>();
        using var client = new HttpClient(new FtbCatalogStubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/modpacks/public/modpack/search/2" => JsonResponse(
                    """{ "status": "success", "packs": [1, 2, 3, 4] }"""),
                "/v1/modpacks/public/modpack/1" => JsonResponse(
                    """{ "status": "success", "id": 1, "name": "One", "private": false, "versions": [] }"""),
                "/v1/modpacks/public/modpack/2" => JsonResponse(
                    """{ "status": "success", "id": 2, "name": "Two", "private": false, "versions": [] }"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var provider = new FtbCatalogProvider(client, "Muhun-MCSV-Manager.Tests/1.0");

        var result = await provider.SearchAsync("pack", limit: 2);

        Assert.Equal([1, 2], result.Packs.Select(pack => pack.Id));
        Assert.Equal(3, requests.Count);
        Assert.DoesNotContain(requests, uri => uri.AbsolutePath.EndsWith("/3", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, uri => uri.AbsolutePath.EndsWith("/4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFeaturedAsync_HydratesBoundedOfficialFeaturedIds()
    {
        var requests = new ConcurrentBag<Uri>();
        using var client = new HttpClient(new FtbCatalogStubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/modpacks/public/modpack/featured/12" => JsonResponse(
                    """{ "status": "success", "packs": [134, 127, 134] }"""),
                "/v1/modpacks/public/modpack/134" => JsonResponse(
                    """{ "status": "success", "id": 134, "name": "FTB Skies 2: Aero", "private": false, "versions": [] }"""),
                "/v1/modpacks/public/modpack/127" => JsonResponse(
                    """{ "status": "success", "id": 127, "name": "Architect's Exodus", "private": false, "versions": [] }"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var provider = new FtbCatalogProvider(client, "Muhun-MCSV-Manager.Tests/1.0");

        var result = await provider.GetFeaturedAsync();

        Assert.Equal([134, 127], result.Packs.Select(pack => pack.Id));
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task GetVersionManifestAsync_ParsesPublicClientFilesAndPrefersFtbMirror()
    {
        const string emptySha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
        const string emptySha256 =
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        const string emptySha512 =
            "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce" +
            "47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e";
        using var client = new HttpClient(new FtbCatalogStubHandler(request =>
        {
            Assert.Equal(
                "/v1/modpacks/public/modpack/130/100140",
                request.RequestUri!.AbsolutePath);
            return JsonResponse(
                $$"""
                {
                  "status": "success", "parent": 130, "id": 100140,
                  "name": "Stable", "type": "release", "private": false,
                  "targets": [
                    { "type": "game", "name": "minecraft", "version": "1.21.1" },
                    { "type": "modloader", "name": "neoforge", "version": "21.1.209" },
                    { "type": "runtime", "name": "java", "version": "21.0.4+7-LTS" }
                  ],
                  "specs": { "minimum": 5120, "recommended": 6144 },
                  "files": [{
                    "id": 42, "path": "./mods", "name": "placeholder.jar", "size": 0,
                    "url": "https://edge.forgecdn.net/files/1/2/placeholder.jar",
                    "mirrors": ["https://files.feed-the-beast.com/blob/abc"],
                    "clientonly": true, "serveronly": false, "optional": true, "type": "mod",
                    "sha1": "{{emptySha1}}",
                    "hashes": {
                      "sha1": "{{emptySha1}}", "sha256": "{{emptySha256}}", "sha512": "{{emptySha512}}"
                    }
                  }]
                }
                """);
        }));
        var provider = new FtbCatalogProvider(client, "Muhun-MCSV-Manager.Tests/1.0");

        var manifest = await provider.GetVersionManifestAsync(130, 100140);

        Assert.Equal("1.21.1", manifest.MinecraftVersion);
        Assert.Equal("neoforge", manifest.ModLoaderName);
        Assert.Equal("21.1.209", manifest.ModLoaderVersion);
        Assert.Equal("21.0.4+7-LTS", manifest.JavaVersion);
        Assert.Equal(new FtbPackMemorySpecs(5120, 6144), manifest.Memory);
        var file = Assert.Single(manifest.Files);
        Assert.Equal("mods/placeholder.jar", file.Path);
        Assert.Equal(0, file.Size);
        Assert.True(file.ClientOnly);
        Assert.True(file.Optional);
        Assert.False(file.ServerOnly);
        Assert.Equal(
            "https://files.feed-the-beast.com/blob/abc",
            file.PreferredDownloadUris[0].AbsoluteUri);
        Assert.Equal(
            "https://edge.forgecdn.net/files/1/2/placeholder.jar",
            file.PreferredDownloadUris[1].AbsoluteUri);
    }

    [Theory]
    [InlineData("../mods", "evil.jar")]
    [InlineData("/mods", "evil.jar")]
    [InlineData("mods//nested", "evil.jar")]
    [InlineData("mods", "../evil.jar")]
    [InlineData("mods", "CON")]
    public void NormalizeManifestDestination_RejectsTraversalRootingAndWindowsAliases(
        string path,
        string name)
    {
        Assert.Throws<InvalidDataException>(() =>
            FtbCatalogProvider.NormalizeManifestDestination(path, name));
    }

    [Fact]
    public void OfficialFileUri_RejectsHttpCredentialsPortsAndUnknownHosts()
    {
        Assert.True(FtbCatalogProvider.IsOfficialFileUri(
            new Uri("https://files.feed-the-beast.com/blob/good")));
        Assert.False(FtbCatalogProvider.IsOfficialFileUri(
            new Uri("http://files.feed-the-beast.com/blob/no")));
        Assert.False(FtbCatalogProvider.IsOfficialFileUri(
            new Uri("https://user@files.feed-the-beast.com/blob/no")));
        Assert.False(FtbCatalogProvider.IsOfficialFileUri(
            new Uri("https://files.feed-the-beast.com:444/blob/no")));
        Assert.False(FtbCatalogProvider.IsOfficialFileUri(
            new Uri("https://attacker.example/blob/no")));
    }

    [Fact]
    public async Task GetVersionManifestAsync_DeduplicatesSameContentCaseOnlyWindowsAlias()
    {
        using var client = new HttpClient(new FtbCatalogStubHandler(_ =>
            JsonResponse(BuildCaseAliasManifestJson(secondSha256: null))));
        var provider = new FtbCatalogProvider(client, "Muhun-MCSV-Manager.Tests/1.0");

        var manifest = await provider.GetVersionManifestAsync(130, 100140);

        var file = Assert.Single(manifest.Files);
        Assert.Equal("config/Obscuria/settings.json", file.Path);
    }

    [Fact]
    public async Task GetVersionManifestAsync_RejectsDifferentContentCaseOnlyWindowsAlias()
    {
        using var client = new HttpClient(new FtbCatalogStubHandler(_ =>
            JsonResponse(BuildCaseAliasManifestJson(new string('0', 64)))));
        var provider = new FtbCatalogProvider(client, "Muhun-MCSV-Manager.Tests/1.0");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetVersionManifestAsync(130, 100140));

        Assert.Contains("conflicting destination", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetVersionManifestAsync_AcceptsCurrentFeaturedPackFileCountAboveTenThousand()
    {
        const int currentFeaturedPackFileCount = 11_332;
        using var client = new HttpClient(new FtbCatalogStubHandler(_ =>
            JsonResponse(BuildManifestJson(currentFeaturedPackFileCount, validEntries: true))));
        var provider = new FtbCatalogProvider(client, "Muhun-MCSV-Manager.Tests/1.0");

        var manifest = await provider.GetVersionManifestAsync(130, 100140);

        Assert.Equal(currentFeaturedPackFileCount, manifest.Files.Count);
    }

    [Fact]
    public async Task GetVersionManifestAsync_RejectsMoreThanTwentyThousandFiles()
    {
        using var client = new HttpClient(new FtbCatalogStubHandler(_ =>
            JsonResponse(BuildManifestJson(20_001, validEntries: false))));
        var provider = new FtbCatalogProvider(client, "Muhun-MCSV-Manager.Tests/1.0");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetVersionManifestAsync(130, 100140));

        Assert.Contains("20000", error.Message, StringComparison.Ordinal);
    }

    private static string BuildManifestJson(int fileCount, bool validEntries)
    {
        var json = new StringBuilder(
            "{\"status\":\"success\",\"parent\":130,\"id\":100140," +
            "\"name\":\"Large stable\",\"type\":\"release\",\"private\":false," +
            "\"targets\":[{\"type\":\"game\",\"name\":\"minecraft\",\"version\":\"1.21.1\"}]," +
            "\"specs\":{\"minimum\":512,\"recommended\":1024},\"files\":[");
        for (var index = 0; index < fileCount; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            if (!validEntries)
            {
                json.Append("{}");
                continue;
            }

            json.Append(
                "{\"id\":" + index + ",\"path\":\"config\",\"name\":\"file-" + index +
                ".txt\",\"size\":0,\"url\":\"https://files.feed-the-beast.com/blob/empty\"," +
                "\"mirrors\":[],\"clientonly\":false,\"serveronly\":false,\"optional\":false," +
                "\"type\":\"config\",\"sha1\":\"da39a3ee5e6b4b0d3255bfef95601890afd80709\"," +
                "\"hashes\":{\"sha1\":\"da39a3ee5e6b4b0d3255bfef95601890afd80709\"," +
                "\"sha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\"," +
                "\"sha512\":\"cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce" +
                "47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e\"}}");
        }

        return json.Append("]}").ToString();
    }

    private static string BuildCaseAliasManifestJson(string? secondSha256)
    {
        const string emptySha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
        const string emptySha256 =
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        const string emptySha512 =
            "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce" +
            "47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e";
        secondSha256 ??= emptySha256;
        return $$"""
                 {
                   "status":"success", "parent":130, "id":100140,
                   "name":"Case aliases", "type":"release", "private":false,
                   "targets":[{"type":"game","name":"minecraft","version":"1.21.1"}],
                   "specs":{"minimum":512,"recommended":1024},
                   "files":[
                     {
                       "id":1, "path":"config/Obscuria", "name":"settings.json", "size":0,
                       "url":"https://files.feed-the-beast.com/blob/one", "mirrors":[],
                       "clientonly":true, "serveronly":false, "optional":false, "type":"config",
                       "sha1":"{{emptySha1}}",
                       "hashes":{"sha1":"{{emptySha1}}","sha256":"{{emptySha256}}","sha512":"{{emptySha512}}"}
                     },
                     {
                       "id":2, "path":"config/obscuria", "name":"settings.json", "size":0,
                       "url":"https://files.feed-the-beast.com/blob/two", "mirrors":[],
                       "clientonly":true, "serveronly":false, "optional":false, "type":"config",
                       "sha1":"{{emptySha1}}",
                       "hashes":{"sha1":"{{emptySha1}}","sha256":"{{secondSha256}}","sha512":"{{emptySha512}}"}
                     }
                   ]
                 }
                 """;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class FtbCatalogStubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
