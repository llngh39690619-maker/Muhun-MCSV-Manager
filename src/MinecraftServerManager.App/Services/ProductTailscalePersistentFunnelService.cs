using System.Text.Json;

namespace MinecraftServerManager.App.Services;

internal sealed record ProductTailscaleIdentityResult(
    bool Succeeded,
    Uri? PublicOrigin,
    string? ErrorCode);

internal enum ProductPersistentFunnelDisposition
{
    Absent,
    ExactTarget,
    Conflict,
    Indeterminate,
}

internal sealed record ProductPersistentFunnelOperationResult(
    bool Succeeded,
    bool Changed,
    ProductPersistentFunnelDisposition Disposition,
    DateTimeOffset? VerifiedAtUtc,
    string? ErrorCode);

/// <summary>
/// Runs the small Tailscale command surface that must execute as the signed-in desktop user.
/// The Windows Service deliberately cannot impersonate this identity: Tailscale for Windows
/// permits only the active local actor to use its LocalAPI while the tray client is connected.
/// </summary>
internal interface IProductTailscalePersistentFunnelService
{
    Task<ProductTailscaleIdentityResult> EnsureProductIdentityAsync(
        CancellationToken cancellationToken = default);

    Task<ProductPersistentFunnelOperationResult> EnsureRouteAsync(
        Uri expectedPublicOrigin,
        int localPort,
        bool allowExistingVerifiedRoute,
        CancellationToken cancellationToken = default);

    Task<ProductPersistentFunnelOperationResult> RemoveRouteAsync(
        Uri expectedPublicOrigin,
        int localPort,
        CancellationToken cancellationToken = default);
}

internal interface IProductTailscaleMachineExecutableLocator
{
    string? FindTrustedExecutable();
}

