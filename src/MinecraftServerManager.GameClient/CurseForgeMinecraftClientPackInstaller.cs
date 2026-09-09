using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Installs one exact CurseForge client export into an isolated staging directory. Every referenced
/// artifact is re-read from the official API and verified by <see cref="CurseForgeModpackProvider"/>
/// before the completed payload is atomically promoted and committed to the client registry.
/// </summary>
public sealed class CurseForgeMinecraftClientPackInstaller : ICurseForgeMinecraftClientPackInstaller
{
    private const long MaximumCatalogArtworkBytes = 5L * 1024 * 1024;
    private const long DefaultMaximumReferencedContentBytes = 32L * 1024 * 1024 * 1024;
    private const int MaximumDownloadConcurrency = 16;
    private readonly string _instancesRoot;
    private readonly string _stagingRoot;
    private readonly MinecraftClientRegistry _registry;
    private readonly IMinecraftReleaseCatalog _releaseCatalog;
    private readonly IMinecraftClientPayloadInstaller _payloadInstaller;
    private readonly CurseForgeModpackProvider _provider;
    private readonly CurseForgeModpackManifestInspector _inspector;
    private readonly long _maximumReferencedContentBytes;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public CurseForgeMinecraftClientPackInstaller(
        string instancesDirectory,
        string stagingDirectory,
        MinecraftClientRegistry registry,
        IMinecraftReleaseCatalog releaseCatalog,
        IMinecraftClientPayloadInstaller payloadInstaller,
        CurseForgeModpackProvider provider,
        CurseForgeModpackManifestInspector inspector)
        : this(
            instancesDirectory,
            stagingDirectory,
            registry,
            releaseCatalog,
            payloadInstaller,
            provider,
            inspector,
            DefaultMaximumReferencedContentBytes)
    {
    }

