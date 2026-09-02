using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;
using MinecraftServerManager.ProviderHost;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

[Collection(ProductServiceHostCollection.Name)]
public sealed class ProductProviderServiceIntegrationTests
{
    [Fact]
    public async Task ProviderIpc_IsApi14VersionedAndUnavailableCoordinatorFailsClosed()
    {
        var state = new ProductServiceState(TimeProvider.System);
        state.Initialize(Guid.NewGuid());
        state.MarkReady();
        var processor = new ProductIpcMessageProcessor(state);
        var request = new ProductIpcRequest(
            ProductIpcProtocol.CurrentSchemaVersion,
            Guid.NewGuid(),
            ProductIpcProtocol.ProviderListMethod,
            ProductApiProtocol.MinimumSupportedVersion,
            ProductApiProtocol.CurrentVersion)
        {
            ListOffset = 0,
            ListLimit = 20,
        };

        var oldApi = await processor.ProcessAsync(
            request with { ClientMaximumApiVersion = new ProductApiVersion(1, 3) },
            default);
        var unavailable = await processor.ProcessAsync(request, default);

        Assert.False(oldApi.Success);
        Assert.Equal("protocol.method_version_unsupported", oldApi.Error?.Code);
        Assert.False(unavailable.Success);
        Assert.Equal("service.provider_unavailable", unavailable.Error?.Code);
        Assert.Null(unavailable.ProviderPage);
        Assert.Null(unavailable.ProviderPublisher);
    }

    [Fact]
    public async Task LocalApi_PinsInstallsDisablesUninstallsAndUnpinsSignedProvider()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var port = GetAvailableLoopbackPort();
        await using var application = ProductServiceApplication.Build(
        [
            $"--{ProductServiceOptions.SectionName}:DataRoot={layout.Root}",
            $"--{ProductServiceOptions.SectionName}:ExchangeRoot={layout.Root}.exchange",
            $"--{ProductServiceOptions.SectionName}:Port={port}",
            UniqueIpcPipeArgument(),
        ]);
        await application.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            client.DefaultRequestHeaders.Add(
                ProductLocalApiAuthentication.HeaderName,
                File.ReadAllText(Path.Combine(layout.Secrets, ProductLocalApiAuthenticator.FileName)).Trim());
            var root = $"{ProductApiProtocol.RestBasePath}/providers";

            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            const string publisherId = "muhun.test";
            using var pinned = await client.PutAsJsonAsync(
                $"{root}/publishers/{publisherId}",
                new ProductPinProviderPublisherRequest(
                    publisherId,
                    signer.ExportSubjectPublicKeyInfoPem()));
            Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
            Assert.DoesNotContain("PRIVATE KEY", await pinned.Content.ReadAsStringAsync());

            var package = CreatePackage(layout, signer, publisherId);
            using var installed = await client.PostAsJsonAsync($"{root}/install", package.Request);
            Assert.Equal(HttpStatusCode.Created, installed.StatusCode);
            var installedProvider = await installed.Content.ReadFromJsonAsync<ProductProviderSummary>();
            Assert.Equal(package.ProviderId, installedProvider?.Id);
            Assert.False(File.Exists(package.InboxPath));

            var listed = await client.GetFromJsonAsync<ProductProviderSummary[]>(root);
            Assert.Single(listed ?? []);
            Assert.False(listed![0].Enabled);

            using var enabled = await client.PutAsJsonAsync(
                $"{root}/{package.ProviderId}/enabled",
                new ProductProviderEnableRequest(true));
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            Assert.True((await enabled.Content.ReadFromJsonAsync<ProductProviderSummary>())?.Enabled);

            using var disabled = await client.PutAsJsonAsync(
                $"{root}/{package.ProviderId}/enabled",
                new ProductProviderEnableRequest(false));
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            Assert.False((await disabled.Content.ReadFromJsonAsync<ProductProviderSummary>())?.Enabled);

            using var cannotUnpin = await client.DeleteAsync($"{root}/publishers/{publisherId}");
            Assert.Equal(HttpStatusCode.Conflict, cannotUnpin.StatusCode);
            Assert.Contains("provider.operation_rejected", await cannotUnpin.Content.ReadAsStringAsync());

            using var removed = await client.DeleteAsync($"{root}/{package.ProviderId}");
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            Assert.Empty(await client.GetFromJsonAsync<ProductProviderSummary[]>(root) ?? []);

