using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

internal sealed class HybridCoreServerCreationBackend : ICoreServerCreationBackend
{
    internal const string SourceId = "hybrid";
    private const string LatestVerifiedBuildText = "建立時重新解析最新已驗證建置";
    private readonly HybridServerCoreCatalogProvider _catalog;
    private readonly HybridServerCoreDownloader _downloader;

    public HybridCoreServerCreationBackend(
        HybridServerCoreCatalogProvider catalog,
        HybridServerCoreDownloader downloader)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
    }

    public Task<IReadOnlyList<CoreServerBackendProduct>> GetProductsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var products = _catalog.GetProducts();
        if (products is null)
        {
            throw new InvalidDataException("Hybrid catalog 回傳了 null product catalog。");
        }

        IReadOnlyList<CoreServerBackendProduct> result = products
            .Select(CreateProduct)
            .ToArray();
        return Task.FromResult(result);
    }

    public async Task<IReadOnlyList<CoreServerBackendVersion>> GetVersionsAsync(
        CoreServerBackendProduct product,
        CancellationToken cancellationToken)
    {
        EnsureOwnedProduct(product);
        var versions = await _catalog.GetVersionsAsync(
                product.ExpectedCoreType,
                cancellationToken)
            .ConfigureAwait(false);
        return FlattenVersions(product, versions);
    }

    public async Task<CoreServerInstallPlan> ResolveExactAsync(
        CoreServerBackendProduct product,
        CoreServerBackendVersion version,
        CancellationToken cancellationToken)
    {
        EnsureOwnedProduct(product);
        var selection = await ResolveCanonicalSelectionAsync(
                product,
                version,
                cancellationToken)
            .ConfigureAwait(false);
        var builds = await _catalog.GetBuildsAsync(
                product.ExpectedCoreType,
                selection.Metadata.MinecraftVersion,
                selection.Loader,
                cancellationToken)
            .ConfigureAwait(false);
        var build = builds.FirstOrDefault(candidate =>
            candidate.IsStable
            && candidate.CoreType == product.ExpectedCoreType
            && candidate.MinecraftVersion.Equals(
                selection.Metadata.MinecraftVersion,
                StringComparison.Ordinal)
            && candidate.ProductVersion.Equals(
                selection.Metadata.ProductVersion,
                StringComparison.Ordinal)
            && string.Equals(candidate.Loader, selection.Loader, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"{product.Product.DisplayName} {version.Version.DisplayName} 已沒有可用的已驗證建置。");
        ValidateResolvedBuild(product, version, selection, build);
        return new CoreServerInstallPlan(
            product,
            version,
            product.ExpectedCoreType,
            build.MinecraftVersion,
            build.JavaMajorVersion,
            CoreServerInstallKind.DirectJar,
            RequiresJdk: false,
            AcceptsArgumentFileLaunch: false,
            RequireMinecraftVersionEvidence: false,
            build.LoaderVersion,
            build);
    }

    public async Task<CoreServerBackendInstallResult> InstallAsync(
        CoreServerInstallPlan plan,
        string stagingDirectory,
        string javaExecutablePath,
        IProgress<CoreServerCreationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(progress);
        EnsureOwnedProduct(plan.Product);
        if (plan.InstallKind != CoreServerInstallKind.DirectJar
            || plan.AcceptsArgumentFileLaunch
            || plan.RequireMinecraftVersionEvidence)
        {
            throw new InvalidDataException("Hybrid install plan 的 direct-JAR 契約無效。");
        }

        var build = plan.SourcePlan as HybridServerCoreBuildInfo
            ?? throw new InvalidDataException("Hybrid install plan 缺少 exact build payload。");
        ValidatePlanBuild(plan, build);
        var destination = SafePath.CombineUnderRoot(stagingDirectory, "server.jar");
        progress.Report(new(
            CoreServerCreationStage.Downloading,
            $"正在下載並驗證 {build.DisplayName}…",
            28));
        var result = await _downloader.DownloadAsync(
                build,
                destination,
                new Progress<double>(value => progress.Report(new(
                    CoreServerCreationStage.Downloading,
                    $"正在下載並驗證 {build.DisplayName}…",
                    28 + Math.Clamp(value, 0d, 1d) * 55))),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Build != build
            || !Path.GetFullPath(result.FilePath).Equals(
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(destination))
        {
            throw new InvalidDataException("Hybrid downloader 回傳的 exact build 或目的檔案不一致。");
        }

        _ = SafePath.EnsureNoReparsePointsUnderRoot(stagingDirectory, destination);
        progress.Report(new(
            CoreServerCreationStage.Verifying,
            $"{build.Verification}、SHA-256 與檔案大小驗證完成。",
            85));
        return new CoreServerBackendInstallResult(["server.jar"]);
    }

    private async Task<HybridVersionSelection> ResolveCanonicalSelectionAsync(
        CoreServerBackendProduct product,
        CoreServerBackendVersion requested,
        CancellationToken cancellationToken)
    {
        var metadata = await _catalog.GetVersionsAsync(
                product.ExpectedCoreType,
                cancellationToken)
            .ConfigureAwait(false);
        var selections = FlattenSelections(product, metadata);
        var matches = selections.Where(candidate => candidate.Version == requested).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException(
                "Hybrid 版本已從實際來源移除、變更，或不是 catalog 的 canonical 項目。"),
            _ => throw new InvalidDataException("Hybrid catalog 回傳不明確的重複版本。")
        };
    }

    private static IReadOnlyList<CoreServerBackendVersion> FlattenVersions(
        CoreServerBackendProduct product,
        IReadOnlyList<HybridServerCoreVersionInfo>? versions)
        => FlattenSelections(product, versions)
            .Select(selection => selection.Version)
            .ToArray();

    private static IReadOnlyList<HybridVersionSelection> FlattenSelections(
        CoreServerBackendProduct product,
        IReadOnlyList<HybridServerCoreVersionInfo>? versions)
    {
        if (versions is null)
        {
            throw new InvalidDataException("Hybrid catalog 回傳了 null version catalog。");
        }

        var result = new List<HybridVersionSelection>();
        foreach (var metadata in versions)
        {
            ValidateCatalogVersion(product, metadata);
            var loaders = metadata.Loaders.Count == 0
                ? new string?[] { null }
                : metadata.Loaders
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(loader => loader, StringComparer.OrdinalIgnoreCase)
                    .Cast<string?>()
                    .ToArray();
            foreach (var loader in loaders)
            {
                var suffix = loader is null ? string.Empty : $" · {loader}";
                var identity = string.Join(
                    '\n',
                    product.Product.CoreId,
                    metadata.MinecraftVersion,
                    metadata.ProductVersion,
                    loader ?? string.Empty);
                var digest = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
                    .ToLowerInvariant();
                var version = new CoreServerBackendVersion(
                    new CoreServerVersion(
                        product.Product.CoreId,
                        $"hybrid:{product.ExpectedCoreType.ToString().ToLowerInvariant()}:{digest}",
                        $"{metadata.MinecraftVersion} · {metadata.ProductVersion}{suffix}",
                        metadata.MinecraftVersion,
                        loader is null
                            ? LatestVerifiedBuildText
                            : $"{LatestVerifiedBuildText} · {loader}",
                        ReleasedAtUtc: null,
                        IsRecommended: result.Count == 0),
                    metadata.JavaMajorVersion);
                result.Add(new HybridVersionSelection(version, metadata, loader));
            }
        }

        return result;
    }

    private static CoreServerBackendProduct CreateProduct(HybridServerCoreProductInfo metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!IsSupportedCore(metadata.CoreType)
            || string.IsNullOrWhiteSpace(metadata.DisplayName)
            || string.IsNullOrWhiteSpace(metadata.Description))
        {
            throw new InvalidDataException("Hybrid catalog 回傳了無效 product metadata。");
        }

        return new CoreServerBackendProduct(
            new CoreServerProduct(
                MapSoftware(metadata.CoreType),
                $"hybrid:{metadata.CoreType.ToString().ToLowerInvariant()}",
                metadata.DisplayName,
                GetDescription(metadata.CoreType)),
            metadata.CoreType,
            SourceId);
    }

    private static void ValidateCatalogVersion(
        CoreServerBackendProduct product,
        HybridServerCoreVersionInfo metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.CoreType != product.ExpectedCoreType
            || string.IsNullOrWhiteSpace(metadata.DisplayName)
            || string.IsNullOrWhiteSpace(metadata.MinecraftVersion)
            || string.IsNullOrWhiteSpace(metadata.ProductVersion)
            || metadata.JavaMajorVersion is < 8 or > 99
            || metadata.Loaders is null
            || metadata.Loaders.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("Hybrid catalog 回傳錯核心或無效版本。");
        }
    }

    private static void ValidateResolvedBuild(
        CoreServerBackendProduct product,
        CoreServerBackendVersion version,
        HybridVersionSelection selection,
        HybridServerCoreBuildInfo build)
    {
        if (build.CoreType != product.ExpectedCoreType
            || !build.IsStable
            || !build.MinecraftVersion.Equals(version.Version.MinecraftVersion, StringComparison.Ordinal)
            || !build.MinecraftVersion.Equals(selection.Metadata.MinecraftVersion, StringComparison.Ordinal)
            || !build.ProductVersion.Equals(selection.Metadata.ProductVersion, StringComparison.Ordinal)
            || !string.Equals(build.Loader, selection.Loader, StringComparison.OrdinalIgnoreCase)
            || build.JavaMajorVersion != version.JavaMajorVersion
            || build.JavaMajorVersion != selection.Metadata.JavaMajorVersion
            || string.IsNullOrWhiteSpace(build.BuildVersion)
            || string.IsNullOrWhiteSpace(build.FileName)
            || build.Size < 1
            || build.Sha256.Length != 64
            || build.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Hybrid exact build 與選取核心／版本不一致。");
        }
    }

    private static void ValidatePlanBuild(
        CoreServerInstallPlan plan,
        HybridServerCoreBuildInfo build)
    {
        if (build.CoreType != plan.ExpectedCoreType
            || !build.MinecraftVersion.Equals(plan.MinecraftVersion, StringComparison.Ordinal)
            || build.JavaMajorVersion != plan.JavaMajorVersion
            || !string.Equals(build.LoaderVersion, plan.LoaderVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Hybrid install plan 與 exact build 不一致。");
        }
    }

    private static void EnsureOwnedProduct(CoreServerBackendProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (!product.SourceId.Equals(SourceId, StringComparison.Ordinal)
            || !product.Product.CoreId.StartsWith("hybrid:", StringComparison.Ordinal)
            || !IsSupportedCore(product.ExpectedCoreType))
        {
            throw new InvalidDataException("核心 product 不屬於 hybrid backend。");
        }
    }

    private static bool IsSupportedCore(CoreType coreType)
        => coreType is CoreType.Mohist
            or CoreType.Arclight
            or CoreType.CatServer
            or CoreType.Akarin;

    private static CoreServerSoftware MapSoftware(CoreType coreType) => coreType switch
    {
        CoreType.Mohist => CoreServerSoftware.Mohist,
        CoreType.Arclight => CoreServerSoftware.Arclight,
        CoreType.CatServer => CoreServerSoftware.CatServer,
        CoreType.Akarin => CoreServerSoftware.Akarin,
        _ => throw new ArgumentOutOfRangeException(nameof(coreType), coreType, "非 hybrid 核心。")
    };

    private static string GetDescription(CoreType coreType) => coreType switch
    {
        CoreType.Mohist => "Forge 模組與 Bukkit／Spigot 插件混合核心。",
        CoreType.Arclight => "Forge／NeoForge／Fabric 模組與 Bukkit 插件混合核心。",
        CoreType.CatServer => "Forge 模組與 Bukkit／Spigot 插件混合核心（含受驗證歷史版本）。",
        CoreType.Akarin => "歷史 Paper 衍生核心；僅提供仍可完整驗證的官方版本。",
        _ => throw new ArgumentOutOfRangeException(nameof(coreType), coreType, "非 hybrid 核心。")
    };

    private sealed record HybridVersionSelection(
        CoreServerBackendVersion Version,
        HybridServerCoreVersionInfo Metadata,
        string? Loader);
}
