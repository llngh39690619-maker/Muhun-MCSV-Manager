using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Plans and installs verified Modrinth mods, resource packs and shader packs. Required mod
/// dependencies are resolved transitively before any file is committed to the instance.
/// </summary>
public sealed class ModrinthClientContentInstaller : IDisposable
{
    private const int MaximumDependencyDepth = 32;
    private const int MaximumArtifacts = 256;
    private const long MaximumArtifactBytes = 2L * 1024 * 1024 * 1024;
    private readonly string _stagingRoot;
    private readonly IModrinthClientContentCatalog _catalog;
    private readonly ModrinthModpackArtifactDownloader _downloader;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private bool _disposed;

    public ModrinthClientContentInstaller(
        string stagingDirectory,
        IModrinthClientContentCatalog catalog,
        HttpClient artifactHttpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        if (!Path.IsPathFullyQualified(stagingDirectory))
        {
            throw new ArgumentException("Content staging directory must be absolute.", nameof(stagingDirectory));
        }

        _stagingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(artifactHttpClient);
        Directory.CreateDirectory(_stagingRoot);
        RejectReparsePoint(_stagingRoot, "Content staging directory");
        _downloader = new ModrinthModpackArtifactDownloader(
            new HttpClientModrinthModpackHttpTransport(artifactHttpClient),
            new OfficialModrinthClientDownloadUriPolicy(),
            maxRedirects: 0);
    }

