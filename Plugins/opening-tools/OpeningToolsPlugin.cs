using System.Text.Json;
using LoadingAI.Core.Openings;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.OpeningTools;

public sealed class OpeningToolsPlugin : IRevitToolPlugin
{
    public string Id => "opening-tools";
    public string Name => "Opening Tools";
    public string Version => "1.1.0";

    public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
    {
        yield return CreateOpeningsFromClash(context);
        yield return CutHostsByOpenings(context);
    }

    private static ToolEntry CreateOpeningsFromClash(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_openings_from_clash",
                Description = "Create and cut openings where MEP crosses a wall, floor, beam, column, or roof. Pick the hosts in the form, or leave that empty to use the current selection. Nearby hits on the same host combine when the leftover strip cannot act as a pier; they stay separate if the remaining edge is too thin or the opening would exceed two-thirds of the host length. A combined width over 700 mm is reported, not blocked. A section box is not a host. Shipped opening families load automatically. dryRun counts only and reports No model changes.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        mepIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
                        hostIds = new { type = "array", items = new { type = "string" }, description = "Wall, floor, beam, column, or roof ids. If omitted, current selection must include a host, MEP, or a Revit link." },
                        shape = new { type = "string", @enum = new[] { "rectangle", "circle", "auto" } },
                        clearanceMm = new { type = "number", minimum = 0, description = "Default 25." },
                        familyName = new { type = "string" },
                        levelName = new { type = "string" },
                        limit = new { type = "integer", minimum = 1, maximum = 200, description = "Default 50, hard cap 200." },
                        dryRun = new { type = "boolean", description = "Count only. Does not place or punch." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "hostIds", Label = "Hosts", Kind = PresetFieldKind.PickElements, Prompt = "Pick the walls, floors, beams, columns, or roofs to cut. Leave empty to use the current selection." },
            new PresetField { Id = "shape", Label = "Shape", Kind = PresetFieldKind.Select, Options = new[] { "auto", "rectangle", "circle" }, Default = "auto" },
            new PresetField { Id = "clearanceMm", Label = "Clearance (mm)", Kind = PresetFieldKind.Number, Default = 25 },
            new PresetField { Id = "limit", Label = "Max openings", Kind = PresetFieldKind.Number, Default = 50 },
            new PresetField { Id = "dryRun", Label = "Dry-run (count only)", Kind = PresetFieldKind.Boolean, Default = false }
        },
        Describe = json => Task.FromResult<string?>(DescribeCreate(json)),
        Handler = async json => (await ExecuteCreateOpeningsFromClashAsync(context, json)).Text,
        StructuredHandler = json => ExecuteCreateOpeningsFromClashAsync(context, json)
    };

    private static async Task<ToolExecutionResult> ExecuteCreateOpeningsFromClashAsync(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var limit = args.Int("limit", 50);
        if (limit < 1) limit = 50;
        if (limit > 200) limit = 200;
        var familyName = args.Str("familyName");
        if (!OpeningFamilyRules.IsAllowedFamilyName(familyName, out var familyError))
            return ToolExecutionResult.Failed("Invalid arguments: " + familyError, ToolWriteCertainty.NotCommitted);

        var request = new OpeningClashRequestDto(
            args.Strings("mepIds"),
            args.Strings("hostIds"),
            args.Str("shape") ?? "auto",
            args.Dbl("clearanceMm", 25),
            familyName,
            args.Str("levelName"),
            limit,
            args.Bool("dryRun"));

        if (request.DryRun)
        {
            var plan = await context.Bridge.OpeningProbeClashAsync(request);
            return plan == null
                ? ToolExecutionResult.Failed("Please open a model first.", ToolWriteCertainty.NotCommitted)
                : plan.Error == null
                    ? ToolExecutionResult.Succeeded(OpeningResultText.ForDryRun(plan), ToolWriteCertainty.NoWrite)
                    : ToolExecutionResult.Failed(OpeningResultText.ForDryRun(plan), ToolWriteCertainty.NoWrite);
        }

        var placed = await context.Bridge.PlaceOpeningsAsync(request);
        if (placed == null)
            return ToolExecutionResult.Failed("Please open a model first.", ToolWriteCertainty.NotCommitted);

        var text = OpeningResultText.ForPlace(placed);
        var committed = placed.Placed > 0;
        if (placed.Placed > 0 && (placed.Failed > 0 || placed.Error != null))
            return ToolExecutionResult.Partial(text, ToolWriteCertainty.Committed, placed.Placed, placed.Failed);
        if (placed.Failed > 0 || placed.Error != null)
            return ToolExecutionResult.Failed(text, committed ? ToolWriteCertainty.Committed : ToolWriteCertainty.NotCommitted, placed.Placed, placed.Failed);
        return ToolExecutionResult.Succeeded(
            text,
            committed ? ToolWriteCertainty.Committed : ToolWriteCertainty.NoWrite,
            placed.Placed,
            placed.Failed);
    }

    private static ToolEntry CutHostsByOpenings(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "cut_hosts_by_openings",
                Description = "Repair local host cuts after an opening family reload. The usual path is create_openings_from_clash, which places and cuts in one step. Pick openings or hosts, or leave both empty to use the current selection. Linked hosts are skipped, not cut.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        openingIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
                        hostIds = new { type = "array", items = new { type = "string" }, description = "Optional. Linked hosts are skipped." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "openingIds", Label = "Openings", Kind = PresetFieldKind.PickElements, Prompt = "Pick the openings to cut with. Leave empty to use the current selection." },
            new PresetField { Id = "hostIds", Label = "Hosts", Kind = PresetFieldKind.PickElements, Prompt = "Pick the walls, floors, or beams to cut. Linked hosts are skipped." }
        },
        Describe = json => Task.FromResult<string?>(DescribeCut(json)),
        Handler = async json => (await ExecuteCutHostsByOpeningsAsync(context, json)).Text,
        StructuredHandler = json => ExecuteCutHostsByOpeningsAsync(context, json)
    };

    private static string DescribeCreate(string json)
    {
        var args = Args.Parse(json);
        var hosts = args.Strings("hostIds");
        var where = hosts.Count == 0 ? "the current selection" : hosts.Count + " picked host(s)";
        var mode = ToolPresets.IsDryRun(json) ? "Dry run only. No model changes." : "This places and cuts openings.";
        return $"{mode} Hosts: {where}. Shape: {args.Str("shape") ?? "auto"}. Clearance: {args.Dbl("clearanceMm", 25):0} mm.";
    }

    private static string DescribeCut(string json)
    {
        var args = Args.Parse(json);
        var openings = args.Strings("openingIds").Count;
        var hosts = args.Strings("hostIds").Count;
        var scope = openings == 0 && hosts == 0
            ? "the current selection"
            : $"{openings} opening(s), {hosts} host(s)";
        return $"Repair cuts using {scope}. Linked hosts are skipped.";
    }

    private static async Task<ToolExecutionResult> ExecuteCutHostsByOpeningsAsync(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var result = await context.Bridge.CutHostsByOpeningsAsync(new OpeningCutRequestDto(
            args.Strings("openingIds"),
            args.Strings("hostIds")));

        if (result == null)
            return ToolExecutionResult.Failed("Please open a model first.", ToolWriteCertainty.NotCommitted);

        var text = OpeningResultText.ForCut(result);
        var committed = result.Cut > 0;
        if (result.Cut > 0 && (result.Failed > 0 || result.Error != null))
            return ToolExecutionResult.Partial(text, ToolWriteCertainty.Committed, result.Cut, result.Failed);
        if (result.Failed > 0 || result.Error != null)
            return ToolExecutionResult.Failed(text, committed ? ToolWriteCertainty.Committed : ToolWriteCertainty.NotCommitted, result.Cut, result.Failed);
        return ToolExecutionResult.Succeeded(
            text,
            committed ? ToolWriteCertainty.Committed : ToolWriteCertainty.NoWrite,
            result.Cut,
            result.Failed);
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

        public bool Bool(string name, bool fallback = false)
        {
            if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return v.GetBoolean();
            return fallback;
        }

        public int Int(string name, int fallback)
        {
            if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
                return n;
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
                    if (s is { Length: > 0 })
                        list.Add(s);
                }
                else if (item.ValueKind == JsonValueKind.Number)
                    list.Add(item.ToString());
            }
            return list;
        }
    }
}
