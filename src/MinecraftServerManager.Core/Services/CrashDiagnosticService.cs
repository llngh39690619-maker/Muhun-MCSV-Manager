using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Services;

public enum CrashDiagnosticSeverity
{
    Information,
    Warning,
    Critical
}

public enum CrashCauseCategory
{
    Unknown,
    OutOfMemory,
    WatchdogHang,
    ModOrMixinFailure,
    MissingDependency,
    JavaIncompatible,
    PortConflict,
    WorldDataFailure,
    DiskSpace,
    FilePermission
}

public sealed record CrashDiagnosticFinding(
    string Code,
    CrashCauseCategory Category,
    CrashDiagnosticSeverity Severity,
    string Title,
    string Evidence,
    string RecommendedAction,
    bool IsSafeForAutomaticRepair = false);

public sealed record CrashDiagnosticInput(
    ServerInstance Instance,
    Guid SessionId,
    DateTimeOffset OccurredAtUtc,
    string Trigger,
    int? ExitCode,
    Exception? Error,
    IReadOnlyList<ConsoleLine> ConsoleLines,
    string? LatestLogTail = null,
    string? NativeCrashReport = null,
    string? LastHealthyRecoveryPoint = null);

public sealed record CrashDiagnosticReport(
    Guid InstanceId,
    Guid SessionId,
    string ServerName,
    string Trigger,
    DateTimeOffset OccurredAtUtc,
    int? ExitCode,
    string? ErrorType,
    string? ErrorMessage,
    string? MinecraftVersion,
    CoreType CoreType,
    int? JavaMajorVersion,
    string? LastHealthyRecoveryPoint,
    IReadOnlyList<CrashDiagnosticFinding> Findings,
    IReadOnlyList<string> SuspectedModIds,
    string DirectoryPath = "");

public sealed record CrashDiagnosticArtifacts(
    CrashDiagnosticReport Report,
    string ReportDirectory,
    string MarkdownPath,
    string JsonPath,
    string ConsoleTailPath);

/// <summary>
/// Produces bounded local crash diagnostics. It never removes mods, edits worlds, or restores a
/// backup automatically; findings marked safe merely indicate that a future guided action may be
/// offered after explicit confirmation.
/// </summary>
public sealed partial class CrashDiagnosticService
{
    private const int MaximumConsoleLines = 500;
    private const int MaximumLineCharacters = 4096;
    private const int MaximumTextCharacters = 4 * 1024 * 1024;

    public CrashDiagnosticReport Analyze(CrashDiagnosticInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Instance);
        ArgumentNullException.ThrowIfNull(input.ConsoleLines);

        var text = BuildAnalysisText(input);
        var findings = new List<CrashDiagnosticFinding>();

        if (input.Trigger.StartsWith("StatusWatchdog", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new CrashDiagnosticFinding(
                "STATUS_PROTOCOL_UNRESPONSIVE",
                CrashCauseCategory.WatchdogHang,
                CrashDiagnosticSeverity.Critical,
                "Minecraft 狀態協定連續無回應",
                "在啟動寬限期後，狀態要求與 ping 連續超過設定門檻；報告的 Trigger 會標示安全停止或強制終止。",
                "檢查主執行緒卡住的模組、磁碟 I/O、GC 與網路綁定；不要只依單次 timeout 刪除模組或回復世界。"));
        }

