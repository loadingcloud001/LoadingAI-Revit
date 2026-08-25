using System.Collections.Concurrent;

namespace LoadingAI.Revit;

public sealed class QueryCache
{
    private readonly ConcurrentDictionary<string, (object Value, DateTime Expires)> _store = new();
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(3);

    public bool TryGet<T>(string key, out T value)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow < entry.Expires && entry.Value is T t)
            {
                value = t;
                return true;
            }
            _store.TryRemove(key, out _);
        }
        value = default!;
        return false;
    }

    public void Set<T>(string key, T value)
    {
        _store[key] = (value!, DateTime.UtcNow + _ttl);
    }

    public void Invalidate()
    {
        _store.Clear();
    }

    public string BuildKey(string tool, string docPath, string extra = "")
        => $"{tool}|{docPath}|{extra}";
}
