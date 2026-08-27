using System.IO;
using System.Collections.Specialized;
using System.Windows.Media;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class ConsoleDiagnosticOutputTests
{
    [Fact]
    public void LegacyPreference_MixesOutputUntilEnabledAndReflowsExistingHistoryImmediately()
    {
        var callbackCount = 0;
        var viewModel = CreateViewModel(
            separateDiagnosticOutput: null,
            _ => callbackCount++);
        var timestamp = DateTimeOffset.UtcNow;

        viewModel.AppendConsoleBatch([
            Line(timestamp, "normal", ConsoleLineSeverity.Information),
            Line(timestamp.AddSeconds(1), "warning", ConsoleLineSeverity.Warning),
            Line(timestamp.AddSeconds(2), "error", ConsoleLineSeverity.Error)
        ]);

        Assert.False(viewModel.SeparateDiagnosticOutput);
        Assert.Equal(["normal", "warning", "error"], viewModel.ConsoleLines.Select(line => line.Text));
        Assert.Equal(["warning", "error"], viewModel.DiagnosticLines.Select(line => line.Text));
        Assert.Equal("3 行", viewModel.ConsoleCountText);
        Assert.Equal(2, viewModel.DiagnosticIncidentCount);
        Assert.Equal("2 項 · 2 行", viewModel.DiagnosticCountText);
        Assert.Equal("錯誤／警告 (2)", viewModel.DiagnosticsTabHeader);
        Assert.True(viewModel.HasDiagnosticLines);

        viewModel.SeparateDiagnosticOutput = true;

        Assert.Equal(["normal"], viewModel.ConsoleLines.Select(line => line.Text));
        Assert.Equal(["warning", "error"], viewModel.DiagnosticLines.Select(line => line.Text));
        Assert.Equal(1, callbackCount);

        viewModel.SeparateDiagnosticOutput = false;

        Assert.Equal(["normal", "warning", "error"], viewModel.ConsoleLines.Select(line => line.Text));
        Assert.Equal(2, callbackCount);
        Assert.True(viewModel.Model.SeparateDiagnosticOutput == false);
    }

    [Fact]
    public void AppendConsoleBatch_RetainsOneBoundedChronologicalHistoryForBothViews()
    {
        var viewModel = CreateViewModel(separateDiagnosticOutput: true);
        var timestamp = DateTimeOffset.UtcNow;

        viewModel.AppendConsoleBatch(Enumerable.Range(0, 10_000).Select(index =>
            Line(
                timestamp.AddMilliseconds(index),
                $"line-{index}",
                index % 2 == 0 ? ConsoleLineSeverity.Information : ConsoleLineSeverity.Warning)));

        Assert.Equal(1_000, viewModel.ConsoleLines.Count);
        Assert.Equal(1_000, viewModel.DiagnosticLines.Count);
        Assert.Equal("line-8000", viewModel.ConsoleLines[0].Text);
        Assert.Equal("line-8001", viewModel.DiagnosticLines[0].Text);
        Assert.Equal("line-9998", viewModel.ConsoleLines[^1].Text);
        Assert.Equal("line-9999", viewModel.DiagnosticLines[^1].Text);

        viewModel.SeparateDiagnosticOutput = false;

        Assert.Equal(2_000, viewModel.ConsoleLines.Count);
        Assert.Equal("line-8000", viewModel.ConsoleLines[0].Text);
        Assert.Equal("line-9999", viewModel.ConsoleLines[^1].Text);
    }

    [Fact]
    public void TenThousandLineBurstAndToggle_PublishOneCollectionResetPerProjection()
    {
        var viewModel = CreateViewModel(separateDiagnosticOutput: true);
        var timestamp = DateTimeOffset.UtcNow;
        var consoleEvents = new List<NotifyCollectionChangedEventArgs>();
        var diagnosticEvents = new List<NotifyCollectionChangedEventArgs>();
        viewModel.ConsoleLines.CollectionChanged += (_, eventArgs) => consoleEvents.Add(eventArgs);
        viewModel.DiagnosticLines.CollectionChanged += (_, eventArgs) => diagnosticEvents.Add(eventArgs);

        viewModel.AppendConsoleBatch(Enumerable.Range(0, 10_000).Select(index =>
            Line(
                timestamp.AddMilliseconds(index),
                $"line-{index}",
                index % 2 == 0 ? ConsoleLineSeverity.Information : ConsoleLineSeverity.Warning)));

        Assert.Single(consoleEvents);
        Assert.Single(diagnosticEvents);
        Assert.All(consoleEvents.Concat(diagnosticEvents), eventArgs =>
            Assert.Equal(NotifyCollectionChangedAction.Reset, eventArgs.Action));
        Assert.Equal(1_000, viewModel.ConsoleLines.Count);
        Assert.Equal(1_000, viewModel.DiagnosticLines.Count);

        consoleEvents.Clear();
        diagnosticEvents.Clear();
        viewModel.SeparateDiagnosticOutput = false;

        Assert.Single(consoleEvents);
        Assert.Equal(NotifyCollectionChangedAction.Reset, consoleEvents[0].Action);
        Assert.Empty(diagnosticEvents);
        Assert.Equal(2_000, viewModel.ConsoleLines.Count);

        consoleEvents.Clear();
        viewModel.SeparateDiagnosticOutput = true;

        Assert.Single(consoleEvents);
        Assert.Equal(NotifyCollectionChangedAction.Reset, consoleEvents[0].Action);
        Assert.Empty(diagnosticEvents);
        Assert.Equal(1_000, viewModel.ConsoleLines.Count);
    }

    [Fact]
    public void ConsolePresentation_UsesSemanticLabelsAndNeverPaintsUnknownStderrAsError()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var information = new ConsoleLineViewModel(Line(
            timestamp,
            "[main/INFO] ready",
            ConsoleLineSeverity.Information,
            ConsoleStream.StandardError));
        var warning = new ConsoleLineViewModel(Line(timestamp, "warning", ConsoleLineSeverity.Warning));
        var error = new ConsoleLineViewModel(Line(timestamp, "error", ConsoleLineSeverity.Error));
        var fatal = new ConsoleLineViewModel(Line(timestamp, "fatal", ConsoleLineSeverity.Fatal));
        var systemError = new ConsoleLineViewModel(Line(
            timestamp,
            "automatic restart failed",
            ConsoleLineSeverity.Error,
            ConsoleStream.System));
        var unknownStderr = new ConsoleLineViewModel(Line(
            timestamp,
            "native launcher detail",
            ConsoleLineSeverity.Unclassified,
            ConsoleStream.StandardError));

        Assert.Equal("INFO", information.StreamText);
        Assert.Equal("WARN", warning.StreamText);
        Assert.Equal("ERROR", error.StreamText);
        Assert.Equal("FATAL", fatal.StreamText);
        Assert.Equal("ERROR", systemError.StreamText);
        Assert.Equal("STDERR", unknownStderr.StreamText);
        Assert.Equal(ColorOf(error.TextBrush), ColorOf(systemError.TextBrush));
        Assert.NotEqual(ColorOf(error.TextBrush), ColorOf(unknownStderr.TextBrush));
        Assert.NotEqual(ColorOf(error.StreamBrush), ColorOf(unknownStderr.StreamBrush));
    }

    [Fact]
    public void PerServerPreferenceAndDiagnosticCollectionsRemainIndependent()
    {
        var first = CreateViewModel(separateDiagnosticOutput: true);
        var second = CreateViewModel(separateDiagnosticOutput: false);
        var warning = Line(DateTimeOffset.UtcNow, "warning", ConsoleLineSeverity.Warning);

        first.AppendConsole(warning);
        second.AppendConsole(warning);

        Assert.Empty(first.ConsoleLines);
        Assert.Single(first.DiagnosticLines);
        Assert.Single(second.ConsoleLines);
        Assert.Single(second.DiagnosticLines);
    }

    [Fact]
    public void ManagerSystemError_IsSeparatedWhileExplicitInformationRemainsGuiConsoleOutput()
    {
        var viewModel = CreateViewModel(separateDiagnosticOutput: true);
        var information = SystemConsoleLineFactory.Create(
            viewModel.Id,
            "ordinary manager information mentioning error text",
            ConsoleLineSeverity.Information);
        var error = SystemConsoleLineFactory.Create(
            viewModel.Id,
            "manager operation failed",
            ConsoleLineSeverity.Error);

        viewModel.AppendConsoleBatch([information, error]);

        var consoleLine = Assert.Single(viewModel.ConsoleLines);
        Assert.Equal(information.Text, consoleLine.Text);
        Assert.Equal("GUI", consoleLine.StreamText);
        var diagnosticLine = Assert.Single(viewModel.DiagnosticLines);
        Assert.Equal(error.Text, diagnosticLine.Text);
        Assert.Equal("ERROR", diagnosticLine.StreamText);
        Assert.NotNull(error.DiagnosticId);
    }

    [Theory]
    [InlineData(ConsoleLineSeverity.Warning)]
    [InlineData(ConsoleLineSeverity.Error)]
    [InlineData(ConsoleLineSeverity.Fatal)]
    public void ManagerDiagnosticFactory_AssignsIncidentIdentity(ConsoleLineSeverity severity)
    {
        var line = SystemConsoleLineFactory.Create(Guid.NewGuid(), "diagnostic", severity);

        Assert.True(line.IsDiagnostic);
        Assert.True(line.StartsDiagnostic);
        Assert.NotNull(line.DiagnosticId);
    }

    [Fact]
    public void DiagnosticCount_CountsOneIncidentForAGroupedMultilineBlock()
    {
        var viewModel = CreateViewModel(separateDiagnosticOutput: true);
        var diagnosticId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;

        viewModel.AppendConsoleBatch([
            new ConsoleLine(timestamp, "root")
            {
                Severity = ConsoleLineSeverity.Error,
                DiagnosticId = diagnosticId
            },
            new ConsoleLine(timestamp.AddMilliseconds(1), "at example.Stack.frame(Stack.java:1)")
            {
                Severity = ConsoleLineSeverity.Error,
                DiagnosticId = diagnosticId,
                IsDiagnosticContinuation = true
            }
        ]);

        Assert.Equal(2, viewModel.DiagnosticLines.Count);
        Assert.Equal(1, viewModel.DiagnosticIncidentCount);
        Assert.Equal("1 項 · 2 行", viewModel.DiagnosticCountText);
        Assert.Equal("錯誤／警告 (1)", viewModel.DiagnosticsTabHeader);
    }

    private static ServerInstanceViewModel CreateViewModel(
        bool? separateDiagnosticOutput,
        Action<ServerInstanceViewModel>? preferenceChanged = null)
        => new(
            new ServerInstance
            {
                Id = Guid.NewGuid(),
                Name = "Test Server",
                DirectoryPath = Path.GetTempPath(),
                CoreType = CoreType.Paper,
                SeparateDiagnosticOutput = separateDiagnosticOutput
            },
            static (_, _) => Task.CompletedTask,
            preferenceChanged);

    private static ConsoleLine Line(
        DateTimeOffset timestamp,
        string text,
        ConsoleLineSeverity severity,
        ConsoleStream stream = ConsoleStream.StandardOutput)
        => new(timestamp, text, stream) { Severity = severity };

    private static Color ColorOf(Brush brush)
        => Assert.IsType<SolidColorBrush>(brush).Color;
}
