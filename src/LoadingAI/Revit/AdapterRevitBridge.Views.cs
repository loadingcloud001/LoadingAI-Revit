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
    // ---- colour overrides: solid fill, and a ledger so they can be undone --------------------------

    /// <summary>What each view looked like before a colour tool first touched it. This Revit session only.</summary>
    private static readonly ViewOverrideLedger<OverrideGraphicSettings> OverrideLedger = new();

    private static string ViewKey(View view) => view.Id.ToString();

    /// <summary>The first solid drafting fill in the document, found by what it is rather than by its localised name.</summary>
    private static ElementId SolidFillPatternId(Document doc)
    {
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)))
        {
            var pattern = ((FillPatternElement)element).GetFillPattern();
            if (pattern.IsSolidFill && pattern.Target == FillPatternTarget.Drafting)
                return element.Id;
        }

        return ElementId.InvalidElementId;
    }

    /// <summary>
    /// Line and surface colour with a solid fill, so the colour shows in Shaded and Consistent Colors as
    /// well as in Hidden Line. A surface with no pattern shows only its edges.
    /// </summary>
    private static OverrideGraphicSettings ColorOverride(Document doc, Color color, int? lineWeight = null)
    {
        var ogs = new OverrideGraphicSettings();
        ogs.SetProjectionLineColor(color);
        ogs.SetSurfaceForegroundPatternColor(color);
        ogs.SetCutForegroundPatternColor(color);
        var solid = SolidFillPatternId(doc);
        if (solid != ElementId.InvalidElementId)
        {
            ogs.SetSurfaceForegroundPatternId(solid);
            ogs.SetCutForegroundPatternId(solid);
        }

        if (lineWeight.HasValue)
            ogs.SetProjectionLineWeight(lineWeight.Value);
        return ogs;
    }

    private static void ApplyElementOverride(View view, ElementId id, OverrideGraphicSettings settings)
    {
        OverrideLedger.Remember(ViewKey(view), ViewOverrideKey.Element(id.ToString()), view.GetElementOverrides(id));
        view.SetElementOverrides(id, settings);
    }

    private static void ApplyCategoryOverride(View view, ElementId categoryId, OverrideGraphicSettings settings)
    {
        OverrideLedger.Remember(ViewKey(view), ViewOverrideKey.Category(categoryId.ToString()), view.GetCategoryOverrides(categoryId));
        view.SetCategoryOverrides(categoryId, settings);
    }

    /// <summary>Back to what the view had before LoadingAI first touched the element; to no override when nothing was recorded.</summary>
    private static void ClearElementOverride(View view, ElementId id)
    {
        var restored = OverrideLedger.TryTake(ViewKey(view), ViewOverrideKey.Element(id.ToString()), out var before);
        view.SetElementOverrides(id, restored ? before! : new OverrideGraphicSettings());
    }

    private static void ClearCategoryOverride(View view, ElementId categoryId)
    {
        var restored = OverrideLedger.TryTake(ViewKey(view), ViewOverrideKey.Category(categoryId.ToString()), out var before);
        view.SetCategoryOverrides(categoryId, restored ? before! : new OverrideGraphicSettings());
    }

    public Task<CommandResultDto> ResetViewOverridesAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var view = doc.ActiveView;
            if (view == null) return new CommandResultDto(false, "No active view.");
            if (view.IsTemplate) return new CommandResultDto(false, "Cannot reset overrides in a view template.");

            var entries = OverrideLedger.Snapshot(ViewKey(view));
            if (entries.Count == 0)
                return new CommandResultDto(
                    true,
                    $"Nothing to restore in '{view.Name}': LoadingAI has not coloured it in this Revit session. Overrides set earlier or by hand are left as they are.",
                    0,
                    view.Name);

            using var tx = new Transaction(doc, $"LoadingAI for Revit: restore overrides in {view.Name}");
            tx.Start();
            try
            {
                int restored = 0, gone = 0;
                foreach (var entry in entries)
                {
                    var id = AllTools.ParseEid(ViewOverrideKey.IdOf(entry.Key));
                    try
                    {
                        if (ViewOverrideKey.IsCategory(entry.Key)) view.SetCategoryOverrides(id, entry.Value);
                        else if (doc.GetElement(id) != null) view.SetElementOverrides(id, entry.Value);
                        else { gone++; continue; }
                        restored++;
                    }
                    catch
                    {
                        gone++;
                    }
                }

                tx.Commit();
                OverrideLedger.Forget(ViewKey(view));
                var msg = $"Restored {restored} override(s) in '{view.Name}'.";
                if (gone > 0) msg += $" {gone} element(s) no longer exist or could not be restored.";
                return new CommandResultDto(true, msg, restored, view.Name);
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

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
                OverrideGraphicSettings? ogs = reset ? null : ColorOverride(doc, ParseColor(colorName));

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
                        if (reset) ClearCategoryOverride(view, catObj.Id);
                        else ApplyCategoryOverride(view, catObj.Id, ogs!);
                        tx.Commit();
                        var catMsg = reset
                            ? $"Reset color for {category} in '{view.Name}'."
                            : $"Set {colorName} override for {category} in '{view.Name}'. Saved in the view; reset_view_overrides puts the previous look back.";
                        return new CommandResultDto(true, catMsg, 0, view.Name);
                    }

                    var ids = elementIds!.Select(AllTools.ParseEid).ToList();
                    foreach (var id in ids)
                    {
                        if (reset) ClearElementOverride(view, id);
                        else ApplyElementOverride(view, id, ogs!);
                    }
                    tx.Commit();
                    var msg = reset
                        ? $"Reset color for {ids.Count} element(s) in '{view.Name}'."
                        : $"Set {colorName} override for {ids.Count} element(s) in '{view.Name}'. Saved in the view; reset_view_overrides puts the previous look back.";
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
                        var ogs = ColorOverride(doc, clr, lineWeight: 4);
                        foreach (var id in ids)
                        {
                            ApplyElementOverride(view, id, ogs);
                            total++;
                        }
                    }
                    tx.Commit();
                    var msg = $"Colored {total} element(s) in '{view.Name}' by construction schedule bottom-up {parsedStart:yyyy-MM-dd} (+{interval}d) across {levels.Count} levels, {bics.Count} categories. Palette {paletteName}.";
                    msg += "\nLegend:\n" + string.Join("\n", levels.Select((l, i) => $"{l.Name} ({l.Elevation:F1}ft) = {dateByLevel[l.Name]} -> #{colors[i].Red:X2}{colors[i].Green:X2}{colors[i].Blue:X2}"));
                    msg += "\nSaved in the view's graphic overrides. To undo: reset_view_overrides, or Ctrl+Z.";
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
                        var ogs = ColorOverride(doc, clr);
                        foreach (var id in kv.Value) { ApplyElementOverride(view, id, ogs); total++; }
                        legend.Add($"{kv.Key} ({kv.Value.Count}) -> #{clr.Red:X2}{clr.Green:X2}{clr.Blue:X2}");
                        i++;
                    }
                    tx.Commit();
                    return new CommandResultDto(
                        true,
                        $"Colored {total} element(s) in '{view.Name}' by '{paramName}' ({groups.Count} values). Palette {paletteName}.\nLegend:\n" + string.Join("\n", legend)
                            + "\nSaved in the view's graphic overrides. To undo: reset_view_overrides, or Ctrl+Z.",
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
                var blocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsElementType().Cast<FamilySymbol>().ToList();
                if (blocks.Count == 0)
                    return new CommandResultDto(false, "No title block family is loaded in this model.");
                FamilySymbol tb;
                if (string.IsNullOrWhiteSpace(titleBlockName))
                {
                    tb = blocks[0];
                }
                else
                {
                    // By the type name, or "Family: Type" as Revit shows it. Family names such as "A1 Sheet"
                    // carry no "Title", so the old name test never found them.
                    var matches = blocks.Where(s =>
                        s.Name.Equals(titleBlockName, StringComparison.OrdinalIgnoreCase)
                        || $"{s.FamilyName}: {s.Name}".Equals(titleBlockName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0)
                        return new CommandResultDto(false,
                            $"Title block '{titleBlockName}' not found. Available: {string.Join(", ", blocks.Take(10).Select(s => $"{s.FamilyName}: {s.Name}"))}.");
                    if (matches.Count > 1)
                        return new CommandResultDto(false,
                            $"Title block '{titleBlockName}' exists in {matches.Count} families ({string.Join(", ", matches.Select(s => s.FamilyName))}). Pass 'Family: Type'.");
                    tb = matches[0];
                }

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
                // A schedule goes on a sheet as a ScheduleSheetInstance; every other view is a viewport,
                // and a view can sit on one sheet only.
                var point = new XYZ(x, y, 0);
                var isSchedule = view is ViewSchedule;
                if (!isSchedule && !Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    return new CommandResultDto(false, $"View '{view.Name}' cannot be placed on sheet {sheet.SheetNumber}: it is already on a sheet, or its type cannot be placed.");
                using var tx = new Transaction(doc, $"LoadingAI for Revit: place {viewId} on {sheetId}");
                tx.Start();
                try
                {
                    var placed = isSchedule
                        ? ScheduleSheetInstance.Create(doc, sheet.Id, view.Id, point).Id
                        : Viewport.Create(doc, sheet.Id, view.Id, point).Id;
                    tx.Commit();
                    return new CommandResultDto(true, $"Placed {(isSchedule ? "schedule" : "view")} {viewId} on sheet {sheetId} at ({xMm:F0},{yMm:F0}) mm: {placed}", 1, ElementIds: new[] { placed.ToString() });
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
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                View? view;
                if (string.IsNullOrWhiteSpace(viewId))
                {
                    view = uidoc!.ActiveView;
                    if (view == null) return new CommandResultDto(false, "No active view.");
                }
                else
                {
                    view = doc.GetElement(AllTools.ParseEid(viewId)) as View;
                    if (view == null) return new CommandResultDto(false, $"View {viewId} not found.");
                }
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
        IReadOnlyList<string>? viewIds = null,
        string? templateName = null,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                if (!TryResolveViews(doc, uidoc!, viewId, viewIds, out var views, out var viewError))
                    return new CommandResultDto(false, viewError);
                if (!TryResolveTemplate(doc, templateId, templateName, out var tid, out var targetLabel, out var templateError))
                    return new CommandResultDto(false, templateError);
                var blocked = views.Where(v => TemplateBlock(v) != null).ToList();
                if (blocked.Count > 0)
                    return new CommandResultDto(false, "Cannot set a template on " + string.Join(", ", blocked.Select(v => $"{v.Id} ({TemplateBlock(v)})")) + ".");
                if (dryRun)
                {
                    var lines = views.Select(v => $"{v.Id} {v.Name}: {TemplateLabel(doc, v)} → {targetLabel}");
                    return new CommandResultDto(true, "No model changes.\n" + string.Join("\n", lines));
                }
                using var tx = new Transaction(doc, "LoadingAI for Revit: set view template");
                tx.Start();
                try
                {
                    foreach (var view in views)
                        view.ViewTemplateId = tid;
                    tx.Commit();
                    var names = string.Join(", ", views.Select(v => v.Name));
                    var msg = tid == ElementId.InvalidElementId
                        ? $"Cleared template for {views.Count} view(s): {names}"
                        : $"Set template {targetLabel} for {views.Count} view(s): {names}";
                    return new CommandResultDto(true, msg, views.Count);
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
        IReadOnlyList<string>? viewIds = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                if (!TryResolveViews(doc, uidoc!, viewId, viewIds, out var views, out var viewError))
                    return new CommandResultDto(false, viewError);
                using var tx = new Transaction(doc, "LoadingAI for Revit: set crop");
                tx.Start();
                try
                {
                    foreach (var view in views)
                    {
                        view.CropBoxActive = enabled;
                        view.CropBoxVisible = enabled;
                    }
                    tx.Commit();
                    var names = string.Join(", ", views.Select(v => v.Name));
                    return new CommandResultDto(true, $"Set crop {(enabled ? "on" : "off")} for {views.Count} view(s): {names}", views.Count);
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
                if (view.ViewType is ViewType.ThreeD or ViewType.Schedule or ViewType.DrawingSheet or ViewType.Legend)
                    return new CommandResultDto(false, $"Dimensions cannot be created in a {view.ViewType} view. Use a plan, section or elevation.");
                var w1 = doc.GetElement(AllTools.ParseEid(wallId1)) as Wall;
                var w2 = doc.GetElement(AllTools.ParseEid(wallId2)) as Wall;
                if (w1 == null || w2 == null) return new CommandResultDto(false, "Walls not found.");
                if ((w1.Location as LocationCurve)?.Curve is not Line l1 || (w2.Location as LocationCurve)?.Curve is not Line l2)
                    return new CommandResultDto(false, "create_dimension supports straight walls only.");

                // Two parallel walls: dimension the clear distance between their facing faces.
                var d1 = l1.Direction;
                if (d1.CrossProduct(l2.Direction).GetLength() > 1e-3)
                    return new CommandResultDto(false, "The walls are not parallel. Dimension two parallel walls.");

                var normal = new XYZ(-d1.Y, d1.X, 0).Normalize();
                var across = normal.DotProduct(l2.Evaluate(0.5, true) - l1.Evaluate(0.5, true));
                if (Math.Abs(across) < 1e-6)
                    return new CommandResultDto(false, "The walls are on the same line; there is no distance to dimension.");
                var toward = across > 0 ? normal : normal.Negate();

                var face1 = FacingFace(doc, w1, toward);
                var face2 = FacingFace(doc, w2, toward.Negate());
                if (face1 == null || face2 == null)
                    return new CommandResultDto(false, "Could not find the facing wall faces to dimension.");

                // The dimension line runs across the walls, one foot beyond the end of the first wall.
                var anchor = l1.GetEndPoint(1) + d1 * 1.0;
                var line = Line.CreateBound(anchor, anchor + toward * Math.Abs(across));

                using var tx = new Transaction(doc, "LoadingAI for Revit: create dimension");
                tx.Start();
                try
                {
                    var references = new ReferenceArray();
                    references.Append(face1);
                    references.Append(face2);
                    var dim = doc.Create.NewDimension(view, line, references);
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
        bool dryRun = false,
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
                var all = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(bic.Value).WhereElementIsNotElementType().ToElements();
                if (dryRun)
                {
                    int located = all.Count(el => TagPoint(el, view) != null);
                    var mapped = TagCategoryFor(bic.Value, out var tagBic);
                    var loaded = mapped && new FilteredElementCollector(doc).OfCategory(tagBic).OfClass(typeof(FamilySymbol)).GetElementCount() > 0;
                    var tagSentence = !mapped
                        ? $"This dry-run cannot tell whether a tag for {category} is loaded."
                        : loaded
                            ? $"A tag for {category} is loaded."
                            : $"No tag for {category} is loaded.";
                    var would = loaded ? Math.Min(MaxTagAll, located) : 0;
                    var left = loaded && located > MaxTagAll
                        ? $" {located - MaxTagAll} left because of the cap of {MaxTagAll}."
                        : "";
                    return new CommandResultDto(true,
                        $"No model changes.\n{all.Count} {category} visible in {view.Name}. {located} have a location in this view. {tagSentence} Would tag {would}.{left}");
                }
                var elems = all.Take(MaxTagAll).ToList();
                if (elems.Count == 0)
                    return new CommandResultDto(false, $"No {category} are visible in '{view.Name}'.");
                int tagged = 0;
                var reasons = new List<string>();
                using var tx = new Transaction(doc, "LoadingAI for Revit: tag all");
                tx.Start();
                try
                {
                    foreach (var el in elems)
                    {
                        try
                        {
                            var at = TagPoint(el, view);
                            if (at == null) { reasons.Add($"{el.Id}: no location in this view"); continue; }
                            IndependentTag.Create(doc, view.Id, new Reference(el), true, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, at);
                            tagged++;
                        }
                        catch (Exception tagEx) { reasons.Add($"{el.Id}: {tagEx.Message}"); }
                    }

                    if (tagged == 0)
                    {
                        tx.RollBack();
                        return new CommandResultDto(false, $"Failed: tagged 0 of {elems.Count} {category} in '{view.Name}'. {Why(reasons)}");
                    }

                    tx.Commit();
                    var capped = all.Count > elems.Count ? $" (the first {elems.Count} of {all.Count}; the rest were not tagged)" : "";
                    var missed = tagged < elems.Count ? $" Not tagged: {elems.Count - tagged}. {Why(reasons)}" : "";
                    return new CommandResultDto(true, $"Tagged {tagged}/{elems.Count} {category} in {view.Name}{capped}.{missed}", tagged);
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

    private const int MaxTagAll = 50;

    /// <summary>Where a tag for this element should sit: its point, the middle of its curve, or the middle of its box in the view.</summary>
    private static XYZ? TagPoint(Element el, View view)
    {
        if (el.Location is LocationPoint point) return point.Point;
        if (el.Location is LocationCurve { Curve: { } curve }) return curve.Evaluate(0.5, true);
        var box = el.get_BoundingBox(view);
        return box == null ? null : (box.Min + box.Max) * 0.5;
    }

    private static string Why(List<string> reasons)
        => reasons.Count == 0 ? "" : "Reasons: " + string.Join("; ", reasons.Take(3)) + (reasons.Count > 3 ? "; ..." : "") + ".";

    /// <summary>The side face of a wall whose normal points along <paramref name="facing"/>, as a reference a dimension accepts.</summary>
    private static Reference? FacingFace(Document doc, Wall wall, XYZ facing)
    {
        foreach (var layer in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
        {
            IList<Reference>? faces;
            try { faces = HostObjectUtils.GetSideFaces(wall, layer); }
            catch { continue; }
            if (faces == null || faces.Count == 0) continue;
            if (doc.GetElement(faces[0]).GetGeometryObjectFromReference(faces[0]) is PlanarFace face
                && face.FaceNormal.DotProduct(facing) > 0.5)
                return faces[0];
        }

        return null;
    }

    private static bool TryResolveViews(
        Document doc, UIDocument uidoc, string? viewId, IReadOnlyList<string>? viewIds,
        out List<View> views, out string error)
    {
        views = new List<View>();
        error = "";
        var ids = new List<string>();
        if (viewIds != null)
        {
            foreach (var id in viewIds)
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
        }
        if (ids.Count == 0 && !string.IsNullOrWhiteSpace(viewId))
            ids.Add(viewId.Trim());
        if (ids.Count == 0)
        {
            var active = uidoc.ActiveView;
            if (active == null) { error = "No active view."; return false; }
            views.Add(active);
            return true;
        }
        var missing = new List<string>();
        foreach (var id in ids)
        {
            View? view;
            try { view = doc.GetElement(AllTools.ParseEid(id)) as View; }
            catch { view = null; }
            if (view == null) missing.Add(id);
            else views.Add(view);
        }
        if (missing.Count > 0)
        {
            error = "View not found: " + string.Join(", ", missing) + ".";
            return false;
        }
        return true;
    }

    private static bool TryResolveTemplate(
        Document doc, string? templateId, string? templateName,
        out ElementId id, out string label, out string error)
    {
        id = ElementId.InvalidElementId;
        label = "(none)";
        error = "";
        if (templateName != null)
        {
            if (string.IsNullOrWhiteSpace(templateName)) return true;
            var matches = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => v.IsTemplate && string.Equals(v.Name, templateName.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) { error = $"No view template named '{templateName}'."; return false; }
            if (matches.Count > 1) { error = $"More than one view template named '{templateName}'."; return false; }
            id = matches[0].Id;
            label = matches[0].Name;
            return true;
        }
        if (string.IsNullOrWhiteSpace(templateId)) return true;
        View? template;
        try { template = doc.GetElement(AllTools.ParseEid(templateId)) as View; }
        catch { template = null; }
        if (template == null || !template.IsTemplate)
        {
            error = $"Template {templateId} not found.";
            return false;
        }
        id = template.Id;
        label = template.Name;
        return true;
    }

    private static string TemplateLabel(Document doc, View view)
    {
        var id = view.ViewTemplateId;
        if (id == null || id == ElementId.InvalidElementId) return "(none)";
        return (doc.GetElement(id) as View)?.Name ?? id.ToString();
    }

    private static string? TemplateBlock(View view)
    {
        if (view.IsTemplate) return "a view template";
        if (view.ViewType is ViewType.DrawingSheet or ViewType.Schedule or ViewType.Legend
            or ViewType.ProjectBrowser or ViewType.SystemBrowser)
            return $"a {view.ViewType}";
        return null;
    }

    private static bool TagCategoryFor(BuiltInCategory bic, out BuiltInCategory tag)
    {
        switch (bic)
        {
            case BuiltInCategory.OST_Walls: tag = BuiltInCategory.OST_WallTags; return true;
            case BuiltInCategory.OST_Doors: tag = BuiltInCategory.OST_DoorTags; return true;
            case BuiltInCategory.OST_Windows: tag = BuiltInCategory.OST_WindowTags; return true;
            case BuiltInCategory.OST_Rooms: tag = BuiltInCategory.OST_RoomTags; return true;
            case BuiltInCategory.OST_Floors: tag = BuiltInCategory.OST_FloorTags; return true;
            case BuiltInCategory.OST_Ceilings: tag = BuiltInCategory.OST_CeilingTags; return true;
            case BuiltInCategory.OST_Roofs: tag = BuiltInCategory.OST_RoofTags; return true;
            case BuiltInCategory.OST_Furniture: tag = BuiltInCategory.OST_FurnitureTags; return true;
            case BuiltInCategory.OST_GenericModel: tag = BuiltInCategory.OST_GenericModelTags; return true;
            case BuiltInCategory.OST_Casework: tag = BuiltInCategory.OST_CaseworkTags; return true;
            case BuiltInCategory.OST_PlumbingFixtures: tag = BuiltInCategory.OST_PlumbingFixtureTags; return true;
            case BuiltInCategory.OST_LightingFixtures: tag = BuiltInCategory.OST_LightingFixtureTags; return true;
            case BuiltInCategory.OST_MechanicalEquipment: tag = BuiltInCategory.OST_MechanicalEquipmentTags; return true;
            case BuiltInCategory.OST_DuctCurves: tag = BuiltInCategory.OST_DuctTags; return true;
            case BuiltInCategory.OST_PipeCurves: tag = BuiltInCategory.OST_PipeTags; return true;
            case BuiltInCategory.OST_Areas: tag = BuiltInCategory.OST_AreaTags; return true;
            case BuiltInCategory.OST_StructuralFraming: tag = BuiltInCategory.OST_StructuralFramingTags; return true;
            case BuiltInCategory.OST_StructuralColumns: tag = BuiltInCategory.OST_StructuralColumnTags; return true;
            case BuiltInCategory.OST_StructuralFoundation: tag = BuiltInCategory.OST_StructuralFoundationTags; return true;
            case BuiltInCategory.OST_Stairs: tag = BuiltInCategory.OST_StairsTags; return true;
            case BuiltInCategory.OST_Parking: tag = BuiltInCategory.OST_ParkingTags; return true;
            case BuiltInCategory.OST_Columns: tag = BuiltInCategory.OST_ColumnTags; return true;
            default: tag = default; return false;
        }
    }
}
