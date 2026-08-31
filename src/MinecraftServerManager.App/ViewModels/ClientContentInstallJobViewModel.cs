using MinecraftServerManager.App.Infrastructure;

namespace MinecraftServerManager.App.ViewModels;

public enum ClientContentInstallJobState
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Canceled = 3,
}

public sealed class ClientContentInstallJobViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private string _statusText;
    private string _stage = "queued";
    private double _progressValue;
    private bool _isProgressIndeterminate = true;
    private ClientContentInstallJobState _state = ClientContentInstallJobState.Running;
    private bool _disposed;

    public ClientContentInstallJobViewModel(
        Guid id,
        Guid targetInstanceId,
        string targetInstanceName,
        string projectId,
        string projectTitle,
        string versionId,
        string versionName,
        string initialStatus,
        CancellationToken applicationCancellation)
    {
        if (id == Guid.Empty || targetInstanceId == Guid.Empty)
        {
            throw new ArgumentException("Job and target identifiers cannot be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetInstanceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialStatus);
        Id = id;
        TargetInstanceId = targetInstanceId;
        TargetInstanceName = targetInstanceName.Trim();
        ProjectId = projectId.Trim();
        ProjectTitle = projectTitle.Trim();
        VersionId = versionId.Trim();
        VersionName = versionName.Trim();
        _statusText = initialStatus.Trim();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationCancellation);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
    }

    public Guid Id { get; }

    public Guid TargetInstanceId { get; }

    public string TargetInstanceName { get; }

    public string ProjectId { get; }

    public string ProjectTitle { get; }

    public string VersionId { get; }

    public string VersionName { get; }

    public string DisplayName => $"{ProjectTitle} · {VersionName}";

    public CancellationToken CancellationToken => _cancellation.Token;

    public RelayCommand CancelCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string Stage
    {
        get => _stage;
        private set => SetProperty(ref _stage, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, Math.Clamp(value, 0d, 1d));
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public ClientContentInstallJobState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsTerminal));
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRunning => State == ClientContentInstallJobState.Running;

    public bool IsTerminal => !IsRunning;

    public void Report(string stage, string statusText, double? progress = null)
    {
        if (!IsRunning)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
        Stage = stage;
        StatusText = statusText.Trim();
        if (progress is { } value)
        {
            ProgressValue = value;
            IsProgressIndeterminate = false;
        }
    }

    public void MarkCompleted(string statusText)
    {
        Report("complete", statusText, 1d);
        State = ClientContentInstallJobState.Completed;
        IsProgressIndeterminate = false;
    }

    public void MarkFailed(string statusText)
    {
        Report("failed", statusText, ProgressValue);
        State = ClientContentInstallJobState.Failed;
        IsProgressIndeterminate = false;
    }

    public void MarkCanceled(string statusText)
    {
        Report("canceled", statusText, ProgressValue);
        State = ClientContentInstallJobState.Canceled;
        IsProgressIndeterminate = false;
    }

    public void Cancel()
    {
        if (IsRunning && !_cancellation.IsCancellationRequested)
        {
            _cancellation.Cancel();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Dispose();
    }
}
