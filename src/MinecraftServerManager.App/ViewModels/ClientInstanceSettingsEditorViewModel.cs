using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.GameClient;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.ViewModels;

public sealed record ClientMemoryModeChoice(MinecraftClientMemoryMode Mode, string Name);

public sealed record ClientMemoryRangePreview(int MinimumMemoryMb, int MaximumMemoryMb);

public sealed class ClientInstanceSettingsEditorViewModel : ObservableObject, INotifyDataErrorInfo
{
    private static readonly HashSet<string> SupportedIconExtensions = new(
        [".png", ".jpg", ".jpeg", ".bmp", ".ico"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ManagedMemoryArgumentPrefixes =
    [
        "-Xms",
        "-Xmx",
        "-XX:InitialHeapSize",
        "-XX:MaxHeapSize",
        "-XX:InitialRAMPercentage",
        "-XX:MinRAMPercentage",
        "-XX:MaxRAMPercentage",
        "-XX:MaxRAM",
    ];

    private readonly EditorSnapshot _baseline;
    private readonly Func<MinecraftClientMemoryMode, ClientMemoryRangePreview>? _resolveMemoryRange;
    private Dictionary<string, string[]> _errors = new(StringComparer.Ordinal);
    private string _name;
    private string _iconImagePath;
    private string _windowWidthText;
    private string _windowHeightText;
    private bool _fullScreen;
    private bool _enableQuickLaunch;
    private bool _hideLauncherAfterGameStarts;
    private bool _showGameLog;
    private bool _enableDedicatedGpu;
    private bool _enableDiscordPresence;
    private MinecraftClientMemoryMode _memoryMode;
    private int _minimumMemoryMb;
    private int _maximumMemoryMb;
    private string _javaExecutablePath;
    private string _jvmArgumentsText;
    private bool _isDirty;
    private bool _hasErrors;
    private IReadOnlyList<ClientMemoryModeChoice> _memoryModes = [];

    public ClientInstanceSettingsEditorViewModel(
        Guid instanceId,
        MinecraftClientInstanceSettingsUpdate settings,
        Func<MinecraftClientMemoryMode, ClientMemoryRangePreview>? resolveMemoryRange = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InstanceId = instanceId;
        _resolveMemoryRange = resolveMemoryRange;
        _name = settings.Name;
        _iconImagePath = settings.IconImagePath ?? string.Empty;
        _windowWidthText = settings.WindowWidth.ToString(CultureInfo.InvariantCulture);
        _windowHeightText = settings.WindowHeight.ToString(CultureInfo.InvariantCulture);
        _fullScreen = settings.FullScreen;
        _enableQuickLaunch = settings.EnableQuickLaunch;
        _hideLauncherAfterGameStarts = settings.HideLauncherAfterGameStarts;
        _showGameLog = settings.ShowGameLog;
        _enableDedicatedGpu = settings.EnableDedicatedGpu;
        _enableDiscordPresence = settings.EnableDiscordPresence;
        _memoryMode = settings.MemoryMode;
        _minimumMemoryMb = settings.MinimumMemoryMb;
        _maximumMemoryMb = settings.MaximumMemoryMb;
        if (_memoryMode is MinecraftClientMemoryMode.UseGlobalDefault or MinecraftClientMemoryMode.Automatic &&
            _resolveMemoryRange is not null)
        {
            ApplyResolvedMemoryRange(_resolveMemoryRange(_memoryMode), raiseNotifications: false);
        }
        _javaExecutablePath = settings.JavaExecutablePath ?? string.Empty;
        _jvmArgumentsText = string.Join(Environment.NewLine, settings.JvmArguments);
        _baseline = CaptureSnapshot();
        _memoryModes = CreateMemoryModes();
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);
        RefreshState();
    }

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public Guid InstanceId { get; }

    public IReadOnlyList<ClientMemoryModeChoice> MemoryModes => _memoryModes;

    public ClientMemoryModeChoice SelectedMemoryMode
    {
        get => MemoryModes.Single(choice => choice.Mode == MemoryMode);
        set
        {
            if (value is not null)
            {
                MemoryMode = value.Mode;
            }
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty))
            {
                RefreshState();
            }
        }
    }

    public string IconImagePath
    {
        get => _iconImagePath;
        set
        {
            if (SetProperty(ref _iconImagePath, value ?? string.Empty))
            {
                RefreshState();
            }
        }
    }

    public string WindowWidthText
    {
        get => _windowWidthText;
        set
        {
            if (SetProperty(ref _windowWidthText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(WindowWidth));
                RefreshState();
            }
        }
    }

    public string WindowHeightText
    {
        get => _windowHeightText;
        set
        {
            if (SetProperty(ref _windowHeightText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(WindowHeight));
                RefreshState();
            }
        }
    }

    public int WindowWidth
    {
        get => ParseIntegerOrZero(WindowWidthText);
        set => WindowWidthText = value.ToString(CultureInfo.InvariantCulture);
    }

    public int WindowHeight
    {
        get => ParseIntegerOrZero(WindowHeightText);
        set => WindowHeightText = value.ToString(CultureInfo.InvariantCulture);
    }

    public bool FullScreen { get => _fullScreen; set => SetAndRefresh(ref _fullScreen, value); }

    public bool EnableQuickLaunch { get => _enableQuickLaunch; set => SetAndRefresh(ref _enableQuickLaunch, value); }

    public bool HideLauncherAfterGameStarts
    {
        get => _hideLauncherAfterGameStarts;
        set => SetAndRefresh(ref _hideLauncherAfterGameStarts, value);
    }

    public bool ShowGameLog { get => _showGameLog; set => SetAndRefresh(ref _showGameLog, value); }

    public bool EnableDedicatedGpu
    {
        get => _enableDedicatedGpu;
        set => SetAndRefresh(ref _enableDedicatedGpu, value);
    }

    public bool EnableDiscordPresence
    {
        get => _enableDiscordPresence;
        set => SetAndRefresh(ref _enableDiscordPresence, value);
    }

    public MinecraftClientMemoryMode MemoryMode
    {
        get => _memoryMode;
        set
        {
            if (SetProperty(ref _memoryMode, value))
            {
                OnPropertyChanged(nameof(SelectedMemoryMode));
                if (value is MinecraftClientMemoryMode.UseGlobalDefault or MinecraftClientMemoryMode.Automatic &&
                    _resolveMemoryRange is not null)
                {
                    ApplyResolvedMemoryRange(_resolveMemoryRange(value), raiseNotifications: true);
                }

                RefreshState();
            }
        }
    }

    public int MinimumMemoryMb
    {
        get => _minimumMemoryMb;
        set => SetUserMemoryValue(ref _minimumMemoryMb, value);
    }

    public int MaximumMemoryMb
    {
        get => _maximumMemoryMb;
        set => SetUserMemoryValue(ref _maximumMemoryMb, value);
    }

    public string JavaExecutablePath
    {
        get => _javaExecutablePath;
        set
        {
            if (SetProperty(ref _javaExecutablePath, value ?? string.Empty))
            {
                RefreshState();
            }
        }
    }

    public string JvmArgumentsText
    {
        get => _jvmArgumentsText;
        set
        {
            if (SetProperty(ref _jvmArgumentsText, value ?? string.Empty))
            {
                RefreshState();
            }
        }
    }

    public bool IsDirty => _isDirty;

    public bool HasErrors => _hasErrors;

    public bool CanSave => IsDirty && !HasErrors;

    public string NameError => GetFirstError(nameof(Name));

    public string IconError => GetFirstError(nameof(IconImagePath));

    public string ResolutionError => JoinErrors(nameof(WindowWidthText), nameof(WindowHeightText));

    public string MemoryError => JoinErrors(nameof(MinimumMemoryMb), nameof(MaximumMemoryMb));

    public string JavaError => GetFirstError(nameof(JavaExecutablePath));

    public string JvmArgumentsError => GetFirstError(nameof(JvmArgumentsText));

    public string ValidationSummary => string.Join(
        "  ",
        _errors.Values.SelectMany(value => value).Distinct(StringComparer.Ordinal));

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return _errors.Values.SelectMany(value => value).ToArray();
        }

        return _errors.TryGetValue(propertyName, out var errors) ? errors : [];
    }

    public MinecraftClientInstanceSettingsUpdate BuildUpdate()
    {
        RefreshState();
        if (HasErrors)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(ValidationSummary)
                    ? L("client.vm.settings.error.invalid")
                    : ValidationSummary);
        }

        return new MinecraftClientInstanceSettingsUpdate
        {
            Name = Name.Trim(),
            IconImagePath = NormalizeOptionalText(IconImagePath),
            WindowWidth = int.Parse(WindowWidthText, NumberStyles.None, CultureInfo.InvariantCulture),
            WindowHeight = int.Parse(WindowHeightText, NumberStyles.None, CultureInfo.InvariantCulture),
            FullScreen = FullScreen,
            EnableQuickLaunch = EnableQuickLaunch,
            HideLauncherAfterGameStarts = HideLauncherAfterGameStarts,
            ShowGameLog = ShowGameLog,
            EnableDedicatedGpu = EnableDedicatedGpu,
            EnableDiscordPresence = EnableDiscordPresence,
            MemoryMode = MemoryMode,
            MinimumMemoryMb = MinimumMemoryMb,
            MaximumMemoryMb = MaximumMemoryMb,
            JavaExecutablePath = NormalizeOptionalText(JavaExecutablePath),
            JvmArguments = ParseJvmArguments(),
        };
    }

    private void SetAndRefresh<T>(
        ref T storage,
        T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref storage, value, propertyName))
        {
            RefreshState();
        }
    }

    private void SetUserMemoryValue(
        ref int storage,
        int value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref storage, value, propertyName))
        {
            return;
        }

        if (MemoryMode != MinecraftClientMemoryMode.Manual)
        {
            _memoryMode = MinecraftClientMemoryMode.Manual;
            OnPropertyChanged(nameof(MemoryMode));
            OnPropertyChanged(nameof(SelectedMemoryMode));
        }

        RefreshState();
    }

    private void ApplyResolvedMemoryRange(
        ClientMemoryRangePreview range,
        bool raiseNotifications)
    {
        ArgumentNullException.ThrowIfNull(range);
        var minimum = Math.Clamp(
            range.MinimumMemoryMb,
            MinecraftClientMemoryRecommendationService.MinimumAllocationMb,
            MinecraftClientMemoryRecommendationService.MaximumClientHeapMb);
        var maximum = Math.Clamp(
            range.MaximumMemoryMb,
            minimum,
            MinecraftClientMemoryRecommendationService.MaximumClientHeapMb);
        var minimumChanged = _minimumMemoryMb != minimum;
        var maximumChanged = _maximumMemoryMb != maximum;
        _minimumMemoryMb = minimum;
        _maximumMemoryMb = maximum;

        if (!raiseNotifications)
        {
            return;
        }

        if (minimumChanged)
        {
            OnPropertyChanged(nameof(MinimumMemoryMb));
        }

        if (maximumChanged)
        {
            OnPropertyChanged(nameof(MaximumMemoryMb));
        }
    }

    private void RefreshState()
    {
        var nextErrors = Validate();
        var affectedProperties = _errors.Keys
            .Concat(nextErrors.Keys)
            .Distinct(StringComparer.Ordinal)
            .Where(property => !_errors.TryGetValue(property, out var oldErrors) ||
                               !nextErrors.TryGetValue(property, out var newErrors) ||
                               !oldErrors.SequenceEqual(newErrors, StringComparer.Ordinal))
            .ToArray();
        _errors = nextErrors;

        foreach (var property in affectedProperties)
        {
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(property));
        }

        var nextHasErrors = _errors.Count > 0;
        if (_hasErrors != nextHasErrors)
        {
            _hasErrors = nextHasErrors;
            OnPropertyChanged(nameof(HasErrors));
        }

        var nextIsDirty = CaptureSnapshot() != _baseline;
        if (_isDirty != nextIsDirty)
        {
            _isDirty = nextIsDirty;
            OnPropertyChanged(nameof(IsDirty));
        }

        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(NameError));
        OnPropertyChanged(nameof(IconError));
        OnPropertyChanged(nameof(ResolutionError));
        OnPropertyChanged(nameof(MemoryError));
        OnPropertyChanged(nameof(JavaError));
        OnPropertyChanged(nameof(JvmArgumentsError));
        OnPropertyChanged(nameof(ValidationSummary));
    }

    private Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var normalizedName = Name.Trim();
        if (normalizedName.Length is < 1 or > 128 || normalizedName.Any(char.IsControl))
        {
            errors[nameof(Name)] = [L("client.vm.settings.error.name")];
        }

        ValidateIntegerText(WindowWidthText, nameof(WindowWidthText), L("client.settings.width"), 640, 16_384, errors);
        ValidateIntegerText(WindowHeightText, nameof(WindowHeightText), L("client.settings.height"), 360, 16_384, errors);

        if (!Enum.IsDefined(MemoryMode))
        {
            errors[nameof(MemoryMode)] = [L("client.vm.settings.error.memoryType")];
        }

        if (MinimumMemoryMb is < 512 or > 262_144)
        {
            errors[nameof(MinimumMemoryMb)] = [L("client.vm.settings.error.memoryMinimum")];
        }

        if (MaximumMemoryMb is < 512 or > 262_144)
        {
            errors[nameof(MaximumMemoryMb)] = [L("client.vm.settings.error.memoryMaximum")];
        }
        else if (MaximumMemoryMb < MinimumMemoryMb)
        {
            errors[nameof(MaximumMemoryMb)] = [L("client.vm.settings.error.memoryOrder")];
        }

        var iconError = ValidateIconPath(IconImagePath);
        if (iconError is not null)
        {
            errors[nameof(IconImagePath)] = [iconError];
        }

        var javaError = ValidateJavaPath(JavaExecutablePath);
        if (javaError is not null)
        {
            errors[nameof(JavaExecutablePath)] = [javaError];
        }

        var jvmError = ValidateJvmArguments();
        if (jvmError is not null)
        {
            errors[nameof(JvmArgumentsText)] = [jvmError];
        }

        return errors;
    }

    private string? ValidateIconPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!TryGetFullPath(path, out var fullPath) || !Path.IsPathFullyQualified(fullPath))
        {
            return L("client.vm.settings.error.iconPath");
        }

        if (!SupportedIconExtensions.Contains(Path.GetExtension(fullPath)))
        {
            return L("client.vm.settings.error.iconExtension");
        }

        try
        {
            var file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                return L("client.vm.settings.error.iconMissing");
            }

            if (file.Length > MinecraftClientInstanceSettingsService.MaximumIconFileBytes)
            {
                return L("client.vm.settings.error.iconSize");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return L("client.vm.settings.error.iconRead");
        }

        return null;
    }

    private static string? ValidateJavaPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!TryGetFullPath(path, out var fullPath) || !Path.IsPathFullyQualified(fullPath))
        {
            return L("client.vm.settings.error.javaPath");
        }

        var fileName = Path.GetFileName(fullPath);
        if (!string.Equals(fileName, "java.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fileName, "javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            return L("client.vm.settings.error.javaExecutable");
        }

        return File.Exists(fullPath) ? null : L("client.vm.settings.error.javaMissing");
    }

    private string? ValidateJvmArguments()
    {
        var arguments = ParseJvmArguments();
        if (arguments.Length > MinecraftClientInstanceSettingsService.MaximumJvmArgumentCount)
        {
            return L(
                "client.vm.settings.error.jvmCount",
                MinecraftClientInstanceSettingsService.MaximumJvmArgumentCount);
        }

        var totalLength = 0;
        foreach (var argument in arguments)
        {
            if (argument.Length > MinecraftClientInstanceSettingsService.MaximumJvmArgumentLength ||
                !argument.StartsWith("-", StringComparison.Ordinal))
            {
                return L("client.vm.settings.error.jvmFormat");
            }

            if (ManagedMemoryArgumentPrefixes.Any(prefix =>
                    argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return L("client.vm.settings.error.jvmMemory");
            }

            totalLength += argument.Length;
            if (totalLength > MinecraftClientInstanceSettingsService.MaximumJvmArgumentsTotalLength)
            {
                return L("client.vm.settings.error.jvmLength");
            }
        }

        return null;
    }

    private string[] ParseJvmArguments() => JvmArgumentsText
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private EditorSnapshot CaptureSnapshot() => new(
        Name,
        IconImagePath,
        WindowWidthText,
        WindowHeightText,
        FullScreen,
        EnableQuickLaunch,
        HideLauncherAfterGameStarts,
        ShowGameLog,
        EnableDedicatedGpu,
        EnableDiscordPresence,
        MemoryMode,
        MinimumMemoryMb,
        MaximumMemoryMb,
        JavaExecutablePath,
        JvmArgumentsText);

    private static void ValidateIntegerText(
        string value,
        string propertyName,
        string label,
        int minimum,
        int maximum,
        IDictionary<string, string[]> errors)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < minimum ||
            parsed > maximum)
        {
            errors[propertyName] = [L("client.vm.settings.error.integerRange", label, minimum, maximum)];
        }
    }

    private static int ParseIntegerOrZero(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static string? NormalizeOptionalText(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryGetFullPath(string path, out string fullPath)
    {
        try
        {
            var trimmed = path.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
            {
                fullPath = string.Empty;
                return false;
            }

            fullPath = Path.GetFullPath(trimmed);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private string GetFirstError(string propertyName) =>
        _errors.TryGetValue(propertyName, out var errors) ? errors.FirstOrDefault() ?? string.Empty : string.Empty;

    private string JoinErrors(params string[] propertyNames) => string.Join(
        "  ",
        propertyNames.Select(GetFirstError).Where(error => !string.IsNullOrWhiteSpace(error)));

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        _memoryModes = CreateMemoryModes();
        OnPropertyChanged(nameof(MemoryModes));
        OnPropertyChanged(nameof(SelectedMemoryMode));
        RefreshState();
    }

    private static IReadOnlyList<ClientMemoryModeChoice> CreateMemoryModes() =>
    [
        new(MinecraftClientMemoryMode.UseGlobalDefault, L("client.settings.memoryGlobal")),
        new(MinecraftClientMemoryMode.Automatic, L("client.settings.memoryAutomatic")),
        new(MinecraftClientMemoryMode.Manual, L("client.settings.memoryManual")),
    ];

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);

    private sealed record EditorSnapshot(
        string Name,
        string IconImagePath,
        string WindowWidthText,
        string WindowHeightText,
        bool FullScreen,
        bool EnableQuickLaunch,
        bool HideLauncherAfterGameStarts,
        bool ShowGameLog,
        bool EnableDedicatedGpu,
        bool EnableDiscordPresence,
        MinecraftClientMemoryMode MemoryMode,
        int MinimumMemoryMb,
        int MaximumMemoryMb,
        string JavaExecutablePath,
        string JvmArgumentsText);
}
