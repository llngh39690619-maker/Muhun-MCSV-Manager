using System.Collections.Concurrent;
using System.Diagnostics;
using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.Core.Tests.Runtime;

internal sealed class FakeServerProcessFactory : IServerProcessFactory
{
    private static int _nextProcessId = 10_000;
    private readonly ConcurrentQueue<FakeServerProcess> _processes = new();

    public bool ExitWhenStopCommandIsWritten { get; set; } = true;

    public bool StartResult { get; set; } = true;

    public bool IgnoreKill { get; set; }

    public IReadOnlyList<FakeServerProcess> Processes => _processes.ToArray();

    public IServerProcess Create()
    {
        var process = new FakeServerProcess(
            Interlocked.Increment(ref _nextProcessId),
            ExitWhenStopCommandIsWritten,
            StartResult,
            IgnoreKill);
        _processes.Enqueue(process);
        return process;
    }
}

internal sealed class FakeServerProcess(
    int id,
    bool exitWhenStopCommandIsWritten,
    bool startResult,
    bool ignoreKill) : IServerProcess
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<int> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EventHandler<ProcessTextReceivedEventArgs>? _capturedOutputHandlers;
    private EventHandler<ProcessTextReceivedEventArgs>? _capturedErrorHandlers;
    private bool _started;
    private bool _hasExited;
    private int? _exitCode;
    private ProcessMetrics _metrics;

    public event EventHandler<ProcessTextReceivedEventArgs>? OutputReceived;

    public event EventHandler<ProcessTextReceivedEventArgs>? ErrorReceived;

    public int Id { get; } = id;

    public bool HasExited
    {
        get
        {
            lock (_sync)
            {
                return _hasExited;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            lock (_sync)
            {
                return _exitCode;
            }
        }
    }

    public ProcessStartInfo? StartInfo { get; private set; }

    public ConcurrentQueue<string> Commands { get; } = new();

    public bool KillCalled { get; private set; }

    public bool EntireProcessTreeKilled { get; private set; }

    public bool Disposed { get; private set; }

    public bool Start(ProcessStartInfo startInfo)
    {
        lock (_sync)
        {
            if (_started)
            {
                throw new InvalidOperationException("Already started.");
            }

            _started = true;
            StartInfo = startInfo;
            _capturedOutputHandlers = OutputReceived;
            _capturedErrorHandlers = ErrorReceived;
            return startResult;
        }
    }

    public ValueTask WriteLineAsync(string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HasExited)
        {
            throw new InvalidOperationException("Process exited.");
        }

        Commands.Enqueue(value);
        if (exitWhenStopCommandIsWritten
            && string.Equals(value, "stop", StringComparison.OrdinalIgnoreCase))
        {
            Complete(0);
        }

        return ValueTask.CompletedTask;
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _exit.Task.WaitAsync(cancellationToken);

    public void Kill(bool entireProcessTree)
    {
        KillCalled = true;
        EntireProcessTreeKilled = entireProcessTree;
        if (!ignoreKill)
        {
            Complete(-1);
        }
    }

    public ProcessMetrics CaptureMetrics()
    {
        if (HasExited)
        {
            throw new InvalidOperationException("Process exited.");
        }

        lock (_sync)
        {
            return _metrics;
        }
    }

    public void SetMetrics(ProcessMetrics metrics)
    {
        lock (_sync)
        {
            _metrics = metrics;
        }
    }

    public void EmitOutput(string text) =>
        OutputReceived?.Invoke(this, new ProcessTextReceivedEventArgs(text));

    public void EmitError(string text) =>
        ErrorReceived?.Invoke(this, new ProcessTextReceivedEventArgs(text));

    public void EmitLateOutput(string text) =>
        _capturedOutputHandlers?.Invoke(this, new ProcessTextReceivedEventArgs(text));

    public void EmitLateError(string text) =>
        _capturedErrorHandlers?.Invoke(this, new ProcessTextReceivedEventArgs(text));

    public void Complete(int exitCode)
    {
        lock (_sync)
        {
            if (_hasExited)
            {
                return;
            }

            _hasExited = true;
            _exitCode = exitCode;
        }

        _exit.TrySetResult(exitCode);
    }

    public void Dispose() => Disposed = true;
}

internal sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
