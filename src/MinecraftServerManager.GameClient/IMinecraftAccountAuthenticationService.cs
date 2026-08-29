using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

public interface IMinecraftAccountAuthenticationService
{
    IReadOnlyList<MinecraftClientAccountInfo> GetAccounts();

    Task<AuthenticatedMinecraftSession> AddAccountInteractivelyAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the official Microsoft system-browser flow with an account identifier as a login
    /// hint. Existing authentication providers remain source-compatible through this default
    /// implementation; providers that support login hints should override it rather than collect
    /// a password in the launcher.
    /// </summary>
    Task<AuthenticatedMinecraftSession> AddAccountInteractivelyAsync(
        string loginHint,
        CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(loginHint)
            ? AddAccountInteractivelyAsync(cancellationToken)
            : throw new NotSupportedException("This authentication provider does not support login hints.");

    Task<AuthenticatedMinecraftSession> AddAccountWithDeviceCodeAsync(
        Func<MinecraftDeviceCodePrompt, Task> promptCallback,
        CancellationToken cancellationToken = default);

    Task<AuthenticatedMinecraftSession> AuthenticateAsync(
        string accountId,
        CancellationToken cancellationToken = default);

    Task<bool> RefreshIfExpiringAsync(
        string accountId,
        TimeSpan renewalWindow,
        CancellationToken cancellationToken = default);

    Task<MinecraftClientAccountInfo> RefreshProfileAsync(
        string accountId,
        CancellationToken cancellationToken = default);

    Task<MinecraftClientAccountInfo> UpdateSkinAsync(
        string accountId,
        MinecraftClientSkinVariant variant,
        string? pngFilePath,
        CancellationToken cancellationToken = default);

    Task<MinecraftClientAccountInfo> SetActiveCapeAsync(
        string accountId,
        string? capeId,
        CancellationToken cancellationToken = default);

    Task SignOutAsync(string accountId, CancellationToken cancellationToken = default);

    Task SignOutAllAsync(CancellationToken cancellationToken = default);
}
