using MinecraftServerManager.Data;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Service;

public sealed class ProductRemoteSecurityAuditSink(
    ProductSecurityAuditStore auditStore) : IRemoteSecurityAuditSink
{
    public bool TryWrite(RemoteSecurityAuditEvent auditEvent)
    {
        if (!RemoteSecurityAuditEventValidator.IsValid(auditEvent))
        {
            return false;
        }

        return auditStore.TryAppend(new ProductSecurityAuditEntry(
            auditEvent.EventId,
            auditEvent.OccurredAtUtc,
            MapAction(auditEvent.Action),
            MapOutcome(auditEvent.Outcome),
            auditEvent.Username,
            auditEvent.PermissionCode,
            auditEvent.ServerId,
            auditEvent.ReasonCode,
            auditEvent.CorrelationId));
    }

    private static string MapAction(RemoteSecurityAuditAction action) => action switch
    {
        RemoteSecurityAuditAction.CredentialLogin => "credential.login",
        RemoteSecurityAuditAction.SessionSignOut => "session.signout",
        RemoteSecurityAuditAction.RememberedDeviceEnroll => "device.enroll",
        RemoteSecurityAuditAction.RememberedDeviceRefresh => "device.refresh",
        RemoteSecurityAuditAction.ServerMutation => "server.mutation",
        RemoteSecurityAuditAction.RateLimitRejected => "rate_limit.rejected",
        _ => "remote.unknown",
    };

    private static string MapOutcome(RemoteSecurityAuditOutcome outcome) => outcome switch
    {
        RemoteSecurityAuditOutcome.Accepted => "accepted",
        RemoteSecurityAuditOutcome.Rejected => "rejected",
        RemoteSecurityAuditOutcome.Failed => "failed",
        _ => "unknown",
    };
}
