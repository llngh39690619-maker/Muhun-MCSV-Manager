using System.Net;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class ModrinthModpackProviderTests
{
    [Fact]
    public async Task SearchBuildsServerFacetsAndParsesProjectsWithoutLiveNetwork()
    {
        var handler = new FixtureHttpHandler(_ => Json("""
        {"hits":[{"project_id":"p1","project_type":"modpack","slug":"pack","title":"Pack",
        "description":"D","author":"A","license":"MIT","versions":["1.20.1"],
        "categories":["fabric"],"environment":["server_only"],"downloads":42,
        "icon_url":"https://cdn.modrinth.com/data/p1/icon.png",
        "gallery":["https://cdn.modrinth.com/data/p1/images/preview.png"],
        "date_modified":"2026-01-02T03:04:05Z"}],"offset":0,"limit":10,"total_hits":1}
        """));
        using var client = new HttpClient(handler);
        var provider = new ModrinthModpackProvider(client, "Muhun-MCSV-Manager/0.2.5 (test@example.invalid)");

        var result = await provider.SearchAsync(new ModrinthModpackSearchRequest(
            "sky",
            "1.20.1",
            "Fabric",
            Limit: 10,
            Index: "updated",
            SourceCategory: "adventure"));

        var project = Assert.Single(result.Projects);
        Assert.Equal("p1", project.ProjectId);
        Assert.Equal(42, project.Downloads);
        Assert.Equal("https://cdn.modrinth.com/data/p1/icon.png", project.IconUri?.AbsoluteUri);
        Assert.Equal(
            "https://cdn.modrinth.com/data/p1/images/preview.png",
            Assert.Single(project.GalleryImageUris).AbsoluteUri);
        var query = Uri.UnescapeDataString(handler.Requests.Single().RequestUri!.Query);
        Assert.Contains("project_type:modpack", query, StringComparison.Ordinal);
        Assert.Contains("environment:server_only", query, StringComparison.Ordinal);
        Assert.Contains("versions:1.20.1", query, StringComparison.Ordinal);
        Assert.Contains("categories:fabric", query, StringComparison.Ordinal);
        Assert.Contains("categories:adventure", query, StringComparison.Ordinal);
        Assert.Contains("index=updated", query, StringComparison.Ordinal);
        Assert.True(client.DefaultRequestHeaders.UserAgent.Any());
    }

    [Fact]
    public async Task Search_RejectsFacetSyntaxInjectionBeforeNetworkRequest()
    {
        var handler = new FixtureHttpHandler(_ => throw new InvalidOperationException("不可發出請求。"));
        using var client = new HttpClient(handler);
        var provider = new ModrinthModpackProvider(client, "Muhun-Test/1.0");

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SearchAsync(
            new ModrinthModpackSearchRequest(SourceCategory: "adventure:versions:1.0")));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Search_GalleryCandidatesAreSafeDeduplicatedOrderedAndBounded()
    {
        var gallery = new List<string>
        {
            "http://cdn.modrinth.com/data/p/images/unsafe.png",
            "https://localhost/private.png",
            "https://cdn.modrinth.com/data/p/images/0.png",
            "https://cdn.modrinth.com/data/p/images/0.png"
        };
        gallery.AddRange(Enumerable.Range(1, 39)
            .Select(index => $"https://cdn.modrinth.com/data/p/images/{index}.png"));
        var handler = new FixtureHttpHandler(_ => Json($$"""
            {"hits":[{"project_id":"p","project_type":"modpack","slug":"pack","title":"Pack",
            "description":"D","author":"A","license":"MIT","versions":[],"categories":[],
            "environment":["server_only"],"downloads":1,"gallery":{{JsonSerializer.Serialize(gallery)}},
            "date_modified":"2026-01-02T03:04:05Z"}],"offset":0,"limit":20,"total_hits":1}
            """));
        using var client = new HttpClient(handler);
        var provider = new ModrinthModpackProvider(client, "Muhun-Test/1.0");

        var project = Assert.Single((await provider.SearchAsync(new())).Projects);

        Assert.Equal(ModrinthModpackProvider.MaximumGalleryImageUris, project.GalleryImageUris.Count);
        Assert.Equal(
            "https://cdn.modrinth.com/data/p/images/0.png",
            project.GalleryImageUris[0].AbsoluteUri);
        Assert.Equal(
            "https://cdn.modrinth.com/data/p/images/31.png",
            project.GalleryImageUris[^1].AbsoluteUri);
        Assert.Equal(
            project.GalleryImageUris.Count,
            project.GalleryImageUris.Select(static uri => uri.AbsoluteUri).Distinct().Count());
    }

    [Fact]
    public async Task VersionsFilterClientOnlyAndSelectOnlyPrimaryMrpack()
    {
        var sha = new string('a', 128);
        var responseJson = """
        [
          {"project_id":"p","id":"client","name":"Client","version_number":"1","version_type":"release",
           "status":"listed","environment":"client_only","game_versions":["1.20.1"],"loaders":["fabric"],
           "date_published":"2026-01-01T00:00:00Z","files":[]},
          {"project_id":"p","id":"server","name":"Server","version_number":"2","version_type":"release",
           "status":"listed","environment":"server_only","game_versions":["1.20.1"],"loaders":["fabric"],
           "date_published":"2026-01-02T00:00:00Z","files":[
             {"filename":"nulls.mrpack","url":"https://cdn.modrinth.com/nulls","size":null,"primary":true,"hashes":null},
             {"filename":"wrong.zip","url":"https://cdn.modrinth.com/wrong.zip","size":2,"primary":true,"hashes":{"sha512":"__SHA__"}},
             {"filename":"fallback.mrpack","url":"https://cdn.modrinth.com/fallback","size":3,"primary":false,"hashes":{"sha512":"__SHA__"}},
             {"filename":"chosen.mrpack","url":"https://cdn.modrinth.com/chosen","size":4,"primary":true,"hashes":{"sha512":"__SHA__"}}
           ]}
        ]
        """.Replace("__SHA__", sha, StringComparison.Ordinal);
        var handler = new FixtureHttpHandler(_ => Json(responseJson));
        using var client = new HttpClient(handler);
        var provider = new ModrinthModpackProvider(client, "Muhun-Test/1.0");

        var versions = await provider.GetVersionsAsync("p", "1.20.1", "fabric");

        var version = Assert.Single(versions);
        Assert.Equal("server", version.VersionId);
        Assert.Equal("chosen.mrpack", version.MrpackFile!.FileName);
        Assert.Equal(4, version.MrpackFile.Size);
    }

    [Fact]
    public async Task ApiRedirectIsRejectedEvenWhenTheFinalUriUsesTheOfficialHost()
    {
        var handler = new FixtureHttpHandler(_ =>
        {
            var response = Json("{}");
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.modrinth.com/v2/version/unexpected-redirect");
            return response;
        });
        using var client = new HttpClient(handler);
        var provider = new ModrinthModpackProvider(client, "Muhun-Test/1.0");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetVersionAsync("requested-version"));

        Assert.Contains("redirect", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApiResponseWithOversizedDeclaredLengthIsRejectedBeforeParsing()
    {
        var handler = new FixtureHttpHandler(_ =>
        {
            var response = Json("{}");
            response.Content.Headers.ContentLength = 16L * 1024 * 1024 + 1;
            return response;
        });
        using var client = new HttpClient(handler);
        var provider = new ModrinthModpackProvider(client, "Muhun-Test/1.0");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetVersionAsync("oversized"));
    }

    [Fact]
    public async Task ApiErrorBodyUsesTheSmallerBound()
    {
        var handler = new FixtureHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("error", Encoding.UTF8, "text/plain")
            };
            response.Content.Headers.ContentLength = 64L * 1024 + 1;
            return response;
        });
        using var client = new HttpClient(handler);
        var provider = new ModrinthModpackProvider(client, "Muhun-Test/1.0");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetVersionAsync("error"));
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
