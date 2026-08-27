using System.Security.Cryptography;
using System.Runtime.Versioning;

namespace MinecraftServerManager.Data.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretVaultTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-VaultTests",
        Guid.NewGuid().ToString("N"));

    public WindowsDpapiSecretVaultTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task RoundTripAndDelete_DoesNotPersistReferenceOrPlaintext()
    {
        var vault = new WindowsDpapiSecretVault(_directory, Guid.NewGuid());
        const string reference = "discord:primary";
        const string value = "https://discord.com/api/webhooks/123456/not-plaintext";

        await vault.SetSecretAsync(reference, value);
        Assert.Equal(value, await vault.GetSecretAsync(reference));

        var path = Assert.Single(Directory.EnumerateFiles(_directory, "*.secret"));
        Assert.DoesNotContain("discord", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
        var raw = await File.ReadAllBytesAsync(path);
        Assert.DoesNotContain(value, System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);

        Assert.True(await vault.DeleteSecretAsync(reference));
        Assert.Null(await vault.GetSecretAsync(reference));
        Assert.False(await vault.DeleteSecretAsync(reference));
    }

    [Fact]
    public async Task DifferentInstallationEntropy_CannotDecryptEntry()
    {
        var first = new WindowsDpapiSecretVault(_directory, Guid.NewGuid());
        await first.SetSecretAsync("discord:primary", "sensitive-value");
        var second = new WindowsDpapiSecretVault(_directory, Guid.NewGuid());

        await Assert.ThrowsAsync<CryptographicException>(
            () => second.GetSecretAsync("discord:primary"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("Discord Uppercase")]
    [InlineData("a/b")]
    public async Task InvalidReference_IsRejected(string reference)
    {
        var vault = new WindowsDpapiSecretVault(_directory, Guid.NewGuid());

        await Assert.ThrowsAsync<ArgumentException>(
            () => vault.SetSecretAsync(reference, "value"));
    }
}
