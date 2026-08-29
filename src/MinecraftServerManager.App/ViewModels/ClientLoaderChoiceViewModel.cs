using System.Windows;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.ViewModels;

public sealed class ClientLoaderChoiceViewModel : ObservableObject
{
    public ClientLoaderChoiceViewModel(
        MinecraftClientLoader loader,
        IReadOnlyList<MinecraftLoaderCatalogEntry> versions,
        bool isChecking = false,
        bool catalogQueryFailed = false)
    {
        Loader = loader;
        Versions = versions ?? throw new ArgumentNullException(nameof(versions));
        IsChecking = isChecking;
        CatalogQueryFailed = catalogQueryFailed;
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);
    }

    public MinecraftClientLoader Loader { get; }

    public IReadOnlyList<MinecraftLoaderCatalogEntry> Versions { get; }

    public bool IsChecking { get; }

    public bool CatalogQueryFailed { get; }

    public string Name => Loader switch
    {
        MinecraftClientLoader.Vanilla => L("client.vm.loader.vanilla"),
        MinecraftClientLoader.NeoForge => "NeoForge",
        _ => Loader.ToString(),
    };

    public bool IsManaged => Loader == MinecraftClientLoader.Vanilla ||
        Versions.Any(item => item.InstallKind == MinecraftClientLoaderInstallKind.Managed);

    public bool IsExternal => Versions.Any(item =>
        item.InstallKind == MinecraftClientLoaderInstallKind.ExternalInstallerRequired);

    /// <summary>
    /// Whether this fixed catalog entry can actually be selected for the current game release.
    /// Unsupported loaders stay visible so changing a Minecraft version never changes the layout.
    /// </summary>
    public bool IsAvailable => !IsChecking && !CatalogQueryFailed && (IsManaged || IsExternal);

    public string AvailabilityText => IsChecking
        ? L("client.vm.loader.availability.checking")
        : CatalogQueryFailed
            ? L("client.vm.loader.availability.queryFailed")
            : IsExternal
                ? L("client.vm.loader.availability.external")
                : Loader == MinecraftClientLoader.Vanilla
                    ? L("client.vm.loader.availability.full")
                    : IsManaged
                        ? L("client.vm.loader.availability.stableCount", Versions.Count)
                        : L("client.vm.loader.availability.unsupportedVersion");

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AvailabilityText));
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);
}
