namespace MinecraftServerManager.GameClient.Tests;

public sealed class MinecraftClientInstallationIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-client-identity-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadOrCreate_IsStableAcrossRestarts()
    {
        var path = Path.Combine(_root, "installation.id");

        var first = MinecraftClientInstallationIdentity.LoadOrCreate(path);
        var second = MinecraftClientInstallationIdentity.LoadOrCreate(path);

        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void LoadOrCreate_RejectsCorruptIdentity()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "installation.id");
        File.WriteAllText(path, "not-a-guid");

        Assert.Throws<InvalidDataException>(() =>
            MinecraftClientInstallationIdentity.LoadOrCreate(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
