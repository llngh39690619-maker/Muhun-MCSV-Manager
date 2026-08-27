using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Models;

public enum ModrinthLoaderArtifactKind
{
    MinecraftServer,
    FabricInstaller,
    ForgeInstaller,
    NeoForgeInstaller
}

public sealed record ModrinthLoaderArtifact(
    ModrinthLoaderArtifactKind Kind,
    string FilePath,
    Uri Source,
    long Size,
    string HashAlgorithm,
    string Hash);

public sealed record ModrinthLoaderBootstrapOutputLine(bool IsError, string Text);

public sealed record ModrinthLoaderBootstrapProcessResult(
    int ExitCode,
    IReadOnlyList<string> StandardOutput,
    IReadOnlyList<string> StandardError,
    bool OutputTruncated = false);

public sealed record ModrinthLoaderBootstrapProgress(
    string Phase,
    double? Fraction = null,
    string? Detail = null);

/// <summary>
/// Identifies the exact, statically validated launch layout emitted by a verified first-party
/// loader installer. This is deliberately separate from the online-pack validator: third-party
/// packs cannot manufacture this provenance merely by copying a similarly named wrapper.
/// </summary>
public enum OfficialLoaderLaunchLayout
{
    FabricManifestLauncher,
    StandardArgumentFiles,
    NeoForgeDirectMainClass,
    ForgeInstallerEmbeddedShim
}

public sealed record VerifiedInstallFileFingerprint(
    string RelativePath,
    long Length,
    string Sha256);

public sealed record OfficialLoaderInstallProvenance(
    ModrinthModpackLoaderKind Kind,
    string MinecraftVersion,
    string LoaderVersion,
    OfficialLoaderLaunchLayout LaunchLayout,
    IReadOnlyList<VerifiedInstallFileFingerprint> Files);

public sealed record ModrinthLoaderBootstrapResult(
    ModrinthModpackLoaderKind Kind,
    string MinecraftVersion,
    string? LoaderVersion,
    string StagingDirectory,
    IReadOnlyList<string> InstalledPaths,
    IReadOnlyList<string> LaunchCandidates,
    ModrinthLoaderBootstrapProcessResult? ProcessResult,
    OfficialLoaderInstallProvenance? Provenance = null);

public sealed class ModrinthLoaderUnsupportedException : NotSupportedException
{
    public ModrinthLoaderUnsupportedException(
        ModrinthModpackLoaderKind kind,
        string reason)
        : base($"目前無法安全安裝 {kind} Server Loader：{reason}")
    {
        Kind = kind;
    }

    public ModrinthModpackLoaderKind Kind { get; }
}
