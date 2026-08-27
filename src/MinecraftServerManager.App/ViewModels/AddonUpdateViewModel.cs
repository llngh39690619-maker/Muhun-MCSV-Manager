using System.Windows;
using System.Windows.Media;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.App.ViewModels;

public sealed class AddonUpdateViewModel : ObservableObject
{
    private readonly AddonUpdateInfo _info;

    public AddonUpdateViewModel(AddonUpdateInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);
    }

    public AddonUpdateViewModel(ProductServerAddonSummary addon)
        : this(new AddonUpdateInfo(
            string.Empty,
            addon.FileName,
            string.Empty,
            false,
            null,
            addon.Kind == ProductServerAddonKind.Mod ? "Mod" : "Plugin",
            FormatSize(addon.SizeBytes),
            false,
            null,
            null,
            null,
            null,
            LocalizationService.Current.Get("service.readOnly.addons")))
    {
    }

    public string FileName => _info.FileName;
    public string CurrentDisplay => _info.CurrentVersion ?? L("addon.version.unrecognized");
    public string LatestDisplay => _info.LatestVersion ?? "—";
    public string StatusText => _info.Message;
    public string ProjectId => _info.ProjectId ?? "—";
    public string ProjectDisplay => L("addon.project", ProjectId);
    public bool IsUpdateAvailable => _info.IsUpdateAvailable;
    public string UpdateLabel => _info.IsUpdateAvailable
        ? L("addon.update.available")
        : L("addon.update.none");
    public Brush StatusBrush => _info.IsUpdateAvailable
        ? Brushes.Orange
        : _info.IsRecognized ? Brushes.MediumSeaGreen : Brushes.Gray;

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CurrentDisplay));
        OnPropertyChanged(nameof(ProjectDisplay));
        OnPropertyChanged(nameof(UpdateLabel));
    }

    private static string L(string key, params object?[] arguments)
        => LocalizationService.Current.Get(key, arguments);

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):0.##} MB"
            : $"{bytes / 1024d:0.##} KB";
}
