using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost.Tests;

public sealed class ProviderProcessSandboxTests
{
    [Fact]
    public async Task HostileProvider_IsAssignedBeforeResumeAndCannotReadWriteSpawnOrConnectDirectly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SandboxFixture();
        var profilesBefore = GetProviderProfiles();
        var registration = fixture.InstallHostileProvider();
        var secret = Path.Combine(fixture.Layout.State, "service-secret.txt");
        await File.WriteAllTextAsync(secret, "must-not-leak");
        var broker = new ProviderHttpBroker(new HttpMessageInvoker(new StaticHandler()));
        var factory = new ProviderProcessFactory(fixture.Layout);
        await using var process = await factory.StartAsync(registration, CancellationToken.None);
        await using var session = new ProviderRpcSession(
            process,
            registration: registration,
            httpBroker: broker);

        ProductProviderRpcResponse response;
        try
        {
            response = await session.InvokeAsync(
                new ProviderInvocationRequest(
                    ProductProviderOperations.ModpackCatalogSearch,
                    JsonSerializer.SerializeToElement(new
                    {
                        secretPath = secret,
                        brokerUri = "https://api.example.com/catalog",
                    }),
                    new Uri("https://api.example.com/")),
                TimeSpan.FromSeconds(10));
        }
        catch (ProviderProcessCrashedException error)
        {
            throw new InvalidOperationException(
                $"Hostile provider fixture crashed with exit {error.ExitCode}: {error.StandardErrorTail}",
                error);
        }

        var result = response.Result!.Value;
        Assert.False(result.GetProperty("childStarted").GetBoolean());
        Assert.False(result.GetProperty("childEscaped").GetBoolean());
        Assert.False(result.GetProperty("directNetworkConnected").GetBoolean());
        Assert.Equal("AccessDenied", result.GetProperty("directNetworkError").GetString());
        Assert.False(result.GetProperty("secretRead").GetBoolean());
        Assert.False(result.GetProperty("packageWrite").GetBoolean());
        Assert.False(result.GetProperty("packageDelete").GetBoolean());
        Assert.True(
            result.GetProperty("scratchWrite").GetBoolean(),
            result.GetProperty("scratch").GetString() + ": " +
            result.GetProperty("scratchWriteError").GetString());
        Assert.Equal("trusted-broker", result.GetProperty("brokerBody").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("brokerError").ValueKind);
        Assert.False(File.Exists(Path.Combine(fixture.InstallDirectory, "provider-write.txt")));
        await session.DisposeAsync();
        Assert.Equal(profilesBefore, GetProviderProfiles());
    }

    [Fact]
    public async Task ProviderCrash_KillsJobAndRemovesInvocationScratch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SandboxFixture();
        var profilesBefore = GetProviderProfiles();
        var registration = fixture.InstallHostileProvider();
        var factory = new ProviderProcessFactory(fixture.Layout);
        await using (var process = await factory.StartAsync(registration, CancellationToken.None))
        await using (var session = new ProviderRpcSession(process))
        {
            await Assert.ThrowsAsync<ProviderProcessCrashedException>(() => session.InvokeAsync(
                new ProviderInvocationRequest(
                    ProductProviderOperations.HealthGet,
                    JsonSerializer.SerializeToElement(new { crash = true })),
                TimeSpan.FromSeconds(10)));
        }

        var scratchRoot = Path.Combine(fixture.Layout.State, "provider-scratch");
        Assert.Empty(Directory.Exists(scratchRoot)
            ? Directory.EnumerateFiles(scratchRoot, "*", SearchOption.AllDirectories)
            : []);
        Assert.Equal(profilesBefore, GetProviderProfiles());
    }

    [Fact]
    public async Task HostileProvider_BrokerRejectsUnlistedHostBeforeTrustedNetworkCall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SandboxFixture();
        var profilesBefore = GetProviderProfiles();
        var registration = fixture.InstallHostileProvider();
        var handler = new CountingHandler();
        var factory = new ProviderProcessFactory(fixture.Layout);
        await using var process = await factory.StartAsync(registration, CancellationToken.None);
        await using var session = new ProviderRpcSession(
            process,
            registration: registration,
            httpBroker: new ProviderHttpBroker(new HttpMessageInvoker(handler)));

        var response = await session.InvokeAsync(
            new ProviderInvocationRequest(
                ProductProviderOperations.ModpackCatalogSearch,
                JsonSerializer.SerializeToElement(new
                {
                    brokerUri = "https://unlisted.example.net/catalog",
                }),
                new Uri("https://api.example.com/")),
            TimeSpan.FromSeconds(10));

        Assert.Equal(
            "provider.broker_denied",
            response.Result!.Value.GetProperty("brokerError").GetString());
        Assert.Equal(0, handler.CallCount);
        await session.DisposeAsync();
        Assert.Equal(profilesBefore, GetProviderProfiles());
    }

    [Fact]
    public async Task SandboxPreparationFailure_DeletesNewAppContainerProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SandboxFixture();
        var profilesBefore = GetProviderProfiles();
        var registration = fixture.InstallHostileProvider();
        await File.WriteAllTextAsync(Path.Combine(fixture.Layout.State, "provider-scratch"), "blocked");
        var factory = new ProviderProcessFactory(fixture.Layout);

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await factory.StartAsync(registration, CancellationToken.None));

        Assert.Equal(profilesBefore, GetProviderProfiles());
    }

    private static string[] GetProviderProfiles()
    {
        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages");
        return Directory.Exists(packages)
            ? Directory.EnumerateDirectories(packages, "muhun.mcsv.provider.*")
                .Select(path => Path.GetFileName(path)!)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
    }

    private sealed class SandboxFixture : IDisposable
    {
        public SandboxFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "mcsv-provider-sandbox-" + Guid.NewGuid().ToString("N"));
            Layout = new ProviderHostLayout(Path.Combine(Root, "host"));
            Layout.EnsureCreated();
            InstallDirectory = Path.Combine(Layout.Packages, "hostile.security", "1.0.0");
        }

        public string Root { get; }
        public ProviderHostLayout Layout { get; }
        public string InstallDirectory { get; }

        public ProviderRegistration InstallHostileProvider()
        {
            Directory.CreateDirectory(InstallDirectory);
            var outputRoot = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "HostileProviderOutput")
                .Value!;
            var sources = Directory.EnumerateFiles(outputRoot)
                .Where(path => !Path.GetExtension(path).Equals(".pdb", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.NotEmpty(sources);
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                var name = Path.GetFileName(source);
                var destination = Path.Combine(InstallDirectory, name);
                File.Copy(source, destination);
                hashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination)))
                    .ToLowerInvariant();
            }

            File.WriteAllText(
                Path.Combine(InstallDirectory, ProviderPackageInstaller.ManifestFileName),
                "signed fixture manifest");
            var manifest = new ProductProviderManifest(
                ProductProviderManifestValidator.CurrentSchemaVersion,
                "hostile.security",
                "Hostile security fixture",
                "1.0.0",
                ProductApiProtocol.CurrentVersion,
                "Muhun.MCSV.HostileProvider.exe",
                [ProductProviderCapabilities.ModpackCatalog],
                [ProductProviderPermissions.Http],
                ["api.example.com"],
                hashes);
            var now = DateTimeOffset.UtcNow;
            return new ProviderRegistration(
                manifest,
                "muhun.security-tests",
                new string('a', 64),
                "packages/hostile.security/1.0.0",
                true,
                ProviderHealthStatus.Stopped,
                now,
                now,
                0,
                null);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(new Uri("https://api.example.com/catalog"), request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("trusted-broker"u8.ToArray()),
            });
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
