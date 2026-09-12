using System.IO;
using Autodesk.Revit.DB;

namespace LoadingAI.Revit;

public sealed record ContextSnapshot(
    int SelectionCount,
    string SelectionPreview,
    string ActiveViewName,
    string ActiveViewType,
    int VisibleCount,
    int WarningCount,
    string WarningPreview
)
{
    public string ToPromptString(IReadOnlyList<string> mentions)
    {
        var parts = new List<string>();
        if (mentions.Contains("selection"))
            parts.Add($"Selection: {SelectionCount} {(SelectionPreview.Length > 0 ? $"({SelectionPreview})" : "(none)")}");
        if (mentions.Contains("activeView") || mentions.Contains("activeview"))
            parts.Add($"ActiveView: {ActiveViewName} ({ActiveViewType}) Visible:{VisibleCount}");
        if (mentions.Contains("visible"))
            parts.Add($"VisibleInView: {VisibleCount}");
        if (mentions.Contains("warnings"))
            parts.Add($"Warnings: {WarningCount} {(WarningPreview.Length > 0 ? WarningPreview : "")}");
        if (mentions.Contains("levels") || mentions.Contains("sheets") || mentions.Contains("rooms") || mentions.Contains("walls"))
            parts.Add($"Mentioned: {string.Join(",", mentions)} — use tools to expand details");
        if (parts.Count == 0)
            return $"Context: Selection {SelectionCount}, ActiveView {ActiveViewName} ({ActiveViewType}) Visible {VisibleCount}, Warnings {WarningCount}";
        return "Context: " + string.Join(" | ", parts);
    }

    public string ToBarString() => $"Selected {SelectionCount} | View: {ActiveViewName} | Visible {VisibleCount} | Warnings {WarningCount}";
}

public static class ContextCollector
{
    public static async Task<ContextSnapshot> CollectAsync(RevitBridge bridge, CancellationToken ct = default)
    {
        try
        {
            return await bridge.RunAsync(uiApp =>
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null) return new ContextSnapshot(0, "", "NoDoc", "", 0, 0, "");
                var doc = uidoc.Document;
                int selCount = 0;
                string selPreview = "";
                try
                {
                    var ids = uidoc.Selection.GetElementIds();
                    selCount = ids.Count;
                    var sample = ids.Take(3).Select(id =>
                    {
                        var el = doc.GetElement(id);
                        return el == null ? id.ToString() : $"{id} {el.Category?.Name}";
                    });
                    selPreview = string.Join(", ", sample);
                    if (ids.Count > 3) selPreview += $" +{ids.Count - 3} more";
                }
                catch { }

                string viewName = "None";
                string viewType = "";
                int visible = 0;
                try
                {
                    var view = doc.ActiveView;
                    if (view != null)
                    {
                        viewName = view.Name;
                        viewType = view.ViewType.ToString();
                        visible = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds().Count;
                    }
                }
                catch { }

                int warnCount = 0;
                string warnPreview = "";
                try
                {
                    var warns = doc.GetWarnings();
                    warnCount = warns.Count;
                    warnPreview = string.Join(" | ", warns.Take(2).Select(w => w.GetDescriptionText().Length > 80 ? w.GetDescriptionText().Substring(0, 80) : w.GetDescriptionText()));
                }
                catch { }

                return new ContextSnapshot(selCount, selPreview, viewName, viewType, visible, warnCount, warnPreview);
            }, TimeSpan.FromSeconds(5), ct);
        }
        catch
        {
            return new ContextSnapshot(0, "", "Error", "", 0, 0, "");
        }
    }
}
