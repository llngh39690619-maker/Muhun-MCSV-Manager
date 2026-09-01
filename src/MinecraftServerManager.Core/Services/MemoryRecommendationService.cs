using System.Globalization;

namespace MinecraftServerManager.Core.Services;

/// <summary>A bounded JVM-memory recommendation and the cheap evidence used to derive it.</summary>
public sealed record MemoryRecommendation(
    int MinimumMemoryMb,
    int MaximumMemoryMb,
    int AddonJarCount,
    long AddonJarBytes,
    int ReservedSystemMemoryMb,
    int SafeAllocationCeilingMb,
    bool WasConstrainedBySystemMemory,
    string Explanation);

/// <summary>
/// Estimates a useful JVM range from top-level mod/plugin JAR metadata and current physical RAM.
/// It deliberately never opens archives, hashes files, or traverses worlds and nested folders.
/// </summary>
public sealed class MemoryRecommendationService
{
    private const long Mebibyte = 1024L * 1024L;
    private const int MinimumValidAllocationMb = 512;
    private const int MinimumSystemReserveMb = 4096;
    private const int AllocationStepMb = 256;
    private const int HighestTierMinimumAddonCount = 351;

    private readonly ISystemMemoryProbe _memoryProbe;

    public MemoryRecommendationService(ISystemMemoryProbe memoryProbe)
    {
        _memoryProbe = memoryProbe ?? throw new ArgumentNullException(nameof(memoryProbe));
    }

