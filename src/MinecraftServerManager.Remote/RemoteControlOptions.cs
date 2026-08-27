namespace MinecraftServerManager.Remote;

public enum RemoteIngressMode
{
    TailscaleServe,
    CloudflareQuickTunnel,
    CloudflareNamedTunnel,
    TailscaleFunnel,
}

/// <summary>
/// Configuration for the loopback-only remote-control host. The public origin is
/// the HTTPS origin exposed by Tailscale Serve; Kestrel itself never listens on a
/// LAN or public interface.
/// </summary>
public sealed class RemoteControlOptions
{
    public const string DefaultSessionCookieName = "__Host-MCSV-Remote";
    public const string DefaultRememberedDeviceCookieName = "__Host-MCSV-Device";
    public const string CsrfHeaderName = "X-MCSV-CSRF";
    public const string CultureHeaderName = "X-MCSV-Culture";
    public const string TailscaleLoginHeaderName = "Tailscale-User-Login";
    public const string CloudflareConnectingIpHeaderName = "CF-Connecting-IP";
    public const string IdempotencyHeaderName = "Idempotency-Key";
    public const string PublicTunnelCredentialSubject = "mcsv-local-approved-account";
    public const string QuickTunnelCredentialSubject = PublicTunnelCredentialSubject;

    public int Port { get; init; } = 42871;

    public required Uri PublicOrigin { get; init; }

    public required IReadOnlyCollection<string> AllowedGoogleLogins { get; init; }

    /// <summary>
    /// Tailscale supplies a verified identity header. Quick Tunnel is public and therefore ignores
    /// every external identity header and uses a fixed local credential subject. No Gmail identity
    /// or email verification participates in Quick Tunnel authentication.
    /// </summary>
    public RemoteIngressMode IngressMode { get; init; } = RemoteIngressMode.TailscaleServe;

    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(12);

    public int DefaultConsolePageSize { get; init; } = 150;

    public int MaximumConsolePageSize { get; init; } = 500;

    public int MaximumCommandLength { get; init; } = 512;

    public int MaximumConsoleLineCharacters { get; init; } = 4096;

    public int LoginAttemptsPerMinute { get; init; } = 5;

    public int ReadRequestsPerMinute { get; init; } = 180;

    public int MutationRequestsPerMinute { get; init; } = 30;

    public int GlobalRequestsPerMinute { get; init; } = 600;

    public int MaximumSessions { get; init; } = 16;

    public TimeSpan IdempotencyLifetime { get; init; } = TimeSpan.FromMinutes(15);

    public int MaximumIdempotencyEntries { get; init; } = 1024;

    /// <summary>
    /// Lifetime of accepted backend mutations. The desktop supplies its real application-shutdown
    /// token so stopping or reconfiguring only the embedded web host cannot interrupt a restart.
    /// </summary>
    public CancellationToken OperationCancellationToken { get; init; }

    public TimeSpan MutationShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Formal service deployments enable this so no backend mutation can be accepted unless
    /// its authorization decision has first reached the durable security-audit adapter.
    /// Preview desktop hosts keep the compatibility default until that adapter is connected.
    /// </summary>
    public bool RequireDurableSecurityAudit { get; init; }

    public string SessionCookieName { get; init; } = DefaultSessionCookieName;

    public string RememberedDeviceCookieName { get; init; } = DefaultRememberedDeviceCookieName;

    internal bool IsPublicInternetIngress => IngressMode is
        RemoteIngressMode.CloudflareQuickTunnel or
        RemoteIngressMode.CloudflareNamedTunnel or
        RemoteIngressMode.TailscaleFunnel;

    internal bool IsCloudflareTunnel => IngressMode is
        RemoteIngressMode.CloudflareQuickTunnel or
        RemoteIngressMode.CloudflareNamedTunnel;

    internal bool SupportsRememberedDevices => IngressMode is
        RemoteIngressMode.TailscaleServe or
        RemoteIngressMode.CloudflareNamedTunnel or
        RemoteIngressMode.TailscaleFunnel;

    internal IReadOnlySet<string> CreateAllowedLoginSet()
        => new HashSet<string>(AllowedGoogleLogins, StringComparer.OrdinalIgnoreCase);
}

