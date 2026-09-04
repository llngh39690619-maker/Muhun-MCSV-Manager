using System.ComponentModel;
using System.Diagnostics;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Opens only the official machine-wide Tailscale desktop client. PATH, relative paths, registry
/// command strings, user-writable install locations, and reparse points are deliberately rejected.
/// </summary>
internal static class TailscaleInteractiveLauncher
{
    private const string ExecutableName = "tailscale-ipn.exe";

    public static bool TryLaunch()
    {
        foreach (var root in TrustedProgramFilesRoots())
        {
            var candidate = Path.GetFullPath(Path.Combine(root, "Tailscale", ExecutableName));
            if (!IsTrustedCandidate(root, candidate))
            {
                continue;
            }

            try
            {
                using var process = Process.Start(new ProcessStartInfo(candidate)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(candidate)!,
                });
                return process is not null;
            }
            catch (Exception error) when (error is Win32Exception or IOException or
                                                InvalidOperationException or NotSupportedException or
                                                UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    private static IEnumerable<string> TrustedProgramFilesRoots()
        => new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool IsTrustedCandidate(string root, string candidate)
    {
        try
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (!candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(candidate), ExecutableName,
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(candidate))
            {
                return false;
            }

            for (var current = candidate;
                 current.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
                 current = Path.GetDirectoryName(current) ?? string.Empty)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                if (string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                            NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
