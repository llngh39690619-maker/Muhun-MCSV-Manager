using System.Diagnostics;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.ViewModels;

/// <summary>
/// Presents the current Windows user's CurseForge credential without ever exposing the secret.
/// Import and deletion are immediate security operations, independent of the transactional
/// general-settings Apply flow.
/// </summary>
public sealed class CurseForgeCredentialSettingsViewModel : ObservableObject
{
    private readonly ICurseForgeCredentialStore _credentialStore;
    private readonly ICurseForgeCredentialFileImportService _credentialFileImportService;
    private readonly Func<string, bool> _openFile;
    private readonly Action<bool>? _credentialStateChanged;
    private bool _hasCredential;
    private bool _isBusy;
    private string _statusLocalizationKey;

    internal CurseForgeCredentialSettingsViewModel(
        ICurseForgeCredentialStore credentialStore,
        ICurseForgeCredentialFileImportService credentialFileImportService,
        Action<bool>? credentialStateChanged = null,
        Func<string, bool>? openFile = null)
    {
        _credentialStore = credentialStore
            ?? throw new ArgumentNullException(nameof(credentialStore));
        _credentialFileImportService = credentialFileImportService
            ?? throw new ArgumentNullException(nameof(credentialFileImportService));
        _credentialStateChanged = credentialStateChanged;
        _openFile = openFile ?? OpenFileWithWindowsShell;

        if (TryReadCredentialState(out var hasCredential))
        {
            _hasCredential = hasCredential;
            _statusLocalizationKey = hasCredential
                ? "online.curseForgeCredential.saved"
                : "online.curseForgeCredential.notSaved";
        }
        else
        {
            _statusLocalizationKey = "online.curseForgeCredential.readFailed";
        }

        OpenCredentialFileCommand = new RelayCommand(
            OpenCredentialFile,
            () => !IsBusy);
        DeleteCredentialCommand = new RelayCommand(
            DeleteCredential,
            () => !IsBusy && HasCredential);
    }

    public bool HasCredential
    {
        get => _hasCredential;
        private set
        {
            if (!SetProperty(ref _hasCredential, value))
            {
                return;
            }

            DeleteCredentialCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OpenCredentialFileCommand.NotifyCanExecuteChanged();
            DeleteCredentialCommand.NotifyCanExecuteChanged();
        }
    }

    public string CredentialStatusText =>
        LocalizationService.Current.Get(_statusLocalizationKey);

    public RelayCommand OpenCredentialFileCommand { get; }

    public RelayCommand DeleteCredentialCommand { get; }

    /// <summary>
    /// Rechecks the one-time import whenever the settings window regains activation. An untouched
    /// template remains on disk; a valid key is scrubbed, removed, and committed to DPAPI by the
    /// import service before this view model reports success.
    /// </summary>
    internal void ImportAndRefresh()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var previousState = HasCredential;
            CurseForgeCredentialImportResult result;
            try
            {
                result = _credentialFileImportService.ImportIfPresent();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                SetStatus("online.curseForgeCredential.importFailed");
                return;
            }

            if (!TryReadCredentialState(out var hasCredential))
            {
                HasCredential = false;
                SetStatus("online.curseForgeCredential.readFailed");
                if (previousState)
                {
                    NotifyCredentialStateChanged();
                }

                return;
            }

            HasCredential = hasCredential;
            SetStatus(result switch
            {
                CurseForgeCredentialImportResult.Imported when hasCredential =>
                    "online.curseForgeCredential.imported",
                CurseForgeCredentialImportResult.Imported =>
                    "online.curseForgeCredential.importFailed",
                CurseForgeCredentialImportResult.TemplateEmpty =>
                    "online.curseForgeCredential.fileEmpty",
                CurseForgeCredentialImportResult.Invalid =>
                    "online.curseForgeCredential.fileInvalid",
                CurseForgeCredentialImportResult.Failed =>
                    "online.curseForgeCredential.importFailed",
                _ when hasCredential =>
                    "online.curseForgeCredential.saved",
                _ =>
                    "online.curseForgeCredential.notSaved",
            });

            if (previousState != hasCredential ||
                result == CurseForgeCredentialImportResult.Imported)
            {
                NotifyCredentialStateChanged();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal void RefreshLocalization()
        => OnPropertyChanged(nameof(CredentialStatusText));

    private void OpenCredentialFile()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var path = _credentialFileImportService.PrepareEditableFile();
            if (!_openFile(path))
            {
                throw new InvalidOperationException(
                    "The Windows shell did not open the CurseForge API key import file.");
            }

            SetStatus("online.curseForgeCredential.editingFile");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SetStatus("online.curseForgeCredential.openFileFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void DeleteCredential()
    {
        if (IsBusy || !HasCredential)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _credentialStore.Delete();
            HasCredential = false;
            SetStatus("online.curseForgeCredential.deleted");
            NotifyCredentialStateChanged();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SetStatus("online.curseForgeCredential.deleteFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryReadCredentialState(out bool hasCredential)
    {
        try
        {
            hasCredential = _credentialStore.HasCredential;
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            hasCredential = false;
            return false;
        }
    }

    private void NotifyCredentialStateChanged()
    {
        try
        {
            _credentialStateChanged?.Invoke(HasCredential);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The credential operation is already committed. A presentation callback must not
            // turn that successful security operation into a false failure or expose its details.
        }
    }

    private void SetStatus(string localizationKey)
    {
        _statusLocalizationKey = localizationKey;
        OnPropertyChanged(nameof(CredentialStatusText));
    }

    private static bool OpenFileWithWindowsShell(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
        });
        return process is not null;
    }
}
