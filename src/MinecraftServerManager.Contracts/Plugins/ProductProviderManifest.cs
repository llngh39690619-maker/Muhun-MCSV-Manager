using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MinecraftServerManager.Contracts.Notifications;

namespace MinecraftServerManager.Contracts.Plugins;

public static class ProductProviderCapabilities
{
    public const string Notification = "notification";
    public const string ModpackCatalog = "modpack.catalog";
    public const string ServerCoreCatalog = "server-core.catalog";
    public const string RuntimeCatalog = "runtime.catalog";
    public const string Tunnel = "tunnel";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Notification,
        ModpackCatalog,
        ServerCoreCatalog,
        RuntimeCatalog,
        Tunnel,
    };
}

public static class ProductProviderPermissions
{
    public const string Http = "provider.http";
    public const string ReadConfiguration = "provider.config.read";
    public const string WriteState = "provider.state.write";
    public const string EmitNotifications = "provider.notification.emit";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Http,
        ReadConfiguration,
        WriteState,
        EmitNotifications,
    };
}

public sealed record ProductProviderManifest(
    int SchemaVersion,
    string Id,
    string DisplayName,
    string Version,
    ProductApiVersion ApiVersion,
    string EntryPoint,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> NetworkHosts,
    IReadOnlyDictionary<string, string> FileSha256);

public static partial class ProductProviderManifestValidator
{
    // Schema 2 makes the signed payload-file digest table mandatory. Schema 1 packages
    // cannot be installed because they cannot be checked for post-install tampering.
    public const int CurrentSchemaVersion = 2;
    public const int MaximumCapabilities = 16;
    public const int MaximumPermissions = 16;
    public const int MaximumNetworkHosts = 32;
    public const int MaximumFiles = 4096;

    public static ProductContractValidationResult Validate(ProductProviderManifest? manifest)
    {
        var errors = new List<string>();
        if (manifest is null)
        {
            errors.Add("Provider manifest is required.");
            return new ProductContractValidationResult(errors.AsReadOnly());
        }

        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add("Unsupported provider manifest schema version.");
        }

