using System.Globalization;
using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Revit;
using LoadingAI.Plugins;

namespace LoadingAI.Tools;

public static class ViewTools
{
    public static IEnumerable<ToolEntry> Create(IPluginContext context)
    {
        yield return CreateIsolateInView(context);
        yield return CreateHideInView(context);
        yield return CreateResetTemporaryView(context);
        yield return CreateOverrideColorInView(context);
        yield return CreateColorByConstructionSchedule(context);
        yield return CreateColorByParameter(context);
        yield return CreateCreateSheet(context);
        yield return CreatePlaceViewOnSheet(context);
        yield return CreateDuplicateViewEx(context);
        yield return CreateSetViewTemplate(context);
        yield return CreateSetCropBox(context);
        yield return CreateCreateDimension(context);
        yield return CreateTagAll(context);
    }

    internal static ToolEntry CreateIsolateInView(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "isolate_in_view",
                    Description = "Temporarily isolate elements or a whole category in the active view. Use for 'isolate the walls' or 'isolate selection'.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
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
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    if (d.RootElement.TryGetProperty("elementIds", out var arr) && arr.ValueKind == JsonValueKind.Array) eids = arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                    if (d.RootElement.TryGetProperty("isolate", out var iso) && iso.ValueKind is JsonValueKind.True or JsonValueKind.False) isolate = iso.GetBoolean();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat))
                {
                    var sel = await context.Bridge.GetSelectionSummaryAsync();
                    eids = sel.ElementIds.ToList();
                }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return "Please select elements first.";
                var result = await context.Bridge.IsolateInViewAsync(eids, cat, isolate);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateHideInView(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "hide_in_view",
                    Description = "Temporarily hide elements or a category in the active view. For 'hide all link model' use category='RVT Links' (aliases: link, links, revit links).",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
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
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    if (d.RootElement.TryGetProperty("elementIds", out var a) && a.ValueKind == JsonValueKind.Array) eids = a.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat))
                {
                    var sel = await context.Bridge.GetSelectionSummaryAsync();
                    eids = sel.ElementIds.ToList();
                }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return "Please select elements first.";
                var result = await context.Bridge.HideInViewAsync(eids, cat);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateResetTemporaryView(IPluginContext context)
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
                var result = await context.Bridge.ResetViewVisibilityAsync();
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateOverrideColorInView(IPluginContext context)
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
                            elementIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
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
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    if (d.RootElement.TryGetProperty("elementIds", out var a) && a.ValueKind == JsonValueKind.Array) eids = a.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                    if (d.RootElement.TryGetProperty("color", out var col) && col.ValueKind == JsonValueKind.String) color = col.GetString();
                    if (d.RootElement.TryGetProperty("reset", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False) reset = r.GetBoolean();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat))
                {
                    var sel = await context.Bridge.GetSelectionSummaryAsync();
                    eids = sel.ElementIds.ToList();
                }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return "Please select elements first.";
                var result = await context.Bridge.OverrideColorInViewAsync(eids, cat, color, reset);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateColorByConstructionSchedule(IPluginContext context)
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
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    if (d.RootElement.TryGetProperty("startDate", out var sd) && sd.ValueKind == JsonValueKind.String) startDateStr = sd.GetString() ?? "";
                    if (d.RootElement.TryGetProperty("intervalDays", out var iv) && iv.ValueKind == JsonValueKind.Number) interval = iv.GetInt32();
                    if (d.RootElement.TryGetProperty("categories", out var ca) && ca.ValueKind == JsonValueKind.Array) cats = ca.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("palette", out var pal) && pal.ValueKind == JsonValueKind.String) palette = pal.GetString() ?? palette;
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if (!TryParseDate(startDateStr, out _)) return $"Invalid startDate '{startDateStr}'.";
                interval = Polyfills.Clamp(interval, 1, 365);
                var result = await context.Bridge.ColorByConstructionScheduleAsync(startDateStr, interval, cats, palette);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateColorByParameter(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "color_by_parameter",
                    Description = "Color elements in the active view by any instance parameter value (Mark, Comments, Type, Level, …). One color per distinct value. Optional category; if omitted uses current selection, else Walls.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            paramName = new { type = "string", description = "Parameter name, e.g. Mark, Comments, Level" },
                            category = new { type = "string", description = "Optional category. If omitted: selection, else Walls." },
                            palette = new { type = "string", description = "Palette: rainbow, blue_red, heat. Default rainbow." }
                        },
                        required = new[] { "paramName" }
                    }
                }
            },
            Handler = async json =>
            {
                string paramName = "";
                string? cat = null;
                string palette = "rainbow";
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    if (d.RootElement.TryGetProperty("paramName", out var pn) && pn.ValueKind == JsonValueKind.String) paramName = pn.GetString() ?? "";
                    if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                    if (d.RootElement.TryGetProperty("palette", out var pal) && pal.ValueKind == JsonValueKind.String) palette = pal.GetString() ?? palette;
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if (string.IsNullOrWhiteSpace(paramName)) return "Provide paramName.";
                List<string>? selIds = null;
                if (string.IsNullOrWhiteSpace(cat))
                {
                    var sel = await context.Bridge.GetSelectionSummaryAsync();
                    if (sel.ElementCount > 0) selIds = sel.ElementIds.ToList();
                }
                var result = await context.Bridge.ColorByParameterAsync(paramName, cat, selIds, palette);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateCreateSheet(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "create_sheet",
                    Description = "Create a new sheet with a title block. pyRevit: Create Sheets",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            sheetNumber = new { type = "string" },
                            sheetName = new { type = "string" },
                            titleBlockName = new { type = "string", description = "Optional title block type name" }
                        },
                        required = new[] { "sheetNumber", "sheetName" }
                    }
                }
            },
            Handler = async json =>
            {
                string num = "", name = ""; string? tb = null;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    num = d.RootElement.GetProperty("sheetNumber").GetString() ?? "";
                    name = d.RootElement.GetProperty("sheetName").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("titleBlockName", out var t) && t.ValueKind == JsonValueKind.String) tb = t.GetString();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.CreateSheetAsync(num, name, tb);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreatePlaceViewOnSheet(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "place_view_on_sheet",
                    Description = "Place a view onto a sheet at (x,y) mm. pyRevit: Place Views on Sheets",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            viewId = new { type = "string" },
                            sheetId = new { type = "string" },
                            x = new { type = "number", description = "X mm from sheet origin" },
                            y = new { type = "number", description = "Y mm from sheet origin" }
                        },
                        required = new[] { "viewId", "sheetId" }
                    }
                }
            },
            Handler = async json =>
            {
                string vid = "", sid = ""; double x = 0, y = 0;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    vid = d.RootElement.GetProperty("viewId").GetString() ?? "";
                    sid = d.RootElement.GetProperty("sheetId").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("x", out var xv)) x = xv.GetDouble();
                    if (d.RootElement.TryGetProperty("y", out var yv)) y = yv.GetDouble();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.PlaceViewOnSheetAsync(vid, sid, x, y);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateDuplicateViewEx(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "duplicate_view_ex",
                    Description = "Duplicate a view, optionally with detailing. pyRevit: Duplicate Views",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            viewId = new { type = "string" },
                            withDetailing = new { type = "boolean", description = "Include detailing. Default false." }
                        },
                        required = new[] { "viewId" }
                    }
                }
            },
            Handler = async json =>
            {
                string vid = ""; bool det = false;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    vid = d.RootElement.GetProperty("viewId").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("withDetailing", out var w)) det = w.GetBoolean();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.DuplicateViewAsync(vid, det);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateSetViewTemplate(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "set_view_template",
                    Description = "Apply or clear a view template. Empty templateId clears.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            viewId = new { type = "string" },
                            templateId = new { type = "string", description = "Template element id. Empty to clear." }
                        },
                        required = new[] { "viewId" }
                    }
                }
            },
            Handler = async json =>
            {
                string vid = ""; string? tid = null;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    vid = d.RootElement.GetProperty("viewId").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("templateId", out var t) && t.ValueKind == JsonValueKind.String) tid = t.GetString();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.SetViewTemplateAsync(vid, tid);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateSetCropBox(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "set_crop_box",
                    Description = "Turn crop box on or off for a view.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            viewId = new { type = "string" },
                            enabled = new { type = "boolean" }
                        },
                        required = new[] { "viewId", "enabled" }
                    }
                }
            },
            Handler = async json =>
            {
                string vid = ""; bool en = false;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    vid = d.RootElement.GetProperty("viewId").GetString() ?? "";
                    en = d.RootElement.GetProperty("enabled").GetBoolean();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.SetCropBoxAsync(vid, en);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateCreateDimension(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "create_dimension",
                    Description = "Create a dimension between two walls in a view (active if omitted). pyRevit: Dimension",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            wallId1 = new { type = "string" },
                            wallId2 = new { type = "string" },
                            viewId = new { type = "string", description = "Optional. Defaults to active view." }
                        },
                        required = new[] { "wallId1", "wallId2" }
                    }
                }
            },
            Handler = async json =>
            {
                string w1 = "", w2 = ""; string? vid = null;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    w1 = d.RootElement.GetProperty("wallId1").GetString() ?? "";
                    w2 = d.RootElement.GetProperty("wallId2").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("viewId", out var v) && v.ValueKind == JsonValueKind.String) vid = v.GetString();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.CreateDimensionBetweenWallsAsync(w1, w2, vid);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateTagAll(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "tag_all",
                    Description = "Tag up to 50 elements of a category in the active view. pyRevit: Tag All",
                    Parameters = new
                    {
                        type = "object",
                        properties = new { category = new { type = "string", description = "e.g. Walls, Doors, Windows" } },
                        required = new[] { "category" }
                    }
                }
            },
            Handler = async json =>
            {
                string cat = "";
                try
                {
                    using var d = JsonDocument.Parse(json);
                    cat = d.RootElement.GetProperty("category").GetString() ?? "";
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.TagAllInActiveViewAsync(cat);
                return result.Message;
            }
        };
    }

    private static bool TryParseDate(string s, out DateTime dt)
    {
        s = (s ?? "").Trim();
        if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return true;
        if (DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return true;
        if (DateTime.TryParse(s, out dt)) return true;
        dt = default;
        return false;
    }
}
