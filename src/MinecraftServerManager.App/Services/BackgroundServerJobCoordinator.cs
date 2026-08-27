using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

internal sealed record BackgroundServerJobProgress(
    string Stage,
    double? Percentage = null,
    string? Detail = null);

internal readonly record struct BackgroundJobSubmissionResult(
    bool Accepted,
    string? Error = null)
{
    public static BackgroundJobSubmissionResult Success() => new(true);

    public static BackgroundJobSubmissionResult Failure(string error) => new(false, error);
}

internal enum BackgroundServerJobResourceClass
{
    General,
    BuildTools
}

internal sealed record BackgroundServerJobDefinition(
    BackgroundServerJobKind Kind,
    string ServerName,
    string Title,
    Func<IProgress<BackgroundServerJobProgress>, CancellationToken, Task<ServerInstance>> ExecuteAsync,
    BackgroundServerJobResourceClass ResourceClass = BackgroundServerJobResourceClass.General);

internal sealed class BackgroundServerJobCoordinator : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan DefaultCompletedJobRetention = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _globalSlots;
    private readonly Func<ServerInstance, CancellationToken, Task> _commitServerAsync;
    private readonly Func<string, bool> _isServerNameInUse;
    private readonly Func<string, string>? _resolveTargetIdentity;
    private readonly Dispatcher? _dispatcher;
    private readonly Action<Action>? _postProgressToUi;
    private readonly Channel<QueuedJob> _generalQueue;
    private readonly Channel<QueuedJob> _buildToolsQueue;
    private readonly Task[] _workers;
    private readonly ConcurrentDictionary<string, Guid> _activeNames =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Guid> _activeTargets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, BackgroundServerJobViewModel> _jobsById = [];
    private readonly ObservableCollection<BackgroundServerJobViewModel> _jobs = [];
    private readonly ConcurrentDictionary<Guid, Task> _completedJobCleanupTasks = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly TimeSpan _completedJobRetention;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private bool _acceptingJobs = true;
    private bool _disposed;

    public BackgroundServerJobCoordinator(
        Func<ServerInstance, CancellationToken, Task> commitServerAsync,
        Func<string, bool>? isServerNameInUse = null,
        Func<string, string>? resolveTargetIdentity = null,
        int? maximumConcurrentJobs = null,
        int? maximumConcurrentBuildToolsJobs = null,
        Dispatcher? dispatcher = null,
        bool marshalToApplicationDispatcher = true,
        Action<Action>? postProgressToUi = null,
        TimeSpan? completedJobRetention = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(commitServerAsync);
        var detectedMaximum = maximumConcurrentJobs ?? DetectMaximumConcurrentJobs();
        if (detectedMaximum is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentJobs));
        }

        var detectedBuildToolsMaximum = maximumConcurrentBuildToolsJobs
                                        ?? DetectMaximumConcurrentBuildToolsJobs(detectedMaximum);
        if (detectedBuildToolsMaximum is < 1 || detectedBuildToolsMaximum > detectedMaximum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentBuildToolsJobs));
        }

        var retention = completedJobRetention ?? DefaultCompletedJobRetention;
        if (retention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(completedJobRetention));
        }

        _commitServerAsync = commitServerAsync;
        _isServerNameInUse = isServerNameInUse ?? (_ => false);
        _resolveTargetIdentity = resolveTargetIdentity;
        MaximumConcurrentJobs = detectedMaximum;
        MaximumConcurrentBuildToolsJobs = detectedBuildToolsMaximum;
        _globalSlots = new SemaphoreSlim(MaximumConcurrentJobs, MaximumConcurrentJobs);
        _dispatcher = dispatcher
                      ?? (marshalToApplicationDispatcher ? Application.Current?.Dispatcher : null);
        _postProgressToUi = postProgressToUi;
        _completedJobRetention = retention;
        _delayAsync = delayAsync ?? ((delay, cancellationToken) =>
            Task.Delay(delay, cancellationToken));
        _generalQueue = CreateQueue();
        _buildToolsQueue = CreateQueue();
        var generalWorkers = Enumerable.Range(0, MaximumConcurrentJobs)
            .Select(_ => Task.Run(() => WorkerLoopAsync(_generalQueue.Reader)));
        var buildToolsWorkers = Enumerable.Range(0, MaximumConcurrentBuildToolsJobs)
            .Select(_ => Task.Run(() => WorkerLoopAsync(_buildToolsQueue.Reader)));
        _workers = generalWorkers.Concat(buildToolsWorkers).ToArray();
        Jobs = new ReadOnlyObservableCollection<BackgroundServerJobViewModel>(_jobs);
        ClearFinishedCommand = new RelayCommand(ClearFinished, () => _jobs.Any(job => job.IsFinished));
        CancelAllCommand = new RelayCommand(CancelAll, () => _jobs.Any(job => job.CanCancel));

        static Channel<QueuedJob> CreateQueue() => Channel.CreateUnbounded<QueuedJob>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ReadOnlyObservableCollection<BackgroundServerJobViewModel> Jobs { get; }

    public RelayCommand ClearFinishedCommand { get; }

    public RelayCommand CancelAllCommand { get; }

    public int MaximumConcurrentJobs { get; }

    public int MaximumConcurrentBuildToolsJobs { get; }

    public string SchedulingProfileText
        => LocalizationService.Current.Get(
            "jobs.schedulingProfile",
            MaximumConcurrentJobs,
            MaximumConcurrentBuildToolsJobs);

    public int ActiveCount => _jobs.Count(job => job.IsActive);

    public int FinishedCount => _jobs.Count(job => job.IsFinished);

    public bool HasActiveJobs => ActiveCount > 0;

    public bool HasJobs => _jobs.Count > 0;

    public string SummaryText => HasActiveJobs
            ? LocalizationService.Current.Get("jobs.summary.active", ActiveCount)
            : FinishedCount > 0
                ? LocalizationService.Current.Get("jobs.summary.finished", FinishedCount)
                : LocalizationService.Current.Get("jobs.summary.empty");

    public string LatestActivityText
    {
        get
        {
            var latest = _jobs.LastOrDefault(job => job.IsActive) ?? _jobs.LastOrDefault();
            return latest is null
            ? LocalizationService.Current.Get("jobs.activity.empty")
                : $"{latest.ServerName} · {latest.StageText}";
        }
    }

    public double AggregateProgress
    {
        get
        {
            var active = _jobs.Where(job => job.IsActive).ToArray();
            if (active.Length == 0)
            {
                return _jobs.Count > 0 && _jobs[^1].State == BackgroundServerJobState.Completed
                    ? 100
                    : 0;
            }

            return active.Average(job => job.IsProgressIndeterminate
                ? Math.Clamp(job.ProgressPercentage, 0d, 99d)
                : job.ProgressPercentage);
        }
    }

    public bool IsAggregateProgressIndeterminate
        => _jobs.Where(job => job.IsActive).Any(job => job.IsProgressIndeterminate);

    public bool TryEnqueue(
        BackgroundServerJobDefinition definition,
        out BackgroundServerJobViewModel? job,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(definition);
        job = null;
        error = null;
        if (_disposed || !_acceptingJobs)
        {
            error = LocalizationService.Current.Get("jobs.error.shuttingDown");
            return false;
        }

        var serverName = definition.ServerName.Trim();
        if (serverName.Length == 0)
        {
            error = LocalizationService.Current.Get("core.validation.serverName");
            return false;
        }

        if (_isServerNameInUse(serverName))
        {
            error = LocalizationService.Current.Get("jobs.error.duplicateServer", serverName);
            return false;
        }

        var nameKey = NormalizeName(serverName);
        var id = Guid.NewGuid();
        if (!_activeNames.TryAdd(nameKey, id))
        {
            error = LocalizationService.Current.Get("jobs.error.alreadyQueued", serverName);
            return false;
        }

        string? targetKey = null;
        if (_resolveTargetIdentity is not null)
        {
            try
            {
                targetKey = NormalizeTargetIdentity(_resolveTargetIdentity(serverName));
            }
            catch (Exception exception)
            {
                _activeNames.TryRemove(new KeyValuePair<string, Guid>(nameKey, id));
            error = LocalizationService.Current.Get("jobs.error.reserveFolder", exception.Message);
                return false;
            }

            if (!_activeTargets.TryAdd(targetKey, id))
            {
                _activeNames.TryRemove(new KeyValuePair<string, Guid>(nameKey, id));
            error = LocalizationService.Current.Get("jobs.error.folderBusy");
                return false;
            }
        }

        try
        {
            job = new BackgroundServerJobViewModel(
                id,
                definition.Kind,
                serverName,
                definition.Title,
                Cancel);
            job.PropertyChanged += OnJobPropertyChanged;
            _jobsById.Add(id, job);
            _jobs.Add(job);
            NotifySummaryChanged();

            var writer = definition.ResourceClass == BackgroundServerJobResourceClass.BuildTools
                ? _buildToolsQueue.Writer
                : _generalQueue.Writer;
            if (!writer.TryWrite(new QueuedJob(job, nameKey, targetKey, definition)))
            {
            throw new InvalidOperationException(LocalizationService.Current.Get("jobs.error.queueClosed"));
            }
            return true;
        }
        catch
        {
            _activeNames.TryRemove(nameKey, out _);
            if (targetKey is not null)
            {
                _activeTargets.TryRemove(targetKey, out _);
            }
            if (job is not null)
            {
                job.PropertyChanged -= OnJobPropertyChanged;
                _jobsById.Remove(job.Id);
                _jobs.Remove(job);
                job.DisposeCancellation();
                job = null;
            }

            NotifySummaryChanged();
            throw;
        }
    }

    public void Cancel(BackgroundServerJobViewModel job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!_jobsById.TryGetValue(job.Id, out var tracked) || !ReferenceEquals(job, tracked))
        {
            return;
        }

        job.RequestCancellation();
        NotifySummaryChanged();
    }

    public void CancelAll()
    {
        foreach (var job in _jobs.Where(job => job.CanCancel).ToArray())
        {
            job.RequestCancellation();
        }

        NotifySummaryChanged();
    }

    public void ClearFinished()
    {
        foreach (var job in _jobs.Where(job => job.IsFinished).ToArray())
        {
            RemoveJob(job);
        }

        NotifySummaryChanged();
    }

    public async Task CancelAndWaitAsync()
    {
        _acceptingJobs = false;
        _generalQueue.Writer.TryComplete();
        _buildToolsQueue.Writer.TryComplete();
        await InvokeOnUiAsync(CancelAll).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch
        {
            // RunJobAsync converts every expected cancellation/failure into a terminal job state.
            // This catch protects shutdown from a last-resort observer fault.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        await CancelAndWaitAsync().ConfigureAwait(false);
        await WaitForCompletedJobCleanupAsync().ConfigureAwait(false);
        await InvokeOnUiAsync(() =>
        {
            foreach (var job in _jobs)
            {
                job.PropertyChanged -= OnJobPropertyChanged;
                job.DisposeCancellation();
            }

            _jobs.Clear();
            _jobsById.Clear();
            NotifySummaryChanged();
        }).ConfigureAwait(false);
        _globalSlots.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async Task WorkerLoopAsync(ChannelReader<QueuedJob> reader)
    {
        await foreach (var queued in reader.ReadAllAsync().ConfigureAwait(false))
        {
            await RunJobAsync(queued.Job, queued.NameKey, queued.TargetKey, queued.Definition)
                .ConfigureAwait(false);
        }
    }

    private async Task RunJobAsync(
        BackgroundServerJobViewModel job,
        string nameKey,
        string? targetKey,
        BackgroundServerJobDefinition definition)
    {
        var acquiredGlobalSlot = false;
        var workflowReturnedVerifiedServer = false;
        LatestProgressDispatcher? progress = null;
        try
        {
            job.CancellationToken.ThrowIfCancellationRequested();
            await _globalSlots.WaitAsync(job.CancellationToken).ConfigureAwait(false);
            acquiredGlobalSlot = true;
            await InvokeOnUiAsync(job.MarkRunning).ConfigureAwait(false);

            // Installer processes can emit tens of thousands of progress lines. Retain only the
            // newest value while one Background-priority dispatcher operation is pending, instead
            // of allocating one DispatcherOperation per line and making the main GUI unresponsive.
            progress = new LatestProgressDispatcher(job, BeginInvokeOnUi);
            var server = await definition.ExecuteAsync(progress, job.CancellationToken)
                .ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(server);
            workflowReturnedVerifiedServer = true;
            progress.Complete();

            // A successful workflow return means its verified staging tree has already been
            // promoted and ownership has transferred to this coordinator. Cancellation after
            // that boundary must finish the non-cancellable manager commit; otherwise the
            // workflow no longer owns the final directory and an unmanaged orphan is left behind.
            await InvokeOnUiAsync(job.MarkFinalizing).ConfigureAwait(false);
            // Once a verified workflow has committed its final directory, completing the manager
            // record is an atomic finalization step and must not be interrupted by a late Cancel.
            await _commitServerAsync(server, CancellationToken.None).ConfigureAwait(false);
            await InvokeOnUiAsync(job.MarkCompleted).ConfigureAwait(false);
            ScheduleCompletedJobCleanup(job.Id);
        }
        catch (OperationCanceledException) when (
            job.CancellationToken.IsCancellationRequested
            && !workflowReturnedVerifiedServer)
        {
            await InvokeOnUiAsync(job.MarkCancelled).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await InvokeOnUiAsync(() => job.MarkFailed(exception)).ConfigureAwait(false);
        }
        finally
        {
            progress?.Complete();
            if (acquiredGlobalSlot)
            {
                _globalSlots.Release();
            }

            _activeNames.TryRemove(new KeyValuePair<string, Guid>(nameKey, job.Id));
            if (targetKey is not null)
            {
                _activeTargets.TryRemove(new KeyValuePair<string, Guid>(targetKey, job.Id));
            }
            await InvokeOnUiAsync(NotifySummaryChanged).ConfigureAwait(false);
        }
    }

    private void ScheduleCompletedJobCleanup(Guid jobId)
    {
        if (_disposed || _lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        var cleanupTask = RemoveCompletedJobAfterDelayAsync(
            jobId,
            _lifetimeCancellation.Token);
        _completedJobCleanupTasks[jobId] = cleanupTask;
        _ = cleanupTask.ContinueWith(
            static (completedTask, state) =>
            {
                _ = completedTask.Exception;
                var cleanup = ((BackgroundServerJobCoordinator Coordinator, Guid JobId))state!;
                cleanup.Coordinator._completedJobCleanupTasks.TryRemove(cleanup.JobId, out _);
            },
            (this, jobId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RemoveCompletedJobAfterDelayAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _delayAsync(_completedJobRetention, cancellationToken).ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                if (_jobsById.TryGetValue(jobId, out var tracked)
                    && tracked.State == BackgroundServerJobState.Completed)
                {
                    RemoveJob(tracked);
                    NotifySummaryChanged();
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The manager is closing; terminal records are disposed by DisposeAsync instead.
        }
    }

    private async Task WaitForCompletedJobCleanupAsync()
    {
        var cleanupTasks = _completedJobCleanupTasks.Values.ToArray();
        if (cleanupTasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(cleanupTasks).ConfigureAwait(false);
        }
        catch
        {
            // Cleanup is best-effort during shutdown; each tracked job is disposed below.
        }
    }

    private void RemoveJob(BackgroundServerJobViewModel job)
    {
        if (!_jobsById.TryGetValue(job.Id, out var tracked) || !ReferenceEquals(job, tracked))
        {
            return;
        }

        job.PropertyChanged -= OnJobPropertyChanged;
        _jobsById.Remove(job.Id);
        _jobs.Remove(job);
        job.DisposeCancellation();
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BackgroundServerJobViewModel.State)
            or nameof(BackgroundServerJobViewModel.StageText)
            or nameof(BackgroundServerJobViewModel.ProgressPercentage)
            or nameof(BackgroundServerJobViewModel.IsProgressIndeterminate))
        {
            NotifySummaryChanged();
        }
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(FinishedCount));
        OnPropertyChanged(nameof(HasActiveJobs));
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(LatestActivityText));
        OnPropertyChanged(nameof(AggregateProgress));
        OnPropertyChanged(nameof(IsAggregateProgressIndeterminate));
        ClearFinishedCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
    }

    private void BeginInvokeOnUi(Action action)
    {
        if (_postProgressToUi is not null)
        {
            _postProgressToUi(action);
            return;
        }

        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    private async Task InvokeOnUiAsync(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await _dispatcher.InvokeAsync(action, DispatcherPriority.Send);
    }

    private static string NormalizeName(string name)
        => SafePath.SanitizeFileName(name)
            .Normalize(NormalizationForm.FormKC)
            .ToUpperInvariant();

    private static string NormalizeTargetIdentity(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private static int DetectMaximumConcurrentJobs()
    {
        var processorCount = Math.Max(Environment.ProcessorCount, 1);
        var memoryGiB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024d * 1024d * 1024d);
        if (memoryGiB >= 96 && processorCount >= 24)
        {
            return 10;
        }

        if (memoryGiB >= 64 && processorCount >= 16)
        {
            return 8;
        }

        if (memoryGiB >= 32 && processorCount >= 8)
        {
            return 6;
        }

        return processorCount >= 4 ? 4 : 2;
    }

    private static int DetectMaximumConcurrentBuildToolsJobs(int globalMaximum)
    {
        var processorCount = Math.Max(Environment.ProcessorCount, 1);
        var memoryGiB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024d * 1024d * 1024d);
        var preferred = memoryGiB >= 96 && processorCount >= 24
            ? 3
            : processorCount >= 12 && memoryGiB >= 48
                ? 2
                : 1;
        return Math.Min(preferred, globalMaximum);
    }

    private sealed record QueuedJob(
        BackgroundServerJobViewModel Job,
        string NameKey,
        string? TargetKey,
        BackgroundServerJobDefinition Definition);

    private sealed class LatestProgressDispatcher(
        BackgroundServerJobViewModel job,
        Action<Action> schedule) : IProgress<BackgroundServerJobProgress>
    {
        private readonly BackgroundServerJobViewModel _job = job;
        private readonly Action<Action> _schedule = schedule;
        private BackgroundServerJobProgress? _latest;
        private int _isScheduled;
        private int _isComplete;

        public void Report(BackgroundServerJobProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Volatile.Read(ref _isComplete) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _latest, value);
            ScheduleDrain();
        }

        public void Complete()
        {
            Volatile.Write(ref _isComplete, 1);
            Interlocked.Exchange(ref _latest, null);
        }

        private void ScheduleDrain()
        {
            if (Interlocked.CompareExchange(ref _isScheduled, 1, 0) == 0)
            {
                _schedule(Drain);
            }
        }

        private void Drain()
        {
            var latest = Interlocked.Exchange(ref _latest, null);
            if (Volatile.Read(ref _isComplete) == 0 && latest is not null)
            {
                _job.ApplyProgress(latest.Stage, latest.Detail, latest.Percentage);
            }

            Volatile.Write(ref _isScheduled, 0);
            if (Volatile.Read(ref _isComplete) == 0 && Volatile.Read(ref _latest) is not null)
            {
                ScheduleDrain();
            }
        }
    }
}
