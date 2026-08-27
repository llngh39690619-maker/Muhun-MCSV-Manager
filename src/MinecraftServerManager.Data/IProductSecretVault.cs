namespace MinecraftServerManager.Data;

public interface IProductSecretVault
{
    Task SetSecretAsync(string secretReference, string secret, CancellationToken cancellationToken = default);

    Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken = default);

    Task<bool> DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default);
}
