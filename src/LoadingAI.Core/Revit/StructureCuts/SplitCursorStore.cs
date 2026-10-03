using System;
using System.Collections.Generic;
using System.Linq;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>One slice of a frozen host list.</summary>
public sealed class CursorPage
{
    public CursorPage(IReadOnlyList<string> ids, int offset, int total, bool reused)
    {
        Ids = ids;
        Offset = offset;
        Total = total;
        Reused = reused;
    }

    /// <summary>The ids on this page. Some may no longer exist; the caller skips those and still counts them.</summary>
    public IReadOnlyList<string> Ids { get; }

    public int Offset { get; }

    /// <summary>The length of the whole frozen list.</summary>
    public int Total { get; }

    /// <summary>True when the list was taken by an earlier call and not collected again.</summary>
    public bool Reused { get; }
}

/// <summary>
/// Makes <c>continueFrom</c> mean the same thing on every call. A write run deletes and re-creates
/// walls and floors, so a list collected afresh on each call loses members from the front and every
/// unprocessed one shifts up; an offset into it then skips as many hosts as were re-created. The store
/// keeps the ids collected when a run starts (offset 0) and pages through that list on the following
/// calls. Offset 0 always collects again, so "start over" works. A list lives for
/// <see cref="DefaultLifetime"/> after its last use, and the store keeps at most a handful.
/// </summary>
public sealed class SplitCursorStore
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(30);
    public const int DefaultCapacity = 8;

    private sealed class Entry
    {
        public Entry(List<string> ids, DateTime now)
        {
            Ids = ids;
            LastUsedUtc = now;
        }

        public List<string> Ids { get; }

        public DateTime LastUsedUtc { get; set; }
    }

    private readonly object _gate = new object();
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;
    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

    public SplitCursorStore(Func<DateTime>? clock = null, TimeSpan? lifetime = null, int capacity = DefaultCapacity)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _lifetime = lifetime ?? DefaultLifetime;
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>One list per command, document, scope and explicit id list, so two tools never share a cursor.</summary>
    public static string KeyFor(string command, string document, string scope, IReadOnlyList<string> ids)
        => string.Join("|", command, document, scope ?? "", string.Join(",", ids));

    public CursorPage Page(string key, int offset, int limit, Func<IReadOnlyList<string>> collect)
    {
        lock (_gate)
        {
            var now = _clock();
            foreach (var stale in _entries.Where(e => now - e.Value.LastUsedUtc > _lifetime).Select(e => e.Key).ToList())
                _entries.Remove(stale);

            var start = Math.Max(0, offset);
            Entry? entry = null;
            var reused = start > 0 && _entries.TryGetValue(key, out entry);
            if (!reused)
            {
                entry = new Entry(collect().ToList(), now);
                _entries[key] = entry;
                while (_entries.Count > _capacity)
                {
                    var oldest = _entries.OrderBy(e => e.Value.LastUsedUtc).First().Key;
                    _entries.Remove(oldest);
                }
            }

            entry!.LastUsedUtc = now;
            var list = entry.Ids;
            if (start > list.Count)
                start = list.Count;
            var take = Math.Max(0, Math.Min(limit, list.Count - start));
            return new CursorPage(list.GetRange(start, take), start, list.Count, reused);
        }
    }
}
