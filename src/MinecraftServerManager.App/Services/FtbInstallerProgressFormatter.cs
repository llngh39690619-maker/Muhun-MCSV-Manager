using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Converts the FTB installer's terminal-oriented output into bounded UI text. Progress is based
/// on the installer's file count because its stdout does not expose a trustworthy byte total.
/// </summary>
internal sealed partial class FtbInstallerProgressFormatter
{
    private const int DownloadThreads = 16;
    private const double ProgressStart = 32d;
    private const double ProgressSpan = 56d;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly object _sync = new();
    private double _baselineSeconds;
    private int _baselineCurrent;
    private int _baselineTotal;
    private int _lastCurrent;

    public OnlineModpackInstallProgress Format(FtbInstallerOutputLine line)
    {
        var fallback = line.IsError
            ? L("online.workflow.ftb.error")
            : L("online.workflow.ftb.installing");
        var cleaned = TerminalOutputSanitizer.Sanitize(line.Text, fallback);
        var match = DownloadProgressPattern().Match(cleaned);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var current)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            || total <= 0
            || current < 0
            || current > total)
        {
            return new OnlineModpackInstallProgress(
                OnlineModpackInstallStage.Extracting,
                cleaned);
        }

        string detail;
        lock (_sync)
        {
            var now = _elapsed.Elapsed.TotalSeconds;
            if (_baselineTotal != total || current < _lastCurrent)
            {
                _baselineCurrent = current;
                _baselineTotal = total;
                _baselineSeconds = now;
            }
            else if (_baselineTotal == 0)
            {
                _baselineCurrent = current;
                _baselineTotal = total;
                _baselineSeconds = now;
            }

            _lastCurrent = current;
            var seconds = now - _baselineSeconds;
            var downloadedSinceBaseline = current - _baselineCurrent;
            var filesPerSecond = seconds >= 0.5d && downloadedSinceBaseline > 0
                ? downloadedSinceBaseline / seconds
                : 0d;
            detail = filesPerSecond > 0.05d
                ? L(
                    "online.workflow.ftb.rateDetail",
                    DownloadThreads,
                    filesPerSecond,
                    FormatDuration((total - current) / filesPerSecond))
                : L("online.workflow.ftb.estimatingDetail", DownloadThreads);
        }

        var fraction = (double)current / total;
        return new OnlineModpackInstallProgress(
            OnlineModpackInstallStage.Downloading,
            L("online.workflow.ftb.downloadProgress", current, total, fraction),
            ProgressStart + fraction * ProgressSpan,
            detail);
    }

    private static string FormatDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0d)
        {
            return L("online.workflow.duration.calculating");
        }

        var rounded = TimeSpan.FromSeconds(Math.Ceiling(Math.Min(seconds, TimeSpan.FromDays(7).TotalSeconds)));
        if (rounded.TotalHours >= 1d)
        {
            return L("online.workflow.duration.hoursMinutes", (int)rounded.TotalHours, rounded.Minutes);
        }

        if (rounded.TotalMinutes >= 1d)
        {
            return L("online.workflow.duration.minutesSeconds", rounded.Minutes, rounded.Seconds);
        }

        return L("online.workflow.duration.seconds", Math.Max(0, rounded.Seconds));
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);

    [GeneratedRegex(
        @"\bDownloading(?:\.\.\.)?\s*\[\s*(\d{1,9})\s*/\s*(\d{1,9})\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DownloadProgressPattern();
}

/// <summary>Bounded ANSI/terminal text cleaner for untrusted child-process output.</summary>
internal static partial class TerminalOutputSanitizer
{
    private const int MaximumInputCharacters = 4096;
    private const int MaximumOutputCharacters = 240;

    public static string Sanitize(string? value, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var input = value.AsSpan(0, Math.Min(value.Length, MaximumInputCharacters));
        var builder = new StringBuilder(input.Length);
        for (var index = 0; index < input.Length; index++)
        {
            var character = input[index];
            if (character is '\u001b' or '\u009b')
            {
                SkipTerminalSequence(input, ref index, character == '\u009b');
                continue;
            }

            if (char.IsControl(character))
            {
                if (character is '\t' or '\r' or '\n')
                {
                    AppendSpace(builder);
                }

                continue;
            }

            builder.Append(character);
        }

        // Some process/output layers strip ESC but leave SGR fragments such as "[90m" behind.
        var withoutOrphanSgr = OrphanSgrPattern().Replace(builder.ToString(), string.Empty);
        var cleaned = WhitespacePattern().Replace(withoutOrphanSgr, " ").Trim();
        if (cleaned.Length == 0)
        {
            return fallback;
        }

        return cleaned.Length <= MaximumOutputCharacters
            ? cleaned
            : cleaned[..MaximumOutputCharacters];
    }

    private static void SkipTerminalSequence(ReadOnlySpan<char> input, ref int index, bool isC1Csi)
    {
        if (isC1Csi || index + 1 < input.Length && input[index + 1] == '[')
        {
            if (!isC1Csi)
            {
                index++;
            }

            while (index + 1 < input.Length)
            {
                var next = input[++index];
                if (next is >= '@' and <= '~')
                {
                    break;
                }
            }

            return;
        }

        // OSC: ESC ] ... BEL or ST. It is bounded by the already truncated input span.
        if (index + 1 < input.Length && input[index + 1] == ']')
        {
            index++;
            while (index + 1 < input.Length)
            {
                var next = input[++index];
                if (next == '\a')
                {
                    break;
                }

                if (next == '\u001b' && index + 1 < input.Length && input[index + 1] == '\\')
                {
                    index++;
                    break;
                }
            }

            return;
        }

        // A two-character escape sequence has no displayable content.
        if (index + 1 < input.Length)
        {
            index++;
        }
    }

    private static void AppendSpace(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != ' ')
        {
            builder.Append(' ');
        }
    }

    [GeneratedRegex(
        @"(?:\[(?:\d{1,3}(?:;\d{1,3})*)?m){2,}",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex OrphanSgrPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex WhitespacePattern();
}
