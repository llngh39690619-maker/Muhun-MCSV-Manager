using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

/// <summary>
/// Acquires the Service ownership-boundary handle chain and the cross-process server lock as one
/// session lease. The chain is acquired first, so a redirected server path is rejected before a
/// lock file can be created outside the product data root.
/// </summary>
public sealed class ProductServerDirectoryLeaseProvider(ProductDataLayout layout)
{
    public IDisposable Acquire(string serverDirectoryPath)
    {
        var chainLease = SafePath.AcquireNoReparseDirectoryChainLease(
            layout.Servers,
            serverDirectoryPath);
        try
        {
            var serverLease = ServerDirectoryLease.AcquireNoFollow(serverDirectoryPath);
            return new CompositeLease(chainLease, serverLease);
        }
        catch
        {
            chainLease.Dispose();
            throw;
        }
    }

    private sealed class CompositeLease(IDisposable chainLease, IDisposable serverLease) : IDisposable
    {
        private IDisposable? _chainLease = chainLease;
        private IDisposable? _serverLease = serverLease;

        public void Dispose()
        {
            try
            {
                Interlocked.Exchange(ref _serverLease, null)?.Dispose();
            }
            finally
            {
                Interlocked.Exchange(ref _chainLease, null)?.Dispose();
            }
        }
    }
}
