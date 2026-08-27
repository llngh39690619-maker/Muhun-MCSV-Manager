using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

internal sealed record CoreServerJavaDevelopmentKit(
    string JavaExecutablePath,
    string JavacExecutablePath);

internal interface ICoreServerJdkResolver
{
    Task<CoreServerJavaDevelopmentKit> ResolveAsync(
        int majorVersion,
        IProgress<double>? downloadProgress,
        CancellationToken cancellationToken);
}

internal sealed class ManagedCoreServerJdkResolver(
    ApplicationPaths paths,
    AdoptiumRuntimeProvider provider) : ICoreServerJdkResolver
{
    private readonly ApplicationPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly AdoptiumRuntimeProvider _provider = provider
        ?? throw new ArgumentNullException(nameof(provider));

    public async Task<CoreServerJavaDevelopmentKit> ResolveAsync(
        int majorVersion,
        IProgress<double>? downloadProgress,
        CancellationToken cancellationToken)
    {
        if (majorVersion is < 8 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(majorVersion));
        }

        Directory.CreateDirectory(_paths.Runtimes);
        SafePath.EnsureNoReparsePointsUnderRoot(_paths.Runtimes, _paths.Runtimes);
        var inspected = 0;
        foreach (var directory in Directory.EnumerateDirectories(
                     _paths.Runtimes,
                     $"temurin-jdk-{majorVersion}-*",
                     SearchOption.TopDirectoryOnly)
                 .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inspected++ >= 64)
            {
                break;
            }

            var java = Path.Combine(directory, "bin", "java.exe");
            var javac = Path.Combine(directory, "bin", "javac.exe");
            if (!File.Exists(java) || !File.Exists(javac))
            {
                continue;
            }

            try
            {
                java = SafePath.EnsureNoReparsePointsUnderRoot(_paths.Runtimes, java);
                javac = SafePath.EnsureNoReparsePointsUnderRoot(_paths.Runtimes, javac);
                if (await AdoptiumRuntimeProvider.ReadJavaMajorVersionAsync(java, cancellationToken)
                        .ConfigureAwait(false) == majorVersion
                    && await AdoptiumRuntimeProvider.ReadJavacMajorVersionAsync(javac, cancellationToken)
                        .ConfigureAwait(false) == majorVersion)
                {
                    return new CoreServerJavaDevelopmentKit(java, javac);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                                               or InvalidDataException
                                               or UnauthorizedAccessException
                                               or System.ComponentModel.Win32Exception)
            {
                // Ignore incomplete or modified candidates. The provider installs and verifies a
                // fresh JDK below without using a user-controlled shell or runtime.
            }
        }

        var installed = await _provider.InstallJdkAsync(
                majorVersion,
                _paths.Runtimes,
                downloadProgress,
                cancellationToken)
            .ConfigureAwait(false);
        if (installed.MajorVersion != majorVersion)
        {
            throw new InvalidDataException($"JDK 安裝結果版本錯誤，預期 Java {majorVersion}。");
        }

        var installedJava = SafePath.EnsureNoReparsePointsUnderRoot(
            _paths.Runtimes,
            installed.JavaExecutablePath);
        var installedJavac = SafePath.EnsureNoReparsePointsUnderRoot(
            _paths.Runtimes,
            installed.JavacExecutablePath);
        ValidateRegularFile(installedJava, "JDK java");
        ValidateRegularFile(installedJavac, "JDK javac");
        return new CoreServerJavaDevelopmentKit(installedJava, installedJavac);
    }

    private static void ValidateRegularFile(string path, string context)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"{context} 不存在。");
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || new FileInfo(path).Length < 1)
        {
            throw new InvalidDataException($"{context} 不是可信的一般檔案。");
        }
    }
}
