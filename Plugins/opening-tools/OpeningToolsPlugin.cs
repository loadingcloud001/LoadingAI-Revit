using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.OpeningTools;

public sealed class OpeningToolsPlugin : IRevitToolPlugin
{
    public string Id => "opening-tools";
    public string Name => "Opening Tools";
    public string Version => "1.0.0";

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
                Description = "Place and punch opening voids where linked or local MEP solid-intersects a selected wall, floor, beam, column, or roof. Nearby same-host hits combine into one larger opening when leftover web cannot act as a pier. Stay separate if the remaining edge is too thin or the envelope exceeds two-thirds of the host length. Warn if combined width exceeds 700 mm. Uses LoadingAI_Opening_Horizontal_Rectangle or _Circle (walls, beams, columns) and LoadingAI_Opening_Vertical_Rectangle or _Circle (floors, roofs). Four .rfa next to the plugin; auto-loaded on the host. Call immediately; do not ask to bind links. Pass hostIds or select a host — a section box is not a host. Linked MEP is found against that host; query tools cannot see it. Result lists instance ids and XYZ; isolate those plus the host, not only the wall. dryRun counts only and does not write. Params: mepIds, hostIds, shape (rectangle|circle|auto), clearanceMm (default 25), familyName, levelName, limit (default 50, cap 200), dryRun.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        mepIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
                        hostIds = new { type = "array", items = new { type = "string" }, description = "Wall, floor, beam, column, or roof ids. If omitted, current selection must include a host, MEP, or a Revit link." },
                        shape = new { type = "string", description = "rectangle | circle | auto" },
                        clearanceMm = new { type = "number", description = "Default 25." },
                        familyName = new { type = "string" },
                        levelName = new { type = "string" },
                        limit = new { type = "integer", description = "Default 50, hard cap 200." },
                        dryRun = new { type = "boolean", description = "Count only. Does not place or punch." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var limit = args.Int("limit", 50);
            if (limit < 1) limit = 50;
            if (limit > 200) limit = 200;
            var request = new OpeningClashRequestDto(
                args.Strings("mepIds"),
                args.Strings("hostIds"),
                args.Str("shape") ?? "auto",
                args.Dbl("clearanceMm", 25),
                args.Str("familyName"),
                args.Str("levelName"),
                limit,
                args.Bool("dryRun"));
            if (args.Bool("dryRun"))
            {
                var plan = await context.Bridge.OpeningProbeClashAsync(request);
                if (plan == null) return "Please open a model first.";
                return plan.Error ?? $"{plan.Report} Dry run — no write.";
            }
            var placed = await context.Bridge.PlaceOpeningsAsync(request);
            return placed?.Error ?? placed?.Report ?? "Please open a model first.";
        }
    };

    private static ToolEntry CutHostsByOpenings(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "cut_hosts_by_openings",
                Description = "Repair the cut of local hosts with opening voids after a family reload. Happy path is create_openings_from_clash (place and punch). Uses current selection, openings tagged to the selected wall, floor, or beam, or all plugin openings. Linked hosts are skipped, not cut. Params: openingIds, hostIds.",
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
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var result = await context.Bridge.CutHostsByOpeningsAsync(new OpeningCutRequestDto(
                args.Strings("openingIds"),
                args.Strings("hostIds")));
            return result?.Error ?? result?.Report ?? "Please open a model first.";
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
                    if (s is { Length: > 0 }) list.Add(s);
                }
                else if (item.ValueKind == JsonValueKind.Number)
                    list.Add(item.ToString());
            }
            return list;
        }
    }
}
