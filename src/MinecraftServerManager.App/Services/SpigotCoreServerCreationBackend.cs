using System.Security.Cryptography;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

internal sealed class SpigotCoreServerCreationBackend : ICoreServerCreationBackend
{
    internal const string SourceId = "spigot-buildtools";
    private const string OfficialHashBuildText = "官方成品 SHA-256 與來源 refs 雙重驗證";
    private const string OfficialSourcesBuildText =
        "官方來源 refs 驗證（上游未提供成品 SHA-256）";
    private readonly ISpigotBuildToolsSource _source;
    private readonly ISpigotBuildToolsInstaller _installer;

    public SpigotCoreServerCreationBackend(
        SpigotBuildToolsProvider provider,
        SpigotBuildToolsRunner runner)
        : this(
            new SpigotBuildToolsSource(provider),
            new SpigotBuildToolsInstaller(runner))
    {
    }

    internal SpigotCoreServerCreationBackend(
        ISpigotBuildToolsSource source,
        ISpigotBuildToolsInstaller installer)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
    }

    public Task<IReadOnlyList<CoreServerBackendProduct>> GetProductsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CoreServerBackendProduct> products =
        [
            CreateProduct(CoreType.Spigot, CoreServerSoftware.Spigot, "Spigot",
                "Spigot 官方 BuildTools 本機建置；依版本使用官方成品 SHA-256 或官方來源 refs 嚴格驗證。"),
            CreateProduct(CoreType.CraftBukkit, CoreServerSoftware.CraftBukkit,
                "CraftBukkit (Bukkit)",
                "CraftBukkit 官方 BuildTools 本機建置；依版本使用官方成品 SHA-256 或官方來源 refs 嚴格驗證。")
        ];
        return Task.FromResult(products);
    }

    public async Task<IReadOnlyList<CoreServerBackendVersion>> GetVersionsAsync(
        CoreServerBackendProduct product,
        CancellationToken cancellationToken)
    {
        EnsureOwnedProduct(product);
        var versions = await _source.GetVersionsAsync(cancellationToken).ConfigureAwait(false);
        return CreateVersions(product, versions);
    }

    public async Task<CoreServerInstallPlan> ResolveExactAsync(
        CoreServerBackendProduct product,
        CoreServerBackendVersion version,
        CancellationToken cancellationToken)
    {
        EnsureOwnedProduct(product);
        var current = CreateVersions(
            product,
            await _source.GetVersionsAsync(cancellationToken).ConfigureAwait(false));
        if (!current.Contains(version))
        {
            throw new InvalidDataException(
                "Spigot／CraftBukkit 版本已從實際來源移除、變更，或不是 canonical 項目。");
        }

        var resolution = await _source.ResolvePlanAsync(
                product.ExpectedCoreType,
                version.Version.MinecraftVersion,
                cancellationToken)
            .ConfigureAwait(false);
        var buildPlan = resolution.Plan;
        if (!resolution.IsSupported || buildPlan is null)
        {
            throw new InvalidOperationException(
                resolution.UnsupportedReason
                ?? $"{product.Product.DisplayName} {version.Version.MinecraftVersion} 無法建立已驗證計畫。");
        }

        ValidateBuildPlan(product, version, buildPlan);
        return new CoreServerInstallPlan(
            product,
            version,
            product.ExpectedCoreType,
            buildPlan.MinecraftVersion,
            buildPlan.JavaMajorVersion,
            CoreServerInstallKind.SpigotBuildTools,
            RequiresJdk: true,
            AcceptsArgumentFileLaunch: false,
            // Modern definitions pin the exact output SHA-256. Historical definitions instead
            // bind the version through four official refs verified both before and after the build;
            // their flat JARs do not consistently self-report a normalized Minecraft version.
            RequireMinecraftVersionEvidence: false,
            LoaderVersion: null,
            buildPlan);
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
        var buildPlan = plan.SourcePlan as SpigotBuildPlan
            ?? throw new InvalidDataException("BuildTools install plan 缺少 exact build payload。");
        if (plan.InstallKind != CoreServerInstallKind.SpigotBuildTools
            || !plan.RequiresJdk
            || plan.AcceptsArgumentFileLaunch
            || plan.RequireMinecraftVersionEvidence)
        {
            throw new InvalidDataException("BuildTools install plan 的安全契約無效。");
        }

        ValidateBuildPlan(plan.Product, plan.Version, buildPlan);
        var toolsRoot = SafePath.CombineUnderRoot(stagingDirectory, ".buildtools");
        var toolPath = SafePath.CombineUnderRoot(toolsRoot, "BuildTools.jar");
        var destination = SafePath.CombineUnderRoot(stagingDirectory, "server.jar");
        Directory.CreateDirectory(toolsRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(stagingDirectory, toolsRoot);
        try
        {
            progress.Report(new(
                CoreServerCreationStage.Downloading,
                "正在下載並驗證已審查的官方 BuildTools.jar…",
                28));
            await _source.DownloadReviewedBuildToolsAsync(
                    toolPath,
                    new Progress<double>(value => progress.Report(new(
                        CoreServerCreationStage.Downloading,
                        "正在下載並驗證已審查的官方 BuildTools.jar…",
                        28 + Math.Clamp(value, 0d, 1d) * 18))),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var buildStageMessage =
                $"正在隔離環境建置 {buildPlan.DisplayName} {buildPlan.MinecraftVersion}…";
            progress.Report(new(
                CoreServerCreationStage.Installing,
                buildStageMessage,
                48));
            var result = await _installer.BuildAsync(
                    buildPlan,
                    javaExecutablePath,
                    toolPath,
                    stagingDirectory,
                    destination,
                    new CallbackProgress<ModrinthLoaderBootstrapOutputLine>(line => progress.Report(new(
                        CoreServerCreationStage.Installing,
                        buildStageMessage,
                        48,
                         SanitizeOutput(line.Text),
                         IsDetailIndeterminate: true))),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Plan != buildPlan
                || !Path.GetFullPath(result.FilePath).Equals(
                    Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(destination))
            {
                throw new InvalidDataException("BuildTools runner 回傳了錯 plan 或錯誤目的檔案。");
            }

            if (result.ActualOutputSha256.Length != 64
                || result.ActualOutputSha256.Any(character => !Uri.IsHexDigit(character))
                || buildPlan.ExpectedOutputSha256 is { } expected
                    && !CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(expected),
                        Convert.FromHexString(result.ActualOutputSha256)))
            {
                throw new InvalidDataException("BuildTools runner 回傳的 output SHA-256 證明無效。");
            }

            _ = SafePath.EnsureNoReparsePointsUnderRoot(stagingDirectory, destination);
            var verificationMessage = buildPlan.OutputVerificationKind ==
                SpigotBuildOutputVerificationKind.OfficialOutputSha256
                    ? $"{buildPlan.DisplayName} 官方 output SHA-256 驗證完成。"
                    : $"{buildPlan.DisplayName} 官方四個 source refs、核心結構與本機 SHA-256 驗證完成。";
            progress.Report(new(
                CoreServerCreationStage.Verifying,
                verificationMessage,
                86));
            var provenanceKind = buildPlan.OutputVerificationKind ==
                SpigotBuildOutputVerificationKind.OfficialOutputSha256
                    ? CoreServerInstallProvenanceKind.SpigotBuildToolsOfficialOutput
                    : CoreServerInstallProvenanceKind.SpigotBuildToolsOfficialSources;
            return new CoreServerBackendInstallResult(
                ["server.jar"],
                new CoreServerVerifiedInstallProvenance(
                    provenanceKind,
                    buildPlan.CoreType,
                    buildPlan.MinecraftVersion,
                    ["server.jar"],
                    result.ActualOutputSha256));
        }
        finally
        {
            SafePath.DeleteTreeWithoutFollowingReparsePoints(stagingDirectory, toolsRoot);
        }
    }

    private static IReadOnlyList<CoreServerBackendVersion> CreateVersions(
        CoreServerBackendProduct product,
        IReadOnlyList<SpigotBuildToolsVersionInfo>? versions)
    {
        if (versions is null)
        {
            throw new InvalidDataException("BuildTools provider 回傳了 null version catalog。");
        }

        var result = new List<CoreServerBackendVersion>(versions.Count);
        foreach (var metadata in versions)
        {
            if (!metadata.IsSupported
                || metadata.JavaMajorVersion is not (>= 8 and <= 99)
                || string.IsNullOrWhiteSpace(metadata.MinecraftVersion)
                || !string.IsNullOrWhiteSpace(metadata.UnsupportedReason))
            {
                throw new InvalidDataException("BuildTools provider 回傳無效或未受支援的版本。");
            }

            result.Add(new CoreServerBackendVersion(
                new CoreServerVersion(
                    product.Product.CoreId,
                    $"spigot-buildtools:{product.ExpectedCoreType.ToString().ToLowerInvariant()}:{metadata.MinecraftVersion}",
                    metadata.MinecraftVersion,
                    metadata.MinecraftVersion,
                    metadata.VerificationKind ==
                        SpigotBuildOutputVerificationKind.OfficialOutputSha256
                            ? OfficialHashBuildText
                            : OfficialSourcesBuildText,
                    ReleasedAtUtc: null,
                    IsRecommended: result.Count == 0),
                metadata.JavaMajorVersion.Value));
        }

        return result;
    }

    private static void ValidateBuildPlan(
        CoreServerBackendProduct product,
        CoreServerBackendVersion version,
        SpigotBuildPlan buildPlan)
    {
        if (buildPlan.CoreType != product.ExpectedCoreType
            || !buildPlan.DisplayName.Equals(product.Product.DisplayName, StringComparison.Ordinal)
            || !buildPlan.MinecraftVersion.Equals(version.Version.MinecraftVersion, StringComparison.Ordinal)
            || buildPlan.JavaMajorVersion != version.JavaMajorVersion
            || !buildPlan.OutputFileName.Equals("server.jar", StringComparison.Ordinal)
            || !HasValidOutputVerificationContract(buildPlan)
            || buildPlan.BuildTools != SpigotBuildToolsProvider.ReviewedBuildTools
            || buildPlan.RequiredBuildToolsVersion is < 1 or > 197
            || string.IsNullOrWhiteSpace(buildPlan.VersionIdentity)
            || buildPlan.SourceRefs is null
            || buildPlan.SourceRefs.Count != 4)
        {
            throw new InvalidDataException("BuildTools exact plan 與選取核心／版本不一致。");
        }
    }

    private static bool HasValidOutputVerificationContract(SpigotBuildPlan plan)
        => plan.OutputVerificationKind switch
        {
            SpigotBuildOutputVerificationKind.OfficialOutputSha256 =>
                plan.ExpectedOutputSha256 is { Length: 64 } expected
                && expected.All(Uri.IsHexDigit),
            SpigotBuildOutputVerificationKind.OfficialSourceRefs =>
                plan.ExpectedOutputSha256 is null,
            _ => false
        };

    private static CoreServerBackendProduct CreateProduct(
        CoreType coreType,
        CoreServerSoftware software,
        string displayName,
        string description)
        => new(
            new CoreServerProduct(
                software,
                $"spigot-buildtools:{coreType.ToString().ToLowerInvariant()}",
                displayName,
                description),
            coreType,
            SourceId);

    private static void EnsureOwnedProduct(CoreServerBackendProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (!product.SourceId.Equals(SourceId, StringComparison.Ordinal)
            || !product.Product.CoreId.StartsWith("spigot-buildtools:", StringComparison.Ordinal)
            || product.ExpectedCoreType is not (CoreType.Spigot or CoreType.CraftBukkit))
        {
            throw new InvalidDataException("核心 product 不屬於 Spigot BuildTools backend。");
        }
    }

    private static string SanitizeOutput(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "BuildTools 正在執行…";
        }

        var cleaned = new string(value
            .Where(character => !char.IsControl(character) || character == '\t')
            .ToArray())
            .Trim();
        return cleaned.Length == 0
            ? "BuildTools 正在執行…"
            : cleaned[..Math.Min(cleaned.Length, 240)];
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        private readonly Action<T> _callback = callback
            ?? throw new ArgumentNullException(nameof(callback));

        public void Report(T value) => _callback(value);
    }
}

