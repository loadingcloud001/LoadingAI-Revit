using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
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
    public string Version => "1.0.0";

    public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
    {
        yield return LoadFamily(context);
        yield return PlaceFamilyInstance(context);
        yield return ChangeElementType(context);
        yield return SetParameter(context);
        yield return CreateTopography(context);
        yield return ListPhases(context);
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

    private static ToolEntry PlaceFamilyInstance(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "place_family_instance",
                Description = "Place a family instance at x,y,z (Creation.Document.NewFamilyInstance). Params: familyName or familySymbolId, x,y,z (feet), levelName/levelId optional.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        familyName = new { type = "string" },
                        familySymbolId = new { type = "string" },
                        x = new { type = "number" },
                        y = new { type = "number" },
                        z = new { type = "number" },
                        levelName = new { type = "string" },
                        levelId = new { type = "string" }
                    },
                    required = new[] { "x", "y", "z" }
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var famName = args.Str("familyName");
            var famSymId = args.Str("familySymbolId");
            var x = args.Dbl("x", 0);
            var y = args.Dbl("y", 0);
            var z = args.Dbl("z", 0);
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    FamilySymbol? symbol = ResolveSymbol(doc, famSymId, famName);
                    if (symbol == null) return "Family symbol not found. Pass familySymbolId or familyName.";
                    var level = ResolveLevel(doc, args.Str("levelId"), args.Str("levelName"));
                    if (level == null) return "No level found. Pass levelName or levelId.";
                    using var tx = new Transaction(doc, "LoadingAI for Revit: place family instance");
                    tx.Start();
                    if (!symbol.IsActive) symbol.Activate();
                    var inst = doc.Create.NewFamilyInstance(new XYZ(x, y, z), symbol, level, StructuralType.NonStructural);
                    tx.Commit();
                    return $"Placed instance {inst.Id} of \"{symbol.FamilyName}:{symbol.Name}\" at ({x:0.##},{y:0.##},{z:0.##}) on {level.Name}";
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

    private static ToolEntry SetParameter(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "set_parameter",
                Description = "Set a parameter on element(s). Params: elementId or elementIds, paramName, value.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        elementId = new { type = "string" },
                        elementIds = new { type = "array", items = new { type = "string" } },
                        paramName = new { type = "string" },
                        value = new { type = "string" }
                    },
                    required = new[] { "paramName", "value" }
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var paramName = args.Str("paramName");
            var value = args.Str("value");
            if (string.IsNullOrWhiteSpace(paramName) || value == null) return "paramName and value are required.";
            var ids = args.Ids("elementId", "elementIds");
            if (ids.Count == 0) return "Pass elementId or elementIds.";
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    using var tx = new Transaction(doc, "LoadingAI for Revit: set parameter");
                    tx.Start();
                    var ok = 0; var skip = 0; var errors = new List<string>();
                    foreach (var id in ids)
                    {
                        var el = doc.GetElement(id);
                        if (el == null) { skip++; continue; }
                        if (RevitParse.TrySetParameter(el, paramName!, value!, out var err)) ok++;
                        else { skip++; errors.Add(err); }
                    }
                    tx.Commit();
                    var msg = $"Set parameter \"{paramName}\"={value} on {ok} element(s), skipped {skip}.";
                    if (errors.Count > 0) msg += " Issues: " + string.Join("; ", errors.Take(5));
                    return msg;
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
                Description = "Create a toposolid (ground) from points (Toposolid.Create). Params: points array of [x,y,z] (min 3 points, feet). levelName/levelId optional.",
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
            var pts = args.Points("points");
            if (pts.Count < 3) return "Provide at least 3 points [x,y,z].";
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

    private static ToolEntry ListPhases(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "list_phases",
                Description = "List project phases (id, name). Params: none.",
                Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
            }
        },
        Handler = async json => await context.FullAccess!.RunRevitAsync(session =>
        {
            var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
            if (doc == null) return "Please open a model first.";
            try
            {
                var sb = new StringBuilder();
                var phases = doc.Phases.Cast<Phase>().ToList();
                sb.AppendLine($"Phases ({phases.Count}):");
                foreach (var ph in phases)
                    sb.AppendLine($"  {ph.Id} \"{ph.Name}\"");
                return sb.ToString();
            }
            catch (Exception ex) { return $"Failed: {ex.Message}"; }
        })
    };

    private static FamilySymbol? ResolveSymbol(Document doc, string? symbolId, string? familyName)
    {
        if (!string.IsNullOrWhiteSpace(symbolId))
        {
            var s = doc.GetElement(RevitParse.ParseEid(symbolId)) as FamilySymbol;
            if (s != null) return s;
        }
        if (!string.IsNullOrWhiteSpace(familyName))
        {
            var fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));
            if (fam != null)
            {
                var first = fam.GetFamilySymbolIds().FirstOrDefault();
                if (first != null) return doc.GetElement(first) as FamilySymbol;
            }
        }
        return null;
    }

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

        public double Dbl(string name, double fallback)
        {
            if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
            return fallback;
        }

        public List<ElementId> Ids(string single, string many)
        {
            var list = new List<ElementId>();
            if (_root is not { } r) return list;
            if (r.TryGetProperty(single, out var sv) && sv.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sv.GetString()))
                list.Add(RevitParse.ParseEid(sv.GetString()!));
            if (r.TryGetProperty(many, out var mv) && mv.ValueKind == JsonValueKind.Array)
                foreach (var item in mv.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        list.Add(RevitParse.ParseEid(item.GetString()!));
            return list;
        }

        public List<XYZ> Points(string name)
        {
            var list = new List<XYZ>();
            if (_root is not { } r || !r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array) continue;
                var nums = item.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Number).Select(n => n.GetDouble()).ToList();
                if (nums.Count >= 3) list.Add(new XYZ(nums[0], nums[1], nums[2]));
            }
            return list;
        }
    }
}
