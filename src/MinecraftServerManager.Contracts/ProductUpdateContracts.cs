using System.Text.Json.Serialization;

namespace MinecraftServerManager.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ProductUpdateChannel>))]
public enum ProductUpdateChannel
{
    Stable,
    Beta,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProductUpdatePhase>))]
public enum ProductUpdatePhase
{
    Disabled,
    Idle,
    Checking,
    Available,
    Downloading,
    Ready,
    Scheduled,
    Applying,
    RollingBack,
    Failed,
}

/// <summary>
/// Public update state shared by the Service, desktop GUI and remote panel. Feed URLs,
/// signing-key material and local paths are intentionally never exposed by this contract.
/// </summary>
public sealed record ProductUpdateStatus(
    ProductUpdateChannel Channel,
    ProductUpdatePhase Phase,
    string CurrentServiceVersion,
    string CurrentGuiVersion,
    bool InstalledVersionsMatch,
    bool FeedConfigured,
    string? AvailableVersion,
    long? PackageSizeBytes,
    long DownloadedBytes,
    DateTimeOffset? LastCheckedAtUtc,
    DateTimeOffset? ScheduledForUtc,
    string? ErrorCode,
    string? Message);

public sealed record ProductUpdateOperationResult(
    bool Accepted,
    ProductUpdateStatus Status,
    string? OperationId = null);
