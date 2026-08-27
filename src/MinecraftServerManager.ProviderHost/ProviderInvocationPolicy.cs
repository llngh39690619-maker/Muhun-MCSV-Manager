using System.Net;
using System.Text.Json;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost;

public sealed record ProviderInvocationRequest(
    string Operation,
    JsonElement Payload,
    Uri? NetworkTarget = null);

public sealed class ProviderPolicyException(string message) : InvalidOperationException(message);

/// <summary>Fail-closed capability, permission, operation, and exact-host policy.</summary>
public static class ProviderInvocationPolicy
{
    private static readonly IReadOnlyDictionary<string, OperationPolicy> Policies =
        new Dictionary<string, OperationPolicy>(StringComparer.Ordinal)
        {
            [ProductProviderOperations.HealthGet] = new(null, [], false, TimeSpan.FromSeconds(10)),
            [ProductProviderOperations.ConfigurationRead] =
                new(null, [ProductProviderPermissions.ReadConfiguration], false, TimeSpan.FromSeconds(10)),
            [ProductProviderOperations.StateWrite] =
                new(null, [ProductProviderPermissions.WriteState], false, TimeSpan.FromSeconds(15)),
            [ProductProviderOperations.NotificationDeliver] = new(
                ProductProviderCapabilities.Notification,
                [ProductProviderPermissions.EmitNotifications, ProductProviderPermissions.Http],
                true,
                TimeSpan.FromSeconds(30)),
            [ProductProviderOperations.ModpackCatalogSearch] = new(
                ProductProviderCapabilities.ModpackCatalog,
                [ProductProviderPermissions.Http],
                true,
                TimeSpan.FromSeconds(45)),
            [ProductProviderOperations.ServerCoreCatalogSearch] = new(
                ProductProviderCapabilities.ServerCoreCatalog,
                [ProductProviderPermissions.Http],
                true,
                TimeSpan.FromSeconds(45)),
            [ProductProviderOperations.RuntimeCatalogSearch] = new(
                ProductProviderCapabilities.RuntimeCatalog,
                [ProductProviderPermissions.Http],
                true,
                TimeSpan.FromSeconds(45)),
            [ProductProviderOperations.TunnelConnect] = new(
                ProductProviderCapabilities.Tunnel,
                [ProductProviderPermissions.Http, ProductProviderPermissions.WriteState],
                true,
                TimeSpan.FromSeconds(60)),
        };

    public static void EnsureAllowed(
        ProviderRegistration registration,
        ProviderInvocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(request);
        var manifestValidation = ProductProviderManifestValidator.Validate(registration.Manifest);
        if (!manifestValidation.IsValid)
        {
            throw new ProviderPolicyException("Provider manifest is not valid for this host/API version.");
        }

        if (!registration.IsEnabled)
        {
            throw new ProviderPolicyException("Provider is disabled.");
        }

        if (registration.ConsecutiveFailures >= ProviderInvocationHost.CircuitBreakerFailureThreshold)
        {
            throw new ProviderPolicyException("Provider circuit breaker is open and must be reset by an administrator.");
        }

        if (string.IsNullOrWhiteSpace(request.Operation) || request.Operation.Length > 128 ||
            request.Operation.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            throw new ProviderPolicyException("Provider operation identifier is invalid.");
        }

        if (!Policies.TryGetValue(request.Operation, out var policy))
        {
            throw new ProviderPolicyException("Unknown provider operation was rejected.");
        }

        if (request.Payload.ValueKind == JsonValueKind.Undefined)
        {
            throw new ProviderPolicyException("Provider request payload is undefined.");
        }

        if (policy.Capability is not null &&
            !registration.Manifest.Capabilities.Contains(policy.Capability, StringComparer.Ordinal))
        {
            throw new ProviderPolicyException("Provider did not declare the required capability.");
        }

        if (policy.Permissions.Any(permission =>
                !registration.Manifest.Permissions.Contains(permission, StringComparer.Ordinal)))
        {
            throw new ProviderPolicyException("Provider did not declare every required permission.");
        }

        if (policy.RequiresNetwork)
        {
            EnsureNetworkTargetAllowed(registration.Manifest, request.NetworkTarget);
        }
        else if (request.NetworkTarget is not null)
        {
            throw new ProviderPolicyException("This provider operation cannot request network access.");
        }
    }

    public static TimeSpan GetMaximumTimeout(string operation)
    {
        if (!Policies.TryGetValue(operation, out var policy))
        {
            throw new ProviderPolicyException("Unknown provider operation was rejected.");
        }

        return policy.MaximumTimeout;
    }

    private static void EnsureNetworkTargetAllowed(ProductProviderManifest manifest, Uri? target)
    {
        if (target is null || !target.IsAbsoluteUri || target.AbsoluteUri.Length > 4096 ||
            !target.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            (!target.IsDefaultPort && target.Port != 443) ||
            target.HostNameType != UriHostNameType.Dns ||
            IPAddress.TryParse(target.Host, out _) ||
            !string.IsNullOrEmpty(target.UserInfo) ||
            !string.IsNullOrEmpty(target.Fragment))
        {
            throw new ProviderPolicyException("Provider network target must be an exact HTTPS DNS host on port 443.");
        }

        if (!manifest.Permissions.Contains(ProductProviderPermissions.Http, StringComparer.Ordinal) ||
            !manifest.NetworkHosts.Contains(target.IdnHost, StringComparer.Ordinal))
        {
            throw new ProviderPolicyException("Provider network target is outside its exact-host allowlist.");
        }
    }

    private sealed record OperationPolicy(
        string? Capability,
        IReadOnlyList<string> Permissions,
        bool RequiresNetwork,
        TimeSpan MaximumTimeout);
}
