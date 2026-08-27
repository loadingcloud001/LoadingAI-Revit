using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.ModelingTools;

public sealed class ModelingToolsPlugin : IRevitToolPlugin, IRevitFullAccess
{
    public string Id => "modeling-tools";
    public string Name => "Family & Element Modeling Tools";
    public string Version => "1.0.1";

    public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
    {
        yield return LoadFamily(context);
        yield return ChangeElementType(context);
        yield return CreateTopography(context);
    }

    private static ToolEntry LoadFamily(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "load_family",
                Description = "Load a .rfa family file into the model (Document.LoadFamily). Params: path (absolute .rfa path).",
                Parameters = new
                {
                    type = "object",
                    properties = new { path = new { type = "string" } },
                    required = new[] { "path" }
                }
            }
        },
        Handler = async json =>
        {
            var path = Args.Parse(json).Str("path");
            if (string.IsNullOrWhiteSpace(path)) return "path is required.";
            if (!System.IO.File.Exists(path)) return $"File not found: {path}";
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    using var tx = new Transaction(doc, "LoadingAI for Revit: load family");
                    tx.Start();
                    if (!doc.LoadFamily(path, out var family))
                    {
                        tx.RollBack();
                        return $"LoadFamily returned false for {path}";
                    }
                    tx.Commit();
                    var symbols = family!.GetFamilySymbolIds().Select(id => doc.GetElement(id) is FamilySymbol s ? s.Name : id.ToString()).ToList();
                    return $"Loaded family \"{family.Name}\" ({family.Id}) with {symbols.Count} symbol(s): {string.Join(", ", symbols)}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    private static ToolEntry ChangeElementType(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "change_element_type",
                Description = "Change an element's type to a matching type name (Element.ChangeTypeId). Params: elementId, typeName (or typeId).",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        elementId = new { type = "string" },
                        typeName = new { type = "string" },
                        typeId = new { type = "string" }
                    },
                    required = new[] { "elementId" }
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var eid = args.Str("elementId");
            var typeName = args.Str("typeName");
            var typeId = args.Str("typeId");
            if (string.IsNullOrWhiteSpace(eid)) return "elementId is required.";
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    var el = doc.GetElement(RevitParse.ParseEid(eid));
                    if (el == null) return $"Element {eid} not found.";
                    var valid = Element.GetValidTypes(doc, new List<ElementId> { el.Id });
                    ElementType? target = null;
                    foreach (var tid in valid)
                    {
                        var t = doc.GetElement(tid) as ElementType;
                        if (t == null) continue;
                        if (!string.IsNullOrWhiteSpace(typeId) && t.Id == RevitParse.ParseEid(typeId)) { target = t; break; }
                        if (!string.IsNullOrWhiteSpace(typeName) && t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)) { target = t; break; }
                    }
                    if (target == null)
                    {
                        var names = string.Join(", ", valid.Select(id => (doc.GetElement(id) as ElementType)?.Name).Where(n => n != null));
                        return $"No matching type. Valid types: {names}";
                    }
                    using var tx = new Transaction(doc, "LoadingAI for Revit: change element type");
                    tx.Start();
                    el.ChangeTypeId(target.Id);
                    tx.Commit();
                    return $"Changed element {el.Id} type to \"{target.Name}\" ({target.Id})";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    private static ToolEntry CreateTopography(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_topography",
                Description = "Create a toposolid (ground) from points (Toposolid.Create). Params: points array of [x,y,z] in mm (min 3). levelName/levelId optional.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        points = new { type = "array" },
                        levelName = new { type = "string" },
                        levelId = new { type = "string" }
                    },
                    required = new[] { "points" }
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var pts = args.PointsMm("points");
            if (pts.Count < 3) return "Provide at least 3 points [x,y,z] in mm.";
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    var level = ResolveLevel(doc, args.Str("levelId"), args.Str("levelName"));
                    if (level == null) return "No level found. Pass levelName or levelId.";
                    var typeId = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).FirstOrDefault()?.Id;
                    if (typeId == null) return "No ToposolidType found in the model.";
                    using var tx = new Transaction(doc, "LoadingAI for Revit: create toposolid");
                    tx.Start();
                    var surface = Toposolid.Create(doc, pts, level.Id, typeId);
                    tx.Commit();
                    return $"Created toposolid {surface.Id} from {pts.Count} points on {level.Name}.";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    private static Level? ResolveLevel(Document doc, string? levelId, string? levelName)
    {
        if (!string.IsNullOrWhiteSpace(levelId))
            return doc.GetElement(RevitParse.ParseEid(levelId)) as Level;
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
        if (!string.IsNullOrWhiteSpace(levelName))
            return levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
        return levels.OrderBy(l => l.Elevation).FirstOrDefault();
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

        public List<XYZ> PointsMm(string name)
        {
            var list = new List<XYZ>();
            if (_root is not { } r || !r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array) continue;
                var nums = item.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Number).Select(n => n.GetDouble()).ToList();
                if (nums.Count >= 3)
                    list.Add(new XYZ(Metric.MmToFt(nums[0]), Metric.MmToFt(nums[1]), Metric.MmToFt(nums[2])));
            }
            return list;
        }
    }
}
