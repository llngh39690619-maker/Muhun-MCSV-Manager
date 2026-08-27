using System.ComponentModel;
using System.Diagnostics;

namespace MinecraftServerManager.Core.Runtime;

/// <summary>
/// Production adapter around <see cref="Process"/>.
/// </summary>
internal sealed class SystemServerProcess : IServerProcess
{
    private readonly Process _process = new();
    private readonly CancellationTokenSource _outputPumpCancellation = new();
    private Task? _outputPumpTask;
    private Task? _errorPumpTask;
    private WindowsKillOnCloseJob? _job;
    private bool _started;
    private bool _disposed;

    public event EventHandler<ProcessTextReceivedEventArgs>? OutputReceived;

    public event EventHandler<ProcessTextReceivedEventArgs>? ErrorReceived;

    public int Id => _started ? _process.Id : 0;

    public bool HasExited
    {
        get
        {
            if (!_started)
            {
                return false;
            }

            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            if (!HasExited)
            {
                return null;
            }

            try
            {
                return _process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public bool Start(ProcessStartInfo startInfo)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_started)
        {
            throw new InvalidOperationException("A process adapter can only be started once.");
        }

        _process.StartInfo = startInfo;

        if (!_process.Start())
        {
            return false;
        }

        _started = true;
        try
        {
            _job = WindowsKillOnCloseJob.CreateAndAssign(_process);
        }
        catch
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception cleanupError) when (cleanupError is InvalidOperationException or Win32Exception)
            {
            }

            throw;
        }
        // Do not use Process.BeginOutputReadLine: its StreamReader commits the entire stream to
        // one encoding. Java 8-17 can emit Windows ACP bytes while newer Java commonly emits
        // UTF-8. The two raw pumps keep stdout/stderr framing and decoder state independent.
        _outputPumpTask = PumpOutputAsync(
            _process.StandardOutput.BaseStream,
            line => OutputReceived?.Invoke(this, new ProcessTextReceivedEventArgs(line)),
            _outputPumpCancellation.Token);
        _errorPumpTask = PumpOutputAsync(
            _process.StandardError.BaseStream,
            line => ErrorReceived?.Invoke(this, new ProcessTextReceivedEventArgs(line)),
            _outputPumpCancellation.Token);
        return true;
    }

    public async ValueTask WriteLineAsync(
        string value,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_started || HasExited)
        {
            throw new InvalidOperationException("The process is not running.");
        }

        await _process.StandardInput.WriteLineAsync(value.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_started)
        {
            return;
        }

        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        // Process exit and redirected-pipe EOF are separate events. Await both pumps so a final
        // line without a newline is delivered before the manager publishes the terminal state.
        var pumps = new[] { _outputPumpTask, _errorPumpTask }
            .Where(task => task is not null)
            .Cast<Task>();
        await Task.WhenAll(pumps).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Kill(bool entireProcessTree)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_started && !HasExited)
        {
            _process.Kill(entireProcessTree);
        }
    }

    public ProcessMetrics CaptureMetrics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_started || HasExited)
        {
            throw new InvalidOperationException("The process is not running.");
        }

        _process.Refresh();
        return new ProcessMetrics(
            _process.TotalProcessorTime,
            _process.WorkingSet64,
            _process.PrivateMemorySize64);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _outputPumpCancellation.Cancel();
        // Close the Job handle before releasing the Process wrapper. If Java is unexpectedly
        // still alive, Windows terminates it and every descendant atomically at this boundary.
        _job?.Dispose();
        _job = null;
        _process.Dispose();
        _outputPumpCancellation.Dispose();
    }

    private static async Task PumpOutputAsync(
        Stream stream,
        Action<string> emitLine,
        CancellationToken cancellationToken)
    {
        try
        {
            await RawProcessOutputPump.RunAsync(stream, emitLine, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Adapter disposal intentionally cancels outstanding redirected reads.
        }
        catch (Exception error) when (
            cancellationToken.IsCancellationRequested
            && error is IOException or ObjectDisposedException)
        {
            // Closing Process also closes its redirected streams during normal disposal.
        }
    }
}
