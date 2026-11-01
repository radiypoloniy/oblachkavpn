namespace Oblachka;

// Вытесняется только восстанавливаемое представление; модель и история пользователя остаются.
internal sealed class BoundedCache<TKey, TValue>(int capacity, IEqualityComparer<TKey>? comparer = null) where TKey : notnull
{
    private readonly object gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> entries = new(comparer);
    private readonly LinkedList<(TKey Key, TValue Value)> order = new();
    internal int Count { get { lock (gate) return entries.Count; } }

    internal TValue GetOrAdd(TKey key, Func<TKey, TValue> create)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var found))
            {
                order.Remove(found); order.AddLast(found);
                return found.Value.Value;
            }
        }
        var value = create(key);
        lock (gate)
        {
            if (entries.TryGetValue(key, out var found)) return found.Value.Value;
            var node = order.AddLast((key, value));
            entries.Add(key, node);
            while (entries.Count > capacity && order.First is { } first)
            {
                entries.Remove(first.Value.Key); order.RemoveFirst();
            }
            return value;
        }
    }
}
