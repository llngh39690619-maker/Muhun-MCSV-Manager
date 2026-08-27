using System.Diagnostics;
using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Providers;

/// <summary>Constructs the only permitted BuildTools command; no caller-provided flags or shell.</summary>
public sealed class SpigotBuildToolsCommandBuilder
{
    public ProcessStartInfo Build(
        SpigotBuildPlan plan,
        string javaExecutablePath,
        string buildToolsJarPath,
        string freshWorkingDirectory,
        string outputDirectory,
        ManagedMinGitInstallation managedGit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(javaExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildToolsJarPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(freshWorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(managedGit);
        if (plan.CoreType is not (CoreType.Spigot or CoreType.CraftBukkit))
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "BuildTools plan 核心種類無效。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(javaExecutablePath),
            WorkingDirectory = Path.GetFullPath(freshWorkingDirectory),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var privateHome = ConfigureTrustedBuildEnvironment(
            startInfo,
            managedGit,
            plan.JavaMajorVersion);
        Add(startInfo, $"-Duser.home={privateHome}");
        Add(startInfo, "-jar", Path.GetFullPath(buildToolsJarPath));
        // Modern plans use an immutable official identity. Some legacy aliases have no identity
        // endpoint; those use the freshly resolved Minecraft alias and are protected by the
        // pre-pinned plus post-run four-ref equality checks.
        Add(startInfo, "--rev", plan.BuildRevision ?? plan.VersionIdentity);
        Add(startInfo, "--output-dir", Path.GetFullPath(outputDirectory));
        Add(startInfo, "--final-name", plan.OutputFileName);
        if (plan.CoreType == CoreType.CraftBukkit)
        {
            Add(startInfo, "--compile", "craftbukkit");
        }

        return startInfo;
    }

    private static string ConfigureTrustedBuildEnvironment(
        ProcessStartInfo startInfo,
        ManagedMinGitInstallation managedGit,
        int javaMajorVersion)
    {
        var javaBin = Path.GetDirectoryName(startInfo.FileName)
            ?? throw new InvalidDataException("Java executable 缺少 bin 目錄。");
        var javaHome = Directory.GetParent(javaBin)?.FullName
            ?? throw new InvalidDataException("Java executable 缺少 JDK 根目錄。");
        var managedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(managedGit.InstallDirectory));
        foreach (var executable in new[]
                  {
                      managedGit.CommandGitExecutablePath,
                      managedGit.MingwGitExecutablePath,
                      managedGit.ShellExecutablePath
                  })
        {
            var normalized = SafePath.EnsureNoReparsePointsUnderRoot(
                managedRoot,
                executable);
            if (!File.Exists(normalized))
            {
                throw new FileNotFoundException(
                    "受管理 MinGit executable 不存在。",
                    normalized);
            }

            var attributes = File.GetAttributes(normalized);
            if (attributes.HasFlag(FileAttributes.Directory)
                || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    $"受管理 MinGit executable 必須是非連結的一般檔案：{normalized}");
            }
        }

        var systemDirectory = Environment.SystemDirectory;
        if (string.IsNullOrWhiteSpace(systemDirectory))
        {
            throw new InvalidOperationException("無法解析 Windows System32 目錄。");
        }

        var windowsDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDirectory))
        {
            throw new InvalidOperationException("無法解析 Windows 目錄。");
        }

        // Never inherit the user's PATH or Maven/Git/Bash injection variables. BuildTools receives
        // only the reviewed MinGit tree, selected managed JDK, and Windows system tools.
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            new[]
            {
                managedGit.CommandDirectory,
                managedGit.MingwBinDirectory,
                managedGit.UsrBinDirectory,
                javaBin,
                systemDirectory
            }.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase));
        startInfo.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        startInfo.Environment["JAVA_HOME"] = javaHome;
        startInfo.Environment["SHELL"] = Path.GetFullPath(managedGit.ShellExecutablePath);
        startInfo.Environment["COMSPEC"] = Path.Combine(systemDirectory, "cmd.exe");
        startInfo.Environment["SystemRoot"] = windowsDirectory;
        startInfo.Environment["WINDIR"] = windowsDirectory;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] =
            SpigotBuildToolsGitEnvironment.EnsurePrivateGlobalConfig(
                startInfo.WorkingDirectory,
                createIfMissing: false);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";

        // BuildTools and Maven create source/resource files with Java's line.separator. Windows'
        // default CRLF changes generated NMS blobs and Maven pom.properties, so the official
        // output SHA cannot be reproduced by Git normalization alone. _JAVA_OPTIONS is deliberately
        // rebuilt after Environment.Clear so the main launcher and every child Java/Maven process
        // receive one real LF character without inheriting user-controlled JVM injection options.
        startInfo.Environment["_JAVA_OPTIONS"] = javaMajorVersion >= 25
            ? "-XX:TieredStopAtLevel=1 -Dline.separator=\"\n\""
            : "-Dline.separator=\"\n\"";

        var privateHome = SafePath.CombineUnderRoot(startInfo.WorkingDirectory, "home");
        var privateTemp = SafePath.CombineUnderRoot(startInfo.WorkingDirectory, "temp");
        Directory.CreateDirectory(privateHome);
        Directory.CreateDirectory(privateTemp);
        SafePath.EnsureNoReparsePointsUnderRoot(startInfo.WorkingDirectory, privateHome);
        SafePath.EnsureNoReparsePointsUnderRoot(startInfo.WorkingDirectory, privateTemp);
        startInfo.Environment["HOME"] = privateHome;
        startInfo.Environment["USERPROFILE"] = privateHome;
        startInfo.Environment["TEMP"] = privateTemp;
        startInfo.Environment["TMP"] = privateTemp;
        startInfo.Environment["MAVEN_USER_HOME"] = Path.Combine(privateHome, ".m2");
        startInfo.Environment["MAVEN_OPTS"] =
            "-Xmx1024M " + QuoteJvmSystemProperty("user.home", privateHome);
        return privateHome;

        static string QuoteJvmSystemProperty(string name, string value) =>
            value.Contains(' ')
                ? $"-D{name}=\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
                : $"-D{name}={value}";
    }

    private static void Add(ProcessStartInfo startInfo, params string[] values)
    {
        foreach (var value in values)
        {
            startInfo.ArgumentList.Add(value);
        }
    }
}