        AddIfMatch(findings, text, OutOfMemoryPattern(), new(
            "JVM_OUT_OF_MEMORY",
            CrashCauseCategory.OutOfMemory,
            CrashDiagnosticSeverity.Critical,
            "Java 記憶體不足",
            "日誌包含 OutOfMemoryError 或 GC overhead limit exceeded。",
            "確認實體記憶體充足後再提高 -Xmx；同時檢查記憶體洩漏、過量區塊載入與最近新增的模組。"));
        AddIfMatch(findings, text, WatchdogPattern(), new(
            "SERVER_WATCHDOG",
            CrashCauseCategory.WatchdogHang,
            CrashDiagnosticSeverity.Critical,
            "Server tick 超時或 Watchdog 終止",
            "日誌顯示單一 tick 過久、ServerHangWatchdog 或 Watchdog crash。",
            "先使用最近一次健康恢復點保護世界，再檢查報告中的主執行緒堆疊、卡住的模組與區塊。不要直接永久調高 max-tick-time 掩蓋問題。"));
        AddIfMatch(findings, text, MixinPattern(), new(
            "MOD_MIXIN_FAILURE",
            CrashCauseCategory.ModOrMixinFailure,
            CrashDiagnosticSeverity.Critical,
            "模組或 Mixin 套用失敗",
            "日誌包含 MixinApplyError、MixinTransformerError 或模組載入例外。",
            "核對疑似模組是否支援目前 Minecraft、Loader 與 Java；依賴關係確認後再於備份狀態下停用或回退該模組。"));
        AddIfMatch(findings, text, MissingDependencyPattern(), new(
            "MOD_DEPENDENCY_MISSING",
            CrashCauseCategory.MissingDependency,
            CrashDiagnosticSeverity.Critical,
            "缺少或不相容的模組依賴",
            "日誌指出 mandatory dependency、dependency resolution 或 mod dependency 失敗。",
            "依錯誤列出的 Mod ID 安裝正確版本，或將衝突模組回退到模組包指定版本；不要只刪除依賴檔。"));
        AddIfMatch(findings, text, JavaVersionPattern(), new(
            "JAVA_CLASS_VERSION",
            CrashCauseCategory.JavaIncompatible,
            CrashDiagnosticSeverity.Critical,
            "Java 版本與核心或模組不相容",
            "日誌包含 UnsupportedClassVersionError 或 class file version 不相容。",
            "改用模組包 metadata／Minecraft 版本要求的精確 Java major，再重新啟動；不要以任意最新版 Java 取代舊 Forge 所需版本。"));
        AddIfMatch(findings, text, PortPattern(), new(
            "PORT_BIND_FAILED",
            CrashCauseCategory.PortConflict,
            CrashDiagnosticSeverity.Warning,
            "Server Port 無法綁定",
            "日誌包含 Address already in use 或 Failed to bind to port。",
            "讓管理器重新配置第一個可用 Port，並確認沒有第二份 GUI 對同一 Server 資料夾啟動。",
            IsSafeForAutomaticRepair: true));
        AddIfMatch(findings, text, WorldPattern(), new(
            "WORLD_DATA_FAILURE",
            CrashCauseCategory.WorldDataFailure,
            CrashDiagnosticSeverity.Critical,
            "世界或區塊資料讀取失敗",
            "日誌包含世界載入、region/chunk corruption 或 level.dat 錯誤。",
            "保留目前崩潰現場後，從最近健康恢復點建立另一份復原 Server；不要直接覆寫唯一的世界資料。"));
        AddIfMatch(findings, text, DiskPattern(), new(
            "DISK_SPACE_EXHAUSTED",
            CrashCauseCategory.DiskSpace,
            CrashDiagnosticSeverity.Critical,
            "磁碟空間不足",
            "日誌包含 No space left on device、disk full 或磁碟空間錯誤。",
            "先停止反覆重啟並釋放磁碟空間；確認世界與備份可完整寫入後再啟動。"));
        AddIfMatch(findings, text, PermissionPattern(), new(
            "FILE_PERMISSION_DENIED",
            CrashCauseCategory.FilePermission,
            CrashDiagnosticSeverity.Warning,
            "檔案權限或同步鎖定失敗",
            "日誌包含 AccessDeniedException、Permission denied 或 UnauthorizedAccessException。",
            "檢查防毒、OneDrive 同步與檔案權限；不要以系統管理員身分長期繞過不明的鎖定原因。"));

        AddStaticSelfChecks(input.Instance, findings);
        if (findings.Count == 0)
        {
            findings.Add(new CrashDiagnosticFinding(
                "UNKNOWN_CRASH",
                CrashCauseCategory.Unknown,
                CrashDiagnosticSeverity.Warning,
                "尚未辨識崩潰原因",
                "現有日誌沒有符合安全規則的已知原因。",
                "查看本報告附帶的控制台尾端與 Minecraft crash-report；先保留最近健康恢復點，再逐步回退最近的模組或設定變更。"));
        }

        var suspectedMods = SuspectedModPattern().Matches(text).Cast<Match>()
            .Select(match => match.Groups[1].Value)
            .Where(value => value.Length is >= 2 and <= 64)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToArray();

