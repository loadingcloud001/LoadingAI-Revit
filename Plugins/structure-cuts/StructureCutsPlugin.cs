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
    public string Version => "2.0.1";

    public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
    {
        yield return SplitBeamsByColumns(context);
        yield return SplitFloorsByBeams(context);
        yield return SplitByLevels(context);
        yield return SplitByGrids(context);
    }

    private static ToolEntry SplitBeamsByColumns(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "split_beams_by_columns",
                Description = "Split structural beams at columns (pyRevit: Cutbeambycolumn). Splits the element in place with Revit's own split where it can, so the member, its host and its parameters stay; a member Revit will not split is re-created as location-curve segments instead. Not a solid cut. Straight and curved beams, cut at the real column footprint, not a bounding box. Copies instance parameters onto pieces it has to re-create unless copyParams is off. Params: beamIds[] optional (else current selection), columnIds[] optional (else selection, then all columns), scope selection|visible|all, continueFrom, dryRun boolean counts only without changing the model, uniqueMark boolean suffixes the new Marks, copyParams boolean, limit default 50 max 200. Selects the new pieces after a write run.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        beamIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses scope (selection, visible, or all) filtered to structural framing." },
                        columnIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses selection columns, else all columns in the model." },
                        scope = ScopeProperty(),
                        continueFrom = ContinueFromProperty(),
                        dryRun = DryRunProperty(),
                        uniqueMark = UniqueMarkProperty(),
                        copyParams = CopyParamsProperty(),
                        limit = LimitProperty()
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = Concat(
            new PresetField { Id = "beamIds", Label = "Beams", Kind = PresetFieldKind.PickElements, Category = "beams", Prompt = "Pick structural beams, then Finish." },
            new PresetField { Id = "columnIds", Label = "Columns", Kind = PresetFieldKind.PickElements, Category = "OST_StructuralColumns", Prompt = "Pick structural columns, then Finish." },
            SharedKnobs()),
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
                Description = "Split floors along beam edges and add column openings (pyRevit: Cutslabbybeam). Rebuilds planar floor regions through the flooring API and carries a single-plane slope with a slope arrow. Not a solid cut. Handles floors with openings, curved beams and real column footprints, and leaves alone, counted by reason, any slab it cannot rebuild exactly. Copies instance parameters onto new pieces unless copyParams is off. Params: floorIds[] optional (else current selection), beamIds[]/columnIds[] optional, scope selection|visible|all, continueFrom, dryRun boolean counts only without changing the model, uniqueMark boolean suffixes the new Marks, copyParams boolean, limit default 50 max 200. Selects the new pieces after a write run.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        floorIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses scope (selection, visible, or all) filtered to floors and structural foundation slabs." },
                        beamIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses selection framing, else beams whose bbox hits the floors." },
                        columnIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses selection columns, else columns whose bbox hits the floors." },
                        scope = ScopeProperty(),
                        continueFrom = ContinueFromProperty(),
                        dryRun = DryRunProperty(),
                        uniqueMark = UniqueMarkProperty(),
                        copyParams = CopyParamsProperty(),
                        limit = LimitProperty()
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = Concat(
            new PresetField { Id = "floorIds", Label = "Floors", Kind = PresetFieldKind.PickElements, Category = "floors", Prompt = "Pick floors, then Finish." },
            new PresetField { Id = "beamIds", Label = "Beams", Kind = PresetFieldKind.PickElements, Category = "beams", Prompt = "Pick beams that split the floors, then Finish." },
            new PresetField { Id = "columnIds", Label = "Columns", Kind = PresetFieldKind.PickElements, Category = "OST_StructuralColumns", Prompt = "Pick columns for openings, then Finish." },
            SharedKnobs()),
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

    private static ToolEntry SplitByLevels(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "split_by_levels",
                Description = "Split walls, columns, beams and floors at storey elevations (Naviate-style split by levels). Curve members use FamilyInstance.Split where Revit allows it, else location-curve recreate; walls recreate the location curve; stacked and curtain walls are skipped by name. Horizontal floors skip with no-level-hit; a leaning slab is cut along the storey plane. Copies instance parameters onto re-created pieces. Params: elementIds[] optional (else scope), levelIds[] optional (else selection, then all levels), categories[] optional (walls, columns, beams, floors), scope selection|visible|all, continueFrom, dryRun boolean counts only without changing the model, uniqueMark boolean, copyParams boolean, limit default 50 max 200.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        elementIds = ElementIdsProperty(),
                        levelIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses selection levels, else all levels in the model." },
                        categories = CategoriesProperty(),
                        scope = ScopeProperty(),
                        continueFrom = ContinueFromProperty(),
                        dryRun = DryRunProperty(),
                        uniqueMark = UniqueMarkProperty(),
                        copyParams = CopyParamsProperty(),
                        limit = LimitProperty()
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = Concat(
            new PresetField { Id = "elementIds", Label = "Elements", Kind = PresetFieldKind.PickElements, Prompt = "Pick walls, columns, beams or floors, then Finish." },
            new PresetField { Id = "levelIds", Label = "Levels", Kind = PresetFieldKind.PickElements, Category = "OST_Levels", Prompt = "Pick levels to split at, then Finish." },
            CategoriesPreset(),
            SharedKnobs()),
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var options = Options(args, json);
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var uiApp = session.As<UIApplication>();
                return LevelSplit.Run(uiApp, args.Strings("elementIds"), args.Strings("levelIds"), args.Strings("categories"), options);
            });
        }
    };

    private static ToolEntry SplitByGrids(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "split_by_grids",
                Description = "Split walls, columns, beams and floors at grid lines (Naviate-style split by grids). Curve members cut on the vertical plane through each grid; floors reuse the planar region pipeline with grid lines as cutters. Copies instance parameters onto re-created pieces. Params: elementIds[] optional (else scope), gridIds[] optional (else selection, then all grids), categories[] optional (walls, columns, beams, floors), scope selection|visible|all, continueFrom, dryRun boolean counts only without changing the model, uniqueMark boolean, copyParams boolean, limit default 50 max 200.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        elementIds = ElementIdsProperty(),
                        gridIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses selection grids, else all grids in the model." },
                        categories = CategoriesProperty(),
                        scope = ScopeProperty(),
                        continueFrom = ContinueFromProperty(),
                        dryRun = DryRunProperty(),
                        uniqueMark = UniqueMarkProperty(),
                        copyParams = CopyParamsProperty(),
                        limit = LimitProperty()
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = Concat(
            new PresetField { Id = "elementIds", Label = "Elements", Kind = PresetFieldKind.PickElements, Prompt = "Pick walls, columns, beams or floors, then Finish." },
            new PresetField { Id = "gridIds", Label = "Grids", Kind = PresetFieldKind.PickElements, Category = "OST_Grids", Prompt = "Pick grids to split at, then Finish." },
            CategoriesPreset(),
            SharedKnobs()),
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var options = Options(args, json);
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var uiApp = session.As<UIApplication>();
                return GridSplit.Run(uiApp, args.Strings("elementIds"), args.Strings("gridIds"), args.Strings("categories"), options);
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
        CopyParams = args.Bool("copyParams", true),
        Scope = args.Scope(),
        ContinueFrom = args.ContinueFrom()
    };

    private static IReadOnlyList<PresetField> SharedKnobs() => new[]
    {
        new PresetField
        {
            Id = "scope",
            Label = "Scope",
            Kind = PresetFieldKind.Select,
            Options = new[] { "selection", "visible", "all" },
            Default = SplitOptions.DefaultScope
        },
        new PresetField { Id = "continueFrom", Label = "Continue from", Kind = PresetFieldKind.Number, Default = 0 },
        new PresetField { Id = "uniqueMark", Label = "Unique Marks (-1, -2, ...)", Kind = PresetFieldKind.Boolean, Default = false },
        new PresetField { Id = "copyParams", Label = "Copy instance parameters", Kind = PresetFieldKind.Boolean, Default = true },
        new PresetField { Id = "limit", Label = "Max elements", Kind = PresetFieldKind.Number, Default = SplitOptions.DefaultLimit },
        new PresetField { Id = "dryRun", Label = "Dry-run (count only)", Kind = PresetFieldKind.Boolean, Default = false }
    };

    private static PresetField CategoriesPreset() => new()
    {
        Id = "categories",
        Label = "Categories",
        Kind = PresetFieldKind.Select,
        Options = new[] { "walls", "columns", "beams", "floors" }
    };

    private static IReadOnlyList<PresetField> Concat(params object[] parts)
    {
        var list = new List<PresetField>();
        foreach (var part in parts)
        {
            if (part is PresetField field)
                list.Add(field);
            else if (part is IEnumerable<PresetField> many)
                list.AddRange(many);
        }
        return list;
    }

    private static object ScopeProperty() => new
    {
        type = "string",
        description = "Optional. selection (default), visible, or all. Used when host ids are omitted."
    };

    private static object ContinueFromProperty() => new
    {
        type = "integer",
        description = "Optional. Skip this many matching hosts before applying limit. Default 0."
    };

    private static object DryRunProperty() => new
    {
        type = "boolean",
        description = "Optional. When true, counts splits without modifying the model. Default false."
    };

    private static object UniqueMarkProperty() => new
    {
        type = "boolean",
        description = "Optional. When true, the first piece keeps the original Mark and later pieces become <Mark>-1, -2, ... Default false."
    };

    private static object CopyParamsProperty() => new
    {
        type = "boolean",
        description = "Optional. When false, new pieces keep their type defaults instead of the original's instance parameters. Default true."
    };

    private static object LimitProperty() => new
    {
        type = "integer",
        description = "Max hosts to process. Default 50, hard cap 200."
    };

    private static object ElementIdsProperty() => new
    {
        type = "array",
        items = new { type = "string" },
        description = "Optional. If omitted, uses scope (selection, visible, or all) filtered by categories."
    };

    private static object CategoriesProperty() => new
    {
        type = "array",
        items = new { type = "string" },
        description = "Optional. walls, columns, beams, floors. Default all four."
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
            if (!TryGet(name, out var v))
                return list;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
                return list;
            }
            if (v.ValueKind != JsonValueKind.Array)
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

        public string Scope()
        {
            if (!TryGet("scope", out var v) || v.ValueKind != JsonValueKind.String)
                return SplitOptions.DefaultScope;
            var scope = (v.GetString() ?? "").Trim().ToLowerInvariant();
            return scope is "selection" or "visible" or "all" ? scope : SplitOptions.DefaultScope;
        }

        public int ContinueFrom()
        {
            var n = 0;
            if (TryGet("continueFrom", out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) n = i;
                else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) n = s;
            }
            return n < 0 ? 0 : n;
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
