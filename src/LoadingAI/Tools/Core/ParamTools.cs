using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Revit;
using LoadingAI.Plugins;

namespace LoadingAI.Tools;

public static class ParamTools
{
    public static IEnumerable<ToolEntry> Create(IPluginContext context)
    {
        yield return CreateSetParameterById(context);
        yield return CreateSetCommentsOnSelection(context);
        yield return CreateBatchSetParameter(context);
        yield return CreateRenameElements(context);
        yield return CreateApplyConstructionDates(context);
    }

    internal static ToolEntry CreateSetParameterById(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "set_parameter_by_id",
                    Description = "Set a parameter on a single element by Id. For Comments use 'Comments', for Mark use 'Mark', etc.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementId = new { type = "string", description = "ElementId" },
                            paramName = new { type = "string", description = "Parameter name, e.g. Comments, Mark, Level" },
                            value = new { type = "string", description = "Value to set" }
                        },
                        required = new[] { "elementId", "paramName", "value" }
                    }
                }
            },
            Handler = async json =>
            {
                string eidStr = "", paramName = "", value = "";
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    eidStr = d.RootElement.GetProperty("elementId").GetString() ?? "";
                    paramName = d.RootElement.GetProperty("paramName").GetString() ?? "";
                    value = d.RootElement.GetProperty("value").GetString() ?? "";
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.SetParameterAsync(eidStr, paramName, value);
                if (!result.Ok) return result.Message;
                return $"Set {paramName}=\"{value}\" on {eidStr}.";
            }
        };
    }

    internal static ToolEntry CreateSetCommentsOnSelection(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "set_comments_on_selection",
                    Description = "Set the Comments parameter on the currently selected elements. Requires user confirmation before writing.",
                    Parameters = new { type = "object", properties = new { text = new { type = "string", description = "Text to write into the Comments parameter" } }, required = new[] { "text" } }
                }
            },
            Handler = async json =>
            {
                string text;
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    if (!d.RootElement.TryGetProperty("text", out var p)) return "Missing required parameter 'text'.";
                    text = p.GetString() ?? "";
                }
                catch (Exception ex) { return $"Invalid arguments: {ex.Message}"; }
                var sel = await context.Bridge.GetSelectionSummaryAsync();
                if (sel.ElementCount == 0) return "Please select elements first.";
                var result = await context.Bridge.SetParametersAsync(sel.ElementIds, "Comments", text);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateBatchSetParameter(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "batch_set_parameter",
                    Description = "Batch set a parameter on all elements matching a category+filter. Previews count, asks confirm, then writes in one transaction.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            category = new { type = "string", description = "Category, e.g. Walls" },
                            filterParam = new { type = "string", description = "Optional filter param name, e.g. Comments" },
                            filterOp = new { type = "string", description = "Filter op =, !=, empty, notempty, contains", @enum = new[] { "=", "!=", "contains", "empty", "notempty" } },
                            filterValue = new { type = "string", description = "Filter value" },
                            targetParam = new { type = "string", description = "Param to set, e.g. Comments, Mark" },
                            targetValue = new { type = "string", description = "Value to set" },
                            limit = new { type = "integer", description = "Max to update, default 200" }
                        },
                        required = new[] { "category", "targetParam", "targetValue" }
                    }
                }
            },
            Handler = async json =>
            {
                string category = "", targetParam = "", targetValue = "";
                string? filterParam = null, filterOp = "=", filterValue = null;
                int limit = 200;
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    category = d.RootElement.GetProperty("category").GetString() ?? "";
                    targetParam = d.RootElement.GetProperty("targetParam").GetString() ?? "";
                    targetValue = d.RootElement.GetProperty("targetValue").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("filterParam", out var fp) && fp.ValueKind == JsonValueKind.String) filterParam = fp.GetString();
                    if (d.RootElement.TryGetProperty("filterOp", out var fo) && fo.ValueKind == JsonValueKind.String) filterOp = fo.GetString();
                    if (d.RootElement.TryGetProperty("filterValue", out var fv) && fv.ValueKind == JsonValueKind.String) filterValue = fv.GetString();
                    if (d.RootElement.TryGetProperty("limit", out var lim) && lim.ValueKind == JsonValueKind.Number) limit = lim.GetInt32();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                limit = Polyfills.Clamp(limit, 1, 1000);
                var found = await context.Bridge.FindElementsFilteredAsync(category, filterParam, filterOp, filterValue, limit, 0);
                if (!string.IsNullOrEmpty(found.Error)) return found.Error;
                if (found.Total == 0) return $"No elements matched filter for {category}.";
                var ids = found.Items.Select(e => e.Id).ToList();
                var result = await context.Bridge.SetParametersAsync(ids, targetParam, targetValue);
                if (!result.Ok) return result.Message;
                return $"Batch updated {result.AffectedCount} {category} (matched {found.Total}, limited {ids.Count}).";
            }
        };
    }

    internal static ToolEntry CreateRenameElements(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "rename_elements",
                    Description = "Rename views/sheets or other named elements by Id. Provide elementIds and newNames array (same length) or newNamePattern with {i}.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" }, description = "Ids to rename" },
                            newNames = new { type = "array", items = new { type = "string" }, description = "New names (same length as ids). Alternative to pattern." },
                            newNamePattern = new { type = "string", description = "Pattern with {i}, e.g. Test-{i}" }
                        },
                        required = new[] { "elementIds" }
                    }
                }
            },
            Handler = async json =>
            {
                List<string> eids = new();
                List<string>? newNames = null;
                string? pattern = null;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    eids = d.RootElement.GetProperty("elementIds").EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("newNames", out var nn) && nn.ValueKind == JsonValueKind.Array) newNames = nn.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("newNamePattern", out var pat) && pat.ValueKind == JsonValueKind.String) pattern = pat.GetString();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if (eids.Count == 0) return "Provide elementIds.";
                if (newNames != null && newNames.Count != eids.Count) return "newNames length must match elementIds.";
                if (newNames == null && string.IsNullOrWhiteSpace(pattern)) return "Provide newNames or newNamePattern.";
                var newNamesCapture = newNames; var patternCapture = pattern;
                var preview = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var s = "";
                    for (int i = 0; i < eids.Count; i++)
                    {
                        var el = doc.GetElement(AllTools.ParseEid(eids[i]));
                        var nn = newNamesCapture != null ? newNamesCapture[i] : patternCapture!.Replace("{i}", i.ToString());
                        s += $"{eids[i]} \"{el?.Name}\" -> \"{nn}\"\n";
                    }
                    return s;
                });
                bool confirmed = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var td = new TaskDialog("LoadingAI for Revit") { MainInstruction = $"Rename {eids.Count} element(s)?", MainContent = preview, CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
                    return td.Show() == TaskDialogResult.Yes;
                });
                if (!confirmed) return "Cancelled by user.";
                var result = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    int updated = 0;
                    using var tx = new Transaction(doc, $"LoadingAI for Revit: rename {eids.Count} elements");
                    tx.Start();
                    try
                    {
                        for (int i = 0; i < eids.Count; i++)
                        {
                            var el = doc.GetElement(AllTools.ParseEid(eids[i]));
                            if (el == null) continue;
                            var nn = newNamesCapture != null ? newNamesCapture[i] : patternCapture!.Replace("{i}", i.ToString());
                            // View.Name, etc.
                            el.Name = nn;
                            updated++;
                        }
                        tx.Commit();
                    }
                    catch { try { tx.RollBack(); } catch { } throw; }
                    return $"Renamed {updated}/{eids.Count} element(s).";
                });
                return result;
            }
        };
    }

    // ---------- geometry / create ----------


    internal static ToolEntry CreateApplyConstructionDates(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "apply_construction_dates",
                    Description = "Bulk apply Construction Date (or any text param) bottom-up by level elevation in ONE transaction. Provide startDate (YYYY-MM-DD or YYYYMMDD) and intervalDays. Colors? No ??this only sets the parameter value. For coloring use color_by_construction_schedule. Handles all levels sorted low?igh and all categories at once to avoid max rounds.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            startDate = new { type = "string", description = "Start date for lowest level, e.g. 2024-01-08 or 20240108" },
                            intervalDays = new { type = "integer", description = "Days between levels, default 14" },
                            targetParam = new { type = "string", description = "Param name to write, default 'Construction Date'. Falls back to 'Comments' if not found." },
                            categories = new { type = "array", items = new { type = "string" }, description = "Categories to apply, e.g. [\"Walls\",\"Floors\"]. Default all common building categories." }
                        },
                        required = new[] { "startDate" }
                    }
                }
            },
            Handler = async json =>
            {
                string startDateStr = ""; int interval = 14; string targetParam = "Construction Date"; List<string>? cats = null;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    startDateStr = d.RootElement.GetProperty("startDate").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("intervalDays", out var iv) && iv.ValueKind == JsonValueKind.Number) interval = iv.GetInt32();
                    if (d.RootElement.TryGetProperty("targetParam", out var tp) && tp.ValueKind == JsonValueKind.String) targetParam = tp.GetString() ?? targetParam;
                    if (d.RootElement.TryGetProperty("categories", out var ca) && ca.ValueKind == JsonValueKind.Array) cats = ca.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if (!TryParseDate(startDateStr, out var startDate)) return $"Invalid startDate '{startDateStr}'. Use YYYY-MM-DD or YYYYMMDD.";
                interval = Polyfills.Clamp(interval, 1, 365);

                // Preview: get levels
                var preview = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return (err: "Please open a model first.", levels: Array.Empty<(string name, double elev)>());
                    try
                    {
                        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).Select(l => (name: l.Name, elev: l.Elevation)).ToArray();
                        return (err: "", levels: levels);
                    }
                    catch (Exception ex) { return (err: ex.Message, levels: Array.Empty<(string name, double elev)>()); }
                });
                if (!string.IsNullOrEmpty(preview.err)) return preview.err;
                var levelNames = preview.levels.Select(l => l.name).ToArray();
                var schedule = levelNames.Select((n, i) => (n, date: startDate.AddDays(i * interval).ToString("yyyy-MM-dd"))).ToArray();
                var previewText = string.Join("\n", schedule.Select(s => $"{s.n} -> {s.date}"));
                bool confirmed = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var td = new TaskDialog("LoadingAI for Revit")
                    {
                        MainInstruction = $"Apply {targetParam} bottom-up from {startDate:yyyy-MM-dd} (+{interval}d) to {levelNames.Length} levels?",
                        MainContent = previewText + $"\n\nCategories: {(cats == null ? "all common" : string.Join(", ", cats))}\nOne transaction, undoable.",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.No
                    };
                    return td.Show() == TaskDialogResult.Yes;
                });
                if (!confirmed) return "Cancelled by user.";

                var result = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                        var levelById = levels.ToDictionary(l => l.Id, l => l.Name);
                        var levelByName = levels.ToDictionary(l => l.Name, l => l, StringComparer.OrdinalIgnoreCase);
                        var dateByLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < levels.Count; i++) dateByLevel[levels[i].Name] = startDate.AddDays(i * interval).ToString("yyyy-MM-dd");

                        // Categories to process
                        List<BuiltInCategory> bics = new();
                        if (cats != null && cats.Count > 0)
                        {
                            foreach (var c in cats) { var bic = AllTools.ParseCategory(c); if (bic != null) bics.Add(bic.Value); }
                        }
                        else
                        {
                            string[] defaults = { "Walls", "Doors", "Windows", "Floors", "Ceilings", "Roofs", "Structural Framing", "Structural Columns", "Columns", "Generic Models", "Furniture", "Rooms" };
                            foreach (var c in defaults) { var bic = AllTools.ParseCategory(c); if (bic != null) bics.Add(bic.Value); }
                        }

                        int updated = 0, skipped = 0, noParam = 0;
                        using var tx = new Transaction(doc, $"LoadingAI for Revit: apply {targetParam} bottom-up");
                        tx.Start();
                        try
                        {
                            foreach (var bic in bics)
                            {
                                var elems = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements();
                                foreach (var el in elems)
                                {
                                    string? lvlName = null;
                                    if (el.LevelId != ElementId.InvalidElementId && levelById.TryGetValue(el.LevelId, out var ln)) lvlName = ln;
                                    else
                                    {
                                        // try params
                                        var p = el.LookupParameter("Base Constraint") ?? el.LookupParameter("Level") ?? el.LookupParameter("Reference Level") ?? el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ?? el.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT) ?? el.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                                        if (p != null)
                                        {
                                            if (p.StorageType == StorageType.ElementId) { var id = p.AsElementId(); if (levelById.TryGetValue(id, out var n2)) lvlName = n2; }
                                            else lvlName = p.AsString() ?? p.AsValueString();
                                        }
                                        if (lvlName != null && !levelByName.ContainsKey(lvlName)) lvlName = null; // ignore non-level values like "Parking" may still be valid but not in level list? Keep it if matches level list else fallback to nearest? For now skip unmatched
                                    }
                                    if (lvlName == null || !dateByLevel.TryGetValue(lvlName, out var dateVal)) continue;
                                    if (!AllTools.TrySetParameter(el, targetParam, dateVal, out var err))
                                    {
                                        // fallback to Comments if Construction Date missing
                                        if (targetParam.Equals("Construction Date", StringComparison.OrdinalIgnoreCase) && err.Contains("not found"))
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
                        }
                        catch
                        {
                            try { tx.RollBack(); } catch { }
                            throw;
                        }
                        var msg = $"Applied {targetParam} bottom-up {startDate:yyyy-MM-dd} (+{interval}d) to {updated} element(s) across {bics.Count} categories, {levels.Count} levels.";
                        if (skipped > 0) msg += $" Skipped {skipped} (read-only/type).";
                        if (noParam > 0) msg += $" No param on {noParam} (used Comments fallback where possible).";
                        msg += "\nSchedule:\n" + string.Join("\n", schedule.Select(s => $"{s.n} = {s.date}"));
                        return msg;
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static bool TryParseDate(string s, out DateTime dt)
    {
        s = s.Trim();
        if (DateTime.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt)) return true;
        if (DateTime.TryParseExact(s, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt)) return true;
        if (DateTime.TryParse(s, out dt)) return true;
        dt = default;
        return false;
    }

}
