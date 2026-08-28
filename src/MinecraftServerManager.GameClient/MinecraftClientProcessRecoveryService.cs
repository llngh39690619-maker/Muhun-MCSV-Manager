using System.ComponentModel;
using System.Diagnostics;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// A durable process identity. A PID alone is deliberately insufficient because Windows can reuse
/// it after a process exits.
/// </summary>
public sealed record MinecraftClientProcessIdentity(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string ExecutablePath);

/// <summary>
/// Reattaches the interactive launcher to Minecraft processes which survived a manager restart.
/// Only a live java.exe/javaw.exe process whose PID, creation time and executable path all match
/// the registry marker is accepted.
/// </summary>
public sealed class MinecraftClientProcessRecoveryService
{
    public MinecraftClientProcessSession? TryAttach(MinecraftClientInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!TryGetPersistedIdentity(instance, out var expected))
        {
            return null;
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(expected.ProcessId);
            if (!TryReadLiveIdentity(process, out var actual) || !IdentitiesEqual(expected, actual))
            {
                process.Dispose();
                return null;
            }

            if (!string.IsNullOrWhiteSpace(instance.JavaExecutablePath) &&
                !PathsEqual(instance.JavaExecutablePath, actual.ExecutablePath))
            {
                process.Dispose();
                return null;
            }

            var session = new MinecraftClientProcessSession(process, actual);
            process = null;
            return session;
        }
        catch (Exception error) when (IsExpectedProcessInspectionFailure(error))
        {
            process?.Dispose();
            return null;
        }
    }

    public bool IsMatchingProcessActive(MinecraftClientInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!TryGetPersistedIdentity(instance, out var expected))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(expected.ProcessId);
            return TryReadLiveIdentity(process, out var actual) &&
                   IdentitiesEqual(expected, actual) &&
                   (string.IsNullOrWhiteSpace(instance.JavaExecutablePath) ||
                    PathsEqual(instance.JavaExecutablePath, actual.ExecutablePath));
        }
        catch (Exception error) when (IsExpectedProcessInspectionFailure(error))
        {
            return false;
        }
    }

    public static bool HasPersistedIdentity(MinecraftClientInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.ActiveProcessId is not null ||
               instance.ActiveProcessStartedAtUtc is not null ||
               instance.ActiveProcessExecutablePath is not null;
    }

    public static bool TryGetPersistedIdentity(
        MinecraftClientInstance instance,
        out MinecraftClientProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.ActiveProcessId is not > 0 ||
            instance.ActiveProcessStartedAtUtc is not { } startedAtUtc ||
            startedAtUtc == default ||
            !TryNormalizeJavaExecutablePath(instance.ActiveProcessExecutablePath, out var executablePath))
        {
            identity = null!;
            return false;
        }

        identity = new MinecraftClientProcessIdentity(
            instance.ActiveProcessId.Value,
            startedAtUtc.ToUniversalTime(),
            executablePath);
        return true;
    }

    public static void RecordIdentity(
        MinecraftClientInstance instance,
        MinecraftClientProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.ProcessId <= 0 || identity.StartedAtUtc == default ||
            !TryNormalizeJavaExecutablePath(identity.ExecutablePath, out var executablePath))
        {
            throw new ArgumentException("Only a valid Java process identity can be persisted.", nameof(identity));
        }

        instance.ActiveProcessId = identity.ProcessId;
        instance.ActiveProcessStartedAtUtc = identity.StartedAtUtc.ToUniversalTime();
        instance.ActiveProcessExecutablePath = executablePath;
    }

    public static void ClearIdentity(MinecraftClientInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        instance.ActiveProcessId = null;
        instance.ActiveProcessStartedAtUtc = null;
        instance.ActiveProcessExecutablePath = null;
    }

    public static bool MarkerMatches(
        MinecraftClientInstance instance,
        MinecraftClientProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(identity);
        return TryGetPersistedIdentity(instance, out var persisted) &&
               IdentitiesEqual(persisted, identity);
    }

    internal static MinecraftClientProcessIdentity? CaptureStartedProcessIdentity(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (TryReadLiveIdentity(process, out var identity))
        {
            return identity;
        }

        // A very short-lived process can become uninspectable immediately after Start(). For a
        // normal Minecraft Java process this fallback still records the exact start request; the
        // next manager run will only accept it after independently reading and matching the live
        // process image.
        try
        {
            if (TryNormalizeJavaExecutablePath(process.StartInfo.FileName, out var executablePath))
            {
                DateTimeOffset startedAtUtc;
                try
                {
                    startedAtUtc = new DateTimeOffset(
                        process.StartTime.ToUniversalTime(),
                        TimeSpan.Zero);
                }
                catch (Exception error) when (IsExpectedProcessInspectionFailure(error))
                {
                    startedAtUtc = DateTimeOffset.UtcNow;
                }

                return new MinecraftClientProcessIdentity(
                    process.Id,
                    startedAtUtc,
                    executablePath);
            }
        }
        catch (Exception error) when (IsExpectedProcessInspectionFailure(error))
        {
        }

        return null;
    }

    internal static bool IsAcceptableJavaExecutablePath(string? path) =>
        TryNormalizeJavaExecutablePath(path, out _);

    internal static bool IdentitiesEqual(
        MinecraftClientProcessIdentity first,
        MinecraftClientProcessIdentity second) =>
        first.ProcessId == second.ProcessId &&
        first.StartedAtUtc.UtcTicks == second.StartedAtUtc.UtcTicks &&
        PathsEqual(first.ExecutablePath, second.ExecutablePath);

    private static bool TryReadLiveIdentity(
        Process process,
        out MinecraftClientProcessIdentity identity)
    {
        identity = null!;
        if (process.HasExited)
        {
            return false;
        }

        var executablePath = process.MainModule?.FileName;
        if (!TryNormalizeJavaExecutablePath(executablePath, out var normalizedPath))
        {
            return false;
        }

        var startedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        if (process.HasExited)
        {
            return false;
        }

        identity = new MinecraftClientProcessIdentity(process.Id, startedAtUtc, normalizedPath);
        return true;
    }

    private static bool TryNormalizeJavaExecutablePath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_767 || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        if (!fileName.Equals("java.exe", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsExpectedProcessInspectionFailure(Exception error) =>
        error is ArgumentException or InvalidOperationException or NotSupportedException or
            Win32Exception or UnauthorizedAccessException;
}