/// <summary>
/// A BuildTools-specific process failure. The shared bounded process host is also used by loader
/// installers, but callers of this runner must never receive the misleading loader error label.
/// </summary>
public sealed class SpigotBuildToolsProcessException : Exception
{
    internal SpigotBuildToolsProcessException(
        SpigotBuildPlan plan,
        ModrinthLoaderBootstrapProcessResult result,
        SpigotBuildToolsFailureForensics forensics)
        : base(BuildMessage(plan, result, forensics))
    {
        Plan = plan;
        ExitCode = result.ExitCode;
        OutputTruncated = result.OutputTruncated;
        HotSpotFatalDetected = forensics.HotSpotFatalDetected;
        JitCompilerFatalDetected = forensics.JitCompilerFatalDetected;
        ReplayFileNames = forensics.ReplayFileNames;
        HsErrFileNames = forensics.HsErrFileNames;
        DeclaredCrashFileNames = forensics.DeclaredCrashFileNames;
        RedactedDiagnosticLines = forensics.RedactedDiagnosticLines;
    }

    public SpigotBuildPlan Plan { get; }

    public int ExitCode { get; }

    public bool OutputTruncated { get; }

    public bool HotSpotFatalDetected { get; }

    public bool JitCompilerFatalDetected { get; }

    /// <summary>Replay files that still existed when the child exited, before workspace cleanup.</summary>
    public IReadOnlyList<string> ReplayFileNames { get; }

    /// <summary>HotSpot error reports that still existed when the child exited, before cleanup.</summary>
    public IReadOnlyList<string> HsErrFileNames { get; }

    /// <summary>Crash artifact basenames declared by bounded process output.</summary>
    public IReadOnlyList<string> DeclaredCrashFileNames { get; }

    /// <summary>A small redacted diagnostic excerpt retained after the operation tree is removed.</summary>
    public IReadOnlyList<string> RedactedDiagnosticLines { get; }

    private static string BuildMessage(
        SpigotBuildPlan plan,
        ModrinthLoaderBootstrapProcessResult result,
        SpigotBuildToolsFailureForensics forensics)
    {
        var message = new StringBuilder()
            .Append("Spigot BuildTools ")
            .Append(plan.DisplayName)
            .Append(' ')
            .Append(plan.MinecraftVersion)
            .Append(" 結束碼為 ")
            .Append(result.ExitCode)
            .Append("，建置未完成。");
        if (forensics.JitCompilerFatalDetected)
        {
            message.AppendLine()
                .Append("偵測到 HotSpot JVM JIT 編譯器致命錯誤；這不是一般的 Server installer 失敗。");
            if (plan.JavaMajorVersion >= 25)
            {
                message.AppendLine()
                    .Append("本次已使用受控 C1 模式（-XX:TieredStopAtLevel=1）。");
            }
        }
        else if (forensics.HotSpotFatalDetected)
        {
            message.AppendLine()
                .Append("偵測到 HotSpot JVM 致命錯誤；未取得 replay／compiler task 證據，")
                .Append("不會誤判為 JIT 編譯器崩潰。");
        }

        message.AppendLine()
            .Append("清理前 JVM 鑑識：replay=")
            .Append(FormatArtifactState(forensics.ReplayFileNames, "無"))
            .Append("；hs_err=")
            .Append(FormatArtifactState(forensics.HsErrFileNames, "無"));
        if (forensics.DeclaredCrashFileNames.Count > 0)
        {
            message.Append("；輸出宣告=")
                .Append(string.Join(", ", forensics.DeclaredCrashFileNames));
        }

        if (result.OutputTruncated)
        {
            message.Append("；stdout/stderr 已依安全上限截斷");
        }

        message.Append('。');
        if (forensics.RedactedDiagnosticLines.Count > 0)
        {
            message.AppendLine().AppendLine("已遮蔽診斷摘要：");
            message.Append(string.Join(Environment.NewLine, forensics.RedactedDiagnosticLines));
        }

        return message.ToString();

        static string FormatArtifactState(IReadOnlyList<string> names, string whenEmpty)
            => names.Count == 0 ? whenEmpty : "有（" + string.Join(", ", names) + "）";
    }
}

internal sealed record SpigotBuildToolsFailureForensics(
    bool HotSpotFatalDetected,
    bool JitCompilerFatalDetected,
    IReadOnlyList<string> ReplayFileNames,
    IReadOnlyList<string> HsErrFileNames,
    IReadOnlyList<string> DeclaredCrashFileNames,
    IReadOnlyList<string> RedactedDiagnosticLines);

