namespace MinecraftServerManager.Core.Services;

/// <summary>
/// A thread-safe FIFO queue of distinct keys and their latest values. Updating an existing key
/// replaces its value without changing its original queue position. Enqueuing a new key into a
/// full queue discards the oldest key.
/// </summary>
public sealed class BoundedLatestValueQueue<TKey, TValue>
    where TKey : notnull
{
    private readonly object _sync = new();
    private readonly LinkedList<TKey> _keyOrder = new();
    private readonly Dictionary<TKey, TValue> _entries;

    public BoundedLatestValueQueue(
        int capacity,
        IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _entries = new Dictionary<TKey, TValue>(comparer);
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public void Enqueue(TKey key, TValue value)
        => Enqueue(key, value, out _);

    /// <summary>
    /// Enqueues the latest value and reports the key discarded when a new key exceeds capacity.
    /// Updating an existing key never evicts another entry.
    /// </summary>
    public bool Enqueue(TKey key, TValue value, out TKey evictedKey)
    {
        ArgumentNullException.ThrowIfNull(key);
        evictedKey = default!;

        lock (_sync)
        {
            var evicted = false;
            if (_entries.ContainsKey(key))
            {
                _entries[key] = value;
                return false;
            }

            if (_entries.Count == Capacity)
            {
                var oldestNode = _keyOrder.First
                    ?? throw new InvalidOperationException("The keyed queue order is inconsistent.");
                _keyOrder.RemoveFirst();
                _entries.Remove(oldestNode.Value);
                evictedKey = oldestNode.Value;
                evicted = true;
            }

            _keyOrder.AddLast(key);
            _entries.Add(key, value);
            return evicted;
        }
    }

    /// <summary>
    /// Atomically removes and returns up to <paramref name="maximumItems"/> oldest keys with
    /// the latest value observed for each key.
    /// </summary>
    public IReadOnlyList<KeyValuePair<TKey, TValue>> Take(int maximumItems)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumItems, 1);

        lock (_sync)
        {
            var takeCount = Math.Min(maximumItems, _entries.Count);
            if (takeCount == 0)
            {
                return Array.Empty<KeyValuePair<TKey, TValue>>();
            }

            var result = new KeyValuePair<TKey, TValue>[takeCount];
            for (var index = 0; index < takeCount; index++)
            {
                var oldestNode = _keyOrder.First
                    ?? throw new InvalidOperationException("The keyed queue order is inconsistent.");
                var key = oldestNode.Value;
                var value = _entries[key];
                result[index] = new KeyValuePair<TKey, TValue>(key, value);
                _keyOrder.RemoveFirst();
                _entries.Remove(key);
            }

            return result;
        }
    }
}
