using System.IO.Compression;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Runtime;

public sealed record ModpackUpdateBackupPlan(
    BackupOptions Options,
    IReadOnlyList<string> IncludedRelativePaths,
    string Notice)
{
    /// <summary>The archive deliberately excludes the server core and cannot boot by itself.</summary>
    public bool IsCompleteServerBackup => false;
}

/// <summary>
/// Produces the strict data allowlist used before a modpack version update. Loader libraries,
/// bundled Java, launch scripts, logs, caches and existing backups are absent by construction.
/// </summary>
public sealed class ModpackUpdateBackupPlanner
{
    public const string DataOnlyBackupNotice =
        "這是更新前資料備份，只包含模組、設定、玩家與地圖資料；不包含 Server 核心，不能單獨啟動。";

    private static readonly string[] DataDirectoryNames =
    [
        "mods",
        "plugins",
        "config",
        "defaultconfigs",
        "kubejs",
        "scripts"
    ];

    private static readonly string[] DataFileNames =
    [
        "ops.json",
        "whitelist.json",
        "banned-ips.json",
        "banned-players.json",
        "usercache.json",
        "server.properties",
        "eula.txt",
        "user_jvm_args.txt"
    ];

    private static readonly HashSet<string> ReservedWorldTopLevelNames = new(
        DataDirectoryNames.Concat(
        [
            "libraries",
            "versions",
            "jre",
            "runtime",
            "logs",
            "cache",
            "backups",
            "crash-reports"
        ]),
        StringComparer.OrdinalIgnoreCase);

    private readonly MinecraftWorldLayoutResolver _worldLayoutResolver;

    public ModpackUpdateBackupPlanner(MinecraftWorldLayoutResolver? worldLayoutResolver = null)
        => _worldLayoutResolver = worldLayoutResolver ?? new MinecraftWorldLayoutResolver();

    public async Task<ModpackUpdateBackupPlan> CreatePlanAsync(
        ServerInstance instance,
        string targetVersionName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.DirectoryPath);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.DirectoryPath));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"找不到 Server 資料夾：{root}");
        }

        SafePath.EnsureNoReparsePointsUnderRoot(root, root);
        var worldLayout = await _worldLayoutResolver.ResolveAsync(root, cancellationToken)
            .ConfigureAwait(false);
        foreach (var worldPath in worldLayout.RelativeWorldDirectories)
        {
            var topLevelName = worldPath.Split('/', 2, StringSplitOptions.None)[0];
            if (ReservedWorldTopLevelNames.Contains(topLevelName))
            {
                throw new InvalidDataException(
                    $"世界路徑「{worldPath}」與模組包／核心保留目錄衝突，無法建立可靠的更新前資料備份。");
            }
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var included = new HashSet<string>(comparison);
        foreach (var relativePath in DataDirectoryNames
                     .Concat(DataFileNames)
                     .Concat(worldLayout.RelativeWorldDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddExistingRootConfinedPath(root, relativePath, included);
        }

        var sourceVersion = string.IsNullOrWhiteSpace(instance.ModpackVersionName)
            ? instance.MinecraftVersion ?? "unknown"
            : instance.ModpackVersionName;
        var archiveBaseName = SafePath.SanitizeFileName(
            $"{instance.Name}-pre-update-{sourceVersion}-to-{targetVersionName}",
            fallback: "modpack-pre-update",
            maxLength: 120);
        var destination = Path.Combine(root, "backups", "modpack-updates");
        var includedPaths = included
            .OrderBy(static path => path, comparison)
            .ToArray();
        var options = new BackupOptions
        {
            DestinationDirectory = destination,
            ArchiveFileName = $"{archiveBaseName}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip",
            CompressionLevel = CompressionLevel.Fastest,
            // The allowlist already excludes operational directories. Do not drop a nested world
            // or mod configuration directory merely because a pack happened to name it "cache".
            ExcludedDirectoryNames = [],
            ExcludedFileNames = [],
            ExcludedFileNamePrefixes = [],
            IncludedRelativePaths = includedPaths,
            FailOnReparsePoint = true
        };
        return new ModpackUpdateBackupPlan(options, includedPaths, DataOnlyBackupNotice);
    }

    private static void AddExistingRootConfinedPath(
        string root,
        string relativePath,
        ISet<string> included)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.None)
                .Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException($"更新前備份規劃器收到不安全的相對路徑：{relativePath}");
        }

        var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var candidate = SafePath.EnsureWithinRoot(root, normalizedRelative, allowRoot: false);
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
        {
            return;
        }

        SafePath.EnsureNoReparsePointsUnderRoot(root, candidate);
        included.Add(NormalizeRelativePath(Path.GetRelativePath(root, candidate)));
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/').Replace(Path.DirectorySeparatorChar, '/');
}
