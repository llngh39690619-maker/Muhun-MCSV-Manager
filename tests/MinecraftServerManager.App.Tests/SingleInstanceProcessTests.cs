using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class SingleInstanceProcessTests
{
    [Fact]
    public async Task SecondAppProcessFromSameDirectory_IsRejectedWhileOwnerIsAlive()
    {
        var processDirectory = Path.Combine(
            Path.GetTempPath(),
            $"mcsv-single-process-app-{Guid.NewGuid():N}");
        Directory.CreateDirectory(processDirectory);
        CopyApplicationFiles(AppContext.BaseDirectory, processDirectory);
        var appExecutable = Path.Combine(
            processDirectory,
            "Muhun MCSV Manager.exe");
        var signalDirectory = Path.Combine(
            Path.GetTempPath(),
            $"mcsv-single-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(signalDirectory);
        var readyPath = Path.Combine(signalDirectory, "ready.signal");
        var releasePath = Path.Combine(signalDirectory, "release.signal");
        Process? owner = null;
        try
        {
            owner = StartApp(
                appExecutable,
                "--single-instance-hold-test",
                readyPath,
                releasePath);
            // A newly produced apphost can take longer on its first launch while
            // Windows performs runtime loading and security scanning. This remains
            // a bounded wait, but avoids turning a busy machine into a false failure.
            await WaitForFileAsync(readyPath, owner, TimeSpan.FromSeconds(30));

            using var duplicate = StartApp(appExecutable, "--smoke-test");
            await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(3, duplicate.ExitCode);

            await File.WriteAllTextAsync(releasePath, "release");
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, owner.ExitCode);
            Assert.False(File.Exists(Path.Combine(
                Path.GetDirectoryName(appExecutable)!,
                SingleInstanceGuard.LockFileName)));
        }
        finally
        {
            try
            {
                if (owner is { HasExited: false })
                {
                    owner.Kill(entireProcessTree: true);
                    owner.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
            catch (InvalidOperationException)
            {
            }

            owner?.Dispose();
            try
            {
                Directory.Delete(signalDirectory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }

            try
            {
                Directory.Delete(processDirectory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static void CopyApplicationFiles(string sourceDirectory, string destinationDirectory)
    {
        string[] fileNames =
        [
            "Muhun MCSV Manager.exe",
            "Muhun MCSV Manager.dll",
            "Muhun MCSV Manager.deps.json",
            "Muhun MCSV Manager.runtimeconfig.json",
            "MinecraftServerManager.Contracts.dll",
            "MinecraftServerManager.Core.dll",
            "MinecraftServerManager.Client.dll",
            "MinecraftServerManager.Remote.dll",
            "MailKit.dll",
            "MimeKit.dll",
            "BouncyCastle.Cryptography.dll"
        ];

        foreach (var fileName in fileNames)
        {
            var sourcePath = Path.Combine(sourceDirectory, fileName);
            Assert.True(File.Exists(sourcePath), $"Missing application file: {fileName}");
            File.Copy(sourcePath, Path.Combine(destinationDirectory, fileName));
        }
    }

    private static Process StartApp(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The released product is self-contained, while this test launches the
        // framework-dependent build output. Pin the child to the exact SDK/runtime
        // hosting the test instead of falling back to an older machine-wide .NET.
        var dotnetRoot = Path.GetFullPath(Path.Combine(
            RuntimeEnvironment.GetRuntimeDirectory(),
            "..",
            "..",
            ".."));
        Assert.True(File.Exists(Path.Combine(dotnetRoot, "dotnet.exe")));
        startInfo.Environment["DOTNET_ROOT"] = dotnetRoot;
        startInfo.Environment["DOTNET_ROOT_X64"] = dotnetRoot;

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("無法啟動單一實例跨程序診斷。");
    }

    private static async Task WaitForFileAsync(string path, Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"單一實例診斷在建立 ready signal 前結束（Exit Code {process.ExitCode}）。" +
                    $" Standard output: {stdout} Standard error: {stderr}");
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("等候單一實例診斷 ready signal 逾時。");
            }

            await Task.Delay(25);
        }
    }
}
