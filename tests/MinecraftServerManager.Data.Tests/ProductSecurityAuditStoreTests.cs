namespace MinecraftServerManager.Data.Tests;

public sealed class ProductSecurityAuditStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-AuditTests",
        Guid.NewGuid().ToString("N"));
    private ProductSecurityAuditStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var database = new ProductDatabase(Path.Combine(_directory, "product.db"));
        await database.InitializeAsync();
        _store = new ProductSecurityAuditStore(database);
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
    public async Task ValidDecision_IsDurableAndBounded()
    {
        var entry = CreateEntry();

        Assert.True(_store.TryAppend(entry));

        var saved = Assert.Single(await _store.ReadRecentAsync(10));
        Assert.Equal(entry, saved);
    }

    [Theory]
    [InlineData("contains whitespace")]
    [InlineData("password=super-secret")]
    [InlineData("line\nbreak")]
    public async Task UnsafeReason_IsRejectedWithoutWriting(string reasonCode)
    {
        Assert.False(_store.TryAppend(CreateEntry() with { ReasonCode = reasonCode }));
        Assert.Empty(await _store.ReadRecentAsync(10));
    }

    [Fact]
    public void DuplicateAuditIdentifier_FailsClosed()
    {
        var entry = CreateEntry();

        Assert.True(_store.TryAppend(entry));
        Assert.False(_store.TryAppend(entry));
    }

    [Fact]
    public async Task Prune_AppliesAgeBoundaryAndRetainsRecentDecisions()
    {
        var old = CreateEntry() with { OccurredAtUtc = DateTimeOffset.UtcNow.AddDays(-400) };
        var recent = CreateEntry();
        Assert.True(_store.TryAppend(old));
        Assert.True(_store.TryAppend(recent));

        Assert.Equal(
            1,
            await _store.PruneAsync(DateTimeOffset.UtcNow.AddDays(-365), 1_000));

        Assert.Equal(recent.AuditId, Assert.Single(await _store.ReadRecentAsync(10)).AuditId);
    }

    private static ProductSecurityAuditEntry CreateEntry()
        => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "server.mutation",
            "accepted",
            "operator01",
            "server.start",
            Guid.NewGuid(),
            "authorization.granted",
            Guid.NewGuid());
}
