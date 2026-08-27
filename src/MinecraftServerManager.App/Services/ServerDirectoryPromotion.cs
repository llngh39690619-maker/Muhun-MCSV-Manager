using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Atomically promotes a manager-owned staging directory to the next available Server name.
/// The name proposal is inherently racy, so destination collisions are retried while ownership
/// of the unchanged source directory remains with the caller.
/// </summary>
internal static class ServerDirectoryPromotion
{
    private const int MaximumDestinationAttempts = 1_000;

    public static string PromoteToUniqueDirectory(
        string serversRoot,
        string stagingDirectory,
        string preferredName)
        => PromoteToUniqueDirectory(
            serversRoot,
            stagingDirectory,
            preferredName,
            afterCandidateProposedForTesting: null);

    internal static string PromoteToUniqueDirectory(
        string serversRoot,
        string stagingDirectory,
        string preferredName,
        Action<string>? afterCandidateProposedForTesting)
    {
        var source = SafePath.EnsureWithinRoot(serversRoot, stagingDirectory, allowRoot: false);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        for (var attempt = 0; attempt < MaximumDestinationAttempts; attempt++)
        {
            var destination = SafePath.CreateUniqueDirectoryPath(serversRoot, preferredName);
            afterCandidateProposedForTesting?.Invoke(destination);
            try
            {
                Directory.Move(source, destination);
                return destination;
            }
            catch (IOException) when (
                Directory.Exists(source)
                && (Directory.Exists(destination) || File.Exists(destination)))
            {
                // Only a destination collision is retried. Other IO failures preserve their
                // original exception and the source remains caller-owned for safe cleanup.
            }
        }

        throw new IOException(
            "無法為已完成的 Server 保留唯一目的地；同名目的地變更過於頻繁。");
    }
}
