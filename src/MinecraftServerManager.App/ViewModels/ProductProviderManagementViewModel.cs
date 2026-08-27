using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.App.ViewModels;

internal sealed class ProductProviderManagementViewModel : ObservableObject, IDisposable
{
    private const int MaximumPublisherKeyCharacters = 16 * 1024;
    private const int MaximumDetachedSignatureCharacters = 16 * 1024;
    private readonly IProductProviderManagementClient _client;
    private readonly Func<string, bool> _confirm;
    private readonly CancellationTokenSource _lifetime = new();
    private ProductProviderSummary? _selectedProvider;
    private ProductTrustedProviderPublisherSummary? _selectedPublisher;
    private bool _isBusy;
    private bool _hasError;
    private string _statusMessage = L("provider.status.loading");
    private string _publisherId = string.Empty;
    private string _publisherPublicKeyPem = string.Empty;
    private string _inboxFileName = string.Empty;
    private string _expectedSha256 = string.Empty;
    private string _expectedProviderId = string.Empty;
    private string _expectedVersion = string.Empty;
    private string _expectedPublisherId = string.Empty;
    private string _signatureAlgorithm = "ECDSA-P256-SHA256";
    private string _signatureBase64 = string.Empty;
    private int _signatureFormatVersion = 1;
    private bool _allowDowngrade;
    private int _disposed;

    public ProductProviderManagementViewModel(
        IProductProviderManagementClient client,
        Func<string, bool>? confirm = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _confirm = confirm ?? (_ => true);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        EnableCommand = new AsyncRelayCommand(
            () => SetEnabledAsync(true),
            () => !IsBusy && SelectedProvider is { Enabled: false });
        DisableCommand = new AsyncRelayCommand(
            () => SetEnabledAsync(false),
            () => !IsBusy && SelectedProvider is { Enabled: true });
        HealthCommand = new AsyncRelayCommand(
            CheckHealthAsync,
            () => !IsBusy && SelectedProvider is { Enabled: true });
        UninstallCommand = new AsyncRelayCommand(
            UninstallAsync,
            () => !IsBusy && SelectedProvider is not null);
        PinPublisherCommand = new AsyncRelayCommand(PinPublisherAsync, CanPinPublisher);
        RemovePublisherCommand = new AsyncRelayCommand(
            RemovePublisherAsync,
            () => !IsBusy && SelectedPublisher is not null);
        InstallCommand = new AsyncRelayCommand(InstallAsync, CanInstall);
    }

    public ObservableCollection<ProductProviderSummary> Providers { get; } = [];
    public ObservableCollection<ProductTrustedProviderPublisherSummary> Publishers { get; } = [];

