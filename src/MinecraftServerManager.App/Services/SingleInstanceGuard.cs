namespace MinecraftServerManager.App.Services;

/// <summary>
/// Prevents two manager processes from keeping conflicting in-memory views of the
/// same portable data directory. A live FileShare.None handle applies across Windows
/// sessions and is released by the OS if the process terminates unexpectedly.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    internal const string LockFileName = ".mcsv-manager.instance.lock";
    private FileStream? _stream;
    private readonly string _lockFilePath;

    private SingleInstanceGuard(FileStream stream, string lockFilePath)
    {
        _stream = stream;
        _lockFilePath = lockFilePath;
    }

    public static SingleInstanceGuard? TryAcquire(string applicationDirectory)
    {
        if (string.IsNullOrWhiteSpace(applicationDirectory))
        {
            throw new ArgumentException("An application directory is required.", nameof(applicationDirectory));
        }

        var directory = Path.GetFullPath(applicationDirectory);
        Directory.CreateDirectory(directory);
        var lockFilePath = Path.Combine(directory, LockFileName);
        try
        {
            var stream = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            return new SingleInstanceGuard(stream, lockFilePath);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            return null;
        }
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null) return;
        stream.Dispose();
        try
        {
            File.Delete(_lockFilePath);
        }
        catch (IOException)
        {
            // A new instance may have acquired the same file after this handle closed.
        }
        catch (UnauthorizedAccessException)
        {
            // The empty lock file contains no secret and is harmless if cleanup is denied.
        }
    }

    private static bool IsSharingViolation(IOException exception)
        => (exception.HResult & 0xFFFF) is 32 or 33;
}
