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
