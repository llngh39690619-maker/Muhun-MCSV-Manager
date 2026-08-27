using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Security;
using MinecraftServerManager.Data;

namespace MinecraftServerManager.Service;

internal sealed record ProductLocalIpcAuditDescriptor(
    string ActionCode,
    string PermissionCode,
    Guid? ServerId);

/// <summary>
/// Fail-closed audit boundary for privileged named-pipe mutations.  The policy intentionally has
/// no fields for commands, PINs, webhook URLs, package bodies, or paths, so secrets cannot enter
/// the durable audit record by accident.
/// </summary>
public sealed class ProductLocalIpcAuditPolicy(
    ProductSecurityAuditStore store,
    TimeProvider timeProvider)
{
    internal async Task<ProductIpcResponse> ExecuteAsync(
        ProductIpcRequest request,
        string? operatingSystemIdentity,
        Func<ProductIpcRequest, CancellationToken, Task<ProductIpcResponse>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);
        var descriptor = Describe(request);
        if (descriptor is null)
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }

        var correlationId = request.RequestId;
        var username = NormalizeIdentity(operatingSystemIdentity);
        if (!TryAppend(descriptor, username, "accepted", "ipc_requested", correlationId))
        {
            return ProductIpcMessageProcessor.Failure(
                request.RequestId,
                new ProductIpcError(
                    "security.audit_unavailable",
                    "The privileged operation was rejected because its security audit could not be persisted."));
        }

        try
        {
            var response = await next(request, cancellationToken).ConfigureAwait(false);
            _ = TryAppend(
                descriptor,
                username,
                response.Success ? "succeeded" : "rejected",
                response.Success ? "ipc_succeeded" : "ipc_rejected",
                correlationId);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = TryAppend(descriptor, username, "failed", "ipc_cancelled", correlationId);
            throw;
        }
        catch
        {
            _ = TryAppend(descriptor, username, "failed", "ipc_failed", correlationId);
            throw;
        }
    }

    internal static ProductLocalIpcAuditDescriptor? Describe(ProductIpcRequest request)
    {
        var permission = request.Method switch
        {
            ProductIpcProtocol.ServerRegisterMethod or
            ProductIpcProtocol.ServerSettingsUpdateMethod or
            ProductIpcProtocol.ServerRemoveMethod or
            ProductIpcProtocol.ServerDeleteMethod => ProductPermissionCodes.ServerSettingsWrite,
            ProductIpcProtocol.ServerDirectoryMethod or
            ProductIpcProtocol.ServerAdministrationMethod => ProductPermissionCodes.FileRead,
            ProductIpcProtocol.ServerStartMethod => ProductPermissionCodes.ServerStart,
            ProductIpcProtocol.ServerStopMethod => ProductPermissionCodes.ServerStop,
            ProductIpcProtocol.ServerRestartMethod => ProductPermissionCodes.ServerRestart,
            ProductIpcProtocol.ServerCommandMethod => ProductPermissionCodes.ConsoleWrite,
            ProductIpcProtocol.ServerImportBeginMethod or
            ProductIpcProtocol.ServerImportCommitMethod or
            ProductIpcProtocol.ServerImportCancelMethod => ProductPermissionCodes.FileWrite,
            ProductIpcProtocol.ServerBackupCreateMethod => ProductPermissionCodes.BackupCreate,
            ProductIpcProtocol.ServerBackupRestoreMethod => ProductPermissionCodes.BackupRestore,
            ProductIpcProtocol.ServerModpackUpdateBeginMethod or
            ProductIpcProtocol.ServerModpackUpdateCommitMethod or
            ProductIpcProtocol.ServerModpackUpdateCancelMethod => ProductPermissionCodes.ServerUpdate,
            ProductIpcProtocol.UpdateCheckMethod or
            ProductIpcProtocol.UpdateDownloadMethod or
            ProductIpcProtocol.UpdateScheduleMethod => ProductPermissionCodes.UpdateManage,
            ProductIpcProtocol.RemoteAccessStartMethod or
            ProductIpcProtocol.RemoteAccessStopMethod or
            ProductIpcProtocol.RemoteAccessReconnectMethod => ProductPermissionCodes.ServiceManage,
            ProductIpcProtocol.RemoteAccountCreateMethod or
            ProductIpcProtocol.RemoteAccountPinUpdateMethod or
            ProductIpcProtocol.RemoteAccountPinRevealMethod or
            ProductIpcProtocol.RemoteAccountDeleteMethod or
            ProductIpcProtocol.RemoteDeviceRevokeMethod => ProductPermissionCodes.UserManage,
            ProductIpcProtocol.RemoteAccountAuthorizationUpdateMethod => ProductPermissionCodes.PermissionManage,
            ProductIpcProtocol.NotificationDiscordSetMethod or
            ProductIpcProtocol.NotificationDiscordDeleteMethod => ProductPermissionCodes.NotificationManage,
            ProductIpcProtocol.NotificationPreferencesSetMethod => ProductPermissionCodes.NotificationManage,
            ProductIpcProtocol.ProviderSetEnabledMethod or
            ProductIpcProtocol.ProviderHealthMethod or
            ProductIpcProtocol.ProviderUninstallMethod or
            ProductIpcProtocol.ProviderInstallMethod or
            ProductIpcProtocol.ProviderPublisherPinMethod or
            ProductIpcProtocol.ProviderPublisherRemoveMethod => ProductPermissionCodes.ProviderManage,
            _ => null,
        };
        if (permission is null)
        {
            return null;
        }

        return new ProductLocalIpcAuditDescriptor(
            $"ipc.{request.Method}",
            permission,
            request.ServerId);
    }

    private bool TryAppend(
        ProductLocalIpcAuditDescriptor descriptor,
        string username,
        string outcome,
        string reason,
        Guid correlationId)
        => store.TryAppend(new ProductSecurityAuditEntry(
            Guid.NewGuid(),
            timeProvider.GetUtcNow(),
            descriptor.ActionCode,
            outcome,
            username,
            descriptor.PermissionCode,
            descriptor.ServerId,
            reason,
            correlationId));

    internal static string NormalizeIdentity(string? identity)
    {
        var value = identity?.Trim();
        if (!string.IsNullOrWhiteSpace(value) &&
            value.Length <= 64 &&
            !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return value;
        }

        if (string.IsNullOrEmpty(value))
        {
            return "local-operator";
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"local-operator-{Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant()}";
    }
}
