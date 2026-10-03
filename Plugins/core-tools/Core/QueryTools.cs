using System.IO;
using System.Text;
using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Revit;
using LoadingAI.Plugins;

namespace LoadingAI.Tools;

public static class QueryTools
{
    public static IEnumerable<ToolEntry> Create(IPluginContext context)
    {
        // Every tool in this file is an L1 read, so none can write the model and none may report
        // a commit. Rather than re-type 27 bespoke report formatters as ToolExecutionResult, each
        // tool keeps its string body and gains a structured half here: AsQuery wraps the same
        // Handler and classifies the text it returns. The 27 CreateX bodies stay untouched.
        var entries = new[]
        {
            CreateGetModelSummary(context),
            CreateGetWallMetrics(context),
            CreateGetSelectionSummary(context),
            CreateGetActiveViewInfo(context),
            CreateGetVisibleElementsInView(context),
            CreateFindElementsByFilter(context),
            CreateGetElementDetails(context),
            CreateGetWarnings(context),
            CreateGetRooms(context),
            CreateGetLevelsAndGrids(context),
            CreateGetQuantities(context),
            CreateSaveAndSyncInfo(context),
            CreateGetWorksets(context),
            CreateGetPhases(context),
            CreateGetDesignOptions(context),
            CreateGetSheetsViews(context),
            CreateGetFamilyTypes(context),
            CreateGetMaterials(context),
            CreateGetRoomArea(context),
            CreateGetClashByBb(context),
            CreateGetModelHealthScore(context),
            CreateSyncInfo(context),
            CreateAuditWarningsFix(context),
            CreateQuantityTakeoff(context),
        };

        foreach (var entry in entries)
            yield return AsQuery(entry);
    }

