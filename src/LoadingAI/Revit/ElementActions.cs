using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LoadingAI.Revit;

/// <summary>
/// The change receipt's Select, Zoom and Isolate buttons (ADR-0017). Each runs on the Revit thread, only when no
/// turn is writing, and never changes the model: selection and temporary isolation are view state. Isolate
/// needs a transaction in Revit's API, so it opens its own small one outside the turn group.
/// </summary>
internal static class ElementActions
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static Task<string> SelectAsync(IReadOnlyList<long> ids) => RunAsync(ids, (uidoc, list) =>
    {
        uidoc.Selection.SetElementIds(list);
        return list.Count == 0 ? "Nothing selectable to select." : $"Selected {list.Count} element{(list.Count == 1 ? "" : "s")}.";
    });

    public static Task<string> ShowAsync(IReadOnlyList<long> ids) => RunAsync(ids, (uidoc, list) =>
    {
        if (list.Count == 0) return "Nothing to zoom to.";
        uidoc.ShowElements(list);
        return $"Zoomed to {list.Count} element{(list.Count == 1 ? "" : "s")}.";
    });

    public static Task<string> IsolateAsync(IReadOnlyList<long> ids) => RunAsync(ids, (uidoc, list) =>
    {
        if (list.Count == 0) return "Nothing to isolate.";
        var doc = uidoc.Document;
        var view = doc.ActiveView;
        if (view == null || !view.CanUseTemporaryVisibilityModes())
            return "This view cannot isolate elements. Open a plan, section or 3D view.";
        using var t = new Transaction(doc, "LoadingAI isolate changed elements");
        t.Start();
        view.IsolateElementsTemporary(list);
        t.Commit();
        return $"Isolated {list.Count} element{(list.Count == 1 ? "" : "s")} in {view.Name}. Use Reset Temporary Hide/Isolate to undo.";
    });

    private static async Task<string> RunAsync(IReadOnlyList<long> ids, Func<UIDocument, List<ElementId>, string> action)
    {
        var bridge = App.Bridge;
        if (bridge == null) return "Please open a model first.";
        if (bridge.HasActiveTurnGroup) return "The agent is still changing the model. Try again when it finishes.";
        if (ids.Count == 0) return "No elements to act on.";
        return await bridge.RunAsync(app =>
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return "Please open a model first.";
            return action(uidoc, Resolve(uidoc.Document, ids));
        }, Timeout);
    }

    /// <summary>
    /// The ids that still exist and can be picked in a view. Views, sheets, types and other definitions are
    /// dropped: selecting them is meaningless, and a stale id (deleted since the turn) must not throw.
    /// </summary>
    private static List<ElementId> Resolve(Document doc, IReadOnlyList<long> ids)
    {
        var list = new List<ElementId>();
        foreach (var raw in ids)
        {
            try
            {
                var id = new ElementId(raw);
                var el = doc.GetElement(id);
                if (el == null || el is ElementType || el is View) continue;
                if (el.Category == null) continue;
                list.Add(id);
            }
            catch
            {
                // An id Revit rejects is skipped; the rest still select.
            }
        }
        return list;
    }
}
