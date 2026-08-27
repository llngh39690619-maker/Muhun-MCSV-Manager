using System.Windows.Media;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.ViewModels;

public sealed class ConsoleLineViewModel
{
    private static readonly Brush OutputBrush = Freeze(new SolidColorBrush(Color.FromRgb(220, 226, 233)));
    private static readonly Brush WarningBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 209, 102)));
    private static readonly Brush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 112, 112)));
    private static readonly Brush FatalBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 91, 142)));
    private static readonly Brush SystemBrush = Freeze(new SolidColorBrush(Color.FromRgb(89, 217, 142)));
    private static readonly Brush OutputTagBrush = Freeze(new SolidColorBrush(Color.FromRgb(111, 125, 143)));
    private static readonly Brush WarningTagBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 184, 77)));
    private static readonly Brush ErrorTagBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 107, 107)));
    private static readonly Brush FatalTagBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 91, 142)));
    private static readonly Brush SystemTagBrush = Freeze(new SolidColorBrush(Color.FromRgb(89, 217, 142)));

    public ConsoleLineViewModel(ConsoleLine line, long sequence = 0)
    {
        Sequence = sequence;
        TimestampUtc = line.Timestamp.ToUniversalTime();
        TimeText = line.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        Text = line.Text;
        Severity = line.Severity;
        IsDiagnostic = line.IsDiagnostic;
        StartsDiagnostic = line.StartsDiagnostic;
        DiagnosticId = line.DiagnosticId;

        if (line.Stream == ConsoleStream.System
            && line.Severity is ConsoleLineSeverity.Unclassified or ConsoleLineSeverity.Information)
        {
            StreamText = "GUI";
            TextBrush = SystemBrush;
            StreamBrush = SystemTagBrush;
            return;
        }

        (StreamText, TextBrush, StreamBrush) = line.Severity switch
        {
            ConsoleLineSeverity.Information => ("INFO", OutputBrush, OutputTagBrush),
            ConsoleLineSeverity.Warning => ("WARN", WarningBrush, WarningTagBrush),
            ConsoleLineSeverity.Error => ("ERROR", ErrorBrush, ErrorTagBrush),
            ConsoleLineSeverity.Fatal => ("FATAL", FatalBrush, FatalTagBrush),
            _ when line.Stream == ConsoleStream.StandardError => ("STDERR", WarningBrush, WarningTagBrush),
            _ when line.IsDiagnosticContinuation => ("DIAG", WarningBrush, WarningTagBrush),
            _ => ("OUT", OutputBrush, OutputTagBrush)
        };
    }

    public string TimeText { get; }
    public long Sequence { get; }
    public DateTimeOffset TimestampUtc { get; }
    public string StreamText { get; }
    public string Text { get; }
    public Brush TextBrush { get; }
    public Brush StreamBrush { get; }
    public ConsoleLineSeverity Severity { get; }
    public bool IsDiagnostic { get; }
    public bool StartsDiagnostic { get; }
    public Guid? DiagnosticId { get; }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
