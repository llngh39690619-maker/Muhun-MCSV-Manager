using System.Net;
using System.Text.Json;

namespace MinecraftServerManager.Service;

internal enum ProductFunnelRouteDisposition
{
    Absent,
    ExactTarget,
    Conflict,
    Indeterminate,
}

internal sealed record ProductTailscaleNodeStatus(
    bool IsConnected,
    string? DnsName,
    Uri? PublicOrigin,
    string? ErrorCode);

internal sealed record ProductFunnelRouteStatus(
    ProductFunnelRouteDisposition Disposition,
    string? ErrorCode);

internal static class ProductTailscaleProtocol
{
    internal const int MaximumJsonBytes = 64 * 1024;
    private const string TailscaleSuffix = ".ts.net";

    public static ProductTailscaleNodeStatus ParseNodeStatus(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonBytes)
        {
            return NodeFailure("tailscale.status_payload_invalid");
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetUnique(root, "BackendState", out var backend) ||
                backend.ValueKind != JsonValueKind.String ||
                !TryGetUnique(root, "Self", out var self) ||
                self.ValueKind != JsonValueKind.Object ||
                !TryGetUnique(self, "DNSName", out var dnsElement) ||
                dnsElement.ValueKind != JsonValueKind.String ||
                !TryNormalizeDnsName(dnsElement.GetString(), out var dnsName))
            {
                return NodeFailure("tailscale.status_schema_invalid");
            }

            if (!string.Equals(backend.GetString(), "Running", StringComparison.Ordinal))
            {
                return new ProductTailscaleNodeStatus(
                    false,
                    dnsName,
                    null,
                    "tailscale.backend_not_running");
            }

            if (!TryGetUnique(root, "CertDomains", out var certDomains) ||
                certDomains.ValueKind != JsonValueKind.Array ||
                !ContainsExactCertificateDomain(certDomains, dnsName))
            {
                return new ProductTailscaleNodeStatus(
                    true,
                    dnsName,
                    null,
                    "tailscale.https_not_enabled");
            }

