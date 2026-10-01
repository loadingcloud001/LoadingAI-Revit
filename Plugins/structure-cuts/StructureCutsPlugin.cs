using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Revit;
using LoadingAI.Revit.StructureCuts;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.StructureCuts;

public sealed class StructureCutsPlugin : IRevitToolPlugin
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
        yield return SplitSecondaryBeamsByMainBeams(context);
    }

    private static ToolEntry SplitBeamsByColumns(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "split_beams_by_columns",
                Description = "Split structural beams at columns (pyRevit: Cutbeambycolumn). Splits the element in place with Revit's own split where it can, so the member, its host and its parameters stay; a member Revit will not split is re-created as location-curve segments instead. Not a solid cut. Straight and curved beams, cut at the real column footprint, not a bounding box. Copies instance parameters onto pieces it has to re-create unless copyParams is off. Params: beamIds[] optional (else current selection), columnIds[] optional (else selection, then all columns), scope selection|visible|all, continueFrom, dryRun boolean counts only without changing the model, uniqueMark boolean suffixes the new Marks, copyParams boolean, limit default 50 max 200, includeLinks boolean (default off) also uses loaded-link cutters; hosts stay in this model. Selects the new pieces after a write run.",
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
                        limit = LimitProperty(),
                        includeLinks = IncludeLinksProperty()
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = Concat(
            new PresetField { Id = "beamIds", Label = "Beams", Kind = PresetFieldKind.PickElements, Category = "beams", Prompt = "Pick structural beams, then Finish." },
            new PresetField { Id = "columnIds", Label = "Columns", Kind = PresetFieldKind.PickElements, Category = "OST_StructuralColumns", Prompt = "Pick structural columns, then Finish." },
            SharedKnobs()),
        Handler = async json => (await ExecuteSplitBeamsByColumns(context, json)).Text,
        StructuredHandler = json => ExecuteSplitBeamsByColumns(context, json)
    };

    private static ToolEntry SplitFloorsByBeams(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "split_floors_by_beams",
                Description = "Split floors along beam edges and add column openings (pyRevit: Cutslabbybeam). Rebuilds planar floor regions through the flooring API and carries a single-plane slope with a slope arrow. Not a solid cut. Handles floors with openings, curved beams and real column footprints, and leaves alone, counted by reason, any slab it cannot rebuild exactly. Copies instance parameters onto new pieces unless copyParams is off. Params: floorIds[] optional (else current selection), beamIds[]/columnIds[] optional, scope selection|visible|all, continueFrom, dryRun boolean counts only without changing the model, uniqueMark boolean suffixes the new Marks, copyParams boolean, limit default 50 max 200, includeLinks boolean (default off) also uses loaded-link cutters; hosts stay in this model. Selects the new pieces after a write run.",
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
                        limit = LimitProperty(),
                        includeLinks = IncludeLinksProperty()
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
        Handler = async json => (await ExecuteSplitFloorsByBeams(context, json)).Text,
        StructuredHandler = json => ExecuteSplitFloorsByBeams(context, json)
    };

    private static ToolEntry SplitByLevels(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "split_by_levels",
                Description = "Split walls, columns, beams and floors at storey elevations (Naviate-style split by levels). Curve members use FamilyInstance.Split where Revit allows it, else location-curve recreate; walls recreate the location curve; stacked and curtain walls are skipped by name. Horizontal floors skip with no-level-hit; a leaning slab is cut along the storey plane. Copies instance parameters onto re-created pieces. Params: elementIds[] optional (else scope), levelIds[] optional (else selection, then all levels), categories[] optional (walls, columns, beams, floors), scope selection|visible|all, continueFrom, dryRun boolean counts only without changing the model, uniqueMark boolean, copyParams boolean, limit default 50 max 200, includeLinks boolean (default off) also uses loaded-link cutters; hosts stay in this model.",
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
                        limit = LimitProperty(),
                        includeLinks = IncludeLinksProperty()
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
        Handler = async json => (await ExecuteSplitByLevels(context, json)).Text,
        StructuredHandler = json => ExecuteSplitByLevels(context, json)
    };

    private static ToolEntry SplitByGrids(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "split_by_grids",
                Description = "Split walls, columns, beams and floors at grid lines (Naviate-style split by grids). Curve members cut on the vertical plane through each grid; floors reuse the planar region pipeline with grid lines as cutters. Copies instance parameters onto re-created pieces. Params: elementIds[] optional (else scope), gridIds[] optional (else selection, then all grids), categories[] optional (walls, columns, beams, floors), scope selection|visible|all, continueFrom, dryRun boolean counts only without changing the model, uniqueMark boolean, copyParams boolean, limit default 50 max 200, includeLinks boolean (default off) also uses loaded-link cutters; hosts stay in this model.",
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
                        limit = LimitProperty(),
                        includeLinks = IncludeLinksProperty()
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
        Handler = async json => (await ExecuteSplitByGrids(context, json)).Text,
        StructuredHandler = json => ExecuteSplitByGrids(context, json)
    };

    private static ToolEntry SplitSecondaryBeamsByMainBeams(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "split_secondary_beams_by_main_beams",
                Description = "Split a strictly-shallower beam at a same-height pass-through with a strictly deeper main. Splits the shallower beam into two pieces at the crossing, no stub inside the main. Main means strictly deeper type section height; equal depth, bearing-on, or different heights = no cut. Not a cope. Empty scope refuses. Copies instance parameters onto pieces unless copyParams is off. Params: beamIds[] optional (else scope), scope selection|visible|all, continueFrom, dryRun boolean counts only without changing the model, uniqueMark boolean suffixes the new Marks, copyParams boolean, limit default 50 max 200, includeLinks boolean (default off) also uses loaded-link cutters; hosts stay in this model. Selects the new pieces after a write run.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        beamIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses scope (selection, visible, or all) filtered to structural framing." },
                        scope = ScopeProperty(),
                        continueFrom = ContinueFromProperty(),
                        dryRun = DryRunProperty(),
                        uniqueMark = UniqueMarkProperty(),
                        copyParams = CopyParamsProperty(),
                        limit = LimitProperty(),
                        includeLinks = IncludeLinksProperty()
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Presets = Concat(
            new PresetField { Id = "beamIds", Label = "Beams", Kind = PresetFieldKind.PickElements, Category = "beams", Prompt = "Pick structural beams, then Finish." },
            SharedKnobs()),
        Handler = async json => (await ExecuteSplitSecondaryBeamsByMainBeams(context, json)).Text,
        StructuredHandler = json => ExecuteSplitSecondaryBeamsByMainBeams(context, json)
    };

    /// <summary>The exact sentence the host splits return when no document is open.</summary>
    private const string NoDocument = "Please open a model first.";

    private static async Task<ToolExecutionResult> ExecuteSplitBeamsByColumns(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var options = Options(args, json);
        var result = await context.Bridge.SplitBeamsByColumnsAsync(
            Request(options, Filter(args.Strings("beamIds")), Filter(args.Strings("columnIds")), null));
        return FromSplit(result);
    }

    private static async Task<ToolExecutionResult> ExecuteSplitFloorsByBeams(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var options = Options(args, json);
        var cutters = new List<string>();
        cutters.AddRange(args.Strings("beamIds"));
        cutters.AddRange(args.Strings("columnIds"));
        var result = await context.Bridge.SplitFloorsByBeamsAsync(
            Request(options, Filter(args.Strings("floorIds")), Filter(cutters), null));
        return FromSplit(result);
    }

    private static async Task<ToolExecutionResult> ExecuteSplitByLevels(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var options = Options(args, json);
        var result = await context.Bridge.SplitByLevelsAsync(
            Request(options, Filter(args.Strings("elementIds")), Filter(args.Strings("levelIds")), Filter(args.Strings("categories"))));
        return FromSplit(result);
    }

    private static async Task<ToolExecutionResult> ExecuteSplitByGrids(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var options = Options(args, json);
        var result = await context.Bridge.SplitByGridsAsync(
            Request(options, Filter(args.Strings("elementIds")), Filter(args.Strings("gridIds")), Filter(args.Strings("categories"))));
        return FromSplit(result);
    }

    private static async Task<ToolExecutionResult> ExecuteSplitSecondaryBeamsByMainBeams(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var options = Options(args, json);
        var result = await context.Bridge.SplitSecondaryBeamsByMainBeamsAsync(
            Request(options, Filter(args.Strings("beamIds")), null, null));
        return FromSplit(result);
    }

    /// <summary>
    /// Status comes from the counters <c>SplitResultDto</c> has always carried: <c>Created</c>
    /// counts hosts replaced by a piece and <c>Openings</c> counts openings punched, so either
    /// being positive is a real write.
    ///
    /// A dry run short-circuits before any transaction and always reports both at zero, so this
    /// classifies it as a read without having to re-read the request flag.
    ///
    /// <c>Skipped</c> is deliberately not folded into the write count: a grouped, pinned or
    /// unsplittable member is left alone by design and named in the report, and the DTO carries
    /// no failure count at all, so treating a skip as a failure would invent one.
    ///
    /// <c>Error</c> is checked first, matching ADR-0007 and ADR-0009. The <c>Failed:</c> prefix
    /// stays as a fallback because the adapter's catch returns it as the <c>Report</c> with
    /// <c>Error</c> null and every counter at zero, which counts alone cannot separate from a run
    /// that simply changed nothing. Same rule <c>ToolExecutionResult.FromLegacy</c> applies.
    /// </summary>
    private static ToolExecutionResult FromSplit(SplitResultDto? result)
    {
        if (result == null || result.Report == NoDocument)
            return ToolExecutionResult.Failed(NoDocument, ToolWriteCertainty.NotCommitted);
        if (result.Error != null)
            return ToolExecutionResult.Failed(result.Error, ToolWriteCertainty.NotCommitted);
        if (result.Report.StartsWith("Failed:", StringComparison.OrdinalIgnoreCase))
            return ToolExecutionResult.Failed(result.Report, ToolWriteCertainty.NotCommitted);

        var writes = result.Created + result.Openings;
        return writes > 0
            ? ToolExecutionResult.Succeeded(result.Report, ToolWriteCertainty.Committed, writes)
            : ToolExecutionResult.Succeeded(result.Report, ToolWriteCertainty.NoWrite);
    }

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
        IncludeLinks = args.Bool("includeLinks", false),
        Scope = args.Scope(),
        ContinueFrom = args.ContinueFrom()
    };

    /// <summary>
    /// Packs the shared options plus the two per-tool id filters into one request. <b>An empty list is
    /// not null</b>: the host reads null as "use the scope", so a filter the caller left out must be
    /// normalised to null here rather than forwarded as an empty list.
    /// </summary>
    private static SplitRequestDto Request(
        SplitOptions options, IReadOnlyList<string>? hosts, IReadOnlyList<string>? cutters, IReadOnlyList<string>? categories)
        => new(hosts, cutters, categories, options.Scope, options.ContinueFrom, options.Limit,
            options.DryRun, options.UniqueMark, options.CopyParams, options.IncludeLinks);

    private static IReadOnlyList<string>? Filter(List<string> ids) => ids.Count == 0 ? null : ids;

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
        new PresetField { Id = "includeLinks", Label = "Include linked cutters", Kind = PresetFieldKind.Boolean, Default = false },
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

    private static object IncludeLinksProperty() => new
    {
        type = "boolean",
        description = "Optional. When true, cutting elements may also come from loaded Revit links. Hosts being split stay in this model. Default false."
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
