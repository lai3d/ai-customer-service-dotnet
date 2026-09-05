namespace CustomerService.Support;

/// <summary>
/// A map that forgets its least recently used entries once full. Not thread-safe: callers
/// hold their own lock, because what they do around the lookup has to be atomic with it.
///
/// A map keyed by conversation id that nothing ever removes from is a memory leak with a
/// long fuse -- it grows with traffic and is only noticed as a slow heap climb weeks later.
/// Both the budget and the ticket table sit on this.
/// </summary>
public sealed class BoundedLru<TKey, TValue>(int capacity) where TKey : notnull
{
    readonly Dictionary<TKey, LinkedListNode<(TKey key, TValue value)>> map = new();
    readonly LinkedList<(TKey key, TValue value)> order = new();
    readonly int capacity = capacity <= 0 ? 10_000 : capacity;

    public int Count => map.Count;

    public bool TryGet(TKey key, out TValue value)
    {
        if (map.TryGetValue(key, out var node))
        {
            order.Remove(node);
            order.AddFirst(node);
            value = node.Value.value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>Returns the entry for the key, creating it -- and evicting the oldest -- if absent.</summary>
    public TValue GetOrAdd(TKey key, Func<TValue> create)
    {
        if (TryGet(key, out var existing)) return existing;
        while (map.Count >= capacity && order.Last is { } oldest)
        {
            order.RemoveLast();
            map.Remove(oldest.Value.key);
        }
        var value = create();
        var node = new LinkedListNode<(TKey, TValue)>((key, value));
        order.AddFirst(node);
        map[key] = node;
        return value;
    }
}
