using System.Diagnostics;
using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Providers;

internal interface ISpigotBuildToolsWorkspace
{
    Task PrepareAsync(
        SpigotBuildPlan plan,
        string operationDirectory,
        ManagedMinGitInstallation managedGit,
        IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
        CancellationToken cancellationToken = default);

    Task VerifyAsync(
        SpigotBuildPlan plan,
        string operationDirectory,
        ManagedMinGitInstallation managedGit,
        IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates the four official BuildTools repositories without an initial checkout, persists
/// core.autocrlf=input, and only then checks out the source revisions from the immutable version
/// identity. BuildTools generates CRLF source files on Windows before committing them; input
/// normalizes those generated files to the LF blobs expected by the official Spigot patches while
/// leaving checked-out source as LF. BuildTools may fetch/reset these repositories, so the same
/// local/global configuration and ref invariants are checked again before its output is trusted.
/// </summary>
internal sealed class SpigotBuildToolsManagedGitWorkspace(
    IModrinthLoaderBootstrapProcessRunner? processRunner = null)
    : ISpigotBuildToolsWorkspace
{
    private static readonly RepositoryDefinition[] Repositories =
    [
        new(
            "BuildData",
            new Uri("https://hub.spigotmc.org/stash/scm/spigot/builddata.git")),
        new(
            "Bukkit",
            new Uri("https://hub.spigotmc.org/stash/scm/spigot/bukkit.git")),
        new(
            "CraftBukkit",
            new Uri("https://hub.spigotmc.org/stash/scm/spigot/craftbukkit.git")),
        new(
            "Spigot",
            new Uri("https://hub.spigotmc.org/stash/scm/spigot/spigot.git"))
    ];

    private readonly IModrinthLoaderBootstrapProcessRunner _processRunner = processRunner
        ?? new ModrinthLoaderBootstrapProcessRunner();

    public async Task PrepareAsync(
        SpigotBuildPlan plan,
        string operationDirectory,
        ManagedMinGitInstallation managedGit,
        IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(managedGit);
        var operation = RequireOperationDirectory(operationDirectory);
        _ = RequireManagedGitExecutable(managedGit);
        _ = SpigotBuildToolsGitEnvironment.EnsurePrivateGlobalConfig(
            operation,
            createIfMissing: true);

        foreach (var repository in Repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedRef = RequireExpectedRef(plan, repository.Name);
            var repositoryPath = SafePath.CombineUnderRoot(operation, repository.Name);
            if (File.Exists(repositoryPath) || Directory.Exists(repositoryPath))
            {
                throw WorkspaceFailure(
                    plan,
                    repository,
                    $"預先 clone 前目標已存在：{repositoryPath}");
            }

            output?.Report(new ModrinthLoaderBootstrapOutputLine(
                false,
                $"正在準備 {repository.Name} 官方 source（core.autocrlf=input）…"));
            await RunGitAsync(
                    plan,
                    repository,
                    operation,
                    managedGit,
                    output,
                    cancellationToken,
                    "clone",
                    "--no-checkout",
                    "--no-progress",
                    "--config",
                    "core.autocrlf=input",
                    "--origin",
                    "origin",
                    "--",
                    repository.Remote.AbsoluteUri,
                    repositoryPath)
                .ConfigureAwait(false);

            RequireRepositoryDirectory(operation, repositoryPath, plan, repository);

            // Clone --config writes this before clone could perform its initial checkout. Keep an
            // explicit canonical local value too, then prove it before our first checkout.
            await RunGitAsync(
                    plan,
                    repository,
                    operation,
                    managedGit,
                    output: null,
                    cancellationToken,
                    "-C",
                    repositoryPath,
                    "config",
                    "--local",
                    "--replace-all",
                    "core.autocrlf",
                    "input")
                .ConfigureAwait(false);
            await VerifyAutoCrlfAsync(
                    plan,
                    repository,
                    operation,
                    repositoryPath,
                    managedGit,
                    cancellationToken)
                .ConfigureAwait(false);

            await RunGitAsync(
                    plan,
                    repository,
                    operation,
                    managedGit,
                    output,
                    cancellationToken,
                    "-C",
                    repositoryPath,
                    "checkout",
                    "--detach",
                    "--force",
                    expectedRef)
                .ConfigureAwait(false);

            await VerifyRefAndRemoteAsync(
                    plan,
                    repository,
                    operation,
                    repositoryPath,
                    managedGit,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task VerifyAsync(
        SpigotBuildPlan plan,
        string operationDirectory,
        ManagedMinGitInstallation managedGit,
        IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(managedGit);
        var operation = RequireOperationDirectory(operationDirectory);
        _ = RequireManagedGitExecutable(managedGit);
        _ = SpigotBuildToolsGitEnvironment.EnsurePrivateGlobalConfig(
            operation,
            createIfMissing: false);

        output?.Report(new ModrinthLoaderBootstrapOutputLine(
            false,
            $"正在驗證 BuildTools source refs（identity {plan.VersionIdentity}）…"));
        foreach (var repository in Repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repositoryPath = SafePath.CombineUnderRoot(operation, repository.Name);
            RequireRepositoryDirectory(operation, repositoryPath, plan, repository);
            await VerifyAutoCrlfAsync(
                    plan,
                    repository,
                    operation,
                    repositoryPath,
                    managedGit,
                    cancellationToken)
                .ConfigureAwait(false);
            await VerifyRefAndRemoteAsync(
                    plan,
                    repository,
                    operation,
                    repositoryPath,
                    managedGit,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task VerifyAutoCrlfAsync(
        SpigotBuildPlan plan,
        RepositoryDefinition repository,
        string operation,
        string repositoryPath,
        ManagedMinGitInstallation managedGit,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
                plan,
                repository,
                operation,
                managedGit,
                output: null,
                cancellationToken,
                "-C",
                repositoryPath,
                "config",
                "--local",
                "--get-all",
                "core.autocrlf")
            .ConfigureAwait(false);
        var values = RequireExactOutput(result, plan, repository, "local core.autocrlf");
        if (values.Count != 1 || !values[0].Equals("input", StringComparison.Ordinal))
        {
            throw WorkspaceFailure(
                plan,
                repository,
                "local core.autocrlf 驗證失敗；expected=input；actual="
                + (values.Count == 0 ? "<missing>" : string.Join(',', values)));
        }

        var globalResult = await RunGitAsync(
                plan,
                repository,
                operation,
                managedGit,
                output: null,
                cancellationToken,
                "config",
                "--global",
                "--get-all",
                "core.autocrlf")
            .ConfigureAwait(false);
        var globalValues = RequireExactOutput(
            globalResult,
            plan,
            repository,
            "global core.autocrlf");
        if (globalValues.Count != 1
            || !globalValues[0].Equals("input", StringComparison.Ordinal))
        {
            throw WorkspaceFailure(
                plan,
                repository,
                "global core.autocrlf 驗證失敗；expected=input；actual="
                + (globalValues.Count == 0
                    ? "<missing>"
                    : string.Join(',', globalValues)));
        }
    }

    private async Task VerifyRefAndRemoteAsync(
        SpigotBuildPlan plan,
        RepositoryDefinition repository,
        string operation,
        string repositoryPath,
        ManagedMinGitInstallation managedGit,
        CancellationToken cancellationToken)
    {
        var expectedRef = RequireExpectedRef(plan, repository.Name);
        var refResult = await RunGitAsync(
                plan,
                repository,
                operation,
                managedGit,
                output: null,
                cancellationToken,
                "-C",
                repositoryPath,
                "rev-parse",
                "--verify",
                "HEAD^{commit}")
            .ConfigureAwait(false);
        var refLines = RequireExactOutput(refResult, plan, repository, "HEAD ref");
        var actualRef = refLines.Count == 1 ? refLines[0] : "<invalid-output>";
        if (refLines.Count != 1
            || !actualRef.Equals(expectedRef, StringComparison.OrdinalIgnoreCase))
        {
            throw WorkspaceFailure(
                plan,
                repository,
                $"HEAD ref 驗證失敗；expected={expectedRef}；actual={actualRef}");
        }

        var remoteResult = await RunGitAsync(
                plan,
                repository,
                operation,
                managedGit,
                output: null,
                cancellationToken,
                "-C",
                repositoryPath,
                "remote",
                "get-url",
                "--all",
                "origin")
            .ConfigureAwait(false);
        var remoteLines = RequireExactOutput(remoteResult, plan, repository, "origin URL");
        var actualRemote = remoteLines.Count == 1 ? remoteLines[0] : "<invalid-output>";
        if (remoteLines.Count != 1
            || !actualRemote.Equals(repository.Remote.AbsoluteUri, StringComparison.Ordinal))
        {
            throw WorkspaceFailure(
                plan,
                repository,
                $"origin URL 驗證失敗；expected={repository.Remote.AbsoluteUri}；"
                + $"actual={actualRemote}");
        }
    }

    private async Task<ModrinthLoaderBootstrapProcessResult> RunGitAsync(
        SpigotBuildPlan plan,
        RepositoryDefinition repository,
        string operation,
        ManagedMinGitInstallation managedGit,
        IProgress<ModrinthLoaderBootstrapOutputLine>? output,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = BuildGitStartInfo(operation, managedGit, arguments);
        try
        {
            return await _processRunner.RunAsync(startInfo, output, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw WorkspaceFailure(
                plan,
                repository,
                $"受管理 MinGit 命令失敗：git {string.Join(' ', arguments)}",
                exception);
        }
    }

    private static ProcessStartInfo BuildGitStartInfo(
        string operation,
        ManagedMinGitInstallation managedGit,
        IReadOnlyList<string> arguments)
    {
        var git = RequireManagedGitExecutable(managedGit);
        var startInfo = new ProcessStartInfo
        {
            FileName = git,
            WorkingDirectory = operation,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var systemDirectory = Environment.SystemDirectory;
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(systemDirectory)
            || string.IsNullOrWhiteSpace(windowsDirectory))
        {
            throw new PlatformNotSupportedException(
                "BuildTools managed Git workspace 只能在 Windows 執行。");
        }

        var privateHome = SpigotBuildToolsGitEnvironment.GetPrivateHome(operation);
        var privateTemp = SafePath.CombineUnderRoot(operation, "git-temp");
        Directory.CreateDirectory(privateHome);
        Directory.CreateDirectory(privateTemp);
        SafePath.EnsureNoReparsePointsUnderRoot(operation, privateHome);
        SafePath.EnsureNoReparsePointsUnderRoot(operation, privateTemp);

        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            new[]
            {
                managedGit.CommandDirectory,
                managedGit.MingwBinDirectory,
                managedGit.UsrBinDirectory,
                systemDirectory
            }.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase));
        startInfo.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        startInfo.Environment["SHELL"] = Path.GetFullPath(managedGit.ShellExecutablePath);
        startInfo.Environment["COMSPEC"] = Path.Combine(systemDirectory, "cmd.exe");
        startInfo.Environment["SystemRoot"] = windowsDirectory;
        startInfo.Environment["WINDIR"] = windowsDirectory;
        startInfo.Environment["HOME"] = privateHome;
        startInfo.Environment["USERPROFILE"] = privateHome;
        startInfo.Environment["TEMP"] = privateTemp;
        startInfo.Environment["TMP"] = privateTemp;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] =
            SpigotBuildToolsGitEnvironment.EnsurePrivateGlobalConfig(
                operation,
                createIfMissing: false);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";
        return startInfo;
    }

    private static string RequireOperationDirectory(string operationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDirectory);
        var operation = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(operationDirectory));
        if (!Directory.Exists(operation))
        {
            throw new DirectoryNotFoundException(
                $"BuildTools operation workspace 不存在：{operation}");
        }

        var attributes = File.GetAttributes(operation);
        if (!attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                "BuildTools operation workspace 必須是非連結的一般資料夾。");
        }

        return operation;
    }

    private static string RequireManagedGitExecutable(ManagedMinGitInstallation managedGit)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(managedGit.InstallDirectory));
        var executable = SafePath.EnsureNoReparsePointsUnderRoot(
            root,
            managedGit.CommandGitExecutablePath);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("受管理 MinGit git.exe 不存在。", executable);
        }

