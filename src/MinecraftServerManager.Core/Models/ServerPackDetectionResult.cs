namespace MinecraftServerManager.Core.Models;

/// <summary>
/// Result of static inspection of an already-installed Forge/NeoForge server pack.
/// No executable, JAR, batch file, or shell script is run while producing this result.
/// </summary>
public sealed class ServerPackDetectionResult
{
    public string DirectoryPath { get; init; } = string.Empty;

    public bool IsRecognized { get; init; }

    public bool IsRunnable { get; init; }

    public string? Error { get; init; }

    public string SuggestedName { get; init; } = string.Empty;

    public string? PackName { get; init; }

    public string? PackVersion { get; init; }

    public CoreType CoreType { get; init; } = CoreType.Unknown;

    public string? MinecraftVersion { get; init; }

    public string? ModLoaderVersion { get; init; }

    public int? JavaMajorVersion { get; init; }

    public string? JavaExecutablePath { get; init; }

    public string? SourceLaunchScriptPath { get; init; }

    /// <summary>Ordered paths relative to <see cref="DirectoryPath"/>, without leading @.</summary>
    public IReadOnlyList<string> JavaArgumentFilePaths { get; init; } = [];

    public IReadOnlyList<string> ServerArguments { get; init; } = [];

    public int? MinimumMemoryMb { get; init; }

    public int? MaximumMemoryMb { get; init; }

    public int ConfidencePercent { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public HostOperatingSystem HostOperatingSystem { get; init; }
}
