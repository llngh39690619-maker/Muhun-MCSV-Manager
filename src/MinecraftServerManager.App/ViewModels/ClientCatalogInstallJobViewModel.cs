using System.Collections.ObjectModel;
using MinecraftServerManager.App.Infrastructure;

namespace MinecraftServerManager.App.ViewModels;

public enum ClientCatalogInstallJobState
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Canceled = 3,
}

public sealed class ClientCatalogInstallJobViewModel : ObservableObject
{
    private const int MaximumActivityEntries = 24;
    private string _statusText;
    private string _currentStage = "queued";
    private double _progressValue;
    private bool _isProgressIndeterminate = true;
    private ClientCatalogInstallJobState _state = ClientCatalogInstallJobState.Running;

    public ClientCatalogInstallJobViewModel(
        Guid id,
        string projectTitle,
        string versionName,
        string sourceLabel,
        string initialStatus)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The job identifier cannot be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(projectTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialStatus);
        Id = id;
        ProjectTitle = projectTitle.Trim();
        VersionName = versionName.Trim();
        SourceLabel = sourceLabel.Trim();
        _statusText = initialStatus.Trim();
        Activities.Add(new ClientCatalogInstallActivityItemViewModel(
            _currentStage,
            _statusText));
    }

    public Guid Id { get; }

    public string ProjectTitle { get; }

    public string VersionName { get; }

    public string SourceLabel { get; }

    public string DisplayName => $"{ProjectTitle} · {VersionName}";

    public ObservableCollection<ClientCatalogInstallActivityItemViewModel> Activities { get; } = [];

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentStage
    {
        get => _currentStage;
        private set => SetProperty(ref _currentStage, value);
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

    public ClientCatalogInstallJobState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsTerminal));
            }
        }
    }

    public bool IsRunning => State == ClientCatalogInstallJobState.Running;

    public bool IsTerminal => !IsRunning;

    internal void Report(string stage, string statusText, double? progressValue = null)
    {
        if (!IsRunning)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
        CurrentStage = stage;
        StatusText = statusText;
        if (progressValue is { } progress)
        {
            ProgressValue = progress;
            IsProgressIndeterminate = false;
        }

        if (Activities.LastOrDefault() is { } last &&
            string.Equals(last.Stage, stage, StringComparison.Ordinal))
        {
            last.Update(statusText);
            return;
        }

        Activities.Add(new ClientCatalogInstallActivityItemViewModel(stage, statusText));
        while (Activities.Count > MaximumActivityEntries)
        {
            Activities.RemoveAt(0);
        }
    }

    internal void MarkCompleted(string statusText)
    {
        Report("complete", statusText, 1d);
        State = ClientCatalogInstallJobState.Completed;
        IsProgressIndeterminate = false;
    }

    internal void MarkFailed(string statusText)
    {
        Report("failed", statusText, ProgressValue);
        State = ClientCatalogInstallJobState.Failed;
        IsProgressIndeterminate = false;
    }

    internal void MarkCanceled(string statusText)
    {
        Report("canceled", statusText, ProgressValue);
        State = ClientCatalogInstallJobState.Canceled;
        IsProgressIndeterminate = false;
    }

    internal void RefreshStatus(string statusText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
        StatusText = statusText;
        Activities.Clear();
        Activities.Add(new ClientCatalogInstallActivityItemViewModel(CurrentStage, statusText));
    }
}

public sealed class ClientCatalogInstallActivityItemViewModel : ObservableObject
{
    private string _statusText;

    internal ClientCatalogInstallActivityItemViewModel(string stage, string statusText)
    {
        Stage = stage;
        _statusText = statusText;
    }

    public string Stage { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    internal void Update(string statusText) => StatusText = statusText;
}
