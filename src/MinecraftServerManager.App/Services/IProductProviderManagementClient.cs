using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Administrator-only desktop projection of the Service-owned provider registry. Implementations
/// must cross the authenticated local IPC boundary; the GUI never starts provider executables or
/// installs from an arbitrary host path.
/// </summary>
internal interface IProductProviderManagementClient
{
    Task<IReadOnlyList<ProductProviderSummary>> ListProvidersAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductTrustedProviderPublisherSummary>> ListTrustedProviderPublishersAsync(
        CancellationToken cancellationToken = default);

    Task<ProductProviderSummary> SetProviderEnabledAsync(
        string providerId,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<ProductProviderHealthCheckResult> CheckProviderHealthAsync(
        string providerId,
        CancellationToken cancellationToken = default);

    Task UninstallProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default);

    Task<ProductTrustedProviderPublisherSummary> PinProviderPublisherAsync(
        ProductPinProviderPublisherRequest request,
        CancellationToken cancellationToken = default);

    Task RemoveProviderPublisherAsync(
        string publisherId,
        CancellationToken cancellationToken = default);

    Task<ProductProviderSummary> InstallProviderFromInboxAsync(
        ProductProviderInstallFromInboxRequest request,
        CancellationToken cancellationToken = default);
}
