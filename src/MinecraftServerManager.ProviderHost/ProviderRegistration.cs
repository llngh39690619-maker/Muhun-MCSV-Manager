using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost;

public enum ProviderHealthStatus
{
    Disabled,
    Stopped,
    Starting,
    Healthy,
    Degraded,
    Failed,
}

public sealed record ProviderRegistration(
    ProductProviderManifest Manifest,
    string PublisherId,
    string PackageSha256,
    string InstallRelativePath,
    bool IsEnabled,
    ProviderHealthStatus Health,
    DateTimeOffset InstalledAtUtc,
    DateTimeOffset LastHealthTransitionUtc,
    int ConsecutiveFailures,
    string? LastError);

public static class ProviderRegistrationValidator
{
    public static void ValidateAndThrow(ProviderRegistration registration, ProviderHostLayout layout)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(layout);

        var manifestResult = ProductProviderManifestValidator.Validate(registration.Manifest);
        if (!manifestResult.IsValid)
        {
            throw new InvalidDataException(
                "Provider registry contains an invalid manifest: " + string.Join(" ", manifestResult.Errors));
        }

        if (string.IsNullOrWhiteSpace(registration.PublisherId) || registration.PublisherId.Length > 128 ||
            registration.PublisherId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new InvalidDataException("Provider publisher id is invalid.");
        }

        if (registration.PackageSha256.Length != 64 ||
            !registration.PackageSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Provider package SHA-256 is invalid.");
        }

        var normalizedInstallPath = ProviderPathSafety.NormalizeRelativePath(registration.InstallRelativePath);
        var expectedInstallPath = $"packages/{registration.Manifest.Id}/{registration.Manifest.Version}";
        if (!normalizedInstallPath.Equals(expectedInstallPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Provider install path does not match its manifest identity/version.");
        }

        _ = ProviderPathSafety.ResolveOwnedRelativePath(layout.Root, normalizedInstallPath);
        if (!Enum.IsDefined(registration.Health) || registration.ConsecutiveFailures is < 0 or > 1_000_000)
        {
            throw new InvalidDataException("Provider health metadata is invalid.");
        }

        if (registration.LastError is { Length: > 1024 } ||
            registration.LastError?.Any(character => character is '\0' or '\r' or '\n') == true)
        {
            throw new InvalidDataException("Provider health error is invalid.");
        }

        if (!registration.IsEnabled && registration.Health != ProviderHealthStatus.Disabled)
        {
            throw new InvalidDataException("A disabled provider must have Disabled health.");
        }
    }

    public static ProviderRegistration Clone(ProviderRegistration value) => value with
    {
        Manifest = value.Manifest with
        {
            Capabilities = value.Manifest.Capabilities.ToArray(),
            Permissions = value.Manifest.Permissions.ToArray(),
            NetworkHosts = value.Manifest.NetworkHosts.ToArray(),
            FileSha256 = value.Manifest.FileSha256.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal),
        },
    };
}
