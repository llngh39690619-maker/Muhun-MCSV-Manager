using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.App.Tests;

public sealed class CurseForgeLoaderResolutionTests
{
    [Fact]
    public void ExplicitFileLabelWinsOverConflictingIndexes()
    {
        var file = CreateFile(gameVersions: ["1.20.1", "Fabric Loader"]);
        var indexes = new[]
        {
            CreateIndex(file.FileId, "1.20.1", CurseForgeModLoaderType.Forge)
        };

        var loader = OnlineModpackWorkflow.ResolveCurseForgeLoader(file, indexes);

        Assert.Equal("Fabric", loader);
    }

    [Theory]
    [InlineData(CurseForgeModLoaderType.Forge, "Forge")]
    [InlineData(CurseForgeModLoaderType.Fabric, "Fabric")]
    [InlineData(CurseForgeModLoaderType.Quilt, "Quilt")]
    [InlineData(CurseForgeModLoaderType.NeoForge, "NeoForge")]
    public void ExactFileIndexMapsEverySupportedLoader(
        CurseForgeModLoaderType indexedLoader,
        string expected)
    {
        var file = CreateFile();
        var indexes = new[]
        {
            CreateIndex(file.FileId, "1.20.1", indexedLoader),
            CreateIndex(file.FileId + 1, "1.20.1", CurseForgeModLoaderType.Forge)
        };

        var loader = OnlineModpackWorkflow.ResolveCurseForgeLoader(file, indexes);

        Assert.Equal(expected, loader);
    }

    [Fact]
    public void UniqueGameVersionIndexResolvesRlcCraftWithoutHardCodingThePack()
    {
        var file = CreateFile(
            fileId: 4_612_979,
            displayName: "RLCraft 1.12.2 - Release v2.9.3.zip",
            gameVersions: ["1.12.2"]);
        var indexes = new[]
        {
            CreateIndex(file.FileId, "1.12.2", CurseForgeModLoaderType.Any),
            CreateIndex(4_286_138, "1.12.2", CurseForgeModLoaderType.Forge)
        };

        var loader = OnlineModpackWorkflow.ResolveCurseForgeLoader(file, indexes);

        Assert.Equal("Forge", loader);
    }

    [Fact]
    public void ConflictingGameVersionIndexesRemainUnknown()
    {
        var file = CreateFile();
        var indexes = new[]
        {
            CreateIndex(file.FileId + 1, "1.20.1", CurseForgeModLoaderType.Forge),
            CreateIndex(file.FileId + 2, "1.20.1", CurseForgeModLoaderType.Fabric)
        };

        var loader = OnlineModpackWorkflow.ResolveCurseForgeLoader(file, indexes);

        Assert.Equal(string.Empty, loader);
    }

    [Fact]
    public void ConflictingExactFileIndexesRemainUnknown()
    {
        var file = CreateFile();
        var indexes = new[]
        {
            CreateIndex(file.FileId, "1.20.1", CurseForgeModLoaderType.Forge),
            CreateIndex(file.FileId, "1.20.1", CurseForgeModLoaderType.Fabric)
        };

        var loader = OnlineModpackWorkflow.ResolveCurseForgeLoader(file, indexes);

        Assert.Equal(string.Empty, loader);
    }

    [Fact]
    public void MinecraftVersionAloneDoesNotGuessForge()
    {
        var file = CreateFile(gameVersions: ["1.12.2"]);

        var loader = OnlineModpackWorkflow.ResolveCurseForgeLoader(file, []);

        Assert.Equal(string.Empty, loader);
    }

    [Fact]
    public void UnsupportedExactLoaderDoesNotFallBackToAnotherProjectLoader()
    {
        var file = CreateFile();
        var indexes = new[]
        {
            CreateIndex(file.FileId, "1.20.1", CurseForgeModLoaderType.Cauldron),
            CreateIndex(file.FileId + 1, "1.20.1", CurseForgeModLoaderType.Forge)
        };

        var loader = OnlineModpackWorkflow.ResolveCurseForgeLoader(file, indexes);

        Assert.Equal(string.Empty, loader);
    }

