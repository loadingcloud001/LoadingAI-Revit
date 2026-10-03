using System;
using System.Collections.Generic;

namespace LoadingAI.Revit;

/// <summary>
/// Keys for <see cref="ViewOverrideLedger{T}"/>. An element and a category can carry the same number,
/// so the kind is part of the key.
/// </summary>
public static class ViewOverrideKey
{
    public static string Element(string id) => "e:" + id;

    public static string Category(string id) => "c:" + id;

    public static bool IsCategory(string key) => key.StartsWith("c:", StringComparison.Ordinal);

    public static string IdOf(string key) => key.Substring(2);
}

/// <summary>
/// What a view looked like before LoadingAI coloured it. The colour tools write graphic overrides into
/// the project, where they stay and sync like any other edit; this ledger remembers the override an
/// element or category had the first time a tool touched it, so <c>reset_view_overrides</c> can put
/// that back instead of clearing everything. It lives for the Revit session, and the first value wins:
/// colouring twice must not make the second colour the one to restore.
/// </summary>
/// <typeparam name="T">The host's override type. Core never needs to know what it holds.</typeparam>
public sealed class ViewOverrideLedger<T> where T : class
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, Dictionary<string, T>> _byView =
        new Dictionary<string, Dictionary<string, T>>(StringComparer.Ordinal);

    public void Remember(string viewId, string key, T before)
    {
        lock (_gate)
        {
            if (!_byView.TryGetValue(viewId, out var entries))
            {
                entries = new Dictionary<string, T>(StringComparer.Ordinal);
                _byView[viewId] = entries;
            }

            if (!entries.ContainsKey(key))
                entries[key] = before;
        }
    }

    /// <summary>Removes and returns what was remembered for <paramref name="key"/>, if anything.</summary>
    public bool TryTake(string viewId, string key, out T? before)
    {
        lock (_gate)
        {
            before = null;
            if (!_byView.TryGetValue(viewId, out var entries) || !entries.TryGetValue(key, out var found))
                return false;

            before = found;
            entries.Remove(key);
            if (entries.Count == 0)
                _byView.Remove(viewId);
            return true;
        }
    }

    public IReadOnlyList<KeyValuePair<string, T>> Snapshot(string viewId)
    {
        lock (_gate)
        {
            return _byView.TryGetValue(viewId, out var entries)
                ? new List<KeyValuePair<string, T>>(entries)
                : new List<KeyValuePair<string, T>>();
        }
    }

    public int Count(string viewId)
    {
        lock (_gate)
            return _byView.TryGetValue(viewId, out var entries) ? entries.Count : 0;
    }

    public void Forget(string viewId)
    {
        lock (_gate)
            _byView.Remove(viewId);
    }
}
