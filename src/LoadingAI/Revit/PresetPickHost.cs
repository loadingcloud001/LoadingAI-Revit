using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using LoadingAI.Tools;

namespace LoadingAI.Revit;

/// <summary>
/// Host-owned pick-in-model for chat preset fields. Plugins declare a field;
/// they do not call Selection.PickObjects themselves.
/// </summary>
internal static class PresetPickHost
{
    public static readonly TimeSpan PickTimeout = TimeSpan.FromMinutes(10);

    public static object PickElements(UIApplication app, string? category, string? prompt)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc?.Document == null)
            return new { ok = false, error = "Please open a model first." };

        var status = string.IsNullOrWhiteSpace(prompt) ? "Pick elements, then Finish on the options bar." : prompt;
        try
        {
            var filter = new CategoryHintFilter(category);
            IList<Reference> refs = filter.HasHint
                ? uidoc.Selection.PickObjects(ObjectType.Element, filter, status)
                : uidoc.Selection.PickObjects(ObjectType.Element, status);
            var ids = refs.Select(r => r.ElementId.ToString()).Distinct().ToList();
            var eids = new List<ElementId>();
            foreach (var id in ids)
            {
                try { eids.Add(RevitParse.ParseEid(id)); } catch { }
            }
            try { uidoc.Selection.SetElementIds(eids); } catch { }
            return new { ok = true, ids };
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return new { ok = true, cancelled = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    public static object PickBox(UIApplication app, string? prompt)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc?.Document == null)
            return new { ok = false, error = "Please open a model first." };

        var status = string.IsNullOrWhiteSpace(prompt) ? "Drag a selection box in the current view." : prompt;
        try
        {
            var box = uidoc.Selection.PickBox(PickBoxStyle.Crossing, status);
            return new
            {
                ok = true,
                box = new
                {
                    minMm = new[] { Metric.FtToMm(box.Min.X), Metric.FtToMm(box.Min.Y), Metric.FtToMm(box.Min.Z) },
                    maxMm = new[] { Metric.FtToMm(box.Max.X), Metric.FtToMm(box.Max.Y), Metric.FtToMm(box.Max.Z) }
                }
            };
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return new { ok = true, cancelled = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    private sealed class CategoryHintFilter : ISelectionFilter
    {
        private readonly BuiltInCategory? _bic;
        public bool HasHint => _bic.HasValue;

        public CategoryHintFilter(string? category) => _bic = RevitParse.ParseCategory(category);

        public bool AllowElement(Element elem)
        {
            if (!_bic.HasValue) return true;
            var want = ((int)_bic.Value).ToString();
            return elem.Category != null && elem.Category.Id.ToString() == want;
        }

        public bool AllowReference(Reference reference, XYZ position) => true;
    }
}
