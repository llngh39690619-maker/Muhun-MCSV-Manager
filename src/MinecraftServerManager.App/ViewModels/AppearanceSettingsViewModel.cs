using System.Windows;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.ViewModels;

public sealed record AppearancePatternOption(AppearancePattern Value, string DisplayName);

/// <summary>
/// Transactional editor for application-wide appearance: preview changes are reversible, save is
/// delegated to the owner, and reset does not persist until the user explicitly saves.
/// </summary>
public sealed class AppearanceSettingsViewModel : ObservableObject
{
    private readonly AppearanceThemeService _themeService;
    private readonly ResourceDictionary _resources;
    private readonly Func<ApplicationAppearanceSettings, Task> _persistAsync;
    private ApplicationAppearanceSettings _originalSettings;
    private string? _uncommittedImportedBackground;
    private string _windowColor = ApplicationAppearanceSettings.DefaultWindowColor;
    private string _panelColor = ApplicationAppearanceSettings.DefaultPanelColor;
    private string _panelRaisedColor = ApplicationAppearanceSettings.DefaultPanelRaisedColor;
    private string _borderColor = ApplicationAppearanceSettings.DefaultBorderColor;
    private string _accentColor = ApplicationAppearanceSettings.DefaultAccentColor;
    private string _accentDarkColor = ApplicationAppearanceSettings.DefaultAccentDarkColor;
    private string _textColor = ApplicationAppearanceSettings.DefaultTextColor;
    private string _mutedTextColor = ApplicationAppearanceSettings.DefaultMutedTextColor;
    private AppearancePattern _pattern;
    private string _patternColor = ApplicationAppearanceSettings.DefaultPatternColor;
    private double _patternOpacity = ApplicationAppearanceSettings.DefaultPatternOpacity;
    private string? _backgroundImagePath;
    private double _backgroundImageOpacity = ApplicationAppearanceSettings.DefaultBackgroundImageOpacity;
    private bool _isValid = true;
    private bool _isBusy;
    private bool _previewApplied;
    private string _validationMessage = string.Empty;
    private string _statusMessage = LocalizationService.Current.Get("appearance.status.initial");

    public AppearanceSettingsViewModel(
        AppearanceThemeService themeService,
        ResourceDictionary resources,
        ApplicationAppearanceSettings? currentSettings,
        Func<ApplicationAppearanceSettings, Task> persistAsync)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _persistAsync = persistAsync ?? throw new ArgumentNullException(nameof(persistAsync));
        _originalSettings = _themeService.Repair(currentSettings);

