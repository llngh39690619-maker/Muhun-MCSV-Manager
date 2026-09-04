using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MinecraftServerManager.Service;

internal sealed record ProductTailscaleCommandResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

internal sealed record ProductTailscaleHostnameUpdateResult(
    bool Succeeded,
    bool Changed,
    string? ErrorCode);

internal interface IProductOwnedFunnelProcess : IAsyncDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
    string StandardOutput { get; }
    string StandardError { get; }
    Task Completion { get; }
    Task StopAsync(CancellationToken cancellationToken);
}

internal interface IProductTailscaleProcessRunner
{
    Task<ProductTailscaleCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<IProductOwnedFunnelProcess> StartForegroundAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal interface IProductTailscaleExecutableLocator
{
    string? FindTrustedExecutable();
}

internal interface IProductTailscalePlatform
{
    Task<ProductTailscaleNodeStatus> GetNodeStatusAsync(CancellationToken cancellationToken);

    Task<ProductFunnelRouteStatus> GetFunnelStatusAsync(
        string dnsName,
        int localPort,
        CancellationToken cancellationToken);

    Task<ProductTailscaleHostnameUpdateResult> EnsureMachineHostnameAsync(
        string expectedHostname,
        CancellationToken cancellationToken);

    Task<IProductOwnedFunnelProcess> StartFunnelAsync(
        int localPort,
        CancellationToken cancellationToken);
}

internal sealed class ProductTailscalePlatform(
    IProductTailscaleExecutableLocator locator,
    IProductTailscaleProcessRunner runner) : IProductTailscalePlatform
{
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(4);

    public async Task<ProductTailscaleNodeStatus> GetNodeStatusAsync(
        CancellationToken cancellationToken)
    {
        var executable = locator.FindTrustedExecutable();
        if (executable is null)
        {
            return new ProductTailscaleNodeStatus(false, null, null, "tailscale.not_installed");
        }

        var result = await runner.RunAsync(
                executable,
                ["status", "--json"],
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new ProductTailscaleNodeStatus(
                false,
                null,
                null,
                result.TimedOut
                    ? "tailscale.status_timeout"
                    : ClassifyLocalApiFailure(
                        result.StandardOutput,
                        result.StandardError,
                        "tailscale.status_failed"));
        }

        return ProductTailscaleProtocol.ParseNodeStatus(result.StandardOutput);
    }

    public async Task<ProductFunnelRouteStatus> GetFunnelStatusAsync(
        string dnsName,
        int localPort,
        CancellationToken cancellationToken)
    {
        ValidatePort(localPort);
        var executable = locator.FindTrustedExecutable();
        if (executable is null)
        {
            return new ProductFunnelRouteStatus(
                ProductFunnelRouteDisposition.Indeterminate,
                "tailscale.not_installed");
        }

        var result = await runner.RunAsync(
                executable,
                ["funnel", "status", "--json"],
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new ProductFunnelRouteStatus(
                ProductFunnelRouteDisposition.Indeterminate,
                result.TimedOut
                    ? "tailscale.funnel_status_timeout"
                    : ClassifyLocalApiFailure(
                        result.StandardOutput,
                        result.StandardError,
                        "tailscale.funnel_status_failed"));
        }

        return ProductTailscaleProtocol.ParseFunnelStatus(
            result.StandardOutput,
            dnsName,
            CreateTarget(localPort));
    }

    public async Task<ProductTailscaleHostnameUpdateResult> EnsureMachineHostnameAsync(
        string expectedHostname,
        CancellationToken cancellationToken)
    {
        ValidateMachineHostname(expectedHostname);
        var executable = locator.FindTrustedExecutable();
        if (executable is null)
        {
            return new ProductTailscaleHostnameUpdateResult(
                false,
                false,
                "tailscale.not_installed");
        }

        // Query the preference first. This prevents every Service retry or restart from issuing a
        // control-plane mutation after the requested name has already been accepted. It also
        // distinguishes a delayed/conflicting MagicDNS assignment from a failed set operation.
        var current = await runner.RunAsync(
                executable,
                ["get", "hostname"],
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!current.Succeeded)
        {
            return new ProductTailscaleHostnameUpdateResult(
                false,
                false,
                current.TimedOut
                    ? "tailscale.hostname_status_timeout"
                    : ClassifyLocalApiFailure(
                        current.StandardOutput,
                        current.StandardError,
                        "tailscale.hostname_status_failed"));
        }

        if (!TryParseMachineHostnameOutput(current.StandardOutput, out var configuredHostname))
        {
            return new ProductTailscaleHostnameUpdateResult(
                false,
                false,
                "tailscale.hostname_status_invalid");
        }

        if (string.Equals(configuredHostname, expectedHostname, StringComparison.OrdinalIgnoreCase))
        {
            return new ProductTailscaleHostnameUpdateResult(true, false, null);
        }

        var update = await runner.RunAsync(
                executable,
                ["set", $"--hostname={expectedHostname}"],
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!update.Succeeded)
        {
            return new ProductTailscaleHostnameUpdateResult(
                false,
                false,
                update.TimedOut
                    ? "tailscale.hostname_set_timeout"
                    : ClassifyLocalApiFailure(
                        update.StandardOutput,
                        update.StandardError,
                        "tailscale.hostname_set_failed"));
        }

        return new ProductTailscaleHostnameUpdateResult(true, true, null);
    }

    /// <summary>
    /// Reduces Tailscale's local diagnostic text to a small public error vocabulary. The raw
    /// stderr is never copied into Service status or IPC responses because it can contain local
    /// account and process details. Windows Tailscale permits only one active local user identity;
    /// its tray process normally holds a long-running request, so a virtual Service SID receives
    /// the stable "Tailscale already in use" rejection even though the daemon is healthy.
    /// </summary>
    internal static string ClassifyLocalApiFailure(
        string standardOutput,
        string standardError,
        string fallbackErrorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackErrorCode);
        var diagnostic = string.Concat(standardError, "\n", standardOutput);
        if (diagnostic.Contains("Tailscale already in use", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "tailscale.localapi_identity_conflict";
        }

        var namesLocalApi = diagnostic.Contains("local-tailscaled.sock", StringComparison.OrdinalIgnoreCase) ||
                            diagnostic.Contains("ProtectedPrefix", StringComparison.OrdinalIgnoreCase) ||
                            diagnostic.Contains(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase);
        var namesAccessDenied = diagnostic.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
                                diagnostic.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
                                diagnostic.Contains("拒絕存取", StringComparison.Ordinal) ||
                                diagnostic.Contains("存取被拒", StringComparison.Ordinal);
        return namesLocalApi && namesAccessDenied
            ? "tailscale.localapi_access_denied"
            : fallbackErrorCode;
    }

    public Task<IProductOwnedFunnelProcess> StartFunnelAsync(
        int localPort,
        CancellationToken cancellationToken)
    {
        ValidatePort(localPort);
        var executable = locator.FindTrustedExecutable()
            ?? throw new InvalidOperationException("tailscale.not_installed");
        return runner.StartForegroundAsync(
            executable,
            ["funnel", "--yes", "--https=443", CreateTarget(localPort)],
            cancellationToken);
    }

    internal static string CreateTarget(int localPort)
    {
        ValidatePort(localPort);
        return $"http://127.0.0.1:{localPort}";
    }

    private static void ValidatePort(int localPort)
    {
        if (localPort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort));
        }
    }

    private static void ValidateMachineHostname(string hostname)
    {
        if (hostname.Length is < 1 or > 63 ||
            hostname[0] == '-' ||
            hostname[^1] == '-' ||
            hostname.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Tailscale machine hostname is invalid.", nameof(hostname));
        }
    }

    private static bool TryParseMachineHostnameOutput(string output, out string hostname)
    {
        hostname = string.Empty;
        if (output.Length > 256)
        {
            return false;
        }

        var candidate = output.TrimEnd('\r', '\n');
        // Tailscale 1.102 returns an empty successful value when no explicit hostname override
        // has been configured. That is a valid "not configured" state and should lead to one set.
        if (candidate.Length == 0)
        {
            return true;
        }

        if (candidate.Contains('\r') ||
            candidate.Contains('\n') ||
            !string.Equals(candidate, candidate.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            ValidateMachineHostname(candidate);
            hostname = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// Resolves only official machine-wide install locations. PATH, current directory, environment
/// overrides, user profiles, and registry command strings are intentionally ignored.
/// </summary>
internal sealed class ProductTailscaleExecutableLocator : IProductTailscaleExecutableLocator
{
    public string? FindTrustedExecutable()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        var relativePaths = new[]
        {
            Path.Combine("Tailscale", "tailscale.exe"),
            Path.Combine("Tailscale IPN", "tailscale.exe"),
        };

        foreach (var root in roots.Where(candidate => !string.IsNullOrWhiteSpace(candidate)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var relativePath in relativePaths)
            {
                var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
                if (!candidate.StartsWith(
                        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(candidate) ||
                    TraversesReparsePoint(candidate))
                {
                    continue;
                }

                return candidate;
            }
        }

        return null;
    }

    private static bool TraversesReparsePoint(string path)
    {
        for (var current = new FileInfo(path) as FileSystemInfo; current is not null; current = current switch
             {
                 FileInfo file => file.Directory,
                 DirectoryInfo directory => directory.Parent,
                 _ => null,
             })
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class ProductTailscaleProcessRunner : IProductTailscaleProcessRunner
{
    private const int MaximumCapturedCharacters = 32 * 1024;
    private static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(3);

    public async Task<ProductTailscaleCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        await using var process = await StartCoreAsync(executablePath, arguments, cancellationToken)
            .ConfigureAwait(false);
        var timedOut = false;
        try
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            await process.WaitForCompletionAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            await process.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ProductTailscaleCommandResult(
            process.ExitCode,
            process.StandardOutput,
            process.StandardError,
            timedOut);
    }

    public async Task<IProductOwnedFunnelProcess> StartForegroundAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => await StartCoreAsync(executablePath, arguments, cancellationToken).ConfigureAwait(false);

    private static Task<ProductOwnedTailscaleProcess> StartCoreAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateExecutable(executablePath);
        if (arguments.Count is < 1 or > 16 ||
            arguments.Any(argument => string.IsNullOrEmpty(argument) || argument.Length > 512 || argument.Contains('\0')))
        {
            throw new ArgumentException("Tailscale arguments are invalid.", nameof(arguments));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        SafeFileHandle? job = null;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Tailscale process did not start.");
            }

            job = ProductKillOnCloseJob.CreateAndAssign(process);
            var owned = new ProductOwnedTailscaleProcess(process, job, MaximumCapturedCharacters, ForcedExitTimeout);
            process = null!;
            job = null;
            return Task.FromResult(owned);
        }
        catch
        {
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
            {
            }

            job?.Dispose();
            process?.Dispose();
            throw;
        }
    }

    private static void ValidateExecutable(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) ||
            !string.Equals(Path.GetFileName(executablePath), "tailscale.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executablePath))
        {
            throw new FileNotFoundException("Trusted Tailscale executable is unavailable.");
        }
    }

    private sealed class ProductOwnedTailscaleProcess : IProductOwnedFunnelProcess
    {
        private readonly Process _process;
        private readonly SafeFileHandle _job;
        private readonly BoundedTextCapture _output;
        private readonly BoundedTextCapture _error;
        private readonly Task _outputPump;
        private readonly Task _errorPump;
        private readonly Task _completion;
        private readonly TimeSpan _forcedExitTimeout;
        private int _disposed;

        public ProductOwnedTailscaleProcess(
            Process process,
            SafeFileHandle job,
            int maximumCharacters,
            TimeSpan forcedExitTimeout)
        {
            _process = process;
            _job = job;
            _forcedExitTimeout = forcedExitTimeout;
            _output = new BoundedTextCapture(maximumCharacters);
            _error = new BoundedTextCapture(maximumCharacters);
            _outputPump = PumpAsync(process.StandardOutput, _output);
            _errorPump = PumpAsync(process.StandardError, _error);
            _completion = ObserveCompletionAsync();
        }

        public bool HasExited
        {
            get
            {
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
                try
                {
                    return _process.HasExited ? _process.ExitCode : null;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }

        public string StandardOutput => _output.ToString();
        public string StandardError => _error.ToString();
        public Task Completion => _completion;

        public async Task WaitForCompletionAsync(CancellationToken cancellationToken)
            => await _completion.WaitAsync(cancellationToken).ConfigureAwait(false);

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (!HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Closing the Kill-on-close Job below is the second, kernel-enforced stop.
                }
            }

            _job.Dispose();
            try
            {
                await _completion.WaitAsync(_forcedExitTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new IOException("Owned Tailscale process did not stop within the bounded timeout.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
            }
            finally
            {
                _job.Dispose();
                _process.Dispose();
            }
        }

        private async Task ObserveCompletionAsync()
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(_outputPump, _errorPump).ConfigureAwait(false);
        }

        private static async Task PumpAsync(StreamReader reader, BoundedTextCapture capture)
        {
            var buffer = new char[2_048];
            while (true)
            {
                var count = await reader.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0)
                {
                    return;
                }

                capture.Append(buffer.AsSpan(0, count));
            }
        }
    }

    private sealed class BoundedTextCapture(int maximumCharacters)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _value = new(Math.Min(maximumCharacters, 4_096));
        private bool _truncated;

        public void Append(ReadOnlySpan<char> value)
        {
            lock (_gate)
            {
                var remaining = maximumCharacters - _value.Length;
                if (remaining > 0)
                {
                    _value.Append(value[..Math.Min(value.Length, remaining)]);
                }

                _truncated |= value.Length > remaining;
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _truncated ? _value.ToString() + "…[truncated]" : _value.ToString();
            }
        }
    }
}

internal static class ProductKillOnCloseJob
{
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    public static SafeFileHandle CreateAndAssign(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Job objects are required.");
        }

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create process Job.");
        }

        try
        {
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(information, pointer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, pointer, (uint)size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure process Job.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }

            if (!AssignProcessToJobObject(job, process.SafeHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to assign Tailscale process to Job.");
            }

            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        uint informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
