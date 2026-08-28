using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// The effective heap range selected for one interactive Minecraft Java client launch.
/// </summary>
public sealed record MinecraftClientMemoryResolution(
    MinecraftClientMemoryMode ConfiguredMode,
    MinecraftClientMemoryMode EffectiveMode,
    int MinimumMemoryMb,
    int MaximumMemoryMb,
    int InstalledModJarCount,
    long InstalledModJarBytes,
    bool ModScanTruncated,
    int ReservedSystemMemoryMb,
    int SystemSafeAllocationCeilingMb,
    int EffectiveAllocationCeilingMb,
    bool WasConstrainedBySafetyCeiling,
    bool UsedFallbackMemoryProbe,
    string Explanation);

/// <summary>
/// Resolves global-default, automatic, and manual client heap policies without allowing one game
/// process to consume all currently available physical memory. Automatic mode performs only a
/// bounded, top-level metadata scan of the instance's <c>mods</c> directory; it never opens or
/// hashes mod archives.
/// </summary>
public sealed class MinecraftClientMemoryRecommendationService
{
    public const int MinimumAllocationMb = 512;
    public const int MaximumConfiguredAllocationMb = 262_144;
    public const int MaximumClientHeapMb = 32_768;
    public const int MaximumScannedModFiles = 4_096;

    private const long Mebibyte = 1024L * 1024L;
    private const int MinimumSystemReserveMb = 4_096;
    private const int AllocationStepMb = 256;

    private readonly ISystemMemoryProbe _memoryProbe;

    public MinecraftClientMemoryRecommendationService(ISystemMemoryProbe memoryProbe)
    {
        _memoryProbe = memoryProbe ?? throw new ArgumentNullException(nameof(memoryProbe));
    }

    public MinecraftClientMemoryResolution Resolve(
        MinecraftClientInstance instance,
        NewMinecraftClientDefaultsSettings globalDefaults,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(globalDefaults);
        cancellationToken.ThrowIfCancellationRequested();

        if (instance.Edition != MinecraftClientEdition.Java)
        {
            throw new NotSupportedException(
                "Java heap allocation is available only for Minecraft Java Edition instances.");
        }

        var capacity = ResolveCapacity(_memoryProbe.GetSnapshot());
        var configuredMode = instance.MemoryMode;
        var effectiveMode = configuredMode;
        var sourceText = "instance";
        ModScan scan = default;
        int requestedMinimumMb;
        int requestedMaximumMb;

        switch (configuredMode)
        {
            case MinecraftClientMemoryMode.UseGlobalDefault:
                effectiveMode = globalDefaults.MemoryMode;
                sourceText = "global default";
                if (effectiveMode == MinecraftClientMemoryMode.UseGlobalDefault)
                {
                    throw new InvalidOperationException(
                        "The global Minecraft client memory policy cannot reference itself.");
                }

                if (effectiveMode == MinecraftClientMemoryMode.Automatic)
                {
                    scan = ScanInstalledMods(instance.DirectoryPath, cancellationToken);
                    (requestedMinimumMb, requestedMaximumMb) = SelectAutomaticRange(instance.Loader, scan);
                }
                else
                {
                    ValidateConfiguredRange(
                        globalDefaults.MinimumMemoryMb,
                        globalDefaults.MaximumMemoryMb,
                        nameof(globalDefaults));
                    requestedMinimumMb = globalDefaults.MinimumMemoryMb;
                    requestedMaximumMb = globalDefaults.MaximumMemoryMb;
                }

                break;

            case MinecraftClientMemoryMode.Automatic:
                scan = ScanInstalledMods(instance.DirectoryPath, cancellationToken);
                (requestedMinimumMb, requestedMaximumMb) = SelectAutomaticRange(instance.Loader, scan);
                break;

            case MinecraftClientMemoryMode.Manual:
                ValidateConfiguredRange(
                    instance.MinimumMemoryMb,
                    instance.MaximumMemoryMb,
                    nameof(instance));
                requestedMinimumMb = instance.MinimumMemoryMb;
                requestedMaximumMb = instance.MaximumMemoryMb;
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(instance),
                    configuredMode,
                    "The Minecraft client memory mode is unsupported.");
        }

        var maximumMb = Math.Min(requestedMaximumMb, capacity.EffectiveCeilingMb);
        maximumMb = Math.Max(MinimumAllocationMb, maximumMb);
        var minimumMb = Math.Clamp(requestedMinimumMb, MinimumAllocationMb, maximumMb);
        var constrained = minimumMb != requestedMinimumMb || maximumMb != requestedMaximumMb;

        var scanText = effectiveMode == MinecraftClientMemoryMode.Automatic
            ? FormattableString.Invariant(
                $"; found {scan.Count} top-level mod JAR(s) ({scan.Bytes / (double)Mebibyte:0.##} MiB)")
                + (scan.Truncated ? ", scan bounded at the safety limit" : string.Empty)
            : string.Empty;
        var constraintText = constrained
            ? FormattableString.Invariant(
                $"; reduced to {minimumMb}-{maximumMb} MB by the safe allocation ceiling")
            : string.Empty;
        var fallbackText = capacity.UsedFallbackProbe
            ? "; physical-memory probing used the conservative fallback"
            : string.Empty;
        var explanation =
            $"Resolved {sourceText} {effectiveMode} memory at {minimumMb}-{maximumMb} MB"
            + $"; reserved {capacity.ReservedMb} MB for Windows and other processes"
            + scanText
            + constraintText
            + fallbackText
            + ".";

