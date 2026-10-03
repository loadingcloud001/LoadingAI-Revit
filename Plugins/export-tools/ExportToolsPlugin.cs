using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.ExportTools;

public sealed class ExportToolsPlugin : IRevitToolPlugin
{
    public string Id => "export-tools";
    public string Name => "Export Tools";
    public string Version => "1.0.1";

    public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
    {
        yield return ExportPdf(context);
        yield return ExportDwg(context);
        yield return ExportIfc(context);
        yield return PurgeUnused(context);
    }

    private static ToolEntry ExportPdf(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "export_pdf",
                Description = "Export sheets/views to PDF (DiRoots). Params: sheetIds array (from get_sheets_views); leaving it out exports EVERY sheet and can block Revit for minutes, so that also needs confirmAll=true. folder, name, combine (default true).",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        sheetIds = new { type = "array", items = new { type = "string" } },
                        folder = new { type = "string" },
                        name = new { type = "string" },
                        combine = new { type = "boolean" },
                        confirmAll = new { type = "boolean", description = "Required to export every sheet when sheetIds is empty." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json => (await ExportPdfAsync(context, json)).Text,
        StructuredHandler = json => ExportPdfAsync(context, json)
    };

    private static async Task<ToolExecutionResult> ExportPdfAsync(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var ids = args.Strings("sheetIds");
        var folder = args.Str("folder");
        var name = args.Str("name");
        var combine = args.Bool("combine", true);
        string dir;
        try { dir = ExportPaths.ResolveDirectory(folder); }
        catch (Exception ex) { return ExportFailed($"Failed: {ex.Message}"); }
        if (ids.Count == 0 && !args.Bool("confirmAll", false))
            return ExportFailed("export_pdf without sheetIds would export every sheet and block Revit until it finishes (up to 10 minutes). Pass sheetIds (see get_sheets_views), or confirmAll=true to export all sheets.");
        var result = await context.Bridge.ExportPdfAsync(dir, ids.Count == 0 ? null : ids, name, combine);
        return FromExport(result);
    }

    private static ToolEntry ExportDwg(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "export_dwg",
                Description = "Export views/sheets to DWG (pyRevit). Params: viewIds array optional (else active view), folder, name.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        viewIds = new { type = "array", items = new { type = "string" } },
                        folder = new { type = "string" },
                        name = new { type = "string" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json => (await ExportDwgAsync(context, json)).Text,
        StructuredHandler = json => ExportDwgAsync(context, json)
    };

    private static async Task<ToolExecutionResult> ExportDwgAsync(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var ids = args.Strings("viewIds");
        var folder = args.Str("folder");
        var name = args.Str("name");
        string dir;
        try { dir = ExportPaths.ResolveDirectory(folder); }
        catch (Exception ex) { return ExportFailed($"Failed: {ex.Message}"); }
        var result = await context.Bridge.ExportDwgAsync(dir, ids.Count == 0 ? null : ids, name);
        return FromExport(result);
    }

    private static ToolEntry ExportIfc(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "export_ifc",
                Description = "Export IFC to folder (BIM Interoperability). Params: folder, name optional, version optional (e.g. IFC2x3CV2, IFC4RV).",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        folder = new { type = "string" },
                        name = new { type = "string" },
                        version = new { type = "string" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json => (await ExportIfcAsync(context, json)).Text,
        StructuredHandler = json => ExportIfcAsync(context, json)
    };

    private static async Task<ToolExecutionResult> ExportIfcAsync(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var folder = args.Str("folder");
        var name = args.Str("name");
        var version = args.Str("version");
        string dir;
        try { dir = ExportPaths.ResolveDirectory(folder); }
        catch (Exception ex) { return ExportFailed($"Failed: {ex.Message}"); }
        var result = await context.Bridge.ExportIfcAsync(dir, name, version);
        return FromExport(result);
    }

    private static ToolEntry PurgeUnused(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "purge_unused",
                Description = "Purge unused families/types (pyRevit: Purge). Uses Document.GetUnusedElements. Params: preview bool (default true).",
                Parameters = new
                {
                    type = "object",
                    properties = new { preview = new { type = "boolean" } },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json => (await PurgeUnusedAsync(context, json)).Text,
        StructuredHandler = json => PurgeUnusedAsync(context, json)
    };

    private static async Task<ToolExecutionResult> PurgeUnusedAsync(IPluginContext context, string json)
    {
        var preview = Args.Parse(json).Bool("preview", true);
        var result = await context.Bridge.PurgeUnusedAsync(preview);

        // Error is checked first, matching ADR-0006. DeletedCount is authoritative over
        // the error text: a purge that already removed elements in an earlier pass is a
        // partial write, not a clean failure, and must not be rolled back as one.
        if (result.Error != null)
            return result.DeletedCount > 0
                ? ToolExecutionResult.Partial(result.Error, ToolWriteCertainty.Committed, result.DeletedCount, 1)
                : ToolExecutionResult.Failed(result.Error, ToolWriteCertainty.NotCommitted);

        if (result.UnusedCount == 0)
            return ToolExecutionResult.Succeeded("Nothing to purge.", ToolWriteCertainty.NoWrite);

        if (result.Preview)
        {
            // A preview never opens a transaction, so it can never be mistaken for a write.
            var text = $"Purge preview: {result.UnusedCount} unused elements that can be deleted.\n"
                     + $"Sample:\n{string.Join("\n", result.Sample)}\n"
                     + "Run with preview:false to delete.";
            return ToolExecutionResult.Succeeded(text, ToolWriteCertainty.NoWrite);
        }

        var report = $"Purged {result.DeletedCount} unused element(s) in {result.Passes} pass(es).";
        return result.DeletedCount > 0
            ? ToolExecutionResult.Succeeded(report, ToolWriteCertainty.Committed, result.DeletedCount)
            : ToolExecutionResult.Succeeded(report, ToolWriteCertainty.NoWrite);
    }

    /// <summary>
    /// An export writes files, never the Revit model, so it is always
    /// <see cref="ToolWriteCertainty.NoWrite"/> whatever the outcome. That keeps a failed
    /// export from being read as a model write that needs rolling back.
    /// </summary>
    private static ToolExecutionResult FromExport(CommandResultDto result)
        => result.Ok
            ? ToolExecutionResult.Succeeded(result.Message, ToolWriteCertainty.NoWrite, result.AffectedCount)
            : ToolExecutionResult.Failed(result.Message, ToolWriteCertainty.NoWrite, failedCount: 1);

    private static ToolExecutionResult ExportFailed(string text)
        => ToolExecutionResult.Failed(text, ToolWriteCertainty.NoWrite);

    private readonly struct Args
    {
        private readonly JsonElement? _root;
        private Args(JsonElement? root) => _root = root;

        public static Args Parse(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return new Args(null);
                using var doc = JsonDocument.Parse(json);
                return new Args(doc.RootElement.Clone());
            }
            catch { return new Args(null); }
        }

        public string? Str(string name)
        {
            if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }

        public bool Bool(string name, bool fallback)
        {
            if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return v.GetBoolean();
            return fallback;
        }

        public List<string> Strings(string name)
        {
            var list = new List<string>();
            if (_root is not { } r || !r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
                else if (item.ValueKind == JsonValueKind.Number)
                    list.Add(item.ToString());
            }
            return list;
        }
    }
}
