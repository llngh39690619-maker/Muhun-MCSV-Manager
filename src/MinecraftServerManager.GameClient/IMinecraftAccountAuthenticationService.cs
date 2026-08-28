using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

public interface IMinecraftAccountAuthenticationService
{
    IReadOnlyList<MinecraftClientAccountInfo> GetAccounts();

    Task<AuthenticatedMinecraftSession> AddAccountInteractivelyAsync(
        CancellationToken cancellationToken = default);

    Task<AuthenticatedMinecraftSession> AuthenticateAsync(
        string accountId,
        CancellationToken cancellationToken = default);

    Task SignOutAsync(string accountId, CancellationToken cancellationToken = default);

    Task SignOutAllAsync(CancellationToken cancellationToken = default);
}
