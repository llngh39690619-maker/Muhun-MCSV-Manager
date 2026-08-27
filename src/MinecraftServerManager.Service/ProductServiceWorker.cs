using Microsoft.Extensions.Hosting;

namespace MinecraftServerManager.Service;

public sealed class ProductServiceWorker(
    ProductDataLayout layout,
    ProductDatabaseInitializer databaseInitializer,
    ProductInstallationIdentityStore identityStore,
    ProductServerRegistry registry,
    ProductLocalApiAuthenticator localApiAuthenticator,
    ProductServerRuntime serverRuntime,
    ProductServerImportService serverImports,
    ProductServerModpackUpdateCoordinator modpackUpdates,
    ProductProviderCoordinator providers,
    ProductServiceState state,
    ILogger<ProductServiceWorker> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        layout.EnsureCreated();
        await databaseInitializer.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        await serverImports.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await modpackUpdates.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await providers.InitializeAsync(cancellationToken).ConfigureAwait(false);
        localApiAuthenticator.Initialize();
        state.Initialize(identityStore.GetOrCreate());
        state.MarkFoundationReady();
        logger.LogInformation(
            "Muhun MCSV Service foundation is ready. Data root: {DataRoot}",
            layout.Root);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        state.MarkNotReady();
        logger.LogInformation("Muhun MCSV Service foundation is stopping.");
        try
        {
            try
            {
                await modpackUpdates.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await serverImports.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await serverRuntime.ShutdownAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
