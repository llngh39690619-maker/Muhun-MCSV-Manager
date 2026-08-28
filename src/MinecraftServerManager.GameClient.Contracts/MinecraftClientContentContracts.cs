namespace MinecraftServerManager.GameClient.Contracts;

/// <summary>The user-managed content areas supported by a Java client instance.</summary>
public enum MinecraftClientContentKind
{
    Mod = 0,
    ResourcePack = 1,
    ShaderPack = 2,
    Save = 3,
    Screenshot = 4,
}

public enum MinecraftClientContentState
{
    Enabled = 0,
    Disabled = 1,
    Recycled = 2,
}

/// <summary>
/// An opaque, root-confined key suitable for retaining in a UI. Callers must not construct keys
/// from arbitrary paths; every operation validates the key again before touching the filesystem.
/// </summary>
public sealed record MinecraftClientContentItemKey(
    MinecraftClientContentKind Kind,
    MinecraftClientContentState State,
    string StorageName,
    Guid? RecycleId = null);

public sealed record MinecraftClientContentEntry(
    MinecraftClientContentItemKey Key,
    string DisplayName,
    string RelativePath,
    bool IsDirectory,
    long SizeBytes,
    int FileCount,
    DateTimeOffset LastWriteTimeUtc,
    bool InspectionTruncated,
    bool IsSafe,
    string? SafetyWarning = null);

public sealed record MinecraftClientContentSnapshot(
    MinecraftClientContentKind Kind,
    DateTimeOffset ScannedAtUtc,
    IReadOnlyList<MinecraftClientContentEntry> Entries,
    bool ItemLimitReached);

public sealed record MinecraftClientContentImportRequest(
    MinecraftClientContentKind Kind,
    IReadOnlyList<string> SourcePaths);

public sealed record MinecraftClientContentImportResult(
    MinecraftClientContentKind Kind,
    IReadOnlyList<MinecraftClientContentEntry> ImportedEntries);

public sealed record MinecraftClientContentProgress(
    string Stage,
    string Message,
    int CompletedItems,
    int TotalItems,
    long CopiedBytes);

/// <summary>Bounded defaults used for both malicious-input resistance and responsive UI scans.</summary>
public sealed record MinecraftClientContentLimits
{
    public int MaximumItemsPerCategory { get; init; } = 4_096;

    public int MaximumImportSources { get; init; } = 64;

    public int MaximumImportFiles { get; init; } = 100_000;

    public long MaximumImportBytes { get; init; } = 16L * 1024 * 1024 * 1024;

    public long MaximumSingleFileBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    public long MaximumScreenshotBytes { get; init; } = 128L * 1024 * 1024;

    public int MaximumDirectoryDepth { get; init; } = 64;

    public int MaximumInspectionFilesPerItem { get; init; } = 100_000;

    public long MaximumInspectionBytesPerItem { get; init; } = 16L * 1024 * 1024 * 1024;

    /// <summary>
    /// Maximum number of filesystem entries inspected by one visible snapshot. This is shared
    /// across every item so a category containing many large folders cannot multiply the
    /// per-item allowance and monopolize a background worker indefinitely.
    /// </summary>
    public int MaximumSnapshotInspectionEntries { get; init; } = 50_000;

    /// <summary>Maximum aggregate file bytes accounted by one visible snapshot.</summary>
    public long MaximumSnapshotInspectionBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Maximum wall-clock time spent inspecting one visible snapshot.</summary>
    public int MaximumSnapshotInspectionMilliseconds { get; init; } = 1_000;

    /// <summary>
    /// Maximum recycle slots considered before sorting. This bounds enumeration memory even when
    /// an instance has accumulated an unusually large recycle directory.
    /// </summary>
    public int MaximumRecycleCandidates { get; init; } = 16_384;
}
