using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Production composition for the FTB and Modrinth modpack sources. Every installation is created
/// in a manager-owned staging directory and is promoted to its final name only after static launch
/// detection succeeds. Retained compatibility code for other providers is not reachable through
/// the public production workflow entries.
/// </summary>
public sealed partial class OnlineModpackWorkflow : IOnlineModpackWorkflow, IDisposable
{
    private const string UserAgent = "MuhunMCSVManager/1.0 (Windows; modpack-installer)";
    private const int MinimumStrictOnlineJarConfidence = 80;
    private readonly ApplicationPaths _paths;
    private readonly HttpClient _ftbCatalogClient = new();
    private readonly HttpClient _ftbDownloadClient = new();
    private readonly HttpClient _modrinthApiClient;
    private readonly HttpClient _modrinthDownloadClient;
    private readonly HttpClient _modrinthLoaderClient = new();
    private readonly HttpClient _javaRuntimeClient = new();
    private readonly HttpClient _curseApiClient;
    private readonly HttpClient _curseDownloadClient = new();
    private readonly FtbCatalogProvider _ftbCatalog;
    private readonly FtbInstallerDownloader _ftbDownloader;
    private readonly FtbServerInstaller _ftbInstaller;
    private readonly ModrinthModpackProvider _modrinthCatalog;
    private readonly ModrinthModpackInstaller _modrinthInstaller;
    private readonly ModrinthLoaderServerBootstrapper _modrinthLoaderBootstrapper;
    private readonly IModrinthJavaRuntimeResolver _modrinthJavaRuntimeResolver;
    private readonly CurseForgeModpackProvider _curseForge;
    private readonly CurseForgeModpackManifestInspector _curseManifestInspector;
    private readonly IOnlineModpackArtworkCache _artworkCache;
    private readonly OnlineModpackArtworkDecoder _artworkDecoder = new();
    private readonly bool _ownsArtworkCache;
    private readonly Action<string>? _afterStagingPromotedForTesting;
    private readonly BackupRestoreService _archiveRestore = new();
    private readonly ServerPackDetector _serverPackDetector = new();
    private readonly JarCoreDetector _jarDetector = new();
    private readonly JavaVersionRecommendationService _javaRecommendation = new();
    private bool _disposed;

    public OnlineModpackWorkflow(ApplicationPaths paths)
        : this(paths, null, null, null, null, null, null)
    {
    }

