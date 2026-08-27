namespace MinecraftServerManager.ProviderHost;

/// <summary>
/// Removes an installed provider by first taking its executable tree out of the runnable package
/// namespace, then committing the registry removal. A failed registry commit restores the tree.
/// </summary>
public sealed class ProviderPackageUninstaller(
    ProviderHostLayout layout,
    ProviderRegistry registry)
{
    public async Task<bool> UninstallAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (!registry.TryGet(providerId, out _))
        {
            return false;
        }

        layout.EnsureCreated();
        var providerDirectory = ProviderPathSafety.ResolveOwnedRelativePath(layout.Packages, providerId);
        if (!Directory.Exists(providerDirectory))
        {
            throw new InvalidDataException("Registered provider package directory is missing.");
        }

        ProviderPathSafety.EnsureExistingPathHasNoReparsePoints(layout.Packages, providerDirectory);
        ProviderPathSafety.EnsureTreeHasNoReparsePoints(
            providerDirectory,
            ProviderPackageInstaller.MaximumEntries * 8);

        var quarantine = Path.Combine(layout.Packages, $".uninstall-{Guid.NewGuid():N}");
        Directory.Move(providerDirectory, quarantine);
        var registryCommitted = false;
        try
        {
            registryCommitted = await registry.RemoveAsync(providerId, cancellationToken)
                .ConfigureAwait(false);
            if (!registryCommitted)
            {
                Directory.Move(quarantine, providerDirectory);
                return false;
            }
        }
        catch
        {
            if (Directory.Exists(quarantine) && !Directory.Exists(providerDirectory))
            {
                Directory.Move(quarantine, providerDirectory);
            }

            throw;
        }

        if (registryCommitted)
        {
            try
            {
                ProviderPathSafety.DeleteOwnedTree(quarantine);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The dot-prefixed quarantine is outside every registry path and cannot execute.
                // A later maintenance pass may remove it after transient scanners release handles.
            }
        }

        return true;
    }
}
