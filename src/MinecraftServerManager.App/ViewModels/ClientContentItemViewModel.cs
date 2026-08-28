using System.Windows;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.ViewModels;

public sealed class ClientContentItemViewModel : ObservableObject
{
    public ClientContentItemViewModel(MinecraftClientContentEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);
    }

    public MinecraftClientContentEntry Entry { get; }

    public string Name => Entry.DisplayName;

    public string RelativePath => Entry.RelativePath;

    public string SizeText => Entry.SizeBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{Entry.SizeBytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{Entry.SizeBytes / (1024d * 1024):0.##} MB",
        >= 1024 => $"{Entry.SizeBytes / 1024d:0.##} KB",
        _ => $"{Entry.SizeBytes} B",
    };

    public string StateText => Entry.Key.State switch
    {
        MinecraftClientContentState.Enabled => L("client.vm.content.state.enabled"),
        MinecraftClientContentState.Disabled => L("client.vm.content.state.disabled"),
        MinecraftClientContentState.Recycled => L("client.vm.content.state.recycled"),
        _ => Entry.Key.State.ToString(),
    };

    public bool IsEnabled => Entry.Key.State == MinecraftClientContentState.Enabled;

    public bool IsRecycled => Entry.Key.State == MinecraftClientContentState.Recycled;

    public bool IsActive => !IsRecycled;

    public bool IsSafe => Entry.IsSafe;

    public string DetailText => Entry.IsDirectory
        ? L("client.vm.content.detail.files", Entry.FileCount, SizeText)
        : SizeText;

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(DetailText));
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);
}
