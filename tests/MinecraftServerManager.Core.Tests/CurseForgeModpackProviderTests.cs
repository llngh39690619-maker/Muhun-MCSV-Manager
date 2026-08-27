using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class CurseForgeModpackProviderTests
{
    private const string ApiKey = "test-key-that-must-never-leak";
    private const string UserAgent = "MuhunMCSVManager.Tests/1.0";

    [Fact]
    public async Task Search_DynamicallyResolvesCatalog_EncodesQueryAndCapsPageSizeAtFifty()
    {
        var requests = new List<RequestSnapshot>();
        using var apiClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add(RequestSnapshot.Capture(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/games" => JsonResponse(GamesJson),
                "/v1/categories" => JsonResponse(CategoriesJson),
                "/v1/mods/search" => JsonResponse(SearchJson),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        using var downloadClient = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("搜尋不可呼叫 CDN。")));
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var page = await provider.SearchAsync(
            ApiKey,
            new CurseForgeModpackSearchRequest(
                Query: "FTB 天空 & 2",
                GameVersion: "1.21.1 beta",
                ModLoader: CurseForgeModLoaderType.NeoForge,
                Index: 25,
                PageSize: 999,
                SortField: CurseForgeModpackSortField.LastUpdated,
                CategoryId: 4484));

        Assert.Equal(432, page.Catalog.MinecraftGameId);
        Assert.Equal(4471, page.Catalog.ModpacksClassId);
        var project = Assert.Single(page.Projects);
        Assert.Equal(100, project.ModId);
        Assert.Equal("https://media.example.test/icon.png", project.IconUri?.AbsoluteUri);
        Assert.Equal("https://media.example.test/preview.jpg", project.PreviewImageUri?.AbsoluteUri);
        Assert.Equal(123, project.DownloadCount);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T00:00:00Z"), project.DateModified);
        var search = Assert.Single(requests, request => request.Path == "/v1/mods/search");
        Assert.Contains("searchFilter=FTB%20%E5%A4%A9%E7%A9%BA%20%26%202", search.OriginalUri, StringComparison.Ordinal);
        Assert.Contains("gameVersion=1.21.1%20beta", search.OriginalUri, StringComparison.Ordinal);
        Assert.Contains("modLoaderType=6", search.OriginalUri, StringComparison.Ordinal);
        Assert.Contains("categoryId=4484", search.OriginalUri, StringComparison.Ordinal);
        Assert.Contains("sortField=3", search.OriginalUri, StringComparison.Ordinal);
        Assert.Contains("sortOrder=desc", search.OriginalUri, StringComparison.Ordinal);
        Assert.Contains("pageSize=50", search.OriginalUri, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, search.OriginalUri, StringComparison.Ordinal);
        Assert.All(requests, request => Assert.Equal(ApiKey, Assert.Single(request.ApiKeys)));
        Assert.False(apiClient.DefaultRequestHeaders.Contains("x-api-key"));
        Assert.False(downloadClient.DefaultRequestHeaders.Contains("x-api-key"));
    }

    [Fact]
    public async Task Search_DoesNotPersistCatalogBetweenCalls()
    {
        var gamesCalls = 0;
        var categoriesCalls = 0;
        using var apiClient = new HttpClient(new StubHandler(request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/games":
                    gamesCalls++;
                    return JsonResponse(GamesJson);
                case "/v1/categories":
                    categoriesCalls++;
                    return JsonResponse(CategoriesJson);
                case "/v1/mods/search":
                    return JsonResponse(SearchJson);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        await provider.SearchAsync(ApiKey, new CurseForgeModpackSearchRequest());
        await provider.SearchAsync(ApiKey, new CurseForgeModpackSearchRequest());

        Assert.Equal(2, gamesCalls);
        Assert.Equal(2, categoriesCalls);
    }

    [Fact]
    public async Task GetFiles_UsesFiltersAndNeverRequestsMoreThanFifty()
    {
        RequestSnapshot? snapshot = null;
        using var apiClient = new HttpClient(new StubHandler(request =>
        {
            snapshot = RequestSnapshot.Capture(request);
            return JsonResponse($$"""
                {
                  "data": [{{FileObjectJson(200, false, 201)}}],
                  "pagination": { "index": 0, "pageSize": 50, "resultCount": 1, "totalCount": 1 }
                }
                """);
        }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var page = await provider.GetFilesAsync(
            ApiKey,
            100,
            "1.20.1 & test",
            CurseForgeModLoaderType.Forge,
            pageSize: 200);

        Assert.Single(page.Files);
        Assert.NotNull(snapshot);
        Assert.Contains("gameVersion=1.20.1%20%26%20test", snapshot.OriginalUri, StringComparison.Ordinal);
        Assert.Contains("modLoaderType=1", snapshot.OriginalUri, StringComparison.Ordinal);
        Assert.Contains("pageSize=50", snapshot.OriginalUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveServerPack_FollowsExplicitLinkAndRevalidatesProjectAndFileIdentity()
    {
        var paths = new List<string>();
        using var apiClient = new HttpClient(new StubHandler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/v1/mods/100" => JsonResponse(ProjectResponseJson()),
                "/v1/mods/100/files/200" => JsonResponse(FileResponseJson(200, false, 201)),
                "/v1/mods/100/files/201" => JsonResponse(FileResponseJson(201, true, null)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var result = await provider.ResolveServerPackAsync(ApiKey, 100, 200);

        Assert.True(result.IsAvailable);
        Assert.Equal(CurseForgeServerPackResolutionStatus.Available, result.Status);
        Assert.Equal(200, result.SelectedFile!.FileId);
        Assert.Equal(201, result.ServerPackFile!.FileId);
        Assert.Equal(
            ["/v1/mods/100", "/v1/mods/100/files/200", "/v1/mods/100/files/201"],
            paths);
    }

    [Fact]
    public async Task ResolveServerPack_AcceptsAFileExplicitlyMarkedAsServerPack()
    {
        using var apiClient = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/100" => JsonResponse(ProjectResponseJson()),
            "/v1/mods/100/files/201" => JsonResponse(FileResponseJson(201, true, null)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var result = await provider.ResolveServerPackAsync(ApiKey, 100, 201);

        Assert.Equal(CurseForgeServerPackResolutionStatus.Available, result.Status);
        Assert.Same(result.SelectedFile, result.ServerPackFile);
    }

    [Fact]
    public async Task ResolveServerPack_ReportsNoOfficialPackWithoutGuessingAnotherFile()
    {
        var calls = 0;
        using var apiClient = new HttpClient(new StubHandler(request =>
        {
            calls++;
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/mods/100" => JsonResponse(ProjectResponseJson()),
                "/v1/mods/100/files/200" => JsonResponse(FileResponseJson(200, false, null)),
                _ => throw new InvalidOperationException("Provider 不可猜測其他檔案或 CDN URL。")
            };
        }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var result = await provider.ResolveServerPackAsync(ApiKey, 100, 200);

        Assert.Equal(CurseForgeServerPackResolutionStatus.NoOfficialServerPack, result.Status);
        Assert.False(result.IsAvailable);
        Assert.Null(result.ServerPackFile);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ResolveServerPack_RejectsDistributionBeforeReadingFiles()
    {
        var calls = 0;
        using var apiClient = new HttpClient(new StubHandler(request =>
        {
            calls++;
            Assert.Equal("/v1/mods/100", request.RequestUri!.AbsolutePath);
            return JsonResponse(ProjectResponseJson(allowDistribution: false));
        }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var result = await provider.ResolveServerPackAsync(ApiKey, 100, 200);

        Assert.Equal(CurseForgeServerPackResolutionStatus.DistributionUnavailable, result.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ResolveServerPack_RejectsLinkedFileWhoseModIdDoesNotMatch()
    {
        using var apiClient = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/100" => JsonResponse(ProjectResponseJson()),
            "/v1/mods/100/files/200" => JsonResponse(FileResponseJson(200, false, 201)),
            "/v1/mods/100/files/201" => JsonResponse(FileResponseJson(201, true, null, modId: 999)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.ResolveServerPackAsync(ApiKey, 100, 200));

        Assert.Contains("modId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveServerPack_MapsMissingLinkedFileToExplicitUnavailableState()
    {
        using var apiClient = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/100" => JsonResponse(ProjectResponseJson()),
            "/v1/mods/100/files/200" => JsonResponse(FileResponseJson(200, false, 201)),
            "/v1/mods/100/files/201" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var result = await provider.ResolveServerPackAsync(ApiKey, 100, 200);

        Assert.Equal(CurseForgeServerPackResolutionStatus.OfficialServerPackUnavailable, result.Status);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ResolveServerPack_RevalidatesLinkedFileAvailabilityAndServerPackFlag(
        bool isAvailable,
        bool isServerPack)
    {
        using var apiClient = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/100" => JsonResponse(ProjectResponseJson()),
            "/v1/mods/100/files/200" => JsonResponse(FileResponseJson(200, false, 201)),
            "/v1/mods/100/files/201" => JsonResponse(
                FileResponseJson(201, isServerPack, null, isAvailable: isAvailable)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var result = await provider.ResolveServerPackAsync(ApiKey, 100, 200);

        Assert.Equal(CurseForgeServerPackResolutionStatus.OfficialServerPackUnavailable, result.Status);
        Assert.False(result.IsAvailable);
        Assert.Equal(201, result.ServerPackFile!.FileId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CurseForgeApiErrorCode.InvalidApiKey)]
    [InlineData(HttpStatusCode.Forbidden, CurseForgeApiErrorCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, CurseForgeApiErrorCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, CurseForgeApiErrorCode.RateLimited)]
    public async Task ApiErrors_AreMappedWithoutLeakingKeyOrResponseBody(
        HttpStatusCode statusCode,
        CurseForgeApiErrorCode expectedCode)
    {
        RequestSnapshot? snapshot = null;
        using var apiClient = new HttpClient(new StubHandler(request =>
        {
            snapshot = RequestSnapshot.Capture(request);
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent($"server echoed secret: {ApiKey}")
            };
            if (statusCode == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            }

            return response;
        }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var exception = await Assert.ThrowsAsync<CurseForgeApiException>(
            () => provider.GetProjectAsync(ApiKey, 100));

        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.Equal(statusCode, exception.StatusCode);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
        Assert.NotNull(snapshot);
        Assert.Equal(ApiKey, Assert.Single(snapshot.ApiKeys));
        Assert.DoesNotContain(ApiKey, snapshot.OriginalUri, StringComparison.Ordinal);
        Assert.False(apiClient.DefaultRequestHeaders.Contains("x-api-key"));
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            Assert.Equal(TimeSpan.FromSeconds(7), exception.RetryAfter);
        }
    }

    [Fact]
    public void Constructor_RejectsSharedClientsAndPreconfiguredDefaultApiKeys()
    {
        using var shared = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ArgumentException>(() => new CurseForgeModpackProvider(shared, shared, UserAgent));

        using var apiClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var downloadClient = EmptyDownloadClient();
        apiClient.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", ApiKey);
        var exception = Assert.Throws<InvalidOperationException>(
            () => new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent));
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDownloadUri_RejectsMissingOrNonHttpsUrlsInsteadOfGuessingCdnPath()
    {
        using var apiClient = new HttpClient(new StubHandler(_ => JsonResponse("{ \"data\": null }")));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.GetDownloadUriAsync(ApiKey, 100, 201));
    }

    [Fact]
    public async Task ApiRedirect_FailsClosedWithoutReadingBodyOrFollowingLocation()
    {
        var calls = 0;
        using var apiClient = new HttpClient(new StubHandler(request =>
        {
            calls++;
            Assert.Equal("api.curseforge.com", request.RequestUri!.Host);
            var response = new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Content = new ExplodingContent()
            };
            response.Headers.Location = new Uri("https://evil.example.test/steal-key");
            return response;
        }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var exception = await Assert.ThrowsAsync<CurseForgeApiException>(
            () => provider.GetProjectAsync(ApiKey, 100));

        Assert.Equal(CurseForgeApiErrorCode.ApiFailure, exception.ErrorCode);
        Assert.Equal(HttpStatusCode.Redirect, exception.StatusCode);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiTransportReportingChangedFinalUri_FailsClosedBeforeReadingBody()
    {
        using var apiClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://evil.example.test/redirected"),
                Content = new ExplodingContent()
            }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.GetProjectAsync(ApiKey, 100));

        Assert.Contains("final URI", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiJson_DeclaredOversize_IsRejectedBeforeBodyRead()
    {
        using var apiClient = new HttpClient(new StubHandler(_ =>
        {
            var content = new ExplodingContent();
            content.Headers.ContentLength = 8L * 1024 * 1024 + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.GetProjectAsync(ApiKey, 100));

        Assert.Contains("安全上限", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiJson_UnknownLengthStream_IsBoundedAfterDecompression()
    {
        var content = new ByteArrayContent(new byte[8 * 1024 * 1024 + 1]);
        content.Headers.ContentLength = null;
        using var apiClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var downloadClient = EmptyDownloadClient();
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.GetProjectAsync(ApiKey, 100));

        Assert.Contains("安全上限", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    internal static HttpClient EmptyDownloadClient()
        => new(new StubHandler(_ => throw new InvalidOperationException("CDN 不應被呼叫。")));

    internal static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    internal static string ProjectResponseJson(bool allowDistribution = true, bool isAvailable = true)
        => $$"""
           { "data": {{ProjectObjectJson(allowDistribution, isAvailable)}} }
           """;

    internal static string ProjectObjectJson(bool allowDistribution = true, bool isAvailable = true)
        => $$"""
           {
             "id": 100, "gameId": 432, "classId": 4471,
             "slug": "test-pack", "name": "Test Pack", "summary": "Server pack test",
             "authors": [{ "name": "Author" }],
             "links": { "websiteUrl": "https://www.curseforge.com/minecraft/modpacks/test-pack" },
             "logo": { "thumbnailUrl": "https://media.example.test/icon.png" },
             "screenshots": [{ "thumbnailUrl": "https://media.example.test/preview.jpg" }],
             "downloadCount": 123, "dateModified": "2026-08-16T00:00:00Z",
             "isAvailable": {{isAvailable.ToString().ToLowerInvariant()}},
             "allowModDistribution": {{allowDistribution.ToString().ToLowerInvariant()}}
           }
           """;

    internal static string FileResponseJson(
        int fileId,
        bool isServerPack,
        int? serverPackFileId,
        int modId = 100,
        string hashesJson = "[]",
        long fileLength = 10,
        bool isAvailable = true)
        => $$"""
           { "data": {{FileObjectJson(fileId, isServerPack, serverPackFileId, modId, hashesJson, fileLength, isAvailable)}} }
           """;

    internal static string FileObjectJson(
        int fileId,
        bool isServerPack,
        int? serverPackFileId,
        int modId = 100,
        string hashesJson = "[]",
        long fileLength = 10,
        bool isAvailable = true)
        => $$"""
           {
             "id": {{fileId}}, "gameId": 432, "modId": {{modId}},
             "isAvailable": {{isAvailable.ToString().ToLowerInvariant()}},
             "displayName": "File {{fileId}}", "fileName": "file-{{fileId}}.zip",
             "releaseType": 1, "fileStatus": 10,
             "hashes": {{hashesJson}}, "fileLength": {{fileLength}},
             "fileDate": "2026-08-16T00:00:00Z", "gameVersions": ["1.21.1", "NeoForge"],
             "isServerPack": {{isServerPack.ToString().ToLowerInvariant()}},
             "serverPackFileId": {{(serverPackFileId is null ? "null" : serverPackFileId.Value.ToString())}}
           }
           """;

    internal const string GamesJson = """
        {
          "data": [{ "id": 432, "name": "Minecraft", "slug": "minecraft" }],
          "pagination": { "index": 0, "pageSize": 50, "resultCount": 1, "totalCount": 1 }
        }
        """;

    internal const string CategoriesJson = """
        {
          "data": [
            { "id": 6, "name": "Mods", "slug": "mc-mods", "isClass": true },
            { "id": 4471, "name": "Modpacks", "slug": "modpacks", "isClass": true }
          ]
        }
        """;

    internal static readonly string SearchJson = $$"""
        {
          "data": [{{ProjectObjectJson()}}],
          "pagination": { "index": 25, "pageSize": 50, "resultCount": 1, "totalCount": 1 }
        }
        """;

    internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
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

    private sealed class ExplodingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new Xunit.Sdk.XunitException("API response body must not be read.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    internal sealed record RequestSnapshot(string Path, string OriginalUri, IReadOnlyList<string> ApiKeys)
    {
        public static RequestSnapshot Capture(HttpRequestMessage request)
        {
            request.Headers.TryGetValues("x-api-key", out var keys);
            return new RequestSnapshot(
                request.RequestUri!.AbsolutePath,
                request.RequestUri.OriginalString,
                keys?.ToArray() ?? []);
        }
    }
}
