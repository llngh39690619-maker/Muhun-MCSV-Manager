using System;
using System.IO;
using System.Threading;

namespace MinecraftServerManager.TestInfrastructure;

internal static class TestRepositoryPaths
{
    private const string SolutionFileName = "MinecraftServerManager.sln";
    private static readonly Lazy<string> RepositoryRootValue = new(
        FindRepositoryRoot,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static string RepositoryRoot => RepositoryRootValue.Value;

    public static string AppSource(params string[] relativeSegments)
        => SourceProject("MinecraftServerManager.App", relativeSegments);

    public static string CoreSource(params string[] relativeSegments)
        => SourceProject("MinecraftServerManager.Core", relativeSegments);

    public static string RemoteSource(params string[] relativeSegments)
        => SourceProject("MinecraftServerManager.Remote", relativeSegments);

    public static string SourceProject(
        string projectName,
        params string[] relativeSegments)
        => FromRepositoryRoot(
            ["src", projectName, .. relativeSegments]);

    public static string FromRepositoryRoot(params string[] relativeSegments)
    {
        var pathSegments = new string[relativeSegments.Length + 1];
        pathSegments[0] = RepositoryRoot;
        relativeSegments.CopyTo(pathSegments, 1);
        return Path.GetFullPath(Path.Combine(pathSegments));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {SolutionFileName} from test base directory " +
            $"'{AppContext.BaseDirectory}'.");
    }
}