/// <summary>
/// Runs a reviewed BuildTools JAR in a fresh owned directory and promotes only one output whose
/// SHA-256 matches the official Spigot version JSON. It does not execute BAT/CMD/PowerShell/Git
/// wrappers and exposes no arbitrary BuildTools flags.
/// </summary>
public sealed class SpigotBuildToolsRunner
{
    private const int MaximumForensicArtifactsPerKind = 8;
    private const int MaximumForensicDiagnosticLines = 24;
    private static readonly Regex CrashArtifactNamePattern = new(
        @"(?i)\b(?:replay|hs_err)_pid[0-9]+\.log\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex ExactReplayFileNamePattern = new(
        @"(?i)\Areplay_pid[0-9]+\.log\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex ExactHsErrFileNamePattern = new(
        @"(?i)\Ahs_err_pid[0-9]+\.log\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private readonly IModrinthLoaderBootstrapProcessRunner _processRunner;
    private readonly SpigotBuildToolsCommandBuilder _commandBuilder;
    private readonly IManagedMinGitProvider _managedGitProvider;
    private readonly ISpigotBuildToolsWorkspace _workspace;
    private readonly SpigotBuildToolsArtifactInfo _trustedBuildTools;
    private readonly string _localWorkspaceRoot;

    public SpigotBuildToolsRunner(
        IModrinthLoaderBootstrapProcessRunner? processRunner = null,
        SpigotBuildToolsCommandBuilder? commandBuilder = null,
        string? localWorkspaceRoot = null)
        : this(
            processRunner,
            commandBuilder,
            SpigotBuildToolsProvider.ReviewedBuildTools,
            localWorkspaceRoot ?? GetDefaultLocalWorkspaceRoot(),
            new ManagedMinGitProvider(GetDefaultManagedMinGitCacheRoot()))
    {
    }

    internal SpigotBuildToolsRunner(
        IModrinthLoaderBootstrapProcessRunner? processRunner,
        SpigotBuildToolsCommandBuilder? commandBuilder,
        SpigotBuildToolsArtifactInfo trustedBuildTools,
        string localWorkspaceRoot,
        IManagedMinGitProvider managedGitProvider,
        ISpigotBuildToolsWorkspace? workspace = null)
    {
        _processRunner = processRunner ?? new ModrinthLoaderBootstrapProcessRunner();
        _commandBuilder = commandBuilder ?? new SpigotBuildToolsCommandBuilder();
        _trustedBuildTools = trustedBuildTools
            ?? throw new ArgumentNullException(nameof(trustedBuildTools));
        _managedGitProvider = managedGitProvider
            ?? throw new ArgumentNullException(nameof(managedGitProvider));
        _workspace = workspace ?? new SpigotBuildToolsManagedGitWorkspace();
        ArgumentException.ThrowIfNullOrWhiteSpace(localWorkspaceRoot);
        _localWorkspaceRoot = Path.GetFullPath(localWorkspaceRoot);
    }

    public SpigotBuildToolsPreflightResult CheckPreflight()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SpigotBuildToolsPreflightResult(
                false,
                false,
                "目前只在 Windows 啟用安全自動建立。Linux/macOS 的 BuildTools 需要外部 Git，"
                + "本程式不會從 PATH 執行未驗證的 Git。");
        }

        if (_localWorkspaceRoot.Contains('!'))
        {
            return new SpigotBuildToolsPreflightResult(
                false,
                true,
                "BuildTools 官方文件警告工作路徑不可含驚嘆號；請改用本機暫存路徑。");
        }

        if (IsCloudSyncedPath(_localWorkspaceRoot))
        {
            return new SpigotBuildToolsPreflightResult(
                false,
                true,
                "BuildTools 官方文件不支援 OneDrive/Dropbox 工作目錄；"
                + "請改用未同步的本機暫存路徑。");
        }