            return new ProductTailscaleNodeStatus(
                true,
                dnsName,
                new Uri($"https://{dnsName}", UriKind.Absolute),
                null);
        }
        catch (JsonException)
        {
            return NodeFailure("tailscale.status_json_invalid");
        }
    }

    public static ProductFunnelRouteStatus ParseFunnelStatus(
        string json,
        string dnsName,
        string expectedTarget)
    {
        if (!TryNormalizeDnsName(dnsName, out var normalizedDns) ||
            !TryNormalizeTarget(expectedTarget, out var normalizedTarget) ||
            string.IsNullOrWhiteSpace(json) ||
            json.Length > MaximumJsonBytes)
        {
            return RouteFailure("tailscale.funnel_status_payload_invalid");
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 48,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return RouteFailure("tailscale.funnel_status_schema_invalid");
            }

            var candidates = new List<RouteCandidate>();
            if (!TryCollectRootCandidates(
                    document.RootElement,
                    normalizedDns,
                    normalizedTarget,
                    candidates))
            {
                return RouteFailure("tailscale.funnel_status_schema_invalid");
            }

            if (candidates.Count == 0)
            {
                return new ProductFunnelRouteStatus(ProductFunnelRouteDisposition.Absent, null);
            }

            if (candidates.Count == 1 && candidates[0].IsExact && candidates[0].IsForeground)
            {
                return new ProductFunnelRouteStatus(ProductFunnelRouteDisposition.ExactTarget, null);
            }

            return new ProductFunnelRouteStatus(
                ProductFunnelRouteDisposition.Conflict,
                "tailscale.funnel_route_conflict");
        }
        catch (JsonException)
        {
            return RouteFailure("tailscale.funnel_status_json_invalid");
        }
    }

    private static bool TryCollectRootCandidates(
        JsonElement root,
        string dnsName,
        string expectedTarget,
        List<RouteCandidate> candidates)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return false;
            }

            if (property.Name.Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("Web", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("AllowFunnel", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Name.Equals("Foreground", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var sessions = new HashSet<string>(StringComparer.Ordinal);
                foreach (var session in property.Value.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(session.Name) ||
                        !sessions.Add(session.Name) ||
                        session.Value.ValueKind != JsonValueKind.Object ||
                        !TryCollectNodeCandidate(
                            session.Value,
                            dnsName,
                            expectedTarget,
                            candidates,
                            isForeground: true,
                            allowRootContainers: false))
                    {
                        return false;
                    }
                }

                continue;
            }

            if (property.Name.Equals("Services", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var serviceNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var service in property.Value.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(service.Name) ||
                        !serviceNames.Add(service.Name) ||
                        service.Value.ValueKind != JsonValueKind.Object ||
                        !TryCollectNodeCandidate(
                            service.Value,
                            dnsName,
                            expectedTarget,
                            candidates,
                            isForeground: false,
                            allowRootContainers: false))
                    {
                        return false;
                    }
                }

                continue;
            }

            // Unknown fields may represent a future Tailscale route schema. Fail closed instead
            // of treating a non-empty, unrecognized configuration as route absence.
            return false;
        }

        return TryCollectNodeCandidate(
            root,
            dnsName,
            expectedTarget,
            candidates,
            isForeground: false,
            allowRootContainers: true);
    }

    private static bool TryCollectNodeCandidate(
        JsonElement node,
        string dnsName,
        string expectedTarget,
        List<RouteCandidate> candidates,
        bool isForeground,
        bool allowRootContainers)
    {
        var nodeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in node.EnumerateObject())
        {
            if (!nodeNames.Add(property.Name))
            {
                return false;
            }

            var knownRouteProperty = property.Name.Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                                     property.Name.Equals("Web", StringComparison.OrdinalIgnoreCase) ||
                                     property.Name.Equals("AllowFunnel", StringComparison.OrdinalIgnoreCase);
            var knownRootContainer = allowRootContainers &&
                                     (property.Name.Equals("Foreground", StringComparison.OrdinalIgnoreCase) ||
                                      property.Name.Equals("Services", StringComparison.OrdinalIgnoreCase));
            if (!knownRouteProperty && !knownRootContainer)
            {
                return false;
            }
        }

        if (!TryGetOptionalUnique(node, "TCP", out var tcp) ||
            !TryGetOptionalUnique(node, "Web", out var web) ||
            !TryGetOptionalUnique(node, "AllowFunnel", out var allowFunnel))
        {
            return false;
        }

        var hasTcp443 = false;
        var exactTcp = false;
        if (tcp is { } tcpValue)
        {
            if (tcpValue.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var ports = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in tcpValue.EnumerateObject())
            {
                if (!ports.Add(port.Name) || !ushort.TryParse(
                        port.Name,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var number))
                {
                    return false;
                }

                if (number == 443)
                {
                    hasTcp443 = true;
                    exactTcp = IsExactHttpsTcpEntry(port.Value);
                }
            }
        }

        var hasWeb443 = false;
        var exactWeb = false;
        if (web is { } webValue)
        {
            if (webValue.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var endpoint in webValue.EnumerateObject())
            {
                if (!endpoints.Add(endpoint.Name) || !TryParseDnsEndpoint(endpoint.Name, out var host, out var port))
                {
                    return false;
                }

                if (port == 443)
                {
                    hasWeb443 = true;
                    exactWeb = string.Equals(host, dnsName, StringComparison.OrdinalIgnoreCase) &&
                               IsExactWebHandler(endpoint.Value, expectedTarget);
                }
            }
        }

        var hasAllow443 = false;
        var exactAllow = false;
        if (allowFunnel is { } allowValue)
        {
            if (allowValue.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var endpoint in allowValue.EnumerateObject())
            {
                if (!endpoints.Add(endpoint.Name) || !TryParseDnsEndpoint(endpoint.Name, out var host, out var port))
                {
                    return false;
                }

                if (port == 443)
                {
                    hasAllow443 = true;
                    exactAllow = string.Equals(host, dnsName, StringComparison.OrdinalIgnoreCase) &&
                                 endpoint.Value.ValueKind == JsonValueKind.True;
                }
            }
        }

        if (hasTcp443 || hasWeb443 || hasAllow443)
        {
            candidates.Add(new RouteCandidate(
                hasTcp443 && hasWeb443 && hasAllow443 && exactTcp && exactWeb && exactAllow,
                isForeground));
        }

        return true;
    }

    private static bool IsExactHttpsTcpEntry(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !TryGetUnique(value, "HTTPS", out var https) ||
            https.ValueKind != JsonValueKind.True)
        {
            return false;
        }

        return value.EnumerateObject().Count() == 1;
    }

    private static bool IsExactWebHandler(JsonElement value, string expectedTarget)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !TryGetUnique(value, "Handlers", out var handlers) ||
            handlers.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Count() != 1)
        {
            return false;
        }

        var entries = handlers.EnumerateObject().ToArray();
        if (entries.Length != 1 || entries[0].Name != "/" ||
            entries[0].Value.ValueKind != JsonValueKind.Object ||
            !TryGetUnique(entries[0].Value, "Proxy", out var proxy) ||
            proxy.ValueKind != JsonValueKind.String ||
            entries[0].Value.EnumerateObject().Count() != 1 ||
            !TryNormalizeTarget(proxy.GetString(), out var target))
        {
            return false;
        }

        return string.Equals(target, expectedTarget, StringComparison.Ordinal);
    }

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

    internal static bool TryNormalizeDnsName(string? value, out string dnsName)
    {
        dnsName = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 || value != value.Trim())
        {
            return false;
        }

        var candidate = value.EndsWith(".", StringComparison.Ordinal) ? value[..^1] : value;
        if (!candidate.EndsWith(TailscaleSuffix, StringComparison.OrdinalIgnoreCase) ||
            candidate.Length <= TailscaleSuffix.Length ||
            candidate.Any(character => !char.IsAscii(character)) ||
            Uri.CheckHostName(candidate) != UriHostNameType.Dns)
        {
            return false;
        }

        var labels = candidate.Split('.');
        if (labels.Any(label => label.Length is < 1 or > 63 ||
                                label[0] == '-' || label[^1] == '-' ||
                                label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
        {
            return false;
        }

        dnsName = candidate.ToLowerInvariant();
        return true;
    }

    internal static bool TryNormalizeTarget(string? value, out string target)
    {
        target = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            uri.UserInfo.Length != 0 ||
            uri.Host != "127.0.0.1" ||
            uri.Port is < 1024 or > 65535 ||
            (uri.AbsolutePath != "/" && uri.AbsolutePath.Length != 0) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return false;
        }

        target = $"http://127.0.0.1:{uri.Port}";
        return true;
    }

    private static bool TryParseDnsEndpoint(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var separator = value.LastIndexOf(':');
        return separator > 0 &&
               int.TryParse(
                   value[(separator + 1)..],
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out port) &&
               port is >= 1 and <= 65535 &&
               TryNormalizeDnsName(value[..separator], out host);
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

    private static bool TryGetOptionalUnique(JsonElement value, string name, out JsonElement? property)
    {
        property = null;
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

        return true;
    }

    private static ProductTailscaleNodeStatus NodeFailure(string code)
        => new(false, null, null, code);

    private static ProductFunnelRouteStatus RouteFailure(string code)
        => new(ProductFunnelRouteDisposition.Indeterminate, code);

    private readonly record struct RouteCandidate(bool IsExact, bool IsForeground);
}
