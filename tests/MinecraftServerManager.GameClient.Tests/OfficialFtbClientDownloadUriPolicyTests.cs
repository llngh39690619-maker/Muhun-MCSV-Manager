using System.Net;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class OfficialFtbClientDownloadUriPolicyTests
{
    private static readonly Uri EdgeUri =
        new("https://edge.forgecdn.net/files/1/2/client.jar");
    private static readonly Uri MediaUri =
        new("https://mediafilez.forgecdn.net/files/1/2/client.jar");

    [Fact]
    public void Policy_AllowsMediafilezOnlyAsTheCheckedRedirectTarget()
    {
        var policy = new OfficialFtbClientDownloadUriPolicy();

        policy.EnsureAllowed(EdgeUri, isRedirect: false);
        policy.EnsureAllowed(MediaUri, isRedirect: true);
        Assert.Throws<InvalidDataException>(() =>
            policy.EnsureAllowed(MediaUri, isRedirect: false));
        Assert.Throws<InvalidDataException>(() => policy.EnsureAllowed(
            new Uri("https://unknown.forgecdn.net/files/1/2/client.jar"),
            isRedirect: true));
        Assert.Throws<InvalidDataException>(() => policy.EnsureAllowed(
            new Uri("http://mediafilez.forgecdn.net/files/1/2/client.jar"),
            isRedirect: true));
    }

    [Fact]
    public async Task Downloader_AllowsExactlyOneCheckedForgeCdnRedirect()
    {
        var bytes = Encoding.UTF8.GetBytes("verified forge cdn payload");
        var transport = new StubTransport(uri => uri == EdgeUri
            ? Redirect(MediaUri)
            : uri == MediaUri
                ? Bytes(bytes)
                : throw new InvalidOperationException($"Unexpected URI: {uri}"));
        var downloader = new ModrinthModpackArtifactDownloader(
            transport,
            new OfficialFtbClientDownloadUriPolicy(),
            maxRedirects: 1);
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "client.jar");

        await downloader.DownloadAsync(
            [EdgeUri],
            destination,
            bytes.LongLength,
            Convert.ToHexString(SHA512.HashData(bytes)),
            Convert.ToHexString(SHA1.HashData(bytes)));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.Equal([EdgeUri, MediaUri], transport.Requests);
    }

    [Fact]
    public async Task Downloader_RejectsASecondForgeCdnRedirectAndLeavesNoArtifact()
    {
        var second = new Uri("https://mediafilez.forgecdn.net/files/1/2/second.jar");
        var transport = new StubTransport(uri => uri == EdgeUri
            ? Redirect(MediaUri)
            : uri == MediaUri
                ? Redirect(second)
                : throw new InvalidOperationException("A second redirect must not be followed."));
        var downloader = new ModrinthModpackArtifactDownloader(
            transport,
            new OfficialFtbClientDownloadUriPolicy(),
            maxRedirects: 1);
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "client.jar");

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(
            [EdgeUri],
            destination,
            0,
            Convert.ToHexString(SHA512.HashData(Array.Empty<byte>())),
            Convert.ToHexString(SHA1.HashData(Array.Empty<byte>()))));

        Assert.False(File.Exists(destination));
        Assert.Equal([EdgeUri, MediaUri], transport.Requests);
    }

    private static HttpResponseMessage Redirect(Uri location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = location;
        return response;
    }

    private static HttpResponseMessage Bytes(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentLength = bytes.LongLength;
        return response;
    }

    private sealed class StubTransport(Func<Uri, HttpResponseMessage> responseFactory)
        : IModrinthModpackHttpTransport
    {
        public List<Uri> Requests { get; } = [];

        public Task<HttpResponseMessage> GetAsync(
            Uri uri,
            CancellationToken cancellationToken)
        {
            Requests.Add(uri);
            return Task.FromResult(responseFactory(uri));
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "x-mcsv-ftb-redirect-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
