using System.Text;

namespace MinecraftServerManager.Core.Providers;

/// <summary>
/// Drains process text streams without allowing an installer to allocate an unbounded line or
/// retain an unbounded transcript. Reading continues after the capture limit so a child process
/// cannot deadlock on a full stdout/stderr pipe.
/// </summary>
internal static class BoundedProcessOutputCapture
{
    internal const int DefaultMaximumLines = 50_000;
    internal const int DefaultMaximumCharacters = 8 * 1024 * 1024;
    internal const int DefaultMaximumLineCharacters = 64 * 1024;

    internal static async Task<BoundedCapturedStream> CaptureAsync(
        TextReader reader,
        Action<string>? onCapturedLine = null,
        int maximumLines = DefaultMaximumLines,
        int maximumCharacters = DefaultMaximumCharacters,
        int maximumLineCharacters = DefaultMaximumLineCharacters)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maximumLines < 1 || maximumCharacters < 1 || maximumLineCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLines));
        }

        var lines = new List<string>();
        var current = new StringBuilder(Math.Min(maximumLineCharacters, 4096));
        var buffer = new char[4096];
        var characters = 0;
        var discardRemainderOfLine = false;
        var previousWasCarriageReturn = false;
        var truncated = false;

        void CompleteLine()
        {
            if (lines.Count < maximumLines
                && characters <= maximumCharacters - current.Length)
            {
                var line = current.ToString();
                lines.Add(line);
                characters += line.Length;
                onCapturedLine?.Invoke(line);
            }
            else
            {
                truncated = true;
            }

            current.Clear();
            discardRemainderOfLine = false;
        }

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\r')
                {
                    CompleteLine();
                    previousWasCarriageReturn = true;
                    continue;
                }

                if (character == '\n')
                {
                    if (!previousWasCarriageReturn)
                    {
                        CompleteLine();
                    }

                    previousWasCarriageReturn = false;
                    continue;
                }

                previousWasCarriageReturn = false;
                if (!discardRemainderOfLine && current.Length < maximumLineCharacters)
                {
                    current.Append(character);
                }
                else
                {
                    discardRemainderOfLine = true;
                    truncated = true;
                }
            }
        }

        if (current.Length > 0 || discardRemainderOfLine)
        {
            CompleteLine();
        }

        return new BoundedCapturedStream(lines, truncated);
    }
}

internal sealed record BoundedCapturedStream(
    IReadOnlyList<string> Lines,
    bool Truncated);
