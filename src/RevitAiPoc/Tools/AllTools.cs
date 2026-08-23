using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAiPoc.Llm;
using RevitAiPoc.Revit;
using RevitAiPoc.Tools.Compat;

namespace RevitAiPoc.Tools;

public static class AllTools
{
    public static IEnumerable<ToolEntry> CreateAll(RevitBridge bridge, QueryCache cache)
    {
        yield return CreateGetModelSummary(bridge, cache);
        yield return CreateGetWallMetrics(bridge, cache);
        yield return CreateGetSelectionSummary(bridge);
        yield return CreateGetActiveViewInfo(bridge, cache);
        yield return CreateGetVisibleElementsInView(bridge, cache);
        yield return CreateFindElementsByFilter(bridge, cache);
        yield return CreateGetElementDetails(bridge);
        yield return CreateGetWarnings(bridge, cache);
        yield return CreateGetLevelsAndGrids(bridge, cache);
        yield return CreateGetQuantities(bridge);
        // L2 view
        yield return CreateIsolateInView(bridge);
        yield return CreateHideInView(bridge);
        yield return CreateResetTemporaryView(bridge);
        yield return CreateOverrideColorInView(bridge);
        // param
        yield return CreateSetParameterById(bridge);
        yield return CreateSetCommentsOnSelection(bridge);
        yield return CreateBatchSetParameter(bridge);
        yield return CreateRenameElements(bridge);
        // geometry / destructive / create
        yield return CreateMoveElements(bridge);
        yield return CreateDeleteElements(bridge);
        yield return CreateCreateWall(bridge);
        yield return CreateCreateFamilyInstance(bridge);
        yield return CreateDuplicateElement(bridge);
        yield return CreateSaveAndSyncInfo(bridge, cache);
        yield return CreateApplyConstructionDates(bridge);
        yield return CreateColorByConstructionSchedule(bridge);
        // --- Compat 30+ native (pyRevit/Dynamo/AppStore) ---
        foreach (var t in QueryHealthTools.Create(bridge, cache)) yield return t;
        foreach (var t in ParamExcelTools.Create(bridge, cache)) yield return t;
        foreach (var t in SheetDocTools.Create(bridge, cache)) yield return t;
        foreach (var t in GeometryCreateTools.Create(bridge, cache)) yield return t;
        foreach (var t in WorksharingTools.Create(bridge, cache)) yield return t;
        foreach (var t in IfcCostTools.Create(bridge, cache)) yield return t;
    }

    // ---------- helpers ----------

