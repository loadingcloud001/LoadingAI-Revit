using System.Text;
using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.ViewTools;

public sealed class ViewToolsPlugin : IRevitToolPlugin
{
    public string Id => "view-tools";
    public string Name => "View & Schedule Tools";
    public string Version => "1.0.1";

    public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
    {
        yield return CreateSchedule(context);
        yield return GetScheduleData(context);
        yield return CreateFloorPlan(context);
        yield return Create3dView(context);
        yield return CreateSectionView(context);
        yield return PathOfTravelTool(context);
    }

    private static ToolEntry CreateSchedule(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_schedule",
                Description = "Create a category schedule (ScheduleCreation). Params: category, name optional, fields array optional (else first instance fields).",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        category = new { type = "string" },
                        name = new { type = "string" },
                        fields = new { type = "array", items = new { type = "string" } }
                    },
                    required = new[] { "category" }
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var cat = args.Str("category");
            var name = args.Str("name");
            var fields = args.Strings("fields");
            if (string.IsNullOrWhiteSpace(cat)) return "category is required.";
            // The bridge takes one token: "<category>", or "<category>:<name>" for an explicit schedule name.
            var token = string.IsNullOrWhiteSpace(name) ? cat!.Trim() : $"{cat.Trim()}:{name.Trim()}";
            var result = await context.Bridge.CreateScheduleAsync(token, fields);
            return result == null ? "Please open a model first." : result.Error ?? result.Message;
        }
    };

    private static ToolEntry GetScheduleData(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "get_schedule_data",
                Description = "Read schedule table cells. Params: scheduleId or name, rowLimit (default 25, max 40).",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        scheduleId = new { type = "string" },
                        name = new { type = "string" },
                        rowLimit = new { type = "integer" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var sid = args.Str("scheduleId");
            var name = args.Str("name");
            var limit = args.Int("rowLimit", 0);
            var data = await context.Bridge.GetScheduleDataAsync(sid, name, limit);
            if (data == null) return "Please open a model first.";
            if (data.Error != null) return data.Error;
            var sb = new StringBuilder();
            sb.AppendLine($"{data.Name} ({data.Id}) rows={data.TotalRows} cols={data.TotalColumns} showing={data.ShownRows}");
            foreach (var row in data.Rows)
                sb.AppendLine(string.Join("\t", row));
            return sb.ToString();
        }
    };

    private static ToolEntry CreateFloorPlan(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_floor_plan",
                Description = "Create a floor plan view on a level. Params: levelId or levelName, name optional.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        levelId = new { type = "string" },
                        levelName = new { type = "string" },
                        name = new { type = "string" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var levelId = args.Str("levelId");
            var levelName = args.Str("levelName");
            var name = args.Str("name");
            // The bridge resolves a level by id first, then by name, then falls back to the lowest level.
            var levelToken = !string.IsNullOrWhiteSpace(levelId) ? levelId!.Trim() : (levelName ?? string.Empty).Trim();
            var result = await context.Bridge.CreateFloorPlanAsync(levelToken, (name ?? string.Empty).Trim());
            return result == null ? "Please open a model first." : result.Error ?? result.Message;
        }
    };

    private static ToolEntry Create3dView(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_3d_view",
                Description = "Create an isometric 3D view. Params: name optional.",
                Parameters = new
                {
                    type = "object",
                    properties = new { name = new { type = "string" } },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var name = Args.Parse(json).Str("name");
            // This tool has always created an isometric view; the bridge also offers perspective.
            var result = await context.Bridge.Create3DViewAsync((name ?? string.Empty).Trim(), true);
            return result == null ? "Please open a model first." : result.Error ?? result.Message;
        }
    };

    private static ToolEntry CreateSectionView(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_section_view",
                Description = "Create a section (CreateViewSection). Params: elementId of a wall/curve, or x1,y1,x2,y2 in mm. Optional name, height, depth in mm.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        elementId = new { type = "string" },
                        x1 = new { type = "number" },
                        y1 = new { type = "number" },
                        x2 = new { type = "number" },
                        y2 = new { type = "number" },
                        z = new { type = "number" },
                        name = new { type = "string" },
                        height = new { type = "number" },
                        depth = new { type = "number" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var eid = args.Str("elementId");
            var name = args.Str("name");
            // The bridge's section contract owns the box extents (3000 x 2500 mm, the former
            // defaults), so custom extents are rejected instead of being silently ignored.
            if ((args.HasNumber("height") && Math.Abs(args.Dbl("height", 3000) - 3000) > 1)
                || (args.HasNumber("depth") && Math.Abs(args.Dbl("depth", 2500) - 2500) > 1))
                return "custom height/depth are not available; the section box is fixed at 3000 x 2500 mm.";
            if (!string.IsNullOrWhiteSpace(eid))
                return $"elementId sections need explicit coordinates: pass x1,y1,x2,y2 (mm) instead of {eid}.";
            if (!(args.HasNumber("x1") && args.HasNumber("y1") && args.HasNumber("x2") && args.HasNumber("y2")))
                return "Pass elementId or x1,y1,x2,y2.";
            var z = args.Dbl("z", 0);
            var a = new PointMmDto(args.Dbl("x1", 0), args.Dbl("y1", 0), z);
            var b = new PointMmDto(args.Dbl("x2", 0), args.Dbl("y2", 0), z);
            var result = await context.Bridge.CreateSectionViewAsync((name ?? string.Empty).Trim(), a, b, null);
            return result == null ? "Please open a model first." : result.Error ?? result.Message;
        }
    };

    private static ToolEntry PathOfTravelTool(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "path_of_travel",
                Description = "Create path of travel on a floor plan (PathOfTravel.CreateMultiple). Params: startX,startY,endX,endY (mm) or starts/ends as [x,y] arrays. viewId optional. Max 5 pairs.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        startX = new { type = "number" },
                        startY = new { type = "number" },
                        endX = new { type = "number" },
                        endY = new { type = "number" },
                        starts = new { type = "array" },
                        ends = new { type = "array" },
                        viewId = new { type = "string" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var viewId = args.Str("viewId");
            var starts = args.Points("starts");
            var ends = args.Points("ends");
            if (starts.Count == 0 && args.HasNumber("startX") && args.HasNumber("startY"))
                starts.Add(new PointMmDto(args.Dbl("startX", 0), args.Dbl("startY", 0), 0));
            if (ends.Count == 0 && args.HasNumber("endX") && args.HasNumber("endY"))
                ends.Add(new PointMmDto(args.Dbl("endX", 0), args.Dbl("endY", 0), 0));
            if (starts.Count == 0 || ends.Count == 0) return "Pass startX/startY/endX/endY or starts/ends arrays.";
            if (starts.Count != ends.Count) return "starts and ends must have the same count.";
            var pairs = new List<PathPairDto>(starts.Count);
            for (var i = 0; i < starts.Count; i++) pairs.Add(new PathPairDto(starts[i], ends[i]));
            // The bridge clamps to 5 pairs and needs a floor plan view; both checks stay there.
            var result = await context.Bridge.CreatePathOfTravelAsync((viewId ?? string.Empty).Trim(), pairs);
            if (result == null) return "Please open a model first.";
            if (result.Error != null) return result.Error;
            var ids = string.Join(", ", result.CreatedIds);
            var fail = string.Join(", ", result.Failures);
            return $"Created {result.Created}/{result.Requested} path(s) in {result.ViewName}: {ids}"
                + (string.IsNullOrEmpty(fail) ? "" : $"\nFailed: {fail}");
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

        public int Int(string name, int fallback)
        {
            if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
            return fallback;
        }

        public double Dbl(string name, double fallback)
        {
            if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
            return fallback;
        }

        public bool HasNumber(string name)
            => _root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number;

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
            }
            return list;
        }

        public List<PointMmDto> Points(string name)
        {
            var list = new List<PointMmDto>();
            if (_root is not { } r || !r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array) continue;
                var nums = item.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Number).Select(n => n.GetDouble()).ToList();
                if (nums.Count >= 2) list.Add(new PointMmDto(nums[0], nums[1], nums.Count > 2 ? nums[2] : 0));
            }
            return list;
        }
    }
}
