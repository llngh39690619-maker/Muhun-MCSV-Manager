using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.ViewModels;

internal static class SystemConsoleLineFactory
{
    public static ConsoleLine Create(
        Guid instanceId,
        string text,
        ConsoleLineSeverity severity)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (severity is not (
            ConsoleLineSeverity.Information or
            ConsoleLineSeverity.Warning or
            ConsoleLineSeverity.Error or
            ConsoleLineSeverity.Fatal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(severity),
                severity,
                "Manager-generated console lines must declare their semantic severity.");
        }

        var isDiagnostic = severity is
            ConsoleLineSeverity.Warning or
            ConsoleLineSeverity.Error or
            ConsoleLineSeverity.Fatal;
        return new ConsoleLine(DateTimeOffset.UtcNow, text, ConsoleStream.System)
        {
            ServerInstanceId = instanceId,
            Severity = severity,
            DiagnosticId = isDiagnostic ? Guid.NewGuid() : null,
        };
    }
}
