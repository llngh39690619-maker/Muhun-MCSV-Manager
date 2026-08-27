using System.Windows;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ProductNotificationSettingsViewModelTests
{
    [Fact]
    public async Task Initialize_ProjectsWriteOnlyConfigurationAndBoundedHistory()
    {
        var delivery = Delivery("discord", "Delivered");
        var client = new StubNotificationClient
        {
            Configuration = new ProductDiscordWebhookConfiguration(true),
            Deliveries = [delivery],
        };
        var viewModel = new ProductNotificationSettingsViewModel(client);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsWebhookConfigured);
        Assert.DoesNotContain("https://", viewModel.WebhookConfigurationText, StringComparison.Ordinal);
        var row = Assert.Single(viewModel.Deliveries);
        Assert.Equal(delivery.DispatchId, row.DispatchId);
        Assert.Equal("discord", row.Provider);
        Assert.Equal(100, client.LastHistoryLimit);
        Assert.True(viewModel.ServerLifecycle);
        Assert.Equal(30, viewModel.ExternalThrottleSeconds);
    }

    [Fact]
    public async Task Configure_ClearsTransientSecretAndNeverReceivesItBack()
    {
        var client = new StubNotificationClient();
        var viewModel = new ProductNotificationSettingsViewModel(client)
        {
            WebhookUrl = "https://discord.com/api/webhooks/123456/token-value",
        };

        viewModel.ConfigureWebhookCommand.Execute(null);
        await client.SetObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.True(viewModel.IsWebhookConfigured);
        Assert.Equal(string.Empty, viewModel.WebhookUrl);
        Assert.Equal("https://discord.com/api/webhooks/123456/token-value", client.ReceivedSecret);
        Assert.DoesNotContain("token-value", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Dialog_LoadsOnDarkSurfaceAndClosesWithoutEchoingWebhook()
    {
        WpfStaTestHost.Run(() =>
        {
            var client = new StubNotificationClient();
            var viewModel = new ProductNotificationSettingsViewModel(client);
            var dialog = new ProductNotificationSettingsDialog(viewModel);
            var rendered = false;
            dialog.ContentRendered += (_, _) =>
            {
                dialog.UpdateLayout();
                rendered = true;
                Assert.Equal(Application.Current.Resources["WindowBrush"], dialog.Background);
                dialog.Close();
            };

            dialog.ShowDialog();

            Assert.True(rendered);
            Assert.False(dialog.IsVisible);
            Assert.Equal(string.Empty, viewModel.WebhookUrl);
        });
    }

    [Fact]
    public async Task SavePreferences_RoundTripsVersionedExternalPolicy()
    {
        var client = new StubNotificationClient();
        var viewModel = new ProductNotificationSettingsViewModel(client);
        LocalizationService.Current.SetCulture("en-US");
        try
        {
            await viewModel.InitializeAsync();
            viewModel.ServerLifecycle = false;
            viewModel.ModpackUpdates = false;
            viewModel.ExternalThrottleSeconds = 75;

            viewModel.SavePreferencesCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsBusy);

            Assert.False(client.Preferences.ServerLifecycle);
            Assert.False(client.Preferences.ModpackUpdates);
            Assert.Equal(75, client.Preferences.ExternalThrottleSeconds);
            Assert.Equal("en-US", client.Preferences.CultureName);
        }
        finally
        {
            LocalizationService.Current.SetCulture("zh-TW");
        }
    }

    private static ProductNotificationDeliverySummary Delivery(string provider, string state)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            provider,
            state,
            1,
            DateTimeOffset.UtcNow,
            null,
            DateTimeOffset.UtcNow);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Notification operation did not settle.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class StubNotificationClient : IProductNotificationManagementClient
    {
        public ProductDiscordWebhookConfiguration Configuration { get; set; } = new(false);
        public IReadOnlyList<ProductNotificationDeliverySummary> Deliveries { get; set; } = [];
        public ProductNotificationPreferences Preferences { get; set; } =
            ProductNotificationPreferences.Default;
        public int LastHistoryLimit { get; private set; }
        public string? ReceivedSecret { get; private set; }
        public TaskCompletionSource SetObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProductDiscordWebhookConfiguration> GetDiscordWebhookConfigurationAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(Configuration);

        public Task<ProductDiscordWebhookConfiguration> SetDiscordWebhookAsync(
            string webhookUrl,
            CancellationToken cancellationToken = default)
        {
            ReceivedSecret = webhookUrl;
            Configuration = new ProductDiscordWebhookConfiguration(true);
            SetObserved.TrySetResult();
            return Task.FromResult(Configuration);
        }

        public Task<ProductDiscordWebhookConfiguration> DeleteDiscordWebhookAsync(
            CancellationToken cancellationToken = default)
        {
            Configuration = new ProductDiscordWebhookConfiguration(false);
            return Task.FromResult(Configuration);
        }

        public Task<IReadOnlyList<ProductNotificationDeliverySummary>> ListNotificationHistoryAsync(
            int maximumCount = 100,
            CancellationToken cancellationToken = default)
        {
            LastHistoryLimit = maximumCount;
            return Task.FromResult(Deliveries);
        }

        public Task<ProductNotificationPreferences> GetNotificationPreferencesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(Preferences);

        public Task<ProductNotificationPreferences> SetNotificationPreferencesAsync(
            ProductNotificationPreferences preferences,
            CancellationToken cancellationToken = default)
        {
            Preferences = preferences;
            return Task.FromResult(preferences);
        }
    }
}