            using var unpinned = await client.DeleteAsync($"{root}/publishers/{publisherId}");
            Assert.Equal(HttpStatusCode.NoContent, unpinned.StatusCode);
            Assert.Empty(
                await client.GetFromJsonAsync<ProductTrustedProviderPublisherSummary[]>(
                    $"{root}/publishers") ?? []);
        }
        finally
        {
            await application.StopAsync();
        }
    }

    [Fact]
    public async Task LocalApi_RejectsUnsignedOrMismatchedPublisherPackage()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var port = GetAvailableLoopbackPort();
        await using var application = ProductServiceApplication.Build(
        [
            $"--{ProductServiceOptions.SectionName}:DataRoot={layout.Root}",
            $"--{ProductServiceOptions.SectionName}:ExchangeRoot={layout.Root}.exchange",
            $"--{ProductServiceOptions.SectionName}:Port={port}",
            UniqueIpcPipeArgument(),
        ]);
        await application.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            client.DefaultRequestHeaders.Add(
                ProductLocalApiAuthentication.HeaderName,
                File.ReadAllText(Path.Combine(layout.Secrets, ProductLocalApiAuthenticator.FileName)).Trim());
            var root = $"{ProductApiProtocol.RestBasePath}/providers";

            using var trustedSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var attackerSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            const string publisherId = "muhun.test";
            using var pinned = await client.PutAsJsonAsync(
                $"{root}/publishers/{publisherId}",
                new ProductPinProviderPublisherRequest(
                    publisherId,
                    trustedSigner.ExportSubjectPublicKeyInfoPem()));
            Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);

            var package = CreatePackage(layout, attackerSigner, publisherId);
            using var rejected = await client.PostAsJsonAsync($"{root}/install", package.Request);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            Assert.Contains("provider.signature_rejected", await rejected.Content.ReadAsStringAsync());
            Assert.Empty(await client.GetFromJsonAsync<ProductProviderSummary[]>(root) ?? []);
            Assert.True(File.Exists(package.InboxPath));
        }
        finally
        {
            await application.StopAsync();
        }
    }

    private static ProviderTestPackage CreatePackage(
        ProductDataLayout layout,
        ECDsa signer,
        string publisherId)
    {
        const string providerId = "muhun.test.runtime";
        const string version = "1.0.0";
        var inbox = Path.Combine(layout.Plugins, ProductProviderCoordinator.InboxDirectoryName);
        Directory.CreateDirectory(inbox);
        var packagePath = Path.Combine(inbox, $"{Guid.NewGuid():N}.mcsvp");
        var executable = new byte[] { 0x4d, 0x5a, 0x00, 0x01, 0x02, 0x03 };
        var executableDigest = Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant();
        var manifest = new ProductProviderManifest(
            ProductProviderManifestValidator.CurrentSchemaVersion,
            providerId,
            "Test runtime catalog",
            version,
            ProductApiProtocol.CurrentVersion,
            "provider.exe",
            [ProductProviderCapabilities.RuntimeCatalog],
            [ProductProviderPermissions.Http],
            ["example.org"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["provider.exe"] = executableDigest,
            });

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
            using var executableStream = executableEntry.Open();
            executableStream.Write(executable);
        }

        var packageInfo = new FileInfo(packagePath);
        var packageSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))).ToLowerInvariant();
        var payload = ProviderPackageSignatureFormat.CreatePayload(
            new ProviderPackageTrustContext(packageInfo.Name, packageInfo.Length, packageSha));
        byte[] signature;
        try
        {
            signature = signer.SignData(
                payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }

        try
        {
            return new ProviderTestPackage(
                providerId,
                packagePath,
                new ProductProviderInstallFromInboxRequest(
                    packageInfo.Name,
                    packageSha,
                    providerId,
                    version,
                    publisherId,
                    new ProductProviderDetachedSignature(
                        publisherId,
                        EcdsaProviderPackageTrustVerifier.SupportedAlgorithm,
                        Convert.ToBase64String(signature),
                        ProviderPackageSignatureFormat.CurrentVersion)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static int GetAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string UniqueIpcPipeArgument()
        => $"--{ProductServiceOptions.SectionName}:IpcPipeName=muhun.mcsv.test.{Guid.NewGuid():N}";

    private sealed record ProviderTestPackage(
        string ProviderId,
        string InboxPath,
        ProductProviderInstallFromInboxRequest Request);
}
