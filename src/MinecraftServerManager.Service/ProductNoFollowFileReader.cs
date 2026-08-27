using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

/// <summary>
/// Opens an untrusted import file while holding non-delete-sharing handles for every ancestor.
/// This closes the Windows check/use gap where an operator could replace a verified directory
/// with a junction between File.GetAttributes and FileStream.Open.
/// </summary>
internal static class ProductNoFollowFileReader
{
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOverlapped = 0x40000000;

    public static ProductNoFollowReadLease Open(string trustedRoot, string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Product no-follow imports require Windows.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        var candidate = SafePath.EnsureWithinRoot(root, path, allowRoot: false);
        var relative = Path.GetRelativePath(root, candidate);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new UnauthorizedAccessException("Import file escaped its trusted root.");
        }

        var ancestors = new List<SafeFileHandle>(segments.Length + 1);
        SafeFileHandle? fileHandle = null;
        try
        {
            var current = root;
            ancestors.Add(OpenDirectory(current));
            for (var index = 0; index < segments.Length - 1; index++)
            {
                current = Path.Combine(current, segments[index]);
                ancestors.Add(OpenDirectory(current));
            }

            fileHandle = OpenHandle(
                candidate,
                GenericRead | FileReadAttributes,
                FileShareRead,
                FileFlagOpenReparsePoint | FileFlagSequentialScan | FileFlagOverlapped);
            var information = GetInformation(fileHandle, candidate);
            var attributes = (FileAttributes)information.FileAttributes;
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException("Import payload file cannot be a directory or reparse point.");
            }

            // A normal GUI copy creates a single-link staging inode. Rejecting additional hard
            // links prevents the privileged Service from being used to read a different path's
            // object on the same NTFS volume.
            if (information.NumberOfLinks != 1)
            {
                throw new InvalidDataException("Import payload file cannot be a hard link.");
            }

            var stream = new FileStream(
                fileHandle,
                FileAccess.Read,
                bufferSize: 128 * 1024,
                isAsync: true);
            fileHandle = null;
            return new ProductNoFollowReadLease(stream, ancestors);
        }
        catch
        {
            fileHandle?.Dispose();
            foreach (var handle in ancestors)
            {
                handle.Dispose();
            }

            throw;
        }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = OpenHandle(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        var attributes = (FileAttributes)GetInformation(handle, path).FileAttributes;
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            handle.Dispose();
            throw new InvalidDataException("Import path cannot pass through a reparse point.");
        }

        return handle;
    }

    private static SafeFileHandle OpenHandle(
        string path,
        uint access,
        uint share,
        uint flags)
    {
        var handle = CreateFileW(
            ToExtendedPath(path),
            access,
            share,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var code = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException(
            $"Import file could not be opened without following links (Win32 {code}).",
            new Win32Exception(code));
    }

    private static ByHandleFileInformation GetInformation(SafeFileHandle handle, string path)
    {
        if (GetFileInformationByHandle(handle, out var information))
        {
            return information;
        }

        var code = Marshal.GetLastWin32Error();
        throw new IOException(
            $"Import file identity could not be inspected (Win32 {code}): {Path.GetFileName(path)}.",
            new Win32Exception(code));
    }

    private static string ToExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return full;
        }

        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);
}

internal sealed class ProductNoFollowReadLease : IAsyncDisposable
{
    private FileStream? _stream;
    private List<SafeFileHandle>? _ancestors;

    internal ProductNoFollowReadLease(FileStream stream, List<SafeFileHandle> ancestors)
    {
        _stream = stream;
        _ancestors = ancestors;
    }

    public FileStream Stream => _stream
        ?? throw new ObjectDisposedException(nameof(ProductNoFollowReadLease));

    public async ValueTask DisposeAsync()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        var ancestors = Interlocked.Exchange(ref _ancestors, null);
        if (ancestors is not null)
        {
            for (var index = ancestors.Count - 1; index >= 0; index--)
            {
                ancestors[index].Dispose();
            }
        }
    }
}
