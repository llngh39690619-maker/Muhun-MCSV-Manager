using System.Diagnostics;

namespace MinecraftServerManager.Core.Tests;

internal static class ReparsePointTestHelper
{
    public static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        linkPath = Path.GetFullPath(linkPath);
        targetPath = Path.GetFullPath(targetPath);

        if (OperatingSystem.IsWindows())
        {
            if (linkPath.Contains('"') || targetPath.Contains('"'))
            {
                throw new ArgumentException("Test junction paths cannot contain quote characters.");
            }

            using var process = Process.Start(new ProcessStartInfo(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                Arguments = $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Could not start cmd.exe to create a test junction.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Could not create test junction (exit {process.ExitCode}): "
                    + standardError + standardOutput);
            }
        }
        else
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }

        if (!File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The test link was not reported as a reparse point.");
        }
    }
}
