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
    public string Version => "1.5.0";

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
                Description = "Split structural beams at columns (pyRevit: Cutbeambycolumn). Splits the element in place with Revit's own split where it can, so the member, its host and its parameters stay; a member Revit will not split is re-created as location-curve segments instead. Not a solid cut. Straight and curved beams, cut at the real column footprint, not a bounding box. Copies instance parameters onto pieces it has to re-create unless copyParams is off. Params: beamIds[] optional (else current selection), columnIds[] optional (else selection, then all columns), dryRun boolean counts only without changing the model, uniqueMark boolean suffixes the new Marks, copyParams boolean, limit default 50 max 200. Selects the new pieces after a write run.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        beamIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection filtered to structural framing." },
                        columnIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses selection columns, else all columns in the model." },
                        dryRun = new { type = "boolean", description = "Optional. When true, counts the beam splits and segments that would be created without modifying the model. Default false." },
                        uniqueMark = new { type = "boolean", description = "Optional. When true, the first piece keeps the original Mark and later pieces become <Mark>-1, -2, ... Default false." },
                        copyParams = new { type = "boolean", description = "Optional. When false, new pieces keep their type defaults instead of the original's instance parameters. Default true." },
                        limit = new { type = "integer", description = "Max beams to process. Default 50, hard cap 200." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "beamIds", Label = "Beams", Kind = PresetFieldKind.PickElements, Category = "beams", Prompt = "Pick structural beams, then Finish." },
            new PresetField { Id = "columnIds", Label = "Columns", Kind = PresetFieldKind.PickElements, Category = "OST_StructuralColumns", Prompt = "Pick structural columns, then Finish." },
            new PresetField { Id = "uniqueMark", Label = "Unique Marks (-1, -2, ...)", Kind = PresetFieldKind.Boolean, Default = false },
            new PresetField { Id = "copyParams", Label = "Copy instance parameters", Kind = PresetFieldKind.Boolean, Default = true },
            new PresetField { Id = "limit", Label = "Max elements", Kind = PresetFieldKind.Number, Default = SplitOptions.DefaultLimit },
            new PresetField { Id = "dryRun", Label = "Dry-run (count only)", Kind = PresetFieldKind.Boolean, Default = false }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var options = Options(args, json);
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var uiApp = session.As<UIApplication>();
                return BeamSplit.Run(uiApp, args.Strings("beamIds"), args.Strings("columnIds"), options);
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
                Description = "Split floors along beam edges and add column openings (pyRevit: Cutslabbybeam). Rebuilds planar floor regions through the flooring API and carries a single-plane slope with a slope arrow. Not a solid cut. Handles floors with openings, curved beams and real column footprints, and leaves alone, counted by reason, any slab it cannot rebuild exactly. Copies instance parameters onto new pieces unless copyParams is off. Params: floorIds[] optional (else current selection), beamIds[]/columnIds[] optional, dryRun boolean counts only without changing the model, uniqueMark boolean suffixes the new Marks, copyParams boolean, limit default 50 max 200. Selects the new pieces after a write run.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        floorIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection filtered to floors and structural foundation slabs." },
                        beamIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses selection framing, else beams whose bbox hits the floors." },
                        columnIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses selection columns, else columns whose bbox hits the floors." },
                        dryRun = new { type = "boolean", description = "Optional. When true, counts the floor regions and column openings that would be created without modifying the model. Default false." },
                        uniqueMark = new { type = "boolean", description = "Optional. When true, the first piece keeps the original Mark and later pieces become <Mark>-1, -2, ... Default false." },
                        copyParams = new { type = "boolean", description = "Optional. When false, new pieces keep their type defaults instead of the original's instance parameters. Default true." },
                        limit = new { type = "integer", description = "Max floors to process. Default 50, hard cap 200." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = new[]
        {
            new PresetField { Id = "floorIds", Label = "Floors", Kind = PresetFieldKind.PickElements, Category = "floors", Prompt = "Pick floors, then Finish." },
            new PresetField { Id = "beamIds", Label = "Beams", Kind = PresetFieldKind.PickElements, Category = "beams", Prompt = "Pick beams that split the floors, then Finish." },
            new PresetField { Id = "columnIds", Label = "Columns", Kind = PresetFieldKind.PickElements, Category = "OST_StructuralColumns", Prompt = "Pick columns for openings, then Finish." },
            new PresetField { Id = "uniqueMark", Label = "Unique Marks (-1, -2, ...)", Kind = PresetFieldKind.Boolean, Default = false },
            new PresetField { Id = "copyParams", Label = "Copy instance parameters", Kind = PresetFieldKind.Boolean, Default = true },
            new PresetField { Id = "limit", Label = "Max elements", Kind = PresetFieldKind.Number, Default = SplitOptions.DefaultLimit },
            new PresetField { Id = "dryRun", Label = "Dry-run (count only)", Kind = PresetFieldKind.Boolean, Default = false }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var options = Options(args, json);
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var uiApp = session.As<UIApplication>();
                return FloorSplit.Run(uiApp, args.Strings("floorIds"), args.Strings("beamIds"), args.Strings("columnIds"), options);
            });
        }
    };

    /// <summary>
    /// Builds the shared options. dryRun goes through <see cref="ToolPresets.IsDryRun"/> so the plugin
    /// and the host's L3 write-confirm gate always agree on whether a call may write.
    /// </summary>
    private static SplitOptions Options(in Args args, string json) => new()
    {
        Limit = args.Limit(),
        DryRun = ToolPresets.IsDryRun(json),
        UniqueMark = args.Bool("uniqueMark", false),
        CopyParams = args.Bool("copyParams", true)
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
            if (!TryGet(name, out var v) || v.ValueKind != JsonValueKind.Array)
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

        public bool Bool(string name, bool fallback)
        {
            if (!TryGet(name, out var v)) return fallback;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => v.TryGetInt32(out var n) && n != 0,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : fallback,
                _ => fallback
            };
        }

        public int Limit()
        {
            var n = SplitOptions.DefaultLimit;
            if (TryGet("limit", out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) n = i;
                else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) n = s;
            }
            if (n < 1) n = 1;
            if (n > SplitOptions.MaxLimit) n = SplitOptions.MaxLimit;
            return n;
        }

        /// <summary>Case-insensitive on purpose: the host's own preset reader is, so both sides agree.</summary>
        private bool TryGet(string name, out JsonElement value)
        {
            value = default;
            if (_root is not { } r) return false;
            if (r.TryGetProperty(name, out value)) return true;
            foreach (var p in r.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
            return false;
        }
    }
}
