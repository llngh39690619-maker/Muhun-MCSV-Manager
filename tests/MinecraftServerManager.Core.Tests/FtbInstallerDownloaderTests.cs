using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class FtbInstallerDownloaderTests
{
    [Fact]
    public async Task FailClosedSignatureVerifier_AlwaysRejectsExecutable()
    {
        var verifier = new FtbFailClosedExecutableSignatureVerifier();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync("ftb-server-windows-amd64.exe"));

        Assert.Contains("Authenticode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadLatestWindowsX64Async_VerifiesAllDigestsSignatureAndChinesePath()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installerBytes = Encoding.UTF8.GetBytes("MZ\0fake signed FTB installer payload");
        var fixture = FtbReleaseFixture.Create(installerBytes);
        var verifier = new RecordingSignatureVerifier();
        using var client = new HttpClient(fixture.CreateHandler());
        var downloader = new FtbInstallerDownloader(
            client,
            "Muhun-MCSV-Manager.Tests/1.0",
            verifier);
        var destination = Path.Combine(temporaryDirectory.Path, "工具 快取", "FTB 官方安裝器.exe");

        var result = await downloader.DownloadLatestWindowsX64Async(destination);

        Assert.Equal("v9.9.9", result.ReleaseTag);
        Assert.Equal(fixture.InstallerHash.ToLowerInvariant(), result.Sha256);
        Assert.Equal(installerBytes.Length, result.Size);
        Assert.Equal(installerBytes, await File.ReadAllBytesAsync(destination));
        var verifiedPartial = Assert.Single(verifier.VerifiedPaths);
        Assert.StartsWith(destination + ".", verifiedPartial, StringComparison.Ordinal);
        Assert.EndsWith(".partial", verifiedPartial, StringComparison.Ordinal);
        Assert.Contains(fixture.Requests, uri => uri.Host == "api.github.com");
        Assert.Contains(fixture.Requests, uri => uri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal));
        Assert.Contains(fixture.Requests, uri => uri.AbsolutePath.EndsWith(".exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadLatestWindowsX64Async_HashMismatchRemovesAllPartialFiles()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = FtbReleaseFixture.Create(
            Encoding.UTF8.GetBytes("actual installer"),
            advertisedInstallerHash: new string('0', 64));
        using var client = new HttpClient(fixture.CreateHandler());
        var downloader = new FtbInstallerDownloader(
            client,
            "Muhun-MCSV-Manager.Tests/1.0",
            new RecordingSignatureVerifier());
        var destination = Path.Combine(temporaryDirectory.Path, "installer.exe");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadLatestWindowsX64Async(destination));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "*.partial"));
    }

    [Fact]
    public async Task DownloadLatestWindowsX64Async_SignatureFailureRemovesPartialAndDestination()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = FtbReleaseFixture.Create(Encoding.UTF8.GetBytes("unsigned installer"));
        using var client = new HttpClient(fixture.CreateHandler());
        var downloader = new FtbInstallerDownloader(
            client,
            "Muhun-MCSV-Manager.Tests/1.0",
            new RecordingSignatureVerifier(_ => throw new InvalidDataException("signature invalid")));
        var destination = Path.Combine(temporaryDirectory.Path, "installer.exe");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadLatestWindowsX64Async(destination));

        Assert.Contains("signature", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "*.partial"));
    }

    [Fact]
    public async Task DownloadLatestWindowsX64Async_RejectsUnapprovedFinalRedirect()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = FtbReleaseFixture.Create(Encoding.UTF8.GetBytes("installer"));
        fixture.RedirectInstallerTo = new Uri("https://attacker.example/installer.exe");
        using var client = new HttpClient(fixture.CreateHandler());
        var downloader = new FtbInstallerDownloader(
            client,
            "Muhun-MCSV-Manager.Tests/1.0",
            new RecordingSignatureVerifier());
        var destination = Path.Combine(temporaryDirectory.Path, "installer.exe");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadLatestWindowsX64Async(destination));

        Assert.Contains("未核准", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadLatestWindowsX64Async_RequiresExactContentLength()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = FtbReleaseFixture.Create(
            Encoding.UTF8.GetBytes("installer"),
            advertisedInstallerSize: 999);
        using var client = new HttpClient(fixture.CreateHandler());
        var downloader = new FtbInstallerDownloader(
            client,
            "Muhun-MCSV-Manager.Tests/1.0",
            new RecordingSignatureVerifier());

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadLatestWindowsX64Async(Path.Combine(temporaryDirectory.Path, "installer.exe")));

        Assert.Contains("Content-Length", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadLatestWindowsX64Async_ExistingDestinationIsNeverOverwritten()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var destination = Path.Combine(temporaryDirectory.Path, "existing installer.exe");
        await File.WriteAllTextAsync(destination, "user-owned");
        var fixture = FtbReleaseFixture.Create(Encoding.UTF8.GetBytes("new installer"));
        using var client = new HttpClient(fixture.CreateHandler());
        var downloader = new FtbInstallerDownloader(
            client,
            "Muhun-MCSV-Manager.Tests/1.0",
            new RecordingSignatureVerifier());

        await Assert.ThrowsAsync<IOException>(() =>
            downloader.DownloadLatestWindowsX64Async(destination));

        Assert.Equal("user-owned", await File.ReadAllTextAsync(destination));
        Assert.Empty(fixture.Requests);
    }

    private sealed class RecordingSignatureVerifier(
        Func<string, Task>? verify = null) : IFtbExecutableSignatureVerifier
    {
        public List<string> VerifiedPaths { get; } = [];

        public async Task VerifyAsync(string executablePath, CancellationToken cancellationToken = default)
        {
            Assert.True(File.Exists(executablePath));
            VerifiedPaths.Add(executablePath);
            if (verify is not null)
            {
                await verify(executablePath);
            }
        }
    }

    private sealed class FtbReleaseFixture
    {
        private const string InstallerName = "ftb-server-windows-amd64.exe";
        private readonly byte[] _installerBytes;
        private readonly byte[] _checksumBytes;
        private readonly byte[] _releaseBytes;

        private FtbReleaseFixture(
            byte[] installerBytes,
            string advertisedInstallerHash,
            long advertisedInstallerSize)
        {
            _installerBytes = installerBytes;
            InstallerHash = advertisedInstallerHash;
            _checksumBytes = Encoding.UTF8.GetBytes(advertisedInstallerHash.ToLowerInvariant() + "\n");
            var checksumDigest = Convert.ToHexString(SHA256.HashData(_checksumBytes)).ToLowerInvariant();
            var release = new
            {
                tag_name = "v9.9.9",
                draft = false,
                prerelease = false,
                assets = new object[]
                {
                    new
                    {
                        name = InstallerName,
                        size = advertisedInstallerSize,
                        browser_download_url =
                            "https://github.com/FTBTeam/FTB-Server-Installer/releases/download/v9.9.9/"
                            + InstallerName,
                        digest = "sha256:" + advertisedInstallerHash.ToLowerInvariant(),
                    },
                    new
                    {
                        name = InstallerName + ".sha256",
                        size = _checksumBytes.LongLength,
                        browser_download_url =
                            "https://github.com/FTBTeam/FTB-Server-Installer/releases/download/v9.9.9/"
                            + InstallerName + ".sha256",
                        digest = "sha256:" + checksumDigest,
                    },
                },
            };
            _releaseBytes = JsonSerializer.SerializeToUtf8Bytes(release);
        }

        public string InstallerHash { get; }

        public List<Uri> Requests { get; } = [];

        public Uri? RedirectInstallerTo { get; set; }

        public static FtbReleaseFixture Create(
            byte[] installerBytes,
            string? advertisedInstallerHash = null,
            long? advertisedInstallerSize = null)
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(installerBytes));
            return new FtbReleaseFixture(
                installerBytes,
                advertisedInstallerHash ?? actualHash,
                advertisedInstallerSize ?? installerBytes.LongLength);
        }

        public HttpMessageHandler CreateHandler() => new FtbDownloadStubHandler(request =>
        {
            Requests.Add(request.RequestUri!);
            HttpResponseMessage response;
            if (request.RequestUri!.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                response = BytesResponse(_releaseBytes, "application/json");
            }
            else if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
            {
                response = BytesResponse(_checksumBytes);
            }
            else if (request.RequestUri.AbsolutePath.EndsWith(".exe", StringComparison.Ordinal))
            {
                response = BytesResponse(_installerBytes);
                if (RedirectInstallerTo is not null)
                {
                    response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, RedirectInstallerTo);
                }
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return response;
        });

        private static HttpResponseMessage BytesResponse(
            byte[] bytes,
            string contentType = "application/octet-stream")
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class FtbDownloadStubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
