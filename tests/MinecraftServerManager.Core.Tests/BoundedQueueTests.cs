using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class BoundedDropOldestQueueTests
{
    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedDropOldestQueue<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedDropOldestQueue<int>(-1));
    }

    [Fact]
    public void Enqueue_WhenFull_DropsOldestAndPreservesFifoOrder()
    {
        var queue = new BoundedDropOldestQueue<int>(3);

        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);
        queue.Enqueue(4);

        Assert.Equal(3, queue.Count);
        Assert.Equal([2, 3, 4], queue.Take(10));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Take_RemovesItemsInBatches()
    {
        var queue = new BoundedDropOldestQueue<int>(5);
        foreach (var value in Enumerable.Range(1, 5))
        {
            queue.Enqueue(value);
        }

        Assert.Equal([1, 2], queue.Take(2));
        Assert.Equal(3, queue.Count);
        Assert.Equal([3, 4], queue.Take(2));
        queue.Enqueue(6);
        Assert.Equal([5, 6], queue.Take(10));
        Assert.Empty(queue.Take(1));
    }

    [Fact]
    public void Take_RejectsNonPositiveMaximum()
    {
        var queue = new BoundedDropOldestQueue<int>(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Take(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Take(-1));
    }

    [Fact]
    public void ConcurrentEnqueueAndTake_NeverExceedsCapacity()
    {
        const int capacity = 64;
        var queue = new BoundedDropOldestQueue<int>(capacity);
        var capacityExceeded = 0;

        Parallel.For(0, 20_000, value =>
        {
            queue.Enqueue(value);
            if (queue.Count > capacity)
            {
                Interlocked.Exchange(ref capacityExceeded, 1);
            }

            if (value % 17 == 0)
            {
                queue.Take(3);
            }
        });

        Assert.Equal(0, capacityExceeded);
        Assert.InRange(queue.Count, 0, capacity);
        Assert.InRange(queue.Take(capacity).Count, 0, capacity);
        Assert.Equal(0, queue.Count);
    }
}

public sealed class BoundedLatestValueQueueTests
{
    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedLatestValueQueue<string, int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedLatestValueQueue<string, int>(-1));
    }

    [Fact]
    public void Enqueue_SameKeyKeepsLatestValueAndOriginalOrder()
    {
        var queue = new BoundedLatestValueQueue<string, int>(3);

        queue.Enqueue("alpha", 1);
        queue.Enqueue("beta", 2);
        queue.Enqueue("alpha", 3);

        Assert.Equal(2, queue.Count);
        Assert.Equal(
            [
                new KeyValuePair<string, int>("alpha", 3),
                new KeyValuePair<string, int>("beta", 2)
            ],
            queue.Take(3));
    }

    [Fact]
    public void Enqueue_NewKeyWhenFull_DropsOldestKey()
    {
        var queue = new BoundedLatestValueQueue<string, int>(3);
        queue.Enqueue("alpha", 1);
        queue.Enqueue("beta", 2);
        queue.Enqueue("gamma", 3);
        queue.Enqueue("alpha", 10);

        queue.Enqueue("delta", 4);

        Assert.Equal(
            [
                new KeyValuePair<string, int>("beta", 2),
                new KeyValuePair<string, int>("gamma", 3),
                new KeyValuePair<string, int>("delta", 4)
            ],
            queue.Take(10));
    }

    [Fact]
    public void Enqueue_WithEvictionResult_ReturnsTheDiscardedKeyIncludingDefaultValues()
    {
        var queue = new BoundedLatestValueQueue<int, string>(2);
        Assert.False(queue.Enqueue(0, "zero", out var firstEvicted));
        Assert.Equal(default, firstEvicted);
        Assert.False(queue.Enqueue(1, "one", out _));

        Assert.True(queue.Enqueue(2, "two", out var evicted));

        Assert.Equal(0, evicted);
        Assert.Equal([1, 2], queue.Take(2).Select(item => item.Key));
    }

    [Fact]
    public void Comparer_CoalescesEquivalentKeys()
    {
        var queue = new BoundedLatestValueQueue<string, int>(2, StringComparer.OrdinalIgnoreCase);

        queue.Enqueue("PlayerOne", 1);
        queue.Enqueue("PLAYERONE", 2);

        var item = Assert.Single(queue.Take(2));
        Assert.Equal("PlayerOne", item.Key);
        Assert.Equal(2, item.Value);
    }

    [Fact]
    public void Take_RemovesKeysInBatches()
    {
        var queue = new BoundedLatestValueQueue<int, string>(4);
        queue.Enqueue(1, "one");
        queue.Enqueue(2, "two");
        queue.Enqueue(3, "three");
        queue.Enqueue(4, "four");

        Assert.Equal([1, 2], queue.Take(2).Select(item => item.Key));
        Assert.Equal(2, queue.Count);
        Assert.Equal([3], queue.Take(1).Select(item => item.Key));
        queue.Enqueue(5, "five");
        Assert.Equal([4, 5], queue.Take(10).Select(item => item.Key));
        Assert.Empty(queue.Take(1));
    }

    [Fact]
    public void Take_RejectsNonPositiveMaximum()
    {
        var queue = new BoundedLatestValueQueue<string, int>(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Take(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Take(-1));
    }

    [Fact]
    public void Enqueue_RejectsNullKey()
    {
        var queue = new BoundedLatestValueQueue<string, int>(1);

        Assert.Throws<ArgumentNullException>(() => queue.Enqueue(null!, 1));
    }

    [Fact]
    public void ConcurrentEnqueueAndTake_NeverExceedsCapacity()
    {
        const int capacity = 64;
        var queue = new BoundedLatestValueQueue<int, int>(capacity);
        var capacityExceeded = 0;

        Parallel.For(0, 20_000, value =>
        {
            queue.Enqueue(value % 128, value);
            if (queue.Count > capacity)
            {
                Interlocked.Exchange(ref capacityExceeded, 1);
            }

            if (value % 19 == 0)
            {
                queue.Take(5);
            }
        });

        Assert.Equal(0, capacityExceeded);
        Assert.InRange(queue.Count, 0, capacity);
        Assert.InRange(queue.Take(capacity).Count, 0, capacity);
        Assert.Equal(0, queue.Count);
    }
}