public static class RemoteControlOptionsValidator
{
    public static void ValidateAndThrow(RemoteControlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = Validate(options);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(options));
        }
    }

    public static IReadOnlyList<string> Validate(RemoteControlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (options.Port is < 1024 or > 65535)
        {
            errors.Add("Port must be between 1024 and 65535.");
        }

        var validOriginHost = options.PublicOrigin is { IsAbsoluteUri: true }
                              && options.IngressMode switch
                              {
                                  RemoteIngressMode.TailscaleServe =>
                                      options.PublicOrigin.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase),
                                  RemoteIngressMode.TailscaleFunnel =>
                                      options.PublicOrigin.Port == 443 &&
                                      IsValidTailscaleFunnelHost(options.PublicOrigin.Host),
                                  RemoteIngressMode.CloudflareQuickTunnel =>
                                      options.PublicOrigin.Port == 443 &&
                                      IsValidQuickTunnelHost(options.PublicOrigin.Host),
                                  RemoteIngressMode.CloudflareNamedTunnel =>
                                      options.PublicOrigin.Port == 443 &&
                                      options.PublicOrigin.HostNameType == UriHostNameType.Dns &&
                                      IsValidNamedTunnelHost(options.PublicOrigin.Host),
                                  _ => false,
                              };
        if (options.PublicOrigin is null || !options.PublicOrigin.IsAbsoluteUri ||
            !string.Equals(options.PublicOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !validOriginHost ||
            options.PublicOrigin.UserInfo.Length != 0 ||
            options.PublicOrigin.AbsolutePath != "/" ||
            options.PublicOrigin.Query.Length != 0 ||
            options.PublicOrigin.Fragment.Length != 0)
        {
            errors.Add("PublicOrigin must be an approved HTTPS ingress origin without credentials, path, query, or fragment.");
        }

        if (options.AllowedGoogleLogins is null)
        {
            errors.Add("AllowedGoogleLogins must not be null.");
        }
        else
        {
            foreach (var login in options.AllowedGoogleLogins)
            {
                if (!RemoteIdentity.IsCanonicalGmailLogin(login))
                {
                    errors.Add("Every allowed login must be a canonical Gmail address.");
                    break;
                }
            }

            if (options.AllowedGoogleLogins.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                options.AllowedGoogleLogins.Count)
            {
                errors.Add("Allowed Gmail logins must not contain duplicates.");
            }

            if (options.IngressMode == RemoteIngressMode.TailscaleServe
                && options.AllowedGoogleLogins.Count == 0)
            {
                errors.Add("Tailscale Serve requires at least one exact Gmail login.");
            }

            if (options.IsPublicInternetIngress
                && options.AllowedGoogleLogins.Count != 0)
            {
                errors.Add("Public internet ingress must not use a Gmail ingress allowlist.");
            }
        }

        if (options.SessionLifetime < TimeSpan.FromMinutes(15) ||
            options.SessionLifetime > TimeSpan.FromDays(7))
        {
            errors.Add("Session lifetime must be between 15 minutes and 7 days.");
        }

        if (options.DefaultConsolePageSize is < 1 or > 500 ||
            options.MaximumConsolePageSize is < 1 or > 1000 ||
            options.DefaultConsolePageSize > options.MaximumConsolePageSize)
        {
            errors.Add("Console page sizes are invalid or unbounded.");
        }

        if (options.MaximumCommandLength is < 32 or > 4096)
        {
            errors.Add("Maximum command length must be between 32 and 4096 characters.");
        }

        if (options.MaximumConsoleLineCharacters is < 256 or > 16384)
        {
            errors.Add("Maximum console line length must be between 256 and 16384 characters.");
        }

        if (options.LoginAttemptsPerMinute is < 1 or > 30 ||
            options.ReadRequestsPerMinute is < 10 or > 3000 ||
            options.MutationRequestsPerMinute is < 1 or > 300 ||
            options.GlobalRequestsPerMinute is < 30 or > 6000)
        {
            errors.Add("Rate limits are outside their safe bounded ranges.");
        }

        if (options.MaximumSessions is < 1 or > 128)
        {
            errors.Add("MaximumSessions must be between 1 and 128.");
        }

        if (options.IdempotencyLifetime < TimeSpan.FromMinutes(1) ||
            options.IdempotencyLifetime > TimeSpan.FromHours(24))
        {
            errors.Add("Idempotency lifetime must be between 1 minute and 24 hours.");
        }

        if (options.MaximumIdempotencyEntries is < 16 or > 10_000)
        {
            errors.Add("MaximumIdempotencyEntries must be between 16 and 10000.");
        }

        if (options.MutationShutdownDrainTimeout < TimeSpan.FromMilliseconds(100)
            || options.MutationShutdownDrainTimeout > TimeSpan.FromMinutes(1))
        {
            errors.Add("Mutation shutdown drain timeout must be between 100 milliseconds and 1 minute.");
        }

        if (!string.Equals(options.SessionCookieName, RemoteControlOptions.DefaultSessionCookieName, StringComparison.Ordinal))
        {
            errors.Add($"Session cookie name must remain {RemoteControlOptions.DefaultSessionCookieName}.");
        }

        if (!string.Equals(
                options.RememberedDeviceCookieName,
                RemoteControlOptions.DefaultRememberedDeviceCookieName,
                StringComparison.Ordinal))
        {
            errors.Add(
                $"Remembered device cookie name must remain {RemoteControlOptions.DefaultRememberedDeviceCookieName}.");
        }

        return errors.AsReadOnly();
    }

    private static bool IsValidQuickTunnelHost(string host)
    {
        const string suffix = ".trycloudflare.com";
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var label = host[..^suffix.Length];
        return label.Length is >= 8 and <= 63
               && label[0] is not '-'
               && label[^1] is not '-'
               && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
    }

    private static bool IsValidTailscaleFunnelHost(string host)
    {
        const string suffix = ".ts.net";
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            || host.Length <= suffix.Length
            || Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            return false;
        }

        return host[..^suffix.Length]
            .Split('.')
            .All(label => label.Length is >= 1 and <= 63
                          && label[0] != '-'
                          && label[^1] != '-'
                          && label.All(character =>
                              char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private static bool IsValidNamedTunnelHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            host.Length > 253 ||
            host.EndsWith(".", StringComparison.Ordinal) ||
            host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase) ||
            host.Any(character => !char.IsAscii(character)))
        {
            return false;
        }

        var labels = host.Split('.');
        return labels.Length >= 2 && labels.All(label =>
            label.Length is >= 1 and <= 63 &&
            label[0] != '-' &&
            label[^1] != '-' &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }
}
