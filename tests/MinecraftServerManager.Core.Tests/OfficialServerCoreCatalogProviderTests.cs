using System.Net;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class OfficialServerCoreCatalogProviderTests
{
    private const string UserAgent = "Muhun-MCSV-Manager.Tests/1.0 (tests@example.invalid)";

    [Fact]
    public void SupportedCores_AreTheSixFirstPartyCatalogs()
    {
        Assert.Collection(
            OfficialServerCoreCatalogProvider.SupportedCores,
            item => AssertDescriptor(item, CoreType.Paper, "Paper", false),
            item => AssertDescriptor(item, CoreType.Velocity, "Velocity", true),
            item => AssertDescriptor(item, CoreType.Vanilla, "Minecraft 原版", false),
            item => AssertDescriptor(item, CoreType.Fabric, "Fabric", false),
            item => AssertDescriptor(item, CoreType.Forge, "Forge", false),
            item => AssertDescriptor(item, CoreType.NeoForge, "NeoForge", false));
    }

    [Fact]
    public void Constructor_RejectsHeaderInjectionInUserAgent()
    {
        using var client = new HttpClient(new StubHandler(_ => JsonResponse("{}")));

        Assert.Throws<ArgumentException>(() =>
            new OfficialServerCoreCatalogProvider(client, "Product/1.0\r\nX-Evil: true"));
    }

    [Fact]
    public async Task Paper_DiscoveryOmitsUnstableOutOfRangeAndBuildlessVersions_AndCachesResults()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            Interlocked.Increment(ref requestCount);
            Assert.Equal(UserAgent, request.Headers.UserAgent.ToString());
            return request.RequestUri!.AbsoluteUri switch
            {
                "https://fill.papermc.io/v3/projects/paper" => JsonResponse("""
                    {
                      "versions": {
                        "26": ["26.3", "26.2"],
                        "1.21": ["1.21.10", "1.21.9-rc1"],
                        "1.0": ["1.0"]
                      }
                    }
                    """),
                "https://fill.papermc.io/v3/projects/paper/versions/26.2/builds?channel=STABLE" =>
                    JsonResponse(FillBuildJson("paper", "26.2", 112, 'a')),
                "https://fill.papermc.io/v3/projects/paper/versions/1.21.10/builds?channel=STABLE" =>
                    JsonResponse("[]"),
                "https://fill.papermc.io/v3/projects/paper/versions/1.0/builds?channel=STABLE" =>
                    JsonResponse("[]"),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        }));
        var provider = CreateProvider(client);

        var versions = await provider.GetVersionsAsync(CoreType.Paper);
        var repeated = await provider.GetVersionsAsync(CoreType.Paper);
        var builds = await provider.GetBuildsAsync(CoreType.Paper, "26.2");

        var version = Assert.Single(versions);
        Assert.Same(versions, repeated);
        Assert.Equal("26.2", version.MinecraftVersion);
        Assert.Equal(25, version.JavaMajorVersion);
        var build = Assert.Single(builds);
        Assert.Equal("112", build.BuildVersion);
        Assert.Equal(OfficialServerInstallStrategy.DirectServerJar, build.InstallStrategy);
        Assert.Equal("SHA-256", build.HashAlgorithm);
        Assert.Equal(new string('a', 64), build.Hash);
        Assert.Equal(4, requestCount);
    }

    [Fact]
    public async Task Velocity_UsesProductVersionsStableBuildsAndArtifactSpecificJavaLevels()
    {
        var productVersions = new[] { "4.0.0", "3.5.0", "3.4.0", "3.1.1", "3.1.0", "1.1.9", "1.0.10" };
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v3/projects/velocity")
            {
                return JsonResponse("""
                    {
                      "versions": {
                        "4": ["4.0.0", "4.1.0-SNAPSHOT"],
                        "3": ["3.5.0", "3.4.0", "3.1.1", "3.1.0"],
                        "1": ["1.1.9", "1.0.10"]
                      }
                    }
                    """);
            }

            foreach (var productVersion in productVersions)
            {
                if (request.RequestUri.AbsoluteUri.Equals(
                    $"https://fill.papermc.io/v3/projects/velocity/versions/{productVersion}/builds?channel=STABLE",
                    StringComparison.Ordinal))
                {
                    return productVersion == "3.5.0"
                        ? JsonResponse("[]")
                        : JsonResponse(FillBuildJson("velocity", productVersion, 7, 'b'));
                }
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }));
        var provider = CreateProvider(client);

        var versions = await provider.GetVersionsAsync(CoreType.Velocity);

        Assert.Equal(
            ["4.0.0", "3.4.0", "3.1.1", "3.1.0", "1.1.9", "1.0.10"],
            versions.Select(item => item.ProductVersion));
        Assert.Equal([25, 17, 11, 11, 8, 8], versions.Select(item => item.JavaMajorVersion));
        Assert.All(versions, item => Assert.Equal(item.ProductVersion, item.MinecraftVersion));
    }

    [Fact]
    public async Task PaperBuild_RejectsArtifactOutsideFillDataObjectContract()
    {
        var json = FillBuildJson("paper", "1.21.10", 10, 'c')
            .Replace(
                $"https://fill-data.papermc.io/v1/objects/{new string('c', 64)}/paper-1.21.10-10.jar",
                "https://evil.example/paper.jar",
                StringComparison.Ordinal);
        using var client = new HttpClient(new StubHandler(_ => JsonResponse(json)));
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetBuildsAsync(CoreType.Paper, "1.21.10"));
    }

    [Fact]
    public async Task Vanilla_DiscoveryVerifiesMetadataAndOmitsHistoricalReleaseWithoutServerJar()
    {
        var serverHash = new string('d', 40);
        var currentMetadata = Encoding.UTF8.GetBytes($$"""
            {
              "id": "26.2",
              "downloads": {
                "server": {
                  "url": "https://piston-data.mojang.com/v1/objects/{{serverHash}}/server.jar",
                  "size": 123456,
                  "sha1": "{{serverHash}}"
                }
              }
            }
            """);
        var legacyMetadata = Encoding.UTF8.GetBytes("{\"id\":\"1.0\",\"downloads\":{}}");
        var currentMetadataHash = Sha1(currentMetadata);
        var legacyMetadataHash = Sha1(legacyMetadata);
        var currentUri = MojangMetadataUri(currentMetadataHash, "26.2");
        var legacyUri = MojangMetadataUri(legacyMetadataHash, "1.0");
        var manifest = $$"""
            {
              "versions": [
                { "id": "1.0", "type": "release", "url": "{{legacyUri}}", "sha1": "{{legacyMetadataHash}}" },
                { "id": "26.2", "type": "release", "url": "{{currentUri}}", "sha1": "{{currentMetadataHash}}" },
                { "id": "26.3-snapshot-1", "type": "snapshot", "url": "{{currentUri}}", "sha1": "{{currentMetadataHash}}" }
              ]
            }
            """;
        var requestCount = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            Interlocked.Increment(ref requestCount);
            return request.RequestUri!.AbsoluteUri switch
            {
                "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json" =>
                    JsonResponse(manifest),
                var uri when uri == currentUri => ByteResponse(currentMetadata, "application/json"),
                var uri when uri == legacyUri => ByteResponse(legacyMetadata, "application/json"),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        }));
        var provider = CreateProvider(client);

        var versions = await provider.GetVersionsAsync(CoreType.Vanilla);
        var repeated = await provider.GetVersionsAsync(CoreType.Vanilla);

        var version = Assert.Single(versions);
        Assert.Same(versions, repeated);
        Assert.Equal("26.2", version.MinecraftVersion);
        Assert.Equal(25, version.JavaMajorVersion);
        Assert.Equal(3, requestCount);
    }

    [Fact]
    public async Task Vanilla_RejectsMetadataWhoseSha1DoesNotMatchManifest()
    {
        var metadata = Encoding.UTF8.GetBytes("{\"id\":\"26.2\"}");
        var wrongHash = new string('e', 40);
        var metadataUri = MojangMetadataUri(wrongHash, "26.2");
        var manifest = $$"""
            { "versions": [
              { "id": "26.2", "type": "release", "url": "{{metadataUri}}", "sha1": "{{wrongHash}}" }
            ] }
            """;
        using var client = new HttpClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/mc/game/version_manifest_v2.json"
                ? JsonResponse(manifest)
                : ByteResponse(metadata, "application/json")));
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetVersionsAsync(CoreType.Vanilla));
    }

    [Fact]
    public async Task Fabric_DiscoveryRequiresStableGameLoaderAndInstaller_AndSortsNaturally()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            Interlocked.Increment(ref requestCount);
            return request.RequestUri!.AbsolutePath switch
            {
                "/v2/versions/game" => JsonResponse("""
                    [
                      { "version": "1.21.9", "stable": true },
                      { "version": "26.3", "stable": true },
                      { "version": "1.14", "stable": true },
                      { "version": "26.2", "stable": true },
                      { "version": "26.2-rc-2", "stable": false }
                    ]
                    """),
                "/v2/versions/installer" => JsonResponse(FabricInstallerJson("1.1.2")),
                "/v2/versions/loader/26.2" => JsonResponse(FabricLoaderJson("26.2", "0.19.3")),
                "/v2/versions/loader/1.21.9" => JsonResponse(FabricLoaderJson("1.21.9", "0.16.10")),
                "/v2/versions/loader/1.14" => JsonResponse("[]"),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        }));
        var provider = CreateProvider(client);

        var versions = await provider.GetVersionsAsync(CoreType.Fabric);
        var repeated = await provider.GetVersionsAsync(CoreType.Fabric);

        Assert.Same(versions, repeated);
        Assert.Equal(["26.2", "1.21.9"], versions.Select(item => item.MinecraftVersion));
        Assert.Equal(5, requestCount);
    }

    [Fact]
    public async Task FabricBuild_ExposesLoaderAndInstallerVersions()
    {
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v2/versions/game" => JsonResponse("[{\"version\":\"26.2\",\"stable\":true}]"),
            "/v2/versions/loader/26.2" => JsonResponse(FabricLoaderJson("26.2", "0.19.3")),
            "/v2/versions/installer" => JsonResponse(FabricInstallerJson("1.1.2")),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        }));
        var provider = CreateProvider(client);

        var build = Assert.Single(await provider.GetBuildsAsync(CoreType.Fabric, "26.2"));

        Assert.Equal("0.19.3", build.LoaderVersion);
        Assert.Equal("1.1.2", build.BuildVersion);
        Assert.Equal(OfficialServerInstallStrategy.FabricInstaller, build.InstallStrategy);
        Assert.Equal(
            "https://maven.fabricmc.net/net/fabricmc/fabric-installer/1.1.2/fabric-installer-1.1.2.jar",
            build.DownloadUri!.AbsoluteUri);
    }

    [Fact]
    public async Task FabricBuild_RejectsMismatchedIntermediaryGameVersion()
    {
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v2/versions/game" => JsonResponse("[{\"version\":\"26.2\",\"stable\":true}]"),
            "/v2/versions/loader/26.2" => JsonResponse(FabricLoaderJson("1.21.10", "0.19.3")),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        }));
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetBuildsAsync(CoreType.Fabric, "26.2"));
    }

    [Fact]
    public async Task Forge_MapsOnlyStableInstallerEraCoordinatesAndPreservesFullLoaderSuffix()
    {
        var xml = MavenMetadata(
            "net.minecraftforge",
            "forge",
            [
                "1.5.1-7.7.2.682",
                "1.5.2-7.8.1.738",
                "1.7.2-10.12.2.1161-mc172",
                "1.21.10-60.1.9",
                "26.2-65.1.1",
                "26.3-66.0.0",
                "1.21.10-60.1.10-beta"
            ]);
        using var client = new HttpClient(new StubHandler(_ => XmlResponse(xml)));
        var provider = CreateProvider(client);

        var versions = await provider.GetVersionsAsync(CoreType.Forge);
        var builds = await provider.GetBuildsAsync(CoreType.Forge, "1.7.2");

        Assert.Equal(["26.2", "1.21.10", "1.7.2", "1.5.2"],
            versions.Select(item => item.MinecraftVersion));
        var build = Assert.Single(builds);
        Assert.Equal("10.12.2.1161-mc172", build.LoaderVersion);
        Assert.Equal("1.7.2-10.12.2.1161-mc172", build.BuildVersion);
        Assert.EndsWith("forge-1.7.2-10.12.2.1161-mc172-installer.jar", build.FileName);
    }

    [Fact]
    public async Task NeoForge_MapsOfficialLoaderSchemeAndOmitsPrereleases()
    {
        var xml = MavenMetadata(
            "net.neoforged",
            "neoforge",
            [
                "20.2.12-beta",
                "20.2.86",
                "21.11.45",
                "26.1.2.95",
                "26.2.0.57",
                "26.3.0.1"
            ]);
        using var client = new HttpClient(new StubHandler(_ => XmlResponse(xml)));
        var provider = CreateProvider(client);

        var versions = await provider.GetVersionsAsync(CoreType.NeoForge);
        var builds = await provider.GetBuildsAsync(CoreType.NeoForge, "26.1.2");

        Assert.Equal(["26.2", "26.1.2", "1.21.11", "1.20.2"],
            versions.Select(item => item.MinecraftVersion));
        var build = Assert.Single(builds);
        Assert.Equal("26.1.2.95", build.LoaderVersion);
        Assert.Equal(OfficialServerInstallStrategy.NeoForgeInstaller, build.InstallStrategy);
    }

    [Fact]
    public async Task MavenMetadata_RejectsDtd()
    {
        const string xml = """
            <!DOCTYPE metadata [<!ENTITY x "net.minecraftforge">]>
            <metadata>
              <groupId>&x;</groupId><artifactId>forge</artifactId>
              <versioning><versions><version>26.2-65.1.1</version></versions></versioning>
            </metadata>
            """;
        using var client = new HttpClient(new StubHandler(_ => XmlResponse(xml)));
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetVersionsAsync(CoreType.Forge));
    }

    [Fact]
    public async Task Catalog_RejectsCrossHostRedirect()
    {
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = JsonResponse("[]");
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://evil.example/v2/versions/game");
            return response;
        }));
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetVersionsAsync(CoreType.Fabric));
    }

    [Fact]
    public async Task Catalog_RejectsDeclaredJsonBodyAboveBound()
    {
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = JsonResponse("[]");
            response.Content.Headers.ContentLength = (16L * 1024 * 1024) + 1;
            return response;
        }));
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetVersionsAsync(CoreType.Fabric));
    }

    [Theory]
    [InlineData("0.99")]
    [InlineData("26.2.1")]
    [InlineData("26.3")]
    [InlineData("1.21.10-rc1")]
    [InlineData("1")]
    public async Task GetBuilds_RejectsVersionOutsideStrictReleaseRange(string version)
    {
        using var client = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("Network must not be used.")));
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.GetBuildsAsync(CoreType.Paper, version));
    }

    private static OfficialServerCoreCatalogProvider CreateProvider(HttpClient client)
        => new(client, UserAgent);

    private static void AssertDescriptor(
        OfficialServerCoreDescriptor descriptor,
        CoreType expectedType,
        string expectedDisplayName,
        bool expectedProxy)
    {
        Assert.Equal(expectedType, descriptor.CoreType);
        Assert.Equal(expectedDisplayName, descriptor.DisplayName);
        Assert.Equal(expectedProxy, descriptor.IsProxy);
    }

    private static string FillBuildJson(string project, string version, int build, char hashCharacter)
    {
        var hash = new string(hashCharacter, 64);
        var fileName = $"{project}-{version}-{build}.jar";
        return $$"""
            [{
              "id": {{build}},
              "channel": "STABLE",
              "downloads": {
                "server:default": {
                  "name": "{{fileName}}",
                  "url": "https://fill-data.papermc.io/v1/objects/{{hash}}/{{fileName}}",
                  "size": 12345,
                  "checksums": { "sha256": "{{hash}}" }
                }
              }
            }]
            """;
    }

    private static string FabricLoaderJson(string intermediaryVersion, string loaderVersion)
        => $$"""
            [{
              "loader": { "version": "{{loaderVersion}}", "stable": true },
              "intermediary": { "version": "{{intermediaryVersion}}" }
            }]
            """;

    private static string FabricInstallerJson(string version)
        => $$"""
            [{
              "version": "{{version}}",
              "stable": true,
              "maven": "net.fabricmc:fabric-installer:{{version}}",
              "url": "https://maven.fabricmc.net/net/fabricmc/fabric-installer/{{version}}/fabric-installer-{{version}}.jar"
            }]
            """;

    private static string MavenMetadata(string group, string artifact, IEnumerable<string> versions)
    {
        var entries = string.Concat(versions.Select(version => $"<version>{version}</version>"));
        return $"<metadata><groupId>{group}</groupId><artifactId>{artifact}</artifactId>"
            + $"<versioning><versions>{entries}</versions></versioning></metadata>";
    }

    private static string MojangMetadataUri(string hash, string version)
        => $"https://piston-meta.mojang.com/v1/packages/{hash}/{version}.json";

    private static string Sha1(byte[] value)
        => Convert.ToHexString(SHA1.HashData(value)).ToLowerInvariant();

    private static HttpResponseMessage JsonResponse(string value)
        => ByteResponse(Encoding.UTF8.GetBytes(value), "application/json");

    private static HttpResponseMessage XmlResponse(string value)
        => ByteResponse(Encoding.UTF8.GetBytes(value), "application/xml");

    private static HttpResponseMessage ByteResponse(byte[] value, string contentType)
    {
        var content = new ByteArrayContent(value);
        content.Headers.ContentType = new(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
