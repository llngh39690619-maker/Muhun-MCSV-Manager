using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

/// <summary>
/// Applies the one-shot, local-user EULA confirmation while Core holds the server directory lease.
/// Automatic recovery and remote starts never gain confirmation implicitly; they may proceed only
/// when the authoritative server-root document already contains <c>eula=true</c>.
/// </summary>
public sealed class ProductServerEulaCoordinator
{
    private readonly ProductDataLayout _layout;
    private readonly MinecraftEulaAcceptanceService _acceptance;

    public ProductServerEulaCoordinator(
        ProductDataLayout layout,
        MinecraftEulaAcceptanceService acceptance)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _acceptance = acceptance ?? throw new ArgumentNullException(nameof(acceptance));
    }

    /// <summary>
    /// Called only from ServerProcessManager's preparation hook after it acquired the exclusive
    /// cross-process lease for <paramref name="launchSnapshot"/>'s directory.
    /// </summary>
    public async Task PrepareStartAsync(
        ServerInstance launchSnapshot,
        ServerStartContext startContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchSnapshot);
        if (!MinecraftEulaAcceptanceService.IsRequired(launchSnapshot.CoreType))
        {
            return;
        }

        var verifiedDirectory = SafePath.EnsureNoReparsePointsUnderRoot(
            _layout.Servers,
            launchSnapshot.DirectoryPath);
        await _acceptance.EnsureAcceptedAsync(
                verifiedDirectory,
                startContext.UserConfirmedMinecraftEula,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Read-only check performed before Restart stops a live process. A positive user confirmation
    /// allows the stop/start transaction to proceed but does not write until the authoritative,
    /// directory-locked preparation hook runs.
    /// </summary>
    public async Task EnsureRestartMayProceedAsync(
        ServerInstance launchSnapshot,
        ServerStartContext startContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchSnapshot);
        if (!MinecraftEulaAcceptanceService.IsRequired(launchSnapshot.CoreType)
            || startContext.UserConfirmedMinecraftEula)
        {
            return;
        }

        var verifiedDirectory = SafePath.EnsureNoReparsePointsUnderRoot(
            _layout.Servers,
            launchSnapshot.DirectoryPath);
        if (!await _acceptance.IsAcceptedAsync(verifiedDirectory, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new MinecraftEulaAcceptanceRequiredException();
        }
    }
}
