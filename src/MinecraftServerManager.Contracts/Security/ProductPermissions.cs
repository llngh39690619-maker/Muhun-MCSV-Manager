using System.Collections.ObjectModel;

namespace MinecraftServerManager.Contracts.Security;

public static class ProductPermissionCodes
{
    public const string ServerRead = "server.read";
    public const string ServerStart = "server.start";
    public const string ServerStop = "server.stop";
    public const string ServerRestart = "server.restart";
    public const string ConsoleRead = "console.read";
    public const string ConsoleWrite = "console.write";
    public const string PlayerRead = "player.read";
    public const string PlayerManage = "player.manage";
    public const string FileRead = "file.read";
    public const string FileWrite = "file.write";
    public const string BackupRead = "backup.read";
    public const string BackupCreate = "backup.create";
    public const string BackupRestore = "backup.restore";
    public const string BackupDelete = "backup.delete";
    public const string ServerSettingsRead = "server.settings.read";
    public const string ServerSettingsWrite = "server.settings.write";
    public const string ServerUpdate = "server.update";
    public const string UserRead = "user.read";
    public const string UserManage = "user.manage";
    public const string PermissionManage = "permission.manage";
    public const string NotificationRead = "notification.read";
    public const string NotificationManage = "notification.manage";
    public const string ProviderRead = "provider.read";
    public const string ProviderManage = "provider.manage";
    public const string ProductUpdate = "product.update";
    public const string UpdateManage = "update.manage";
    public const string AuditRead = "audit.read";
    public const string ServiceManage = "service.manage";
}

public sealed record ProductPermissionDescriptor(
    string Code,
    string Category,
    bool SupportsServerScope,
    bool IsHighRisk);

public static class ProductPermissionCatalog
{
    private static readonly IReadOnlyDictionary<string, ProductPermissionDescriptor> Descriptors =
        new ReadOnlyDictionary<string, ProductPermissionDescriptor>(
            new Dictionary<string, ProductPermissionDescriptor>(StringComparer.Ordinal)
            {
                [ProductPermissionCodes.ServerRead] = Server(ProductPermissionCodes.ServerRead),
                [ProductPermissionCodes.ServerStart] = Server(ProductPermissionCodes.ServerStart, true),
                [ProductPermissionCodes.ServerStop] = Server(ProductPermissionCodes.ServerStop, true),
                [ProductPermissionCodes.ServerRestart] = Server(ProductPermissionCodes.ServerRestart, true),
                [ProductPermissionCodes.ConsoleRead] = Server(ProductPermissionCodes.ConsoleRead),
                [ProductPermissionCodes.ConsoleWrite] = Server(ProductPermissionCodes.ConsoleWrite, true),
                [ProductPermissionCodes.PlayerRead] = Server(ProductPermissionCodes.PlayerRead),
                [ProductPermissionCodes.PlayerManage] = Server(ProductPermissionCodes.PlayerManage, true),
                [ProductPermissionCodes.FileRead] = Server(ProductPermissionCodes.FileRead),
                [ProductPermissionCodes.FileWrite] = Server(ProductPermissionCodes.FileWrite, true),
                [ProductPermissionCodes.BackupRead] = Server(ProductPermissionCodes.BackupRead),
                [ProductPermissionCodes.BackupCreate] = Server(ProductPermissionCodes.BackupCreate, true),
                [ProductPermissionCodes.BackupRestore] = Server(ProductPermissionCodes.BackupRestore, true),
                [ProductPermissionCodes.BackupDelete] = Server(ProductPermissionCodes.BackupDelete, true),
                [ProductPermissionCodes.ServerSettingsRead] = Server(ProductPermissionCodes.ServerSettingsRead),
                [ProductPermissionCodes.ServerSettingsWrite] = Server(ProductPermissionCodes.ServerSettingsWrite, true),
                [ProductPermissionCodes.ServerUpdate] = Server(ProductPermissionCodes.ServerUpdate, true),
                [ProductPermissionCodes.UserRead] = Global(ProductPermissionCodes.UserRead),
                [ProductPermissionCodes.UserManage] = Global(ProductPermissionCodes.UserManage, true),
                [ProductPermissionCodes.PermissionManage] = Global(ProductPermissionCodes.PermissionManage, true),
                [ProductPermissionCodes.NotificationRead] = Global(ProductPermissionCodes.NotificationRead),
                [ProductPermissionCodes.NotificationManage] = Global(ProductPermissionCodes.NotificationManage, true),
                [ProductPermissionCodes.ProviderRead] = Global(ProductPermissionCodes.ProviderRead),
                [ProductPermissionCodes.ProviderManage] = Global(ProductPermissionCodes.ProviderManage, true),
                [ProductPermissionCodes.ProductUpdate] = Global(ProductPermissionCodes.ProductUpdate, true),
                [ProductPermissionCodes.UpdateManage] = Global(ProductPermissionCodes.UpdateManage, true),
                [ProductPermissionCodes.AuditRead] = Global(ProductPermissionCodes.AuditRead),
                [ProductPermissionCodes.ServiceManage] = Global(ProductPermissionCodes.ServiceManage, true),
            });

