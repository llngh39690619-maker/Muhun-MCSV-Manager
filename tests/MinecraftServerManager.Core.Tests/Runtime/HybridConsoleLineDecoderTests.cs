using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.Core.Tests.Runtime;

public sealed class HybridConsoleLineDecoderTests
{
    static HybridConsoleLineDecoderTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void Append_ValidUtf8SplitInsideMultibyteCharacter_DecodesUtf8()
    {
        var decoder = CreateDecoder();
        var bytes = Encoding.UTF8.GetBytes("玩家上線：炎龍\n");
        var split = Array.IndexOf(bytes, (byte)0xE7) + 1;
        var lines = new List<string>();

        decoder.Append(bytes.AsSpan(0, split), lines.Add);
        decoder.Append(bytes.AsSpan(split), lines.Add);
        decoder.Complete(lines.Add);

        Assert.Equal(["玩家上線：炎龍"], lines);
    }

    [Fact]
    public void Append_InvalidUtf8AcrossChunks_FallsBackToWindowsCodePage()
    {
        var decoder = CreateDecoder();
        var expected = "載入 C:\\Users\\llngh\\OneDrive\\文件\\新增資料夾\\server.jar 中的 Java 代理程式時發生錯誤";
        var bytes = Encoding.GetEncoding(950).GetBytes(expected + "\r\n");
        var lines = new List<string>();

        for (var index = 0; index < bytes.Length; index += 3)
        {
            decoder.Append(bytes.AsSpan(index, Math.Min(3, bytes.Length - index)), lines.Add);
        }
        decoder.Complete(lines.Add);

        Assert.Equal([expected], lines);
    }

    [Fact]
    public void Append_CrlfLfAndBlankLines_PreservesLineFraming()
    {
        var decoder = CreateDecoder();
        var lines = new List<string>();

        decoder.Append(Encoding.UTF8.GetBytes("one\r"), lines.Add);
        decoder.Append(Encoding.UTF8.GetBytes("\ntwo\n\r\nthree"), lines.Add);
        decoder.Complete(lines.Add);

        Assert.Equal(["one", "two", string.Empty, "three"], lines);
    }

    [Fact]
    public void Complete_EmitsFinalLineWithoutNewline_ExactlyOnce()
    {
        var decoder = CreateDecoder();
        var lines = new List<string>();

        decoder.Append(Encoding.UTF8.GetBytes("tail"), lines.Add);
        decoder.Complete(lines.Add);

        Assert.Equal(["tail"], lines);
        Assert.Throws<ObjectDisposedException>(() => decoder.Complete(lines.Add));
    }

    [Fact]
    public void Append_OversizedLine_IsBoundedAndNextLineStillDecodes()
    {
        var decoder = new HybridConsoleLineDecoder(
            maximumLineBytes: 4,
            fallbackEncoding: Encoding.GetEncoding(950));
        var lines = new List<string>();

        decoder.Append(Encoding.UTF8.GetBytes("abcdef\r\nok\n"), lines.Add);
        decoder.Complete(lines.Add);

        Assert.Equal("abcd" + HybridConsoleLineDecoder.TruncationMarker, lines[0]);
        Assert.Equal("ok", lines[1]);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void Append_IndependentStreamDecoders_DoNotSharePartialBytes()
    {
        var outputDecoder = CreateDecoder();
        var errorDecoder = CreateDecoder();
        var outputLines = new List<string>();
        var errorLines = new List<string>();

        outputDecoder.Append(Encoding.UTF8.GetBytes("std"), outputLines.Add);
        errorDecoder.Append(Encoding.UTF8.GetBytes("err\n"), errorLines.Add);
        outputDecoder.Append(Encoding.UTF8.GetBytes("out\n"), outputLines.Add);
        errorDecoder.Complete(errorLines.Add);
        outputDecoder.Complete(outputLines.Add);

        Assert.Equal(["stdout"], outputLines);
        Assert.Equal(["err"], errorLines);
    }

    [Fact]
    public async Task RawPump_DrainsEofTailAndStripsOnlyCrlfDelimiter()
    {
        var bytes = Encoding.UTF8.GetBytes("first\r\nsecond\r");
        await using var stream = new MemoryStream(bytes, writable: false);
        var lines = new List<string>();

        await RawProcessOutputPump.RunAsync(
            stream,
            lines.Add,
            CancellationToken.None,
            fallbackEncoding: Encoding.GetEncoding(950));

        Assert.Equal(["first", "second\r"], lines);
    }

    [Fact]
    public void Constructor_RejectsUnboundedLineSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HybridConsoleLineDecoder(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HybridConsoleLineDecoder(HybridConsoleLineDecoder.MaximumAllowedLineBytes + 1));
    }

    [Fact]
    public void BuildStartInfo_ConfiguresOnlyStdinEncoding_OutputRemainsRaw()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(temporaryDirectory.Path, "server.jar"), [0]);
        var inputEncoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        var manager = new ServerProcessManager(new ServerProcessManagerOptions
        {
            StandardInputEncoding = inputEncoding
        });
        var instance = new ServerInstance
        {
            DirectoryPath = temporaryDirectory.Path,
            ServerJarPath = "server.jar",
            JavaExecutablePath = "java.exe"
        };

        var startInfo = manager.BuildStartInfo(instance);

        Assert.Equal(inputEncoding, startInfo.StandardInputEncoding);
        Assert.Null(startInfo.StandardOutputEncoding);
        Assert.Null(startInfo.StandardErrorEncoding);
    }

    private static HybridConsoleLineDecoder CreateDecoder() =>
        new(fallbackEncoding: Encoding.GetEncoding(950));
}
