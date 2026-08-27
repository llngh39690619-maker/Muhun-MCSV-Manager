namespace MinecraftServerManager.Service;

public sealed class ProductDataLayout
{
    public ProductDataLayout(string root)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("Product data root must be an absolute path.", nameof(root));
        }

        Root = Path.GetFullPath(root);
        Data = Path.Combine(Root, "data");
        Secrets = Path.Combine(Root, "secrets");
        Operations = Path.Combine(Root, "operations");
        Imports = Path.Combine(Root, "imports");
        Servers = Path.Combine(Root, "servers");
        Runtimes = Path.Combine(Root, "runtimes");
        Backups = Path.Combine(Root, "backups");
        Updates = Path.Combine(Root, "updates");
        Plugins = Path.Combine(Root, "plugins");
        Logs = Path.Combine(Root, "logs");
    }

    public string Root { get; }
    public string Data { get; }
    public string Secrets { get; }
    public string Operations { get; }
    public string Imports { get; }
    public string Servers { get; }
    public string Runtimes { get; }
    public string Backups { get; }
    public string Updates { get; }
    public string Plugins { get; }
    public string Logs { get; }

    public static ProductDataLayout FromOptions(ProductServiceOptions options)
    {
        ProductServiceOptionsValidator.ValidateAndThrow(options);
        var root = string.IsNullOrWhiteSpace(options.DataRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Muhun",
                "MCSV")
            : options.DataRoot;
        return new ProductDataLayout(root);
    }

    public void EnsureCreated()
    {
        foreach (var directory in new[]
                 {
                     Root,
                     Data,
                     Secrets,
                     Operations,
                     Imports,
                     Servers,
                     Runtimes,
                     Backups,
                     Updates,
                     Plugins,
                     Logs,
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
