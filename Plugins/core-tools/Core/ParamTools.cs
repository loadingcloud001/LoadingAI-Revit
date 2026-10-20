using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
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
        yield return CreateExportParamsToExcel(context);
        yield return CreateImportExcelToParams(context);
        yield return CreateCopyParams(context);
        yield return CreateReplaceText(context);
        yield return CreateNumberingAuto(context);
    }

    internal static ToolEntry CreateSetParameterById(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Describe = json => DescribeSetParameterById(context, json),
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
                        value = new { type = "string", description = "Value to set. Length parameters take millimetres, area m², volume m³, angles degrees: a plain number, or a number with its unit such as '3000 mm' or '3 m'." }
                    },
                    required = new[] { "elementId", "paramName", "value" }
                }
            }
        },
        Handler = async json => (await SetParameterByIdAsync(context, json)).Text,
        StructuredHandler = json => SetParameterByIdAsync(context, json)
    };

    private static async Task<ToolExecutionResult> SetParameterByIdAsync(IPluginContext context, string json)
    {
        string eidStr = "", paramName = "", value = "";
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            eidStr = d.RootElement.GetProperty("elementId").GetString() ?? "";
            paramName = d.RootElement.GetProperty("paramName").GetString() ?? "";
            value = d.RootElement.GetProperty("value").GetString() ?? "";
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Failed($"Invalid args: {ex.Message}", ToolWriteCertainty.NotCommitted);
        }
        var result = await context.Bridge.SetParameterAsync(eidStr, paramName, value);
        if (!result.Ok) return ToolExecutionResult.Failed(result.Message, ToolWriteCertainty.NotCommitted);
        // The composed success sentence is part of the tool's contract and is preserved verbatim.
        return ToolExecutionResult.Succeeded(
            $"Set {paramName}=\"{value}\" on {eidStr}.", ToolWriteCertainty.Committed, result.AffectedCount);
    }

    internal static ToolEntry CreateSetCommentsOnSelection(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Describe = json => DescribeSetComments(context, json),
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "set_comments_on_selection",
                Description = "Set the Comments parameter on the currently selected elements. Requires user confirmation before writing.",
                Parameters = new { type = "object", properties = new { text = new { type = "string", description = "Text to write into the Comments parameter" } }, required = new[] { "text" } }
            }
        },
        Handler = async json => (await SetCommentsOnSelectionAsync(context, json)).Text,
        StructuredHandler = json => SetCommentsOnSelectionAsync(context, json)
    };

    private static async Task<ToolExecutionResult> SetCommentsOnSelectionAsync(IPluginContext context, string json)
    {
        string text;
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (!d.RootElement.TryGetProperty("text", out var p))
                return ToolExecutionResult.Failed("Missing required parameter 'text'.", ToolWriteCertainty.NotCommitted);
            text = p.GetString() ?? "";
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Failed($"Invalid arguments: {ex.Message}", ToolWriteCertainty.NotCommitted);
        }
        var sel = await context.Bridge.GetSelectionSummaryAsync();
        if (sel.ElementCount == 0)
            return ToolExecutionResult.Failed("Please select elements first.", ToolWriteCertainty.NotCommitted);
        var truncated = SelectionGuards.TruncationRefusal(sel);
        if (truncated != null)
            return ToolExecutionResult.Failed(truncated, ToolWriteCertainty.NotCommitted);
        var result = await context.Bridge.SetParametersAsync(sel.ElementIds, "Comments", text);
        return WriteResult(result);
    }

    internal static ToolEntry CreateBatchSetParameter(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Describe = json => DescribeBatchSet(context, json),
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "batch_set_parameter",
                Description = "Batch set a parameter on the elements matching a category+filter (the first `limit`, default 200, max 1000) in one transaction. The approval card shows how many match.",
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
                        targetValue = new { type = "string", description = "Value to set. Length parameters take millimetres, area m², volume m³, angles degrees: a plain number, or a number with its unit such as '3000 mm' or '3 m'." },
                        limit = new { type = "integer", description = "Max to update, default 200" }
                    },
                    required = new[] { "category", "targetParam", "targetValue" }
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "category", Label = "Category", Kind = PresetFieldKind.String, Required = true },
            new PresetField { Id = "filterParam", Label = "Filter parameter", Kind = PresetFieldKind.String },
            new PresetField { Id = "filterOp", Label = "Filter", Kind = PresetFieldKind.Select, Options = new[] { "=", "!=", "contains", "empty", "notempty" }, Default = "=" },
            new PresetField { Id = "filterValue", Label = "Filter value", Kind = PresetFieldKind.String },
            new PresetField { Id = "targetParam", Label = "Parameter to set", Kind = PresetFieldKind.String, Required = true },
            new PresetField { Id = "targetValue", Label = "Value", Kind = PresetFieldKind.String, Required = true },
            new PresetField { Id = "limit", Label = "Max elements", Kind = PresetFieldKind.Number, Default = 200 }
        },
        Handler = async json => (await BatchSetParameterAsync(context, json)).Text,
        StructuredHandler = json => BatchSetParameterAsync(context, json)
    };

    private static async Task<ToolExecutionResult> BatchSetParameterAsync(IPluginContext context, string json)
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
        catch (Exception ex)
        {
            return ToolExecutionResult.Failed($"Invalid args: {ex.Message}", ToolWriteCertainty.NotCommitted);
        }
        limit = Polyfills.Clamp(limit, 1, 1000);
        var found = await context.Bridge.FindElementsFilteredAsync(category, filterParam, filterOp, filterValue, limit, 0);
        if (!string.IsNullOrEmpty(found.Error))
            return ToolExecutionResult.Failed(found.Error, ToolWriteCertainty.NotCommitted);
        if (found.Total == 0)
            return ToolExecutionResult.Failed($"No elements matched filter for {category}.", ToolWriteCertainty.NotCommitted);
        var ids = found.Items.Select(e => e.Id).ToList();
        var result = await context.Bridge.SetParametersAsync(ids, targetParam, targetValue);
        if (!result.Ok) return ToolExecutionResult.Failed(result.Message, ToolWriteCertainty.NotCommitted);
        return ToolExecutionResult.Succeeded(
            $"Batch updated {result.AffectedCount} {category} (matched {found.Total}, limited {ids.Count}).",
            ToolWriteCertainty.Committed, result.AffectedCount);
    }

    internal static ToolEntry CreateRenameElements(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "rename_elements",
                Description = "Rename views, sheets, or other named elements. Pick the elements and give a pattern such as Plan-{i}, or pass newNames of the same length.",
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
        Presets = new[]
        {
            new PresetField { Id = "elementIds", Label = "Elements", Kind = PresetFieldKind.PickElements, Required = true, Prompt = "Pick the elements to rename." },
            new PresetField { Id = "newNamePattern", Label = "Name pattern ({i})", Kind = PresetFieldKind.String }
        },
        Handler = async json => (await RenameElementsAsync(context, json)).Text,
        StructuredHandler = json => RenameElementsAsync(context, json)
    };

    private static async Task<ToolExecutionResult> RenameElementsAsync(IPluginContext context, string json)
    {
        List<string> eids = new();
        List<string>? newNames = null;
        string? pattern = null;
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            eids = d.RootElement.GetProperty("elementIds").EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
            if (d.RootElement.TryGetProperty("newNames", out var nn) && nn.ValueKind == JsonValueKind.Array) newNames = nn.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
            if (d.RootElement.TryGetProperty("newNamePattern", out var pat) && pat.ValueKind == JsonValueKind.String) pattern = pat.GetString();
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Failed($"Invalid args: {ex.Message}", ToolWriteCertainty.NotCommitted);
        }
        if (eids.Count == 0)
            return ToolExecutionResult.Failed("Provide elementIds.", ToolWriteCertainty.NotCommitted);
        if (newNames != null && newNames.Count != eids.Count)
            return ToolExecutionResult.Failed("newNames length must match elementIds.", ToolWriteCertainty.NotCommitted);
        if (newNames == null && string.IsNullOrWhiteSpace(pattern))
            return ToolExecutionResult.Failed("Provide newNames or newNamePattern.", ToolWriteCertainty.NotCommitted);
        var result = await context.Bridge.RenameElementsAsync(eids, newNames, pattern);
        return WriteResult(result);
    }

    internal static ToolEntry CreateApplyConstructionDates(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "apply_construction_dates",
                Description = "Bulk apply Construction Date (or any text param) bottom-up by level elevation in ONE transaction. Provide startDate (YYYY-MM-DD or YYYYMMDD) and intervalDays. This only sets the parameter value. For coloring use color_by_construction_schedule. Handles all levels sorted low-high and all categories at once to avoid max rounds.",
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
        Handler = async json => (await ApplyConstructionDatesAsync(context, json)).Text,
        StructuredHandler = json => ApplyConstructionDatesAsync(context, json)
    };

    private static async Task<ToolExecutionResult> ApplyConstructionDatesAsync(IPluginContext context, string json)
    {
        string startDateStr = ""; int interval = 14; string targetParam = "Construction Date"; List<string>? cats = null;
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            startDateStr = d.RootElement.GetProperty("startDate").GetString() ?? "";
            if (d.RootElement.TryGetProperty("intervalDays", out var iv) && iv.ValueKind == JsonValueKind.Number) interval = iv.GetInt32();
            if (d.RootElement.TryGetProperty("targetParam", out var tp) && tp.ValueKind == JsonValueKind.String) targetParam = tp.GetString() ?? targetParam;
            if (d.RootElement.TryGetProperty("categories", out var ca) && ca.ValueKind == JsonValueKind.Array) cats = ca.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Failed($"Invalid args: {ex.Message}", ToolWriteCertainty.NotCommitted);
        }
        if (!TryParseDate(startDateStr, out _))
            return ToolExecutionResult.Failed($"Invalid startDate '{startDateStr}'. Use YYYY-MM-DD or YYYYMMDD.", ToolWriteCertainty.NotCommitted);
        interval = Polyfills.Clamp(interval, 1, 365);
        var result = await context.Bridge.ApplyConstructionDatesAsync(startDateStr, interval, targetParam, cats);
        return WriteResult(result);
    }

    internal static ToolEntry CreateExportParamsToExcel(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "export_params_to_excel",
                Description = "Export element parameters to CSV (Excel compatible) — pyRevit: Export/DiRoots. Params: category, paramNames (comma), filePath optional, limit. Numeric values are in mm / m² / m³ / degrees.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        category = new { type = "string" },
                        paramNames = new { type = "string", description = "Comma separated e.g. Comments,Mark,Level" },
                        filePath = new { type = "string" },
                        limit = new { type = "integer" }
                    },
                    required = new[] { "category" }
                }
            }
        },
        Handler = async json => (await ExportParamsToExcelAsync(context, json)).Text,
        StructuredHandler = json => ExportParamsToExcelAsync(context, json)
    };

    private static async Task<ToolExecutionResult> ExportParamsToExcelAsync(IPluginContext context, string json)
    {
        string cat = ""; string? paramNames = null; string? filePath = null; int lim = 200;
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            cat = d.RootElement.GetProperty("category").GetString() ?? "";
            if (d.RootElement.TryGetProperty("paramNames", out var pn) && pn.ValueKind == JsonValueKind.String) paramNames = pn.GetString();
            if (d.RootElement.TryGetProperty("filePath", out var fp) && fp.ValueKind == JsonValueKind.String) filePath = fp.GetString();
            if (d.RootElement.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number) lim = l.GetInt32();
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Failed($"Invalid args: {ex.Message}", ToolWriteCertainty.NoWrite);
        }
        lim = Polyfills.Clamp(lim, 1, 1000);
        string[] cols = string.IsNullOrWhiteSpace(paramNames) ? new[] { "Comments", "Mark" } : paramNames.SplitTrim(',');
        var dto = await context.Bridge.ExportParamsAsync(cat, cols, lim);
        if (dto == null)
            return ToolExecutionResult.Failed("Please open a model first.", ToolWriteCertainty.NoWrite);
        if (!string.IsNullOrEmpty(dto.Error))
            return ToolExecutionResult.Failed(dto.Error, ToolWriteCertainty.NoWrite);
        try
        {
            var path = ExportPaths.ResolveWrite(filePath, $"export_{cat}_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("Id," + string.Join(",", dto.Columns.Select(CsvText.Cell)));
            foreach (var row in dto.Rows)
            {
                var vals = new List<string> { row.Id };
                foreach (var v in row.Values)
                    vals.Add(CsvText.Cell(v));
                sb.AppendLine(string.Join(",", vals));
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            // L1: writing a file is not a model write, so this reports NoWrite.
            return ToolExecutionResult.Succeeded(
                $"Exported {dto.Rows.Count}{(dto.Total > dto.Rows.Count ? $" of {dto.Total}" : "")} {cat} to {path}\nColumns: {string.Join(", ", dto.Columns)}\nFirst 3:\n" + string.Join("\n", sb.ToString().Split('\n').Take(4)),
                ToolWriteCertainty.NoWrite);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Failed($"Failed: {ex.Message}", ToolWriteCertainty.NoWrite);
        }
    }

    internal static ToolEntry CreateImportExcelToParams(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "import_excel_to_params",
                Description = "Import CSV (Excel) to set parameters by Id — pyRevit: Import. Params: filePath, paramName. Numeric values are read in mm / m² / m³ / degrees.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string" },
                        paramName = new { type = "string" }
                    },
                    required = new[] { "filePath", "paramName" }
                }
            }
        },
        Handler = async json => (await ImportExcelToParamsAsync(context, json)).Text,
        StructuredHandler = json => ImportExcelToParamsAsync(context, json)
    };

    private static async Task<ToolExecutionResult> ImportExcelToParamsAsync(IPluginContext context, string json)
    {
        string fp = "", pn = "";
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            fp = d.RootElement.GetProperty("filePath").GetString() ?? "";
            pn = d.RootElement.GetProperty("paramName").GetString() ?? "";
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Failed($"Invalid args: {ex.Message}", ToolWriteCertainty.NotCommitted);
        }
        try { fp = ExportPaths.ResolveRead(fp); }
        catch (Exception ex) { return ToolExecutionResult.Failed(ex.Message, ToolWriteCertainty.NotCommitted); }
        if (!File.Exists(fp))
            return ToolExecutionResult.Failed($"File not found: {fp}", ToolWriteCertainty.NotCommitted);
        var lines = File.ReadAllLines(fp);
        if (lines.Length < 2)
            return ToolExecutionResult.Failed("File empty or no data rows", ToolWriteCertainty.NotCommitted);
        var header = lines[0].Split(',');
        int idIdx = 0; int valIdx = 1;
        for (int i = 0; i < header.Length; i++)
            if (header[i].Trim('"').Equals(pn, StringComparison.OrdinalIgnoreCase)) valIdx = i;
        var rows = new List<ParamImportRowDto>();
        for (int i = 1; i < lines.Length; i++)
        {
            var parts = SplitCsv(lines[i]);
            if (parts.Length < 2) continue;
            var id = idIdx < parts.Length ? parts[idIdx].Trim('"') : "";
            var val = valIdx < parts.Length ? parts[valIdx].Trim('"') : "";
            rows.Add(new ParamImportRowDto(id, val));
        }
        if (rows.Count == 0)
            return ToolExecutionResult.Failed("No rows parsed", ToolWriteCertainty.NotCommitted);
        var result = await context.Bridge.ImportParamsAsync(pn, rows, fp);
        return WriteResult(result);
    }

    internal static ToolEntry CreateCopyParams(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Describe = json => DescribeCopyParams(context, json),
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "copy_params",
                Description = "Copy parameter value from source param to target param for a category (pyRevit: Copy Param). Params: category, sourceParam, targetParam, limit. Length, area, volume and angle values are copied as numbers, not text.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        category = new { type = "string" },
                        sourceParam = new { type = "string" },
                        targetParam = new { type = "string" },
                        limit = new { type = "integer" }
                    },
                    required = new[] { "category", "sourceParam", "targetParam" }
                }
            }
        },
        Handler = async json => (await CopyParamsAsync(context, json)).Text,
        StructuredHandler = json => CopyParamsAsync(context, json)
    };

    private static async Task<ToolExecutionResult> CopyParamsAsync(IPluginContext context, string json)
    {
        string cat = "", src = "", tgt = ""; int lim = 200;
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            cat = d.RootElement.GetProperty("category").GetString() ?? "";
            src = d.RootElement.GetProperty("sourceParam").GetString() ?? "";
            tgt = d.RootElement.GetProperty("targetParam").GetString() ?? "";
            if (d.RootElement.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number) lim = l.GetInt32();
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Failed($"Invalid args: {ex.Message}", ToolWriteCertainty.NotCommitted);
        }
        lim = Polyfills.Clamp(lim, 1, 1000);
        var result = await context.Bridge.CopyParamsAsync(cat, src, tgt, lim);
        return WriteResult(result);
    }

    internal static ToolEntry CreateReplaceText(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Describe = json => DescribeReplaceText(context, json),
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "replace_text",
                Description = "Regex replace in parameter (pyRevit: Replace). Params: category, paramName, pattern, replacement, limit.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        category = new { type = "string" },
                        paramName = new { type = "string" },
                        pattern = new { type = "string" },
                        replacement = new { type = "string" },
                        limit = new { type = "integer" }
                    },
                    required = new[] { "category", "paramName", "pattern", "replacement" }
                }
            }
        },
        Handler = async json => (await ReplaceTextAsync(context, json)).Text,
        StructuredHandler = json => ReplaceTextAsync(context, json)
    };

    private static async Task<ToolExecutionResult> ReplaceTextAsync(IPluginContext context, string json)
    {
        string cat = "", pn = "", pat = "", rep = ""; int lim = 200;
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            cat = d.RootElement.GetProperty("category").GetString() ?? "";
            pn = d.RootElement.GetProperty("paramName").GetString() ?? "";
            pat = d.RootElement.GetProperty("pattern").GetString() ?? "";
            rep = d.RootElement.GetProperty("replacement").GetString() ?? "";
            if (d.RootElement.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number) lim = l.GetInt32();
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Failed($"Invalid args: {ex.Message}", ToolWriteCertainty.NotCommitted);
        }
        lim = Polyfills.Clamp(lim, 1, 1000);
        var result = await context.Bridge.ReplaceTextInParamAsync(cat, pn, pat, rep, lim);
        return WriteResult(result);
    }

    internal static ToolEntry CreateNumberingAuto(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Describe = json => DescribeNumbering(context, json),
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "numbering_auto",
                Description = "Number the Mark parameter. Uses the current selection, or a category when one is given. Left-to-right orders by position. A level name keeps only elements on that level. Dry-run lists old mark and new mark and writes nothing. Numbering every element of a category from the form requires All in this category.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        category = new { type = "string" },
                        prefix = new { type = "string" },
                        start = new { type = "integer" },
                        pad = new { type = "integer" },
                        order = new { type = "string", @enum = new[] { "document", "left-to-right" } },
                        levelName = new { type = "string" },
                        allInCategory = new { type = "boolean" },
                        dryRun = new { type = "boolean" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "category", Label = "Category (empty = selection)", Kind = PresetFieldKind.String },
            new PresetField { Id = "levelName", Label = "Level name", Kind = PresetFieldKind.String },
            new PresetField { Id = "order", Label = "Order", Kind = PresetFieldKind.Select, Options = new[] { "document", "left-to-right" }, Default = "document" },
            new PresetField { Id = "prefix", Label = "Prefix", Kind = PresetFieldKind.String },
            new PresetField { Id = "start", Label = "Start at", Kind = PresetFieldKind.Number, Default = 1 },
            new PresetField { Id = "pad", Label = "Digits", Kind = PresetFieldKind.Number, Default = 3 },
            new PresetField { Id = "allInCategory", Label = "All in this category", Kind = PresetFieldKind.Boolean, Default = false, Warning = "This numbers every element of the category, not just the selection." },
            new PresetField { Id = "dryRun", Label = "Dry-run (show old and new marks)", Kind = PresetFieldKind.Boolean, Default = false }
        },
        Handler = async json => (await NumberingAutoAsync(context, json)).Text,
        StructuredHandler = json => NumberingAutoAsync(context, json)
    };

    private static async Task<ToolExecutionResult> NumberingAutoAsync(IPluginContext context, string json)
    {
        string? cat = null; string pref = ""; int start = 1, pad = 3;
        string? order = null, levelName = null;
        bool? allInCategory = null;
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
            if (d.RootElement.TryGetProperty("prefix", out var p) && p.ValueKind == JsonValueKind.String) pref = p.GetString() ?? "";
            if (d.RootElement.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.Number) start = s.GetInt32();
            if (d.RootElement.TryGetProperty("pad", out var pa) && pa.ValueKind == JsonValueKind.Number) pad = pa.GetInt32();
            if (d.RootElement.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.String) order = o.GetString();
            if (d.RootElement.TryGetProperty("levelName", out var lv) && lv.ValueKind == JsonValueKind.String) levelName = lv.GetString();
            if (d.RootElement.TryGetProperty("allInCategory", out var all) && all.ValueKind is JsonValueKind.True or JsonValueKind.False)
                allInCategory = all.GetBoolean();
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(cat) && allInCategory == false)
            return ToolExecutionResult.Failed(
                $"This would number every {cat}. Turn on All in this category, or clear the category and use the selection.",
                ToolWriteCertainty.NotCommitted);
        var dryRun = ToolPresets.IsDryRun(json);
        var result = await context.Bridge.NumberElementsAsync(cat, pref, start, pad, order, levelName, dryRun);
        if (dryRun)
            return result.Ok
                ? ToolExecutionResult.Succeeded(result.Message, ToolWriteCertainty.NoWrite)
                : ToolExecutionResult.Failed(result.Message, ToolWriteCertainty.NotCommitted);
        return WriteResult(result);
    }

    private static JsonDocument? ParseArgs(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch { return null; }
    }

    private static string? ArgStr(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int ArgInt(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

    private static string Sample(IEnumerable<string> lines)
        => string.Join("\n", lines.Take(3).Select(l => "  " + l));

    private static async Task<string?> DescribeSetParameterById(IPluginContext context, string json)
    {
        using var doc = ParseArgs(json);
        if (doc == null) return null;
        var eid = ArgStr(doc.RootElement, "elementId");
        var param = ArgStr(doc.RootElement, "paramName");
        var value = ArgStr(doc.RootElement, "value");
        if (string.IsNullOrWhiteSpace(eid) || string.IsNullOrWhiteSpace(param)) return null;
        var details = await context.Bridge.GetElementDetailsAsync(eid!);
        if (details == null || !string.IsNullOrEmpty(details.Error)) return null;
        var current = details.Parameters
            .FirstOrDefault(p => p.Name.Equals(param, StringComparison.OrdinalIgnoreCase))?.Value;
        var change = current == null ? $"{param} = \"{value}\"" : $"{param}: \"{current}\" → \"{value}\"";
        return $"Set a parameter on {details.Category} \"{details.Name}\" (id {details.Id}):\n{change}\nUndo with Ctrl+Z.";
    }

    private static async Task<string?> DescribeSetComments(IPluginContext context, string json)
    {
        using var doc = ParseArgs(json);
        var text = doc == null ? null : ArgStr(doc.RootElement, "text");
        var sel = await context.Bridge.GetSelectionSummaryAsync();
        if (sel.ElementCount == 0) return null;
        var cats = string.Join(", ", sel.CategoryNames.Take(4));
        return $"Set Comments on {sel.ElementCount} selected element(s) ({cats}) to:\n\"{text}\"\n"
               + "Existing Comments on them are overwritten. Undo with Ctrl+Z.";
    }

    private static async Task<string?> DescribeCopyParams(IPluginContext context, string json)
    {
        using var doc = ParseArgs(json);
        if (doc == null) return null;
        var r = doc.RootElement;
        var cat = ArgStr(r, "category");
        var src = ArgStr(r, "sourceParam");
        var tgt = ArgStr(r, "targetParam");
        if (string.IsNullOrWhiteSpace(cat) || string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt)) return null;
        var limit = Polyfills.Clamp(ArgInt(r, "limit", 200), 1, 1000);
        var dto = await context.Bridge.ExportParamsAsync(cat!, new[] { src!, tgt! }, limit);
        if (dto == null || !string.IsNullOrEmpty(dto.Error)) return null;
        var changes = dto.Rows
            .Where(row => row.Values.Count == 2 && row.Values[0].Length > 0 && row.Values[0] != row.Values[1])
            .ToList();
        var more = dto.Total > dto.Rows.Count
            ? $" Only the first {dto.Rows.Count} of {dto.Total} {cat} are processed." : "";
        return $"Copy {src} → {tgt} on {cat}: {changes.Count} of {dto.Rows.Count} values would change.{more}\n"
               + Sample(changes.Select(c => $"{c.Id}: \"{c.Values[1]}\" → \"{c.Values[0]}\"")) + "\nUndo with Ctrl+Z.";
    }

    private static async Task<string?> DescribeReplaceText(IPluginContext context, string json)
    {
        using var doc = ParseArgs(json);
        if (doc == null) return null;
        var r = doc.RootElement;
        var cat = ArgStr(r, "category");
        var param = ArgStr(r, "paramName");
        var pattern = ArgStr(r, "pattern");
        var replacement = ArgStr(r, "replacement") ?? "";
        if (string.IsNullOrWhiteSpace(cat) || string.IsNullOrWhiteSpace(param)) return null;
        if (!SafeRegex.TryCreate(pattern, out var re, out var reError))
            return $"The pattern cannot run: {reError}";
        var limit = Polyfills.Clamp(ArgInt(r, "limit", 200), 1, 1000);
        var dto = await context.Bridge.ExportParamsAsync(cat!, new[] { param! }, limit);
        if (dto == null || !string.IsNullOrEmpty(dto.Error)) return null;
        var changes = new List<(string Id, string Old, string New)>();
        try
        {
            foreach (var row in dto.Rows)
            {
                var old = row.Values.Count > 0 ? row.Values[0] : "";
                var next = re!.Replace(old, replacement);
                if (old != next) changes.Add((row.Id, old, next));
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return $"The pattern /{pattern}/ is too slow and will be refused. Use a simpler pattern.";
        }
        var more = dto.Total > dto.Rows.Count
            ? $" Only the first {dto.Rows.Count} of {dto.Total} {cat} are processed." : "";
        return $"Replace /{pattern}/ with \"{replacement}\" in {param} on {cat}: {changes.Count} of {dto.Rows.Count} values would change.{more}\n"
               + Sample(changes.Select(c => $"{c.Id}: \"{c.Old}\" → \"{c.New}\"")) + "\nUndo with Ctrl+Z.";
    }

    private static async Task<string?> DescribeNumbering(IPluginContext context, string json)
    {
        using var doc = ParseArgs(json);
        if (doc == null) return null;
        var r = doc.RootElement;
        var cat = ArgStr(r, "category");
        var prefix = ArgStr(r, "prefix") ?? "";
        var start = ArgInt(r, "start", 1);
        var pad = Math.Max(0, ArgInt(r, "pad", 3));
        int count;
        string scope;
        if (!string.IsNullOrWhiteSpace(cat))
        {
            var found = await context.Bridge.FindElementsFilteredAsync(cat!, null, null, null, 1, 0);
            if (!string.IsNullOrEmpty(found.Error)) return null;
            count = found.Total;
            scope = cat!;
        }
        else
        {
            var sel = await context.Bridge.GetSelectionSummaryAsync();
            count = sel.ElementCount;
            scope = "selected element(s)";
        }
        if (count == 0) return null;
        string Mark(int n) => prefix + n.ToString().PadLeft(pad, '0');
        return $"Number {count} {scope}: Mark {Mark(start)} … {Mark(start + count - 1)}.\n"
               + "Existing Marks are overwritten, in Revit's element order (not click order). Undo with Ctrl+Z.";
    }

    private static async Task<string?> DescribeBatchSet(IPluginContext context, string json)
    {
        using var doc = ParseArgs(json);
        if (doc == null) return null;
        var r = doc.RootElement;
        var cat = ArgStr(r, "category");
        var target = ArgStr(r, "targetParam");
        var value = ArgStr(r, "targetValue");
        var filterParam = ArgStr(r, "filterParam");
        var filterOp = ArgStr(r, "filterOp") ?? "=";
        var filterValue = ArgStr(r, "filterValue");
        if (string.IsNullOrWhiteSpace(cat) || string.IsNullOrWhiteSpace(target)) return null;
        var limit = Polyfills.Clamp(ArgInt(r, "limit", 200), 1, 1000);
        var found = await context.Bridge.FindElementsFilteredAsync(cat!, filterParam, filterOp, filterValue, limit, 0);
        if (!string.IsNullOrEmpty(found.Error)) return null;
        var filter = string.IsNullOrWhiteSpace(filterParam) ? "" : $" where {filterParam} {filterOp} \"{filterValue}\"";
        var rest = found.Total > limit ? $" The other {found.Total - limit} are not touched." : "";
        return $"Set {target} = \"{value}\" on {Math.Min(found.Total, limit)} of {found.Total} {cat}{filter}.{rest}\nUndo with Ctrl+Z.";
    }

    /// <summary>
    /// Status comes from CommandResultDto, whose Ok and AffectedCount most handlers discarded in
    /// favour of result.Message, so FromLegacy could only guess from the prose. Ok=false covers
    /// every refusal and wrote nothing, so it is Failed + NotCommitted and forces a turn rollback.
    /// Ok=true is a commit with the affected count as ChangedCount.
    /// </summary>
    private static ToolExecutionResult WriteResult(CommandResultDto result)
        => result.Ok
            ? ToolExecutionResult.Succeeded(result.Message, ToolWriteCertainty.Committed, result.AffectedCount)
            : ToolExecutionResult.Failed(result.Message, ToolWriteCertainty.NotCommitted);

    private static string[] SplitCsv(string line)
    {
        var res = new List<string>();
        var cur = new StringBuilder();
        bool inQ = false;
        foreach (var ch in line)
        {
            if (ch == '"') inQ = !inQ;
            else if (ch == ',' && !inQ) { res.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        res.Add(cur.ToString());
        return res.ToArray();
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
