using System.Diagnostics;
using System.Text;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Providers;

public sealed class FtbInstallerCommandBuilder
{
    public ProcessStartInfo Build(FtbInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "FTB Pack ID 必須是正整數。");
        }

        if (request.VersionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "FTB Version ID 必須是正整數。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstallerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstallationDirectory);
        var installerPath = Path.GetFullPath(request.InstallerPath);
        var installDirectory = Path.GetFullPath(request.InstallationDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            WorkingDirectory = installDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        Add(startInfo, "-provider", "ftb");
        Add(startInfo, "-pack", request.PackId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(startInfo, "-version", request.VersionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(startInfo, "-dir", installDirectory);
        startInfo.ArgumentList.Add("-auto");
        startInfo.ArgumentList.Add("-validate");
        startInfo.ArgumentList.Add("-no-colours");
        // The official FTB installer owns file-level parallelism. Sixteen workers keeps the
        // operation bounded while allowing fast (including 1 Gbps) connections to make progress
        // across the many small files in a typical pack.
        Add(startInfo, "-threads", "16");
        Add(startInfo, "-timeout", "5m");
        startInfo.ArgumentList.Add("-accept-eula");
        return startInfo;
    }

    private static void Add(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }
}

public interface IFtbInstallerProcessRunner
{
    Task<FtbInstallerProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        IProgress<FtbInstallerOutputLine>? output = null,
        CancellationToken cancellationToken = default);
}

public sealed class FtbInstallerProcessException : Exception
{
    public FtbInstallerProcessException(FtbInstallerProcessResult result)
        : base(BuildMessage(result))
    {
        Result = result;
    }

    public FtbInstallerProcessResult Result { get; }

    private static string BuildMessage(FtbInstallerProcessResult result)
    {
        var details = result.StandardError.Concat(result.StandardOutput).TakeLast(12);
        return $"FTB Installer 結束碼為 {result.ExitCode}，安裝未完成。"
            + Environment.NewLine
            + string.Join(Environment.NewLine, details);
    }
}

public interface IFtbProcessHost
{
    IFtbRunningProcess Start(ProcessStartInfo startInfo);
}

public interface IFtbRunningProcess : IAsyncDisposable
{
    TextReader StandardOutput { get; }

    TextReader StandardError { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Kill(bool entireProcessTree);
}

public sealed class FtbInstallerProcessRunner(
    IFtbProcessHost? processHost = null,
    TimeSpan? drainTimeout = null)
    : IFtbInstallerProcessRunner
{
    private readonly IFtbProcessHost _processHost = processHost ?? new FtbSystemProcessHost();
    private readonly TimeSpan _drainTimeout = ValidateDrainTimeout(drainTimeout ?? TimeSpan.FromSeconds(10));

    public async Task<FtbInstallerProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        IProgress<FtbInstallerOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute
            || !startInfo.RedirectStandardOutput
            || !startInfo.RedirectStandardError)
        {
            throw new InvalidOperationException(
                "FTB Installer 必須停用 shell 並重新導向 stdout/stderr。");
        }

        await using var process = _processHost.Start(startInfo);
        var stdoutTask = CaptureLinesAsync(process.StandardOutput, isError: false, output);
        var stderrTask = CaptureLinesAsync(process.StandardError, isError: true, output);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancellationException)
        {
            await ManagedProcessTermination.EnsureExitedAfterCancellationAsync(
                    "FTB Installer",
                    () => process.HasExited,
                    () => process.Kill(entireProcessTree: true),
                    token => process.WaitForExitAsync(token),
                    _drainTimeout,
                    cancellationException)
                .ConfigureAwait(false);

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask)
                    .WaitAsync(_drainTimeout)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The managed process is confirmed stopped, so cancellation remains the primary
                // outcome even if captured output did not drain before the deadline.
            }

            throw;
        }

        BoundedCapturedStream[] captured;
        try
        {
            captured = await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(_drainTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidDataException(
                "FTB Installer 已結束，但輸出串流未在安全時間內關閉。",
                exception);
        }

        var result = new FtbInstallerProcessResult(
            process.ExitCode,
            captured[0].Lines,
            captured[1].Lines,
            captured[0].Truncated || captured[1].Truncated);
        if (result.ExitCode != 0)
        {
            throw new FtbInstallerProcessException(result);
        }

        return result;
    }

    private static Task<BoundedCapturedStream> CaptureLinesAsync(
        TextReader reader,
        bool isError,
        IProgress<FtbInstallerOutputLine>? output)
        => BoundedProcessOutputCapture.CaptureAsync(
            reader,
            line => output?.Report(new FtbInstallerOutputLine(isError, line)));

    private static TimeSpan ValidateDrainTimeout(TimeSpan value)
        => value > TimeSpan.Zero && value <= TimeSpan.FromMinutes(1)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
}

public sealed class FtbSystemProcessHost : IFtbProcessHost
{
    public IFtbRunningProcess Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("無法啟動 FTB Installer。");
            }

            return new FtbSystemRunningProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

internal sealed class FtbSystemRunningProcess(Process process) : IFtbRunningProcess
{
    public TextReader StandardOutput => process.StandardOutput;

    public TextReader StandardError => process.StandardError;

    public bool HasExited => process.HasExited;

    public int ExitCode => process.ExitCode;

    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => process.WaitForExitAsync(cancellationToken);

    public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);

    public ValueTask DisposeAsync()
    {
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}
