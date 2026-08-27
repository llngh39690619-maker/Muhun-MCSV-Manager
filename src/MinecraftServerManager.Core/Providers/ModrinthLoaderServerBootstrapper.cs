using System.Diagnostics;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Providers;

/// <summary>
/// Bootstraps a first-party Minecraft server loader beside an already extracted Modrinth pack.
/// Loader installers always run in an isolated, newly-created sibling directory. Only a fully
/// validated, conflict-free output tree is moved into the caller's staging directory.
/// </summary>
public sealed class ModrinthLoaderServerBootstrapper
{
    private const int MaximumOutputEntries = 200_000;
    private const long MaximumOutputBytes = 16L * 1024 * 1024 * 1024;

    private readonly IModrinthOfficialLoaderArtifactProvider _artifacts;
    private readonly IModrinthLoaderBootstrapProcessRunner _processRunner;
    private readonly ModrinthLoaderBootstrapCommandBuilder _commandBuilder;
    private readonly IModrinthLoaderOperationCleanup _operationCleanup;

    public ModrinthLoaderServerBootstrapper(
        IModrinthOfficialLoaderArtifactProvider artifacts,
        IModrinthLoaderBootstrapProcessRunner processRunner,
        ModrinthLoaderBootstrapCommandBuilder? commandBuilder = null)
        : this(
            artifacts,
            processRunner,
            commandBuilder,
            new ModrinthLoaderOperationCleanup())
    {
    }

