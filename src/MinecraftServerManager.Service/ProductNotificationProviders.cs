using MinecraftServerManager.Contracts.Notifications;
using MinecraftServerManager.Contracts.Localization;
using MinecraftServerManager.Notifications;

namespace MinecraftServerManager.Service;

/// <summary>
/// Makes every product event a durable local-history delivery even when no external provider is
/// configured. The immutable event itself already resides in SQLite before this provider runs.
/// </summary>
public sealed class ProductLocalHistoryNotificationProvider : INotificationDeliveryProvider
{
    public const string Id = "local.history";

    public string ProviderId => Id;

    public Task<NotificationProviderDeliveryResult> DeliverAsync(
        ProductEventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NotificationProviderDeliveryResult.Delivered);
    }
}

public sealed class ProductNotificationMessageRenderer(ProductNotificationPreferenceStore preferences)
    : INotificationMessageRenderer
{
    public async ValueTask<string> RenderAsync(
        ProductEventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(envelope);
        var policy = await preferences.GetAsync(cancellationToken).ConfigureAwait(false);
        var cultureName = policy.CultureName;
        envelope.Data.TryGetValue("server_name", out var serverName);
        serverName = string.IsNullOrWhiteSpace(serverName)
            ? ProductLocalizationCatalog.Format(
                cultureName,
                "notification.message.defaultServerName")
            : serverName;
        return envelope.Type switch
        {
            "server.started" => Format("serverStarted", serverName),
            "server.stopped" => Format("serverStopped", serverName),
            "server.crashed" => Format("serverCrashed", serverName),
            "backup.completed" => Format("backupCompleted", serverName),
            "backup.restored" => Format("backupRestored", serverName),
            "backup.failed" => Format("backupFailed", serverName),
            "modpack.update.completed" => Format("modpackUpdateCompleted", serverName),
            "modpack.update.rolled-back" => Format("modpackUpdateRolledBack", serverName),
            "modpack.update.failed" => Format("modpackUpdateFailed", serverName),
            "product.update.available" => Format("productUpdateAvailable"),
            "product.update.completed" => Format("productUpdateCompleted"),
            "product.update.rolled-back" => Format("productUpdateRolledBack"),
            "product.update.failed" => Format("productUpdateFailed"),
            "provider.disabled" => Format("providerDisabled"),
            _ => Format("unknown", envelope.Type),
        };

        string Format(string suffix, params object?[] arguments)
            => ProductLocalizationCatalog.Format(
                cultureName,
                $"notification.message.{suffix}",
                arguments);
    }
}