    [Fact]
    public void UnknownFutureExactLoaderDoesNotFallBackToAnotherProjectLoader()
    {
        var file = CreateFile();
        var indexes = new[]
        {
            new CurseForgeFileIndex("1.20.1", file.FileId, "future.zip", 1, 1, null),
            CreateIndex(file.FileId + 1, "1.20.1", CurseForgeModLoaderType.Forge)
        };

        var loader = OnlineModpackWorkflow.ResolveCurseForgeLoader(file, indexes);

        Assert.Equal(string.Empty, loader);
    }

    [Fact]
    public async Task GetVersionsUsesProjectIndexesWhenFileLabelsOmitTheLoader()
    {
        const string projectJson = """
            {"data":{"id":285109,"gameId":432,"classId":4471,"slug":"rlcraft","name":"RLCraft",
            "summary":"Fixture","authors":[{"name":"Shivaxi"}],"links":{},"logo":null,
            "downloadCount":1,"dateModified":"2023-06-27T00:00:00Z","isAvailable":true,
            "allowModDistribution":true,"latestFilesIndexes":[
              {"gameVersion":"1.12.2","fileId":4612979,"filename":"RLCraft.zip","releaseType":1,
               "gameVersionTypeId":1,"modLoader":1}]}}
            """;
        const string filesJson = """
            {"data":[{"id":4612979,"gameId":432,"modId":285109,"isAvailable":true,
            "displayName":"RLCraft 1.12.2 - Release v2.9.3.zip","fileName":"RLCraft.zip",
            "releaseType":1,"fileStatus":4,"hashes":[],"fileLength":1,
            "fileDate":"2023-06-27T00:00:00Z","gameVersions":["1.12.2"],
            "isServerPack":false,"serverPackFileId":4612990}],
            "pagination":{"index":0,"pageSize":50,"resultCount":1,"totalCount":1}}
            """;
        using var apiClient = new HttpClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/v1/mods/285109" => JsonResponse(projectJson),
                "/v1/mods/285109/files" => JsonResponse(filesJson),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }));
        using var downloadClient = new HttpClient(new StubHandler(_ =>
            throw new Xunit.Sdk.XunitException("Version discovery must not call the CDN.")));
        var provider = new CurseForgeModpackProvider(
            apiClient,
            downloadClient,
            "MuhunMCSVManager.Tests/1.0");
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var workflow = new OnlineModpackWorkflow(
            new ApplicationPaths(directory.Path),
            modrinthCatalog: null,
            modrinthInstaller: null,
            modrinthLoaderBootstrapper: null,
            modrinthJavaRuntimeResolver: null,
            curseForge: provider);
        using var credential = CreateCredential("test-key");
        var project = new OnlineModpackSearchResult(
            OnlineModpackProvider.CurseForge,
            "285109",
            "RLCraft",
            "Fixture",
            "Shivaxi");

        var versions = await workflow.GetVersionsAsync(project, credential, CancellationToken.None);

        var version = Assert.Single(versions);
        Assert.Equal("1.12.2", version.MinecraftVersion);
        Assert.Equal("Forge", version.Loader);
    }

    private static CurseForgeModpackFile CreateFile(
        int fileId = 200,
        string displayName = "Example pack",
        IReadOnlyList<string>? gameVersions = null) =>
        new(
            fileId,
            432,
            100,
            displayName,
            "example.zip",
            IsAvailable: true,
            IsServerPack: false,
            ServerPackFileId: null,
            ReleaseType: 1,
            FileStatus: 4,
            FileLength: 1,
            FileDate: DateTimeOffset.UnixEpoch,
            gameVersions ?? ["1.20.1"],
            Hashes: []);

    private static CurseForgeFileIndex CreateIndex(
        int fileId,
        string gameVersion,
        CurseForgeModLoaderType loader) =>
        new(gameVersion, fileId, "example.zip", 1, 1, loader);

    private static SecureString CreateCredential(string value)
    {
        var credential = new SecureString();
        foreach (var character in value)
        {
            credential.AppendChar(character);
        }

        credential.MakeReadOnly();
        return credential;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