    /// <summary>
    /// Returns a copy of an L1 query tool whose <see cref="ToolEntry.StructuredHandler"/> runs the
    /// same <see cref="ToolEntry.Handler"/> and classifies its text, so a query is a
    /// <c>NoWrite</c> result on success and a <c>Failed + NoWrite</c> when it reached nothing or
    /// caught an exception. <c>NoWrite</c> rather than <c>NotCommitted</c>: there was no
    /// transaction to commit or roll back.
    /// </summary>
    private static ToolEntry AsQuery(ToolEntry entry)
    {
        var inner = entry.Handler;
        return new ToolEntry
        {
            Definition = entry.Definition,
            Tier = entry.Tier,
            Presets = entry.Presets,
            Tags = entry.Tags,
            Describe = entry.Describe,
            Handler = inner,
            StructuredHandler = async json =>
            {
                string text;
                try
                {
                    text = await inner(json);
                }
                catch (Exception ex)
                {
                    return ToolExecutionResult.Failed($"Failed: {ex.Message}", ToolWriteCertainty.NoWrite);
                }

                if (text.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("Invalid args:", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("Invalid arguments:", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("Please open a model", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("Please select elements", StringComparison.OrdinalIgnoreCase))
                    return ToolExecutionResult.Failed(text, ToolWriteCertainty.NoWrite);

                return ToolExecutionResult.Succeeded(text, ToolWriteCertainty.NoWrite);
            },
        };
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
                    Description = "Get wall quantities: count, total length (mm), total area (m²), total volume (m³) for all walls or filtered by level name. Use for 'total wall area/length' questions.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            levelName = new { type = "string", description = "Optional level name to filter walls" },
                            limit = new { type = "integer", description = "Deprecated and ignored: totals always cover every wall." }
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
                    var s = $"Walls: {dto.Count}";
                    if (!string.IsNullOrWhiteSpace(dto.LevelName)) s += $" on level '{dto.LevelName}'";
                    s += $"\nTotal length: {Metric.Length(dto.TotalLengthFt)}";
                    s += $"\nTotal area: {Metric.Area(dto.TotalAreaSqFt)}";
                    s += $"\nTotal volume: {Metric.Volume(dto.TotalVolumeCuFt)}";
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
                    var total = warnings.Sum(w => w.Count);
                    var s = $"Warnings: {total} in {warnings.Count} kind(s)\n";
                    int i = 0;
                    foreach (var w in warnings.OrderByDescending(x => x.Count).Take(limit))
                    {
                        var ids = w.FailingElementIds.Count > 0 ? $" (e.g. ids: {string.Join(", ", w.FailingElementIds)})" : "";
                        s += $"{++i}. x{w.Count} {w.Description}{ids}\n";
                    }
                    if (warnings.Count > limit) s += $"... and {warnings.Count - limit} more kind(s).";
                    return s;
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetRooms(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_rooms",
                    Description = "List rooms: number, name, level, area (m²). Optional levelName filter. Use for 'what rooms are on this floor'.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            levelName = new { type = "string", description = "Optional exact level name" },
                            limit = new { type = "integer", description = "Max rooms to list, default 200" }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                string? levelName = null;
                int limit = 200;
                TryRead(json, d =>
                {
                    if (d.TryGetProperty("levelName", out var ln) && ln.ValueKind == JsonValueKind.String) levelName = ln.GetString();
                    if (d.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number) limit = l.GetInt32();
                });
                limit = Polyfills.Clamp(limit, 1, 500);
                try
                {
                    var rooms = await context.Bridge.GetRoomsAsync(levelName, limit);
                    if (!context.Bridge.HasActiveDocument && rooms.Count == 0)
                        return "Please open a model first.";
                    if (!string.IsNullOrWhiteSpace(levelName) && rooms.Count == 0)
                    {
                        var levels = await context.Bridge.GetLevelsAsync();
                        var names = levels.Select(l => l.Name).ToList();
                        if (LevelNames.Resolve(levelName!, names) == null)
                            return LevelNames.NotFound(levelName!, names);
                    }
                    if (rooms.Count == 0) return "No rooms.";
                    var placed = rooms.Where(r => !r.Unplaced).ToList();
                    var total = placed.Sum(r => r.AreaSqFt);
                    var lines = rooms.Select(r =>
                    {
                        var tag = string.IsNullOrWhiteSpace(r.Number) ? r.Name : $"{r.Number} {r.Name}".Trim();
                        var extra = r.Unplaced ? " unplaced" : "";
                        return $"  {r.Id} \"{tag}\" Level:{r.LevelName} Area:{Metric.Area(r.AreaSqFt)}{extra}";
                    });
                    return $"Rooms: {rooms.Count} Total area: {Metric.Area(total)}\n" + string.Join("\n", lines);
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
                    Description = "List levels and grids: name, elevation (mm), Id.",
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
                    var s = $"Levels ({levels.Count}):\n" + string.Join("\n", levels.OrderBy(l => l.Elevation).Select(l => $"  {l.Id} \"{l.Name}\" elev {Metric.Length(l.Elevation)}"));
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
                    Description = "Get length (mm) / area (m²) / volume (m³) for given elementIds or current selection.",
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
                    var s = $"Quantities for {list.Count} element(s){(list.Count >= 50 ? " (capped at 50 per call; pass fewer ids for the rest)" : "")}:\n";
                    foreach (var q in list)
                    {
                        if (q.Name == "not found") { s += $"{q.Id}: not found\n"; continue; }
                        s += $"{q.Id} {q.Category} \"{q.Name}\":";
                        if (q.LengthFt.HasValue) s += $" L={Metric.Length(q.LengthFt.Value)}";
                        if (q.AreaSqFt.HasValue) s += $" A={Metric.Area(q.AreaSqFt.Value)}";
                        if (q.VolumeCuFt.HasValue) s += $" V={Metric.Volume(q.VolumeCuFt.Value)}";
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

    internal static ToolEntry CreateGetWorksets(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_worksets",
                    Description = "List worksets (pyRevit: Worksets). No params. Compatible with worksharing models.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                try
                {
                    var dto = await context.Bridge.GetWorksetsAsync();
                    if (dto == null) return "Please open a model first.";
                    var s = $"Worksets ({dto.Worksets.Count}):\n" + string.Join("\n", dto.Worksets.Select(w =>
                        $"  {w.Id} \"{w.Name}\" Open:{w.IsOpen} Vis:{w.IsVisibleByDefault}"));
                    if (!dto.IsWorkshared) s = "(Not workshared) " + s;
                    return s;
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetPhases(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_phases",
                    Description = "List phases (pyRevit: Phases). No params.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                try
                {
                    var phases = await context.Bridge.GetPhasesAsync();
                    if (!context.Bridge.HasActiveDocument && phases.Count == 0)
                        return "Please open a model first.";
                    return $"Phases ({phases.Count}):\n" + string.Join("\n", phases.Select(p => $"  {p.Id} \"{p.Name}\""));
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetDesignOptions(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_design_options",
                    Description = "List design options and option sets (Dynamo: DesignOptions). No params.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                try
                {
                    var opts = await context.Bridge.GetDesignOptionsAsync();
                    if (!context.Bridge.HasActiveDocument && opts.Count == 0)
                        return "Please open a model first.";
                    return $"DesignOptions ({opts.Count}):\n" + string.Join("\n", opts.Select(o => $"  {o.Id} \"{o.Name}\" Primary:{o.IsPrimary}"));
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetSheetsViews(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_sheets_views",
                    Description = "List sheets and views counts by type (DiRoots SheetLink). No params.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                try
                {
                    var dto = await context.Bridge.GetSheetsViewsAsync();
                    if (dto == null) return "Please open a model first.";
                    var sheets = string.Join("\n", dto.Sheets.Select(s => $"  {s.Id} \"{s.Name}\" - {s.SheetNumber}"));
                    var more = dto.SheetCount > 10 ? "\n..." : "";
                    var byType = string.Join("\n", dto.ViewsByType.Select(g => $"  {g.Name}: {g.Count}"));
                    return $"Sheets: {dto.SheetCount}\n{sheets}{more}\nViews: {dto.ViewCount}\n{byType}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetFamilyTypes(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_family_types",
                    Description = "List family symbols by category (Dynamo: archi-lab Family Types). Params: category optional.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new { category = new { type = "string", description = "Category e.g. Doors, Windows" } },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                string? cat = null;
                TryRead(json, d =>
                {
                    if (d.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                });
                try
                {
                    var dto = await context.Bridge.GetFamilyTypesAsync(cat);
                    if (dto == null) return "Please open a model first.";
                    var lines = string.Join("\n", dto.Sample.Select(s => $"  {s.Id} \"{s.FamilyName}:{s.Name}\" Cat:{s.Category}"));
                    var more = dto.Total > 20 ? "\n..." : "";
                    return $"FamilySymbols ({dto.Total}):\n{lines}{more}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetMaterials(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_materials",
                    Description = "List materials in model (BIMOne). No params.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                try
                {
                    var dto = await context.Bridge.GetMaterialsAsync();
                    if (dto == null) return "Please open a model first.";
                    var lines = string.Join("\n", dto.Sample.Select(m => $"  {m.Id} \"{m.Name}\" Class:{m.MaterialClass}"));
                    var more = dto.Total > 20 ? "\n..." : "";
                    return $"Materials ({dto.Total}):\n{lines}{more}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetRoomArea(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_room_area",
                    Description = "Sum room area by level (Dynamo: Room.Area). Optional levelName.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new { levelName = new { type = "string", description = "Level filter" } },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                string? lvl = null;
                TryRead(json, d =>
                {
                    if (d.TryGetProperty("levelName", out var v) && v.ValueKind == JsonValueKind.String) lvl = v.GetString();
                });
                try
                {
                    var dto = await context.Bridge.GetRoomAreaAsync(lvl);
                    if (dto == null) return "Please open a model first.";
                    var body = dto.RoomCount > 0
                        ? "\n" + string.Join("\n", dto.Sample.Select(r =>
                            $"{r.Id} \"{r.Name}\" Level:{r.LevelName} Area:{Metric.Area(r.AreaSqFt)}"))
                        : "";
                    return $"Rooms: {dto.RoomCount} Total area: {Metric.Area(dto.TotalAreaSqFt)}{body}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetClashByBb(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_clash_by_bb",
                    Description = "Find element pairs whose axis-aligned bounding boxes overlap. A screening tool, not a solid clash test: it checks the first 200 elements of each category, and a hosted door or window always overlaps its wall. Params: category1, category2, limit.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            category1 = new { type = "string" },
                            category2 = new { type = "string" },
                            limit = new { type = "integer" }
                        },
                        required = new[] { "category1", "category2" }
                    }
                }
            },
            Handler = async json =>
            {
                string c1, c2;
                int lim = 20;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    c1 = d.RootElement.GetProperty("category1").GetString() ?? "";
                    c2 = d.RootElement.GetProperty("category2").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number)
                        lim = l.GetInt32();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                lim = Polyfills.Clamp(lim, 1, 100);
                try
                {
                    var dto = await context.Bridge.GetClashByBoundingBoxAsync(c1, c2, lim);
                    if (dto == null) return "Please open a model first.";
                    if (!string.IsNullOrEmpty(dto.Error)) return dto.Error;
                    if (dto.ClashCount == 0)
                        return $"No clashes found between {dto.Category1} and {dto.Category2} (sampled 200 each).";
                    var lines = string.Join("\n", dto.Clashes.Select(p => $"{p.IdA}({p.CategoryA}) <-> {p.IdB}({p.CategoryB})"));
                    return $"Bounding-box overlaps ({dto.ClashCount}{(dto.ClashCount >= lim ? ", limit reached" : "")}), first 200 of each category:\n{lines}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateGetModelHealthScore(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "get_model_health_score",
                    Description = "Rough health score from the warning count (70%) and the number of views (30%). It is not a model audit. No params.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                try
                {
                    var dto = await context.Bridge.GetModelHealthScoreAsync();
                    if (dto == null) return "Please open a model first.";
                    return $"Health score: {dto.Score:F0}/100\nWarnings: {dto.WarningCount} (score {dto.WarningScore})\nViews: {dto.ViewCount}\nWorkshared: {dto.IsWorkshared}\nTitle: {dto.Title}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateSyncInfo(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "sync_info",
                    Description = "Get the worksharing central path and whether the model is workshared. It does not report the last sync time. No params.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                try
                {
                    if (!context.Bridge.HasActiveDocument)
                        return "Please open a model first.";
                    var model = await context.Bridge.GetModelSummaryAsync();
                    var dto = await context.Bridge.GetSyncInfoAsync();
                    var isWs = dto?.IsWorkshared ?? false;
                    string central;
                    if (!isWs) central = "(not workshared)";
                    else if (string.IsNullOrEmpty(dto?.CentralPath)) central = "(unknown)";
                    else central = dto!.CentralPath;
                    var title = model?.Title ?? "";
                    var path = model?.Path ?? "";
                    return $"Workshared: {isWs}\nCentral: {central}\nTitle: {title}\nPath: {path}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateAuditWarningsFix(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "audit_warnings_fix",
                    Description = "List individual warnings with the first failing element ids to check. Read-only; it fixes nothing. Params: limit.",
                    Parameters = new { type = "object", properties = new { limit = new { type = "integer" } }, required = Array.Empty<string>() }
                }
            },
            Handler = async json =>
            {
                int lim = 10;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number)
                        lim = l.GetInt32();
                }
                catch { }
                lim = Polyfills.Clamp(lim, 1, 50);
                try
                {
                    if (!context.Bridge.HasActiveDocument)
                        return "Please open a model first.";
                    var warns = await context.Bridge.GetWarningsRawAsync(lim);
                    if (warns.Count == 0) return "No warnings.";
                    var s = $"Warnings {warns.Count} (top {lim}):\n";
                    int i = 0;
                    foreach (var w in warns)
                    {
                        var check = w.FailingElementIds.Count > 0
                            ? string.Join(",", w.FailingElementIds.Take(2))
                            : "";
                        s += $"{++i}. {w.Description} - Suggest: check {check}\n";
                    }
                    return s;
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateQuantityTakeoff(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "quantity_takeoff_by_category",
                    Description = "Quantity takeoff by category: count, total area, volume, length aggregated to CSV (Dynamo: Quantity Takeoff). Params: category, filePath optional.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            category = new { type = "string" },
                            filePath = new { type = "string" }
                        },
                        required = new[] { "category" }
                    }
                }
            },
            Handler = async json =>
            {
                string cat = ""; string? fp = null;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    cat = d.RootElement.GetProperty("category").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("filePath", out var f) && f.ValueKind == JsonValueKind.String)
                        fp = f.GetString();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                try
                {
                    var dto = await context.Bridge.GetQuantityTakeoffAsync(cat);
                    if (dto == null) return "Please open a model first.";
                    if (!string.IsNullOrEmpty(dto.Error)) return dto.Error;
                    var sb = new StringBuilder();
                    sb.AppendLine("Id,Name,Level,AreaM2,VolumeM3,LengthMm");
                    foreach (var row in dto.Rows)
                    {
                        sb.AppendLine(string.Join(",", new[]
                        {
                            row.Id,
                            CsvText.Cell(row.Name),
                            CsvText.Cell(row.Level),
                            CsvText.Number(Metric.Ft2ToM2(row.AreaFt2), "0.00"),
                            CsvText.Number(Metric.Ft3ToM3(row.VolumeFt3), "0.000"),
                            CsvText.Number(Metric.FtToMm(row.LengthFt), "0")
                        }));
                    }
                    var path = ExportPaths.ResolveWrite(fp, $"takeoff_{cat}_{DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.csv");
                    File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                    return $"{cat} takeoff: {dto.Count} instances Area {Metric.Area(dto.TotalAreaFt2)} Vol {Metric.Volume(dto.TotalVolumeFt3)} Len {Metric.Length(dto.TotalLengthFt)}\nCSV: {path}\nPreview:\n" + string.Join("\n", sb.ToString().Split('\n').Take(6));
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
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
