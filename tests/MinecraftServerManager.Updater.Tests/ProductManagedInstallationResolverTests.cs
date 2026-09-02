using MinecraftServerManager.Updater;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductManagedInstallationResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "muhun-managed-install-resolver-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParseServiceImagePath_AcceptsOnlyExactQuotedSeparatedStorageBindings()
    {
        var executable = CreateServiceExecutable();
        var dataRoot = Path.Combine(_root, "service", "beta");
        var exchangeRoot = Path.Combine(_root, "exchange", "beta");

        var parsed = ProductManagedInstallationResolver.ParseServiceImagePath(
            $"\"{executable}\" \"--Mcsv:Service:DataRoot={dataRoot}\" " +
            $"\"--Mcsv:Service:ExchangeRoot={exchangeRoot}\"");

        Assert.Equal(executable, parsed.ServicePath, ignoreCase: true);
        Assert.Equal(dataRoot, parsed.DataRoot, ignoreCase: true);
        Assert.Equal(exchangeRoot, parsed.ExchangeRoot, ignoreCase: true);
    }

    [Theory]
    [InlineData("{0} \"--Mcsv:Service:DataRoot=C:\\ProgramData\\Muhun\\MCSV\"")]
    [InlineData("\"{0}\" --Mcsv:Service:DataRoot=C:\\ProgramData\\Muhun\\MCSV")]
    [InlineData("\"{0}\" \"--Mcsv:Service:DataRoot=C:\\ProgramData\\Muhun\\MCSV\" --extra")]
    [InlineData("\"{0}\" \"--Mcsv:Service:DataRoot=C:\\ProgramData\\Muhun\\MCSV\"\"bad")]
    [InlineData("\"{0}\" \"--Mcsv:Service:DataRoot=C:\\Program Files\\MCSV\\service\\beta\"")]
    [InlineData("\"{0}\" \"--Mcsv:Service:DataRoot=C:\\Program Files\\MCSV\\service\\beta\" \"--Mcsv:Service:ExchangeRoot=C:\\outside\"")]
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
