using CmlLib.Core;
using CmlLib.Core.Installers;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

internal delegate ValueTask CmlLauncherInstallPhase(
    MinecraftPath path,
    string versionId,
    IProgress<InstallerProgressChangedEventArgs> fileProgress,
    IProgress<ByteProgress> byteProgress,
    CancellationToken cancellationToken);

internal delegate Task<string> CmlLoaderProfileInstall(
    MinecraftClientLoader loader,
    string gameVersion,
    string loaderVersion,
    MinecraftPath path);

/// <summary>
/// Installs a Java client into an isolated staging directory. The caller owns promotion of the
/// completed directory and registry mutation, so a cancelled or failed install is never visible.
/// </summary>
public sealed class CmlMinecraftClientPayloadInstaller : IMinecraftClientPayloadInstaller
{
    private readonly HttpClient _httpClient;
    private readonly OfficialMavenClientLoaderInstaller _officialMavenLoaderInstaller;
    private readonly CmlDownloadReliabilityOptions _reliabilityOptions;
    private readonly CmlLauncherInstallPhase _launcherInstallPhase;
    private readonly CmlLoaderProfileInstall _loaderProfileInstall;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public CmlMinecraftClientPayloadInstaller(HttpClient httpClient)
        : this(httpClient, new OfficialMavenClientLoaderInstaller())
    {
    }

    internal CmlMinecraftClientPayloadInstaller(
        HttpClient httpClient,
        OfficialMavenClientLoaderInstaller officialMavenLoaderInstaller)
        : this(
            httpClient,
            officialMavenLoaderInstaller,
            CmlDownloadReliabilityOptions.Default,
            launcherInstallPhase: null,
            delayAsync: null,
            loaderProfileInstall: null)
    {
    }

