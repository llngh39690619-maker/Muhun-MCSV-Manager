using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.ViewModels;

internal sealed record RemoteWebLogLineViewModel(
    string TimeText,
    string ChannelText,
    string Message);

/// <summary>
/// Lightweight, polling presentation over the bounded tunnel snapshot. A single collection reset
/// at most four times per second prevents noisy connector output from flooding the WPF dispatcher.
/// </summary>
internal sealed class RemoteWebConsoleViewModel : ObservableObject, IDisposable
{
    private readonly RemoteAccessCoordinator _coordinator;
    private readonly Func<Task> _startAsync;
    private readonly Func<Task> _stopAsync;
    private readonly DispatcherTimer _refreshTimer;
    private readonly BatchObservableCollection<RemoteWebLogLineViewModel> _logs = [];
    private string _stateText = L("remote.console.state.disabled");
    private string _publicUrl = string.Empty;
    private string _processIdText = "—";
    private string _versionText = "—";
    private string _uptimeText = "—";
    private string _errorText = string.Empty;
    private string _lastLogSignature = string.Empty;
    private bool _isBusy;
    private bool _disposed;

    public RemoteWebConsoleViewModel(
        RemoteAccessCoordinator coordinator,
        Func<Task> startAsync,
        Func<Task> stopAsync,
        Dispatcher dispatcher)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _startAsync = startAsync ?? throw new ArgumentNullException(nameof(startAsync));
        _stopAsync = stopAsync ?? throw new ArgumentNullException(nameof(stopAsync));
        // Closing Web must remain possible while a connection is starting, faulted, or waiting
        // for recovery; the stop callback is idempotent and owns the exact runtime cleanup.
        StopCommand = new AsyncRelayCommand(StopAsync, () => !IsBusy);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync, () => !IsBusy);
        CopyUrlCommand = new RelayCommand(CopyUrl, () => HasPublicUrl);
        OpenUrlCommand = new RelayCommand(OpenUrl, () => HasPublicUrl);
        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            (_, _) => Refresh(),
            dispatcher);
        _refreshTimer.Start();
        LocalizationService.Current.CultureChanged += OnCultureChanged;
        Refresh();
    }

    public ObservableCollection<RemoteWebLogLineViewModel> Logs => _logs;
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ReconnectCommand { get; }
    public RelayCommand CopyUrlCommand { get; }
    public RelayCommand OpenUrlCommand { get; }

    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }
    public string PublicUrl
    {
        get => _publicUrl;
        private set
        {
            if (!SetProperty(ref _publicUrl, value)) return;
            OnPropertyChanged(nameof(HasPublicUrl));
            CopyUrlCommand.NotifyCanExecuteChanged();
            OpenUrlCommand.NotifyCanExecuteChanged();
        }
    }
    public string ProcessIdText { get => _processIdText; private set => SetProperty(ref _processIdText, value); }
    public string VersionText { get => _versionText; private set => SetProperty(ref _versionText, value); }
    public string UptimeText { get => _uptimeText; private set => SetProperty(ref _uptimeText, value); }
    public string ErrorText { get => _errorText; private set { if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public bool HasPublicUrl => !string.IsNullOrWhiteSpace(PublicUrl);
    public bool IsRunning => _coordinator.State.IsRunning;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            NotifyCommands();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        LocalizationService.Current.CultureChanged -= OnCultureChanged;
    }

    private async Task StopAsync() => await RunAsync(_stopAsync);

    private async Task ReconnectAsync()
    {
        // Coordinator.StartAsync already performs ordered quiesce/reconfiguration. Calling the
        // persistent stop path first would leave auto-start disabled if the second step failed.
        await RunAsync(_startAsync);
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ErrorText = Sanitize(exception.Message);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_disposed) return;
        var runtime = _coordinator.State;
        // Cloudflare connectors expose process/log snapshots. Tailscale Serve/Funnel expose
        // their fixed URL and lifecycle through the coordinator instead; do not let a retained
        // Cloudflare diagnostic snapshot overwrite the current Tailscale presentation.
        var snapshot = runtime.AccessMode is RemoteAccessMode.CloudflareQuickTunnel
            or RemoteAccessMode.CloudflareNamedTunnel
            ? _coordinator.WebTunnelSnapshot
            : null;
        StateText = snapshot?.State switch
        {
            WebTunnelLifecycleState.Starting => L("remote.console.state.connecting"),
            WebTunnelLifecycleState.Running => L("remote.console.state.connected"),
            WebTunnelLifecycleState.Stopping => L("remote.console.state.stopping"),
            WebTunnelLifecycleState.Faulted when runtime.AutoRetryRecommended => L("remote.console.state.reconnecting"),
            WebTunnelLifecycleState.Faulted => L("remote.console.state.error"),
            _ when runtime.IsRunning => L("remote.console.state.connected"),
            _ when runtime.IsStarting => L("remote.console.state.connecting"),
            _ when runtime.AutoRetryRecommended => L("remote.console.state.reconnecting"),
            _ when runtime.Error is not null => L("remote.console.state.error"),
            _ => L("remote.console.state.closedForRun")
        };
        PublicUrl = snapshot?.PublicUrl?.AbsoluteUri
                    ?? runtime.PublicUrl?.AbsoluteUri
                    ?? string.Empty;
        ProcessIdText = snapshot?.ProcessId?.ToString() ?? "—";
        VersionText = string.IsNullOrWhiteSpace(snapshot?.ExecutableVersion) ? "—" : snapshot.ExecutableVersion;
        UptimeText = snapshot?.RunningFor is { } duration ? FormatDuration(duration) : "—";
        if (!string.IsNullOrWhiteSpace(snapshot?.Error))
        {
            ErrorText = snapshot.Error;
        }
        else if (!string.IsNullOrWhiteSpace(runtime.Error))
        {
            ErrorText = runtime.Error;
        }
        else if (runtime.IsRunning)
        {
            ErrorText = string.Empty;
        }

        var entries = snapshot?.RecentLogs ?? [];
        var signature = entries.Count == 0
            ? "0"
            : $"{entries.Count}|{entries[^1].TimestampUtc.UtcTicks}|{entries[^1].Message}";
        if (!string.Equals(signature, _lastLogSignature, StringComparison.Ordinal))
        {
            _lastLogSignature = signature;
            _logs.ReplaceAll(entries.Select(entry => new RemoteWebLogLineViewModel(
                entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
                entry.Channel switch
                {
                    WebTunnelLogChannel.StandardError => "ERR",
                    WebTunnelLogChannel.StandardOutput => "OUT",
                    _ => "SYS"
                },
                entry.Message)));
        }

        OnPropertyChanged(nameof(IsRunning));
        NotifyCommands();
    }

    private void CopyUrl()
    {
        if (!HasPublicUrl) return;
        Clipboard.SetText(PublicUrl);
    }

    private void OpenUrl()
    {
        if (!HasPublicUrl) return;
        Process.Start(new ProcessStartInfo(PublicUrl) { UseShellExecute = true });
    }

    private void NotifyCommands()
    {
        StopCommand.NotifyCanExecuteChanged();
        ReconnectCommand.NotifyCanExecuteChanged();
    }

    private void OnCultureChanged(object? sender, EventArgs e) => Refresh();

    private static string FormatDuration(TimeSpan value)
        => value.TotalDays >= 1
            ? L("remote.console.duration.days", (int)value.TotalDays, value.ToString("hh\\:mm\\:ss"))
            : value.ToString("hh\\:mm\\:ss");

    private static string L(string key, params object?[] arguments)
        => LocalizationService.Current.Get(key, arguments);

    private static string Sanitize(string message)
    {
        var value = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 500 ? value : value[..500] + "…";
    }
}
