using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.Contracts.Tests;

public sealed class ProductProviderManifestTests
{
    [Fact]
    public void MinimalNotificationProvider_IsAccepted()
    {
        var manifest = new ProductProviderManifest(
            ProductProviderManifestValidator.CurrentSchemaVersion,
            "muhun.discord",
            "Muhun Discord Provider",
            "1.0.0",
            ProductApiProtocol.CurrentVersion,
            "bin/Muhun.Discord.Provider.exe",
            [ProductProviderCapabilities.Notification],
            [
                ProductProviderPermissions.Http,
                ProductProviderPermissions.ReadConfiguration,
                ProductProviderPermissions.WriteState,
                ProductProviderPermissions.EmitNotifications,
            ],
            ["discord.com"],
            new Dictionary<string, string>
            {
                ["bin/Muhun.Discord.Provider.exe"] = new string('a', 64),
            });

        Assert.True(ProductProviderManifestValidator.Validate(manifest).IsValid);
    }

    [Theory]
    [InlineData("../provider.exe")]
    [InlineData("C:\\provider.exe")]
    [InlineData("provider.dll")]
    [InlineData("bin/../../provider.exe")]
    public void UnsafeEntryPoint_IsRejected(string entryPoint)
    {
        var manifest = ValidManifest() with { EntryPoint = entryPoint };

        Assert.False(ProductProviderManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void WildcardNetworkHost_IsRejected()
    {
        var manifest = ValidManifest() with { NetworkHosts = ["*.discord.com"] };

        Assert.False(ProductProviderManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void NetworkHostWithoutHttpPermission_IsRejected()
    {
        var manifest = ValidManifest() with
        {
            Permissions = [ProductProviderPermissions.ReadConfiguration],
        };

        Assert.False(ProductProviderManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void ProviderRequiringNewerMinorApi_IsRejected()
    {
        var manifest = ValidManifest() with { ApiVersion = new ProductApiVersion(1, 99) };

        Assert.False(ProductProviderManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void LegacySchemaWithoutSignedFileTable_IsRejected()
    {
        var manifest = ValidManifest() with
        {
            SchemaVersion = 1,
            FileSha256 = new Dictionary<string, string>(),
        };

        var result = ProductProviderManifestValidator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("schema", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("digest", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("../provider.exe")]
    [InlineData("bin\\provider.exe")]
    [InlineData("CON/provider.exe")]
    public void UnsafeSignedPayloadPath_IsRejected(string path)
    {
        var manifest = ValidManifest() with
        {
            FileSha256 = new Dictionary<string, string> { [path] = new string('a', 64) },
        };

        Assert.False(ProductProviderManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void SignedDigestMustBeCanonicalLowercaseAndContainExactEntryPoint()
    {
        var uppercase = ValidManifest() with
        {
            FileSha256 = new Dictionary<string, string> { ["provider.exe"] = new string('A', 64) },
        };
        var wrongCasePath = ValidManifest() with
        {
            FileSha256 = new Dictionary<string, string> { ["Provider.exe"] = new string('a', 64) },
        };

        Assert.False(ProductProviderManifestValidator.Validate(uppercase).IsValid);
        Assert.False(ProductProviderManifestValidator.Validate(wrongCasePath).IsValid);
    }

    [Fact]
    public void CapabilityWithoutItsRequiredPermission_IsRejected()
    {
        var manifest = ValidManifest() with
        {
            Capabilities = [ProductProviderCapabilities.Notification],
            Permissions = [ProductProviderPermissions.Http],
        };

        Assert.False(ProductProviderManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void NetworkHostsMustUseCanonicalLowercaseAscii()
    {
        var manifest = ValidManifest() with { NetworkHosts = ["API.Example.com"] };

        Assert.False(ProductProviderManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void NumericPrereleaseIdentifiersWithLeadingZerosAreRejected()
    {
        var manifest = ValidManifest() with { Version = "1.0.0-alpha.01" };

        Assert.False(ProductProviderManifestValidator.Validate(manifest).IsValid);
    }

    private static ProductProviderManifest ValidManifest() => new(
        ProductProviderManifestValidator.CurrentSchemaVersion,
        "muhun.discord",
        "Muhun Discord Provider",
        "1.0.0-alpha.1",
        ProductApiProtocol.CurrentVersion,
        "provider.exe",
        [ProductProviderCapabilities.Notification],
        [ProductProviderPermissions.Http],
        ["discord.com"],
        new Dictionary<string, string> { ["provider.exe"] = new string('a', 64) });
}
