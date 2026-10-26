namespace LoadingAI.Gate;

/// <summary>One category line of the change receipt.</summary>
public sealed class CategoryChanges
{
    public string Category { get; init; } = "";
    public int Added { get; init; }
    public int Modified { get; init; }
    public int Deleted { get; init; }
    /// <summary>Added and modified ids, capped. Deleted elements no longer exist, so they have no ids here.</summary>
    public IReadOnlyList<long> Ids { get; init; } = Array.Empty<long>();
}

/// <summary>What one turn changed in the model. Counts are exact; <see cref="Ids"/> are capped.</summary>
public sealed class TurnChangesSnapshot
{
    public int Added { get; init; }
    public int Modified { get; init; }
    public int Deleted { get; init; }
    public bool IdsTruncated { get; init; }
    public IReadOnlyList<CategoryChanges> ByCategory { get; init; } = Array.Empty<CategoryChanges>();

    public bool IsEmpty => Added == 0 && Modified == 0 && Deleted == 0;

    /// <summary>Every selectable id (added and modified) across categories, in category order.</summary>
    public IReadOnlyList<long> AllIds
        => ByCategory.SelectMany(c => c.Ids).Distinct().ToList();
}

/// <summary>
/// Collects what Revit reports while a turn's TransactionGroup is open and folds it into one receipt.
/// Revit reports each committed transaction separately, so one element can appear several times; the
/// receipt counts it once: added then modified is added, added then deleted is nothing, modified then
/// deleted is deleted. Pure; the host feeds it from <c>Application.DocumentChanged</c>.
/// </summary>
public sealed class TurnChangeSet
{
    public const int DefaultIdCap = 2000;
    public const string UncategorizedName = "Other";

    private enum Kind { Added, Modified }

    private readonly Dictionary<long, (Kind kind, string category)> _live = new();
    private readonly Dictionary<long, string> _deleted = new();
    private readonly object _lock = new();

    public void Added(long id, string? category)
    {
        lock (_lock)
        {
            // An id Revit reuses after a delete is a new element for this receipt.
            _deleted.Remove(id);
            _live[id] = (Kind.Added, Normalize(category));
        }
    }

    public void Modified(long id, string? category)
    {
        lock (_lock)
        {
            if (_deleted.ContainsKey(id)) return;
            if (_live.TryGetValue(id, out var cur))
                _live[id] = (cur.kind, cur.category == UncategorizedName ? Normalize(category) : cur.category);
            else
                _live[id] = (Kind.Modified, Normalize(category));
        }
    }

    public void Deleted(long id, string? categoryIfKnown = null)
    {
        lock (_lock)
        {
            if (_live.TryGetValue(id, out var cur))
            {
                _live.Remove(id);
                // Created and removed within the turn: net nothing to report.
                if (cur.kind == Kind.Added) return;
                _deleted[id] = cur.category;
                return;
            }
            _deleted[id] = Normalize(categoryIfKnown);
        }
    }

    public bool HasAny
    {
        get { lock (_lock) return _live.Count > 0 || _deleted.Count > 0; }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _live.Clear();
            _deleted.Clear();
        }
    }

    public TurnChangesSnapshot Snapshot(int idCap = DefaultIdCap)
    {
        lock (_lock)
        {
            var names = new HashSet<string>(_live.Values.Select(v => v.category), StringComparer.Ordinal);
            foreach (var c in _deleted.Values) names.Add(c);

            var truncated = false;
            var list = new List<CategoryChanges>();
            foreach (var name in names)
            {
                var inCat = _live.Where(kv => kv.Value.category == name).OrderBy(kv => kv.Key).ToList();
                var ids = inCat.Select(kv => kv.Key).ToList();
                if (ids.Count > idCap)
                {
                    truncated = true;
                    ids = ids.Take(idCap).ToList();
                }
                list.Add(new CategoryChanges
                {
                    Category = name,
                    Added = inCat.Count(kv => kv.Value.kind == Kind.Added),
                    Modified = inCat.Count(kv => kv.Value.kind == Kind.Modified),
                    Deleted = _deleted.Count(kv => kv.Value == name),
                    Ids = ids
                });
            }

            // Biggest first, "Other" last, so the receipt leads with what the user will recognise.
            var ordered = list
                .OrderBy(c => c.Category == UncategorizedName ? 1 : 0)
                .ThenByDescending(c => c.Added + c.Modified + c.Deleted)
                .ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new TurnChangesSnapshot
            {
                Added = _live.Count(kv => kv.Value.kind == Kind.Added),
                Modified = _live.Count(kv => kv.Value.kind == Kind.Modified),
                Deleted = _deleted.Count,
                IdsTruncated = truncated,
                ByCategory = ordered
            };
        }
    }

    private static string Normalize(string? category)
        => string.IsNullOrWhiteSpace(category) ? UncategorizedName : category!.Trim();
}
