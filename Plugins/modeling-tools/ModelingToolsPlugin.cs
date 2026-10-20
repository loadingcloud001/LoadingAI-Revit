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
    public string Version => "1.2.0";

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
                Description = "Load one .rfa family into the model. The file must already sit under the LoadingAI families folder. This loads a family; it does not place it.",
                Parameters = new
                {
                    type = "object",
                    properties = new { path = new { type = "string" } },
                    required = new[] { "path" }
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "path", Label = "Family file (.rfa)", Kind = PresetFieldKind.String, Required = true }
        },
        Describe = json => Task.FromResult<string?>($"This loads a family from {Args.Parse(json).Str("path") ?? "(no path)"}."),
        Handler = async json => (await LoadFamilyAsync(context, json)).Text,
        StructuredHandler = json => LoadFamilyAsync(context, json)
    };

    private static async Task<ToolExecutionResult> LoadFamilyAsync(IPluginContext context, string json)
    {
        var path = Args.Parse(json).Str("path");
        // Every rejection below happens before the Bridge is reached, so nothing is written.
        if (string.IsNullOrWhiteSpace(path))
            return ToolExecutionResult.Failed("path is required.", ToolWriteCertainty.NotCommitted);
        try { path = FamilyPaths.ResolveRead(path); }
        catch (Exception ex) { return ToolExecutionResult.Failed($"Failed: {ex.Message}", ToolWriteCertainty.NotCommitted); }
        if (!path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            return ToolExecutionResult.Failed("Failed: path must be a .rfa file.", ToolWriteCertainty.NotCommitted);
        if (!File.Exists(path))
            return ToolExecutionResult.Failed($"File not found: {path}", ToolWriteCertainty.NotCommitted);
        return FromWrite(await context.Bridge.LoadFamilyAsync(path));
    }

    private static ToolEntry ChangeElementType(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "change_element_type",
                Description = "Change the selected element's type. Pick the element, or pass elementId. typeName is the type to switch to.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        elementId = new { type = "string" },
                        elementIds = new { type = "array", items = new { type = "string" } },
                        typeName = new { type = "string" },
                        typeId = new { type = "string" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "elementIds", Label = "Element", Kind = PresetFieldKind.PickElements, Prompt = "Pick the element whose type should change." },
            new PresetField { Id = "typeName", Label = "New type name", Kind = PresetFieldKind.String, Required = true }
        },
        Handler = async json => (await ChangeElementTypeAsync(context, json)).Text,
        StructuredHandler = json => ChangeElementTypeAsync(context, json)
    };

    private static async Task<ToolExecutionResult> ChangeElementTypeAsync(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var eid = args.Str("elementId");
        if (string.IsNullOrWhiteSpace(eid))
            eid = args.Strings("elementIds").FirstOrDefault();
        var typeName = args.Str("typeName");
        var typeId = args.Str("typeId");
        if (string.IsNullOrWhiteSpace(eid))
            return ToolExecutionResult.Failed("elementId is required. Pick an element, or pass elementId.", ToolWriteCertainty.NotCommitted);
        return FromWrite(await context.Bridge.ChangeElementTypeAsync(eid, typeName, typeId));
    }

    private static ToolEntry CreateTopography(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_topography",
                Description = "Create a toposolid from at least 3 points, each [x, y, z] in millimetres. levelName is optional.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        points = new { type = "array", items = new { type = "array", items = new { type = "number" } }, description = "At least 3 points as [x,y,z] in mm." },
                        levelName = new { type = "string" },
                        levelId = new { type = "string" }
                    },
                    required = new[] { "points" }
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "levelName", Label = "Level name", Kind = PresetFieldKind.String }
        },
        Describe = json => Task.FromResult<string?>($"This creates a toposolid from {Args.Parse(json).PointsMm("points").Count} point(s). At least 3 are required."),
        Handler = async json => (await CreateTopographyAsync(context, json)).Text,
        StructuredHandler = json => CreateTopographyAsync(context, json)
    };

    private static async Task<ToolExecutionResult> CreateTopographyAsync(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var pts = args.PointsMm("points");
        if (pts.Count < 3)
            return ToolExecutionResult.Failed("Provide at least 3 points [x,y,z] in mm.", ToolWriteCertainty.NotCommitted);
        return FromWrite(await context.Bridge.CreateTopographyAsync(pts, args.Str("levelName"), args.Str("levelId")));
    }

    /// <summary>
    /// Each of these three is one atomic Revit transaction: the Adapter returns
    /// <c>Ok = true</c> only after <c>Transaction.Commit()</c>, and every failure path either
    /// runs before <c>Start()</c> or calls <c>RollBack()</c>. So <c>Ok</c> alone is the commit
    /// authority, <c>AffectedCount</c> is the changed-element count, and a partial outcome is
    /// not representable.
    /// </summary>
    private static ToolExecutionResult FromWrite(CommandResultDto result)
        => result.Ok
            ? ToolExecutionResult.Succeeded(result.Message, ToolWriteCertainty.Committed, result.AffectedCount)
            : ToolExecutionResult.Failed(result.Message, ToolWriteCertainty.NotCommitted);

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

        public List<string> Strings(string name)
        {
            var list = new List<string>();
            if (_root is not { } r || !r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    list.Add(item.GetString()!);
                else if (item.ValueKind == JsonValueKind.Number)
                    list.Add(item.ToString());
            }
            return list;
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
