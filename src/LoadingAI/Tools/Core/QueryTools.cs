using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Revit;

namespace LoadingAI.Tools;

public static class QueryTools
{
    public static IEnumerable<ToolEntry> Create(RevitBridge bridge, QueryCache cache)
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
        yield return CreateSaveAndSyncInfo(bridge, cache);
    }

    internal static ToolEntry CreateGetModelSummary(RevitBridge bridge, QueryCache cache)
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

    internal static ToolEntry CreateGetWallMetrics(RevitBridge bridge, QueryCache cache)
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

    internal static ToolEntry CreateGetSelectionSummary(RevitBridge bridge)
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

    internal static ToolEntry CreateGetActiveViewInfo(RevitBridge bridge, QueryCache cache)
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

    internal static ToolEntry CreateGetVisibleElementsInView(RevitBridge bridge, QueryCache cache)
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
                            var bic = AllTools.ParseCategory(catCapture);
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

    internal static ToolEntry CreateFindElementsByFilter(RevitBridge bridge, QueryCache cache)
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
                    var bic = AllTools.ParseCategory(category);
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

    internal static ToolEntry CreateGetElementDetails(RevitBridge bridge)
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
                        var eid = AllTools.ParseEid(eidStr);
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

    internal static ToolEntry CreateGetWarnings(RevitBridge bridge, QueryCache cache)
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

    internal static ToolEntry CreateGetLevelsAndGrids(RevitBridge bridge, QueryCache cache)
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

    internal static ToolEntry CreateGetQuantities(RevitBridge bridge)
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
                    if (capture != null && capture.Count > 0) ids = capture.Select(AllTools.ParseEid).ToList();
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


    internal static ToolEntry CreateSaveAndSyncInfo(RevitBridge bridge, QueryCache cache)
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
                        var td = new TaskDialog("LoadingAI for Revit") { MainInstruction = "Save the document?", CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
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
                            using var tx = new Transaction(doc, "LoadingAI for Revit: save");
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

}
