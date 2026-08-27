namespace MinecraftServerManager.Core.Models;

/// <summary>One timestamped line emitted by a managed server process.</summary>
public sealed record ConsoleLine(
    DateTimeOffset Timestamp,
    string Text,
    ConsoleStream Stream = ConsoleStream.StandardOutput)
{
    public Guid? ServerInstanceId { get; init; }

    /// <summary>The process session that emitted this line.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>
    /// Severity parsed from an explicit Minecraft, Forge, NeoForge, or JVM log marker. Unknown
    /// stderr remains unclassified instead of being promoted to an error.
    /// </summary>
    public ConsoleLineSeverity Severity { get; init; } = ConsoleLineSeverity.Unclassified;

    /// <summary>
    /// Identifies one warning/error/fatal block. A root and its recognized stack-trace lines share
    /// the same value so consumers can count incidents instead of counting every stack frame.
    /// </summary>
    public Guid? DiagnosticId { get; init; }

    public bool IsDiagnosticContinuation { get; init; }

    public bool IsDiagnostic => Severity is
        ConsoleLineSeverity.Warning or
        ConsoleLineSeverity.Error or
        ConsoleLineSeverity.Fatal;

    public bool StartsDiagnostic => IsDiagnostic && !IsDiagnosticContinuation;
}
