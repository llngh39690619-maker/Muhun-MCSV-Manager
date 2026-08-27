using System.Net;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class CurseForgeModpackDownloadTests
{
    private const string ApiKey = "download-key-that-must-never-leak";
    private const string UserAgent = "MuhunMCSVManager.Tests/1.0";

    [Fact]
    public async Task Download_PrefersSha1_VerifiesFile_AndNeverSendsApiKeyToCdn()
    {
        var payload = Encoding.UTF8.GetBytes("official CurseForge server pack");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload));
        var md5 = Convert.ToHexString(MD5.HashData(payload));
        var hashes = $$"""
            [
              { "value": "{{md5.ToLowerInvariant()}}", "algo": 2 },
              { "value": "{{sha1.ToLowerInvariant()}}", "algo": 1 }
            ]
            """;
        var apiRequests = new List<CurseForgeModpackProviderTests.RequestSnapshot>();
        var cdnRequests = new List<CurseForgeModpackProviderTests.RequestSnapshot>();
        using var apiClient = CreateApiClient(payload.Length, hashes, apiRequests);
        using var downloadClient = new HttpClient(new CurseForgeModpackProviderTests.StubHandler(request =>
        {
            cdnRequests.Add(CurseForgeModpackProviderTests.RequestSnapshot.Capture(request));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        }));
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);
        var directory = CreateTemporaryDirectory();
        var destination = Path.Combine(directory, "server-pack.zip");

        try
        {
            var result = await provider.DownloadServerPackAsync(ApiKey, 100, 201, destination);

            Assert.Equal(CurseForgeFileHashAlgorithm.Sha1, result.HashAlgorithm);
            Assert.Equal(sha1, result.Hash);
            Assert.Equal(payload.Length, result.Size);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));

            Assert.Equal(3, apiRequests.Count);
            Assert.All(apiRequests, request => Assert.Equal(ApiKey, Assert.Single(request.ApiKeys)));
            var cdnRequest = Assert.Single(cdnRequests);
            Assert.Equal("/server-pack.zip", cdnRequest.Path);
            Assert.Empty(cdnRequest.ApiKeys);
            Assert.DoesNotContain(ApiKey, cdnRequest.OriginalUri, StringComparison.Ordinal);
            Assert.False(apiClient.DefaultRequestHeaders.Contains("x-api-key"));
            Assert.False(downloadClient.DefaultRequestHeaders.Contains("x-api-key"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Download_UsesMd5WhenNoValidSha1Exists()
    {
        var payload = Encoding.UTF8.GetBytes("MD5 fallback payload");
        var md5 = Convert.ToHexString(MD5.HashData(payload));
        var hashes = $$"""
            [
              { "value": "not-a-valid-sha1", "algo": 1 },
              { "value": "{{md5.ToLowerInvariant()}}", "algo": 2 }
            ]
            """;
        using var apiClient = CreateApiClient(payload.Length, hashes);
        using var downloadClient = CreateDownloadClient(payload);
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);
        var directory = CreateTemporaryDirectory();
        var destination = Path.Combine(directory, "server-pack.zip");

        try
        {
            var result = await provider.DownloadServerPackAsync(ApiKey, 100, 201, destination);

            Assert.Equal(CurseForgeFileHashAlgorithm.Md5, result.HashAlgorithm);
            Assert.Equal(md5, result.Hash);
            Assert.True(File.Exists(destination));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Download_HashMismatch_RemovesPartialAndDoesNotPublishDestination()
    {
        var payload = Encoding.UTF8.GetBytes("hash mismatch payload");
        var hashes = "[{ \"value\": \"0000000000000000000000000000000000000000\", \"algo\": 1 }]";
        using var apiClient = CreateApiClient(payload.Length, hashes);
        using var downloadClient = CreateDownloadClient(payload);
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);
        var directory = CreateTemporaryDirectory();
        var destination = Path.Combine(directory, "server-pack.zip");

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => provider.DownloadServerPackAsync(ApiKey, 100, 201, destination));

            Assert.Contains("雜湊", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Download_SizeMismatch_RemovesPartialAndDoesNotPublishDestination()
    {
        var payload = Encoding.UTF8.GetBytes("short payload");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload));
        var hashes = $$"""[{ "value": "{{sha1}}", "algo": 1 }]""";
        using var apiClient = CreateApiClient(payload.Length + 10, hashes);
        using var downloadClient = new HttpClient(new CurseForgeModpackProviderTests.StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent(payload)
            }));
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);
        var directory = CreateTemporaryDirectory();
        var destination = Path.Combine(directory, "server-pack.zip");

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => provider.DownloadServerPackAsync(ApiKey, 100, 201, destination));

            Assert.Contains("大小", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Download_Cancellation_RemovesPartialAndDoesNotPublishDestination()
    {
        var payload = RandomNumberGenerator.GetBytes(384 * 1024);
        var sha1 = Convert.ToHexString(SHA1.HashData(payload));
        var hashes = $$"""[{ "value": "{{sha1}}", "algo": 1 }]""";
        using var apiClient = CreateApiClient(payload.Length, hashes);
        using var downloadClient = CreateDownloadClient(payload);
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(value =>
        {
            if (value > 0d) cancellation.Cancel();
        });
        var directory = CreateTemporaryDirectory();
        var destination = Path.Combine(directory, "server-pack.zip");

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => provider.DownloadServerPackAsync(
                    ApiKey,
                    100,
                    201,
                    destination,
                    progress,
                    cancellation.Token));

            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Download_RejectsApiKeyAddedToCdnDefaultHeadersBeforeRequest()
    {
        var payload = Encoding.UTF8.GetBytes("do not send the key");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload));
        var hashes = $$"""[{ "value": "{{sha1}}", "algo": 1 }]""";
        using var apiClient = CreateApiClient(payload.Length, hashes);
        var cdnWasCalled = false;
        using var downloadClient = new HttpClient(new CurseForgeModpackProviderTests.StubHandler(_ =>
        {
            cdnWasCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }));
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);
        downloadClient.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", ApiKey);
        var directory = CreateTemporaryDirectory();
        var destination = Path.Combine(directory, "server-pack.zip");

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.DownloadServerPackAsync(ApiKey, 100, 201, destination));

            Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
            Assert.False(cdnWasCalled);
            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DownloadVerifiedFile_ClientRole_RevalidatesAndPublishesExactSelectedFile()
    {
        var payload = Encoding.UTF8.GetBytes("verified CurseForge client export");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload));
        var hashes = $$"""[{ "value": "{{sha1}}", "algo": 1 }]""";
        var apiRequests = new List<CurseForgeModpackProviderTests.RequestSnapshot>();
        var cdnRequests = new List<CurseForgeModpackProviderTests.RequestSnapshot>();
        using var apiClient = CreateApiClient(
            payload.Length,
            hashes,
            apiRequests,
            isServerPack: false);
        using var downloadClient = new HttpClient(new CurseForgeModpackProviderTests.StubHandler(request =>
        {
            cdnRequests.Add(CurseForgeModpackProviderTests.RequestSnapshot.Capture(request));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        }));
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);
        var directory = CreateTemporaryDirectory();
        var destination = Path.Combine(directory, "client-pack.zip");

        try
        {
            var result = await provider.DownloadVerifiedFileAsync(
                ApiKey,
                100,
                201,
                CurseForgeModpackFileRole.ClientPack,
                destination);

            Assert.Equal(201, result.FileId);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            Assert.Equal(3, apiRequests.Count);
            Assert.All(apiRequests, request => Assert.Equal(ApiKey, Assert.Single(request.ApiKeys)));
            Assert.Empty(Assert.Single(cdnRequests).ApiKeys);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Theory]
    [InlineData(true, CurseForgeModpackFileRole.ClientPack)]
    [InlineData(false, CurseForgeModpackFileRole.ServerPack)]
    public async Task DownloadVerifiedFile_RoleMismatch_FailsBeforeCdn(
        bool apiSaysServerPack,
        CurseForgeModpackFileRole expectedRole)
    {
        var payload = Encoding.UTF8.GetBytes("role mismatch");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload));
        var hashes = $$"""[{ "value": "{{sha1}}", "algo": 1 }]""";
        using var apiClient = CreateApiClient(
            payload.Length,
            hashes,
            isServerPack: apiSaysServerPack);
        var cdnWasCalled = false;
        using var downloadClient = new HttpClient(new CurseForgeModpackProviderTests.StubHandler(_ =>
        {
            cdnWasCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        }));
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);
        var directory = CreateTemporaryDirectory();
        var destination = Path.Combine(directory, "role-mismatch.zip");

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                provider.DownloadVerifiedFileAsync(
                    ApiKey,
                    100,
                    201,
                    expectedRole,
                    destination));

            Assert.Contains("角色", exception.Message, StringComparison.Ordinal);
            Assert.False(cdnWasCalled);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DownloadVerifiedFile_DeclaredPackLargerThanSafetyLimit_FailsBeforeCdn()
    {
        var hashes = "[{ \"value\": \"0000000000000000000000000000000000000000\", \"algo\": 1 }]";
        using var apiClient = CreateApiClient(2L * 1024 * 1024 * 1024 + 1, hashes);
        var cdnWasCalled = false;
        using var downloadClient = new HttpClient(new CurseForgeModpackProviderTests.StubHandler(_ =>
        {
            cdnWasCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var provider = new CurseForgeModpackProvider(apiClient, downloadClient, UserAgent);
        var directory = CreateTemporaryDirectory();
        var destination = Path.Combine(directory, "oversize.zip");

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                provider.DownloadServerPackAsync(ApiKey, 100, 201, destination));

            Assert.Contains("大小", exception.Message, StringComparison.Ordinal);
            Assert.False(cdnWasCalled);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static HttpClient CreateApiClient(
        long fileLength,
        string hashesJson,
        List<CurseForgeModpackProviderTests.RequestSnapshot>? snapshots = null,
        bool isServerPack = true)
        => new(new CurseForgeModpackProviderTests.StubHandler(request =>
        {
            snapshots?.Add(CurseForgeModpackProviderTests.RequestSnapshot.Capture(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/mods/100" => CurseForgeModpackProviderTests.JsonResponse(
                    CurseForgeModpackProviderTests.ProjectResponseJson()),
                "/v1/mods/100/files/201" => CurseForgeModpackProviderTests.JsonResponse(
                    CurseForgeModpackProviderTests.FileResponseJson(
                        201,
                        isServerPack: isServerPack,
                        serverPackFileId: null,
                        hashesJson: hashesJson,
                        fileLength: fileLength)),
                "/v1/mods/100/files/201/download-url" =>
                    CurseForgeModpackProviderTests.JsonResponse(
                        "{ \"data\": \"https://cdn.example.test/server-pack.zip\" }"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

    private static HttpClient CreateDownloadClient(byte[] payload)
        => new(new CurseForgeModpackProviderTests.StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"MuhunMCSVManager-CurseForgeTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        Assert.StartsWith(tempRoot, fullPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MuhunMCSVManager-CurseForgeTests-", fullPath, StringComparison.Ordinal);
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }

    private sealed class CallbackProgress(Action<double> callback) : IProgress<double>
    {
        public void Report(double value) => callback(value);
    }

    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
