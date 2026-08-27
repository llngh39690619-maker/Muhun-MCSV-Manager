using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.Core.Tests.Runtime;

public sealed class ConsoleLineClassifierTests
{
    [Theory]
    [InlineData("[16:15:23] [Server thread/INFO]: Starting", ConsoleLineSeverity.Information)]
    [InlineData("[16:15:23] [Server thread/WARN]: Warning", ConsoleLineSeverity.Warning)]
    [InlineData("[18Aug2026 18:30:45.009] [main/ERROR] [net.minecraftforge.Loader/CORE]: Failed", ConsoleLineSeverity.Error)]
    [InlineData("[18Aug2026 18:30:45.009] [main/FATAL] [net.neoforged.Loader/]: Fatal", ConsoleLineSeverity.Fatal)]
    [InlineData("[12:34:56 WARN]: Proxy warning", ConsoleLineSeverity.Warning)]
    [InlineData("[12:34:56] [main] [ERROR] Separate fields", ConsoleLineSeverity.Error)]
    [InlineData("2011-11-18 12:00:00 [WARNING] Legacy warning", ConsoleLineSeverity.Warning)]
    [InlineData("2011-11-18 12:00:00 [SEVERE] Legacy error", ConsoleLineSeverity.Error)]
    [InlineData("[0.004s][warning][os,thread] Native warning", ConsoleLineSeverity.Warning)]
    [InlineData("[0.004s][error][os] Native error", ConsoleLineSeverity.Error)]
    [InlineData("2026-08-18T10:30:52.954706900Z Server thread ERROR An exception occurred processing Appender DebugFile", ConsoleLineSeverity.Error)]
    [InlineData("2026-08-18T10:30:45.100+08:00 main WARN Advanced terminal features are not available", ConsoleLineSeverity.Warning)]
    public void ExplicitStructuredLevels_AreRecognizedAcrossSupportedFormats(
        string text,
        ConsoleLineSeverity expected)
    {
        var result = new ConsoleLineClassifier().Classify(text, ConsoleStream.StandardOutput);

        Assert.Equal(expected, result.Severity);
        Assert.Equal(
            expected is ConsoleLineSeverity.Warning or ConsoleLineSeverity.Error or ConsoleLineSeverity.Fatal,
            result.DiagnosticId.HasValue);
        Assert.False(result.IsDiagnosticContinuation);
    }

    [Fact]
    public void ExplicitLevel_OverridesTransportAndMessageWords()
    {
        var classifier = new ConsoleLineClassifier();

        var stderrInformation = classifier.Classify(
            "[12:00:00] [Server thread/INFO]: NoClassDefFoundError and FATAL are quoted text",
            ConsoleStream.StandardError);
        var stdoutError = classifier.Classify(
            "[12:00:01] [Server thread/ERROR]: Actual logger event",
            ConsoleStream.StandardOutput);

        Assert.Equal(ConsoleLineSeverity.Information, stderrInformation.Severity);
        Assert.Null(stderrInformation.DiagnosticId);
        Assert.Equal(ConsoleLineSeverity.Error, stdoutError.Severity);
        Assert.NotNull(stdoutError.DiagnosticId);
    }

    [Theory]
    [InlineData("Advanced terminal features are not available in this environment")]
    [InlineData("Picked up JAVA_TOOL_OPTIONS: -Dfile.encoding=UTF-8")]
    [InlineData("ordinary message mentioning ERROR, WARN and FATAL")]
    [InlineData("mods/ERROR/example.jar")]
    [InlineData("[player/WARN] this is not a timestamped logger header")]
    public void UnknownStderr_IsUnclassifiedInsteadOfError(string text)
    {
        var result = new ConsoleLineClassifier().Classify(text, ConsoleStream.StandardError);

        Assert.Equal(ConsoleLineSeverity.Unclassified, result.Severity);
        Assert.Null(result.DiagnosticId);
    }

    [Fact]
    public void UnknownStdout_DefaultsToInformation()
    {
        var result = new ConsoleLineClassifier().Classify(
            "A plain server message",
            ConsoleStream.StandardOutput);

        Assert.Equal(ConsoleLineSeverity.Information, result.Severity);
        Assert.Null(result.DiagnosticId);
    }

