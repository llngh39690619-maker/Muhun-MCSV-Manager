using System.Collections.Concurrent;

namespace MinecraftServerManager.Service;

/// <summary>
/// Synchronous guard consulted by Core before auto-restart. A failed first launch is blocked in
/// the same state-change callback, before asynchronous registry/filesystem rollback begins.
/// </summary>
public sealed class ProductServerRestartBlocker
{
    private readonly ConcurrentDictionary<Guid, byte> _blocked = [];

    public bool IsBlocked(Guid serverId) => _blocked.ContainsKey(serverId);

    public void Block(Guid serverId) => _blocked[serverId] = 0;

    public void Unblock(Guid serverId) => _blocked.TryRemove(serverId, out _);
}
