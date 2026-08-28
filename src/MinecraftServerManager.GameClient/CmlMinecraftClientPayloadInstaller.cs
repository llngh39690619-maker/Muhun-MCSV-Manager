using CmlLib.Core;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Installs a Java client into an isolated staging directory. The caller owns promotion of the
/// completed directory and registry mutation, so a cancelled or failed install is never visible.
/// </summary>
public sealed class CmlMinecraftClientPayloadInstaller : IMinecraftClientPayloadInstaller
{
    private readonly HttpClient _httpClient;
    private readonly OfficialMavenClientLoaderInstaller _officialMavenLoaderInstaller;

    public CmlMinecraftClientPayloadInstaller(HttpClient httpClient)
        : this(httpClient, new OfficialMavenClientLoaderInstaller())
    {
    }

    internal CmlMinecraftClientPayloadInstaller(
        HttpClient httpClient,
        OfficialMavenClientLoaderInstaller officialMavenLoaderInstaller)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _officialMavenLoaderInstaller = officialMavenLoaderInstaller ??
            throw new ArgumentNullException(nameof(officialMavenLoaderInstaller));
    }

    public async Task<string> InstallAsync(
        MinecraftClientInstallRequest request,
        string stagingDirectory,
        string? javaExecutablePath,
        IProgress<MinecraftClientInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        if (request.Edition != MinecraftClientEdition.Java)
        {
            throw new NotSupportedException("Only Minecraft Java Edition can be installed by the managed client installer.");
        }

        if (request.Loader is MinecraftClientLoader.OptiFine or MinecraftClientLoader.LabyMod)
        {
            throw new NotSupportedException(
                $"{request.Loader} requires its official external installer and cannot be silently downloaded.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(stagingDirectory);
        var path = new MinecraftPath(stagingDirectory);
        var launcher = new MinecraftLauncher(path);
        var fileProgress = new Progress<CmlLib.Core.Installers.InstallerProgressChangedEventArgs>(value =>
        {
            double? fraction = value.TotalTasks > 0
                ? Math.Clamp((double)value.ProgressedTasks / value.TotalTasks, 0d, 1d)
                : null;
            progress?.Report(new MinecraftClientInstallProgress(
                "download",
                string.IsNullOrWhiteSpace(value.Name) ? "正在下載並驗證遊戲檔案…" : value.Name,
                fraction));
        });
        var byteProgress = new Progress<ByteProgress>(value =>
        {
            double? fraction = value.TotalBytes > 0
                ? Math.Clamp((double)value.ProgressedBytes / value.TotalBytes, 0d, 1d)
                : null;
            progress?.Report(new MinecraftClientInstallProgress("download", "正在下載並驗證遊戲檔案…", fraction));
        });

        progress?.Report(new MinecraftClientInstallProgress("base", $"正在安裝 Minecraft {request.GameVersion}…", 0d));
        string installedVersionId;
        switch (request.Loader)
        {
            case MinecraftClientLoader.Vanilla:
                await launcher.InstallAsync(
                        request.GameVersion,
                        fileProgress,
                        byteProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
                installedVersionId = request.GameVersion;
                break;

            case MinecraftClientLoader.Fabric:
                await InstallBaseAsync(launcher, request.GameVersion, fileProgress, byteProgress, cancellationToken)
                    .ConfigureAwait(false);
                installedVersionId = await new FabricInstaller(_httpClient).Install(
                        request.GameVersion,
                        RequireLoaderVersion(request),
                        path)
                    .ConfigureAwait(false);
                await launcher.InstallAsync(installedVersionId, fileProgress, byteProgress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case MinecraftClientLoader.Quilt:
                await InstallBaseAsync(launcher, request.GameVersion, fileProgress, byteProgress, cancellationToken)
                    .ConfigureAwait(false);
                installedVersionId = await new QuiltInstaller(_httpClient).Install(
                        request.GameVersion,
                        RequireLoaderVersion(request),
                        path)
                    .ConfigureAwait(false);
                await launcher.InstallAsync(installedVersionId, fileProgress, byteProgress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case MinecraftClientLoader.Forge:
            case MinecraftClientLoader.NeoForge:
                await InstallBaseAsync(launcher, request.GameVersion, fileProgress, byteProgress, cancellationToken)
                    .ConfigureAwait(false);
                installedVersionId = await _officialMavenLoaderInstaller.InstallAsync(
                        request.Loader,
                        request.GameVersion,
                        RequireLoaderVersion(request),
                        stagingDirectory,
                        RequireJavaPath(javaExecutablePath),
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                await launcher.InstallAsync(installedVersionId, fileProgress, byteProgress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                throw new NotSupportedException($"Unsupported Minecraft client loader: {request.Loader}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(installedVersionId) || installedVersionId.Length > 192)
        {
            throw new InvalidDataException("The client installer returned an invalid launch profile id.");
        }

        progress?.Report(new MinecraftClientInstallProgress("verify", "客戶端檔案已安裝並驗證。", 1d));
        return installedVersionId;
    }

    private static async Task InstallBaseAsync(
        MinecraftLauncher launcher,
        string gameVersion,
        IProgress<CmlLib.Core.Installers.InstallerProgressChangedEventArgs> fileProgress,
        IProgress<ByteProgress> byteProgress,
        CancellationToken cancellationToken)
    {
        await launcher.InstallAsync(gameVersion, fileProgress, byteProgress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string RequireLoaderVersion(MinecraftClientInstallRequest request) =>
        string.IsNullOrWhiteSpace(request.LoaderVersion) || request.LoaderVersion.Length > 128
            ? throw new InvalidOperationException($"{request.Loader} requires a selected compatible loader version.")
            : request.LoaderVersion;

    private static string RequireJavaPath(string? javaExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(javaExecutablePath) ||
            !Path.IsPathFullyQualified(javaExecutablePath) ||
            !File.Exists(javaExecutablePath))
        {
            throw new FileNotFoundException("Forge/NeoForge installation requires a valid Java executable.");
        }

        return javaExecutablePath;
    }
}
