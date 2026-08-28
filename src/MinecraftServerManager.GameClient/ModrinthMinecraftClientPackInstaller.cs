using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Installs one stable Modrinth client pack into an isolated operation directory, promotes only a
/// fully verified runnable payload, and commits the client registry last.
/// </summary>
public sealed class ModrinthMinecraftClientPackInstaller
{
    private const long MaximumCatalogArtworkBytes = 5L * 1024 * 1024;
    private const int MaximumDownloadConcurrency = 16;
    private readonly string _instancesRoot;
    private readonly string _stagingRoot;
    private readonly MinecraftClientRegistry _registry;
    private readonly IMinecraftReleaseCatalog _releaseCatalog;
    private readonly IMinecraftClientPayloadInstaller _payloadInstaller;
    private readonly IModrinthClientModpackCatalog _catalog;
    private readonly ModrinthModpackArtifactDownloader _downloader;
    private readonly SafeModpackArchiveLimits _archiveLimits;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public ModrinthMinecraftClientPackInstaller(
        string instancesDirectory,
        string stagingDirectory,
        MinecraftClientRegistry registry,
        IMinecraftReleaseCatalog releaseCatalog,
        IMinecraftClientPayloadInstaller payloadInstaller,
        IModrinthClientModpackCatalog catalog,
        HttpClient artifactHttpClient,
        SafeModpackArchiveLimits? archiveLimits = null)
    {
        _instancesRoot = NormalizeRoot(instancesDirectory, nameof(instancesDirectory));
        _stagingRoot = NormalizeRoot(stagingDirectory, nameof(stagingDirectory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _releaseCatalog = releaseCatalog ?? throw new ArgumentNullException(nameof(releaseCatalog));
        _payloadInstaller = payloadInstaller ?? throw new ArgumentNullException(nameof(payloadInstaller));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(artifactHttpClient);
        _archiveLimits = archiveLimits ?? new SafeModpackArchiveLimits();
        _downloader = new ModrinthModpackArtifactDownloader(
            new HttpClientModrinthModpackHttpTransport(artifactHttpClient),
            new OfficialModrinthClientDownloadUriPolicy(),
            maxRedirects: 0);

        Directory.CreateDirectory(_instancesRoot);
        Directory.CreateDirectory(_stagingRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(_instancesRoot, _instancesRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(_stagingRoot, _stagingRoot);
    }

    public async Task<ModrinthClientPackInstallResult> InstallAsync(
        ModrinthClientPackInstallRequest request,
        string? javaExecutablePath,
        IProgress<ModrinthClientPackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var projectTask = _catalog.GetProjectAsync(request.ProjectId, cancellationToken);
        var versionTask = _catalog.GetStableVersionAsync(request.VersionId, cancellationToken);
        await Task.WhenAll(projectTask, versionTask).ConfigureAwait(false);
        var project = await projectTask.ConfigureAwait(false);
        var version = await versionTask.ConfigureAwait(false);
        ValidateCatalogSelection(request, project, version);
        if (version.MrpackFile.Size > _archiveLimits.MaxArchiveBytes)
        {
            throw new InvalidDataException("The selected .mrpack exceeds the safe archive size limit.");
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var operationRoot = SafePath.CombineUnderRoot(_stagingRoot, request.InstanceId.ToString("N"));
        var payloadRoot = SafePath.CombineUnderRoot(operationRoot, "payload");
        var packagePath = SafePath.CombineUnderRoot(operationRoot, "package.mrpack");
        var finalRoot = SafePath.CombineUnderRoot(_instancesRoot, request.InstanceId.ToString("N"));
        var promoted = false;
        try
        {
            RejectExistingPath(operationRoot);
            RejectExistingPath(finalRoot);
            Directory.CreateDirectory(operationRoot);
            Directory.CreateDirectory(payloadRoot);
            SafePath.EnsureNoReparsePointsUnderRoot(_stagingRoot, payloadRoot);

            progress?.Report(new ModrinthClientPackInstallProgress(
                "download-pack",
                $"正在下載並驗證 {version.Name}…",
                TotalItems: 1));
            var packageBytes = new InlineProgress<long>(bytes =>
                progress?.Report(new ModrinthClientPackInstallProgress(
                    "download-pack",
                    $"正在下載並驗證 {version.Name}…",
                    TotalItems: 1,
                    CompletedBytes: bytes,
                    Fraction: version.MrpackFile.Size > 0
                        ? Math.Clamp((double)bytes / version.MrpackFile.Size, 0d, 1d)
                        : null)));
            await _downloader.DownloadAsync(
                    [version.MrpackFile.DownloadUri],
                    packagePath,
                    version.MrpackFile.Size,
                    version.MrpackFile.Sha512,
                    version.MrpackFile.Sha1,
                    packageBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new ModrinthClientPackInstallProgress(
                "inspect-pack",
                "正在驗證客戶端模組包內容…"));
            var plan = await SafeModpackArchive.InspectClientAsync(
                    packagePath,
                    _downloader.UriPolicy,
                    _archiveLimits,
                    cancellationToken)
                .ConfigureAwait(false);
            var loader = ValidateManifestAgainstVersion(plan, version);
            ValidateProtectedPaths(plan);

            var releases = await _releaseCatalog.GetStableReleasesAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!releases.Releases.Any(release =>
                    string.Equals(release.Id, plan.MinecraftVersion, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Minecraft {plan.MinecraftVersion} is not an official stable release.");
            }

            var clientRequest = new MinecraftClientInstallRequest(
                request.InstanceId,
                request.Name.Trim(),
                MinecraftClientEdition.Java,
                plan.MinecraftVersion,
                loader,
                plan.LoaderInstallRequest.LoaderVersion,
                request.MemoryMode,
                request.MinimumMemoryMb,
                request.MaximumMemoryMb,
                request.WindowWidth,
                request.WindowHeight,
                request.FullScreen);
            var gameProgress = new InlineProgress<MinecraftClientInstallProgress>(value =>
                progress?.Report(new ModrinthClientPackInstallProgress(
                    "install-game",
                    value.Message,
                    Fraction: value.Fraction)));
            var installedVersionId = await _payloadInstaller.InstallAsync(
                    clientRequest,
                    payloadRoot,
                    javaExecutablePath,
                    gameProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateInstalledVersionId(installedVersionId);

            var selectedFiles = plan.Files
                .Where(file => !file.IsOptional || request.IncludeOptionalFiles)
                .ToArray();
            var installedRemotePaths = await DownloadPackFilesAsync(
                    selectedFiles,
                    payloadRoot,
                    request.MaximumConcurrentDownloads,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new ModrinthClientPackInstallProgress(
                "extract-overrides",
                "正在套用共用與客戶端設定…",
                TotalItems: plan.Overrides.Count + plan.ClientOverrides.Count));
            await SafeModpackArchive.ExtractOverridesAsync(
                    packagePath,
                    payloadRoot,
                    plan.Overrides,
                    cancellationToken)
                .ConfigureAwait(false);
            await SafeModpackArchive.ExtractOverridesAsync(
                    packagePath,
                    payloadRoot,
                    plan.ClientOverrides,
                    cancellationToken)
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

            cancellationToken.ThrowIfCancellationRequested();
            SafePath.EnsureTreeContainsNoReparsePoints(payloadRoot);
            Directory.Move(payloadRoot, finalRoot);
            promoted = true;

            var instance = CreateInstance(
                request,
                project,
                version,
                plan,
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
                .Concat(plan.Overrides.Select(entry => entry.RelativePath))
                .Concat(plan.ClientOverrides.Select(entry => entry.RelativePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            progress?.Report(new ModrinthClientPackInstallProgress(
                "complete",
                "Modrinth 客戶端模組包已安全安裝並加入 X MCSV。",
                1,
                1,
                Fraction: 1d));
            return new ModrinthClientPackInstallResult(
                instance,
                project,
                version,
                plan.Name,
                plan.VersionId,
                selectedFiles.Length,
                plan.SkippedUnsupportedFiles,
                plan.OptionalFiles.Count - selectedFiles.Count(file => file.IsOptional),
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

    private async Task<IReadOnlyList<string>> DownloadPackFilesAsync(
        IReadOnlyList<SafeModpackContentFile> files,
        string payloadRoot,
        int maximumConcurrency,
        IProgress<ModrinthClientPackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var installed = new ConcurrentBag<string>();
        var completedFiles = 0;
        long completedBytes = 0;
        Exception? firstFailure = null;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        progress?.Report(new ModrinthClientPackInstallProgress(
            "download-content",
            "正在下載並驗證模組包內容…",
            0,
            files.Count));
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
                        var destination = PreparePackDestination(payloadRoot, file.Path);
                        long previousBytes = 0;
                        var byteProgress = new InlineProgress<long>(current =>
                        {
                            var delta = Math.Max(0, current - Interlocked.Exchange(ref previousBytes, current));
                            var aggregate = Interlocked.Add(ref completedBytes, delta);
                            progress?.Report(new ModrinthClientPackInstallProgress(
                                "download-content",
                                file.Path,
                                Volatile.Read(ref completedFiles),
                                files.Count,
                                aggregate));
                        });
                        await _downloader.DownloadAsync(
                                file.Downloads,
                                destination,
                                file.FileSize,
                                file.Sha512,
                                file.Sha1,
                                byteProgress,
                                token)
                            .ConfigureAwait(false);
                        installed.Add(file.Path);
                        var count = Interlocked.Increment(ref completedFiles);
                        progress?.Report(new ModrinthClientPackInstallProgress(
                            "download-content",
                            file.Path,
                            count,
                            files.Count,
                            Volatile.Read(ref completedBytes)));
                    }
                    catch (Exception exception)
                    {
                        Interlocked.CompareExchange(ref firstFailure, exception, null);
                        await linkedCancellation.CancelAsync().ConfigureAwait(false);
                        throw;
                    }
                }).ConfigureAwait(false);
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

        return installed.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string PreparePackDestination(string payloadRoot, string relativePath)
    {
        var destination = SafeModpackArchive.ResolveDestination(payloadRoot, relativePath);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("Modpack destination has no parent directory.");
        var relativeParent = Path.GetRelativePath(payloadRoot, parent);
        var current = payloadRoot;
        foreach (var segment in relativeParent.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = SafePath.CombineUnderRoot(current, segment);
            if (File.Exists(current))
            {
                throw new IOException($"Modpack destination parent is a file: '{current}'.");
            }

            Directory.CreateDirectory(current);
            SafePath.EnsureNoReparsePointsUnderRoot(payloadRoot, current);
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"Modpack destination already exists: '{relativePath}'.");
        }

        return destination;
    }

    private static MinecraftClientLoader ValidateManifestAgainstVersion(
        SafeModpackArchivePlan plan,
        ModrinthClientModpackVersion version)
    {
        if (!version.GameVersions.Contains(plan.MinecraftVersion, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The .mrpack Minecraft dependency does not match its Modrinth version metadata.");
        }

        var loader = plan.LoaderInstallRequest.Kind switch
        {
            ModrinthModpackLoaderKind.Vanilla => MinecraftClientLoader.Vanilla,
            ModrinthModpackLoaderKind.Fabric => MinecraftClientLoader.Fabric,
            ModrinthModpackLoaderKind.Forge => MinecraftClientLoader.Forge,
            ModrinthModpackLoaderKind.NeoForge => MinecraftClientLoader.NeoForge,
            ModrinthModpackLoaderKind.Quilt => MinecraftClientLoader.Quilt,
            _ => throw new InvalidDataException("The .mrpack loader is unsupported."),
        };
        var expectedLoader = loader switch
        {
            MinecraftClientLoader.Vanilla => "minecraft",
            MinecraftClientLoader.Fabric => "fabric",
            MinecraftClientLoader.Forge => "forge",
            MinecraftClientLoader.NeoForge => "neoforge",
            MinecraftClientLoader.Quilt => "quilt",
            _ => throw new InvalidDataException("The .mrpack loader is unsupported."),
        };
        if (!version.Loaders.Contains(expectedLoader, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The .mrpack loader dependency does not match its Modrinth version metadata.");
        }

        if (loader != MinecraftClientLoader.Vanilla &&
            string.IsNullOrWhiteSpace(plan.LoaderInstallRequest.LoaderVersion))
        {
            throw new InvalidDataException("The .mrpack does not specify its loader version.");
        }

        return loader;
    }

    private static void ValidateProtectedPaths(SafeModpackArchivePlan plan)
    {
        foreach (var path in plan.Files.Select(file => file.Path)
                     .Concat(plan.Overrides.Select(entry => entry.RelativePath))
                     .Concat(plan.ClientOverrides.Select(entry => entry.RelativePath)))
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
                    $"The modpack attempts to replace protected launcher content: '{path}'.");
            }
        }
    }

    private static MinecraftClientInstance CreateInstance(
        ModrinthClientPackInstallRequest request,
        ModrinthClientModpackProject project,
        ModrinthClientModpackVersion version,
        SafeModpackArchivePlan plan,
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
        GameVersion = plan.MinecraftVersion,
        InstalledVersionId = installedVersionId,
        Loader = loader,
        LoaderVersion = plan.LoaderInstallRequest.LoaderVersion,
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
        CatalogProvider = "modrinth",
        CatalogProjectId = project.ProjectId,
        CatalogVersionId = version.VersionId,
        CatalogIconUri = project.IconUri,
        CatalogPreviewUri = project.FeaturedImageUri,
        CatalogIconImagePath = catalogIconImagePath,
        CatalogPreviewImagePath = catalogPreviewImagePath,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static void ValidateRequest(ModrinthClientPackInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InstanceId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) ||
            request.Name.Length > 128 || !IsIdentifier(request.ProjectId) ||
            !IsIdentifier(request.VersionId))
        {
            throw new ArgumentException("The Modrinth client pack install request is invalid.", nameof(request));
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

    private static void ValidateCatalogSelection(
        ModrinthClientPackInstallRequest request,
        ModrinthClientModpackProject project,
        ModrinthClientModpackVersion version)
    {
        if (!string.Equals(project.ProjectId, request.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(version.ProjectId, request.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(version.VersionId, request.VersionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The selected Modrinth project and version do not match.");
        }

        if (project.IconUri is not null && !ModrinthClientModpackCatalog.IsOfficialCdnUri(project.IconUri) ||
            project.FeaturedImageUri is not null &&
            !ModrinthClientModpackCatalog.IsOfficialCdnUri(project.FeaturedImageUri) ||
            !ModrinthClientModpackCatalog.IsOfficialCdnUri(version.MrpackFile.DownloadUri))
        {
            throw new InvalidDataException("Modrinth catalog media or package URI is not on the official CDN.");
        }
    }

    private static bool IsIdentifier(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
           value.All(char.IsAsciiLetterOrDigit);

    private static string? ValidateCatalogArtworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Cached Modrinth artwork path must be absolute.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Cached Modrinth artwork no longer exists.", fullPath);
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
            throw new InvalidDataException("Cached Modrinth artwork is not a bounded regular image file.");
        }

        return fullPath;
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
                throw new InvalidDataException("Copied catalog artwork failed validation.");
            }

            File.Move(temporary, destination);
            return Path.GetRelativePath(payloadRoot, destination);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static string? ResolveOwnedArtworkPath(string instanceRoot, string? relativePath)
        => relativePath is null ? null : SafePath.CombineUnderRoot(instanceRoot, relativePath);

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
            ".webp" => bytes.Length >= 12 &&
                       bytes[..4].SequenceEqual("RIFF"u8) &&
                       bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            ".gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
            _ => false,
        };
    }

    private static void ValidateInstalledVersionId(string installedVersionId)
    {
        if (string.IsNullOrWhiteSpace(installedVersionId) || installedVersionId.Length > 192 ||
            installedVersionId.Any(character => char.IsControl(character)))
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

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
