using System.Text;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Runtime;

internal readonly record struct ConsoleLineClassification(
    ConsoleLineSeverity Severity,
    Guid? DiagnosticId = null,
    bool IsDiagnosticContinuation = false);

/// <summary>
/// Stateful, per-process-session parser for Java and Minecraft console output. The caller must
/// serialize access. stdout and stderr deliberately retain independent continuation state because
/// redirected operating-system pipes do not have a reliable shared ordering.
/// </summary>
internal sealed class ConsoleLineClassifier
{
    private const int MaximumContinuationLines = 512;

    private StreamContext _standardOutputContext;
    private StreamContext _standardErrorContext;

    public ConsoleLineClassification Classify(string text, ConsoleStream stream)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (stream == ConsoleStream.System)
        {
            return new ConsoleLineClassification(ConsoleLineSeverity.Information);
        }

        ref var context = ref GetContext(stream);
        var normalizedText = StripAnsiEscapeSequences(text);
        var normalized = normalizedText.AsSpan();

        if (normalized.IsEmpty)
        {
            context = default;
            return Fallback(stream);
        }

        // An explicit logger level always wins over the transport and over words in the message.
        // This prevents an INFO line mentioning an Error class from becoming a false diagnostic.
        if (TryParseStructuredSeverity(normalized, out var structuredSeverity))
        {
            context = default;
            return IsDiagnostic(structuredSeverity)
                ? StartDiagnostic(ref context, structuredSeverity, DiagnosticContextKind.Structured)
                : new ConsoleLineClassification(structuredSeverity);
        }

        if (IsJvmFatalMarker(normalized))
        {
            context = default;
            return StartDiagnostic(
                ref context,
                ConsoleLineSeverity.Fatal,
                DiagnosticContextKind.JvmFatal);
        }

        if (IsJvmWarningMarker(normalized))
        {
            if (context.Kind == DiagnosticContextKind.JvmWarning
                && context.ContinuationCount < MaximumContinuationLines)
            {
                return ContinueDiagnostic(ref context);
            }

            context = default;
            return StartDiagnostic(
                ref context,
                ConsoleLineSeverity.Warning,
                DiagnosticContextKind.JvmWarning);
        }

        if (context.IsActive && IsDiagnosticContinuation(normalized))
        {
            if (context.ContinuationCount < MaximumContinuationLines)
            {
                return ContinueDiagnostic(ref context);
            }

            context = default;
            return Fallback(stream);
        }

        // A blank, a normal unstructured line, or a new unrelated record ends a stack trace. The
        // parser intentionally does not keep severity sticky based only on proximity.
        context = default;

        if (IsStandaloneJavaError(normalized))
        {
            return StartDiagnostic(
                ref context,
                ConsoleLineSeverity.Error,
                DiagnosticContextKind.Throwable);
        }