/// <summary>
/// Owns a persistent, background Tailscale Funnel route. It accepts only the canonical
/// x-mcsv.*.ts.net origin and the exact 443 -&gt; 127.0.0.1 target. Because Tailscale 1.102 only
/// exposes whole-config <c>funnel reset</c>, removal is allowed only when the complete JSON
/// document proves that this exact route is the sole configuration.
/// </summary>
internal sealed class ProductTailscalePersistentFunnelService(
    IProductTailscaleMachineExecutableLocator? locator = null,
    ITailscaleCommandRunner? runner = null,
    ITailscaleDelay? delay = null,
    TimeProvider? timeProvider = null,
    TimeSpan? commandTimeout = null,
    int verificationAttempts = 20,
    TimeSpan? verificationInterval = null) : IProductTailscalePersistentFunnelService
{
    internal const string RequiredMachineHostname = "x-mcsv";
    internal const int PublicHttpsPort = 443;
    internal const int MaximumJsonCharacters = 64 * 1024;
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultVerificationInterval = TimeSpan.FromMilliseconds(250);

    private readonly IProductTailscaleMachineExecutableLocator _locator =
        locator ?? new ProductTailscaleMachineExecutableLocator();
    private readonly ITailscaleCommandRunner _runner = runner ?? new SystemTailscaleCommandRunner();
    private readonly ITailscaleDelay _delay = delay ?? new SystemTailscaleDelay();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _commandTimeout = ValidateCommandTimeout(
        commandTimeout ?? DefaultCommandTimeout);
    private readonly int _verificationAttempts = ValidateVerificationAttempts(verificationAttempts);
    private readonly TimeSpan _verificationInterval = ValidateVerificationInterval(
        verificationInterval ?? DefaultVerificationInterval);

    public async Task<ProductTailscaleIdentityResult> EnsureProductIdentityAsync(
        CancellationToken cancellationToken = default)
    {
        var executablePath = _locator.FindTrustedExecutable();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return IdentityFailure("tailscale.not_installed");
        }

        var initial = await QueryNodeAsync(executablePath, cancellationToken).ConfigureAwait(false);
        if (initial.ErrorCode is not null)
        {
            return IdentityFailure(initial.ErrorCode);
        }

        var hostname = await RunSafeAsync(
                executablePath,
                ["get", "hostname"],
                cancellationToken)
            .ConfigureAwait(false);
        if (!hostname.Succeeded)
        {
            return IdentityFailure(hostname.TimedOut
                ? "tailscale.hostname_status_timeout"
                : ClassifyLocalApiFailure(hostname, "tailscale.hostname_status_failed"));
        }

        var configuredHostname = hostname.StandardOutput.Trim();
        if (configuredHostname.Length > 63 ||
            configuredHostname.Contains('\0') ||
            configuredHostname.Contains('\r') ||
            configuredHostname.Contains('\n'))
        {
            return IdentityFailure("tailscale.hostname_status_invalid");
        }

        if (!string.Equals(configuredHostname, RequiredMachineHostname, StringComparison.Ordinal))
        {
            var update = await RunSafeAsync(
                    executablePath,
                    ["set", $"--hostname={RequiredMachineHostname}"],
                    cancellationToken)
                .ConfigureAwait(false);
            if (!update.Succeeded)
            {
                return IdentityFailure(update.TimedOut
                    ? "tailscale.hostname_set_timeout"
                    : ClassifyLocalApiFailure(update, "tailscale.hostname_set_failed"));
            }
        }

        NodeSnapshot? latest = null;
        for (var attempt = 0; attempt < _verificationAttempts; attempt++)
        {
            latest = await QueryNodeAsync(executablePath, cancellationToken).ConfigureAwait(false);
            if (latest.ErrorCode is null &&
                latest.IsConnected &&
                HasRequiredMachineLabel(latest.DnsName))
            {
                if (!latest.HasHttpsCertificate)
                {
                    return IdentityFailure("tailscale.https_not_enabled");
                }

                return new ProductTailscaleIdentityResult(
                    true,
                    new Uri($"https://{latest.DnsName}/", UriKind.Absolute),
                    null);
            }

            if (latest.ErrorCode is not null &&
                latest.ErrorCode is not ("tailscale.status_timeout" or "tailscale.status_failed"))
            {
                return IdentityFailure(latest.ErrorCode);
            }

            if (attempt + 1 < _verificationAttempts)
            {
                await _delay.DelayAsync(_verificationInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return IdentityFailure(latest?.ErrorCode ?? "tailscale.hostname_verification_failed");
    }

    public async Task<ProductPersistentFunnelOperationResult> EnsureRouteAsync(
        Uri expectedPublicOrigin,
        int localPort,
        bool allowExistingVerifiedRoute,
        CancellationToken cancellationToken = default)
    {
        var origin = ValidateCanonicalOrigin(expectedPublicOrigin);
        ValidateLocalPort(localPort);
        var executablePath = _locator.FindTrustedExecutable();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return RouteFailure(ProductPersistentFunnelDisposition.Indeterminate, "tailscale.not_installed");
        }

        var before = await QueryRouteAsync(executablePath, origin, localPort, cancellationToken)
            .ConfigureAwait(false);
        if (before.Disposition == ProductPersistentFunnelDisposition.ExactTarget)
        {
            return allowExistingVerifiedRoute
                ? RouteSuccess(changed: false, ProductPersistentFunnelDisposition.ExactTarget)
                : RouteFailure(
                    ProductPersistentFunnelDisposition.Conflict,
                    "tailscale.funnel_unowned_exact_route");
        }
        if (before.Disposition != ProductPersistentFunnelDisposition.Absent)
        {
            return RouteFailure(before.Disposition, before.ErrorCode ?? "tailscale.funnel_route_conflict");
        }

        var target = CreateTarget(localPort);
        var start = await RunSafeAsync(
                executablePath,
                ["funnel", "--bg", "--yes", "--https=443", target],
                cancellationToken)
            .ConfigureAwait(false);

        ProductPersistentFunnelOperationResult? latest = null;
        for (var attempt = 0; attempt < _verificationAttempts; attempt++)
        {
            latest = await QueryRouteAsync(executablePath, origin, localPort, cancellationToken)
                .ConfigureAwait(false);
            if (latest.Disposition == ProductPersistentFunnelDisposition.ExactTarget)
            {
                return RouteSuccess(changed: true, ProductPersistentFunnelDisposition.ExactTarget);
            }
            if (latest.Disposition is ProductPersistentFunnelDisposition.Conflict or
                ProductPersistentFunnelDisposition.Indeterminate)
            {
                return RouteFailure(
                    latest.Disposition,
                    latest.ErrorCode ?? "tailscale.funnel_route_conflict");
            }

            if (attempt + 1 < _verificationAttempts)
            {
                await _delay.DelayAsync(_verificationInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var startError = start.TimedOut
            ? "tailscale.funnel_start_timeout"
            : !start.Succeeded
                ? ClassifyLocalApiFailure(start, "tailscale.funnel_start_failed")
                : "tailscale.funnel_verification_failed";
        return RouteFailure(latest?.Disposition ?? ProductPersistentFunnelDisposition.Indeterminate, startError);
    }

    public async Task<ProductPersistentFunnelOperationResult> RemoveRouteAsync(
        Uri expectedPublicOrigin,
        int localPort,
        CancellationToken cancellationToken = default)
    {
        var origin = ValidateCanonicalOrigin(expectedPublicOrigin);
        ValidateLocalPort(localPort);
        var executablePath = _locator.FindTrustedExecutable();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return RouteFailure(ProductPersistentFunnelDisposition.Indeterminate, "tailscale.not_installed");
        }

        var before = await QueryRouteAsync(executablePath, origin, localPort, cancellationToken)
            .ConfigureAwait(false);
        if (before.Disposition == ProductPersistentFunnelDisposition.Absent)
        {
            return RouteSuccess(changed: false, ProductPersistentFunnelDisposition.Absent);
        }

        // `funnel reset` removes the complete Tailscale Funnel configuration. Never issue it
        // unless the full strict document proves that the sole route belongs to this challenge.
        if (before.Disposition != ProductPersistentFunnelDisposition.ExactTarget)
        {
            return RouteFailure(before.Disposition, before.ErrorCode ?? "tailscale.funnel_route_conflict");
        }

        var reset = await RunSafeAsync(
                executablePath,
                ["funnel", "reset"],
                cancellationToken)
            .ConfigureAwait(false);

        ProductPersistentFunnelOperationResult? latest = null;
        for (var attempt = 0; attempt < _verificationAttempts; attempt++)
        {
            latest = await QueryRouteAsync(executablePath, origin, localPort, cancellationToken)
                .ConfigureAwait(false);
            if (latest.Disposition == ProductPersistentFunnelDisposition.Absent)
            {
                return RouteSuccess(changed: true, ProductPersistentFunnelDisposition.Absent);
            }
            if (latest.Disposition is ProductPersistentFunnelDisposition.Conflict or
                ProductPersistentFunnelDisposition.Indeterminate)
            {
                return RouteFailure(
                    latest.Disposition,
                    latest.ErrorCode ?? "tailscale.funnel_route_conflict");
            }

            if (attempt + 1 < _verificationAttempts)
            {
                await _delay.DelayAsync(_verificationInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var resetError = reset.TimedOut
            ? "tailscale.funnel_reset_timeout"
            : !reset.Succeeded
                ? ClassifyLocalApiFailure(reset, "tailscale.funnel_reset_failed")
                : "tailscale.funnel_removal_verification_failed";
        return RouteFailure(latest?.Disposition ?? ProductPersistentFunnelDisposition.Indeterminate, resetError);
    }

    private async Task<NodeSnapshot> QueryNodeAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var result = await RunSafeAsync(executablePath, ["status", "--json"], cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new NodeSnapshot(
                false,
                null,
                false,
                result.TimedOut
                    ? "tailscale.status_timeout"
                    : ClassifyLocalApiFailure(result, "tailscale.status_failed"));
        }

        return ParseNodeStatus(result.StandardOutput);
    }

    private async Task<ProductPersistentFunnelOperationResult> QueryRouteAsync(
        string executablePath,
        Uri expectedOrigin,
        int localPort,
        CancellationToken cancellationToken)
    {
        var result = await RunSafeAsync(
                executablePath,
                ["funnel", "status", "--json"],
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return RouteFailure(
                ProductPersistentFunnelDisposition.Indeterminate,
                result.TimedOut
                    ? "tailscale.funnel_status_timeout"
                    : ClassifyLocalApiFailure(result, "tailscale.funnel_status_failed"));
        }

        var parsed = ParseRouteStatus(
            result.StandardOutput,
            expectedOrigin.IdnHost,
            CreateTarget(localPort));
        return parsed.Succeeded
            ? parsed with { VerifiedAtUtc = _timeProvider.GetUtcNow() }
            : parsed;
    }

    private async Task<TailscaleCommandResult> RunSafeAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runner.RunAsync(
                    executablePath,
                    arguments,
                    _commandTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new TailscaleCommandResult(null, string.Empty, exception.GetType().Name);
        }
    }

    internal static NodeSnapshot ParseNodeStatus(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonCharacters)
        {
            return NodeFailure("tailscale.status_payload_invalid");
        }

        try
        {
            using var document = JsonDocument.Parse(json, StrictJsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetUnique(root, "BackendState", out var backend) ||
                backend.ValueKind != JsonValueKind.String)
            {
                return NodeFailure("tailscale.status_schema_invalid");
            }

            if (!string.Equals(backend.GetString(), "Running", StringComparison.Ordinal))
            {
                return new NodeSnapshot(false, null, false, "tailscale.backend_not_running");
            }

            if (!TryGetUnique(root, "Self", out var self) ||
                self.ValueKind != JsonValueKind.Object ||
                !TryGetUnique(self, "DNSName", out var dnsElement) ||
                dnsElement.ValueKind != JsonValueKind.String ||
                !TryNormalizeDnsName(dnsElement.GetString(), out var dnsName) ||
                !TryGetUnique(root, "CertDomains", out var certDomains) ||
                certDomains.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            {
                return NodeFailure("tailscale.status_schema_invalid");
            }

            var hasCertificate = certDomains.ValueKind == JsonValueKind.Array &&
                                 ContainsExactCertificateDomain(certDomains, dnsName);
            return new NodeSnapshot(true, dnsName, hasCertificate, null);
        }
        catch (JsonException)
        {
            return NodeFailure("tailscale.status_json_invalid");
        }
    }

    internal static ProductPersistentFunnelOperationResult ParseRouteStatus(
        string json,
        string expectedDnsName,
        string expectedTarget)
    {
        if (!TryNormalizeDnsName(expectedDnsName, out var normalizedDns) ||
            !TryNormalizeTarget(expectedTarget, out var normalizedTarget) ||
            string.IsNullOrWhiteSpace(json) ||
            json.Length > MaximumJsonCharacters)
        {
            return RouteFailure(
                ProductPersistentFunnelDisposition.Indeterminate,
                "tailscale.funnel_status_payload_invalid");
        }

        try
        {
            using var document = JsonDocument.Parse(json, StrictJsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return RouteFailure(
                    ProductPersistentFunnelDisposition.Indeterminate,
                    "tailscale.funnel_status_schema_invalid");
            }

            var properties = root.EnumerateObject().ToArray();
            if (properties.Length == 0)
            {
                return new ProductPersistentFunnelOperationResult(
                    true,
                    false,
                    ProductPersistentFunnelDisposition.Absent,
                    null,
                    null);
            }

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (properties.Length != 3 || properties.Any(property => !unique.Add(property.Name)) ||
                !TryGetUnique(root, "TCP", out var tcp) ||
                !TryGetUnique(root, "Web", out var web) ||
                !TryGetUnique(root, "AllowFunnel", out var allowFunnel) ||
                !IsExactTcp(tcp) ||
                !IsExactWeb(web, normalizedDns, normalizedTarget) ||
                !IsExactAllowFunnel(allowFunnel, normalizedDns))
            {
                // Unknown fields and malformed documents are indeterminate; fully understood
                // additional routes are still conflicts. Both classifications are fail-closed.
                var onlyKnownNames = properties.All(property =>
                    property.Name.Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("Web", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("AllowFunnel", StringComparison.OrdinalIgnoreCase));
                return RouteFailure(
                    onlyKnownNames
                        ? ProductPersistentFunnelDisposition.Conflict
                        : ProductPersistentFunnelDisposition.Indeterminate,
                    onlyKnownNames
                        ? "tailscale.funnel_route_conflict"
                        : "tailscale.funnel_status_schema_invalid");
            }

            return new ProductPersistentFunnelOperationResult(
                true,
                false,
                ProductPersistentFunnelDisposition.ExactTarget,
                null,
                null);
        }
        catch (JsonException)
        {
            return RouteFailure(
                ProductPersistentFunnelDisposition.Indeterminate,
                "tailscale.funnel_status_json_invalid");
        }
    }

    internal static Uri ValidateCanonicalOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri ||
            !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            origin.Port != PublicHttpsPort ||
            origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 ||
            origin.Fragment.Length != 0 ||
            !TryNormalizeDnsName(origin.IdnHost, out var dnsName) ||
            !HasRequiredMachineLabel(dnsName))
        {
            throw new ArgumentException("A canonical x-mcsv Tailscale HTTPS origin is required.", nameof(origin));
        }

        var canonical = new Uri($"https://{dnsName}/", UriKind.Absolute);
        if (!string.Equals(origin.AbsoluteUri, canonical.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new ArgumentException("The Tailscale HTTPS origin is not canonical.", nameof(origin));
        }

        return canonical;
    }

    private static bool IsExactTcp(JsonElement tcp)
        => tcp.ValueKind == JsonValueKind.Object &&
           tcp.EnumerateObject().ToArray() is [{ Name: "443", Value: var handler }] &&
           handler.ValueKind == JsonValueKind.Object &&
           handler.EnumerateObject().ToArray() is [{ Name: var name, Value.ValueKind: JsonValueKind.True }] &&
           name.Equals("HTTPS", StringComparison.OrdinalIgnoreCase);

    private static bool IsExactWeb(JsonElement web, string dnsName, string target)
    {
        if (web.ValueKind != JsonValueKind.Object ||
            web.EnumerateObject().ToArray() is not [{ Name: var endpoint, Value: var value }] ||
            !string.Equals(endpoint, $"{dnsName}:443", StringComparison.OrdinalIgnoreCase) ||
            value.ValueKind != JsonValueKind.Object ||
            !TryGetUnique(value, "Handlers", out var handlers) ||
            value.EnumerateObject().Count() != 1 ||
            handlers.ValueKind != JsonValueKind.Object ||
            handlers.EnumerateObject().ToArray() is not [{ Name: "/", Value: var route }] ||
            route.ValueKind != JsonValueKind.Object ||
            !TryGetUnique(route, "Proxy", out var proxy) ||
            route.EnumerateObject().Count() != 1 ||
            proxy.ValueKind != JsonValueKind.String ||
            !TryNormalizeTarget(proxy.GetString(), out var configuredTarget))
        {
            return false;
        }

        return string.Equals(configuredTarget, target, StringComparison.Ordinal);
    }

    private static bool IsExactAllowFunnel(JsonElement allowFunnel, string dnsName)
        => allowFunnel.ValueKind == JsonValueKind.Object &&
           allowFunnel.EnumerateObject().ToArray() is [{ Name: var endpoint, Value.ValueKind: JsonValueKind.True }] &&
           string.Equals(endpoint, $"{dnsName}:443", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsExactCertificateDomain(JsonElement domains, string dnsName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = false;
        foreach (var value in domains.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String ||
                !TryNormalizeDnsName(value.GetString(), out var domain) ||
                !seen.Add(domain))
            {
                return false;
            }

            found |= string.Equals(domain, dnsName, StringComparison.OrdinalIgnoreCase);
        }

        return found;
    }

    private static bool TryNormalizeDnsName(string? value, out string dnsName)
    {
        dnsName = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 || value != value.Trim())
        {
            return false;
        }

        var candidate = value.EndsWith(".", StringComparison.Ordinal) ? value[..^1] : value;
        if (!candidate.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) ||
            candidate.Any(character => !char.IsAscii(character)) ||
            Uri.CheckHostName(candidate) != UriHostNameType.Dns)
        {
            return false;
        }

        var labels = candidate.Split('.');
        if (labels.Length < 4 || labels.Any(label => label.Length is < 1 or > 63 ||
                                                   label[0] == '-' || label[^1] == '-' ||
                                                   label.Any(character =>
                                                       !char.IsAsciiLetterOrDigit(character) && character != '-')))
        {
            return false;
        }

        dnsName = candidate.ToLowerInvariant();
        return true;
    }

    private static bool TryNormalizeTarget(string? value, out string target)
    {
        target = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            uri.UserInfo.Length != 0 ||
            uri.Host != "127.0.0.1" ||
            uri.Port is < 1024 or > 65_535 ||
            (uri.AbsolutePath != "/" && uri.AbsolutePath.Length != 0) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return false;
        }

        target = $"http://127.0.0.1:{uri.Port}";
        return true;
    }

    private static bool TryGetUnique(JsonElement value, string name, out JsonElement property)
    {
        property = default;
        var found = false;
        foreach (var candidate in value.EnumerateObject())
        {
            if (!candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (found)
            {
                return false;
            }

            property = candidate.Value;
            found = true;
        }

        return found;
    }

    private static bool HasRequiredMachineLabel(string? dnsName)
        => dnsName is { Length: > 0 } &&
           dnsName.Split('.')[0].Equals(RequiredMachineHostname, StringComparison.Ordinal);

    private static string CreateTarget(int localPort) => $"http://127.0.0.1:{localPort}";

    private static void ValidateLocalPort(int localPort)
    {
        if (localPort is < 1024 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort));
        }
    }

    private ProductPersistentFunnelOperationResult RouteSuccess(
        bool changed,
        ProductPersistentFunnelDisposition disposition)
        => new(true, changed, disposition, _timeProvider.GetUtcNow(), null);

    private static ProductPersistentFunnelOperationResult RouteFailure(
        ProductPersistentFunnelDisposition disposition,
        string errorCode)
        => new(false, false, disposition, null, errorCode);

    private static ProductTailscaleIdentityResult IdentityFailure(string errorCode)
        => new(false, null, errorCode);

    private static NodeSnapshot NodeFailure(string errorCode)
        => new(false, null, false, errorCode);

    private static string ClassifyLocalApiFailure(TailscaleCommandResult result, string fallback)
    {
        var diagnostic = string.Concat(result.StandardError, "\n", result.StandardOutput);
        if (diagnostic.Contains("Tailscale already in use", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "tailscale.localapi_identity_conflict";
        }

        var localApi = diagnostic.Contains("local-tailscaled.sock", StringComparison.OrdinalIgnoreCase) ||
                       diagnostic.Contains("ProtectedPrefix", StringComparison.OrdinalIgnoreCase) ||
                       diagnostic.Contains(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase);
        var denied = diagnostic.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
                     diagnostic.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
                     diagnostic.Contains("拒絕存取", StringComparison.Ordinal) ||
                     diagnostic.Contains("存取被拒", StringComparison.Ordinal);
        return localApi && denied ? "tailscale.localapi_access_denied" : fallback;
    }

    private static TimeSpan ValidateCommandTimeout(TimeSpan value)
        => value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(1)
            ? throw new ArgumentOutOfRangeException(nameof(value))
            : value;

    private static int ValidateVerificationAttempts(int value)
        => value is < 1 or > 100 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;

    private static TimeSpan ValidateVerificationInterval(TimeSpan value)
        => value < TimeSpan.Zero || value > TimeSpan.FromSeconds(5)
            ? throw new ArgumentOutOfRangeException(nameof(value))
            : value;

    private static readonly JsonDocumentOptions StrictJsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 48,
    };

    internal sealed record NodeSnapshot(
        bool IsConnected,
        string? DnsName,
        bool HasHttpsCertificate,
        string? ErrorCode);
}

