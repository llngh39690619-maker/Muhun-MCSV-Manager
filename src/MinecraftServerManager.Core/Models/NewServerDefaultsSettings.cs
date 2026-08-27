namespace MinecraftServerManager.Core.Models;

/// <summary>
/// Defaults copied to servers created or imported after these settings are saved. Existing
/// servers are deliberately not rewritten.
/// </summary>
public sealed class NewServerDefaultsSettings
{
    /// <summary>
    /// Retained for schema compatibility. Formal 1.0 treats the manager-wide default as one fixed
    /// range; per-server Automatic remains an explicit server choice.
    /// </summary>
    public MemoryAllocationMode MemoryMode { get; set; } = MemoryAllocationMode.Manual;

    public int MinimumMemoryMb { get; set; } = 2048;

    public int MaximumMemoryMb { get; set; } = 4096;

    public bool SeparateDiagnosticOutput { get; set; } = true;

    public bool AutoRestart { get; set; }

    public bool EnableHangWatchdog { get; set; }

    public bool EnableAutomaticRecoveryPoints { get; set; }

    public NewServerDefaultsSettings Copy() => new()
    {
        MemoryMode = MemoryMode,
        MinimumMemoryMb = MinimumMemoryMb,
        MaximumMemoryMb = MaximumMemoryMb,
        SeparateDiagnosticOutput = SeparateDiagnosticOutput,
        AutoRestart = AutoRestart,
        EnableHangWatchdog = EnableHangWatchdog,
        EnableAutomaticRecoveryPoints = EnableAutomaticRecoveryPoints,
    };
}
