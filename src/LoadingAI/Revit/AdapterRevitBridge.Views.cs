using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using LoadingAI.Core.Openings;
using LoadingAI.Llm;
using LoadingAI.Revit.Formwork;
using LoadingAI.Revit.Openings;
using LoadingAI.Revit.StructureCuts;
using LoadingAI.Tools;

namespace LoadingAI.Revit;

/// <summary>
/// View slice: isolate, hide, reset, colour overrides, sheets, view templates, crop, dimensions, tagging.
/// Implements <c>IRevitViewCommand</c>. Part of <see cref="AdapterRevitBridge"/>; see that type for the contract.
/// </summary>
public sealed partial class AdapterRevitBridge
{
    public Task<CommandResultDto> IsolateInViewAsync(
        IReadOnlyList<string>? elementIds = null,
        string? category = null,
        bool isolate = true,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = doc?.ActiveView;
            if (uidoc == null || doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (view == null) return new CommandResultDto(false, "No active view.");
            if (view.IsTemplate) return new CommandResultDto(false, "Cannot isolate in a view template.");
            if ((elementIds == null || elementIds.Count == 0) && string.IsNullOrWhiteSpace(category))
                return new CommandResultDto(false, "Provide elementIds or category.");

            using var tx = new Transaction(doc, isolate
                ? $"LoadingAI for Revit: isolate {category ?? elementIds!.Count.ToString()} in {view.Name}"
                : $"LoadingAI for Revit: reset isolate in {view.Name}");
            tx.Start();
            try
            {
                int affected = 0;
                if (!view.IsTemporaryHideIsolateActive() || isolate)
                {
                    if (!string.IsNullOrWhiteSpace(category) && (elementIds == null || elementIds.Count == 0))
                    {
                        var bic = AllTools.ParseCategory(category);
                        if (bic == null) { tx.RollBack(); return new CommandResultDto(false, $"Unknown category '{category}'."); }
                        var catObj = doc.Settings.Categories.get_Item(bic.Value);
                        if (catObj == null) { tx.RollBack(); return new CommandResultDto(false, $"Category '{category}' not found."); }
                        view.IsolateCategoryTemporary(catObj.Id);
                        affected = 0;
                    }
                    else
                    {
                        var ids = elementIds!.Select(AllTools.ParseEid).ToList();
                        view.IsolateElementsTemporary(ids);
                        affected = ids.Count;
                    }
                }
                if (!isolate)
                    view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                tx.Commit();
                var msg = isolate
                    ? $"Isolated {(category ?? affected + " elements")} in view '{view.Name}' (temporary). Use reset_temporary_view to restore."
                    : $"Reset isolate in '{view.Name}'.";
                return new CommandResultDto(true, msg, affected, view.Name);
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> HideInViewAsync(
        IReadOnlyList<string>? elementIds = null,
        string? category = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = doc?.ActiveView;
            if (uidoc == null || doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (view == null) return new CommandResultDto(false, "No active view.");
            if ((elementIds == null || elementIds.Count == 0) && string.IsNullOrWhiteSpace(category))
                return new CommandResultDto(false, "Provide elementIds or category.");

            using var tx = new Transaction(doc, $"LoadingAI for Revit: hide {category ?? elementIds!.Count.ToString()} in {view.Name}");
            tx.Start();
            try
            {
                ICollection<ElementId> ids;
                if (!string.IsNullOrWhiteSpace(category) && (elementIds == null || elementIds.Count == 0))
                {
                    var bic = AllTools.ParseCategory(category);
                    if (bic == null) { tx.RollBack(); return new CommandResultDto(false, $"Unknown category '{category}'. Try RVT Links, Walls, Doors, etc."); }
                    ids = new FilteredElementCollector(doc, view.Id).OfCategory(bic.Value).WhereElementIsNotElementType().ToElementIds();
                    if (ids.Count == 0) { tx.RollBack(); return new CommandResultDto(false, $"No visible {category} in view '{view.Name}'."); }
                    view.HideElementsTemporary(ids);
                    tx.Commit();
                    return new CommandResultDto(true, $"Hid {ids.Count} {category} in '{view.Name}' (temporary). Use reset_temporary_view to restore.", ids.Count, view.Name);
                }

                ids = elementIds!.Select(AllTools.ParseEid).ToList();
                view.HideElementsTemporary(ids);
                tx.Commit();
                return new CommandResultDto(true, $"Hid {ids.Count} element(s) in '{view.Name}' (temporary).", ids.Count, view.Name);
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> ResetViewVisibilityAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = doc?.ActiveView;
            if (uidoc == null || doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (view == null) return new CommandResultDto(false, "No active view.");
            if (!view.IsTemporaryHideIsolateActive())
                return new CommandResultDto(true, $"No temporary hide/isolate active in '{view.Name}'.", 0, view.Name);

            using var tx = new Transaction(doc, $"LoadingAI for Revit: reset view {view.Name}");
            tx.Start();
            try
            {
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                tx.Commit();
                return new CommandResultDto(true, $"Reset temporary view in '{view.Name}'.", 0, view.Name);
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> OverrideColorInViewAsync(
        IReadOnlyList<string>? elementIds = null,
        string? category = null,
        string? color = null,
        bool reset = false,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var view = doc.ActiveView;
            if (view == null) return new CommandResultDto(false, "No active view.");
            if (view.IsTemplate) return new CommandResultDto(false, "Cannot override in view template.");
            if ((elementIds == null || elementIds.Count == 0) && string.IsNullOrWhiteSpace(category))
                return new CommandResultDto(false, "Provide elementIds or category.");

            try
            {
                var colorName = string.IsNullOrWhiteSpace(color) ? "red" : color;
                Color clr = new Color(255, 0, 0);
                if (!reset) clr = ParseColor(colorName);
                var ogs = new OverrideGraphicSettings();
                if (!reset)
                {
                    ogs.SetProjectionLineColor(clr);
                    ogs.SetSurfaceForegroundPatternColor(clr);
                    ogs.SetCutForegroundPatternColor(clr);
                }

                using var tx = new Transaction(doc, reset
                    ? $"LoadingAI for Revit: reset color in {view.Name}"
                    : $"LoadingAI for Revit: color {colorName} in {view.Name}");
                tx.Start();
                try
                {
                    if (!string.IsNullOrWhiteSpace(category) && (elementIds == null || elementIds.Count == 0))
                    {
                        var bic = AllTools.ParseCategory(category);
                        if (bic == null) { tx.RollBack(); return new CommandResultDto(false, $"Unknown category '{category}'."); }
                        var catObj = doc.Settings.Categories.get_Item(bic.Value);
                        if (catObj == null) { tx.RollBack(); return new CommandResultDto(false, $"Category '{category}' not found."); }
                        view.SetCategoryOverrides(catObj.Id, reset ? new OverrideGraphicSettings() : ogs);
                        tx.Commit();
                        var catMsg = reset
                            ? $"Reset color for {category} in '{view.Name}'."
                            : $"Set {colorName} override for {category} in '{view.Name}'.";
                        return new CommandResultDto(true, catMsg, 0, view.Name);
                    }

                    var ids = elementIds!.Select(AllTools.ParseEid).ToList();
                    foreach (var id in ids) view.SetElementOverrides(id, reset ? new OverrideGraphicSettings() : ogs);
                    tx.Commit();
                    var msg = reset
                        ? $"Reset color for {ids.Count} element(s) in '{view.Name}'."
                        : $"Set {colorName} override for {ids.Count} element(s) in '{view.Name}'.";
                    return new CommandResultDto(true, msg, ids.Count, view.Name);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> ColorByConstructionScheduleAsync(
        string startDate,
        int intervalDays = 14,
        IReadOnlyList<string>? categories = null,
        string? palette = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            if (!AllTools.TryParseDate(startDate ?? "", out var parsedStart))
                return new CommandResultDto(false, $"Invalid startDate '{startDate}'.");
            var interval = intervalDays < 1 ? 1 : (intervalDays > 365 ? 365 : intervalDays);
            var paletteName = string.IsNullOrWhiteSpace(palette) ? "rainbow" : palette;

            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var view = doc.ActiveView;
            if (view == null) return new CommandResultDto(false, "No active view.");
            if (view.IsTemplate) return new CommandResultDto(false, "Cannot color in view template.");

            try
            {
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                var levelById = levels.ToDictionary(l => l.Id, l => l.Name);
                var colors = GeneratePalette(levels.Count, paletteName);
                var dateByLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < levels.Count; i++)
                    dateByLevel[levels[i].Name] = parsedStart.AddDays(i * interval).ToString("yyyy-MM-dd");

                var bics = new List<BuiltInCategory>();
                if (categories != null && categories.Count > 0)
                {
                    if (categories.Count == 1 && categories[0].Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] defaults = { "Walls", "Doors", "Windows", "Floors", "Ceilings", "Roofs", "Structural Framing", "Structural Columns", "Columns", "Generic Models" };
                        foreach (var c in defaults)
                        {
                            var bic = AllTools.ParseCategory(c);
                            if (bic != null) bics.Add(bic.Value);
                        }
                    }
                    else
                    {
                        foreach (var c in categories)
                        {
                            var bic = AllTools.ParseCategory(c);
                            if (bic != null) bics.Add(bic.Value);
                        }
                    }
                }
                else
                {
                    bics.Add(BuiltInCategory.OST_Walls);
                }

                var levelToIds = new Dictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);
                foreach (var lvl in levels) levelToIds[lvl.Name] = new List<ElementId>();

                foreach (var bic in bics)
                {
                    var elems = new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType().ToElements();
                    foreach (var el in elems)
                    {
                        string? lvlName = null;
                        if (el.LevelId != ElementId.InvalidElementId && levelById.TryGetValue(el.LevelId, out var ln))
                            lvlName = ln;
                        else
                        {
                            var p = el.LookupParameter("Base Constraint") ?? el.LookupParameter("Level") ?? el.LookupParameter("Reference Level") ?? el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ?? el.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                            if (p != null)
                            {
                                if (p.StorageType == StorageType.ElementId)
                                {
                                    var id = p.AsElementId();
                                    if (levelById.TryGetValue(id, out var n2)) lvlName = n2;
                                }
                                else lvlName = p.AsString() ?? p.AsValueString();
                            }
                        }
                        if (lvlName != null && levelToIds.ContainsKey(lvlName)) levelToIds[lvlName].Add(el.Id);
                    }
                }

                using var tx = new Transaction(doc, $"LoadingAI for Revit: color by schedule {parsedStart:yyyy-MM-dd}");
                tx.Start();
                try
                {
                    int total = 0;
                    for (int i = 0; i < levels.Count; i++)
                    {
                        var lvl = levels[i];
                        var ids = levelToIds[lvl.Name];
                        if (ids.Count == 0) continue;
                        var clr = colors[i];
                        var ogs = new OverrideGraphicSettings();
                        ogs.SetProjectionLineColor(clr);
                        ogs.SetSurfaceForegroundPatternColor(clr);
                        ogs.SetCutForegroundPatternColor(clr);
                        ogs.SetProjectionLineWeight(4);
                        foreach (var id in ids)
                        {
                            view.SetElementOverrides(id, ogs);
                            total++;
                        }
                    }
                    tx.Commit();
                    var msg = $"Colored {total} element(s) in '{view.Name}' by construction schedule bottom-up {parsedStart:yyyy-MM-dd} (+{interval}d) across {levels.Count} levels, {bics.Count} categories. Palette {paletteName}.";
                    msg += "\nLegend:\n" + string.Join("\n", levels.Select((l, i) => $"{l.Name} ({l.Elevation:F1}ft) = {dateByLevel[l.Name]} -> #{colors[i].Red:X2}{colors[i].Green:X2}{colors[i].Blue:X2}"));
                    msg += "\nTo reset: say 'reset view' (reset_temporary_view).";
                    return new CommandResultDto(true, msg, total, view.Name);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> ColorByParameterAsync(
        string paramName,
        string? category = null,
        IReadOnlyList<string>? elementIds = null,
        string? palette = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            if (string.IsNullOrWhiteSpace(paramName))
                return new CommandResultDto(false, "Provide paramName.");
            var paletteName = string.IsNullOrWhiteSpace(palette) ? "rainbow" : palette;

            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var view = doc.ActiveView;
            if (view == null) return new CommandResultDto(false, "No active view.");
            if (view.IsTemplate) return new CommandResultDto(false, "Cannot color in view template.");

            try
            {
                List<Element> elems;
                if (elementIds != null && elementIds.Count > 0)
                {
                    elems = elementIds.Select(id => doc.GetElement(AllTools.ParseEid(id))).Where(e => e != null).Cast<Element>().ToList();
                }
                else
                {
                    var bic = AllTools.ParseCategory(string.IsNullOrWhiteSpace(category) ? "Walls" : category);
                    if (bic == null) return new CommandResultDto(false, $"Unknown category '{category}'.");
                    elems = new FilteredElementCollector(doc, view.Id).OfCategory(bic.Value).WhereElementIsNotElementType().ToElements().ToList();
                }
                var groups = new SortedDictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);
                foreach (var el in elems)
                {
                    var key = ReadParam(el, paramName);
                    if (!groups.TryGetValue(key, out var ids)) { ids = new List<ElementId>(); groups[key] = ids; }
                    ids.Add(el.Id);
                }
                if (groups.Count == 0) return new CommandResultDto(false, "No elements to color.");
                var colors = GeneratePalette(groups.Count, paletteName);
                using var tx = new Transaction(doc, $"LoadingAI for Revit: color by {paramName}");
                tx.Start();
                try
                {
                    int i = 0, total = 0;
                    var legend = new List<string>();
                    foreach (var kv in groups)
                    {
                        var clr = colors[i];
                        var ogs = new OverrideGraphicSettings();
                        ogs.SetProjectionLineColor(clr);
                        ogs.SetSurfaceForegroundPatternColor(clr);
                        ogs.SetCutForegroundPatternColor(clr);
                        foreach (var id in kv.Value) { view.SetElementOverrides(id, ogs); total++; }
                        legend.Add($"{kv.Key} ({kv.Value.Count}) -> #{clr.Red:X2}{clr.Green:X2}{clr.Blue:X2}");
                        i++;
                    }
                    tx.Commit();
                    return new CommandResultDto(
                        true,
                        $"Colored {total} element(s) in '{view.Name}' by '{paramName}' ({groups.Count} values). Palette {paletteName}.\nLegend:\n" + string.Join("\n", legend),
                        total,
                        view.Name);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> CreateSheetAsync(
        string sheetNumber, string sheetName, string? titleBlockName = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                FamilySymbol? tb = null;
                if (!string.IsNullOrWhiteSpace(titleBlockName))
                {
                    tb = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .FirstOrDefault(s => s.FamilyName.Contains("Title") && s.Name.Equals(titleBlockName, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    tb = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .WhereElementIsElementType().Cast<FamilySymbol>().FirstOrDefault();
                }
                if (tb == null) return new CommandResultDto(false, "No title block family found.");

                using var tx = new Transaction(doc, $"LoadingAI for Revit: create sheet {sheetNumber}");
                tx.Start();
                try
                {
                    if (!tb.IsActive) tb.Activate();
                    var sheet = ViewSheet.Create(doc, tb.Id);
                    sheet.SheetNumber = sheetNumber;
                    sheet.Name = sheetName;
                    tx.Commit();
                    return new CommandResultDto(true, $"Created sheet {sheet.Id} {sheetNumber} \"{sheetName}\"", 1, ElementIds: new[] { sheet.Id.ToString() });
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {inner.Message}");
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> PlaceViewOnSheetAsync(
        string viewId, string sheetId, double xMm = 0, double yMm = 0,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var x = Metric.MmToFt(xMm);
            var y = Metric.MmToFt(yMm);
            try
            {
                var view = doc.GetElement(AllTools.ParseEid(viewId)) as View;
                if (view == null) return new CommandResultDto(false, $"View {viewId} not found.");
                var sheet = doc.GetElement(AllTools.ParseEid(sheetId)) as ViewSheet;
                if (sheet == null) return new CommandResultDto(false, $"Sheet {sheetId} not found.");
                using var tx = new Transaction(doc, $"LoadingAI for Revit: place {viewId} on {sheetId}");
                tx.Start();
                try
                {
                    var vp = Viewport.Create(doc, sheet.Id, view.Id, new XYZ(x, y, 0));
                    tx.Commit();
                    return new CommandResultDto(true, $"Placed view {viewId} on sheet {sheetId} at ({x},{y}) viewport {vp.Id}", 1, ElementIds: new[] { vp.Id.ToString() });
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {inner.Message}");
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> DuplicateViewAsync(
        string viewId, bool withDetailing = false,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var view = doc.GetElement(AllTools.ParseEid(viewId)) as View;
                if (view == null) return new CommandResultDto(false, $"View {viewId} not found.");
                using var tx = new Transaction(doc, "LoadingAI for Revit: duplicate view");
                tx.Start();
                try
                {
                    var nid = view.Duplicate(withDetailing ? ViewDuplicateOption.WithDetailing : ViewDuplicateOption.Duplicate);
                    tx.Commit();
                    return new CommandResultDto(true, $"Duplicated {viewId} -> {nid} {(withDetailing ? "with detailing" : "")}", 1, ElementIds: new[] { nid.ToString() });
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {inner.Message}");
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> SetViewTemplateAsync(
        string viewId, string? templateId = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var view = doc.GetElement(AllTools.ParseEid(viewId)) as View;
                if (view == null) return new CommandResultDto(false, $"View {viewId} not found.");
                using var tx = new Transaction(doc, "LoadingAI for Revit: set view template");
                tx.Start();
                try
                {
                    var tid = string.IsNullOrWhiteSpace(templateId) ? ElementId.InvalidElementId : AllTools.ParseEid(templateId);
                    view.ViewTemplateId = tid;
                    tx.Commit();
                    var msg = string.IsNullOrWhiteSpace(templateId)
                        ? $"Cleared template for {viewId}"
                        : $"Set template {templateId} for {viewId}";
                    return new CommandResultDto(true, msg, 1);
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {inner.Message}");
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> SetCropBoxAsync(
        string viewId, bool enabled,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var view = doc.GetElement(AllTools.ParseEid(viewId)) as View;
                if (view == null) return new CommandResultDto(false, $"View {viewId} not found.");
                using var tx = new Transaction(doc, "LoadingAI for Revit: set crop");
                tx.Start();
                try
                {
                    view.CropBoxActive = enabled;
                    view.CropBoxVisible = enabled;
                    tx.Commit();
                    return new CommandResultDto(true, $"Set crop {(enabled ? "on" : "off")} for {viewId}", 1);
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {inner.Message}");
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> CreateDimensionBetweenWallsAsync(
        string wallId1, string wallId2, string? viewId = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                View? view = string.IsNullOrWhiteSpace(viewId)
                    ? uidoc!.ActiveView
                    : doc.GetElement(AllTools.ParseEid(viewId)) as View;
                if (view == null) return new CommandResultDto(false, "View not found.");
                var w1 = doc.GetElement(AllTools.ParseEid(wallId1)) as Wall;
                var w2 = doc.GetElement(AllTools.ParseEid(wallId2)) as Wall;
                if (w1 == null || w2 == null) return new CommandResultDto(false, "Walls not found.");
                var c1 = (w1.Location as LocationCurve)?.Curve;
                var c2 = (w2.Location as LocationCurve)?.Curve;
                if (c1 == null || c2 == null) return new CommandResultDto(false, "Walls have no location curve.");

                using var tx = new Transaction(doc, "LoadingAI for Revit: create dimension");
                tx.Start();
                try
                {
                    var ra = new ReferenceArray();
                    ra.Append(new Reference(w1));
                    ra.Append(new Reference(w2));
                    var mid1 = c1.Evaluate(0.5, true);
                    var mid2 = c2.Evaluate(0.5, true);
                    var dir = (mid2 - mid1).Normalize();
                    var perp = new XYZ(-dir.Y, dir.X, 0);
                    var line = Line.CreateBound(mid1 + perp * 5, mid2 + perp * 5);
                    var dim = doc.Create.NewDimension(view, line, ra);
                    tx.Commit();
                    return new CommandResultDto(true, $"Created dimension {dim.Id} between {wallId1} and {wallId2} in {view.Name}", 1, ElementIds: new[] { dim.Id.ToString() });
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {inner.Message}");
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> TagAllInActiveViewAsync(
        string category,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var view = uidoc?.ActiveView;
            if (view == null) return new CommandResultDto(false, "No active view.");
            try
            {
                var bic = AllTools.ParseCategory(category);
                if (bic == null) return new CommandResultDto(false, $"Unknown category {category}");
                var elems = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(bic.Value).WhereElementIsNotElementType().ToElements().Take(50).ToList();
                int tagged = 0;
                using var tx = new Transaction(doc, "LoadingAI for Revit: tag all");
                tx.Start();
                try
                {
                    foreach (var el in elems)
                    {
                        try
                        {
                            var loc = (el.Location as LocationPoint)?.Point ?? XYZ.Zero;
                            IndependentTag.Create(doc, view.Id, new Reference(el), true, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, loc);
                            tagged++;
                        }
                        catch { }
                    }
                    tx.Commit();
                    return new CommandResultDto(true, $"Tagged {tagged}/{elems.Count} {category} in {view.Name}", tagged);
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {inner.Message}");
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }
}
