using System.Windows;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.App.Tests;

public sealed class ProductProviderManagementViewModelTests
{
    [Fact]
    public async Task InitializeAndLifecycleActions_UseOnlyServiceManagementClient()
    {
        var client = new StubProviderClient();
        using var viewModel = new ProductProviderManagementViewModel(client);

        await viewModel.InitializeAsync();

        Assert.Equal("builtin.catalog", Assert.Single(viewModel.Providers).Id);
        Assert.Equal("muhun.builtin", Assert.Single(viewModel.Publishers).PublisherId);
        viewModel.DisableCommand.Execute(null);
        await client.EnabledObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);
        Assert.False(Assert.Single(viewModel.Providers).Enabled);
        Assert.Equal("builtin.catalog", client.LastEnabledProviderId);
    }

    [Fact]
    public async Task PublisherAndInstallEditors_ClearLargeTransientInputsAfterSuccess()
    {
        var client = new StubProviderClient();
        using var viewModel = new ProductProviderManagementViewModel(client);
        await viewModel.InitializeAsync();
        viewModel.PublisherId = "partner.publisher";
        viewModel.PublisherPublicKeyPem =
            "-----BEGIN PUBLIC KEY-----\nAA==\n-----END PUBLIC KEY-----";
        Assert.True(viewModel.PinPublisherCommand.CanExecute(null));
        viewModel.PinPublisherCommand.Execute(null);
        await client.PinObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);
        Assert.Empty(viewModel.PublisherPublicKeyPem);

        viewModel.InboxFileName = "partner.mcsvp";
        viewModel.ExpectedSha256 = new string('a', 64);
        viewModel.ExpectedProviderId = "partner.catalog";
        viewModel.ExpectedVersion = "1.2.3";
        viewModel.ExpectedPublisherId = "partner.publisher";
        viewModel.SignatureAlgorithm = "ECDSA-P256-SHA256";
        viewModel.SignatureBase64 = Convert.ToBase64String([1, 2, 3]);
        Assert.True(viewModel.InstallCommand.CanExecute(null));
        viewModel.InstallCommand.Execute(null);
        var request = await client.InstallObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.Equal("partner.mcsvp", request.InboxFileName);
        Assert.DoesNotContain('/', request.InboxFileName);
        Assert.Empty(viewModel.SignatureBase64);
        Assert.Empty(viewModel.InboxFileName);
    }

    [Theory]
    [InlineData("C:\\outside.mcsvp")]
    [InlineData("../outside.mcsvp")]
    [InlineData("folder/outside.mcsvp")]
    [InlineData("outside.zip")]
    public void InstallEditor_RejectsArbitraryOrNonInboxPaths(string value)
    {
        using var viewModel = new ProductProviderManagementViewModel(new StubProviderClient());
        viewModel.InboxFileName = value;
        viewModel.ExpectedSha256 = new string('a', 64);
        viewModel.ExpectedProviderId = "partner.catalog";
        viewModel.ExpectedVersion = "1.2.3";
        viewModel.ExpectedPublisherId = "partner.publisher";
        viewModel.SignatureBase64 = Convert.ToBase64String([1, 2, 3]);

        Assert.False(viewModel.InstallCommand.CanExecute(null));
    }

    [Fact]
    public void FormalDialog_LoadsOnDarkNativeSurface()
    {
        WpfStaTestHost.Run(() =>
        {
            using var viewModel = new ProductProviderManagementViewModel(new StubProviderClient());
            var dialog = new ProductProviderManagementDialog(viewModel);
            var rendered = false;
            dialog.ContentRendered += (_, _) =>
            {
                rendered = true;
                dialog.UpdateLayout();
                Assert.Equal(Application.Current.Resources["WindowBrush"], dialog.Background);
                dialog.Close();
            };

            dialog.ShowDialog();

            Assert.True(rendered);
        });
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private sealed class StubProviderClient : IProductProviderManagementClient
    {
        private readonly List<ProductProviderSummary> _providers = [Provider(enabled: true)];
        private readonly List<ProductTrustedProviderPublisherSummary> _publishers =
        [
            new("muhun.builtin", new string('a', 64), DateTimeOffset.UtcNow),
        ];

        public string? LastEnabledProviderId { get; private set; }
        public TaskCompletionSource EnabledObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PinObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProductProviderInstallFromInboxRequest> InstallObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ProductProviderSummary>> ListProvidersAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProductProviderSummary>>(_providers.ToArray());

        public Task<IReadOnlyList<ProductTrustedProviderPublisherSummary>> ListTrustedProviderPublishersAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProductTrustedProviderPublisherSummary>>(_publishers.ToArray());

        public Task<ProductProviderSummary> SetProviderEnabledAsync(
            string providerId,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            LastEnabledProviderId = providerId;
            var updated = Provider(enabled);
            _providers[0] = updated;
            EnabledObserved.TrySetResult();
            return Task.FromResult(updated);
        }

        public Task<ProductProviderHealthCheckResult> CheckProviderHealthAsync(
            string providerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ProductProviderHealthCheckResult(providerId, true, null));

        public Task UninstallProviderAsync(string providerId, CancellationToken cancellationToken = default)
        {
            _providers.Clear();
            return Task.CompletedTask;
        }

        public Task<ProductTrustedProviderPublisherSummary> PinProviderPublisherAsync(
            ProductPinProviderPublisherRequest request,
            CancellationToken cancellationToken = default)
        {
            var summary = new ProductTrustedProviderPublisherSummary(
                request.PublisherId,
                new string('b', 64),
                DateTimeOffset.UtcNow);
            _publishers.Add(summary);
            PinObserved.TrySetResult();
            return Task.FromResult(summary);
        }

        public Task RemoveProviderPublisherAsync(
            string publisherId,
            CancellationToken cancellationToken = default)
        {
            _publishers.RemoveAll(value => value.PublisherId == publisherId);
            return Task.CompletedTask;
        }

        public Task<ProductProviderSummary> InstallProviderFromInboxAsync(
            ProductProviderInstallFromInboxRequest request,
            CancellationToken cancellationToken = default)
        {
            var installed = new ProductProviderSummary(
                request.ExpectedProviderId,
                "Partner Catalog",
                request.ExpectedVersion,
                request.ExpectedPublisherId,
                true,
                ProductProviderHealthState.Stopped,
                [],
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                0,
                null);
            _providers.Add(installed);
            InstallObserved.TrySetResult(request);
            return Task.FromResult(installed);
        }

        private static ProductProviderSummary Provider(bool enabled)
            => new(
                "builtin.catalog",
                "Muhun Catalog Provider",
                "1.0.0",
                "muhun.builtin",
                enabled,
                enabled ? ProductProviderHealthState.Healthy : ProductProviderHealthState.Disabled,
                ["modpack.catalog"],
                ["network.official"],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                0,
                null);
    }
}
