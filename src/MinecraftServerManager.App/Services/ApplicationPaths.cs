namespace MinecraftServerManager.App.Services;

public sealed class ApplicationPaths
{
    public const string ProductDirectoryName = "MCSV";
    public const string PublisherDirectoryName = "Muhun";

    public ApplicationPaths(string applicationRoot)
        : this(
            applicationRoot,
            installRoot: null,
            channel: null,
            currentUserSid: null,
            productExchangeRoot: Path.Combine(Path.GetFullPath(applicationRoot), "exchange"))
    {
    }

    private ApplicationPaths(
        string applicationRoot,
        string? installRoot,
        string? channel,
        string? currentUserSid,
        string productExchangeRoot)
    {
        Root = Path.GetFullPath(applicationRoot);
        InstallRoot = installRoot is null ? null : Path.GetFullPath(installRoot);
        Channel = channel;
        CurrentUserSid = currentUserSid;
        ProductExchangeRoot = Path.GetFullPath(productExchangeRoot);
        Servers = Path.Combine(Root, "servers");
        ClientRoot = Path.Combine(Root, "client");
        Clients = Path.Combine(ClientRoot, "instances");
        ClientCache = Path.Combine(ClientRoot, "cache");
        ClientCatalogCache = Path.Combine(ClientCache, "catalog");
        ClientOperations = Path.Combine(ClientRoot, "operations");
        ClientStaging = Path.Combine(ClientRoot, "staging");
        ClientSecrets = Path.Combine(ClientRoot, "secrets");
        ClientRegistryFile = Path.Combine(ClientRoot, "client-instances.v1.json");
        BedrockShortcutRegistryFile = Path.Combine(ClientRoot, "bedrock-shortcuts.v1.json");
        Runtimes = Path.Combine(Root, "runtimes");
        ClientRuntimes = Path.Combine(ClientRoot, "runtimes");
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
    /// Resolves the writable per-user root used by a managed installation. The install root is
    /// derived from the active, marker-bound executable instead of an ambient profile folder or
    /// current working directory. There is deliberately no LocalAppData/ProgramData fallback.
    /// </summary>
    public static ApplicationPaths CreateForCurrentInstallation(
        string? guiExecutablePath = null,
        string? currentUserSid = null)
    {
        var binding = ManagedGuiDataRootResolver.Resolve(
            guiExecutablePath ?? Environment.ProcessPath,
            currentUserSid);
        return new ApplicationPaths(
            binding.UserDataRoot,
            binding.InstallRoot,
            binding.Channel,
            binding.CurrentUserSid,
            binding.ProductExchangeRoot);
    }

    public string Root { get; }
    public string? InstallRoot { get; }
    public string? Channel { get; }
    public string? CurrentUserSid { get; }
    public bool IsManagedInstallation => InstallRoot is not null;
    public string ProductExchangeRoot { get; }
    public string Servers { get; }
    public string ClientRoot { get; }
    public string Clients { get; }
    public string ClientCache { get; }
    public string ClientCatalogCache { get; }
    public string ClientOperations { get; }
    public string ClientStaging { get; }
    public string ClientSecrets { get; }
    public string ClientRegistryFile { get; }
    public string BedrockShortcutRegistryFile { get; }
    public string Runtimes { get; }
    public string ClientRuntimes { get; }
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
        var directories = new[]
        {
            Root, ProductExchangeRoot, Servers, ClientRoot, Clients, ClientCache,
            ClientCatalogCache, ClientOperations, ClientStaging, ClientSecrets,
            ClientRuntimes, Runtimes, Backups, Cache, OnlineModpackArtworkCache,
            Themes, Logs, CrashReports, RecoveryPoints
        };
        foreach (var path in directories)
        {
            if (InstallRoot is null)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                ManagedGuiDataRootResolver.EnsureDirectoryUnderInstallRoot(InstallRoot, path);
            }
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
