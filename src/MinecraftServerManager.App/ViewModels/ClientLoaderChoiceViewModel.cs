using System.Windows;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.ViewModels;

public sealed class ClientLoaderChoiceViewModel : ObservableObject
{
    public ClientLoaderChoiceViewModel(
        MinecraftClientLoader loader,
        IReadOnlyList<MinecraftLoaderCatalogEntry> versions)
    {
        Loader = loader;
        Versions = versions ?? throw new ArgumentNullException(nameof(versions));
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);
    }

    public MinecraftClientLoader Loader { get; }

    public IReadOnlyList<MinecraftLoaderCatalogEntry> Versions { get; }

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

    public string AvailabilityText => IsExternal
        ? L("client.vm.loader.availability.external")
        : Loader == MinecraftClientLoader.Vanilla
            ? L("client.vm.loader.availability.full")
            : L("client.vm.loader.availability.stableCount", Versions.Count);

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AvailabilityText));
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);
}
