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
                Description = "Export sheets/views to PDF (DiRoots). Params: sheetIds array optional (else all sheets), folder, name, combine (default true).",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        sheetIds = new { type = "array", items = new { type = "string" } },
                        folder = new { type = "string" },
                        name = new { type = "string" },
                        combine = new { type = "boolean" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var ids = args.Strings("sheetIds");
            var folder = args.Str("folder");
            var name = args.Str("name");
            var combine = args.Bool("combine", true);
            string dir;
            try { dir = ExportPaths.ResolveDirectory(folder); }
            catch (Exception ex) { return $"Failed: {ex.Message}"; }
            var result = await context.Bridge.ExportPdfAsync(dir, ids.Count == 0 ? null : ids, name, combine);
            return result.Message;
        }
    };

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
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var ids = args.Strings("viewIds");
            var folder = args.Str("folder");
            var name = args.Str("name");
            string dir;
            try { dir = ExportPaths.ResolveDirectory(folder); }
            catch (Exception ex) { return $"Failed: {ex.Message}"; }
            var result = await context.Bridge.ExportDwgAsync(dir, ids.Count == 0 ? null : ids, name);
            return result.Message;
        }
    };

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
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var folder = args.Str("folder");
            var name = args.Str("name");
            var version = args.Str("version");
            string dir;
            try { dir = ExportPaths.ResolveDirectory(folder); }
            catch (Exception ex) { return $"Failed: {ex.Message}"; }
            var result = await context.Bridge.ExportIfcAsync(dir, name, version);
            return result.Message;
        }
    };

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
        Handler = async json =>
        {
            var preview = Args.Parse(json).Bool("preview", true);
            var result = await context.Bridge.PurgeUnusedAsync(preview);
            if (result.Error != null) return result.Error;
            if (result.UnusedCount == 0) return "Nothing to purge.";
            if (result.Preview)
            {
                return $"Purge preview: {result.UnusedCount} unused elements that can be deleted.\n"
                     + $"Sample:\n{string.Join("\n", result.Sample)}\n"
                     + "Run with preview:false to delete.";
            }
            return $"Purged {result.DeletedCount} unused element(s) in {result.Passes} pass(es).";
        }
    };

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
