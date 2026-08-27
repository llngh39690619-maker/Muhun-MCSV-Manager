using System.Globalization;
using System.Text;

namespace MinecraftServerManager.Core.Services;

/// <summary>Pure parsing and editing operations for the server-port property.</summary>
public static class ServerPropertiesPortEditor
{
    private const string PropertyName = "server-port";

    public static bool TryReadServerPort(string contents, out int port)
    {
        ArgumentNullException.ThrowIfNull(contents);

        foreach (var line in EnumerateLines(contents))
        {
            if (!TryGetPropertyValue(line.Text, out var value))
            {
                continue;
            }

            if (int.TryParse(
                    value.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out port)
                && port is >= 1 and <= ServerPortAllocator.MaximumPort)
            {
                return true;
            }
        }

        port = default;
        return false;
    }

    /// <summary>
    /// Replaces the first active server-port property and removes later active duplicates.
    /// Commented properties and every unrelated line remain byte-for-byte identical at the
    /// string level, including their original line terminators.
    /// </summary>
    public static string SetServerPort(string contents, int port)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ValidatePort(port);

        var builder = new StringBuilder(contents.Length + 24);
        var propertyWritten = false;

        foreach (var line in EnumerateLines(contents))
        {
            if (!TryGetPropertyValue(line.Text, out _, out var indentation))
            {
                builder.Append(line.Text);
                builder.Append(line.Terminator);
                continue;
            }

            if (propertyWritten)
            {
                continue;
            }

            builder.Append(indentation);
            builder.Append(PropertyName);
            builder.Append('=');
            builder.Append(port.ToString(CultureInfo.InvariantCulture));
            builder.Append(line.Terminator);
            propertyWritten = true;
        }

        if (!propertyWritten)
        {
            AppendProperty(builder, contents, port);
        }

        return builder.ToString();
    }

    internal static void ValidatePort(int port)
    {
        if (port is < 1 or > ServerPortAllocator.MaximumPort)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                port,
                $"The server port must be between 1 and {ServerPortAllocator.MaximumPort}.");
        }
    }

    private static bool TryGetPropertyValue(string line, out string value) =>
        TryGetPropertyValue(line, out value, out _);

    private static bool TryGetPropertyValue(
        string line,
        out string value,
        out string indentation)
    {
        var index = 0;
        while (index < line.Length && IsPropertyWhitespace(line[index]))
        {
            index++;
        }

        var propertyStart = index;
        if (!line.AsSpan(index).StartsWith(PropertyName, StringComparison.Ordinal))
        {
            value = string.Empty;
            indentation = string.Empty;
            return false;
        }

        index += PropertyName.Length;
        while (index < line.Length && IsPropertyWhitespace(line[index]))
        {
            index++;
        }

        if (index >= line.Length || line[index] != '=')
        {
            value = string.Empty;
            indentation = string.Empty;
            return false;
        }

        indentation = line[..propertyStart];
        value = line[(index + 1)..];
        return true;
    }

    private static bool IsPropertyWhitespace(char value) => value is ' ' or '\t' or '\f';

    private static void AppendProperty(StringBuilder builder, string contents, int port)
    {
        var lineTerminator = FindFirstLineTerminator(contents) ?? Environment.NewLine;
        var endsWithLineTerminator = contents.EndsWith('\r') || contents.EndsWith('\n');

        if (contents.Length > 0 && !endsWithLineTerminator)
        {
            builder.Append(lineTerminator);
        }

        builder.Append(PropertyName);
        builder.Append('=');
        builder.Append(port.ToString(CultureInfo.InvariantCulture));

        if (endsWithLineTerminator)
        {
            builder.Append(lineTerminator);
        }
    }

    private static string? FindFirstLineTerminator(string contents)
    {
        for (var index = 0; index < contents.Length; index++)
        {
            if (contents[index] == '\n')
            {
                return "\n";
            }

            if (contents[index] == '\r')
            {
                return index + 1 < contents.Length && contents[index + 1] == '\n'
                    ? "\r\n"
                    : "\r";
            }
        }

        return null;
    }

    private static IEnumerable<PropertyLine> EnumerateLines(string contents)
    {
        var start = 0;
        while (start < contents.Length)
        {
            var end = start;
            while (end < contents.Length && contents[end] is not ('\r' or '\n'))
            {
                end++;
            }

            var terminatorEnd = end;
            if (terminatorEnd < contents.Length)
            {
                if (contents[terminatorEnd] == '\r'
                    && terminatorEnd + 1 < contents.Length
                    && contents[terminatorEnd + 1] == '\n')
                {
                    terminatorEnd += 2;
                }
                else
                {
                    terminatorEnd++;
                }
            }

            yield return new PropertyLine(
                contents[start..end],
                contents[end..terminatorEnd]);
            start = terminatorEnd;
        }
    }

    private readonly record struct PropertyLine(string Text, string Terminator);
}

