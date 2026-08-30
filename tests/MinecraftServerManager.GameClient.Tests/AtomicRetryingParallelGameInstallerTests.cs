using System.Net;
using System.Security.Cryptography;
using CmlLib.Core.Files;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class AtomicRetryingParallelGameInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-atomic-cml-tests",
        Guid.NewGuid().ToString("N"));

    public AtomicRetryingParallelGameInstallerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Install_TransientHttpFailureRetriesAndAtomicallyPromotesVerifiedFile()
    {
        var destination = Path.Combine(_root, "client.jar");
        var original = "existing-good-version"u8.ToArray();
        var downloaded = "new-verified-version"u8.ToArray();
        File.WriteAllBytes(destination, original);
        var calls = 0;
        using var client = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            Assert.Equal(original, File.ReadAllBytes(destination));
            return Task.FromResult(calls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Response(downloaded));
        }));
        var installer = CreateInstaller(client, maximumFileAttempts: 2);

        await installer.Install(
            [GameFile(destination, downloaded)],
            fileProgress: null,
            byteProgress: null,
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(downloaded, File.ReadAllBytes(destination));
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Install_Sha1MismatchRetriesBeforePromotion()
    {
        var destination = Path.Combine(_root, "library.jar");
        var invalid = "corrupted"u8.ToArray();
        var expected = "verified"u8.ToArray();
        var calls = 0;
        using var client = new HttpClient(new CallbackHandler((_, _) =>
            Task.FromResult(Response(++calls == 1 ? invalid : expected))));
        var installer = CreateInstaller(client, maximumFileAttempts: 2);

        await installer.Install(
            [GameFile(destination, expected)],
            null,
            null,
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(expected, File.ReadAllBytes(destination));
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Install_ExhaustedSha1MismatchPreservesExistingFileAndReturnsSafeDetails()
    {
        var destination = Path.Combine(_root, "asset.bin");
        var original = "last-known-good"u8.ToArray();
        var expected = "expected"u8.ToArray();
        File.WriteAllBytes(destination, original);
        var calls = 0;
        using var client = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response("badbytes"u8.ToArray()));
        }));
        var installer = CreateInstaller(client, maximumFileAttempts: 2);

        var exception = await Record.ExceptionAsync(async () =>
            await installer.Install(
                [GameFile(destination, expected)],
                null,
                null,
                CancellationToken.None));
        var failure = FindDownloadFailure(Assert.IsAssignableFrom<Exception>(exception));

        Assert.Equal(2, calls);
        Assert.Equal(2, failure.AttemptCount);
        Assert.Equal("downloads.example", failure.Host);
        Assert.Null(failure.HttpStatusCode);
        Assert.Equal(MinecraftClientDownloadFailureKind.Sha1Mismatch, failure.FailureKind);
        Assert.Equal("game-file", failure.Stage);
        Assert.DoesNotContain(destination, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/client?token", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, File.ReadAllBytes(destination));
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Install_PermanentHttpStatusDoesNotRetry()
    {
        var destination = Path.Combine(_root, "missing.jar");
        var calls = 0;
        using var client = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        var installer = CreateInstaller(client, maximumFileAttempts: 4);

        var exception = await Record.ExceptionAsync(async () =>
            await installer.Install(
                [GameFile(destination, "body"u8.ToArray())],
                null,
                null,
                CancellationToken.None));
        var failure = FindDownloadFailure(Assert.IsAssignableFrom<Exception>(exception));

        Assert.Equal(1, calls);
        Assert.Equal(1, failure.AttemptCount);
        Assert.Equal(HttpStatusCode.NotFound, failure.HttpStatusCode);
        Assert.Equal(MinecraftClientDownloadFailureKind.HttpStatus, failure.FailureKind);
        Assert.False(File.Exists(destination));
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Install_CallerCancellationDoesNotRetryOrReplaceExistingFile()
    {
        var destination = Path.Combine(_root, "cancelled.jar");
        var original = "keep"u8.ToArray();
        File.WriteAllBytes(destination, original);
        var calls = 0;
        using var started = new ManualResetEventSlim();
        using var client = new HttpClient(new CallbackHandler(async (_, cancellationToken) =>
        {
            calls++;
            started.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Response([]);
        }));
        var installer = CreateInstaller(client, maximumFileAttempts: 4);
        using var cancellation = new CancellationTokenSource();

        var install = installer.Install(
            [GameFile(destination, "replacement"u8.ToArray())],
            null,
            null,
            cancellation.Token).AsTask();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        Assert.Equal(1, calls);
        Assert.Equal(original, File.ReadAllBytes(destination));
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Install_CancellationAfterVerificationDoesNotPromoteOverExistingFile()
    {
        var destination = Path.Combine(_root, "cancelled-before-promotion.jar");
        var original = "keep-this-version"u8.ToArray();
        var replacement = "verified-but-cancelled"u8.ToArray();
        File.WriteAllBytes(destination, original);
        var calls = 0;
        using var client = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(replacement));
        }));
        using var cancellation = new CancellationTokenSource();
        var installer = CreateInstaller(
            client,
            maximumFileAttempts: 4,
            beforePromotionForTesting: cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.Install(
                [GameFile(destination, replacement)],
                null,
                null,
                cancellation.Token).AsTask());

        Assert.Equal(1, calls);
        Assert.Equal(original, File.ReadAllBytes(destination));
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Install_OutOfMemoryGraphIsNotConvertedToDownloadFailure()
    {
        var destination = Path.Combine(_root, "out-of-memory.jar");
        var expected = new OutOfMemoryException("sensitive atomic diagnostic");
        using var client = new HttpClient(new CallbackHandler((_, _) =>
            throw new AggregateException(new IOException("atomic wrapper", expected))));
        var installer = CreateInstaller(client, maximumFileAttempts: 4);

        var error = await Record.ExceptionAsync(async () =>
            await installer.Install(
                [GameFile(destination, "replacement"u8.ToArray())],
                null,
                null,
                CancellationToken.None));

        Assert.NotNull(error);
        Assert.Same(expected, ExceptionGraphSafety.FindOutOfMemory(error));
        Assert.Null(FindDownloadFailureOrDefault(error));
        Assert.False(File.Exists(destination));
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Install_ParallelDownloadsNeverExceedConfiguredLimit()
    {
        var active = 0;
        var maximumObserved = 0;
        using var client = new HttpClient(new CallbackHandler(async (_, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumObserved, current);
            try
            {
                await Task.Delay(75, cancellationToken);
                return Response("x"u8.ToArray());
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }));
        var installer = CreateInstaller(
            client,
            maximumFileAttempts: 1,
            maximumConcurrentDownloads: 2);
        var files = Enumerable.Range(0, 8)
            .Select(index => GameFile(Path.Combine(_root, $"file-{index}.bin"), "x"u8.ToArray()))
            .ToArray();

        await installer.Install(files, null, null, CancellationToken.None);

        Assert.InRange(maximumObserved, 2, 2);
        Assert.Equal(2, installer.MaxDownloader);
        AssertNoPartialFiles();
    }

    [Fact]
    public void Constructor_DefaultsCapCmlParallelismAndQueueCapacity()
    {
        using var client = new HttpClient(new CallbackHandler((_, _) =>
            throw new InvalidOperationException("No HTTP request is expected.")));

        var installer = new AtomicRetryingParallelGameInstaller(client);

        Assert.Equal(4, installer.MaxChecker);
        Assert.Equal(8, installer.MaxDownloader);
        Assert.Equal(512, installer.BoundedCapacity);
        Assert.True(installer.CheckFileSize);
        Assert.True(installer.CheckFileChecksum);
    }

    [Fact]
    public async Task Install_ValidExistingFileSkipsNetwork()
    {
        var destination = Path.Combine(_root, "already-valid.jar");
        var expected = "already-here"u8.ToArray();
        File.WriteAllBytes(destination, expected);
        var calls = 0;
        using var client = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(expected));
        }));
        var installer = CreateInstaller(client, maximumFileAttempts: 1);

        await installer.Install(
            [GameFile(destination, expected)],
            null,
            null,
            CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.Equal(expected, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task Install_LegacyZeroHashAlwaysRedownloadsAndReplacesPositiveExistingFile()
    {
        var destination = Path.Combine(_root, "legacy.jar");
        var original = "positive-length"u8.ToArray();
        var downloaded = "new-positive-length"u8.ToArray();
        File.WriteAllBytes(destination, original);
        var calls = 0;
        using var client = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(downloaded));
        }));
        var installer = CreateInstaller(client, maximumFileAttempts: 1);
        var file = new GameFile("legacy.jar")
        {
            Path = destination,
            Url = "https://downloads.example/legacy.jar",
            Size = 0,
            Hash = "0",
        };

        await ((CmlLib.Core.Installers.IGameInstaller)installer).Install(
            [file],
            null,
            null,
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(downloaded, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task Install_LegacyZeroHashDownloadsAndRequiresPositiveLength()
    {
        var destination = Path.Combine(_root, "legacy-download.jar");
        var downloaded = "positive"u8.ToArray();
        var calls = 0;
        using var client = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(downloaded));
        }));
        var installer = CreateInstaller(client, maximumFileAttempts: 2);
        var file = new GameFile("legacy-download.jar")
        {
            Path = destination,
            Url = "https://downloads.example/legacy-download.jar",
            Size = 0,
            Hash = "0",
        };

        await installer.Install([file], null, null, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(downloaded, File.ReadAllBytes(destination));
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Install_UnknownSizeZeroByteResponseNeverBecomesOfficialFile()
    {
        var destination = Path.Combine(_root, "empty.jar");
        var calls = 0;
        using var client = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response([]));
        }));
        var installer = CreateInstaller(client, maximumFileAttempts: 2);
        var file = new GameFile("empty.jar")
        {
            Path = destination,
            Url = "https://downloads.example/empty.jar",
            Size = 0,
            Hash = "0",
        };

        var exception = await Record.ExceptionAsync(async () =>
            await installer.Install([file], null, null, CancellationToken.None));
        var failure = FindDownloadFailure(Assert.IsAssignableFrom<Exception>(exception));

        Assert.Equal(2, calls);
        Assert.Equal(MinecraftClientDownloadFailureKind.SizeMismatch, failure.FailureKind);
        Assert.False(File.Exists(destination));
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Install_MetadataAboveSafetyCapIsRejectedBeforeNetworkOrReplacement()
    {
        var destination = Path.Combine(_root, "oversized.jar");
        var original = "keep"u8.ToArray();
        File.WriteAllBytes(destination, original);
        var calls = 0;
        using var client = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response([]));
        }));
        var installer = CreateInstaller(client, maximumFileAttempts: 4);
        var file = new GameFile("oversized.jar")
        {
            Path = destination,
            Url = "https://downloads.example/oversized.jar",
            Size = AtomicRetryingParallelGameInstaller.MaximumGameFileBytes + 1,
            Hash = string.Empty,
        };

        var exception = await Record.ExceptionAsync(async () =>
            await installer.Install([file], null, null, CancellationToken.None));
        var failure = FindDownloadFailure(Assert.IsAssignableFrom<Exception>(exception));

        Assert.Equal(0, calls);
        Assert.Equal(1, failure.AttemptCount);
        Assert.Equal(original, File.ReadAllBytes(destination));
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Install_DeclaredResponseAboveSafetyCapIsRejectedWithoutRetry()
    {
        var destination = Path.Combine(_root, "declared-oversized.jar");
        var calls = 0;
        using var client = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            var response = Response("x"u8.ToArray());
            response.Content.Headers.ContentLength =
                AtomicRetryingParallelGameInstaller.MaximumGameFileBytes + 1;
            return Task.FromResult(response);
        }));
        var installer = CreateInstaller(client, maximumFileAttempts: 4);
        var file = new GameFile("declared-oversized.jar")
        {
            Path = destination,
            Url = "https://downloads.example/declared-oversized.jar",
            Size = 0,
            Hash = "0",
        };

        var exception = await Record.ExceptionAsync(async () =>
            await installer.Install([file], null, null, CancellationToken.None));
        var failure = FindDownloadFailure(Assert.IsAssignableFrom<Exception>(exception));

        Assert.Equal(1, calls);
        Assert.Equal(1, failure.AttemptCount);
        Assert.False(File.Exists(destination));
        AssertNoPartialFiles();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static AtomicRetryingParallelGameInstaller CreateInstaller(
        HttpClient client,
        int maximumFileAttempts,
        int maximumConcurrentDownloads = 4,
        Action? beforePromotionForTesting = null) =>
        new(
            client,
            new CmlDownloadReliabilityOptions
            {
                MaximumFileAttempts = maximumFileAttempts,
                MaximumPhaseAttempts = 1,
                MaximumConcurrentChecks = 2,
                MaximumConcurrentDownloads = maximumConcurrentDownloads,
                BoundedCapacity = 16,
                RetryDelays = [TimeSpan.Zero],
            },
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            beforePromotionForTesting);

    private static GameFile GameFile(string path, byte[] expectedBytes) =>
        new(Path.GetFileName(path))
        {
            Path = path,
            Url = "https://downloads.example/client?token=must-not-leak",
            Size = expectedBytes.LongLength,
            Hash = Convert.ToHexString(SHA1.HashData(expectedBytes)).ToLowerInvariant(),
        };

    private static HttpResponseMessage Response(byte[] body) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };

    private static MinecraftClientDownloadException FindDownloadFailure(Exception exception)
    {
        if (exception is MinecraftClientDownloadException failure)
        {
            return failure;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                try
                {
                    return FindDownloadFailure(inner);
                }
                catch (Xunit.Sdk.XunitException)
                {
                }
            }
        }

        if (exception.InnerException is { } nested)
        {
            return FindDownloadFailure(nested);
        }

        return Assert.IsType<MinecraftClientDownloadException>(exception);
    }

    private static MinecraftClientDownloadException? FindDownloadFailureOrDefault(
        Exception exception)
    {
        if (exception is MinecraftClientDownloadException failure)
        {
            return failure;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions
                .Select(FindDownloadFailureOrDefault)
                .FirstOrDefault(candidate => candidate is not null);
        }

        return exception.InnerException is { } inner
            ? FindDownloadFailureOrDefault(inner)
            : null;
    }

    private void AssertNoPartialFiles() =>
        Assert.Empty(Directory.EnumerateFiles(_root, ".x-mcsv-*.partial", SearchOption.AllDirectories));

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
