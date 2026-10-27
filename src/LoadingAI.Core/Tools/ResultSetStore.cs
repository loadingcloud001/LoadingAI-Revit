namespace LoadingAI.Tools;

/// <summary>Element-id lists a chat saved as @r1, @r2… (P2). Per chat session; Clear session empties it.</summary>
public sealed class ResultSetStore
{
    public const int MaxSets = 20;
    public const int MaxTotalIds = 50_000;

    private readonly List<(int N, IReadOnlyList<string> Ids)> _sets = new();
    private int _next = 1;
    private readonly object _lock = new();

    /// <summary>Saves the ids (deduplicated, order kept) and returns "@rN", or null when there are none.</summary>
    public string? Save(IReadOnlyList<string> ids)
    {
        var clean = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().Take(MaxTotalIds).ToList();
        if (clean.Count == 0) return null;
        lock (_lock)
        {
            var n = _next++;
            _sets.Add((n, clean));
            while (_sets.Count > MaxSets || (_sets.Count > 1 && _sets.Sum(s => s.Ids.Count) > MaxTotalIds))
                _sets.RemoveAt(0);
            return "@r" + n;
        }
    }

    public bool TryGet(string handle, out IReadOnlyList<string> ids)
    {
        ids = Array.Empty<string>();
        if (handle == null || !handle.StartsWith("@r") || !int.TryParse(handle.Substring(2), out var n)) return false;
        lock (_lock)
        {
            foreach (var s in _sets)
                if (s.N == n) { ids = s.Ids; return true; }
        }
        return false;
    }

    public IReadOnlyList<string> Handles
    {
        get { lock (_lock) return _sets.Select(s => $"@r{s.N} ({s.Ids.Count})").ToList(); }
    }

    public void Clear() { lock (_lock) { _sets.Clear(); _next = 1; } }
}
