namespace MinecraftServerManager.Data.Tests;

public sealed class ProductSequenceStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-SequenceTests",
        Guid.NewGuid().ToString("N"));
    private ProductSequenceStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var database = new ProductDatabase(Path.Combine(_directory, "product.db"));
        await database.InitializeAsync();
        _store = new ProductSequenceStore(database);
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentCallersReceiveUniqueMonotonicValues()
    {
        var values = await Task.WhenAll(
            Enumerable.Range(0, 100).Select(_ => _store.NextAsync("notification.event")));

        Assert.Equal(Enumerable.Range(1, 100).Select(value => (long)value), values.Order());
        Assert.Equal(101, await _store.NextAsync("notification.event"));
        Assert.Equal(1, await _store.NextAsync("audit.export"));
    }
}
