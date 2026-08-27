using MinecraftServerManager.Remote;

namespace MinecraftServerManager.App.Services;

internal static class CloudflareNamedTunnelConfiguration
{
    public static bool TryNormalizePublicOrigin(string? value, out Uri? publicOrigin)
    {
        publicOrigin = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate))
        {
            return false;
        }

        var options = new RemoteControlOptions
        {
            PublicOrigin = candidate,
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.CloudflareNamedTunnel
        };
        if (RemoteControlOptionsValidator.Validate(options).Count != 0)
        {
            return false;
        }

        publicOrigin = new Uri(candidate.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        return true;
    }
}
