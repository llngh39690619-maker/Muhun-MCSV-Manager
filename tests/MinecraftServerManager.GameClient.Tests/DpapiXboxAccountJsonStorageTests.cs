using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class DpapiXboxAccountJsonStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-account-vault-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Storage_RoundTripsWithoutWritingRefreshTokenInPlaintext()
    {
        var path = Path.Combine(_root, "accounts.v1.bin");
        const string secret = "refresh-token-that-must-never-be-plaintext";
        var storage = new DpapiXboxAccountJsonStorage(path, Guid.NewGuid());

        storage.Write(
            new JsonObject
            {
                ["account"] = "player",
                ["refreshToken"] = secret,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var raw = File.ReadAllBytes(path);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        var loaded = storage.ReadAsJsonNode();
        Assert.Equal(secret, loaded["refreshToken"]?.GetValue<string>());
    }

    [Fact]
    public void Storage_IsBoundToInstallationId()
    {
        var path = Path.Combine(_root, "accounts.v1.bin");
        new DpapiXboxAccountJsonStorage(path, Guid.NewGuid()).Write(
            new JsonObject { ["value"] = "secret" },
            new JsonSerializerOptions());
        var otherInstallation = new DpapiXboxAccountJsonStorage(path, Guid.NewGuid());

        Assert.Throws<CryptographicException>(() => otherInstallation.ReadAsJsonNode());
    }

    [Fact]
    public void AuthenticationService_StartsWithNoAccountsWithoutNetworkAccess()
    {
        var service = new MicrosoftMinecraftAuthenticationService(
            Path.Combine(_root, "accounts.v1.bin"),
            Guid.NewGuid());

        Assert.Empty(service.GetAccounts());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