    [Fact]
    public void AnsiSgr_DoesNotHideStructuredLevel()
    {
        var result = new ConsoleLineClassifier().Classify(
            "\u001b[31m[12:00:00] [Server thread/ERROR]: boom\u001b[0m",
            ConsoleStream.StandardError);

        Assert.Equal(ConsoleLineSeverity.Error, result.Severity);
        Assert.True(result.DiagnosticId.HasValue);
    }

    [Fact]
    public void StructuredStackTrace_InheritsSeverityAndDiagnosticId()
    {
        var classifier = new ConsoleLineClassifier();
        var root = classifier.Classify(
            "2026-08-18T10:30:52.954706900Z Server thread ERROR An exception occurred processing Appender DebugFile",
            ConsoleStream.StandardError);
        var lines = new[]
        {
            "org.apache.logging.log4j.core.appender.AppenderLoggingException: java.lang.NoClassDefFoundError",
            "\tat org.apache.logging.log4j.core.config.AppenderControl.tryCallAppender(AppenderControl.java:165)",
            "Caused by: java.lang.ExceptionInInitializerError: Exception java.lang.IllegalStateException",
            "\tSuppressed: java.lang.IllegalArgumentException: detail",
            "\t... 12 more",
        };

        Assert.Equal(ConsoleLineSeverity.Error, root.Severity);
        Assert.NotNull(root.DiagnosticId);
        Assert.False(root.IsDiagnosticContinuation);

        foreach (var text in lines)
        {
            var continuation = classifier.Classify(text, ConsoleStream.StandardError);
            Assert.Equal(ConsoleLineSeverity.Error, continuation.Severity);
            Assert.Equal(root.DiagnosticId, continuation.DiagnosticId);
            Assert.True(continuation.IsDiagnosticContinuation);
        }
    }

    [Fact]
    public void NewHeaderBlankAndOrdinaryLine_EndContinuationContext()
    {
        var classifier = new ConsoleLineClassifier();
        classifier.Classify(
            "[12:00:00] [Server thread/ERROR]: first",
            ConsoleStream.StandardError);
        var ordinary = classifier.Classify("Done", ConsoleStream.StandardError);
        var orphanAfterOrdinary = classifier.Classify("\tat example.Main.run(Main.java:1)", ConsoleStream.StandardError);

        classifier.Classify(
            "[12:00:01] [Server thread/ERROR]: second",
            ConsoleStream.StandardError);
        var blank = classifier.Classify(string.Empty, ConsoleStream.StandardError);
        var orphanAfterBlank = classifier.Classify("\tat example.Main.run(Main.java:2)", ConsoleStream.StandardError);

        classifier.Classify(
            "[12:00:02] [Server thread/ERROR]: third",
            ConsoleStream.StandardError);
        var information = classifier.Classify(
            "[12:00:03] [Server thread/INFO]: recovered",
            ConsoleStream.StandardError);
        var orphanAfterInformation = classifier.Classify(
            "\tat example.Main.run(Main.java:3)",
            ConsoleStream.StandardError);

        Assert.All(
            [ordinary, orphanAfterOrdinary, blank, orphanAfterBlank, orphanAfterInformation],
            line => Assert.Equal(ConsoleLineSeverity.Unclassified, line.Severity));
        Assert.Equal(ConsoleLineSeverity.Information, information.Severity);
    }

    [Fact]
    public void StreamsHaveIndependentContinuationState()
    {
        var classifier = new ConsoleLineClassifier();
        var root = classifier.Classify(
            "[12:00:00] [Server thread/ERROR]: stderr root",
            ConsoleStream.StandardError);

        var stdoutOrphan = classifier.Classify(
            "\tat example.Stdout.run(Stdout.java:1)",
            ConsoleStream.StandardOutput);
        var stderrContinuation = classifier.Classify(
            "\tat example.Stderr.run(Stderr.java:1)",
            ConsoleStream.StandardError);

        Assert.Equal(ConsoleLineSeverity.Information, stdoutOrphan.Severity);
        Assert.Null(stdoutOrphan.DiagnosticId);
        Assert.Equal(ConsoleLineSeverity.Error, stderrContinuation.Severity);
        Assert.Equal(root.DiagnosticId, stderrContinuation.DiagnosticId);
    }

