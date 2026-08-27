namespace MinecraftServerManager.Core.Runtime;

/// <summary>
/// A thread-safe ring buffer used to cap retained console history.
/// </summary>
internal sealed class BoundedLogBuffer<T>
{
    private readonly T[] _items;
    private readonly object _sync = new();
    private int _start;
    private int _count;

    public BoundedLogBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public void Add(T item)
    {
        lock (_sync)
        {
            var index = (_start + _count) % _items.Length;

            if (_count == _items.Length)
            {
                _items[_start] = item;
                _start = (_start + 1) % _items.Length;
                return;
            }

            _items[index] = item;
            _count++;
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_sync)
        {
            var snapshot = new T[_count];
            for (var index = 0; index < _count; index++)
            {
                snapshot[index] = _items[(_start + index) % _items.Length];
            }

            return snapshot;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_items);
            _start = 0;
            _count = 0;
        }
    }
}
