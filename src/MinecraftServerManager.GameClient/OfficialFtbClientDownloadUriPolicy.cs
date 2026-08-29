using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Accepts official FTB manifest origins and the single Forge CDN media host used by its checked
/// redirect. The redirect host is deliberately not a valid initial manifest origin.
/// </summary>
public sealed class OfficialFtbClientDownloadUriPolicy : IModrinthModpackUriPolicy
{
    private static readonly IReadOnlySet<string> InitialHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "files.feed-the-beast.com",
            "cdn.feed-the-beast.com",
            "edge.forgecdn.net",
        };
    private const string ForgeCdnRedirectHost = "mediafilez.forgecdn.net";

    public void EnsureAllowed(Uri uri, bool isRedirect)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var host = uri.IsAbsoluteUri ? uri.IdnHost.TrimEnd('.') : string.Empty;
        var allowedHost = isRedirect
            ? host.Equals(ForgeCdnRedirectHost, StringComparison.OrdinalIgnoreCase)
            : InitialHosts.Contains(host);
        if (!uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !allowedHost)
        {
            throw new InvalidDataException(
                $"Only approved FTB HTTPS artifact origins and their exact Forge CDN redirect are allowed: {uri}");
        }
    }
}
