using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Runtime;

/// <summary>
/// A shell-free launch description. Providers for installed Forge/NeoForge layouts can replace
/// the default JAR resolver later without changing process orchestration.
/// </summary>
public sealed record ServerLaunchDefinition(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments);

public interface IServerLaunchDefinitionResolver
{
    ServerLaunchDefinition Resolve(ServerInstance instance);
}

/// <summary>
/// Resolves both conventional java -jar servers and installed Forge/NeoForge argument-file
/// layouts. It never invokes cmd.exe, PowerShell, sh, or a source launch script.
/// </summary>
public sealed class JavaJarLaunchDefinitionResolver : IServerLaunchDefinitionResolver
{
    public ServerLaunchDefinition Resolve(ServerInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Id == Guid.Empty)
        {
            throw new ArgumentException("The server instance ID cannot be empty.", nameof(instance));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(instance.DirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.JavaExecutablePath);

        var workingDirectory = Path.GetFullPath(instance.DirectoryPath);
        return instance.LaunchKind switch
        {
            ServerLaunchKind.ExecutableJar => ResolveExecutableJar(instance, workingDirectory),
            ServerLaunchKind.JavaArgumentFiles => ResolveJavaArgumentFiles(instance, workingDirectory),
            _ => throw new ArgumentOutOfRangeException(
                nameof(instance),
                instance.LaunchKind,
                "The server launch kind is unsupported."),
        };
    }

    private static ServerLaunchDefinition ResolveExecutableJar(
        ServerInstance instance,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.ServerJarPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(instance.MinimumMemoryMb, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(instance.MaximumMemoryMb, 1);

        if (instance.MaximumMemoryMb < instance.MinimumMemoryMb)
        {
            throw new ArgumentException(
                "Maximum memory must be greater than or equal to minimum memory.",
                nameof(instance));
        }

        var jarFileName = Path.GetFileName(instance.ServerJarPath);
        if ((instance.CoreType is CoreType.Forge or CoreType.NeoForge)
            && jarFileName.Contains("installer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Forge and NeoForge installer JARs are not runnable server cores. "
                + "Run the installer workflow first, then launch its generated argument files.");
        }

        if (!string.Equals(
                Path.GetExtension(jarFileName),
                ".jar",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The selected server core must be a JAR file.", nameof(instance));
        }

        string jarPath;
        try
        {
            jarPath = SafePath.EnsureWithinRoot(workingDirectory, instance.ServerJarPath);
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException)
        {
            throw new ArgumentException(
                "The selected server core JAR must remain inside the server directory.",
                nameof(instance),
                error);
        }

        if (!File.Exists(jarPath))
        {
            throw new FileNotFoundException("The selected server core JAR was not found.", jarPath);
        }

        try
        {
            jarPath = SafePath.EnsureNoReparsePointsUnderRoot(workingDirectory, jarPath);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new ArgumentException(
                "The selected server core JAR path contains a reparse point.",
                nameof(instance),
                error);
        }

        // Java's Windows instrumentation launcher can fail when a launcher-agent JAR is supplied
        // through a non-ASCII absolute path. The working directory already identifies the safe,
        // canonical root, so pass the root-confined relative spelling to Java. ArgumentList still
        // performs the platform-specific quoting for nested paths and spaces without a shell.
        var relativeJarPath = Path.GetRelativePath(workingDirectory, jarPath);
        var arguments = new List<string>
        {
            $"-Xms{instance.MinimumMemoryMb}M",
            $"-Xmx{instance.MaximumMemoryMb}M",
        };

        foreach (var argument in instance.JvmArguments.ToArray())
        {
            AddValidatedArgument(arguments, argument, nameof(instance.JvmArguments));
        }

        arguments.Add("-jar");
        arguments.Add(relativeJarPath);

        AppendServerArgumentsAndEnsureHeadless(arguments, instance);

        return new ServerLaunchDefinition(
            instance.JavaExecutablePath!,
            workingDirectory,
            arguments);
    }

    private static ServerLaunchDefinition ResolveJavaArgumentFiles(
        ServerInstance instance,
        string workingDirectory)
    {
        if (instance.JavaArgumentFilePaths is null || instance.JavaArgumentFilePaths.Count == 0)
        {
            throw new ArgumentException(
                "At least one Java argument file is required for this launch mode.",
                nameof(instance));
        }

        var arguments = new List<string>(
            instance.JavaArgumentFilePaths.Count + (instance.ServerArguments?.Count ?? 0));
        foreach (var relativePath in instance.JavaArgumentFilePaths.ToArray())
        {
            ValidateArgumentFilePath(relativePath);

            string fullPath;
            try
            {
                fullPath = SafePath.EnsureWithinRoot(workingDirectory, NormalizeForFileSystem(relativePath));
            }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException)
            {
                throw new ArgumentException(
                    $"Java argument file must remain inside the server directory: {relativePath}",
                    nameof(instance),
                    error);
            }

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("A Java argument file was not found.", fullPath);
            }

            try
            {
                SafePath.EnsureNoReparsePointsUnderRoot(workingDirectory, fullPath);
            }
            catch (UnauthorizedAccessException error)
            {
                throw new ArgumentException(
                    $"Java argument file path contains a reparse point: {relativePath}",
                    nameof(instance),
                    error);
            }

            // Preserve the stored relative spelling. ProcessStartInfo.ArgumentList handles spaces,
            // and Java resolves the @file from WorkingDirectory.
            arguments.Add("@" + relativePath);
        }

        AppendServerArgumentsAndEnsureHeadless(arguments, instance);

        return new ServerLaunchDefinition(
            instance.JavaExecutablePath!,
            workingDirectory,
            arguments);
    }