    public ProductProviderSummary? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!SetProperty(ref _selectedProvider, value)) return;
            NotifyCommands();
        }
    }

    public ProductTrustedProviderPublisherSummary? SelectedPublisher
    {
        get => _selectedPublisher;
        set
        {
            if (!SetProperty(ref _selectedPublisher, value)) return;
            NotifyCommands();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            NotifyCommands();
        }
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string PublisherId
    {
        get => _publisherId;
        set
        {
            if (!SetProperty(ref _publisherId, value ?? string.Empty)) return;
            PinPublisherCommand.NotifyCanExecuteChanged();
        }
    }

    public string PublisherPublicKeyPem
    {
        get => _publisherPublicKeyPem;
        set
        {
            if (!SetProperty(ref _publisherPublicKeyPem, value ?? string.Empty)) return;
            PinPublisherCommand.NotifyCanExecuteChanged();
        }
    }

    public string InboxFileName
    {
        get => _inboxFileName;
        set => SetInstallField(ref _inboxFileName, value);
    }

    public string ExpectedSha256
    {
        get => _expectedSha256;
        set => SetInstallField(ref _expectedSha256, value);
    }

    public string ExpectedProviderId
    {
        get => _expectedProviderId;
        set => SetInstallField(ref _expectedProviderId, value);
    }

    public string ExpectedVersion
    {
        get => _expectedVersion;
        set => SetInstallField(ref _expectedVersion, value);
    }

    public string ExpectedPublisherId
    {
        get => _expectedPublisherId;
        set => SetInstallField(ref _expectedPublisherId, value);
    }

    public string SignatureAlgorithm
    {
        get => _signatureAlgorithm;
        set => SetInstallField(ref _signatureAlgorithm, value);
    }

    public string SignatureBase64
    {
        get => _signatureBase64;
        set => SetInstallField(ref _signatureBase64, value);
    }

    public int SignatureFormatVersion
    {
        get => _signatureFormatVersion;
        set
        {
            if (!SetProperty(ref _signatureFormatVersion, value)) return;
            InstallCommand.NotifyCanExecuteChanged();
        }
    }

    public bool AllowDowngrade
    {
        get => _allowDowngrade;
        set => SetProperty(ref _allowDowngrade, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand EnableCommand { get; }
    public AsyncRelayCommand DisableCommand { get; }
    public AsyncRelayCommand HealthCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public AsyncRelayCommand PinPublisherCommand { get; }
    public AsyncRelayCommand RemovePublisherCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }

    public Task InitializeAsync() => RefreshAsync();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        PublisherPublicKeyPem = string.Empty;
        SignatureBase64 = string.Empty;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task RefreshAsync()
    {
        await RunAsync(async cancellationToken =>
        {
            var selectedProviderId = SelectedProvider?.Id;
            var selectedPublisherId = SelectedPublisher?.PublisherId;
            var providers = await _client.ListProvidersAsync(cancellationToken);
            var publishers = await _client.ListTrustedProviderPublishersAsync(cancellationToken);
            ReplaceProviders(providers, selectedProviderId);
            ReplacePublishers(publishers, selectedPublisherId);
            StatusMessage = L("provider.status.refreshed");
        });
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        var provider = SelectedProvider;
        if (provider is null) return;
        await RunAsync(async cancellationToken =>
        {
            var updated = await _client.SetProviderEnabledAsync(
                provider.Id,
                enabled,
                cancellationToken);
            await ReloadProvidersAsync(updated.Id, cancellationToken);
            StatusMessage = enabled
                ? L("provider.status.enabled", updated.DisplayName)
                : L("provider.status.disabled", updated.DisplayName);
        });
    }

    private async Task CheckHealthAsync()
    {
        var provider = SelectedProvider;
        if (provider is null) return;
        await RunAsync(async cancellationToken =>
        {
            var result = await _client.CheckProviderHealthAsync(provider.Id, cancellationToken);
            await ReloadProvidersAsync(provider.Id, cancellationToken);
            StatusMessage = result.Success
                ? L("provider.status.healthSucceeded", provider.DisplayName)
                : L("provider.status.healthFailed", result.ErrorCode ?? "unknown");
        });
    }

    private async Task UninstallAsync()
    {
        var provider = SelectedProvider;
        if (provider is null || !_confirm(
                L("provider.confirm.uninstall", provider.DisplayName, provider.Id)))
        {
            return;
        }

        await RunAsync(async cancellationToken =>
        {
            await _client.UninstallProviderAsync(provider.Id, cancellationToken);
            await ReloadProvidersAsync(null, cancellationToken);
            StatusMessage = L("provider.status.uninstalled", provider.DisplayName);
        });
    }

    private async Task PinPublisherAsync()
    {
        await RunAsync(async cancellationToken =>
        {
            var pinned = await _client.PinProviderPublisherAsync(
                new ProductPinProviderPublisherRequest(
                    PublisherId.Trim(),
                    PublisherPublicKeyPem.Trim()),
                cancellationToken);
            PublisherId = string.Empty;
            PublisherPublicKeyPem = string.Empty;
            await ReloadPublishersAsync(pinned.PublisherId, cancellationToken);
            StatusMessage = L("provider.status.publisherPinned", pinned.PublisherId);
        });
    }

    private async Task RemovePublisherAsync()
    {
        var publisher = SelectedPublisher;
        if (publisher is null || !_confirm(
                L("provider.confirm.removePublisher", publisher.PublisherId)))
        {
            return;
        }

        await RunAsync(async cancellationToken =>
        {
            await _client.RemoveProviderPublisherAsync(publisher.PublisherId, cancellationToken);
            await ReloadPublishersAsync(null, cancellationToken);
            StatusMessage = L("provider.status.publisherRemoved", publisher.PublisherId);
        });
    }

    private async Task InstallAsync()
    {
        await RunAsync(async cancellationToken =>
        {
            var request = new ProductProviderInstallFromInboxRequest(
                InboxFileName.Trim(),
                ExpectedSha256.Trim().ToLowerInvariant(),
                ExpectedProviderId.Trim(),
                ExpectedVersion.Trim(),
                ExpectedPublisherId.Trim(),
                new ProductProviderDetachedSignature(
                    ExpectedPublisherId.Trim(),
                    SignatureAlgorithm.Trim(),
                    SignatureBase64.Trim(),
                    SignatureFormatVersion),
                AllowDowngrade);
            var installed = await _client.InstallProviderFromInboxAsync(request, cancellationToken);
            ClearInstallEditor();
            await ReloadProvidersAsync(installed.Id, cancellationToken);
            StatusMessage = L(
                "provider.status.installed",
                installed.DisplayName,
                installed.Version);
        });
    }

    private bool CanPinPublisher()
        => !IsBusy
           && IsSafeIdentifier(PublisherId)
           && PublisherPublicKeyPem.Length is > 0 and <= MaximumPublisherKeyCharacters
           && PublisherPublicKeyPem.Contains("PUBLIC KEY", StringComparison.Ordinal);

    private bool CanInstall()
        => !IsBusy
           && InboxFileName.Length is > 6 and <= 180
           && InboxFileName.EndsWith(".mcsvp", StringComparison.OrdinalIgnoreCase)
           && string.Equals(Path.GetFileName(InboxFileName), InboxFileName, StringComparison.Ordinal)
           && ExpectedSha256.Length == 64
           && ExpectedSha256.All(Uri.IsHexDigit)
           && IsSafeIdentifier(ExpectedProviderId)
           && IsSafeIdentifier(ExpectedPublisherId)
           && ExpectedVersion.Length is > 0 and <= 96
           && SignatureAlgorithm.Length is > 0 and <= 64
           && SignatureBase64.Length is > 0 and <= MaximumDetachedSignatureCharacters
           && IsCanonicalBase64(SignatureBase64)
           && SignatureFormatVersion is >= 1 and <= 16;

    private static bool IsSafeIdentifier(string value)
        => value.Length is >= 3 and <= 96
           && Regex.IsMatch(value, "^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$", RegexOptions.CultureInvariant);

    private static bool IsCanonicalBase64(string value)
    {
        var trimmed = value.Trim();
        if (!string.Equals(trimmed, value, StringComparison.Ordinal) || trimmed.Length % 4 != 0)
        {
            return false;
        }

        try
        {
            return Convert.ToBase64String(Convert.FromBase64String(trimmed)) == trimmed;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task ReloadProvidersAsync(string? selectedId, CancellationToken cancellationToken)
        => ReplaceProviders(await _client.ListProvidersAsync(cancellationToken), selectedId);

    private async Task ReloadPublishersAsync(string? selectedId, CancellationToken cancellationToken)
        => ReplacePublishers(
            await _client.ListTrustedProviderPublishersAsync(cancellationToken),
            selectedId);

    private void ReplaceProviders(IReadOnlyList<ProductProviderSummary> values, string? selectedId)
    {
        if (values.Count > 128 || values.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new InvalidDataException(L("provider.error.invalidRegistry"));
        }

        Providers.Clear();
        foreach (var value in values
                     .OrderBy(value => value.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(value => value.Id, StringComparer.Ordinal))
        {
            Providers.Add(value);
        }

        SelectedProvider = Providers.FirstOrDefault(value => value.Id == selectedId)
                           ?? Providers.FirstOrDefault();
    }

    private void ReplacePublishers(
        IReadOnlyList<ProductTrustedProviderPublisherSummary> values,
        string? selectedId)
    {
        if (values.Count > 128 || values.Select(value => value.PublisherId)
                .Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new InvalidDataException(L("provider.error.invalidPublishers"));
        }

        Publishers.Clear();
        foreach (var value in values.OrderBy(value => value.PublisherId, StringComparer.Ordinal))
        {
            Publishers.Add(value);
        }

        SelectedPublisher = Publishers.FirstOrDefault(value => value.PublisherId == selectedId)
                            ?? Publishers.FirstOrDefault();
    }

    private void SetInstallField(ref string field, string? value)
    {
        if (!SetProperty(ref field, value ?? string.Empty)) return;
        InstallCommand.NotifyCanExecuteChanged();
    }

    private void ClearInstallEditor()
    {
        InboxFileName = string.Empty;
        ExpectedSha256 = string.Empty;
        ExpectedProviderId = string.Empty;
        ExpectedVersion = string.Empty;
        ExpectedPublisherId = string.Empty;
        SignatureBase64 = string.Empty;
        AllowDowngrade = false;
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsBusy) return;
        IsBusy = true;
        HasError = false;
        try
        {
            await operation(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            HasError = true;
            StatusMessage = error is MinecraftServerManager.Client.ProductServiceClientException service
                ? L("provider.error.rejected", service.Code)
                : error.Message;
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0) IsBusy = false;
        }
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        HealthCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        PinPublisherCommand.NotifyCanExecuteChanged();
        RemovePublisherCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);
}
