namespace MinecraftServerManager.Core.Services;

/// <summary>
/// A thread-safe FIFO queue that retains at most <see cref="Capacity"/> items.
/// Enqueuing into a full queue discards the oldest item.
/// </summary>
public sealed class BoundedDropOldestQueue<T>
{
    private readonly object _sync = new();
    private readonly Queue<T> _items = new();

    public BoundedDropOldestQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _items.Count;
            }
        }
    }

    public void Enqueue(T item)
    {
        lock (_sync)
        {
            if (_items.Count == Capacity)
            {
                _items.Dequeue();
            }

            _items.Enqueue(item);
        }
    }

    /// <summary>Atomically removes and returns up to <paramref name="maximumItems"/> oldest items.</summary>
    public IReadOnlyList<T> Take(int maximumItems)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumItems, 1);

        lock (_sync)
        {
            var takeCount = Math.Min(maximumItems, _items.Count);
            if (takeCount == 0)
            {
                return Array.Empty<T>();
            }

            var result = new T[takeCount];
            for (var index = 0; index < takeCount; index++)
            {
                result[index] = _items.Dequeue();
            }

            return result;
        }
    }
}
