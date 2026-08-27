using System.Text;

namespace MinecraftServerManager.Service;

public sealed class ProductInstallationIdentityStore(ProductDataLayout layout)
{
    public const string FileName = "installation-id.v1";
    private readonly object _gate = new();

    public string FilePath => Path.Combine(layout.Data, FileName);

    public Guid GetOrCreate()
    {
        lock (_gate)
        {
            if (File.Exists(FilePath))
            {
                RejectExistingReparsePoints(FilePath);
                return ReadExisting();
            }

            Directory.CreateDirectory(layout.Data);
            RejectExistingReparsePoints(layout.Data);
            var installationId = Guid.NewGuid();
            var bytes = Encoding.ASCII.GetBytes(installationId.ToString("D") + Environment.NewLine);
            var temporaryPath = Path.Combine(
                layout.Data,
                $".{FileName}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            try
            {
                using var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128,
                    FileOptions.WriteThrough);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                stream.Dispose();

                File.Move(temporaryPath, FilePath, overwrite: false);
                return installationId;
            }
            catch (IOException) when (File.Exists(FilePath))
            {
                return ReadExisting();
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // A stale private temp file is harmless; never replace the committed identity.
                }
                catch (UnauthorizedAccessException)
                {
                    // Preserve the original failure/result when cleanup is blocked by policy.
                }
            }
        }
    }

    private Guid ReadExisting()
    {
        const int maximumAttempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ReadExistingOnce();
            }
            catch (IOException) when (attempt < maximumAttempts && File.Exists(FilePath))
            {
                // A second process may have just completed the atomic rename. Windows and
                // security scanners can retain a very short-lived handle after the move.
                // Retry only this transient sharing case; corrupt content still fails closed.
                Thread.Sleep(TimeSpan.FromMilliseconds(attempt * 4));
            }
        }
    }

    private Guid ReadExistingOnce()
    {
        RejectExistingReparsePoints(FilePath);
        if (!File.Exists(FilePath) ||
            (File.GetAttributes(FilePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Stored installation identity must be a regular file.");
        }

        using var stream = new FileStream(
            FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 128,
            FileOptions.SequentialScan);
        if (stream.Length is < 36 or > 64)
        {
            throw new InvalidDataException("Stored installation identity has an invalid length.");
        }

        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 128,
            leaveOpen: false);
        var text = reader.ReadToEnd().Trim();
        if (!Guid.TryParseExact(text, "D", out var installationId) || installationId == Guid.Empty)
        {
            throw new InvalidDataException("Stored installation identity is invalid.");
        }

        return installationId;
    }

    private static void RejectExistingReparsePoints(string path)
    {
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(Path.GetFullPath(path))
            : new DirectoryInfo(Path.GetFullPath(path));
        for (; current is not null; current = current switch
               {
                   FileInfo file => file.Directory,
                   DirectoryInfo directory => directory.Parent,
                   _ => null,
               })
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    "Installation identity paths cannot traverse a reparse point.");
            }
        }
    }
}
