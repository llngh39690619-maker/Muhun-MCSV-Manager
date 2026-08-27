namespace MinecraftServerManager.Core.Services;

public interface IJsonSettingsStore<T>
    where T : class
{
    string FilePath { get; }

    Task<T?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(T settings, CancellationToken cancellationToken = default);
}
