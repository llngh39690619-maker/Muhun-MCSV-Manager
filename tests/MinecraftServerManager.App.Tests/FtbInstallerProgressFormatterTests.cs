using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class FtbInstallerProgressFormatterTests
{
    [Fact]
    public void DownloadLine_RemovesAnsiAndProducesStructuredTwoLineProgress()
    {
        var formatter = new FtbInstallerProgressFormatter();
        var raw = "\u001b[90mDownloading... [01884/11251]\u001b[0m"
                  + string.Concat(Enumerable.Repeat("[0m[90m", 200));

        var progress = formatter.Format(new FtbInstallerOutputLine(false, raw));

        Assert.Equal(OnlineModpackInstallStage.Downloading, progress.Stage);
        Assert.Equal(
            LocalizationService.Current.Get(
                "online.workflow.ftb.downloadProgress",
                1884,
                11251,
                1884d / 11251d),
            progress.Message);
        Assert.DoesNotContain("[90m", progress.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', progress.Message);
        Assert.InRange(progress.Percentage!.Value, 41d, 42d);
        Assert.Equal(
            LocalizationService.Current.Get("online.workflow.ftb.estimatingDetail", 16),
            progress.Detail);
        Assert.DoesNotContain("[90m", progress.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherOutput_IsBoundedAndDoesNotExposeTerminalControlSequences()
    {
        var formatter = new FtbInstallerProgressFormatter();
        var raw = "\u001b]0;malicious title\aChecking files\u001b[2K\r"
                  + new string('x', 10_000);

        var progress = formatter.Format(new FtbInstallerOutputLine(false, raw));

        Assert.Equal(OnlineModpackInstallStage.Extracting, progress.Stage);
        Assert.StartsWith("Checking files", progress.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', progress.Message);
        Assert.True(progress.Message.Length <= 240);
        Assert.Null(progress.Detail);
    }

    [Fact]
    public void Sanitizer_PreservesOrdinaryBracketedTextThatOnlyLooksLikeOneSgrFragment()
    {
        var cleaned = TerminalOutputSanitizer.Sanitize("設定值 [90m 保持原文", "fallback");

        Assert.Equal("設定值 [90m 保持原文", cleaned);
    }
}
