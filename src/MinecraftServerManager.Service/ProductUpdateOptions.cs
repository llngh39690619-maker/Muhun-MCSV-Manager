namespace MinecraftServerManager.Service;

public sealed class ProductUpdateOptions
{
    public string? StableManifestUrl { get; init; }

    public string? BetaManifestUrl { get; init; }

    public IReadOnlyList<string> AllowedFeedHosts { get; init; } = [];

    public string? PublicKeyDocumentPath { get; init; }
}

public static class ProductUpdateOptionsValidator
{
    public static IReadOnlyList<string> Validate(ProductUpdateOptions? options)
    {
        var errors = new List<string>();
        if (options is null)
        {
            errors.Add("Update options are required.");
            return errors.AsReadOnly();
        }

        if (options.AllowedFeedHosts.Count > 8)
        {
            errors.Add("At most eight exact update feed hosts may be configured.");
        }

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in options.AllowedFeedHosts)
        {
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253 ||
                Uri.CheckHostName(host) != UriHostNameType.Dns || !hosts.Add(host))
            {
                errors.Add("Every update feed host must be a unique exact DNS host name.");
                break;
            }
        }

        ValidateFeed(options.StableManifestUrl, "stable", hosts, errors);
        ValidateFeed(options.BetaManifestUrl, "beta", hosts, errors);
        if (!string.IsNullOrWhiteSpace(options.PublicKeyDocumentPath) &&
            !Path.IsPathFullyQualified(options.PublicKeyDocumentPath))
        {
            errors.Add("Update public-key document path must be absolute.");
        }

        return errors.AsReadOnly();
    }

    private static void ValidateFeed(
        string? value,
        string name,
        IReadOnlySet<string> hosts,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query) ||
            !hosts.Contains(uri.IdnHost))
        {
            errors.Add($"The {name} update manifest must be an exact allowlisted default-port HTTPS URL.");
        }
    }
}
