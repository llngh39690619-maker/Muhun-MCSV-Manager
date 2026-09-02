using System.ComponentModel;
using System.Security.Principal;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Service;

public sealed class ProductServiceState(TimeProvider timeProvider)
{
    private readonly object _identityGate = new();
    private Guid _installationId;
    private int _foundationReady;
    private int _ipcReady;
    private ProductActivationFailureDiagnostic? _startupFailure;

    public Guid InstallationId
    {
        get
        {
            lock (_identityGate)
            {
                return _installationId;
            }
        }
    }

    public DateTimeOffset StartedAtUtc { get; } = timeProvider.GetUtcNow();

    public ProductActivationFailureDiagnostic? StartupFailure =>
        Volatile.Read(ref _startupFailure);

    public bool IsReady =>
        Volatile.Read(ref _foundationReady) != 0 &&
        Volatile.Read(ref _ipcReady) != 0;

    public void Initialize(Guid installationId)
    {
        if (installationId == Guid.Empty)
        {
            throw new ArgumentException("Installation id must not be empty.", nameof(installationId));
        }

        lock (_identityGate)
        {
            if (_installationId != Guid.Empty && _installationId != installationId)
            {
                throw new InvalidOperationException("Service installation identity was already initialized.");
            }

            _installationId = installationId;
        }
    }

    /// <summary>
    /// Compatibility helper for focused domain tests that do not host the transport layer.
    /// Production startup marks the foundation and IPC listener independently.
    /// </summary>
    public void MarkReady()
    {
        Volatile.Write(ref _startupFailure, null);
        Volatile.Write(ref _foundationReady, 1);
        Volatile.Write(ref _ipcReady, 1);
    }

    public void MarkFoundationReady() => Volatile.Write(ref _foundationReady, 1);

    public void MarkFoundationNotReady() => Volatile.Write(ref _foundationReady, 0);

    public void MarkIpcReady()
    {
        Volatile.Write(ref _startupFailure, null);
        Volatile.Write(ref _ipcReady, 1);
    }

    public void MarkIpcNotReady() => Volatile.Write(ref _ipcReady, 0);

    public ProductActivationFailureDiagnostic MarkIpcFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var diagnostic = CreateIpcFailureDiagnostic(exception);
        Volatile.Write(ref _ipcReady, 0);
        Volatile.Write(ref _startupFailure, diagnostic);
        return diagnostic;
    }

    public void MarkNotReady()
    {
        Volatile.Write(ref _ipcReady, 0);
        Volatile.Write(ref _foundationReady, 0);
        Volatile.Write(ref _startupFailure, null);
    }

    internal static ProductActivationFailureDiagnostic CreateIpcFailureDiagnostic(
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var inner = exception.InnerException;
        return new ProductActivationFailureDiagnostic(
            ClassifyIpcFailure(exception),
            SafeExceptionType(exception),
            exception.HResult,
            inner is null ? null : SafeExceptionType(inner),
            inner?.HResult);
    }

    private static string ClassifyIpcFailure(Exception exception)
    {
        for (Exception? current = exception, previous = null;
             current is not null && !ReferenceEquals(current, previous);
             previous = current, current = current.InnerException)
        {
            if (current is IdentityNotMappedException)
            {
                return "ipc.operator_group_missing";
            }
            if (current is FileNotFoundException)
            {
                return "ipc.binding_missing";
            }
            if (current is InvalidDataException)
            {
                return "ipc.binding_invalid";
            }
            if (current is UnauthorizedAccessException)
            {
                return "ipc.access_denied";
            }
        }

        return exception is IOException
            ? "ipc.io_failure"
            : "ipc.configuration_invalid";
    }

    private static string SafeExceptionType(Exception exception) => exception switch
    {
        IdentityNotMappedException => nameof(IdentityNotMappedException),
        FileNotFoundException => nameof(FileNotFoundException),
        InvalidDataException => nameof(InvalidDataException),
        UnauthorizedAccessException => nameof(UnauthorizedAccessException),
        IOException => nameof(IOException),
        Win32Exception => nameof(Win32Exception),
        InvalidOperationException => nameof(InvalidOperationException),
        _ => nameof(Exception),
    };
}
