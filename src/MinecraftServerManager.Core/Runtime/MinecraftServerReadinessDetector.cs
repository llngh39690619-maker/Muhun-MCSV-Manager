using System.Text.RegularExpressions;

namespace MinecraftServerManager.Core.Runtime;

/// <summary>
/// Recognizes authoritative completion messages emitted by supported Minecraft server runtimes.
/// It deliberately does not treat process creation, arbitrary INFO output, or a listening socket
/// alone as proof that a newly installed modpack completed initialization.
/// </summary>
public static partial class MinecraftServerReadinessDetector
{
    private const int MaximumLineCharacters = 4096;

    public static bool IsReadyLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaximumLineCharacters)
        {
            return false;
        }

        return DonePattern().IsMatch(line);
    }

    [GeneratedRegex(
        "(?:^|\\](?::)?\\s+)Done \\([0-9]+(?:\\.[0-9]+)?s\\)! For help(?:, type [\"']help[\"'])?(?:\\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex DonePattern();
}
