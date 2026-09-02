using System.Net;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Updater;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductServiceRepairHealthControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "muhun-service-repair-health-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WaitForHealthyAsync_RequiresActivationIdentityAndEulaCapableHandshake()
    {
        var version = GetUpdaterProductVersion();
        var layoutRoot = CreateFormalLayout(version);
        var (dataRoot, installationId) = CreateDataRoot();
        var platform = new RecordingPlatform();
        var responses = new QueueHttpHandler(
            Json(HttpStatusCode.OK, new ProductActivationReadyResponse(
                "ready",
                "Muhun MCSV Manager",
                version,
                installationId,
                DateTimeOffset.UtcNow,
                true)),
            Json(HttpStatusCode.OK, new ProductHandshakeResponse(
                "Muhun MCSV Manager",
                version,
                ProductApiProtocol.CurrentVersion,
                ProductApiProtocol.MinimumSupportedVersion,
                true)));
        using var controller = new ProductServiceRepairHealthController(
            dataRoot,
            version,
            platform,
            responses);

        await controller.LaunchAsync(
            Path.Combine(layoutRoot, "gui-win-x64", "Muhun MCSV Manager.exe"),
            CancellationToken.None);
        var healthy = await controller.WaitForHealthyAsync(
            version,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.True(healthy);
        Assert.Equal(
            Path.Combine(layoutRoot, "service-win-x64", "Muhun MCSV Service.exe"),
            platform.ServicePath,
            ignoreCase: true);
        Assert.Equal(dataRoot, platform.DataRoot, ignoreCase: true);
        Assert.Equal(2, responses.Requests.Count);
        Assert.EndsWith("/system/activation-ready", responses.Requests[0].AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/system/handshake", responses.Requests[1].AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForHealthyAsync_RejectsTargetServiceBelowCurrentApiVersion()
    {
        var version = GetUpdaterProductVersion();
        var layoutRoot = CreateFormalLayout(version);
        var (dataRoot, installationId) = CreateDataRoot();
        var responses = new QueueHttpHandler(
            Json(HttpStatusCode.OK, new ProductActivationReadyResponse(
                "ready",
                "Muhun MCSV Manager",
                version,
                installationId,
                DateTimeOffset.UtcNow,
                true)),
            Json(HttpStatusCode.OK, new ProductHandshakeResponse(
                "Muhun MCSV Manager",
                version,
                ProductApiProtocol.MinecraftEulaConsentVersion,
                ProductApiProtocol.MinimumSupportedVersion,
                true)));
        using var controller = new ProductServiceRepairHealthController(
            dataRoot,
            version,
            new RecordingPlatform(),
            responses);

        await controller.LaunchAsync(
            Path.Combine(layoutRoot, "gui-win-x64", "Muhun MCSV Manager.exe"),
            CancellationToken.None);
        var healthy = await controller.WaitForHealthyAsync(
            version,
            TimeSpan.FromMilliseconds(25),
            CancellationToken.None);

        Assert.False(healthy);
    }

    [Fact]
    public async Task WaitForHealthyAsync_RollbackVersionDoesNotRequireTargetApiCapability()
    {
        var rollbackVersion = GetUpdaterProductVersion();
        var targetVersion = "99.0.0-beta.1";
        var layoutRoot = CreateFormalLayout(rollbackVersion);
        var (dataRoot, installationId) = CreateDataRoot();
        var responses = new QueueHttpHandler(
            Json(HttpStatusCode.OK, new ProductActivationReadyResponse(
                "ready",
                "Muhun MCSV Manager",
                rollbackVersion,
                installationId,
                DateTimeOffset.UtcNow,
                true)));
        using var controller = new ProductServiceRepairHealthController(
            dataRoot,
            targetVersion,
            new RecordingPlatform(),
            responses);

        await controller.LaunchAsync(
            Path.Combine(layoutRoot, "gui-win-x64", "Muhun MCSV Manager.exe"),
            CancellationToken.None);
        var healthy = await controller.WaitForHealthyAsync(
            rollbackVersion,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.True(healthy);
        Assert.Single(responses.Requests);
    }

    private string CreateFormalLayout(string version)
    {
        var versionRoot = Path.Combine(_root, version);
        var updaterPath = typeof(ProductServiceRepairHealthController).Assembly.Location;
        var appHost = Path.ChangeExtension(updaterPath, ".exe");
        Assert.True(File.Exists(appHost), $"Updater apphost was not built: {appHost}");
        foreach (var relative in new[]
                 {
                     "gui-win-x64/Muhun MCSV Manager.exe",
                     "service-win-x64/Muhun MCSV Service.exe",
                     "updater-win-x64/Muhun MCSV Updater.exe",
                 })
        {
            var path = Path.Combine(versionRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(appHost, path);
        }

        File.WriteAllText(
            Path.Combine(versionRoot, "service-win-x64", "appsettings.json"),
            "{\"Mcsv\":{\"Service\":{\"Port\":39050}}}");
        return versionRoot;
    }

    private (string DataRoot, Guid InstallationId) CreateDataRoot()
    {
        var root = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(root, "secrets"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, ".muhun-mcsv-data-root"), "muhun.mcsv.manager:1\n");
        File.WriteAllText(
            Path.Combine(root, ProductLocalApiAuthentication.TokenRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            new string('A', 64));
        var installationId = Guid.NewGuid();
        File.WriteAllText(
            Path.Combine(
                root,
                ProductLocalApiAuthentication.InstallationIdentityRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            installationId.ToString("D"));
        return (root, installationId);
    }

    private static string GetUpdaterProductVersion()
    {
        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(
            Path.ChangeExtension(typeof(ProductServiceRepairHealthController).Assembly.Location, ".exe"))
            .ProductVersion?
            .Split('+', 2)[0];
        Assert.False(string.IsNullOrWhiteSpace(version));
        return version!;
    }

    private static HttpResponseMessage Json<T>(HttpStatusCode statusCode, T value)
        => new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json"),
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingPlatform : IProductWindowsServicePlatform
    {
        public string? ServicePath { get; private set; }

        public string? DataRoot { get; private set; }

        public Task ConfigureAndRestartAsync(
            string serviceExecutablePath,
            string dataRoot,
            CancellationToken cancellationToken)
        {
            ServicePath = serviceExecutablePath;
            DataRoot = dataRoot;
            return Task.CompletedTask;
        }

        public Task<ProductGuiActivationAck> RequestGuiActivationAsync(
            string guiExecutablePath,
            string expectedVersion,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Service-only repair must not request GUI activation.");

        public bool IsGuiActivationAlive(ProductGuiActivationAck acknowledgement)
            => throw new InvalidOperationException("Service-only repair must not inspect a GUI process.");
    }

    private sealed class QueueHttpHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (_responses.TryDequeue(out var response))
            {
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
