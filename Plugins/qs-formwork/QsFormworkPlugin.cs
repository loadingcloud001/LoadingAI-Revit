using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.QsFormwork;

public sealed class QsFormworkPlugin : IRevitToolPlugin, IRevitFullAccess
{
    public string Id => "qs-formwork";
    public string Name => "Formwork QS Tools";
    public string Version => "1.0.0";
    private static readonly TimeSpan GenerateTimeout = TimeSpan.FromMinutes(10);

    public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
    {
        yield return Setup(context);
        yield return CreatePanels(context);
        yield return Cleanup(context);
        yield return CopyParentParams(context);
    }

    private static ToolEntry Setup(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "formwork_setup",
                Description = "Bind formwork shared parameters on Generic Models and create the panel schema. Run once per project before generating panels.",
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
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    using var tx = new Transaction(doc, "Setup Formwork Parameters");
                    tx.Start();
                    FormworkCore.GetOrCreateSchema();
                    var (ok, fail, missing) = FormworkParameters.SetupSharedParameters(doc);
                    var panels = FormworkCore.CollectPanels(doc).Count;
                    var links = FormworkCore.LinkCount(doc);
                    tx.Commit();
                    var miss = missing.Count == 0 ? "none" : string.Join(", ", missing);
                    return $"Formwork setup complete. Linked models: {links}. Parameters bound: {ok}, failed: {fail} ({miss}). Existing panels: {panels}. Default thickness {FormworkConfig.SheetThicknessMmDefault:0} mm, coverage skip {FormworkConfig.CoverRatioDelete:P0}.";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    private static ToolEntry CreatePanels(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_formwork_panels",
                Description = "Create DirectShape formwork panels from selected linked (or host) structural elements. Params: elementIds optional (else current selection), thicknessMm default 18, faceTypes optional, collision default true, deleteExisting default false, includeUnselectedLinks default false.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        elementIds = new { type = "array", items = new { type = "string" } },
                        thicknessMm = new { type = "number" },
                        faceTypes = new { type = "array", items = new { type = "string" } },
                        collision = new { type = "boolean" },
                        deleteExisting = new { type = "boolean" },
                        includeUnselectedLinks = new { type = "boolean" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var ids = args.Strings("elementIds");
            if (ids.Count == 0)
            {
                var sel = await context.Bridge.GetSelectionSummaryAsync();
                ids = sel.ElementIds?.ToList() ?? new List<string>();
            }
            if (ids.Count == 0) return "Please select elements first.";
            var thickness = args.Dbl("thicknessMm", FormworkConfig.SheetThicknessMmDefault);
            var faceTypes = args.Strings("faceTypes");
            var collision = args.Bool("collision", true);
            var deleteExisting = args.Bool("deleteExisting", false);
            var includeUnselected = args.Bool("includeUnselectedLinks", false);
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    using var tx = new Transaction(doc, "LoadingAI for Revit: create formwork panels");
                    tx.Start();
                    var result = FormworkGenerator.Generate(doc, ids, new GenerateOptions
                    {
                        ThicknessMm = thickness,
                        FaceTypes = new HashSet<string>(faceTypes, StringComparer.OrdinalIgnoreCase),
                        Collision = collision,
                        DeleteExisting = deleteExisting,
                        IncludeUnselectedLinks = includeUnselected
                    });
                    tx.Commit();
                    var fails = result.FailReasons.Count == 0
                        ? ""
                        : " Failures: " + string.Join(", ", result.FailReasons.Select(kv => $"{kv.Key}={kv.Value}"));
                    return $"Formwork panels: selected {result.SelectedElements}, candidates {result.Candidates}, created {result.Created}, updated {result.Updated}, duplicates {result.Duplicates}, coverage skipped {result.CoverageSkipped}, panel overlap skipped {result.PanelOverlapSkipped}, trimmed {result.Trimmed}, obsolete deleted {result.DeletedObsolete}, failed {result.Failed}.{fails}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            }, GenerateTimeout);
        }
    };

    private static ToolEntry Cleanup(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "cleanup_formwork",
                Description = "Report or delete formwork panels. Params: action (stats, delete_all, delete_by_type, cleanup_orphans), faceType for delete_by_type.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { type = "string" },
                        faceType = new { type = "string" }
                    },
                    required = new[] { "action" }
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var action = (args.Str("action") ?? "stats").Trim().ToLowerInvariant();
            var faceType = args.Str("faceType");
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    var panels = FormworkCore.CollectPanels(doc);
                    if (action is "stats" or "")
                        return FormatStats(panels);

                    using var tx = new Transaction(doc, "LoadingAI for Revit: cleanup formwork");
                    tx.Start();
                    int n;
                    string msg;
                    switch (action)
                    {
                        case "delete_all":
                            n = FormworkCore.DeletePanels(doc, panels);
                            msg = $"Deleted {n} formwork panel(s).";
                            break;
                        case "delete_by_type":
                            if (string.IsNullOrWhiteSpace(faceType))
                            {
                                tx.RollBack();
                                return "faceType is required for delete_by_type.";
                            }
                            var match = panels.Where(p => string.Equals(FormworkCore.ParamText(p, "FW_PanelType"), faceType, StringComparison.OrdinalIgnoreCase)).ToList();
                            n = FormworkCore.DeletePanels(doc, match);
                            msg = $"Deleted {n} formwork panel(s) of type {faceType}.";
                            break;
                        case "cleanup_orphans":
                            var orphans = panels.Where(p => IsOrphan(doc, p)).ToList();
                            n = FormworkCore.DeletePanels(doc, orphans);
                            msg = $"Deleted {n} orphan formwork panel(s).";
                            break;
                        default:
                            tx.RollBack();
                            return "Unknown action. Use stats, delete_all, delete_by_type, or cleanup_orphans.";
                    }
                    tx.Commit();
                    return msg;
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    private static ToolEntry CopyParentParams(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "copy_formwork_parent_params",
                Description = "Copy named parameters from each panel's parent host onto FW_* instance parameters. Params: elementIds optional (else selected panels or all), paramNames required to copy (omit to list catalog).",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        elementIds = new { type = "array", items = new { type = "string" } },
                        paramNames = new { type = "array", items = new { type = "string" } }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var ids = args.Strings("elementIds");
            var names = args.Strings("paramNames");
            if (ids.Count == 0)
            {
                var sel = await context.Bridge.GetSelectionSummaryAsync();
                ids = sel.ElementIds?.ToList() ?? new List<string>();
            }
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    var all = FormworkCore.CollectPanels(doc);
                    var panels = ids.Count == 0
                        ? all
                        : ids.Select(id =>
                        {
                            try { return doc.GetElement(RevitParse.ParseEid(id)) as DirectShape; }
                            catch { return null; }
                        }).Where(ds => ds != null && string.Equals(ds.ApplicationId, FormworkConfig.AppId, StringComparison.Ordinal)).Cast<DirectShape>().ToList();
                    if (panels.Count == 0) return "No formwork panels found. Select panels or run create_formwork_panels first.";

                    if (names.Count == 0)
                        return Catalog(doc, panels);

                    using var tx = new Transaction(doc, "Copy Parent Params to Formwork");
                    tx.Start();
                    var destNames = names.Select(n => n.StartsWith("FW_", StringComparison.OrdinalIgnoreCase) ? n : "FW_" + n).ToList();
                    FormworkParameters.EnsureNamedParameters(doc, destNames);
                    var copied = 0;
                    var skippedEmpty = 0;
                    var skippedMismatch = 0;
                    var unresolved = 0;
                    var unloaded = 0;
                    foreach (var panel in panels)
                    {
                        var parent = FindParent(doc, panel, out var status);
                        if (status == "link_unloaded") { unloaded++; continue; }
                        if (parent == null) { unresolved++; continue; }
                        foreach (var name in names)
                        {
                            Parameter? src = null;
                            foreach (var p in FormworkParameters.IterCopyable(parent))
                            {
                                if (string.Equals(p.Definition?.Name, name, StringComparison.OrdinalIgnoreCase))
                                { src = p; break; }
                            }
                            if (src == null) continue;
                            if (FormworkParameters.IsEmpty(src)) { skippedEmpty++; continue; }
                            var destName = name.StartsWith("FW_", StringComparison.OrdinalIgnoreCase) ? name : "FW_" + name;
                            var dest = panel.LookupParameter(destName);
                            if (dest == null) continue;
                            if (src.StorageType != dest.StorageType) { skippedMismatch++; continue; }
                            if (FormworkParameters.CopyValue(src, dest)) copied++;
                        }
                    }
                    tx.Commit();
                    return $"Copied {copied} parent parameter value(s) onto {panels.Count} panel(s). Empty skipped {skippedEmpty}, type mismatch {skippedMismatch}, unresolved parent {unresolved}, unloaded link {unloaded}.";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    static string Catalog(Document doc, List<DirectShape> panels)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var panel in panels)
        {
            var parent = FindParent(doc, panel, out _);
            if (parent == null) continue;
            foreach (var p in FormworkParameters.IterCopyable(parent))
            {
                var nm = p.Definition?.Name;
                if (nm is { Length: > 0 }) names.Add(nm);
            }
            n++;
            if (n >= FormworkConfig.MaxParentsForCatalog) break;
        }
        if (names.Count == 0) return "No copyable parent parameters found. Pass paramNames to copy.";
        return "Copyable parent parameters (pass paramNames to copy onto FW_*): " + string.Join(", ", names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    static Element? FindParent(Document doc, DirectShape panel, out string status)
    {
        status = "";
        var uid = FormworkCore.ParamText(panel, "FW_ParentUniqueId");
        var linkName = FormworkCore.ParamText(panel, "FW_LinkName");
        var idText = FormworkCore.ParamText(panel, "FW_ParentElementId");
        if (string.IsNullOrWhiteSpace(idText))
        {
            var p = panel.LookupParameter("FW_ParentElementId");
            try { idText = p?.AsInteger().ToString(); }
            catch { idText = ""; }
        }

        Document target = doc;
        if (!string.IsNullOrWhiteSpace(linkName) && !string.Equals(linkName, doc.Title, StringComparison.OrdinalIgnoreCase))
        {
            RevitLinkInstance? found = null;
            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).WhereElementIsNotElementType();
            foreach (RevitLinkInstance link in links)
            {
                var ld = link.GetLinkDocument();
                var title = ld?.Title ?? link.Name;
                if (string.Equals(title, linkName, StringComparison.OrdinalIgnoreCase))
                { found = link; break; }
            }
            if (found == null) { status = "unresolved_parent"; return null; }
            var linked = found.GetLinkDocument();
            if (linked == null) { status = "link_unloaded"; return null; }
            target = linked;
        }

        if (!string.IsNullOrWhiteSpace(uid))
        {
            try
            {
                var byUid = target.GetElement(uid);
                if (byUid != null) return byUid;
            }
            catch { /* */ }
        }
        if (!string.IsNullOrWhiteSpace(idText))
        {
            try
            {
                var byId = target.GetElement(RevitParse.ParseEid(idText!));
                if (byId != null) return byId;
            }
            catch { /* */ }
        }
        status = "unresolved_parent";
        return null;
    }

    static bool IsOrphan(Document doc, DirectShape panel)
    {
        var parentId = panel.LookupParameter("FW_ParentElementId");
        try
        {
            if (parentId != null && parentId.AsInteger() == 0) return true;
        }
        catch { /* */ }
        var uid = FormworkCore.ParamText(panel, "FW_ParentUniqueId");
        var linkName = FormworkCore.ParamText(panel, "FW_LinkName");
        if (!string.IsNullOrWhiteSpace(linkName) && !string.Equals(linkName, doc.Title, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(uid))
        {
            try { return doc.GetElement(uid) == null; }
            catch { return true; }
        }
        var idText = "";
        try { idText = parentId?.AsInteger().ToString() ?? ""; }
        catch { /* */ }
        if (string.IsNullOrWhiteSpace(idText) || idText == "0") return true;
        try { return doc.GetElement(RevitParse.ParseEid(idText)) == null; }
        catch { return true; }
    }

    static string FormatStats(List<DirectShape> panels)
    {
        if (panels.Count == 0) return "No formwork panels in the model.";
        var byType = panels
            .GroupBy(p => FormworkCore.ParamText(p, "FW_PanelType") is { Length: > 0 } t ? t : "Unknown")
            .OrderBy(g => g.Key);
        var total = panels.Sum(p => FormworkCore.ParamArea(p, "FW_Area"));
        var net = panels.Sum(p =>
        {
            var n = FormworkCore.ParamArea(p, "FW_AreaNet");
            return n > 0 ? n : FormworkCore.ParamArea(p, "FW_Area");
        });
        var lines = new List<string>
        {
            $"Formwork panels: {panels.Count}. Gross {Metric.Area(total)}, net {Metric.Area(net)}."
        };
        foreach (var g in byType)
        {
            var a = g.Sum(p => FormworkCore.ParamArea(p, "FW_Area"));
            lines.Add($"  {g.Key}: {g.Count()} ({Metric.Area(a)})");
        }
        return string.Join(" ", lines);
    }

    private readonly struct Args
    {
        private readonly JsonElement? _root;
        private Args(JsonElement? root) => _root = root;

        public static Args Parse(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return new Args(null);
                using var d = JsonDocument.Parse(json);
                return new Args(d.RootElement.Clone());
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

        public double Dbl(string name, double fallback)
        {
            if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
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
                    if (s is { Length: > 0 }) list.Add(s);
                }
                else if (item.ValueKind == JsonValueKind.Number)
                    list.Add(item.ToString());
            }
            return list;
        }
    }
}