    public MemoryRecommendation Recommend(
        string serverRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.GetFullPath(serverRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Server directory does not exist: '{root}'.");
        }

        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                $"Server directory cannot be a reparse point: '{root}'.");
        }

        var scan = new AddonScan();
        ScanTopLevelJarDirectory(root, "mods", scan, cancellationToken);
        ScanTopLevelJarDirectory(root, "plugins", scan, cancellationToken);

        return RecommendCore(
            scan.Count,
            scan.Bytes,
            addonsTruncated: false,
            cancellationToken);
    }

    /// <summary>
    /// Recommends a JVM range from already-bounded add-on metadata without accessing a server
    /// directory. A truncated list is conservatively assigned the highest add-on-count tier.
    /// </summary>
    public MemoryRecommendation Recommend(
        int addonJarCount,
        long addonJarBytes,
        bool addonsTruncated = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(addonJarCount);
        ArgumentOutOfRangeException.ThrowIfNegative(addonJarBytes);
        cancellationToken.ThrowIfCancellationRequested();

        return RecommendCore(
            addonJarCount,
            addonJarBytes,
            addonsTruncated,
            cancellationToken);
    }

    private MemoryRecommendation RecommendCore(
        int addonJarCount,
        long addonJarBytes,
        bool addonsTruncated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tierCount = addonsTruncated
            ? Math.Max(addonJarCount, HighestTierMinimumAddonCount)
            : addonJarCount;
        var tier = SelectTier(tierCount);
        var snapshot = NormalizeSnapshot(_memoryProbe.GetSnapshot());
        cancellationToken.ThrowIfCancellationRequested();
        var totalMb = BytesToWholeMebibytes(snapshot.TotalPhysicalBytes);
        var availableMb = Math.Min(
            totalMb,
            BytesToWholeMebibytes(snapshot.AvailablePhysicalBytes));
        var twentyPercentMb = DivideRoundingUp(totalMb, 5);
        var reserveMb = Math.Max(MinimumSystemReserveMb, twentyPercentMb);

        var ceilingFromInstalledMemory = Math.Max(
            MinimumValidAllocationMb,
            totalMb - reserveMb);
        var ceilingFromCurrentlyAvailableMemory = Math.Max(
            MinimumValidAllocationMb,
            availableMb - reserveMb);
        var safeCeilingMb = FloorToStep(
            Math.Min(ceilingFromInstalledMemory, ceilingFromCurrentlyAvailableMemory),
            AllocationStepMb,
            MinimumValidAllocationMb);

        var maximumMb = Math.Min(tier.MaximumMb, safeCeilingMb);
        var minimumMb = Math.Min(tier.MinimumMb, maximumMb);
        maximumMb = Math.Max(MinimumValidAllocationMb, maximumMb);
        minimumMb = Math.Clamp(minimumMb, MinimumValidAllocationMb, maximumMb);

        var constrained = maximumMb < tier.MaximumMb;
        var sizeMib = addonJarBytes / (double)Mebibyte;
        var fallbackText = snapshot.IsFallback ? "；系統記憶體探測使用保守備援值" : string.Empty;
        var constrainedText = constrained
            ? $"；因目前可用記憶體而將上限縮減為 {maximumMb.ToString(CultureInfo.InvariantCulture)} MB"
            : string.Empty;
        var addonEvidence = addonsTruncated
            ? "模組/插件清單已截斷" +
              $"（已知 {addonJarCount.ToString(CultureInfo.InvariantCulture)} 個頂層 JAR，" +
              $"共 {sizeMib.ToString("0.##", CultureInfo.InvariantCulture)} MiB），" +
              $"為避免低估而按至少 {HighestTierMinimumAddonCount.ToString(CultureInfo.InvariantCulture)} 個估算"
            : $"偵測到 {addonJarCount.ToString(CultureInfo.InvariantCulture)} 個頂層模組/插件 JAR" +
              $"（{sizeMib.ToString("0.##", CultureInfo.InvariantCulture)} MiB）";
        var explanation = addonEvidence + "，" +
            $"基準範圍 {tier.MinimumMb.ToString(CultureInfo.InvariantCulture)}–" +
            $"{tier.MaximumMb.ToString(CultureInfo.InvariantCulture)} MB；" +
            $"為系統保留 {reserveMb.ToString(CultureInfo.InvariantCulture)} MB" +
            constrainedText + fallbackText + "。";

        return new MemoryRecommendation(
            minimumMb,
            maximumMb,
            addonJarCount,
            addonJarBytes,
            reserveMb,
            safeCeilingMb,
            constrained,
            explanation);
    }

    public Task<MemoryRecommendation> RecommendAsync(
        string serverRoot,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Recommend(serverRoot, cancellationToken), cancellationToken);

    private static void ScanTopLevelJarDirectory(
        string root,
        string directoryName,
        AddonScan scan,
        CancellationToken cancellationToken)
    {
        var directoryPath = SafePath.CombineUnderRoot(root, directoryName);
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        if (File.GetAttributes(directoryPath).HasFlag(FileAttributes.ReparsePoint))
        {
            // A redirected directory is not part of the physical server root and must not be
            // followed merely to produce a recommendation.
            return;
        }

        foreach (var path in Directory.EnumerateFiles(
                     directoryPath,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetExtension(path), ".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var file = new FileInfo(path);
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            scan.Count = checked(scan.Count + 1);
            scan.Bytes = SaturatingAdd(scan.Bytes, Math.Max(0, file.Length));
        }
    }

    private static RecommendationTier SelectTier(int jarCount) => jarCount switch
    {
        0 => new RecommendationTier(1024, 2048),
        <= 50 => new RecommendationTier(2048, 4096),
        <= 120 => new RecommendationTier(3072, 6144),
        <= 220 => new RecommendationTier(4096, 8192),
        <= 350 => new RecommendationTier(6144, 10240),
        _ => new RecommendationTier(8192, 12288)
    };

    private static SystemMemorySnapshot NormalizeSnapshot(SystemMemorySnapshot snapshot)
    {
        var total = Math.Max(Mebibyte, snapshot.TotalPhysicalBytes);
        var available = Math.Clamp(snapshot.AvailablePhysicalBytes, 0, total);
        return snapshot with
        {
            TotalPhysicalBytes = total,
            AvailablePhysicalBytes = available
        };
    }

    private static int BytesToWholeMebibytes(long bytes)
        => checked((int)Math.Min(int.MaxValue, Math.Max(0, bytes / Mebibyte)));

    private static int DivideRoundingUp(int value, int divisor)
        => checked((int)(((long)value + divisor - 1) / divisor));

    private static int FloorToStep(int value, int step, int minimum)
        => Math.Max(minimum, value / step * step);

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed class AddonScan
    {
        public int Count { get; set; }

        public long Bytes { get; set; }
    }

    private readonly record struct RecommendationTier(int MinimumMb, int MaximumMb);
}