    internal CmlMinecraftClientPayloadInstaller(
        HttpClient httpClient,
        OfficialMavenClientLoaderInstaller officialMavenLoaderInstaller,
        CmlDownloadReliabilityOptions reliabilityOptions,
        CmlLauncherInstallPhase? launcherInstallPhase,
        Func<TimeSpan, CancellationToken, Task>? delayAsync,
        CmlLoaderProfileInstall? loaderProfileInstall = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _officialMavenLoaderInstaller = officialMavenLoaderInstaller ??
            throw new ArgumentNullException(nameof(officialMavenLoaderInstaller));
        _reliabilityOptions = (reliabilityOptions ??
            throw new ArgumentNullException(nameof(reliabilityOptions))).Validate();
        _launcherInstallPhase = launcherInstallPhase ?? InstallLauncherOnceAsync;
        _loaderProfileInstall = loaderProfileInstall ?? InstallLoaderProfileOnceAsync;
        _delayAsync = delayAsync ?? Task.Delay;
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
        var fileProgress = new Progress<InstallerProgressChangedEventArgs>(value =>
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
                await InstallLauncherPhaseWithRetryAsync(
                        path,
                        request.GameVersion,
                        fileProgress,
                        byteProgress,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                installedVersionId = request.GameVersion;
                break;

            case MinecraftClientLoader.Fabric:
                await InstallLauncherPhaseWithRetryAsync(
                        path,
                        request.GameVersion,
                        fileProgress,
                        byteProgress,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                installedVersionId = await InstallLoaderProfileWithRetryAsync(
                        MinecraftClientLoader.Fabric,
                        request.GameVersion,
                        RequireLoaderVersion(request),
                        path,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                await InstallLauncherPhaseWithRetryAsync(
                        path,
                        installedVersionId,
                        fileProgress,
                        byteProgress,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;

            case MinecraftClientLoader.Quilt:
                await InstallLauncherPhaseWithRetryAsync(
                        path,
                        request.GameVersion,
                        fileProgress,
                        byteProgress,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                installedVersionId = await InstallLoaderProfileWithRetryAsync(
                        MinecraftClientLoader.Quilt,
                        request.GameVersion,
                        RequireLoaderVersion(request),
                        path,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                await InstallLauncherPhaseWithRetryAsync(
                        path,
                        installedVersionId,
                        fileProgress,
                        byteProgress,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;

            case MinecraftClientLoader.Forge:
            case MinecraftClientLoader.NeoForge:
                await InstallLauncherPhaseWithRetryAsync(
                        path,
                        request.GameVersion,
                        fileProgress,
                        byteProgress,
                        progress,
                        cancellationToken)
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
                await InstallLauncherPhaseWithRetryAsync(
                        path,
                        installedVersionId,
                        fileProgress,
                        byteProgress,
                        progress,
                        cancellationToken)
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

    private async Task InstallLauncherPhaseWithRetryAsync(
        MinecraftPath path,
        string versionId,
        IProgress<InstallerProgressChangedEventArgs> fileProgress,
        IProgress<ByteProgress> byteProgress,
        IProgress<MinecraftClientInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _reliabilityOptions.MaximumPhaseAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Each attempt deliberately builds a fresh launcher and a fresh CmlLib installer.
                // CmlLib's parallel installer owns mutable progress/dataflow state and must not be
                // reused after a fault. Rebuilding also retries manifest and version metadata I/O.
                await _launcherInstallPhase(
                        path,
                        versionId,
                        fileProgress,
                        byteProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ExceptionGraphSafety.RethrowOutOfMemory(exception);
                cancellationToken.ThrowIfCancellationRequested();
                if (FindDownloadException(exception) is { } downloadFailure)
                {
                    throw downloadFailure;
                }

                if (attempt >= _reliabilityOptions.MaximumPhaseAttempts ||
                    !CmlDownloadRetryPolicy.IsRetryable(exception, cancellationToken))
                {
                    throw new MinecraftClientDownloadException(
                        attempt,
                        host: null,
                        CmlDownloadRetryPolicy.GetHttpStatusCode(exception),
                        CmlDownloadRetryPolicy.GetFailureKind(exception),
                        "launcher-metadata",
                        exception);
                }

                progress?.Report(new MinecraftClientInstallProgress(
                    "retry",
                    $"下載連線暫時失敗，正在進行第 {attempt + 1} 次嘗試…"));
                await _delayAsync(
                        _reliabilityOptions.GetDelayAfterAttempt(attempt),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async ValueTask InstallLauncherOnceAsync(
        MinecraftPath path,
        string versionId,
        IProgress<InstallerProgressChangedEventArgs> fileProgress,
        IProgress<ByteProgress> byteProgress,
        CancellationToken cancellationToken)
    {
        var parameters = MinecraftLauncherParameters.CreateDefault(path, _httpClient);
        parameters.GameInstaller = new AtomicRetryingParallelGameInstaller(
            _httpClient,
            _reliabilityOptions,
            _delayAsync);
        var launcher = new MinecraftLauncher(parameters);
        await launcher.InstallAsync(versionId, fileProgress, byteProgress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> InstallLoaderProfileWithRetryAsync(
        MinecraftClientLoader loader,
        string gameVersion,
        string loaderVersion,
        MinecraftPath path,
        IProgress<MinecraftClientInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stage = loader switch
        {
            MinecraftClientLoader.Fabric => "fabric-profile",
            MinecraftClientLoader.Quilt => "quilt-profile",
            _ => throw new ArgumentOutOfRangeException(nameof(loader)),
        };
        var host = loader switch
        {
            MinecraftClientLoader.Fabric => "meta.fabricmc.net",
            MinecraftClientLoader.Quilt => "meta.quiltmc.org",
            _ => null,
        };

        for (var attempt = 1; attempt <= _reliabilityOptions.MaximumPhaseAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var installedVersionId = await _loaderProfileInstall(
                        loader,
                        gameVersion,
                        loaderVersion,
                        path)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return installedVersionId;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ExceptionGraphSafety.RethrowOutOfMemory(exception);
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt >= _reliabilityOptions.MaximumPhaseAttempts ||
                    !IsRetryableLoaderProfileFailure(exception, cancellationToken))
                {
                    throw new MinecraftClientDownloadException(
                        attempt,
                        host,
                        CmlDownloadRetryPolicy.GetHttpStatusCode(exception),
                        CmlDownloadRetryPolicy.GetFailureKind(exception),
                        stage,
                        exception);
                }

                progress?.Report(new MinecraftClientInstallProgress(
                    "retry",
                    $"模組載入器設定檔暫時無法下載，正在進行第 {attempt + 1} 次嘗試…"));
                await _delayAsync(
                        _reliabilityOptions.GetDelayAfterAttempt(attempt),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("The bounded loader profile retry loop did not complete.");
    }

    private static bool IsRetryableLoaderProfileFailure(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested ||
            exception is MinecraftClientDownloadException)
        {
            return false;
        }

        if (exception is AggregateException aggregate)
        {
            var failures = aggregate.Flatten().InnerExceptions;
            return failures.Count > 0 &&
                failures.All(failure =>
                    IsRetryableLoaderProfileFailure(failure, cancellationToken));
        }

        // CmlLib's loader profile endpoint is JSON. A cut-off response is safe to fetch again,
        // while an arbitrary InvalidDataException may represent a permanent semantic failure.
        if (exception is System.Text.Json.JsonException)
        {
            return true;
        }

        if (exception is HttpRequestException or TimeoutException or TaskCanceledException or
            HttpIOException or System.Net.Sockets.SocketException)
        {
            return CmlDownloadRetryPolicy.IsRetryable(exception, cancellationToken);
        }

        return exception is IOException { InnerException: { } inner } &&
            IsRetryableLoaderProfileFailure(inner, cancellationToken);
    }

    private Task<string> InstallLoaderProfileOnceAsync(
        MinecraftClientLoader loader,
        string gameVersion,
        string loaderVersion,
        MinecraftPath path) => loader switch
        {
            MinecraftClientLoader.Fabric =>
                new FabricInstaller(_httpClient).Install(gameVersion, loaderVersion, path),
            MinecraftClientLoader.Quilt =>
                new QuiltInstaller(_httpClient).Install(gameVersion, loaderVersion, path),
            _ => throw new ArgumentOutOfRangeException(nameof(loader)),
        };

    private static MinecraftClientDownloadException? FindDownloadException(Exception exception)
    {
        if (exception is MinecraftClientDownloadException download)
        {
            return download;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions
                .Select(FindDownloadException)
                .FirstOrDefault(candidate => candidate is not null);
        }

        return exception.InnerException is null
            ? null
            : FindDownloadException(exception.InnerException);
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
