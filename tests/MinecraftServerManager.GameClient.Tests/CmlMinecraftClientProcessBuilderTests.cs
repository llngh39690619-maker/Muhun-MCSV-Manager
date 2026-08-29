using System.Diagnostics;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class CmlMinecraftClientProcessBuilderTests
{
    [Fact]
    public void ConfigureBackgroundLaunch_HidesConsoleWithoutLosingDiagnosticPipes()
    {
        var startInfo = new ProcessStartInfo("java.exe")
        {
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        CmlMinecraftClientProcessBuilder.ConfigureBackgroundLaunch(startInfo);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Normal, startInfo.WindowStyle);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public async Task ConfigureBackgroundLaunch_RealChildHasNoWindowAndBothPipesRemainReadable()
    {
        var commandInterpreter = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new ProcessStartInfo(commandInterpreter)
        {
            Arguments = "/d /s /c \"echo standard-output & echo standard-error 1>&2\"",
        };
        CmlMinecraftClientProcessBuilder.ConfigureBackgroundLaunch(startInfo);

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        process.Refresh();
        Assert.Equal(IntPtr.Zero, process.MainWindowHandle);

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("standard-output", await standardOutput, StringComparison.Ordinal);
        Assert.Contains("standard-error", await standardError, StringComparison.Ordinal);
    }
}
