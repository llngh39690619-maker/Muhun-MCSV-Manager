using System.Diagnostics;

namespace MinecraftServerManager.GameClient;

public sealed record MinecraftClientExitResult(int ExitCode, TimeSpan PlayTime);

public sealed class MinecraftClientProcessSession : IAsyncDisposable
{
    private const int MaximumBufferedOutputLines = 1_024;
    private readonly Process _process;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly object _outputGate = new();
    private readonly Queue<string> _bufferedOutput = new();
    private EventHandler<string>? _outputReceived;
    private int _disposed;
    private bool _logCaptureAvailable;

    internal MinecraftClientProcessSession(Process process, DateTimeOffset startedAtUtc)
    {
        _process = process;
        _startedAtUtc = startedAtUtc.ToUniversalTime();
        Completion = ObserveExitAsync(process, _startedAtUtc);
    }

    internal MinecraftClientProcessSession(
        Process process,
        MinecraftClientProcessIdentity persistentIdentity)
        : this(process, persistentIdentity.StartedAtUtc)
    {
        PersistentIdentity = persistentIdentity;
    }

    public int ProcessId => _process.Id;

    public DateTimeOffset StartedAtUtc => _startedAtUtc;

    /// <summary>
    /// Strong identity suitable for registry persistence. This is null when the launched process
    /// was not an inspectable java.exe/javaw.exe process.
    /// </summary>
    public MinecraftClientProcessIdentity? PersistentIdentity { get; }

    public Task<MinecraftClientExitResult> Completion { get; }

    /// <summary>
    /// True only for a process launched by this manager with redirected stdout/stderr. A session
    /// reattached after manager restart cannot inherit the old process pipes.
    /// </summary>
    public bool LogCaptureAvailable => _logCaptureAvailable;

    public event EventHandler<string>? OutputReceived
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_outputGate)
            {
                _outputReceived += value;
                // Replay under the same gate used by HandleOutput so the earliest lines are
                // delivered before any later live line. Product handlers only enqueue bounded
                // text and never re-enter this event.
                while (_bufferedOutput.TryDequeue(out var line))
                {
                    value(this, line);
                }
            }
        }
        remove
        {
            lock (_outputGate)
            {
                _outputReceived -= value;
            }
        }
    }

    internal void BeginLogCapture()
    {
        _logCaptureAvailable = true;
        _process.OutputDataReceived += HandleOutput;
        _process.ErrorDataReceived += HandleOutput;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public async Task StopAsync(
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        if (gracefulTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gracefulTimeout));
        }

        if (_process.HasExited)
        {
            await Completion.ConfigureAwait(false);
            return;
        }

        _process.CloseMainWindow();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(gracefulTimeout);
        try
        {
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        await Completion.ConfigureAwait(false);
    }

    internal async Task TerminateImmediatelyAsync(
        TimeSpan waitTimeout,
        CancellationToken cancellationToken = default)
    {
        if (waitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        }

        if (_process.HasExited)
        {
            await Completion.ConfigureAwait(false);
            return;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (_process.HasExited)
        {
            await Completion.ConfigureAwait(false);
            return;
        }

        await Completion.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _process.OutputDataReceived -= HandleOutput;
        _process.ErrorDataReceived -= HandleOutput;
        if (_process.HasExited)
        {
            await Completion.ConfigureAwait(false);
            _process.Dispose();
            return;
        }

        // Disposing Process while WaitForExitAsync is active can fault Completion. Detach the
        // manager immediately, then release the wrapper only after the independently running
        // Minecraft process exits. Disposing Process never terminates the operating-system process.
        _ = DisposeAfterExitAsync(_process, Completion);
    }

    private void HandleOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is { Length: > 0 } line)
        {
            lock (_outputGate)
            {
                if (_outputReceived is { } handlers)
                {
                    handlers.Invoke(this, line);
                    return;
                }

                _bufferedOutput.Enqueue(line);
                while (_bufferedOutput.Count > MaximumBufferedOutputLines)
                {
                    _bufferedOutput.Dequeue();
                }
            }
        }
    }

    private static async Task<MinecraftClientExitResult> ObserveExitAsync(
        Process process,
        DateTimeOffset startedAtUtc)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new MinecraftClientExitResult(
            process.ExitCode,
            DateTimeOffset.UtcNow - startedAtUtc);
    }

    private static async Task DisposeAfterExitAsync(
        Process process,
        Task<MinecraftClientExitResult> completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}
