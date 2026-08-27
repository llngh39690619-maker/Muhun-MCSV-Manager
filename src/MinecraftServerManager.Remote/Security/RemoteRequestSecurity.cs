using Microsoft.AspNetCore.Http;

namespace MinecraftServerManager.Remote;

public static class RemoteRequestSecurity
{
    public static bool HasExactMutationOrigin(HttpRequest request, Uri publicOrigin)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publicOrigin);

        if (!request.Headers.TryGetValue("Origin", out var origins) || origins.Count != 1)
        {
            return false;
        }

        return HasExactMutationOrigin(origins[0], request.Host.Value, publicOrigin);
    }

    public static bool HasExactMutationOrigin(string? origin, string? host, Uri publicOrigin)
    {
        ArgumentNullException.ThrowIfNull(publicOrigin);
        if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(host))
        {
            return false;
        }

        var expectedOrigin = publicOrigin.GetLeftPart(UriPartial.Authority);
        return string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(host, publicOrigin.Authority, StringComparison.OrdinalIgnoreCase);
    }
}
