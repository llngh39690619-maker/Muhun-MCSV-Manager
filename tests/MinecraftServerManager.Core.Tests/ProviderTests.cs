using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Tests;

public sealed class ProviderTests
{
    [Fact]
    public async Task PaperProvider_ParsesGroupedVersionsAndHighestStableBuild()
    {
        var call = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            call++;
            var json = call == 1
                ? """
                  {
                    "project": { "id": "paper", "name": "Paper" },
                    "versions": { "1.21": ["1.21.11", "1.21.10"], "1.20": ["1.20.6"] }
                  }
                  """
                : """
                  [
                    {
                      "id": 12,
                      "channel": "STABLE",
                      "downloads": { "server:default": {
                        "name": "paper-low.jar", "url": "https://example.test/low.jar", "size": 10,
                        "checksums": { "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
                      }}
                    },
                    {
                      "id": 19,
                      "channel": "STABLE",
                      "downloads": { "server:default": {
                        "name": "paper-high.jar", "url": "https://example.test/high.jar", "size": 20,
                        "checksums": { "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
                      }}
                    }
                  ]
                  """;
            return JsonResponse(json);
        }));
        var provider = new PaperDownloadProvider(client, "MinecraftServerManager.Tests/1.0 (tests@example.invalid)");

        var versions = await provider.GetVersionsAsync();
        var build = await provider.GetLatestStableBuildAsync("1.21.11");

        Assert.Equal(["1.21.11", "1.21.10", "1.20.6"], versions);
        Assert.NotNull(build);
        Assert.Equal(19, build.BuildId);
        Assert.Equal("paper-high.jar", build.FileName);
        Assert.Equal(20, build.Size);
    }

    [Fact]
    public async Task AdoptiumProvider_WhenJreIsUnavailable_FallsBackToJdk()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.Query);
            if (request.RequestUri.Query.Contains("image_type=jre", StringComparison.Ordinal))
            {
                return JsonResponse("[]");
            }

            return JsonResponse("""
                [{
                  "release_name": "jdk-16.0.2+7",
                  "vendor": "eclipse",
                  "binary": {
                    "image_type": "jdk",
                    "package": {
                      "name": "OpenJDK16U-jdk.zip",
                      "link": "https://example.test/java16.zip",
                      "size": 123,
                      "checksum": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                    }
                  }
                }]
                """);
        }));
        var provider = new AdoptiumRuntimeProvider(client, "MinecraftServerManager.Tests/1.0 (tests@example.invalid)");

        var package = await provider.GetLatestPackageAsync(16);

        Assert.NotNull(package);
        Assert.Equal("jdk", package.ImageType);
        Assert.Equal(16, package.MajorVersion);
        Assert.Equal(2, requests.Count);
        Assert.Contains(requests, query => query.Contains("image_type=jre", StringComparison.Ordinal));
        Assert.Contains(requests, query => query.Contains("image_type=jdk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdoptiumProvider_JdkQueryNeverFallsBackToJre()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.Query);
            return JsonResponse("""
                [{
                  "release_name": "jdk-21.0.9+10",
                  "vendor": "eclipse",
                  "binary": {
                    "image_type": "jdk",
                    "package": {
                      "name": "OpenJDK21U-jdk.zip",
                      "link": "https://example.test/jdk21.zip",
                      "size": 123,
                      "checksum": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                    }
                  }
                }]
                """);
        }));
        var provider = new AdoptiumRuntimeProvider(
            client,
            "MinecraftServerManager.Tests/1.0");

        var package = await provider.GetLatestJdkPackageAsync(21);

        Assert.NotNull(package);
        Assert.Equal("jdk", package.ImageType);
        Assert.Single(requests);
        Assert.Contains("image_type=jdk", requests[0], StringComparison.Ordinal);
        Assert.DoesNotContain("image_type=jre", requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdoptiumProvider_JdkInstallRejectsArchiveWithoutJavacBeforeExecutingJava()
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateRuntimeArchive(includeJavac: false);
        var sha = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.Host.Equals(
                "api.adoptium.net",
                StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse($$"""
                    [{
                      "release_name": "jdk-21.0.9+10",
                      "vendor": "eclipse",
                      "binary": {
                        "image_type": "jdk",
                        "package": {
                          "name": "OpenJDK21U-jdk.zip",
                          "link": "https://example.test/jdk21.zip",
                          "size": {{archive.Length}},
                          "checksum": "{{sha}}"
                        }
                      }
                    }]
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            };
        }));
        var provider = new AdoptiumRuntimeProvider(
            client,
            "MinecraftServerManager.Tests/1.0");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.InstallJdkAsync(21, Path.Combine(directory.Path, "runtimes")));

        Assert.Contains("javac", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdoptiumProvider_RejectsRedirectedApiMetadata()
    {
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = JsonResponse("[]");
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.adoptium.net/v3/assets/latest/99/hotspot");
            return response;
        }));
        var provider = new AdoptiumRuntimeProvider(client, "MinecraftServerManager.Tests/1.0");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetLatestPackageAsync(21));

        Assert.Contains("redirect", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdoptiumProvider_RejectsOversizedApiResponseBeforeParsing()
    {
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = JsonResponse("[]");
            response.Content.Headers.ContentLength = 16L * 1024 * 1024 + 1;
            return response;
        }));
        var provider = new AdoptiumRuntimeProvider(client, "MinecraftServerManager.Tests/1.0");

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetLatestPackageAsync(21));
    }

    [Fact]
    public async Task AdoptiumProvider_RejectsRedirectingStagingDirectoryWithoutTouchingTarget()
    {
        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "OneDrive 模擬", "runtimes");
        var outside = Path.Combine(directory.Path, "outside");
        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep");
        var staging = Path.Combine(runtimeRoot, ".staging");
        ReparsePointTestHelper.CreateDirectoryLink(staging, outside);
        using var client = new HttpClient(new StubHandler(_ => JsonResponse("""
            [{
              "release_name": "jdk-21.0.1+1",
              "vendor": "eclipse",
              "binary": {
                "image_type": "jre",
                "package": {
                  "name": "OpenJDK21U-jre.zip",
                  "link": "https://example.test/java21.zip",
                  "size": 1,
                  "checksum": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                }
              }
            }]
            """)));
        var provider = new AdoptiumRuntimeProvider(client, "MinecraftServerManager.Tests/1.0");

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                provider.InstallAsync(21, runtimeRoot));
            Assert.Equal("keep", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: false);
            }
        }
    }

    [Fact]
    public async Task VerifiedDownloadClient_WritesOnlyContentMatchingSizeAndHash()
    {
        var bytes = Encoding.UTF8.GetBytes("verified minecraft artifact");
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes));
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var downloader = new VerifiedDownloadClient(client);
        var directory = Path.Combine(Path.GetTempPath(), "msm-provider-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "artifact.partial");

        try
        {
            await downloader.DownloadAsync(
                new Uri("https://example.test/artifact"),
                destination,
                HashAlgorithmName.SHA256,
                expectedHash,
                bytes.Length);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifiedDownloadClient_HashMismatchRemovesPartialFile()
    {
        var bytes = Encoding.UTF8.GetBytes("tampered");
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var downloader = new VerifiedDownloadClient(client);
        var directory = Path.Combine(Path.GetTempPath(), "msm-provider-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "artifact.partial");

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
                new Uri("https://example.test/artifact"),
                destination,
                HashAlgorithmName.SHA256,
                new string('0', 64),
                bytes.Length));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifiedDownloadClient_StopsAsSoonAsBodyExceedsExpectedSize()
    {
        var bytes = Encoding.UTF8.GetBytes("larger-than-declared");
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var downloader = new VerifiedDownloadClient(client);
        var directory = Path.Combine(Path.GetTempPath(), "msm-provider-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "artifact.partial");

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
                new Uri("https://example.test/artifact"),
                destination,
                HashAlgorithmName.SHA256,
                Convert.ToHexString(SHA256.HashData(bytes)),
                expectedSize: 4));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ModrinthProvider_UsesSha512AndReportsCompatibleFileChange()
    {
        var directory = Path.Combine(Path.GetTempPath(), "msm-modrinth-test-" + Guid.NewGuid().ToString("N"));
        var plugins = Path.Combine(directory, "plugins");
        Directory.CreateDirectory(plugins);
        var localPath = Path.Combine(plugins, "ExamplePlugin.jar");
        var localBytes = Encoding.UTF8.GetBytes("current plugin bytes");
        await File.WriteAllBytesAsync(localPath, localBytes);
        var localHash = Convert.ToHexString(SHA512.HashData(localBytes)).ToLowerInvariant();
        var candidateHash = new string('d', 128);
        var requestBodies = new List<string>();

        using var client = new HttpClient(new StubHandler(request =>
        {
            requestBodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var isUpdate = request.RequestUri!.AbsolutePath.EndsWith("/update", StringComparison.Ordinal);
            var json = isUpdate
                ? $$$"""
                    { "{{{localHash}}}": {
                      "project_id": "project123", "version_number": "2.0.0",
                      "files": [{
                        "primary": true, "filename": "ExamplePlugin-2.0.0.jar",
                        "url": "https://cdn.example.test/plugin.jar", "size": 456,
                        "hashes": { "sha512": "{{{candidateHash}}}" }
                      }]
                    }}
                    """
                : $$$"""
                    { "{{{localHash}}}": {
                      "project_id": "project123", "version_number": "1.0.0", "files": []
                    }}
                    """;
            return JsonResponse(json);
        }));
        var provider = new ModrinthUpdateProvider(client, "MinecraftServerManager.Tests/1.0 (tests@example.invalid)");

        try
        {
            var result = await provider.CheckUpdatesAsync(new ServerInstance
            {
                DirectoryPath = directory,
                CoreType = CoreType.Paper,
                MinecraftVersion = "1.21.1"
            });

            var update = Assert.Single(result);
            Assert.True(update.IsRecognized);
            Assert.True(update.IsUpdateAvailable);
            Assert.Equal("1.0.0", update.CurrentVersion);
            Assert.Equal("2.0.0", update.LatestVersion);
            Assert.Equal(candidateHash, update.DownloadSha512);
            Assert.Contains(requestBodies, body => body.Contains("\"algorithm\":\"sha512\"", StringComparison.Ordinal));
            Assert.Contains(requestBodies, body => body.Contains("\"paper\"", StringComparison.Ordinal));
            Assert.Contains(requestBodies, body => body.Contains("\"1.21.1\"", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static byte[] CreateRuntimeArchive(bool includeJavac)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "jdk-21/bin/java.exe", "not executed");
            if (includeJavac)
            {
                WriteEntry(archive, "jdk-21/bin/javac.exe", "not executed");
            }
        }

        return stream.ToArray();

        static void WriteEntry(ZipArchive archive, string name, string value)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(value);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