        if (!ProviderIdPattern().IsMatch(manifest.Id ?? string.Empty))
        {
            errors.Add("Provider id is invalid.");
        }

        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || manifest.DisplayName.Length > 80 ||
            manifest.DisplayName.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            errors.Add("Provider display name is invalid.");
        }

        if (manifest.Version is null || manifest.Version.Length > 96 ||
            !SemanticVersionPattern().IsMatch(manifest.Version))
        {
            errors.Add("Provider version must be a canonical semantic version.");
        }

        if (manifest.ApiVersion.Major != ProductApiProtocol.CurrentVersion.Major ||
            manifest.ApiVersion.CompareTo(ProductApiProtocol.MinimumSupportedVersion) < 0 ||
            manifest.ApiVersion.CompareTo(ProductApiProtocol.CurrentVersion) > 0)
        {
            errors.Add("Provider API version is outside this host's supported range.");
        }

        if (!IsSafeEntryPoint(manifest.EntryPoint))
        {
            errors.Add("Provider entry point must be a relative executable path contained by its package.");
        }

        ValidateSet(
            manifest.Capabilities,
            ProductProviderCapabilities.All,
            MaximumCapabilities,
            "capability",
            errors);
        ValidateSet(
            manifest.Permissions,
            ProductProviderPermissions.All,
            MaximumPermissions,
            "permission",
            errors);
        ValidateCapabilityPermissions(manifest, errors);

        if (manifest.NetworkHosts is null || manifest.NetworkHosts.Count > MaximumNetworkHosts)
        {
            errors.Add("Provider network host list is missing or too large.");
        }
        else if (manifest.NetworkHosts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.NetworkHosts.Count)
        {
            errors.Add("Provider network host list contains duplicates.");
        }
        else
        {
            foreach (var host in manifest.NetworkHosts)
            {
                if (string.IsNullOrWhiteSpace(host) ||
                    host.Length > 253 ||
                    host.Any(character => character > 0x7f || char.IsUpper(character)) ||
                    host.Contains('*') ||
                    host.Contains('/') ||
                    host.Contains(':') ||
                    host.EndsWith('.') ||
                    Uri.CheckHostName(host) != UriHostNameType.Dns ||
                    IPAddress.TryParse(host, out _))
                {
                    errors.Add("Provider network hosts must be exact DNS names without wildcards or ports.");
                    break;
                }
            }
        }

        if (manifest.Permissions?.Contains(ProductProviderPermissions.Http, StringComparer.Ordinal) == true &&
            (manifest.NetworkHosts is null || manifest.NetworkHosts.Count == 0))
        {
            errors.Add("HTTP permission requires at least one exact network host.");
        }

        if (manifest.Permissions?.Contains(ProductProviderPermissions.Http, StringComparer.Ordinal) != true &&
            manifest.NetworkHosts?.Count > 0)
        {
            errors.Add("Network hosts cannot be declared without HTTP permission.");
        }

        ValidateFileDigests(manifest, errors);

        return errors.Count == 0
            ? ProductContractValidationResult.Success
            : new ProductContractValidationResult(errors.AsReadOnly());
    }

    private static void ValidateCapabilityPermissions(
        ProductProviderManifest manifest,
        ICollection<string> errors)
    {
        if (manifest.Capabilities is null || manifest.Permissions is null)
        {
            return;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in manifest.Capabilities)
        {
            switch (capability)
            {
                case ProductProviderCapabilities.Notification:
                    required.Add(ProductProviderPermissions.Http);
                    required.Add(ProductProviderPermissions.EmitNotifications);
                    break;
                case ProductProviderCapabilities.ModpackCatalog:
                case ProductProviderCapabilities.ServerCoreCatalog:
                case ProductProviderCapabilities.RuntimeCatalog:
                    required.Add(ProductProviderPermissions.Http);
                    break;
                case ProductProviderCapabilities.Tunnel:
                    required.Add(ProductProviderPermissions.Http);
                    required.Add(ProductProviderPermissions.WriteState);
                    break;
            }
        }

        if (required.Any(permission => !manifest.Permissions.Contains(permission, StringComparer.Ordinal)))
        {
            errors.Add("Provider capability declarations are missing required permissions.");
        }

        if (manifest.Permissions.Contains(ProductProviderPermissions.EmitNotifications, StringComparer.Ordinal) &&
            !manifest.Capabilities.Contains(ProductProviderCapabilities.Notification, StringComparer.Ordinal))
        {
            errors.Add("Notification emission permission requires the notification capability.");
        }
    }

    private static void ValidateFileDigests(
        ProductProviderManifest manifest,
        ICollection<string> errors)
    {
        if (manifest.FileSha256 is null || manifest.FileSha256.Count is < 1 or > MaximumFiles)
        {
            errors.Add("Provider file digest table is missing, empty, or too large.");
            return;
        }

        var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, digest) in manifest.FileSha256)
        {
            if (!IsSafePayloadPath(path) ||
                !normalizedPaths.Add(path.Normalize(NormalizationForm.FormC)) ||
                path.Equals("provider.manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Provider file digest table contains an unsafe or duplicate path.");
                return;
            }

            if (digest is null || digest.Length != 64 ||
                digest.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            {
                errors.Add("Provider file digest table contains a non-canonical SHA-256 digest.");
                return;
            }
        }

        if (!manifest.FileSha256.Keys.Any(path =>
                path.Equals(manifest.EntryPoint, StringComparison.Ordinal)))
        {
            errors.Add("Provider file digest table must include the exact entry point path.");
        }
    }

    private static void ValidateSet(
        IReadOnlyList<string>? values,
        IReadOnlySet<string> allowed,
        int maximumCount,
        string label,
        ICollection<string> errors)
    {
        if (values is null || values.Count == 0 || values.Count > maximumCount)
        {
            errors.Add($"Provider {label} list is missing, empty, or too large.");
            return;
        }

        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            errors.Add($"Provider {label} list contains duplicates.");
            return;
        }

        if (values.Any(value => !allowed.Contains(value)))
        {
            errors.Add($"Provider {label} list contains an unsupported value.");
        }
    }

    private static bool IsSafeEntryPoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || Path.IsPathRooted(value) ||
            value.Contains('\\'))
        {
            return false;
        }

        if (!string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsSafePayloadPath(value);
    }

    private static bool IsSafePayloadPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Contains('\\') ||
            value.StartsWith('/') || value.Contains(':') ||
            value.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            return false;
        }

        var segments = value.Split('/');
        if (segments.Any(segment => segment.Length is 0 or > 255 || segment is "." or ".." ||
                                    segment != segment.Normalize(NormalizationForm.FormC) ||
                                    segment.EndsWith(' ') || segment.EndsWith('.') ||
                                    segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0))
        {
            return false;
        }

        return segments.All(segment => !ReservedWindowsNamePattern().IsMatch(segment.Split('.')[0]));
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[.-][a-z0-9][a-z0-9-]{0,30}){1,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderIdPattern();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-(?:(?:0|[1-9][0-9]*)|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:(?:0|[1-9][0-9]*)|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();

    [GeneratedRegex("^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedWindowsNamePattern();
}
