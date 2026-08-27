namespace MinecraftServerManager.App.Services;

public sealed class ApplicationPaths
{
    public const string ProductDirectoryName = "MCSV";
    public const string PublisherDirectoryName = "Muhun";

    public ApplicationPaths(string applicationRoot)
    {
        Root = Path.GetFullPath(applicationRoot);
        Servers = Path.Combine(Root, "servers");
        Runtimes = Path.Combine(Root, "runtimes");
        Backups = Path.Combine(Root, "backups");
        Cache = Path.Combine(Root, "cache");
        OnlineModpackArtworkCache = Path.Combine(Cache, "online-modpack-artwork");
        Themes = Path.Combine(Root, "themes");
        Logs = Path.Combine(Root, "logs");
        CrashReports = Path.Combine(Root, "crash-reports");
        RecoveryPoints = Path.Combine(Backups, "recovery-points");
        SettingsFile = Path.Combine(Root, "manager.json");
        LanguageSettingsFile = Path.Combine(Root, "language.json");
        RemoteSecurityFile = Path.Combine(Root, "remote-security.dat");
    }

    /// <summary>
    /// Resolves the writable per-user root used by the formally installed GUI.  The executable
    /// lives below Program Files and must never treat its immutable version directory as a data
    /// directory.  The optional root is intentionally exposed for deterministic contract tests.
    /// </summary>
    public static ApplicationPaths CreateForCurrentUser(string? localApplicationDataRoot = null)
    {
        var localRoot = string.IsNullOrWhiteSpace(localApplicationDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localApplicationDataRoot;
        if (string.IsNullOrWhiteSpace(localRoot) || !Path.IsPathFullyQualified(localRoot))
        {
            throw new InvalidOperationException("Windows LocalApplicationData is unavailable or invalid.");
        }

        var fullRoot = Path.GetFullPath(localRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(fullRoot)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (fullRoot.StartsWith(@"\\", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(volumeRoot) ||
            string.Equals(fullRoot, volumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Windows LocalApplicationData must be a non-root local directory.");
        }

        return new ApplicationPaths(Path.Combine(
            fullRoot,
            PublisherDirectoryName,
            ProductDirectoryName));
    }

    public string Root { get; }
    public string Servers { get; }
    public string Runtimes { get; }
    public string Backups { get; }
    public string Cache { get; }
    public string OnlineModpackArtworkCache { get; }
    public string Themes { get; }
    public string Logs { get; }
    public string CrashReports { get; }
    public string RecoveryPoints { get; }
    public string SettingsFile { get; }
    public string LanguageSettingsFile { get; }
    public string RemoteSecurityFile { get; }

    public void EnsureCreated()
    {
        foreach (var path in new[]
                 {
                     Root, Servers, Runtimes, Backups, Cache, OnlineModpackArtworkCache, Themes,
                     Logs, CrashReports, RecoveryPoints
                 })
        {
            Directory.CreateDirectory(path);
        }

        var probe = Path.Combine(Root, $".write-test-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException(
                $"GUI 使用者資料夾不可寫入：{Root}。請檢查目前 Windows 帳號的資料夾權限。",
                exception);
        }
    }
}
