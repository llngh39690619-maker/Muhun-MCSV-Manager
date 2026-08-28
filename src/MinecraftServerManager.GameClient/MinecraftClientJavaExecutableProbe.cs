using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.GameClient;

internal interface IMinecraftClientJavaExecutableProbe
{
    Task<int> ProbeMajorVersionAsync(
        string javaExecutablePath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates a concrete Java executable by running its own bounded <c>-version</c> command.
/// No file-name, directory-name or environment-string heuristic is used to infer the version.
/// </summary>
internal sealed class MinecraftClientJavaExecutableProbe : IMinecraftClientJavaExecutableProbe
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<string, CancellationToken, Task<int>> _readMajorVersionAsync;
    private readonly TimeSpan _timeout;

    public MinecraftClientJavaExecutableProbe()
        : this(AdoptiumRuntimeProvider.ReadJavaMajorVersionAsync, DefaultTimeout)
    {
    }

    internal MinecraftClientJavaExecutableProbe(
        Func<string, CancellationToken, Task<int>> readMajorVersionAsync,
        TimeSpan timeout)
    {
        _readMajorVersionAsync = readMajorVersionAsync
            ?? throw new ArgumentNullException(nameof(readMajorVersionAsync));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _timeout = timeout;
    }

    public async Task<int> ProbeMajorVersionAsync(
        string javaExecutablePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateExecutablePath(javaExecutablePath);

        // Hold a non-write/non-delete sharing lease while the executable is inspected so it
        // cannot be replaced between the regular-file check and the -version process start.
        await using var lease = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var major = await _readMajorVersionAsync(fullPath, timeout.Token).ConfigureAwait(false);
            if (major is < 8 or > 99)
            {
                throw new InvalidDataException(
                    "The selected Java executable reported an unsupported major version.");
            }

            return major;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidDataException(
                $"The selected Java executable did not finish -version validation within " +
                $"{_timeout.TotalSeconds:0.#} seconds. Choose a working Java executable or " +
                "clear the custom path to let X MCSV manage Java automatically.",
                error);
        }
    }

    private static string ValidateExecutablePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The selected Java executable path must be absolute.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var fileName = Path.GetFileName(fullPath);
        if (!string.Equals(fileName, "java.exe", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fileName, "javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The selected Java executable must be java.exe or javaw.exe.",
                nameof(path));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected Java executable does not exist.", fullPath);
        }

        if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                "The selected Java executable cannot be a reparse point.");
        }

        return fullPath;
    }
}

internal static class MinecraftClientJavaCompatibility
{
    private static readonly JavaVersionRecommendationService Recommendations = new();

    public static int GetRequiredMajorVersion(string gameVersion)
        => Recommendations.GetRecommendation(gameVersion, CoreType.Unknown).MajorVersion;

    public static void EnsureMatchesMinecraft(
        string gameVersion,
        int detectedMajorVersion)
    {
        var requiredMajorVersion = GetRequiredMajorVersion(gameVersion);
        if (detectedMajorVersion == requiredMajorVersion)
        {
            return;
        }

        throw new InvalidDataException(
            $"Minecraft {gameVersion} requires Java {requiredMajorVersion}, but the selected " +
            $"executable reports Java {detectedMajorVersion}. Choose Java {requiredMajorVersion}, " +
            "or clear the custom Java path to let X MCSV install the compatible managed runtime.");
    }
}