        PreviewCommand = new RelayCommand(() => { Preview(); }, () => IsValid && !IsBusy);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsValid && !IsBusy);
        ResetCommand = new RelayCommand(ResetToDefaults, () => !IsBusy);
        ClearBackgroundImageCommand = new RelayCommand(ClearBackgroundImage, () => HasBackgroundImage && !IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => !IsBusy);

        LoadWorkingSettings(_originalSettings);
    }

    public event EventHandler? Saved;
    public event EventHandler? Cancelled;

    public IReadOnlyList<AppearancePatternOption> PatternOptions => CreatePatternOptions();

    public RelayCommand PreviewCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand ClearBackgroundImageCommand { get; }
    public RelayCommand CancelCommand { get; }

    public string WindowColor
    {
        get => _windowColor;
        set => SetAppearanceProperty(ref _windowColor, value);
    }

    public string PanelColor
    {
        get => _panelColor;
        set => SetAppearanceProperty(ref _panelColor, value);
    }

    public string PanelRaisedColor
    {
        get => _panelRaisedColor;
        set => SetAppearanceProperty(ref _panelRaisedColor, value);
    }

    public string BorderColor
    {
        get => _borderColor;
        set => SetAppearanceProperty(ref _borderColor, value);
    }

    public string AccentColor
    {
        get => _accentColor;
        set => SetAppearanceProperty(ref _accentColor, value);
    }

    public string AccentDarkColor
    {
        get => _accentDarkColor;
        set => SetAppearanceProperty(ref _accentDarkColor, value);
    }

    public string TextColor
    {
        get => _textColor;
        set => SetAppearanceProperty(ref _textColor, value);
    }

    public string MutedTextColor
    {
        get => _mutedTextColor;
        set => SetAppearanceProperty(ref _mutedTextColor, value);
    }

    public AppearancePattern Pattern
    {
        get => _pattern;
        set => SetAppearanceProperty(ref _pattern, value);
    }

    public string PatternColor
    {
        get => _patternColor;
        set => SetAppearanceProperty(ref _patternColor, value);
    }

    public double PatternOpacity
    {
        get => _patternOpacity;
        set => SetAppearanceProperty(ref _patternOpacity, value);
    }

    public string? BackgroundImagePath
    {
        get => _backgroundImagePath;
        private set
        {
            if (!SetProperty(ref _backgroundImagePath, value)) return;
            OnPropertyChanged(nameof(HasBackgroundImage));
            OnPropertyChanged(nameof(BackgroundImageFileName));
            ClearBackgroundImageCommand.NotifyCanExecuteChanged();
            AppearancePropertyChanged();
        }
    }

    public double BackgroundImageOpacity
    {
        get => _backgroundImageOpacity;
        set => SetAppearanceProperty(ref _backgroundImageOpacity, value);
    }

    public bool HasBackgroundImage => !string.IsNullOrWhiteSpace(BackgroundImagePath);
    public string BackgroundImageFileName => HasBackgroundImage
        ? Path.GetFileName(BackgroundImagePath!)
        : LocalizationService.Current.Get("appearance.status.noImage");

    public bool IsValid
    {
        get => _isValid;
        private set => SetProperty(ref _isValid, value);
    }

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationMessage);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RefreshCommands();
        }
    }

    public bool IsDirty => !SettingsEqual(CreateCandidate(), _originalSettings);
    public bool IsPreviewApplied => _previewApplied;

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (!SetProperty(ref _validationMessage, value)) return;
            OnPropertyChanged(nameof(HasValidationError));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ApplicationAppearanceSettings CurrentSettings => CreateCandidate();

    public bool TryImportBackgroundImage(string sourcePath)
    {
        if (IsBusy) return false;

        try
        {
            var imported = _themeService.ImportBackgroundImage(sourcePath);
            DeletePreviousUncommittedBackground();
            _uncommittedImportedBackground = imported;
            BackgroundImagePath = imported;
            StatusMessage = LocalizationService.Current.Get(
                "appearance.status.loaded",
                Path.GetFileName(sourcePath));
            return true;
        }
        catch (InvalidDataException exception)
        {
            ValidationMessage = exception.Message;
            StatusMessage = LocalizationService.Current.Get("appearance.status.unchanged");
            return false;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            ValidationMessage = exception.Message;
            StatusMessage = LocalizationService.Current.Get("appearance.status.unchanged");
            return false;
        }
    }

    public bool Preview()
    {
        if (IsBusy) return false;

        try
        {
            var normalized = _themeService.Apply(_resources, CreateCandidate());
            LoadWorkingSettings(normalized);
            _previewApplied = true;
            OnPropertyChanged(nameof(IsPreviewApplied));
            StatusMessage = LocalizationService.Current.Get("appearance.status.previewed");
            return true;
        }
        catch (InvalidDataException exception)
        {
            IsValid = false;
            ValidationMessage = exception.Message;
            RefreshCommands();
            return false;
        }
    }

    public async Task<bool> SaveAsync()
    {
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            // Apply first: this completes image decoding and resource construction before the
            // durable callback can commit a path that the UI is unable to render.
            var normalized = _themeService.Apply(_resources, CreateCandidate());
            _previewApplied = true;
            OnPropertyChanged(nameof(IsPreviewApplied));
            await _persistAsync(normalized.Copy());

            var formerBackground = _originalSettings.BackgroundImagePath;
            _originalSettings = normalized.Copy();
            _uncommittedImportedBackground = null;
            if (!PathsEqualOrBothEmpty(formerBackground, normalized.BackgroundImagePath))
            {
                _themeService.TryDeleteManagedBackground(formerBackground);
            }

            LoadWorkingSettings(normalized);
            _previewApplied = false;
            OnPropertyChanged(nameof(IsPreviewApplied));
            StatusMessage = LocalizationService.Current.Get("appearance.status.saved");

            // Saved closes the modal dialog. Release the busy guard first so the dialog's
            // Closing handler can distinguish this successful close from a user attempting to
            // close the window while persistence is still in progress.
            IsBusy = false;
            Saved?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (InvalidDataException exception)
        {
            ValidationMessage = exception.Message;
            IsValid = false;
            return false;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            ValidationMessage = exception.Message;
            StatusMessage = LocalizationService.Current.Get("appearance.status.saveFailed");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ResetToDefaults()
    {
        if (IsBusy) return;
        DeletePreviousUncommittedBackground();
        LoadWorkingSettings(new ApplicationAppearanceSettings());
        Preview();
        StatusMessage = LocalizationService.Current.Get("appearance.status.resetPreview");
    }

    public void ClearBackgroundImage()
    {
        if (IsBusy || !HasBackgroundImage) return;
        DeletePreviousUncommittedBackground();
        BackgroundImagePath = null;
        StatusMessage = LocalizationService.Current.Get("appearance.status.removed");
    }

    public void Cancel()
    {
        if (IsBusy) return;
        // The managed background can disappear or become invalid while this modal editor is
        // open (for example because a synchronized folder changed it). Repair the opening
        // snapshot before applying it so Cancel always restores the valid colors and safely
        // drops only an unavailable image instead of throwing from Window.Closing.
        _originalSettings = _themeService.Repair(_originalSettings);
        _themeService.Apply(_resources, _originalSettings);
        DeletePreviousUncommittedBackground();
        LoadWorkingSettings(_originalSettings);
        _previewApplied = false;
        OnPropertyChanged(nameof(IsPreviewApplied));
        StatusMessage = LocalizationService.Current.Get("appearance.status.reverted");
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private void LoadWorkingSettings(ApplicationAppearanceSettings settings)
    {
        _windowColor = settings.WindowColor;
        _panelColor = settings.PanelColor;
        _panelRaisedColor = settings.PanelRaisedColor;
        _borderColor = settings.BorderColor;
        _accentColor = settings.AccentColor;
        _accentDarkColor = settings.AccentDarkColor;
        _textColor = settings.TextColor;
        _mutedTextColor = settings.MutedTextColor;
        _pattern = settings.Pattern;
        _patternColor = settings.PatternColor;
        _patternOpacity = settings.PatternOpacity;
        _backgroundImagePath = settings.BackgroundImagePath;
        _backgroundImageOpacity = settings.BackgroundImageOpacity;

        foreach (var propertyName in AppearancePropertyNames)
        {
            OnPropertyChanged(propertyName);
        }

        OnPropertyChanged(nameof(HasBackgroundImage));
        OnPropertyChanged(nameof(BackgroundImageFileName));
        OnPropertyChanged(nameof(IsDirty));
        ValidateCandidate();
    }

    private bool SetAppearanceProperty<T>(ref T storage, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref storage, value, propertyName)) return false;
        AppearancePropertyChanged();
        return true;
    }

    private void AppearancePropertyChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        _previewApplied = false;
        OnPropertyChanged(nameof(IsPreviewApplied));
        ValidateCandidate();
    }

    private void ValidateCandidate()
    {
        try
        {
            _themeService.ValidateAndNormalize(CreateCandidate());
            ValidationMessage = string.Empty;
            IsValid = true;
        }
        catch (InvalidDataException exception)
        {
            ValidationMessage = exception.Message;
            IsValid = false;
        }

        RefreshCommands();
    }

    private ApplicationAppearanceSettings CreateCandidate() => new()
    {
        WindowColor = WindowColor,
        PanelColor = PanelColor,
        PanelRaisedColor = PanelRaisedColor,
        BorderColor = BorderColor,
        AccentColor = AccentColor,
        AccentDarkColor = AccentDarkColor,
        TextColor = TextColor,
        MutedTextColor = MutedTextColor,
        Pattern = Pattern,
        PatternColor = PatternColor,
        PatternOpacity = PatternOpacity,
        BackgroundImagePath = BackgroundImagePath,
        BackgroundImageOpacity = BackgroundImageOpacity
    };

    private void DeletePreviousUncommittedBackground()
    {
        if (string.IsNullOrWhiteSpace(_uncommittedImportedBackground)) return;
        _themeService.TryDeleteManagedBackground(_uncommittedImportedBackground);
        _uncommittedImportedBackground = null;
    }

    private void RefreshCommands()
    {
        PreviewCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        ClearBackgroundImageCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private static bool SettingsEqual(ApplicationAppearanceSettings first, ApplicationAppearanceSettings second)
        => string.Equals(first.WindowColor, second.WindowColor, StringComparison.OrdinalIgnoreCase)
           && string.Equals(first.PanelColor, second.PanelColor, StringComparison.OrdinalIgnoreCase)
           && string.Equals(first.PanelRaisedColor, second.PanelRaisedColor, StringComparison.OrdinalIgnoreCase)
           && string.Equals(first.BorderColor, second.BorderColor, StringComparison.OrdinalIgnoreCase)
           && string.Equals(first.AccentColor, second.AccentColor, StringComparison.OrdinalIgnoreCase)
           && string.Equals(first.AccentDarkColor, second.AccentDarkColor, StringComparison.OrdinalIgnoreCase)
           && string.Equals(first.TextColor, second.TextColor, StringComparison.OrdinalIgnoreCase)
           && string.Equals(first.MutedTextColor, second.MutedTextColor, StringComparison.OrdinalIgnoreCase)
           && first.Pattern == second.Pattern
           && string.Equals(first.PatternColor, second.PatternColor, StringComparison.OrdinalIgnoreCase)
           && Math.Abs(first.PatternOpacity - second.PatternOpacity) < 0.0001
           && PathsEqualOrBothEmpty(first.BackgroundImagePath, second.BackgroundImagePath)
           && Math.Abs(first.BackgroundImageOpacity - second.BackgroundImageOpacity) < 0.0001;

    private static bool PathsEqualOrBothEmpty(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(second);
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), comparison);
    }

    private static readonly string[] AppearancePropertyNames =
    [
        nameof(WindowColor),
        nameof(PanelColor),
        nameof(PanelRaisedColor),
        nameof(BorderColor),
        nameof(AccentColor),
        nameof(AccentDarkColor),
        nameof(TextColor),
        nameof(MutedTextColor),
        nameof(Pattern),
        nameof(PatternColor),
        nameof(PatternOpacity),
        nameof(BackgroundImagePath),
        nameof(BackgroundImageOpacity)
    ];

    private static IReadOnlyList<AppearancePatternOption> CreatePatternOptions() =>
    [
        new(AppearancePattern.None, LocalizationService.Current.Get("appearance.pattern.none")),
        new(AppearancePattern.Dots, LocalizationService.Current.Get("appearance.pattern.dots")),
        new(AppearancePattern.Grid, LocalizationService.Current.Get("appearance.pattern.grid")),
        new(AppearancePattern.Diagonal, LocalizationService.Current.Get("appearance.pattern.diagonal"))
    ];
}
