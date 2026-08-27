using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class CloudflaredBootstrapServiceTests
{
    private const string Tag = "2026.8.1";
    private const string AssetName = CloudflaredBootstrapService.WindowsAmd64AssetName;
    private static readonly Uri BrowserDownloadUri = new(
        $"https://github.com/cloudflare/cloudflared/releases/download/{Tag}/{AssetName}");

    [Fact]
    public async Task MissingDigest_FailsClosedBeforeDownloading()
    {
        using var directory = new TemporaryDirectory();
        var artifactRequests = 0;
        using var service = CreateService(
            directory.Path,
            ReleaseJson([1, 2, 3], digest: null),
            _ =>
            {
                artifactRequests++;
                return Binary([1, 2, 3]);
            });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.InstallLatestAsync());

        Assert.Contains("digest", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, artifactRequests);
        Assert.False(File.Exists(Destination(directory.Path)));
    }

    [Fact]
    public async Task DigestMismatch_LeavesExistingExecutableUntouchedAndCleansPartials()
    {
        using var directory = new TemporaryDirectory();
        var original = Encoding.UTF8.GetBytes("previous verified cloudflared");
        var incoming = Encoding.UTF8.GetBytes("tampered download");
        WriteExistingExecutable(directory.Path, original);
        using var service = CreateService(
            directory.Path,
            ReleaseJson(incoming, digest: new string('0', 64)),
            _ => Binary(incoming));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallLatestAsync());

        Assert.Equal(original, await File.ReadAllBytesAsync(Destination(directory.Path)));
        AssertStagingContainsNoFiles(directory.Path);
    }

    [Fact]
    public async Task TruncatedResponse_LeavesExistingExecutableUntouchedAndCleansPartials()
    {
        using var directory = new TemporaryDirectory();
        var original = Encoding.UTF8.GetBytes("previous verified cloudflared");
        var incoming = Encoding.UTF8.GetBytes("short");
        var expectedSize = incoming.Length + 7L;
        WriteExistingExecutable(directory.Path, original);
        using var service = CreateService(
            directory.Path,
            ReleaseJson(incoming, size: expectedSize),
            _ => Binary(incoming, declaredSize: expectedSize));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallLatestAsync());

        Assert.Equal(original, await File.ReadAllBytesAsync(Destination(directory.Path)));
        AssertStagingContainsNoFiles(directory.Path);
    }

    [Fact]
    public async Task CancellationDuringDownload_LeavesExistingExecutableUntouchedAndCleansPartials()
    {
        using var directory = new TemporaryDirectory();
        var original = Encoding.UTF8.GetBytes("previous verified cloudflared");
        var incoming = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        WriteExistingExecutable(directory.Path, original);
        var stream = new GatedReadStream(incoming);
        using var service = CreateService(
            directory.Path,
            ReleaseJson(incoming),
            _ => Binary(stream, incoming.Length));
        using var cancellation = new CancellationTokenSource();

        var operation = service.InstallLatestAsync(cancellationToken: cancellation.Token);
        await stream.FirstRead.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(original, await File.ReadAllBytesAsync(Destination(directory.Path)));
        AssertStagingContainsNoFiles(directory.Path);
    }

    [Fact]
    public async Task VerifiedDownload_ReplacesOnlyAfterCompleteAndFollowsTrustedRedirect()
    {
        using var directory = new TemporaryDirectory();
        var original = Encoding.UTF8.GetBytes("previous verified cloudflared");
        var incoming = Enumerable.Range(0, 192).Select(value => (byte)(255 - value)).ToArray();
        WriteExistingExecutable(directory.Path, original);
        var stream = new GatedReadStream(incoming);
        var redirected = new Uri(
            "https://release-assets.githubusercontent.com/github-production-release-asset/cloudflared.exe?token=test");
        using var service = CreateService(
            directory.Path,
            ReleaseJson(incoming),
            request => request.RequestUri == BrowserDownloadUri
                ? Redirect(redirected)
                : request.RequestUri == redirected
                    ? Binary(stream, incoming.Length)
                    : throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"));

        var operation = service.InstallLatestAsync();
        await stream.FirstRead.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(original, await File.ReadAllBytesAsync(Destination(directory.Path)));

        stream.Release();
        var result = await operation;

        Assert.Equal(Path.GetFullPath(Destination(directory.Path)), result.ExecutablePath);
        Assert.Equal(Tag, result.Version);
        Assert.Equal(incoming, await File.ReadAllBytesAsync(Destination(directory.Path)));
        AssertStagingContainsNoFiles(directory.Path);
    }

    [Theory]
    [InlineData("https://evil.example/cloudflared.exe", false)]
    [InlineData("https://github.com/cloudflare/cloudflared/releases/download/other/cloudflared-windows-amd64.exe", false)]
    [InlineData("https://github.com/cloudflare/cloudflared/releases/download/2026.8.1/cloudflared-windows-arm64.exe", false)]
    [InlineData("https://github.com/cloudflare/cloudflared/releases/download/2026.8.1/cloudflared-windows-amd64.exe", true)]
    public async Task BrowserDownloadUrl_MustBeExactOfficialReleaseAsset(
        string browserDownloadUrl,
        bool isValid)
    {
        using var directory = new TemporaryDirectory();
        var bytes = Encoding.UTF8.GetBytes("official binary");
        var artifactRequests = 0;
        using var service = CreateService(
            directory.Path,
            ReleaseJson(bytes, browserDownloadUrl: browserDownloadUrl),
            _ =>
            {
                artifactRequests++;
                return Binary(bytes);
            });

        if (isValid)
        {
            await service.InstallLatestAsync();
            Assert.Equal(1, artifactRequests);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallLatestAsync());
            Assert.Equal(0, artifactRequests);
        }
    }

    [Fact]
    public async Task NonOfficialAssetApiUrl_IsRejectedBeforeDownloading()
    {
        using var directory = new TemporaryDirectory();
        var bytes = Encoding.UTF8.GetBytes("official binary");
        var artifactRequests = 0;
        using var service = CreateService(
            directory.Path,
            ReleaseJson(bytes, assetApiUrl: "https://api.github.com/repos/other/project/releases/assets/42"),
            _ =>
            {
                artifactRequests++;
                return Binary(bytes);
            });

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallLatestAsync());
        Assert.Equal(0, artifactRequests);
    }

    [Fact]
    public async Task RedirectToNonGitHubAssetHost_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var original = Encoding.UTF8.GetBytes("previous verified cloudflared");
        var incoming = Encoding.UTF8.GetBytes("official binary");
        WriteExistingExecutable(directory.Path, original);
        var artifactRequests = 0;
        using var service = CreateService(
            directory.Path,
            ReleaseJson(incoming),
            _ =>
            {
                artifactRequests++;
                return Redirect(new Uri("https://evil.example/cloudflared.exe"));
            });

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallLatestAsync());

        Assert.Equal(1, artifactRequests);
        Assert.Equal(original, await File.ReadAllBytesAsync(Destination(directory.Path)));
        AssertStagingContainsNoFiles(directory.Path);
    }

    [Fact]
    public async Task MetadataClientThatSilentlyRedirects_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var bytes = Encoding.UTF8.GetBytes("official binary");
        var metadata = JsonResponse(ReleaseJson(bytes));
        metadata.RequestMessage = new HttpRequestMessage(
            HttpMethod.Get,
            "https://evil.example/latest");
        using var metadataClient = Client(_ => metadata);
        using var artifactClient = Client(_ => throw new InvalidOperationException());
        using var service = new CloudflaredBootstrapService(
            directory.Path,
            metadataClient,
            artifactClient);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallLatestAsync());
    }

    private static CloudflaredBootstrapService CreateService(
        string applicationRoot,
        string metadataJson,
        Func<HttpRequestMessage, HttpResponseMessage> artifactResponse)
    {
        var metadataClient = Client(_ => JsonResponse(metadataJson));
        var artifactClient = Client(artifactResponse);
        return new CloudflaredBootstrapService(
            applicationRoot,
            metadataClient,
            artifactClient,
            ownsClients: true);
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response)
        => new(new StubHandler(response), disposeHandler: true);

    private static string ReleaseJson(
        byte[] bytes,
        string? digest = "computed",
        long? size = null,
        string? browserDownloadUrl = null,
        string? assetApiUrl = null)
    {
        var sha256 = digest == "computed"
            ? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            : digest;
        return JsonSerializer.Serialize(new
        {
            tag_name = Tag,
            assets = new[]
            {
                new
                {
                    id = 42,
                    name = AssetName,
                    state = "uploaded",
                    size = size ?? bytes.LongLength,
                    digest = sha256 is null ? null : $"sha256:{sha256}",
                    url = assetApiUrl ??
                          "https://api.github.com/repos/cloudflare/cloudflared/releases/assets/42",
                    browser_download_url = browserDownloadUrl ?? BrowserDownloadUri.AbsoluteUri,
                },
            },
        });
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)),
        };

    private static HttpResponseMessage Binary(byte[] bytes, long? declaredSize = null)
        => Binary(new MemoryStream(bytes, writable: false), declaredSize ?? bytes.LongLength);

    private static HttpResponseMessage Binary(Stream stream, long declaredSize)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentLength = declaredSize;
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = AssetName,
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Redirect(Uri destination)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = destination;
        return response;
    }

    private static string Destination(string applicationRoot)
        => Path.Combine(applicationRoot, "tools", "cloudflared", "cloudflared.exe");

    private static void WriteExistingExecutable(string applicationRoot, byte[] bytes)
    {
        var destination = Destination(applicationRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, bytes);
    }

    private static void AssertStagingContainsNoFiles(string applicationRoot)
    {
        var staging = Path.Combine(applicationRoot, "tools", "cloudflared", ".staging");
        if (Directory.Exists(staging))
        {
            Assert.Empty(Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories));
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = response(request);
            result.RequestMessage ??= request;
            return Task.FromResult(result);
        }
    }

    private sealed class GatedReadStream(byte[] contents) : Stream
    {
        private readonly TaskCompletionSource _firstRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offset;
        private int _phase;

        public Task FirstRead => _firstRead.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset >= contents.Length)
            {
                return 0;
            }

            if (_phase++ == 0)
            {
                var firstLength = Math.Min(Math.Max(1, contents.Length / 2), buffer.Length);
                contents.AsMemory(0, firstLength).CopyTo(buffer);
                _offset = firstLength;
                _firstRead.TrySetResult();
                return firstLength;
            }

            await _release.Task.WaitAsync(cancellationToken);
            var length = Math.Min(contents.Length - _offset, buffer.Length);
            contents.AsMemory(_offset, length).CopyTo(buffer);
            _offset += length;
            return length;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => contents.LongLength;
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
