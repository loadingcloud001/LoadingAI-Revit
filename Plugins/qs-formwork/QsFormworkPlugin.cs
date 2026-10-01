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
        return FromFormwork(result?.Error, result?.Report);
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
                        thicknessMm = new { type = "number" },
                        faceTypes = new { type = "array", items = new { type = "string" } },
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
        return FromFormwork(result?.Error, result?.Report);
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
                        action = new { type = "string" },
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
        var result = await context.Bridge.CleanupFormworkAsync(new CleanupRequestDto(
            (args.Str("action") ?? "stats").Trim().ToLowerInvariant(),
            args.Str("faceType")));
        return FromFormwork(result?.Error, result?.Report);
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
        var result = await context.Bridge.CopyFormworkParentParamsAsync(
            new CopyParamsRequestDto(ids, args.Strings("paramNames")));
        return FromFormwork(result?.Error, result?.Report);
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
    /// The formwork result DTOs are only (Report, Error): they carry no counts, so this cannot
    /// report a changed count, and it cannot tell a partially-applied run from a full one. The
    /// Adapter returns a null DTO when there is no document (its RunFormwork maps a null report
    /// to default), and the host algorithms use the exact sentence Please open a model first.
    /// for the same condition, so both shapes are treated as not-committed. Error is never set
    /// by the Adapter today but the branch stays for a future DTO that carries it. A Partial is
    /// deliberately not claimed: enriching the DTOs with created/deleted/failed counts (a
    /// LoadingAI.Core change) is what would let this report it precisely.
    /// </summary>
    private static ToolExecutionResult FromFormwork(string? error, string? report)
    {
        if (report == null || report == NoDocument)
            return ToolExecutionResult.Failed("Please open a model first.", ToolWriteCertainty.NotCommitted);
        if (error != null)
            return ToolExecutionResult.Failed(error, ToolWriteCertainty.NotCommitted);
        return ToolExecutionResult.Succeeded(report, ToolWriteCertainty.Committed);
    }

    /// <summary>The exact sentence the host formwork algorithms use when there is no document.</summary>
    private const string NoDocument = "Please open a model first.";

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