    internal OnlineModpackWorkflow(
        ApplicationPaths paths,
        ModrinthModpackProvider? modrinthCatalog,
        ModrinthModpackInstaller? modrinthInstaller,
        ModrinthLoaderServerBootstrapper? modrinthLoaderBootstrapper,
        IModrinthJavaRuntimeResolver? modrinthJavaRuntimeResolver,
        CurseForgeModpackProvider? curseForge = null,
        CurseForgeModpackManifestInspector? curseManifestInspector = null,
        Action<string>? afterStagingPromotedForTesting = null,
        FtbCatalogProvider? ftbCatalog = null,
        IOnlineModpackArtworkCache? artworkCache = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _artworkCache = artworkCache ?? new OnlineModpackArtworkCache(_paths);
        _ownsArtworkCache = artworkCache is null;
        _modrinthApiClient = new HttpClient(new SocketsHttpHandler
        {
            // Catalogue calls never need redirects. Keeping them visible lets the provider reject
            // every 3xx response before another origin receives a follow-up request.
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });
        _modrinthDownloadClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });
        _curseApiClient = new HttpClient(new SocketsHttpHandler
        {
            // x-api-key is attached per request. Redirects must fail closed so HttpClient can never
            // replay that request header to a different origin.
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });
        _ftbCatalog = ftbCatalog ?? new FtbCatalogProvider(_ftbCatalogClient, UserAgent);
        _ftbDownloader = new FtbInstallerDownloader(
            _ftbDownloadClient,
            UserAgent,
            new WindowsFtbExecutableSignatureVerifier());
        _ftbInstaller = new FtbServerInstaller(
            new FtbInstallerProcessRunner(),
            new FtbInstalledServerValidator(_serverPackDetector));
        _modrinthCatalog = modrinthCatalog
            ?? new ModrinthModpackProvider(_modrinthApiClient, UserAgent);
        _modrinthInstaller = modrinthInstaller ?? new ModrinthModpackInstaller(
            new ModrinthModpackArtifactDownloader(
                new HttpClientModrinthModpackHttpTransport(_modrinthDownloadClient)));
        _modrinthLoaderBootstrapper = modrinthLoaderBootstrapper
            ?? new ModrinthLoaderServerBootstrapper(
                new ModrinthOfficialLoaderArtifactProvider(_modrinthLoaderClient, UserAgent),
                new ModrinthLoaderBootstrapProcessRunner());
        _modrinthJavaRuntimeResolver = modrinthJavaRuntimeResolver
            ?? new ManagedModrinthJavaRuntimeResolver(
                _paths,
                new AdoptiumRuntimeProvider(_javaRuntimeClient, UserAgent));
        _curseForge = curseForge ?? new CurseForgeModpackProvider(
            _curseApiClient,
            _curseDownloadClient,
            UserAgent);
        _curseManifestInspector = curseManifestInspector
            ?? new CurseForgeModpackManifestInspector();
        _afterStagingPromotedForTesting = afterStagingPromotedForTesting;
    }

    public IOnlineModpackArtworkCache ArtworkCache => _artworkCache;

    public async Task<IReadOnlyList<OnlineModpackSearchResult>> SearchAsync(
        OnlineModpackProvider provider,
        string query,
        SecureString? transientApiKey,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return await BrowseAsync(
                new OnlineModpackBrowseRequest(
                    provider,
                    Query: query.Trim(),
                    Limit: provider == OnlineModpackProvider.Ftb ? 8 : 20),
                transientApiKey,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OnlineModpackSearchResult>> GetFeaturedAsync(
        OnlineModpackProvider provider,
        SecureString? transientApiKey,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await BrowseAsync(
                new OnlineModpackBrowseRequest(
                    provider,
                    Sort: OnlineModpackSort.Downloads,
                    Limit: provider == OnlineModpackProvider.Ftb ? 12 : 20),
                transientApiKey,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OnlineModpackSearchResult>> BrowseAsync(
        OnlineModpackBrowseRequest request,
        SecureString? transientApiKey,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return request.Provider switch
        {
            OnlineModpackProvider.Ftb => await BrowseFtbAsync(request, cancellationToken)
                .ConfigureAwait(false),
            OnlineModpackProvider.Modrinth => (await _modrinthCatalog.SearchAsync(
                    new ModrinthModpackSearchRequest(
                        Query: request.Query.Trim(),
                        GameVersion: TrimOrNull(request.GameVersion),
                        Loader: TrimOrNull(request.Loader),
                        Offset: request.Offset,
                        Limit: request.Limit,
                        Index: MapModrinthIndex(request.Sort),
                        SourceCategory: TrimOrNull(request.SourceCategory)),
                    cancellationToken)
                .ConfigureAwait(false)).Projects.Select(MapModrinthProject).ToArray(),
            OnlineModpackProvider.CurseForge => await WithApiKeyAsync(
                transientApiKey,
                key => BrowseCurseForgeAsync(key, request, cancellationToken)),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }

    private async Task<IReadOnlyList<OnlineModpackSearchResult>> BrowseCurseForgeAsync(
        string apiKey,
        OnlineModpackBrowseRequest request,
        CancellationToken cancellationToken)
    {
        const int providerPageLimit = 50;
        var results = new List<OnlineModpackSearchResult>(request.Limit);
        var seenModIds = new HashSet<int>();
        var index = request.Offset;
        var scanEndExclusive = checked(request.Offset + request.Limit);
        while (results.Count < request.Limit && index < scanEndExclusive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Bound network work by the requested catalogue window rather than by the count of
            // unique results. A provider that repeats rows can therefore never keep paging
            // indefinitely in an attempt to fill the UI limit.
            var pageSize = Math.Min(providerPageLimit, scanEndExclusive - index);
            var page = await _curseForge.SearchAsync(
                    apiKey,
                    new CurseForgeModpackSearchRequest(
                        Query: request.Query.Trim(),
                        GameVersion: TrimOrNull(request.GameVersion),
                        ModLoader: MapCurseForgeLoader(request.Loader),
                        Index: index,
                        PageSize: pageSize,
                        SortField: MapCurseForgeSort(request.Sort),
                        SortDescending: true,
                        CategoryId: ParseCurseForgeCategory(request.SourceCategory)),
                    cancellationToken)
                .ConfigureAwait(false);

            var received = page.Projects.Count;
            if (received == 0)
            {
                break;
            }

            foreach (var project in page.Projects)
            {
                if (seenModIds.Add(project.ModId))
                {
                    results.Add(MapCurseForgeProject(project));
                    if (results.Count == request.Limit)
                    {
                        break;
                    }
                }
            }

            var nextIndexLong = (long)index + received;
            if (nextIndexLong <= index)
            {
                break;
            }

            var nextIndex = (int)Math.Min(nextIndexLong, scanEndExclusive);
            if (received < pageSize
                || nextIndex >= scanEndExclusive
                || nextIndex >= Math.Max(0, page.Pagination.TotalCount))
            {
                break;
            }

            index = nextIndex;
        }

        return results.Take(request.Limit).ToArray();
    }

    private async Task<IReadOnlyList<OnlineModpackSearchResult>> BrowseFtbAsync(
        OnlineModpackBrowseRequest request,
        CancellationToken cancellationToken)
    {
        // FTB does not currently expose server-side game/loader filters. Hydrate only a bounded
        // candidate window and filter its returned version metadata locally.
        var requestedWindow = request.Offset + request.Limit;
        var hasLocalFilters = !string.IsNullOrWhiteSpace(request.GameVersion)
                              || !string.IsNullOrWhiteSpace(request.Loader);
        var fetchLimit = Math.Min(
            100,
            Math.Max(requestedWindow, hasLocalFilters ? Math.Min(40, requestedWindow * 2) : requestedWindow));
        var page = string.IsNullOrWhiteSpace(request.Query)
            ? await _ftbCatalog.GetFeaturedAsync(fetchLimit, cancellationToken).ConfigureAwait(false)
            : await _ftbCatalog.SearchAsync(
                    request.Query.Trim(),
                    fetchLimit,
                    cancellationToken)
                .ConfigureAwait(false);
        return FilterAndSortFtbPacks(page.Packs, request)
            .Skip(request.Offset)
            .Take(request.Limit)
            .Select(MapFtbProject)
            .ToArray();
    }

    public async Task<IReadOnlyList<OnlineModpackVersion>> GetVersionsAsync(
        OnlineModpackSearchResult project,
        SecureString? transientApiKey,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(project);
        return project.Provider switch
        {
            OnlineModpackProvider.Ftb => await GetFtbVersionsAsync(project, cancellationToken)
                .ConfigureAwait(false),
            OnlineModpackProvider.Modrinth => await GetModrinthVersionsAsync(project, cancellationToken)
                .ConfigureAwait(false),
            OnlineModpackProvider.CurseForge => await WithApiKeyAsync(
                transientApiKey,
                key => GetCurseForgeVersionsAsync(project, key, cancellationToken)),
            _ => throw new ArgumentOutOfRangeException(nameof(project))
        };
    }

    public async Task<ServerInstance> InstallAsync(
        OnlineModpackInstallRequest request,
        SecureString? transientApiKey,
        IProgress<OnlineModpackInstallProgress> progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentNullException.ThrowIfNull(request.Version);
        ValidateInstallRequest(request);
        var installed = request.Project.Provider switch
        {
            OnlineModpackProvider.Ftb => await InstallFtbAsync(request, progress, cancellationToken)
                .ConfigureAwait(false),
            OnlineModpackProvider.CurseForge => await WithApiKeyAsync(
                transientApiKey,
                key => InstallCurseForgeAsync(request, key, progress, cancellationToken)),
            OnlineModpackProvider.Modrinth => await InstallModrinthAsync(
                    request,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        await AttachCatalogArtworkAsync(installed, request, cancellationToken).ConfigureAwait(false);
        return installed;
    }

    private async Task<IReadOnlyList<OnlineModpackVersion>> GetFtbVersionsAsync(
        OnlineModpackSearchResult project,
        CancellationToken cancellationToken)
    {
        var packId = ParsePositiveInt(project.ProjectId, "FTB Pack ID");
        var pack = await _ftbCatalog.GetPackAsync(packId, cancellationToken).ConfigureAwait(false);
        return pack.Versions
            .Where(version => version.Type is "release" or "beta" or "alpha")
            .Select(version => new OnlineModpackVersion(
                OnlineModpackProvider.Ftb,
                project.ProjectId,
                version.Id.ToString(CultureInfo.InvariantCulture),
                version.Name,
                version.MinecraftVersion ?? L("common.unknown"),
                FormatLoader(version.ModLoaderName, version.ModLoaderVersion),
                version.Type,
                FtbTimestampNormalizer.NormalizeUtc(version.Updated) ?? DateTimeOffset.MinValue,
                HasOfficialServerPack: !pack.IsPrivate))
            .ToArray();
    }

    private async Task<IReadOnlyList<OnlineModpackVersion>> GetModrinthVersionsAsync(
        OnlineModpackSearchResult project,
        CancellationToken cancellationToken)
    {
        var versions = await _modrinthCatalog.GetVersionsAsync(
                project.ProjectId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return versions.Select(version => new OnlineModpackVersion(
                OnlineModpackProvider.Modrinth,
                project.ProjectId,
                version.VersionId,
                $"{version.Name} ({version.VersionNumber})",
                version.GameVersions.FirstOrDefault() ?? L("common.unknown"),
                string.Join(", ", version.Loaders),
                version.VersionType,
                version.DatePublished,
                HasOfficialServerPack: version.MrpackFile is not null))
            .ToArray();
    }

    private async Task<IReadOnlyList<OnlineModpackVersion>> GetCurseForgeVersionsAsync(
        OnlineModpackSearchResult project,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var modId = ParsePositiveInt(project.ProjectId, "CurseForge Mod ID");
        var page = await _curseForge.GetFilesAsync(
                apiKey,
                modId,
                pageSize: 50,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return page.Files
            .Where(file => !file.IsServerPack)
            .OrderByDescending(file => file.FileDate)
            .Select(file => new OnlineModpackVersion(
                OnlineModpackProvider.CurseForge,
                project.ProjectId,
                file.FileId.ToString(CultureInfo.InvariantCulture),
                file.DisplayName,
                FindMinecraftVersion(file.GameVersions),
                FindLoader(file.GameVersions),
                file.ReleaseType switch { 1 => "release", 2 => "beta", 3 => "alpha", _ => "unknown" },
                file.FileDate ?? DateTimeOffset.MinValue,
                HasOfficialServerPack: file.ServerPackFileId is not null))
            .ToArray();
    }

    private async Task<ServerInstance> InstallFtbAsync(
        OnlineModpackInstallRequest request,
        IProgress<OnlineModpackInstallProgress> progress,
        CancellationToken cancellationToken)
    {
        var packId = ParsePositiveInt(request.Project.ProjectId, "FTB Pack ID");
        var versionId = ParsePositiveInt(request.Version.VersionId, "FTB Version ID");
        Directory.CreateDirectory(_paths.Cache);
        Directory.CreateDirectory(_paths.Servers);
        var installerPath = SafePath.CombineUnderRoot(
            _paths.Cache,
            $"ftb-server-installer-{Guid.NewGuid():N}.exe");
        var staging = CreateStagingPath();
        var ownsStaging = false;
        string? ownedFinalRoot = null;
        var completedSuccessfully = false;
        try
        {
            progress.Report(new(
                OnlineModpackInstallStage.Downloading,
                L("online.workflow.ftb.downloadInstaller"),
                5));
            await _ftbDownloader.DownloadLatestWindowsX64Async(
                    installerPath,
                    new Progress<double>(value => progress.Report(new(
                        OnlineModpackInstallStage.Downloading,
                        L("online.workflow.ftb.downloadVerifyInstaller"),
                        5 + value * 25))),
                    cancellationToken)
                .ConfigureAwait(false);
            progress.Report(new(
                OnlineModpackInstallStage.Verifying,
                L("online.workflow.ftb.installerVerified"),
                32));
            var ftbProgress = new FtbInstallerProgressFormatter();
            var output = new Progress<FtbInstallerOutputLine>(line =>
                progress.Report(ftbProgress.Format(line)));
            ownsStaging = true;
            var installed = await _ftbInstaller.InstallAsync(
                    new FtbInstallRequest(packId, versionId, installerPath, staging),
                    output,
                    cancellationToken)
                .ConfigureAwait(false);
            progress.Report(new(
                OnlineModpackInstallStage.DetectingServer,
                L("online.workflow.ftb.detecting"),
                90));
            cancellationToken.ThrowIfCancellationRequested();
            var finalRoot = CommitStaging(staging, request.ServerName);
            ownsStaging = false;
            ownedFinalRoot = finalRoot;
            _afterStagingPromotedForTesting?.Invoke(finalRoot);
            cancellationToken.ThrowIfCancellationRequested();
            var instance = CreateInstanceFromPackDetection(
                request.ServerName,
                installed.Detection,
                staging,
                finalRoot);
            ApplyVerifiedModpackProvenance(
                instance,
                request,
                OnlineModpackProvider.Ftb,
                packId.ToString(CultureInfo.InvariantCulture),
                versionId.ToString(CultureInfo.InvariantCulture),
                installed.Detection.PackVersion);
            cancellationToken.ThrowIfCancellationRequested();
            completedSuccessfully = true;
            ownedFinalRoot = null;
            return instance;
        }
        finally
        {
            try
            {
                await DeleteOwnedFileAsync(
                        _paths.Cache,
                        installerPath,
                        requireCompleteCleanup: !completedSuccessfully)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (ownsStaging)
                {
                    await DeleteOwnedStagingAsync(staging).ConfigureAwait(false);
                }
                else if (ownedFinalRoot is not null)
                {
                    await DeleteOwnedFinalTreeAsync(ownedFinalRoot).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<ServerInstance> InstallCurseForgeAsync(
        OnlineModpackInstallRequest request,
        string apiKey,
        IProgress<OnlineModpackInstallProgress> progress,
        CancellationToken cancellationToken)
    {
        var modId = ParsePositiveInt(request.Project.ProjectId, "CurseForge Mod ID");
        var selectedFileId = ParsePositiveInt(request.Version.VersionId, "CurseForge File ID");
        progress.Report(new(
            OnlineModpackInstallStage.ResolvingMetadata,
            L("online.workflow.curse.resolvingServerPack"),
            5));
        var resolution = await _curseForge.ResolveServerPackAsync(
                apiKey,
                modId,
                selectedFileId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.IsAvailable || resolution.ServerPackFile is null)
        {
            throw new CurseForgeServerPackException(resolution.Status, resolution.Message);
        }

        Directory.CreateDirectory(_paths.Cache);
        Directory.CreateDirectory(_paths.Servers);
        var archivePath = SafePath.CombineUnderRoot(
            _paths.Cache,
            $"curseforge-server-pack-{Guid.NewGuid():N}.zip");
        var clientArchivePath = SafePath.CombineUnderRoot(
            _paths.Cache,
            $"curseforge-client-pack-{Guid.NewGuid():N}.zip");
        var staging = CreateStagingPath();
        var ownsStaging = false;
        string? ownedFinalRoot = null;
        var completedSuccessfully = false;
        try
        {
            await _curseForge.DownloadServerPackAsync(
                    apiKey,
                    modId,
                    resolution.ServerPackFile.FileId,
                    archivePath,
                    new Progress<double>(value => progress.Report(new(
                        OnlineModpackInstallStage.Downloading,
                        L("online.workflow.curse.downloadingServerPack"),
                        10 + value * 30))),
                    cancellationToken)
                .ConfigureAwait(false);
            progress.Report(new(
                OnlineModpackInstallStage.Extracting,
                L("online.workflow.extractingServerPack"),
                42));
            ownsStaging = true;
            await _archiveRestore.RestoreAsync(
                    archivePath,
                    staging,
                    new BackupRestoreOptions { TrustedDestinationRoot = _paths.Servers },
                    progress: new Progress<BackupRestoreProgress>(value => progress.Report(new(
                        OnlineModpackInstallStage.Extracting,
                        L("online.workflow.extractingFiles", value.CompletedFiles, value.TotalFiles),
                        value.TotalFiles == 0
                            ? null
                            : 42 + (double)value.CompletedFiles / value.TotalFiles * 18))),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            progress.Report(new(
                OnlineModpackInstallStage.DetectingServer,
                L("online.workflow.detectingTrustedLaunch"),
                61));
            var detection = await TryDetectStrictOnlineServerAsync(
                    staging,
                    expectedLoader: null,
                    cancellationToken)
                .ConfigureAwait(false);
            int? javaMajorVersion = null;
            string? javaExecutable = null;
            if (detection is null)
            {
                progress.Report(new(
                    OnlineModpackInstallStage.Downloading,
                    L("online.workflow.curse.downloadingClientManifest"),
                    62));
                await _curseForge.DownloadVerifiedFileAsync(
                        apiKey,
                        modId,
                        selectedFileId,
                        CurseForgeModpackFileRole.ClientPack,
                        clientArchivePath,
                        new Progress<double>(value => progress.Report(new(
                            OnlineModpackInstallStage.Downloading,
                            L("online.workflow.curse.downloadingClientMetadata"),
                            62 + value * 8))),
                        cancellationToken)
                    .ConfigureAwait(false);
                progress.Report(new(
                    OnlineModpackInstallStage.Verifying,
                    L("online.workflow.curse.readingManifest"),
                    71));
                var manifest = await _curseManifestInspector.InspectAsync(
                        clientArchivePath,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var loaderRequest = manifest.LoaderInstallRequest;
                if (loaderRequest.Kind == ModrinthModpackLoaderKind.Quilt)
                {
                    throw new ModrinthLoaderUnsupportedException(
                        ModrinthModpackLoaderKind.Quilt,
                        L("online.workflow.quiltUnsupported"));
                }

                var recommendation = _javaRecommendation.GetRecommendation(
                    manifest.MinecraftVersion,
                    MapModrinthLoaderCore(loaderRequest.Kind));
                javaMajorVersion = recommendation.MajorVersion;
                progress.Report(new(
                    OnlineModpackInstallStage.InstallingLoader,
                    L("online.workflow.preparingJava", recommendation.MajorVersion),
                    72));
                javaExecutable = await _modrinthJavaRuntimeResolver.ResolveAsync(
                        recommendation.MajorVersion,
                        new Progress<double>(value => progress.Report(new(
                            OnlineModpackInstallStage.Downloading,
                            L("online.workflow.resolvingJava", recommendation.MajorVersion),
                            72 + Math.Clamp(value, 0d, 1d) * 6))),
                        cancellationToken)
                    .ConfigureAwait(false);

                progress.Report(new(
                    OnlineModpackInstallStage.InstallingLoader,
                    L("online.workflow.installingLoader", loaderRequest.Kind),
                    79));
                await _modrinthLoaderBootstrapper.BootstrapAsync(
                        loaderRequest,
                        staging,
                        javaExecutable,
                        progress: new Progress<ModrinthLoaderBootstrapProgress>(value =>
                            ReportCurseLoaderProgress(progress, value)),
                        processOutput: new Progress<ModrinthLoaderBootstrapOutputLine>(line => progress.Report(new(
                            OnlineModpackInstallStage.InstallingLoader,
                            SanitizeProgressText(line.Text, L("online.workflow.curse.loaderInstalling")),
                            null))),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                progress.Report(new(
                    OnlineModpackInstallStage.DetectingServer,
                    L("online.workflow.loaderInstalledDetecting"),
                    95));
                detection = await TryDetectStrictOnlineServerAsync(
                        staging,
                        loaderRequest,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (detection is null)
                {
                    throw new InvalidDataException(
                        L("online.workflow.curse.launchNotFound"));
                }
            }

            progress.Report(new(
                OnlineModpackInstallStage.Finalizing,
                L("online.workflow.serverPackFinalizing"),
                97));
            cancellationToken.ThrowIfCancellationRequested();
            var finalRoot = CommitStaging(staging, request.ServerName);
            ownsStaging = false;
            ownedFinalRoot = finalRoot;
            _afterStagingPromotedForTesting?.Invoke(finalRoot);
            cancellationToken.ThrowIfCancellationRequested();
            var instance = CreateInstanceFromDetection(request.ServerName, detection, staging, finalRoot);
            if (javaMajorVersion is not null)
            {
                instance.JavaMajorVersion ??= javaMajorVersion;
            }

            if (!string.IsNullOrWhiteSpace(javaExecutable)
                && (string.IsNullOrWhiteSpace(instance.JavaExecutablePath)
                    || !File.Exists(instance.JavaExecutablePath)))
            {
                instance.JavaExecutablePath = javaExecutable;
            }

            ApplyVerifiedModpackProvenance(
                instance,
                request,
                OnlineModpackProvider.CurseForge,
                modId.ToString(CultureInfo.InvariantCulture),
                selectedFileId.ToString(CultureInfo.InvariantCulture),
                resolution.SelectedFile?.DisplayName);

            cancellationToken.ThrowIfCancellationRequested();
            completedSuccessfully = true;
            ownedFinalRoot = null;
            return instance;
        }
        finally
        {
            try
            {
                try
                {
                    await DeleteOwnedFileAsync(
                            _paths.Cache,
                            archivePath,
                            requireCompleteCleanup: !completedSuccessfully)
                        .ConfigureAwait(false);
                }
                finally
                {
                    await DeleteOwnedFileAsync(
                            _paths.Cache,
                            clientArchivePath,
                            requireCompleteCleanup: !completedSuccessfully)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                if (ownsStaging)
                {
                    await DeleteOwnedStagingAsync(staging).ConfigureAwait(false);
                }
                else if (ownedFinalRoot is not null)
                {
                    await DeleteOwnedFinalTreeAsync(ownedFinalRoot).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<ServerInstance> InstallModrinthAsync(
        OnlineModpackInstallRequest request,
        IProgress<OnlineModpackInstallProgress> progress,
        CancellationToken cancellationToken)
    {
        var apiVersion = await _modrinthCatalog.GetVersionAsync(
                request.Version.VersionId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!apiVersion.ProjectId.Equals(request.Project.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(L("online.workflow.error.modrinthProjectMismatch"));
        }

        Directory.CreateDirectory(_paths.Servers);
        var staging = CreateStagingPath();
        Directory.CreateDirectory(staging);
        var ownsStaging = true;
        string? ownedFinalRoot = null;
        try
        {
            var installResult = await _modrinthInstaller.InstallAsync(
                    apiVersion,
                    staging,
                    progress: new Progress<ModrinthModpackInstallProgress>(value =>
                        progress.Report(MapModrinthPackProgress(value))),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (installResult.LoaderInstallRequest.Kind == ModrinthModpackLoaderKind.Quilt)
            {
                throw new ModrinthLoaderUnsupportedException(
                    ModrinthModpackLoaderKind.Quilt,
                    L("online.workflow.quiltUnsupported"));
            }

            var recommendation = _javaRecommendation.GetRecommendation(
                installResult.MinecraftVersion,
                MapModrinthLoaderCore(installResult.LoaderInstallRequest.Kind));
            progress.Report(new(
                OnlineModpackInstallStage.InstallingLoader,
                L("online.workflow.preparingJava", recommendation.MajorVersion),
                62));
            var javaExecutable = await _modrinthJavaRuntimeResolver.ResolveAsync(
                    recommendation.MajorVersion,
                    new Progress<double>(value => progress.Report(new(
                        OnlineModpackInstallStage.Downloading,
                        L("online.workflow.resolvingJava", recommendation.MajorVersion),
                        62 + Math.Clamp(value, 0d, 1d) * 8))),
                    cancellationToken)
                .ConfigureAwait(false);

            progress.Report(new(
                OnlineModpackInstallStage.InstallingLoader,
                L("online.workflow.installingLoader", installResult.LoaderInstallRequest.Kind),
                70));
            await _modrinthLoaderBootstrapper.BootstrapAsync(
                    installResult.LoaderInstallRequest,
                    staging,
                    javaExecutable,
                    progress: new Progress<ModrinthLoaderBootstrapProgress>(value =>
                        ReportModrinthLoaderProgress(progress, value)),
                    processOutput: new Progress<ModrinthLoaderBootstrapOutputLine>(line => progress.Report(new(
                        OnlineModpackInstallStage.InstallingLoader,
                        SanitizeProgressText(line.Text, L("online.workflow.modrinth.loaderInstalling")),
                        null))),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            progress.Report(new(
                OnlineModpackInstallStage.DetectingServer,
                L("online.workflow.loaderInstalledDetecting"),
                91));
            var detection = await TryDetectStrictOnlineServerAsync(
                    staging,
                    installResult.LoaderInstallRequest,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    L("online.workflow.modrinth.launchNotFound"));
            progress.Report(new(
                OnlineModpackInstallStage.Finalizing,
                L("online.workflow.modrinth.finalizing"),
                96));
            cancellationToken.ThrowIfCancellationRequested();
            var finalRoot = CommitStaging(staging, request.ServerName);
            ownsStaging = false;
            ownedFinalRoot = finalRoot;
            _afterStagingPromotedForTesting?.Invoke(finalRoot);
            cancellationToken.ThrowIfCancellationRequested();
            var instance = CreateInstanceFromDetection(request.ServerName, detection, staging, finalRoot);
            instance.JavaMajorVersion ??= recommendation.MajorVersion;
            if (string.IsNullOrWhiteSpace(instance.JavaExecutablePath)
                || !File.Exists(instance.JavaExecutablePath))
            {
                instance.JavaExecutablePath = javaExecutable;
            }

            ApplyVerifiedModpackProvenance(
                instance,
                request,
                OnlineModpackProvider.Modrinth,
                apiVersion.ProjectId,
                apiVersion.VersionId,
                apiVersion.VersionNumber);

            cancellationToken.ThrowIfCancellationRequested();
            ownedFinalRoot = null;
            return instance;
        }
        finally
        {
            if (ownsStaging)
            {
                await DeleteOwnedStagingAsync(staging).ConfigureAwait(false);
            }
            else if (ownedFinalRoot is not null)
            {
                await DeleteOwnedFinalTreeAsync(ownedFinalRoot).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Online packs frequently contain launcher wrappers, tools, or ordinary mod JARs. Only a
    /// statically validated installed argument-file layout or a recognized standard server JAR name
    /// matching the expected loader is accepted. No candidate is executed during detection.
    /// </summary>
    private async Task<InstalledDetection?> TryDetectStrictOnlineServerAsync(
        string root,
        ModrinthModpackLoaderInstallRequest? expectedLoader,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string> { root };
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                     .Take(16))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
            {
                candidates.Add(directory);
            }
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pack = await _serverPackDetector.DetectAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            if (pack.IsRecognized
                && pack.IsRunnable
                && MatchesExpectedLoader(
                    pack.CoreType,
                    pack.MinecraftVersion,
                    pack.ModLoaderVersion,
                    expectedLoader))
            {
                try
                {
                    await OnlineServerPackSafetyValidator.ValidateAsync(pack, cancellationToken)
                        .ConfigureAwait(false);
                    return new InstalledDetection(candidate, pack, null, null);
                }
                catch (InvalidDataException)
                {
                    // Continue to the strict standard-JAR gate or official-loader fallback. The
                    // online pack's argument files are never executed to probe whether they work.
                }
            }

            var jars = Directory.EnumerateFiles(candidate, "*.jar", SearchOption.TopDirectoryOnly)
                .Where(path => IsStandardOnlineServerJarName(Path.GetFileName(path)))
                .OrderBy(path => GetJarPreference(Path.GetFileName(path)))
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
            foreach (var jar in jars)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var jarDetection = await _jarDetector.DetectAsync(jar, cancellationToken)
                    .ConfigureAwait(false);
                if (!jarDetection.IsValidJar
                    || !jarDetection.IsRecognized
                    || jarDetection.ConfidencePercent < MinimumStrictOnlineJarConfidence
                    || !StandardJarNameMatchesCore(Path.GetFileName(jar), jarDetection.CoreType)
                    || !MatchesExpectedLoader(
                        jarDetection.CoreType,
                        jarDetection.MinecraftVersion,
                        modLoaderVersion: null,
                        expectedLoader))
                {
                    continue;
                }

                return new InstalledDetection(candidate, null, jar, jarDetection);
            }
        }

        return null;
    }

    private static bool IsStandardOnlineServerJarName(string fileName)
        => fileName.Equals("server.jar", StringComparison.OrdinalIgnoreCase)
           || fileName.Equals("fabric-server-launch.jar", StringComparison.OrdinalIgnoreCase)
           || fileName.StartsWith("forge-", StringComparison.OrdinalIgnoreCase)
               && !fileName.Contains("installer", StringComparison.OrdinalIgnoreCase)
           || fileName.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase)
               && !fileName.Contains("installer", StringComparison.OrdinalIgnoreCase);

    private static bool StandardJarNameMatchesCore(string fileName, CoreType coreType)
        => fileName.Equals("server.jar", StringComparison.OrdinalIgnoreCase)
            ? coreType == CoreType.Vanilla
            : fileName.Equals("fabric-server-launch.jar", StringComparison.OrdinalIgnoreCase)
                ? coreType == CoreType.Fabric
                : fileName.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase)
                    ? coreType == CoreType.NeoForge
                    : fileName.StartsWith("forge-", StringComparison.OrdinalIgnoreCase)
                      && coreType == CoreType.Forge;

    private static bool MatchesExpectedLoader(
        CoreType actualCore,
        string? actualMinecraftVersion,
        string? modLoaderVersion,
        ModrinthModpackLoaderInstallRequest? expected)
    {
        if (expected is null)
        {
            return actualCore is CoreType.Vanilla or CoreType.Fabric or CoreType.Forge or CoreType.NeoForge;
        }

        if (actualCore != MapModrinthLoaderCore(expected.Kind))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(actualMinecraftVersion)
            && !actualMinecraftVersion.Equals(expected.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(modLoaderVersion)
            || string.IsNullOrWhiteSpace(expected.LoaderVersion))
        {
            return true;
        }

        return modLoaderVersion.Equals(expected.LoaderVersion, StringComparison.OrdinalIgnoreCase)
               || modLoaderVersion.Equals(
                   $"{expected.MinecraftVersion}-{expected.LoaderVersion}",
                   StringComparison.OrdinalIgnoreCase);
    }

    private ServerInstance CreateInstanceFromDetection(
        string name,
        InstalledDetection detection,
        string oldRoot,
        string finalRoot)
        => detection.Pack is { } pack
            ? CreateInstanceFromPackDetection(name, pack, oldRoot, finalRoot)
            : CreateInstanceFromJarDetection(name, detection, oldRoot, finalRoot);

    private static ServerInstance CreateInstanceFromPackDetection(
        string name,
        ServerPackDetectionResult detection,
        string oldRoot,
        string finalRoot)
    {
        var finalDirectory = RemapPath(oldRoot, finalRoot, detection.DirectoryPath)
            ?? throw new InvalidDataException(L("online.workflow.error.packPathMap"));
        return new ServerInstance
        {
            Name = name.Trim(),
            DirectoryPath = finalDirectory,
            ServerJarPath = string.Empty,
            LaunchKind = ServerLaunchKind.JavaArgumentFiles,
            JavaArgumentFilePaths = [.. detection.JavaArgumentFilePaths],
            SourceLaunchScriptPath = RemapPath(oldRoot, finalRoot, detection.SourceLaunchScriptPath),
            CoreType = detection.CoreType,
            MinecraftVersion = detection.MinecraftVersion,
            JavaMajorVersion = detection.JavaMajorVersion,
            JavaExecutablePath = RemapPath(oldRoot, finalRoot, detection.JavaExecutablePath)
                ?? detection.JavaExecutablePath,
            MinimumMemoryMb = detection.MinimumMemoryMb ?? 1024,
            MaximumMemoryMb = detection.MaximumMemoryMb ?? 4096,
            ServerArguments = [.. detection.ServerArguments]
        };
    }

    private ServerInstance CreateInstanceFromJarDetection(
        string name,
        InstalledDetection installed,
        string oldRoot,
        string finalRoot)
    {
        var jar = RemapPath(oldRoot, finalRoot, installed.JarPath)
            ?? throw new InvalidDataException(L("online.workflow.error.jarPathMap"));
        var directory = RemapPath(oldRoot, finalRoot, installed.DirectoryPath)
            ?? throw new InvalidDataException(L("online.workflow.error.folderPathMap"));
        var detection = installed.JarDetection
            ?? throw new InvalidOperationException(L("online.workflow.error.jarDetectionMissing"));
        var recommendation = _javaRecommendation.GetRecommendation(detection.MinecraftVersion);
        return new ServerInstance
        {
            Name = name.Trim(),
            DirectoryPath = directory,
            ServerJarPath = jar,
            LaunchKind = ServerLaunchKind.ExecutableJar,
            CoreType = detection.IsRecognized ? detection.CoreType : CoreType.CustomJar,
            MinecraftVersion = detection.MinecraftVersion,
            JavaMajorVersion = recommendation.RequiresUserConfirmation ? null : recommendation.MajorVersion
        };
    }

    private string CreateStagingPath()
        => SafePath.CombineUnderRoot(_paths.Servers, $".installing-{Guid.NewGuid():N}");

    private string CommitStaging(string staging, string preferredName)
    {
        var source = SafePath.EnsureWithinRoot(_paths.Servers, staging);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        SafePath.EnsureTreeContainsNoReparsePoints(source);
        return ServerDirectoryPromotion.PromoteToUniqueDirectory(
            _paths.Servers,
            source,
            preferredName);
    }

    private static string? RemapPath(string oldRoot, string newRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var oldFull = Path.GetFullPath(oldRoot);
        var candidate = Path.GetFullPath(path);
        if (!SafePath.IsWithinRoot(oldFull, candidate)) return null;
        return SafePath.CombineUnderRoot(newRoot, Path.GetRelativePath(oldFull, candidate));
    }

    internal static IReadOnlyList<FtbPack> FilterAndSortFtbPacks(
        IReadOnlyList<FtbPack> packs,
        OnlineModpackBrowseRequest request)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        // FTB's current catalogue response has no category identifiers. Returning no matches is
        // safer and less surprising than silently ignoring a selected source category.
        if (!string.IsNullOrWhiteSpace(request.SourceCategory))
        {
            return [];
        }

        IEnumerable<FtbPack> filtered = packs.Where(static pack => !pack.IsPrivate);
        if (!string.IsNullOrWhiteSpace(request.GameVersion))
        {
            var gameVersion = request.GameVersion.Trim();
            filtered = filtered.Where(pack => pack.Versions.Any(version => IsBrowsableFtbVersion(version)
                &&
                version.MinecraftVersion?.Equals(gameVersion, StringComparison.OrdinalIgnoreCase) == true));
        }

        if (!string.IsNullOrWhiteSpace(request.Loader))
        {
            var loader = NormalizeLoaderName(request.Loader);
            filtered = filtered.Where(pack => pack.Versions.Any(version => IsBrowsableFtbVersion(version)
                &&
                NormalizeLoaderName(version.ModLoaderName).Equals(loader, StringComparison.Ordinal)));
        }

        return request.Sort switch
        {
            OnlineModpackSort.Relevance => filtered.ToArray(),
            OnlineModpackSort.Downloads => filtered
                .OrderByDescending(static pack => pack.InstallCount ?? -1)
                .ThenBy(static pack => pack.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static pack => pack.Id)
                .ToArray(),
            OnlineModpackSort.RecentlyUpdated => filtered
                .OrderByDescending(static pack => LatestFtbUpdate(pack))
                .ThenBy(static pack => pack.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static pack => pack.Id)
                .ToArray(),
            OnlineModpackSort.Newest => filtered
                .OrderByDescending(static pack => pack.Versions.Count == 0
                    ? int.MinValue
                    : pack.Versions
                        .Where(IsBrowsableFtbVersion)
                        .Select(static version => version.Id)
                        .DefaultIfEmpty(int.MinValue)
                        .Max())
                .ThenBy(static pack => pack.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static pack => pack.Id)
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }

    internal static OnlineModpackSearchResult MapFtbProject(FtbPack pack)
    {
        var latest = pack.LatestRelease;
        var versionSummary = latest is null
            ? L("online.workflow.ftb.pack")
            : $"Minecraft {latest.MinecraftVersion ?? L("common.unknown")} · {FormatLoader(latest.ModLoaderName, latest.ModLoaderVersion)}";
        var summary = string.IsNullOrWhiteSpace(pack.Synopsis)
            ? versionSummary
            : pack.Synopsis.Trim();
        return new OnlineModpackSearchResult(
            OnlineModpackProvider.Ftb,
            pack.Id.ToString(CultureInfo.InvariantCulture),
            pack.Name,
            summary,
            "Feed The Beast",
            iconUri: pack.IconUri,
            previewImageUri: pack.PreviewImageUri,
            downloadCount: pack.InstallCount,
            updatedAtUtc: LatestFtbUpdate(pack),
            iconUriCandidates: pack.IconUriCandidates,
            previewImageUriCandidates: pack.PreviewImageUriCandidates);
    }

    internal static OnlineModpackSearchResult MapModrinthProject(ModrinthModpackProject project)
        => new(
            OnlineModpackProvider.Modrinth,
            project.ProjectId,
            project.Title,
            project.Description,
            project.Author,
            new Uri($"https://modrinth.com/modpack/{Uri.EscapeDataString(project.Slug)}"),
            project.IconUri,
            project.GalleryImageUris.FirstOrDefault(),
            project.Downloads,
            project.DateModified,
            iconUriCandidates: project.GalleryImageUris,
            previewImageUriCandidates: project.GalleryImageUris
                .Concat(project.IconUri is { } icon ? [icon] : []));

    internal static OnlineModpackSearchResult MapCurseForgeProject(CurseForgeModpackProject project)
        => new(
            OnlineModpackProvider.CurseForge,
            project.ModId.ToString(CultureInfo.InvariantCulture),
            project.Name,
            project.Summary,
            project.Author,
            project.WebsiteUri,
            project.IconUri,
            project.PreviewImageUri,
            project.DownloadCount,
            project.DateModified);

    internal static string MapModrinthIndex(OnlineModpackSort sort) => sort switch
    {
        OnlineModpackSort.Relevance => "relevance",
        OnlineModpackSort.Downloads => "downloads",
        OnlineModpackSort.RecentlyUpdated => "updated",
        OnlineModpackSort.Newest => "newest",
        _ => throw new ArgumentOutOfRangeException(nameof(sort))
    };

    internal static CurseForgeModpackSortField MapCurseForgeSort(OnlineModpackSort sort) => sort switch
    {
        OnlineModpackSort.Relevance => CurseForgeModpackSortField.Popularity,
        OnlineModpackSort.Downloads => CurseForgeModpackSortField.TotalDownloads,
        OnlineModpackSort.RecentlyUpdated => CurseForgeModpackSortField.LastUpdated,
        OnlineModpackSort.Newest => CurseForgeModpackSortField.ReleasedDate,
        _ => throw new ArgumentOutOfRangeException(nameof(sort))
    };

    internal static CurseForgeModLoaderType MapCurseForgeLoader(string? loader)
        => NormalizeLoaderName(loader) switch
        {
            "" => CurseForgeModLoaderType.Any,
            "forge" => CurseForgeModLoaderType.Forge,
            "cauldron" => CurseForgeModLoaderType.Cauldron,
            "liteloader" => CurseForgeModLoaderType.LiteLoader,
            "fabric" => CurseForgeModLoaderType.Fabric,
            "quilt" => CurseForgeModLoaderType.Quilt,
            "neoforge" => CurseForgeModLoaderType.NeoForge,
            _ => throw new ArgumentException(L("online.workflow.error.curseLoader"), nameof(loader))
        };

    internal static int? ParseCurseForgeCategory(string? sourceCategory)
    {
        if (string.IsNullOrWhiteSpace(sourceCategory)) return null;
        return int.TryParse(
                   sourceCategory.Trim(),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var categoryId)
               && categoryId > 0
            ? categoryId
            : throw new ArgumentException(
                L("online.workflow.error.curseCategory"),
                nameof(sourceCategory));
    }

    private static string? TrimOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeLoaderName(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value
                .Where(static character => character != '-'
                                           && character != '_'
                                           && !char.IsWhiteSpace(character))
                .Select(char.ToLowerInvariant)
                .ToArray());

    private static DateTimeOffset? LatestFtbUpdate(FtbPack pack)
        => pack.Versions
            .Where(static version => IsBrowsableFtbVersion(version))
            .Select(static version => FtbTimestampNormalizer.NormalizeUtc(version.Updated))
            .Where(static updated => updated is not null)
            .DefaultIfEmpty(null)
            .Max();

    private static bool IsBrowsableFtbVersion(FtbPackVersion version)
        => version.Type.Equals("release", StringComparison.OrdinalIgnoreCase)
           || version.Type.Equals("beta", StringComparison.OrdinalIgnoreCase)
           || version.Type.Equals("alpha", StringComparison.OrdinalIgnoreCase);

    private static int ParsePositiveInt(string value, string label)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidDataException(L("online.workflow.error.invalidIdentifier", label));

    private static string FindMinecraftVersion(IReadOnlyList<string> values)
        => values.FirstOrDefault(value => MinecraftVersionPattern().IsMatch(value)) ?? L("common.unknown");

    private static string FindLoader(IReadOnlyList<string> values)
        => values.FirstOrDefault(value => value.Equals("Forge", StringComparison.OrdinalIgnoreCase)
                                          || value.Equals("NeoForge", StringComparison.OrdinalIgnoreCase)
                                          || value.Equals("Fabric", StringComparison.OrdinalIgnoreCase)
                                          || value.Equals("Quilt", StringComparison.OrdinalIgnoreCase))
            ?? L("common.unknown");

    private static string FormatLoader(string? name, string? version)
        => string.IsNullOrWhiteSpace(name)
            ? L("online.workflow.unknownLoader")
            : string.IsNullOrWhiteSpace(version) ? name : $"{name} {version}";

    private static int GetJarPreference(string fileName)
        => fileName.Equals("server.jar", StringComparison.OrdinalIgnoreCase) ? 0
            : fileName.Contains("fabric-server-launch", StringComparison.OrdinalIgnoreCase) ? 1
            : 2;

    private static CoreType MapModrinthLoaderCore(ModrinthModpackLoaderKind kind) => kind switch
    {
        ModrinthModpackLoaderKind.Vanilla => CoreType.Vanilla,
        ModrinthModpackLoaderKind.Fabric => CoreType.Fabric,
        ModrinthModpackLoaderKind.Forge => CoreType.Forge,
        ModrinthModpackLoaderKind.NeoForge => CoreType.NeoForge,
        ModrinthModpackLoaderKind.Quilt => CoreType.Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, L("online.workflow.error.modrinthLoader"))
    };

    private static void ReportModrinthLoaderProgress(
        IProgress<OnlineModpackInstallProgress> progress,
        ModrinthLoaderBootstrapProgress value)
    {
        var phase = value.Phase ?? string.Empty;
        var stage = phase.StartsWith("download-", StringComparison.Ordinal)
            ? OnlineModpackInstallStage.Downloading
            : phase.Equals("validate-output", StringComparison.Ordinal)
                ? OnlineModpackInstallStage.Verifying
                : OnlineModpackInstallStage.InstallingLoader;
        var fraction = value.Fraction is { } known
            ? Math.Clamp(known, 0d, 1d)
            : (double?)null;
        double? percentage = fraction is null
            ? null
            : phase.StartsWith("download-", StringComparison.Ordinal)
                ? 70 + fraction.Value * 10
                : phase.Equals("run-installer", StringComparison.Ordinal)
                    ? 80 + fraction.Value * 6
                    : phase.Equals("validate-output", StringComparison.Ordinal)
                        ? 86 + fraction.Value * 2
                        : phase.Equals("merge-output", StringComparison.Ordinal)
                            ? 88 + fraction.Value * 2
                            : null;
        var message = phase switch
        {
            "download-vanilla" => L("online.workflow.loader.downloadVanilla"),
            "download-fabric-installer" => L("online.workflow.loader.downloadFabric"),
            "download-forge-installer" => L("online.workflow.loader.downloadForge"),
            "download-neoforge-installer" => L("online.workflow.loader.downloadNeoForge"),
            "run-installer" => L("online.workflow.loader.runInstaller", value.Detail ?? "ModLoader"),
            "validate-output" => L("online.workflow.loader.validateOutput"),
            "merge-output" => L("online.workflow.loader.mergeOutput"),
            _ => L("online.workflow.modrinth.loaderInstalling")
        };
        progress.Report(new OnlineModpackInstallProgress(stage, message, percentage));
    }

    private static void ReportCurseLoaderProgress(
        IProgress<OnlineModpackInstallProgress> progress,
        ModrinthLoaderBootstrapProgress value)
    {
        var phase = value.Phase ?? string.Empty;
        var stage = phase.StartsWith("download-", StringComparison.Ordinal)
            ? OnlineModpackInstallStage.Downloading
            : phase.Equals("validate-output", StringComparison.Ordinal)
                ? OnlineModpackInstallStage.Verifying
                : OnlineModpackInstallStage.InstallingLoader;
        var fraction = value.Fraction is { } known
            ? Math.Clamp(known, 0d, 1d)
            : (double?)null;
        double? percentage = fraction is null
            ? null
            : phase.StartsWith("download-", StringComparison.Ordinal)
                ? 79 + fraction.Value * 6
                : phase.Equals("run-installer", StringComparison.Ordinal)
                    ? 85 + fraction.Value * 5
                    : phase.Equals("validate-output", StringComparison.Ordinal)
                        ? 90 + fraction.Value * 2
                        : phase.Equals("merge-output", StringComparison.Ordinal)
                            ? 92 + fraction.Value * 2
                            : null;
        var message = phase switch
        {
            "download-vanilla" => L("online.workflow.loader.downloadVanilla"),
            "download-fabric-installer" => L("online.workflow.loader.downloadFabric"),
            "download-forge-installer" => L("online.workflow.loader.downloadForge"),
            "download-neoforge-installer" => L("online.workflow.loader.downloadNeoForge"),
            "run-installer" => L("online.workflow.loader.runIsolatedInstaller", value.Detail ?? "ModLoader"),
            "validate-output" => L("online.workflow.loader.validateOfficialOutput"),
            "merge-output" => L("online.workflow.loader.mergeOfficialOutput"),
            _ => L("online.workflow.curse.loaderInstalling")
        };
        progress.Report(new OnlineModpackInstallProgress(stage, message, percentage));
    }

    private static string SanitizeProgressText(string? value, string fallback)
    {
        return TerminalOutputSanitizer.Sanitize(value, fallback);
    }

    internal static OnlineModpackInstallProgress MapModrinthPackProgress(
        ModrinthModpackInstallProgress value)
    {
        var downloading = value.Phase.StartsWith("download", StringComparison.Ordinal);
        var message = value.Phase switch
        {
            "download-pack" => L("online.workflow.modrinth.downloadPack"),
            "download-files" => L("online.workflow.modrinth.downloadFiles"),
            "inspect" => L("online.workflow.modrinth.inspect"),
            "overrides" => L("online.workflow.modrinth.overrides"),
            "server-overrides" => L("online.workflow.modrinth.serverOverrides"),
            _ => downloading
                ? L("online.workflow.modrinth.downloading")
                : L("online.workflow.modrinth.extracting")
        };
        var concurrencyMode = value.UsesAdaptiveConcurrency
            ? L("online.workflow.concurrency.auto")
            : L("online.workflow.concurrency.fixed");
        var detail = value.Phase == "download-files"
                     && value.TotalFiles > 0
                     && value.EffectiveConcurrentDownloads > 0
            ? L(
                "online.workflow.downloadDetail",
                concurrencyMode,
                value.EffectiveConcurrentDownloads,
                value.CompletedFiles,
                value.TotalFiles)
            : null;
        return new OnlineModpackInstallProgress(
            downloading ? OnlineModpackInstallStage.Downloading : OnlineModpackInstallStage.Extracting,
            message,
            value.TotalFiles == 0
                ? null
                : 5 + (double)value.CompletedFiles / value.TotalFiles * 55,
            detail);
    }

    private static void ValidateInstallRequest(OnlineModpackInstallRequest request)
    {
        if (request.Project.Provider != request.Version.Provider
            || !request.Project.ProjectId.Equals(request.Version.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(L("online.workflow.error.sourceMismatch"));
        }

        if (!request.Version.HasOfficialServerPack)
        {
            throw new InvalidOperationException(L("online.workflow.error.noServerPack"));
        }

        if (string.IsNullOrWhiteSpace(request.ServerName) || request.ServerName.Trim().Length > 80)
        {
            throw new InvalidOperationException(L("online.workflow.error.serverNameLength"));
        }
    }

    /// <summary>
    /// Persists only provenance that was independently verified by the provider-specific install
    /// path. Project and version identifiers are the durable identity; the version name is an
    /// informational label and falls back to the catalog selection when the installed artifact
    /// does not expose one.
    /// </summary>
    internal static void ApplyVerifiedModpackProvenance(
        ServerInstance instance,
        OnlineModpackInstallRequest request,
        OnlineModpackProvider verifiedProvider,
        string verifiedProjectId,
        string verifiedVersionId,
        string? verifiedVersionName)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentNullException.ThrowIfNull(request.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedVersionId);

        var projectId = verifiedProjectId.Trim();
        var versionId = verifiedVersionId.Trim();
        if (request.Project.Provider != verifiedProvider
            || request.Version.Provider != verifiedProvider
            || !request.Project.ProjectId.Equals(projectId, StringComparison.Ordinal)
            || !request.Version.ProjectId.Equals(projectId, StringComparison.Ordinal)
            || !request.Version.VersionId.Equals(versionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(L("online.workflow.error.provenanceMismatch"));
        }

        var source = verifiedProvider switch
        {
            OnlineModpackProvider.Ftb => ModpackSourceKind.Ftb,
            OnlineModpackProvider.Modrinth => ModpackSourceKind.Modrinth,
            OnlineModpackProvider.CurseForge => ModpackSourceKind.CurseForge,
            _ => throw new InvalidDataException(L("online.workflow.error.provenanceUnavailable"))
        };
        var versionName = string.IsNullOrWhiteSpace(verifiedVersionName)
            ? request.Version.VersionName?.Trim()
            : verifiedVersionName.Trim();
        if (string.IsNullOrWhiteSpace(versionName))
        {
            versionName = versionId;
        }

        // Do not mutate the instance until every provider identity check above has succeeded.
        instance.ModpackSource = source;
        instance.ModpackProjectId = projectId;
        instance.ModpackVersionId = versionId;
        instance.ModpackVersionName = versionName;
    }

    private static async Task<T> WithApiKeyAsync<T>(
        SecureString? secureApiKey,
        Func<string, Task<T>> action)
    {
        if (secureApiKey is null || secureApiKey.Length == 0)
        {
            throw new InvalidOperationException(L("online.workflow.error.curseApiKeyRequired"));
        }

        IntPtr bstr = IntPtr.Zero;
        try
        {
            bstr = Marshal.SecureStringToBSTR(secureApiKey);
            var apiKey = Marshal.PtrToStringBSTR(bstr);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(L("online.workflow.error.curseApiKeyEmpty"));
            }

            return await action(apiKey).ConfigureAwait(false);
        }
        finally
        {
            if (bstr != IntPtr.Zero) Marshal.ZeroFreeBSTR(bstr);
        }
    }

    private static async Task DeleteOwnedFileAsync(
        string root,
        string path,
        bool requireCompleteCleanup)
    {
        try
        {
            var safePath = SafePath.EnsureWithinRoot(root, path, allowRoot: false);
            await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                    root,
                    safePath,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch when (!requireCompleteCleanup)
        {
            // Do not turn an already committed, fully verified Server into a false failure solely
            // because antivirus retained a disposable cache file beyond the bounded retry window.
        }
    }

    private async Task DeleteOwnedStagingAsync(string path)
    {
        var safePath = SafePath.EnsureWithinRoot(
            _paths.Servers,
            path,
            allowRoot: false);
        if (!Path.GetFileName(safePath).StartsWith(
                ".installing-",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(L("online.workflow.error.unsafeStagingCleanup"));
        }

        await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                _paths.Servers,
                safePath,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task DeleteOwnedFinalTreeAsync(string path)
    {
        var safePath = SafePath.EnsureWithinRoot(
            _paths.Servers,
            path,
            allowRoot: false);
        await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                _paths.Servers,
                safePath,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task AttachCatalogArtworkAsync(
        ServerInstance instance,
        OnlineModpackInstallRequest request,
        CancellationToken cancellationToken)
    {
        instance.ModpackProviderId = request.Project.Provider.ToString().ToLowerInvariant();
        try
        {
            var iconCachePath = await _artworkCache.GetOrCacheAsync(
                    request.Project.Provider,
                    request.Project.IconUri,
                    cancellationToken)
                .ConfigureAwait(false);
            var previewCachePath = await _artworkCache.GetOrCacheAsync(
                    request.Project.Provider,
                    request.Project.PreviewImageUri,
                    cancellationToken)
                .ConfigureAwait(false);

            instance.CatalogIconImagePath = await CopyCatalogArtworkIntoServerAsync(
                    instance.DirectoryPath,
                    iconCachePath ?? previewCachePath,
                    "catalog-icon",
                    OnlineModpackArtworkDecoder.ServerIconWidth,
                    OnlineModpackArtworkDecoder.ServerIconHeight,
                    cancellationToken)
                .ConfigureAwait(false);
            if (instance.CatalogIconImagePath is null
                && iconCachePath is not null
                && previewCachePath is not null
                && !Path.GetFullPath(iconCachePath).Equals(
                    Path.GetFullPath(previewCachePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                instance.CatalogIconImagePath = await CopyCatalogArtworkIntoServerAsync(
                        instance.DirectoryPath,
                        previewCachePath,
                        "catalog-icon",
                        OnlineModpackArtworkDecoder.ServerIconWidth,
                        OnlineModpackArtworkDecoder.ServerIconHeight,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            instance.CatalogPreviewImagePath = await CopyCatalogArtworkIntoServerAsync(
                    instance.DirectoryPath,
                    previewCachePath,
                    "catalog-preview",
                    OnlineModpackArtworkDecoder.ServerPreviewWidth,
                    OnlineModpackArtworkDecoder.ServerPreviewHeight,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Artwork is optional and must never turn a verified, already-promoted installation
            // into a failed/orphaned background job.
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or NotSupportedException
                                           or ObjectDisposedException)
        {
            // The server remains valid and falls back to its core initial when artwork is absent.
        }
    }

    private async Task<string?> CopyCatalogArtworkIntoServerAsync(
        string serverDirectory,
        string? cachedPath,
        string fileStem,
        int maximumWidth,
        int maximumHeight,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cachedPath) || !File.Exists(cachedPath))
        {
            return null;
        }

        var verifiedServerDirectory = SafePath.EnsureWithinRoot(
            _paths.Servers,
            serverDirectory,
            allowRoot: false);
        var verifiedCachePath = SafePath.EnsureNoReparsePointsUnderRoot(
            _paths.OnlineModpackArtworkCache,
            cachedPath);
        var extension = Path.GetExtension(verifiedCachePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".webp" or ".gif"))
        {
            return null;
        }

        var metadataDirectory = Path.Combine(verifiedServerDirectory, ".mcsv");
        var assetsDirectory = Path.Combine(metadataDirectory, "assets");
        RejectExistingReparsePoint(metadataDirectory);
        RejectExistingReparsePoint(assetsDirectory);
        Directory.CreateDirectory(assetsDirectory);
        SafePath.EnsureNoReparsePointsUnderRoot(verifiedServerDirectory, assetsDirectory);

        var destination = SafePath.EnsureWithinRoot(
            verifiedServerDirectory,
            Path.Combine(assetsDirectory, fileStem + ".png"),
            allowRoot: false);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (!await _artworkDecoder.WriteScaledPngAsync(
                    verifiedCachePath,
                    temporary,
                    maximumWidth,
                    maximumHeight,
                    cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // Best-effort cleanup of optional artwork staging only.
            }
        }

        static void RejectExistingReparsePoint(string path)
        {
            if ((Directory.Exists(path) || File.Exists(path))
                && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException(L("online.workflow.error.artworkReparsePoint"));
            }
        }
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ftbCatalogClient.Dispose();
        _ftbDownloadClient.Dispose();
        _modrinthApiClient.Dispose();
        _modrinthDownloadClient.Dispose();
        _modrinthLoaderClient.Dispose();
        _javaRuntimeClient.Dispose();
        _curseApiClient.Dispose();
        _curseDownloadClient.Dispose();
        if (_ownsArtworkCache && _artworkCache is IDisposable disposableArtworkCache)
        {
            disposableArtworkCache.Dispose();
        }
    }

    private sealed record InstalledDetection(
        string DirectoryPath,
        ServerPackDetectionResult? Pack,
        string? JarPath,
        DetectionResult? JarDetection);

    [GeneratedRegex(@"^\d+(?:\.\d+){1,3}(?:[-+].*)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex MinecraftVersionPattern();
}

internal interface IModrinthJavaRuntimeResolver
{
    Task<string> ResolveAsync(
        int majorVersion,
        IProgress<double>? downloadProgress,
        CancellationToken cancellationToken);
}

internal sealed class ManagedModrinthJavaRuntimeResolver(
    ApplicationPaths paths,
    AdoptiumRuntimeProvider provider) : IModrinthJavaRuntimeResolver
{
    private readonly ApplicationPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly AdoptiumRuntimeProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public async Task<string> ResolveAsync(
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
                     "temurin-*",
                     SearchOption.TopDirectoryOnly)
                 .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inspected++ >= 64)
            {
                break;
            }

            var javaExecutable = Path.Combine(directory, "bin", "java.exe");
            if (!File.Exists(javaExecutable))
            {
                continue;
            }

            try
            {
                javaExecutable = SafePath.EnsureNoReparsePointsUnderRoot(
                    _paths.Runtimes,
                    javaExecutable);
                var actualMajor = await AdoptiumRuntimeProvider.ReadJavaMajorVersionAsync(
                        javaExecutable,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (actualMajor == majorVersion)
                {
                    return javaExecutable;
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
                // An incomplete or manually modified runtime is ignored. The verified provider
                // below installs a fresh matching runtime without executing shell commands.
            }
        }

        var installed = await _provider.InstallAsync(
                majorVersion,
                _paths.Runtimes,
                downloadProgress,
                cancellationToken)
            .ConfigureAwait(false);
        if (installed.MajorVersion != majorVersion
            || string.IsNullOrWhiteSpace(installed.JavaExecutablePath)
            || !File.Exists(installed.JavaExecutablePath))
        {
            throw new InvalidDataException(LocalizationService.Current.Get(
                "online.workflow.error.javaRuntime",
                majorVersion));
        }

        return SafePath.EnsureNoReparsePointsUnderRoot(
            _paths.Runtimes,
            installed.JavaExecutablePath);
    }
}