    internal ModrinthLoaderServerBootstrapper(
        IModrinthOfficialLoaderArtifactProvider artifacts,
        IModrinthLoaderBootstrapProcessRunner processRunner,
        ModrinthLoaderBootstrapCommandBuilder? commandBuilder,
        IModrinthLoaderOperationCleanup operationCleanup)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _commandBuilder = commandBuilder ?? new ModrinthLoaderBootstrapCommandBuilder();
        _operationCleanup = operationCleanup ?? throw new ArgumentNullException(nameof(operationCleanup));
    }

    public async Task<ModrinthLoaderBootstrapResult> BootstrapAsync(
        ModrinthModpackLoaderInstallRequest request,
        string stagingDirectory,
        string javaExecutablePath,
        IProgress<ModrinthLoaderBootstrapProgress>? progress = null,
        IProgress<ModrinthLoaderBootstrapOutputLine>? processOutput = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        if (request.Kind == ModrinthModpackLoaderKind.Quilt)
        {
            throw new ModrinthLoaderUnsupportedException(
                request.Kind,
                "官方 Quilt Installer CLI 目前會吞掉下載/安裝例外並可能回傳成功結束碼，"
                + "且其 server libraries 流程未提供可由本程式強制驗證的 hash。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        var staging = SafeModpackArchive.EnsureSafeStagingDirectory(
            stagingDirectory,
            requireEmpty: false);
        RejectRootDirectory(staging);
        var java = ResolveJavaExecutable(javaExecutablePath);
        var parent = Path.GetDirectoryName(staging)
            ?? throw new InvalidOperationException("Staging 資料夾沒有父目錄。");
        RejectReparse(new DirectoryInfo(parent), "Staging 父目錄");

        var operationRoot = Path.Combine(parent, ".muhun-loader-" + Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(operationRoot, "output");
        var toolsDirectory = Path.Combine(operationRoot, "tools");
        var environmentDirectory = Path.Combine(operationRoot, "environment");
        var privateHomeDirectory = Path.Combine(environmentDirectory, "home");
        var privateTempDirectory = Path.Combine(environmentDirectory, "temp");
        SafePathObjectIdentity? operationIdentity = null;
        Exception? operationFailure = null;
        try
        {
            // The operation becomes manager-owned as soon as the first CreateDirectory call can
            // create any part of it. Keep every creation and validation inside this protected
            // region so even a pre-run failure reaches the same mandatory cleanup path.
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(toolsDirectory);
            Directory.CreateDirectory(privateHomeDirectory);
            Directory.CreateDirectory(privateTempDirectory);
            RejectReparse(new DirectoryInfo(operationRoot), "Loader 暫存資料夾");
            RejectReparse(new DirectoryInfo(outputDirectory), "Loader output 資料夾");
            RejectReparse(new DirectoryInfo(toolsDirectory), "Loader tools 資料夾");
            RejectReparse(new DirectoryInfo(environmentDirectory), "Loader environment 資料夾");
            RejectReparse(new DirectoryInfo(privateHomeDirectory), "Loader private HOME");
            RejectReparse(new DirectoryInfo(privateTempDirectory), "Loader private TEMP");
            if (OperatingSystem.IsWindows())
            {
                operationIdentity = SafePath.GetExistingObjectIdentity(operationRoot);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ModrinthLoaderBootstrapProcessResult? processResult = null;
            OfficialLoaderInstallProvenance? provenance = null;
            string? installerPath = null;

            switch (request.Kind)
            {
                case ModrinthModpackLoaderKind.Vanilla:
                {
                    progress?.Report(new ModrinthLoaderBootstrapProgress("download-vanilla", 0d));
                    await _artifacts.DownloadVanillaServerAsync(
                            request.MinecraftVersion,
                            Path.Combine(outputDirectory, "server.jar"),
                            new PhaseProgress(progress, "download-vanilla"),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }

                case ModrinthModpackLoaderKind.Fabric:
                {
                    installerPath = Path.Combine(toolsDirectory, "fabric-installer.jar");
                    progress?.Report(new ModrinthLoaderBootstrapProgress("download-fabric-installer", 0d));
                    await _artifacts.DownloadLatestStableFabricInstallerAsync(
                            installerPath,
                            new PhaseProgress(progress, "download-fabric-installer"),
                            cancellationToken)
                        .ConfigureAwait(false);
                    processResult = await RunInstallerAsync(
                            request,
                            java,
                            installerPath,
                            outputDirectory,
                            privateHomeDirectory,
                            privateTempDirectory,
                            progress,
                            processOutput,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await ValidateRegularNonEmptyFileAsync(
                            Path.Combine(outputDirectory, "fabric-server-launch.jar"),
                            "Fabric server launcher",
                            cancellationToken)
                        .ConfigureAwait(false);
                    await _artifacts.VerifyVanillaServerAsync(
                            request.MinecraftVersion,
                            Path.Combine(outputDirectory, "server.jar"),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }

                case ModrinthModpackLoaderKind.Forge:
                {
                    installerPath = Path.Combine(toolsDirectory, "forge-installer.jar");
                    progress?.Report(new ModrinthLoaderBootstrapProgress("download-forge-installer", 0d));
                    await _artifacts.DownloadForgeInstallerAsync(
                            request.MinecraftVersion,
                            request.LoaderVersion!,
                            installerPath,
                            new PhaseProgress(progress, "download-forge-installer"),
                            cancellationToken)
                        .ConfigureAwait(false);
                    processResult = await RunInstallerAsync(
                            request,
                            java,
                            installerPath,
                            outputDirectory,
                            privateHomeDirectory,
                            privateTempDirectory,
                            progress,
                            processOutput,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }

                case ModrinthModpackLoaderKind.NeoForge:
                {
                    installerPath = Path.Combine(toolsDirectory, "neoforge-installer.jar");
                    progress?.Report(new ModrinthLoaderBootstrapProgress("download-neoforge-installer", 0d));
                    await _artifacts.DownloadNeoForgeInstallerAsync(
                            request.LoaderVersion!,
                            installerPath,
                            new PhaseProgress(progress, "download-neoforge-installer"),
                            cancellationToken)
                        .ConfigureAwait(false);
                    processResult = await RunInstallerAsync(
                            request,
                            java,
                            installerPath,
                            outputDirectory,
                            privateHomeDirectory,
                            privateTempDirectory,
                            progress,
                            processOutput,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "未知 Loader kind。");
            }

            if (installerPath is not null)
            {
                TryDeleteFile(Path.Combine(outputDirectory, Path.GetFileName(installerPath) + ".log"));
            }

            progress?.Report(new ModrinthLoaderBootstrapProgress("validate-output", 0d));
            var installedPaths = InspectOutputTree(outputDirectory, cancellationToken);
            var launchCandidates = GetLaunchCandidates(request.Kind, installedPaths);
            ValidateRunnableOutput(
                request.Kind,
                outputDirectory,
                installedPaths,
                launchCandidates);
            if (installerPath is not null)
            {
                provenance = await OfficialLoaderInstallerOutputValidator.ValidateAndCreateAsync(
                        request,
                        outputDirectory,
                        installerPath,
                        installedPaths,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            progress?.Report(new ModrinthLoaderBootstrapProgress("validate-output", 1d));

            progress?.Report(new ModrinthLoaderBootstrapProgress("merge-output", 0d));
            MergeOutputIntoStaging(outputDirectory, staging, cancellationToken);
            progress?.Report(new ModrinthLoaderBootstrapProgress("merge-output", 1d));
            return new ModrinthLoaderBootstrapResult(
                request.Kind,
                request.MinecraftVersion,
                request.LoaderVersion,
                staging,
                installedPaths,
                launchCandidates,
                processResult,
                provenance);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                // Cleanup deliberately ignores the already-cancelled operation token. The bounded
                // retry absorbs short-lived Java/antivirus handles, while no-follow deletion and
                // the captured Windows identity prevent a replaced operation root from redirecting
                // cleanup into another tree.
                await _operationCleanup.DeleteAsync(
                        parent,
                        operationRoot,
                        operationIdentity,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                processOutput?.Report(new ModrinthLoaderBootstrapOutputLine(
                    true,
                    "Loader operation 暫存資料夾在有界重試後仍無法完整清除；本次作業不會回報成功。"));
                throw CreateCleanupFailure(operationFailure, cleanupFailure);
            }
        }
    }

    private async Task<ModrinthLoaderBootstrapProcessResult> RunInstallerAsync(
        ModrinthModpackLoaderInstallRequest request,
        string javaExecutablePath,
        string installerPath,
        string outputDirectory,
        string privateHomeDirectory,
        string privateTempDirectory,
        IProgress<ModrinthLoaderBootstrapProgress>? progress,
        IProgress<ModrinthLoaderBootstrapOutputLine>? processOutput,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ModrinthLoaderBootstrapProgress("run-installer", null, request.Kind.ToString()));
        var startInfo = _commandBuilder.Build(
            request,
            javaExecutablePath,
            installerPath,
            outputDirectory,
            privateHomeDirectory,
            privateTempDirectory);
        var result = await _processRunner.RunAsync(startInfo, processOutput, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new ModrinthLoaderBootstrapProgress("run-installer", 1d, request.Kind.ToString()));
        return result;
    }

    private static void ValidateRequest(ModrinthModpackLoaderInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ModrinthOfficialLoaderArtifactProvider.ValidateVersionArgument(
            request.MinecraftVersion,
            nameof(request.MinecraftVersion));
        if (!Enum.IsDefined(request.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "未知 Loader kind。");
        }

        if (request.Kind != ModrinthModpackLoaderKind.Vanilla)
        {
            if (string.IsNullOrWhiteSpace(request.LoaderVersion))
            {
                throw new ArgumentException($"{request.Kind} 缺少 LoaderVersion。", nameof(request));
            }

            ModrinthOfficialLoaderArtifactProvider.ValidateVersionArgument(
                request.LoaderVersion,
                nameof(request.LoaderVersion));
        }
        else if (!string.IsNullOrWhiteSpace(request.LoaderVersion))
        {
            throw new ArgumentException("Vanilla 不得指定 LoaderVersion。", nameof(request));
        }
    }

    private static string ResolveJavaExecutable(string javaExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(javaExecutablePath);
        var fullPath = Path.GetFullPath(javaExecutablePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("找不到 Java executable。", fullPath);
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            var target = info.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new InvalidDataException("Java executable 的 symbolic link 無法解析。");
            info = new FileInfo(target.FullName);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Java executable symbolic link 未指向一般檔案。");
            }
        }

        if (info.Length < 1)
        {
            throw new InvalidDataException("Java executable 是空檔案。");
        }

        return info.FullName;
    }

    private static async Task ValidateRegularNonEmptyFileAsync(
        string path,
        string context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1)
        {
            throw new InvalidDataException($"{context} 未被 installer 正確建立。");
        }

        RejectReparse(info, context);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static IReadOnlyList<string> InspectOutputTree(
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(outputDirectory);
        var entries = 0;
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries++;
                if (entries > MaximumOutputEntries)
                {
                    throw new InvalidDataException("ModLoader 安裝結果檔案數量超過安全上限。");
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"ModLoader 安裝結果含有 reparse point：{path}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                    continue;
                }

                var length = new FileInfo(path).Length;
                totalBytes = checked(totalBytes + length);
                if (totalBytes > MaximumOutputBytes)
                {
                    throw new InvalidDataException("ModLoader 安裝結果超過安全大小上限。");
                }

                files.Add(ToRelativePath(outputDirectory, path));
            }
        }

        if (files.Count == 0)
        {
            throw new InvalidDataException("ModLoader installer 沒有建立任何檔案。");
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static IReadOnlyList<string> GetLaunchCandidates(
        ModrinthModpackLoaderKind kind,
        IReadOnlyList<string> installedPaths)
    {
        var candidates = new List<string>();
        foreach (var path in installedPaths)
        {
            var isRootFile = !path.Contains('/', StringComparison.Ordinal);
            if (kind == ModrinthModpackLoaderKind.Vanilla
                && path.Equals("server.jar", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(path);
            }
            else if (kind == ModrinthModpackLoaderKind.Fabric
                && path.Equals("fabric-server-launch.jar", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(path);
            }
            else if (kind is ModrinthModpackLoaderKind.Forge or ModrinthModpackLoaderKind.NeoForge)
            {
                if (path.Equals("run.bat", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("run.sh", StringComparison.OrdinalIgnoreCase)
                    || (isRootFile
                        && path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                        && path.Contains(
                            kind == ModrinthModpackLoaderKind.Forge ? "forge" : "neoforge",
                            StringComparison.OrdinalIgnoreCase)
                        && !path.Contains("installer", StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(path);
                }
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ValidateRunnableOutput(
        ModrinthModpackLoaderKind kind,
        string outputDirectory,
        IReadOnlyList<string> installedPaths,
        IReadOnlyList<string> launchCandidates)
    {
        if (launchCandidates.Count == 0)
        {
            throw new InvalidDataException($"{kind} installer 結束後找不到可啟動檔案。");
        }

        foreach (var relativePath in launchCandidates)
        {
            var launchFile = new FileInfo(Path.Combine(
                outputDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!launchFile.Exists || launchFile.Length < 1)
            {
                throw new InvalidDataException($"{kind} 可啟動檔案是空檔或已消失：{relativePath}");
            }

            RejectReparse(launchFile, $"{kind} 可啟動檔案");
        }

        if (kind == ModrinthModpackLoaderKind.Fabric
            && !installedPaths.Contains("server.jar", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Fabric installer 未建立已驗證的 Minecraft server.jar。");
        }
    }

    private static void MergeOutputIntoStaging(
        string outputDirectory,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var entries = Directory.EnumerateFileSystemEntries(outputDirectory)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var moves = new List<(string Source, string Destination, bool IsDirectory)>();
        foreach (var source in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(stagingDirectory, Path.GetFileName(source));
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new IOException(
                    $"ModLoader 安裝結果與模組包內容衝突，不會覆寫：{destination}");
            }

            moves.Add((source, destination, Directory.Exists(source)));
        }

        var moved = new List<(string Source, string Destination, bool IsDirectory)>();
        try
        {
            foreach (var move in moves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (move.IsDirectory)
                {
                    Directory.Move(move.Source, move.Destination);
                }
                else
                {
                    File.Move(move.Source, move.Destination, overwrite: false);
                }

                moved.Add(move);
            }
        }
        catch (Exception exception)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var move in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (move.IsDirectory && Directory.Exists(move.Destination))
                    {
                        Directory.Move(move.Destination, move.Source);
                    }
                    else if (!move.IsDirectory && File.Exists(move.Destination))
                    {
                        File.Move(move.Destination, move.Source, overwrite: false);
                    }
                }
                catch (Exception rollbackException)
                {
                    rollbackFailures.Add(rollbackException);
                }
            }

            if (rollbackFailures.Count > 0)
            {
                throw new AggregateException(
                    "ModLoader 輸出合併失敗，且回復 staging 時也發生錯誤。",
                    new[] { exception }.Concat(rollbackFailures));
            }

            throw;
        }
    }

    private static string ToRelativePath(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static void RejectRootDirectory(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(trimmed) ?? string.Empty);
        if (trimmed.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Staging 資料夾不得是磁碟根目錄。");
        }
    }

    private static void RejectReparse(FileSystemInfo info, string context)
    {
        info.Refresh();
        if (!info.Exists)
        {
            throw new DirectoryNotFoundException($"{context} 不存在：{info.FullName}");
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{context} 不得是 reparse point。");
        }
    }

    private static void TryDeleteFile(string path)
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
            // Tool logs are not part of the installed server; operation cleanup retries below.
        }
    }

    private static Exception CreateCleanupFailure(
        Exception? operationFailure,
        Exception cleanupFailure)
    {
        if (operationFailure is null)
        {
            return new IOException(
                "Loader 輸出已完成驗證，但 operation 暫存資料夾無法完整清除；不回報安裝成功。",
                cleanupFailure);
        }

        var combined = new AggregateException(operationFailure, cleanupFailure);
        return operationFailure is OperationCanceledException canceled
            ? new OperationCanceledException(
                "Loader 安裝已取消，但未完成的 operation 暫存資料夾無法完整清除。",
                combined,
                canceled.CancellationToken)
            : new IOException(
                "Loader 安裝失敗，且未完成的 operation 暫存資料夾無法完整清除。",
                combined);
    }

    private sealed class PhaseProgress(
        IProgress<ModrinthLoaderBootstrapProgress>? progress,
        string phase) : IProgress<double>
    {
        public void Report(double value)
            => progress?.Report(new ModrinthLoaderBootstrapProgress(phase, value));
    }
}

internal interface IModrinthLoaderOperationCleanup
{
    Task DeleteAsync(
        string trustedParent,
        string operationRoot,
        SafePathObjectIdentity? expectedOperationIdentity,
        CancellationToken cancellationToken);
}

internal sealed class ModrinthLoaderOperationCleanup : IModrinthLoaderOperationCleanup
{
    public Task DeleteAsync(
        string trustedParent,
        string operationRoot,
        SafePathObjectIdentity? expectedOperationIdentity,
        CancellationToken cancellationToken)
        => expectedOperationIdentity is { } identity
            ? SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                trustedParent,
                operationRoot,
                identity,
                cancellationToken: cancellationToken)
            : SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                trustedParent,
                operationRoot,
                cancellationToken);
}
