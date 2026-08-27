using System.IO.Compression;
using System.Text;

namespace MinecraftServerManager.ProviderHost;

public sealed class ProviderHostLayout
{
    public ProviderHostLayout(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(Root)!);
        if (Root.Equals(volumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Provider host root cannot be a volume root.", nameof(rootDirectory));
        }

        Packages = Path.Combine(Root, "packages");
        State = Path.Combine(Root, "state");
    }

    public string Root { get; }
    public string Packages { get; }
    public string State { get; }
    public string RegistryFile => Path.Combine(State, "provider-registry.v1.json");

    public void EnsureCreated()
    {
        ProviderPathSafety.RejectExistingReparseAncestors(Root);
        Directory.CreateDirectory(Root);
        ProviderPathSafety.RejectExistingReparseAncestors(Root);
        Directory.CreateDirectory(Packages);
        Directory.CreateDirectory(State);
        ProviderPathSafety.EnsureExistingPathHasNoReparsePoints(Root, Packages);
        ProviderPathSafety.EnsureExistingPathHasNoReparsePoints(Root, State);
    }
}

internal static class ProviderPathSafety
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string ResolveOwnedRelativePath(string rootDirectory, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var destination = Path.GetFullPath(
            Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Provider path leaves its managed root.");
        }

        return destination;
    }

    public static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Contains('\\') ||
            value.StartsWith('/') || value.Contains(':') ||
            value.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            throw new InvalidDataException("Provider package contains an unsafe path.");
        }

        var parts = value.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
        {
            throw new InvalidDataException("Provider package path traversal was rejected.");
        }

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index].Normalize(NormalizationForm.FormC);
            if (part.Length is 0 or > 255 || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            {
                throw new InvalidDataException("Provider package contains a Windows-unsafe path.");
            }

            var baseName = part.Split('.')[0].TrimEnd(' ', '.');
            if (ReservedWindowsNames.Contains(baseName))
            {
                throw new InvalidDataException("Provider package contains a reserved Windows path.");
            }

            parts[index] = part;
        }

        return string.Join('/', parts);
    }

    public static void RejectArchiveLink(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var dosAttributes = entry.ExternalAttributes & 0xFFFF;
        var upperAttributes = (entry.ExternalAttributes >> 16) & 0xFFFF;
        if (unixType == 0xA000 ||
            (dosAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
            (upperAttributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Provider packages cannot contain links or reparse points.");
        }
    }

    public static void EnsureExistingPathHasNoReparsePoints(string rootDirectory, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var candidate = Path.GetFullPath(candidatePath);
        if (candidate != root &&
            !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Provider path leaves its managed root.");
        }

        RejectExistingReparsePoint(root);
        if (candidate == root)
        {
            return;
        }

        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            RejectExistingReparsePoint(current);
        }
    }

    public static void EnsureTreeHasNoReparsePoints(string rootDirectory, int maximumEntries = 4096)
    {
        RejectExistingReparsePoint(rootDirectory);
        var pending = new Stack<string>();
        pending.Push(rootDirectory);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > maximumEntries)
                {
                    throw new InvalidDataException("Provider package tree exceeds its entry limit.");
                }

                var attributes = File.GetAttributes(item);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException("Provider package tree contains a reparse point.");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(item);
                }
            }
        }
    }

    public static void RejectExistingReparsePoint(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Provider managed paths cannot be reparse points.");
        }
    }

    public static void RejectExistingReparseAncestors(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(fullPath)
                         ?? throw new InvalidDataException("Provider path has no volume root.");
        var current = volumeRoot;
        RejectExistingReparsePoint(current);
        var relative = Path.GetRelativePath(volumeRoot, fullPath);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                return;
            }

            RejectExistingReparsePoint(current);
        }
    }

    public static void CreateSafeParentDirectories(string rootDirectory, string destination)
    {
        var parent = Path.GetDirectoryName(destination)
                     ?? throw new InvalidDataException("Provider package destination has no parent directory.");
        var relative = Path.GetRelativePath(rootDirectory, parent);
        var current = rootDirectory;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
            {
                throw new IOException("Provider package directory collides with a file.");
            }

            Directory.CreateDirectory(current);
            RejectExistingReparsePoint(current);
        }
    }

    public static void DeleteOwnedTree(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return;
        }

        var attributes = File.GetAttributes(rootDirectory);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(rootDirectory);
            return;
        }

        foreach (var item in Directory.EnumerateFileSystemEntries(rootDirectory))
        {
            var itemAttributes = File.GetAttributes(item);
            if (itemAttributes.HasFlag(FileAttributes.Directory) &&
                !itemAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                DeleteOwnedTree(item);
            }
            else if (itemAttributes.HasFlag(FileAttributes.Directory))
            {
                Directory.Delete(item);
            }
            else
            {
                File.Delete(item);
            }
        }

        Directory.Delete(rootDirectory);
    }
}
