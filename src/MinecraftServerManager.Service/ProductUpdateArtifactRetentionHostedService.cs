using MinecraftServerManager.Updater;

namespace MinecraftServerManager.Service;

/// <summary>
/// Runs signed-update artifact retention independently of notification/audit retention. Cleanup
/// is serialized by ProductUpdateCoordinator and by the external Updater's durable lease.
/// </summary>
public sealed class ProductUpdateArtifactRetentionHostedService(
    ProductUpdateCoordinator coordinator,
    TimeProvider timeProvider,
    ILogger<ProductUpdateArtifactRetentionHostedService> logger) : BackgroundService
{
    internal static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(MaintenanceInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var context = ResolveServiceInstallContext(AppContext.BaseDirectory);
            var result = await coordinator.RunArtifactRetentionAsync(
                    context.InstallRoot,
                    context.Version,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.SkippedBecauseUpdaterLeaseUnavailable)
            {
                logger.LogInformation(
                    "Update artifact retention deferred because the signed Updater owns the activation lease.");
                return;
            }

            if (result.TotalRemoved != 0 || result.FailedArtifacts != 0)
            {
                logger.LogInformation(
                    "Update retention removed {VersionCount} old versions, {PackageCount} packages, " +
                    "{CacheCount} verified caches, {StagingCount} staging trees and " +
                    "{VerificationCount} verification trees; {FailureCount} artifacts were retained fail-closed.",
                    result.InstalledVersionsRemoved,
                    result.PackagesRemoved,
                    result.VerifiedManifestCachesRemoved,
                    result.StagingDirectoriesRemoved,
                    result.VerificationDirectoriesRemoved,
                    result.FailedArtifacts);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            logger.LogWarning(
                exception,
                "Update artifact retention failed closed and will be retried without deleting protected state.");
        }
    }

    internal static ProductUpdateInstallContext ResolveServiceInstallContext(string serviceBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(serviceBaseDirectory) ||
            !Path.IsPathFullyQualified(serviceBaseDirectory))
        {
            throw new InvalidDataException("Service base directory must be absolute.");
        }

        var payloadRoot = new DirectoryInfo(Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(serviceBaseDirectory)));
        if (!string.Equals(payloadRoot.Name, "service-win-x64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Service is not running from the formal service-win-x64 payload directory.");
        }

        var versionRoot = payloadRoot.Parent
            ?? throw new InvalidDataException("Service version directory is missing.");
        ProductUpdateManifestParser.ValidateVersion(versionRoot.Name);
        var versionsRoot = versionRoot.Parent
            ?? throw new InvalidDataException("Managed versions directory is missing.");
        if (!string.Equals(versionsRoot.Name, "versions", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Service is not running from a managed versions directory.");
        }

        var installRoot = versionsRoot.Parent?.FullName
            ?? throw new InvalidDataException("Product install root is missing.");
        return new ProductUpdateInstallContext(installRoot, versionRoot.Name);
    }

    internal sealed record ProductUpdateInstallContext(string InstallRoot, string Version);
}
