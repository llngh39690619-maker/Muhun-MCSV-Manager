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
                "/v1/modpacks/modpack/search/8" => JsonResponse(
                    """
                    {
                      "status": "success",
                      "packs": [134, 129, 134],
                      "curseforge": [1611302, 999999],
                      "count": 4
                    }
                    """),
                "/v1/modpacks/modpack/134" => JsonResponse(
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
                "/v1/modpacks/modpack/129" => JsonResponse(
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
                "/v1/modpacks/modpack/search/2" => JsonResponse(
                    """{ "status": "success", "packs": [1, 2, 3, 4] }"""),
                "/v1/modpacks/modpack/1" => JsonResponse(
                    """{ "status": "success", "id": 1, "name": "One", "private": false, "versions": [] }"""),
                "/v1/modpacks/modpack/2" => JsonResponse(
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
                "/v1/modpacks/modpack/featured/12" => JsonResponse(
                    """{ "status": "success", "packs": [134, 127, 134] }"""),
                "/v1/modpacks/modpack/134" => JsonResponse(
                    """{ "status": "success", "id": 134, "name": "FTB Skies 2: Aero", "private": false, "versions": [] }"""),
                "/v1/modpacks/modpack/127" => JsonResponse(
                    """{ "status": "success", "id": 127, "name": "Architect's Exodus", "private": false, "versions": [] }"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var provider = new FtbCatalogProvider(client, "Muhun-MCSV-Manager.Tests/1.0");

        var result = await provider.GetFeaturedAsync();

        Assert.Equal([134, 127], result.Packs.Select(pack => pack.Id));
        Assert.Equal(3, requests.Count);
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
