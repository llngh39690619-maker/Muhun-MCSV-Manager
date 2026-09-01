using System.Text.Json;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Providers;

public interface IFtbInstalledServerValidator
{
    Task<ServerPackDetectionResult> ValidateAsync(
        string installationDirectory,
        int expectedPackId,
        int expectedVersionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates both the official FTB manifest identity and the existing safe ServerPackDetector
/// result. A zero installer exit code alone is not sufficient to declare an import successful.
/// </summary>
public sealed class FtbInstalledServerValidator(ServerPackDetector detector)
    : IFtbInstalledServerValidator
{
    private const long MaximumManifestBytes = 16L * 1024 * 1024;
    private readonly ServerPackDetector _detector = detector
        ?? throw new ArgumentNullException(nameof(detector));

    public async Task<ServerPackDetectionResult> ValidateAsync(
        string installationDirectory,
        int expectedPackId,
        int expectedVersionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationDirectory);
        if (expectedPackId <= 0 || expectedVersionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedPackId),
                "FTB Pack ID 與 Version ID 必須是正整數。");
        }

        var root = Path.GetFullPath(installationDirectory);
        var manifestPath = Path.Combine(root, ".manifest.json");
        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists)
        {
            throw new InvalidDataException("FTB Installer 結束後找不到 .manifest.json。");
        }

        if ((manifestInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("FTB .manifest.json 不得是 reparse point。");
        }

        if (manifestInfo.Length is < 2 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("FTB .manifest.json 大小無效。");
        }

        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                    stream,
                    new JsonDocumentOptions { MaxDepth = 64 },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("FTB .manifest.json 格式無效。", exception);
        }

        using (document)
        {
            var actualPackId = ReadRequiredInt(document.RootElement, "id");
            var actualVersionId = ReadRequiredInt(document.RootElement, "versionId");
            if (actualPackId != expectedPackId || actualVersionId != expectedVersionId)
            {
                throw new InvalidDataException(
                    "FTB 安裝結果與要求不符："
                    + $"預期 Pack {expectedPackId} / Version {expectedVersionId}，"
                    + $"實際 Pack {actualPackId} / Version {actualVersionId}。");
            }
        }

        var detection = await _detector.DetectAsync(root, cancellationToken).ConfigureAwait(false);
        if (!detection.IsRecognized || !detection.IsRunnable)
        {
            throw new InvalidDataException(
                "FTB 安裝完成但伺服器不可執行："
                + (detection.Error ?? "ServerPackDetector 未辨識出可執行啟動方式。"));
        }

        await OnlineServerPackSafetyValidator.ValidateAsync(detection, cancellationToken)
            .ConfigureAwait(false);

        return detection;
    }

    private static int ReadRequiredInt(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || !element.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"FTB .manifest.json 缺少整數欄位 {property}。");
        }

        return value;
    }
}

/// <summary>
/// Owns a brand-new staging directory for one FTB installation. Any process, cancellation, manifest
/// or detector failure removes that staging directory; existing directories are never overwritten.
/// </summary>
public sealed class FtbServerInstaller(
    IFtbInstallerProcessRunner processRunner,
    IFtbInstalledServerValidator installedServerValidator,
    FtbInstallerCommandBuilder? commandBuilder = null)
{
    private readonly IFtbInstallerProcessRunner _processRunner = processRunner
        ?? throw new ArgumentNullException(nameof(processRunner));
    private readonly IFtbInstalledServerValidator _installedServerValidator = installedServerValidator
        ?? throw new ArgumentNullException(nameof(installedServerValidator));
    private readonly FtbInstallerCommandBuilder _commandBuilder = commandBuilder ?? new();

    public async Task<FtbInstallResult> InstallAsync(
        FtbInstallRequest request,
        IProgress<FtbInstallerOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.MinecraftEulaAccepted)
        {
            // Reject before inspecting the executable, creating staging, or starting the official
            // installer. The command builder repeats this guard as defense in depth.
            throw new MinecraftEulaAcceptanceRequiredException();
        }

        if (request.PackId <= 0 || request.VersionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "FTB Pack ID 與 Version ID 必須是正整數。");
        }

        var installerPath = Path.GetFullPath(request.InstallerPath);
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("找不到已驗證的 FTB Installer。", installerPath);
        }

        var installationDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(request.InstallationDirectory));
        if (installationDirectory.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetPathRoot(installationDirectory) ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("FTB 安裝 staging 不得是磁碟根目錄。");
        }

        if (File.Exists(installationDirectory) || Directory.Exists(installationDirectory))
        {
            throw new IOException(
                $"FTB 安裝 staging 已存在；為避免覆寫或混用模組包，已取消：{installationDirectory}");
        }

        var parent = Path.GetDirectoryName(installationDirectory)
            ?? throw new InvalidOperationException("FTB 安裝 staging 沒有父目錄。");
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException($"FTB 安裝 staging 的父目錄不存在：{parent}");
        }

        Directory.CreateDirectory(installationDirectory);
        var ownsDirectory = true;
        try
        {
            var normalizedRequest = request with
            {
                InstallerPath = installerPath,
                InstallationDirectory = installationDirectory,
            };
            var startInfo = _commandBuilder.Build(normalizedRequest);
            var processResult = await _processRunner.RunAsync(startInfo, output, cancellationToken)
                .ConfigureAwait(false);
            var detection = await _installedServerValidator.ValidateAsync(
                    installationDirectory,
                    request.PackId,
                    request.VersionId,
                    cancellationToken)
                .ConfigureAwait(false);

            ownsDirectory = false;
            return new FtbInstallResult(installationDirectory, detection, processResult);
        }
        finally
        {
            if (ownsDirectory)
            {
                TryDeleteOwnedDirectory(installationDirectory);
            }
        }
    }

    private static void TryDeleteOwnedDirectory(string path)
    {
        try
        {
            var parent = Directory.GetParent(Path.GetFullPath(path))?.FullName
                ?? throw new InvalidOperationException("FTB staging 沒有安全的父目錄。");
            SafePath.DeleteTreeWithoutFollowingReparsePoints(parent, path);
        }
        catch
        {
            // The process/validation exception remains the primary failure. A caller can surface the
            // retained staging path for manual cleanup if Windows still has a file handle open.
        }
    }
}
