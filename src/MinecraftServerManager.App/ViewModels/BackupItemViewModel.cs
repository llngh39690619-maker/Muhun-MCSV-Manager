using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.App.ViewModels;

public sealed class BackupItemViewModel
{
    public BackupItemViewModel(string path)
    {
        var file = new FileInfo(path);
        FilePath = file.FullName;
        FileName = file.Name;
        ArchiveBytes = file.Exists ? file.Length : 0;
        CreatedAt = file.Exists ? new DateTimeOffset(file.LastWriteTime) : null;
    }

    public BackupItemViewModel(ProductServerBackupSummary backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        BackupId = backup.BackupId;
        FileName = backup.FileName;
        ArchiveBytes = backup.ArchiveBytes;
        CreatedAt = backup.CreatedAtUtc.ToLocalTime();
        IsServiceOwned = true;
    }

    public string? BackupId { get; }
    public string? FilePath { get; }
    public string FileName { get; }
    public long ArchiveBytes { get; }
    public DateTimeOffset? CreatedAt { get; }
    public bool IsServiceOwned { get; }
    public string SizeDisplay => FormatBytes(ArchiveBytes);
    public string CreatedDisplay => CreatedAt?.ToString("yyyy/MM/dd HH:mm:ss") ?? "—";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(bytes, 0);
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {units[index]}";
    }
}
