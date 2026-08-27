using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.ViewModels;

public enum BackgroundServerJobKind
{
    CoreServer,
    OnlineModpack
}

public enum BackgroundServerJobState
{
    Queued,
    Running,
    Finalizing,
    Cancelling,
    Completed,
    Failed,
    Cancelled
}

public sealed class BackgroundServerJobViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cancellation = new();
    private BackgroundServerJobState _state = BackgroundServerJobState.Queued;
    private string _stageText = L("jobs.status.queuedPosition");
    private string _detailText = string.Empty;
    private string _errorMessage = string.Empty;
    private double _progressPercentage;
    private bool _isProgressIndeterminate = true;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;

    internal BackgroundServerJobViewModel(
        Guid id,
        BackgroundServerJobKind kind,
        string serverName,
        string title,
        Action<BackgroundServerJobViewModel> cancel)
    {
        Id = id;
        Kind = kind;
        ServerName = serverName;
        Title = title;
        CreatedAt = DateTimeOffset.Now;
        CancelCommand = new RelayCommand(
            () => cancel(this),
            () => CanCancel);
    }

    public Guid Id { get; }

    public BackgroundServerJobKind Kind { get; }

    public string ServerName { get; }

    public string Title { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? StartedAt
    {
        get => _startedAt;
        private set
        {
            if (SetProperty(ref _startedAt, value))
            {
                OnPropertyChanged(nameof(TimeText));
            }
        }
    }

    public DateTimeOffset? FinishedAt
    {
        get => _finishedAt;
        private set
        {
            if (SetProperty(ref _finishedAt, value))
            {
                OnPropertyChanged(nameof(TimeText));
            }
        }
    }

    public BackgroundServerJobState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(CanCancel));
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    public string StateText => State switch
    {
        BackgroundServerJobState.Queued => L("jobs.state.queued"),
        BackgroundServerJobState.Running => L("jobs.state.running"),
        BackgroundServerJobState.Finalizing => L("jobs.state.finalizing"),
        BackgroundServerJobState.Cancelling => L("jobs.state.cancelling"),
        BackgroundServerJobState.Completed => L("jobs.state.completed"),
        BackgroundServerJobState.Failed => L("jobs.state.failed"),
        BackgroundServerJobState.Cancelled => L("jobs.state.cancelled"),
        _ => State.ToString()
    };

    public bool IsActive => State is BackgroundServerJobState.Queued
        or BackgroundServerJobState.Running
        or BackgroundServerJobState.Finalizing
        or BackgroundServerJobState.Cancelling;

    public bool IsFinished => !IsActive;

    public bool CanCancel => State is BackgroundServerJobState.Queued
        or BackgroundServerJobState.Running;

    public string KindText => Kind switch
    {
        BackgroundServerJobKind.CoreServer => L("jobs.kind.core"),
        BackgroundServerJobKind.OnlineModpack => L("jobs.kind.modpack"),
        _ => "Server"
    };

    public string StageText
    {
        get => _stageText;
        private set => SetProperty(ref _stageText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public double ProgressPercentage
    {
        get => _progressPercentage;
        private set
        {
            if (SetProperty(ref _progressPercentage, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set
        {
            if (SetProperty(ref _isProgressIndeterminate, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public string ProgressText => IsProgressIndeterminate && IsActive
            ? L("jobs.time.processing")
        : $"{ProgressPercentage:0}%";

    public string TimeText
    {
        get
        {
            if (FinishedAt is { } finished)
            {
                var elapsed = finished - (StartedAt ?? CreatedAt);
            return L("jobs.time.finished", finished.ToString("HH:mm:ss"), FormatDuration(elapsed));
            }

            if (StartedAt is { } started)
            {
            return L("jobs.time.started", started.ToString("HH:mm:ss"));
            }

        return L("jobs.time.enqueued", CreatedAt.ToString("HH:mm:ss"));
        }
    }

    public RelayCommand CancelCommand { get; }

    internal CancellationToken CancellationToken => _cancellation.Token;

    internal void MarkRunning()
    {
        if (State == BackgroundServerJobState.Cancelling)
        {
            return;
        }

        StartedAt = DateTimeOffset.Now;
        State = BackgroundServerJobState.Running;
        StageText = L("jobs.status.preparing");
    }

    internal void ApplyProgress(string stage, string? detail, double? percentage)
    {
        if (State is not BackgroundServerJobState.Running)
        {
            return;
        }

        StageText = string.IsNullOrWhiteSpace(stage) ? L("jobs.status.processing") : stage.Trim();
        DetailText = detail?.Trim() ?? string.Empty;
        IsProgressIndeterminate = percentage is null;
        if (percentage is { } known)
        {
            ProgressPercentage = Math.Clamp(known, 0d, 100d);
        }
    }

    internal void MarkFinalizing()
    {
        State = BackgroundServerJobState.Finalizing;
        StageText = L("jobs.status.finalizing");
        DetailText = string.Empty;
        IsProgressIndeterminate = true;
    }

    internal void MarkCompleted()
    {
        ProgressPercentage = 100;
        IsProgressIndeterminate = false;
        StageText = L("jobs.status.completed");
        DetailText = string.Empty;
        State = BackgroundServerJobState.Completed;
        FinishedAt = DateTimeOffset.Now;
    }

    internal void MarkFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ErrorMessage = exception.Message;
        StageText = L("jobs.status.failed");
        DetailText = string.Empty;
        IsProgressIndeterminate = false;
        State = BackgroundServerJobState.Failed;
        FinishedAt = DateTimeOffset.Now;
    }

    internal void MarkCancelled()
    {
        StageText = L("jobs.status.cancelled");
        DetailText = string.Empty;
        IsProgressIndeterminate = false;
        State = BackgroundServerJobState.Cancelled;
        FinishedAt = DateTimeOffset.Now;
    }

    internal void RequestCancellation()
    {
        if (!CanCancel)
        {
            return;
        }

        State = BackgroundServerJobState.Cancelling;
        StageText = L("jobs.status.cancellingCleanup");
        DetailText = string.Empty;
        IsProgressIndeterminate = true;
        _cancellation.Cancel();
    }

    internal void DisposeCancellation() => _cancellation.Dispose();

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);
}
