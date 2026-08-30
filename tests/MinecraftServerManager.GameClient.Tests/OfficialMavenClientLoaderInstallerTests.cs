using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class OfficialMavenClientLoaderInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-official-loader-tests",
        Guid.NewGuid().ToString("N"));

    public OfficialMavenClientLoaderInstallerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task InstallAsync_HashMismatchNeverRunsInstallerAndDeletesDownloadedBytes()
    {
        var artifactUri = OfficialMavenClientLoaderInstaller.CreateArtifactUri(
            MinecraftClientLoader.Forge,
            "1.21.1",
            "52.1.0");
        var expectedBytes = Encoding.UTF8.GetBytes("expected official bytes");
        var unverifiedBytes = Encoding.UTF8.GetBytes("different unverified bytes");
        var transport = CreateTransport(artifactUri, expectedBytes, unverifiedBytes);
        var runner = new RecordingRunner();
        var installer = CreateRetryingInstaller(transport, runner, maximumAttempts: 4);

        var exception = await Assert.ThrowsAsync<MinecraftClientDownloadException>(() =>
            installer.InstallAsync(
                MinecraftClientLoader.Forge,
                "1.21.1",
                "52.1.0",
                _root,
                Path.Combine(_root, "java.exe")));

        Assert.Equal(4, exception.AttemptCount);
        Assert.Equal(MinecraftClientDownloadFailureKind.Sha256Mismatch, exception.FailureKind);
        Assert.Equal("maven.minecraftforge.net", exception.Host);
        Assert.Equal("loader-installer", exception.Stage);
        Assert.Equal(0, runner.CallCount);
        Assert.Equal(
            [
                artifactUri.AbsoluteUri + ".sha256",
                artifactUri.AbsoluteUri,
                artifactUri.AbsoluteUri,
                artifactUri.AbsoluteUri,
                artifactUri.AbsoluteUri,
            ],
            transport.Requests.Select(uri => uri.AbsoluteUri));
        Assert.Empty(Directory.EnumerateFiles(_root, ".loader-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task InstallAsync_TransientArtifactFailureRetriesDownloadButRunsJavaOnlyOnce()
    {
        var artifactUri = OfficialMavenClientLoaderInstaller.CreateArtifactUri(
            MinecraftClientLoader.Forge,
            "1.21.1",
            "52.1.0");
        var sidecarUri = new Uri(artifactUri.AbsoluteUri + ".sha256");
        var installerBytes = "verified installer"u8.ToArray();
        var sidecarBytes = Encoding.ASCII.GetBytes(Convert.ToHexString(
            SHA256.HashData(installerBytes)));
        var artifactAttempts = 0;
        var transport = new CallbackTransport(uri =>
        {
            if (uri == sidecarUri)
            {
                return CreateResponse(uri, sidecarBytes);
            }

            Assert.Equal(artifactUri, uri);
            artifactAttempts++;
            return artifactAttempts == 1
                ? CreateResponse(uri, [], HttpStatusCode.ServiceUnavailable)
                : CreateResponse(uri, installerBytes);
        });
        var runner = new RecordingRunner((_, instanceDirectory) =>
            WriteProfile(
                instanceDirectory,
                "1.21.1-forge-52.1.0",
                "net.minecraftforge:forge:1.21.1-52.1.0"));
        var installer = CreateRetryingInstaller(transport, runner, maximumAttempts: 3);

        var profile = await installer.InstallAsync(
            MinecraftClientLoader.Forge,
            "1.21.1",
            "52.1.0",
            _root,
            Path.Combine(_root, "java.exe"));

        Assert.Equal("1.21.1-forge-52.1.0", profile);
        Assert.Equal(2, artifactAttempts);
        Assert.Equal(1, runner.CallCount);
        Assert.Equal(
            [sidecarUri, artifactUri, artifactUri],
            transport.Requests);
        Assert.Empty(Directory.EnumerateFiles(_root, ".loader-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task InstallAsync_TransientSidecarFailureRetriesBeforeDownloadingArtifact()
    {
        var artifactUri = OfficialMavenClientLoaderInstaller.CreateArtifactUri(
            MinecraftClientLoader.NeoForge,
            "1.21.1",
            "21.1.102");
        var sidecarUri = new Uri(artifactUri.AbsoluteUri + ".sha256");
        var installerBytes = "verified NeoForge installer"u8.ToArray();
        var sidecarBytes = Encoding.ASCII.GetBytes(Convert.ToHexString(
            SHA256.HashData(installerBytes)));
        var sidecarAttempts = 0;
        var transport = new CallbackTransport(uri =>
        {
            if (uri == sidecarUri)
            {
                sidecarAttempts++;
                return sidecarAttempts == 1
                    ? CreateResponse(uri, [], HttpStatusCode.BadGateway)
                    : CreateResponse(uri, sidecarBytes);
            }

            Assert.Equal(artifactUri, uri);
            return CreateResponse(uri, installerBytes);
        });
        var runner = new RecordingRunner((_, instanceDirectory) =>
            WriteProfile(
                instanceDirectory,
                "1.21.1-neoforge-21.1.102",
                "net.neoforged:neoforge:21.1.102"));
        var installer = CreateRetryingInstaller(transport, runner, maximumAttempts: 3);

        var profile = await installer.InstallAsync(
            MinecraftClientLoader.NeoForge,
            "1.21.1",
            "21.1.102",
            _root,
            Path.Combine(_root, "java.exe"));

        Assert.Equal("1.21.1-neoforge-21.1.102", profile);
        Assert.Equal(2, sidecarAttempts);
        Assert.Equal(1, runner.CallCount);
        Assert.Equal([sidecarUri, sidecarUri, artifactUri], transport.Requests);
    }

    [Fact]
    public async Task InstallAsync_ProcessFailureIsNeverRetried()
    {
        var artifactUri = OfficialMavenClientLoaderInstaller.CreateArtifactUri(
            MinecraftClientLoader.Forge,
            "1.21.1",
            "52.1.0");
        var installerBytes = "verified process failure fixture"u8.ToArray();
        var transport = CreateTransport(artifactUri, installerBytes, installerBytes);
        var runner = new RecordingRunner((_, _) =>
            throw new InvalidOperationException("simulated Java loader failure"));
        var installer = CreateRetryingInstaller(transport, runner, maximumAttempts: 4);

        var error = await Assert.ThrowsAsync<MinecraftClientLoaderProcessException>(() =>
            installer.InstallAsync(
                MinecraftClientLoader.Forge,
                "1.21.1",
                "52.1.0",
                _root,
                Path.Combine(_root, "java.exe")));

        Assert.Equal("loader-process", error.Stage);
        Assert.Equal("maven.minecraftforge.net", error.Host);
        Assert.DoesNotContain("simulated Java loader failure", error.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(1, runner.CallCount);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Empty(Directory.EnumerateFiles(_root, ".loader-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task InstallAsync_ProcessOutOfMemoryIsNeverWrappedOrRetried()
    {
        var artifactUri = OfficialMavenClientLoaderInstaller.CreateArtifactUri(
            MinecraftClientLoader.Forge,
            "1.21.1",
            "52.1.0");
        var installerBytes = "verified process OOM fixture"u8.ToArray();
        var transport = CreateTransport(artifactUri, installerBytes, installerBytes);
        var expected = new OutOfMemoryException("sensitive process diagnostic");
        var runner = new RecordingRunner((_, _) => throw new AggregateException(
            new IOException("process wrapper", expected)));
        var installer = CreateRetryingInstaller(transport, runner, maximumAttempts: 4);

        var error = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            installer.InstallAsync(
                MinecraftClientLoader.Forge,
                "1.21.1",
                "52.1.0",
                _root,
                Path.Combine(_root, "java.exe")));

        Assert.Same(expected, error);
        Assert.Equal(1, runner.CallCount);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Empty(Directory.EnumerateFiles(_root, ".loader-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task InstallAsync_MissingSidecarNeverDownloadsOrRunsInstaller()
    {
        var artifactUri = OfficialMavenClientLoaderInstaller.CreateArtifactUri(
            MinecraftClientLoader.Forge,
            "1.21.1",
            "52.1.0");
        var sidecarUri = new Uri(artifactUri.AbsoluteUri + ".sha256");
        var transport = new StubTransport(new Dictionary<string, ResponseSpec>(StringComparer.Ordinal)
        {
            [sidecarUri.AbsoluteUri] = new(
                Encoding.ASCII.GetBytes("not found"),
                HttpStatusCode.NotFound),
        });
        var runner = new RecordingRunner();
        var installer = new OfficialMavenClientLoaderInstaller(transport, runner);

        var exception = await Assert.ThrowsAsync<MinecraftClientDownloadException>(() =>
            installer.InstallAsync(
                MinecraftClientLoader.Forge,
                "1.21.1",
                "52.1.0",
                _root,
                Path.Combine(_root, "java.exe")));

        Assert.Equal(1, exception.AttemptCount);
        Assert.Equal(HttpStatusCode.NotFound, exception.HttpStatusCode);
        Assert.Equal(MinecraftClientDownloadFailureKind.HttpStatus, exception.FailureKind);
        Assert.Equal("loader-sidecar", exception.Stage);
        Assert.Equal("maven.minecraftforge.net", exception.Host);
        Assert.DoesNotContain(sidecarUri.AbsolutePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.CallCount);
        Assert.Equal(sidecarUri, Assert.Single(transport.Requests));
        Assert.Empty(Directory.EnumerateFiles(_root, ".loader-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task InstallAsync_DownloadOutOfMemoryIsNeverWrappedOrRetried()
    {
        var artifactUri = OfficialMavenClientLoaderInstaller.CreateArtifactUri(
            MinecraftClientLoader.Forge,
            "1.21.1",
            "52.1.0");
        var sidecarUri = new Uri(artifactUri.AbsoluteUri + ".sha256");
        var expected = new OutOfMemoryException("sensitive download diagnostic");
        var transport = new CallbackTransport(_ => throw new AggregateException(
            new IOException("download wrapper", expected)));
        var runner = new RecordingRunner();
        var installer = CreateRetryingInstaller(transport, runner, maximumAttempts: 4);

        var error = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            installer.InstallAsync(
                MinecraftClientLoader.Forge,
                "1.21.1",
                "52.1.0",
                _root,
                Path.Combine(_root, "java.exe")));

        Assert.Same(expected, error);
        Assert.Equal(sidecarUri, Assert.Single(transport.Requests));
        Assert.Equal(0, runner.CallCount);
        Assert.Empty(Directory.EnumerateFiles(_root, ".loader-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task InstallAsync_OnlyPassesSidecarBoundBytesToRunnerAndReturnsBoundProfile()
    {
        var artifactUri = OfficialMavenClientLoaderInstaller.CreateArtifactUri(
            MinecraftClientLoader.Forge,
            "1.21.1",
            "52.1.0");
        var installerBytes = Encoding.UTF8.GetBytes("verified Forge installer fixture");
        var transport = CreateTransport(artifactUri, installerBytes, installerBytes);
        var runner = new RecordingRunner((artifact, instanceDirectory) =>
        {
            Assert.Equal(installerBytes, File.ReadAllBytes(artifact.Path));
            Assert.Equal(SHA256.HashData(installerBytes), artifact.ExpectedSha256);
            Assert.EndsWith(".verified.jar", artifact.Path, StringComparison.Ordinal);
            WriteProfile(
                instanceDirectory,
                "1.21.1-forge-52.1.0",
                "net.minecraftforge:forge:1.21.1-52.1.0");
        });
        var installer = new OfficialMavenClientLoaderInstaller(transport, runner);

        var profileId = await installer.InstallAsync(
            MinecraftClientLoader.Forge,
            "1.21.1",
            "52.1.0",
            _root,
            Path.Combine(_root, "java.exe"));

        Assert.Equal("1.21.1-forge-52.1.0", profileId);
        Assert.Equal(1, runner.CallCount);
        Assert.Empty(Directory.EnumerateFiles(_root, ".loader-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task InstallAsync_MissingInstalledProfileReturnsSafeLoaderVerificationFailure()
    {
        var artifactUri = OfficialMavenClientLoaderInstaller.CreateArtifactUri(
            MinecraftClientLoader.Forge,
            "1.21.1",
            "52.1.0");
        var installerBytes = "verified missing-profile fixture"u8.ToArray();
        var transport = CreateTransport(artifactUri, installerBytes, installerBytes);
        var runner = new RecordingRunner();
        var installer = CreateRetryingInstaller(transport, runner, maximumAttempts: 4);

        var error = await Assert.ThrowsAsync<MinecraftClientLoaderProcessException>(() =>
            installer.InstallAsync(
                MinecraftClientLoader.Forge,
                "1.21.1",
                "52.1.0",
                _root,
                Path.Combine(_root, "java.exe")));

        Assert.Equal("loader-profile-verification", error.Stage);
        Assert.Equal("maven.minecraftforge.net", error.Host);
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal(1, runner.CallCount);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Empty(Directory.EnumerateFiles(_root, ".loader-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task InstallAsync_RedirectResponseNeverRunsInstaller()
    {
        var artifactUri = OfficialMavenClientLoaderInstaller.CreateArtifactUri(
            MinecraftClientLoader.NeoForge,
            "1.21.1",
            "21.1.102");
        var sidecarUri = new Uri(artifactUri.AbsoluteUri + ".sha256");
        var transport = new StubTransport(new Dictionary<string, ResponseSpec>(StringComparer.Ordinal)
        {
            [sidecarUri.AbsoluteUri] = new(
                [],
                HttpStatusCode.Found,
                RedirectLocation: new Uri("https://attacker.invalid/installer.jar.sha256")),
        });
        var runner = new RecordingRunner();
        var installer = new OfficialMavenClientLoaderInstaller(transport, runner);

        var exception = await Assert.ThrowsAsync<MinecraftClientDownloadException>(() =>
            installer.InstallAsync(
                MinecraftClientLoader.NeoForge,
                "1.21.1",
                "21.1.102",
                _root,
                Path.Combine(_root, "java.exe")));

        Assert.Equal(1, exception.AttemptCount);
        Assert.Equal("loader-sidecar", exception.Stage);
        Assert.Equal("maven.neoforged.net", exception.Host);
        Assert.Equal(MinecraftClientDownloadFailureKind.InvalidResponse, exception.FailureKind);
        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Equal(0, runner.CallCount);
        Assert.Single(transport.Requests);
        Assert.Empty(Directory.EnumerateFiles(_root, ".loader-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task InstallAsync_OversizedDeclaredArtifactNeverRunsInstaller()
    {
        var artifactUri = OfficialMavenClientLoaderInstaller.CreateArtifactUri(
            MinecraftClientLoader.Forge,
            "1.20.1",
            "47.4.10");
        var installerBytes = Encoding.UTF8.GetBytes("small body");
        var sidecarBytes = Encoding.ASCII.GetBytes(Convert.ToHexString(
            SHA256.HashData(installerBytes)));
        var sidecarUri = new Uri(artifactUri.AbsoluteUri + ".sha256");
        var transport = new StubTransport(new Dictionary<string, ResponseSpec>(StringComparer.Ordinal)
        {
            [sidecarUri.AbsoluteUri] = new(sidecarBytes),
            [artifactUri.AbsoluteUri] = new(
                installerBytes,
                DeclaredLength: OfficialMavenClientLoaderInstaller.MaximumInstallerBytes + 1),
        });
        var runner = new RecordingRunner();
        var installer = new OfficialMavenClientLoaderInstaller(transport, runner);

        var exception = await Assert.ThrowsAsync<MinecraftClientDownloadException>(() =>
            installer.InstallAsync(
                MinecraftClientLoader.Forge,
                "1.20.1",
                "47.4.10",
                _root,
                Path.Combine(_root, "java.exe")));

        Assert.Equal(1, exception.AttemptCount);
        Assert.Equal("loader-installer", exception.Stage);
        Assert.Equal("maven.minecraftforge.net", exception.Host);
        Assert.Equal(MinecraftClientDownloadFailureKind.InvalidResponse, exception.FailureKind);
        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Equal(0, runner.CallCount);
        Assert.Empty(Directory.EnumerateFiles(_root, ".loader-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void OfficialCoordinatesAreExactAndRejectUntrustedCoordinates()
    {
        Assert.Equal(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.1.0/forge-1.21.1-52.1.0-installer.jar",
            OfficialMavenClientLoaderInstaller.CreateArtifactUri(
                MinecraftClientLoader.Forge,
                "1.21.1",
                "52.1.0").AbsoluteUri);
        Assert.Equal(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.102/neoforge-21.1.102-installer.jar",
            OfficialMavenClientLoaderInstaller.CreateArtifactUri(
                MinecraftClientLoader.NeoForge,
                "1.21.1",
                "21.1.102").AbsoluteUri);
        Assert.Equal(
            "https://maven.neoforged.net/releases/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar",
            OfficialMavenClientLoaderInstaller.CreateArtifactUri(
                MinecraftClientLoader.NeoForge,
                "1.20.1",
                "1.20.1-47.1.106").AbsoluteUri);

        Assert.Throws<InvalidDataException>(() =>
            OfficialMavenClientLoaderInstaller.CreateArtifactUri(
                MinecraftClientLoader.Forge,
                "1.21.1",
                "52.1.0/../../evil"));
        Assert.Throws<InvalidDataException>(() =>
            OfficialMavenClientLoaderInstaller.CreateArtifactUri(
                MinecraftClientLoader.NeoForge,
                "1.21.1",
                "20.4.250"));
    }

    [Fact]
    public void MavenAllowlistRejectsHttpLookalikeHostsQueriesAndFragments()
    {
        var official = OfficialMavenClientLoaderInstaller.CreateArtifactUri(
            MinecraftClientLoader.Forge,
            "1.21.1",
            "52.1.0");
        OfficialMavenClientLoaderInstaller.EnsureAllowed(MinecraftClientLoader.Forge, official);
        OfficialMavenClientLoaderInstaller.EnsureAllowed(
            MinecraftClientLoader.Forge,
            new Uri(official.AbsoluteUri + ".sha256"));

        Assert.Throws<InvalidDataException>(() =>
            OfficialMavenClientLoaderInstaller.EnsureAllowed(
                MinecraftClientLoader.Forge,
                new Uri(official.AbsoluteUri.Replace("https://", "http://", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() =>
            OfficialMavenClientLoaderInstaller.EnsureAllowed(
                MinecraftClientLoader.Forge,
                new Uri(official.AbsoluteUri.Replace(
                    "maven.minecraftforge.net",
                    "maven.minecraftforge.net.attacker.invalid",
                    StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() =>
            OfficialMavenClientLoaderInstaller.EnsureAllowed(
                MinecraftClientLoader.Forge,
                new Uri(official.AbsoluteUri + "?mirror=attacker")));
        Assert.Throws<InvalidDataException>(() =>
            OfficialMavenClientLoaderInstaller.EnsureAllowed(
                MinecraftClientLoader.Forge,
                new Uri(official.AbsoluteUri + "#installer")));
    }

    [Fact]
    public void VerifiedProcessStartInfoNeverUsesShellBrowserOrUrl()
    {
        var startInfo = VerifiedMavenClientLoaderProcessRunner.CreateStartInfo(
            @"C:\Java\bin\java.exe",
            @"C:\instance\.loader.verified.jar",
            @"C:\instance");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(@"C:\Java\bin\java.exe", startInfo.FileName);
        Assert.Equal(
            ["-jar", @"C:\instance\.loader.verified.jar", "--installClient", @"C:\instance"],
            startInfo.ArgumentList);
        Assert.All(
            startInfo.ArgumentList.Prepend(startInfo.FileName),
            value =>
            {
                Assert.DoesNotContain("http", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("adfoc", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("browser", value, StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public void GameClientAssemblyDoesNotReferenceAdBearingThirdPartyInstallers()
    {
        var references = typeof(CmlMinecraftClientPayloadInstaller).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("CmlLib.Core.Installer.Forge", references);
        Assert.DoesNotContain("CmlLib.Core.Installer.NeoForge", references);
        Assert.DoesNotContain("HtmlAgilityPack", references);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static StubTransport CreateTransport(
        Uri artifactUri,
        byte[] hashSource,
        byte[] artifactBytes)
    {
        var sidecarUri = new Uri(artifactUri.AbsoluteUri + ".sha256");
        var sidecarBytes = Encoding.ASCII.GetBytes(Convert.ToHexString(
            SHA256.HashData(hashSource)));
        return new StubTransport(new Dictionary<string, ResponseSpec>(StringComparer.Ordinal)
        {
            [sidecarUri.AbsoluteUri] = new(sidecarBytes),
            [artifactUri.AbsoluteUri] = new(artifactBytes),
        });
    }

    private static OfficialMavenClientLoaderInstaller CreateRetryingInstaller(
        IOfficialMavenClientLoaderHttpTransport transport,
        IVerifiedMavenClientLoaderProcessRunner runner,
        int maximumAttempts) =>
        new(
            transport,
            runner,
            new CmlDownloadReliabilityOptions
            {
                MaximumFileAttempts = maximumAttempts,
                MaximumPhaseAttempts = 1,
                MaximumConcurrentChecks = 1,
                MaximumConcurrentDownloads = 1,
                BoundedCapacity = 4,
                RetryDelays = [TimeSpan.Zero],
            },
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

    private static HttpResponseMessage CreateResponse(
        Uri uri,
        byte[] body,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri),
            Content = new ByteArrayContent(body),
        };
        response.Content.Headers.ContentLength = body.LongLength;
        return response;
    }

    private static void WriteProfile(
        string instanceDirectory,
        string profileId,
        string library)
    {
        var profileDirectory = Path.Combine(instanceDirectory, "versions", profileId);
        Directory.CreateDirectory(profileDirectory);
        var json = JsonSerializer.Serialize(new
        {
            id = profileId,
            libraries = new[] { new { name = library } },
        });
        File.WriteAllText(Path.Combine(profileDirectory, profileId + ".json"), json);
    }

    private sealed class RecordingRunner(
        Action<VerifiedMavenClientLoaderArtifact, string>? onRun = null)
        : IVerifiedMavenClientLoaderProcessRunner
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task RunAsync(
            VerifiedMavenClientLoaderArtifact artifact,
            string javaExecutablePath,
            string instanceDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            onRun?.Invoke(artifact, instanceDirectory);
            return Task.CompletedTask;
        }
    }

    private sealed record ResponseSpec(
        byte[] Body,
        HttpStatusCode StatusCode = HttpStatusCode.OK,
        Uri? EffectiveUri = null,
        long? DeclaredLength = null,
        Uri? RedirectLocation = null);

    private sealed class StubTransport(
        IReadOnlyDictionary<string, ResponseSpec> responses)
        : IOfficialMavenClientLoaderHttpTransport
    {
        private readonly List<Uri> _requests = [];

        public IReadOnlyList<Uri> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        public Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_requests)
            {
                _requests.Add(uri);
            }

            if (!responses.TryGetValue(uri.AbsoluteUri, out var specification))
            {
                throw new InvalidOperationException($"Unexpected URI: {uri}");
            }

            var response = new HttpResponseMessage(specification.StatusCode)
            {
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    specification.EffectiveUri ?? uri),
                Content = new ByteArrayContent(specification.Body),
            };
            response.Content.Headers.ContentLength =
                specification.DeclaredLength ?? specification.Body.LongLength;
            response.Headers.Location = specification.RedirectLocation;
            return Task.FromResult(response);
        }
    }

    private sealed class CallbackTransport(Func<Uri, HttpResponseMessage> callback)
        : IOfficialMavenClientLoaderHttpTransport
    {
        private readonly List<Uri> _requests = [];

        public IReadOnlyList<Uri> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        public Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_requests)
            {
                _requests.Add(uri);
            }

            return Task.FromResult(callback(uri));
        }
    }
}
