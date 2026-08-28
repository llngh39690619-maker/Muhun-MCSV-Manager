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
        var installer = new OfficialMavenClientLoaderInstaller(transport, runner);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(
                MinecraftClientLoader.Forge,
                "1.21.1",
                "52.1.0",
                _root,
                Path.Combine(_root, "java.exe")));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.CallCount);
        Assert.Equal(
            [artifactUri.AbsoluteUri + ".sha256", artifactUri.AbsoluteUri],
            transport.Requests.Select(uri => uri.AbsoluteUri));
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

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            installer.InstallAsync(
                MinecraftClientLoader.Forge,
                "1.21.1",
                "52.1.0",
                _root,
                Path.Combine(_root, "java.exe")));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal(0, runner.CallCount);
        Assert.Equal(sidecarUri, Assert.Single(transport.Requests));
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

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(
                MinecraftClientLoader.NeoForge,
                "1.21.1",
                "21.1.102",
                _root,
                Path.Combine(_root, "java.exe")));

        Assert.Contains("redirect", exception.Message, StringComparison.OrdinalIgnoreCase);
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

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(
                MinecraftClientLoader.Forge,
                "1.20.1",
                "47.4.10",
                _root,
                Path.Combine(_root, "java.exe")));

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
}
