using System.Security.Cryptography;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

internal sealed class OfficialCoreServerCreationBackend : ICoreServerCreationBackend
{
    internal const string SourceId = "official";
    private const string LatestStableBuildText = "建立時重新解析最新穩定建置";
    private readonly OfficialServerCoreCatalogProvider _catalog;
    private readonly VerifiedDownloadClient _directDownloader;
    private readonly IModrinthOfficialLoaderArtifactProvider _loaderArtifacts;
    private readonly IModrinthLoaderBootstrapProcessRunner _loaderProcessRunner;

    public OfficialCoreServerCreationBackend(
        OfficialServerCoreCatalogProvider catalog,
        VerifiedDownloadClient directDownloader,
        IModrinthOfficialLoaderArtifactProvider loaderArtifacts,
        IModrinthLoaderBootstrapProcessRunner loaderProcessRunner)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _directDownloader = directDownloader ?? throw new ArgumentNullException(nameof(directDownloader));
        _loaderArtifacts = loaderArtifacts ?? throw new ArgumentNullException(nameof(loaderArtifacts));
        _loaderProcessRunner = loaderProcessRunner
            ?? throw new ArgumentNullException(nameof(loaderProcessRunner));
    }

    public Task<IReadOnlyList<CoreServerBackendProduct>> GetProductsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CoreServerBackendProduct> products = OfficialServerCoreCatalogProvider
            .SupportedCores
            .Select(descriptor => new CoreServerBackendProduct(
                new CoreServerProduct(
                    MapSoftware(descriptor.CoreType),
                    $"official:{descriptor.CoreType.ToString().ToLowerInvariant()}",
                    descriptor.DisplayName,
                    GetDescription(descriptor.CoreType)),
                descriptor.CoreType,
                SourceId,
                descriptor.IsProxy))
            .ToArray();
        return Task.FromResult(products);
    }

    public async Task<IReadOnlyList<CoreServerBackendVersion>> GetVersionsAsync(
        CoreServerBackendProduct product,
        CancellationToken cancellationToken)
    {
        EnsureOwnedProduct(product);
        var versions = await _catalog.GetVersionsAsync(product.ExpectedCoreType, cancellationToken)
            .ConfigureAwait(false);
        var result = new List<CoreServerBackendVersion>(versions.Count);
        for (var index = 0; index < versions.Count; index++)
        {
            var version = versions[index];
            ValidateCatalogVersion(product, version);
            result.Add(new CoreServerBackendVersion(
                new CoreServerVersion(
                    product.Product.CoreId,
                    $"{product.Product.CoreId}:{version.ProductVersion}",
                    version.ProductVersion,
                    version.MinecraftVersion,
                    LatestStableBuildText,
                    ReleasedAtUtc: null,
                    IsRecommended: index == 0),
                version.JavaMajorVersion));
        }

        return result;
    }

    public async Task<CoreServerInstallPlan> ResolveExactAsync(
        CoreServerBackendProduct product,
        CoreServerBackendVersion version,
        CancellationToken cancellationToken)
    {
        EnsureOwnedProduct(product);
        if (!version.Version.CoreId.Equals(product.Product.CoreId, StringComparison.OrdinalIgnoreCase)
            || !version.Version.Build.Equals(LatestStableBuildText, StringComparison.Ordinal))
        {
            throw new InvalidDataException("官方核心 version 不是此來源產生的 canonical 項目。");
        }

        var builds = await _catalog.GetBuildsAsync(
                product.ExpectedCoreType,
                version.Version.MinecraftVersion,
                cancellationToken)
            .ConfigureAwait(false);
        var build = builds.FirstOrDefault(item => item.IsStable)
            ?? throw new InvalidOperationException(
                $"{product.Product.DisplayName} {version.Version.DisplayName} 已沒有可用的 stable build。");
        ValidateResolvedBuild(product, version, build);
        return new CoreServerInstallPlan(
            product,
            version,
            product.ExpectedCoreType,
            version.Version.MinecraftVersion,
            version.JavaMajorVersion,
            build.InstallStrategy == OfficialServerInstallStrategy.DirectServerJar
                ? CoreServerInstallKind.DirectJar
                : CoreServerInstallKind.OfficialLoaderInstaller,
            RequiresJdk: false,
            AcceptsArgumentFileLaunch: build.InstallStrategy is
                OfficialServerInstallStrategy.ForgeInstaller
                or OfficialServerInstallStrategy.NeoForgeInstaller,
            // Direct artifacts are already tied to the selected version by upstream hash/size;
            // many legitimate historical Paper/Vanilla JARs do not self-report MinecraftVersion.
            // Forge/NeoForge output remains checked against its argument-file metadata.
            RequireMinecraftVersionEvidence: build.InstallStrategy is
                OfficialServerInstallStrategy.ForgeInstaller
                or OfficialServerInstallStrategy.NeoForgeInstaller,
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
        EnsureOwnedProduct(plan.Product);
        var build = plan.SourcePlan as OfficialServerCoreBuildInfo
            ?? throw new InvalidDataException("官方 install plan 缺少 exact build payload。");
        ValidateResolvedBuild(plan.Product, plan.Version, build);
        return build.InstallStrategy == OfficialServerInstallStrategy.DirectServerJar
            ? await InstallDirectJarAsync(
                    build,
                    stagingDirectory,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false)
            : await InstallLoaderAsync(
                    build,
                    stagingDirectory,
                    javaExecutablePath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<CoreServerBackendInstallResult> InstallDirectJarAsync(
        OfficialServerCoreBuildInfo build,
        string stagingDirectory,
        IProgress<CoreServerCreationProgress> progress,
        CancellationToken cancellationToken)
    {
        var source = build.DownloadUri
            ?? throw new InvalidDataException("官方 direct JAR build 缺少下載 URI。");
        var hash = build.Hash
            ?? throw new InvalidDataException("官方 direct JAR build 缺少 hash。");
        var algorithm = build.HashAlgorithm switch
        {
            "SHA-1" => HashAlgorithmName.SHA1,
            "SHA-256" => HashAlgorithmName.SHA256,
            _ => throw new InvalidDataException("官方 direct JAR 使用不支援的 hash algorithm。")
        };
        if (build.Size is not > 0)
        {
            throw new InvalidDataException("官方 direct JAR build 缺少有效大小。");
        }

        var finalPath = SafePath.CombineUnderRoot(stagingDirectory, "server.jar");
        var partialPath = SafePath.CombineUnderRoot(
            stagingDirectory,
            $".server-{Guid.NewGuid():N}.partial");
        progress.Report(new(
            CoreServerCreationStage.Downloading,
            $"正在下載並驗證 {build.DisplayName} {build.BuildVersion}…",
            28));
        await _directDownloader.DownloadAsync(
                source,
                partialPath,
                algorithm,
                hash,
                build.Size,
                new Progress<double>(value => progress.Report(new(
                    CoreServerCreationStage.Downloading,
                    $"正在下載並驗證 {build.DisplayName} {build.BuildVersion}…",
                    28 + Math.Clamp(value, 0d, 1d) * 52))),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(partialPath, finalPath, overwrite: false);
        progress.Report(new(
            CoreServerCreationStage.Verifying,
            $"{build.HashAlgorithm} 與檔案大小驗證完成。",
            84));
        return new CoreServerBackendInstallResult(["server.jar"]);
    }

    private async Task<CoreServerBackendInstallResult> InstallLoaderAsync(
        OfficialServerCoreBuildInfo build,
        string stagingDirectory,
        string javaExecutablePath,
        IProgress<CoreServerCreationProgress> progress,
        CancellationToken cancellationToken)
    {
        var kind = build.InstallStrategy switch
        {
            OfficialServerInstallStrategy.FabricInstaller => ModrinthModpackLoaderKind.Fabric,
            OfficialServerInstallStrategy.ForgeInstaller => ModrinthModpackLoaderKind.Forge,
            OfficialServerInstallStrategy.NeoForgeInstaller => ModrinthModpackLoaderKind.NeoForge,
            _ => throw new InvalidDataException("官方 build 不是支援的 Loader installer。")
        };
        if (string.IsNullOrWhiteSpace(build.LoaderVersion))
        {
            throw new InvalidDataException("官方 Loader build 缺少 exact LoaderVersion。");
        }

        var pinnedArtifacts = new ExactBuildLoaderArtifactProvider(_loaderArtifacts, build);
        var bootstrapper = new ModrinthLoaderServerBootstrapper(
            pinnedArtifacts,
            _loaderProcessRunner);
        progress.Report(new(
            CoreServerCreationStage.Installing,
            $"正在以官方 installer 建立 {build.DisplayName} {build.LoaderVersion}…",
            28));
        var result = await bootstrapper.BootstrapAsync(
                new ModrinthModpackLoaderInstallRequest(
                    kind,
                    build.MinecraftVersion,
                    build.LoaderVersion),
                stagingDirectory,
                javaExecutablePath,
                new Progress<ModrinthLoaderBootstrapProgress>(value =>
                    ReportLoaderProgress(progress, value)),
                new Progress<ModrinthLoaderBootstrapOutputLine>(line => progress.Report(new(
                    CoreServerCreationStage.Installing,
                    SanitizeOutput(line.Text),
                    null))),
                cancellationToken)
            .ConfigureAwait(false);
        var provenance = result.Provenance
            ?? throw new InvalidDataException(
                $"官方 {kind} installer 沒有回傳可重新驗證的 typed output provenance。");
        return new CoreServerBackendInstallResult(
            result.LaunchCandidates,
            new CoreServerVerifiedInstallProvenance(
                CoreServerInstallProvenanceKind.OfficialLoader,
                build.CoreType,
                build.MinecraftVersion,
                result.LaunchCandidates,
                ArtifactSha256: null,
                LoaderVersion: build.LoaderVersion,
                OfficialLoader: provenance));
    }

    private static void ReportLoaderProgress(
        IProgress<CoreServerCreationProgress> progress,
        ModrinthLoaderBootstrapProgress value)
    {
        var fraction = value.Fraction is { } known ? Math.Clamp(known, 0d, 1d) : (double?)null;
        var phase = value.Phase ?? string.Empty;
        var stage = phase.StartsWith("download-", StringComparison.Ordinal)
            ? CoreServerCreationStage.Downloading
            : phase.Equals("validate-output", StringComparison.Ordinal)
                ? CoreServerCreationStage.Verifying
                : CoreServerCreationStage.Installing;
        var start = phase switch
        {
            var item when item.StartsWith("download-", StringComparison.Ordinal) => 28d,
            "run-installer" => 56d,
            "validate-output" => 78d,
            "merge-output" => 84d,
            _ => 40d
        };
        var width = phase switch
        {
            var item when item.StartsWith("download-", StringComparison.Ordinal) => 26d,
            "run-installer" => 20d,
            "validate-output" => 5d,
            "merge-output" => 4d,
            _ => 0d
        };
        var message = phase switch
        {
            "download-fabric-installer" => "正在下載並驗證 exact Fabric Installer…",
            "download-forge-installer" => "正在下載並驗證 exact Forge Installer…",
            "download-neoforge-installer" => "正在下載並驗證 exact NeoForge Installer…",
            "run-installer" => "正在隔離環境執行官方 Server Installer…",
            "validate-output" => "正在驗證官方 Loader 安裝輸出…",
            "merge-output" => "正在合併已驗證的 Loader 輸出…",
            _ => "正在安裝官方 Server Loader…"
        };
        progress.Report(new(
            stage,
            message,
            fraction is null || width == 0 ? null : start + fraction.Value * width));
    }

    private static void EnsureOwnedProduct(CoreServerBackendProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (!product.SourceId.Equals(SourceId, StringComparison.Ordinal)
            || !product.Product.CoreId.StartsWith("official:", StringComparison.Ordinal))
        {
            throw new InvalidDataException("核心 product 不屬於 official backend。");
        }
    }

    private static void ValidateCatalogVersion(
        CoreServerBackendProduct product,
        OfficialServerCoreVersionInfo version)
    {
        if (version.CoreType != product.ExpectedCoreType
            || string.IsNullOrWhiteSpace(version.ProductVersion)
            || string.IsNullOrWhiteSpace(version.MinecraftVersion)
            || version.JavaMajorVersion is < 8 or > 99)
        {
            throw new InvalidDataException("官方 catalog 回傳錯核心或無效版本。");
        }
    }

    private static void ValidateResolvedBuild(
        CoreServerBackendProduct product,
        CoreServerBackendVersion version,
        OfficialServerCoreBuildInfo build)
    {
        if (build.CoreType != product.ExpectedCoreType
            || !build.IsStable
            || !build.MinecraftVersion.Equals(
                version.Version.MinecraftVersion,
                StringComparison.Ordinal)
            || !build.ProductVersion.Equals(
                version.Version.DisplayName,
                StringComparison.Ordinal)
            || !Enum.IsDefined(build.InstallStrategy)
            || !InstallStrategyMatchesCore(build.InstallStrategy, product.ExpectedCoreType))
        {
            throw new InvalidDataException("官方 exact build 與選取核心／版本不一致。");
        }
    }

    private static bool InstallStrategyMatchesCore(
        OfficialServerInstallStrategy strategy,
        CoreType coreType)
        => coreType switch
        {
            CoreType.Paper or CoreType.Velocity or CoreType.Vanilla =>
                strategy == OfficialServerInstallStrategy.DirectServerJar,
            CoreType.Fabric => strategy == OfficialServerInstallStrategy.FabricInstaller,
            CoreType.Forge => strategy == OfficialServerInstallStrategy.ForgeInstaller,
            CoreType.NeoForge => strategy == OfficialServerInstallStrategy.NeoForgeInstaller,
            _ => false
        };

    private static CoreServerSoftware MapSoftware(CoreType coreType) => coreType switch
    {
        CoreType.Paper => CoreServerSoftware.Paper,
        CoreType.Velocity => CoreServerSoftware.Velocity,
        CoreType.Vanilla => CoreServerSoftware.Vanilla,
        CoreType.Fabric => CoreServerSoftware.Fabric,
        CoreType.Forge => CoreServerSoftware.Forge,
        CoreType.NeoForge => CoreServerSoftware.NeoForge,
        _ => throw new ArgumentOutOfRangeException(nameof(coreType), coreType, "非 official 核心。")
    };

    private static string GetDescription(CoreType coreType) => coreType switch
    {
        CoreType.Paper => "PaperMC 官方高效能 Bukkit 相容核心。",
        CoreType.Velocity => "PaperMC 官方 Minecraft Proxy；Port 由 --port 參數管理。",
        CoreType.Vanilla => "Mojang 官方原版 Dedicated Server。",
        CoreType.Fabric => "Fabric 官方 Loader Server。",
        CoreType.Forge => "MinecraftForge 官方 Server Loader。",
        CoreType.NeoForge => "NeoForged 官方 Server Loader。",
        _ => "官方 Server 核心。"
    };

    private static string SanitizeOutput(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "官方 Loader Installer 正在執行…";
        }

        var cleaned = new string(value
            .Where(character => !char.IsControl(character) || character == '\t')
            .ToArray())
            .Trim();
        return cleaned.Length == 0
            ? "官方 Loader Installer 正在執行…"
            : cleaned[..Math.Min(cleaned.Length, 240)];
    }

    private sealed class ExactBuildLoaderArtifactProvider(
        IModrinthOfficialLoaderArtifactProvider inner,
        OfficialServerCoreBuildInfo expected) : IModrinthOfficialLoaderArtifactProvider
    {
        public Task<ModrinthLoaderArtifact> DownloadVanillaServerAsync(
            string minecraftVersion,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Loader plan 不得改走 Vanilla download。");

        public Task VerifyVanillaServerAsync(
            string minecraftVersion,
            string serverJarPath,
            CancellationToken cancellationToken = default)
            => inner.VerifyVanillaServerAsync(minecraftVersion, serverJarPath, cancellationToken);

        public async Task<ModrinthLoaderArtifact> DownloadLatestStableFabricInstallerAsync(
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
            => ValidateExactArtifact(await inner.DownloadLatestStableFabricInstallerAsync(
                    destinationPath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false));

        public async Task<ModrinthLoaderArtifact> DownloadForgeInstallerAsync(
            string minecraftVersion,
            string loaderVersion,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
            => ValidateExactArtifact(await inner.DownloadForgeInstallerAsync(
                    minecraftVersion,
                    loaderVersion,
                    destinationPath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false));

        public async Task<ModrinthLoaderArtifact> DownloadNeoForgeInstallerAsync(
            string loaderVersion,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
            => ValidateExactArtifact(await inner.DownloadNeoForgeInstallerAsync(
                    loaderVersion,
                    destinationPath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false));

        private ModrinthLoaderArtifact ValidateExactArtifact(ModrinthLoaderArtifact artifact)
        {
            var expectedUri = expected.DownloadUri
                ?? throw new InvalidDataException("Official Loader build 缺少 expected URI。");
            if (!artifact.Source.AbsoluteUri.Equals(expectedUri.AbsoluteUri, StringComparison.Ordinal))
            {
                TryDelete(artifact.FilePath);
                throw new InvalidDataException(
                    "官方 Loader 的 latest build 在建立期間改變；為避免安裝非選定 build，請重新整理版本後再試。");
            }

            return artifact;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // The enclosing isolated loader operation owns and removes the whole tool tree.
            }
        }
    }
}
