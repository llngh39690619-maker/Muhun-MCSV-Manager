using System.Windows;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.ViewModels;

public sealed record PlayerStatusRecord(
    string Name,
    string? Uuid,
    bool IsOnline,
    bool IsOperator,
    bool IsWhitelisted,
    bool IsBanned);

public sealed class PlayerEntryViewModel : ObservableObject
{
    private bool _isOnline;
    private bool _isOperator;
    private bool _isWhitelisted;
    private bool _isBanned;

    public PlayerEntryViewModel(PlayerStatusRecord record)
    {
        Name = record.Name;
        Uuid = record.Uuid;
        Update(record);
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);
    }

    public string Name { get; }
    public string? Uuid { get; private set; }

    public bool IsOnline
    {
        get => _isOnline;
        private set => SetProperty(ref _isOnline, value);
    }

    public bool IsOperator
    {
        get => _isOperator;
        private set => SetProperty(ref _isOperator, value);
    }

    public bool IsWhitelisted
    {
        get => _isWhitelisted;
        private set => SetProperty(ref _isWhitelisted, value);
    }

    public bool IsBanned
    {
        get => _isBanned;
        private set => SetProperty(ref _isBanned, value);
    }

    public string OnlineText => L(IsOnline ? "player.state.online" : "player.state.offline");

    public string RoleText
    {
        get
        {
            var labels = new List<string>();
            if (IsOperator) labels.Add("OP");
            if (IsWhitelisted) labels.Add(L("player.role.whitelist"));
            if (IsBanned) labels.Add(L("player.role.banned"));
            return labels.Count == 0 ? L("player.role.regular") : string.Join(" · ", labels);
        }
    }

    public void UpdatePresence(bool isOnline)
    {
        if (IsOnline == isOnline) return;
        IsOnline = isOnline;
        OnPropertyChanged(nameof(OnlineText));
    }

    public void Update(PlayerStatusRecord record)
    {
        var nextUuid = record.Uuid ?? Uuid;
        var uuidChanged = !string.Equals(Uuid, nextUuid, StringComparison.Ordinal);
        var onlineChanged = IsOnline != record.IsOnline;
        var roleChanged = IsOperator != record.IsOperator
            || IsWhitelisted != record.IsWhitelisted
            || IsBanned != record.IsBanned;

        Uuid = nextUuid;
        IsOnline = record.IsOnline;
        IsOperator = record.IsOperator;
        IsWhitelisted = record.IsWhitelisted;
        IsBanned = record.IsBanned;
        if (uuidChanged) OnPropertyChanged(nameof(Uuid));
        if (onlineChanged) OnPropertyChanged(nameof(OnlineText));
        if (roleChanged) OnPropertyChanged(nameof(RoleText));
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(OnlineText));
        OnPropertyChanged(nameof(RoleText));
    }

    private static string L(string key)
        => LocalizationService.Current.Get(key);
}
