namespace MinecraftServerManager.Core.Models;

/// <summary>The result of static inspection of a server JAR.</summary>
public sealed class DetectionResult
{
    public string FilePath { get; init; } = string.Empty;

    public CoreType CoreType { get; init; } = CoreType.Unknown;

    public string? MinecraftVersion { get; init; }

    public string? MainClass { get; init; }

    /// <summary>A heuristic confidence value in the inclusive range 0-100.</summary>
    public int ConfidencePercent { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

    /// <summary>A non-null diagnostic when the file could not be completely inspected.</summary>
    public string? Error { get; init; }

    public bool IsRecognized => CoreType != CoreType.Unknown;

    public bool IsValidJar => Error is null;
}
