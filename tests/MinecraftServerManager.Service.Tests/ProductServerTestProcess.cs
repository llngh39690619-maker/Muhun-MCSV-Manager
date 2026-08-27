using System.Collections.Concurrent;
using System.Diagnostics;
using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.Service.Tests;

internal sealed class ProductServerTestProcessFactory : IServerProcessFactory
{
    private int _nextId = 30_000;
    private readonly ConcurrentQueue<ProductServerTestProcess> _processes = new();

    public IReadOnlyList<ProductServerTestProcess> Processes => _processes.ToArray();

    public ConcurrentQueue<bool> StartResults { get; } = new();

    public IServerProcess Create()
    {
        var process = new ProductServerTestProcess(
            Interlocked.Increment(ref _nextId),
            StartResults.TryDequeue(out var startResult) ? startResult : true);
        _processes.Enqueue(process);
        return process;
    }
}

internal sealed class ProductServerTestProcess(int id, bool startResult = true) : IServerProcess
{
    private readonly TaskCompletionSource<int> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _exited;
    private int? _exitCode;

    public event EventHandler<ProcessTextReceivedEventArgs>? OutputReceived;

    public event EventHandler<ProcessTextReceivedEventArgs>? ErrorReceived;

    public int Id { get; } = id;

    public bool HasExited => Volatile.Read(ref _exited) != 0;

    public int? ExitCode => _exitCode;

    public ProcessStartInfo? StartInfo { get; private set; }

    public ConcurrentQueue<string> Commands { get; } = new();

    public bool Start(ProcessStartInfo startInfo)
    {
        StartInfo = startInfo;
        return startResult;
    }

    public ValueTask WriteLineAsync(string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Commands.Enqueue(value);
        if (string.Equals(value, "stop", StringComparison.OrdinalIgnoreCase))
        {
            Complete(0);
        }

        return ValueTask.CompletedTask;
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        => _exit.Task.WaitAsync(cancellationToken);

    public void Kill(bool entireProcessTree) => Complete(-1);

    public ProcessMetrics CaptureMetrics() => new(TimeSpan.Zero, 123_456, 234_567);

    public void EmitOutput(string text)
        => OutputReceived?.Invoke(this, new ProcessTextReceivedEventArgs(text));

    public void EmitError(string text)
        => ErrorReceived?.Invoke(this, new ProcessTextReceivedEventArgs(text));

    public void Complete(int exitCode)
    {
        if (Interlocked.Exchange(ref _exited, 1) != 0)
        {
            return;
        }

        _exitCode = exitCode;
        _exit.TrySetResult(exitCode);
    }

    public void Dispose()
    {
    }
}
