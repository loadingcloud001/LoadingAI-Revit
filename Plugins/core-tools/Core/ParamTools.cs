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
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    eids = d.RootElement.GetProperty("elementIds").EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("newNames", out var nn) && nn.ValueKind == JsonValueKind.Array) newNames = nn.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("newNamePattern", out var pat) && pat.ValueKind == JsonValueKind.String) pattern = pat.GetString();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if (eids.Count == 0) return "Provide elementIds.";
                if (newNames != null && newNames.Count != eids.Count) return "newNames length must match elementIds.";
                if (newNames == null && string.IsNullOrWhiteSpace(pattern)) return "Provide newNames or newNamePattern.";
                var result = await context.Bridge.RenameElementsAsync(eids, newNames, pattern);
                return result.Message;
            }
        };
    }

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
            Handler = async json =>
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
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if (!TryParseDate(startDateStr, out _)) return $"Invalid startDate '{startDateStr}'. Use YYYY-MM-DD or YYYYMMDD.";
                interval = Polyfills.Clamp(interval, 1, 365);
                var result = await context.Bridge.ApplyConstructionDatesAsync(startDateStr, interval, targetParam, cats);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateExportParamsToExcel(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "export_params_to_excel",
                    Description = "Export element parameters to CSV (Excel compatible) — pyRevit: Export/DiRoots. Params: category, paramNames (comma), filePath optional, limit.",
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
            Handler = async json =>
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
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                lim = Polyfills.Clamp(lim, 1, 1000);
                string[] cols = string.IsNullOrWhiteSpace(paramNames) ? new[] { "Comments", "Mark" } : paramNames.SplitTrim(',');
                var dto = await context.Bridge.ExportParamsAsync(cat, cols, lim);
                if (dto == null) return "Please open a model first.";
                if (!string.IsNullOrEmpty(dto.Error)) return dto.Error;
                try
                {
                    var path = ExportPaths.ResolveWrite(filePath, $"export_{cat}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                    var sb = new StringBuilder();
                    sb.AppendLine("Id," + string.Join(",", dto.Columns));
                    foreach (var row in dto.Rows)
                    {
                        var vals = new List<string> { row.Id };
                        foreach (var v in row.Values)
                            vals.Add($"\"{(v ?? "").Replace("\"", "\"\"")}\"");
                        sb.AppendLine(string.Join(",", vals));
                    }
                    File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                    return $"Exported {dto.Rows.Count} {cat} to {path}\nColumns: {string.Join(", ", dto.Columns)}\nFirst 3:\n" + string.Join("\n", sb.ToString().Split('\n').Take(4));
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }
        };
    }

    internal static ToolEntry CreateImportExcelToParams(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "import_excel_to_params",
                    Description = "Import CSV (Excel) to set parameters by Id — pyRevit: Import. Params: filePath, paramName.",
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
            Handler = async json =>
            {
                string fp = "", pn = "";
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    fp = d.RootElement.GetProperty("filePath").GetString() ?? "";
                    pn = d.RootElement.GetProperty("paramName").GetString() ?? "";
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                try { fp = ExportPaths.ResolveRead(fp); } catch (Exception ex) { return ex.Message; }
                if (!File.Exists(fp)) return $"File not found: {fp}";
                var lines = File.ReadAllLines(fp);
                if (lines.Length < 2) return "File empty or no data rows";
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
                if (rows.Count == 0) return "No rows parsed";
                var result = await context.Bridge.ImportParamsAsync(pn, rows, fp);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateCopyParams(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "copy_params",
                    Description = "Copy parameter value from source param to target param for a category (pyRevit: Copy Param). Params: category, sourceParam, targetParam, limit.",
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
            Handler = async json =>
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
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                lim = Polyfills.Clamp(lim, 1, 1000);
                var result = await context.Bridge.CopyParamsAsync(cat, src, tgt, lim);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateReplaceText(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            RequiresWriteConfirm = true,
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
            Handler = async json =>
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
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                lim = Polyfills.Clamp(lim, 1, 1000);
                var result = await context.Bridge.ReplaceTextInParamAsync(cat, pn, pat, rep, lim);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateNumberingAuto(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "numbering_auto",
                    Description = "Auto number Mark by selection order or category (pyRevit: Auto Number). Params: category or use selection if omitted, prefix, start, pad.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            category = new { type = "string" },
                            prefix = new { type = "string" },
                            start = new { type = "integer" },
                            pad = new { type = "integer" }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                string? cat = null; string pref = ""; int start = 1, pad = 3;
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                    if (d.RootElement.TryGetProperty("prefix", out var p) && p.ValueKind == JsonValueKind.String) pref = p.GetString() ?? "";
                    if (d.RootElement.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.Number) start = s.GetInt32();
                    if (d.RootElement.TryGetProperty("pad", out var pa) && pa.ValueKind == JsonValueKind.Number) pad = pa.GetInt32();
                }
                catch { }
                var result = await context.Bridge.NumberElementsAsync(cat, pref, start, pad);
                return result.Message;
            }
        };
    }

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
