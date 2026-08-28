namespace MinecraftServerManager.App.Infrastructure;

/// <summary>
/// Coordinates replaceable asynchronous projections such as filesystem scans. Starting a newer
/// operation cancels the previous one, every result carries a monotonic generation, and disposal
/// waits until all superseded operations have observed cancellation and unwound.
/// </summary>
internal sealed class LatestOperationCoordinator : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly HashSet<OperationRegistration> _registrations = [];
    private readonly HashSet<Task> _inFlight = [];
    private OperationRegistration? _current;
    private long _generation;
    private bool _disposed;

    public LatestOperationCoordinator(CancellationToken lifetimeToken = default)
    {
        _lifetimeToken = lifetimeToken;
    }

    public Task<LatestOperationResult<T>> RunLatestAsync<T>(
        Func<LatestOperationContext, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CancellationTokenSource? superseded;
        TaskCompletionSource start;
        Task<LatestOperationResult<T>> task;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            superseded = _current?.Cancellation;

            var registration = new OperationRegistration(
                checked(++_generation),
                CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken));
            start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            task = ExecuteAsync(start.Task, registration, operation);
            registration.Task = task;
            _registrations.Add(registration);
            _inFlight.Add(task);
            _current = registration;
            _ = task.ContinueWith(
                static (_, state) =>
                    ((CompletionState)state!).Coordinator.Complete(
                        ((CompletionState)state!).Registration),
                new CompletionState(this, registration),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        }

        try
        {
            TryCancel(superseded);
        }
        finally
        {
            // ExecuteAsync cannot complete before it is present in the tracked sets.
            start.SetResult();
        }

        return task;
    }

    public bool IsCurrent(long generation)
    {
        lock (_sync)
        {
            return !_disposed && _generation == generation;
        }
    }

    public void CancelCurrent()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _generation = checked(_generation + 1);
            cancellation = _current?.Cancellation;
            _current = null;
        }

        TryCancel(cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        CancellationTokenSource[] cancellations;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation = checked(_generation + 1);
            _current = null;
            cancellations = _registrations
                .Select(registration => registration.Cancellation)
                .ToArray();
            pending = _inFlight.ToArray();
        }

        foreach (var cancellation in cancellations)
        {
            TryCancel(cancellation);
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Cancellation is the expected shutdown path. A concurrently completed optional
            // projection must not make application shutdown fail either.
        }

        lock (_sync)
        {
            _registrations.Clear();
            _inFlight.Clear();
        }
    }

    private static async Task<LatestOperationResult<T>> ExecuteAsync<T>(
        Task start,
        OperationRegistration registration,
        Func<LatestOperationContext, Task<T>> operation)
    {
        await start.ConfigureAwait(false);
        registration.Cancellation.Token.ThrowIfCancellationRequested();
        var context = new LatestOperationContext(
            registration.Generation,
            registration.Cancellation.Token);
        var value = await operation(context).ConfigureAwait(false);
        registration.Cancellation.Token.ThrowIfCancellationRequested();
        return new LatestOperationResult<T>(registration.Generation, value);
    }

    private void Complete(OperationRegistration registration)
    {
        lock (_sync)
        {
            _registrations.Remove(registration);
            if (registration.Task is { } task)
            {
                _inFlight.Remove(task);
            }

            if (ReferenceEquals(_current, registration))
            {
                _current = null;
            }
        }

        registration.Cancellation.Dispose();
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion can dispose a superseded registration between capture and cancellation.
        }
    }

    private sealed class OperationRegistration(
        long generation,
        CancellationTokenSource cancellation)
    {
        public long Generation { get; } = generation;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task? Task { get; set; }
    }

    private sealed record CompletionState(
        LatestOperationCoordinator Coordinator,
        OperationRegistration Registration);
}

internal readonly record struct LatestOperationContext(
    long Generation,
    CancellationToken CancellationToken);

internal readonly record struct LatestOperationResult<T>(long Generation, T Value);
