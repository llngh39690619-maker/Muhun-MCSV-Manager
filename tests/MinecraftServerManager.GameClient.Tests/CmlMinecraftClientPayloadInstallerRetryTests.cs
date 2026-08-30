using System.Net;
using System.Text.Json;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class CmlMinecraftClientPayloadInstallerRetryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-cml-phase-tests",
        Guid.NewGuid().ToString("N"));

    public CmlMinecraftClientPayloadInstallerRetryTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task InstallAsync_TransientMetadataFailureRebuildsPhaseUntilSuccess()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var progress = new List<MinecraftClientInstallProgress>();
        var installer = CreateInstaller(
            launcherInstallPhase: (_, _, _, _, _) =>
            {
                attempts++;
                return attempts < 3
                    ? ValueTask.FromException(new HttpRequestException(
                        "temporary",
                        null,
                        HttpStatusCode.ServiceUnavailable))
                    : ValueTask.CompletedTask;
            },
            maximumPhaseAttempts: 3,
            delayAsync: (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var installed = await installer.InstallAsync(
            Request(),
            _root,
            javaExecutablePath: null,
            new InlineProgress<MinecraftClientInstallProgress>(progress.Add));

        Assert.Equal("1.21.1", installed);
        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.Zero, TimeSpan.Zero], delays);
        Assert.Equal(2, progress.Count(value => value.Stage == "retry"));
    }

    [Fact]
    public async Task InstallAsync_PermanentHttpStatusDoesNotRetry()
    {
        var attempts = 0;
        var installer = CreateInstaller(
            launcherInstallPhase: (_, _, _, _, _) =>
            {
                attempts++;
                return ValueTask.FromException(new HttpRequestException(
                    "missing",
                    null,
                    HttpStatusCode.NotFound));
            },
            maximumPhaseAttempts: 4);

        var failure = await Assert.ThrowsAsync<MinecraftClientDownloadException>(() =>
            installer.InstallAsync(Request(), _root, javaExecutablePath: null));

        Assert.Equal(1, attempts);
        Assert.Equal(1, failure.AttemptCount);
        Assert.Equal(HttpStatusCode.NotFound, failure.HttpStatusCode);
        Assert.Equal(MinecraftClientDownloadFailureKind.HttpStatus, failure.FailureKind);
        Assert.Equal("launcher-metadata", failure.Stage);
    }

    [Fact]
    public async Task InstallAsync_CallerCancellationDoesNotRetryOrWrap()
    {
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var installer = CreateInstaller(
            launcherInstallPhase: (_, _, _, _, token) =>
            {
                attempts++;
                return ValueTask.FromCanceled(token);
            },
            maximumPhaseAttempts: 4);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.InstallAsync(
                Request(),
                _root,
                javaExecutablePath: null,
                cancellationToken: cancellation.Token));

        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task InstallAsync_ExhaustedAtomicFileFailureIsNotMultipliedByPhaseRetries()
    {
        var attempts = 0;
        var inner = new HttpRequestException(
            "temporary",
            null,
            HttpStatusCode.ServiceUnavailable);
        var expected = new MinecraftClientDownloadException(
            4,
            "resources.download.minecraft.net",
            HttpStatusCode.ServiceUnavailable,
            MinecraftClientDownloadFailureKind.HttpStatus,
            "game-file",
            inner);
        var installer = CreateInstaller(
            launcherInstallPhase: (_, _, _, _, _) =>
            {
                attempts++;
                return ValueTask.FromException(expected);
            },
            maximumPhaseAttempts: 4);

        var failure = await Assert.ThrowsAsync<MinecraftClientDownloadException>(() =>
            installer.InstallAsync(Request(), _root, javaExecutablePath: null));

        Assert.Same(expected, failure);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task InstallAsync_LauncherOutOfMemoryGraphBubblesOriginalWithoutRetry()
    {
        var attempts = 0;
        var expected = new OutOfMemoryException("sensitive launcher diagnostic");
        var installer = CreateInstaller(
            launcherInstallPhase: (_, _, _, _, _) =>
            {
                attempts++;
                return ValueTask.FromException(new AggregateException(
                    new IOException("launcher wrapper", expected)));
            },
            maximumPhaseAttempts: 4);

        var error = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            installer.InstallAsync(Request(), _root, javaExecutablePath: null));

        Assert.Same(expected, error);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(MinecraftClientLoader.Fabric)]
    [InlineData(MinecraftClientLoader.Quilt)]
    public async Task InstallAsync_TransientLoaderProfileFailureRetriesBeforeFinalPhase(
        MinecraftClientLoader loader)
    {
        var profileAttempts = 0;
        var launcherVersions = new List<string>();
        var progress = new List<MinecraftClientInstallProgress>();
        var installer = CreateInstaller(
            launcherInstallPhase: (_, versionId, _, _, _) =>
            {
                launcherVersions.Add(versionId);
                return ValueTask.CompletedTask;
            },
            maximumPhaseAttempts: 3,
            loaderProfileInstall: (actualLoader, _, _, _) =>
            {
                Assert.Equal(loader, actualLoader);
                profileAttempts++;
                return profileAttempts < 3
                    ? Task.FromException<string>(new HttpRequestException(
                        "temporary",
                        null,
                        HttpStatusCode.ServiceUnavailable))
                    : Task.FromResult("managed-loader-profile");
            });

        var installed = await installer.InstallAsync(
            Request(loader),
            Path.Combine(_root, loader.ToString()),
            javaExecutablePath: null,
            new InlineProgress<MinecraftClientInstallProgress>(progress.Add));

        Assert.Equal("managed-loader-profile", installed);
        Assert.Equal(3, profileAttempts);
        Assert.Equal(["1.21.1", "managed-loader-profile"], launcherVersions);
        Assert.Equal(2, progress.Count(value => value.Stage == "retry"));
    }

    [Fact]
    public async Task InstallAsync_TruncatedLoaderProfileJsonRetries()
    {
        var profileAttempts = 0;
        var installer = CreateInstaller(
            launcherInstallPhase: (_, _, _, _, _) => ValueTask.CompletedTask,
            maximumPhaseAttempts: 2,
            loaderProfileInstall: (_, _, _, _) =>
            {
                profileAttempts++;
                return profileAttempts == 1
                    ? Task.FromException<string>(new JsonException("truncated"))
                    : Task.FromResult("fabric-profile-id");
            });

        var installed = await installer.InstallAsync(
            Request(MinecraftClientLoader.Fabric),
            Path.Combine(_root, "fabric-json"),
            javaExecutablePath: null);

        Assert.Equal("fabric-profile-id", installed);
        Assert.Equal(2, profileAttempts);
    }

    [Theory]
    [InlineData(MinecraftClientLoader.Fabric, "fabric-profile", "meta.fabricmc.net")]
    [InlineData(MinecraftClientLoader.Quilt, "quilt-profile", "meta.quiltmc.org")]
    public async Task InstallAsync_PermanentLoaderProfileHttpFailureDoesNotRetry(
        MinecraftClientLoader loader,
        string expectedStage,
        string expectedHost)
    {
        var profileAttempts = 0;
        var installer = CreateInstaller(
            launcherInstallPhase: (_, _, _, _, _) => ValueTask.CompletedTask,
            maximumPhaseAttempts: 4,
            loaderProfileInstall: (_, _, _, _) =>
            {
                profileAttempts++;
                return Task.FromException<string>(new HttpRequestException(
                    "missing",
                    null,
                    HttpStatusCode.NotFound));
            });

        var error = await Assert.ThrowsAsync<MinecraftClientDownloadException>(() =>
            installer.InstallAsync(
                Request(loader),
                Path.Combine(_root, loader + "-missing"),
                javaExecutablePath: null));

        Assert.Equal(1, profileAttempts);
        Assert.Equal(1, error.AttemptCount);
        Assert.Equal(expectedStage, error.Stage);
        Assert.Equal(expectedHost, error.Host);
        Assert.Equal(HttpStatusCode.NotFound, error.HttpStatusCode);
    }

    [Fact]
    public async Task InstallAsync_LoaderProfileDiskAndPermissionFailuresDoNotRetry()
    {
        foreach (var permanentError in new Exception[]
                 {
                     new IOException("disk failure"),
                     new UnauthorizedAccessException("permission failure"),
                     new InvalidDataException("semantic profile failure"),
                 })
        {
            var profileAttempts = 0;
            var installer = CreateInstaller(
                launcherInstallPhase: (_, _, _, _, _) => ValueTask.CompletedTask,
                maximumPhaseAttempts: 4,
                loaderProfileInstall: (_, _, _, _) =>
                {
                    profileAttempts++;
                    return Task.FromException<string>(permanentError);
                });

            var error = await Assert.ThrowsAsync<MinecraftClientDownloadException>(() =>
                installer.InstallAsync(
                    Request(MinecraftClientLoader.Fabric),
                    Path.Combine(_root, Guid.NewGuid().ToString("N")),
                    javaExecutablePath: null));

            Assert.Equal(1, profileAttempts);
            Assert.Equal(1, error.AttemptCount);
            Assert.Equal("fabric-profile", error.Stage);
        }
    }

    [Fact]
    public async Task InstallAsync_CancellationAfterLoaderProfileStopsBeforeFinalPhase()
    {
        using var cancellation = new CancellationTokenSource();
        var launcherCalls = 0;
        var profileCalls = 0;
        var installer = CreateInstaller(
            launcherInstallPhase: (_, _, _, _, _) =>
            {
                launcherCalls++;
                return ValueTask.CompletedTask;
            },
            maximumPhaseAttempts: 4,
            loaderProfileInstall: (_, _, _, _) =>
            {
                profileCalls++;
                cancellation.Cancel();
                return Task.FromResult("fabric-profile-id");
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.InstallAsync(
                Request(MinecraftClientLoader.Fabric),
                Path.Combine(_root, "cancel-after-profile"),
                javaExecutablePath: null,
                cancellationToken: cancellation.Token));

        Assert.Equal(1, launcherCalls);
        Assert.Equal(1, profileCalls);
    }

    [Fact]
    public async Task InstallAsync_LoaderProfileOutOfMemoryGraphBubblesOriginalWithoutRetry()
    {
        var launcherCalls = 0;
        var profileCalls = 0;
        var expected = new OutOfMemoryException("sensitive profile diagnostic");
        var installer = CreateInstaller(
            launcherInstallPhase: (_, _, _, _, _) =>
            {
                launcherCalls++;
                return ValueTask.CompletedTask;
            },
            maximumPhaseAttempts: 4,
            loaderProfileInstall: (_, _, _, _) =>
            {
                profileCalls++;
                return Task.FromException<string>(new AggregateException(
                    new IOException("profile wrapper", expected)));
            });

        var error = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            installer.InstallAsync(
                Request(MinecraftClientLoader.Fabric),
                Path.Combine(_root, "profile-oom"),
                javaExecutablePath: null));

        Assert.Same(expected, error);
        Assert.Equal(1, launcherCalls);
        Assert.Equal(1, profileCalls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CmlMinecraftClientPayloadInstaller CreateInstaller(
        CmlLauncherInstallPhase launcherInstallPhase,
        int maximumPhaseAttempts,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        CmlLoaderProfileInstall? loaderProfileInstall = null)
    {
        var client = new HttpClient(new RejectUnexpectedHttpHandler());
        return new CmlMinecraftClientPayloadInstaller(
            client,
            new OfficialMavenClientLoaderInstaller(),
            new CmlDownloadReliabilityOptions
            {
                MaximumFileAttempts = 1,
                MaximumPhaseAttempts = maximumPhaseAttempts,
                MaximumConcurrentChecks = 1,
                MaximumConcurrentDownloads = 1,
                BoundedCapacity = 4,
                RetryDelays = [TimeSpan.Zero],
            },
            launcherInstallPhase,
            delayAsync ?? ((_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }),
            loaderProfileInstall);
    }

    private static MinecraftClientInstallRequest Request(
        MinecraftClientLoader loader = MinecraftClientLoader.Vanilla) =>
        new(
            Guid.NewGuid(),
            "retry-test",
            MinecraftClientEdition.Java,
            "1.21.1",
            loader,
            LoaderVersion: loader == MinecraftClientLoader.Vanilla ? null : "test-loader-version",
            MinecraftClientMemoryMode.Automatic,
            MinimumMemoryMb: 1024,
            MaximumMemoryMb: 4096,
            WindowWidth: 1280,
            WindowHeight: 720,
            FullScreen: false);

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class RejectUnexpectedHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The injected phase must prevent real HTTP calls.");
    }
}
