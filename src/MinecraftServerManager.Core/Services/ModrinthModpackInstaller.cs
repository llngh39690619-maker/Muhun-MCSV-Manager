using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Services;

public sealed record ModrinthModpackInstallOptions(
    bool IncludeOptionalFiles = false,
    SafeModpackArchiveLimits? SafetyLimits = null,
    int MaxConcurrentDownloads = 16,
    bool UseAdaptiveConcurrency = true);

public sealed record ModrinthModpackInstallProgress(
    string Phase,
    int CompletedFiles,
    int TotalFiles,
    string? CurrentPath = null,
    int EffectiveConcurrentDownloads = 0,
    bool UsesAdaptiveConcurrency = false);

public sealed record ModrinthModpackInstallResult(
    string ProjectId,
    string ApiVersionId,
    string PackName,
    string PackVersionId,
    string StagingDirectory,
    string MinecraftVersion,
    ModrinthModpackLoaderInstallRequest LoaderInstallRequest,
    int InstalledContentFiles,
    int SkippedUnsupportedFiles,
    int SkippedOptionalFiles,
    IReadOnlyList<SafeModpackOptionalFile> OptionalFiles,
    IReadOnlyList<string> InstalledPaths);

/// <summary>
/// Installs verified Modrinth content into a caller-created empty staging directory. It does not
/// register a server or execute a loader installer; the returned request is the explicit handoff
/// contract for the existing server-core installation workflow.
/// </summary>
public sealed class ModrinthModpackInstaller
{
    private const int MaximumDownloadConcurrency = 16;
    private readonly ModrinthModpackArtifactDownloader _downloader;

