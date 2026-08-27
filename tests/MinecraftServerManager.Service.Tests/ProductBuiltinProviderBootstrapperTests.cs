using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;
using MinecraftServerManager.Data;
using MinecraftServerManager.ProviderHost;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductBuiltinProviderBootstrapperTests
{
    [Fact]
    public async Task SignedDeployment_InstallsEnablesAndRevalidatesThroughSharedRegistry()
    {
        var productLayout = ProductServerRegistryTests.CreateLayout();
        productLayout.EnsureCreated();
        var deploymentRoot = Path.Combine(productLayout.Root, "release-provider");
        Directory.CreateDirectory(deploymentRoot);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateDeployment(deploymentRoot, signer);

        var hostLayout = new ProviderHostLayout(productLayout.Plugins);
        var registry = new ProviderRegistry(hostLayout);
        var trust = new ProductProviderPublisherTrustStore(hostLayout);
        await registry.LoadAsync();
        await trust.LoadAsync();
        var database = new ProductDatabase(Path.Combine(productLayout.Data, "product.v1.db"));
        await database.InitializeAsync();
        var bootstrapper = new ProductBuiltinProviderBootstrapper(
            new ProductBuiltinProviderDeploymentOptions(true, deploymentRoot),
            hostLayout,
            registry,
            trust,
            new ProviderPackageInstaller(hostLayout, registry, trust),
            new ProductSecurityAuditStore(database),
            TimeProvider.System);

        await bootstrapper.EnsureInstalledAsync();
        await bootstrapper.EnsureInstalledAsync();

        Assert.True(registry.TryGet(ProductFirstPartyProviderIdentities.CatalogProviderId, out var registration));
        Assert.True(registration.IsEnabled);
        Assert.Equal(ProductFirstPartyProviderIdentities.PublisherId, registration.PublisherId);
        Assert.Single(trust.List());
        await ProviderPackageIntegrityVerifier.VerifyAsync(hostLayout, registration);

        var executablePath = Path.Combine(
            hostLayout.Root,
            registration.InstallRelativePath.Replace('/', Path.DirectorySeparatorChar),
            "provider.exe");
        await File.WriteAllBytesAsync(executablePath, [0x00, 0x01]);
        await Assert.ThrowsAsync<CryptographicException>(
            () => bootstrapper.EnsureInstalledAsync());
    }

    [Fact]
    public async Task RequiredDeployment_MissingDirectoryFailsClosed()
    {
        var productLayout = ProductServerRegistryTests.CreateLayout();
        productLayout.EnsureCreated();
        var hostLayout = new ProviderHostLayout(productLayout.Plugins);
        var registry = new ProviderRegistry(hostLayout);
        var trust = new ProductProviderPublisherTrustStore(hostLayout);
        await registry.LoadAsync();
        await trust.LoadAsync();
        var database = new ProductDatabase(Path.Combine(productLayout.Data, "product.v1.db"));
        await database.InitializeAsync();
        var missing = Path.Combine(productLayout.Root, "missing-provider");
        var bootstrapper = new ProductBuiltinProviderBootstrapper(
            new ProductBuiltinProviderDeploymentOptions(true, missing),
            hostLayout,
            registry,
            trust,
            new ProviderPackageInstaller(hostLayout, registry, trust),
            new ProductSecurityAuditStore(database),
            TimeProvider.System);

        await Assert.ThrowsAsync<InvalidDataException>(() => bootstrapper.EnsureInstalledAsync());
    }

    [Fact]
    public void FormalReleaseLayout_ResolvesSignedProviderBesideServicePayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcsv-formal-provider-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var version = Path.Combine(root, "versions", "1.0.0");
            var service = Path.Combine(version, "service-win-x64");
            var provider = Path.Combine(
                version,
                "providers",
                ProductBuiltinProviderBootstrapper.DeploymentDirectoryName);
            Directory.CreateDirectory(service);
            Directory.CreateDirectory(provider);

            var resolved = ProductBuiltinProviderBootstrapper.ResolveDeploymentRoot(service, configuredRoot: null);

            Assert.Equal(Path.GetFullPath(provider), resolved);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void FormalReleaseLayout_RejectsProviderNestedInsideServicePayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcsv-ambiguous-provider-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var version = Path.Combine(root, "versions", "1.0.0");
            var service = Path.Combine(version, "service-win-x64");
            Directory.CreateDirectory(Path.Combine(
                service,
                "providers",
                ProductBuiltinProviderBootstrapper.DeploymentDirectoryName));
            Directory.CreateDirectory(Path.Combine(
                version,
                "providers",
                ProductBuiltinProviderBootstrapper.DeploymentDirectoryName));

            Assert.Throws<InvalidDataException>(() =>
                ProductBuiltinProviderBootstrapper.ResolveDeploymentRoot(service, configuredRoot: null));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void FormalReleaseLayout_RejectsDirectOnlyLegacyDeployment()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcsv-wrong-provider-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = Path.Combine(root, "versions", "1.0.0", "service-win-x64");
            Directory.CreateDirectory(Path.Combine(
                service,
                "providers",
                ProductBuiltinProviderBootstrapper.DeploymentDirectoryName));

            Assert.Throws<InvalidDataException>(() =>
                ProductBuiltinProviderBootstrapper.ResolveDeploymentRoot(service, configuredRoot: null));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PublisherTrustStore_RejectsPrivateKeyPemWithoutPersistingIt()
    {
        var productLayout = ProductServerRegistryTests.CreateLayout();
        productLayout.EnsureCreated();
        var hostLayout = new ProviderHostLayout(productLayout.Plugins);
        var trust = new ProductProviderPublisherTrustStore(hostLayout);
        await trust.LoadAsync();
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privatePem = signer.ExportPkcs8PrivateKeyPem();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => trust.PinAsync("muhun.private", privatePem));

        Assert.Empty(trust.List());
        Assert.False(File.Exists(trust.StorePath));
    }

    private static void CreateDeployment(string root, ECDsa signer)
    {
        var executable = new byte[] { 0x4d, 0x5a, 0x00, 0x01, 0x02, 0x03 };
        var executableSha = Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant();
        var manifest = new ProductProviderManifest(
            ProductProviderManifestValidator.CurrentSchemaVersion,
            ProductFirstPartyProviderIdentities.CatalogProviderId,
            "Muhun built-in catalogue",
            "1.0.0",
            ProductApiProtocol.CurrentVersion,
            "provider.exe",
            [ProductProviderCapabilities.ModpackCatalog],
            [ProductProviderPermissions.Http],
            ["api.feed-the-beast.com", "api.modrinth.com"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["provider.exe"] = executableSha,
            });
        var packagePath = Path.Combine(root, ProductBuiltinProviderBootstrapper.PackageFileName);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry(
                ProviderPackageInstaller.ManifestFileName,
                CompressionLevel.NoCompression);
            using (var stream = manifestEntry.Open())
            {
                JsonSerializer.Serialize(stream, manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }

            var executableEntry = archive.CreateEntry("provider.exe", CompressionLevel.NoCompression);
            using var stream2 = executableEntry.Open();
            stream2.Write(executable);
        }

        var packageInfo = new FileInfo(packagePath);
        var packageSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))).ToLowerInvariant();
        var trustPayload = ProviderPackageSignatureFormat.CreatePayload(
            new ProviderPackageTrustContext(packageInfo.Name, packageInfo.Length, packageSha));
        byte[] signature;
        try
        {
            signature = signer.SignData(
                trustPayload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(trustPayload);
        }

        try
        {
            var publicKey = signer.ExportSubjectPublicKeyInfo();
            string publicKeySha;
            try
            {
                publicKeySha = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(publicKey);
            }

            File.WriteAllText(
                Path.Combine(root, ProductBuiltinProviderBootstrapper.PublicKeyFileName),
                signer.ExportSubjectPublicKeyInfoPem());
            var descriptor = new ProductBuiltinProviderBootstrapper.BuiltinProviderDeploymentDescriptor(
                1,
                ProductBuiltinProviderBootstrapper.PackageFileName,
                ProductBuiltinProviderBootstrapper.PublicKeyFileName,
                publicKeySha,
                packageSha,
                ProductFirstPartyProviderIdentities.CatalogProviderId,
                "1.0.0",
                ProductFirstPartyProviderIdentities.PublisherId,
                new ProviderPackageSignature(
                    ProductFirstPartyProviderIdentities.PublisherId,
                    EcdsaProviderPackageTrustVerifier.SupportedAlgorithm,
                    Convert.ToBase64String(signature),
                    ProviderPackageSignatureFormat.CurrentVersion));
            File.WriteAllText(
                Path.Combine(root, ProductBuiltinProviderBootstrapper.DescriptorFileName),
                JsonSerializer.Serialize(
                    descriptor,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
