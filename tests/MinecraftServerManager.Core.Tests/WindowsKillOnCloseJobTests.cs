using System.Diagnostics;
using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.Core.Tests;

public sealed class WindowsKillOnCloseJobTests
{
    [Fact]
    public async Task Dispose_TerminatesAssignedProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pingPath = Path.Combine(Environment.SystemDirectory, "ping.exe");
        Assert.True(File.Exists(pingPath), $"Missing harmless Windows test child: {pingPath}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pingPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            },
        };
        process.StartInfo.ArgumentList.Add("127.0.0.1");
        process.StartInfo.ArgumentList.Add("-t");

        WindowsKillOnCloseJob? job = null;
        try
        {
            Assert.True(process.Start());
            job = WindowsKillOnCloseJob.CreateAndAssign(process);
            Assert.NotNull(job);
            Assert.False(process.HasExited);

            job.Dispose();
            job = null;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.HasExited);
        }
        finally
        {
            job?.Dispose();
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
