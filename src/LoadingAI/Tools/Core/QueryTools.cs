using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Revit;
using LoadingAI.Plugins;

namespace LoadingAI.Tools;

public static class QueryTools
{
    public static IEnumerable<ToolEntry> Create(IPluginContext context)
    {
        yield return CreateGetModelSummary(context);
        yield return CreateGetWallMetrics(context);
        yield return CreateGetSelectionSummary(context);
        yield return CreateGetActiveViewInfo(context);
        yield return CreateGetVisibleElementsInView(context);
        yield return CreateFindElementsByFilter(context);
        yield return CreateGetElementDetails(context);
        yield return CreateGetWarnings(context);
        yield return CreateGetLevelsAndGrids(context);
        yield return CreateGetQuantities(context);
        yield return CreateSaveAndSyncInfo(context);
        yield return CreateSaveDocument(context);
    }

    internal static ToolEntry CreateGetModelSummary(IPluginContext context)
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
                try
                {
                    var dto = await context.Bridge.GetModelSummaryAsync();
                    if (dto == null) return "Please open a model first.";
                    return $"Model: {dto.Title}\nPath: {dto.Path}\nViews: {dto.ViewCount}\nWalls: {dto.WallCount}\nDoors: {dto.DoorCount}\nWindows: {dto.WindowCount}";
                }
                catch (Exception ex) { return $"Failed to read model: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetWallMetrics(IPluginContext context)
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
                TryRead(json, d =>
                {
                    if (d.TryGetProperty("levelName", out var lv) && lv.ValueKind == JsonValueKind.String) levelName = lv.GetString();
                    if (d.TryGetProperty("limit", out var lim) && lim.ValueKind == JsonValueKind.Number) limit = lim.GetInt32();
                });
                try
                {
                    var dto = await context.Bridge.GetWallMetricsAsync(levelName, limit);
                    if (dto == null) return "Please open a model first.";
                    var s = $"Walls: {dto.Count} (sampled {dto.Sampled})";
                    if (!string.IsNullOrWhiteSpace(dto.LevelName)) s += $" on level '{dto.LevelName}'";
                    s += $"\nTotal length: {dto.TotalLengthFt:F2} ft";
                    s += $"\nTotal area: {dto.TotalAreaSqFt:F2} sq ft";
                    s += $"\nTotal volume: {dto.TotalVolumeCuFt:F2} cu ft";
                    if (dto.Truncated) s += $"\nNote: limited to {limit} walls for performance; totals are from the sample.";
                    return s;
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetSelectionSummary(IPluginContext context)
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
                var dto = await context.Bridge.GetSelectionSummaryAsync();
                if (dto.ElementCount == 0) return "Please select elements first. (0 selected)";
                var s = $"Selected: {dto.ElementCount} element(s)\nBy category:\n";
                s += string.Join("\n", dto.CategoryNames.Select(c => $"  {c}"));
                var sample = dto.ElementIds.Take(10);
                s += "\nSample (first 10):\n" + string.Join("\n", sample);
                if (dto.ElementIds.Count > 10) s += $"\n... and {dto.ElementCount - 10} more.";
                return s;
            }
        };
    }

    internal static ToolEntry CreateGetActiveViewInfo(IPluginContext context)
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
                TryRead(json, d =>
                {
                    if (d.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number) limit = l.GetInt32();
                });
                limit = Polyfills.Clamp(limit, 1, 500);
                try
                {
                    var dto = await context.Bridge.GetActiveViewDetailsAsync(limit);
                    if (dto == null) return "Please open a model first.";
                    var s = $"Active view: {dto.Name} ({dto.ViewType}) Scale 1:{dto.Scale} Detail:{dto.DetailLevel} Id:{dto.ViewId}\n";
                    s += $"Total visible instances: {dto.VisibleElementCount}\nTop categories:\n";
                    s += string.Join("\n", dto.TopCategories.Select(c => $"  {c.Name}: {c.Count}"));
                    s += $"\nSample Ids (first {Math.Min(limit, dto.SampleIds.Count)}): {string.Join(", ", dto.SampleIds)}";
                    if (dto.VisibleElementCount > dto.SampleIds.Count)
                        s += $" ... and {dto.VisibleElementCount - dto.SampleIds.Count} more.";
                    return s;
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetVisibleElementsInView(IPluginContext context)
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
                TryRead(json, d =>
                {
                    if (d.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                    if (d.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number) limit = l.GetInt32();
                });
                limit = Polyfills.Clamp(limit, 1, 500);
                try
                {
                    var dto = await context.Bridge.GetVisibleElementsInViewAsync(cat, limit);
                    if (dto == null) return "Please open a model first.";
                    if (!string.IsNullOrEmpty(dto.Error)) return dto.Error;
                    var s = $"Visible in '{dto.ViewName}': {dto.Count} instance(s)";
                    if (!string.IsNullOrWhiteSpace(dto.Category)) s += $" (category {dto.Category})";
                    s += $"\nSample: {string.Join(", ", dto.SampleIds)}";
                    if (dto.Count > dto.SampleIds.Count) s += $" ... and {dto.Count - dto.SampleIds.Count} more.";
                    return s;
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateFindElementsByFilter(IPluginContext context)
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
                            op = new { type = "string", description = "Operator: =, !=, contains, empty, notempty. Default =", @enum = new[] { "=", "!=", "contains", "empty", "notempty" } },
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
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    category = d.RootElement.GetProperty("category").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("paramName", out var pn) && pn.ValueKind == JsonValueKind.String) paramName = pn.GetString();
                    if (d.RootElement.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String) op = o.GetString();
                    if (d.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String) value = v.GetString();
                    if (d.RootElement.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number) limit = l.GetInt32();
                    if (d.RootElement.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.Number) offset = off.GetInt32();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                limit = Polyfills.Clamp(limit, 1, 500);
                offset = Math.Max(0, offset);
                try
                {
                    var dto = await context.Bridge.FindElementsFilteredAsync(category, paramName, op, value, limit, offset);
                    if (!string.IsNullOrEmpty(dto.Error)) return dto.Error;
                    var s = $"Found {dto.Total} {category} instance(s)";
                    if (!string.IsNullOrWhiteSpace(paramName)) s += $" where {paramName} {op} '{value}'";
                    s += $" (showing {dto.Items.Count} from offset {dto.Offset}):\n";
                    s += string.Join("\n", dto.Items.Select(e => $"{e.Id} \"{e.Name}\" cat:{e.Category}"));
                    if (dto.Total > dto.Offset + dto.Items.Count)
                        s += $"\n... {dto.Total - dto.Offset - dto.Items.Count} more. Use offset {dto.Offset + limit}.";
                    return s;
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetElementDetails(IPluginContext context)
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
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    eidStr = d.RootElement.GetProperty("elementId").GetString() ?? "";
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                try
                {
                    var dto = await context.Bridge.GetElementDetailsAsync(eidStr);
                    if (dto == null) return "Please open a model first.";
                    if (!string.IsNullOrEmpty(dto.Error)) return dto.Error;
                    var s = $"Id:{dto.Id} Name:\"{dto.Name}\" Cat:{dto.Category} Type:{dto.Type} Level:{dto.Level} Loc:{dto.Location}\nParameters (up to 30):\n";
                    foreach (var p in dto.Parameters)
                        s += $"  {p.Name} = {p.Value} (RO:{p.IsReadOnly} ST:{p.StorageType})\n";
                    if (dto.ParameterTotal > dto.Parameters.Count)
                        s += $"... and {dto.ParameterTotal - dto.Parameters.Count} more.";
                    return s;
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetWarnings(IPluginContext context)
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
                TryRead(json, d =>
                {
                    if (d.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number) limit = l.GetInt32();
                });
                limit = Polyfills.Clamp(limit, 1, 100);
                try
                {
                    if (!context.Bridge.HasActiveDocument)
                        return "Please open a model first.";
                    var warnings = await context.Bridge.GetWarningsAsync();
                    if (warnings.Count == 0) return "No warnings.";
                    var s = $"Warnings: {warnings.Count}\n";
                    int i = 0;
                    foreach (var w in warnings.Take(limit))
                    {
                        var ids = w.FailingElementIds.Count > 0 ? $" (Ids: {string.Join(", ", w.FailingElementIds)})" : "";
                        s += $"{++i}. {w.Description}{ids}\n";
                    }
                    if (warnings.Count > limit) s += $"... and {warnings.Count - limit} more.";
                    return s;
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetLevelsAndGrids(IPluginContext context)
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
                try
                {
                    var levels = await context.Bridge.GetLevelsAsync();
                    var grids = await context.Bridge.GetGridsAsync();
                    if (!context.Bridge.HasActiveDocument && levels.Count == 0 && grids.Count == 0)
                        return "Please open a model first.";
                    var s = $"Levels ({levels.Count}):\n" + string.Join("\n", levels.OrderBy(l => l.Elevation).Select(l => $"  {l.Id} \"{l.Name}\" elev {l.Elevation:F2} ft"));
                    s += $"\nGrids ({grids.Count}):\n" + string.Join("\n", grids.Select(g => $"  {g.Id} \"{g.Name}\""));
                    return s;
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetQuantities(IPluginContext context)
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
                TryRead(json, d =>
                {
                    if (d.TryGetProperty("elementIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        eids = arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                });
                try
                {
                    var list = await context.Bridge.GetElementQuantitiesAsync(eids);
                    if (list.Count == 0)
                    {
                        if (!context.Bridge.HasActiveDocument) return "Please open a model first.";
                        return "Please select elements first or provide elementIds.";
                    }
                    var s = $"Quantities for {list.Count} element(s):\n";
                    foreach (var q in list)
                    {
                        if (q.Name == "not found") { s += $"{q.Id}: not found\n"; continue; }
                        s += $"{q.Id} {q.Category} \"{q.Name}\":";
                        if (q.LengthFt.HasValue) s += $" L={q.LengthFt.Value:F2}ft";
                        if (q.AreaSqFt.HasValue) s += $" A={q.AreaSqFt.Value:F2}sqft";
                        if (q.VolumeCuFt.HasValue) s += $" V={q.VolumeCuFt.Value:F2}cuft";
                        if (!string.IsNullOrEmpty(q.BoundingBox)) s += $" BB={q.BoundingBox}";
                        s += "\n";
                    }
                    return s;
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateSaveAndSyncInfo(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_document_info",
                    Description = "Get document info: title, path, worksharing, central. Read-only. Use save_document to save.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new { },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async _ =>
            {
                try
                {
                    var model = await context.Bridge.GetModelSummaryAsync();
                    var sync = await context.Bridge.GetSyncInfoAsync();
                    if (model == null) return "Please open a model first.";
                    var ws = sync != null && sync.IsWorkshared ? "Workshared" : "Not workshared";
                    var central = sync?.CentralPath ?? "";
                    return $"Model: {model.Title}\nPath: {model.Path}\n{ws}\nCentral: {central}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateSaveDocument(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "save_document",
                    Description = "Save the active Revit document. L3. Host confirms before write. Fails if the document has never been saved to a path.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new { },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async _ =>
            {
                if (context.FullAccess == null) return "Cannot save: raw Revit access not granted.";
                return await context.FullAccess.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        var title = doc.Title;
                        var path = string.IsNullOrEmpty(doc.PathName) ? "(unsaved)" : doc.PathName;
                        var ws = doc.IsWorkshared ? "Workshared" : "Not workshared";
                        var central = "";
                        try { central = doc.IsWorkshared ? doc.GetWorksharingCentralModelPath()?.ToString() ?? "" : ""; } catch { }
                        if (string.IsNullOrEmpty(doc.PathName)) return "Cannot save: document has no path (unsaved).";
                        doc.Save();
                        return $"Saved {title} to {path} ({ws}) central: {central}";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
            }
        };
    }

    private static void TryRead(string json, Action<JsonElement> read)
    {
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            read(d.RootElement);
        }
        catch { }
    }
}
