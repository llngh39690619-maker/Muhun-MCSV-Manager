using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

public interface IModrinthClientContentCatalog
{
    Task<ModrinthClientContentSearchPage> SearchAsync(
        ModrinthClientContentSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<ModrinthClientContentProject> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModrinthClientContentVersion>> GetStableVersionsAsync(
        string projectId,
        string gameVersion,
        MinecraftClientLoader? loader = null,
        CancellationToken cancellationToken = default);

    Task<ModrinthClientContentVersion> GetStableVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default);

    Task<ModrinthClientContentVersion> SelectStableVersionAsync(
        string projectId,
        string gameVersion,
        MinecraftClientLoader? loader = null,
        CancellationToken cancellationToken = default);
}
