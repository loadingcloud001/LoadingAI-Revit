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
        yield return CreateResetViewOverrides(context);
        yield return CreateCreateSheet(context);
        yield return CreatePlaceViewOnSheet(context);
        yield return CreateDuplicateViewEx(context);
        yield return CreateSetViewTemplate(context);
        yield return CreateSetCropBox(context);
        yield return CreateCreateDimension(context);
        yield return CreateTagAll(context);
    }

    internal static ToolEntry CreateIsolateInView(IPluginContext context) => new()
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
        Handler = async json => (await IsolateInViewAsync(context, json)).Text,
        StructuredHandler = json => IsolateInViewAsync(context, json)
    };

    private static async Task<ToolExecutionResult> IsolateInViewAsync(IPluginContext context, string json)
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
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat))
        {
            var sel = await context.Bridge.GetSelectionSummaryAsync();
            var truncated = SelectionGuards.TruncationRefusal(sel);
            if (truncated != null) return Refused(truncated);
            eids = sel.ElementIds.ToList();
        }
        if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return Refused("Please select elements first.");
        return WriteResult(await context.Bridge.IsolateInViewAsync(eids, cat, isolate));
    }

    internal static ToolEntry CreateHideInView(IPluginContext context) => new()
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
        Handler = async json => (await HideInViewAsync(context, json)).Text,
        StructuredHandler = json => HideInViewAsync(context, json)
    };

    private static async Task<ToolExecutionResult> HideInViewAsync(IPluginContext context, string json)
    {
        List<string>? eids = null;
        string? cat = null;
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (d.RootElement.TryGetProperty("elementIds", out var a) && a.ValueKind == JsonValueKind.Array) eids = a.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
            if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
        }
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat))
        {
            var sel = await context.Bridge.GetSelectionSummaryAsync();
            var truncated = SelectionGuards.TruncationRefusal(sel);
            if (truncated != null) return Refused(truncated);
            eids = sel.ElementIds.ToList();
        }
        if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return Refused("Please select elements first.");
        return WriteResult(await context.Bridge.HideInViewAsync(eids, cat));
    }

    internal static ToolEntry CreateResetTemporaryView(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "reset_temporary_view",
                Description = "Reset (disable) temporary hide/isolate in the active view. Does not touch colour overrides; use reset_view_overrides for those.",
                Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
            }
        },
        Handler = async _ => (await ResetTemporaryViewAsync(context)).Text,
        StructuredHandler = _ => ResetTemporaryViewAsync(context)
    };

    private static async Task<ToolExecutionResult> ResetTemporaryViewAsync(IPluginContext context)
        => WriteResult(await context.Bridge.ResetViewVisibilityAsync());

    internal static ToolEntry CreateOverrideColorInView(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "override_color_in_view",
                Description = "Colour elements or a whole category in the active view with a solid fill. The override is saved in the view and syncs like any edit; it is not a temporary mode. Undo with Ctrl+Z or reset_view_overrides. Use e.g. 'color those doors red'.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        elementIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
                        category = new { type = "string", description = "Category to color. Alternative to elementIds." },
                        color = new { type = "string", description = "Color name (red, blue, green, yellow, orange) or hex #RRGGBB" },
                        reset = new { type = "boolean", description = "True to put back what the view had before LoadingAI coloured these elements. Default false." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json => (await OverrideColorInViewAsync(context, json)).Text,
        StructuredHandler = json => OverrideColorInViewAsync(context, json)
    };

    private static async Task<ToolExecutionResult> OverrideColorInViewAsync(IPluginContext context, string json)
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
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat))
        {
            var sel = await context.Bridge.GetSelectionSummaryAsync();
            var truncated = SelectionGuards.TruncationRefusal(sel);
            if (truncated != null) return Refused(truncated);
            eids = sel.ElementIds.ToList();
        }
        if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return Refused("Please select elements first.");
        return WriteResult(await context.Bridge.OverrideColorInViewAsync(eids, cat, color, reset));
    }

    internal static ToolEntry CreateColorByConstructionSchedule(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "color_by_construction_schedule",
                Description = "Bulk colour elements in the active view by construction schedule, bottom-up (earliest blue → latest red), in ONE transaction, with a solid fill saved in the view. Undo with reset_view_overrides. Provide startDate and intervalDays, optional categories and palette.",
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
        Handler = async json => (await ColorByConstructionScheduleAsync(context, json)).Text,
        StructuredHandler = json => ColorByConstructionScheduleAsync(context, json)
    };

    private static async Task<ToolExecutionResult> ColorByConstructionScheduleAsync(IPluginContext context, string json)
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
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        if (!TryParseDate(startDateStr, out _)) return Refused($"Invalid startDate '{startDateStr}'.");
        interval = Polyfills.Clamp(interval, 1, 365);
        return WriteResult(await context.Bridge.ColorByConstructionScheduleAsync(startDateStr, interval, cats, palette));
    }

    internal static ToolEntry CreateColorByParameter(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "color_by_parameter",
                Description = "Colour elements in the active view by any instance parameter value (Mark, Comments, Type, Level, …), one colour per distinct value, with a solid fill saved in the view. Undo with reset_view_overrides. Optional category; if omitted uses current selection, else Walls.",
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
        Handler = async json => (await ColorByParameterAsync(context, json)).Text,
        StructuredHandler = json => ColorByParameterAsync(context, json)
    };

    private static async Task<ToolExecutionResult> ColorByParameterAsync(IPluginContext context, string json)
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
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        if (string.IsNullOrWhiteSpace(paramName)) return Refused("Provide paramName.");
        List<string>? selIds = null;
        if (string.IsNullOrWhiteSpace(cat))
        {
            var sel = await context.Bridge.GetSelectionSummaryAsync();
            var truncated = SelectionGuards.TruncationRefusal(sel);
            if (truncated != null) return Refused(truncated);
            if (sel.ElementCount > 0) selIds = sel.ElementIds.ToList();
        }
        return WriteResult(await context.Bridge.ColorByParameterAsync(paramName, cat, selIds, palette));
    }

    internal static ToolEntry CreateResetViewOverrides(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "reset_view_overrides",
                Description = "Put back the graphic overrides the active view had before override_color_in_view, color_by_parameter or color_by_construction_schedule changed them. Works for this Revit session only; Ctrl+Z also undoes a colouring. Does not touch temporary hide/isolate (use reset_temporary_view).",
                Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
            }
        },
        Handler = async _ => (await ResetViewOverridesAsync(context)).Text,
        StructuredHandler = _ => ResetViewOverridesAsync(context)
    };

    private static async Task<ToolExecutionResult> ResetViewOverridesAsync(IPluginContext context)
        => WriteResult(await context.Bridge.ResetViewOverridesAsync());

    internal static ToolEntry CreateCreateSheet(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_sheet",
                Description = "Create one sheet. Sheet number and sheet name are required. Title block is a type name or 'Family: Type'; when omitted, the first loaded title block is used.",
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
        Presets = new[]
        {
            new PresetField { Id = "sheetNumber", Label = "Sheet number", Kind = PresetFieldKind.String, Required = true },
            new PresetField { Id = "sheetName", Label = "Sheet name", Kind = PresetFieldKind.String, Required = true },
            new PresetField { Id = "titleBlockName", Label = "Title block", Kind = PresetFieldKind.String }
        },
        Handler = async json => (await CreateSheetAsync(context, json)).Text,
        StructuredHandler = json => CreateSheetAsync(context, json)
    };

    private static async Task<ToolExecutionResult> CreateSheetAsync(IPluginContext context, string json)
    {
        string num = "", name = ""; string? tb = null;
        try
        {
            using var d = JsonDocument.Parse(json);
            num = d.RootElement.GetProperty("sheetNumber").GetString() ?? "";
            name = d.RootElement.GetProperty("sheetName").GetString() ?? "";
            if (d.RootElement.TryGetProperty("titleBlockName", out var t) && t.ValueKind == JsonValueKind.String) tb = t.GetString();
        }
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        return WriteResult(await context.Bridge.CreateSheetAsync(num, name, tb));
    }

    internal static ToolEntry CreatePlaceViewOnSheet(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "place_view_on_sheet",
                Description = "Place a view or schedule onto a sheet. Pick the view and the sheet, or pass their ids. x and y are millimetres from the sheet origin. A view can be on one sheet only; the result says why a view cannot be placed.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        viewId = new { type = "string" },
                        sheetId = new { type = "string" },
                        viewIds = new { type = "array", items = new { type = "string" }, description = "Pick. The first id is placed." },
                        sheetIds = new { type = "array", items = new { type = "string" }, description = "Pick. The first id receives the view." },
                        x = new { type = "number", description = "X mm from sheet origin" },
                        y = new { type = "number", description = "Y mm from sheet origin" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "viewIds", Label = "View", Kind = PresetFieldKind.PickElements, Prompt = "Pick the view to place." },
            new PresetField { Id = "sheetIds", Label = "Sheet", Kind = PresetFieldKind.PickElements, Prompt = "Pick the sheet." },
            new PresetField { Id = "x", Label = "X (mm from sheet origin)", Kind = PresetFieldKind.Number, Default = 0 },
            new PresetField { Id = "y", Label = "Y (mm from sheet origin)", Kind = PresetFieldKind.Number, Default = 0 }
        },
        Handler = async json => (await PlaceViewOnSheetAsync(context, json)).Text,
        StructuredHandler = json => PlaceViewOnSheetAsync(context, json)
    };

    private static async Task<ToolExecutionResult> PlaceViewOnSheetAsync(IPluginContext context, string json)
    {
        string vid = "", sid = ""; double x = 0, y = 0;
        try
        {
            using var d = JsonDocument.Parse(json);
            vid = FirstId(d.RootElement, "viewIds") ?? (d.RootElement.TryGetProperty("viewId", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "");
            sid = FirstId(d.RootElement, "sheetIds") ?? (d.RootElement.TryGetProperty("sheetId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "");
            if (d.RootElement.TryGetProperty("x", out var xv) && xv.ValueKind == JsonValueKind.Number) x = xv.GetDouble();
            if (d.RootElement.TryGetProperty("y", out var yv) && yv.ValueKind == JsonValueKind.Number) y = yv.GetDouble();
        }
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        if (string.IsNullOrWhiteSpace(vid) || string.IsNullOrWhiteSpace(sid))
            return Refused("Pick a view and a sheet, or pass viewId and sheetId.");
        return WriteResult(await context.Bridge.PlaceViewOnSheetAsync(vid, sid, x, y));
    }

    internal static ToolEntry CreateDuplicateViewEx(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "duplicate_view_ex",
                Description = "Duplicate a view, optionally with detailing. Pick a view, or leave it empty to duplicate the active view. pyRevit: Duplicate Views",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        viewId = new { type = "string" },
                        viewIds = new { type = "array", items = new { type = "string" }, description = "Pick. The first id is duplicated. Empty uses the active view." },
                        withDetailing = new { type = "boolean", description = "Include detailing. Default false." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "viewIds", Label = "View (empty = active view)", Kind = PresetFieldKind.PickElements, Prompt = "Pick the view to duplicate. Leave empty to use the active view." },
            new PresetField { Id = "withDetailing", Label = "Include detailing", Kind = PresetFieldKind.Boolean, Default = false }
        },
        Handler = async json => (await DuplicateViewExAsync(context, json)).Text,
        StructuredHandler = json => DuplicateViewExAsync(context, json)
    };

    private static async Task<ToolExecutionResult> DuplicateViewExAsync(IPluginContext context, string json)
    {
        string vid = ""; bool det = false;
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            vid = FirstId(d.RootElement, "viewIds")
                ?? (d.RootElement.TryGetProperty("viewId", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "");
            if (d.RootElement.TryGetProperty("withDetailing", out var w) && w.ValueKind is JsonValueKind.True or JsonValueKind.False)
                det = w.GetBoolean();
        }
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        return WriteResult(await context.Bridge.DuplicateViewAsync(vid, det));
    }

    internal static ToolEntry CreateSetViewTemplate(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "set_view_template",
                Description = "Apply or clear a view template on one or more views. Pick the views, or leave them empty to use the active view. templateName is the template's name; an empty name clears. templateId still works when no name is given. Dry-run lists each view's current template and the name it would get, and writes nothing.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        viewId = new { type = "string" },
                        viewIds = new { type = "array", items = new { type = "string" }, description = "Pick. Empty uses the active view." },
                        templateId = new { type = "string", description = "Template element id. Ignored when templateName is present." },
                        templateName = new { type = "string", description = "Template name. Empty clears." },
                        dryRun = new { type = "boolean", description = "List the change only. Writes nothing." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "viewIds", Label = "Views (empty = active view)", Kind = PresetFieldKind.PickElements, Prompt = "Pick the views. Leave empty to use the active view." },
            new PresetField { Id = "templateName", Label = "Template name (empty = clear)", Kind = PresetFieldKind.String },
            new PresetField { Id = "dryRun", Label = "Dry-run (list only)", Kind = PresetFieldKind.Boolean, Default = false }
        },
        Handler = async json => (await SetViewTemplateAsync(context, json)).Text,
        StructuredHandler = json => SetViewTemplateAsync(context, json)
    };

    private static async Task<ToolExecutionResult> SetViewTemplateAsync(IPluginContext context, string json)
    {
        string vid = ""; string? tid = null; string? templateName = null;
        IReadOnlyList<string>? viewIds = null;
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var ids = AllIds(d.RootElement, "viewIds");
            if (ids.Count > 0) viewIds = ids;
            if (d.RootElement.TryGetProperty("viewId", out var v) && v.ValueKind == JsonValueKind.String) vid = v.GetString() ?? "";
            if (d.RootElement.TryGetProperty("templateId", out var t) && t.ValueKind == JsonValueKind.String) tid = t.GetString();
            if (d.RootElement.TryGetProperty("templateName", out var n) && n.ValueKind == JsonValueKind.String) templateName = n.GetString() ?? "";
        }
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        var dryRun = ToolPresets.IsDryRun(json);
        var result = await context.Bridge.SetViewTemplateAsync(vid, tid, viewIds, templateName, dryRun);
        return FromDryOrWrite(result, dryRun);
    }

    internal static ToolEntry CreateSetCropBox(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "set_crop_box",
                Description = "Turn the crop box on or off. Pick the views, or leave them empty to use the active view.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        viewId = new { type = "string" },
                        viewIds = new { type = "array", items = new { type = "string" }, description = "Pick. Empty uses the active view." },
                        enabled = new { type = "boolean" }
                    },
                    required = new[] { "enabled" }
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "viewIds", Label = "Views (empty = active view)", Kind = PresetFieldKind.PickElements, Prompt = "Pick the views. Leave empty to use the active view." },
            new PresetField { Id = "enabled", Label = "Crop on", Kind = PresetFieldKind.Boolean, Default = true }
        },
        Handler = async json => (await SetCropBoxAsync(context, json)).Text,
        StructuredHandler = json => SetCropBoxAsync(context, json)
    };

    private static async Task<ToolExecutionResult> SetCropBoxAsync(IPluginContext context, string json)
    {
        string vid = ""; bool en = false;
        IReadOnlyList<string>? viewIds = null;
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var ids = AllIds(d.RootElement, "viewIds");
            if (ids.Count > 0) viewIds = ids;
            if (d.RootElement.TryGetProperty("viewId", out var v) && v.ValueKind == JsonValueKind.String) vid = v.GetString() ?? "";
            en = d.RootElement.GetProperty("enabled").GetBoolean();
        }
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        return WriteResult(await context.Bridge.SetCropBoxAsync(vid, en, viewIds));
    }

    internal static ToolEntry CreateCreateDimension(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_dimension",
                Description = "Dimension the clear distance between two parallel straight walls, face to face, in a plan, section or elevation view. Pick the two walls. An empty view uses the active view. Refuses walls that are not parallel, and views that are not a plan, section, or elevation.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        wallId1 = new { type = "string" },
                        wallId2 = new { type = "string" },
                        wallIds1 = new { type = "array", items = new { type = "string" }, description = "Pick. The first id is the first wall." },
                        wallIds2 = new { type = "array", items = new { type = "string" }, description = "Pick. The first id is the second wall." },
                        viewId = new { type = "string", description = "Optional. Defaults to the active view." },
                        viewIds = new { type = "array", items = new { type = "string" }, description = "Pick. The first id is the view. Empty uses the active view." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "wallIds1", Label = "First wall", Kind = PresetFieldKind.PickElements, Prompt = "Pick the first wall.", Required = true },
            new PresetField { Id = "wallIds2", Label = "Second wall", Kind = PresetFieldKind.PickElements, Prompt = "Pick the second wall.", Required = true },
            new PresetField { Id = "viewIds", Label = "View (empty = active view)", Kind = PresetFieldKind.PickElements, Prompt = "Pick a plan, section, or elevation. Leave empty to use the active view." }
        },
        Handler = async json => (await CreateDimensionAsync(context, json)).Text,
        StructuredHandler = json => CreateDimensionAsync(context, json)
    };

    private static async Task<ToolExecutionResult> CreateDimensionAsync(IPluginContext context, string json)
    {
        string w1 = "", w2 = ""; string? vid = null;
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            w1 = FirstId(d.RootElement, "wallIds1")
                ?? (d.RootElement.TryGetProperty("wallId1", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "");
            w2 = FirstId(d.RootElement, "wallIds2")
                ?? (d.RootElement.TryGetProperty("wallId2", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "");
            vid = FirstId(d.RootElement, "viewIds")
                ?? (d.RootElement.TryGetProperty("viewId", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null);
        }
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        if (string.IsNullOrWhiteSpace(w1) || string.IsNullOrWhiteSpace(w2))
            return Refused("Pick two walls, or pass wallId1 and wallId2.");
        return WriteResult(await context.Bridge.CreateDimensionBetweenWallsAsync(w1, w2, vid));
    }

    internal static ToolEntry CreateTagAll(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "tag_all",
                Description = "Tag up to 50 elements of one category in the active view. Each tag is placed on its element. The category needs a loaded tag family. The result says how many were tagged and why the rest were not. Dry-run counts what is visible and says whether a tag for that category is loaded, and writes nothing.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        category = new { type = "string", description = "e.g. Walls, Doors, Windows" },
                        dryRun = new { type = "boolean", description = "Count only. Writes nothing." }
                    },
                    required = new[] { "category" }
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "category", Label = "Category", Kind = PresetFieldKind.String, Required = true, Prompt = "Walls, Doors, Windows, Rooms" },
            new PresetField { Id = "dryRun", Label = "Dry-run (count only)", Kind = PresetFieldKind.Boolean, Default = false }
        },
        Handler = async json => (await TagAllAsync(context, json)).Text,
        StructuredHandler = json => TagAllAsync(context, json)
    };

    private static async Task<ToolExecutionResult> TagAllAsync(IPluginContext context, string json)
    {
        string cat = "";
        try
        {
            using var d = JsonDocument.Parse(json);
            cat = d.RootElement.GetProperty("category").GetString() ?? "";
        }
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        var dryRun = ToolPresets.IsDryRun(json);
        var result = await context.Bridge.TagAllInActiveViewAsync(cat, dryRun);
        return FromDryOrWrite(result, dryRun);
    }

    private static ToolExecutionResult FromDryOrWrite(CommandResultDto result, bool dryRun)
        => dryRun
            ? result.Ok
                ? ToolExecutionResult.Succeeded(result.Message, ToolWriteCertainty.NoWrite)
                : ToolExecutionResult.Failed(result.Message, ToolWriteCertainty.NotCommitted)
            : WriteResult(result);

    /// <summary>
    /// Every view tool is a write, and every one of them returns CommandResultDto, so Ok decides
    /// commit versus not-committed and AffectedCount becomes the changed count. Ok=false wrote
    /// nothing and forces a turn rollback.
    /// </summary>
    private static ToolExecutionResult WriteResult(CommandResultDto result)
        => result.Ok
            ? ToolExecutionResult.Succeeded(result.Message, ToolWriteCertainty.Committed, result.AffectedCount)
            : ToolExecutionResult.Failed(result.Message, ToolWriteCertainty.NotCommitted);

    /// <summary>
    /// A precondition refusal or a parse guard. None of these carry the Failed prefix the legacy
    /// path looked for, so each used to read as a successful committed write. They reached no
    /// transaction, so they are Failed + NotCommitted.
    /// </summary>
    private static ToolExecutionResult Refused(string text)
        => ToolExecutionResult.Failed(text, ToolWriteCertainty.NotCommitted);

    static List<string> AllIds(JsonElement root, string name)
    {
        var ids = new List<string>();
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return ids;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                ids.Add(item.GetString()!);
            else if (item.ValueKind == JsonValueKind.Number)
                ids.Add(item.ToString());
        }
        return ids;
    }

    static string? FirstId(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                return item.GetString();
            if (item.ValueKind == JsonValueKind.Number)
                return item.ToString();
        }
        return null;
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
