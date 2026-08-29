using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.GameClient.Contracts;
using System.Windows;

namespace MinecraftServerManager.App.ViewModels;

public sealed class BedrockClientShortcutItemViewModel : ObservableObject
{
    public BedrockClientShortcutItemViewModel(BedrockClientShortcut model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);
    }

    public BedrockClientShortcut Model { get; }

    public Guid Id => Model.Id;

    public string Name => Model.DisplayName;

    public MinecraftBedrockChannel Channel => Model.Channel;

    public string ChannelText => L(Channel switch
    {
        MinecraftBedrockChannel.Stable => "client.bedrock.channel.stable",
        MinecraftBedrockChannel.Preview => "client.bedrock.channel.preview",
        _ => "client.bedrock.channel.unknown",
    });

    public string VersionSummary => L("client.bedrock.shortcut.summary", ChannelText);

    public string StatusText => L("client.bedrock.shortcut.official");

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ChannelText));
        OnPropertyChanged(nameof(VersionSummary));
        OnPropertyChanged(nameof(StatusText));
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);
}

public sealed record BedrockChannelChoiceViewModel(
    MinecraftBedrockChannel Channel,
    string Name,
    string Description);
