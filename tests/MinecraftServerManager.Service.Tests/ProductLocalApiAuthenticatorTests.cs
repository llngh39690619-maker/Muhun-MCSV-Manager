using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductLocalApiAuthenticatorTests
{
    [Fact]
    public void GeneratedCapability_IsStableAndFailsClosed()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var first = new ProductLocalApiAuthenticator(layout);
        first.Initialize();
        var token = File.ReadAllText(first.FilePath).Trim();

        var second = new ProductLocalApiAuthenticator(layout);
        second.Initialize();

        Assert.Equal(64, token.Length);
        Assert.Equal(
            ProductLocalApiAuthenticationResult.Missing,
            second.Authenticate(null));
        Assert.Equal(
            ProductLocalApiAuthenticationResult.Rejected,
            second.Authenticate(new string('0', 64)));
        Assert.Equal(
            ProductLocalApiAuthenticationResult.Authenticated,
            second.Authenticate(token));
    }

    [Fact]
    public void CorruptStoredCapability_IsRejected()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        File.WriteAllText(
            Path.Combine(layout.Secrets, ProductLocalApiAuthenticator.FileName),
            "weak");

        Assert.Throws<InvalidDataException>(
            () => new ProductLocalApiAuthenticator(layout).Initialize());
    }
}
