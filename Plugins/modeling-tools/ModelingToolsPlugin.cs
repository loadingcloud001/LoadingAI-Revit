using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.ModelingTools;

public sealed class ModelingToolsPlugin : IRevitToolPlugin
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
        RequiresWriteConfirm = true,
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
            if (!File.Exists(path)) return $"File not found: {path}";
            var result = await context.Bridge.LoadFamilyAsync(path);
            return result.Message;
        }
    };

    private static ToolEntry ChangeElementType(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
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
            var result = await context.Bridge.ChangeElementTypeAsync(eid, typeName, typeId);
            return result.Message;
        }
    };

    private static ToolEntry CreateTopography(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
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
            var result = await context.Bridge.CreateTopographyAsync(pts, args.Str("levelName"), args.Str("levelId"));
            return result.Message;
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

        public List<PointMmDto> PointsMm(string name)
        {
            var list = new List<PointMmDto>();
            if (_root is not { } r || !r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array) continue;
                var nums = item.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Number).Select(n => n.GetDouble()).ToList();
                if (nums.Count >= 3)
                    list.Add(new PointMmDto(nums[0], nums[1], nums[2]));
            }
            return list;
        }
    }
}