    [Fact]
    public void ConsecutiveJvmWarnings_AreOneDiagnosticBlock()
    {
        var classifier = new ConsoleLineClassifier();

        var root = classifier.Classify(
            "WARNING: A restricted method in java.lang.System has been called",
            ConsoleStream.StandardError);
        var continuation = classifier.Classify(
            "WARNING: java.lang.System::load has been called by com.sun.jna.Native",
            ConsoleStream.StandardError);

        Assert.Equal(ConsoleLineSeverity.Warning, root.Severity);
        Assert.True(root.DiagnosticId.HasValue);
        Assert.False(root.IsDiagnosticContinuation);
        Assert.Equal(root.DiagnosticId, continuation.DiagnosticId);
        Assert.True(continuation.IsDiagnosticContinuation);
    }

    [Theory]
    [InlineData("Exception in thread \"main\" java.lang.IllegalStateException: boom", ConsoleLineSeverity.Error)]
    [InlineData("java.lang.NoClassDefFoundError: Could not initialize class io.netty.channel.epoll.Native", ConsoleLineSeverity.Error)]
    [InlineData("Error: Unable to access jarfile server.jar", ConsoleLineSeverity.Error)]
    [InlineData("# A fatal error has been detected by the Java Runtime Environment:", ConsoleLineSeverity.Fatal)]
    [InlineData("Error occurred during initialization of VM", ConsoleLineSeverity.Fatal)]
    [InlineData("Could not create the Java Virtual Machine.", ConsoleLineSeverity.Fatal)]
    public void CanonicalJvmMarkers_AreClassified(
        string text,
        ConsoleLineSeverity expected)
    {
        var result = new ConsoleLineClassifier().Classify(text, ConsoleStream.StandardError);

        Assert.Equal(expected, result.Severity);
        Assert.True(result.DiagnosticId.HasValue);
        Assert.False(result.IsDiagnosticContinuation);
    }

    [Fact]
    public void Reset_DropsBothStreamContexts()
    {
        var classifier = new ConsoleLineClassifier();
        classifier.Classify(
            "[12:00:00] [Server thread/ERROR]: stdout",
            ConsoleStream.StandardOutput);
        classifier.Classify(
            "[12:00:00] [Server thread/WARN]: stderr",
            ConsoleStream.StandardError);

        classifier.Reset();

        Assert.Equal(
            ConsoleLineSeverity.Information,
            classifier.Classify("\tat example.Output.run(Output.java:1)", ConsoleStream.StandardOutput).Severity);
        Assert.Equal(
            ConsoleLineSeverity.Unclassified,
            classifier.Classify("\tat example.Error.run(Error.java:1)", ConsoleStream.StandardError).Severity);
    }

    [Fact]
    public void ContinuationState_IsBounded()
    {
        var classifier = new ConsoleLineClassifier();
        classifier.Classify(
            "[12:00:00] [Server thread/ERROR]: root",
            ConsoleStream.StandardError);

        ConsoleLineClassification result = default;
        for (var index = 0; index < 512; index++)
        {
            result = classifier.Classify(
                $"\tat example.Frame{index}.run(Frame.java:{index + 1})",
                ConsoleStream.StandardError);
        }

        Assert.Equal(ConsoleLineSeverity.Error, result.Severity);
        Assert.True(result.IsDiagnosticContinuation);

        var overflow = classifier.Classify(
            "\tat example.Overflow.run(Overflow.java:1)",
            ConsoleStream.StandardError);
        Assert.Equal(ConsoleLineSeverity.Unclassified, overflow.Severity);
        Assert.Null(overflow.DiagnosticId);
    }

    [Fact]
    public void MaximumSizedUntrustedLine_DoesNotSearchForTrailingKeywords()
    {
        var text = new string('x', 64 * 1024 - 6) + " ERROR";

        var result = new ConsoleLineClassifier().Classify(text, ConsoleStream.StandardError);

        Assert.Equal(ConsoleLineSeverity.Unclassified, result.Severity);
        Assert.Null(result.DiagnosticId);
    }
}
