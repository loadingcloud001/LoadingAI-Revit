using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.ExportTools;

public sealed class ExportToolsPlugin : IRevitToolPlugin, IRevitFullAccess
{
    public string Id => "export-tools";
    public string Name => "Export Tools";
    public string Version => "1.0.0";
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(10);

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
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    var dir = ExportPaths.ResolveDirectory(folder);
                    var views = ResolvePrintable(doc, ids, preferSheets: true);
                    if (views.Count == 0) return "No printable sheets/views to export.";
                    var options = new PDFExportOptions
                    {
                        FileName = ExportPaths.SanitizeFileName(name, doc.Title),
                        Combine = combine
                    };
                    var ok = doc.Export(dir, views, options);
                    if (!ok) return $"PDF export failed ({views.Count} views) to {dir}.";
                    var dest = combine
                        ? Path.Combine(dir, options.FileName + ".pdf")
                        : dir;
                    return $"Exported {views.Count} sheet(s)/view(s) to PDF: {dest}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }, ExportTimeout);
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
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    var dir = ExportPaths.ResolveDirectory(folder);
                    var views = ResolvePrintable(doc, ids, preferSheets: false);
                    if (views.Count == 0) return "No printable views to export.";
                    var fname = ExportPaths.SanitizeFileName(name, doc.Title);
                    var options = new DWGExportOptions { FileVersion = ACADVersion.R2018 };
                    var ok = doc.Export(dir, fname, views, options);
                    if (!ok) return $"DWG export failed ({views.Count} views) to {dir}.";
                    return $"Exported {views.Count} view(s) to DWG in {dir} (prefix \"{fname}\")";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }, ExportTimeout);
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
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    var dir = ExportPaths.ResolveDirectory(folder);
                    var fname = ExportPaths.SanitizeFileName(name, doc.Title);
                    var options = new IFCExportOptions();
                    if (!string.IsNullOrWhiteSpace(version) && Enum.TryParse<IFCVersion>(version, true, out var ver))
                        options.FileVersion = ver;
                    var path = Path.Combine(dir, fname + ".ifc");
                    DebugLog.Write("export_ifc.start", new
                    {
                        dir,
                        fname,
                        version,
                        title = doc.Title,
                        inTransaction = doc.IsModifiable
                    });
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var ok = doc.Export(dir, fname, options);
                    sw.Stop();
                    DebugLog.Write("export_ifc.end", new { ok, path, elapsedMs = sw.ElapsedMilliseconds });
                    if (!ok) return $"IFC export failed to {path}.";
                    return $"Exported IFC: {path}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }, ExportTimeout);
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
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    var unused = doc.GetUnusedElements(new HashSet<ElementId>());
                    if (unused == null || unused.Count == 0) return "Nothing to purge.";
                    if (preview)
                    {
                        var sample = unused.Take(15).Select(id =>
                        {
                            var el = doc.GetElement(id);
                            if (el == null) return id.ToString();
                            var cat = el.Category?.Name ?? "(no category)";
                            return $"{cat}/{el.Name} ({id})";
                        });
                        return $"Purge preview: {unused.Count} unused elements that can be deleted.\nSample:\n{string.Join("\n", sample)}\nRun with preview:false to delete.";
                    }

                    int deleted = 0, passes = 0;
                    using var tx = new Transaction(doc, "LoadingAI for Revit: purge unused");
                    tx.Start();
                    for (var i = 0; i < 8; i++)
                    {
                        var batch = doc.GetUnusedElements(new HashSet<ElementId>());
                        if (batch == null || batch.Count == 0) break;
                        doc.Delete(batch);
                        deleted += batch.Count;
                        passes++;
                    }
                    tx.Commit();
                    return $"Purged {deleted} unused element(s) in {passes} pass(es).";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    private static List<ElementId> ResolvePrintable(Document doc, List<string> ids, bool preferSheets)
    {
        var result = new List<ElementId>();
        if (ids.Count > 0)
        {
            foreach (var s in ids)
            {
                try
                {
                    var view = doc.GetElement(RevitParse.ParseEid(s)) as View;
                    if (view != null && IsExportable(view)) result.Add(view.Id);
                }
                catch { }
            }
            return result;
        }

        if (preferSheets)
        {
            result.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder && IsExportable(s))
                .Select(s => s.Id));
            if (result.Count > 0) return result;
        }

        var active = doc.ActiveView;
        if (active != null && IsExportable(active)) result.Add(active.Id);
        return result;
    }

    private static bool IsExportable(View view)
        => !view.IsTemplate && view.CanBePrinted;

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
