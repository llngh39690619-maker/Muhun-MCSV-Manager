using System.Collections.ObjectModel;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.App.ViewModels;

internal sealed record ProductNotificationDeliveryRow(
    Guid DispatchId,
    string Provider,
    string State,
    int AttemptCount,
    string NextAttempt,
    string Result)
{
    internal static ProductNotificationDeliveryRow FromContract(
        ProductNotificationDeliverySummary delivery)
    {
        var localization = LocalizationService.Current;
        var result = delivery.DeliveredAtUtc is { } delivered
            ? localization.Get(
                "notification.result.deliveredAt",
                delivered.ToLocalTime().ToString("g", localization.Culture))
            : string.IsNullOrWhiteSpace(delivery.LastFailureCode)
                ? localization.Get("notification.result.pending")
                : localization.Get("notification.result.failureCode", delivery.LastFailureCode);
        return new ProductNotificationDeliveryRow(
            delivery.DispatchId,
            delivery.ProviderId,
            LocalizeState(delivery.State, localization),
            delivery.AttemptCount,
            delivery.NextAttemptAtUtc.ToLocalTime().ToString("g", localization.Culture),
            result);
    }

    private static string LocalizeState(string state, LocalizationService localization) => state switch
    {
        "Pending" => localization.Get("notification.status.queued"),
        "Delivered" => localization.Get("notification.status.sent"),
        "TerminalFailure" => localization.Get("notification.status.failed"),
        _ => state,
    };
}

/// <summary>
/// Administrator-only editor for the Service-owned notification outbox.  The entered webhook is
/// cleared after every attempt and is never populated from the Service.
/// </summary>
internal sealed class ProductNotificationSettingsViewModel : ObservableObject
{
    private const int HistoryLimit = 100;
    private readonly IProductNotificationManagementClient _client;
    private bool _isBusy;
    private bool _isWebhookConfigured;
    private bool _isWebhookEnabled;
    private bool _serverLifecycle = true;
    private bool _backupOperations = true;
    private bool _modpackUpdates = true;
    private bool _productUpdates = true;
    private bool _providerHealth = true;
    private int _externalThrottleSeconds = 30;
    private string _webhookUrl = string.Empty;
    private string _statusMessage = LocalizationService.Current.Get("notification.status.loading");

