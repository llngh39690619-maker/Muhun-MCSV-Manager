using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftServerManager.Contracts.Plugins;
using MinecraftServerManager.Data;
using MinecraftServerManager.ProviderHost;

namespace MinecraftServerManager.Service;

public sealed record ProductBuiltinProviderDeploymentOptions(
    bool Required,
    string? DeploymentRoot = null);

/// <summary>
/// Imports the release-signed first-party provider through the exact same trust, package, registry,
/// integrity, and process boundary used by external providers.
/// </summary>
public sealed class ProductBuiltinProviderBootstrapper(
    ProductBuiltinProviderDeploymentOptions options,
    ProviderHostLayout providerLayout,
    ProviderRegistry registry,
    ProductProviderPublisherTrustStore trustStore,
    ProviderPackageInstaller installer,
    ProductSecurityAuditStore auditStore,
    TimeProvider timeProvider)
{
    public const string DeploymentDirectoryName = "muhun.catalog";
    public const string DescriptorFileName = "deployment.v1.json";
    public const string PackageFileName = "muhun.catalog.mcsvp";
    public const string PublicKeyFileName = "publisher-public.pem";
    public const long MaximumDescriptorBytes = 64 * 1024;

    public string DeploymentRoot => ResolveDeploymentRoot(
        AppContext.BaseDirectory,
        options.DeploymentRoot);

    /// <summary>
    /// Formal releases place Service and signed provider deployment as siblings beneath the
    /// immutable version root. Development layouts retain the historical in-process-directory
    /// location. A provider nested beneath a formal Service payload is a wrong layout and is
    /// rejected instead of being accepted by fallback or precedence.
    /// </summary>
    public static string ResolveDeploymentRoot(string serviceBaseDirectory, string? configuredRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceBaseDirectory);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        var serviceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serviceBaseDirectory));
        EnsureDeploymentPathSafe(serviceRoot);
        var direct = Path.Combine(serviceRoot, "providers", DeploymentDirectoryName);
        var serviceDirectoryName = Path.GetFileName(serviceRoot);
        if (!serviceDirectoryName.Equals("service-win-x64", StringComparison.OrdinalIgnoreCase))
        {
            return direct;
        }

        var versionRoot = Directory.GetParent(serviceRoot)?.FullName
                          ?? throw new InvalidDataException(
                              "Formal Service directory has no version root.");
        var volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(versionRoot)!);
        if (Path.TrimEndingDirectorySeparator(versionRoot)
            .Equals(volumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Formal provider version root cannot be a volume root.");
        }

        EnsureDeploymentPathSafe(versionRoot);
        var sibling = Path.Combine(versionRoot, "providers", DeploymentDirectoryName);
        if (Directory.Exists(direct))
        {
            throw new InvalidDataException(
                "Formal provider deployment must be a sibling of the Service payload.");
        }

        if (Directory.Exists(sibling))
        {
            EnsureDeploymentPathSafe(sibling);
        }

        return sibling;
    }

    public async Task EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DeploymentRoot))
        {
            if (options.Required)
            {
                throw new InvalidDataException("Required first-party provider deployment is missing.");
            }

            return;
        }

        EnsureDeploymentPathSafe(DeploymentRoot);
        var descriptorPath = Path.Combine(DeploymentRoot, DescriptorFileName);
        var packagePath = Path.Combine(DeploymentRoot, PackageFileName);
        var publicKeyPath = Path.Combine(DeploymentRoot, PublicKeyFileName);
        EnsureRegularFile(descriptorPath, MaximumDescriptorBytes);
        EnsureRegularFile(packagePath, ProviderPackageInstaller.MaximumPackageBytes);
        EnsureRegularFile(publicKeyPath, 16 * 1024);

        var descriptor = await ReadDescriptorAsync(descriptorPath, cancellationToken).ConfigureAwait(false);
        ValidateDescriptor(descriptor);
        var publicKeyPem = await File.ReadAllTextAsync(publicKeyPath, cancellationToken).ConfigureAwait(false);
        var publicKeySha = ComputePublicKeySha256(publicKeyPem);
        if (!string.Equals(publicKeySha, descriptor.PublicKeySha256, StringComparison.Ordinal))
        {
            throw new CryptographicException("First-party provider publisher key fingerprint is invalid.");
        }

        var correlationId = Guid.NewGuid();
        RequireAcceptedAudit(correlationId);
        try
        {
            await trustStore.PinAsync(
                    descriptor.ExpectedPublisherId,
                    publicKeyPem,
                    cancellationToken)
                .ConfigureAwait(false);

            var shouldEnable = true;
            if (registry.TryGet(descriptor.ExpectedProviderId, out var current))
            {
                if (!string.Equals(
                        current.PublisherId,
                        descriptor.ExpectedPublisherId,
                        StringComparison.Ordinal))
                {
                    throw new CryptographicException(
                        "First-party provider identity is already owned by another publisher.");
                }

                shouldEnable = current.IsEnabled;
                var comparison = ProviderSemanticVersionComparer.Compare(
                    current.Manifest.Version,
                    descriptor.ExpectedVersion);
                if (comparison > 0)
                {
                    await ProviderPackageIntegrityVerifier.VerifyAsync(
                            providerLayout,
                            current,
                            cancellationToken)
                        .ConfigureAwait(false);
                    TryOutcomeAudit("succeeded", "provider_bootstrap_newer_retained", correlationId);
                    return;
                }

                if (comparison == 0)
                {
                    if (!string.Equals(
                            current.PackageSha256,
                            descriptor.ExpectedSha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new CryptographicException(
                            "First-party provider version is immutable but its package digest changed.");
                    }

                    await ProviderPackageIntegrityVerifier.VerifyAsync(
                            providerLayout,
                            current,
                            cancellationToken)
                        .ConfigureAwait(false);
                    TryOutcomeAudit("succeeded", "provider_bootstrap_verified", correlationId);
                    return;
                }
            }

            var result = await installer.InstallAsync(
                    new ProviderPackageInstallRequest(
                        packagePath,
                        descriptor.ExpectedSha256,
                        descriptor.ExpectedProviderId,
                        descriptor.ExpectedVersion,
                        descriptor.ExpectedPublisherId,
                        descriptor.Signature),
                    cancellationToken)
                .ConfigureAwait(false);
            if (shouldEnable && !result.Registration.IsEnabled)
            {
                await registry.SetEnabledAsync(
                        descriptor.ExpectedProviderId,
                        true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            TryOutcomeAudit("succeeded", "provider_bootstrap_installed", correlationId);
        }
        catch
        {
            TryOutcomeAudit("failed", "provider_bootstrap_failed", correlationId);
            throw;
        }
    }

    private static async Task<BuiltinProviderDeploymentDescriptor> ReadDescriptorAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions { MaxDepth = 12 },
                cancellationToken)
            .ConfigureAwait(false);
        RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<BuiltinProviderDeploymentDescriptor>(JsonOptions)
               ?? throw new InvalidDataException("First-party provider deployment descriptor is empty.");
    }

    private static void ValidateDescriptor(BuiltinProviderDeploymentDescriptor descriptor)
    {
        if (descriptor.SchemaVersion != 1 ||
            descriptor.PackageFileName != PackageFileName ||
            descriptor.PublicKeyFileName != PublicKeyFileName ||
            descriptor.ExpectedProviderId != ProductFirstPartyProviderIdentities.CatalogProviderId ||
            descriptor.ExpectedPublisherId != ProductFirstPartyProviderIdentities.PublisherId ||
            descriptor.ExpectedVersion is null or { Length: < 1 or > 96 } ||
            descriptor.ExpectedSha256 is null or { Length: not 64 } ||
            !descriptor.ExpectedSha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            descriptor.PublicKeySha256 is null or { Length: not 64 } ||
            !descriptor.PublicKeySha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            descriptor.Signature is null ||
            descriptor.Signature.PublisherId != descriptor.ExpectedPublisherId ||
            descriptor.Signature.Algorithm != EcdsaProviderPackageTrustVerifier.SupportedAlgorithm ||
            descriptor.Signature.FormatVersion != ProviderPackageSignatureFormat.CurrentVersion)
        {
            throw new InvalidDataException("First-party provider deployment descriptor is invalid.");
        }
    }

    private static string ComputePublicKeySha256(string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize != 256)
            {
                throw new CryptographicException("First-party provider publisher key must use P-256.");
            }

            var publicKey = key.ExportSubjectPublicKeyInfo();
            try
            {
                return Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(publicKey);
            }
        }
        catch (ArgumentException error)
        {
            throw new CryptographicException("First-party provider publisher key is invalid.", error);
        }
    }

    private static void EnsureDeploymentPathSafe(string root)
    {
        var fullPath = Path.GetFullPath(root);
        var volumeRoot = Path.GetPathRoot(fullPath)
                         ?? throw new InvalidDataException("Provider deployment path has no volume root.");
        var current = volumeRoot;
        RejectReparse(current);
        foreach (var segment in Path.GetRelativePath(volumeRoot, fullPath)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparse(current);
        }
    }

    private static void EnsureRegularFile(string path, long maximumBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 || file.Length > maximumBytes ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("First-party provider deployment file is missing or unsafe.");
        }
    }

    private static void RejectReparse(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("First-party provider deployment cannot contain reparse points.");
        }
    }

    private void RequireAcceptedAudit(Guid correlationId)
    {
        if (!TryOutcomeAudit("accepted", "provider_bootstrap_requested", correlationId))
        {
            throw new InvalidOperationException(
                "First-party provider bootstrap was rejected because security audit is unavailable.");
        }
    }

    private bool TryOutcomeAudit(string outcome, string reason, Guid correlationId)
        => auditStore.TryAppend(new ProductSecurityAuditEntry(
            Guid.NewGuid(),
            timeProvider.GetUtcNow().ToUniversalTime(),
            "provider.bootstrap",
            outcome,
            Username: null,
            PermissionCode: "provider.manage",
            ServerId: null,
            reason,
            correlationId));

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "First-party provider descriptor contains duplicate JSON properties.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 12,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public sealed record BuiltinProviderDeploymentDescriptor(
        int SchemaVersion,
        string PackageFileName,
        string PublicKeyFileName,
        string PublicKeySha256,
        string ExpectedSha256,
        string ExpectedProviderId,
        string ExpectedVersion,
        string ExpectedPublisherId,
        ProviderPackageSignature Signature);
}
