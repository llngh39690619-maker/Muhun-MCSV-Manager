using System.Diagnostics;

namespace MinecraftServerManager.Core.Providers;

/// <summary>
/// Builds the minimal environment shared by manager-owned Java subprocesses. In particular, this
/// prevents ambient JVM launch options, Git, Maven, or Gradle settings from changing a verified
/// executable's working directory or loading code into it.
/// </summary>
internal static class ManagedJavaProcessEnvironment
{
    internal static void Configure(
        ProcessStartInfo startInfo,
        string javaExecutablePath,
        string? privateHomeDirectory = null,
        string? privateTempDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(javaExecutablePath);
        if ((privateHomeDirectory is null) != (privateTempDirectory is null))
        {
            throw new ArgumentException(
                "Private Java HOME 與 TEMP 必須同時提供或同時省略。");
        }

        var java = Path.GetFullPath(javaExecutablePath);
        var javaBin = Path.GetDirectoryName(java)
            ?? throw new InvalidDataException("Java executable 缺少 bin 目錄。");
        var javaHome = Directory.GetParent(javaBin)?.FullName
            ?? throw new InvalidDataException("Java executable 缺少 runtime 根目錄。");
        var trustedPath = new List<string> { javaBin };

        startInfo.Environment.Clear();
        startInfo.Environment["JAVA_HOME"] = javaHome;

        if (OperatingSystem.IsWindows())
        {
            var systemDirectory = Environment.SystemDirectory;
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(systemDirectory)
                || string.IsNullOrWhiteSpace(windowsDirectory))
            {
                throw new InvalidOperationException("無法解析受控 Java 子程序所需的 Windows 目錄。");
            }

            systemDirectory = Path.GetFullPath(systemDirectory);
            windowsDirectory = Path.GetFullPath(windowsDirectory);
            var commandInterpreter = Path.Combine(systemDirectory, "cmd.exe");
            if (!File.Exists(commandInterpreter))
            {
                throw new FileNotFoundException(
                    "找不到受控 Java 子程序所需的 Windows command interpreter。",
                    commandInterpreter);
            }

            trustedPath.Add(systemDirectory);
            startInfo.Environment["COMSPEC"] = commandInterpreter;
            startInfo.Environment["SystemRoot"] = windowsDirectory;
        }

        startInfo.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            trustedPath
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase));

        if (privateHomeDirectory is not null && privateTempDirectory is not null)
        {
            var privateHome = RequirePrivateDirectory(privateHomeDirectory, "Java private HOME");
            var privateTemp = RequirePrivateDirectory(privateTempDirectory, "Java private TEMP");
            startInfo.Environment["HOME"] = privateHome;
            startInfo.Environment["USERPROFILE"] = privateHome;
            startInfo.Environment["TEMP"] = privateTemp;
            startInfo.Environment["TMP"] = privateTemp;
        }
    }

    private static string RequirePrivateDirectory(string path, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = new DirectoryInfo(fullPath);
        directory.Refresh();
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"{context} 不存在：{fullPath}");
        }

        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{context} 不得是 reparse point：{fullPath}");
        }

        return directory.FullName;
    }
}