        return Fallback(stream);
    }

    public void Reset()
    {
        _standardOutputContext = default;
        _standardErrorContext = default;
    }

    private ref StreamContext GetContext(ConsoleStream stream)
    {
        switch (stream)
        {
            case ConsoleStream.StandardOutput:
                return ref _standardOutputContext;
            case ConsoleStream.StandardError:
                return ref _standardErrorContext;
            default:
                throw new ArgumentOutOfRangeException(nameof(stream), stream, "Unknown console stream.");
        }
    }

    private static ConsoleLineClassification StartDiagnostic(
        ref StreamContext context,
        ConsoleLineSeverity severity,
        DiagnosticContextKind kind)
    {
        var diagnosticId = Guid.NewGuid();
        context = new StreamContext(severity, diagnosticId, kind, 0);
        return new ConsoleLineClassification(severity, diagnosticId);
    }

    private static ConsoleLineClassification ContinueDiagnostic(ref StreamContext context)
    {
        context.ContinuationCount++;
        return new ConsoleLineClassification(
            context.Severity,
            context.DiagnosticId,
            IsDiagnosticContinuation: true);
    }

    private static ConsoleLineClassification Fallback(ConsoleStream stream) => new(
        stream == ConsoleStream.StandardError
            ? ConsoleLineSeverity.Unclassified
            : ConsoleLineSeverity.Information);

    private static bool IsDiagnostic(ConsoleLineSeverity severity) => severity is
        ConsoleLineSeverity.Warning or
        ConsoleLineSeverity.Error or
        ConsoleLineSeverity.Fatal;

    private static bool TryParseStructuredSeverity(
        ReadOnlySpan<char> text,
        out ConsoleLineSeverity severity)
    {
        if (TryParseMinecraftBracketedSeverity(text, out severity)
            || TryParseIsoStatusSeverity(text, out severity)
            || TryParseLegacySeverity(text, out severity)
            || TryParseJdkUnifiedSeverity(text, out severity))
        {
            return true;
        }

        severity = default;
        return false;
    }

    private static bool TryParseMinecraftBracketedSeverity(
        ReadOnlySpan<char> text,
        out ConsoleLineSeverity severity)
    {
        severity = default;
        if (!TryReadBracket(text, 0, out var first, out var next)
            || !LooksLikeMinecraftTimestamp(first))
        {
            return false;
        }

        // Some proxy/legacy layouts place the level in the timestamp bracket:
        // [12:34:56 WARN]: message
        var finalWhitespace = LastWhitespace(first);
        if (finalWhitespace >= 0
            && LooksLikeMinecraftTimestamp(first[..finalWhitespace])
            && TryMapLevel(first[(finalWhitespace + 1)..], out severity))
        {
            return true;
        }

        next = SkipSpaces(text, next);
        if (!TryReadBracket(text, next, out var second, out next))
        {
            return false;
        }

        // Vanilla, Bukkit, Spigot, Forge and NeoForge all use thread/LEVEL in this position.
        var slash = second.LastIndexOf('/');
        if (slash >= 0 && TryMapLevel(second[(slash + 1)..], out severity))
        {
            return true;
        }

        // Older java.util.logging based servers use [timestamp] [WARNING] or [SEVERE].
        if (TryMapLevel(second, out severity))
        {
            return true;
        }

        // Accommodate strict Log4j layouts with separate [thread] [LEVEL] fields.
        next = SkipSpaces(text, next);
        return TryReadBracket(text, next, out var third, out _)
               && TryMapLevel(third, out severity);
    }

    private static bool TryParseIsoStatusSeverity(
        ReadOnlySpan<char> text,
        out ConsoleLineSeverity severity)
    {
        severity = default;
        if (!TryConsumeIsoInstant(text, out var next))
        {
            return false;
        }

        next = SkipSpaces(text, next);
        var threadTokens = 0;
        var threadCharacters = 0;
        while (threadTokens < 8 && threadCharacters <= 128 && next < text.Length)
        {
            var tokenEnd = text[next..].IndexOf(' ');
            if (tokenEnd < 0)
            {
                return false;
            }

            var token = text.Slice(next, tokenEnd);
            if (TryMapLevel(token, out severity))
            {
                // A Log4j status line always has a thread field before its exact level token.
                return threadTokens > 0;
            }

            if (!LooksLikeThreadToken(token))
            {
                return false;
            }

            threadTokens++;
            threadCharacters += token.Length;
            next = SkipSpaces(text, next + tokenEnd);
        }

        return false;
    }

    private static bool TryParseLegacySeverity(
        ReadOnlySpan<char> text,
        out ConsoleLineSeverity severity)
    {
        severity = default;
        var next = 0;

        if (LooksLikeIsoDateTime(text))
        {
            next = 19;
            if (next < text.Length && text[next] is '.' or ',')
            {
                next++;
                while (next < text.Length && char.IsAsciiDigit(text[next]))
                {
                    next++;
                }
            }
        }
        else if (LooksLikeClockTime(text))
        {
            next = 8;
        }
        else
        {
            return false;
        }

        next = SkipSpaces(text, next);
        return TryReadBracket(text, next, out var level, out _)
               && TryMapLevel(level, out severity);
    }

    private static bool TryParseJdkUnifiedSeverity(
        ReadOnlySpan<char> text,
        out ConsoleLineSeverity severity)
    {
        severity = default;
        if (text.IsEmpty || text[0] != '[')
        {
            return false;
        }

        var next = 0;
        var bracketCount = 0;
        var sawJdkTimeOrTag = false;
        ConsoleLineSeverity parsed = default;
        var sawLevel = false;

        while (bracketCount < 8 && TryReadBracket(text, next, out var field, out next))
        {
            bracketCount++;
            if (TryMapLevel(field, out var candidate))
            {
                parsed = candidate;
                sawLevel = true;
            }
            else if (LooksLikeJdkUptime(field) || LooksLikeJdkLogTag(field))
            {
                sawJdkTimeOrTag = true;
            }

            // Unified JVM logging normally has adjacent bracket fields. Permit one layout space,
            // but stop before an arbitrary message that happens to contain another bracket.
            var afterSpaces = SkipSpaces(text, next);
            if (afterSpaces - next > 1 || afterSpaces >= text.Length || text[afterSpaces] != '[')
            {
                break;
            }

            next = afterSpaces;
        }

        if (sawLevel && bracketCount >= 2 && sawJdkTimeOrTag)
        {
            severity = parsed;
            return true;
        }

        return false;
    }

    private static bool TryMapLevel(
        ReadOnlySpan<char> value,
        out ConsoleLineSeverity severity)
    {
        value = value.Trim();
        if (value.Equals("WARN", StringComparison.OrdinalIgnoreCase)
            || value.Equals("WARNING", StringComparison.OrdinalIgnoreCase))
        {
            severity = ConsoleLineSeverity.Warning;
            return true;
        }

        if (value.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            || value.Equals("SEVERE", StringComparison.OrdinalIgnoreCase))
        {
            severity = ConsoleLineSeverity.Error;
            return true;
        }

        if (value.Equals("FATAL", StringComparison.OrdinalIgnoreCase))
        {
            severity = ConsoleLineSeverity.Fatal;
            return true;
        }

        if (value.Equals("TRACE", StringComparison.OrdinalIgnoreCase)
            || value.Equals("DEBUG", StringComparison.OrdinalIgnoreCase)
            || value.Equals("INFO", StringComparison.OrdinalIgnoreCase)
            || value.Equals("CONFIG", StringComparison.OrdinalIgnoreCase)
            || value.Equals("FINE", StringComparison.OrdinalIgnoreCase)
            || value.Equals("FINER", StringComparison.OrdinalIgnoreCase)
            || value.Equals("FINEST", StringComparison.OrdinalIgnoreCase))
        {
            severity = ConsoleLineSeverity.Information;
            return true;
        }

        severity = default;
        return false;
    }

    private static bool IsJvmWarningMarker(ReadOnlySpan<char> text)
    {
        if (text.StartsWith("WARNING:", StringComparison.Ordinal))
        {
            return true;
        }

        var warningMarker = text.IndexOf(" VM warning:", StringComparison.OrdinalIgnoreCase);
        if (warningMarker is <= 0 or > 96)
        {
            return false;
        }

        var prefix = text[..warningMarker];
        return prefix.StartsWith("OpenJDK ", StringComparison.OrdinalIgnoreCase)
               || prefix.StartsWith("Java HotSpot", StringComparison.OrdinalIgnoreCase)
               || prefix.StartsWith("OpenJ9", StringComparison.OrdinalIgnoreCase)
               || prefix.StartsWith("Eclipse OpenJ9", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJvmFatalMarker(ReadOnlySpan<char> text)
    {
        text = text.TrimStart();
        if (!text.IsEmpty && text[0] == '#')
        {
            text = text[1..].TrimStart();
        }

        return text.StartsWith(
                   "A fatal error has been detected by the Java Runtime Environment",
                   StringComparison.OrdinalIgnoreCase)
               || text.StartsWith(
                   "There is insufficient memory for the Java Runtime Environment to continue",
                   StringComparison.OrdinalIgnoreCase)
               || text.StartsWith(
                   "Error occurred during initialization of VM",
                   StringComparison.OrdinalIgnoreCase)
               || text.StartsWith(
                   "Could not create the Java Virtual Machine",
                   StringComparison.OrdinalIgnoreCase)
               || text.StartsWith(
                   "A fatal exception has occurred. Program will exit",
                   StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("EXCEPTION_ACCESS_VIOLATION", StringComparison.Ordinal)
               || text.StartsWith("SIGSEGV", StringComparison.Ordinal)
               || text.StartsWith("SIGBUS", StringComparison.Ordinal)
               || text.StartsWith("SIGILL", StringComparison.Ordinal)
               || text.StartsWith("SIGFPE", StringComparison.Ordinal)
               || text.StartsWith("SIGABRT", StringComparison.Ordinal);
    }

    private static bool IsStandaloneJavaError(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        if (text.StartsWith("Exception in thread \"", StringComparison.Ordinal)
            || text.StartsWith("Caused by:", StringComparison.Ordinal)
            || text.StartsWith("Suppressed:", StringComparison.Ordinal)
            || text.StartsWith("Error: Could not find or load main class", StringComparison.Ordinal)
            || text.StartsWith("Error: Unable to initialize main class", StringComparison.Ordinal)
            || text.StartsWith("Error: Unable to access jarfile", StringComparison.Ordinal)
            || text.StartsWith("Error: LinkageError occurred while loading main class", StringComparison.Ordinal)
            || text.StartsWith("Error: A JNI error has occurred", StringComparison.Ordinal))
        {
            return true;
        }

        return LooksLikeThrowableTypePrefix(text);
    }

    private static bool IsDiagnosticContinuation(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || char.IsWhiteSpace(text[^1]) && text.Trim().IsEmpty)
        {
            return false;
        }

        var leadingWhitespace = 0;
        while (leadingWhitespace < text.Length && text[leadingWhitespace] is ' ' or '\t')
        {
            leadingWhitespace++;
        }

        var trimmed = text[leadingWhitespace..].TrimEnd();
        if (trimmed.StartsWith("at ", StringComparison.Ordinal)
            && trimmed.IndexOf('(') > 3)
        {
            return true;
        }

        if (trimmed.StartsWith("Caused by:", StringComparison.Ordinal)
            || trimmed.StartsWith("Suppressed:", StringComparison.Ordinal)
            || trimmed.StartsWith("Wrapped by:", StringComparison.Ordinal)
            || trimmed.Equals("Stacktrace:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Exception Details:", StringComparison.OrdinalIgnoreCase)
            || LooksLikeThrowableTypePrefix(trimmed)
            || LooksLikeElidedStackFrames(trimmed))
        {
            return true;
        }

        if (trimmed.Length >= 6
            && trimmed.StartsWith("-- ", StringComparison.Ordinal)
            && trimmed.EndsWith(" --", StringComparison.Ordinal))
        {
            return true;
        }

        // Exception renderers often include indented detail records that are not Java frames.
        return leadingWhitespace > 0
               && (text[0] == '\t' || leadingWhitespace >= 4);
    }

    private static bool LooksLikeThrowableTypePrefix(ReadOnlySpan<char> text)
    {
        var tokenEnd = text.IndexOfAny(':', ' ', '\t');
        var token = tokenEnd < 0 ? text : text[..tokenEnd];
        if (token.Length is < 3 or > 512 || token.IndexOf('.') <= 0)
        {
            return false;
        }

        if (!(token.EndsWith("Exception", StringComparison.Ordinal)
              || token.EndsWith("Error", StringComparison.Ordinal)
              || token.EndsWith("Throwable", StringComparison.Ordinal)))
        {
            return false;
        }

        foreach (var value in token)
        {
            if (!(char.IsAsciiLetterOrDigit(value) || value is '.' or '$' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeElidedStackFrames(ReadOnlySpan<char> text)
    {
        if (!text.StartsWith("... ", StringComparison.Ordinal)
            || !text.EndsWith(" more", StringComparison.Ordinal))
        {
            return false;
        }

        var count = text[4..^5].Trim();
        return !count.IsEmpty && count.IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static bool TryReadBracket(
        ReadOnlySpan<char> text,
        int start,
        out ReadOnlySpan<char> contents,
        out int next)
    {
        contents = default;
        next = start;
        if ((uint)start >= (uint)text.Length || text[start] != '[')
        {
            return false;
        }

        var relativeEnd = text[(start + 1)..].IndexOf(']');
        if (relativeEnd < 0 || relativeEnd > 512)
        {
            return false;
        }

        var end = start + 1 + relativeEnd;
        contents = text[(start + 1)..end];
        next = end + 1;
        return true;
    }

    private static bool LooksLikeMinecraftTimestamp(ReadOnlySpan<char> value)
    {
        if (value.Length is < 5 or > 96 || value.IndexOf(':') < 0)
        {
            return false;
        }

        var digits = 0;
        foreach (var character in value)
        {
            if (char.IsAsciiDigit(character) && ++digits >= 2)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeIsoDateTime(ReadOnlySpan<char> text) =>
        text.Length >= 19
        && char.IsAsciiDigit(text[0])
        && char.IsAsciiDigit(text[1])
        && char.IsAsciiDigit(text[2])
        && char.IsAsciiDigit(text[3])
        && text[4] == '-'
        && char.IsAsciiDigit(text[5])
        && char.IsAsciiDigit(text[6])
        && text[7] == '-'
        && char.IsAsciiDigit(text[8])
        && char.IsAsciiDigit(text[9])
        && text[10] is ' ' or 'T'
        && char.IsAsciiDigit(text[11])
        && char.IsAsciiDigit(text[12])
        && text[13] == ':'
        && char.IsAsciiDigit(text[14])
        && char.IsAsciiDigit(text[15])
        && text[16] == ':'
        && char.IsAsciiDigit(text[17])
        && char.IsAsciiDigit(text[18]);

    private static bool TryConsumeIsoInstant(ReadOnlySpan<char> text, out int next)
    {
        next = 0;
        if (!LooksLikeIsoDateTime(text) || text[10] != 'T')
        {
            return false;
        }

        next = 19;
        if (next < text.Length && text[next] is '.' or ',')
        {
            next++;
            var fractionalStart = next;
            while (next < text.Length && char.IsAsciiDigit(text[next]))
            {
                next++;
            }

            if (next == fractionalStart || next - fractionalStart > 18)
            {
                return false;
            }
        }

        if (next < text.Length && text[next] is 'Z' or 'z')
        {
            next++;
        }
        else if (next + 5 < text.Length && text[next] is '+' or '-')
        {
            next++;
            if (!char.IsAsciiDigit(text[next])
                || !char.IsAsciiDigit(text[next + 1])
                || text[next + 2] != ':'
                || !char.IsAsciiDigit(text[next + 3])
                || !char.IsAsciiDigit(text[next + 4]))
            {
                return false;
            }

            next += 5;
        }
        else
        {
            return false;
        }

        return next < text.Length && text[next] == ' ';
    }

    private static bool LooksLikeClockTime(ReadOnlySpan<char> text) =>
        text.Length >= 8
        && char.IsAsciiDigit(text[0])
        && char.IsAsciiDigit(text[1])
        && text[2] == ':'
        && char.IsAsciiDigit(text[3])
        && char.IsAsciiDigit(text[4])
        && text[5] == ':'
        && char.IsAsciiDigit(text[6])
        && char.IsAsciiDigit(text[7]);

    private static bool LooksLikeJdkUptime(ReadOnlySpan<char> field)
    {
        if (field.Length < 2 || !field.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var value in field[..^1])
        {
            if (!(char.IsAsciiDigit(value) || value == '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeJdkLogTag(ReadOnlySpan<char> field)
    {
        if (field.IsEmpty || field.Length > 128)
        {
            return false;
        }

        foreach (var value in field)
        {
            if (!(char.IsAsciiLetterOrDigit(value) || value is ',' or '+' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeThreadToken(ReadOnlySpan<char> token)
    {
        if (token.IsEmpty || token.Length > 64)
        {
            return false;
        }

        foreach (var value in token)
        {
            if (!(char.IsAsciiLetterOrDigit(value)
                  || value is '-' or '_' or '.' or '/' or '#' or '[' or ']'))
            {
                return false;
            }
        }

        return true;
    }

    private static int SkipSpaces(ReadOnlySpan<char> text, int start)
    {
        while (start < text.Length && text[start] == ' ')
        {
            start++;
        }

        return start;
    }

    private static int LastWhitespace(ReadOnlySpan<char> text)
    {
        for (var index = text.Length - 1; index >= 0; index--)
        {
            if (text[index] is ' ' or '\t')
            {
                return index;
            }
        }

        return -1;
    }

    private static string StripAnsiEscapeSequences(string text)
    {
        var firstEscape = text.IndexOf('\u001b');
        if (firstEscape < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, firstEscape);
        for (var index = firstEscape; index < text.Length; index++)
        {
            if (text[index] != '\u001b')
            {
                builder.Append(text[index]);
                continue;
            }

            if (index + 1 >= text.Length)
            {
                break;
            }

            if (text[index + 1] == '[')
            {
                index += 2;
                while (index < text.Length && text[index] is < '@' or > '~')
                {
                    index++;
                }

                continue;
            }

            if (text[index + 1] == ']')
            {
                index += 2;
                while (index < text.Length)
                {
                    if (text[index] == '\a')
                    {
                        break;
                    }

                    if (text[index] == '\u001b'
                        && index + 1 < text.Length
                        && text[index + 1] == '\\')
                    {
                        index++;
                        break;
                    }

                    index++;
                }

                continue;
            }

            // Unknown two-byte ANSI escape. Remove only the escape and its command byte.
            index++;
        }

        return builder.ToString();
    }

    private enum DiagnosticContextKind
    {
        None = 0,
        Structured,
        JvmWarning,
        Throwable,
        JvmFatal
    }

    private struct StreamContext(
        ConsoleLineSeverity severity,
        Guid diagnosticId,
        DiagnosticContextKind kind,
        int continuationCount)
    {
        public ConsoleLineSeverity Severity { get; } = severity;

        public Guid DiagnosticId { get; } = diagnosticId;

        public DiagnosticContextKind Kind { get; } = kind;

        public int ContinuationCount { get; set; } = continuationCount;

        public bool IsActive => Kind != DiagnosticContextKind.None;
    }
}