        var attributes = File.GetAttributes(executable);
        if (attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("受管理 MinGit git.exe 必須是非連結的一般檔案。");
        }

        return executable;
    }

    private static void RequireRepositoryDirectory(
        string operation,
        string repositoryPath,
        SpigotBuildPlan plan,
        RepositoryDefinition repository)
    {
        if (!Directory.Exists(repositoryPath))
        {
            throw WorkspaceFailure(plan, repository, "repository 目錄不存在。");
        }

        SafePath.EnsureNoReparsePointsUnderRoot(operation, repositoryPath);
        var repositoryAttributes = File.GetAttributes(repositoryPath);
        if (!repositoryAttributes.HasFlag(FileAttributes.Directory)
            || repositoryAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw WorkspaceFailure(plan, repository, "repository 目錄不是一般資料夾。");
        }

        var gitDirectory = SafePath.CombineUnderRoot(repositoryPath, ".git");
        if (!Directory.Exists(gitDirectory))
        {
            throw WorkspaceFailure(plan, repository, ".git 目錄不存在或不是資料夾。");
        }

        SafePath.EnsureNoReparsePointsUnderRoot(operation, gitDirectory);
        var gitAttributes = File.GetAttributes(gitDirectory);
        if (!gitAttributes.HasFlag(FileAttributes.Directory)
            || gitAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw WorkspaceFailure(plan, repository, ".git 必須是非連結的一般資料夾。");
        }
    }

    private static string RequireExpectedRef(SpigotBuildPlan plan, string name)
    {
        if (!plan.SourceRefs.TryGetValue(name, out var value)
            || value.Length != 40
            || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                $"BuildTools plan 缺少有效的 {name} source ref（identity "
                + $"{plan.VersionIdentity}）。");
        }

        return value.ToLowerInvariant();
    }

    private static IReadOnlyList<string> RequireExactOutput(
        ModrinthLoaderBootstrapProcessResult result,
        SpigotBuildPlan plan,
        RepositoryDefinition repository,
        string context)
    {
        if (result.OutputTruncated || result.StandardError.Count != 0)
        {
            throw WorkspaceFailure(
                plan,
                repository,
                $"{context} 查詢輸出不完整或含 stderr。");
        }

        return result.StandardOutput;
    }

    private static InvalidDataException WorkspaceFailure(
        SpigotBuildPlan plan,
        RepositoryDefinition repository,
        string detail,
        Exception? innerException = null)
    {
        var message = $"BuildTools workspace 驗證失敗（identity {plan.VersionIdentity}，"
            + $"repository {repository.Name}）：{detail}";
        return innerException is null
            ? new InvalidDataException(message)
            : new InvalidDataException(message, innerException);
    }

    private sealed record RepositoryDefinition(string Name, Uri Remote);
}

