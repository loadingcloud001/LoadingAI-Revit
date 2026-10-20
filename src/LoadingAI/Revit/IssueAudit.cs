using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using LoadingAI.Tools;

namespace LoadingAI.Revit;

/// <summary>
/// Read-only documentation check. Counts the whole model and keeps a short sample of ids.
/// </summary>
internal static class IssueAudit
{
    public static IssueAuditDto Collect(Document doc, string? category, string? parameter, int limit)
    {
        var items = new List<IssueAuditItemDto>();
        var viewsNotOnSheet = 0;
        var viewsMissingTemplate = 0;
        var duplicateSheets = 0;
        var duplicateMarks = 0;
        var roomProblems = 0;
        var importedCad = 0;
        var inPlace = 0;
        var missing = 0;

        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            placed.Add(vp.ViewId.ToString());

        foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            if (view.IsTemplate || view is ViewSheet) continue;
            if (view.ViewType is ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Internal or ViewType.DrawingSheet)
                continue;
            if (!placed.Contains(view.Id.ToString()))
            {
                viewsNotOnSheet++;
                Add(items, limit, "view-not-on-sheet", view.Id.ToString(), view.Name, view.ViewType.ToString());
            }
            if (view.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.Elevation or ViewType.Section or ViewType.ThreeD or ViewType.AreaPlan)
            {
                var template = view.ViewTemplateId;
                if (template == null || template == ElementId.InvalidElementId)
                {
                    viewsMissingTemplate++;
                    Add(items, limit, "view-missing-template", view.Id.ToString(), view.Name, view.ViewType.ToString());
                }
            }
        }

        foreach (var group in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                     .GroupBy(s => s.SheetNumber ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.ToList();
            if (rows.Count < 2 || string.IsNullOrWhiteSpace(group.Key)) continue;
            duplicateSheets += rows.Count;
            foreach (var sheet in rows)
                Add(items, limit, "duplicate-sheet-number", sheet.Id.ToString(), sheet.SheetNumber, sheet.Name);
        }

        foreach (var bic in new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Walls })
        {
            var marked = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType()
                .ToElements()
                .Select(el => (el, mark: el.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""))
                .Where(row => !string.IsNullOrWhiteSpace(row.mark))
                .GroupBy(row => row.mark, StringComparer.OrdinalIgnoreCase);
            foreach (var group in marked.Where(g => g.Count() > 1))
            {
                duplicateMarks += group.Count();
                foreach (var row in group)
                    Add(items, limit, "duplicate-mark", row.el.Id.ToString(), row.mark, bic.ToString());
            }
        }

        foreach (var room in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>())
        {
            if (room.Location != null && room.Area > 1e-6) continue;
            roomProblems++;
            var why = room.Location == null ? "unplaced" : "zero area";
            Add(items, limit, "room-problem", room.Id.ToString(), room.Number + " " + room.Name, why);
        }

        foreach (var cad in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
        {
            if (cad.IsLinked) continue;
            importedCad++;
            Add(items, limit, "imported-cad", cad.Id.ToString(), cad.Category?.Name ?? "CAD", "imported, not linked");
        }

        foreach (var fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
        {
            if (fi.Symbol?.Family == null || !fi.Symbol.Family.IsInPlace) continue;
            inPlace++;
            Add(items, limit, "in-place-family", fi.Id.ToString(), fi.Name, fi.Category?.Name ?? "");
        }

        if (!string.IsNullOrWhiteSpace(category) && !string.IsNullOrWhiteSpace(parameter))
        {
            var bic = AllTools.ParseCategory(category);
            if (bic == null)
                return new IssueAuditDto(viewsNotOnSheet, viewsMissingTemplate, duplicateSheets, duplicateMarks, roomProblems, importedCad, inPlace, 0, items, $"Unknown category {category}");
            foreach (var el in new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType())
            {
                var p = el.LookupParameter(parameter);
                if (p != null && p.HasValue && !(p.StorageType == StorageType.String && string.IsNullOrWhiteSpace(p.AsString())))
                    continue;
                missing++;
                Add(items, limit, "missing-parameter", el.Id.ToString(), el.Name, parameter!);
            }
        }

        return new IssueAuditDto(
            viewsNotOnSheet, viewsMissingTemplate, duplicateSheets, duplicateMarks,
            roomProblems, importedCad, inPlace, missing, items);
    }

    static void Add(List<IssueAuditItemDto> items, int limit, string kind, string id, string name, string detail)
    {
        if (items.Count >= limit) return;
        items.Add(new IssueAuditItemDto(kind, id, name ?? "", detail ?? ""));
    }
}
