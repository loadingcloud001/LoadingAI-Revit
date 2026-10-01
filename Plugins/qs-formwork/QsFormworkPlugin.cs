using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Revit;
using LoadingAI.Revit.Formwork;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.QsFormwork;

public sealed class QsFormworkPlugin : IRevitToolPlugin
{
    public string Id => "qs-formwork";
    public string Name => "Formwork QS Tools";
    public string Version => "1.0.1";

    /// <summary>The exact sentence the host formwork algorithms use when there is no document.</summary>
    private const string NoDocument = "Please open a model first.";

    public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
    {
        yield return Setup(context);
        yield return CreatePanels(context);
        yield return Cleanup(context);
        yield return CopyParentParams(context);
    }

    private static ToolEntry Setup(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "formwork_setup",
                Description = "Bind formwork shared parameters on Generic Models and create the panel schema. Run once per project before generating panels.",
                Parameters = new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async _ => (await FormworkSetupAsync(context)).Text,
        StructuredHandler = _ => FormworkSetupAsync(context)
    };

    private static async Task<ToolExecutionResult> FormworkSetupAsync(IPluginContext context)
    {
        var result = await context.Bridge.FormworkSetupAsync(new FormworkSetupRequestDto());
        return FromFormwork(result, readOnly: false);
    }

