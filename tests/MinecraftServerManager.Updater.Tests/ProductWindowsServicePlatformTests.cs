namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductWindowsServicePlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "muhun-windows-service-platform-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConfigureAndRestart_StopsVerifiesRegistrationStartsAndWaitsForRunning()
    {
        var (servicePath, dataRoot, exchangeRoot) = CreateManagedInputs();
        var commands = new List<string[]>();
        var states = new Queue<int>([1, 4]);
        var expectedImagePath = $"\"{servicePath}\" \"--Mcsv:Service:DataRoot={dataRoot}\" " +
                                $"\"--Mcsv:Service:ExchangeRoot={exchangeRoot}\"";
        var platform = new ProductWindowsServicePlatform(
            (arguments, _, _) =>
            {
                commands.Add([.. arguments]);
                return Task.FromResult(string.Empty);
            },
            () => new ProductWindowsServiceRegistration(
                expectedImagePath,
                @"NT SERVICE\MuhunMCSV",
                2),
            () => states.Dequeue());

        await platform.ConfigureAndRestartAsync(
            servicePath,
            dataRoot,
            exchangeRoot,
            CancellationToken.None);

        Assert.Collection(
            commands,
            command => Assert.Equal(["stop", "MuhunMCSV"], command),
            command => Assert.Equal(
                [
                    "config",
                    "MuhunMCSV",
                    "binPath=",
                    expectedImagePath,
                    "start=",
                    "delayed-auto",
                ],
                command),
            command => Assert.Equal(["start", "MuhunMCSV"], command));
        Assert.Empty(states);
    }

    [Fact]
    public async Task ConfigureAndRestart_RegistrationMismatchFailsBeforeStartingService()
    {
        var (servicePath, dataRoot, exchangeRoot) = CreateManagedInputs();
        var commands = new List<string[]>();
        var platform = new ProductWindowsServicePlatform(
            (arguments, _, _) =>
            {
                commands.Add([.. arguments]);
                return Task.FromResult(string.Empty);
            },
            () => new ProductWindowsServiceRegistration(
                $"\"{servicePath}\" \"--Mcsv:Service:DataRoot={dataRoot}\" " +
                $"\"--Mcsv:Service:ExchangeRoot={exchangeRoot}\"",
                @"LocalSystem",
                2),
            () => 1);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            platform.ConfigureAndRestartAsync(
                servicePath,
                dataRoot,
                exchangeRoot,
                CancellationToken.None));

        Assert.Equal(2, commands.Count);
        Assert.Equal("stop", commands[0][0]);
        Assert.Equal("config", commands[1][0]);
        Assert.DoesNotContain(commands, command => command[0] == "start");
    }

    [Fact]
    public async Task ConfigureAndRestart_StartPendingThenRunning_ResendsStopBeforeConfiguring()
    {
        var (servicePath, dataRoot, exchangeRoot) = CreateManagedInputs();
        var commands = new List<(string[] Arguments, bool AllowsTransientStopFailure)>();
        var states = new Queue<int>([2, 4, 3, 1, 4]);
        var expectedImagePath = $"\"{servicePath}\" \"--Mcsv:Service:DataRoot={dataRoot}\" " +
                                $"\"--Mcsv:Service:ExchangeRoot={exchangeRoot}\"";
        var platform = new ProductWindowsServicePlatform(
            (arguments, allowsTransientStopFailure, _) =>
            {
                commands.Add(([.. arguments], allowsTransientStopFailure));
                return Task.FromResult(string.Empty);
            },
            () => new ProductWindowsServiceRegistration(
                expectedImagePath,
                @"NT SERVICE\MuhunMCSV",
                2),
            () => states.Dequeue());

        await platform.ConfigureAndRestartAsync(
            servicePath,
            dataRoot,
            exchangeRoot,
            CancellationToken.None);

        Assert.Equal(4, commands.Count);
        Assert.Equal(["stop", "MuhunMCSV"], commands[0].Arguments);
        Assert.True(commands[0].AllowsTransientStopFailure);
        Assert.Equal(["stop", "MuhunMCSV"], commands[1].Arguments);
        Assert.True(commands[1].AllowsTransientStopFailure);
        Assert.Equal("config", commands[2].Arguments[0]);
        Assert.False(commands[2].AllowsTransientStopFailure);
        Assert.Equal(["start", "MuhunMCSV"], commands[3].Arguments);
        Assert.False(commands[3].AllowsTransientStopFailure);
        Assert.Empty(states);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup on Windows CI.
        }
    }

    private (string ServicePath, string DataRoot, string ExchangeRoot) CreateManagedInputs()
    {
        var serviceRoot = Path.Combine(_root, "version", "service-win-x64");
        var servicePath = Path.Combine(serviceRoot, "Muhun MCSV Service.exe");
        var dataRoot = Path.Combine(_root, "service", "beta");
        var exchangeRoot = Path.Combine(_root, "exchange", "beta");
        Directory.CreateDirectory(serviceRoot);
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(exchangeRoot);
        File.WriteAllBytes(servicePath, "MZ"u8.ToArray());
        File.WriteAllText(
            Path.Combine(dataRoot, ".muhun-mcsv-data-root"),
            "muhun.mcsv.manager:1\n");
        return (servicePath, dataRoot, exchangeRoot);
    }
}
