namespace MinecraftServerManager.App.Services;

/// <summary>
/// Detects whether a Server directory is inside one of the OneDrive roots configured for the
/// current Windows user. This is deliberately a lexical, canonical path check: it does not touch
/// the filesystem, follow links, or delay Server selection while OneDrive is busy.
/// </summary>
internal static class OneDriveSyncPathDetector
{
    private static readonly string[] OneDriveEnvironmentVariables =
    [
        "OneDrive",
        "OneDriveConsumer",
        "OneDriveCommercial"
    ];

    internal static bool IsInConfiguredRoot(
        string? candidatePath,
        IEnumerable<string?>? configuredRoots = null)
    {
        if (!TryCanonicalize(candidatePath, out var candidate))
        {
            return false;
        }

        configuredRoots ??= ReadConfiguredRoots();
        foreach (var configuredRoot in configuredRoots)
        {
            if (!TryCanonicalize(configuredRoot, out var root))
            {
                continue;
            }

            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Require a path boundary after the root. For example, C:\OneDriveBackup must not
            // match C:\OneDrive. A drive or share root already ends in a separator.
            if (EndsWithDirectorySeparator(root)
                || (candidate.Length > root.Length
                    && IsDirectorySeparator(candidate[root.Length])))
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<string> ReadConfiguredRoots(
        Func<string, string?>? environmentVariableReader = null)
    {
        environmentVariableReader ??= Environment.GetEnvironmentVariable;
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var variableName in OneDriveEnvironmentVariables)
        {
            if (TryCanonicalize(environmentVariableReader(variableName), out var root))
            {
                roots.Add(root);
            }
        }

        return roots.ToArray();
    }

    private static bool TryCanonicalize(string? path, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            return canonicalPath.Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private static bool EndsWithDirectorySeparator(string path)
        => path.Length > 0 && IsDirectorySeparator(path[^1]);

    private static bool IsDirectorySeparator(char value)
        => value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
}
