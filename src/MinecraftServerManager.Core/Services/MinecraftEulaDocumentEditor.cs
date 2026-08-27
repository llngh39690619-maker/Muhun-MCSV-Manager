using System.Text.RegularExpressions;

namespace MinecraftServerManager.Core.Services;

/// <summary>
/// Reads and updates only the eula property while preserving all unrelated text.
/// File locking, encoding retention and atomic replacement are handled by the caller.
/// </summary>
public static class MinecraftEulaDocumentEditor
{
    private static readonly Regex EulaPropertyPattern = new(
        @"^(?<prefix>[ \t]*eula[ \t]*=[ \t]*)(?<value>[^\r\n]*)",
        RegexOptions.CultureInvariant
        | RegexOptions.IgnoreCase
        | RegexOptions.Multiline
        | RegexOptions.NonBacktracking);

    public static bool IsAccepted(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var matches = EulaPropertyPattern.Matches(contents);
        if (matches.Count == 0)
        {
            return false;
        }

        // java.util.Properties keeps the last value when a key occurs more than once.
        return matches[^1].Groups["value"].Value.Trim()
            .Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public static string EnsureAccepted(
        string contents,
        string newline,
        DateTimeOffset acceptedAt)
    {
        ArgumentNullException.ThrowIfNull(contents);
        if (newline is not "\n" and not "\r\n")
        {
            throw new ArgumentException("Newline must be LF or CRLF.", nameof(newline));
        }

        if (IsAccepted(contents))
        {
            return contents;
        }

        if (EulaPropertyPattern.IsMatch(contents))
        {
            return EulaPropertyPattern.Replace(contents, "${prefix}true");
        }

        var separator = contents.Length > 0
                        && !contents.EndsWith('\n')
                        && !contents.EndsWith('\r')
            ? newline
            : string.Empty;
        return contents
               + separator
               + $"# Automatically accepted by configured user preference at {acceptedAt:O}{newline}"
               + $"eula=true{newline}";
    }
}
