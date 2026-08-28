using System.Net;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class OfficialLoaderCatalogProviderTests
{
    [Fact]
    public async Task Fabric_ReturnsOnlyStableVersionsForTheExactMojangRelease()
    {
        using var client = FixtureClient("fabric-loader-1.21.1.json", out var handler);
        var provider = new FabricLoaderCatalogProvider(client);

        var result = await provider.GetVersionsAsync(StableSnapshot("1.21.1"), "1.21.1");

        Assert.Equal(["0.16.9", "0.15.11"], result.Select(entry => entry.Version));
        Assert.All(result, entry =>
        {
            Assert.Equal(MinecraftLoaderReleaseChannel.Stable, entry.ReleaseChannel);
            Assert.Equal(MinecraftClientLoaderInstallKind.Managed, entry.InstallKind);
            Assert.Equal("meta.fabricmc.net", entry.OfficialSourceUri.Host);
            var profileUri = Assert.IsType<Uri>(entry.InstallProfileOrArtifactUri);
            Assert.Equal("meta.fabricmc.net", profileUri.Host);
            Assert.EndsWith("/profile/json", profileUri.AbsolutePath);
        });
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("/v2/versions/loader/1.21.1", handler.RequestUris.Single().AbsolutePath);
    }

    [Fact]
    public async Task Quilt_ReturnsOnlyPlainNumericNonPrereleaseVersions()
    {
        using var client = FixtureClient("quilt-loader-1.21.1.json", out _);
        var provider = new QuiltLoaderCatalogProvider(client);

        var result = await provider.GetVersionsAsync(StableSnapshot("1.21.1"), "1.21.1");

        Assert.Equal(["0.27.1", "0.26.4"], result.Select(entry => entry.Version));
        Assert.DoesNotContain(result, entry => entry.Version.Contains("beta", StringComparison.Ordinal));
        Assert.All(result, entry => Assert.Equal("meta.quiltmc.org", entry.OfficialSourceUri.Host));
    }

    [Fact]
    public async Task Forge_ReturnsRecommendedButNeverFallsBackToLatest()
    {
        using var client = FixtureClient("forge-promotions.json", out _);
        var provider = new ForgeLoaderCatalogProvider(client);

        var recommended = await provider.GetVersionsAsync(
            StableSnapshot("1.21.1", "1.20.1"),
            "1.21.1");

        var entry = Assert.Single(recommended);
        Assert.Equal("52.1.0", entry.Version);
        Assert.Equal(MinecraftLoaderReleaseChannel.Recommended, entry.ReleaseChannel);
        var forgeInstallerUri = Assert.IsType<Uri>(entry.InstallProfileOrArtifactUri);
        Assert.Equal("maven.minecraftforge.net", forgeInstallerUri.Host);
        Assert.EndsWith(
            "/forge-1.21.1-52.1.0-installer.jar",
            forgeInstallerUri.AbsolutePath);

        var latestOnly = await provider.GetVersionsAsync(
            StableSnapshot("1.21.1", "1.20.1"),
            "1.20.1");
        Assert.Empty(latestOnly);
    }

    [Fact]
    public async Task NeoForge_MapsLegacyAndModernMinecraftVersionSchemes()
    {
        using var modernClient = FixtureClient("neoforge-metadata.xml", out _);
        var modernProvider = new NeoForgeLoaderCatalogProvider(modernClient);

        var oldScheme = await modernProvider.GetVersionsAsync(
            StableSnapshot("1.21.1"),
            "1.21.1");
        Assert.Equal(["21.1.102", "21.1.100"], oldScheme.Select(entry => entry.Version));

        var calver = await modernProvider.GetVersionsAsync(
            StableSnapshot("26.1"),
            "26.1");
        var calverEntry = Assert.Single(calver);
        Assert.Equal("26.1.0.6", calverEntry.Version);
        var calverInstallerUri = Assert.IsType<Uri>(calverEntry.InstallProfileOrArtifactUri);
        Assert.Equal("maven.neoforged.net", calverInstallerUri.Host);

        using var legacyClient = FixtureClient("neoforge-legacy-1.20.1-metadata.xml", out _);
        var legacyProvider = new NeoForgeLoaderCatalogProvider(legacyClient);
        var legacy = await legacyProvider.GetVersionsAsync(
            StableSnapshot("1.20.1"),
            "1.20.1");
        Assert.Equal(
            ["1.20.1-47.1.106", "1.20.1-47.1.80"],
            legacy.Select(entry => entry.Version));
        Assert.All(legacy, entry =>
            Assert.Contains("/forge/", Assert.IsType<Uri>(entry.InstallProfileOrArtifactUri).AbsolutePath));
    }

    [Fact]
    public async Task NonReleaseSelection_ReturnsEmptyWithoutAnyNetworkRequest()
    {
        using var client = FixtureClient("fabric-loader-1.21.1.json", out var handler);
        var provider = new FabricLoaderCatalogProvider(client);

        var result = await provider.GetVersionsAsync(StableSnapshot("1.21.1"), "25w35a");

        Assert.Empty(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ExternalClients_RequireOfficialManualInstallAndDoNotExposeArtifacts()
    {
        var snapshot = StableSnapshot("1.21.1");
        IMinecraftLoaderCatalogProvider[] providers =
        [
            new OptiFineExternalInstallerCatalogProvider(),
            new LabyModExternalInstallerCatalogProvider(),
        ];

        foreach (var provider in providers)
        {
            var entry = Assert.Single(await provider.GetVersionsAsync(snapshot, "1.21.1"));
            Assert.Equal(MinecraftClientLoaderInstallKind.ExternalInstallerRequired, entry.InstallKind);
            Assert.Equal(MinecraftLoaderReleaseChannel.External, entry.ReleaseChannel);
            Assert.Null(entry.InstallProfileOrArtifactUri);
            Assert.Equal("https", entry.OfficialSourceUri.Scheme);
            Assert.Contains(entry.Loader.ToString(), entry.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CatalogReader_RejectsRedirectedResponseOutsideOfficialAllowlist()
    {
        var bytes = FixtureBytes("fabric-loader-1.21.1.json");
        var handler = new CatalogStubHandler((request, _) =>
        {
            var response = Success(bytes, request);
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://catalog-attacker.invalid/loader.json");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var provider = new FabricLoaderCatalogProvider(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetVersionsAsync(StableSnapshot("1.21.1"), "1.21.1"));
    }

    [Fact]
    public async Task CatalogReader_RejectsOversizedDeclaredResponseBeforeReadingBody()
    {
        var bytes = FixtureBytes("fabric-loader-1.21.1.json");
        var handler = new CatalogStubHandler((request, _) =>
        {
            var response = Success(bytes, request);
            response.Content.Headers.ContentLength = 4L * 1024 * 1024 + 1;
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var provider = new FabricLoaderCatalogProvider(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetVersionsAsync(StableSnapshot("1.21.1"), "1.21.1"));
    }

    [Fact]
    public async Task CatalogReader_HonorsProviderTimeoutAndCallerCancellation()
    {
        var slowHandler = new CatalogStubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The delay should always be cancelled.");
        });
        using var slowClient = new HttpClient(slowHandler);
        var timedProvider = new FabricLoaderCatalogProvider(slowClient, TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            timedProvider.GetVersionsAsync(StableSnapshot("1.21.1"), "1.21.1"));

        using var cancellableClient = FixtureClient("fabric-loader-1.21.1.json", out _);
        var cancellableProvider = new FabricLoaderCatalogProvider(cancellableClient);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancellableProvider.GetVersionsAsync(
                StableSnapshot("1.21.1"),
                "1.21.1",
                cancellation.Token));
    }

    [Fact]
    public async Task Fabric_RejectsDuplicateStableVersionInsteadOfReturningAmbiguousData()
    {
        const string duplicateJson = """
            [
              {
                "loader": { "maven": "net.fabricmc:fabric-loader:0.16.9", "version": "0.16.9", "stable": true },
                "intermediary": { "version": "1.21.1" }
              },
              {
                "loader": { "maven": "net.fabricmc:fabric-loader:0.16.9", "version": "0.16.9", "stable": true },
                "intermediary": { "version": "1.21.1" }
              }
            ]
            """;
        var bytes = System.Text.Encoding.UTF8.GetBytes(duplicateJson);
        var handler = new CatalogStubHandler((request, _) => Task.FromResult(Success(bytes, request)));
        using var client = new HttpClient(handler);
        var provider = new FabricLoaderCatalogProvider(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetVersionsAsync(StableSnapshot("1.21.1"), "1.21.1"));
    }

    private static HttpClient FixtureClient(string fixtureName, out CatalogStubHandler handler)
    {
        var bytes = FixtureBytes(fixtureName);
        handler = new CatalogStubHandler((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Success(bytes, request));
        });
        return new HttpClient(handler);
    }

    private static HttpResponseMessage Success(byte[] bytes, HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentLength = bytes.Length;
        return response;
    }

    private static byte[] FixtureBytes(string fixtureName)
    {
        var assembly = typeof(OfficialLoaderCatalogProviderTests).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(fixtureName, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Fixture '{fixtureName}' is unavailable.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static MinecraftReleaseCatalogSnapshot StableSnapshot(params string[] versions)
    {
        if (versions.Length == 0)
        {
            throw new ArgumentException("At least one stable version is required.", nameof(versions));
        }

        var now = DateTimeOffset.Parse("2026-08-28T00:00:00Z");
        var releases = versions.Select((version, index) =>
            new MinecraftReleaseInfo(
                version,
                now.AddDays(-index),
                new Uri($"https://piston-meta.mojang.com/v1/packages/a/{version}.json"),
                new string('a', 40),
                1)).ToArray();
        return new MinecraftReleaseCatalogSnapshot(versions[0], now, releases);
    }

    private sealed class CatalogStubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        private int _callCount;
        private readonly List<Uri> _requestUris = [];

        public int CallCount => Volatile.Read(ref _callCount);

        public IReadOnlyList<Uri> RequestUris
        {
            get
            {
                lock (_requestUris)
                {
                    return _requestUris.ToArray();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            lock (_requestUris)
            {
                _requestUris.Add(request.RequestUri!);
            }

            return responder(request, cancellationToken);
        }
    }
}
