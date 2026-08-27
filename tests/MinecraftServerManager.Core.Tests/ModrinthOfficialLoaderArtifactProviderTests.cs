using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class ModrinthOfficialLoaderArtifactProviderTests
{
    [Fact]
    public async Task Vanilla_ValidatesManifestMetadataServerHashSizeAndChinesePath()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = MojangFixture.Create("1.20.1", Encoding.UTF8.GetBytes("verified server jar"));
        using var client = new HttpClient(fixture.CreateHandler());
        var provider = CreateProvider(client);
        var parent = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "伺服器 核心")).FullName;
        var destination = Path.Combine(parent, "server.jar");

        var artifact = await provider.DownloadVanillaServerAsync("1.20.1", destination);
        await provider.VerifyVanillaServerAsync("1.20.1", destination);

        Assert.Equal(ModrinthLoaderArtifactKind.MinecraftServer, artifact.Kind);
        Assert.Equal("SHA-1", artifact.HashAlgorithm);
        Assert.Equal(fixture.ServerSha1.ToLowerInvariant(), artifact.Hash);
        Assert.Equal(fixture.ServerBytes, await File.ReadAllBytesAsync(destination));
        Assert.Equal(2, fixture.Requests.Count(uri => uri == fixture.GlobalManifestUri));
        Assert.Equal(2, fixture.Requests.Count(uri => uri == fixture.VersionMetadataUri));
        Assert.Equal(1, fixture.Requests.Count(uri => uri == fixture.ServerUri));
    }

    [Fact]
    public async Task Vanilla_RejectsTamperedVersionMetadataBeforeServerDownload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = MojangFixture.Create("1.20.1", Encoding.UTF8.GetBytes("server"));
        fixture.AdvertisedMetadataSha1 = new string('0', 40);
        using var client = new HttpClient(fixture.CreateHandler());
        var provider = CreateProvider(client);
        var destination = Path.Combine(temporaryDirectory.Path, "server.jar");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.DownloadVanillaServerAsync("1.20.1", destination));

        Assert.Contains("SHA1", error.Message.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.ServerUri, fixture.Requests);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "*.partial"));
    }

    [Fact]
    public async Task Vanilla_RejectsOffHostFinalRedirectAndCleansPartial()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = MojangFixture.Create("1.20.1", Encoding.UTF8.GetBytes("server"));
        fixture.ServerFinalUri = new Uri("https://attacker.example/v1/objects/bad/server.jar");
        using var client = new HttpClient(fixture.CreateHandler());
        var provider = CreateProvider(client);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.DownloadVanillaServerAsync(
                "1.20.1",
                Path.Combine(temporaryDirectory.Path, "server.jar")));

        Assert.Contains("未核准", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path));
    }

    [Fact]
    public async Task Fabric_SelectsFirstStableMetaInstallerAndValidatesMavenSha256()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installer = Encoding.UTF8.GetBytes("fabric installer");
        var stableVersion = "1.1.2";
        var source = new Uri(
            $"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{stableVersion}/"
            + $"fabric-installer-{stableVersion}.jar");
        var meta = JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            new
            {
                url = "https://maven.fabricmc.net/net/fabricmc/fabric-installer/1.1.3/"
                    + "fabric-installer-1.1.3.jar",
                maven = "net.fabricmc:fabric-installer:1.1.3",
                version = "1.1.3",
                stable = false,
            },
            new
            {
                url = source.AbsoluteUri,
                maven = "net.fabricmc:fabric-installer:" + stableVersion,
                version = stableVersion,
                stable = true,
            },
        });
        var fixture = MavenFixture.Create(
            new Uri("https://meta.fabricmc.net/v2/versions/installer"),
            meta,
            source,
            installer);
        using var client = new HttpClient(fixture.CreateHandler());
        var destination = Path.Combine(temporaryDirectory.Path, "fabric-installer.jar");

        var artifact = await CreateProvider(client)
            .DownloadLatestStableFabricInstallerAsync(destination);

        Assert.Equal(ModrinthLoaderArtifactKind.FabricInstaller, artifact.Kind);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant(), artifact.Hash);
        Assert.Equal(installer, await File.ReadAllBytesAsync(destination));
        Assert.Equal(
            [fixture.MetadataUri!, new Uri(source.AbsoluteUri + ".sha256"), source],
            fixture.Requests);
    }

    [Fact]
    public async Task Forge_ConstructsOfficialCoordinateAndValidatesSha256()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        const string coordinate = "1.20.1-47.2.0";
        var source = new Uri(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/"
            + $"{coordinate}/forge-{coordinate}-installer.jar");
        var bytes = Encoding.UTF8.GetBytes("forge installer");
        var fixture = MavenFixture.Create(metadataUri: null, metadata: null, source, bytes);
        using var client = new HttpClient(fixture.CreateHandler());

        var artifact = await CreateProvider(client).DownloadForgeInstallerAsync(
            "1.20.1",
            "47.2.0",
            Path.Combine(temporaryDirectory.Path, "forge-installer.jar"));

        Assert.Equal(ModrinthLoaderArtifactKind.ForgeInstaller, artifact.Kind);
        Assert.Equal(source, artifact.Source);
        Assert.Equal([new Uri(source.AbsoluteUri + ".sha256"), source], fixture.Requests);
    }

    [Fact]
    public async Task Forge1122_MissingSha256FallsBackToExactOfficialSha1()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        const string coordinate = "1.12.2-14.23.5.2864";
        var source = new Uri(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/"
            + $"{coordinate}/forge-{coordinate}-installer.jar");
        var bytes = Encoding.UTF8.GetBytes("historical Forge 1.12.2 installer");
        var fixture = MavenFixture.Create(metadataUri: null, metadata: null, source, bytes);
        fixture.Sha256StatusCode = HttpStatusCode.NotFound;
        using var client = new HttpClient(fixture.CreateHandler());
        var destination = Path.Combine(temporaryDirectory.Path, "forge-1.12.2-installer.jar");

        var artifact = await CreateProvider(client).DownloadForgeInstallerAsync(
            "1.12.2",
            "14.23.5.2864",
            destination);

        Assert.Equal(ModrinthLoaderArtifactKind.ForgeInstaller, artifact.Kind);
        Assert.Equal("SHA-1", artifact.HashAlgorithm);
        Assert.Equal(
            Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
            artifact.Hash);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.Equal(
            [
                new Uri(source.AbsoluteUri + ".sha256"),
                new Uri(source.AbsoluteUri + ".sha1"),
                source
            ],
            fixture.Requests);
    }

    [Fact]
    public async Task Forge_MissingBothOfficialHashesFailsBeforeArtifactDownload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        const string coordinate = "1.12.2-14.23.5.2864";
        var source = new Uri(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/"
            + $"{coordinate}/forge-{coordinate}-installer.jar");
        var fixture = MavenFixture.Create(
            metadataUri: null,
            metadata: null,
            source,
            Encoding.UTF8.GetBytes("must not download"));
        fixture.Sha256StatusCode = HttpStatusCode.NotFound;
        fixture.Sha1StatusCode = HttpStatusCode.NotFound;
        using var client = new HttpClient(fixture.CreateHandler());
        var destination = Path.Combine(temporaryDirectory.Path, "installer.jar");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => CreateProvider(client)
            .DownloadForgeInstallerAsync("1.12.2", "14.23.5.2864", destination));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.DoesNotContain(source, fixture.Requests);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "*.partial"));
    }

    [Fact]
    public async Task Forge_MalformedSha256DoesNotSilentlyFallBackToSha1()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = new Uri(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.12.2-14.23.5.2864/"
            + "forge-1.12.2-14.23.5.2864-installer.jar");
        var fixture = MavenFixture.Create(
            metadataUri: null,
            metadata: null,
            source,
            Encoding.UTF8.GetBytes("installer"));
        fixture.ChecksumText = "not-a-valid-sha256";
        using var client = new HttpClient(fixture.CreateHandler());

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateProvider(client)
            .DownloadForgeInstallerAsync(
                "1.12.2",
                "14.23.5.2864",
                Path.Combine(temporaryDirectory.Path, "installer.jar")));

        Assert.Equal([new Uri(source.AbsoluteUri + ".sha256")], fixture.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Forge_Sha256ErrorsOtherThanNotFoundDoNotFallBack(
        HttpStatusCode statusCode)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = new Uri(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.12.2-14.23.5.2864/"
            + "forge-1.12.2-14.23.5.2864-installer.jar");
        var fixture = MavenFixture.Create(
            metadataUri: null,
            metadata: null,
            source,
            Encoding.UTF8.GetBytes("installer"));
        fixture.Sha256StatusCode = statusCode;
        using var client = new HttpClient(fixture.CreateHandler());

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => CreateProvider(client)
            .DownloadForgeInstallerAsync(
                "1.12.2",
                "14.23.5.2864",
                Path.Combine(temporaryDirectory.Path, "installer.jar")));

        Assert.Equal(statusCode, error.StatusCode);
        Assert.Equal([new Uri(source.AbsoluteUri + ".sha256")], fixture.Requests);
    }

    [Fact]
    public async Task Forge_Sha1FallbackRejectsRedirectedOrOversizedChecksumBody()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = new Uri(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.12.2-14.23.5.2864/"
            + "forge-1.12.2-14.23.5.2864-installer.jar");
        var fixture = MavenFixture.Create(
            metadataUri: null,
            metadata: null,
            source,
            Encoding.UTF8.GetBytes("installer"));
        fixture.Sha256StatusCode = HttpStatusCode.NotFound;
        fixture.Sha1FinalUri = new Uri("https://attacker.example/forge-installer.jar.sha1");
        using var redirectedClient = new HttpClient(fixture.CreateHandler());

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateProvider(redirectedClient)
            .DownloadForgeInstallerAsync(
                "1.12.2",
                "14.23.5.2864",
                Path.Combine(temporaryDirectory.Path, "redirected.jar")));
        Assert.DoesNotContain(source, fixture.Requests);

        var oversized = MavenFixture.Create(
            metadataUri: null,
            metadata: null,
            source,
            Encoding.UTF8.GetBytes("installer"));
        oversized.Sha256StatusCode = HttpStatusCode.NotFound;
        oversized.Sha1ChecksumText = new string('a', 4 * 1024 + 1);
        using var oversizedClient = new HttpClient(oversized.CreateHandler());

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateProvider(oversizedClient).DownloadForgeInstallerAsync(
                "1.12.2",
                "14.23.5.2864",
                Path.Combine(temporaryDirectory.Path, "oversized.jar")));
        Assert.Contains("大小", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(source, oversized.Requests);
    }

    [Fact]
    public async Task NeoForge_UsesOfficialReleaseMavenCoordinate()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        const string loaderVersion = "21.1.248";
        var source = new Uri(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/"
            + $"{loaderVersion}/neoforge-{loaderVersion}-installer.jar");
        var bytes = Encoding.UTF8.GetBytes("neoforge installer");
        var fixture = MavenFixture.Create(metadataUri: null, metadata: null, source, bytes);
        using var client = new HttpClient(fixture.CreateHandler());

        var artifact = await CreateProvider(client).DownloadNeoForgeInstallerAsync(
            loaderVersion,
            Path.Combine(temporaryDirectory.Path, "neoforge-installer.jar"));

        Assert.Equal(ModrinthLoaderArtifactKind.NeoForgeInstaller, artifact.Kind);
        Assert.Equal(source, artifact.Source);
        Assert.Equal(bytes.Length, artifact.Size);
    }

    [Fact]
    public async Task MavenHashMismatch_RemovesPartialAndDestination()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = new Uri(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.2.0/"
            + "forge-1.20.1-47.2.0-installer.jar");
        var fixture = MavenFixture.Create(
            metadataUri: null,
            metadata: null,
            source,
            Encoding.UTF8.GetBytes("actual installer"));
        fixture.ChecksumText = new string('0', 64);
        using var client = new HttpClient(fixture.CreateHandler());
        var destination = Path.Combine(temporaryDirectory.Path, "installer.jar");

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateProvider(client)
            .DownloadForgeInstallerAsync("1.20.1", "47.2.0", destination));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "*.partial"));
    }

    [Fact]
    public async Task MavenDownload_RequiresContentLengthAndNeverOverwritesDestination()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = new Uri(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.2.0/"
            + "forge-1.20.1-47.2.0-installer.jar");
        var fixture = MavenFixture.Create(
            metadataUri: null,
            metadata: null,
            source,
            Encoding.UTF8.GetBytes("installer"));
        fixture.OmitArtifactContentLength = true;
        using var client = new HttpClient(fixture.CreateHandler());
        var destination = Path.Combine(temporaryDirectory.Path, "installer.jar");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CreateProvider(client)
            .DownloadForgeInstallerAsync("1.20.1", "47.2.0", destination));

        Assert.Contains("Content-Length", error.Message, StringComparison.Ordinal);
        await File.WriteAllTextAsync(destination, "owned");
        await Assert.ThrowsAsync<IOException>(() => CreateProvider(client)
            .DownloadForgeInstallerAsync("1.20.1", "47.2.0", destination));
        Assert.Equal("owned", await File.ReadAllTextAsync(destination));
    }

    private static ModrinthOfficialLoaderArtifactProvider CreateProvider(HttpClient client)
        => new(client, "Muhun-MCSV-Manager.Tests/1.0");

    private sealed class MojangFixture
    {
        private byte[] _globalManifest = [];

        private MojangFixture(string version, byte[] serverBytes)
        {
            Version = version;
            ServerBytes = serverBytes;
            ServerSha1 = Convert.ToHexString(SHA1.HashData(serverBytes));
            ServerUri = new Uri(
                $"https://piston-data.mojang.com/v1/objects/{ServerSha1.ToLowerInvariant()}/server.jar");
            VersionMetadataUri = new Uri(
                $"https://piston-meta.mojang.com/v1/packages/metadata/{version}.json");
            VersionMetadata = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = version,
                downloads = new
                {
                    server = new
                    {
                        sha1 = ServerSha1.ToLowerInvariant(),
                        size = serverBytes.LongLength,
                        url = ServerUri.AbsoluteUri,
                    },
                },
            });
            AdvertisedMetadataSha1 = Convert.ToHexString(SHA1.HashData(VersionMetadata));
            RebuildManifest();
        }

        public Uri GlobalManifestUri { get; } = new(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

        public string Version { get; }

        public byte[] ServerBytes { get; }

        public string ServerSha1 { get; }

        public Uri ServerUri { get; }

        public Uri VersionMetadataUri { get; }

        public byte[] VersionMetadata { get; }

        public string AdvertisedMetadataSha1
        {
            get => field;
            set
            {
                field = value;
                RebuildManifest();
            }
        } = string.Empty;

        public Uri? ServerFinalUri { get; set; }

        public List<Uri> Requests { get; } = [];

        public static MojangFixture Create(string version, byte[] serverBytes)
            => new(version, serverBytes);

        public HttpMessageHandler CreateHandler() => new StubHandler(request =>
        {
            Requests.Add(request.RequestUri!);
            HttpResponseMessage response;
            if (request.RequestUri == GlobalManifestUri)
            {
                response = BytesResponse(_globalManifest, "application/json");
            }
            else if (request.RequestUri == VersionMetadataUri)
            {
                response = BytesResponse(VersionMetadata, "application/json");
            }
            else if (request.RequestUri == ServerUri)
            {
                response = BytesResponse(ServerBytes);
                if (ServerFinalUri is not null)
                {
                    response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, ServerFinalUri);
                }
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("not found"),
                };
            }

            return response;
        });

        private void RebuildManifest()
        {
            if (string.IsNullOrWhiteSpace(AdvertisedMetadataSha1))
            {
                return;
            }

            _globalManifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                versions = new[]
                {
                    new
                    {
                        id = Version,
                        type = "release",
                        url = VersionMetadataUri.AbsoluteUri,
                        sha1 = AdvertisedMetadataSha1.ToLowerInvariant(),
                    },
                },
            });
        }
    }

    private sealed class MavenFixture
    {
        private readonly byte[]? _metadata;
        private readonly byte[] _artifact;

        private MavenFixture(Uri? metadataUri, byte[]? metadata, Uri artifactUri, byte[] artifact)
        {
            MetadataUri = metadataUri;
            _metadata = metadata;
            ArtifactUri = artifactUri;
            _artifact = artifact;
            ChecksumText = Convert.ToHexString(SHA256.HashData(artifact)).ToLowerInvariant();
            Sha1ChecksumText = Convert.ToHexString(SHA1.HashData(artifact)).ToLowerInvariant();
        }

        public Uri? MetadataUri { get; }

        public Uri ArtifactUri { get; }

        public string ChecksumText { get; set; }

        public string Sha1ChecksumText { get; set; }

        public HttpStatusCode Sha256StatusCode { get; set; } = HttpStatusCode.OK;

        public HttpStatusCode Sha1StatusCode { get; set; } = HttpStatusCode.OK;

        public Uri? Sha1FinalUri { get; set; }

        public bool OmitArtifactContentLength { get; set; }

        public List<Uri> Requests { get; } = [];

        public static MavenFixture Create(
            Uri? metadataUri,
            byte[]? metadata,
            Uri artifactUri,
            byte[] artifact)
            => new(metadataUri, metadata, artifactUri, artifact);

        public HttpMessageHandler CreateHandler() => new StubHandler(request =>
        {
            Requests.Add(request.RequestUri!);
            if (MetadataUri is not null && request.RequestUri == MetadataUri)
            {
                return BytesResponse(_metadata!, "application/json");
            }

            if (request.RequestUri!.AbsoluteUri == ArtifactUri.AbsoluteUri + ".sha256")
            {
                return ChecksumResponse(Sha256StatusCode, ChecksumText);
            }

            if (request.RequestUri.AbsoluteUri == ArtifactUri.AbsoluteUri + ".sha1")
            {
                var response = ChecksumResponse(Sha1StatusCode, Sha1ChecksumText);
                if (Sha1FinalUri is not null)
                {
                    response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, Sha1FinalUri);
                }

                return response;
            }

            if (request.RequestUri == ArtifactUri)
            {
                if (!OmitArtifactContentLength)
                {
                    return BytesResponse(_artifact);
                }

                var content = new UnknownLengthContent(_artifact);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "application/java-archive");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found"),
            };
        });

        private static HttpResponseMessage ChecksumResponse(
            HttpStatusCode statusCode,
            string text)
        {
            var content = new StringContent(text, Encoding.UTF8, "text/plain");
            return new HttpResponseMessage(statusCode) { Content = content };
        }
    }

    private static HttpResponseMessage BytesResponse(
        byte[] bytes,
        string contentType = "application/octet-stream")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
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

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
            => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