internal interface ISpigotBuildToolsInstaller
{
    Task<SpigotBuildToolsBuildResult> BuildAsync(
        SpigotBuildPlan plan,
        string javaExecutablePath,
        string buildToolsJarPath,
        string stagingRoot,
        string destinationPath,
        IProgress<ModrinthLoaderBootstrapOutputLine>? output,
        CancellationToken cancellationToken);
}

internal interface ISpigotBuildToolsSource
{
    Task<IReadOnlyList<SpigotBuildToolsVersionInfo>> GetVersionsAsync(
        CancellationToken cancellationToken);

    Task<SpigotBuildPlanResolution> ResolvePlanAsync(
        CoreType coreType,
        string minecraftVersion,
        CancellationToken cancellationToken);

    Task<string> DownloadReviewedBuildToolsAsync(
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

internal sealed class SpigotBuildToolsSource(SpigotBuildToolsProvider provider)
    : ISpigotBuildToolsSource
{
    private readonly SpigotBuildToolsProvider _provider = provider
        ?? throw new ArgumentNullException(nameof(provider));

    public Task<IReadOnlyList<SpigotBuildToolsVersionInfo>> GetVersionsAsync(
        CancellationToken cancellationToken)
        => _provider.GetVersionsAsync(cancellationToken);

    public Task<SpigotBuildPlanResolution> ResolvePlanAsync(
        CoreType coreType,
        string minecraftVersion,
        CancellationToken cancellationToken)
        => _provider.ResolvePlanAsync(coreType, minecraftVersion, cancellationToken);

    public Task<string> DownloadReviewedBuildToolsAsync(
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
        => _provider.DownloadReviewedBuildToolsAsync(
            destinationPath,
            progress,
            cancellationToken);
}

internal sealed class SpigotBuildToolsInstaller(SpigotBuildToolsRunner runner)
    : ISpigotBuildToolsInstaller
{
    private readonly SpigotBuildToolsRunner _runner = runner
        ?? throw new ArgumentNullException(nameof(runner));

    public Task<SpigotBuildToolsBuildResult> BuildAsync(
        SpigotBuildPlan plan,
        string javaExecutablePath,
        string buildToolsJarPath,
        string stagingRoot,
        string destinationPath,
        IProgress<ModrinthLoaderBootstrapOutputLine>? output,
        CancellationToken cancellationToken)
        => _runner.BuildAsync(
            plan,
            javaExecutablePath,
            buildToolsJarPath,
            stagingRoot,
            destinationPath,
            output,
            cancellationToken);
}
