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
/// Parameter slice: set, rename, construction dates, Excel/CSV import and export, copy, replace text, numbering.
/// Implements <c>IRevitParamCommand</c>. Part of <see cref="AdapterRevitBridge"/>; see that type for the contract.
/// </summary>
public sealed partial class AdapterRevitBridge
{
    public Task<CommandResultDto> SetParameterAsync(
        string elementId, string paramName, string value,
        CancellationToken ct = default)
    {
        return SetParametersAsync(new[] { elementId }, paramName, value, ct);
    }

    public Task<CommandResultDto> SetParametersAsync(
        IReadOnlyList<string> elementIds, string paramName, string value,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (uidoc == null || doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (elementIds == null || elementIds.Count == 0)
                return new CommandResultDto(false, "Please select elements first or provide elementIds.");

            using var tx = new Transaction(doc, $"LoadingAI for Revit: set {paramName} on {elementIds.Count}");
            tx.Start();
            try
            {
                int updated = 0, skipped = 0;
                var skippedIds = new List<string>();
                string? lastErr = null;
                foreach (var eidStr in elementIds)
                {
                    Element? el;
                    try { el = doc.GetElement(AllTools.ParseEid(eidStr)); }
                    catch { skipped++; skippedIds.Add(eidStr); continue; }
                    if (el == null) { skipped++; skippedIds.Add(eidStr); continue; }
                    if (!AllTools.TrySetParameter(el, paramName, value, out var err))
                    {
                        skipped++;
                        skippedIds.Add(eidStr);
                        lastErr = err;
                        continue;
                    }
                    updated++;
                }
                if (updated == 0)
                {
                    tx.RollBack();
                    return new CommandResultDto(false, lastErr ?? $"Could not set {paramName} on any of {elementIds.Count} element(s).");
                }
                tx.Commit();
                var msg = $"Updated {updated} element(s).";
                if (skipped > 0)
                    msg += $" Skipped {skipped}: {string.Join(", ", skippedIds.Take(10))}" + (skipped > 10 ? "..." : "");
                return new CommandResultDto(true, msg, updated);
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> RenameElementsAsync(
        IReadOnlyList<string> elementIds,
        IReadOnlyList<string>? newNames = null,
        string? newNamePattern = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (elementIds == null || elementIds.Count == 0)
                return new CommandResultDto(false, "Provide elementIds.");
            if (newNames != null && newNames.Count != elementIds.Count)
                return new CommandResultDto(false, "newNames length must match elementIds.");
            if (newNames == null && string.IsNullOrWhiteSpace(newNamePattern))
                return new CommandResultDto(false, "Provide newNames or newNamePattern.");

            using var tx = new Transaction(doc, $"LoadingAI for Revit: rename {elementIds.Count} elements");
            tx.Start();
            try
            {
                int updated = 0;
                for (int i = 0; i < elementIds.Count; i++)
                {
                    Element? el;
                    try { el = doc.GetElement(AllTools.ParseEid(elementIds[i])); }
                    catch { continue; }
                    if (el == null) continue;
                    var nn = newNames != null ? newNames[i] : newNamePattern!.Replace("{i}", i.ToString());
                    el.Name = nn;
                    updated++;
                }
                tx.Commit();
                return new CommandResultDto(true, $"Renamed {updated}/{elementIds.Count} element(s).", updated);
            }
            catch (Exception failEx)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> ApplyConstructionDatesAsync(
        string startDate,
        int intervalDays = 14,
        string? targetParam = null,
        IReadOnlyList<string>? categories = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            if (!AllTools.TryParseDate(startDate ?? "", out var parsedStart))
                return new CommandResultDto(false, $"Invalid startDate '{startDate}'. Use YYYY-MM-DD or YYYYMMDD.");
            var interval = intervalDays < 1 ? 1 : (intervalDays > 365 ? 365 : intervalDays);
            var paramName = string.IsNullOrWhiteSpace(targetParam) ? "Construction Date" : targetParam;

            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");

            try
            {
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                var levelById = levels.ToDictionary(l => l.Id, l => l.Name);
                var levelByName = levels.ToDictionary(l => l.Name, l => l, StringComparer.OrdinalIgnoreCase);
                var dateByLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < levels.Count; i++)
                    dateByLevel[levels[i].Name] = parsedStart.AddDays(i * interval).ToString("yyyy-MM-dd");

                var bics = new List<BuiltInCategory>();
                if (categories != null && categories.Count > 0)
                {
                    foreach (var c in categories)
                    {
                        var bic = AllTools.ParseCategory(c);
                        if (bic != null) bics.Add(bic.Value);
                    }
                }
                else
                {
                    string[] defaults =
                    {
                        "Walls", "Doors", "Windows", "Floors", "Ceilings", "Roofs",
                        "Structural Framing", "Structural Columns", "Columns",
                        "Generic Models", "Furniture", "Rooms"
                    };
                    foreach (var c in defaults)
                    {
                        var bic = AllTools.ParseCategory(c);
                        if (bic != null) bics.Add(bic.Value);
                    }
                }

                int updated = 0, skipped = 0, noParam = 0;
                using var tx = new Transaction(doc, $"LoadingAI for Revit: apply {paramName} bottom-up");
                tx.Start();
                try
                {
                    foreach (var bic in bics)
                    {
                        var elems = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements();
                        foreach (var el in elems)
                        {
                            string? lvlName = null;
                            if (el.LevelId != ElementId.InvalidElementId && levelById.TryGetValue(el.LevelId, out var ln))
                                lvlName = ln;
                            else
                            {
                                var p = el.LookupParameter("Base Constraint")
                                    ?? el.LookupParameter("Level")
                                    ?? el.LookupParameter("Reference Level")
                                    ?? el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                                    ?? el.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)
                                    ?? el.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                                if (p != null)
                                {
                                    if (p.StorageType == StorageType.ElementId)
                                    {
                                        var id = p.AsElementId();
                                        if (levelById.TryGetValue(id, out var n2)) lvlName = n2;
                                    }
                                    else lvlName = p.AsString() ?? p.AsValueString();
                                    if (lvlName != null && !levelByName.ContainsKey(lvlName)) lvlName = null;
                                }
                            }
                            if (lvlName == null || !dateByLevel.TryGetValue(lvlName, out var dateVal)) continue;
                            if (!AllTools.TrySetParameter(el, paramName, dateVal, out var err))
                            {
                                if (paramName.Equals("Construction Date", StringComparison.OrdinalIgnoreCase) && err.Contains("not found"))
                                {
                                    if (!AllTools.TrySetParameter(el, "Comments", dateVal, out _)) noParam++;
                                    else updated++;
                                    continue;
                                }
                                skipped++;
                                continue;
                            }
                            updated++;
                        }
                    }
                    tx.Commit();
                    var msg = $"Applied {paramName} bottom-up {parsedStart:yyyy-MM-dd} (+{interval}d) to {updated} element(s) across {bics.Count} categories, {levels.Count} levels.";
                    if (skipped > 0) msg += $" Skipped {skipped} (read-only/type).";
                    if (noParam > 0) msg += $" No param on {noParam} (used Comments fallback where possible).";
                    msg += "\nSchedule:\n" + string.Join("\n", levels.Select((l, i) => $"{l.Name} = {parsedStart.AddDays(i * interval):yyyy-MM-dd}"));
                    return new CommandResultDto(true, msg, updated);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<ParamExportDto?> ExportParamsAsync(
        string category,
        IReadOnlyList<string> columns,
        int limit = 200,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var cat = category ?? "";
            var cols = columns == null || columns.Count == 0
                ? new[] { "Comments", "Mark" }
                : columns.ToArray();
            var lim = limit < 1 ? 1 : (limit > 1000 ? 1000 : limit);
            var bic = AllTools.ParseCategory(cat);
            if (bic == null)
                return new ParamExportDto(cat, cols, Array.Empty<ParamExportRowDto>(), $"Unknown category {cat}");

            var elems = new FilteredElementCollector(doc)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToElements()
                .Take(lim)
                .ToList();
            var rows = new List<ParamExportRowDto>(elems.Count);
            foreach (var el in elems)
            {
                var vals = new List<string>(cols.Length);
                foreach (var c in cols)
                {
                    var p = el.LookupParameter(c);
                    string v = "";
                    if (p != null)
                        v = p.StorageType switch
                        {
                            StorageType.String => p.AsString() ?? "",
                            StorageType.Double => ParamUnits.FormatNumber(RevitParse.UnitKindOf(p), p.AsDouble()),
                            _ => p.AsValueString() ?? p.AsString() ?? ""
                        };
                    vals.Add(v);
                }
                rows.Add(new ParamExportRowDto(el.Id.ToString(), vals));
            }
            return new ParamExportDto(cat, cols, rows);
        }, ct);
    }

    public Task<CommandResultDto> ImportParamsAsync(
        string paramName,
        IReadOnlyList<ParamImportRowDto> rows,
        string sourcePath,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var list = rows ?? Array.Empty<ParamImportRowDto>();
            int upd = 0, skip = 0;
            using var tx = new Transaction(doc, $"LoadingAI for Revit: import {paramName}");
            tx.Start();
            try
            {
                foreach (var row in list)
                {
                    try
                    {
                        var el = doc.GetElement(AllTools.ParseEid(row.Id));
                        if (el == null) { skip++; continue; }
                        if (!AllTools.TrySetParameter(el, paramName, row.Value, out _)) { skip++; continue; }
                        upd++;
                    }
                    catch { skip++; }
                }
                tx.Commit();
                return new CommandResultDto(true, $"Imported {upd}/{list.Count} ({skip} skipped) {paramName} from {sourcePath}", upd);
            }
            catch (Exception failEx)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> CopyParamsAsync(
        string category,
        string sourceParam,
        string targetParam,
        int limit = 200,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var bic = AllTools.ParseCategory(category);
                if (bic == null) return new CommandResultDto(false, $"Unknown category {category}");
                var lim = limit < 1 ? 1 : (limit > 1000 ? 1000 : limit);
                var elems = new FilteredElementCollector(doc)
                    .OfCategory(bic.Value)
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .Take(lim)
                    .ToList();
                int upd = 0;
                using var tx = new Transaction(doc, $"LoadingAI for Revit: copy {sourceParam}->{targetParam}");
                tx.Start();
                try
                {
                    foreach (var el in elems)
                    {
                        if (el.LookupParameter(sourceParam) == null) continue;
                        if (AllTools.TryCopyParameter(el, sourceParam, targetParam, out _)) upd++;
                    }
                    tx.Commit();
                    return new CommandResultDto(true, $"Copied {upd}/{elems.Count} {sourceParam}->{targetParam} for {category}", upd);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> ReplaceTextInParamAsync(
        string category,
        string paramName,
        string pattern,
        string replacement,
        int limit = 200,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var bic = AllTools.ParseCategory(category);
                if (bic == null) return new CommandResultDto(false, $"Unknown category {category}");
                var lim = limit < 1 ? 1 : (limit > 1000 ? 1000 : limit);
                var elems = new FilteredElementCollector(doc)
                    .OfCategory(bic.Value)
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .Take(lim)
                    .ToList();
                int upd = 0;
                var re = new System.Text.RegularExpressions.Regex(pattern);
                using var tx = new Transaction(doc, $"LoadingAI for Revit: replace {paramName}");
                tx.Start();
                try
                {
                    foreach (var el in elems)
                    {
                        var p = el.LookupParameter(paramName);
                        if (p == null || p.StorageType != StorageType.String) continue;
                        var cur = p.AsString() ?? "";
                        var nxt = re.Replace(cur, replacement);
                        if (cur == nxt) continue;
                        if (AllTools.TrySetParameter(el, paramName, nxt, out _)) upd++;
                    }
                    tx.Commit();
                    return new CommandResultDto(true, $"Replaced {upd}/{elems.Count} {paramName} /{pattern}/ -> {replacement}", upd);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> NumberElementsAsync(
        string? category,
        string prefix,
        int start,
        int pad,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            if (uidoc == null) return new CommandResultDto(false, "Please open a model first.");
            var doc = uidoc.Document;
            try
            {
                ICollection<ElementId> ids;
                if (!string.IsNullOrWhiteSpace(category))
                {
                    var bic = AllTools.ParseCategory(category);
                    if (bic == null) return new CommandResultDto(false, $"Unknown category {category}");
                    ids = new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType().ToElementIds();
                }
                else ids = uidoc.Selection.GetElementIds();
                if (ids.Count == 0) return new CommandResultDto(false, "Please select elements first or provide category.");
                var pref = prefix ?? "";
                int n = start;
                int upd = 0;
                using var tx = new Transaction(doc, "LoadingAI for Revit: auto numbering");
                tx.Start();
                try
                {
                    foreach (var id in ids)
                    {
                        var el = doc.GetElement(id);
                        if (el == null) continue;
                        var mark = pref + n.ToString().PadLeft(pad, '0');
                        if (AllTools.TrySetParameter(el, "Mark", mark, out _)) upd++;
                        n++;
                    }
                    tx.Commit();
                    return new CommandResultDto(true, $"Numbered {upd}/{ids.Count} Mark from {pref}{start.ToString().PadLeft(pad, '0')}", upd);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }
}
