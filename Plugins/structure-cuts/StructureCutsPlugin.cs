using System.Text.Json;
using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.StructureCuts;

public sealed class StructureCutsPlugin : IRevitToolPlugin, IRevitFullAccess
{
    public string Id => "structure-cuts";
    public string Name => "Structure Split Tools";
    public string Version => "1.1.0";

    public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
    {
        yield return SplitBeamsByColumns(context);
        yield return SplitFloorsByBeams(context);
    }

    private static ToolEntry SplitBeamsByColumns(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "split_beams_by_columns",
                Description = "Split structural beams at columns (pyRevit: Cutbeambycolumn). Recreates location-curve segments; not a solid cut. Copies instance parameters onto new pieces. Params: beamIds[] optional (else current selection), columnIds[] optional (else selection, then all columns), limit default 50 max 200.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        beamIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection filtered to structural framing." },
                        columnIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses selection columns, else all columns in the model." },
                        limit = new { type = "integer", description = "Max beams to process. Default 50, hard cap 200." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var uiApp = session.As<UIApplication>();
                return BeamSplit.Run(uiApp, args.Strings("beamIds"), args.Strings("columnIds"), args.Limit());
            });
        }
    };

    private static ToolEntry SplitFloorsByBeams(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "split_floors_by_beams",
                Description = "Split floors along beam edges and add column openings (pyRevit: Cutslabbybeam). Recreates planar floor regions; not a solid cut. Copies instance parameters onto new pieces. Params: floorIds[] optional (else current selection), beamIds[]/columnIds[] optional, limit default 50 max 200. Skips sloped/curved.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        floorIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection filtered to floors and structural foundation slabs." },
                        beamIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses selection framing, else beams whose bbox hits the floors." },
                        columnIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses selection columns, else columns whose bbox hits the floors." },
                        limit = new { type = "integer", description = "Max floors to process. Default 50, hard cap 200." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var uiApp = session.As<UIApplication>();
                return FloorSplit.Run(uiApp, args.Strings("floorIds"), args.Strings("beamIds"), args.Strings("columnIds"), args.Limit());
            });
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
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
                }
                else if (item.ValueKind == JsonValueKind.Number)
                    list.Add(item.ToString());
            }
            return list;
        }

        public int Limit()
        {
            var n = 50;
            if (_root is { } r && r.TryGetProperty("limit", out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) n = i;
                else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) n = s;
            }
            if (n < 1) n = 1;
            if (n > 200) n = 200;
            return n;
        }
    }
}
