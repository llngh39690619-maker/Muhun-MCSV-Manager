using System.ComponentModel;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Dialogs;

public partial class OnlineModpackDialog : Window
{
    private static readonly TimeSpan DefaultCatalogRefreshDebounce = TimeSpan.FromMilliseconds(150);
    private readonly OnlineModpackViewModel _viewModel;
    private readonly Func<OnlineModpackInstallRequest, BackgroundJobSubmissionResult>? _backgroundSubmitter;
    private readonly bool _loadFeaturedOnOpen;
    private readonly TimeSpan _catalogRefreshDebounce;
    private CancellationTokenSource? _scheduledCatalogRefreshCancellation;
    private bool _completed;
    private bool _hasLoaded;

    public OnlineModpackDialog(IOnlineModpackWorkflow workflow)
        : this(new OnlineModpackViewModel(workflow))
    {
    }

    public OnlineModpackDialog(OnlineModpackViewModel viewModel)
        : this(viewModel, loadFeaturedOnOpen: true)
    {
    }

    internal OnlineModpackDialog(
        OnlineModpackViewModel viewModel,
        bool loadFeaturedOnOpen)
        : this(viewModel, loadFeaturedOnOpen, backgroundSubmitter: null)
    {
    }

    internal OnlineModpackDialog(
        OnlineModpackViewModel viewModel,
        bool loadFeaturedOnOpen,
        Func<OnlineModpackInstallRequest, BackgroundJobSubmissionResult>? backgroundSubmitter,
        TimeSpan? catalogRefreshDebounce = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var refreshDebounce = catalogRefreshDebounce ?? DefaultCatalogRefreshDebounce;
        if (refreshDebounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(catalogRefreshDebounce));
        }

