using System.ComponentModel;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Runtime;

/// <summary>
/// Holds the same cross-process directory lock used by <see cref="ServerProcessManager"/> without
/// starting Java. Backup and maintenance workflows can use it to prevent another manager process
/// from launching the same world while a stopped-server operation is in progress.
/// </summary>
public sealed class ServerDirectoryLease : IDisposable, IAsyncDisposable
{
    public const string LockFileName = ".minecraft-server-manager.lock";
    private IDisposable? _lease;

    private ServerDirectoryLease(IDisposable lease)
    {
        _lease = lease;
    }

    public static ServerDirectoryLease Acquire(string serverDirectoryPath)
        => new(ServerDirectoryLock.Acquire(serverDirectoryPath));

    public static ServerDirectoryLease AcquireNoFollow(string serverDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverDirectoryPath);
        var serverRoot = Path.GetFullPath(serverDirectoryPath);
        if (!Directory.Exists(serverRoot))
        {
            throw new DirectoryNotFoundException(
                $"無法啟動 Server，因為找不到資料夾：{serverRoot}");
        }

        var lockFilePath = Path.Combine(serverRoot, LockFileName);
        try
        {
            return new ServerDirectoryLease(
                SafePath.AcquireNoFollowExclusiveFileLease(lockFilePath));
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new ServerDirectoryLockException(serverRoot, lockFilePath, error);
        }
    }

    public void Dispose()
        => Interlocked.Exchange(ref _lease, null)?.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
