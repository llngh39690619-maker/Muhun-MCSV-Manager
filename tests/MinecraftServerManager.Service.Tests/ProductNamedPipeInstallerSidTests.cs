using System.Security.Principal;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductNamedPipeInstallerSidTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-InstallerSidTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExactPersistedInstallerAccountSid_IsLoaded()
    {
        var layout = new ProductDataLayout(_root);
        layout.EnsureCreated();
        var expected = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Test user has no SID.");
        File.WriteAllText(
            Path.Combine(layout.Data, "installer-operator-sid.v1"),
            expected.Value + "\n");

        var actual = ProductNamedPipeFactory.ReadInstallerOperatorSid(layout);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("S-1-1-0")]
    [InlineData("not-a-sid")]
    public void BroadOrMalformedSid_IsRejected(string value)
    {
        var layout = new ProductDataLayout(_root);
        layout.EnsureCreated();
        File.WriteAllText(
            Path.Combine(layout.Root, ProductLocalIpcAccess.InstallerOperatorSidRelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar)),
            value + "\n");

        Assert.Throws<InvalidDataException>(() =>
            ProductNamedPipeFactory.ReadInstallerOperatorSid(layout));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
