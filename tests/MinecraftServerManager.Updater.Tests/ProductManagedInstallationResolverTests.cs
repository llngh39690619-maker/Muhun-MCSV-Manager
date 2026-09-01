using MinecraftServerManager.Updater;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductManagedInstallationResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "muhun-managed-install-resolver-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParseServiceImagePath_AcceptsOnlyExactQuotedDataRootBinding()
    {
        var executable = CreateServiceExecutable();
        var dataRoot = Path.Combine(_root, "data root");

        var parsed = ProductManagedInstallationResolver.ParseServiceImagePath(
            $"\"{executable}\" \"--Mcsv:Service:DataRoot={dataRoot}\"");

        Assert.Equal(executable, parsed.ServicePath, ignoreCase: true);
        Assert.Equal(dataRoot, parsed.DataRoot, ignoreCase: true);
    }

    [Theory]
    [InlineData("{0} \"--Mcsv:Service:DataRoot=C:\\ProgramData\\Muhun\\MCSV\"")]
    [InlineData("\"{0}\" --Mcsv:Service:DataRoot=C:\\ProgramData\\Muhun\\MCSV")]
    [InlineData("\"{0}\" \"--Mcsv:Service:DataRoot=C:\\ProgramData\\Muhun\\MCSV\" --extra")]
    [InlineData("\"{0}\" \"--Mcsv:Service:DataRoot=C:\\ProgramData\\Muhun\\MCSV\"\"bad")]
    public void ParseServiceImagePath_RejectsUnmanagedCommandShape(string format)
    {
        var executable = CreateServiceExecutable();
        var command = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, executable);

        Assert.ThrowsAny<Exception>(() => ProductManagedInstallationResolver.ParseServiceImagePath(command));
    }

    private string CreateServiceExecutable()
    {
        var path = Path.Combine(_root, "Muhun MCSV Service.exe");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(path, "MZ"u8.ToArray());
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
