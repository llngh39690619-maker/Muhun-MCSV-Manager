namespace MinecraftServerManager.Core.Runtime;

/// <summary>
/// Holds the same cross-process directory lock used by <see cref="ServerProcessManager"/> without
/// starting Java. Backup and maintenance workflows can use it to prevent another manager process
/// from launching the same world while a stopped-server operation is in progress.
/// </summary>
public sealed class ServerDirectoryLease : IDisposable, IAsyncDisposable
{
    private FileStream? _stream;

    private ServerDirectoryLease(FileStream stream)
    {
        _stream = stream;
    }

    public static ServerDirectoryLease Acquire(string serverDirectoryPath)
        => new(ServerDirectoryLock.Acquire(serverDirectoryPath));

    public void Dispose()
        => Interlocked.Exchange(ref _stream, null)?.Dispose();

    public ValueTask DisposeAsync()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        return stream is null ? ValueTask.CompletedTask : stream.DisposeAsync();
    }
}
