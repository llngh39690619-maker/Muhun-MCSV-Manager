namespace MinecraftServerManager.Core.Runtime;

/// <summary>
/// Indicates that the manager could not obtain exclusive ownership of a server directory.
/// This is intentionally distinct from a port conflict: changing ports must never allow two
/// Minecraft processes to write to the same world.
/// </summary>
public sealed class ServerDirectoryLockException : InvalidOperationException
{
    internal ServerDirectoryLockException(
        string serverDirectoryPath,
        string lockFilePath,
        Exception innerException)
        : base(
            $"無法啟動位於「{serverDirectoryPath}」的 Server，因為不能取得獨占執行鎖「{lockFilePath}」。"
            + "可能已有另一份 Muhun MCSV Manager 正在執行同一資料夾，或鎖定檔無法存取。"
            + "請先停止既有 Server 再重試；共用同一份世界資料時，只更換 Port 並不安全。",
            innerException)
    {
        ServerDirectoryPath = serverDirectoryPath;
        LockFilePath = lockFilePath;
    }

    public string ServerDirectoryPath { get; }

    public string LockFilePath { get; }
}

internal static class ServerDirectoryLock
{
    internal const string FileName = ServerDirectoryLease.LockFileName;

    internal static FileStream Acquire(string serverDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverDirectoryPath);
        var serverRoot = Path.GetFullPath(serverDirectoryPath);
        if (!Directory.Exists(serverRoot))
        {
            throw new DirectoryNotFoundException(
                $"無法啟動 Server，因為找不到資料夾：{serverRoot}");
        }

        var lockFilePath = Path.Combine(serverRoot, FileName);
        try
        {
            // The stream, rather than the presence of the file, is the lock. Keep the file in
            // place after releasing the stream to avoid delete/recreate inode races on Unix.
            return new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ServerDirectoryLockException(serverRoot, lockFilePath, error);
        }
    }
}
