using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Client packs are intentionally stricter than the general mrpack allow-list: every package and
/// manifest file must come directly from Modrinth's official HTTPS CDN, and redirects are denied.
/// </summary>
public sealed class OfficialModrinthClientDownloadUriPolicy : IModrinthModpackUriPolicy
{
    public void EnsureAllowed(Uri uri, bool isRedirect)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (isRedirect || !ModrinthClientModpackCatalog.IsOfficialCdnUri(uri))
        {
            throw new InvalidDataException(
                $"Only direct downloads from the official Modrinth HTTPS CDN are allowed: {uri}");
        }
    }
}
