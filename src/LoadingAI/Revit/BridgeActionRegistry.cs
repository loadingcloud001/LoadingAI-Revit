
namespace LoadingAI.Revit;

public enum BridgeActionState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Abandoned
}

public sealed record BridgeActionSnapshot(
    string Id,
    BridgeActionState State,
    string? Error,
    bool WasAbandoned,
    DateTime UpdatedUtc);

/// <summary>
/// Tracks the execution state of every Revit bridge action. Caller timeout or
/// cancellation abandons the wait but never silently removes queued Revit work.
/// </summary>
public sealed class BridgeActionRegistry
{
    private const int MaxRecords = 500;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private sealed class Entry
    {
        public string Id = "";
        public BridgeActionState State = BridgeActionState.Queued;
        public string? Error;
        public bool WasAbandoned;
        public DateTime CreatedUtc;
        public DateTime UpdatedUtc;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Queue<string> _order = new();

    public int Count { get { lock (_lock) return _entries.Count; } }

    public string Begin(DateTime? createdUtc = null)
    {
        var now = createdUtc ?? DateTime.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            PruneUnlocked(now);
            var entry = new Entry
            {
                Id = id,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            _entries[id] = entry;
            _order.Enqueue(id);
            PruneUnlocked(now);
            return id;
        }
    }

    public BridgeActionSnapshot Get(string id)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry))
                throw new KeyNotFoundException($"Unknown bridge action '{id}'.");
            return Snapshot(entry);
        }
    }

    public bool TryGet(string id, out BridgeActionSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                snapshot = Snapshot(entry);
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    public void MarkRunning(string id, DateTime? updatedUtc = null)
        => Update(id, updatedUtc, entry => entry.State = BridgeActionState.Running);

    public void MarkSucceeded(string id, DateTime? updatedUtc = null)
        => Complete(id, BridgeActionState.Succeeded, null, updatedUtc);

    public void MarkFailed(string id, string? error, DateTime? updatedUtc = null)
        => Complete(id, BridgeActionState.Failed, error, updatedUtc);

    public void MarkAbandoned(string id, string? error, DateTime? updatedUtc = null)
        => Update(id, updatedUtc, entry =>
        {
            entry.WasAbandoned = true;
            if (IsTerminal(entry.State))
                return;
            entry.State = BridgeActionState.Abandoned;
            entry.Error = error;
        });

    public bool TryCancelQueued(string id, string? error, DateTime? updatedUtc = null)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.State != BridgeActionState.Queued)
                return false;
            entry.State = BridgeActionState.Cancelled;
            entry.Error = error;
            entry.UpdatedUtc = updatedUtc ?? DateTime.UtcNow;
            return true;
        }
    }

    public void Prune(DateTime? nowUtc = null)
    {
        lock (_lock) PruneUnlocked(nowUtc ?? DateTime.UtcNow);
    }

    private void Complete(string id, BridgeActionState state, string? error, DateTime? updatedUtc)
        => Update(id, updatedUtc, entry =>
        {
            if (entry.State == BridgeActionState.Cancelled)
                return;
            entry.State = state;
            entry.Error = error;
        });

    private void Update(string id, DateTime? updatedUtc, Action<Entry> update)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry))
                throw new KeyNotFoundException($"Unknown bridge action '{id}'.");
            update(entry);
            entry.UpdatedUtc = updatedUtc ?? DateTime.UtcNow;
            PruneUnlocked(entry.UpdatedUtc);
        }
    }

    private void PruneUnlocked(DateTime now)
    {
        foreach (var pair in _entries.Where(p => IsTerminal(p.Value.State) && now - p.Value.UpdatedUtc > MaxAge).ToList())
            _entries.Remove(pair.Key);

        while (_order.Count > 0 && !_entries.ContainsKey(_order.Peek()))
            _order.Dequeue();

        if (_entries.Count <= MaxRecords)
            return;

        foreach (var id in _order.Where(id => _entries.TryGetValue(id, out var e) && IsTerminal(e.State)).ToList())
        {
            if (_entries.Count <= MaxRecords)
                break;
            _entries.Remove(id);
        }

        while (_order.Count > 0 && !_entries.ContainsKey(_order.Peek()))
            _order.Dequeue();
    }

    private static bool IsTerminal(BridgeActionState state)
        => state is BridgeActionState.Succeeded
            or BridgeActionState.Failed
            or BridgeActionState.Cancelled
            or BridgeActionState.Abandoned;

    private static BridgeActionSnapshot Snapshot(Entry entry)
        => new(entry.Id, entry.State, entry.Error, entry.WasAbandoned, entry.UpdatedUtc);
}