/// <summary>
/// Resolves only fixed machine-wide Tailscale installation paths. PATH, the current directory,
/// user profiles, and registry command strings are intentionally ignored.
/// </summary>
internal sealed class ProductTailscaleMachineExecutableLocator : IProductTailscaleMachineExecutableLocator
{
    private readonly Func<Environment.SpecialFolder, string> _getFolderPath;
    private readonly Func<string, bool> _fileExists;

    public ProductTailscaleMachineExecutableLocator()
        : this(Environment.GetFolderPath, File.Exists)
    {
    }

    internal ProductTailscaleMachineExecutableLocator(
        Func<Environment.SpecialFolder, string> getFolderPath,
        Func<string, bool> fileExists)
    {
        _getFolderPath = getFolderPath ?? throw new ArgumentNullException(nameof(getFolderPath));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    }

    public string? FindTrustedExecutable()
    {
        var roots = new[]
        {
            _getFolderPath(Environment.SpecialFolder.ProgramFiles),
            _getFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        string[] relativePaths =
        [
            Path.Combine("Tailscale", "tailscale.exe"),
            Path.Combine("Tailscale IPN", "tailscale.exe"),
        ];

        foreach (var root in roots.Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                           Path.DirectorySeparatorChar;
            foreach (var relativePath in relativePaths)
            {
                var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
                if (candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
                    _fileExists(candidate) &&
                    !TraversesReparsePoint(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool TraversesReparsePoint(string path)
    {
        for (FileSystemInfo? current = new FileInfo(path); current is not null; current = current switch
             {
                 FileInfo file => file.Directory,
                 DirectoryInfo directory => directory.Parent,
                 _ => null,
             })
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
