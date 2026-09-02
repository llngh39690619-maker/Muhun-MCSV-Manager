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
    private const double MaximumNonCompletedProgress = 0.99d;
    private string _statusText;
    private string _currentStage = "queued";
    private string? _failedStage;
    private string? _failureDiagnosticId;
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

    public string? FailedStage
    {
        get => _failedStage;
        private set => SetProperty(ref _failedStage, value);
    }

    public string? FailureDiagnosticId
    {
        get => _failureDiagnosticId;
        private set => SetProperty(ref _failureDiagnosticId, value);
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
                OnPropertyChanged(nameof(IsFailed));
            }
        }
    }

    public bool IsRunning => State == ClientCatalogInstallJobState.Running;

    public bool IsTerminal => !IsRunning;

    public bool IsFailed => State == ClientCatalogInstallJobState.Failed;

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
            ProgressValue = Math.Max(
                ProgressValue,
                Math.Min(progress, MaximumNonCompletedProgress));
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
        if (!IsRunning)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
        State = ClientCatalogInstallJobState.Completed;
        CurrentStage = "complete";
        StatusText = statusText;
        ProgressValue = 1d;
        IsProgressIndeterminate = false;
        AddOrUpdateActivity(CurrentStage, statusText);
    }

    internal void MarkFailed(
        string statusText,
        string? failedStage = null,
        string? diagnosticId = null)
    {
        if (!IsRunning)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
        FailedStage = NormalizeFailureValue(failedStage) ?? CurrentStage;
        FailureDiagnosticId = NormalizeFailureValue(diagnosticId);
        State = ClientCatalogInstallJobState.Failed;
        StatusText = statusText;
        ProgressValue = Math.Min(ProgressValue, MaximumNonCompletedProgress);
        IsProgressIndeterminate = false;
        AddOrUpdateActivity(FailedStage, statusText);
    }

    internal void MarkCanceled(string statusText)
    {
        if (!IsRunning)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
        State = ClientCatalogInstallJobState.Canceled;
        CurrentStage = "canceled";
        StatusText = statusText;
        ProgressValue = Math.Min(ProgressValue, MaximumNonCompletedProgress);
        IsProgressIndeterminate = false;
        AddOrUpdateActivity(CurrentStage, statusText);
    }

    internal void UpdateFailureDiagnostic(string statusText, string? diagnosticId)
    {
        if (!IsFailed)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
        FailureDiagnosticId = NormalizeFailureValue(diagnosticId);
        StatusText = statusText;
        AddOrUpdateActivity(FailedStage ?? CurrentStage, statusText);
    }

    internal void RefreshStatus(string statusText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
        StatusText = statusText;
        Activities.Clear();
        Activities.Add(new ClientCatalogInstallActivityItemViewModel(
            IsFailed ? FailedStage ?? CurrentStage : CurrentStage,
            statusText));
    }

    private void AddOrUpdateActivity(string stage, string statusText)
    {
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

    private static string? NormalizeFailureValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