/// <summary>
/// Owns the only Git global configuration visible to BuildTools and every managed child Git.
/// This file deliberately lives under the fresh operation directory and is validated byte-for-byte
/// whenever it is reused, so user/system Git settings cannot alter checkout or clean behavior.
/// </summary>
internal static class SpigotBuildToolsGitEnvironment
{
    private static readonly byte[] PrivateGlobalConfigBytes =
        Encoding.ASCII.GetBytes("[core]\n\tautocrlf = input\n");

    public static string GetPrivateHome(string operationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDirectory);
        return SafePath.CombineUnderRoot(
            Path.GetFullPath(operationDirectory),
            "home");
    }

    public static string EnsurePrivateGlobalConfig(
        string operationDirectory,
        bool createIfMissing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDirectory);
        var operation = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(operationDirectory));
        if (!Directory.Exists(operation)
            || File.GetAttributes(operation).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                "BuildTools operation workspace 必須是非連結的一般資料夾。");
        }

        var privateHome = GetPrivateHome(operation);
        if (!Directory.Exists(privateHome))
        {
            if (!createIfMissing)
            {
                throw new InvalidDataException("BuildTools private Git home 不存在。");
            }

            Directory.CreateDirectory(privateHome);
        }

        SafePath.EnsureNoReparsePointsUnderRoot(operation, privateHome);
        var homeAttributes = File.GetAttributes(privateHome);
        if (!homeAttributes.HasFlag(FileAttributes.Directory)
            || homeAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                "BuildTools private Git home 必須是非連結的一般資料夾。");
        }

        var configPath = SafePath.CombineUnderRoot(privateHome, ".gitconfig");
        if (!File.Exists(configPath))
        {
            if (!createIfMissing)
            {
                throw new InvalidDataException("BuildTools private Git global config 不存在。");
            }

            var partial = SafePath.CombineUnderRoot(
                privateHome,
                $".gitconfig.{Guid.NewGuid():N}.partial");
            try
            {
                using (var stream = new FileStream(
                    partial,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(PrivateGlobalConfigBytes);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(partial, configPath, overwrite: false);
            }
            finally
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }
            }
        }

        SafePath.EnsureNoReparsePointsUnderRoot(operation, configPath);
        var attributes = File.GetAttributes(configPath);
        if (attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                "BuildTools private Git global config 必須是非連結的一般檔案。");
        }

        var actual = File.ReadAllBytes(configPath);
        if (!actual.AsSpan().SequenceEqual(PrivateGlobalConfigBytes))
        {
            throw new InvalidDataException(
                "BuildTools private Git global config 已遭變更；expected core.autocrlf=input。");
        }

        return configPath;
    }
}
