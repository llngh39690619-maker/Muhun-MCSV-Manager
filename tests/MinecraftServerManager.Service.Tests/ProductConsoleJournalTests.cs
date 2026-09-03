using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductConsoleJournalTests
{
    [Fact]
    public void BoundedJournal_ReportsHistoryGapAndStableCursor()
    {
        var journal = new ProductConsoleJournal(capacity: 2);
        var sessionId = Guid.NewGuid();
        journal.Add(sessionId, new ConsoleLine(DateTimeOffset.UtcNow, "one"));
        journal.Add(sessionId, new ConsoleLine(DateTimeOffset.UtcNow, "two"));
        journal.Add(sessionId, new ConsoleLine(DateTimeOffset.UtcNow, "three"));

        var page = journal.Read(Guid.NewGuid(), afterCursor: 0, limit: 50);

        Assert.True(page.HistoryGap);
        Assert.Equal(2, page.OldestAvailableCursor);
        Assert.Equal(["two", "three"], page.Entries.Select(entry => entry.Text));
        Assert.Equal(3, page.NextCursor);
    }

    [Fact]
    public void OversizedLine_IsTruncatedBeforeRetentionAndSerialization()
    {
        var journal = new ProductConsoleJournal(capacity: 2);
        journal.Add(
            Guid.NewGuid(),
            new ConsoleLine(DateTimeOffset.UtcNow, new string('x', 10_000)));

        var entry = Assert.Single(journal.Read(Guid.NewGuid(), 0, 1).Entries);

        Assert.True(entry.TextTruncated);
        Assert.Equal(ProductConsoleJournal.MaximumTextCharacters, entry.Text.Length);
    }

    [Fact]
    public void FutureCursor_IsDetectedAfterServiceJournalReset()
    {
        var page = new ProductConsoleJournal(capacity: 10)
            .Read(Guid.NewGuid(), afterCursor: 500, limit: 10);

        Assert.True(page.HistoryGap);
        Assert.Equal(0, page.NextCursor);
        Assert.Empty(page.Entries);
    }

    [Fact]
    public async Task Wait_ReturnsImmediatelyWhenCursorCanReplayRetainedEntries()
    {
        var journal = new ProductConsoleJournal(capacity: 10);
        var serverId = Guid.NewGuid();
        journal.Add(Guid.NewGuid(), new ConsoleLine(DateTimeOffset.UtcNow, "already available"));

        var page = await journal.WaitForChangeAsync(
            serverId,
            afterCursor: 0,
            limit: 10,
            TimeSpan.FromSeconds(1));

        Assert.Equal("already available", Assert.Single(page.Entries).Text);
        Assert.Equal(1, page.NextCursor);
    }

    [Fact]
    public async Task Wait_WakesWhenCursorAdvancesWithoutPolling()
    {
        var journal = new ProductConsoleJournal(capacity: 10);
        var serverId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var waiting = journal.WaitForChangeAsync(
            serverId,
            afterCursor: 0,
            limit: 10,
            TimeSpan.FromSeconds(2));
        await Task.Yield();
        journal.Add(sessionId, new ConsoleLine(DateTimeOffset.UtcNow, "live"));

        var page = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("live", Assert.Single(page.Entries).Text);
    }

    [Fact]
    public async Task Wait_ExpiresAsSuccessfulEmptyCursorPage()
    {
        var journal = new ProductConsoleJournal(capacity: 10);

        var page = await journal.WaitForChangeAsync(
            Guid.NewGuid(),
            afterCursor: 0,
            limit: 10,
            TimeSpan.FromMilliseconds(ProductConsoleContract.MinimumWaitTimeoutMilliseconds));

        Assert.Empty(page.Entries);
        Assert.Equal(0, page.NextCursor);
        Assert.False(page.HistoryGap);
    }

    [Fact]
    public async Task Wait_PropagatesCallerCancellation()
    {
        var journal = new ProductConsoleJournal(capacity: 10);
        using var cancellation = new CancellationTokenSource();
        var waiting = journal.WaitForChangeAsync(
            Guid.NewGuid(),
            afterCursor: 0,
            limit: 10,
            TimeSpan.FromSeconds(2),
            cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task MaximumConsolePage_FitsBoundedIpcResponseFrame()
    {
        var journal = new ProductConsoleJournal(capacity: 50);
        var serverId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        for (var index = 0; index < 50; index++)
        {
            journal.Add(
                sessionId,
                new ConsoleLine(DateTimeOffset.UtcNow, new string('x', 10_000))
                {
                    Severity = ConsoleLineSeverity.Error,
                    DiagnosticId = Guid.NewGuid(),
                });
        }

        var response = new ProductIpcResponse(
            ProductIpcProtocol.CurrentSchemaVersion,
            Guid.NewGuid(),
            true,
            null,
            null)
        {
            Console = journal.Read(serverId, 0, ProductConsoleJournal.MaximumPageSize),
        };
        await using var stream = new MemoryStream();

        await ProductIpcFrameCodec.WriteResponseAsync(stream, response, CancellationToken.None);

        Assert.InRange(stream.Length, 1, ProductIpcProtocol.MaximumFrameBytes);
    }

    [Fact]
    public async Task MaximumServerListPage_FitsBoundedIpcResponseFrame()
    {
        var servers = Enumerable.Range(0, 50)
            .Select(index => new ProductServerSummary(
                Guid.NewGuid(),
                new string('服', 128),
                ProductServerState.Running,
                25565 + index,
                "NeoForge",
                new string('版', 64)))
            .ToArray();
        var response = new ProductIpcResponse(
            ProductIpcProtocol.CurrentSchemaVersion,
            Guid.NewGuid(),
            true,
            null,
            null)
        {
            ServerPage = new ProductServerListPage(0, 50, true, servers),
        };
        await using var stream = new MemoryStream();

        await ProductIpcFrameCodec.WriteResponseAsync(stream, response, CancellationToken.None);

        Assert.InRange(stream.Length, 1, ProductIpcProtocol.MaximumFrameBytes);
    }
}
