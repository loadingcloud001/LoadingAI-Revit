using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using System;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// One capped page of hosts: the subset this run will touch, plus how far through the collected set
/// it got so the next call can start at <see cref="NextOffset"/>.
/// </summary>
internal sealed class SplitBatch
{
    public SplitBatch(List<Element> items, int offset, int total)
        : this(items, offset, total, Array.Empty<string>(), 0)
    {
    }

    public SplitBatch(
        List<Element> items, int offset, int total, IReadOnlyList<string> missed, int suppliedIds,
        int? consumed = null, bool reused = false)
    {
        Items = items;
        Offset = offset;
        Total = total;
        Missed = missed;
        SuppliedIds = suppliedIds;
        Consumed = consumed ?? items.Count;
        Reused = reused;
    }

    public List<Element> Items { get; }

    public int Offset { get; }

    public int Total { get; }

    /// <summary>Caller-supplied tokens that did not become a host. Empty when no ids were supplied.</summary>
    public IReadOnlyList<string> Missed { get; }

    /// <summary>How many id tokens the caller supplied, including the ones that missed.</summary>
    public int SuppliedIds { get; }

    /// <summary>
    /// How many ids of the frozen list this page used, including hosts that no longer exist because an
    /// earlier page re-created them. The next call continues after all of them.
    /// </summary>
    public int Consumed { get; }

    /// <summary>True when this page continued a list taken by an earlier call.</summary>
    public bool Reused { get; }

    public int NextOffset => Offset + Consumed;
}

/// <summary>
/// Collects hosts inside the plugin so chat never has to paste hundreds of ids. Explicit ids win;
/// otherwise <c>selection</c>, the active view, or the whole model.
/// </summary>
internal static class SplitScope
{
    private static readonly SplitCursorStore Cursors = new();

    public static SplitBatch Collect(
        Document doc,
        UIDocument uidoc,
        Func<Element, bool> wanted,
        SplitOptions options,
        IReadOnlyList<string> ids,
        string command)
    {
        // Explicit ids are the work set. An empty Revit selection must not replace them, and a
        // miss is kept so the caller can name it instead of falling through to "select first".
        // The list is frozen when a run starts (offset 0): a write run re-creates hosts, so a list
        // collected afresh would shift under the offset and skip the hosts that moved up.
        var missed = new List<string>();
        var key = SplitCursorStore.KeyFor(command, DocumentKey(doc), options.Scope, ids);
        var page = Cursors.Page(key, options.ContinueFrom, options.Limit, () =>
        {
            List<Element> collected;
            if (ids.Count > 0)
            {
                collected = SplitElements.Resolve(doc, ids, wanted, out missed);
            }
            else
            {
                collected = ByScope(doc, uidoc, wanted, options.Scope);
            }

            return collected.Select(element => SplitElements.Fmt(element.Id)).ToList();
        });

        // A host an earlier page deleted and re-created is gone from the model: skipped here, but still
        // counted in the offset so the next call does not repeat or lose a place.
        var items = new List<Element>(page.Ids.Count);
        foreach (var token in page.Ids)
        {
            var element = SplitElements.ElementOf(doc, token);
            if (element != null && wanted(element))
                items.Add(element);
        }

        return new SplitBatch(
            items, page.Offset, page.Total, page.Reused ? Array.Empty<string>() : missed, ids.Count, page.Ids.Count, page.Reused);
    }

    private static string DocumentKey(Document doc)
        => string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;

    public static List<Element> AllInModel(Document doc, Func<Element, bool> wanted)
        => new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .ToElements()
            .Where(wanted)
            .ToList();

    public static List<Element> VisibleInView(Document doc, UIDocument uidoc, Func<Element, bool> wanted)
    {
        var view = uidoc.ActiveView ?? doc.ActiveView;
        if (view == null)
            return new List<Element>();

        return new FilteredElementCollector(doc, view.Id)
            .WhereElementIsNotElementType()
            .ToElements()
            .Where(wanted)
            .ToList();
    }

    /// <summary>
    /// Cutters when the caller did not pick any: selection first, then every matching element in the
    /// model. Beams-by-columns needs this for empty <c>columnIds</c>; the plugin.json already said so.
    /// </summary>
    public static List<Element> Cutters(
        Document doc,
        UIDocument uidoc,
        IReadOnlyList<string> ids,
        Func<Element, bool> wanted)
    {
        if (ids.Count > 0)
            return SplitElements.Resolve(doc, ids, wanted);

        var selected = SplitElements.GetSelection(uidoc).Where(wanted).ToList();
        return selected.Count > 0 ? selected : AllInModel(doc, wanted);
    }

    /// <summary>
    /// Loaded-link cutters, only when the flag is on. Off returns an empty set and does not walk
    /// link instances. The result is added to host cutters; it never replaces them.
    /// </summary>
    public static SplitElements.LinkCutters LinkCutters(Document doc, SplitOptions options, Func<Element, bool> wanted)
    {
        if (options == null || !options.IncludeLinks)
            return new SplitElements.LinkCutters();

        return SplitElements.Links(doc, wanted);
    }

    /// <summary>
    /// Empty / unknown scope falls back to selection, matching the host Select default.
    /// Public because <c>CollectHostsAsync</c> pages the same three scopes without the
    /// per-call options the split runs carry.
    /// </summary>
    public static List<Element> ByScope(Document doc, UIDocument uidoc, Func<Element, bool> wanted, string scope)
    {
        if (string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
            return AllInModel(doc, wanted);
        if (string.Equals(scope, "visible", StringComparison.OrdinalIgnoreCase))
            return VisibleInView(doc, uidoc, wanted);

        return SplitElements.GetSelection(uidoc).Where(wanted).ToList();
    }
}