        InitializeComponent();
        _viewModel = viewModel;
        _loadFeaturedOnOpen = loadFeaturedOnOpen;
        _backgroundSubmitter = backgroundSubmitter;
        _catalogRefreshDebounce = refreshDebounce;
        DataContext = viewModel;
        viewModel.Installed += OnInstalled;
        viewModel.BrowseCriteriaChanged += OnBrowseCriteriaChanged;
    }

    public ServerInstance? InstalledServer => _viewModel.InstalledServer;

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_hasLoaded)
        {
            return;
        }

        _hasLoaded = true;
        if (!_loadFeaturedOnOpen)
        {
            return;
        }

        if (!CanBrowseWithCurrentCredential())
        {
            return;
        }

        await RunWithTransientApiKeyAsync(_viewModel.LoadFeaturedAsync);
    }

    private async void OnFeaturedClick(object sender, RoutedEventArgs e)
    {
        CancelScheduledCatalogRefresh();
        if (!CanBrowseWithCurrentCredential())
        {
            return;
        }

        await RunWithTransientApiKeyAsync(_viewModel.LoadFeaturedAsync);
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        CancelScheduledCatalogRefresh();
        if (!CanBrowseWithCurrentCredential())
        {
            return;
        }

        await RunWithTransientApiKeyAsync(_viewModel.SearchAsync);
    }

    private async void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_viewModel.CanSearch)
        {
            return;
        }

        e.Handled = true;
        CancelScheduledCatalogRefresh();
        if (!CanBrowseWithCurrentCredential())
        {
            return;
        }

        await RunWithTransientApiKeyAsync(_viewModel.SearchAsync);
    }

    private async void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is not OnlineModpackCatalogCardViewModel selectedCard)
        {
            return;
        }

        var selected = selectedCard.Project;
        await RunWithTransientApiKeyAsync(key => _viewModel.SelectResultAsync(selected, key));
        if (_viewModel.SelectedResult != selected)
        {
            ResultList.SelectedItem = null;
        }
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsCurseForgeSelected)
        {
            // The official CurseForge key never enters a persisted/background job definition.
            // The asynchronous in-dialog install remains cancellable and disposes its read-only
            // SecureString copy as soon as this operation completes.
            await RunWithTransientApiKeyAsync(_viewModel.InstallAsync);
            return;
        }

        if (_backgroundSubmitter is null)
        {
            await RunWithTransientApiKeyAsync(_viewModel.InstallAsync);
            return;
        }

        if (!_viewModel.TryBuildInstallRequest(out var request))
        {
            return;
        }

        BackgroundJobSubmissionResult submission;
        try
        {
            submission = _backgroundSubmitter(request);
        }
        catch (Exception exception)
        {
            _viewModel.SetBackgroundSubmissionError(
                LocalizationService.Current.Get("jobs.error.addModpackDetail", exception.Message));
            return;
        }

        if (!submission.Accepted)
        {
            _viewModel.SetBackgroundSubmissionError(
                submission.Error ?? LocalizationService.Current.Get("jobs.error.addModpack"));
            return;
        }

        _completed = true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            _viewModel.CancelCurrentOperation();
            return;
        }

        _completed = true;
        DialogResult = false;
    }

    private void OnProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedItem is OnlineModpackProviderChoice selected)
        {
            if (selected.Provider != OnlineModpackProvider.CurseForge)
            {
                CurseForgeApiKeyBox.Clear();
            }
        }
    }

    private void OnCurseForgeApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (!_hasLoaded || !_viewModel.IsCurseForgeSelected)
        {
            return;
        }

        if (CurseForgeApiKeyBox.SecurePassword.Length == 0)
        {
            CancelScheduledCatalogRefresh();
            _viewModel.SetTransientCredentialRequired();
            return;
        }

        ScheduleCatalogRefresh();
    }

    private void OnBrowseCriteriaChanged(object? sender, EventArgs e)
        => ScheduleCatalogRefresh();

    private void ScheduleCatalogRefresh()
    {
        if (!_hasLoaded || _completed)
        {
            return;
        }

        if (!CanBrowseWithCurrentCredential())
        {
            CancelScheduledCatalogRefresh();
            return;
        }

        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _scheduledCatalogRefreshCancellation, cancellation)?.Cancel();
        _ = RefreshCatalogAfterDebounceAsync(cancellation);
    }

    private async Task RefreshCatalogAfterDebounceAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_catalogRefreshDebounce, cancellation.Token);
            if (_completed || cancellation.IsCancellationRequested)
            {
                return;
            }

            if (!CanBrowseWithCurrentCredential())
            {
                return;
            }

            await RunWithTransientApiKeyAsync(_viewModel.RefreshCurrentCatalogAsync);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer criteria change superseded this scheduled refresh.
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _scheduledCatalogRefreshCancellation,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    private bool CanBrowseWithCurrentCredential()
    {
        if (!_viewModel.IsCurseForgeSelected || CurseForgeApiKeyBox.SecurePassword.Length > 0)
        {
            return true;
        }

        _viewModel.SetTransientCredentialRequired();
        return false;
    }

    private void CancelScheduledCatalogRefresh()
        => Interlocked.Exchange(ref _scheduledCatalogRefreshCancellation, null)?.Cancel();

    private void OnInstalled(object? sender, EventArgs e)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        DialogResult = true;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.IsInstalling)
        {
            e.Cancel = true;
            _viewModel.CancelCurrentOperation();
            return;
        }

        if (_viewModel.IsBusy)
        {
            _viewModel.CancelCurrentOperation();
        }

        CancelScheduledCatalogRefresh();
        _viewModel.Installed -= OnInstalled;
        _viewModel.BrowseCriteriaChanged -= OnBrowseCriteriaChanged;
        CurseForgeApiKeyBox.Clear();
        _viewModel.Dispose();
        _completed = true;
    }

    private async Task RunWithTransientApiKeyAsync(Func<SecureString?, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var credential = CopyTransientApiKey();
        await operation(credential);
    }

    private SecureString? CopyTransientApiKey()
    {
        if (!_viewModel.IsCurseForgeSelected)
        {
            return null;
        }

        var credential = CurseForgeApiKeyBox.SecurePassword.Copy();
        credential.MakeReadOnly();
        return credential;
    }

}
