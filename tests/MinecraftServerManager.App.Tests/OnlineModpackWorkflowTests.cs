using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class OnlineModpackWorkflowTests
{
    [Fact]
    public async Task InstallFtb_RejectsMissingMinecraftEulaConsentBeforeAnyDownload()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var workflow = new OnlineModpackWorkflow(new ApplicationPaths(directory.Path));
        var project = new OnlineModpackSearchResult(
            OnlineModpackProvider.Ftb,
            "134",
            "FTB Fixture",
            "Fixture",
            "Tests");
        var version = new OnlineModpackVersion(
            OnlineModpackProvider.Ftb,
            "134",
            "100466",
            "FTB Fixture 1.0",
            "1.21.1",
            "NeoForge",
            "release",
            DateTimeOffset.UtcNow,
            HasOfficialServerPack: true);
        var request = new OnlineModpackInstallRequest(project, version, "FTB Fixture");

        await Assert.ThrowsAsync<MinecraftEulaAcceptanceRequiredException>(() =>
            workflow.InstallAsync(
                request,
                transientApiKey: null,
                new InlineProgress<OnlineModpackInstallProgress>(),
                CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task BrowseCurseForge_DeduplicatesModIdsAndAdvancesAcrossBoundedPages()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var searchIndexes = new List<int>();
        using var apiClient = new HttpClient(new CurseHttpHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/games" => JsonResponse(CurseGamesJson),
                "/v1/categories" => JsonResponse(CurseCategoriesJson),
                "/v1/mods/search" => CreateCurseSearchResponse(
                    RecordSearchIndex(request.RequestUri, searchIndexes),
                    ReadQueryInteger(request.RequestUri, "pageSize"),
                    ReadQueryInteger(request.RequestUri, "index") == 0
                        ? Enumerable.Range(1, 50)
                        : Enumerable.Range(26, 50),
                    totalCount: 100),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }));
        using var downloadClient = new HttpClient(new CurseHttpHandler(_ =>
            throw new Xunit.Sdk.XunitException("Catalogue browse must not call the CDN.")));
        var provider = new CurseForgeModpackProvider(
            apiClient,
            downloadClient,
            "MuhunMCSVManager.Tests/1.0");
        using var workflow = new OnlineModpackWorkflow(
            new ApplicationPaths(directory.Path),
            modrinthCatalog: null,
            modrinthInstaller: null,
            modrinthLoaderBootstrapper: null,
            modrinthJavaRuntimeResolver: null,
            curseForge: provider);
        using var apiKey = CreateSecureString("transient-curse-key");

        var results = await workflow.BrowseAsync(
            new OnlineModpackBrowseRequest(OnlineModpackProvider.CurseForge, Limit: 100),
            apiKey,
            CancellationToken.None);

        Assert.Equal([0, 50], searchIndexes);
        Assert.Equal(75, results.Count);
        Assert.Equal(75, results.Select(result => result.ProjectId).Distinct().Count());
        Assert.Equal("1", results[0].ProjectId);
        Assert.Equal("75", results[^1].ProjectId);
    }

    [Fact]
    public async Task BrowseCurseForge_OversizedProviderPageIsHardCappedAtRequestedLimit()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var searchCalls = 0;
        using var apiClient = new HttpClient(new CurseHttpHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/v1/games" => JsonResponse(CurseGamesJson),
                "/v1/categories" => JsonResponse(CurseCategoriesJson),
                "/v1/mods/search" => CreateCurseSearchResponse(
                    ReadQueryInteger(request.RequestUri, "index"),
                    ReadQueryInteger(request.RequestUri, "pageSize"),
                    Enumerable.Range(1, 80),
                    totalCount: 1000,
                    onCreate: () => searchCalls++),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }));
        using var downloadClient = new HttpClient(new CurseHttpHandler(_ =>
            throw new Xunit.Sdk.XunitException("Catalogue browse must not call the CDN.")));
        var provider = new CurseForgeModpackProvider(
            apiClient,
            downloadClient,
            "MuhunMCSVManager.Tests/1.0");
        using var workflow = new OnlineModpackWorkflow(
            new ApplicationPaths(directory.Path),
            modrinthCatalog: null,
            modrinthInstaller: null,
            modrinthLoaderBootstrapper: null,
            modrinthJavaRuntimeResolver: null,
            curseForge: provider);
        using var apiKey = CreateSecureString("transient-curse-key");

        var results = await workflow.BrowseAsync(
            new OnlineModpackBrowseRequest(OnlineModpackProvider.CurseForge, Limit: 60),
            apiKey,
            CancellationToken.None);

        Assert.Equal(60, results.Count);
        Assert.Equal(60, results.Select(result => result.ProjectId).Distinct().Count());
        Assert.Equal(1, searchCalls);
    }

    [Fact]
    public void ModrinthDownloadProgress_MapsActualAdaptiveConcurrencyToSecondLine()
    {
        var mapped = OnlineModpackWorkflow.MapModrinthPackProgress(new ModrinthModpackInstallProgress(
            "download-files",
            7,
            40,
            "mods/example.jar",
            EffectiveConcurrentDownloads: 8,
            UsesAdaptiveConcurrency: true));

        Assert.Equal(OnlineModpackInstallStage.Downloading, mapped.Stage);
        Assert.Equal("自動 8 線｜已完成 7 / 40 個檔案", mapped.Detail);
        Assert.DoesNotContain("12 線", mapped.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallModrinth_VanillaBootstrapsDetectsThenCommits()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var fixture = CreateFixture(directory.Path, ModrinthModpackLoaderKind.Vanilla);
        var progress = new InlineProgress<OnlineModpackInstallProgress>();

        var instance = await fixture.Workflow.InstallAsync(
            CreateRequest("Vanilla 測試伺服器"),
            transientApiKey: null,
            progress,
            CancellationToken.None);

        Assert.Equal("Vanilla 測試伺服器", instance.Name);
        Assert.Equal(ServerLaunchKind.ExecutableJar, instance.LaunchKind);
        Assert.Equal(CoreType.Vanilla, instance.CoreType);
        Assert.Equal("1.20.1", instance.MinecraftVersion);
        Assert.Equal(ModpackSourceKind.Modrinth, instance.ModpackSource);
        Assert.Equal("project-id", instance.ModpackProjectId);
        Assert.Equal("version-id", instance.ModpackVersionId);
        Assert.Equal("1.0.0", instance.ModpackVersionName);
        Assert.Equal(17, instance.JavaMajorVersion);
        Assert.Equal(fixture.JavaResolver.JavaPath, instance.JavaExecutablePath);
        Assert.True(File.Exists(instance.ServerJarPath));
        Assert.True(SafePath.IsWithinRoot(fixture.Paths.Servers, instance.DirectoryPath));
        Assert.Equal(
            "installed",
            await File.ReadAllTextAsync(Path.Combine(instance.DirectoryPath, "config", "fixture.txt")));
        Assert.True(new JarCoreDetector().Detect(instance.ServerJarPath).IsValidJar);
        Assert.Equal(1, fixture.ApiHandler.CallCount);
        Assert.Equal(1, fixture.PackTransport.CallCount);
        Assert.Equal(1, fixture.Artifacts.VanillaDownloadCalls);
        Assert.Equal(0, fixture.ProcessRunner.CallCount);
        Assert.Equal([17], fixture.JavaResolver.RequestedMajors);
        Assert.Contains(progress.Values, value => value.Stage == OnlineModpackInstallStage.InstallingLoader);
        Assert.Contains(progress.Values, value => value.Stage == OnlineModpackInstallStage.DetectingServer);
        Assert.Contains(progress.Values, value => value.Stage == OnlineModpackInstallStage.Finalizing);
        AssertNoTemporaryInstallEntries(fixture.Paths.Servers);
        Assert.Single(Directory.EnumerateDirectories(fixture.Paths.Servers));
    }

    [Fact]
    public async Task InstallModrinth_PersistsCatalogArtworkWithoutOverwritingUserIcon()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        Directory.CreateDirectory(paths.OnlineModpackArtworkCache);
        var cachedArtwork = Path.Combine(paths.OnlineModpackArtworkCache, "verified.png");
        await File.WriteAllBytesAsync(cachedArtwork, ValidOnePixelPng());
        var artworkCache = new RecordingArtworkCache(cachedArtwork);
        using var fixture = CreateFixture(
            directory.Path,
            ModrinthModpackLoaderKind.Vanilla,
            artworkCache: artworkCache);

        var instance = await fixture.Workflow.InstallAsync(
            CreateArtworkRequest("有目錄圖片的伺服器"),
            transientApiKey: null,
            new InlineProgress<OnlineModpackInstallProgress>(),
            CancellationToken.None);

        Assert.Equal("modrinth", instance.ModpackProviderId);
        Assert.Null(instance.IconImagePath);
        Assert.Equal(
            Path.Combine(instance.DirectoryPath, ".mcsv", "assets", "catalog-icon.png"),
            instance.CatalogIconImagePath);
        Assert.Equal(
            Path.Combine(instance.DirectoryPath, ".mcsv", "assets", "catalog-preview.png"),
            instance.CatalogPreviewImagePath);
        Assert.True(File.Exists(instance.CatalogIconImagePath));
        Assert.True(File.Exists(instance.CatalogPreviewImagePath));
        Assert.Equal(2, artworkCache.Requests.Count);
    }

    [Fact]
    public async Task InstallModrinth_QuiltThrowsClearlyAndDeletesOwnedStaging()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var fixture = CreateFixture(directory.Path, ModrinthModpackLoaderKind.Quilt);

        var exception = await Assert.ThrowsAsync<ModrinthLoaderUnsupportedException>(() =>
            fixture.Workflow.InstallAsync(
                CreateRequest("Quilt 不支援測試"),
                transientApiKey: null,
                new InlineProgress<OnlineModpackInstallProgress>(),
                CancellationToken.None));

        Assert.Equal(ModrinthModpackLoaderKind.Quilt, exception.Kind);
        Assert.Contains("Quilt", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            LocalizationService.Current.Get("online.workflow.quiltUnsupported"),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.ApiHandler.CallCount);
        Assert.Equal(1, fixture.PackTransport.CallCount);
        Assert.Equal(0, fixture.Artifacts.TotalCalls);
        Assert.Equal(0, fixture.ProcessRunner.CallCount);
        Assert.Empty(fixture.JavaResolver.RequestedMajors);
        AssertNoTemporaryInstallEntries(fixture.Paths.Servers);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.Servers));
    }

    [Fact]
    public async Task InstallModrinth_InvalidBootstrappedJarNeverUsesOverrideWrapperOrCommits()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var fixture = CreateFixture(
            directory.Path,
            ModrinthModpackLoaderKind.Vanilla,
            createValidJar: false,
            includeOverrideWrapper: true);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Workflow.InstallAsync(
                CreateRequest("不可提交的伺服器"),
                transientApiKey: null,
                new InlineProgress<OnlineModpackInstallProgress>(),
                CancellationToken.None));

        Assert.Contains("wrapper", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Artifacts.VanillaDownloadCalls);
        Assert.Equal([17], fixture.JavaResolver.RequestedMajors);
        AssertNoTemporaryInstallEntries(fixture.Paths.Servers);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.Servers));
    }

    [Fact]
    public async Task InstallModrinth_CancelImmediatelyAfterPromotion_RemovesFinalTree()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var cancellation = new CancellationTokenSource();
        string? promotedPath = null;
        using var fixture = CreateFixture(
            directory.Path,
            ModrinthModpackLoaderKind.Vanilla,
            afterStagingPromotedForTesting: path =>
            {
                promotedPath = path;
                cancellation.Cancel();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Workflow.InstallAsync(
                CreateRequest("取消後不可保留"),
                transientApiKey: null,
                new InlineProgress<OnlineModpackInstallProgress>(),
                cancellation.Token));

        Assert.NotNull(promotedPath);
        Assert.False(Directory.Exists(promotedPath));
        AssertNoTemporaryInstallEntries(fixture.Paths.Servers);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.Servers));
    }

    [Fact]
    public async Task ProductionWorkflow_CurseForgeRequiresTransientApiKeyBeforeNetworkUse()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var fixture = CreateCurseFixture(directory.Path, "forge-47.2.0");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Workflow.InstallAsync(
                CreateCurseRequest("Missing credential"),
                transientApiKey: null,
                new InlineProgress<OnlineModpackInstallProgress>(),
                CancellationToken.None));

        Assert.Contains("API Key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.ApiHandler.CallCount);
        Assert.Equal(0, fixture.CdnHandler.CallCount);
        Assert.Empty(fixture.JavaResolver.RequestedMajors);
        Assert.Empty(fixture.ProcessRunner.StartInfos);
        Assert.Equal(0, fixture.Artifacts.TotalCalls);
        Assert.True(
            !Directory.Exists(fixture.Paths.Servers)
            || !Directory.EnumerateFileSystemEntries(fixture.Paths.Servers).Any());
        Assert.True(
            !Directory.Exists(fixture.Paths.Cache)
            || !Directory.EnumerateFiles(fixture.Paths.Cache, "curseforge-*.zip").Any());
    }

    [Fact]
    public async Task ProductionWorkflow_CurseForgeTransientApiKeyInstallsVerifiedServerPack()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var fixture = CreateCurseFixture(directory.Path, "forge-47.2.0");
        using var apiKey = CreateSecureString("transient-curse-key");

        var instance = await fixture.Workflow.InstallAsync(
            CreateCurseRequest("CurseForge verified pack"),
            apiKey,
            new InlineProgress<OnlineModpackInstallProgress>(),
            CancellationToken.None);

        Assert.Equal("CurseForge verified pack", instance.Name);
        Assert.Equal("curseforge", instance.ModpackProviderId);
        Assert.Equal(ModpackSourceKind.CurseForge, instance.ModpackSource);
        Assert.Equal("100", instance.ModpackProjectId);
        Assert.Equal("200", instance.ModpackVersionId);
        Assert.True(SafePath.IsWithinRoot(fixture.Paths.Servers, instance.DirectoryPath));
        Assert.True(File.Exists(Path.Combine(instance.DirectoryPath, "mods", "fixture-mod.jar")));
        Assert.True(fixture.ApiHandler.CallCount >= 3);
        Assert.True(fixture.CdnHandler.CallCount >= 1);
        AssertNoTemporaryInstallEntries(fixture.Paths.Servers);
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.Cache, "curseforge-*.zip"));
    }

    private static WorkflowFixture CreateFixture(
        string root,
        ModrinthModpackLoaderKind loaderKind,
        bool createValidJar = true,
        bool includeOverrideWrapper = false,
        Action<string>? afterStagingPromotedForTesting = null,
        IOnlineModpackArtworkCache? artworkCache = null)
    {
        var paths = new ApplicationPaths(root);
        var mrpack = CreateMrpack(loaderKind, includeOverrideWrapper);
        var packUri = new Uri(
            "https://cdn.modrinth.com/data/project-id/versions/version-id/fixture.mrpack");
        var apiJson = CreateVersionJson(loaderKind, mrpack, packUri);
        var apiHandler = new StubHttpHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.modrinth.com/v2/version/version-id", request.RequestUri?.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(apiJson, Encoding.UTF8, "application/json")
            };
        });
        var apiClient = new HttpClient(apiHandler);
        var catalog = new ModrinthModpackProvider(apiClient, "MuhunMCSVManager.Tests/1.0");
        var packTransport = new ByteTransport(packUri, mrpack);
        var installer = new ModrinthModpackInstaller(
            new ModrinthModpackArtifactDownloader(packTransport));
        var artifacts = new RecordingArtifactProvider(createValidJar);
        var processRunner = new UnexpectedProcessRunner();
        var bootstrapper = new ModrinthLoaderServerBootstrapper(artifacts, processRunner);
        var javaResolver = new RecordingJavaResolver(root);
        var workflow = new OnlineModpackWorkflow(
            paths,
            catalog,
            installer,
            bootstrapper,
            javaResolver,
            afterStagingPromotedForTesting: afterStagingPromotedForTesting,
            artworkCache: artworkCache);
        return new WorkflowFixture(
            paths,
            workflow,
            apiClient,
            apiHandler,
            packTransport,
            artifacts,
            processRunner,
            javaResolver);
    }

    private static CurseWorkflowFixture CreateCurseFixture(
        string root,
        string loaderId,
        string wrapperJarName = "ServerStart.jar")
    {
        var paths = new ApplicationPaths(root);
        var wrapperJar = CreateWrapperJar();
        var serverPack = CreateZip(
            (wrapperJarName, wrapperJar),
            ("install.bat", Encoding.UTF8.GetBytes(
                $"@echo off\r\njava -jar \"{wrapperJarName}\"\r\n")),
            ("mods/fixture-mod.jar", Encoding.UTF8.GetBytes("server-side pack mod")));
        var clientPack = CreateZip(
            ("manifest.json", Encoding.UTF8.GetBytes(CreateCurseManifest(loaderId))));
        var serverHash = Convert.ToHexString(SHA1.HashData(serverPack));
        var clientHash = Convert.ToHexString(SHA1.HashData(clientPack));
        var apiHandler = new CurseHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/100" => JsonResponse(CurseProjectJson()),
            "/v1/mods/100/files/200" => JsonResponse(CurseFileJson(
                200,
                isServerPack: false,
                serverPackFileId: 201,
                clientPack.LongLength,
                clientHash)),
            "/v1/mods/100/files/201" => JsonResponse(CurseFileJson(
                201,
                isServerPack: true,
                serverPackFileId: null,
                serverPack.LongLength,
                serverHash)),
            "/v1/mods/100/files/200/download-url" => JsonResponse(
                "{\"data\":\"https://cdn.example.test/client-pack.zip\"}"),
            "/v1/mods/100/files/201/download-url" => JsonResponse(
                "{\"data\":\"https://cdn.example.test/server-pack.zip\"}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var cdnHandler = new CurseHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/client-pack.zip" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(clientPack)
            },
            "/server-pack.zip" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(serverPack)
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var apiClient = new HttpClient(apiHandler);
        var cdnClient = new HttpClient(cdnHandler);
        var curseProvider = new CurseForgeModpackProvider(
            apiClient,
            cdnClient,
            "MuhunMCSVManager.Tests/1.0");
        var artifacts = new CurseForgeArtifactProvider();
        var processRunner = new CurseForgeProcessRunner();
        var bootstrapper = new ModrinthLoaderServerBootstrapper(artifacts, processRunner);
        var javaResolver = new RecordingJavaResolver(root);
        var workflow = new OnlineModpackWorkflow(
            paths,
            modrinthCatalog: null,
            modrinthInstaller: null,
            modrinthLoaderBootstrapper: bootstrapper,
            modrinthJavaRuntimeResolver: javaResolver,
            curseForge: curseProvider,
            curseManifestInspector: new CurseForgeModpackManifestInspector());
        return new CurseWorkflowFixture(
            paths,
            workflow,
            apiClient,
            cdnClient,
            apiHandler,
            cdnHandler,
            artifacts,
            processRunner,
            javaResolver);
    }

    private static OnlineModpackInstallRequest CreateCurseRequest(string serverName)
    {
        var project = new OnlineModpackSearchResult(
            OnlineModpackProvider.CurseForge,
            "100",
            "Curse Fixture",
            "Fixture",
            "Tests");
        var version = new OnlineModpackVersion(
            OnlineModpackProvider.CurseForge,
            "100",
            "200",
            "Curse Fixture 1.0",
            "1.20.1",
            "Forge",
            "release",
            new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero),
            HasOfficialServerPack: true);
        return new OnlineModpackInstallRequest(project, version, serverName);
    }

    private static SecureString CreateSecureString(string value)
    {
        var result = new SecureString();
        foreach (var character in value)
        {
            result.AppendChar(character);
        }

        result.MakeReadOnly();
        return result;
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage CreateCurseSearchResponse(
        int index,
        int pageSize,
        IEnumerable<int> modIds,
        int totalCount,
        Action? onCreate = null)
    {
        onCreate?.Invoke();
        var projects = modIds.Select(static modId => new
        {
            id = modId,
            gameId = 432,
            classId = 4471,
            slug = $"fixture-{modId}",
            name = $"Fixture {modId}",
            summary = "Fixture",
            authors = new[] { new { name = "Tests" } },
            links = new { },
            logo = (object?)null,
            screenshots = Array.Empty<object>(),
            downloadCount = modId,
            dateModified = "2026-08-16T00:00:00Z",
            isAvailable = true,
            allowModDistribution = true,
        }).ToArray();
        return JsonResponse(JsonSerializer.Serialize(new
        {
            data = projects,
            pagination = new
            {
                index,
                pageSize,
                resultCount = projects.Length,
                totalCount,
            },
        }));
    }

    private static int RecordSearchIndex(Uri uri, ICollection<int> indexes)
    {
        var index = ReadQueryInteger(uri, "index");
        indexes.Add(index);
        return index;
    }

    private static int ReadQueryInteger(Uri uri, string name)
    {
        foreach (var segment in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Split('=', 2);
            if (pair.Length == 2
                && Uri.UnescapeDataString(pair[0]).Equals(name, StringComparison.Ordinal))
            {
                return int.Parse(Uri.UnescapeDataString(pair[1]), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        throw new Xunit.Sdk.XunitException($"Missing query parameter: {name}");
    }

    private const string CurseGamesJson = """
        {
          "data": [{ "id": 432, "name": "Minecraft", "slug": "minecraft" }],
          "pagination": { "index": 0, "pageSize": 50, "resultCount": 1, "totalCount": 1 }
        }
        """;

    private const string CurseCategoriesJson = """
        {
          "data": [{ "id": 4471, "name": "Modpacks", "slug": "modpacks", "isClass": true }]
        }
        """;

    private static string CurseProjectJson()
        => """
           {"data":{"id":100,"gameId":432,"classId":4471,"slug":"fixture", "name":"Fixture",
           "summary":"Fixture","authors":[{"name":"Tests"}],"links":{},"logo":null,
           "downloadCount":1,"dateModified":"2026-08-16T00:00:00Z",
           "isAvailable":true,"allowModDistribution":true}}
           """;

    private static string CurseFileJson(
        int fileId,
        bool isServerPack,
        int? serverPackFileId,
        long length,
        string sha1)
        => JsonSerializer.Serialize(new
        {
            data = new
            {
                id = fileId,
                gameId = 432,
                modId = 100,
                isAvailable = true,
                displayName = $"File {fileId}",
                fileName = $"file-{fileId}.zip",
                releaseType = 1,
                fileStatus = 10,
                hashes = new[] { new { value = sha1, algo = 1 } },
                fileLength = length,
                fileDate = "2026-08-16T00:00:00Z",
                gameVersions = new[] { "1.20.1", "Forge" },
                isServerPack,
                serverPackFileId
            }
        });

    private static string CreateCurseManifest(string loaderId)
        => $$"""
           {"minecraft":{"version":"1.20.1","modLoaders":[{"id":"{{loaderId}}","primary":true}]},
           "manifestType":"minecraftModpack","manifestVersion":1,"name":"Fixture",
           "version":"1.0.0","author":"Tests","files":[],"overrides":"overrides"}
           """;

    private static byte[] CreateZip(params (string Path, byte[] Contents)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, contents) in entries)
            {
                WriteEntry(archive, path, contents);
            }
        }

        return output.ToArray();
    }

    private static byte[] CreateWrapperJar()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(
                archive,
                "META-INF/MANIFEST.MF",
                Encoding.UTF8.GetBytes(
                    "Manifest-Version: 1.0\r\nMain-Class: com.example.ServerStart\r\n\r\n"));
            WriteEntry(archive, "com/example/ServerStart.class", [0xCA, 0xFE, 0xBA, 0xBE]);
        }

        return output.ToArray();
    }

    private static OnlineModpackInstallRequest CreateRequest(string serverName)
    {
        var project = new OnlineModpackSearchResult(
            OnlineModpackProvider.Modrinth,
            "project-id",
            "Fixture Pack",
            "Fixture",
            "Tests");
        var version = new OnlineModpackVersion(
            OnlineModpackProvider.Modrinth,
            "project-id",
            "version-id",
            "Fixture 1.0.0",
            "1.20.1",
            "Vanilla",
            "release",
            new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero),
            HasOfficialServerPack: true);
        return new OnlineModpackInstallRequest(project, version, serverName);
    }

    private static OnlineModpackInstallRequest CreateArtworkRequest(string serverName)
    {
        var original = CreateRequest(serverName);
        return original with
        {
            Project = new OnlineModpackSearchResult(
                OnlineModpackProvider.Modrinth,
                original.Project.ProjectId,
                original.Project.Name,
                original.Project.Summary,
                original.Project.Authors,
                iconUri: new Uri("https://cdn.modrinth.com/data/project-id/icon.png"),
                previewImageUri: new Uri("https://cdn.modrinth.com/data/project-id/gallery.png"))
        };
    }

    private static byte[] ValidOnePixelPng()
        => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static byte[] CreateMrpack(
        ModrinthModpackLoaderKind loaderKind,
        bool includeOverrideWrapper = false)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["minecraft"] = "1.20.1"
        };
        if (loaderKind == ModrinthModpackLoaderKind.Quilt)
        {
            dependencies["quilt-loader"] = "0.26.4";
        }

        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 1,
            game = "minecraft",
            versionId = "fixture-pack-1",
            name = "Fixture Pack",
            summary = "Offline fixture",
            files = Array.Empty<object>(),
            dependencies
        });
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "modrinth.index.json", manifest);
            WriteEntry(archive, "server-overrides/config/fixture.txt", Encoding.UTF8.GetBytes("installed"));
            if (includeOverrideWrapper)
            {
                WriteEntry(archive, "server-overrides/ServerStart.jar", CreateWrapperJar());
            }
        }

        return output.ToArray();
    }

    private static string CreateVersionJson(
        ModrinthModpackLoaderKind loaderKind,
        byte[] mrpack,
        Uri packUri)
        => JsonSerializer.Serialize(new
        {
            project_id = "project-id",
            id = "version-id",
            name = "Fixture Pack 1",
            version_number = "1.0.0",
            version_type = "release",
            status = "listed",
            environment = "server_only",
            game_versions = new[] { "1.20.1" },
            loaders = new[]
            {
                loaderKind == ModrinthModpackLoaderKind.Quilt ? "quilt" : "vanilla"
            },
            date_published = "2026-08-16T00:00:00Z",
            files = new[]
            {
                new
                {
                    hashes = new
                    {
                        sha512 = Convert.ToHexString(SHA512.HashData(mrpack)).ToLowerInvariant(),
                        sha1 = Convert.ToHexString(SHA1.HashData(mrpack)).ToLowerInvariant()
                    },
                    url = packUri.AbsoluteUri,
                    filename = "fixture.mrpack",
                    primary = true,
                    size = mrpack.LongLength
                }
            }
        });

    private static byte[] CreateServerJar()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(
                archive,
                "META-INF/MANIFEST.MF",
                Encoding.UTF8.GetBytes(
                    "Manifest-Version: 1.0\r\n"
                    + "Main-Class: net.minecraft.server.Main\r\n"
                    + "Minecraft-Version: 1.20.1\r\n\r\n"));
            WriteEntry(archive, "net/minecraft/server/Main.class", [0xCA, 0xFE, 0xBA, 0xBE]);
            WriteEntry(archive, "version.json", Encoding.UTF8.GetBytes("{\"id\":\"1.20.1\"}"));
        }

        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(contents);
    }

    private static void AssertNoTemporaryInstallEntries(string serversRoot)
    {
        var names = Directory.EnumerateFileSystemEntries(serversRoot)
            .Select(Path.GetFileName)
            .ToArray();
        Assert.DoesNotContain(
            names,
            name => name?.StartsWith(".installing-", StringComparison.Ordinal) == true
                    || name?.StartsWith(".muhun-loader-", StringComparison.Ordinal) == true
                    || name?.StartsWith(".muhun-modrinth-", StringComparison.Ordinal) == true);
    }

    private sealed record CurseWorkflowFixture(
        ApplicationPaths Paths,
        OnlineModpackWorkflow Workflow,
        HttpClient ApiClient,
        HttpClient CdnClient,
        CurseHttpHandler ApiHandler,
        CurseHttpHandler CdnHandler,
        CurseForgeArtifactProvider Artifacts,
        CurseForgeProcessRunner ProcessRunner,
        RecordingJavaResolver JavaResolver) : IDisposable
    {
        public void Dispose()
        {
            Workflow.Dispose();
            ApiClient.Dispose();
            CdnClient.Dispose();
        }
    }

    private sealed record CurseRequestSnapshot(Uri Uri, IReadOnlyList<string> ApiKeys);

    private sealed class CurseHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public List<CurseRequestSnapshot> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            request.Headers.TryGetValues("x-api-key", out var keys);
            Requests.Add(new CurseRequestSnapshot(
                request.RequestUri!,
                keys?.ToArray() ?? []));
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class CurseForgeArtifactProvider : IModrinthOfficialLoaderArtifactProvider
    {
        public int ForgeDownloads { get; private set; }

        public int TotalCalls { get; private set; }

        public Task<ModrinthLoaderArtifact> DownloadVanillaServerAsync(
            string minecraftVersion,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
            => throw Unexpected();

        public Task VerifyVanillaServerAsync(
            string minecraftVersion,
            string serverJarPath,
            CancellationToken cancellationToken = default)
            => throw Unexpected();

        public Task<ModrinthLoaderArtifact> DownloadLatestStableFabricInstallerAsync(
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
            => throw Unexpected();

        public async Task<ModrinthLoaderArtifact> DownloadForgeInstallerAsync(
            string minecraftVersion,
            string loaderVersion,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            ForgeDownloads++;
            Assert.Equal("1.20.1", minecraftVersion);
            Assert.Equal("47.2.0", loaderVersion);
            var bytes = CreateWrapperJar();
            await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);
            progress?.Report(1d);
            return new ModrinthLoaderArtifact(
                ModrinthLoaderArtifactKind.ForgeInstaller,
                destinationPath,
                new Uri(
                    "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.2.0/"
                    + "forge-1.20.1-47.2.0-installer.jar"),
                bytes.LongLength,
                "SHA-256",
                Convert.ToHexString(SHA256.HashData(bytes)));
        }

        public Task<ModrinthLoaderArtifact> DownloadNeoForgeInstallerAsync(
            string loaderVersion,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
            => throw Unexpected();

        private Exception Unexpected()
        {
            TotalCalls++;
            return new Xunit.Sdk.XunitException("Unexpected official loader artifact request.");
        }
    }

    private sealed class CurseForgeProcessRunner : IModrinthLoaderBootstrapProcessRunner
    {
        public List<System.Diagnostics.ProcessStartInfo> StartInfos { get; } = [];

        public async Task<ModrinthLoaderBootstrapProcessResult> RunAsync(
            System.Diagnostics.ProcessStartInfo startInfo,
            IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartInfos.Add(startInfo);
            Assert.DoesNotContain("ServerStart.jar", startInfo.ArgumentList, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("install.bat", startInfo.ArgumentList, StringComparer.OrdinalIgnoreCase);
            var versionDirectory = Path.Combine(
                startInfo.WorkingDirectory,
                "libraries",
                "net",
                "minecraftforge",
                "forge",
                "1.20.1-47.2.0");
            Directory.CreateDirectory(versionDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(versionDirectory, "win_args.txt"),
                "--launchTarget forge_server\n--fml.mcVersion 1.20.1\n--fml.forgeVersion 47.2.0\n",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(versionDirectory, "unix_args.txt"),
                "--launchTarget forge_server\n--fml.mcVersion 1.20.1\n--fml.forgeVersion 47.2.0\n",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(startInfo.WorkingDirectory, "user_jvm_args.txt"),
                "-Xms1G\n-Xmx4G\n",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(startInfo.WorkingDirectory, "run.bat"),
                "@echo off\r\njava @user_jvm_args.txt @libraries/net/minecraftforge/forge/1.20.1-47.2.0/win_args.txt nogui\r\n",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(startInfo.WorkingDirectory, "run.sh"),
                "#!/bin/sh\njava @user_jvm_args.txt @libraries/net/minecraftforge/forge/1.20.1-47.2.0/unix_args.txt nogui\n",
                cancellationToken);
            output?.Report(new ModrinthLoaderBootstrapOutputLine(false, "installed official Forge fixture"));
            return new ModrinthLoaderBootstrapProcessResult(0, ["installed"], []);
        }
    }

    private sealed record WorkflowFixture(
        ApplicationPaths Paths,
        OnlineModpackWorkflow Workflow,
        HttpClient ApiClient,
        StubHttpHandler ApiHandler,
        ByteTransport PackTransport,
        RecordingArtifactProvider Artifacts,
        UnexpectedProcessRunner ProcessRunner,
        RecordingJavaResolver JavaResolver) : IDisposable
    {
        public void Dispose()
        {
            Workflow.Dispose();
            ApiClient.Dispose();
        }
    }

    private sealed class RecordingArtworkCache(string localPath) : IOnlineModpackArtworkCache
    {
        public List<Uri> Requests { get; } = [];

        public Task<string?> GetOrCacheAsync(
            OnlineModpackProvider provider,
            Uri? remoteUri,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(OnlineModpackProvider.Modrinth, provider);
            Requests.Add(Assert.IsType<Uri>(remoteUri));
            return Task.FromResult<string?>(localPath);
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class ByteTransport(Uri expectedUri, byte[] bytes) : IModrinthModpackHttpTransport
    {
        public int CallCount { get; private set; }

        public Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(expectedUri, uri);
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri),
                Content = new ByteArrayContent(bytes)
            });
        }
    }

    private sealed class RecordingArtifactProvider(bool createValidJar)
        : IModrinthOfficialLoaderArtifactProvider
    {
        public int VanillaDownloadCalls { get; private set; }

        public int TotalCalls { get; private set; }

        public async Task<ModrinthLoaderArtifact> DownloadVanillaServerAsync(
            string minecraftVersion,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            VanillaDownloadCalls++;
            Assert.Equal("1.20.1", minecraftVersion);
            var bytes = createValidJar ? CreateServerJar() : Encoding.UTF8.GetBytes("not-a-jar");
            await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);
            progress?.Report(1d);
            return new ModrinthLoaderArtifact(
                ModrinthLoaderArtifactKind.MinecraftServer,
                destinationPath,
                new Uri("https://piston-data.mojang.com/v1/objects/fixture/server.jar"),
                bytes.LongLength,
                "SHA-1",
                Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant());
        }

        public Task VerifyVanillaServerAsync(
            string minecraftVersion,
            string serverJarPath,
            CancellationToken cancellationToken = default)
            => throw Unexpected();

        public Task<ModrinthLoaderArtifact> DownloadLatestStableFabricInstallerAsync(
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
            => throw Unexpected();

        public Task<ModrinthLoaderArtifact> DownloadForgeInstallerAsync(
            string minecraftVersion,
            string loaderVersion,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
            => throw Unexpected();

        public Task<ModrinthLoaderArtifact> DownloadNeoForgeInstallerAsync(
            string loaderVersion,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
            => throw Unexpected();

        private Exception Unexpected()
        {
            TotalCalls++;
            return new Xunit.Sdk.XunitException("Unexpected loader artifact request.");
        }
    }

    private sealed class UnexpectedProcessRunner : IModrinthLoaderBootstrapProcessRunner
    {
        public int CallCount { get; private set; }

        public Task<ModrinthLoaderBootstrapProcessResult> RunAsync(
            System.Diagnostics.ProcessStartInfo startInfo,
            IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new Xunit.Sdk.XunitException("Vanilla bootstrap must not start a Java installer process.");
        }
    }

    private sealed class RecordingJavaResolver : IModrinthJavaRuntimeResolver
    {
        public RecordingJavaResolver(string root)
        {
            JavaPath = Path.Combine(root, "fake managed Java", "bin", "java.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(JavaPath)!);
            File.WriteAllBytes(JavaPath, [0x4D, 0x5A]);
        }

        public string JavaPath { get; }

        public List<int> RequestedMajors { get; } = [];

        public Task<string> ResolveAsync(
            int majorVersion,
            IProgress<double>? downloadProgress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedMajors.Add(majorVersion);
            downloadProgress?.Report(1d);
            return Task.FromResult(JavaPath);
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}