public sealed record ServerPropertiesPortUpdateResult(
    string FilePath,
    string? BackupPath,
    int Port);

/// <summary>
/// Opaque format information returned when a server.properties document is read.
/// Pass the token back to <see cref="ServerPropertiesPortService.SaveDocumentAsync"/>
/// to retain the detected text encoding and exact byte-order mark.
/// </summary>
public sealed class ServerPropertiesDocumentFormatToken
{
    private readonly byte[] _preamble;

    internal ServerPropertiesDocumentFormatToken(Encoding encoding, byte[] preamble)
    {
        Encoding = encoding;
        _preamble = [.. preamble];
    }

    public string EncodingName => Encoding.WebName;
    public bool HasByteOrderMark => _preamble.Length > 0;
    public int ByteOrderMarkLength => _preamble.Length;

    internal Encoding Encoding { get; }
    internal byte[] CopyPreamble() => [.. _preamble];
}

public sealed record ServerPropertiesDocument(
    string FilePath,
    string Text,
    ServerPropertiesDocumentFormatToken FormatToken);

public sealed record ServerPropertiesDocumentUpdateResult(
    string FilePath,
    string? BackupPath,
    ServerPropertiesDocumentFormatToken FormatToken);

/// <summary>
/// Reads and atomically updates the one server.properties path supplied by the caller.
/// No directory scanning or server-path inference is performed.
/// </summary>
public sealed class ServerPropertiesPortService
{
    private static readonly Encoding StrictUtf8WithoutBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Encoding StrictLatin1 = Encoding.GetEncoding(
        Encoding.Latin1.CodePage,
        EncoderFallback.ExceptionFallback,
        DecoderFallback.ExceptionFallback);

    /// <summary>
    /// Reads the complete document and returns an opaque format token for a lossless later save.
    /// Returns <see langword="null"/> when the explicitly supplied file does not exist.
    /// </summary>
    public async Task<ServerPropertiesDocument?> ReadDocumentAsync(
        string serverPropertiesPath,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetExplicitFullPath(serverPropertiesPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            return null;
        }

        var decoded = await ReadTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        return new ServerPropertiesDocument(
            filePath,
            decoded.Text,
            CreateFormatToken(decoded));
    }

    /// <summary>
    /// Atomically saves complete editor text. When supplied, <paramref name="formatToken"/>
    /// preserves the encoding and exact BOM returned by <see cref="ReadDocumentAsync"/>.
    /// Without a token, an existing file is detected before writing; a new file uses UTF-8
    /// without a BOM. Line endings are taken verbatim from <paramref name="contents"/>.
    /// </summary>
    public async Task<ServerPropertiesDocumentUpdateResult> SaveDocumentAsync(
        string serverPropertiesPath,
        string contents,
        ServerPropertiesDocumentFormatToken? formatToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var filePath = GetExplicitFullPath(serverPropertiesPath);
        cancellationToken.ThrowIfCancellationRequested();

        var originalExists = File.Exists(filePath);
        DecodedText format;
        if (formatToken is not null)
        {
            format = new DecodedText(
                string.Empty,
                formatToken.Encoding,
                formatToken.CopyPreamble());
        }
        else if (originalExists)
        {
            format = await ReadTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            format = CreateDefaultDecodedText();
        }

        var backupPath = await WriteDocumentAtomicallyAsync(
            filePath,
            contents,
            format.Encoding,
            format.Preamble,
            originalExists,
            cancellationToken).ConfigureAwait(false);

        return new ServerPropertiesDocumentUpdateResult(
            filePath,
            backupPath,
            CreateFormatToken(format));
    }

    public async Task<int?> ReadServerPortAsync(
        string serverPropertiesPath,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetExplicitFullPath(serverPropertiesPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            return null;
        }

        var decoded = await ReadTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        return ServerPropertiesPortEditor.TryReadServerPort(decoded.Text, out var port)
            ? port
            : null;
    }

    public async Task<ServerPropertiesPortUpdateResult> SetServerPortAsync(
        string serverPropertiesPath,
        int port,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetExplicitFullPath(serverPropertiesPath);
        ServerPropertiesPortEditor.ValidatePort(port);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("The server.properties path has no parent directory.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The server.properties directory was not found: {directory}");
        }

