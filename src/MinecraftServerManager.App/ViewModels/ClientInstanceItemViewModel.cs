using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.ViewModels;

public sealed class ClientInstanceItemViewModel : ObservableObject
{
    private const int MaximumGameLogLines = 2_000;
    private const int MaximumPendingGameLogLines = 4_096;
    private static readonly TimeSpan GameLogPublishInterval = TimeSpan.FromMilliseconds(75);
    private readonly BatchObservableCollection<string> _gameLogLines = [];
    private readonly ConcurrentQueue<string> _pendingGameLogLines = new();
    private MinecraftClientInstance _model;
    private MinecraftClientInstanceState _state;
    private int _pendingGameLogCount;
    private int _gameLogPublishScheduled;

    public ClientInstanceItemViewModel(MinecraftClientInstance model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);
    }

    public MinecraftClientInstance Model => _model;

    public Guid Id => Model.Id;

    public string Name => Model.Name;

    public string GameVersion => Model.GameVersion;

    public string LoaderText => Model.Loader == MinecraftClientLoader.Vanilla
        ? "Vanilla"
        : $"{Model.Loader} {Model.LoaderVersion}";

    public string VersionSummary => $"{LoaderText} · {GameVersion}";

    public string? IconImagePath => Model.IconImagePath
                                    ?? Model.CatalogIconImagePath
                                    ?? Model.CatalogPreviewImagePath;

    public string PlayTimeText
    {
        get
        {
            var duration = TimeSpan.FromSeconds(Math.Max(0, Model.TotalPlayTimeSeconds));
            return duration.TotalHours >= 1
                ? L("client.vm.instance.playTime.hours", duration.TotalHours)
                : L("client.vm.instance.playTime.minutes", duration.TotalMinutes);
        }
    }

    public string LastPlayedText => Model.LastPlayedAtUtc is { } value
        ? value.ToLocalTime().ToString("g", LocalizationService.Current.Culture)
        : L("client.vm.instance.lastNever");

    public MinecraftClientInstanceState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(CanQuickLaunch));
        }
    }

    public bool IsRunning => State is MinecraftClientInstanceState.Starting or
        MinecraftClientInstanceState.Running or MinecraftClientInstanceState.Stopping;

    public bool CanQuickLaunch => Model.EnableQuickLaunch && !IsRunning;

    public bool IsGameLogEnabled => Model.ShowGameLog;

    public bool HasGameLogLines => _gameLogLines.Count > 0;

    public ObservableCollection<string> GameLogLines => _gameLogLines;

    public string StatusText => L(State switch
    {
        MinecraftClientInstanceState.Installing => "client.vm.instance.state.installing",
        MinecraftClientInstanceState.Ready => "client.vm.instance.state.ready",
        MinecraftClientInstanceState.Starting => "client.vm.instance.state.starting",
        MinecraftClientInstanceState.Running => "client.vm.instance.state.running",
        MinecraftClientInstanceState.Stopping => "client.vm.instance.state.stopping",
        MinecraftClientInstanceState.Failed => "client.vm.instance.state.failed",
        _ => "client.vm.instance.state.stopped",
    });

    public void RefreshMetadata()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(GameVersion));
        OnPropertyChanged(nameof(LoaderText));
        OnPropertyChanged(nameof(VersionSummary));
        OnPropertyChanged(nameof(IconImagePath));
        OnPropertyChanged(nameof(PlayTimeText));
        OnPropertyChanged(nameof(LastPlayedText));
        OnPropertyChanged(nameof(CanQuickLaunch));
        OnPropertyChanged(nameof(IsGameLogEnabled));
    }

    public void ReplaceModel(MinecraftClientInstance model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        RefreshMetadata();
    }

    public void ClearGameLog()
    {
        while (_pendingGameLogLines.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _pendingGameLogCount, 0);
        PublishGameLog([]);
    }

    public void QueueGameLogLine(string? line)
    {
        if (!IsGameLogEnabled || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _pendingGameLogLines.Enqueue(line);
        var count = Interlocked.Increment(ref _pendingGameLogCount);
        while (count > MaximumPendingGameLogLines && _pendingGameLogLines.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref _pendingGameLogCount);
        }

        if (Interlocked.Exchange(ref _gameLogPublishScheduled, 1) == 0)
        {
            _ = PublishPendingGameLogAsync();
        }
    }

    private async Task PublishPendingGameLogAsync()
    {
        try
        {
            await Task.Delay(GameLogPublishInterval).ConfigureAwait(false);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(PublishPendingGameLog, DispatcherPriority.Background);
            }
            else
            {
                PublishPendingGameLog();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _gameLogPublishScheduled, 0);
            if (!_pendingGameLogLines.IsEmpty &&
                Interlocked.Exchange(ref _gameLogPublishScheduled, 1) == 0)
            {
                _ = PublishPendingGameLogAsync();
            }
        }
    }

    private void PublishPendingGameLog()
    {
        var replacement = _gameLogLines.ToList();
        while (_pendingGameLogLines.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _pendingGameLogCount);
            replacement.Add(line);
        }

        if (replacement.Count > MaximumGameLogLines)
        {
            replacement.RemoveRange(0, replacement.Count - MaximumGameLogLines);
        }

        PublishGameLog(replacement);
    }

    private void PublishGameLog(IReadOnlyList<string> replacement)
    {
        if (_gameLogLines.ReplaceAll(replacement))
        {
            OnPropertyChanged(nameof(HasGameLogLines));
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(PlayTimeText));
        OnPropertyChanged(nameof(LastPlayedText));
        OnPropertyChanged(nameof(StatusText));
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);
}