        return new CrashDiagnosticReport(
            input.Instance.Id,
            input.SessionId,
            input.Instance.Name,
            input.Trigger,
            input.OccurredAtUtc,
            input.ExitCode,
            input.Error?.GetType().FullName,
            input.Error is null ? null : RedactSecrets(input.Error.Message),
            input.Instance.MinecraftVersion,
            input.Instance.CoreType,
            input.Instance.JavaMajorVersion,
            input.LastHealthyRecoveryPoint,
            findings,
            suspectedMods);
    }

    public async Task<CrashDiagnosticArtifacts> CreateReportAsync(
        string crashReportsRoot,
        CrashDiagnosticInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(crashReportsRoot);
        var root = Path.GetFullPath(crashReportsRoot);
        Directory.CreateDirectory(root);
        SafePath.EnsureNoReparsePointsUnderRoot(root, root);

        var serverFolder = SafePath.CombineUnderRoot(
            root,
            $"{SafePath.SanitizeFileName(input.Instance.Name, maxLength: 48)}-{input.Instance.Id:N}");
        Directory.CreateDirectory(serverFolder);
        SafePath.EnsureNoReparsePointsUnderRoot(root, serverFolder);

        var preferred = $"{input.OccurredAtUtc:yyyyMMdd-HHmmss}-{input.SessionId:N}";
        var reportDirectory = SafePath.CreateUniqueDirectoryPath(serverFolder, preferred);
        Directory.CreateDirectory(reportDirectory);
        SafePath.EnsureNoReparsePointsUnderRoot(root, reportDirectory);

        var report = Analyze(input) with { DirectoryPath = reportDirectory };
        var jsonPath = SafePath.CombineUnderRoot(reportDirectory, "report.json");
        var markdownPath = SafePath.CombineUnderRoot(reportDirectory, "report.md");
        var consoleTailPath = SafePath.CombineUnderRoot(reportDirectory, "console-tail.txt");

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        var consoleTail = BuildConsoleTail(input.ConsoleLines);
        var markdown = BuildMarkdown(report);
        await WriteNewFileAtomicallyAsync(jsonPath, json, cancellationToken).ConfigureAwait(false);
        await WriteNewFileAtomicallyAsync(consoleTailPath, consoleTail, cancellationToken).ConfigureAwait(false);
        await WriteNewFileAtomicallyAsync(markdownPath, markdown, cancellationToken).ConfigureAwait(false);

        return new CrashDiagnosticArtifacts(report, reportDirectory, markdownPath, jsonPath, consoleTailPath);
    }

    private static string BuildAnalysisText(CrashDiagnosticInput input)
    {
        var builder = new StringBuilder();
        if (input.Error is not null)
        {
            builder.AppendLine(input.Error.ToString());
        }

        foreach (var line in input.ConsoleLines.TakeLast(MaximumConsoleLines))
        {
            AppendBounded(builder, line.Text);
        }

        AppendBounded(builder, input.LatestLogTail);
        AppendBounded(builder, input.NativeCrashReport);
        return builder.Length <= MaximumTextCharacters
            ? builder.ToString()
            : builder.ToString(0, MaximumTextCharacters);
    }

    private static void AppendBounded(StringBuilder builder, string? value)
    {
        if (string.IsNullOrEmpty(value) || builder.Length >= MaximumTextCharacters) return;
        var length = Math.Min(value.Length, MaximumTextCharacters - builder.Length);
        builder.AppendLine(value[..length]);
    }

    private static string BuildConsoleTail(IReadOnlyList<ConsoleLine> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines.TakeLast(MaximumConsoleLines))
        {
            var text = RedactSecrets(line.Text);
            if (text.Length > MaximumLineCharacters)
            {
                text = text[..MaximumLineCharacters] + " …[truncated]";
            }

            builder.Append('[').Append(line.Timestamp.ToString("O")).Append("] [")
                .Append(line.Stream).Append("] ").AppendLine(text);
        }

        return builder.ToString();
    }

    private static string BuildMarkdown(CrashDiagnosticReport report)
    {
        var builder = new StringBuilder()
            .AppendLine($"# {EscapeMarkdown(report.ServerName)} 崩潰診斷")
            .AppendLine()
            .AppendLine($"- 發生時間：`{report.OccurredAtUtc:O}`")
            .AppendLine($"- 觸發原因：`{EscapeMarkdown(report.Trigger)}`")
            .AppendLine($"- Exit Code：`{report.ExitCode?.ToString() ?? "未知"}`")
            .AppendLine($"- 核心：`{report.CoreType}`")
            .AppendLine($"- Minecraft：`{EscapeMarkdown(report.MinecraftVersion ?? "未知")}`")
            .AppendLine($"- Java：`{report.JavaMajorVersion?.ToString() ?? "未知"}`")
            .AppendLine($"- 最近健康恢復點：`{EscapeMarkdown(report.LastHealthyRecoveryPoint ?? "尚無")}`")
            .AppendLine()
            .AppendLine("## 自檢結果")
            .AppendLine();

        foreach (var finding in report.Findings)
        {
            builder.AppendLine($"### [{finding.Severity}] {EscapeMarkdown(finding.Title)}")
                .AppendLine()
                .AppendLine($"- 代碼：`{finding.Code}`")
                .AppendLine($"- 依據：{EscapeMarkdown(finding.Evidence)}")
                .AppendLine($"- 建議處理：{EscapeMarkdown(finding.RecommendedAction)}")
                .AppendLine($"- 可無人值守自動修復：{(finding.IsSafeForAutomaticRepair ? "是" : "否")}")
                .AppendLine();
        }

        if (report.SuspectedModIds.Count > 0)
        {
            builder.AppendLine("## 日誌中出現的疑似 Mod ID")
                .AppendLine()
                .AppendLine(string.Join("、", report.SuspectedModIds.Select(id => $"`{EscapeMarkdown(id)}`")))
                .AppendLine();
        }

        builder.AppendLine("## 資料安全說明")
            .AppendLine()
            .AppendLine("本報告不會自動刪除模組、覆寫世界或靜默還原。若要還原，應先保留目前崩潰現場，再由使用者確認以最近健康恢復點建立獨立復原副本。");
        return builder.ToString();
    }

    private static void AddStaticSelfChecks(
        ServerInstance instance,
        ICollection<CrashDiagnosticFinding> findings)
    {
        if (!Directory.Exists(instance.DirectoryPath))
        {
            findings.Add(new CrashDiagnosticFinding(
                "SERVER_DIRECTORY_MISSING",
                CrashCauseCategory.FilePermission,
                CrashDiagnosticSeverity.Critical,
                "Server 資料夾不存在",
                instance.DirectoryPath,
                "重新掛載或還原原始 Server 資料夾；在路徑恢復前不要建立同名空資料夾。"));
        }

        if (string.IsNullOrWhiteSpace(instance.JavaExecutablePath)
            || !File.Exists(instance.JavaExecutablePath))
        {
            findings.Add(new CrashDiagnosticFinding(
                "JAVA_EXECUTABLE_MISSING",
                CrashCauseCategory.JavaIncompatible,
                CrashDiagnosticSeverity.Critical,
                "指定的 Java 執行檔不存在",
                instance.JavaExecutablePath ?? "未設定",
                $"重新下載並驗證 Java {instance.JavaMajorVersion?.ToString() ?? "需求版本"}，再更新此 Instance 的 Java 路徑。"));
        }

        if (instance.LaunchKind == ServerLaunchKind.ExecutableJar
            && (string.IsNullOrWhiteSpace(instance.ServerJarPath) || !File.Exists(instance.ServerJarPath)))
        {
            findings.Add(new CrashDiagnosticFinding(
                "SERVER_JAR_MISSING",
                CrashCauseCategory.ModOrMixinFailure,
                CrashDiagnosticSeverity.Critical,
                "Server 核心 JAR 不存在",
                instance.ServerJarPath,
                "從原始官方來源重新下載並驗證相同版本核心；不要用不同核心直接覆蓋現有世界。"));
        }
    }

    private static void AddIfMatch(
        ICollection<CrashDiagnosticFinding> findings,
        string text,
        Regex pattern,
        CrashDiagnosticFinding finding)
    {
        if (pattern.IsMatch(text)
            && !findings.Any(existing => existing.Code.Equals(finding.Code, StringComparison.Ordinal)))
        {
            findings.Add(finding);
        }
    }

    private static async Task WriteNewFileAtomicallyAsync(
        string destination,
        string contents,
        CancellationToken cancellationToken)
    {
        var partial = destination + $".{Guid.NewGuid():N}.partial";
        try
        {
            await using (var stream = new FileStream(
                             partial,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using (var writer = new StreamWriter(
                                 stream,
                                 new UTF8Encoding(false),
                                 bufferSize: 64 * 1024,
                                 leaveOpen: true))
                {
                    await writer.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(partial, destination, overwrite: false);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
    }

    private static string RedactSecrets(string value)
    {
        var redacted = SecretPattern().Replace(value, "$1=[REDACTED]");
        redacted = BearerPattern().Replace(redacted, "$1[REDACTED]");
        redacted = UriUserInfoPattern().Replace(redacted, "$1[REDACTED]@");
        return UriQuerySecretPattern().Replace(redacted, "$1[REDACTED]");
    }

    private static string EscapeMarkdown(string value)
        => value.Replace("`", "'").Replace("\r", " ").Replace("\n", " ");

    [GeneratedRegex("(?i)(?:java\\.lang\\.)?OutOfMemoryError|GC overhead limit exceeded", RegexOptions.NonBacktracking)]
    private static partial Regex OutOfMemoryPattern();

    [GeneratedRegex("(?i)A single server tick took|ServerHangWatchdog|watchdog.*(?:crash|timeout|hang)", RegexOptions.NonBacktracking)]
    private static partial Regex WatchdogPattern();

    [GeneratedRegex("(?i)MixinApplyError|MixinTransformerError|ModLoadingException|Failed to load mod", RegexOptions.NonBacktracking)]
    private static partial Regex MixinPattern();

    [GeneratedRegex("(?i)missing mandatory dependenc|dependency resolution failed|requires .* but .* is not installed|mod dependenc", RegexOptions.NonBacktracking)]
    private static partial Regex MissingDependencyPattern();

    [GeneratedRegex("(?i)UnsupportedClassVersionError|class file version .* only recognizes|compiled by a more recent version of the Java Runtime", RegexOptions.NonBacktracking)]
    private static partial Regex JavaVersionPattern();

    [GeneratedRegex("(?i)Address already in use|Failed to bind to port|Perhaps a server is already running", RegexOptions.NonBacktracking)]
    private static partial Regex PortPattern();

    [GeneratedRegex("(?i)Failed to load (?:world|level)|level\\.dat.*(?:corrupt|failed)|region file.*(?:corrupt|invalid)|chunk.*corrupt|Exception reading .*level", RegexOptions.NonBacktracking)]
    private static partial Regex WorldPattern();

    [GeneratedRegex("(?i)No space left on device|disk (?:is )?full|not enough space on the disk", RegexOptions.NonBacktracking)]
    private static partial Regex DiskPattern();

    [GeneratedRegex("(?i)AccessDeniedException|UnauthorizedAccessException|Permission denied|The process cannot access the file", RegexOptions.NonBacktracking)]
    private static partial Regex PermissionPattern();

    [GeneratedRegex("(?i)(?:mod(?:id)?|from mod)[ \\t]*[:=][ \\t]*([a-z0-9_.-]{2,64})", RegexOptions.NonBacktracking)]
    private static partial Regex SuspectedModPattern();

    [GeneratedRegex("(?i)(\\\"?(?:api[-_ ]?key|access[-_ ]?token|password|secret|client[-_ ]?secret)\\\"?)[ \\t]*[:=][ \\t]*(?:\\\"[^\\\"\\r\\n]*\\\"|'[^'\\r\\n]*'|[^ \\t\\r\\n,}]+)", RegexOptions.NonBacktracking)]
    private static partial Regex SecretPattern();

    [GeneratedRegex("(?i)(\\bAuthorization[ \\t]*:[ \\t]*Bearer[ \\t]+)[A-Za-z0-9._~+/=-]+", RegexOptions.NonBacktracking)]
    private static partial Regex BearerPattern();

    [GeneratedRegex("(?i)(https?://)[^/@:\\s]+:[^/@\\s]+@", RegexOptions.NonBacktracking)]
    private static partial Regex UriUserInfoPattern();

    [GeneratedRegex("(?i)([?&](?:api[-_]?key|access[-_]?token|token|password|secret)=)[^&#\\s]+", RegexOptions.NonBacktracking)]
    private static partial Regex UriQuerySecretPattern();
}