        return new MinecraftClientMemoryResolution(
            configuredMode,
            effectiveMode,
            minimumMb,
            maximumMb,
            scan.Count,
            scan.Bytes,
            scan.Truncated,
            capacity.ReservedMb,
            capacity.SystemCeilingMb,
            capacity.EffectiveCeilingMb,
            constrained,
            capacity.UsedFallbackProbe,
            explanation);
    }

    public Task<MinecraftClientMemoryResolution> ResolveAsync(
        MinecraftClientInstance instance,
        NewMinecraftClientDefaultsSettings globalDefaults,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => Resolve(instance, globalDefaults, cancellationToken),
            cancellationToken);

    private static ModScan ScanInstalledMods(
        string instanceDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceDirectory);
        if (!Path.IsPathFullyQualified(instanceDirectory))
        {
            throw new ArgumentException(
                "Minecraft client instance directory must be an absolute path.",
                nameof(instanceDirectory));
        }

        var root = Path.GetFullPath(instanceDirectory);
        if (File.Exists(root) && !Directory.Exists(root))
        {
            throw new InvalidDataException("Minecraft client instance path is a file.");
        }

        if (!Directory.Exists(root))
        {
            return default;
        }

        RejectReparsePoint(root, "Minecraft client instance directory");
        var modsDirectory = SafePath.CombineUnderRoot(root, "mods");
        if (File.Exists(modsDirectory) && !Directory.Exists(modsDirectory))
        {
            throw new InvalidDataException("Minecraft client mods path is a file.");
        }

        if (!Directory.Exists(modsDirectory))
        {
            return default;
        }

        RejectReparsePoint(modsDirectory, "Minecraft client mods directory");
        var count = 0;
        var inspectedFiles = 0;
        long bytes = 0;
        var truncated = false;
        foreach (var path in Directory.EnumerateFiles(
                     modsDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inspectedFiles >= MaximumScannedModFiles)
            {
                truncated = true;
                break;
            }

            inspectedFiles++;
            if (!string.Equals(Path.GetExtension(path), ".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var file = new FileInfo(path);
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            count = checked(count + 1);
            bytes = SaturatingAdd(bytes, Math.Max(0, file.Length));
        }

        return new ModScan(count, bytes, truncated);
    }

    private static (int MinimumMb, int MaximumMb) SelectAutomaticRange(
        MinecraftClientLoader loader,
        ModScan scan)
    {
        if (scan.Truncated || scan.Count > 350)
        {
            return (8_192, 12_288);
        }

        return scan.Count switch
        {
            0 => loader switch
            {
                MinecraftClientLoader.Vanilla or MinecraftClientLoader.OptiFine => (1_024, 3_072),
                MinecraftClientLoader.Fabric or MinecraftClientLoader.Quilt => (1_536, 3_584),
                MinecraftClientLoader.Forge or MinecraftClientLoader.NeoForge or
                    MinecraftClientLoader.LabyMod => (2_048, 4_096),
                _ => (2_048, 4_096),
            },
            <= 40 => (2_048, 4_096),
            <= 100 => (3_072, 6_144),
            <= 200 => (4_096, 8_192),
            <= 350 => (6_144, 10_240),
            _ => (8_192, 12_288),
        };
    }

    private static Capacity ResolveCapacity(SystemMemorySnapshot snapshot)
    {
        var minimumBytes = MinimumAllocationMb * Mebibyte;
        var totalBytes = Math.Max(minimumBytes, snapshot.TotalPhysicalBytes);
        var availableBytes = Math.Clamp(snapshot.AvailablePhysicalBytes, 0, totalBytes);
        var totalMb = BytesToWholeMebibytes(totalBytes);
        var availableMb = BytesToWholeMebibytes(availableBytes);
        var reserveMb = Math.Max(MinimumSystemReserveMb, DivideRoundingUp(totalMb, 5));
        var installedCeilingMb = Math.Max(MinimumAllocationMb, totalMb - reserveMb);
        var availableCeilingMb = Math.Max(MinimumAllocationMb, availableMb - reserveMb);
        var systemCeilingMb = FloorToStep(
            Math.Min(installedCeilingMb, availableCeilingMb),
            AllocationStepMb,
            MinimumAllocationMb);
        var effectiveCeilingMb = Math.Min(systemCeilingMb, MaximumClientHeapMb);
        return new Capacity(
            reserveMb,
            systemCeilingMb,
            effectiveCeilingMb,
            snapshot.IsFallback);
    }

    private static void ValidateConfiguredRange(int minimumMb, int maximumMb, string parameterName)
    {
        if (minimumMb is < MinimumAllocationMb or > MaximumConfiguredAllocationMb ||
            maximumMb < minimumMb ||
            maximumMb > MaximumConfiguredAllocationMb)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Minecraft client memory must be a valid 512-262144 MB range.");
        }
    }

    private static void RejectReparsePoint(string path, string label)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException($"{label} cannot be a reparse point: '{path}'.");
        }
    }

    private static int BytesToWholeMebibytes(long bytes)
        => checked((int)Math.Min(int.MaxValue, Math.Max(0, bytes / Mebibyte)));

    private static int DivideRoundingUp(int value, int divisor)
        => checked((int)(((long)value + divisor - 1) / divisor));

    private static int FloorToStep(int value, int step, int minimum)
        => Math.Max(minimum, value / step * step);

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private readonly record struct ModScan(int Count, long Bytes, bool Truncated);

    private readonly record struct Capacity(
        int ReservedMb,
        int SystemCeilingMb,
        int EffectiveCeilingMb,
        bool UsedFallbackProbe);
}
