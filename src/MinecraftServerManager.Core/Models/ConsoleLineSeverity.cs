namespace MinecraftServerManager.Core.Models;

/// <summary>
/// Semantic severity parsed from a console line. The process stream is deliberately not a
/// severity: Java and native libraries routinely write informational diagnostics to stderr.
/// </summary>
public enum ConsoleLineSeverity
{
    Unclassified = 0,
    Information,
    Warning,
    Error,
    Fatal
}
