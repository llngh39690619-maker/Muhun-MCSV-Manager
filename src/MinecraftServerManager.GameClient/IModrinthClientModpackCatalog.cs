using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

public interface IModrinthClientModpackCatalog
{
    Task<ModrinthClientModpackSearchPage> SearchAsync(
        ModrinthClientModpackSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<ModrinthClientModpackSearchPage> GetPopularAsync(
        ModrinthClientModpackSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<ModrinthClientModpackProject> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModrinthClientModpackVersion>> GetStableVersionsAsync(
        string projectId,
        string? gameVersion = null,
        MinecraftClientLoader? loader = null,
        CancellationToken cancellationToken = default);

    Task<ModrinthClientModpackVersion> GetStableVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default);
}
