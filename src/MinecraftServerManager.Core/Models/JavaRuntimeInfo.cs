namespace MinecraftServerManager.Core.Models;

/// <summary>Metadata describing an installed or discovered Java runtime.</summary>
public sealed class JavaRuntimeInfo
{
    public string JavaExecutablePath { get; init; } = string.Empty;

    public string HomeDirectory { get; init; } = string.Empty;

    public int MajorVersion { get; init; }

    public string FullVersion { get; init; } = string.Empty;

    public string Vendor { get; init; } = string.Empty;

    public string Architecture { get; init; } = string.Empty;

    public bool IsManaged { get; init; }

    public bool IsValid { get; init; }
}
