using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class HybridServerCoreCatalogProviderTests
{
    private const string UserAgent = "Muhun-MCSV-Manager.Tests/1.0";

    [Fact]
    public void Products_ExposeOnlyExecutableHybridFamilies()
    {
        using var github = Client(_ => Json("[]"));
        using var mohist = Client(_ => Json("[]"));
        var provider = new HybridServerCoreCatalogProvider(github, mohist, UserAgent);

        Assert.Equal(
            [CoreType.Mohist, CoreType.Arclight, CoreType.CatServer, CoreType.Akarin],
            provider.GetProducts().Select(product => product.CoreType));
        Assert.DoesNotContain(provider.GetProducts(), product => product.DisplayName == "Bukkit");
    }

    [Fact]
    public async Task Mohist_UsesFirstPartyShaAndExactDownloadHeaders()
    {
        var commit = new string('a', 40);
        using var github = Client(_ => throw new InvalidOperationException());
        using var mohist = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/project/mohist/1.20.1/builds/latest" => Json($$"""
                {
                  "id": 471,
                  "file_sha256": "{{new string('b', 64)}}",
                  "build_date": "2026-08-01T00:00:00Z",
                  "commit": {
                    "hash": "{{commit}}",
                    "commit_date": "2026-08-01T00:00:00Z"
                  },
                  "loader": { "forge_version": "47.4.13", "neoforge_version": "" }
                }
                """),
            "/project/mohist/1.20.1/builds/471/download" => DownloadHeaders(
                141_965_847,
                "mohist-1.20.1-aaaaaaaa-server.jar"),
            _ => throw new InvalidOperationException(request.RequestUri.AbsoluteUri)
        });
        var provider = new HybridServerCoreCatalogProvider(github, mohist, UserAgent);

        var build = Assert.Single(await provider.GetBuildsAsync(CoreType.Mohist, "1.20.1"));

        Assert.Equal(17, build.JavaMajorVersion);
        Assert.Equal(141_965_847, build.Size);
        Assert.Equal(new string('b', 64), build.Sha256);
        Assert.Equal(HybridArtifactVerification.UpstreamSha256, build.Verification);
        Assert.Equal("forge", build.Loader);
        Assert.Equal("47.4.13", build.LoaderVersion);
    }

    [Fact]
    public async Task Arclight_SkipsAssetsWithoutUpstreamDigest_AndAcceptsExactDigestAsset()
    {
        var sha = new string('c', 64);
        using var github = Client(_ => Json($$"""
            [{
              "id": 456,
              "tag_name": "FeudalKings/1.0.1",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "id": 1, "state": "uploaded", "name": "arclight-forge-1.20.1-0.0.1-abcdef1.jar",
                  "size": 123, "digest": null,
                  "browser_download_url": "https://github.com/IzzelAliz/Arclight/releases/download/FeudalKings/1.0.1/arclight-forge-1.20.1-0.0.1-abcdef1.jar"
                },
                {
                  "id": 2, "state": "uploaded", "name": "arclight-neoforge-1.21.1-1.0.1-abcdef1.jar",
                  "size": 7378283, "digest": "sha256:{{sha}}",
                  "browser_download_url": "https://github.com/IzzelAliz/Arclight/releases/download/FeudalKings/1.0.1/arclight-neoforge-1.21.1-1.0.1-abcdef1.jar"
                }
              ]
            }]
            """));
        using var mohist = Client(_ => throw new InvalidOperationException());
        var provider = new HybridServerCoreCatalogProvider(github, mohist, UserAgent);

        var build = Assert.Single(await provider.GetBuildsAsync(
            CoreType.Arclight,
            "1.21.1",
            "neoforge"));

        Assert.Equal(sha, build.Sha256);
        Assert.Equal(2, build.SourceAssetId);
        Assert.Equal(HybridArtifactVerification.UpstreamSha256, build.Verification);
    }

    [Fact]
    public async Task Akarin_PinCrossChecksOfficialReleaseIdentity()
    {
        using var github = Client(_ => Json("""
            {
              "id": 93122943,
              "tag_name": "1.12.2-R0.4.4",
              "draft": false,
              "prerelease": false,
              "assets": [{
                "id": 96490178,
                "name": "akarin-1.12.2.jar",
                "size": 48696258,
                "state": "uploaded",
                "digest": null,
                "browser_download_url": "https://github.com/Akarin-project/Akarin/releases/download/1.12.2-R0.4.4/akarin-1.12.2.jar"
              }]
            }
            """));
        using var mohist = Client(_ => throw new InvalidOperationException());
        var provider = new HybridServerCoreCatalogProvider(github, mohist, UserAgent);

        var build = Assert.Single(await provider.GetBuildsAsync(CoreType.Akarin, "1.12.2"));

        Assert.Equal(96490178, build.SourceAssetId);
        Assert.Equal(48_696_258, build.Size);
        Assert.Equal(
            "b6eae9e1f9e831505939db26ac032dc866b9dbb6f2a21f5762e6f2a0f5099e68",
            build.Sha256);
        Assert.Equal(HybridArtifactVerification.PinnedCatalogSha256, build.Verification);
    }

    [Fact]
    public async Task Downloader_AllowsServerJarDestinationButVerifiesCatalogNameAndHash()
    {
        using var directory = new TemporaryDirectory();
        var bytes = Encoding.UTF8.GetBytes("official hybrid fixture");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var build = new HybridServerCoreBuildInfo(
            CoreType.Arclight,
            "Arclight fixture",
            "1.21.1",
            "1.0.1",
            "neoforge",
            null,
            "1.0.1-abcdef1",
            21,
            true,
            false,
            new Uri("https://github.com/IzzelAliz/Arclight/releases/download/FeudalKings/1.0.1/arclight-neoforge-1.21.1-1.0.1-abcdef1.jar"),
            "arclight-neoforge-1.21.1-1.0.1-abcdef1.jar",
            bytes.Length,
            sha,
            HybridArtifactVerification.UpstreamSha256,
            new Uri("https://api.github.com/repos/IzzelAliz/Arclight/releases"),
            "FeudalKings/1.0.1",
            123,
            456);
        using var github = Client(_ => Binary(
            bytes,
            build.FileName));
        using var mohist = Client(_ => throw new InvalidOperationException());
        var downloader = new HybridServerCoreDownloader(github, mohist);
        var destination = Path.Combine(directory.Path, "server.jar");

        var result = await downloader.DownloadAsync(build, destination);

        Assert.Equal(destination, result.FilePath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Metadata_FailsClosedWhenClientSilentlyFollowedRedirect()
    {
        using var github = Client(request =>
        {
            var response = Json("[]");
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://evil.example/releases");
            return response;
        });
        using var mohist = Client(_ => throw new InvalidOperationException());
        var provider = new HybridServerCoreCatalogProvider(github, mohist, UserAgent);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetVersionsAsync(CoreType.Arclight));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response)
        => new(new StubHandler(response));

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json))
        };

    private static HttpResponseMessage DownloadHeaders(long size, string fileName)
    {
        var content = new HeaderOnlyContent(size);
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = fileName
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Binary(byte[] bytes, string fileName)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = fileName
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class HeaderOnlyContent(long length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.CompletedTask;

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = length;
            return true;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var result = response(request);
            result.RequestMessage ??= request;
            return Task.FromResult(result);
        }
    }
}
