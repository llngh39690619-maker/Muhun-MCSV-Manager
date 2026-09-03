using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductConsoleRealtimeRegressionTests
{
    [Fact]
    public async Task ConcurrentWaitRegistrationAndAdd_DoesNotLoseWakeup()
    {
        const int raceCount = 64;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var index = 0; index < raceCount; index++)
        {
            var journal = new ProductConsoleJournal(capacity: 4);
            var serverId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var waiting = Task.Run(async () =>
            {
                await start.Task.WaitAsync(cancellation.Token);
                if ((index & 1) == 0)
                {
                    await Task.Yield();
                }

                return await journal.WaitForChangeAsync(
                    serverId,
                    afterCursor: 0,
                    limit: 4,
                    TimeSpan.FromMilliseconds(ProductConsoleContract.MaximumWaitTimeoutMilliseconds),
                    cancellation.Token);
            }, cancellation.Token);
            var adding = Task.Run(async () =>
            {
                await start.Task.WaitAsync(cancellation.Token);
                if ((index & 1) != 0)
                {
                    await Task.Yield();
                }

                journal.Add(
                    sessionId,
                    new ConsoleLine(DateTimeOffset.UtcNow, $"race-{index}"));
            }, cancellation.Token);

            start.TrySetResult();
            await adding;
            var page = await waiting;

            var entry = Assert.Single(page.Entries);
            Assert.Equal($"race-{index}", entry.Text);
            Assert.Equal(sessionId, entry.SessionId);
            Assert.Equal(1, page.NextCursor);
        }
    }

    [Fact]
    public async Task TimedOutWait_DoesNotDetachAnotherWaiterFromSharedGeneration()
    {
        var journal = new ProductConsoleJournal(capacity: 4);
        var serverId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var survivor = journal.WaitForChangeAsync(
            serverId,
            afterCursor: 0,
            limit: 4,
            TimeSpan.FromMilliseconds(ProductConsoleContract.MaximumWaitTimeoutMilliseconds));
        var expiring = journal.WaitForChangeAsync(
            serverId,
            afterCursor: 0,
            limit: 4,
            TimeSpan.FromMilliseconds(ProductConsoleContract.MinimumWaitTimeoutMilliseconds));

        var expiredPage = await expiring;
        Assert.Empty(expiredPage.Entries);
        Assert.False(survivor.IsCompleted);

        journal.Add(sessionId, new ConsoleLine(DateTimeOffset.UtcNow, "after-timeout"));

        var livePage = await survivor.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("after-timeout", Assert.Single(livePage.Entries).Text);
    }

    [Fact]
    public async Task CancelledWait_DoesNotCancelAnotherWaiterOrPoisonNextGeneration()
    {
        var journal = new ProductConsoleJournal(capacity: 4);
        var serverId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        using var cancelledCaller = new CancellationTokenSource();
        var cancelled = journal.WaitForChangeAsync(
            serverId,
            afterCursor: 0,
            limit: 4,
            TimeSpan.FromMilliseconds(ProductConsoleContract.MaximumWaitTimeoutMilliseconds),
            cancelledCaller.Token);
        var survivor = journal.WaitForChangeAsync(
            serverId,
            afterCursor: 0,
            limit: 4,
            TimeSpan.FromMilliseconds(ProductConsoleContract.MaximumWaitTimeoutMilliseconds));

        cancelledCaller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(survivor.IsCompleted);

        journal.Add(sessionId, new ConsoleLine(DateTimeOffset.UtcNow, "after-cancel"));
        var survivorPage = await survivor.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("after-cancel", Assert.Single(survivorPage.Entries).Text);

        var nextWait = journal.WaitForChangeAsync(
            serverId,
            afterCursor: survivorPage.NextCursor,
            limit: 4,
            TimeSpan.FromMilliseconds(ProductConsoleContract.MaximumWaitTimeoutMilliseconds));
        journal.Add(sessionId, new ConsoleLine(DateTimeOffset.UtcNow, "next-generation"));
        var nextPage = await nextWait.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("next-generation", Assert.Single(nextPage.Entries).Text);
        Assert.Equal(2, nextPage.NextCursor);
    }

    [Fact]
    public async Task RetentionOverflow_WaitReportsGapThenContinuesFromReturnedCursor()
    {
        var journal = new ProductConsoleJournal(capacity: 3);
        var serverId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        for (var index = 1; index <= 6; index++)
        {
            journal.Add(sessionId, new ConsoleLine(DateTimeOffset.UtcNow, $"line-{index}"));
        }

        var gapTask = journal.WaitForChangeAsync(
            serverId,
            afterCursor: 1,
            limit: 2,
            TimeSpan.FromSeconds(1));
        Assert.True(gapTask.IsCompletedSuccessfully);
        var gapPage = await gapTask;

        Assert.True(gapPage.HistoryGap);
        Assert.Equal(4, gapPage.OldestAvailableCursor);
        Assert.Equal([4L, 5L], gapPage.Entries.Select(entry => entry.Cursor));
        Assert.Equal(["line-4", "line-5"], gapPage.Entries.Select(entry => entry.Text));
        Assert.Equal(5, gapPage.NextCursor);

        var continuationTask = journal.WaitForChangeAsync(
            serverId,
            gapPage.NextCursor,
            limit: 2,
            TimeSpan.FromSeconds(1));
        Assert.True(continuationTask.IsCompletedSuccessfully);
        var continuation = await continuationTask;

        Assert.False(continuation.HistoryGap);
        Assert.Equal("line-6", Assert.Single(continuation.Entries).Text);
        Assert.Equal(6, continuation.NextCursor);
    }

    [Fact]
    public async Task FutureCursorAfterJournalReset_ReturnsImmediateGapAndCanResumeAtZero()
    {
        var journal = new ProductConsoleJournal(capacity: 4);
        var serverId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var resetTask = journal.WaitForChangeAsync(
            serverId,
            afterCursor: 500,
            limit: 4,
            TimeSpan.FromMilliseconds(ProductConsoleContract.MaximumWaitTimeoutMilliseconds));
        Assert.True(resetTask.IsCompletedSuccessfully);
        var reset = await resetTask;

        Assert.True(reset.HistoryGap);
        Assert.Empty(reset.Entries);
        Assert.Equal(1, reset.OldestAvailableCursor);
        Assert.Equal(0, reset.NextCursor);

        journal.Add(sessionId, new ConsoleLine(DateTimeOffset.UtcNow, "after-reset"));
        var resumedTask = journal.WaitForChangeAsync(
            serverId,
            reset.NextCursor,
            limit: 4,
            TimeSpan.FromMilliseconds(ProductConsoleContract.MaximumWaitTimeoutMilliseconds));
        Assert.True(resumedTask.IsCompletedSuccessfully);
        var resumed = await resumedTask;

        Assert.False(resumed.HistoryGap);
        Assert.Equal("after-reset", Assert.Single(resumed.Entries).Text);
        Assert.Equal(1, resumed.NextCursor);
    }

    [Fact]
    public async Task RetainedBurst_DrainsImmediateBoundedPagesWithoutDuplicates()
    {
        const int lineCount = 123;
        var journal = new ProductConsoleJournal(capacity: lineCount);
        var serverId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        for (var index = 1; index <= lineCount; index++)
        {
            journal.Add(sessionId, new ConsoleLine(DateTimeOffset.UtcNow, $"line-{index}"));
        }

        var cursors = new List<long>(lineCount);
        var cursor = 0L;
        while (cursors.Count < lineCount)
        {
            var pageTask = journal.WaitForChangeAsync(
                serverId,
                cursor,
                ProductConsoleContract.MaximumPageSize,
                TimeSpan.FromSeconds(1));
            Assert.True(pageTask.IsCompletedSuccessfully);
            var page = await pageTask;
            Assert.False(page.HistoryGap);
            Assert.InRange(page.Entries.Count, 1, ProductConsoleContract.MaximumPageSize);

            cursors.AddRange(page.Entries.Select(entry => entry.Cursor));
            Assert.True(page.NextCursor > cursor);
            cursor = page.NextCursor;
        }

        Assert.Equal(Enumerable.Range(1, lineCount).Select(index => (long)index), cursors);
        Assert.Equal(lineCount, cursor);
    }
}
