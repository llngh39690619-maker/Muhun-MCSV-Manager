using System.Security.Principal;

namespace MinecraftServerManager.Installer;

internal sealed record InstallerLayout(
    string Root,
    string Channel,
    string VersionsRoot,
    string ActivationRoot,
    string ServiceRoot,
    string ExchangeRoot,
    string UserRoot,
    string StagingRoot,
    string LauncherRoot)
{
    internal const string InstallMarkerName = ".muhun-mcsv-install-root";
    internal const string InstallMarkerValue = "muhun.mcsv.manager:1";

    public static string DefaultRoot
    {
        get
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                programFiles = @"C:\Program Files";
            }

            return Path.Combine(programFiles, "MCSV");
        }
    }

    public static InstallerLayout Resolve(string root, string channel, string? userSid = null)
    {
        var normalizedRoot = ValidateRoot(root);
        if (channel is not ("beta" or "stable"))
        {
            throw new ArgumentOutOfRangeException(nameof(channel), "安裝通道只允許 beta 或 stable。");
        }

        var sid = userSid;
        if (string.IsNullOrWhiteSpace(sid))
        {
            using var identity = WindowsIdentity.GetCurrent();
            sid = identity.User?.Value;
        }

        if (string.IsNullOrWhiteSpace(sid))
        {
            throw new InvalidOperationException("無法取得目前 Windows 使用者 SID。");
        }

        _ = new SecurityIdentifier(sid);
        return new InstallerLayout(
            normalizedRoot,
            channel,
            Path.Combine(normalizedRoot, "versions"),
            Path.Combine(normalizedRoot, "activation-state"),
            Path.Combine(normalizedRoot, "service", channel),
            Path.Combine(normalizedRoot, "exchange", channel),
            Path.Combine(normalizedRoot, "users", sid, channel),
            Path.Combine(normalizedRoot, "install-staging"),
            Path.Combine(normalizedRoot, "launcher"));
    }

    internal static string ValidateRoot(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("X MCSV 安裝程式僅支援 Windows x64。");
        }

        RejectPreservedDataTreePath(root);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("安裝位置必須是完整的本機路徑。", nameof(root));
        }

        var normalized = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (normalized.Length > 220 || normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("安裝位置過長或不是本機磁碟。", nameof(root));
        }

        var volumeRoot = Path.GetPathRoot(normalized)
            ?? throw new ArgumentException("安裝位置沒有有效磁碟區。", nameof(root));
        if (string.Equals(
                normalized,
                volumeRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("不可直接安裝在磁碟根目錄。", nameof(root));
        }

        var drive = new DriveInfo(volumeRoot);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed ||
            !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("安裝位置必須位於已就緒的本機 NTFS 固定磁碟。", nameof(root));
        }

        RejectExistingReparsePoints(normalized);
        return normalized;
    }

    /// <summary>
    /// Rejects the preserved legacy data tree using path-string operations only. This method must
    /// stay ahead of every DriveInfo, DirectoryInfo, File or Directory call in ValidateRoot so a
    /// rejected spelling can never probe the protected volume. The optional base path exists for
    /// deterministic tests of relative paths and is likewise normalized without filesystem I/O.
    /// </summary>
    internal static void RejectPreservedDataTreePath(string? path, string? basePath = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var rawPath = path.Trim();
        RejectWindowsDeviceOrUncSyntax(rawPath, nameof(path));
        string fullPath;
        try
        {
            if (Path.IsPathFullyQualified(rawPath))
            {
                fullPath = Path.GetFullPath(rawPath);
            }
            else
            {
                var lexicalBase = string.IsNullOrWhiteSpace(basePath)
                    ? Environment.CurrentDirectory
                    : basePath.Trim();
                RejectWindowsDeviceOrUncSyntax(lexicalBase, nameof(basePath));
                fullPath = Path.GetFullPath(rawPath, Path.GetFullPath(lexicalBase));
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("安裝位置不是可安全正規化的 Windows 路徑。", nameof(path), exception);
        }

        RejectWindowsDeviceOrUncSyntax(fullPath, nameof(path));
        var protectionIdentity = NormalizeWindowsSegmentsForProtection(fullPath);
        const string preservedDataRoot = @"D:\MCSV";
        if (string.Equals(protectionIdentity, preservedDataRoot, StringComparison.OrdinalIgnoreCase) ||
            protectionIdentity.StartsWith(
                preservedDataRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "D:\\MCSV 與其子目錄是目前保留中的既有資料，安裝程式不會讀取、覆寫或清理它。",
                nameof(path));
        }
    }

    private static void RejectWindowsDeviceOrUncSyntax(string path, string parameterName)
    {
        var windowsPath = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (windowsPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            windowsPath.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase) ||
            windowsPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("安裝位置不可使用 UNC 或 Windows 裝置路徑。", parameterName);
        }
    }

    private static string NormalizeWindowsSegmentsForProtection(string path)
    {
        var fullPath = Path.GetFullPath(path)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("安裝位置沒有有效磁碟區。", nameof(path));
        var segments = fullPath[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.TrimEnd(' ', '.'))
            .ToArray();
        if (segments.Any(string.IsNullOrEmpty) || segments.Any(segment => segment.Contains(':')))
        {
            throw new ArgumentException("安裝位置含有不安全的 Windows 路徑片段。", nameof(path));
        }

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar);
        return segments.Length == 0
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);
    }

    internal static void RejectExistingReparsePoints(string path)
    {
        for (var cursor = new DirectoryInfo(Path.GetFullPath(path)); cursor is not null; cursor = cursor.Parent)
        {
            if (cursor.Exists && cursor.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("安裝路徑不可穿越連結或 reparse point。");
            }
        }
    }
}
