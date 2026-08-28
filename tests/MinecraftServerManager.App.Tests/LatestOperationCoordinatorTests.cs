using System.Collections.Specialized;
using MinecraftServerManager.App.Infrastructure;

namespace MinecraftServerManager.App.Tests;

public sealed class LatestOperationCoordinatorTests
{
    [Fact]
    public async Task RunLatestAsync_CancelsSupersededOperationAndInvalidatesItsGeneration()
    {
        await using var coordinator = new LatestOperationCoordinator();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        long firstGeneration = 0;
        var first = coordinator.RunLatestAsync<int>(async context =>
        {
            firstGeneration = context.Generation;
            firstStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return 1;
        });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await coordinator.RunLatestAsync(
            context => Task.FromResult((context.Generation, Value: 2)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(coordinator.IsCurrent(firstGeneration));
        Assert.True(coordinator.IsCurrent(second.Generation));
        Assert.Equal(2, second.Value.Value);
    }

    [Fact]
    public async Task CancelCurrent_InvalidatesAlreadyCompletedGeneration()
    {
        await using var coordinator = new LatestOperationCoordinator();
        var result = await coordinator.RunLatestAsync(_ => Task.FromResult("current"));
        Assert.True(coordinator.IsCurrent(result.Generation));

        coordinator.CancelCurrent();

        Assert.False(coordinator.IsCurrent(result.Generation));
    }

    [Fact]
    public async Task DisposeAsync_CancelsAndWaitsForOutstandingOperationCleanup()
    {
        var coordinator = new LatestOperationCoordinator();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFinished = false;
        var operation = coordinator.RunLatestAsync<int>(async context =>
        {
            try
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                return 1;
            }
            finally
            {
                await Task.Yield();
                cleanupFinished = true;
            }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.DisposeAsync();

        Assert.True(cleanupFinished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public void BatchObservableCollection_ReplaceAllPublishesOneResetForLargeProjection()
    {
        var collection = new BatchObservableCollection<int>();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, args) => notifications.Add(args);

        var changed = collection.ReplaceAll(Enumerable.Range(0, 4_096));

        Assert.True(changed);
        Assert.Equal(4_096, collection.Count);
        var reset = Assert.Single(notifications);
        Assert.Equal(NotifyCollectionChangedAction.Reset, reset.Action);
    }
}
