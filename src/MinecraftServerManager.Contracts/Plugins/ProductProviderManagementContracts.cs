namespace MinecraftServerManager.Contracts.Plugins;

/// <summary>Service-owned provider state exposed to trusted local management clients.</summary>
public enum ProductProviderHealthState
{
    Disabled,
    Stopped,
    Starting,
    Healthy,
    Degraded,
    Failed,
}

public sealed record ProductProviderSummary(
    string Id,
    string DisplayName,
    string Version,
    string PublisherId,
    bool Enabled,
    ProductProviderHealthState Health,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Permissions,
    DateTimeOffset InstalledAtUtc,
    DateTimeOffset LastHealthTransitionUtc,
    int ConsecutiveFailures,
    string? LastError);

public sealed record ProductProviderPage(
    int Offset,
    int NextOffset,
    bool HasMore,
    IReadOnlyList<ProductProviderSummary> Providers);

/// <summary>
/// Bounded health projection for local management. Arbitrary provider result payloads never cross
/// the 64 KiB desktop IPC boundary.
/// </summary>
public sealed record ProductProviderHealthCheckResult(
    string ProviderId,
    bool Success,
    string? ErrorCode);

/// <summary>
/// Detached package signature. Private signing keys never enter the Service API or product data.
/// </summary>
public sealed record ProductProviderDetachedSignature(
    string PublisherId,
    string Algorithm,
    string SignatureBase64,
    int FormatVersion);

/// <summary>
/// Installs only a file already placed in the Service-owned provider inbox. Arbitrary host paths
/// are intentionally not part of the public contract.
/// </summary>
public sealed record ProductProviderInstallFromInboxRequest(
    string InboxFileName,
    string ExpectedSha256,
    string ExpectedProviderId,
    string ExpectedVersion,
    string ExpectedPublisherId,
    ProductProviderDetachedSignature Signature,
    bool AllowDowngrade = false);

public sealed record ProductProviderEnableRequest(bool Enabled);

public sealed record ProductTrustedProviderPublisherSummary(
    string PublisherId,
    string PublicKeySha256,
    DateTimeOffset PinnedAtUtc);

public sealed record ProductTrustedProviderPublisherPage(
    int Offset,
    int NextOffset,
    bool HasMore,
    IReadOnlyList<ProductTrustedProviderPublisherSummary> Publishers);

/// <summary>
/// Pins an administrator-reviewed ECDSA P-256 public key. Private-key PEM is rejected at the
/// contract boundary; only canonical public SubjectPublicKeyInfo is persisted by the Service.
/// </summary>
public sealed record ProductPinProviderPublisherRequest(
    string PublisherId,
    string PublicKeyPem);
