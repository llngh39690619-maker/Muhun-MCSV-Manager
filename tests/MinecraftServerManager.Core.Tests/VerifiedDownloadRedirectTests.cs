using System.Net;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class VerifiedDownloadRedirectTests
{
    [Fact]
    public async Task DownloadAsync_FollowsExplicitlyApprovedRedirectAndStillVerifiesPayload()
    {
        var payload = Encoding.UTF8.GetBytes("verified redirected artifact");
        var requested = new List<Uri>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requested.Add(request.RequestUri!);
            return request.RequestUri!.AbsolutePath == "/start"
                ? Redirect("https://cdn.example.test/artifact")
                : Ok(payload);
        }));
        var destination = CreateTemporaryDestination(out var directory);
        try
        {
            await new VerifiedDownloadClient(client).DownloadAsync(
                new Uri("https://origin.example.test/start"),
                destination,
                HashAlgorithmName.SHA256,
                Convert.ToHexString(SHA256.HashData(payload)),
                payload.Length,
                cancellationToken: CancellationToken.None,
                redirectPolicy: static (source, target) =>
                    source.Host == "origin.example.test"
                    && target.Host == "cdn.example.test");

            Assert.Equal(2, requested.Count);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_RejectsRedirectToUntrustedHostAndDeletesPartial()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            Redirect("https://evil.example/artifact")));
        var destination = CreateTemporaryDestination(out var directory);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new VerifiedDownloadClient(client).DownloadAsync(
                    new Uri("https://origin.example.test/start"),
                    destination,
                    HashAlgorithmName.SHA256,
                    new string('0', 64),
                    expectedSize: 1,
                    cancellationToken: CancellationToken.None,
                    redirectPolicy: static (_, target) =>
                        target.Host == "cdn.example.test"));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_RejectsRedirectWhenCallerDidNotProvideAPolicy()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            Redirect("https://cdn.example.test/artifact")));
        var destination = CreateTemporaryDestination(out var directory);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new VerifiedDownloadClient(client).DownloadAsync(
                    new Uri("https://origin.example.test/start"),
                    destination,
                    HashAlgorithmName.SHA256,
                    new string('0', 64),
                    expectedSize: 1,
                    cancellationToken: CancellationToken.None));

            Assert.Contains("does not allow redirects", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_RejectsHttpsDowngradeEvenWhenPolicyWouldApprove()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            Redirect("http://cdn.example.test/artifact")));
        await AssertRejectedAsync(client, static (_, _) => true);
    }

    [Fact]
    public async Task DownloadAsync_RejectsRedirectLoop()
    {
        using var client = new HttpClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/start"
                ? Redirect("https://origin.example.test/next")
                : Redirect("https://origin.example.test/start")));
        await AssertRejectedAsync(client, static (_, _) => true);
    }

    [Fact]
    public async Task DownloadAsync_RejectsMoreThanThreeRedirects()
    {
        using var client = new HttpClient(new StubHandler(request =>
        {
            var current = request.RequestUri!.AbsolutePath == "/start"
                ? 0
                : int.Parse(request.RequestUri.AbsolutePath[4..],
                    System.Globalization.CultureInfo.InvariantCulture);
            return Redirect($"https://origin.example.test/hop{current + 1}");
        }));
        await AssertRejectedAsync(client, static (_, _) => true);
    }

    [Fact]
    public void AdoptiumRedirectPolicy_AllowsOnlyExactOfficialReleaseAssetHop()
    {
        var original = new Uri(
            "https://github.com/adoptium/temurin21-binaries/releases/download/" +
            "jdk-21.0.12.1%2B1/OpenJDK21U-jre_x64_windows_hotspot_21.0.12.1_1.zip");
        var official = new Uri(
            "https://release-assets.githubusercontent.com/github-production-release-asset/" +
            "602574963/artifact?sig=test");

        Assert.True(AdoptiumRuntimeProvider.IsAllowedPackageRedirect(
            original,
            original,
            official));
        Assert.False(AdoptiumRuntimeProvider.IsAllowedPackageRedirect(
            original,
            original,
            new Uri("https://evil.example/github-production-release-asset/1/file")));
        Assert.False(AdoptiumRuntimeProvider.IsAllowedPackageRedirect(
            original,
            original,
            new Uri("http://release-assets.githubusercontent.com/github-production-release-asset/1/file")));
        Assert.False(AdoptiumRuntimeProvider.IsAllowedPackageRedirect(
            original,
            original,
            new Uri("https://release-assets.githubusercontent.com/not-a-release-asset/file")));
    }

    [Fact]
    public async Task AdoptiumProvider_RejectsUntrustedOriginalPackageHost()
    {
        using var client = new HttpClient(new StubHandler(_ => Json("""
            [{
              "release_name": "jdk-21.0.12.1+1",
              "vendor": "eclipse",
              "binary": {
                "image_type": "jre",
                "package": {
                  "name": "OpenJDK21U-jre.zip",
                  "link": "https://evil.example/OpenJDK21U-jre.zip",
                  "size": 1,
                  "checksum": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                }
              }
            }]
            """)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AdoptiumRuntimeProvider(client, "X-MCSV.Tests/1.0")
                .GetLatestPackageAsync(21, CancellationToken.None));
    }

    private static async Task AssertRejectedAsync(
        HttpClient client,
        VerifiedDownloadRedirectPolicy policy)
    {
        var destination = CreateTemporaryDestination(out var directory);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new VerifiedDownloadClient(client).DownloadAsync(
                    new Uri("https://origin.example.test/start"),
                    destination,
                    HashAlgorithmName.SHA256,
                    new string('0', 64),
                    expectedSize: 1,
                    cancellationToken: CancellationToken.None,
                    redirectPolicy: policy));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDestination(out string directory)
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "x-mcsv-verified-redirect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "artifact.partial");
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Redirect)
    {
        Headers = { Location = new Uri(location) },
    };

    private static HttpResponseMessage Ok(byte[] payload) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(payload),
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
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
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
