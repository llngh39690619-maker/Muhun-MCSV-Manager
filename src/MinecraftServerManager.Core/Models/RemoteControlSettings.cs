namespace MinecraftServerManager.Core.Models;

/// <summary>
/// Persisted, non-secret settings for the formal mobile remote-control host.
/// Session cookies, SMTP credentials, and approved-account credentials are deliberately kept out of manager.json.
/// </summary>
public sealed class RemoteControlSettings
{
    public const int DefaultLocalPort = 39049;

    /// <summary>
    /// Whether the configured remote host should run. Formal 1.0 defaults this to enabled and
    /// preserves migration behavior so a completed setup reconnects whenever MCSV starts.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Exact Tailscale identity allowed to sign in. The backend compares this value using
    /// ordinal-ignore-case semantics; it never attempts Gmail dot or plus-tag normalization.
    /// </summary>
    public string AllowedLogin { get; set; } = string.Empty;

    /// <summary>Loopback Kestrel port proxied by Tailscale Serve.</summary>
    public int LocalPort { get; set; } = DefaultLocalPort;

    public RemoteAccessMode AccessMode { get; set; } = RemoteAccessMode.Tailscale;

    /// <summary>
    /// Optional absolute path selected on this computer. The manager never downloads, installs,
    /// updates, or registers cloudflared as a Windows service.
    /// </summary>
    public string CloudflaredExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Canonical fixed HTTPS origin assigned to a remotely-managed Cloudflare Named Tunnel.
    /// This is public routing metadata, never the connector token. The token is held only in
    /// the DPAPI-protected remote-security vault.
    /// </summary>
    public string CloudflareNamedPublicOrigin { get; set; } = string.Empty;

    public RemoteControlSettings Copy() => new()
    {
        Enabled = Enabled,
        AllowedLogin = AllowedLogin,
        LocalPort = LocalPort,
        AccessMode = AccessMode,
        CloudflaredExecutablePath = CloudflaredExecutablePath,
        CloudflareNamedPublicOrigin = CloudflareNamedPublicOrigin,
    };
}
