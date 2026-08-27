using MinecraftServerManager.Contracts.Security;

namespace MinecraftServerManager.Contracts;

/// <summary>
/// Stable governance role for a Service-owned remote account. Roles are management metadata;
/// remote Web authorization continues to be evaluated exclusively from explicit scoped grants.
/// </summary>
public enum ProductRemoteAccountRole
{
    Owner = 1,
    Admin = 2,
    Operator = 3,
    Viewer = 4,
}

public sealed record ProductRemoteAccountSummary(
    string Username,
    string CredentialSubject,
    string? Email,
    bool Enabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LockedUntilUtc,
    IReadOnlyList<ProductPermissionGrant> Grants,
    ProductRemoteAccountRole Role = ProductRemoteAccountRole.Viewer);

public sealed record ProductCreateRemoteAccountRequest(
    string Username,
    string CredentialSubject,
    string? Email,
    string Pin,
    IReadOnlyList<ProductPermissionGrant> Grants,
    ProductRemoteAccountRole? Role = null);

public sealed record ProductUpdateRemoteAccountAuthorizationRequest(
    bool Enabled,
    IReadOnlyList<ProductPermissionGrant> Grants,
    ProductRemoteAccountRole? Role = null);

public sealed record ProductUpdateRemoteAccountPinRequest(string Pin);

public sealed record ProductRevealRemoteAccountPinResponse(string Pin);

public sealed record ProductRememberedDeviceSummary(
    Guid DeviceId,
    string Username,
    string Label,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastUsedAtUtc,
    DateTimeOffset IdleExpiresAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc,
    string Status,
    DateTimeOffset? RevokedAtUtc,
    string? RevocationReason);

/// <summary>
/// Safe local-management projection of the Service-owned remote Web lifecycle. It contains no
/// authentication secret and can therefore cross the administrator-only named-pipe boundary.
/// </summary>
public sealed record ProductRemoteAccessStatus(
    bool DesiredEnabled,
    bool HostRunning,
    bool FunnelRunning,
    string? PublicUrl,
    string State,
    string? ErrorCode,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? NextRetryAtUtc);

public sealed record ProductRemoteAccountPage(
    int Offset,
    int NextOffset,
    bool HasMore,
    IReadOnlyList<ProductRemoteAccountSummary> Accounts);

public sealed record ProductRememberedDevicePage(
    int Offset,
    int NextOffset,
    bool HasMore,
    IReadOnlyList<ProductRememberedDeviceSummary> Devices);
