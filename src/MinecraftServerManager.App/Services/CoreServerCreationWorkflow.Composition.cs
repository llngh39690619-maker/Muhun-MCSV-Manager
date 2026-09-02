using System.Net;
using System.Net.Http.Headers;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.App.Services;

public sealed partial class CoreServerCreationWorkflow
{
    private const string ProviderUserAgent =
        "MuhunMCSVManager/1.0 (contact: Muhun; Windows core-installer)";

    public CoreServerCreationWorkflow(ApplicationPaths paths)
        : this(CreateProductionComposition(paths))
    {
    }

    private CoreServerCreationWorkflow(ProductionComposition composition)
        : this(
            composition.Paths,
            composition.Backend,
            composition.JavaRuntimeResolver,
            composition.JdkResolver,
            composition.OwnedResources)
    {
    }

    private static ProductionComposition CreateProductionComposition(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var catalogClient = CreateNoRedirectClient();
        var directDownloadClient = CreateNoRedirectClient();
        directDownloadClient.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderUserAgent);
        var loaderClient = CreateNoRedirectClient();
        var hybridGitHubCatalogClient = CreateNoRedirectClient();
        var hybridMohistCatalogClient = CreateNoRedirectClient();
        var hybridGitHubArtifactClient = CreateNoRedirectClient();
        var hybridMohistArtifactClient = CreateNoRedirectClient();
        var spigotMetadataClient = CreateNoRedirectClient();
        var spigotArtifactClient = CreateNoRedirectClient();
        hybridGitHubArtifactClient.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderUserAgent);
        hybridMohistArtifactClient.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderUserAgent);
        var javaClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        });
        var official = new OfficialCoreServerCreationBackend(
            new OfficialServerCoreCatalogProvider(catalogClient, ProviderUserAgent),
            new VerifiedDownloadClient(directDownloadClient),
            new ModrinthOfficialLoaderArtifactProvider(loaderClient, ProviderUserAgent),
            new ModrinthLoaderBootstrapProcessRunner());
        var hybrid = new HybridCoreServerCreationBackend(
            new HybridServerCoreCatalogProvider(
                hybridGitHubCatalogClient,
                hybridMohistCatalogClient,
                ProviderUserAgent),
            new HybridServerCoreDownloader(
                hybridGitHubArtifactClient,
                hybridMohistArtifactClient));
        var spigot = new SpigotCoreServerCreationBackend(
            new SpigotBuildToolsProvider(
                spigotMetadataClient,
                spigotArtifactClient,
                ProviderUserAgent),
            new SpigotBuildToolsRunner(
                localWorkspaceRoot: Path.Combine(paths.Cache, "build-tools-work"),
                managedMinGitCacheRoot: Path.Combine(paths.Cache, "managed-tools", "mingit")));
        var backend = new CompositeCoreServerCreationBackend(
        [
            new KeyValuePair<string, ICoreServerCreationBackend>(
                OfficialCoreServerCreationBackend.SourceId,
                official),
            new KeyValuePair<string, ICoreServerCreationBackend>(
                HybridCoreServerCreationBackend.SourceId,
                hybrid),
            new KeyValuePair<string, ICoreServerCreationBackend>(
                SpigotCoreServerCreationBackend.SourceId,
                spigot)
        ]);
        var adoptium = new AdoptiumRuntimeProvider(javaClient, ProviderUserAgent);
        var javaResolver = new ManagedModrinthJavaRuntimeResolver(paths, adoptium);
        var jdkResolver = new ManagedCoreServerJdkResolver(paths, adoptium);
        return new ProductionComposition(
            paths,
            backend,
            javaResolver,
            jdkResolver,
            [
                catalogClient,
                directDownloadClient,
                loaderClient,
                hybridGitHubCatalogClient,
                hybridMohistCatalogClient,
                hybridGitHubArtifactClient,
                hybridMohistArtifactClient,
                spigotMetadataClient,
                spigotArtifactClient,
                javaClient
            ]);
    }

    private static HttpClient CreateNoRedirectClient()
        => new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        });

    private sealed record ProductionComposition(
        ApplicationPaths Paths,
        ICoreServerCreationBackend Backend,
        IModrinthJavaRuntimeResolver JavaRuntimeResolver,
        ICoreServerJdkResolver JdkResolver,
        IReadOnlyList<IDisposable> OwnedResources);
}
