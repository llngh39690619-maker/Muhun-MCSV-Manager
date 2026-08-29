using System.Diagnostics;
using System.Reflection;
using CmlLib.Core;
using CmlLib.Core.ProcessBuilder;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>Builds an interactive user-session Minecraft process. It is never used by the service.</summary>
public sealed class CmlMinecraftClientProcessBuilder : IMinecraftClientProcessBuilder
{
    private static readonly string ProductVersion =
        typeof(CmlMinecraftClientProcessBuilder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+', 2)[0]
        ?? "1.0";

    private readonly WindowsDedicatedGpuPreferenceService _gpuPreferenceService;

    public CmlMinecraftClientProcessBuilder()
        : this(new WindowsDedicatedGpuPreferenceService())
    {
    }

    internal CmlMinecraftClientProcessBuilder(
        WindowsDedicatedGpuPreferenceService gpuPreferenceService)
    {
        _gpuPreferenceService = gpuPreferenceService ??
            throw new ArgumentNullException(nameof(gpuPreferenceService));
    }

    public async Task<Process> BuildAsync(
        MinecraftClientInstance instance,
        AuthenticatedMinecraftSession authenticatedSession,
        MinecraftClientMemoryResolution memory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(authenticatedSession);
        ArgumentNullException.ThrowIfNull(memory);
        ValidateInstance(instance);
        if (instance.EnableDedicatedGpu)
        {
            _ = _gpuPreferenceService.TryApply(instance.JavaExecutablePath);
        }

        var launcher = new MinecraftLauncher(new MinecraftPath(instance.DirectoryPath));
        var options = new MLaunchOption
        {
            Session = authenticatedSession.ToCmlSession(),
            JavaPath = string.IsNullOrWhiteSpace(instance.JavaExecutablePath)
                ? null
                : instance.JavaExecutablePath,
            MinimumRamMb = memory.MinimumMemoryMb,
            MaximumRamMb = memory.MaximumMemoryMb,
            ScreenWidth = instance.WindowWidth,
            ScreenHeight = instance.WindowHeight,
            FullScreen = instance.FullScreen,
            GameLauncherName = "X MCSV",
            GameLauncherVersion = ProductVersion,
            ExtraJvmArguments = instance.JvmArguments.Select(CreateJvmArgument).ToArray(),
        };

        var process = await launcher.BuildProcessAsync(
                instance.InstalledVersionId,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        process.StartInfo.WorkingDirectory = instance.DirectoryPath;
        ConfigureBackgroundLaunch(process.StartInfo);
        foreach (var pair in instance.EnvironmentVariables)
        {
            ValidateEnvironmentVariable(pair.Key, pair.Value);
            process.StartInfo.Environment[pair.Key] = pair.Value;
        }

        return process;
    }

    internal static void ConfigureBackgroundLaunch(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        // Minecraft may still require java.exe (instead of javaw.exe), especially for older
        // releases and user-selected runtimes. CREATE_NO_WINDOW hides only its console without a
        // shell. Keep WindowStyle normal so the later LWJGL game window remains visible, while
        // redirected pipes preserve diagnostics and are drained asynchronously by the session.
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Normal;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
    }

    private static MArgument CreateJvmArgument(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument) || argument.Length > 2_048 ||
            !argument.StartsWith("-", StringComparison.Ordinal) ||
            argument.Contains('\0') || argument.Contains('\r') || argument.Contains('\n'))
        {
            throw new InvalidDataException("A custom JVM argument is invalid.");
        }

        return new MArgument(argument);
    }

    private static void ValidateInstance(MinecraftClientInstance instance)
    {
        if (instance.Edition != MinecraftClientEdition.Java)
        {
            throw new NotSupportedException("Only Java Edition uses the managed Java process builder.");
        }

        if (string.IsNullOrWhiteSpace(instance.DirectoryPath) ||
            !Path.IsPathFullyQualified(instance.DirectoryPath) ||
            !Directory.Exists(instance.DirectoryPath))
        {
            throw new DirectoryNotFoundException("The Minecraft client instance directory does not exist.");
        }

        if (File.GetAttributes(instance.DirectoryPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The Minecraft client instance directory cannot be a reparse point.");
        }

        if (string.IsNullOrWhiteSpace(instance.InstalledVersionId) ||
            instance.InstalledVersionId.Length > 192 ||
            instance.InstalledVersionId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new InvalidDataException("The installed Minecraft launch profile id is invalid.");
        }

        if (!string.IsNullOrWhiteSpace(instance.JavaExecutablePath) &&
            (!Path.IsPathFullyQualified(instance.JavaExecutablePath) || !File.Exists(instance.JavaExecutablePath)))
        {
            throw new FileNotFoundException("The selected Java executable does not exist.", instance.JavaExecutablePath);
        }
    }

    private static void ValidateEnvironmentVariable(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Contains('=') ||
            name.Contains('\0') || value.Length > 4_096 || value.Contains('\0'))
        {
            throw new InvalidDataException("A custom client environment variable is invalid.");
        }
    }
}
