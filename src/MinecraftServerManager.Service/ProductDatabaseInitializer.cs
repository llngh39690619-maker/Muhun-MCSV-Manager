using MinecraftServerManager.Data;

namespace MinecraftServerManager.Service;

/// <summary>Serializes the idempotent schema initialization shared by hosted services.</summary>
public sealed class ProductDatabaseInitializer(ProductDatabase database)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _initialized))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _initialized, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
