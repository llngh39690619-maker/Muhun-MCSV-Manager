using System.Security.Cryptography;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Creates a server from a live, exact core catalog. The operation owns a new staging tree below
/// <see cref="ApplicationPaths.Servers"/> and atomically promotes it only after static launch
/// validation succeeds.
/// </summary>
public sealed partial class CoreServerCreationWorkflow :
    ICoreServerCreationWorkflow,
    IIncrementalCoreServerCatalogWorkflow,
    IDisposable
{
    private const int MinimumAcceptedJarConfidence = 80;
    private const int MaximumLaunchCandidates = 32;
    private const int MaximumCatalogVersionsPerCore = 2_048;
    private readonly ApplicationPaths _paths;
    private readonly ICoreServerCreationBackend _backend;
    private readonly IModrinthJavaRuntimeResolver _javaRuntimeResolver;
    private readonly ICoreServerJdkResolver? _jdkResolver;
    private readonly IReadOnlyList<IDisposable> _ownedResources;
    private readonly CoreServerCatalogCache _catalogCache;
    private readonly TimeProvider _catalogTimeProvider;
    private readonly CancellationTokenSource _catalogLifetimeCancellation = new();
    private readonly SemaphoreSlim _catalogBootstrapGate = new(1, 1);
    private readonly SemaphoreSlim _catalogRefreshGate = new(1, 1);
    private readonly object _catalogStateSync = new();
    private readonly ServerPackDetector _serverPackDetector = new();
    private readonly JarCoreDetector _jarCoreDetector = new();
    private IReadOnlyList<CoreServerBackendProduct>? _catalogProducts;
    private Dictionary<string, CoreServerCatalogCacheEntry>? _catalogEntries;
    private string? _catalogCacheLoadWarning;
    private DateTimeOffset? _lastCatalogRefreshAttemptUtc;
    private bool _lastCatalogRefreshHadFailures;
    private bool _disposed;

    internal CoreServerCreationWorkflow(
        ApplicationPaths paths,
        ICoreServerCreationBackend backend,
        IModrinthJavaRuntimeResolver javaRuntimeResolver,
        ICoreServerJdkResolver? jdkResolver = null,
        IReadOnlyList<IDisposable>? ownedResources = null,
        CoreServerCatalogCache? catalogCache = null,
        TimeProvider? catalogTimeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _javaRuntimeResolver = javaRuntimeResolver
            ?? throw new ArgumentNullException(nameof(javaRuntimeResolver));
        _jdkResolver = jdkResolver;
        _ownedResources = ownedResources ?? [];
        _catalogTimeProvider = catalogTimeProvider ?? TimeProvider.System;
        _catalogCache = catalogCache
            ?? new CoreServerCatalogCache(paths.Cache, _catalogTimeProvider);
    }

    public async Task<IReadOnlyList<CoreServerProduct>> GetAvailableCoresAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var products = await GetValidatedProductsAsync(cancellationToken).ConfigureAwait(false);
        return products
            .Select(item => item.Product)
            .OrderBy(item => item.Software)
            .ToArray();
    }

    public async Task<IReadOnlyList<CoreServerVersion>> GetVersionsAsync(
        CoreServerProduct core,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(core);
        var product = await ResolveCanonicalProductAsync(core, cancellationToken).ConfigureAwait(false);
        var versions = await GetValidatedVersionsAsync(product, cancellationToken).ConfigureAwait(false);
        return versions.Select(item => item.Version).ToArray();
    }

    public async Task<ServerInstance> CreateAsync(
        CoreServerCreationRequest request,
        IProgress<CoreServerCreationProgress> progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        ValidateServerName(request.ServerName);
        cancellationToken.ThrowIfCancellationRequested();

        progress.Report(new(
            CoreServerCreationStage.ResolvingVersion,
            "正在重新確認選取的核心、版本與實際建置…",
            3));
        var product = await ResolveCanonicalProductAsync(request.Core, cancellationToken)
            .ConfigureAwait(false);
        var version = await ResolveCanonicalVersionAsync(product, request.Version, cancellationToken)
            .ConfigureAwait(false);
        var plan = await _backend.ResolveExactAsync(product, version, cancellationToken)
            .ConfigureAwait(false);
        ValidateResolvedPlan(product, version, plan);

        var javaExecutable = await ResolveJavaAsync(plan, progress, cancellationToken)
            .ConfigureAwait(false);

        Directory.CreateDirectory(_paths.Servers);
        SafePath.EnsureNoReparsePointsUnderRoot(_paths.Servers, _paths.Servers);
        var staging = SafePath.CombineUnderRoot(
            _paths.Servers,
            $".core-installing-{Guid.NewGuid():N}");
        string? ownedTree = staging;
        try
        {
            Directory.CreateDirectory(staging);
            SafePath.EnsureNoReparsePointsUnderRoot(_paths.Servers, staging);
            progress.Report(new(
                CoreServerCreationStage.PreparingDirectory,
                "已建立隔離的 Server 暫存資料夾。",
                22));
            var installed = await _backend.InstallAsync(
                    plan,
                    staging,
                    javaExecutable,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(installed);
            cancellationToken.ThrowIfCancellationRequested();

            SafePath.EnsureTreeContainsNoReparsePoints(staging);
            progress.Report(new(
                CoreServerCreationStage.DetectingServer,
                "正在靜態驗證核心類型、版本與啟動方式…",
                90));
            var detection = await DetectInstalledServerAsync(
                    staging,
                    plan,
                    installed,
                    cancellationToken)
                .ConfigureAwait(false);

            progress.Report(new(
                CoreServerCreationStage.Finalizing,
                "驗證完成，正在原子加入 Server 清單資料夾…",
                97));
            cancellationToken.ThrowIfCancellationRequested();
            var finalRoot = CommitStaging(staging, request.ServerName);
            ownedTree = finalRoot;
            cancellationToken.ThrowIfCancellationRequested();
            var instance = CreateServerInstance(
                request.ServerName,
                plan,
                detection,
                staging,
                finalRoot,
                javaExecutable);
            ownedTree = null;
            progress.Report(new(
                CoreServerCreationStage.Finalizing,
                "核心 Server 已建立完成。",
                100));
            return instance;
        }
        finally
        {
            if (ownedTree is not null)
            {
                await DeleteOwnedTreeAsync(ownedTree).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<CoreServerBackendProduct>> GetValidatedProductsAsync(
        CancellationToken cancellationToken)
    {
        var products = await _backend.GetProductsAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("核心來源回傳了 null product catalog。");
        var result = new List<CoreServerBackendProduct>(products.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in products)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(product);
            ValidateProduct(product);
            if (!ids.Add(product.Product.CoreId))
            {
                throw new InvalidDataException($"核心來源回傳重複 CoreId：{product.Product.CoreId}");
            }

            result.Add(product);
        }

        return result;
    }

    private async Task<CoreServerBackendProduct> ResolveCanonicalProductAsync(
        CoreServerProduct requested,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var matches = (await GetValidatedProductsAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => item.Product.CoreId.Equals(requested.CoreId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var product = matches.Length switch
        {
            0 => throw new InvalidDataException("選取的核心已不在實際來源清單中。"),
            1 => matches[0],
            _ => throw new InvalidDataException("核心來源回傳不明確的重複核心。")
        };
        if (product.Product != requested)
        {
            throw new InvalidDataException("選取的核心資料已變更或不是來源回傳的原始項目。");
        }

        return product;
    }

    private async Task<IReadOnlyList<CoreServerBackendVersion>> GetValidatedVersionsAsync(
        CoreServerBackendProduct product,
        CancellationToken cancellationToken)
    {
        var versions = await _backend.GetVersionsAsync(product, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("核心來源回傳了 null version catalog。");
        if (versions.Count > MaximumCatalogVersionsPerCore)
        {
            throw new InvalidDataException("核心來源回傳的 version catalog 超過安全數量上限。");
        }

        var result = new List<CoreServerBackendVersion>(versions.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(version);
            ValidateVersion(product, version);
            if (!ids.Add(version.Version.VersionId))
            {
                throw new InvalidDataException(
                    $"核心來源回傳重複 VersionId：{version.Version.VersionId}");
            }

            result.Add(version);
        }

        return result;
    }

    private async Task<CoreServerBackendVersion> ResolveCanonicalVersionAsync(
        CoreServerBackendProduct product,
        CoreServerVersion requested,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var matches = (await GetValidatedVersionsAsync(product, cancellationToken).ConfigureAwait(false))
            .Where(item => item.Version.VersionId.Equals(
                requested.VersionId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var version = matches.Length switch
        {
            0 => throw new InvalidDataException("選取的版本已不在實際來源清單中。"),
            1 => matches[0],
            _ => throw new InvalidDataException("核心來源回傳不明確的重複版本。")
        };
        if (version.Version != requested)
        {
            throw new InvalidDataException("選取的版本資料已變更或不是來源回傳的原始項目。");
        }

        return version;
    }

    private async Task<InstalledServerDetection> DetectInstalledServerAsync(
        string staging,
        CoreServerInstallPlan plan,
        CoreServerBackendInstallResult installed,
        CancellationToken cancellationToken)
    {
        var officialLoader = await ValidateOfficialLoaderProvenanceAsync(
                staging,
                plan,
                installed,
                cancellationToken)
            .ConfigureAwait(false);

        if (plan.AcceptsArgumentFileLaunch)
        {
            var pack = await _serverPackDetector.DetectAsync(staging, cancellationToken)
                .ConfigureAwait(false);
            if (pack.IsRecognized
                && pack.IsRunnable
                && pack.CoreType == plan.ExpectedCoreType
                && MatchesMinecraftVersion(pack.MinecraftVersion, plan))
            {
                if (officialLoader is null
                    || officialLoader.LaunchLayout == OfficialLoaderLaunchLayout.StandardArgumentFiles)
                {
                    await OnlineServerPackSafetyValidator.ValidateAsync(pack, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (officialLoader.LaunchLayout is not (
                             OfficialLoaderLaunchLayout.NeoForgeDirectMainClass
                             or OfficialLoaderLaunchLayout.ForgeInstallerEmbeddedShim))
                {
                    throw new InvalidDataException(
                        "官方 Loader typed provenance 與 argument-file 啟動格式不一致。");
                }

                if (!string.IsNullOrWhiteSpace(plan.LoaderVersion)
                    && !LoaderVersionsMatch(pack.ModLoaderVersion, plan.LoaderVersion, plan.MinecraftVersion))
                {
                    throw new InvalidDataException("安裝結果的 Loader 版本與重新解析的 exact build 不一致。");
                }

                return new InstalledServerDetection(pack, null, null);
            }
        }

        var candidates = NormalizeLaunchCandidates(staging, installed.LaunchCandidates);
        if (officialLoader?.LaunchLayout == OfficialLoaderLaunchLayout.FabricManifestLauncher)
        {
            if (candidates.Count != 1
                || !Path.GetFileName(candidates[0]).Equals(
                    "fabric-server-launch.jar",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "官方 Fabric typed provenance 沒有唯一的 manifest launcher。");
            }

            var launcher = await _jarCoreDetector.DetectAsync(candidates[0], cancellationToken)
                .ConfigureAwait(false);
            if (!launcher.IsValidJar
                || !string.Equals(
                    launcher.MainClass,
                    "net.fabricmc.loader.impl.launch.server.FabricServerLauncher",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "官方 Fabric manifest launcher 在 provenance 驗證後已無法重新解析。");
            }

            // The official launcher is intentionally a tiny two-entry manifest JAR. Its trust
            // comes from the exact installer provenance above, not generic wrapper heuristics.
            return new InstalledServerDetection(null, candidates[0], launcher);
        }

        if (installed.Provenance?.Kind is
            CoreServerInstallProvenanceKind.SpigotBuildToolsOfficialOutput
            or CoreServerInstallProvenanceKind.SpigotBuildToolsOfficialSources)
        {
            return await ValidateSpigotBuildToolsProvenanceAsync(
                    candidates,
                    plan,
                    installed.Provenance,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (plan.Product.SourceId == SpigotCoreServerCreationBackend.SourceId
            && plan.InstallKind == CoreServerInstallKind.SpigotBuildTools)
        {
            throw new InvalidDataException(
                "BuildTools backend 沒有回傳可重新驗證的 typed output provenance。");
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var jar = await _jarCoreDetector.DetectAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            if (jar.IsValidJar
                && jar.IsRecognized
                && jar.ConfidencePercent >= MinimumAcceptedJarConfidence
                && jar.CoreType == plan.ExpectedCoreType
                && MatchesMinecraftVersion(jar.MinecraftVersion, plan))
            {
                return new InstalledServerDetection(null, candidate, jar);
            }

            if (await IsExactOfficialVanillaArtifactAsync(
                    candidate,
                    jar,
                    plan,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return new InstalledServerDetection(null, candidate, jar);
            }
        }

        throw new InvalidDataException(
            $"安裝結果不是選取的 {plan.Product.Product.DisplayName} 核心，"
            + "或只包含無法信任的 wrapper／錯誤版本 JAR。暫存內容不會加入管理器。");
    }

    private static async Task<OfficialLoaderInstallProvenance?> ValidateOfficialLoaderProvenanceAsync(
        string staging,
        CoreServerInstallPlan plan,
        CoreServerBackendInstallResult installed,
        CancellationToken cancellationToken)
    {
        var wrapper = installed.Provenance;
        if (plan.InstallKind != CoreServerInstallKind.OfficialLoaderInstaller)
        {
            if (wrapper?.Kind == CoreServerInstallProvenanceKind.OfficialLoader)
            {
                throw new InvalidDataException(
                    "非官方 Loader install plan 不可使用 OfficialLoader typed provenance。");
            }

            return null;
        }

        if (wrapper?.Kind != CoreServerInstallProvenanceKind.OfficialLoader
            || plan.Product.SourceId != OfficialCoreServerCreationBackend.SourceId
            || plan.SourcePlan is not OfficialServerCoreBuildInfo build
            || build.InstallStrategy is not (
                OfficialServerInstallStrategy.FabricInstaller
                or OfficialServerInstallStrategy.ForgeInstaller
                or OfficialServerInstallStrategy.NeoForgeInstaller)
            || build.CoreType != plan.ExpectedCoreType
            || !build.IsStable
            || !build.MinecraftVersion.Equals(plan.MinecraftVersion, StringComparison.Ordinal)
            || !build.ProductVersion.Equals(
                plan.Version.Version.DisplayName,
                StringComparison.Ordinal)
            || !string.Equals(build.LoaderVersion, plan.LoaderVersion, StringComparison.Ordinal)
            || wrapper.CoreType != plan.ExpectedCoreType
            || !wrapper.MinecraftVersion.Equals(plan.MinecraftVersion, StringComparison.Ordinal)
            || wrapper.ArtifactSha256 is not null
            || !string.Equals(wrapper.LoaderVersion, plan.LoaderVersion, StringComparison.Ordinal)
            || wrapper.OfficialLoader is not { } provenance
            || provenance.Kind != MapOfficialLoaderKind(build.InstallStrategy)
            || !provenance.MinecraftVersion.Equals(plan.MinecraftVersion, StringComparison.Ordinal)
            || !provenance.LoaderVersion.Equals(plan.LoaderVersion, StringComparison.Ordinal)
            || !OfficialLoaderLayoutMatches(provenance.Kind, provenance.LaunchLayout))
        {
            throw new InvalidDataException(
                "官方 Loader typed output provenance 與 exact install plan 不一致。");
        }

        ValidateOfficialLoaderLaunchCandidates(
            staging,
            installed.LaunchCandidates,
            wrapper.LaunchCandidates);
        await OfficialLoaderInstallerOutputValidator.RevalidateAsync(
                provenance,
                staging,
                cancellationToken)
            .ConfigureAwait(false);
        return provenance;
    }

    private static ModrinthModpackLoaderKind MapOfficialLoaderKind(
        OfficialServerInstallStrategy strategy)
        => strategy switch
        {
            OfficialServerInstallStrategy.FabricInstaller => ModrinthModpackLoaderKind.Fabric,
            OfficialServerInstallStrategy.ForgeInstaller => ModrinthModpackLoaderKind.Forge,
            OfficialServerInstallStrategy.NeoForgeInstaller => ModrinthModpackLoaderKind.NeoForge,
            _ => throw new InvalidDataException("官方 Loader install strategy 無效。")
        };

    private static bool OfficialLoaderLayoutMatches(
        ModrinthModpackLoaderKind kind,
        OfficialLoaderLaunchLayout layout)
        => kind switch
        {
            ModrinthModpackLoaderKind.Fabric =>
                layout == OfficialLoaderLaunchLayout.FabricManifestLauncher,
            ModrinthModpackLoaderKind.Forge => layout is
                OfficialLoaderLaunchLayout.StandardArgumentFiles
                or OfficialLoaderLaunchLayout.ForgeInstallerEmbeddedShim,
            ModrinthModpackLoaderKind.NeoForge => layout is
                OfficialLoaderLaunchLayout.StandardArgumentFiles
                or OfficialLoaderLaunchLayout.NeoForgeDirectMainClass,
            _ => false
        };

    private static void ValidateOfficialLoaderLaunchCandidates(
        string staging,
        IReadOnlyList<string>? installedCandidates,
        IReadOnlyList<string>? provenCandidates)
    {
        if (installedCandidates is null
            || provenCandidates is null
            || installedCandidates.Count is < 1 or > MaximumLaunchCandidates
            || installedCandidates.Count != provenCandidates.Count)
        {
            throw new InvalidDataException(
                "官方 Loader typed provenance 的 launch candidates 數量無效。");
        }

        var installed = ValidateCandidateSet(staging, installedCandidates);
        var proven = ValidateCandidateSet(staging, provenCandidates);
        if (!installed.SetEquals(proven))
        {
            throw new InvalidDataException(
                "官方 Loader backend 回傳的 launch candidates 與 typed provenance 不一致。");
        }
    }

    private static HashSet<string> ValidateCandidateSet(
        string staging,
        IReadOnlyList<string> candidates)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || Path.IsPathRooted(candidate))
            {
                throw new InvalidDataException("官方 Loader launch candidate 必須是相對路徑。");
            }

            var fullPath = SafePath.CombineUnderRoot(staging, candidate);
            fullPath = SafePath.EnsureNoReparsePointsUnderRoot(staging, fullPath);
            var file = new FileInfo(fullPath);
            file.Refresh();
            if (!file.Exists
                || file.Length < 1
                || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || !result.Add(file.FullName))
            {
                throw new InvalidDataException(
                    "官方 Loader launch candidate 不存在、重複或不是一般檔案。");
            }
        }

        return result;
    }

    private async Task<InstalledServerDetection> ValidateSpigotBuildToolsProvenanceAsync(
        IReadOnlyList<string> candidates,
        CoreServerInstallPlan plan,
        CoreServerVerifiedInstallProvenance provenance,
        CancellationToken cancellationToken)
    {
        if (plan.Product.SourceId != SpigotCoreServerCreationBackend.SourceId
            || plan.InstallKind != CoreServerInstallKind.SpigotBuildTools
            || plan.SourcePlan is not SpigotBuildPlan build
            || build.CoreType != plan.ExpectedCoreType
            || !build.MinecraftVersion.Equals(plan.MinecraftVersion, StringComparison.Ordinal)
            || provenance.CoreType != plan.ExpectedCoreType
            || !provenance.MinecraftVersion.Equals(plan.MinecraftVersion, StringComparison.Ordinal)
            || provenance.LoaderVersion is not null
            || provenance.OfficialLoader is not null
            || provenance.LaunchCandidates.Count != 1
            || candidates.Count != 1
            || !provenance.LaunchCandidates[0].Equals("server.jar", StringComparison.Ordinal)
            || !Path.GetFileName(candidates[0]).Equals("server.jar", StringComparison.OrdinalIgnoreCase)
            || provenance.ArtifactSha256 is not { Length: 64 } proofHash
            || proofHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("BuildTools typed output provenance 與 exact plan 不一致。");
        }

        var expectedKind = build.OutputVerificationKind switch
        {
            SpigotBuildOutputVerificationKind.OfficialOutputSha256 =>
                CoreServerInstallProvenanceKind.SpigotBuildToolsOfficialOutput,
            SpigotBuildOutputVerificationKind.OfficialSourceRefs =>
                CoreServerInstallProvenanceKind.SpigotBuildToolsOfficialSources,
            _ => throw new InvalidDataException("BuildTools output verification kind 無效。")
        };
        if (provenance.Kind != expectedKind
            || build.ExpectedOutputSha256 is { } officialHash
                && !officialHash.Equals(proofHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("BuildTools typed output provenance 的驗證模式或 hash 不符。");
        }

        var jar = await _jarCoreDetector.DetectAsync(candidates[0], cancellationToken)
            .ConfigureAwait(false);
        if (!jar.IsValidJar)
        {
            throw new InvalidDataException("BuildTools typed output 已不再是有效的 JAR。");
        }

        await using var stream = new FileStream(
            candidates[0],
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(proofHash), actual))
        {
            throw new InvalidDataException(
                "BuildTools output 在 backend 驗證後已變更；暫存內容不會加入管理器。");
        }

        return new InstalledServerDetection(null, candidates[0], jar);
    }

    private static IReadOnlyList<string> NormalizeLaunchCandidates(
        string staging,
        IReadOnlyList<string>? candidates)
    {
        if (candidates is null)
        {
            throw new InvalidDataException("核心安裝流程沒有回傳 launch candidates。");
        }

        if (candidates.Count > MaximumLaunchCandidates)
        {
            throw new InvalidDataException("核心安裝流程回傳過多 launch candidates。");
        }

        var result = new List<string>(candidates.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || Path.IsPathRooted(candidate))
            {
                throw new InvalidDataException("Launch candidate 必須是非空白相對路徑。");
            }

            var fullPath = SafePath.CombineUnderRoot(staging, candidate);
            if (!Path.GetExtension(fullPath).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(fullPath))
            {
                throw new InvalidDataException("Launch candidate 必須是暫存資料夾內既存的 JAR。");
            }

            fullPath = SafePath.EnsureNoReparsePointsUnderRoot(staging, fullPath);
            if (seen.Add(fullPath))
            {
                result.Add(fullPath);
            }
        }

        return result;
    }

    private static ServerInstance CreateServerInstance(
        string serverName,
        CoreServerInstallPlan plan,
        InstalledServerDetection detection,
        string staging,
        string finalRoot,
        string javaExecutable)
    {
        if (detection.Pack is { } pack)
        {
            var finalDirectory = RemapPath(staging, finalRoot, pack.DirectoryPath)
                ?? throw new InvalidDataException("Argument-file Server 路徑無法映射到完成資料夾。");
            return new ServerInstance
            {
                Name = serverName.Trim(),
                DirectoryPath = finalDirectory,
                ServerJarPath = string.Empty,
                LaunchKind = ServerLaunchKind.JavaArgumentFiles,
                JavaArgumentFilePaths = [.. pack.JavaArgumentFilePaths],
                SourceLaunchScriptPath = RemapPath(staging, finalRoot, pack.SourceLaunchScriptPath),
                CoreType = plan.ExpectedCoreType,
                MinecraftVersion = plan.MinecraftVersion,
                JavaMajorVersion = plan.JavaMajorVersion,
                JavaExecutablePath = javaExecutable,
                MinimumMemoryMb = pack.MinimumMemoryMb ?? 1024,
                MaximumMemoryMb = pack.MaximumMemoryMb ?? 4096,
                ServerArguments = [.. pack.ServerArguments]
            };
        }

        var jarPath = RemapPath(staging, finalRoot, detection.JarPath)
            ?? throw new InvalidDataException("Server JAR 路徑無法映射到完成資料夾。");
        return new ServerInstance
        {
            Name = serverName.Trim(),
            DirectoryPath = finalRoot,
            ServerJarPath = jarPath,
            LaunchKind = ServerLaunchKind.ExecutableJar,
            CoreType = plan.ExpectedCoreType,
            MinecraftVersion = plan.MinecraftVersion,
            JavaMajorVersion = plan.JavaMajorVersion,
            JavaExecutablePath = javaExecutable,
            ServerArguments = plan.ExpectedCoreType == CoreType.Velocity
                ? ["--port", "25565"]
                : ["nogui"],
            StopCommand = plan.ExpectedCoreType == CoreType.Velocity ? "shutdown" : null
        };
    }

    private string CommitStaging(string staging, string preferredName)
    {
        var source = SafePath.EnsureWithinRoot(_paths.Servers, staging, allowRoot: false);
        SafePath.EnsureNoReparsePointsUnderRoot(_paths.Servers, source);
        SafePath.EnsureTreeContainsNoReparsePoints(source);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        return ServerDirectoryPromotion.PromoteToUniqueDirectory(
            _paths.Servers,
            source,
            preferredName);
    }

    private static void ValidateProduct(CoreServerBackendProduct product)
    {
        if (string.IsNullOrWhiteSpace(product.Product.CoreId)
            || string.IsNullOrWhiteSpace(product.Product.DisplayName)
            || string.IsNullOrWhiteSpace(product.SourceId)
            || product.Product.Description is null
            || product.Product.CoreId.Length > 160
            || product.Product.DisplayName.Length > 120
            || product.Product.Description.Length > 500)
        {
            throw new InvalidDataException("核心來源回傳無效的 product metadata。");
        }

        if (!Enum.IsDefined(product.Product.Software)
            || !Enum.IsDefined(product.ExpectedCoreType)
            || product.ExpectedCoreType == CoreType.Unknown
            || !SoftwareMatchesCoreType(product.Product.Software, product.ExpectedCoreType))
        {
            throw new InvalidDataException("核心來源的 software 與預期 CoreType 不一致。");
        }
    }

    private static void ValidateVersion(
        CoreServerBackendProduct product,
        CoreServerBackendVersion version)
    {
        if (!version.Version.CoreId.Equals(product.Product.CoreId, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(version.Version.VersionId)
            || string.IsNullOrWhiteSpace(version.Version.DisplayName)
            || string.IsNullOrWhiteSpace(version.Version.MinecraftVersion)
            || version.Version.VersionId.Length > 240
            || version.Version.DisplayName.Length > 240
            || version.Version.MinecraftVersion.Length > 80
            || version.Version.Build.Length > 240
            || version.JavaMajorVersion is < 8 or > 99)
        {
            throw new InvalidDataException("核心來源回傳無效或錯核心的 version metadata。");
        }
    }

    private static void ValidateResolvedPlan(
        CoreServerBackendProduct product,
        CoreServerBackendVersion version,
        CoreServerInstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Product != product
            || plan.Version != version
            || plan.ExpectedCoreType != product.ExpectedCoreType
            || !plan.MinecraftVersion.Equals(
                version.Version.MinecraftVersion,
                StringComparison.OrdinalIgnoreCase)
            || plan.JavaMajorVersion != version.JavaMajorVersion
            || plan.JavaMajorVersion is < 8 or > 99
            || !Enum.IsDefined(plan.InstallKind)
            || plan.RequiresJdk != (plan.InstallKind == CoreServerInstallKind.SpigotBuildTools))
        {
            throw new InvalidDataException("核心來源重新解析的 exact install plan 與選取項目不一致。");
        }
    }

    private static void ValidateServerName(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName) || serverName.Trim().Length > 80)
        {
            throw new InvalidOperationException("Server 名稱必須介於 1 到 80 個字元。");
        }
    }

    private static void ValidateManagedJavaResult(string javaExecutable)
    {
        if (string.IsNullOrWhiteSpace(javaExecutable) || !File.Exists(javaExecutable))
        {
            throw new InvalidDataException("Managed Java resolver 沒有回傳既存的 Java executable。");
        }

        var attributes = File.GetAttributes(javaExecutable);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || new FileInfo(javaExecutable).Length < 1)
        {
            throw new InvalidDataException("Managed Java executable 不是可信的一般檔案。");
        }
    }

    private async Task<string> ResolveJavaAsync(
        CoreServerInstallPlan plan,
        IProgress<CoreServerCreationProgress> progress,
        CancellationToken cancellationToken)
    {
        var kind = plan.RequiresJdk ? "JDK" : "Runtime";
        progress.Report(new(
            CoreServerCreationStage.Preparing,
            $"正在準備並驗證 Java {plan.JavaMajorVersion} {kind}…",
            8));
        var downloadProgress = new Progress<double>(value => progress.Report(new(
            CoreServerCreationStage.Downloading,
            $"正在取得並驗證 Java {plan.JavaMajorVersion} {kind}…",
            8 + Math.Clamp(value, 0d, 1d) * 12)));
        if (!plan.RequiresJdk)
        {
            var runtime = await _javaRuntimeResolver.ResolveAsync(
                    plan.JavaMajorVersion,
                    downloadProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateManagedJavaResult(runtime);
            return runtime;
        }

        var resolver = _jdkResolver
            ?? throw new InvalidOperationException("此建立流程缺少 BuildTools 所需的可信 JDK resolver。");
        var jdk = await resolver.ResolveAsync(
                plan.JavaMajorVersion,
                downloadProgress,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateManagedJavaResult(jdk.JavaExecutablePath);
        ValidateManagedJavaResult(jdk.JavacExecutablePath);
        var expectedJavac = Path.Combine(
            Path.GetDirectoryName(jdk.JavaExecutablePath)
                ?? throw new InvalidDataException("JDK java 缺少 bin 目錄。"),
            "javac.exe");
        if (!Path.GetFullPath(jdk.JavacExecutablePath).Equals(
            Path.GetFullPath(expectedJavac),
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("JDK java 與 javac 不在同一個受管理的 bin 目錄。");
        }

        return jdk.JavaExecutablePath;
    }

    private static bool MatchesMinecraftVersion(string? actualVersion, CoreServerInstallPlan plan)
    {
        if (!plan.RequireMinecraftVersionEvidence)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(actualVersion)
               && actualVersion.Equals(plan.MinecraftVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsExactOfficialVanillaArtifactAsync(
        string jarPath,
        DetectionResult detection,
        CoreServerInstallPlan plan,
        CancellationToken cancellationToken)
    {
        if (!detection.IsValidJar
            || detection.CoreType != CoreType.Vanilla
            || detection.MainClass is not (
                "net.minecraft.server.MinecraftServer"
                or "net.minecraft.server.Main"
                or "net.minecraft.bundler.Main")
            || detection.MinecraftVersion is { } detectedVersion
                && !detectedVersion.Equals(plan.MinecraftVersion, StringComparison.OrdinalIgnoreCase)
            || plan.Product.SourceId != OfficialCoreServerCreationBackend.SourceId
            || plan.ExpectedCoreType != CoreType.Vanilla
            || plan.InstallKind != CoreServerInstallKind.DirectJar
            || plan.RequireMinecraftVersionEvidence
            || plan.SourcePlan is not OfficialServerCoreBuildInfo build
            || build.CoreType != CoreType.Vanilla
            || build.InstallStrategy != OfficialServerInstallStrategy.DirectServerJar
            || !build.IsStable
            || !build.MinecraftVersion.Equals(plan.MinecraftVersion, StringComparison.Ordinal)
            || !build.ProductVersion.Equals(plan.Version.Version.DisplayName, StringComparison.Ordinal)
            || build.DownloadUri is not { } source
            || !IsOfficialMojangServerUri(source, build.Hash)
            || build.FileName != "server.jar"
            || build.Size is not > 0
            || string.IsNullOrWhiteSpace(build.Hash))
        {
            return false;
        }

        var algorithm = build.HashAlgorithm switch
        {
            "SHA-1" when build.Hash.Length == 40 => HashAlgorithmName.SHA1,
            "SHA-256" when build.Hash.Length == 64 => HashAlgorithmName.SHA256,
            _ => default
        };
        if (algorithm == default || build.Hash.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        var file = new FileInfo(jarPath);
        if (file.Length != build.Size.Value)
        {
            throw new InvalidDataException("Mojang 官方 Vanilla JAR 大小與 exact catalog 不一致。");
        }

        await using var stream = new FileStream(
            jarPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = algorithm == HashAlgorithmName.SHA1
            ? await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)
            : await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var expected = Convert.FromHexString(build.Hash);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new InvalidDataException("Mojang 官方 Vanilla JAR hash 與 exact catalog 不一致。");
        }

        return true;
    }

    private static bool IsOfficialMojangServerUri(Uri source, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        return source.IsAbsoluteUri
            && source.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && source.IsDefaultPort
            && string.IsNullOrEmpty(source.UserInfo)
            && string.IsNullOrEmpty(source.Query)
            && string.IsNullOrEmpty(source.Fragment)
            && (source.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase)
                || source.Host.Equals("launcher.mojang.com", StringComparison.OrdinalIgnoreCase))
            && source.AbsolutePath.Equals(
                $"/v1/objects/{expectedHash.ToLowerInvariant()}/server.jar",
                StringComparison.Ordinal);
    }

    private static bool LoaderVersionsMatch(
        string? actual,
        string expected,
        string minecraftVersion)
        => !string.IsNullOrWhiteSpace(actual)
           && (actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
               || actual.Equals(
                   $"{minecraftVersion}-{expected}",
                   StringComparison.OrdinalIgnoreCase));

    private static bool SoftwareMatchesCoreType(CoreServerSoftware software, CoreType coreType)
        => software switch
        {
            CoreServerSoftware.Paper => coreType == CoreType.Paper,
            CoreServerSoftware.Spigot => coreType == CoreType.Spigot,
            CoreServerSoftware.CraftBukkit => coreType == CoreType.CraftBukkit,
            CoreServerSoftware.Forge => coreType == CoreType.Forge,
            CoreServerSoftware.NeoForge => coreType == CoreType.NeoForge,
            CoreServerSoftware.Fabric => coreType == CoreType.Fabric,
            CoreServerSoftware.Mohist => coreType == CoreType.Mohist,
            CoreServerSoftware.Arclight => coreType == CoreType.Arclight,
            CoreServerSoftware.CatServer => coreType == CoreType.CatServer,
            CoreServerSoftware.Akarin => coreType == CoreType.Akarin,
            CoreServerSoftware.Velocity => coreType == CoreType.Velocity,
            CoreServerSoftware.Vanilla => coreType == CoreType.Vanilla,
            _ => false
        };

    private static string? RemapPath(string oldRoot, string newRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var oldFull = Path.GetFullPath(oldRoot);
        var candidate = Path.GetFullPath(path);
        if (!SafePath.IsWithinRoot(oldFull, candidate))
        {
            return null;
        }

        return SafePath.CombineUnderRoot(newRoot, Path.GetRelativePath(oldFull, candidate));
    }

    private async Task DeleteOwnedTreeAsync(string path)
    {
        var safe = SafePath.EnsureWithinRoot(_paths.Servers, path, allowRoot: false);
        // Cleanup deliberately does not reuse the already-cancelled operation token. Wait for
        // short-lived Git/Java/antivirus handles to close, then require the incomplete tree to be
        // gone before cancellation/failure is reported to the UI.
        await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                _paths.Servers,
                safe,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _catalogLifetimeCancellation.Cancel();
        var disposed = new HashSet<IDisposable>(ReferenceEqualityComparer.Instance);
        for (var index = _ownedResources.Count - 1; index >= 0; index--)
        {
            if (disposed.Add(_ownedResources[index]))
            {
                _ownedResources[index].Dispose();
            }
        }

        _catalogLifetimeCancellation.Dispose();
    }

    private sealed record InstalledServerDetection(
        ServerPackDetectionResult? Pack,
        string? JarPath,
        DetectionResult? JarDetection);
}

internal enum CoreServerInstallKind
{
    DirectJar,
    OfficialLoaderInstaller,
    SpigotBuildTools
}

internal sealed record CoreServerBackendProduct(
    CoreServerProduct Product,
    CoreType ExpectedCoreType,
    string SourceId,
    bool IsProxy = false);

internal sealed record CoreServerBackendVersion(
    CoreServerVersion Version,
    int JavaMajorVersion);

internal sealed record CoreServerInstallPlan(
    CoreServerBackendProduct Product,
    CoreServerBackendVersion Version,
    CoreType ExpectedCoreType,
    string MinecraftVersion,
    int JavaMajorVersion,
    CoreServerInstallKind InstallKind,
    bool RequiresJdk,
    bool AcceptsArgumentFileLaunch,
    bool RequireMinecraftVersionEvidence,
    string? LoaderVersion,
    object SourcePlan);

internal sealed record CoreServerBackendInstallResult(
    IReadOnlyList<string> LaunchCandidates,
    CoreServerVerifiedInstallProvenance? Provenance = null);

internal enum CoreServerInstallProvenanceKind
{
    SpigotBuildToolsOfficialOutput,
    SpigotBuildToolsOfficialSources,
    OfficialLoader
}

internal sealed record CoreServerVerifiedInstallProvenance(
    CoreServerInstallProvenanceKind Kind,
    CoreType CoreType,
    string MinecraftVersion,
    IReadOnlyList<string> LaunchCandidates,
    string? ArtifactSha256 = null,
    string? LoaderVersion = null,
    OfficialLoaderInstallProvenance? OfficialLoader = null);

internal interface ICoreServerCreationBackend
{
    Task<IReadOnlyList<CoreServerBackendProduct>> GetProductsAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CoreServerBackendVersion>> GetVersionsAsync(
        CoreServerBackendProduct product,
        CancellationToken cancellationToken);

    Task<CoreServerInstallPlan> ResolveExactAsync(
        CoreServerBackendProduct product,
        CoreServerBackendVersion version,
        CancellationToken cancellationToken);

    Task<CoreServerBackendInstallResult> InstallAsync(
        CoreServerInstallPlan plan,
        string stagingDirectory,
        string javaExecutablePath,
        IProgress<CoreServerCreationProgress> progress,
        CancellationToken cancellationToken);
}