    internal ProductNotificationSettingsViewModel(IProductNotificationManagementClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ConfigureWebhookCommand = new AsyncRelayCommand(
            ConfigureWebhookAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(WebhookUrl));
        RemoveWebhookCommand = new AsyncRelayCommand(
            RemoveWebhookAsync,
            () => !IsBusy && IsWebhookConfigured);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        SavePreferencesCommand = new AsyncRelayCommand(SavePreferencesAsync, () => !IsBusy);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    internal event EventHandler? CloseRequested;

    internal ObservableCollection<ProductNotificationDeliveryRow> Deliveries { get; } = [];

    internal bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            NotifyCommands();
        }
    }

    internal bool IsWebhookConfigured
    {
        get => _isWebhookConfigured;
        private set
        {
            if (!SetProperty(ref _isWebhookConfigured, value)) return;
            OnPropertyChanged(nameof(WebhookConfigurationText));
            NotifyCommands();
        }
    }

    internal bool IsWebhookEnabled
    {
        get => _isWebhookEnabled;
        private set
        {
            if (!SetProperty(ref _isWebhookEnabled, value)) return;
            OnPropertyChanged(nameof(WebhookConfigurationText));
        }
    }

    internal string WebhookConfigurationText => IsWebhookConfigured
        ? IsWebhookEnabled
            ? LocalizationService.Current.Get("notification.discord.configured")
            : LocalizationService.Current.Get("notification.discord.disabled")
        : LocalizationService.Current.Get("notification.discord.notConfigured");

    internal bool ServerLifecycle
    {
        get => _serverLifecycle;
        set => SetProperty(ref _serverLifecycle, value);
    }

    internal bool BackupOperations
    {
        get => _backupOperations;
        set => SetProperty(ref _backupOperations, value);
    }

    internal bool ModpackUpdates
    {
        get => _modpackUpdates;
        set => SetProperty(ref _modpackUpdates, value);
    }

    internal bool ProductUpdates
    {
        get => _productUpdates;
        set => SetProperty(ref _productUpdates, value);
    }

    internal bool ProviderHealth
    {
        get => _providerHealth;
        set => SetProperty(ref _providerHealth, value);
    }

    internal int ExternalThrottleSeconds
    {
        get => _externalThrottleSeconds;
        set => SetProperty(
            ref _externalThrottleSeconds,
            Math.Clamp(
                value,
                ProductNotificationPreferences.MinimumThrottleSeconds,
                ProductNotificationPreferences.MaximumThrottleSeconds));
    }

    internal string WebhookUrl
    {
        get => _webhookUrl;
        set
        {
            if (!SetProperty(ref _webhookUrl, value ?? string.Empty)) return;
            ConfigureWebhookCommand.NotifyCanExecuteChanged();
        }
    }

    internal string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    internal AsyncRelayCommand ConfigureWebhookCommand { get; }
    internal AsyncRelayCommand RemoveWebhookCommand { get; }
    internal AsyncRelayCommand RefreshCommand { get; }
    internal AsyncRelayCommand SavePreferencesCommand { get; }
    internal RelayCommand CloseCommand { get; }

    internal async Task InitializeAsync() => await RefreshAsync().ConfigureAwait(true);

    internal void ClearTransientSecret() => WebhookUrl = string.Empty;

    private async Task ConfigureWebhookAsync()
    {
        var transientSecret = WebhookUrl;
        if (string.IsNullOrWhiteSpace(transientSecret)) return;

        await RunAsync(
            async cancellationToken =>
            {
                var configuration = await _client
                    .SetDiscordWebhookAsync(transientSecret, cancellationToken)
                    .ConfigureAwait(true);
                IsWebhookConfigured = configuration.Configured;
                IsWebhookEnabled = configuration.Enabled;
                StatusMessage = LocalizationService.Current.Get("notification.status.configured");
                await LoadHistoryAsync(cancellationToken).ConfigureAwait(true);
            }).ConfigureAwait(true);
        ClearTransientSecret();
    }

    private async Task RemoveWebhookAsync()
    {
        await RunAsync(
            async cancellationToken =>
            {
                var configuration = await _client
                    .DeleteDiscordWebhookAsync(cancellationToken)
                    .ConfigureAwait(true);
                IsWebhookConfigured = configuration.Configured;
                IsWebhookEnabled = configuration.Enabled;
                StatusMessage = LocalizationService.Current.Get("notification.status.removed");
            }).ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        await RunAsync(
            async cancellationToken =>
            {
                var configurationTask = _client.GetDiscordWebhookConfigurationAsync(cancellationToken);
                var preferencesTask = _client.GetNotificationPreferencesAsync(cancellationToken);
                await Task.WhenAll(configurationTask, preferencesTask).ConfigureAwait(true);
                var configuration = await configurationTask.ConfigureAwait(true);
                var preferences = await preferencesTask.ConfigureAwait(true);
                IsWebhookConfigured = configuration.Configured;
                IsWebhookEnabled = configuration.Enabled;
                ApplyPreferences(preferences);
                await LoadHistoryAsync(cancellationToken).ConfigureAwait(true);
                StatusMessage = LocalizationService.Current.Get("notification.status.refreshed");
            }).ConfigureAwait(true);
    }

    private async Task SavePreferencesAsync()
    {
        await RunAsync(
            async cancellationToken =>
            {
                var saved = await _client.SetNotificationPreferencesAsync(
                        new ProductNotificationPreferences(
                            ProductNotificationPreferences.CurrentSchemaVersion,
                            ServerLifecycle,
                            BackupOperations,
                            ModpackUpdates,
                            ProductUpdates,
                            ProviderHealth,
                            ExternalThrottleSeconds)
                        {
                            CultureName = LocalizationService.Current.CultureName,
                        },
                        cancellationToken)
                    .ConfigureAwait(true);
                ApplyPreferences(saved);
                StatusMessage = LocalizationService.Current.Get(
                    "notification.status.preferencesSaved");
            }).ConfigureAwait(true);
    }

    private void ApplyPreferences(ProductNotificationPreferences preferences)
    {
        ProductNotificationPreferencesValidator.ValidateAndThrow(preferences);
        ServerLifecycle = preferences.ServerLifecycle;
        BackupOperations = preferences.BackupOperations;
        ModpackUpdates = preferences.ModpackUpdates;
        ProductUpdates = preferences.ProductUpdates;
        ProviderHealth = preferences.ProviderHealth;
        ExternalThrottleSeconds = preferences.ExternalThrottleSeconds;
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        var history = await _client
            .ListNotificationHistoryAsync(HistoryLimit, cancellationToken)
            .ConfigureAwait(true);
        Deliveries.Clear();
        foreach (var delivery in history)
        {
            Deliveries.Add(ProductNotificationDeliveryRow.FromContract(delivery));
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        IsBusy = true;
        try
        {
            await operation(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            StatusMessage = LocalizationService.Current.Get(
                "notification.status.operationFailed",
                error.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyCommands()
    {
        ConfigureWebhookCommand.NotifyCanExecuteChanged();
        RemoveWebhookCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        SavePreferencesCommand.NotifyCanExecuteChanged();
    }
}
