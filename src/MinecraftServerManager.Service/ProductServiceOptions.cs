using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Service;

public sealed class ProductServiceOptions
{
    public const string SectionName = "Mcsv:Service";
    public const string WindowsServiceName = "MuhunMCSV";
    public const string WindowsServiceDisplayName = "Muhun MCSV Service";
    public const int DefaultPort = 39050;

    public int Port { get; init; } = DefaultPort;

    public string? DataRoot { get; init; }

    /// <summary>
    /// Administrator-provisioned exchange directory writable by both the desktop operator and
    /// the Service. It is intentionally outside the Service-owned data tree so staging access
    /// never grants desktop users access to secrets, registries, backups or live worlds.
    /// </summary>
    public string? ExchangeRoot { get; init; }

    /// <summary>
    /// Named-pipe endpoint used by the local desktop client. Production installations keep the
    /// protocol default; isolated diagnostics may choose a unique name so a signed candidate can
    /// be verified without stopping the active Service.
    /// </summary>
    public string IpcPipeName { get; init; } = ProductApiProtocol.IpcPackage;

    /// <summary>
    /// Development-only opt-in for console execution. Installed Windows Services always honor
    /// the durable Remote Web intent; tests and ad-hoc console builds never change Tailscale by
    /// merely starting the process.
    /// </summary>
    public bool EnableRemoteWebInConsole { get; init; }

    public ProductUpdateOptions Updates { get; init; } = new();
}

public static class ProductServiceOptionsValidator
{
    public static IReadOnlyList<string> Validate(ProductServiceOptions? options)
    {
        var errors = new List<string>();
        if (options is null)
        {
            errors.Add("Service options are required.");
            return errors.AsReadOnly();
        }

        if (options.Port is < 1024 or > 65535)
        {
            errors.Add("Service port must be between 1024 and 65535.");
        }

        if (!IsValidIpcPipeName(options.IpcPipeName))
        {
            errors.Add(
                "Service IPC pipe name must contain 1-128 ASCII letters, digits, dots, underscores or hyphens.");
        }

        if (!string.IsNullOrWhiteSpace(options.DataRoot))
        {
            ValidateLocalRoot(options.DataRoot, "data", errors);
        }

        if (!string.IsNullOrWhiteSpace(options.ExchangeRoot))
        {
            ValidateLocalRoot(options.ExchangeRoot, "exchange", errors);
        }

        if (!string.IsNullOrWhiteSpace(options.DataRoot) &&
            !string.IsNullOrWhiteSpace(options.ExchangeRoot) &&
            errors.Count == 0)
        {
            try
            {
                ProductManagedStorageLayout.ValidateSeparatedRoots(
                    options.DataRoot,
                    options.ExchangeRoot);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                errors.Add(exception.Message);
            }
        }

        errors.AddRange(ProductUpdateOptionsValidator.Validate(options.Updates));

        return errors.AsReadOnly();
    }

    internal static bool IsValidIpcPipeName(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length <= 128 &&
           value.All(character =>
               character is >= 'a' and <= 'z' or
                   >= 'A' and <= 'Z' or
                   >= '0' and <= '9' or
                   '.' or '_' or '-');

    private static bool ContainsExistingReparsePoint(string fullPath)
    {
        for (var current = new DirectoryInfo(fullPath); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateLocalRoot(
        string value,
        string label,
        ICollection<string> errors)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            errors.Add($"Service {label} root must be an absolute path.");
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(value);
            var pathRoot = Path.GetPathRoot(fullPath);
            var isRemoteOrDevicePath = fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                                       fullPath.StartsWith("//", StringComparison.Ordinal);
            if (isRemoteOrDevicePath)
            {
                errors.Add($"Service {label} root must be on a local drive, not a UNC or device path.");
            }

            if (string.Equals(
                    fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    pathRoot?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Service {label} root cannot be a drive root.");
            }

            if (!isRemoteOrDevicePath && File.Exists(fullPath))
            {
                errors.Add($"Service {label} root must be a directory, not a file.");
            }
            else if (!isRemoteOrDevicePath && ContainsExistingReparsePoint(fullPath))
            {
                errors.Add($"Service {label} root cannot traverse an existing reparse point.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"Service {label} root is not a valid absolute path.");
        }
    }

    public static void ValidateAndThrow(ProductServiceOptions options)
    {
        var errors = Validate(options);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }
    }
}
