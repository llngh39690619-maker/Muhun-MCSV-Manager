namespace MinecraftServerManager.Remote;

public enum RemoteSecurityAuditAction
{
    CredentialLogin,
    SessionSignOut,
    RememberedDeviceEnroll,
    RememberedDeviceRefresh,
    ServerMutation,
    RateLimitRejected,
}

public enum RemoteSecurityAuditOutcome
{
    Accepted,
    Rejected,
    Failed,
}

/// <summary>
/// Bounded security event safe for a durable local audit log. It intentionally excludes
/// PINs, cookies, CSRF values, commands, player names/reasons, request bodies and public URLs.
/// </summary>
public sealed record RemoteSecurityAuditEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    RemoteSecurityAuditAction Action,
    RemoteSecurityAuditOutcome Outcome,
    string? Username,
    string? PermissionCode,
    Guid? ServerId,
    string ReasonCode,
    Guid? CorrelationId = null);

public static class RemoteSecurityAuditEventValidator
{
    public static bool IsValid(RemoteSecurityAuditEvent? auditEvent)
        => auditEvent is not null &&
           auditEvent.EventId != Guid.Empty &&
           auditEvent.OccurredAtUtc.Offset == TimeSpan.Zero &&
           Enum.IsDefined(auditEvent.Action) &&
           Enum.IsDefined(auditEvent.Outcome) &&
           auditEvent.ServerId != Guid.Empty &&
           auditEvent.CorrelationId != Guid.Empty &&
           IsSafeIdentifier(auditEvent.Username, 32) &&
           IsSafeIdentifier(auditEvent.PermissionCode, 64) &&
           IsSafeIdentifier(auditEvent.ReasonCode, 64, required: true);

    private static bool IsSafeIdentifier(string? value, int maximumLength, bool required = false)
    {
        if (value is null)
        {
            return !required;
        }

        return value.Length > 0 &&
               value.Length <= maximumLength &&
               value == value.Trim() &&
               !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));
    }
}

/// <summary>
/// Synchronous acceptance lets a production host persist the authorization decision before
/// registering a backend mutation. Returning false is fail-closed when durable audit is required.
/// </summary>
public interface IRemoteSecurityAuditSink
{
    bool TryWrite(RemoteSecurityAuditEvent auditEvent);
}

internal sealed class NullRemoteSecurityAuditSink : IRemoteSecurityAuditSink
{
    public static NullRemoteSecurityAuditSink Instance { get; } = new();

    public bool TryWrite(RemoteSecurityAuditEvent auditEvent) => true;
}