        return new SpigotBuildToolsPreflightResult(
            true,
            true,
            null);
    }

    public async Task<SpigotBuildToolsBuildResult> BuildAsync(
        SpigotBuildPlan plan,
        string javaExecutablePath,
        string buildToolsJarPath,
        string stagingRoot,
        string destinationPath,
        IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlan(plan, _trustedBuildTools);
        var preflight = CheckPreflight();
        if (!preflight.CanRun)
        {
            throw new PlatformNotSupportedException(preflight.UnsupportedReason);
        }

        var java = RequireRegularFile(javaExecutablePath, "Java executable");
        RequireJdkCompiler(java);
        var tool = RequireRegularFile(buildToolsJarPath, "BuildTools.jar");
        await VerifyFileAsync(
                tool,
                plan.BuildTools.Size,
                plan.BuildTools.Sha256,
                "BuildTools.jar",
                cancellationToken)
            .ConfigureAwait(false);

        output?.Report(new ModrinthLoaderBootstrapOutputLine(
            false,
            "正在準備受管理 MinGit（避免跳出 PortableGit 原生進度視窗）…"));
        var managedGit = await _managedGitProvider.EnsureInstalledAsync(
                new ManagedMinGitOutputProgress(output),
                cancellationToken)
            .ConfigureAwait(false);
        output?.Report(new ModrinthLoaderBootstrapOutputLine(
            false,
            $"受管理 MinGit {managedGit.Version} 已驗證，開始建立 Server。"));

        var destination = PrepareDestination(stagingRoot, destinationPath);
        var root = _localWorkspaceRoot;
        Directory.CreateDirectory(root);
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException("BuildTools 本機工作根目錄不得是 reparse point。");
        }

        await CleanupStaleOperationsAsync(root, output, cancellationToken)
            .ConfigureAwait(false);

        var operation = SafePath.CombineUnderRoot(root, $"buildtools-{Guid.NewGuid():N}");
        var outputDirectory = SafePath.CombineUnderRoot(operation, "output");
        var leasePath = SafePath.CombineUnderRoot(operation, ".manager-operation.lock");
        FileStream? operationLease = null;
        var promoted = false;
        Exception? operationFailure = null;
        try
        {
            Directory.CreateDirectory(operation);
            Directory.CreateDirectory(outputDirectory);
            SafePath.EnsureNoReparsePointsUnderRoot(root, outputDirectory);
            operationLease = new FileStream(
                leasePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            _ = SpigotBuildToolsGitEnvironment.EnsurePrivateGlobalConfig(
                operation,
                createIfMissing: true);
            await _workspace.PrepareAsync(
                    plan,
                    operation,
                    managedGit,
                    output,
                    cancellationToken)
                .ConfigureAwait(false);

            var startInfo = _commandBuilder.Build(
                plan,
                java,
                tool,
                operation,
                outputDirectory,
                managedGit);
            ModrinthLoaderBootstrapProcessResult processResult;
            try
            {
                processResult = await _processRunner.RunAsync(
                        startInfo,
                        output,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ModrinthLoaderBootstrapProcessException exception)
            {
                throw CreateProcessFailure(
                    plan,
                    operation,
                    stagingRoot,
                    destination,
                    exception.Result);
            }

            if (processResult.ExitCode != 0)
            {
                // Production hosts throw before returning a non-zero result. Keep the runner's
                // contract correct for injected hosts as well, without leaking their loader label.
                throw CreateProcessFailure(
                    plan,
                    operation,
                    stagingRoot,
                    destination,
                    processResult);
            }

            await _workspace.VerifyAsync(
                    plan,
                    operation,
                    managedGit,
                    output,
                    cancellationToken)
                .ConfigureAwait(false);

            SafePath.EnsureNoReparsePointsUnderRoot(operation, outputDirectory);
            var buildToolsOutputFileName = plan.CoreType == CoreType.CraftBukkit
                ? $"craftbukkit-{plan.MinecraftVersion}.jar"
                : plan.OutputFileName;
            var built = SafePath.CombineUnderRoot(outputDirectory, buildToolsOutputFileName);
            if (!File.Exists(built))
            {
                throw new InvalidDataException(
                    $"BuildTools 未產生預期輸出：{buildToolsOutputFileName}。");
            }

            _ = RequireRegularFile(built, $"{plan.DisplayName} output");
            SafePath.EnsureNoReparsePointsUnderRoot(operation, built);

            if (plan.OutputVerificationKind ==
                SpigotBuildOutputVerificationKind.OfficialSourceRefs)
            {
                await VerifyHistoricalOutputStructureAsync(built, plan, cancellationToken)
                    .ConfigureAwait(false);
            }

            var actualOutputSha256 = await VerifyFileAsync(
                    built,
                    expectedSize: null,
                    plan.ExpectedOutputSha256,
                    $"{plan.DisplayName} output",
                    cancellationToken,
                    plan.VersionIdentity)
                .ConfigureAwait(false);

            await PromoteAcrossVolumesAsync(
                    built,
                    destination,
                    stagingRoot,
                    actualOutputSha256,
                    plan.VersionIdentity,
                    cancellationToken)
                .ConfigureAwait(false);
            promoted = true;
            return new SpigotBuildToolsBuildResult(
                plan,
                destination,
                processResult.StandardOutput,
                processResult.StandardError,
                processResult.OutputTruncated,
                actualOutputSha256);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            operationLease?.Dispose();
            operationLease = null;
            try
            {
                await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                        root,
                        operation,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (promoted)
            {
                // The verified server.jar is already atomically promoted. Do not turn a successful
                // build into a false failure solely because antivirus retained a work-tree handle.
                output?.Report(new ModrinthLoaderBootstrapOutputLine(
                    true,
                    "Server 已建立，但 BuildTools 暫存資料夾未能立即清除："
                    + FormatCleanupDiagnostic(exception)));
            }
            catch (Exception cleanupFailure) when (operationFailure is not null)
            {
                throw CreateCleanupFailure(operationFailure, cleanupFailure);
            }
        }
    }

    private static void ValidatePlan(
        SpigotBuildPlan plan,
        SpigotBuildToolsArtifactInfo trustedBuildTools)
    {
        try
        {
            SpigotBuildToolsProvider.ValidateMinecraftVersion(plan.MinecraftVersion);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Spigot BuildTools plan 的 Minecraft version 無效。",
                exception);
        }

        var expectedRefs = new[] { "BuildData", "Bukkit", "CraftBukkit", "Spigot" };
        if (plan.CoreType is not (CoreType.Spigot or CoreType.CraftBukkit)
            || !plan.OutputFileName.Equals("server.jar", StringComparison.Ordinal)
            || !HasValidOutputVerificationContract(plan)
            || plan.JavaMajorVersion < 8
            || plan.RequiredBuildToolsVersion is < 1 or > 197
            || !IsSafeBuildRevision(plan.VersionIdentity)
            || !IsSafeBuildRevision(plan.BuildRevision ?? plan.VersionIdentity)
            || plan.SourceRefs.Count != expectedRefs.Length
            || expectedRefs.Any(name =>
                !plan.SourceRefs.TryGetValue(name, out var value)
                || value.Length != 40
                || value.Any(character => !Uri.IsHexDigit(character)))
            || plan.BuildTools != trustedBuildTools)
        {
            throw new InvalidDataException("Spigot BuildTools plan 未通過固定安全契約。");
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

    private static bool IsSafeRevisionToken(string value)
        => value.Length is >= 1 and <= 64
           && value[0] is >= '1' and <= '9'
           && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsSafeBuildRevision(string value)
        => IsSafeRevisionToken(value)
           || IsSafeMinecraftRevision(value);

    private static bool IsSafeMinecraftRevision(string value)
    {
        try
        {
            SpigotBuildToolsProvider.ValidateMinecraftVersion(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task VerifyHistoricalOutputStructureAsync(
        string jarPath,
        SpigotBuildPlan plan,
        CancellationToken cancellationToken)
    {
        const int maximumEntries = 100_000;
        const long maximumManifestBytes = 1024 * 1024;
        await using var file = new FileStream(
            jarPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is < 1 or > maximumEntries)
        {
            throw new InvalidDataException("BuildTools 歷史版 output 的 JAR entry 數量無效。");
        }

        var names = archive.Entries
            .Select(entry => entry.FullName.Replace('\\', '/').ToLowerInvariant())
            .ToArray();
        var manifestEntries = archive.Entries
            .Where(entry => entry.FullName.Equals(
                "META-INF/MANIFEST.MF",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (manifestEntries.Length != 1
            || manifestEntries[0].Length is < 1 or > maximumManifestBytes)
        {
            throw new InvalidDataException("BuildTools 歷史版 output 缺少唯一且有界的 manifest。");
        }

        string manifest;
        await using (var input = manifestEntries[0].Open())
        using (var reader = new StreamReader(
                   input,
                   Encoding.UTF8,
                   detectEncodingFromByteOrderMarks: true,
                   bufferSize: 4096,
                   leaveOpen: false))
        {
            manifest = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var hasCraftMain = names.Contains(
            "org/bukkit/craftbukkit/main.class",
            StringComparer.Ordinal)
            || names.Contains(
                "org/bukkit/craftbukkit/bootstrap/main.class",
                StringComparer.Ordinal);
        var hasSpigotMarker = names.Contains(
            "org/spigotmc/spigotconfig.class",
            StringComparer.Ordinal);
        var expectedPayload = plan.CoreType == CoreType.Spigot
            ? $"meta-inf/versions/spigot-{plan.MinecraftVersion}-r0.1-snapshot.jar"
            : $"meta-inf/versions/craftbukkit-{plan.MinecraftVersion}-r0.1-snapshot.jar";
        var otherPayloadPrefix = plan.CoreType == CoreType.Spigot
            ? "meta-inf/versions/craftbukkit-"
            : "meta-inf/versions/spigot-";
        var hasExactPayload = names.Count(name => name.Equals(
            expectedPayload,
            StringComparison.Ordinal)) == 1;
        var hasOtherPayload = names.Any(name => name.StartsWith(
            otherPayloadPrefix,
            StringComparison.Ordinal));
        var usesCraftMain = manifest.Contains(
            "Main-Class: org.bukkit.craftbukkit.Main",
            StringComparison.OrdinalIgnoreCase)
            || manifest.Contains(
                "Main-Class: org.bukkit.craftbukkit.bootstrap.Main",
                StringComparison.OrdinalIgnoreCase);

        var coreMatches = plan.CoreType switch
        {
            CoreType.Spigot => hasExactPayload || hasCraftMain && hasSpigotMarker,
            CoreType.CraftBukkit => hasExactPayload || hasCraftMain && !hasSpigotMarker,
            _ => false
        };
        if (!usesCraftMain || !coreMatches || hasOtherPayload)
        {
            throw new InvalidDataException(
                $"BuildTools 歷史版 output 結構不是選取的 {plan.DisplayName} 核心。"
                + "官方四個 source refs 已固定版本，但 JAR 核心 marker 不一致。");
        }
    }

    private static SpigotBuildToolsProcessException CreateProcessFailure(
        SpigotBuildPlan plan,
        string operationDirectory,
        string stagingRoot,
        string destinationPath,
        ModrinthLoaderBootstrapProcessResult result)
    {
        SpigotBuildToolsFailureForensics forensics;
        try
        {
            forensics = CaptureProcessFailureForensics(
                operationDirectory,
                stagingRoot,
                destinationPath,
                result);
        }
        catch
        {
            // Diagnostic capture is strictly secondary: never let it replace the original exit
            // result or prevent the outer finally from cleaning the operation tree.
            forensics = new SpigotBuildToolsFailureForensics(
                false,
                false,
                [],
                [],
                [],
                ["[JVM forensic summary unavailable; workspace cleanup continued]"]);
        }

        return new SpigotBuildToolsProcessException(plan, result, forensics);
    }

    private static SpigotBuildToolsFailureForensics CaptureProcessFailureForensics(
        string operationDirectory,
        string stagingRoot,
        string destinationPath,
        ModrinthLoaderBootstrapProcessResult result)
    {
        var replayFiles = EnumerateCrashArtifacts(
            operationDirectory,
            "replay_pid*.log",
            ExactReplayFileNamePattern);
        var hsErrFiles = EnumerateCrashArtifacts(
            operationDirectory,
            "hs_err_pid*.log",
            ExactHsErrFileNamePattern);
        var combined = result.StandardError.Concat(result.StandardOutput);
        var declared = combined
            .SelectMany(ExtractCrashArtifactNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumForensicArtifactsPerKind * 2)
            .ToArray();
        var jitCompilerFatal = replayFiles.Count > 0
            || declared.Any(name => name.StartsWith("replay_pid", StringComparison.OrdinalIgnoreCase))
            || combined.Any(IsJitCompilerEvidenceLine);
        var hotSpotFatal = jitCompilerFatal
            || hsErrFiles.Count > 0
            || declared.Any(name => name.StartsWith("hs_err_pid", StringComparison.OrdinalIgnoreCase))
            || combined.Any(IsHotSpotVmFatalLine);

        var significant = combined
            .Select(line => NormalizeForensicDiagnosticLine(
                line,
                operationDirectory,
                stagingRoot,
                destinationPath,
                hotSpotFatal))
            .Where(static line => line is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumForensicDiagnosticLines)
            .ToArray();
        return new SpigotBuildToolsFailureForensics(
            hotSpotFatal,
            jitCompilerFatal,
            replayFiles,
            hsErrFiles,
            declared,
            significant);
    }

    private static IReadOnlyList<string> EnumerateCrashArtifacts(
        string operationDirectory,
        string searchPattern,
        Regex exactFileNamePattern)
    {
        try
        {
            return Directory.EnumerateFiles(
                    operationDirectory,
                    searchPattern,
                    SearchOption.TopDirectoryOnly)
                .Take(MaximumForensicArtifactsPerKind)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    info.Refresh();
                    if (!info.Exists
                        || info.Attributes.HasFlag(FileAttributes.Directory)
                        || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        || !exactFileNamePattern.IsMatch(info.Name))
                    {
                        return null;
                    }

                    return $"{info.Name} ({info.Length} bytes)";
                })
                .Where(static value => value is not null)
                .Cast<string>()
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Failure diagnostics must never replace the primary BuildTools process failure.
            return [];
        }
    }

    private static IEnumerable<string> ExtractCrashArtifactNames(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            yield break;
        }

        MatchCollection matches;
        try
        {
            matches = CrashArtifactNamePattern.Matches(line);
        }
        catch (RegexMatchTimeoutException)
        {
            yield break;
        }

        foreach (Match match in matches)
        {
            yield return match.Value;
        }
    }

    private static bool IsHotSpotVmFatalLine(string line)
        => line.Contains("A fatal error has been detected by the Java Runtime Environment", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Problematic frame:", StringComparison.OrdinalIgnoreCase);

    private static bool IsJitCompilerEvidenceLine(string line)
        => line.Contains("Compiler replay data is saved as", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Current CompileTask", StringComparison.OrdinalIgnoreCase)
           || line.Contains("CompilerThread", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeForensicDiagnosticLine(
        string? value,
        string operationDirectory,
        string stagingRoot,
        string destinationPath,
        bool hotSpotFatalDetected)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Contains(
                "A fatal error has been detected by the Java Runtime Environment",
                StringComparison.OrdinalIgnoreCase))
        {
            return "HotSpot fatal-error marker detected.";
        }

        if (value.Contains("Compiler replay data is saved as", StringComparison.OrdinalIgnoreCase)
            || value.Contains("replay_pid", StringComparison.OrdinalIgnoreCase))
        {
            return FormatArtifactMarker("Compiler replay", value);
        }

        if (value.Contains("hs_err_pid", StringComparison.OrdinalIgnoreCase))
        {
            return FormatArtifactMarker("HotSpot error report", value);
        }

        if (value.Contains("Current CompileTask", StringComparison.OrdinalIgnoreCase)
            || value.Contains("CompilerThread", StringComparison.OrdinalIgnoreCase))
        {
            return "HotSpot compiler-task marker detected.";
        }

        if (value.Contains("Problematic frame:", StringComparison.OrdinalIgnoreCase))
        {
            return "HotSpot problematic-frame marker detected; raw frame omitted.";
        }

        if (hotSpotFatalDetected
            && value.Contains("Internal Error", StringComparison.OrdinalIgnoreCase))
        {
            return "HotSpot internal-error marker detected; raw detail omitted.";
        }

        if (value.Contains("JRE version:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("openjdk version", StringComparison.OrdinalIgnoreCase))
        {
            return "JRE version marker detected; use the verified plan Java major for diagnosis.";
        }

        if (value.Contains("Java VM:", StringComparison.OrdinalIgnoreCase))
        {
            return "Java VM marker detected; raw host detail omitted.";
        }

        if (value.Contains("Decompiling class", StringComparison.OrdinalIgnoreCase))
        {
            return "BuildTools decompilation was active when the process failed.";
        }

        if (value.Contains("Picked up _JAVA_OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return "Controlled JVM options inheritance marker detected.";
        }

        // Strict allowlist: unclassified console lines are deliberately omitted rather than
        // relying on a denylist that could miss credentials, URLs, hosts, e-mail, or paths.
        return null;

        string FormatArtifactMarker(string kind, string line)
        {
            var names = ExtractCrashArtifactNames(line)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumForensicArtifactsPerKind)
                .ToArray();
            var nameText = names.Length == 0 ? "name unavailable" : string.Join(", ", names);
            return $"{kind} marker detected: {nameText}; location={GetLocationLabel(line)}.";
        }

        string GetLocationLabel(string line)
        {
            if (ContainsPath(line, destinationPath) || ContainsPath(line, stagingRoot))
            {
                return "<TARGET>";
            }

            if (ContainsPath(line, operationDirectory))
            {
                return "<WORKSPACE>";
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData) && ContainsPath(line, localAppData))
            {
                return "<LOCALAPPDATA>";
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile) && ContainsPath(line, userProfile))
            {
                return "<USERPROFILE>";
            }

            return line.Contains(":\\", StringComparison.Ordinal)
                   || line.Contains("\\\\", StringComparison.Ordinal)
                   || line.Contains("file:/", StringComparison.OrdinalIgnoreCase)
                ? "<EXTERNAL-OMITTED>"
                : "not retained";
        }

        static bool ContainsPath(string line, string path)
            => line.Contains(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }

    private static string RequireRegularFile(string path, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"找不到 {context}。", fullPath);
        }

        var attributes = File.GetAttributes(fullPath);
        if (attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException($"{context} 必須是非連結的一般檔案。");
        }

        return fullPath;
    }

    private static string PrepareDestination(string stagingRoot, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var targetRoot = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(targetRoot);
        var destination = SafePath.EnsureWithinRoot(
            targetRoot,
            destinationPath,
            allowRoot: false);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("BuildTools output 目的路徑缺少父目錄。");
        Directory.CreateDirectory(parent);

        EnsureDestinationDescendantsAreNotReparsePoints(targetRoot, destination);

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"目的檔案已存在，為避免覆寫已取消：{destination}");
        }

        return destination;
    }

    private static void EnsureDestinationDescendantsAreNotReparsePoints(
        string targetRoot,
        string destination)
    {
        // The workflow staging root itself is a caller-declared trust boundary and may live under
        // OneDrive/reparse-backed storage. Redirecting descendants inside that root remain blocked.
        targetRoot = Path.GetFullPath(targetRoot);
        destination = SafePath.EnsureWithinRoot(targetRoot, destination, allowRoot: false);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("BuildTools output 目的路徑缺少父目錄。");
        var relativeParent = Path.GetRelativePath(targetRoot, parent);
        var current = targetRoot;
        foreach (var segment in relativeParent.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException(
                    $"BuildTools output 目的路徑包含 redirecting descendant：{current}");
            }
        }
    }

    private static async Task PromoteAcrossVolumesAsync(
        string sourcePath,
        string destinationPath,
        string targetRoot,
        string expectedSha256,
        string versionIdentity,
        CancellationToken cancellationToken)
    {
        EnsureDestinationDescendantsAreNotReparsePoints(targetRoot, destinationPath);
        var sourceSize = new FileInfo(sourcePath).Length;
        var partial = destinationPath + $".{Guid.NewGuid():N}.partial";
        var destinationCreated = false;
        try
        {
            await using (var input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                partial,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total = checked(total + read);
                    if (total > sourceSize
                        || total > FirstPartyArtifactHttp.MaximumArtifactBytes)
                    {
                        throw new InvalidDataException("BuildTools output 複製大小超過驗證來源。");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }

                if (total != sourceSize)
                {
                    throw new InvalidDataException("BuildTools output 跨磁碟複製大小不符。");
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            EnsureDestinationDescendantsAreNotReparsePoints(targetRoot, destinationPath);
            var partialAttributes = File.GetAttributes(partial);
            if (partialAttributes.HasFlag(FileAttributes.Directory)
                || partialAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException(
                    "BuildTools target-volume partial 必須是非連結的一般檔案。");
            }

            // Keep a no-write/share-delete handle open from verification through the atomic rename.
            // This prevents another process from replacing or modifying the verified file in the
            // post-hash/pre-move gap while still allowing Windows to rename this exact file object.
            await using var verifiedPartial = new FileStream(
                partial,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await VerifyOpenFileAsync(
                    verifiedPartial,
                    sourceSize,
                    expectedSha256,
                    "BuildTools target-volume partial",
                    cancellationToken,
                    versionIdentity)
                .ConfigureAwait(false);
            EnsureDestinationDescendantsAreNotReparsePoints(targetRoot, destinationPath);
            File.Move(partial, destinationPath, overwrite: false);
            destinationCreated = true;
            EnsureDestinationDescendantsAreNotReparsePoints(targetRoot, destinationPath);
            await VerifyFileAsync(
                    destinationPath,
                    sourceSize,
                    expectedSha256,
                    "BuildTools promoted output",
                    cancellationToken,
                    versionIdentity)
                .ConfigureAwait(false);
        }
        catch (Exception operationFailure)
        {
            try
            {
                await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                        targetRoot,
                        partial,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (destinationCreated)
                {
                    await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                            targetRoot,
                            destinationPath,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception cleanupFailure)
            {
                throw CreateCleanupFailure(operationFailure, cleanupFailure);
            }

            throw;
        }
    }

    private static async Task<string> VerifyFileAsync(
        string path,
        long? expectedSize,
        string? expectedSha256,
        string context,
        CancellationToken cancellationToken,
        string? versionIdentity = null)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await VerifyOpenFileAsync(
                stream,
                expectedSize,
                expectedSha256,
                context,
                cancellationToken,
                versionIdentity)
            .ConfigureAwait(false);
    }

    private static async Task<string> VerifyOpenFileAsync(
        FileStream stream,
        long? expectedSize,
        string? expectedSha256,
        string context,
        CancellationToken cancellationToken,
        string? versionIdentity = null)
    {
        if (stream.Length is < 1 or > FirstPartyArtifactHttp.MaximumArtifactBytes
            || (expectedSize is not null && stream.Length != expectedSize.Value))
        {
            throw new InvalidDataException($"{context} 檔案大小驗證失敗。");
        }

        stream.Position = 0;
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actualText = Convert.ToHexString(actual).ToLowerInvariant();
        if (expectedSha256 is null)
        {
            return actualText;
        }

        var expected = Convert.FromHexString(expectedSha256);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            var expectedText = expectedSha256.ToLowerInvariant();
            var identityText = string.IsNullOrWhiteSpace(versionIdentity)
                ? "<not-applicable>"
                : versionIdentity;
            throw new InvalidDataException(
                $"{context} SHA-256 驗證失敗；expected={expectedText}；"
                + $"actual={actualText}；identity={identityText}。");
        }

        return actualText;
    }

    private static Exception CreateCleanupFailure(
        Exception operationFailure,
        Exception cleanupFailure)
    {
        var combined = new AggregateException(operationFailure, cleanupFailure);
        return operationFailure is OperationCanceledException canceled
            ? new OperationCanceledException(
                "作業已取消，但未完成的暫存檔案無法完整清除。",
                combined,
                canceled.CancellationToken)
            : new IOException(
                "作業失敗，且未完成的暫存檔案無法完整清除。",
                combined);
    }

    private static string FormatCleanupDiagnostic(Exception exception)
    {
        var native = exception is Win32Exception win32
            ? $"；Win32={win32.NativeErrorCode}"
            : string.Empty;
        return $"{exception.Message}（type={exception.GetType().Name}；"
            + $"HResult=0x{exception.HResult:X8}{native}；"
            + "retry-policy=transient only / attempts<=5 / wait<=30s）";
    }

    private static void RequireJdkCompiler(string javaExecutablePath)
    {
        var binDirectory = Path.GetDirectoryName(javaExecutablePath)
            ?? throw new InvalidDataException("Java executable 缺少 bin 目錄。");
        var compiler = Path.Combine(
            binDirectory,
            OperatingSystem.IsWindows() ? "javac.exe" : "javac");
        try
        {
            RequireRegularFile(compiler, "JDK compiler (javac)");
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException(
                "Spigot BuildTools 需要完整 JDK（同一個 Java bin 目錄必須含 javac.exe），"
                + "目前選到的是 JRE 或不完整 runtime。",
                exception);
        }
    }

    private static string GetDefaultLocalWorkspaceRoot()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = Path.GetTempPath();
        }

        return Path.Combine(basePath, "MuhunMCSVManager", "BuildToolsWork");
    }

    private static string GetDefaultManagedMinGitCacheRoot()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = Path.GetTempPath();
        }

        return Path.Combine(basePath, "MuhunMCSVManager", "ManagedTools", "MinGit");
    }

    private static async Task CleanupStaleOperationsAsync(
        string workspaceRoot,
        IProgress<ModrinthLoaderBootstrapOutputLine>? output,
        CancellationToken cancellationToken)
    {
        const int maximumCandidates = 256;
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        List<DirectoryInfo> candidates;
        try
        {
            candidates = new DirectoryInfo(workspaceRoot)
                .EnumerateDirectories(
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                        ReturnSpecialDirectories = false,
                        AttributesToSkip = 0
                    })
                .Take(maximumCandidates)
                .ToList();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            output?.Report(new ModrinthLoaderBootstrapOutputLine(
                true,
                "無法掃描先前的 BuildTools 暫存；本次建立仍會繼續：" + exception.Message));
            return;
        }

        foreach (var directory in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isEligible;
            try
            {
                isEligible = IsManagerOperationDirectoryName(directory.Name)
                    && directory.LastWriteTimeUtc <= cutoff
                    && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                output?.Report(new ModrinthLoaderBootstrapOutputLine(
                    true,
                    $"略過無法檢查的 BuildTools 暫存 {directory.Name}：{exception.Message}"));
                continue;
            }

            if (!isEligible)
            {
                continue;
            }

            var path = directory.FullName;
            var leasePath = Path.Combine(path, ".manager-operation.lock");
            if (File.Exists(leasePath) && !CanExclusivelyOpen(leasePath))
            {
                continue;
            }

            try
            {
                await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                        workspaceRoot,
                        path,
                        cancellationToken)
                    .ConfigureAwait(false);
                output?.Report(new ModrinthLoaderBootstrapOutputLine(
                    false,
                    $"已清除先前未完成的 BuildTools 暫存：{directory.Name}"));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                output?.Report(new ModrinthLoaderBootstrapOutputLine(
                    true,
                    $"略過仍在使用的 BuildTools 暫存 {directory.Name}：{exception.Message}"));
            }
        }
    }

    private static bool IsManagerOperationDirectoryName(string name)
    {
        const string prefix = "buildtools-";
        return name.Length == prefix.Length + 32
            && name.StartsWith(prefix, StringComparison.Ordinal)
            && name.AsSpan(prefix.Length).ContainsAnyExcept(
                "0123456789abcdefABCDEF") is false;
    }

    private static bool CanExclusivelyOpen(string path)
    {
        try
        {
            using var lease = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class ManagedMinGitOutputProgress(
        IProgress<ModrinthLoaderBootstrapOutputLine>? output)
        : IProgress<ManagedMinGitProgress>
    {
        private readonly object _sync = new();
        private ManagedMinGitProgressPhase? _lastPhase;
        private int _lastBucket = -1;

        public void Report(ManagedMinGitProgress value)
        {
            if (output is null)
            {
                return;
            }

            string? message;
            lock (_sync)
            {
                var bucket = value.Percentage is null
                    ? -1
                    : Math.Clamp((int)Math.Floor(value.Percentage.Value * 10d), 0, 10);
                if (_lastPhase == value.Phase && (_lastBucket == bucket || bucket < 0))
                {
                    return;
                }

                _lastPhase = value.Phase;
                _lastBucket = bucket;
                var suffix = bucket < 0 ? string.Empty : $" {bucket * 10}%";
                message = value.Phase switch
                {
                    ManagedMinGitProgressPhase.CheckingCache => "正在檢查受管理 MinGit cache…",
                    ManagedMinGitProgressPhase.Downloading => "正在下載受管理 MinGit…" + suffix,
                    ManagedMinGitProgressPhase.Extracting => "正在安全解壓受管理 MinGit…" + suffix,
                    ManagedMinGitProgressPhase.Verifying => "正在驗證受管理 MinGit 版本…",
                    _ => null
                };
            }

            if (message is not null)
            {
                output.Report(new ModrinthLoaderBootstrapOutputLine(false, message));
            }
        }
    }

    private static bool IsCloudSyncedPath(string path)
    {
        var segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment =>
            segment.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
            || segment.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("Dropbox", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (var variable in new[]
                 {
                     "OneDrive", "OneDriveConsumer", "OneDriveCommercial", "Dropbox"
                 })
        {
            var configured = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(configured)
                && SafePath.IsWithinRoot(configured, path))
            {
                return true;
            }
        }

        return false;
    }
}