    public async Task<ModrinthClientContentInstallPlan> PlanAsync(
        string projectId,
        MinecraftClientContentKind kind,
        string gameVersion,
        MinecraftClientLoader? loader = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateSelection(projectId, kind, gameVersion, loader);
        var project = await _catalog.GetProjectAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        if (project.Kind != kind)
        {
            throw new InvalidOperationException("The selected Modrinth project has a different content type.");
        }

        var version = await _catalog.SelectStableVersionAsync(
                project.ProjectId,
                gameVersion.Trim(),
                loader,
                cancellationToken)
            .ConfigureAwait(false);
        var compatibleLoaders = version.Loaders
            .Select(static value => ModrinthClientContentCatalog.TryGetLoader(value, out var parsed)
                ? (MinecraftClientLoader?)parsed
                : null)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Distinct()
            .ToArray();
        var requiredLoader = kind == MinecraftClientContentKind.Mod
            ? loader ?? SelectPreferredLoader(compatibleLoaders)
            : null;

        var state = new PlanningState(gameVersion.Trim(), requiredLoader);
        if (kind == MinecraftClientContentKind.Mod && requiredLoader is null)
        {
            state.Fallbacks.Add(CreateFallback(
                project,
                version,
                ModrinthClientContentFallbackReason.UnsupportedFile,
                "The stable release does not declare a supported Minecraft mod loader."));
        }
        else
        {
            await VisitAsync(
                    project,
                    version,
                    kind,
                    isDependency: false,
                    depth: 0,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new ModrinthClientContentInstallPlan(
            project,
            version,
            state.GameVersion,
            requiredLoader,
            compatibleLoaders,
            state.Artifacts,
            state.Fallbacks);
    }

    public async Task<ModrinthClientContentInstallResult> InstallAsync(
        ModrinthClientContentInstallRequest request,
        IProgress<ModrinthClientContentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateInstallRequest(request);
        var plan = await PlanAsync(
                request.ProjectId,
                request.Kind,
                request.GameVersion,
                request.Loader,
                cancellationToken)
            .ConfigureAwait(false);
        if (!plan.CanInstallAutomatically)
        {
            return new ModrinthClientContentInstallResult(plan, [], plan.Fallbacks);
        }

        var instanceRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(request.InstanceDirectory));
        RejectReparsePoint(instanceRoot, "Minecraft client instance directory");
        EnsureRootsDoNotOverlap(instanceRoot, _stagingRoot);

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var operationRoot = SafePath.CombineUnderRoot(
            _stagingRoot,
            $"content-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(operationRoot);
            SafePath.EnsureNoReparsePointsUnderRoot(_stagingRoot, operationRoot);
            var downloaded = new List<string>(plan.Artifacts.Count);
            for (var index = 0; index < plan.Artifacts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artifact = plan.Artifacts[index];
                var destination = SafePath.CombineUnderRoot(operationRoot, artifact.File.FileName);
                progress?.Report(new ModrinthClientContentInstallProgress(
                    "download",
                    $"Downloading and verifying {artifact.ProjectTitle}…",
                    index,
                    plan.Artifacts.Count));
                var bytes = new InlineProgress<long>(completed =>
                    progress?.Report(new ModrinthClientContentInstallProgress(
                        "download",
                        $"Downloading and verifying {artifact.ProjectTitle}…",
                        index,
                        plan.Artifacts.Count,
                        completed)));
                try
                {
                    await _downloader.DownloadAsync(
                            [artifact.File.DownloadUri],
                            destination,
                            artifact.File.Size,
                            artifact.File.Sha512!,
                            artifact.File.Sha1,
                            bytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is IOException or HttpRequestException or InvalidDataException or
                    UnauthorizedAccessException)
                {
                    var fallback = new ModrinthClientContentFallback(
                        artifact.ProjectId,
                        artifact.VersionId,
                        artifact.ProjectTitle,
                        ModrinthClientContentFallbackReason.DownloadFailed,
                        "The verified automatic download failed. Use the official link to download this exact release.",
                        artifact.VersionPageUri,
                        artifact.File.DownloadUri);
                    var fallbacks = plan.Fallbacks.Append(fallback).ToArray();
                    var failedPlan = plan with { Fallbacks = fallbacks };
                    return new ModrinthClientContentInstallResult(failedPlan, [], fallbacks);
                }

                downloaded.Add(destination);
            }

            progress?.Report(new ModrinthClientContentInstallProgress(
                "commit",
                "Safely adding verified content to the client instance…",
                plan.Artifacts.Count,
                plan.Artifacts.Count));
            using var manager = new MinecraftClientContentManager(instanceRoot);
            var imported = await manager.ImportAsync(
                    new MinecraftClientContentImportRequest(request.Kind, downloaded),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new ModrinthClientContentInstallProgress(
                "complete",
                $"Installed {imported.ImportedEntries.Count} verified item(s).",
                plan.Artifacts.Count,
                plan.Artifacts.Count));
            return new ModrinthClientContentInstallResult(plan, imported.ImportedEntries, []);
        }
        finally
        {
            TryDeleteOperation(operationRoot);
            _mutationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _mutationGate.Dispose();
        _disposed = true;
    }

    private async Task VisitAsync(
        ModrinthClientContentProject project,
        ModrinthClientContentVersion version,
        MinecraftClientContentKind kind,
        bool isDependency,
        int depth,
        PlanningState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > MaximumDependencyDepth || state.SelectedVersions.Count >= MaximumArtifacts)
        {
            state.Fallbacks.Add(CreateFallback(
                project,
                version,
                ModrinthClientContentFallbackReason.UnresolvedDependency,
                "The required dependency graph exceeds the safe automatic-install limit."));
            return;
        }

        if (!string.Equals(project.ProjectId, version.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Modrinth dependency metadata points to a different project.");
        }

        if (state.SelectedVersions.TryGetValue(project.ProjectId, out var selectedVersion))
        {
            if (!string.Equals(selectedVersion, version.VersionId, StringComparison.Ordinal))
            {
                state.Fallbacks.Add(CreateFallback(
                    project,
                    version,
                    ModrinthClientContentFallbackReason.DependencyConflict,
                    "Two required dependencies request different releases of the same project."));
            }

            return;
        }

        state.SelectedVersions.Add(project.ProjectId, version.VersionId);
        if (!state.VisitingVersions.Add(version.VersionId))
        {
            return;
        }

        try
        {
            if (!IsCompatible(version, state.GameVersion, state.RequiredLoader, kind))
            {
                state.Fallbacks.Add(CreateFallback(
                    project,
                    version,
                    ModrinthClientContentFallbackReason.UnresolvedDependency,
                    "The required release is not compatible with the selected Minecraft version and loader."));
                return;
            }

            if (kind == MinecraftClientContentKind.Mod)
            {
                foreach (var dependency in version.Dependencies.Where(static dependency =>
                             dependency.Kind == ModrinthClientDependencyKind.Required))
                {
                    await VisitDependencyAsync(
                            project,
                            version,
                            dependency,
                            depth + 1,
                            state,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            AddArtifactOrFallback(project, version, kind, isDependency, state);
        }
        finally
        {
            state.VisitingVersions.Remove(version.VersionId);
        }
    }

    private async Task VisitDependencyAsync(
        ModrinthClientContentProject parentProject,
        ModrinthClientContentVersion parentVersion,
        ModrinthClientContentDependency dependency,
        int depth,
        PlanningState state,
        CancellationToken cancellationToken)
    {
        if (dependency.ProjectId is null && dependency.VersionId is null)
        {
            state.Fallbacks.Add(CreateFallback(
                parentProject,
                parentVersion,
                ModrinthClientContentFallbackReason.UnresolvedDependency,
                $"A required dependency ({dependency.FileName ?? "unknown file"}) has no resolvable project or version identifier."));
            return;
        }

        try
        {
            ModrinthClientContentVersion dependencyVersion;
            string dependencyProjectId;
            if (dependency.VersionId is { } versionId)
            {
                dependencyVersion = await _catalog.GetStableVersionAsync(versionId, cancellationToken)
                    .ConfigureAwait(false);
                dependencyProjectId = dependency.ProjectId ?? dependencyVersion.ProjectId;
                if (!string.Equals(dependencyProjectId, dependencyVersion.ProjectId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("A dependency project and version identifier do not match.");
                }
            }
            else
            {
                dependencyProjectId = dependency.ProjectId!;
                dependencyVersion = await _catalog.SelectStableVersionAsync(
                        dependencyProjectId,
                        state.GameVersion,
                        state.RequiredLoader,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var dependencyProject = await _catalog.GetProjectAsync(
                    dependencyProjectId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (dependencyProject.Kind != MinecraftClientContentKind.Mod)
            {
                throw new InvalidDataException("A required mod dependency is not a Modrinth mod project.");
            }

            await VisitAsync(
                    dependencyProject,
                    dependencyVersion,
                    MinecraftClientContentKind.Mod,
                    isDependency: true,
                    depth,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or InvalidDataException or HttpRequestException or
            ArgumentException)
        {
            state.Fallbacks.Add(CreateFallback(
                parentProject,
                parentVersion,
                ModrinthClientContentFallbackReason.UnresolvedDependency,
                "A required dependency could not be resolved automatically; review it on the official version page."));
        }
    }

    private static void AddArtifactOrFallback(
        ModrinthClientContentProject project,
        ModrinthClientContentVersion version,
        MinecraftClientContentKind kind,
        bool isDependency,
        PlanningState state)
    {
        var expectedExtension = kind == MinecraftClientContentKind.Mod ? ".jar" : ".zip";
        var matchingFiles = version.Files
            .Where(file => file.FileName.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static file => file.Primary)
            .ToArray();
        var selected = matchingFiles.FirstOrDefault(IsVerifiedFile);
        if (selected is null)
        {
            var direct = matchingFiles.FirstOrDefault()?.DownloadUri;
            state.Fallbacks.Add(CreateFallback(
                project,
                version,
                matchingFiles.Length == 0
                    ? ModrinthClientContentFallbackReason.UnsupportedFile
                    : ModrinthClientContentFallbackReason.MissingVerifiedFile,
                matchingFiles.Length == 0
                    ? $"The stable release has no supported {expectedExtension} file."
                    : "The stable release has no official file with a valid SHA-512 value.",
                direct));
            return;
        }

        if (state.FileNames.TryGetValue(selected.FileName, out var owner) &&
            !string.Equals(owner, project.ProjectId, StringComparison.Ordinal))
        {
            state.Fallbacks.Add(CreateFallback(
                project,
                version,
                ModrinthClientContentFallbackReason.FileNameConflict,
                "Two selected projects use the same destination file name.",
                selected.DownloadUri));
            return;
        }

        state.FileNames[selected.FileName] = project.ProjectId;
        state.Artifacts.Add(new ModrinthClientContentArtifact(
            project.ProjectId,
            project.Slug,
            project.Title,
            kind,
            version.VersionId,
            version.VersionNumber,
            selected,
            BuildVersionPageUri(project, version.VersionId),
            isDependency));
    }

    private static bool IsVerifiedFile(ModrinthClientContentFile file)
        => file.Size is > 0 and <= MaximumArtifactBytes &&
           ModrinthClientContentCatalog.IsOfficialCdnUri(file.DownloadUri) &&
           IsHash(file.Sha512, 128) && (file.Sha1 is null || IsHash(file.Sha1, 40));

    private static bool IsHash(string? value, int length)
        => value is not null && value.Length == length && value.All(Uri.IsHexDigit);

    private static bool IsCompatible(
        ModrinthClientContentVersion version,
        string gameVersion,
        MinecraftClientLoader? loader,
        MinecraftClientContentKind kind)
    {
        if (!version.GameVersions.Contains(gameVersion, StringComparer.Ordinal))
        {
            return false;
        }

        return kind != MinecraftClientContentKind.Mod || loader is null ||
               version.Loaders.Contains(
                   ModrinthClientContentCatalog.GetLoaderName(loader.Value),
                   StringComparer.Ordinal);
    }

    private static MinecraftClientLoader? SelectPreferredLoader(
        IReadOnlyCollection<MinecraftClientLoader> loaders)
    {
        MinecraftClientLoader[] preference =
        [
            MinecraftClientLoader.NeoForge,
            MinecraftClientLoader.Forge,
            MinecraftClientLoader.Fabric,
            MinecraftClientLoader.Quilt,
            MinecraftClientLoader.Vanilla,
        ];
        return preference.FirstOrDefault(loaders.Contains) is var selected && loaders.Contains(selected)
            ? selected
            : null;
    }

    private static ModrinthClientContentFallback CreateFallback(
        ModrinthClientContentProject project,
        ModrinthClientContentVersion version,
        ModrinthClientContentFallbackReason reason,
        string message,
        Uri? directDownloadUri = null)
        => new(
            project.ProjectId,
            version.VersionId,
            project.Title,
            reason,
            message,
            BuildVersionPageUri(project, version.VersionId),
            ModrinthClientContentCatalog.IsOfficialCdnUri(directDownloadUri)
                ? directDownloadUri
                : null);

    private static Uri BuildVersionPageUri(
        ModrinthClientContentProject project,
        string versionId)
        => new($"{project.ProjectPageUri.AbsoluteUri.TrimEnd('/')}/version/{Uri.EscapeDataString(versionId)}");

    private static void ValidateInstallRequest(ModrinthClientContentInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSelection(request.ProjectId, request.Kind, request.GameVersion, request.Loader);
        if (request.Kind == MinecraftClientContentKind.Mod && request.Loader is null)
        {
            throw new ArgumentException(
                "Installing a mod requires the client instance's actual loader. Use PlanAsync first to discover the required loader.",
                nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceDirectory);
        if (!Path.IsPathFullyQualified(request.InstanceDirectory) ||
            !Directory.Exists(request.InstanceDirectory))
        {
            throw new DirectoryNotFoundException(
                "Minecraft client instance directory must be an existing absolute directory.");
        }
    }

    private static void ValidateSelection(
        string projectId,
        MinecraftClientContentKind kind,
        string gameVersion,
        MinecraftClientLoader? loader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        if (kind is not (MinecraftClientContentKind.Mod or MinecraftClientContentKind.ResourcePack or
            MinecraftClientContentKind.ShaderPack))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Only mods, resource packs and shader packs can be installed from Modrinth.");
        }

        if (gameVersion.Length > 64 || gameVersion.Trim().Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException("Minecraft version is invalid.", nameof(gameVersion));
        }

        if (loader is { } selectedLoader)
        {
            _ = ModrinthClientContentCatalog.GetLoaderName(selectedLoader);
        }
    }

    private static void EnsureRootsDoNotOverlap(string first, string second)
    {
        if (IsUnder(first, second) || IsUnder(second, first))
        {
            throw new UnauthorizedAccessException(
                "The content staging directory and Minecraft instance directory must be separate.");
        }
    }

    private static bool IsUnder(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
               (!relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !Path.IsPathFullyQualified(relative));
    }

    private static void RejectReparsePoint(string path, string label)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException($"{label} cannot be a reparse point: '{path}'.");
        }
    }

    private void TryDeleteOperation(string operationRoot)
    {
        try
        {
            if (Directory.Exists(operationRoot))
            {
                SafePath.DeleteTreeWithoutFollowingReparsePoints(_stagingRoot, operationRoot);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class PlanningState(
        string gameVersion,
        MinecraftClientLoader? requiredLoader)
    {
        public string GameVersion { get; } = gameVersion;

        public MinecraftClientLoader? RequiredLoader { get; } = requiredLoader;

        public Dictionary<string, string> SelectedVersions { get; } =
            new(StringComparer.Ordinal);

        public HashSet<string> VisitingVersions { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> FileNames { get; } =
            new(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        public List<ModrinthClientContentArtifact> Artifacts { get; } = [];

        public List<ModrinthClientContentFallback> Fallbacks { get; } = [];
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
