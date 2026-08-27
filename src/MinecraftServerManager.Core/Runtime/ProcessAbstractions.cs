using System.Diagnostics;

namespace MinecraftServerManager.Core.Runtime;

/// <summary>
/// Creates process adapters. The abstraction keeps the runtime orchestration testable without
/// launching Java (or Minecraft) during unit tests.
/// </summary>
public interface IServerProcessFactory
{
    IServerProcess Create();
}

/// <summary>
/// Minimal process surface required by <see cref="ServerProcessManager"/>.
/// </summary>
public interface IServerProcess : IDisposable
{
    event EventHandler<ProcessTextReceivedEventArgs>? OutputReceived;

    event EventHandler<ProcessTextReceivedEventArgs>? ErrorReceived;

    int Id { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    bool Start(ProcessStartInfo startInfo);

    ValueTask WriteLineAsync(string value, CancellationToken cancellationToken = default);

    Task WaitForExitAsync(CancellationToken cancellationToken = default);

    void Kill(bool entireProcessTree);

    ProcessMetrics CaptureMetrics();
}

public sealed class ProcessTextReceivedEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}

/// <summary>
/// A raw point-in-time process measurement. CPU percentage is calculated by the manager from
/// consecutive samples.
/// </summary>
public readonly record struct ProcessMetrics(
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes,
    long PrivateMemoryBytes);

public sealed class SystemServerProcessFactory : IServerProcessFactory
{
    public IServerProcess Create() => new SystemServerProcess();
}
