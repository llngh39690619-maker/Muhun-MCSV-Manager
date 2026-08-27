using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class OnlineModpackArtworkCacheTests
{
    private static readonly Uri ApprovedUri = new("https://images.example.test/project/hero.png");

    [Theory]
    [InlineData(OnlineModpackProvider.Modrinth, "https://cdn.modrinth.com/data/project/icon.png")]
    [InlineData(OnlineModpackProvider.CurseForge, "https://media.forgecdn.net/avatars/icon.png")]
    [InlineData(OnlineModpackProvider.Ftb, "https://cdn.feed-the-beast.com/pack/icon.webp")]
    public void OfficialPolicy_AllowsOnlyTheExactHostForItsSource(
        OnlineModpackProvider provider,
        string approved)
    {
        var policy = new OnlineModpackArtworkUriPolicy();

        Assert.True(policy.IsAllowed(provider, new Uri(approved)));
        Assert.False(policy.IsAllowed(provider, new Uri("https://cdn.modrinth.com.evil.test/icon.png")));
        Assert.False(policy.IsAllowed(provider, new Uri("https://cdn.modrinth.com:8443/icon.png")));
        Assert.False(policy.IsAllowed(provider, new Uri("https://user@cdn.modrinth.com/icon.png")));
    }

    [Fact]
    public async Task GetOrCacheAsync_SourceSpecificAllowlistRejectsBeforeNetwork()
    {
        using var directory = new TemporaryDirectory();
        var requests = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(ImageResponse(PngBytes(), "image/png"));
        }));
        using var cache = CreateCache(directory.Path, client, OnlineModpackProvider.Modrinth);

        var wrongSource = await cache.GetOrCacheAsync(OnlineModpackProvider.Ftb, ApprovedUri);
        var correctSource = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.Null(wrongSource);
        Assert.NotNull(correctSource);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task GetOrCacheAsync_RejectsPlainHttpBeforeNetwork()
    {
        using var directory = new TemporaryDirectory();
        var requests = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(ImageResponse(PngBytes(), "image/png"));
        }));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(
            OnlineModpackProvider.Modrinth,
            new Uri("http://images.example.test/project/hero.png"));

        Assert.Null(result);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task GetOrCacheAsync_RejectsRedirectWithoutFollowingIt()
    {
        using var directory = new TemporaryDirectory();
        var requests = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://images.example.test/redirected.png");
            return Task.FromResult(response);
        }));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.Null(result);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task GetOrCacheAsync_RejectsClientThatAlreadyFollowedRedirect()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = ImageResponse(PngBytes(), "image/png");
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://images.example.test/final.png");
            return Task.FromResult(response);
        }));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetOrCacheAsync_RejectsDeclaredOversizeWithoutReadingBody()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = ImageResponse(PngBytes(), "image/png");
            response.Content.Headers.ContentLength = OnlineModpackArtworkCache.MaximumImageBytes + 1;
            return Task.FromResult(response);
        }));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.Null(result);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "cache", "online-modpack-artwork"),
            "*.tmp"));
    }

    [Fact]
    public async Task GetOrCacheAsync_RejectsChunkedBodyThatCrossesStreamingLimit()
    {
        using var directory = new TemporaryDirectory();
        var bytes = new byte[checked((int)OnlineModpackArtworkCache.MaximumImageBytes + 1)];
        PngBytes().CopyTo(bytes, 0);
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent(bytes)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(response);
        }));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.Null(result);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "cache", "online-modpack-artwork"),
            "*.tmp"));
    }

    [Fact]
    public async Task GetOrCacheAsync_RejectsInvalidBodyEvenWhenDeclaredAsImage()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(ImageResponse("not an image"u8.ToArray(), "image/png"))));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.Null(result);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "cache", "online-modpack-artwork"),
            "*.tmp"));
    }

    [Fact]
    public async Task GetOrCacheAsync_AcceptsSupportedMagicWhenOfficialCdnMislabelsImageSubtype()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(ImageResponse(WebPBytes(), "image/png"))));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.NotNull(result);
        Assert.EndsWith(".webp", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WebPBytes(), await File.ReadAllBytesAsync(result));
    }

    [Fact]
    public async Task GetOrCacheAsync_FtbAcceptsOfficialBinaryContentTypeAfterMagicValidation()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(ImageResponse(PngBytes(), "application/octet-stream"))));
        using var cache = CreateCache(directory.Path, client, OnlineModpackProvider.Ftb);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Ftb, ApprovedUri);

        Assert.NotNull(result);
        Assert.EndsWith(".png", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetOrCacheAsync_OtherProviderRejectsBinaryContentType()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(ImageResponse(PngBytes(), "application/octet-stream"))));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetOrCacheAsync_FtbBinaryContentTypeStillRejectsNonImageBody()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(ImageResponse("not an image"u8.ToArray(), "application/octet-stream"))));
        using var cache = CreateCache(directory.Path, client, OnlineModpackProvider.Ftb);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Ftb, ApprovedUri);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetOrCacheAsync_RejectsPixelBombDimensions()
    {
        using var directory = new TemporaryDirectory();
        var bytes = PngBytes(width: 50_000, height: 50_000);
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(ImageResponse(bytes, "image/png"))));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.Null(result);
    }

    [Theory]
    [MemberData(nameof(SupportedImages))]
    public async Task GetOrCacheAsync_AcceptsSupportedContentTypeAndMatchingMagic(
        string mediaType,
        string expectedExtension,
        byte[] bytes)
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(ImageResponse(bytes, mediaType))));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.NotNull(result);
        Assert.EndsWith(expectedExtension, result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result));
    }

    [Fact]
    public async Task GetOrCacheAsync_CacheHitDoesNotIssueSecondRequest()
    {
        using var directory = new TemporaryDirectory();
        var requests = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(ImageResponse(PngBytes(), "image/png"));
        }));
        using var cache = CreateCache(directory.Path, client);

        var first = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);
        var second = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task GetOrCacheAsync_TransientServerFailureRetriesExactlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var requests = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(ImageResponse(PngBytes(), "image/png"));
        }));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task GetOrCacheAsync_PermanentClientFailureDoesNotRetry()
    {
        using var directory = new TemporaryDirectory();
        var requests = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.Null(result);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task GetOrCacheAsync_ConcurrentSameUriSharesOneDownload()
    {
        using var directory = new TemporaryDirectory();
        var requests = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new StubHandler(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref requests);
            await release.Task.WaitAsync(cancellationToken);
            return ImageResponse(PngBytes(), "image/png");
        }));
        using var cache = CreateCache(directory.Path, client);

        var first = cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);
        var second = cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);
        await Task.Delay(50);
        release.TrySetResult();

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.NotNull(result));
        Assert.Equal(results[0], results[1]);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task GetOrCacheAsync_LimitsConcurrentDownloadsToThree()
    {
        using var directory = new TemporaryDirectory();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var threeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var started = 0;
        using var client = new HttpClient(new StubHandler(async (_, cancellationToken) =>
        {
            var nowActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, nowActive);
            if (Interlocked.Increment(ref started) == 3)
            {
                threeStarted.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return ImageResponse(PngBytes(), "image/png");
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }));
        using var cache = CreateCache(directory.Path, client);

        var operations = Enumerable.Range(0, 4)
            .Select(index => cache.GetOrCacheAsync(
                OnlineModpackProvider.Modrinth,
                new Uri($"https://images.example.test/project/{index}.png")))
            .ToArray();
        await threeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, Volatile.Read(ref started));
        Assert.Equal(3, Volatile.Read(ref maximumActive));
        release.TrySetResult();

        var results = await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.NotNull(result));
        Assert.Equal(4, Volatile.Read(ref started));
        Assert.Equal(3, Volatile.Read(ref maximumActive));
    }

    [Fact]
    public async Task GetOrCacheAsync_CallerCancellationPropagatesAndLeavesNoPartialFile()
    {
        using var directory = new TemporaryDirectory();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new StubHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }));
        using var cache = CreateCache(directory.Path, client);
        using var cancellation = new CancellationTokenSource();

        var operation = cache.GetOrCacheAsync(
            OnlineModpackProvider.Modrinth,
            ApprovedUri,
            cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        var cacheDirectory = Path.Combine(directory.Path, "cache", "online-modpack-artwork");
        Assert.False(Directory.Exists(cacheDirectory)
                     && Directory.EnumerateFiles(cacheDirectory, "*.tmp").Any());
    }

    [Fact]
    public async Task GetOrCacheAsync_OrdinaryNetworkFailureReturnsNull()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new StubHandler((_, _) =>
            throw new HttpRequestException("offline")));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetOrCacheAsync_InternalHttpTimeoutReturnsNull()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new StubHandler((_, _) =>
            throw new OperationCanceledException("HTTP timeout")));
        using var cache = CreateCache(directory.Path, client);

        var result = await cache.GetOrCacheAsync(OnlineModpackProvider.Modrinth, ApprovedUri);

        Assert.Null(result);
    }

    public static TheoryData<string, string, byte[]> SupportedImages => new()
    {
        { "image/png", ".png", PngBytes() },
        { "image/jpeg", ".jpg", JpegBytes() },
        { "image/webp", ".webp", WebPBytes() },
        { "image/webp", ".webp", LosslessWebPBytes() },
        { "image/gif", ".gif", "GIF89a\x01\0\x01\0"u8.ToArray() }
    };

    private static OnlineModpackArtworkCache CreateCache(
        string root,
        HttpClient client,
        OnlineModpackProvider provider = OnlineModpackProvider.Modrinth)
    {
        IReadOnlyDictionary<OnlineModpackProvider, IReadOnlyCollection<string>> hosts =
            new Dictionary<OnlineModpackProvider, IReadOnlyCollection<string>>
            {
                [provider] = ["images.example.test"]
            };
        return new OnlineModpackArtworkCache(
            new ApplicationPaths(root),
            client,
            new OnlineModpackArtworkUriPolicy(hosts));
    }

    private static HttpResponseMessage ImageResponse(byte[] bytes, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    private static byte[] PngBytes(int width = 1, int height = 1)
    {
        var bytes = new byte[24];
        new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(bytes, 0);
        bytes[11] = 0x0d;
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        WriteBigEndian32(bytes.AsSpan(16, 4), width);
        WriteBigEndian32(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private static byte[] JpegBytes() =>
    [
        0xff, 0xd8,
        0xff, 0xc0, 0x00, 0x11, 0x08,
        0x00, 0x01,
        0x00, 0x01,
        0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00
    ];

    private static byte[] WebPBytes()
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes.AsSpan(0, 4));
        "WEBP"u8.CopyTo(bytes.AsSpan(8, 4));
        "VP8X"u8.CopyTo(bytes.AsSpan(12, 4));
        bytes[16] = 10;
        return bytes;
    }

    private static byte[] LosslessWebPBytes()
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes.AsSpan(0, 4));
        "WEBP"u8.CopyTo(bytes.AsSpan(8, 4));
        "VP8L"u8.CopyTo(bytes.AsSpan(12, 4));
        bytes[20] = 0x2f;
        return bytes;
    }

    private static void WriteBigEndian32(Span<byte> destination, int value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current
                || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await responseFactory(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mcsv-modpack-artwork-cache-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
