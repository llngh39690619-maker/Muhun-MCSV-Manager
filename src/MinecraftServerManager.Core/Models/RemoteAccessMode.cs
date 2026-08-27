namespace MinecraftServerManager.Core.Models;

public enum RemoteAccessMode
{
    Tailscale = 0,
    CloudflareQuickTunnel = 1,
    CloudflareNamedTunnel = 2,
    TailscaleFunnel = 3,
}
