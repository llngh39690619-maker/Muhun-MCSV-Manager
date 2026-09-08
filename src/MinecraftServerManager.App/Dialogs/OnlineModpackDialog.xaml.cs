using System.ComponentModel;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Dialogs;

public partial class OnlineModpackDialog : Window
{
    private static readonly TimeSpan DefaultCatalogRefreshDebounce = TimeSpan.FromMilliseconds(150);
    private readonly OnlineModpackViewModel _viewModel;
    private readonly Func<OnlineModpackInstallRequest, BackgroundJobSubmissionResult>? _backgroundSubmitter;
    private readonly ICurseForgeCredentialStore? _curseForgeCredentialStore;
    private readonly bool _loadFeaturedOnOpen;
    private readonly TimeSpan _catalogRefreshDebounce;
    private CancellationTokenSource? _scheduledCatalogRefreshCancellation;
    private bool _suppressCurseForgePasswordChanged;
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
        TimeSpan? catalogRefreshDebounce = null,
        ICurseForgeCredentialStore? curseForgeCredentialStore = null)
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
        _curseForgeCredentialStore = curseForgeCredentialStore;
        _catalogRefreshDebounce = refreshDebounce;
        DataContext = viewModel;
        viewModel.Installed += OnInstalled;
        viewModel.BrowseCriteriaChanged += OnBrowseCriteriaChanged;
        UpdateCurseForgeCredentialStatus();
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

    private void OnMinecraftEulaLinkRequestNavigate(
        object sender,
        RequestNavigateEventArgs e)
    {
        _ = MinecraftEulaLinkOpener.TryOpen(this);
        e.Handled = true;
    }

    private void OnProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedItem is OnlineModpackProviderChoice selected)
        {
            if (selected.Provider != OnlineModpackProvider.CurseForge)
            {
                ClearCurseForgeApiKeyBox();
            }

            UpdateCurseForgeCredentialStatus();
        }
    }

    private void OnCurseForgeApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressCurseForgePasswordChanged ||
            !_hasLoaded ||
            !_viewModel.IsCurseForgeSelected)
        {
            return;
        }

        UpdateCurseForgeCredentialStatus();
        if (!HasUsableCurseForgeCredential())
        {
            CancelScheduledCatalogRefresh();
            _viewModel.SetTransientCredentialRequired();
            return;
        }

        ScheduleCatalogRefresh();
    }

    private void OnSaveCurseForgeCredentialClick(object sender, RoutedEventArgs e)
    {
        if (_curseForgeCredentialStore is null)
        {
            SetCurseForgeCredentialStatus("online.curseForgeCredential.unavailable");
            return;
        }

        using var credential = CopyEnteredCurseForgeApiKey();
        if (credential is null)
        {
            SetCurseForgeCredentialStatus("online.curseForgeCredential.enterToSave");
            if (!HasUsableCurseForgeCredential())
            {
                _viewModel.SetTransientCredentialRequired();
            }

            CurseForgeApiKeyBox.Focus();
            return;
        }

        try
        {
            _curseForgeCredentialStore.Save(credential);
            // Never refill the PasswordBox from persistent storage. Once saved, future operations
            // obtain a fresh read-only copy directly from the CurrentUser DPAPI store.
            ClearCurseForgeApiKeyBox();
            SetCurseForgeCredentialStatus("online.curseForgeCredential.saved");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetCurseForgeCredentialStatus("online.curseForgeCredential.saveFailed");
        }
    }

    private void OnDeleteCurseForgeCredentialClick(object sender, RoutedEventArgs e)
    {
        if (_curseForgeCredentialStore is null)
        {
            SetCurseForgeCredentialStatus("online.curseForgeCredential.unavailable");
            return;
        }

        try
        {
            var deleted = _curseForgeCredentialStore.Delete();
            if (HasEnteredCurseForgeApiKey())
            {
                SetCurseForgeCredentialStatus(
                    "online.curseForgeCredential.typedNotSaved");
            }
            else
            {
                SetCurseForgeCredentialStatus(
                    deleted
                        ? "online.curseForgeCredential.deleted"
                        : "online.curseForgeCredential.notSaved");
                CancelScheduledCatalogRefresh();
                _viewModel.SetTransientCredentialRequired();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetCurseForgeCredentialStatus("online.curseForgeCredential.deleteFailed");
        }
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
        if (!_viewModel.IsCurseForgeSelected || HasUsableCurseForgeCredential())
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
        ClearCurseForgeApiKeyBox();
        _viewModel.Dispose();
        _completed = true;
    }

    private async Task RunWithTransientApiKeyAsync(Func<SecureString?, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var credential = CopyOperationCurseForgeApiKey();
        if (_viewModel.IsCurseForgeSelected && credential is null)
        {
            _viewModel.SetTransientCredentialRequired();
            return;
        }

        await operation(credential);
        if (_viewModel.IsCurseForgeSelected &&
            _viewModel.WasLastCurseForgeKeyRejected)
        {
            SetCurseForgeCredentialStatus("online.curseForgeCredential.rejected");
        }
    }

    private SecureString? CopyOperationCurseForgeApiKey()
    {
        if (!_viewModel.IsCurseForgeSelected)
        {
            return null;
        }

        var entered = CopyEnteredCurseForgeApiKey();
        if (entered is not null)
        {
            return entered;
        }

        try
        {
            var stored = _curseForgeCredentialStore?.AcquireReadOnly();
            if (stored is not null && !stored.IsReadOnly())
            {
                stored.MakeReadOnly();
            }

            return stored;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetCurseForgeCredentialStatus("online.curseForgeCredential.readFailed");
            return null;
        }
    }

    private SecureString? CopyEnteredCurseForgeApiKey()
    {
        using var source = CurseForgeApiKeyBox.SecurePassword;
        if (source.Length == 0)
        {
            return null;
        }

        var credential = source.Copy();
        credential.MakeReadOnly();
        return credential;
    }

    private bool HasEnteredCurseForgeApiKey()
    {
        using var source = CurseForgeApiKeyBox.SecurePassword;
        return source.Length > 0;
    }

    private void ClearCurseForgeApiKeyBox()
    {
        _suppressCurseForgePasswordChanged = true;
        try
        {
            CurseForgeApiKeyBox.Clear();
        }
        finally
        {
            _suppressCurseForgePasswordChanged = false;
        }
    }

    private bool HasUsableCurseForgeCredential()
    {
        if (HasEnteredCurseForgeApiKey())
        {
            return true;
        }

        try
        {
            return _curseForgeCredentialStore?.HasCredential == true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetCurseForgeCredentialStatus("online.curseForgeCredential.readFailed");
            return false;
        }
    }

    private void UpdateCurseForgeCredentialStatus()
    {
        if (HasEnteredCurseForgeApiKey())
        {
            SetCurseForgeCredentialStatus("online.curseForgeCredential.typedNotSaved");
            return;
        }

        try
        {
            SetCurseForgeCredentialStatus(
                _curseForgeCredentialStore?.HasCredential == true
                    ? "online.curseForgeCredential.saved"
                    : "online.curseForgeCredential.notSaved");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetCurseForgeCredentialStatus("online.curseForgeCredential.readFailed");
        }
    }

    private void SetCurseForgeCredentialStatus(string localizationKey)
        => CurseForgeCredentialStatusText.Text = LocalizationService.Current.Get(localizationKey);

}
