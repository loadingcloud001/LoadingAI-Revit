using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// One capped page of hosts: the subset this run will touch, plus how far through the collected set
/// it got so the next call can start at <see cref="NextOffset"/>.
/// </summary>
internal sealed class SplitBatch
{
    public SplitBatch(List<Element> items, int offset, int total)
    {
        Items = items;
        Offset = offset;
        Total = total;
    }

    public List<Element> Items { get; }

    public int Offset { get; }

    public int Total { get; }

    public int NextOffset => Offset + Items.Count;
}

/// <summary>
/// Collects hosts inside the plugin so chat never has to paste hundreds of ids. Explicit ids win;
/// otherwise <c>selection</c>, the active view, or the whole model.
/// </summary>
internal static class SplitScope
{
    public static SplitBatch Collect(
        Document doc,
        UIDocument uidoc,
        Func<Element, bool> wanted,
        SplitOptions options,
        IReadOnlyList<string> ids)
    {
        var collected = ids.Count > 0
            ? SplitElements.Resolve(doc, ids, wanted)
            : ByScope(doc, uidoc, wanted, options.Scope);

        var offset = Math.Max(0, options.ContinueFrom);
        if (offset > collected.Count)
            offset = collected.Count;

        var remaining = collected.Count - offset;
        var take = Math.Min(options.Limit, remaining);
        var page = collected.Skip(offset).Take(take).ToList();
        return new SplitBatch(page, offset, collected.Count);
    }

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
