using Microsoft.Extensions.Logging.Abstractions;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductUpdateArtifactRetentionHostedServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-RetentionHostedTests",
        Guid.NewGuid().ToString("N"));

    public ProductUpdateArtifactRetentionHostedServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ImmediateMaintenance_FromQaTestPayloadPathFailsSoft()
    {
        var layout = new ProductDataLayout(Path.Combine(_root, "data-root"));
        layout.EnsureCreated();
        var updateOptions = new ProductUpdateOptions();
        var serviceOptions = new ProductServiceOptions
        {
            DataRoot = layout.Root,
            Updates = updateOptions,
        };
        using var coordinator = new ProductUpdateCoordinator(
            updateOptions,
            serviceOptions,
            layout,
            new ProductInstallationIdentityStore(layout),
            new NeverLaunchUpdater(),
            TimeProvider.System);
        var hosted = new ProductUpdateArtifactRetentionHostedService(
            coordinator,
            TimeProvider.System,
            NullLogger<ProductUpdateArtifactRetentionHostedService>.Instance);

        // The testhost path is intentionally not ...\versions\<semver>\service-win-x64.
        // Startup maintenance must log and defer rather than terminate the Service.
        await hosted.RunOnceAsync(CancellationToken.None);
    }

    [Fact]
    public void FormalServicePayloadPathResolvesExactInstallContext()
    {
        var installRoot = Path.Combine(_root, "formal-product");
        var payloadRoot = Path.Combine(
            installRoot,
            "versions",
            "1.2.3",
            "service-win-x64");

        var context = ProductUpdateArtifactRetentionHostedService.ResolveServiceInstallContext(payloadRoot);

        Assert.Equal(Path.GetFullPath(installRoot), Path.GetFullPath(context.InstallRoot));
        Assert.Equal("1.2.3", context.Version);
    }

    private sealed class NeverLaunchUpdater : IProductUpdateActivationLauncher
    {
        public Task LaunchAsync(
            string updaterExecutablePath,
            string requestPath,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Updater should not launch during retention startup tests.");
    }
}
