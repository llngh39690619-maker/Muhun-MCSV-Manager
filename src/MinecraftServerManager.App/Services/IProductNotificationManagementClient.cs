using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Narrow desktop projection for Service-owned notification administration.  Discord webhook
/// secrets are deliberately write-only: the desktop can query only whether one is configured.
/// </summary>
internal interface IProductNotificationManagementClient
{
    Task<ProductDiscordWebhookConfiguration> GetDiscordWebhookConfigurationAsync(
        CancellationToken cancellationToken = default);

    Task<ProductDiscordWebhookConfiguration> SetDiscordWebhookAsync(
        string webhookUrl,
        CancellationToken cancellationToken = default);

    Task<ProductDiscordWebhookConfiguration> DeleteDiscordWebhookAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductNotificationDeliverySummary>> ListNotificationHistoryAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default);

    Task<ProductNotificationPreferences> GetNotificationPreferencesAsync(
        CancellationToken cancellationToken = default);

    Task<ProductNotificationPreferences> SetNotificationPreferencesAsync(
        ProductNotificationPreferences preferences,
        CancellationToken cancellationToken = default);
}