    private static void ValidateArgumentFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Java argument-file paths cannot be blank.");
        }

        if (path[0] == '@')
        {
            throw new ArgumentException("Java argument-file paths must not include the leading '@'.");
        }

        if (path.Contains('\0') || path.Contains('\r') || path.Contains('\n'))
        {
            throw new ArgumentException("Java argument-file paths cannot contain control characters.");
        }

        var normalized = NormalizeForFileSystem(path);
        if (Path.IsPathRooted(normalized)
            || normalized.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                "Java argument-file paths must be root-confined relative paths.");
        }
    }

    private static string NormalizeForFileSystem(string path) => path
        .Replace('\\', Path.DirectorySeparatorChar)
        .Replace('/', Path.DirectorySeparatorChar);

    private static void AppendServerArgumentsAndEnsureHeadless(
        ICollection<string> destination,
        ServerInstance instance)
    {
        var hasHeadlessArgument = false;
        foreach (var argument in (instance.ServerArguments ?? []).ToArray())
        {
            AddValidatedArgument(destination, argument, nameof(instance.ServerArguments));
            hasHeadlessArgument |= string.Equals(
                argument,
                "nogui",
                StringComparison.OrdinalIgnoreCase);
        }

        if (RequiresHeadlessServerArgument(instance.CoreType)
            && !hasHeadlessArgument)
        {
            // CreateNoWindow only suppresses an operating-system console window; it does not
            // disable Minecraft's own AWT/Swing server GUI. Keep this application argument after
            // -jar <server.jar>, or after every JVM @argument-file, so it can never become a JVM
            // option or alter an installer-verified argument file. Official Forge/NeoForge run
            // scripts deliberately end in %* / $@, so their persisted ServerArguments can be
            // empty even though the manager must launch the dedicated server headlessly.
            destination.Add("nogui");
        }
    }

    private static bool RequiresHeadlessServerArgument(CoreType coreType) => coreType is
        CoreType.Vanilla
        or CoreType.Paper
        or CoreType.Purpur
        or CoreType.Folia
        or CoreType.Spigot
        or CoreType.CraftBukkit
        or CoreType.Fabric
        or CoreType.Forge
        or CoreType.NeoForge
        or CoreType.Mohist
        or CoreType.Arclight
        or CoreType.CatServer
        or CoreType.Akarin;

    private static void AddValidatedArgument(
        ICollection<string> destination,
        string argument,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new ArgumentException("Launch arguments cannot be blank.", parameterName);
        }

        if (argument.Contains('\0') || argument.Contains('\r') || argument.Contains('\n'))
        {
            throw new ArgumentException("Launch arguments cannot contain control characters.", parameterName);
        }

        destination.Add(argument);
    }
}
