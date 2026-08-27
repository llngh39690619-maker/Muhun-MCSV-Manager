using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class CrashDiagnosticServiceTests
{
    [Fact]
    public void Analyze_ClassifiesKnownFailuresAndDoesNotClaimUnsafeAutomaticRepair()
    {
        using var temporary = new TemporaryDirectory();
        var java = Path.Combine(temporary.Path, "java.exe");
        var jar = Path.Combine(temporary.Path, "server.jar");
        File.WriteAllBytes(java, [1]);
        File.WriteAllBytes(jar, [1]);
        var instance = CreateInstance(temporary.Path, java, jar);
        var lines = new[]
        {
            Line("java.lang.OutOfMemoryError: Java heap space"),
            Line("org.spongepowered.asm.mixin.throwables.MixinApplyError from mod: example_mod"),
            Line("Failed to bind to port! Address already in use")
        };

        var report = new CrashDiagnosticService().Analyze(new CrashDiagnosticInput(
            instance,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "UnexpectedExit",
            1,
            null,
            lines));

        Assert.Contains(report.Findings, finding => finding.Category == CrashCauseCategory.OutOfMemory);
        Assert.Contains(report.Findings, finding => finding.Category == CrashCauseCategory.ModOrMixinFailure);
        Assert.Contains(report.Findings, finding => finding.Code == "PORT_BIND_FAILED" && finding.IsSafeForAutomaticRepair);
        Assert.Contains("example_mod", report.SuspectedModIds, StringComparer.OrdinalIgnoreCase);
        Assert.All(
            report.Findings.Where(finding => finding.Category != CrashCauseCategory.PortConflict),
            finding => Assert.False(finding.IsSafeForAutomaticRepair));
    }

    [Fact]
    public void Analyze_ReportsMissingLaunchFilesDuringSelfCheck()
    {
        using var temporary = new TemporaryDirectory();
        var instance = CreateInstance(
            temporary.Path,
            Path.Combine(temporary.Path, "missing-java.exe"),
            Path.Combine(temporary.Path, "missing-server.jar"));

        var report = new CrashDiagnosticService().Analyze(new CrashDiagnosticInput(
            instance,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "UnexpectedExit",
            1,
            null,
            []));

        Assert.Contains(report.Findings, finding => finding.Code == "JAVA_EXECUTABLE_MISSING");
        Assert.Contains(report.Findings, finding => finding.Code == "SERVER_JAR_MISSING");
    }

    [Fact]
    public async Task CreateReportAsync_WritesBoundedArtifactsAndRedactsSecrets()
    {
        using var temporary = new TemporaryDirectory();
        var serverRoot = Path.Combine(temporary.Path, "伺服器 空白");
        var reportRoot = Path.Combine(temporary.Path, "crash-reports");
        Directory.CreateDirectory(serverRoot);
        var java = Path.Combine(serverRoot, "java.exe");
        var jar = Path.Combine(serverRoot, "server.jar");
        File.WriteAllBytes(java, [1]);
        File.WriteAllBytes(jar, [1]);
        var instance = CreateInstance(serverRoot, java, jar);
        var session = Guid.NewGuid();

        var artifacts = await new CrashDiagnosticService().CreateReportAsync(
            reportRoot,
            new CrashDiagnosticInput(
                instance,
                session,
                new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
                "WatchdogUnresponsive",
                null,
                new IOException(
                    "watchdog password=error-secret Authorization: Bearer error-bearer "
                    + "https://user:error-uri@example.invalid/?access_token=error-query"),
                [
                    Line("api_key=do-not-write-this"),
                    Line("{\"password\":\"json-secret\"}"),
                    Line("Authorization: Bearer console-bearer"),
                    Line("https://user:console-uri@example.invalid/?token=console-query"),
                    Line("A single server tick took 120 seconds")
                ],
                LastHealthyRecoveryPoint: "healthy-001.zip"));

        Assert.True(File.Exists(artifacts.MarkdownPath));
        Assert.True(File.Exists(artifacts.JsonPath));
        Assert.True(File.Exists(artifacts.ConsoleTailPath));
        Assert.True(SafePath.IsWithinRoot(reportRoot, artifacts.ReportDirectory));
        Assert.Contains(artifacts.Report.Findings, finding => finding.Category == CrashCauseCategory.WatchdogHang);
        var console = await File.ReadAllTextAsync(artifacts.ConsoleTailPath);
        Assert.DoesNotContain("do-not-write-this", console, StringComparison.Ordinal);
        Assert.DoesNotContain("json-secret", console, StringComparison.Ordinal);
        Assert.DoesNotContain("console-bearer", console, StringComparison.Ordinal);
        Assert.DoesNotContain("console-uri", console, StringComparison.Ordinal);
        Assert.DoesNotContain("console-query", console, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", console, StringComparison.Ordinal);
        var json = await File.ReadAllTextAsync(artifacts.JsonPath);
        Assert.DoesNotContain("error-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("error-bearer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("error-uri", json, StringComparison.Ordinal);
        Assert.DoesNotContain("error-query", json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
        var markdown = await File.ReadAllTextAsync(artifacts.MarkdownPath);
        Assert.Contains("健康恢復點", markdown, StringComparison.Ordinal);
        Assert.Contains("不會自動刪除模組", markdown, StringComparison.Ordinal);
    }

    private static ConsoleLine Line(string text)
        => new(DateTimeOffset.UtcNow, text, ConsoleStream.StandardError);

    private static ServerInstance CreateInstance(string directory, string java, string jar)
        => new()
        {
            Name = "測試 Server",
            DirectoryPath = directory,
            JavaExecutablePath = java,
            ServerJarPath = jar,
            MinecraftVersion = "1.20.1",
            JavaMajorVersion = 17,
            CoreType = CoreType.Forge
        };
}
