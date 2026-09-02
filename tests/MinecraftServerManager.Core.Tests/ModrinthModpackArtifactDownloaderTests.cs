using System.Net;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class ModrinthModpackArtifactDownloaderTests
{
    [Fact]
    public async Task DownloadFollowsCheckedRedirectAndVerifiesBothHashesAndSize()
    {
        using var temp = new TemporaryDirectory();
        var content = "verified"u8.ToArray();
        var hashes = ModrinthModpackTestFixtures.Hashes(content);
        var policy = new TestUriPolicy();
        var transport = new FixtureTransport((uri, _) => Task.FromResult(
            uri.AbsolutePath == "/start"
                ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://files.test/final") } }
                : FixtureTransport.Bytes(content)));
        var downloader = new ModrinthModpackArtifactDownloader(transport, policy);
        var destination = Path.Combine(temp.Path, "result.bin");

        await downloader.DownloadAsync(
            new[] { new Uri("https://files.test/start") }, destination, content.Length, hashes.Sha512, hashes.Sha1);

        Assert.Equal(content, File.ReadAllBytes(destination));
        Assert.Contains(policy.Checks, check => check.Redirect && check.Uri.AbsolutePath == "/final");
    }

    [Fact]
    public async Task HashMismatchLeavesNoDestinationOrPartialFile()
    {
        using var temp = new TemporaryDirectory();
        var content = "wrong"u8.ToArray();
        var destination = Path.Combine(temp.Path, "bad.bin");
        var downloader = new ModrinthModpackArtifactDownloader(
            new FixtureTransport((_, _) => Task.FromResult(FixtureTransport.Bytes(content))), new TestUriPolicy());
        var reportedBytes = new List<long>();

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(
            new[] { new Uri("https://files.test/bad") },
            destination,
            content.Length,
            new string('0', 128),
            new string('0', 40),
            new SynchronousProgress<long>(reportedBytes.Add)));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.partial-*"));
        Assert.DoesNotContain(content.LongLength, reportedBytes);
    }

    [Fact]
    public async Task DownloadThrottlesByteProgressAndPublishesExactTotalOnlyAfterHashVerification()
    {
        using var temp = new TemporaryDirectory();
        var content = new byte[(4 * 1024 * 1024) + 317];
        Random.Shared.NextBytes(content);
        var hashes = ModrinthModpackTestFixtures.Hashes(content);
        var downloader = new ModrinthModpackArtifactDownloader(
            new FixtureTransport((_, _) => Task.FromResult(FixtureTransport.Bytes(content))),
            new TestUriPolicy());
        var destination = Path.Combine(temp.Path, "progress.bin");
        var reportedBytes = new List<long>();

        await downloader.DownloadAsync(
            new[] { new Uri("https://files.test/progress") },
            destination,
            content.LongLength,
            hashes.Sha512,
            hashes.Sha1,
            new SynchronousProgress<long>(reportedBytes.Add));

        Assert.NotEmpty(reportedBytes);
        Assert.Equal(content.LongLength, reportedBytes[^1]);
        Assert.DoesNotContain(content.LongLength, reportedBytes.Take(reportedBytes.Count - 1));
        Assert.InRange(reportedBytes.Count, 1, 5);
        Assert.Equal(content, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task DownloadFallsBackToNextDeclaredMirror()
    {
        using var temp = new TemporaryDirectory();
        var content = "mirror-two"u8.ToArray();
        var hashes = ModrinthModpackTestFixtures.Hashes(content);
        var requested = new List<string>();
        var downloader = new ModrinthModpackArtifactDownloader(
            new FixtureTransport((uri, _) =>
            {
                requested.Add(uri.AbsolutePath);
                return Task.FromResult(uri.AbsolutePath == "/first"
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : FixtureTransport.Bytes(content));
            }),
            new TestUriPolicy());
        var destination = Path.Combine(temp.Path, "mirror.bin");

        await downloader.DownloadAsync(
            new[] { new Uri("https://files.test/first"), new Uri("https://files.test/second") },
            destination, content.Length, hashes.Sha512, hashes.Sha1);

        Assert.Equal(new[] { "/first", "/second" }, requested);
        Assert.Equal(content, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task DownloadRejectsBodyLargerThanDeclaredSize()
    {
        using var temp = new TemporaryDirectory();
        var content = "too-large"u8.ToArray();
        var hashes = ModrinthModpackTestFixtures.Hashes(content);
        var destination = Path.Combine(temp.Path, "overrun.bin");
        var downloader = new ModrinthModpackArtifactDownloader(
            new FixtureTransport((_, _) => Task.FromResult(FixtureTransport.Bytes(content))), new TestUriPolicy());

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(
            new[] { new Uri("https://files.test/overrun") }, destination,
            content.Length - 1, hashes.Sha512, hashes.Sha1));
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData("http://cdn.modrinth.com/file")]
    [InlineData("https://evil.cdn.modrinth.com/file")]
    [InlineData("https://cdn.modrinth.com:444/file")]
    public void OfficialPolicyRejectsNonExactSecureOrigins(string value)
        => Assert.Throws<InvalidDataException>(() =>
            new OfficialModrinthModpackUriPolicy().EnsureAllowed(new Uri(value), isRedirect: false));

    [Theory]
    [InlineData("https://objects.githubusercontent.com/release/file.jar")]
    [InlineData("https://release-assets.githubusercontent.com/release/file.jar")]
    public void OfficialPolicy_AllowsKnownGithubContentHostsOnlyAsRedirects(string value)
    {
        var policy = new OfficialModrinthModpackUriPolicy();
        var uri = new Uri(value);

        Assert.Throws<InvalidDataException>(() => policy.EnsureAllowed(uri, isRedirect: false));
        policy.EnsureAllowed(uri, isRedirect: true);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