    private static readonly IReadOnlyList<ProductPermissionDescriptor> AllDescriptors =
        Descriptors.Values.ToArray();

    public static IReadOnlyCollection<ProductPermissionDescriptor> All => AllDescriptors;

    public static bool TryGet(string? code, out ProductPermissionDescriptor descriptor)
    {
        if (code is null)
        {
            descriptor = null!;
            return false;
        }

        return Descriptors.TryGetValue(code, out descriptor!);
    }

    private static ProductPermissionDescriptor Server(string code, bool highRisk = false)
        => new(code, code[..code.IndexOf('.')], SupportsServerScope: true, highRisk);

    private static ProductPermissionDescriptor Global(string code, bool highRisk = false)
        => new(code, code[..code.IndexOf('.')], SupportsServerScope: false, highRisk);
}

public enum ProductPermissionScopeKind
{
    Global,
    Server,
}

public sealed record ProductPermissionScope(ProductPermissionScopeKind Kind, Guid? ServerId)
{
    public static ProductPermissionScope Global { get; } = new(ProductPermissionScopeKind.Global, null);

    public static ProductPermissionScope ForServer(Guid serverId)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server permission scope requires a non-empty server id.", nameof(serverId));
        }

        return new ProductPermissionScope(ProductPermissionScopeKind.Server, serverId);
    }
}

public sealed record ProductPermissionGrant(string PermissionCode, ProductPermissionScope Scope);

public enum ProductAuthorizationDecision
{
    Granted,
    Denied,
    UnknownPermission,
    MissingServerScope,
    InvalidGrant,
}

public static class ProductAuthorization
{
    public static ProductAuthorizationDecision Evaluate(
        IEnumerable<ProductPermissionGrant> grants,
        string permissionCode,
        Guid? targetServerId = null)
    {
        ArgumentNullException.ThrowIfNull(grants);

        if (!ProductPermissionCatalog.TryGet(permissionCode, out var descriptor))
        {
            return ProductAuthorizationDecision.UnknownPermission;
        }

        if (descriptor.SupportsServerScope &&
            (targetServerId is null || targetServerId == Guid.Empty))
        {
            return ProductAuthorizationDecision.MissingServerScope;
        }

        if (!descriptor.SupportsServerScope && targetServerId is not null)
        {
            return ProductAuthorizationDecision.MissingServerScope;
        }

        var hasMatchingGrant = false;
        foreach (var grant in grants)
        {
            if (!TryValidateGrant(grant))
            {
                return ProductAuthorizationDecision.InvalidGrant;
            }

            if (!string.Equals(grant.PermissionCode, permissionCode, StringComparison.Ordinal))
            {
                continue;
            }

            if (grant.Scope.Kind == ProductPermissionScopeKind.Global)
            {
                hasMatchingGrant = true;
            }

            if (descriptor.SupportsServerScope && grant.Scope.ServerId == targetServerId)
            {
                hasMatchingGrant = true;
            }
        }

        return hasMatchingGrant
            ? ProductAuthorizationDecision.Granted
            : ProductAuthorizationDecision.Denied;
    }

    public static bool TryValidateGrant(ProductPermissionGrant? grant)
    {
        if (grant is null ||
            grant.Scope is null ||
            !ProductPermissionCatalog.TryGet(grant.PermissionCode, out var descriptor))
        {
            return false;
        }

        return grant.Scope.Kind switch
        {
            ProductPermissionScopeKind.Global => grant.Scope.ServerId is null,
            ProductPermissionScopeKind.Server =>
                descriptor.SupportsServerScope && grant.Scope.ServerId is { } id && id != Guid.Empty,
            _ => false,
        };
    }
}