    private static ToolEntry CreatePanels(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_formwork_panels",
                Description = "Create DirectShape formwork panels from selected linked (or host) structural elements. Params: elementIds optional (else current selection), thicknessMm default 18, faceTypes optional, collision default true, deleteExisting default false, includeUnselectedLinks default false.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        elementIds = new { type = "array", items = new { type = "string" } },
                        // Bounds rather than a fixed value: sheet thickness genuinely varies, but
                        // the generator would otherwise build whatever number it was handed.
                        thicknessMm = new { type = "number", minimum = FormworkDefaults.SheetThicknessMmMin, maximum = FormworkDefaults.SheetThicknessMmMax },
                        // Built from the Core list, so a face type added host-side is immediately
                        // offered to the model and one removed is immediately refused.
                        faceTypes = new { type = "array", items = new { type = "string", @enum = FormworkDefaults.AllFaceTypes } },
                        collision = new { type = "boolean" },
                        deleteExisting = new { type = "boolean" },
                        includeUnselectedLinks = new { type = "boolean" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json => (await CreatePanelsAsync(context, json)).Text,
        StructuredHandler = json => CreatePanelsAsync(context, json)
    };

    private static async Task<ToolExecutionResult> CreatePanelsAsync(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var ids = await ResolveIdsAsync(context, args.Strings("elementIds"));
        if (ids.Count == 0)
            return ToolExecutionResult.Failed("Please select elements first.", ToolWriteCertainty.NotCommitted);
        var result = await context.Bridge.CreateFormworkPanelsAsync(new FormworkPanelsRequestDto(
            ids,
            args.Dbl("thicknessMm", FormworkDefaults.SheetThicknessMmDefault),
            args.Strings("faceTypes"),
            args.Bool("collision", true),
            args.Bool("deleteExisting", false),
            args.Bool("includeUnselectedLinks", false)));
        return FromFormwork(result, readOnly: false);
    }

    private static ToolEntry Cleanup(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "cleanup_formwork",
                Description = "Report or delete formwork panels. Params: action (stats, delete_all, delete_by_type, cleanup_orphans), faceType for delete_by_type.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        // enum so ToolArgumentValidator refuses an unimplemented action before
                        // approval; FormworkCleanup.Run keeps its own default case as
                        // defence-in-depth, because Handler can still be called directly.
                        action = new { type = "string", @enum = FormworkDefaults.CleanupActions },
                        faceType = new { type = "string" }
                    },
                    required = new[] { "action" }
                }
            }
        },
        Handler = async json => (await CleanupAsync(context, json)).Text,
        StructuredHandler = json => CleanupAsync(context, json)
    };

    private static async Task<ToolExecutionResult> CleanupAsync(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var action = (args.Str("action") ?? "stats").Trim().ToLowerInvariant();
        var result = await context.Bridge.CleanupFormworkAsync(new CleanupRequestDto(action, args.Str("faceType")));
        // stats returns before tx.Start in FormworkCleanup.Run, so it never opens a transaction
        // and must not start a turn TransactionGroup for a query.
        return FromFormwork(result, readOnly: action is "stats" or "");
    }

    private static ToolEntry CopyParentParams(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "copy_formwork_parent_params",
                Description = "Copy named parameters from each panel's parent host onto FW_* instance parameters. Params: elementIds optional (else selected panels or all), paramNames required to copy (omit to list catalog).",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        elementIds = new { type = "array", items = new { type = "string" } },
                        paramNames = new { type = "array", items = new { type = "string" } }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json => (await CopyParentParamsAsync(context, json)).Text,
        StructuredHandler = json => CopyParentParamsAsync(context, json)
    };

    private static async Task<ToolExecutionResult> CopyParentParamsAsync(IPluginContext context, string json)
    {
        var args = Args.Parse(json);
        var ids = await ResolveIdsAsync(context, args.Strings("elementIds"));
        var names = args.Strings("paramNames");
        var result = await context.Bridge.CopyFormworkParentParamsAsync(new CopyParamsRequestDto(ids, names));
        // An empty paramNames asks for the catalogue and returns before tx.Start, so it reads
        // rather than writes. Non-empty opens a transaction even when every value is skipped.
        return FromFormwork(result, readOnly: names.Count == 0);
    }

    /// <summary>
    /// Element ids, falling back to the current selection when the caller passed none.
    /// Returns an empty list when there is no selection, which each caller reports.
    /// </summary>
    private static async Task<List<string>> ResolveIdsAsync(IPluginContext context, List<string> explicitIds)
    {
        if (explicitIds.Count > 0) return explicitIds;
        var sel = await context.Bridge.GetSelectionSummaryAsync();
        return sel.ElementIds?.ToList() ?? new List<string>();
    }

    /// <summary>
    /// Status is derived from counts, never from the wording of the report (ADR-0013).
    ///
    /// <c>readOnly</c> is the one thing counts cannot express: it is the caller's own knowledge
    /// that this path never opened a <c>Transaction</c>, so it reports NoWrite instead of
    /// starting a turn TransactionGroup for a query. Everything else defaults to Committed,
    /// which is the conservative direction — assuming a write when there was none only folds an
    /// empty undo step in, whereas the reverse could roll back real work.
    ///
    /// The <c>Failed:</c> fallback stays because counts alone cannot distinguish a crash from a
    /// clean run that happened to change nothing: <c>FormworkTools</c> absorbs its own exceptions
    /// into the Report with zero counts. Same prefix rule <c>ToolExecutionResult.FromLegacy</c>
    /// applies, so a formwork crash is classified like any other legacy failure.
    /// </summary>
    private static ToolExecutionResult FromFormwork(FormworkSetupResultDto? r, bool readOnly)
        => Classify(new FormworkResultCounts(r?.Report, r?.Error, r?.Created ?? 0, r?.Updated ?? 0, r?.Deleted ?? 0, r?.Failed ?? 0), readOnly);

    private static ToolExecutionResult FromFormwork(FormworkPanelsResultDto? r, bool readOnly)
        => Classify(new FormworkResultCounts(r?.Report, r?.Error, r?.Created ?? 0, r?.Updated ?? 0, r?.Deleted ?? 0, r?.Failed ?? 0), readOnly);

    private static ToolExecutionResult FromFormwork(CleanupResultDto? r, bool readOnly)
        => Classify(new FormworkResultCounts(r?.Report, r?.Error, r?.Created ?? 0, r?.Updated ?? 0, r?.Deleted ?? 0, r?.Failed ?? 0), readOnly);

    private static ToolExecutionResult FromFormwork(CopyParamsResultDto? r, bool readOnly)
        => Classify(new FormworkResultCounts(r?.Report, r?.Error, r?.Created ?? 0, r?.Updated ?? 0, r?.Deleted ?? 0, r?.Failed ?? 0), readOnly);

    /// <summary>
    /// The part of any formwork result DTO that the classifier reads. Deliberately a plain
    /// readonly struct with get-only properties: the plugin targets netstandard2.0 and no longer
    /// carries an IsExternalInit polyfill, so a positional record would not compile.
    /// </summary>
    private readonly struct FormworkResultCounts
    {
        public FormworkResultCounts(string? report, string? error, int created, int updated, int deleted, int failed)
        {
            Report = report;
            Error = error;
            Created = created;
            Updated = updated;
            Deleted = deleted;
            Failed = failed;
        }

        public string? Report { get; }
        public string? Error { get; }
        public int Created { get; }
        public int Updated { get; }
        public int Deleted { get; }
        public int Failed { get; }
    }

    private static ToolExecutionResult Classify(FormworkResultCounts result, bool readOnly)
    {
        if (result.Report == null || result.Report == NoDocument)
            return ToolExecutionResult.Failed(NoDocument, ToolWriteCertainty.NotCommitted);
        // Error first, matching ADR-0007 / ADR-0009 / ADR-0013.
        if (result.Error != null)
            return ToolExecutionResult.Failed(result.Error, ToolWriteCertainty.NotCommitted);
        if (result.Report.StartsWith("Failed:", StringComparison.OrdinalIgnoreCase))
            return ToolExecutionResult.Failed(result.Report, ToolWriteCertainty.NotCommitted);

        var writes = result.Created + result.Updated + result.Deleted;
        if (result.Failed > 0)
        {
            // Wrote and failed: the parts that landed are kept and named, and the outer
            // TransactionGroup assimilates them. Failed with nothing written: roll back.
            return writes > 0
                ? ToolExecutionResult.Partial(result.Report, ToolWriteCertainty.Committed, writes, result.Failed)
                : ToolExecutionResult.Failed(result.Report, ToolWriteCertainty.NotCommitted, 0, result.Failed);
        }

        if (writes > 0)
            return ToolExecutionResult.Succeeded(result.Report, ToolWriteCertainty.Committed, writes);

        return readOnly
            ? ToolExecutionResult.Succeeded(result.Report, ToolWriteCertainty.NoWrite)
            : ToolExecutionResult.Succeeded(result.Report, ToolWriteCertainty.Committed);
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

        public bool Bool(string name, bool fallback)
        {
            if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return v.GetBoolean();
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