    public static BuiltInCategory? ParseCategory(string? name)
    {
        return ParseCategoryInternal(name);
    }
    private static BuiltInCategory? ParseCategoryInternal(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        // normalize: Walls -> OST_Walls, Wall -> OST_Walls etc
        if (n.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
        {
            if (Enum.TryParse<BuiltInCategory>(n, true, out var bic1)) return bic1;
            return null;
        }
        // common aliases
        var map = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
        {
            ["wall"] = BuiltInCategory.OST_Walls, ["walls"] = BuiltInCategory.OST_Walls,
            ["door"] = BuiltInCategory.OST_Doors, ["doors"] = BuiltInCategory.OST_Doors,
            ["window"] = BuiltInCategory.OST_Windows, ["windows"] = BuiltInCategory.OST_Windows,
            ["floor"] = BuiltInCategory.OST_Floors, ["floors"] = BuiltInCategory.OST_Floors,
            ["ceiling"] = BuiltInCategory.OST_Ceilings, ["ceilings"] = BuiltInCategory.OST_Ceilings,
            ["roof"] = BuiltInCategory.OST_Roofs, ["roofs"] = BuiltInCategory.OST_Roofs,
            ["column"] = BuiltInCategory.OST_Columns, ["columns"] = BuiltInCategory.OST_Columns,
            ["beam"] = BuiltInCategory.OST_StructuralFraming, ["beams"] = BuiltInCategory.OST_StructuralFraming,
            ["structural framing"] = BuiltInCategory.OST_StructuralFraming,
            ["furniture"] = BuiltInCategory.OST_Furniture, ["generic model"] = BuiltInCategory.OST_GenericModel,
            ["generic models"] = BuiltInCategory.OST_GenericModel,
            ["level"] = BuiltInCategory.OST_Levels, ["levels"] = BuiltInCategory.OST_Levels,
            ["grid"] = BuiltInCategory.OST_Grids, ["grids"] = BuiltInCategory.OST_Grids,
            ["view"] = BuiltInCategory.OST_Views, ["views"] = BuiltInCategory.OST_Views,
            ["sheet"] = BuiltInCategory.OST_Sheets, ["sheets"] = BuiltInCategory.OST_Sheets,
            ["room"] = BuiltInCategory.OST_Rooms, ["rooms"] = BuiltInCategory.OST_Rooms,
            ["area"] = BuiltInCategory.OST_Areas, ["areas"] = BuiltInCategory.OST_Areas,
            ["pipe"] = BuiltInCategory.OST_PipeCurves, ["pipes"] = BuiltInCategory.OST_PipeCurves,
            ["duct"] = BuiltInCategory.OST_DuctCurves, ["ducts"] = BuiltInCategory.OST_DuctCurves,
        };
        if (map.TryGetValue(n, out var v)) return v;
        // try OST_ + upper
        var cand = "OST_" + n.Replace(" ", "");
        if (Enum.TryParse<BuiltInCategory>(cand, true, out var bic2)) return bic2;
        return null;
    }

    public static ElementId ParseEid(string s)
    {
        return ParseEidInternal(s);
    }
    private static ElementId ParseEidInternal(string s)
    {
        s = s.Trim();
        if (long.TryParse(s, out var l)) return new ElementId(l);
        // try ElementId.Parse if exists? fallback
        throw new FormatException($"Invalid ElementId '{s}'");
    }

    public static bool TrySetParameter(Element el, string paramName, string value, out string error)
    {
        return TrySetParameterInternal(el, paramName, value, out error);
    }
    private static bool TrySetParameterInternal(Element el, string paramName, string value, out string error)
    {
        error = "";
        Parameter? p = el.LookupParameter(paramName);
        if (p == null) p = el.get_Parameter((BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), paramName, true));
        // Actually try BuiltInParameter lookup only if string matches; fallback
        if (p == null)
        {
            // try ALL_MODEL_INSTANCE_COMMENTS alias
            if (paramName.Equals("Comments", StringComparison.OrdinalIgnoreCase))
                p = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        }
        if (p == null) { error = $"Parameter '{paramName}' not found on element {el.Id}"; return false; }
        if (p.IsReadOnly) { error = $"Parameter '{paramName}' is read-only on element {el.Id}"; return false; }
        try
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    p.Set(value);
                    break;
                case StorageType.Integer:
                    if (int.TryParse(value, out var iv)) p.Set(iv);
                    else if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) p.Set(1);
                    else if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) p.Set(0);
                    else { error = $"Cannot set integer param '{paramName}' to '{value}'"; return false; }
                    break;
                case StorageType.Double:
                    if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dv))
                    {
                        // For length/area params, value is assumed in feet if numeric; we keep as-is. If user provided mm, we could convert but keep simple.
                        p.Set(dv);
                    }
                    else { error = $"Cannot set double param '{paramName}' to '{value}'"; return false; }
                    break;
                case StorageType.ElementId:
                    if (long.TryParse(value, out var eid)) p.Set(new ElementId(eid));
                    else { error = $"Cannot set ElementId param '{paramName}' to '{value}'"; return false; }
                    break;
                default:
                    error = $"Unsupported storage type {p.StorageType}";
                    return false;
            }
        }
        catch (Exception ex) { error = ex.Message; return false; }
        return true;
    }

    private static bool Confirm(RevitBridge bridge, int count, string operation, string preview)
    {
        bool confirmed = false;
        // Blockingly run on Revit UI thread
        var t = bridge.RunAsync(uiApp =>
        {
            var td = new TaskDialog("Revit AI POC")
            {
                MainInstruction = $"{operation} on {count} element(s)?",
                MainContent = preview + "\n\nThis will create an undoable transaction.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            var r = td.Show();
            return r == TaskDialogResult.Yes;
        });
        t.Wait();
        confirmed = t.Result;
        return confirmed;
    }

    // ---------- L1 tools ----------

    private static ToolEntry CreateGetModelSummary(RevitBridge bridge, QueryCache cache)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_model_summary",
                    Description = "Get summary of the active Revit model: title, path, view count, wall/door/window instance counts. No parameters.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                var key = cache.BuildKey("get_model_summary", "");
                // We cache per doc path dynamically inside RunAsync
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var cacheKey = cache.BuildKey("get_model_summary", doc.PathName);
                    if (cache.TryGet<string>(cacheKey, out var cached)) return cached;
                    try
                    {
                        var title = doc.Title;
                        var path = string.IsNullOrEmpty(doc.PathName) ? "(unsaved)" : doc.PathName;
                        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => !v.IsTemplate);
                        int walls = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().ToElementIds().Count;
                        int doors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().ToElementIds().Count;
                        int windows = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType().ToElementIds().Count;
                        var s = $"Model: {title}\nPath: {path}\nViews: {views}\nWalls: {walls}\nDoors: {doors}\nWindows: {windows}";
                        cache.Set(cacheKey, s);
                        return s;
                    }
                    catch (Exception ex) { return $"Failed to read model: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateGetWallMetrics(RevitBridge bridge, QueryCache cache)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_wall_metrics",
                    Description = "Get wall quantities: count, total length (feet), total area (sq ft), total volume (cu ft) for all walls or filtered by level name. Use for 'total wall area/length' questions.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            levelName = new { type = "string", description = "Optional level name to filter walls" },
                            limit = new { type = "integer", description = "Max walls to sample for detail, default 500" }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                string? levelName = null;
                int limit = 500;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.TryGetProperty("levelName", out var lv) && lv.ValueKind == JsonValueKind.String) levelName = lv.GetString();
                    if (d.RootElement.TryGetProperty("limit", out var lim) && lim.ValueKind == JsonValueKind.Number) limit = lim.GetInt32();
                }
                catch { }

                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var ck = cache.BuildKey("get_wall_metrics", doc.PathName, levelName ?? "" + limit);
                    if (cache.TryGet<string>(ck, out var cached)) return cached;
                    try
                    {
                        var collector = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType();
                        var walls = collector.ToElements().Where(e=> e is Wall).Cast<Wall>().ToList();
                        if (!string.IsNullOrWhiteSpace(levelName))
                        {
                            walls = walls.Where(w => w.LevelId != ElementId.InvalidElementId && doc.GetElement(w.LevelId) is Level lvl && lvl.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase)).ToList();
                        }
                        int count = walls.Count;
                        double totalLength = 0, totalArea = 0, totalVol = 0;
                        int sampled = 0;
                        foreach (var w in walls.Take(limit))
                        {
                            var lenP = w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                            if (lenP != null && lenP.StorageType == StorageType.Double) totalLength += lenP.AsDouble();
                            // HOST_AREA_COMPUTED etc may not exist on all wall types; try lookup
                            var areaP = w.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                            if (areaP != null && areaP.StorageType == StorageType.Double) totalArea += areaP.AsDouble();
                            var volP = w.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                            if (volP != null && volP.StorageType == StorageType.Double) totalVol += volP.AsDouble();
                            sampled++;
                        }
                        if (walls.Count > limit) totalLength = totalLength / sampled * count; // rough extrapolate? Better keep sampled total and note truncated
                        var s = $"Walls: {count} (sampled {Math.Min(count, limit)})";
                        if (!string.IsNullOrWhiteSpace(levelName)) s += $" on level '{levelName}'";
                        s += $"\nTotal length: {totalLength:F2} ft";
                        s += $"\nTotal area: {totalArea:F2} sq ft";
                        s += $"\nTotal volume: {totalVol:F2} cu ft";
                        if (count > limit) s += $"\nNote: limited to {limit} walls for performance; totals scaled from sample.";
                        cache.Set(ck, s);
                        return s;
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateGetSelectionSummary(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_selection_summary",
                    Description = "Summarize current selection: count by category and type, with sample element IDs and names. No parameters.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                var result = await bridge.RunAsync(uiApp =>
                {
                    var uidoc = uiApp.ActiveUIDocument;
                    if (uidoc == null) return "Please open a model first.";
                    var ids = uidoc.Selection.GetElementIds();
                    if (ids.Count == 0) return "Please select elements first. (0 selected)";
                    var doc = uidoc.Document;
                    var byCat = new Dictionary<string, int>();
                    var byType = new Dictionary<string, int>();
                    var samples = new List<string>();
                    int i = 0;
                    foreach (var eid in ids.Take(100))
                    {
                        var el = doc.GetElement(eid);
                        if (el == null) continue;
                        var cat = el.Category?.Name ?? "No Category";
                        byCat[cat] = byCat.TryGetValue(cat, out var c) ? c + 1 : 1;
                        var typeName = el is ElementType ? el.Name : (doc.GetElement(el.GetTypeId()) as ElementType)?.Name ?? el.GetType().Name;
                        byType[typeName] = byType.TryGetValue(typeName, out var ct) ? ct + 1 : 1;
                        if (i < 10) samples.Add($"{eid} ({el.Category?.Name}:{typeName} \"{el.Name}\")");
                        i++;
                    }
                    var s = $"Selected: {ids.Count} element(s)\nBy category:\n" + string.Join("\n", byCat.Select(kv => $"  {kv.Key}: {kv.Value}"));
                    s += "\nBy type:\n" + string.Join("\n", byType.Select(kv => $"  {kv.Key}: {kv.Value}"));
                    s += "\nSample (first 10):\n" + string.Join("\n", samples);
                    if (ids.Count > 100) s += $"\n... and {ids.Count - 100} more.";
                    return s;
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateGetActiveViewInfo(RevitBridge bridge, QueryCache cache)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_active_view_info",
                    Description = "Get active view info: name, type, scale, level, visible element counts by category. Use for 'what is in the active view'.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            limit = new { type = "integer", description = "Max elements to sample, default 200" }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                int limit = 200;
                try { using var d = JsonDocument.Parse(json); if (d.RootElement.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number) limit = l.GetInt32(); } catch { }
                limit = Math.Clamp(limit, 1, 500);
                var result = await bridge.RunAsync(uiApp =>
                {
                    var uidoc = uiApp.ActiveUIDocument;
                    if (uidoc == null) return "Please open a model first.";
                    var doc = uidoc.Document;
                    var view = doc.ActiveView;
                    if (view == null) return "No active view.";
                    var ck = cache.BuildKey("get_active_view_info", doc.PathName + view.Id.ToString(), limit.ToString());
                    if (cache.TryGet<string>(ck, out var cached)) return cached;
                    try
                    {
                        var name = view.Name;
                        var vt = view.ViewType;
                        var scale = view.Scale;
                        var detail = view.DetailLevel;
                        // Count visible elements per category (sample)
                        var collector = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
                        var all = collector.ToElements();
                        var byCat = new Dictionary<string, int>();
                        foreach (var el in all)
                        {
                            var cat = el.Category?.Name ?? "NoCat";
                            byCat[cat] = byCat.TryGetValue(cat, out var c) ? c + 1 : 1;
                        }
                        var topCats = byCat.OrderByDescending(kv => kv.Value).Take(10).ToList();
                        var s = $"Active view: {name} ({vt}) Scale 1:{scale} Detail:{detail} Id:{view.Id}\n";
                        s += $"Total visible instances: {all.Count}\nTop categories:\n" + string.Join("\n", topCats.Select(kv => $"  {kv.Key}: {kv.Value}"));
                        var sampleIds = all.Take(limit).Select(e => e.Id.ToString()).ToArray();
                        s += $"\nSample Ids (first {Math.Min(limit, all.Count)}): {string.Join(", ", sampleIds)}";
                        if (all.Count > limit) s += $" ... and {all.Count - limit} more.";
                        cache.Set(ck, s);
                        return s;
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateGetVisibleElementsInView(RevitBridge bridge, QueryCache cache)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_visible_elements_in_view",
                    Description = "List visible elements in active view, optionally filtered by category. Returns counts and sample IDs.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            category = new { type = "string", description = "Category name like 'Walls', 'Doors', 'OST_Walls'. If omitted, all." },
                            limit = new { type = "integer", description = "Max to return, default 100" }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                string? cat = null;
                int limit = 100;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                    if (d.RootElement.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number) limit = l.GetInt32();
                }
                catch { }
                limit = Math.Clamp(limit, 1, 500);
                var catCapture = cat;
                var result = await bridge.RunAsync(uiApp =>
                {
                    var uidoc = uiApp.ActiveUIDocument;
                    if (uidoc == null) return "Please open a model first.";
                    var doc = uidoc.Document;
                    var view = doc.ActiveView;
                    if (view == null) return "No active view.";
                    try
                    {
                        FilteredElementCollector col = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
                        if (!string.IsNullOrWhiteSpace(catCapture))
                        {
                            var bic = ParseCategory(catCapture);
                            if (bic == null) return $"Unknown category '{catCapture}'. Try Walls, Doors, Windows, Floors, etc.";
                            col = col.OfCategory(bic.Value);
                        }
                        var ids = col.ToElementIds();
                        var s = $"Visible in '{view.Name}': {ids.Count} instance(s)";
                        if (!string.IsNullOrWhiteSpace(catCapture)) s += $" (category {catCapture})";
                        var sample = ids.Take(limit).Select(id => id.ToString()).ToArray();
                        s += $"\nSample: {string.Join(", ", sample)}";
                        if (ids.Count > limit) s += $" ... and {ids.Count - limit} more.";
                        return s;
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateFindElementsByFilter(RevitBridge bridge, QueryCache cache)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "find_elements_by_filter",
                    Description = "Find elements by category and optional parameter filter. The workhorse for 'find all walls on Level 2' etc.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            category = new { type = "string", description = "Required category, e.g. Walls, Doors, OST_Walls" },
                            paramName = new { type = "string", description = "Parameter name to filter, e.g. Level, Comments, Mark" },
                            op = new { type = "string", description = "Operator: =, !=, contains, empty, notempty. Default =" , @enum = new[] { "=", "!=", "contains", "empty", "notempty" } },
                            value = new { type = "string", description = "Value to compare against" },
                            limit = new { type = "integer", description = "Max to return, default 100" },
                            offset = new { type = "integer", description = "Offset for pagination, default 0" }
                        },
                        required = new[] { "category" }
                    }
                }
            },
            Handler = async json =>
            {
                string category = "";
                string? paramName = null, op = "=", value = null;
                int limit = 100, offset = 0;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    category = d.RootElement.GetProperty("category").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("paramName", out var pn) && pn.ValueKind == JsonValueKind.String) paramName = pn.GetString();
                    if (d.RootElement.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String) op = o.GetString();
                    if (d.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String) value = v.GetString();
                    if (d.RootElement.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number) limit = l.GetInt32();
                    if (d.RootElement.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.Number) offset = off.GetInt32();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                limit = Math.Clamp(limit, 1, 500);
                offset = Math.Max(0, offset);
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var bic = ParseCategory(category);
                    if (bic == null) return $"Unknown category '{category}'.";
                    try
                    {
                        var col = new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType();
                        var elements = col.ToElements();
                        if (!string.IsNullOrWhiteSpace(paramName))
                        {
                            op ??= "=";
                            var filtered = new List<Element>();
                            foreach (var el in elements)
                            {
                                var p = el.LookupParameter(paramName!);
                                if (op == "empty") { if (p == null || string.IsNullOrWhiteSpace(p.AsString()) ) filtered.Add(el); continue; }
                                if (op == "notempty") { if (p != null && !string.IsNullOrWhiteSpace(p.AsString())) filtered.Add(el); continue; }
                                if (p == null) continue;
                                string? pv = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString() ?? p.AsString();
                                pv ??= "";
                                bool match = op switch
                                {
                                    "=" => pv.Equals(value ?? "", StringComparison.OrdinalIgnoreCase),
                                    "!=" => !pv.Equals(value ?? "", StringComparison.OrdinalIgnoreCase),
                                    "contains" => pv.IndexOf(value ?? "", StringComparison.OrdinalIgnoreCase) >= 0,
                                    _ => false
                                };
                                if (match) filtered.Add(el);
                            }
                            elements = filtered;
                        }
                        int total = elements.Count;
                        var page = elements.Skip(offset).Take(limit).ToList();
                        var s = $"Found {total} {category} instance(s)";
                        if (!string.IsNullOrWhiteSpace(paramName)) s += $" where {paramName} {op} '{value}'";
                        s += $" (showing {page.Count} from offset {offset}):\n";
                        s += string.Join("\n", page.Select(e => $"{e.Id} \"{e.Name}\" cat:{e.Category?.Name}"));
                        if (total > offset + page.Count) s += $"\n... {total - offset - page.Count} more. Use offset {offset + limit}.";
                        return s;
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateGetElementDetails(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_element_details",
                    Description = "Get detailed info for one element by ID: name, category, type, level, location, parameters (first 30).",
                    Parameters = new
                    {
                        type = "object",
                        properties = new { elementId = new { type = "string", description = "ElementId as integer string" } },
                        required = new[] { "elementId" }
                    }
                }
            },
            Handler = async json =>
            {
                string eidStr = "";
                try { using var d = JsonDocument.Parse(json); eidStr = d.RootElement.GetProperty("elementId").GetString() ?? ""; } catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        var eid = ParseEid(eidStr);
                        var el = doc.GetElement(eid);
                        if (el == null) return $"Element {eidStr} not found.";
                        var cat = el.Category?.Name ?? "NoCat";
                        var type = doc.GetElement(el.GetTypeId()) as ElementType;
                        var level = doc.GetElement(el.LevelId) as Level;
                        var loc = el.Location is LocationPoint lp ? $"Point {lp.Point}" : el.Location is LocationCurve lc ? $"Curve {lc.Curve}" : "No location";
                        var s = $"Id:{el.Id} Name:\"{el.Name}\" Cat:{cat} Type:{type?.Name ?? el.GetType().Name} Level:{level?.Name ?? el.LevelId.ToString()} Loc:{loc}\nParameters (up to 30):\n";
                        int n = 0;
                        foreach (Parameter p in el.Parameters)
                        {
                            if (n++ >= 30) break;
                            string vs = p.StorageType switch { StorageType.String => p.AsString() ?? "", StorageType.Integer => p.AsInteger().ToString(), StorageType.Double => p.AsDouble().ToString("F3"), StorageType.ElementId => p.AsElementId().ToString(), _ => "?" };
                            s += $"  {p.Definition.Name} = {vs} (RO:{p.IsReadOnly} ST:{p.StorageType})\n";
                        }
                        if (el.Parameters.Size > 30) s += $"... and {el.Parameters.Size - 30} more.";
                        return s;
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateGetWarnings(RevitBridge bridge, QueryCache cache)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_warnings",
                    Description = "Get document warnings (count + first N messages).",
                    Parameters = new { type = "object", properties = new { limit = new { type = "integer", description = "Max warnings, default 20" } }, required = Array.Empty<string>() }
                }
            },
            Handler = async json =>
            {
                int limit = 20;
                try { using var d = JsonDocument.Parse(json); if (d.RootElement.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number) limit = l.GetInt32(); } catch { }
                limit = Math.Clamp(limit, 1, 100);
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var ck = cache.BuildKey("get_warnings", doc.PathName, limit.ToString());
                    if (cache.TryGet<string>(ck, out var cached)) return cached;
                    try
                    {
                        var warnings = doc.GetWarnings();
                        var s = $"Warnings: {warnings.Count}\n";
                        int i = 0;
                        foreach (var w in warnings.Take(limit))
                        {
                            s += $"{++i}. {w.GetDescriptionText()} (Ids: {string.Join(", ", w.GetFailingElements().Take(3).Select(id => id.ToString()))})\n";
                        }
                        if (warnings.Count > limit) s += $"... and {warnings.Count - limit} more.";
                        if (warnings.Count == 0) s = "No warnings.";
                        cache.Set(ck, s);
                        return s;
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateGetLevelsAndGrids(RevitBridge bridge, QueryCache cache)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_levels_and_grids",
                    Description = "List levels and grids: name, elevation (feet), Id.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var ck = cache.BuildKey("get_levels_and_grids", doc.PathName);
                    if (cache.TryGet<string>(ck, out var cached)) return cached;
                    try
                    {
                        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                        var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
                        var s = $"Levels ({levels.Count}):\n" + string.Join("\n", levels.Select(l => $"  {l.Id} \"{l.Name}\" elev {l.Elevation:F2} ft"));
                        s += $"\nGrids ({grids.Count}):\n" + string.Join("\n", grids.Select(g => $"  {g.Id} \"{g.Name}\""));
                        cache.Set(ck, s);
                        return s;
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateGetQuantities(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_quantities",
                    Description = "Get length/area/volume for given elementIds or current selection.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" }, description = "ElementIds. If omitted, uses current selection" }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                List<string>? eids = null;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.TryGetProperty("elementIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        eids = arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                }
                catch { }
                var capture = eids;
                var result = await bridge.RunAsync(uiApp =>
                {
                    var uidoc = uiApp.ActiveUIDocument;
                    if (uidoc == null) return "Please open a model first.";
                    var doc = uidoc.Document;
                    ICollection<ElementId> ids;
                    if (capture != null && capture.Count > 0) ids = capture.Select(ParseEid).ToList();
                    else ids = uidoc.Selection.GetElementIds();
                    if (ids.Count == 0) return "Please select elements first or provide elementIds.";
                    var s = $"Quantities for {ids.Count} element(s):\n";
                    foreach (var eid in ids.Take(50))
                    {
                        var el = doc.GetElement(eid);
                        if (el == null) { s += $"{eid}: not found\n"; continue; }
                        var len = el.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble();
                        var area = el.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble();
                        var vol = el.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.AsDouble();
                        s += $"{eid} {el.Category?.Name} \"{el.Name}\":";
                        if (len.HasValue) s += $" L={len.Value:F2}ft";
                        if (area.HasValue) s += $" A={area.Value:F2}sqft";
                        if (vol.HasValue) s += $" V={vol.Value:F2}cuft";
                        // bbox
                        var bb = el.get_BoundingBox(doc.ActiveView);
                        if (bb != null) s += $" BB=({bb.Min.X:F1},{bb.Min.Y:F1},{bb.Min.Z:F1})-({bb.Max.X:F1},{bb.Max.Y:F1},{bb.Max.Z:F1})";
                        s += "\n";
                    }
                    if (ids.Count > 50) s += $"... and {ids.Count - 50} more.";
                    return s;
                });
                return result;
            }
        };
    }

    // ---------- L2 view tools ----------

    private static ToolEntry CreateIsolateInView(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "isolate_in_view",
                    Description = "Temporarily isolate elements or a whole category in the active view. Use for 'isolate the walls'. Requires confirm if many.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" }, description = "ElementIds to isolate. If omitted and category given, isolates category." },
                            category = new { type = "string", description = "Category to isolate, e.g. Walls. Alternative to elementIds." },
                            isolate = new { type = "boolean", description = "True to isolate, false to undo isolate. Default true." }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                List<string>? eids = null;
                string? cat = null;
                bool isolate = true;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.TryGetProperty("elementIds", out var arr) && arr.ValueKind == JsonValueKind.Array) eids = arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                    if (d.RootElement.TryGetProperty("isolate", out var iso) && iso.ValueKind is JsonValueKind.True or JsonValueKind.False) isolate = iso.GetBoolean();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return "Provide elementIds or category.";
                var result = await bridge.RunAsync(uiApp =>
                {
                    var uidoc = uiApp.ActiveUIDocument;
                    if (uidoc == null) return "Please open a model first.";
                    var doc = uidoc.Document;
                    var view = doc.ActiveView;
                    if (view == null) return "No active view.";
                    if (view.IsTemplate) return "Cannot isolate in a view template.";
                    try
                    {
                        // Confirm
                        int count = eids?.Count ?? 0;
                        string preview = !string.IsNullOrWhiteSpace(cat) ? $"Category: {cat}\nView: {view.Name}\nIsolate: {isolate}" : $"Ids: {string.Join(", ", eids!.Take(5))}{(eids.Count > 5 ? "..." : "")}\nView: {view.Name}";
                        // Skip confirm for L2? But we use L2 tier => bridge will confirm via HostPage's audit? Actually we need explicit dialog here for view ops as well.
                        // Use same Confirm helper
                        if (count > 10 || !string.IsNullOrWhiteSpace(cat))
                        {
                            var td = new TaskDialog("Revit AI POC") { MainInstruction = isolate ? $"Isolate {(cat ?? count + " elements")} in '{view.Name}'?" : $"Reset isolate in '{view.Name}'?", MainContent = preview, CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
                            if (td.Show() != TaskDialogResult.Yes) return "Cancelled by user.";
                        }
                        // All temporary view modifications require a Transaction in Revit 2027
                        using (var tx = new Transaction(doc, isolate ? $"Revit AI POC: isolate {cat ?? eids!.Count.ToString()} in {view.Name}" : $"Revit AI POC: reset isolate in {view.Name}"))
                        {
                            tx.Start();
                            try
                            {
                                if (!view.IsTemporaryHideIsolateActive() || isolate)
                                {
                                    if (!string.IsNullOrWhiteSpace(cat) && (eids == null || eids.Count == 0))
                                    {
                                        var bic = ParseCategory(cat);
                                        if (bic == null) { tx.RollBack(); return $"Unknown category '{cat}'."; }
                                        var catObj = doc.Settings.Categories.get_Item(bic.Value);
                                        if (catObj == null) { tx.RollBack(); return $"Category '{cat}' not found."; }
                                        view.IsolateCategoryTemporary(catObj.Id);
                                    }
                                    else
                                    {
                                        var ids = eids!.Select(ParseEid).ToList();
                                        view.IsolateElementsTemporary(ids);
                                    }
                                }
                                if (!isolate)
                                {
                                    view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                                }
                                tx.Commit();
                            }
                            catch
                            {
                                try { tx.RollBack(); } catch { }
                                throw;
                            }
                        }
                        return isolate ? $"Isolated {(cat ?? eids!.Count + " elements")} in view '{view.Name}' (temporary). Use reset_temporary_view to restore." : $"Reset isolate in '{view.Name}'.";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateHideInView(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "hide_in_view",
                    Description = "Temporarily hide elements or a category in the active view.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" }, description = "Ids to hide" },
                            category = new { type = "string", description = "Category to hide" }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                List<string>? eids = null;
                string? cat = null;
                try { using var d = JsonDocument.Parse(json); if (d.RootElement.TryGetProperty("elementIds", out var a) && a.ValueKind == JsonValueKind.Array) eids = a.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList(); if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString(); } catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return "Provide elementIds or category.";
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var view = doc.ActiveView;
                    if (view == null) return "No active view.";
                    try
                    {
                        using var tx = new Transaction(doc, $"Revit AI POC: hide {cat ?? eids!.Count.ToString()} in {view.Name}");
                        tx.Start();
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(cat) && (eids == null || eids.Count == 0))
                            {
                                var bic = ParseCategory(cat);
                                if (bic == null) { tx.RollBack(); return $"Unknown category '{cat}'."; }
                                var ids = new FilteredElementCollector(doc, view.Id).OfCategory(bic.Value).WhereElementIsNotElementType().ToElementIds();
                                if (ids.Count == 0) { tx.RollBack(); return $"No visible {cat} in view."; }
                                view.HideElementsTemporary(ids);
                                tx.Commit();
                                return $"Hid {ids.Count} {cat} in '{view.Name}' (temporary).";
                            }
                            else
                            {
                                var ids = eids!.Select(ParseEid).ToList();
                                view.HideElementsTemporary(ids);
                                tx.Commit();
                                return $"Hid {ids.Count} element(s) in '{view.Name}' (temporary).";
                            }
                        }
                        catch
                        {
                            try { tx.RollBack(); } catch { }
                            throw;
                        }
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateResetTemporaryView(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "reset_temporary_view",
                    Description = "Reset (disable) temporary hide/isolate in the active view.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var view = doc.ActiveView;
                    if (view == null) return "No active view.";
                    try
                    {
                        if (view.IsTemporaryHideIsolateActive())
                        {
                            using var tx = new Transaction(doc, $"Revit AI POC: reset view {view.Name}");
                            tx.Start();
                            try
                            {
                                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                                tx.Commit();
                            }
                            catch
                            {
                                try { tx.RollBack(); } catch { }
                                throw;
                            }
                            return $"Reset temporary view in '{view.Name}'.";
                        }
                        return $"No temporary hide/isolate active in '{view.Name}'.";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateOverrideColorInView(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "override_color_in_view",
                    Description = "Set temporary color override for elements or a whole category in the active view. Use e.g. 'color those doors red'.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" }, description = "Ids to color" },
                            category = new { type = "string", description = "Category to color. Alternative to elementIds." },
                            color = new { type = "string", description = "Color name (red, blue, green, yellow, orange) or hex #RRGGBB" },
                            reset = new { type = "boolean", description = "True to reset override. Default false." }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                List<string>? eids = null;
                string? cat = null, color = "red";
                bool reset = false;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.TryGetProperty("elementIds", out var a) && a.ValueKind == JsonValueKind.Array) eids = a.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                    if (d.RootElement.TryGetProperty("color", out var col) && col.ValueKind == JsonValueKind.String) color = col.GetString();
                    if (d.RootElement.TryGetProperty("reset", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False) reset = r.GetBoolean();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return "Provide elementIds or category.";
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var view = doc.ActiveView;
                    if (view == null) return "No active view.";
                    if (view.IsTemplate) return "Cannot override in view template.";
                    try
                    {
                        Color clr = new Color(255, 0, 0);
                        if (!reset && !string.IsNullOrWhiteSpace(color))
                        {
                            clr = ParseColor(color!);
                        }
                        var ogs = new OverrideGraphicSettings();
                        if (reset) { /* empty resets */ }
                        else
                        {
                            ogs.SetProjectionLineColor(clr);
                            ogs.SetSurfaceForegroundPatternColor(clr);
                            ogs.SetCutForegroundPatternColor(clr);
                        }

                        using var tx = new Transaction(doc, reset ? $"Revit AI POC: reset color in {view.Name}" : $"Revit AI POC: color {color} in {view.Name}");
                        tx.Start();
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(cat) && (eids == null || eids.Count == 0))
                            {
                                var bic = ParseCategory(cat);
                                if (bic == null) { tx.RollBack(); return $"Unknown category '{cat}'."; }
                                var catObj = doc.Settings.Categories.get_Item(bic.Value);
                                if (catObj == null) { tx.RollBack(); return $"Category '{cat}' not found."; }
                                view.SetCategoryOverrides(catObj.Id, reset ? new OverrideGraphicSettings() : ogs);
                                tx.Commit();
                                return reset ? $"Reset color for {cat} in '{view.Name}'." : $"Set {color} override for {cat} in '{view.Name}'.";
                            }
                            else
                            {
                                var ids = eids!.Select(ParseEid).ToList();
                                foreach (var id in ids) view.SetElementOverrides(id, reset ? new OverrideGraphicSettings() : ogs);
                                tx.Commit();
                                return reset ? $"Reset color for {ids.Count} element(s) in '{view.Name}'." : $"Set {color} override for {ids.Count} element(s) in '{view.Name}'.";
                            }
                        }
                        catch
                        {
                            try { tx.RollBack(); } catch { }
                            throw;
                        }
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static Color ParseColor(string s)
    {
        s = s.Trim().ToLowerInvariant();
        return s switch
        {
            "red" => new Color(255, 0, 0),
            "green" => new Color(0, 200, 0),
            "blue" => new Color(0, 0, 255),
            "yellow" => new Color(255, 255, 0),
            "orange" => new Color(255, 165, 0),
            "purple" => new Color(128, 0, 128),
            _ when s.StartsWith("#") && s.Length == 7 && int.TryParse(s[1..3], System.Globalization.NumberStyles.HexNumber, null, out var r) && int.TryParse(s[3..5], System.Globalization.NumberStyles.HexNumber, null, out var g) && int.TryParse(s[5..7], System.Globalization.NumberStyles.HexNumber, null, out var b) => new Color((byte)r, (byte)g, (byte)b),
            _ => new Color(255, 0, 0)
        };
    }

    // ---------- param ----------

    private static ToolEntry CreateSetParameterById(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
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
                try { using var d = JsonDocument.Parse(json); eidStr = d.RootElement.GetProperty("elementId").GetString() ?? ""; paramName = d.RootElement.GetProperty("paramName").GetString() ?? ""; value = d.RootElement.GetProperty("value").GetString() ?? ""; } catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var pre = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return (err: "Please open a model first.", elName: "");
                    try
                    {
                        var el = doc.GetElement(ParseEid(eidStr));
                        if (el == null) return (err: $"Element {eidStr} not found.", elName: "");
                        return (err: "", elName: $"{el.Category?.Name} \"{el.Name}\"");
                    }
                    catch (Exception ex) { return (err: ex.Message, elName: ""); }
                });
                if (!string.IsNullOrEmpty(pre.err)) return pre.err;
                bool confirmed = await bridge.RunAsync(uiApp =>
                {
                    var td = new TaskDialog("Revit AI POC") { MainInstruction = $"Set {paramName} on element {eidStr}?", MainContent = $"Element: {pre.elName}\n{paramName} = \"{value}\"", CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
                    return td.Show() == TaskDialogResult.Yes;
                });
                if (!confirmed) return "Cancelled by user.";
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        var el = doc.GetElement(ParseEid(eidStr));
                        if (el == null) return $"Element {eidStr} not found.";
                        using var tx = new Transaction(doc, $"Revit AI POC: set {paramName} on {eidStr}");
                        tx.Start();
                        try
                        {
                            if (!TrySetParameter(el, paramName, value, out var err)) { tx.RollBack(); return err; }
                            tx.Commit();
                        }
                        catch { try { tx.RollBack(); } catch { } throw; }
                        return $"Set {paramName}=\"{value}\" on {eidStr} ({pre.elName}).";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateSetCommentsOnSelection(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
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
                try { using var d = JsonDocument.Parse(json); if (!d.RootElement.TryGetProperty("text", out var p)) return "Missing required parameter 'text'."; text = p.GetString() ?? ""; } catch (Exception ex) { return $"Invalid arguments: {ex.Message}"; }
                var pre = await bridge.RunAsync(uiApp =>
                {
                    var uidoc = uiApp.ActiveUIDocument;
                    if (uidoc == null) return (error: "Please open a model first.", count: 0);
                    var ids = uidoc.Selection.GetElementIds();
                    if (ids.Count == 0) return (error: "Please select elements first.", count: 0);
                    return (error: "", count: ids.Count);
                });
                if (!string.IsNullOrEmpty(pre.error)) return pre.error;
                int count = pre.count;
                bool confirmed = await bridge.RunAsync(uiApp =>
                {
                    var td = new TaskDialog("Revit AI POC") { MainInstruction = $"Set Comments on {count} selected element(s)?", MainContent = $"New Comments value:\n\"{text}\"\n\nThis will create an undoable transaction.", CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
                    return td.Show() == TaskDialogResult.Yes;
                });
                if (!confirmed) return "Cancelled by user.";
                var writeResult = await bridge.RunAsync(uiApp =>
                {
                    var uidoc = uiApp.ActiveUIDocument;
                    var doc = uidoc?.Document;
                    if (doc == null) return "Please open a model first.";
                    var selIds = uidoc!.Selection.GetElementIds();
                    if (selIds.Count == 0) return "Please select elements first.";
                    int updated = 0, skipped = 0; var skippedIds = new List<string>();
                    using (var tx = new Transaction(doc, "Revit AI POC: set comments"))
                    {
                        tx.Start();
                        try
                        {
                            foreach (var eid in selIds)
                            {
                                var el = doc.GetElement(eid);
                                if (el == null) { skipped++; continue; }
                                if (!TrySetParameter(el, "Comments", text, out _))
                                {
                                    skipped++; skippedIds.Add(eid.ToString()); continue;
                                }
                                updated++;
                            }
                            tx.Commit();
                        }
                        catch { try { tx.RollBack(); } catch { } throw; }
                    }
                    var msg = $"Updated {updated} element(s).";
                    if (skipped > 0) msg += $" Skipped {skipped} (no Comments): {string.Join(", ", skippedIds.Take(10))}" + (skipped > 10 ? "..." : "");
                    return msg;
                });
                return writeResult;
            }
        };
    }

    private static ToolEntry CreateBatchSetParameter(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
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
                    using var d = JsonDocument.Parse(json);
                    category = d.RootElement.GetProperty("category").GetString() ?? "";
                    targetParam = d.RootElement.GetProperty("targetParam").GetString() ?? "";
                    targetValue = d.RootElement.GetProperty("targetValue").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("filterParam", out var fp) && fp.ValueKind == JsonValueKind.String) filterParam = fp.GetString();
                    if (d.RootElement.TryGetProperty("filterOp", out var fo) && fo.ValueKind == JsonValueKind.String) filterOp = fo.GetString();
                    if (d.RootElement.TryGetProperty("filterValue", out var fv) && fv.ValueKind == JsonValueKind.String) filterValue = fv.GetString();
                    if (d.RootElement.TryGetProperty("limit", out var lim) && lim.ValueKind == JsonValueKind.Number) limit = lim.GetInt32();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                limit = Math.Clamp(limit, 1, 1000);
                var preview = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return (err: "Please open a model first.", count: 0, sample: Array.Empty<string>());
                    var bic = ParseCategory(category);
                    if (bic == null) return (err: $"Unknown category '{category}'.", count: 0, sample: Array.Empty<string>());
                    try
                    {
                        var col = new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType();
                        var els = col.ToElements();
                        if (!string.IsNullOrWhiteSpace(filterParam))
                        {
                            var filtered = new List<Element>();
                            foreach (var el in els)
                            {
                                var p = el.LookupParameter(filterParam!);
                                string? pv = p?.AsString() ?? p?.AsValueString();
                                pv ??= "";
                                bool match = filterOp switch
                                {
                                    "empty" => string.IsNullOrWhiteSpace(pv),
                                    "notempty" => !string.IsNullOrWhiteSpace(pv),
                                    "=" => pv.Equals(filterValue ?? "", StringComparison.OrdinalIgnoreCase),
                                    "!=" => !pv.Equals(filterValue ?? "", StringComparison.OrdinalIgnoreCase),
                                    "contains" => pv.IndexOf(filterValue ?? "", StringComparison.OrdinalIgnoreCase) >= 0,
                                    _ => false
                                };
                                if (match) filtered.Add(el);
                            }
                            els = filtered;
                        }
                        var ids = els.Take(limit).Select(e => e.Id.ToString()).ToArray();
                        return (err: "", count: els.Count, sample: ids);
                    }
                    catch (Exception ex) { return (err: ex.Message, count: 0, sample: Array.Empty<string>()); }
                });
                if (!string.IsNullOrEmpty(preview.err)) return preview.err;
                int total = preview.count;
                if (total == 0) return $"No elements matched filter for {category}.";
                // confirm
                int toUpdate = Math.Min(total, limit);
                bool confirmed = await bridge.RunAsync(uiApp =>
                {
                    var td = new TaskDialog("Revit AI POC")
                    {
                        MainInstruction = $"Batch set {targetParam} on {toUpdate} {category} element(s)?",
                        MainContent = $"Filter: {filterParam} {filterOp} '{filterValue}' matched {total}.\nSet {targetParam} = \"{targetValue}\" on first {toUpdate}.\nSample Ids: {string.Join(", ", preview.sample.Take(5))}",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.No
                    };
                    return td.Show() == TaskDialogResult.Yes;
                });
                if (!confirmed) return "Cancelled by user.";
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var bic = ParseCategory(category);
                    if (bic == null) return $"Unknown category '{category}'.";
                    try
                    {
                        var col = new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType();
                        var els = col.ToElements();
                        if (!string.IsNullOrWhiteSpace(filterParam))
                        {
                            els = els.Where(el =>
                            {
                                var p = el.LookupParameter(filterParam!);
                                string? pv = p?.AsString() ?? p?.AsValueString() ?? "";
                                return filterOp switch
                                {
                                    "empty" => string.IsNullOrWhiteSpace(pv),
                                    "notempty" => !string.IsNullOrWhiteSpace(pv),
                                    "=" => pv.Equals(filterValue ?? "", StringComparison.OrdinalIgnoreCase),
                                    "!=" => !pv.Equals(filterValue ?? "", StringComparison.OrdinalIgnoreCase),
                                    "contains" => pv.IndexOf(filterValue ?? "", StringComparison.OrdinalIgnoreCase) >= 0,
                                    _ => false
                                };
                            }).ToList();
                        }
                        els = els.Take(limit).ToList();
                        int updated = 0, skipped = 0; var skippedIds = new List<string>();
                        using var tx = new Transaction(doc, $"Revit AI POC: batch {targetParam} on {category}");
                        tx.Start();
                        try
                        {
                            foreach (var el in els)
                            {
                                if (!TrySetParameter(el, targetParam, targetValue, out _)) { skipped++; skippedIds.Add(el.Id.ToString()); continue; }
                                updated++;
                            }
                            tx.Commit();
                        }
                        catch { try { tx.RollBack(); } catch { } throw; }
                        var msg = $"Batch updated {updated} {category} (matched {total}, limited {toUpdate}).";
                        if (skipped > 0) msg += $" Skipped {skipped}: {string.Join(", ", skippedIds.Take(5))}";
                        return msg;
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateRenameElements(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
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
                var preview = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var s = "";
                    for (int i = 0; i < eids.Count; i++)
                    {
                        var el = doc.GetElement(ParseEid(eids[i]));
                        var nn = newNamesCapture != null ? newNamesCapture[i] : patternCapture!.Replace("{i}", i.ToString());
                        s += $"{eids[i]} \"{el?.Name}\" -> \"{nn}\"\n";
                    }
                    return s;
                });
                bool confirmed = await bridge.RunAsync(uiApp =>
                {
                    var td = new TaskDialog("Revit AI POC") { MainInstruction = $"Rename {eids.Count} element(s)?", MainContent = preview, CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
                    return td.Show() == TaskDialogResult.Yes;
                });
                if (!confirmed) return "Cancelled by user.";
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    int updated = 0;
                    using var tx = new Transaction(doc, $"Revit AI POC: rename {eids.Count} elements");
                    tx.Start();
                    try
                    {
                        for (int i = 0; i < eids.Count; i++)
                        {
                            var el = doc.GetElement(ParseEid(eids[i]));
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

    private static ToolEntry CreateMoveElements(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "move_elements",
                    Description = "Move elements by vector (feet).",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" } },
                            dx = new { type = "number", description = "Delta X in feet" },
                            dy = new { type = "number", description = "Delta Y in feet" },
                            dz = new { type = "number", description = "Delta Z in feet" }
                        },
                        required = new[] { "elementIds", "dx", "dy", "dz" }
                    }
                }
            },
            Handler = async json =>
            {
                List<string> eids = new(); double dx = 0, dy = 0, dz = 0;
                try { using var d = JsonDocument.Parse(json); eids = d.RootElement.GetProperty("elementIds").EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList(); dx = d.RootElement.GetProperty("dx").GetDouble(); dy = d.RootElement.GetProperty("dy").GetDouble(); dz = d.RootElement.GetProperty("dz").GetDouble(); } catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                bool confirmed = await bridge.RunAsync(uiApp =>
                {
                    var td = new TaskDialog("Revit AI POC") { MainInstruction = $"Move {eids.Count} element(s) by ({dx},{dy},{dz}) ft?", CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
                    return td.Show() == TaskDialogResult.Yes;
                });
                if (!confirmed) return "Cancelled by user.";
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        using var tx = new Transaction(doc, $"Revit AI POC: move {eids.Count} elements");
                        tx.Start();
                        var vec = new XYZ(dx, dy, dz);
                        int ok = 0;
                        foreach (var eidStr in eids)
                        {
                            var eid = ParseEid(eidStr);
                            try { ElementTransformUtils.MoveElement(doc, eid, vec); ok++; } catch (Exception ex) { /* per element */ }
                        }
                        tx.Commit();
                        return $"Moved {ok}/{eids.Count} element(s) by ({dx},{dy},{dz}) ft.";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateDeleteElements(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "delete_elements",
                    Description = "Delete elements by Id. Destructive — asks for confirmation and shows preview.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new { elementIds = new { type = "array", items = new { type = "string" } } },
                        required = new[] { "elementIds" }
                    }
                }
            },
            Handler = async json =>
            {
                List<string> eids = new();
                try { using var d = JsonDocument.Parse(json); eids = d.RootElement.GetProperty("elementIds").EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList(); } catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if (eids.Count == 0) return "Provide elementIds.";
                var preview = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return (err: "Please open a model first.", s: "");
                    var s = string.Join("\n", eids.Take(5).Select(id => { var el = doc.GetElement(ParseEid(id)); return $"{id} {el?.Category?.Name} \"{el?.Name}\""; }));
                    if (eids.Count > 5) s += $"\n... and {eids.Count - 5} more.";
                    return (err: "", s: s);
                });
                if (!string.IsNullOrEmpty(preview.err)) return preview.err;
                bool confirmed = await bridge.RunAsync(uiApp =>
                {
                    var td = new TaskDialog("Revit AI POC") { MainInstruction = $"Delete {eids.Count} element(s)? This cannot be undone except via Undo.", MainContent = preview.s, CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
                    return td.Show() == TaskDialogResult.Yes;
                });
                if (!confirmed) return "Cancelled by user.";
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        using var tx = new Transaction(doc, $"Revit AI POC: delete {eids.Count} elements");
                        tx.Start();
                        var ids = eids.Select(ParseEid).ToList();
                        doc.Delete(ids);
                        tx.Commit();
                        return $"Deleted {ids.Count} element(s). Undo with Ctrl+Z.";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateCreateWall(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "create_wall",
                    Description = "Create a wall by start/end points (feet) on a level. For testing geometry creation.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            levelName = new { type = "string", description = "Level name, e.g. Level 1" },
                            x1 = new { type = "number" }, y1 = new { type = "number" }, x2 = new { type = "number" }, y2 = new { type = "number" },
                            wallTypeName = new { type = "string", description = "Optional wall type name" }
                        },
                        required = new[] { "levelName", "x1", "y1", "x2", "y2" }
                    }
                }
            },
            Handler = async json =>
            {
                string levelName = ""; double x1 = 0, y1 = 0, x2 = 0, y2 = 0; string? wallTypeName = null;
                try { using var d = JsonDocument.Parse(json); levelName = d.RootElement.GetProperty("levelName").GetString() ?? ""; x1 = d.RootElement.GetProperty("x1").GetDouble(); y1 = d.RootElement.GetProperty("y1").GetDouble(); x2 = d.RootElement.GetProperty("x2").GetDouble(); y2 = d.RootElement.GetProperty("y2").GetDouble(); if (d.RootElement.TryGetProperty("wallTypeName", out var w) && w.ValueKind == JsonValueKind.String) wallTypeName = w.GetString(); } catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                bool confirmed = await bridge.RunAsync(uiApp =>
                {
                    var td = new TaskDialog("Revit AI POC") { MainInstruction = $"Create wall on {levelName} from ({x1},{y1}) to ({x2},{y2}) ft?", CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
                    return td.Show() == TaskDialogResult.Yes;
                });
                if (!confirmed) return "Cancelled by user.";
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
                        if (level == null) return $"Level '{levelName}' not found.";
                        ElementId wallTypeId = ElementId.InvalidElementId;
                        if (!string.IsNullOrWhiteSpace(wallTypeName))
                        {
                            var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(t => t.Name.Equals(wallTypeName, StringComparison.OrdinalIgnoreCase));
                            if (wt == null) return $"Wall type '{wallTypeName}' not found.";
                            wallTypeId = wt.Id;
                        }
                        else
                        {
                            var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault();
                            if (wt == null) return "No wall types found.";
                            wallTypeId = wt.Id;
                        }
                        var p1 = new XYZ(x1, y1, 0); var p2 = new XYZ(x2, y2, 0);
                        var line = Line.CreateBound(p1, p2);
                        using var tx = new Transaction(doc, "Revit AI POC: create wall");
                        tx.Start();
                        var wall = Wall.Create(doc, line, wallTypeId, level.Id, 10, 0, false, false);
                        tx.Commit();
                        return $"Created wall {wall.Id} on {levelName} from ({x1},{y1}) to ({x2},{y2}) type {wallTypeId}.";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateCreateFamilyInstance(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "create_family_instance",
                    Description = "Place a family instance by symbol name at a point (feet) on a level.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            symbolName = new { type = "string", description = "Family symbol name, e.g. 'Single-Flush'" },
                            familyName = new { type = "string", description = "Family name, e.g. 'Door'" },
                            levelName = new { type = "string", description = "Level name" },
                            x = new { type = "number" }, y = new { type = "number" }, z = new { type = "number", description = "Optional Z, default 0" }
                        },
                        required = new[] { "symbolName", "x", "y" }
                    }
                }
            },
            Handler = async json =>
            {
                string symbolName = "", familyName = "", levelName = ""; double x = 0, y = 0, z = 0;
                try { using var d = JsonDocument.Parse(json); symbolName = d.RootElement.GetProperty("symbolName").GetString() ?? ""; if (d.RootElement.TryGetProperty("familyName", out var fn) && fn.ValueKind == JsonValueKind.String) familyName = fn.GetString() ?? ""; if (d.RootElement.TryGetProperty("levelName", out var ln) && ln.ValueKind == JsonValueKind.String) levelName = ln.GetString() ?? ""; x = d.RootElement.GetProperty("x").GetDouble(); y = d.RootElement.GetProperty("y").GetDouble(); if (d.RootElement.TryGetProperty("z", out var zz) && zz.ValueKind == JsonValueKind.Number) z = zz.GetDouble(); } catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                bool confirmed = await bridge.RunAsync(uiApp =>
                {
                    var td = new TaskDialog("Revit AI POC") { MainInstruction = $"Place '{symbolName}' at ({x},{y},{z})?", CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
                    return td.Show() == TaskDialogResult.Yes;
                });
                if (!confirmed) return "Cancelled by user.";
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
                        FamilySymbol? sym = null;
                        if (!string.IsNullOrWhiteSpace(familyName)) sym = symbols.FirstOrDefault(s => s.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase) && s.Name.Equals(symbolName, StringComparison.OrdinalIgnoreCase));
                        sym ??= symbols.FirstOrDefault(s => s.Name.Equals(symbolName, StringComparison.OrdinalIgnoreCase));
                        if (sym == null) return $"Symbol '{symbolName}' not found. Available: {string.Join(", ", symbols.Take(10).Select(s => s.Name))}";
                        Level? lvl = null;
                        if (!string.IsNullOrWhiteSpace(levelName)) lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
                        if (lvl == null) lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
                        if (lvl == null) return "No levels found.";
                        if (!sym.IsActive) sym.Activate();
                        var pt = new XYZ(x, y, z);
                        using var tx = new Transaction(doc, "Revit AI POC: create instance");
                        tx.Start();
                        var inst = doc.Create.NewFamilyInstance(pt, sym, lvl, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        tx.Commit();
                        return $"Created instance {inst.Id} '{symbolName}' at ({x},{y},{z}) on {lvl.Name}.";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateDuplicateElement(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "duplicate_element",
                    Description = "Duplicate a view/sheet/element by Id.",
                    Parameters = new { type = "object", properties = new { elementId = new { type = "string" } }, required = new[] { "elementId" } }
                }
            },
            Handler = async json =>
            {
                string eidStr = "";
                try { using var d = JsonDocument.Parse(json); eidStr = d.RootElement.GetProperty("elementId").GetString() ?? ""; } catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                bool confirmed = await bridge.RunAsync(uiApp =>
                {
                    var td = new TaskDialog("Revit AI POC") { MainInstruction = $"Duplicate element {eidStr}?", CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
                    return td.Show() == TaskDialogResult.Yes;
                });
                if (!confirmed) return "Cancelled by user.";
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        var eid = ParseEid(eidStr);
                        var el = doc.GetElement(eid);
                        if (el == null) return $"Element {eidStr} not found.";
                        using var tx = new Transaction(doc, "Revit AI POC: duplicate");
                        tx.Start();
                        ICollection<ElementId> newIds;
                        if (el is View v) { var nid = v.Duplicate(ViewDuplicateOption.Duplicate); newIds = new[] { nid }; }
                        else { newIds = ElementTransformUtils.CopyElement(doc, eid, XYZ.Zero); }
                        tx.Commit();
                        return $"Duplicated {eidStr} -> {string.Join(", ", newIds.Select(id => id.ToString()))}";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateSaveAndSyncInfo(RevitBridge bridge, QueryCache cache)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_document_info",
                    Description = "Get document info: title, path, worksharing, central, and optionally save.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            save = new { type = "boolean", description = "True to save the document. Default false." }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                bool save = false;
                try { using var d = JsonDocument.Parse(json); if (d.RootElement.TryGetProperty("save", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False) save = s.GetBoolean(); } catch { }
                if (save)
                {
                    bool confirmed = await bridge.RunAsync(uiApp =>
                    {
                        var td = new TaskDialog("Revit AI POC") { MainInstruction = "Save the document?", CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
                        return td.Show() == TaskDialogResult.Yes;
                    });
                    if (!confirmed) return "Cancelled by user.";
                }
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        var title = doc.Title;
                        var path = string.IsNullOrEmpty(doc.PathName) ? "(unsaved)" : doc.PathName;
                        var ws = doc.IsWorkshared ? "Workshared" : "Not workshared";
                        var central = "";
                        try { central = doc.IsWorkshared ? doc.GetWorksharingCentralModelPath()?.ToString() ?? "" : ""; } catch { }
                        if (save)
                        {
                            if (string.IsNullOrEmpty(doc.PathName)) return "Cannot save: document has no path (unsaved).";
                            using var tx = new Transaction(doc, "Revit AI POC: save");
                            // Save is not a transaction; just call doc.Save
                            doc.Save();
                            cache.Invalidate();
                            return $"Saved {title} to {path} ({ws}) central: {central}";
                        }
                        return $"Model: {title}\nPath: {path}\n{ws}\nCentral: {central}";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static ToolEntry CreateApplyConstructionDates(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "apply_construction_dates",
                    Description = "Bulk apply Construction Date (or any text param) bottom-up by level elevation in ONE transaction. Provide startDate (YYYY-MM-DD or YYYYMMDD) and intervalDays. Colors? No — this only sets the parameter value. For coloring use color_by_construction_schedule. Handles all levels sorted low→high and all categories at once to avoid max rounds.",
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
                interval = Math.Clamp(interval, 1, 365);

                // Preview: get levels
                var preview = await bridge.RunAsync(uiApp =>
                {
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
                bool confirmed = await bridge.RunAsync(uiApp =>
                {
                    var td = new TaskDialog("Revit AI POC")
                    {
                        MainInstruction = $"Apply {targetParam} bottom-up from {startDate:yyyy-MM-dd} (+{interval}d) to {levelNames.Length} levels?",
                        MainContent = previewText + $"\n\nCategories: {(cats == null ? "all common" : string.Join(", ", cats))}\nOne transaction, undoable.",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.No
                    };
                    return td.Show() == TaskDialogResult.Yes;
                });
                if (!confirmed) return "Cancelled by user.";

                var result = await bridge.RunAsync(uiApp =>
                {
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
                            foreach (var c in cats) { var bic = ParseCategory(c); if (bic != null) bics.Add(bic.Value); }
                        }
                        else
                        {
                            string[] defaults = { "Walls", "Doors", "Windows", "Floors", "Ceilings", "Roofs", "Structural Framing", "Structural Columns", "Columns", "Generic Models", "Furniture", "Rooms" };
                            foreach (var c in defaults) { var bic = ParseCategory(c); if (bic != null) bics.Add(bic.Value); }
                        }

                        int updated = 0, skipped = 0, noParam = 0;
                        using var tx = new Transaction(doc, $"Revit AI POC: apply {targetParam} bottom-up");
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
                                    if (!TrySetParameter(el, targetParam, dateVal, out var err))
                                    {
                                        // fallback to Comments if Construction Date missing
                                        if (targetParam.Equals("Construction Date", StringComparison.OrdinalIgnoreCase) && err.Contains("not found"))
                                        {
                                            if (!TrySetParameter(el, "Comments", dateVal, out _)) noParam++;
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

    private static ToolEntry CreateColorByConstructionSchedule(RevitBridge bridge)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "color_by_construction_schedule",
                    Description = "Bulk color elements in active view by construction schedule bottom-up (earliest blue → latest red) in ONE transaction. Avoids per-level find+override loops. Provide startDate and intervalDays, optional categories and palette.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            startDate = new { type = "string", description = "Start date for lowest level, e.g. 2024-01-08" },
                            intervalDays = new { type = "integer", description = "Days between levels, default 14" },
                            categories = new { type = "array", items = new { type = "string" }, description = "Categories to color, default [\"Walls\"] or all if you say all" },
                            palette = new { type = "string", description = "Palette: rainbow, blue_red, heat. Default rainbow." }
                        },
                        required = new[] { "startDate" }
                    }
                }
            },
            Handler = async json =>
            {
                string startDateStr = ""; int interval = 14; List<string>? cats = null; string palette = "rainbow";
                try
                {
                    using var d = JsonDocument.Parse(json);
                    startDateStr = d.RootElement.GetProperty("startDate").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("intervalDays", out var iv) && iv.ValueKind == JsonValueKind.Number) interval = iv.GetInt32();
                    if (d.RootElement.TryGetProperty("categories", out var ca) && ca.ValueKind == JsonValueKind.Array) cats = ca.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("palette", out var pal) && pal.ValueKind == JsonValueKind.String) palette = pal.GetString() ?? palette;
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if (!TryParseDate(startDateStr, out var startDate)) return $"Invalid startDate '{startDateStr}'.";
                interval = Math.Clamp(interval, 1, 365);

                var preview = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return (err: "Please open a model first.", n: 0);
                    try
                    {
                        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                        return (err: "", n: levels.Count);
                    }
                    catch (Exception ex) { return (err: ex.Message, n: 0); }
                });
                if (!string.IsNullOrEmpty(preview.err)) return preview.err;

                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var view = doc.ActiveView;
                    if (view == null) return "No active view.";
                    if (view.IsTemplate) return "Cannot color in view template.";
                    try
                    {
                        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                        var levelById = levels.ToDictionary(l => l.Id, l => l.Name);
                        // date map not needed for color, just index
                        var colors = GeneratePalette(levels.Count, palette);
                        var dateByLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < levels.Count; i++) dateByLevel[levels[i].Name] = startDate.AddDays(i * interval).ToString("yyyy-MM-dd");

                        // categories
                        List<BuiltInCategory> bics = new();
                        if (cats != null && cats.Count > 0)
                        {
                            // handle "all" keyword
                            if (cats.Count == 1 && cats[0].Equals("all", StringComparison.OrdinalIgnoreCase))
                            {
                                string[] defaults = { "Walls", "Doors", "Windows", "Floors", "Ceilings", "Roofs", "Structural Framing", "Structural Columns", "Columns", "Generic Models" };
                                foreach (var c in defaults) { var bic = ParseCategory(c); if (bic != null) bics.Add(bic.Value); }
                            }
                            else foreach (var c in cats) { var bic = ParseCategory(c); if (bic != null) bics.Add(bic.Value); }
                        }
                        else
                        {
                            bics.Add(BuiltInCategory.OST_Walls); // default walls if not specified, to keep it light
                        }

                        // Build level -> ids map
                        var levelToIds = new Dictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var lvl in levels) levelToIds[lvl.Name] = new List<ElementId>();

                        foreach (var bic in bics)
                        {
                            var elems = new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType().ToElements();
                            // also include not visible? Use view collector to only color visible elements to avoid off-view clutter
                            // For each element, resolve level name
                            foreach (var el in elems)
                            {
                                string? lvlName = null;
                                if (el.LevelId != ElementId.InvalidElementId && levelById.TryGetValue(el.LevelId, out var ln)) lvlName = ln;
                                else
                                {
                                    var p = el.LookupParameter("Base Constraint") ?? el.LookupParameter("Level") ?? el.LookupParameter("Reference Level") ?? el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ?? el.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                                    if (p != null)
                                    {
                                        if (p.StorageType == StorageType.ElementId) { var id = p.AsElementId(); if (levelById.TryGetValue(id, out var n2)) lvlName = n2; }
                                        else lvlName = p.AsString() ?? p.AsValueString();
                                    }
                                }
                                if (lvlName != null && levelToIds.ContainsKey(lvlName)) levelToIds[lvlName].Add(el.Id);
                            }
                        }

                        // Single transaction for all colors
                        using var tx = new Transaction(doc, $"Revit AI POC: color by schedule {startDate:yyyy-MM-dd}");
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
                            var msg = $"Colored {total} element(s) in '{view.Name}' by construction schedule bottom-up {startDate:yyyy-MM-dd} (+{interval}d) across {levels.Count} levels, {bics.Count} categories. Palette {palette}.";
                            msg += "\nLegend:\n" + string.Join("\n", levels.Select((l, i) => $"{l.Name} ({l.Elevation:F1}ft) = {dateByLevel[l.Name]} -> #{colors[i].Red:X2}{colors[i].Green:X2}{colors[i].Blue:X2}"));
                            msg += "\nTo reset: say 'reset view' (reset_temporary_view).";
                            return msg;
                        }
                        catch
                        {
                            try { tx.RollBack(); } catch { }
                            throw;
                        }
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static List<Color> GeneratePalette(int n, string palette)
    {
        palette = palette?.ToLowerInvariant() ?? "rainbow";
        // Predefined gradient blue->red
        string[] hexRainbow = { "#1565C0", "#1E88E5", "#039BE5", "#00ACC1", "#00897B", "#43A047", "#7CB342", "#FBC02D", "#FFA000", "#FB8C00", "#F4511E", "#E53935", "#D32F2F", "#C2185B", "#7B1FA2", "#512DA8", "#303F9F", "#455A64", "#FF6F00", "#FF4081" };
        var list = new List<Color>();
        for (int i = 0; i < n; i++)
        {
            string hex;
            if (palette == "heat") hex = InterpolateHeat((double)i / Math.Max(1, n - 1));
            else hex = hexRainbow[i % hexRainbow.Length];
            list.Add(HexToColor(hex));
        }
        return list;
    }

    private static string InterpolateHeat(double t)
    {
        // blue -> yellow -> red
        if (t < 0.5) { int r = (int)(255 * t * 2); int g = (int)(255 * t * 2); int b = 255; return $"#{r:X2}{g:X2}{b:X2}"; }
        else { int r = 255; int g = (int)(255 * (1 - t) * 2); int b = (int)(255 * (1 - t) * 2); return $"#{r:X2}{g:X2}{b:X2}"; }
    }

    private static Color HexToColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) && int.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) && int.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) return new Color((byte)r, (byte)g, (byte)b);
        return new Color(255, 0, 0);
    }
}
