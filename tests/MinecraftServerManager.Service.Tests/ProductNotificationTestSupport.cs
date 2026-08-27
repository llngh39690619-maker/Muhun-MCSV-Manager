using System.Collections.Concurrent;
using MinecraftServerManager.Data;

namespace MinecraftServerManager.Service.Tests;

internal sealed class MemoryProductSecretVault : IProductSecretVault
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task SetSecretAsync(
        string secretReference,
        string secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values[secretReference] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_values.TryGetValue(secretReference, out var value) ? value : null);
    }

    public Task<bool> DeleteSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_values.TryRemove(secretReference, out _));
    }
}
