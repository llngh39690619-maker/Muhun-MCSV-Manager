using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class CurseForgeMetadataPreviewTests
{
    private const string ApiKey = "fixture-preview-key";
    private static readonly Uri DownloadUri = new("https://edge.forgecdn.net/exact-pack.zip");

    [Theory]
    [InlineData("forge-14.23.5.2860", ModrinthModpackLoaderKind.Forge, "14.23.5.2860")]
    [InlineData("fabric-0.16.9", ModrinthModpackLoaderKind.Fabric, "0.16.9")]
    [InlineData("neoforge-21.1.248", ModrinthModpackLoaderKind.NeoForge, "21.1.248")]
    [InlineData("quilt-0.26.4", ModrinthModpackLoaderKind.Quilt, "0.26.4")]
    public async Task PreviewReadsExactPrimaryLoaderWithSmallRangesAndNoCdnKey(
        string loader, ModrinthModpackLoaderKind expectedKind, string expectedVersion)
    {
        var payload = CreateArchive(loader, paddingBytes: 12 * 1024 * 1024);
        var apiPaths = new List<string>();
        var ranges = new List<(long Start, long End)>();
        using var apiClient = CreateApiClient(payload.Length, apiPaths);
        using var cdnClient = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(DownloadUri, request.RequestUri);
            Assert.False(request.Headers.Contains("x-api-key"));
            Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).Value);
            if (request.Method == HttpMethod.Head)
            {
                Assert.Null(request.Headers.Range);
                return HeadResponse(payload.Length);
            }
            if (ranges.Count > 0)
            {
                Assert.Equal("\"exact-file-v1\"", Assert.Single(request.Headers.IfMatch).Tag);
            }

            var range = Assert.Single(request.Headers.Range!.Ranges);
            ranges.Add((range.From!.Value, range.To!.Value));
            return RangeResponse(request, payload);
        }));
        var provider = new CurseForgeModpackProvider(apiClient, cdnClient, "preview-tests");

        var result = await provider.InspectClientPackMetadataAsync(ApiKey, 100, 200);

        Assert.Equal("1.12.2", result.MinecraftVersion);
        Assert.Equal("2.9.3", result.PackVersion);
        Assert.Equal(expectedKind, result.LoaderInstallRequest.Kind);
        Assert.Equal(expectedVersion, result.LoaderInstallRequest.LoaderVersion);
        Assert.Equal(["/v1/mods/100", "/v1/mods/100/files/200", "/v1/mods/100/files/200/download-url"], apiPaths);
        Assert.InRange(ranges.Count, 1, 5);
        Assert.True(ranges.Sum(range => range.End - range.Start + 1) < 256 * 1024);
        Assert.False(apiClient.DefaultRequestHeaders.Contains("x-api-key"));
        Assert.False(cdnClient.DefaultRequestHeaders.Contains("x-api-key"));
    }

    [Fact]
    public async Task InspectorStreamOverloadLeavesCallerStreamOpen()
    {
        using var stream = new MemoryStream(CreateArchive("forge-14.23.5.2860"));

        var result = await new CurseForgeModpackManifestInspector().InspectAsync(stream);

        Assert.Equal(ModrinthModpackLoaderKind.Forge, result.LoaderInstallRequest.Kind);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("wrong-project")]
    [InlineData("wrong-file")]
    [InlineData("server-pack")]
    [InlineData("unavailable")]
    [InlineData("oversize")]
    [InlineData("missing-url")]
    public async Task PreviewRevalidatesExactFileBeforeAnyCdnRequest(string invalidCase)
    {
        using var apiClient = CreateApiClient(500, [], invalidCase);
        var cdnWasCalled = false;
        using var cdnClient = new HttpClient(new StubHandler(_ =>
        {
            cdnWasCalled = true;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var provider = new CurseForgeModpackProvider(apiClient, cdnClient, "preview-tests");

        await Assert.ThrowsAnyAsync<Exception>(() => provider.InspectClientPackMetadataAsync(ApiKey, 100, 200));
        Assert.False(cdnWasCalled);
    }

    [Fact]
    public async Task PreviewAcceptsSmallWholeResponseWhenServerIgnoresRanges()
    {
        var payload = CreateArchive("forge-14.23.5.2860");
        var requests = 0;
        using var apiClient = CreateApiClient(payload.Length, []);
        using var cdnClient = new HttpClient(new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Head) return HeadResponse(payload.Length);
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }));
        var provider = new CurseForgeModpackProvider(apiClient, cdnClient, "preview-tests");

        var result = await provider.InspectClientPackMetadataAsync(ApiKey, 100, 200);

        Assert.Equal(ModrinthModpackLoaderKind.Forge, result.LoaderInstallRequest.Kind);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task PreviewResolvesEdgeHeadRedirectBeforeRequestingMediafilezRanges()
    {
        var payload = CreateArchive("forge-14.23.5.2860");
        var finalUri = new Uri("https://mediafilez.forgecdn.net/files/4612/979/RLCraft%201.12.2%20-%20Release%20v2.9.3.zip");
        var requests = new List<(HttpMethod Method, Uri Uri)>();
        using var apiClient = CreateApiClient(payload.Length, []);
        using var downloadClient = new HttpClient(new StubHandler(_ =>
            throw new Xunit.Sdk.XunitException("Preview must use its separate metadata client.")));
        using var metadataClient = new HttpClient(new StubHandler(request =>
        {
            Assert.False(request.Headers.Contains("x-api-key"));
            requests.Add((request.Method, request.RequestUri!));
            if (request.RequestUri == DownloadUri)
            {
                Assert.Equal(HttpMethod.Head, request.Method);
                Assert.Null(request.Headers.Range);
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = finalUri;
                return redirect;
            }

            Assert.Equal(finalUri, request.RequestUri);
            return RangeResponse(request, payload);
        }));
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, "preview-tests", metadataClient);

        var result = await provider.InspectClientPackMetadataAsync(ApiKey, 100, 200);

        Assert.Equal(ModrinthModpackLoaderKind.Forge, result.LoaderInstallRequest.Kind);
        Assert.Equal((HttpMethod.Head, DownloadUri), requests[0]);
        Assert.Equal((HttpMethod.Head, finalUri), requests[1]);
        Assert.All(requests.Skip(2), request => Assert.Equal((HttpMethod.Get, finalUri), request));
    }

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task UnsupportedHeadFallsBackToRangesOnTheApiProvidedUrl(HttpStatusCode headStatus)
    {
        var payload = CreateArchive("forge-14.23.5.2860");
        var headRequests = 0;
        var rangeRequests = 0;
        using var apiClient = CreateApiClient(payload.Length, []);
        using var cdnClient = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(DownloadUri, request.RequestUri);
            if (request.Method == HttpMethod.Head)
            {
                headRequests++;
                Assert.Null(request.Headers.Range);
                return new HttpResponseMessage(headStatus);
            }

            rangeRequests++;
            return RangeResponse(request, payload);
        }));
        var provider = new CurseForgeModpackProvider(apiClient, cdnClient, "preview-tests");

        var result = await provider.InspectClientPackMetadataAsync(ApiKey, 100, 200);

        Assert.Equal(ModrinthModpackLoaderKind.Forge, result.LoaderInstallRequest.Kind);
        Assert.Equal(1, headRequests);
        Assert.True(rangeRequests > 0);
    }

    [Theory]
    [InlineData("https://forgecdn.net.evil.example/pack.zip")]
    [InlineData("http://mediafilez.forgecdn.net/pack.zip")]
    [InlineData("https://user@mediafilez.forgecdn.net/pack.zip")]
    [InlineData("https://mediafilez.forgecdn.net:444/pack.zip")]
    public async Task UnsafeHeadRedirectIsRejectedWithoutContactingDestination(string destination)
    {
        var calls = 0;
        using var apiClient = CreateApiClient(500, []);
        using var cdnClient = new HttpClient(new StubHandler(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Equal(DownloadUri, request.RequestUri);
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(destination);
            return response;
        }));
        var provider = new CurseForgeModpackProvider(apiClient, cdnClient, "preview-tests");

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.InspectClientPackMetadataAsync(ApiKey, 100, 200));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InjectedAutoRedirectClientCannotReturnAnUnsafeFinalHeadUri()
    {
        var calls = 0;
        using var apiClient = CreateApiClient(500, []);
        using var cdnClient = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Head, "https://unrelated.example/pack.zip")
            };
        }));
        var provider = new CurseForgeModpackProvider(apiClient, cdnClient, "preview-tests");

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.InspectClientPackMetadataAsync(ApiKey, 100, 200));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task HeadRedirectLoopsStopAfterThreeHopsWithoutReadingArchiveBytes()
    {
        var calls = 0;
        using var apiClient = CreateApiClient(500, []);
        using var cdnClient = new HttpClient(new StubHandler(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Null(request.Headers.Range);
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("/next.zip", UriKind.Relative);
            return response;
        }));
        var provider = new CurseForgeModpackProvider(apiClient, cdnClient, "preview-tests");

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.InspectClientPackMetadataAsync(ApiKey, 100, 200));
        Assert.Equal(4, calls);
    }

    [Fact]
    public void IgnoredRangeForLargeFileIsRejectedWithoutReadingResponseBody()
    {
        using var body = new RejectReadStream();
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
        using var preview = CreateStream(client, 20 * 1024 * 1024);

        Assert.Throws<InvalidDataException>(() => preview.ReadByte());

        Assert.Equal(0, body.ReadCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("total")]
    [InlineData("encoding")]
    [InlineData("length")]
    public void InvalidRangeOrRepresentationHeadersAreRejected(string invalidCase)
    {
        var payload = new byte[2 * CurseForgeMetadataPreviewStream.BlockBytes];
        using var client = new HttpClient(new StubHandler(request =>
        {
            var response = RangeResponse(request, payload);
            response.Content.Headers.ContentRange = invalidCase switch
            {
                "missing" => null,
                "start" => new ContentRangeHeaderValue(1, 65_535, payload.Length),
                "end" => new ContentRangeHeaderValue(0, 65_534, payload.Length),
                "total" => new ContentRangeHeaderValue(0, 65_535, payload.Length + 1),
                _ => response.Content.Headers.ContentRange
            };
            if (invalidCase == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            if (invalidCase == "length") response.Content.Headers.ContentLength = 123;
            return response;
        }));
        using var preview = CreateStream(client, payload.Length);

        Assert.Throws<InvalidDataException>(() => preview.ReadByte());
    }

    [Fact]
    public void CachedBlocksAvoidRepeatRequestsAndChangedEntityTagIsRejected()
    {
        var payload = new byte[2 * CurseForgeMetadataPreviewStream.BlockBytes];
        var calls = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            var response = RangeResponse(request, payload);
            if (++calls == 2)
            {
                Assert.Equal("\"exact-file-v1\"", Assert.Single(request.Headers.IfMatch).Tag);
                response.Headers.ETag = new EntityTagHeaderValue("\"changed-file\"");
            }

            return response;
        }));
        using var preview = CreateStream(client, payload.Length);
        Assert.Equal(0, preview.ReadByte());
        preview.Position = 0;
        Assert.Equal(0, preview.ReadByte());
        Assert.Equal(1, calls);
        preview.Position = CurseForgeMetadataPreviewStream.BlockBytes;

        Assert.Throws<InvalidDataException>(() => preview.ReadByte());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void ShortOrOverlongRangeBodyIsRejected(int lengthDifference)
    {
        var payload = new byte[2 * CurseForgeMetadataPreviewStream.BlockBytes];
        using var client = new HttpClient(new StubHandler(request =>
        {
            var response = RangeResponse(request, payload);
            response.Content.Dispose();
            response.Content = new ByteArrayContent(new byte[CurseForgeMetadataPreviewStream.BlockBytes + lengthDifference]);
            response.Content.Headers.ContentLength = CurseForgeMetadataPreviewStream.BlockBytes;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 65_535, payload.Length);
            return response;
        }));
        using var preview = CreateStream(client, payload.Length);

        Assert.Throws<InvalidDataException>(() => preview.ReadByte());
    }

    [Fact]
    public void ChangingRedirectTargetCannotMixRangesWithMatchingTags()
    {
        var payload = new byte[2 * CurseForgeMetadataPreviewStream.BlockBytes];
        var calls = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            var response = RangeResponse(request, payload);
            if (++calls == 2)
            {
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://edge.forgecdn.net/different-pack.zip");
            }

            return response;
        }));
        using var preview = CreateStream(client, payload.Length);
        Assert.Equal(0, preview.ReadByte());
        preview.Position = CurseForgeMetadataPreviewStream.BlockBytes;

        Assert.Throws<InvalidDataException>(() => preview.ReadByte());
    }

    [Fact]
    public void LastModifiedValidatorIsUsedWhenStrongEntityTagIsMissing()
    {
        var payload = new byte[2 * CurseForgeMetadataPreviewStream.BlockBytes];
        var modified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var calls = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (++calls == 2) Assert.Equal(modified, request.Headers.IfUnmodifiedSince);
            var response = RangeResponse(request, payload);
            response.Headers.ETag = null;
            response.Content.Headers.LastModified = modified;
            return response;
        }));
        using var preview = CreateStream(client, payload.Length);

        Assert.Equal(0, preview.ReadByte());
        preview.Position = CurseForgeMetadataPreviewStream.BlockBytes;
        Assert.Equal(0, preview.ReadByte());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void PartialResponsesWithoutStableValidatorAreNotCombined()
    {
        var payload = new byte[2 * CurseForgeMetadataPreviewStream.BlockBytes];
        using var client = new HttpClient(new StubHandler(request =>
        {
            var response = RangeResponse(request, payload);
            response.Headers.ETag = null;
            return response;
        }));
        using var preview = CreateStream(client, payload.Length);

        Assert.Throws<InvalidDataException>(() => preview.ReadByte());
    }

    [Fact]
    public void TransferAndRequestBudgetStopBeforeFetchingBlock129()
    {
        var length = 129L * CurseForgeMetadataPreviewStream.BlockBytes;
        var calls = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            calls++;
            var range = Assert.Single(request.Headers.Range!.Ranges);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[CurseForgeMetadataPreviewStream.BlockBytes])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(range.From!.Value, range.To!.Value, length);
            response.Headers.ETag = new EntityTagHeaderValue("\"exact-file-v1\"");
            return response;
        }));
        using var preview = CreateStream(client, length);
        for (var index = 0; index < 128; index++)
        {
            preview.Position = (long)index * CurseForgeMetadataPreviewStream.BlockBytes;
            Assert.Equal(0, preview.ReadByte());
        }

        preview.Position = 128L * CurseForgeMetadataPreviewStream.BlockBytes;
        Assert.Throws<InvalidDataException>(() => preview.ReadByte());
        Assert.Equal(128, calls);
    }

    [Fact]
    public void CancellationStopsFurtherCdnReads()
    {
        using var cancellation = new CancellationTokenSource();
        var payload = new byte[2 * CurseForgeMetadataPreviewStream.BlockBytes];
        var calls = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            calls++;
            return RangeResponse(request, payload);
        }));
        using var preview = CreateStream(client, payload.Length, cancellation.Token);
        Assert.Equal(0, preview.ReadByte());
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => preview.ReadByte());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PreviewStillRejectsAnAmbiguousManifest()
    {
        var payload = CreateArchive("forge-14.23.5.2860", ambiguous: true);
        using var apiClient = CreateApiClient(payload.Length, []);
        using var cdnClient = new HttpClient(new StubHandler(request => RangeResponse(request, payload)));
        var provider = new CurseForgeModpackProvider(apiClient, cdnClient, "preview-tests");

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.InspectClientPackMetadataAsync(ApiKey, 100, 200));
    }

    private static CurseForgeMetadataPreviewStream CreateStream(
        HttpClient client, long length, CancellationToken cancellationToken = default)
        => new(client, DownloadUri, length, "preview-tests", cancellationToken);

    private static HttpResponseMessage RangeResponse(HttpRequestMessage request, byte[] payload)
    {
        if (request.Method == HttpMethod.Head) return HeadResponse(payload.Length);
        var range = Assert.Single(request.Headers.Range!.Ranges);
        var start = checked((int)range.From!.Value);
        var end = checked((int)range.To!.Value);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(payload.AsSpan(start, end - start + 1).ToArray())
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, payload.Length);
        response.Headers.ETag = new EntityTagHeaderValue("\"exact-file-v1\"");
        return response;
    }

    private static HttpResponseMessage HeadResponse(long length)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = length;
        return response;
    }

    private static HttpClient CreateApiClient(long length, List<string> paths, string? invalidCase = null)
        => new(new StubHandler(request =>
        {
            Assert.Equal("api.curseforge.com", request.RequestUri!.Host);
            Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("x-api-key")));
            paths.Add(request.RequestUri.AbsolutePath);
            var json = request.RequestUri.AbsolutePath switch
            {
                "/v1/mods/100" => $$"""
                    {"data":{"id":100,"gameId":432,"classId":4471,"slug":"fixture","name":"Fixture",
                    "isAvailable":true,"allowModDistribution":{{(invalidCase == "blocked" ? "false" : "true")}}
                    } }
                    """,
                "/v1/mods/100/files/200" => $$"""
                    {"data":{"id":{{(invalidCase == "wrong-file" ? 201 : 200)}},"gameId":432,
                    "modId":{{(invalidCase == "wrong-project" ? 999 : 100)}},
                    "displayName":"Historical pack","fileName":"pack.zip",
                    "isAvailable":{{(invalidCase == "unavailable" ? "false" : "true")}},
                    "isServerPack":{{(invalidCase == "server-pack" ? "true" : "false")}},
                    "fileLength":{{(invalidCase == "oversize" ? 2L * 1024 * 1024 * 1024 + 1 : length)}},
                    "gameVersions":["1.12.2"],"hashes":[] } }
                    """,
                "/v1/mods/100/files/200/download-url" => invalidCase == "missing-url"
                    ? "{\"data\":null}" : "{\"data\":\"https://edge.forgecdn.net/exact-pack.zip\"}",
                _ => throw new Xunit.Sdk.XunitException("Unexpected API path.")
            };
            return CurseForgeModpackProviderTests.JsonResponse(json);
        }));

    private static byte[] CreateArchive(string loader, int paddingBytes = 0, bool ambiguous = false)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (paddingBytes > 0)
            {
                using var padding = archive.CreateEntry("overrides/padding.bin", CompressionLevel.NoCompression).Open();
                padding.Write(new byte[paddingBytes]);
            }

            using var output = archive.CreateEntry("manifest.json", CompressionLevel.Fastest).Open();
            var extraLoader = ambiguous ? ", {\"id\":\"fabric-0.16.9\",\"primary\":true}" : string.Empty;
            var manifest = $$"""
                {"manifestType":"minecraftModpack","manifestVersion":1,
                "name":"Historical Fixture","version":"2.9.3","overrides":"overrides","files":[],
                "minecraft":{"version":"1.12.2","modLoaders":[{"id":"{{loader}}","primary":true}{{extraLoader}}] } }
                """;
            output.Write(Encoding.UTF8.GetBytes(manifest));
        }

        return buffer.ToArray();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class RejectReadStream : Stream
    {
        public int ReadCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            throw new Xunit.Sdk.XunitException("Large response body must not be read.");
        }

        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