    internal CurseForgeMinecraftClientPackInstaller(
        string instancesDirectory,
        string stagingDirectory,
        MinecraftClientRegistry registry,
        IMinecraftReleaseCatalog releaseCatalog,
        IMinecraftClientPayloadInstaller payloadInstaller,
        CurseForgeModpackProvider provider,
        CurseForgeModpackManifestInspector inspector,
        long maximumReferencedContentBytes)
    {
        _instancesRoot = NormalizeRoot(instancesDirectory, nameof(instancesDirectory));
        _stagingRoot = NormalizeRoot(stagingDirectory, nameof(stagingDirectory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _releaseCatalog = releaseCatalog ?? throw new ArgumentNullException(nameof(releaseCatalog));
        _payloadInstaller = payloadInstaller ?? throw new ArgumentNullException(nameof(payloadInstaller));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        if (maximumReferencedContentBytes is < 1 or > DefaultMaximumReferencedContentBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumReferencedContentBytes),
                $"The CurseForge referenced-content byte limit must be between 1 and "
                + $"{DefaultMaximumReferencedContentBytes.ToString(CultureInfo.InvariantCulture)}.");
        }

        _maximumReferencedContentBytes = maximumReferencedContentBytes;

        Directory.CreateDirectory(_instancesRoot);
        Directory.CreateDirectory(_stagingRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(_instancesRoot, _instancesRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(_stagingRoot, _stagingRoot);
    }

    public async Task<CurseForgeClientPackInstallResult> InstallAsync(
        CurseForgeClientPackInstallRequest request,
        SecureString apiKey,
        string? javaExecutablePath,
        IProgress<CurseForgeClientPackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(apiKey);
        if (apiKey.Length == 0)
        {
            throw new ArgumentException("The CurseForge API key cannot be empty.", nameof(apiKey));
        }

        IntPtr secret = IntPtr.Zero;
        try
        {
            secret = Marshal.SecureStringToBSTR(apiKey);
            var operationKey = Marshal.PtrToStringBSTR(secret);
            if (string.IsNullOrWhiteSpace(operationKey))
            {
                throw new ArgumentException("The CurseForge API key cannot be empty.", nameof(apiKey));
            }

            return await InstallCoreAsync(
                    request,
                    operationKey,
                    javaExecutablePath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (secret != IntPtr.Zero)
            {
                Marshal.ZeroFreeBSTR(secret);
            }
        }
    }

    private async Task<CurseForgeClientPackInstallResult> InstallCoreAsync(
        CurseForgeClientPackInstallRequest request,
        string apiKey,
        string? javaExecutablePath,
        IProgress<CurseForgeClientPackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var operationRoot = SafePath.CombineUnderRoot(_stagingRoot, request.InstanceId.ToString("N"));
        var payloadRoot = SafePath.CombineUnderRoot(operationRoot, "payload");
        var downloadsRoot = SafePath.CombineUnderRoot(operationRoot, "downloads");
        var packagePath = SafePath.CombineUnderRoot(operationRoot, "package.zip");
        var finalRoot = SafePath.CombineUnderRoot(_instancesRoot, request.InstanceId.ToString("N"));
        var promoted = false;
        try
        {
            RejectExistingPath(operationRoot);
            RejectExistingPath(finalRoot);
            Directory.CreateDirectory(operationRoot);
            Directory.CreateDirectory(payloadRoot);
            Directory.CreateDirectory(downloadsRoot);
            SafePath.EnsureNoReparsePointsUnderRoot(_stagingRoot, payloadRoot);
            SafePath.EnsureNoReparsePointsUnderRoot(_stagingRoot, downloadsRoot);

            progress?.Report(new CurseForgeClientPackInstallProgress(
                "resolve-project",
                "正在重新驗證 CurseForge 專案…",
                Fraction: 0.02d));
            var project = await _provider.GetProjectAsync(apiKey, request.ModId, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new CurseForgeClientPackInstallProgress(
                "download-pack",
                $"正在下載並驗證 {project.Name}…",
                TotalItems: 1,
                Fraction: 0.04d));
            await _provider.DownloadVerifiedFileAsync(
                    apiKey,
                    request.ModId,
                    request.FileId,
                    CurseForgeModpackFileRole.ClientPack,
                    packagePath,
                    new InlineProgress<double>(value => progress?.Report(
                        new CurseForgeClientPackInstallProgress(
                            "download-pack",
                            $"正在下載並驗證 {project.Name}…",
                            TotalItems: 1,
                            Fraction: 0.04d + Math.Clamp(value, 0d, 1d) * 0.20d))),
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new CurseForgeClientPackInstallProgress(
                "inspect-pack",
                "正在驗證 CurseForge 客戶端模組包內容…",
                Fraction: 0.25d));
            var manifest = await _inspector.InspectAsync(packagePath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(request.ExpectedMinecraftVersion) &&
                !string.Equals(
                    manifest.MinecraftVersion,
                    request.ExpectedMinecraftVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The CurseForge manifest Minecraft target does not match the selected catalog version.");
            }

            var loader = ValidateManifest(manifest);
            ValidateProtectedOverrides(manifest);

            var releases = await _releaseCatalog.GetStableReleasesAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!releases.Releases.Any(release =>
                    string.Equals(release.Id, manifest.MinecraftVersion, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Minecraft {manifest.MinecraftVersion} is not an official stable release.");
            }

            var clientRequest = new MinecraftClientInstallRequest(
                request.InstanceId,
                request.Name.Trim(),
                MinecraftClientEdition.Java,
                manifest.MinecraftVersion,
                loader,
                manifest.LoaderInstallRequest.LoaderVersion,
                request.MemoryMode,
                request.MinimumMemoryMb,
                request.MaximumMemoryMb,
                request.WindowWidth,
                request.WindowHeight,
                request.FullScreen);
            var gameProgress = new InlineProgress<MinecraftClientInstallProgress>(value =>
                progress?.Report(new CurseForgeClientPackInstallProgress(
                    "install-game",
                    value.Message,
                    Fraction: value.Fraction is { } fraction
                        ? 0.28d + Math.Clamp(fraction, 0d, 1d) * 0.27d
                        : null)));
            var installedVersionId = await _payloadInstaller.InstallAsync(
                    clientRequest,
                    payloadRoot,
                    javaExecutablePath,
                    gameProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateInstalledVersionId(installedVersionId);

            var selectedFiles = manifest.Files
                .Where(file => file.IsRequired || request.IncludeOptionalFiles)
                .ToArray();
            var installedRemotePaths = await DownloadReferencedFilesAsync(
                    selectedFiles,
                    apiKey,
                    downloadsRoot,
                    payloadRoot,
                    request.MaximumConcurrentDownloads,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new CurseForgeClientPackInstallProgress(
                "extract-overrides",
                "正在安全套用 CurseForge 模組包設定…",
                TotalItems: manifest.OverrideEntries.Count,
                Fraction: 0.88d));
            await _inspector.ExtractOverridesAsync(
                    packagePath,
                    payloadRoot,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var catalogIconRelativePath = CopyArtworkIntoOwnedPayload(
                request.CatalogIconImagePath,
                payloadRoot,
                "catalog-icon",
                cancellationToken);
            var catalogPreviewRelativePath = CopyArtworkIntoOwnedPayload(
                request.CatalogPreviewImagePath,
                payloadRoot,
                "catalog-preview",
                cancellationToken);

            progress?.Report(new CurseForgeClientPackInstallProgress(
                "finalize",
                "正在完成 CurseForge 模組包的安全驗證與安裝交易…",
                Fraction: 0.96d));
            cancellationToken.ThrowIfCancellationRequested();
            SafePath.EnsureTreeContainsNoReparsePoints(payloadRoot);
            Directory.Move(payloadRoot, finalRoot);
            promoted = true;

            var instance = CreateInstance(
                request,
                project,
                manifest,
                loader,
                finalRoot,
                installedVersionId,
                javaExecutablePath,
                ResolveOwnedArtworkPath(finalRoot, catalogIconRelativePath),
                ResolveOwnedArtworkPath(finalRoot, catalogPreviewRelativePath));
            try
            {
                await _registry.UpdateAsync(
                        document =>
                        {
                            if (document.Instances.Any(item => item.Id == instance.Id))
                            {
                                throw new InvalidOperationException(
                                    "A client instance with the same id already exists.");
                            }

                            document.Instances.Add(instance);
                            return true;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                DeleteOwnedTree(_instancesRoot, finalRoot);
                promoted = false;
                throw;
            }

            var installedPaths = installedRemotePaths
                .Concat(manifest.OverrideEntries.Select(static entry => entry.RelativePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            progress?.Report(new CurseForgeClientPackInstallProgress(
                "complete",
                "CurseForge 客戶端模組包已安全安裝並加入 X MCSV。",
                1,
                1,
                Fraction: 1d));
            return new CurseForgeClientPackInstallResult(
                instance,
                request.ModId,
                request.FileId,
                project.Name,
                manifest.Name,
                manifest.PackVersion,
                selectedFiles.Length,
                manifest.Files.Count - selectedFiles.Length,
                installedPaths);
        }
        catch
        {
            if (promoted)
            {
                DeleteOwnedTree(_instancesRoot, finalRoot);
            }

            throw;
        }
        finally
        {
            DeleteOwnedTree(_stagingRoot, operationRoot);
            _mutationGate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> DownloadReferencedFilesAsync(
        IReadOnlyList<CurseForgeModpackManifestFile> files,
        string apiKey,
        string downloadsRoot,
        string payloadRoot,
        int maximumConcurrency,
        IProgress<CurseForgeClientPackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return [];
        }

        var installed = new ConcurrentBag<string>();
        var completedFiles = 0;
        long downloadedBytes = 0;
        Exception? firstFailure = null;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        progress?.Report(new CurseForgeClientPackInstallProgress(
            "download-content",
            "正在下載並驗證 CurseForge 模組包內容…",
            0,
            files.Count,
            0.55d));
        try
        {
            await Parallel.ForEachAsync(
                    files,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Min(maximumConcurrency, Math.Max(1, files.Count)),
                        CancellationToken = linkedCancellation.Token,
                    },
                    async (file, token) =>
                    {
                        try
                        {
                            var artifactPath = SafePath.CombineUnderRoot(
                                downloadsRoot,
                                $"{file.ProjectId.ToString(CultureInfo.InvariantCulture)}-"
                                + $"{file.FileId.ToString(CultureInfo.InvariantCulture)}.artifact");
                            var result = await DownloadReferencedFileAsync(
                                    file,
                                    apiKey,
                                    artifactPath,
                                    token)
                                .ConfigureAwait(false);
                            var aggregateBytes = AddChecked(ref downloadedBytes, result.Size);
                            if (aggregateBytes > _maximumReferencedContentBytes)
                            {
                                throw new InvalidDataException(
                                    $"CurseForge referenced content exceeds the configured "
                                    + $"{_maximumReferencedContentBytes.ToString(CultureInfo.InvariantCulture)} byte limit.");
                            }

                            var fileName = ValidateReferencedFileName(result.FileName);
                            var relativePath = $"mods/{fileName}";
                            var destination = PreparePackDestination(payloadRoot, relativePath);
                            File.Move(artifactPath, destination, overwrite: false);
                            installed.Add(relativePath);
                            var count = Interlocked.Increment(ref completedFiles);
                            progress?.Report(new CurseForgeClientPackInstallProgress(
                                "download-content",
                                fileName,
                                count,
                                files.Count,
                                0.55d + (double)count / files.Count * 0.32d));
                        }
                        catch (Exception exception)
                        {
                            Interlocked.CompareExchange(ref firstFailure, exception, null);
                            await linkedCancellation.CancelAsync().ConfigureAwait(false);
                            throw;
                        }
                    })
                .ConfigureAwait(false);
        }
        catch
        {
            await linkedCancellation.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (firstFailure is not null)
            {
                ExceptionDispatchInfo.Capture(firstFailure).Throw();
            }

            throw;
        }

        return installed.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<CurseForgeModpackDownloadResult> DownloadReferencedFileAsync(
        CurseForgeModpackManifestFile file,
        string apiKey,
        string artifactPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _provider.DownloadVerifiedFileAsync(
                    apiKey,
                    file.ProjectId,
                    file.FileId,
                    CurseForgeModpackFileRole.ClientPack,
                    artifactPath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CurseForgeServerPackException exception)
        {
            // The UI may turn this provider-specific exception into an "open project page"
            // fallback for the selected pack. A blocked dependency must instead fail the whole
            // staged installation, because opening the parent pack page cannot repair it.
            throw new InvalidOperationException(
                $"CurseForge dependency {file.ProjectId.ToString(CultureInfo.InvariantCulture)}/"
                + $"{file.FileId.ToString(CultureInfo.InvariantCulture)} cannot be downloaded through the official API.",
                exception);
        }
        catch (CurseForgeApiException exception) when (
            exception.ErrorCode is CurseForgeApiErrorCode.Forbidden or CurseForgeApiErrorCode.NotFound)
        {
            throw new InvalidOperationException(
                $"CurseForge dependency {file.ProjectId.ToString(CultureInfo.InvariantCulture)}/"
                + $"{file.FileId.ToString(CultureInfo.InvariantCulture)} is unavailable through the official API.",
                exception);
        }
    }

    private static long AddChecked(ref long location, long value)
    {
        if (value < 0)
        {
            throw new InvalidDataException("CurseForge returned a negative dependency size.");
        }

        while (true)
        {
            var current = Volatile.Read(ref location);
            long updated;
            try
            {
                updated = checked(current + value);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("CurseForge referenced content size overflowed.", exception);
            }

            if (Interlocked.CompareExchange(ref location, updated, current) == current)
            {
                return updated;
            }
        }
    }

    private static MinecraftClientLoader ValidateManifest(CurseForgeModpackManifestInfo manifest)
    {
        var loader = manifest.LoaderInstallRequest.Kind switch
        {
            ModrinthModpackLoaderKind.Vanilla => MinecraftClientLoader.Vanilla,
            ModrinthModpackLoaderKind.Fabric => MinecraftClientLoader.Fabric,
            ModrinthModpackLoaderKind.Forge => MinecraftClientLoader.Forge,
            ModrinthModpackLoaderKind.NeoForge => MinecraftClientLoader.NeoForge,
            ModrinthModpackLoaderKind.Quilt => MinecraftClientLoader.Quilt,
            _ => throw new InvalidDataException("The CurseForge modpack loader is unsupported."),
        };
        if (loader != MinecraftClientLoader.Vanilla &&
            string.IsNullOrWhiteSpace(manifest.LoaderInstallRequest.LoaderVersion))
        {
            throw new InvalidDataException("The CurseForge manifest does not specify its loader version.");
        }

        return loader;
    }

    private static void ValidateProtectedOverrides(CurseForgeModpackManifestInfo manifest)
    {
        foreach (var path in manifest.OverrideEntries.Select(static entry => entry.RelativePath))
        {
            var first = path.Split('/', 2)[0];
            if (first.Equals("versions", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("libraries", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("assets", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("runtime", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("jre", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("natives", StringComparison.OrdinalIgnoreCase) ||
                first.Equals(".x-mcsv-content", StringComparison.OrdinalIgnoreCase) ||
                first.Equals(".x-mcsv", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("installation.id", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("launcher_accounts.json", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("launcher_profiles.json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The CurseForge modpack attempts to replace protected launcher content: '{path}'.");
            }
        }
    }

    private static string ValidateReferencedFileName(string value)
    {
        var fileName = value?.Trim() ?? string.Empty;
        if (fileName.Length is < 1 or > 240 ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            fileName is "." or ".." ||
            fileName.Any(character => char.IsControl(character) ||
                                      Path.GetInvalidFileNameChars().Contains(character)))
        {
            throw new InvalidDataException("CurseForge returned an invalid dependency file name.");
        }

        var extension = Path.GetExtension(fileName);
        if (!extension.Equals(".jar", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".litemod", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"CurseForge dependency '{fileName}' is not a supported Minecraft mod artifact.");
        }

        return fileName;
    }

    private static string PreparePackDestination(string payloadRoot, string relativePath)
    {
        var destination = SafeModpackArchive.ResolveDestination(payloadRoot, relativePath);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("CurseForge mod destination has no parent directory.");
        Directory.CreateDirectory(parent);
        SafePath.EnsureNoReparsePointsUnderRoot(payloadRoot, parent);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"CurseForge mod destination already exists: '{relativePath}'.");
        }

        return destination;
    }

    private static MinecraftClientInstance CreateInstance(
        CurseForgeClientPackInstallRequest request,
        CurseForgeModpackProject project,
        CurseForgeModpackManifestInfo manifest,
        MinecraftClientLoader loader,
        string finalRoot,
        string installedVersionId,
        string? javaExecutablePath,
        string? catalogIconImagePath,
        string? catalogPreviewImagePath) => new()
    {
        Id = request.InstanceId,
        Name = request.Name.Trim(),
        Edition = MinecraftClientEdition.Java,
        DirectoryPath = finalRoot,
        GameVersion = manifest.MinecraftVersion,
        InstalledVersionId = installedVersionId,
        Loader = loader,
        LoaderVersion = manifest.LoaderInstallRequest.LoaderVersion,
        LoaderInstallKind = MinecraftClientLoaderInstallKind.Managed,
        JavaExecutablePath = javaExecutablePath,
        JavaMajorVersion = request.JavaMajorVersion,
        MemoryMode = request.MemoryMode,
        MinimumMemoryMb = request.MinimumMemoryMb,
        MaximumMemoryMb = request.MaximumMemoryMb,
        WindowWidth = request.WindowWidth,
        WindowHeight = request.WindowHeight,
        FullScreen = request.FullScreen,
        EnableQuickLaunch = request.EnableQuickLaunch,
        HideLauncherAfterGameStarts = request.HideLauncherAfterGameStarts,
        ShowGameLog = request.ShowGameLog,
        EnableDedicatedGpu = request.EnableDedicatedGpu,
        EnableDiscordPresence = request.EnableDiscordPresence,
        CatalogProvider = "curseforge",
        CatalogProjectId = request.ModId.ToString(CultureInfo.InvariantCulture),
        CatalogVersionId = request.FileId.ToString(CultureInfo.InvariantCulture),
        CatalogIconUri = NormalizeCurseForgeArtworkUri(project.IconUri),
        CatalogPreviewUri = NormalizeCurseForgeArtworkUri(project.PreviewImageUri),
        CatalogIconImagePath = catalogIconImagePath,
        CatalogPreviewImagePath = catalogPreviewImagePath,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    public static bool IsOfficialCurseForgeArtworkUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            string.IsNullOrWhiteSpace(uri.IdnHost) || IPAddress.TryParse(uri.IdnHost, out _))
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.');
        return host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri? NormalizeCurseForgeArtworkUri(Uri? uri) =>
        IsOfficialCurseForgeArtworkUri(uri) ? uri : null;

    private static void ValidateRequest(CurseForgeClientPackInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InstanceId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) ||
            request.Name.Length > 128 || request.Name.Any(char.IsControl) ||
            request.ModId <= 0 || request.FileId <= 0)
        {
            throw new ArgumentException("The CurseForge client pack install request is invalid.", nameof(request));
        }

        if (!string.IsNullOrEmpty(request.ExpectedMinecraftVersion))
        {
            OfficialCatalogValidation.ValidateVersionToken(
                request.ExpectedMinecraftVersion,
                nameof(request.ExpectedMinecraftVersion));
        }

        if (request.MinimumMemoryMb is < 512 or > 262_144 ||
            request.MaximumMemoryMb < request.MinimumMemoryMb || request.MaximumMemoryMb > 262_144 ||
            request.WindowWidth is < 640 or > 16_384 || request.WindowHeight is < 360 or > 16_384 ||
            request.MaximumConcurrentDownloads is < 1 or > MaximumDownloadConcurrency ||
            request.JavaMajorVersion is < 8 or > 99)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The client memory, resolution, or download concurrency is outside the safe range.");
        }
    }

    private static string? CopyArtworkIntoOwnedPayload(
        string? sourcePath,
        string payloadRoot,
        string fileStem,
        CancellationToken cancellationToken)
    {
        var source = ValidateCatalogArtworkPath(sourcePath);
        if (source is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var assetsDirectory = SafePath.CombineUnderRoot(payloadRoot, ".x-mcsv", "assets");
        Directory.CreateDirectory(assetsDirectory);
        SafePath.EnsureNoReparsePointsUnderRoot(payloadRoot, assetsDirectory);
        var destination = SafePath.CombineUnderRoot(assetsDirectory, fileStem + extension);
        var temporary = SafePath.CombineUnderRoot(
            assetsDirectory,
            $".{fileStem}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var input = new FileStream(
                       source,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       64 * 1024,
                       FileOptions.SequentialScan))
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var copied = new FileInfo(temporary);
            if (copied.Length is <= 0 or > MaximumCatalogArtworkBytes ||
                !HasMatchingArtworkSignature(temporary, extension))
            {
                throw new InvalidDataException("Copied CurseForge catalog artwork failed validation.");
            }

            File.Move(temporary, destination);
            return Path.GetRelativePath(payloadRoot, destination);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static string? ValidateCatalogArtworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Cached CurseForge artwork path must be absolute.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Cached CurseForge artwork no longer exists.", fullPath);
        }

        var file = new FileInfo(fullPath);
        var extension = file.Extension;
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            file.Length is <= 0 or > MaximumCatalogArtworkBytes ||
            !extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            !HasMatchingArtworkSignature(fullPath, extension))
        {
            throw new InvalidDataException("Cached CurseForge artwork is not a bounded regular image file.");
        }

        return fullPath;
    }

    private static bool HasMatchingArtworkSignature(string path, string extension)
    {
        Span<byte> header = stackalloc byte[12];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = input.Read(header);
        var bytes = header[..read];
        return extension.ToLowerInvariant() switch
        {
            ".png" => bytes.StartsWith(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            ".jpg" => bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }),
            ".webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) &&
                       bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            ".gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
            _ => false,
        };
    }

    private static string? ResolveOwnedArtworkPath(string instanceRoot, string? relativePath) =>
        relativePath is null ? null : SafePath.CombineUnderRoot(instanceRoot, relativePath);

    private static void ValidateInstalledVersionId(string installedVersionId)
    {
        if (string.IsNullOrWhiteSpace(installedVersionId) || installedVersionId.Length > 192 ||
            installedVersionId.Any(char.IsControl))
        {
            throw new InvalidDataException("The client installer returned an invalid launch profile id.");
        }
    }

    private static string NormalizeRoot(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void RejectExistingPath(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"Managed client path already exists: '{path}'.");
        }
    }

    private static void DeleteOwnedTree(string trustedRoot, string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                SafePath.DeleteTreeWithoutFollowingReparsePoints(trustedRoot, path);
            }
            else if (File.Exists(path))
            {
                var safe = SafePath.EnsureNoReparsePointsUnderRoot(trustedRoot, path);
                File.Delete(safe);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