    public ModrinthModpackInstaller(ModrinthModpackArtifactDownloader downloader)
        => _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));

    public Task<SafeModpackArchivePlan> InspectAsync(
        string mrpackPath,
        SafeModpackArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
        => SafeModpackArchive.InspectAsync(mrpackPath, _downloader.UriPolicy, limits, cancellationToken);

    public async Task<ModrinthModpackInstallResult> InstallAsync(
        ModrinthModpackVersion version,
        string stagingDirectory,
        ModrinthModpackInstallOptions? options = null,
        IProgress<ModrinthModpackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        options ??= new ModrinthModpackInstallOptions();
        ValidateDownloadConcurrency(options.MaxConcurrentDownloads);
        var limits = options.SafetyLimits ?? new SafeModpackArchiveLimits();
        limits.Validate();
        var mrpack = version.MrpackFile
            ?? throw new InvalidOperationException("此 Modrinth 版本沒有可安裝的 .mrpack 檔案。");
        if (mrpack.Size > limits.MaxArchiveBytes)
        {
            throw new InvalidDataException("Modrinth API 宣告的 .mrpack 大小超過安全上限。");
        }
        var staging = SafeModpackArchive.EnsureSafeStagingDirectory(stagingDirectory, requireEmpty: true);
        var parent = Path.GetDirectoryName(staging)
            ?? throw new InvalidOperationException("Staging 資料夾沒有父目錄。");
        var temporaryPack = Path.Combine(parent, ".muhun-modrinth-" + Guid.NewGuid().ToString("N") + ".mrpack");
        try
        {
            progress?.Report(new ModrinthModpackInstallProgress("download-pack", 0, 1, mrpack.FileName));
            await _downloader.DownloadAsync(
                new[] { mrpack.DownloadUri }, temporaryPack, mrpack.Size, mrpack.Sha512, mrpack.Sha1,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            progress?.Report(new ModrinthModpackInstallProgress("download-pack", 1, 1, mrpack.FileName));
            return await InstallDownloadedAsync(
                version.ProjectId, version.VersionId, temporaryPack, staging, options, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                    parent,
                    temporaryPack,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    public async Task<ModrinthModpackInstallResult> InstallDownloadedAsync(
        string projectId,
        string apiVersionId,
        string mrpackPath,
        string stagingDirectory,
        ModrinthModpackInstallOptions? options = null,
        IProgress<ModrinthModpackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiVersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mrpackPath);
        options ??= new ModrinthModpackInstallOptions();
        ValidateDownloadConcurrency(options.MaxConcurrentDownloads);
        var staging = SafeModpackArchive.EnsureSafeStagingDirectory(stagingDirectory, requireEmpty: true);
        progress?.Report(new ModrinthModpackInstallProgress("inspect", 0, 1));
        var plan = await SafeModpackArchive.InspectAsync(
            mrpackPath, _downloader.UriPolicy, options.SafetyLimits, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ModrinthModpackInstallProgress("inspect", 1, 1));

        var selected = plan.Files.Where(file => !file.IsOptional || options.IncludeOptionalFiles).ToArray();
        var totalDownloadBytes = selected.Aggregate(
            0L,
            static (total, file) => checked(total + file.FileSize));
        var effectiveConcurrency = ModrinthDownloadConcurrencyPlanner.Plan(
            selected.Length,
            totalDownloadBytes,
            options.MaxConcurrentDownloads,
            options.UseAdaptiveConcurrency);
        var installed = new ConcurrentBag<string>();
        var completed = 0;
        progress?.Report(new ModrinthModpackInstallProgress(
            "download-files",
            0,
            selected.Length,
            EffectiveConcurrentDownloads: effectiveConcurrency,
            UsesAdaptiveConcurrency: options.UseAdaptiveConcurrency));
        using var batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? firstFailure = null;
        try
        {
            await Parallel.ForEachAsync(
                selected,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = effectiveConcurrency,
                    CancellationToken = batchCancellation.Token
                },
                async (file, token) =>
                {
                    try
                    {
                        var destination = SafeModpackArchive.PrepareDestination(staging, file.Path);
                        await _downloader.DownloadAsync(
                            file.Downloads, destination, file.FileSize, file.Sha512, file.Sha1,
                            cancellationToken: token).ConfigureAwait(false);
                        installed.Add(file.Path);
                        var current = Interlocked.Increment(ref completed);
                        progress?.Report(new ModrinthModpackInstallProgress(
                            "download-files",
                            current,
                            selected.Length,
                            file.Path,
                            effectiveConcurrency,
                            options.UseAdaptiveConcurrency));
                    }
                    catch (Exception exception)
                    {
                        // Stop scheduling work and cancel every in-flight request as soon as the
                        // first file fails. Each downloader removes its own atomic partial file.
                        Interlocked.CompareExchange(ref firstFailure, exception, null);
                        await batchCancellation.CancelAsync().ConfigureAwait(false);
                        throw;
                    }
                }).ConfigureAwait(false);
        }
        catch
        {
            await batchCancellation.CancelAsync().ConfigureAwait(false);
            CleanupOwnedStagingContents(staging);
            cancellationToken.ThrowIfCancellationRequested();
            if (firstFailure is not null)
            {
                ExceptionDispatchInfo.Capture(firstFailure).Throw();
            }

            throw;
        }

        progress?.Report(new ModrinthModpackInstallProgress("overrides", 0, plan.Overrides.Count));
        await SafeModpackArchive.ExtractOverridesAsync(
            mrpackPath, staging, plan.Overrides, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ModrinthModpackInstallProgress("overrides", plan.Overrides.Count, plan.Overrides.Count));
        progress?.Report(new ModrinthModpackInstallProgress("server-overrides", 0, plan.ServerOverrides.Count));
        await SafeModpackArchive.ExtractOverridesAsync(
            mrpackPath, staging, plan.ServerOverrides, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ModrinthModpackInstallProgress(
            "server-overrides", plan.ServerOverrides.Count, plan.ServerOverrides.Count));

        var installedPaths = installed
            .Concat(plan.Overrides.Select(static entry => entry.RelativePath))
            .Concat(plan.ServerOverrides.Select(static entry => entry.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ModrinthModpackInstallResult(
            projectId.Trim(), apiVersionId.Trim(), plan.Name, plan.VersionId, staging,
            plan.MinecraftVersion, plan.LoaderInstallRequest, selected.Length,
            plan.SkippedUnsupportedFiles, plan.OptionalFiles.Count - selected.Count(static file => file.IsOptional),
            plan.OptionalFiles, installedPaths);
    }

    private static void ValidateDownloadConcurrency(int value)
    {
        if (value is < 1 or > MaximumDownloadConcurrency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ModrinthModpackInstallOptions.MaxConcurrentDownloads),
                $"Modrinth 平行下載數必須介於 1 與 {MaximumDownloadConcurrency}。");
        }
    }

    private static void CleanupOwnedStagingContents(string staging)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(staging))
            {
                if (Directory.Exists(entry))
                {
                    SafePath.DeleteTreeWithoutFollowingReparsePoints(staging, entry);
                }
                else
                {
                    var safeEntry = SafePath.EnsureNoReparsePointsUnderRoot(staging, entry);
                    if (File.Exists(safeEntry))
                    {
                        File.Delete(safeEntry);
                    }
                }
            }
        }
        catch
        {
            // Preserve the first verification/download failure. The owning application workflow
            // also removes its entire manager-owned staging directory in its finally block.
        }
    }
}

internal static class ModrinthDownloadConcurrencyPlanner
{
    private const long Mebibyte = 1024L * 1024;

    /// <summary>
    /// Chooses one bounded batch width from verified manifest metadata. This deliberately plans
    /// between files instead of splitting a file into HTTP ranges, so every artifact keeps one
    /// atomic download/hash-verification owner.
    /// </summary>
    internal static int Plan(
        int fileCount,
        long totalBytes,
        int hardCap,
        bool useAdaptiveConcurrency = true)
    {
        if (fileCount < 0) throw new ArgumentOutOfRangeException(nameof(fileCount));
        if (totalBytes < 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));
        if (hardCap is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(hardCap));

        // ParallelOptions requires a positive value even when an empty manifest has no work.
        var availableFiles = Math.Max(1, fileCount);
        if (!useAdaptiveConcurrency)
        {
            return Math.Min(availableFiles, hardCap);
        }

        var countTier = fileCount switch
        {
            <= 4 => 1,
            <= 12 => 2,
            <= 32 => 4,
            <= 64 => 8,
            <= 128 => 12,
            _ => 16
        };
        var byteTier = totalBytes switch
        {
            <= 32 * Mebibyte => 1,
            <= 128 * Mebibyte => 2,
            <= 512 * Mebibyte => 4,
            <= 1024 * Mebibyte => 8,
            <= 2048 * Mebibyte => 12,
            _ => 16
        };
        var workloadTier = Math.Max(countTier, byteTier);
        return Math.Min(availableFiles, Math.Min(hardCap, workloadTier));
    }
}
