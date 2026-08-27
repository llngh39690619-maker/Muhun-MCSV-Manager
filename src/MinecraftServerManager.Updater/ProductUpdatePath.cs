namespace MinecraftServerManager.Updater;

public static class ProductUpdatePath
{
    private static readonly HashSet<string> ReservedWindowsNames = CreateReservedNames();

    public static void ValidateRelativeFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 ||
            path.StartsWith('/') || path.EndsWith('/') || path.Contains('\\') ||
            path.Contains(':') || Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Update file path is not a safe canonical relative path.");
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length is < 1 or > 100 || segment is "." or ".." ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Any(character => char.IsControl(character) || Path.GetInvalidFileNameChars().Contains(character)))
            {
                throw new InvalidDataException("Update file path contains an invalid segment.");
            }

            var stem = segment.Split('.', 2)[0];
            if (ReservedWindowsNames.Contains(stem))
            {
                throw new InvalidDataException("Update file path contains a reserved Windows name.");
            }
        }
    }

    public static string ResolveUnderRoot(string root, string relativePath)
    {
        ValidateRelativeFilePath(relativePath);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(
            Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update file path escapes its destination.");
        }

        return fullPath;
    }

    private static HashSet<string> CreateReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
        };
        for (var index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }

        return names;
    }
}
