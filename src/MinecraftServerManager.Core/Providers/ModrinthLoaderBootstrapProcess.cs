using System.Diagnostics;
using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Providers;

public sealed class ModrinthLoaderBootstrapCommandBuilder
{
    public ProcessStartInfo Build(
        ModrinthModpackLoaderInstallRequest request,
        string javaExecutablePath,
        string installerPath,
        string freshWorkingDirectory,
        string privateHomeDirectory,
        string privateTempDirectory)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(javaExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(freshWorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateHomeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateTempDirectory);
        ModrinthOfficialLoaderArtifactProvider.ValidateVersionArgument(
            request.MinecraftVersion,
            nameof(request.MinecraftVersion));

        if (request.Kind is ModrinthModpackLoaderKind.Vanilla or ModrinthModpackLoaderKind.Quilt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"{request.Kind} 不使用此 Java installer command builder。");
        }

        if (string.IsNullOrWhiteSpace(request.LoaderVersion))
        {
            throw new ArgumentException($"{request.Kind} 缺少 LoaderVersion。", nameof(request));
        }

        ModrinthOfficialLoaderArtifactProvider.ValidateVersionArgument(
            request.LoaderVersion,
            nameof(request.LoaderVersion));
        var java = Path.GetFullPath(javaExecutablePath);
        var installer = Path.GetFullPath(installerPath);
        var workingDirectory = Path.GetFullPath(freshWorkingDirectory);
        var privateHome = Path.GetFullPath(privateHomeDirectory);
        var privateTemp = Path.GetFullPath(privateTempDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = java,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        ManagedJavaProcessEnvironment.Configure(
            startInfo,
            java,
            privateHome,
            privateTemp);
        startInfo.ArgumentList.Add($"-Duser.home={privateHome}");
        startInfo.ArgumentList.Add($"-Djava.io.tmpdir={privateTemp}");
        startInfo.ArgumentList.Add($"-Duser.dir={workingDirectory}");
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(installer);

        if (request.Kind == ModrinthModpackLoaderKind.Fabric)
        {
            Add(startInfo, "server");
            Add(startInfo, "-dir", workingDirectory);
            Add(startInfo, "-mcversion", request.MinecraftVersion);
            Add(startInfo, "-loader", request.LoaderVersion);
            Add(startInfo, "-downloadMinecraft");
        }
        else
        {
            Add(startInfo, "--installServer", workingDirectory);
        }

        return startInfo;
    }

    private static void Add(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }
}

public interface IModrinthLoaderBootstrapProcessRunner
{
    Task<ModrinthLoaderBootstrapProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
        CancellationToken cancellationToken = default);
}

public sealed class ModrinthLoaderBootstrapProcessException : Exception
{
    public ModrinthLoaderBootstrapProcessException(ModrinthLoaderBootstrapProcessResult result)
        : base(BuildMessage(result))
    {
        Result = result;
    }

    public ModrinthLoaderBootstrapProcessResult Result { get; }

    private static string BuildMessage(ModrinthLoaderBootstrapProcessResult result)
    {
        var tail = result.StandardError.Concat(result.StandardOutput).TakeLast(20);
        return $"ModLoader Installer 結束碼為 {result.ExitCode}，安裝未完成。"
            + Environment.NewLine
            + string.Join(Environment.NewLine, tail);
    }
}

public interface IModrinthLoaderProcessHost
{
    IModrinthLoaderRunningProcess Start(ProcessStartInfo startInfo);
}

public interface IModrinthLoaderRunningProcess : IAsyncDisposable
{
    TextReader StandardOutput { get; }

    TextReader StandardError { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Kill(bool entireProcessTree);
}

public sealed class ModrinthLoaderBootstrapProcessRunner(
    IModrinthLoaderProcessHost? processHost = null,
    TimeSpan? drainTimeout = null)
    : IModrinthLoaderBootstrapProcessRunner
{
    private readonly IModrinthLoaderProcessHost _processHost = processHost
        ?? new ModrinthLoaderSystemProcessHost();
    private readonly TimeSpan _drainTimeout = ValidateDrainTimeout(drainTimeout ?? TimeSpan.FromSeconds(10));

    public async Task<ModrinthLoaderBootstrapProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute
            || !startInfo.RedirectStandardOutput
            || !startInfo.RedirectStandardError)
        {
            throw new InvalidOperationException(
                "ModLoader Installer 必須停用 shell 並重新導向 stdout/stderr。");
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
                    "ModLoader Installer",
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
                "ModLoader Installer 已結束，但輸出串流未在安全時間內關閉。",
                exception);
        }

        var stdout = captured[0];
        var stderr = captured[1];
        var result = new ModrinthLoaderBootstrapProcessResult(
            process.ExitCode,
            stdout.Lines,
            stderr.Lines,
            stdout.Truncated || stderr.Truncated);
        if (result.ExitCode != 0)
        {
            throw new ModrinthLoaderBootstrapProcessException(result);
        }

        return result;
    }

    private static Task<BoundedCapturedStream> CaptureLinesAsync(
        TextReader reader,
        bool isError,
        IProgress<ModrinthLoaderBootstrapOutputLine>? output)
        => BoundedProcessOutputCapture.CaptureAsync(
            reader,
            line => output?.Report(new ModrinthLoaderBootstrapOutputLine(isError, line)));

    private static TimeSpan ValidateDrainTimeout(TimeSpan value)
        => value > TimeSpan.Zero && value <= TimeSpan.FromMinutes(1)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
}

public sealed class ModrinthLoaderSystemProcessHost : IModrinthLoaderProcessHost
{
    public IModrinthLoaderRunningProcess Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("無法啟動 ModLoader Installer。");
            }

            return new ModrinthLoaderSystemRunningProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

internal sealed class ModrinthLoaderSystemRunningProcess(Process process)
    : IModrinthLoaderRunningProcess
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