        var originalExists = File.Exists(filePath);
        var decoded = originalExists
            ? await ReadTextAsync(filePath, cancellationToken).ConfigureAwait(false)
            : CreateDefaultDecodedText();
        var updatedContents = ServerPropertiesPortEditor.SetServerPort(decoded.Text, port);
        var backupPath = await WriteDocumentAtomicallyAsync(
            filePath,
            updatedContents,
            decoded.Encoding,
            decoded.Preamble,
            originalExists,
            cancellationToken).ConfigureAwait(false);

        return new ServerPropertiesPortUpdateResult(filePath, backupPath, port);
    }

    private static string GetExplicitFullPath(string serverPropertiesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverPropertiesPath);
        return Path.GetFullPath(serverPropertiesPath);
    }

    private static async Task<DecodedText> ReadTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var (encoding, preambleLength, isUtf8) = DetectEncoding(bytes);
        var contentBytes = bytes.AsSpan(preambleLength);
        string text;

        try
        {
            text = encoding.GetString(contentBytes);
        }
        catch (DecoderFallbackException) when (isUtf8)
        {
            // Traditional Java .properties files are byte-oriented and are commonly encoded
            // as ISO-8859-1. Latin-1 maps every byte to the same code point, so an invalid UTF-8
            // file can be edited without replacing or otherwise corrupting unrelated bytes.
            encoding = StrictLatin1;
            text = encoding.GetString(contentBytes);
        }

        return new DecodedText(
            text,
            encoding,
            bytes.AsSpan(0, preambleLength).ToArray());
    }

    private static DecodedText CreateDefaultDecodedText() =>
        new(string.Empty, StrictUtf8WithoutBom, []);

    private static ServerPropertiesDocumentFormatToken CreateFormatToken(DecodedText decoded) =>
        new(decoded.Encoding, decoded.Preamble);

    private static async Task<string?> WriteDocumentAtomicallyAsync(
        string filePath,
        string contents,
        Encoding encoding,
        byte[] preamble,
        bool originalExists,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("The server.properties path has no parent directory.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The server.properties directory was not found: {directory}");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        string? backupPath = null;

        try
        {
            await WriteFlushedTextAsync(
                temporaryPath,
                contents,
                encoding,
                preamble,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (originalExists)
            {
                backupPath = CreateNonOverwritingBackup(filePath);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // The uniquely named temporary file is beside the target, making this a same-volume
            // atomic rename on file systems that provide atomic replacement semantics.
            File.Move(temporaryPath, filePath, overwrite: originalExists);
            temporaryPath = string.Empty;
            return backupPath;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath))
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
    }

    private static string CreateNonOverwritingBackup(string filePath)
    {
        var preferredPath = filePath + ".bak";
        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var candidate = suffix == 0
                ? preferredPath
                : $"{preferredPath}.{suffix + 1}";
            try
            {
                File.Copy(filePath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Preserve every pre-existing backup, including files not created by this app.
                // File.Copy with overwrite:false also closes the race between two writers.
            }
        }

        throw new IOException($"Too many server.properties backup files exist beside '{filePath}'.");
    }

    private static async Task WriteFlushedTextAsync(
        string path,
        string contents,
        Encoding encoding,
        byte[] preamble,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);

        if (preamble.Length > 0)
        {
            // Write the exact prefix that was read rather than asking the selected encoding to
            // regenerate it. This also preserves a BOM if malformed UTF-8 required Latin-1
            // byte-preserving fallback for the file body.
            await stream.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
        }

        await stream.WriteAsync(encoding.GetBytes(contents), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static (Encoding Encoding, int PreambleLength, bool IsUtf8) DetectEncoding(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith(Encoding.UTF8.GetPreamble()))
        {
            return (StrictUtf8WithoutBom, Encoding.UTF8.GetPreamble().Length, true);
        }

        if (span.StartsWith(Encoding.UTF32.GetPreamble()))
        {
            return (Encoding.UTF32, Encoding.UTF32.GetPreamble().Length, false);
        }

        var utf32BigEndian = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        if (span.StartsWith(utf32BigEndian.GetPreamble()))
        {
            return (utf32BigEndian, utf32BigEndian.GetPreamble().Length, false);
        }

        if (span.StartsWith(Encoding.Unicode.GetPreamble()))
        {
            return (Encoding.Unicode, Encoding.Unicode.GetPreamble().Length, false);
        }

        if (span.StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
        {
            return (Encoding.BigEndianUnicode, Encoding.BigEndianUnicode.GetPreamble().Length, false);
        }

        return (StrictUtf8WithoutBom, 0, true);
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
            // Preserve the update/cancellation exception. The random name prevents reuse.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the update/cancellation exception.
        }
    }

    private sealed record DecodedText(string Text, Encoding Encoding, byte[] Preamble);
}
