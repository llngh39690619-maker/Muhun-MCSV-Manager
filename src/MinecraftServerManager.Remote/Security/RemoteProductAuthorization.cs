using MinecraftServerManager.Contracts.Security;

namespace MinecraftServerManager.Remote;

/// <summary>
/// One immutable authorization revision supplied by the protected desktop account store.
/// SecurityStamp must change whenever credentials, account state, or remembered-device
/// authorization is revoked. Grants are still read again for every request so a permission
/// reduction takes effect even if an adapter fails to rotate its stamp.
/// </summary>
public sealed record RemoteAuthorizationSnapshot(
    string SecurityStamp,
    IReadOnlyList<ProductPermissionGrant> Grants);

/// <summary>
/// Optional formal-product adapter implemented by the durable desktop identity store.
/// When this adapter is present, the Remote host fails closed if it cannot re-read a valid
/// snapshot. Legacy stores remain supported during migration, but never claim scoped RBAC.
/// </summary>
public interface IRemoteAuthorizationStore
{
    bool TryGetAuthorization(
        string credentialSubject,
        string username,
        out RemoteAuthorizationSnapshot snapshot);
}

public static class RemoteAuthorizationSnapshotValidator
{
    public const int MaximumSecurityStampLength = 128;
    public const int MaximumGrants = 256;

    public static bool TryCreateImmutable(
        RemoteAuthorizationSnapshot? candidate,
        out RemoteAuthorizationSnapshot snapshot)
    {
        snapshot = default!;
        if (candidate is null ||
            string.IsNullOrWhiteSpace(candidate.SecurityStamp) ||
            candidate.SecurityStamp.Length > MaximumSecurityStampLength ||
            candidate.SecurityStamp != candidate.SecurityStamp.Trim() ||
            candidate.SecurityStamp.Any(char.IsControl) ||
            candidate.Grants is null ||
            candidate.Grants.Count > MaximumGrants)
        {
            return false;
        }

        var grants = new ProductPermissionGrant[candidate.Grants.Count];
        for (var index = 0; index < candidate.Grants.Count; index++)
        {
            var grant = candidate.Grants[index];
            if (!ProductAuthorization.TryValidateGrant(grant))
            {
                return false;
            }

            grants[index] = grant;
        }

        snapshot = new RemoteAuthorizationSnapshot(candidate.SecurityStamp, grants);
        return true;
    }
}

internal static class RemoteLegacyPermissionMapping
{
    public static bool IsGranted(RemoteWebPermission permissions, string permissionCode)
        => permissionCode switch
        {
            // The preview host historically allowed all authenticated accounts to read.
            // This compatibility path is deliberately isolated and disappears as soon as
            // IRemoteAuthorizationStore is connected by the desktop/service account store.
            ProductPermissionCodes.ServerRead or
            ProductPermissionCodes.ConsoleRead or
            ProductPermissionCodes.PlayerRead => true,
            ProductPermissionCodes.ServerStart => permissions.HasFlag(RemoteWebPermission.StartServer),
            ProductPermissionCodes.ServerStop => permissions.HasFlag(RemoteWebPermission.StopServer),
            ProductPermissionCodes.ServerRestart => permissions.HasFlag(RemoteWebPermission.RestartServer),
            ProductPermissionCodes.ConsoleWrite => permissions.HasFlag(RemoteWebPermission.SendConsoleCommand),
            ProductPermissionCodes.PlayerManage => permissions.HasFlag(RemoteWebPermission.ManagePlayers),
            ProductPermissionCodes.BackupCreate => permissions.HasFlag(RemoteWebPermission.CreateBackup),
            _ => false,
        };
}

/// <summary>
/// Explicit migration bridge for the six preview checkboxes. A caller must provide exactly
/// one product Server identity; the bridge never turns those choices into global grants.
/// Read grants are intentionally separate because the preview UI never exposed them.
/// </summary>
public static class RemoteWebPermissionProductMapper
{
    public static IReadOnlyList<ProductPermissionGrant> MapToServerScope(
        RemoteWebPermission permissions,
        Guid serverId)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty server id is required.", nameof(serverId));
        }

        if ((permissions & ~RemoteWebPermission.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permissions));
        }

        var scope = ProductPermissionScope.ForServer(serverId);
        var grants = new List<ProductPermissionGrant>(6);
        Add(RemoteWebPermission.StartServer, ProductPermissionCodes.ServerStart);
        Add(RemoteWebPermission.StopServer, ProductPermissionCodes.ServerStop);
        Add(RemoteWebPermission.RestartServer, ProductPermissionCodes.ServerRestart);
        Add(RemoteWebPermission.SendConsoleCommand, ProductPermissionCodes.ConsoleWrite);
        Add(RemoteWebPermission.ManagePlayers, ProductPermissionCodes.PlayerManage);
        Add(RemoteWebPermission.CreateBackup, ProductPermissionCodes.BackupCreate);
        return grants;

        void Add(RemoteWebPermission flag, string permissionCode)
        {
            if (permissions.HasFlag(flag))
            {
                grants.Add(new ProductPermissionGrant(permissionCode, scope));
            }
        }
    }
}

public enum RemoteAuthorizationStatus
{
    Granted,
    Unauthorized,
    Forbidden,
}

public sealed record RemoteMutationAcceptanceContext(
    Guid SessionId,
    string Username,
    string PermissionCode,
    Guid? ServerId);
