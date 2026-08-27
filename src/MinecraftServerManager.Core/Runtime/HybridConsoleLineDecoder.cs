using System.Runtime.InteropServices;
using System.Text;

namespace MinecraftServerManager.Core.Runtime;

/// <summary>
/// Splits redirected process bytes into bounded lines and decodes each complete line without
/// assuming that every Java generation uses the same Windows encoding. Valid UTF-8 wins;
/// otherwise Windows' active ANSI code page is used as a compatibility fallback.
/// </summary>
internal sealed class HybridConsoleLineDecoder
{
    internal const int DefaultMaximumLineBytes = 64 * 1024;
    internal const int MaximumAllowedLineBytes = 16 * 1024 * 1024;
    internal const string TruncationMarker = " … [console line truncated]";

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Encoding LenientUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    private readonly byte[] _lineBuffer;
    private readonly Encoding _fallbackEncoding;
    private int _lineLength;
    private bool _pendingCarriageReturn;
    private bool _lineWasTruncated;
    private bool _isFirstLine = true;
    private bool _completed;

    public HybridConsoleLineDecoder(
        int maximumLineBytes = DefaultMaximumLineBytes,
        Encoding? fallbackEncoding = null)
    {
        if (maximumLineBytes is < 1 or > MaximumAllowedLineBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLineBytes),
                $"Maximum line size must be between 1 and {MaximumAllowedLineBytes} bytes.");
        }

        _lineBuffer = new byte[maximumLineBytes];
        _fallbackEncoding = fallbackEncoding ?? CreateHostFallbackEncoding();
    }

    public void Append(ReadOnlySpan<byte> bytes, Action<string> emitLine)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        ArgumentNullException.ThrowIfNull(emitLine);

        foreach (var value in bytes)
        {
            if (value == (byte)'\n')
            {
                // A pending CR belongs to the delimiter. A CR not followed by LF is preserved.
                _pendingCarriageReturn = false;
                EmitCurrentLine(emitLine);
                continue;
            }

            if (_pendingCarriageReturn)
            {
                AppendContentByte((byte)'\r');
                _pendingCarriageReturn = false;
            }

            if (value == (byte)'\r')
            {
                _pendingCarriageReturn = true;
                continue;
            }

            AppendContentByte(value);
        }
    }

    public void Complete(Action<string> emitLine)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        ArgumentNullException.ThrowIfNull(emitLine);

        if (_pendingCarriageReturn)
        {
            AppendContentByte((byte)'\r');
            _pendingCarriageReturn = false;
        }

        if (_lineLength > 0 || _lineWasTruncated)
        {
            EmitCurrentLine(emitLine);
        }

        _completed = true;
    }

    internal static Encoding CreateHostFallbackEncoding()
    {
        if (!OperatingSystem.IsWindows())
        {
            return LenientUtf8;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            var activeCodePage = checked((int)GetACP());
            return Encoding.GetEncoding(
                activeCodePage,
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or OverflowException)
        {
            // UTF-8 with replacement is a safe last resort on unusual Windows configurations.
            return LenientUtf8;
        }
    }

    private void AppendContentByte(byte value)
    {
        if (_lineLength < _lineBuffer.Length)
        {
            _lineBuffer[_lineLength++] = value;
            return;
        }

        // Discard the remainder of this logical line while retaining only a bounded prefix.
        _lineWasTruncated = true;
    }

    private void EmitCurrentLine(Action<string> emitLine)
    {
        var text = Decode(_lineBuffer.AsSpan(0, _lineLength));
        if (_isFirstLine)
        {
            text = text.TrimStart('\uFEFF');
            _isFirstLine = false;
        }

        if (_lineWasTruncated)
        {
            text += TruncationMarker;
        }

        _lineLength = 0;
        _lineWasTruncated = false;
        emitLine(text);
    }

    private string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return _fallbackEncoding.GetString(bytes);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}

/// <summary>Drains one redirected byte stream through its own line decoder.</summary>
internal static class RawProcessOutputPump
{
    private const int ReadBufferBytes = 16 * 1024;

    public static async Task RunAsync(
        Stream stream,
        Action<string> emitLine,
        CancellationToken cancellationToken,
        int maximumLineBytes = HybridConsoleLineDecoder.DefaultMaximumLineBytes,
        Encoding? fallbackEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(emitLine);

        var decoder = new HybridConsoleLineDecoder(maximumLineBytes, fallbackEncoding);
        var buffer = new byte[ReadBufferBytes];
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            decoder.Append(buffer.AsSpan(0, bytesRead), emitLine);
        }

        decoder.Complete(emitLine);
    }
}
