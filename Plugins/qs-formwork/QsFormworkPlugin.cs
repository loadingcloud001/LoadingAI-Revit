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
    public string Version => "1.0.0";

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
        Handler = async _ =>
        {
            var result = await context.Bridge.FormworkSetupAsync(new FormworkSetupRequestDto());
            return result?.Error ?? result?.Report ?? "Please open a model first.";
        }
    };

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
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var ids = args.Strings("elementIds");
            if (ids.Count == 0)
            {
                var sel = await context.Bridge.GetSelectionSummaryAsync();
                ids = sel.ElementIds?.ToList() ?? new List<string>();
            }
            if (ids.Count == 0) return "Please select elements first.";
            var result = await context.Bridge.CreateFormworkPanelsAsync(new FormworkPanelsRequestDto(
                ids,
                args.Dbl("thicknessMm", FormworkDefaults.SheetThicknessMmDefault),
                args.Strings("faceTypes"),
                args.Bool("collision", true),
                args.Bool("deleteExisting", false),
                args.Bool("includeUnselectedLinks", false)));
            return result?.Error ?? result?.Report ?? "Please open a model first.";
        }
    };

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
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var result = await context.Bridge.CleanupFormworkAsync(new CleanupRequestDto(
                (args.Str("action") ?? "stats").Trim().ToLowerInvariant(),
                args.Str("faceType")));
            return result?.Error ?? result?.Report ?? "Please open a model first.";
        }
    };

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
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var ids = args.Strings("elementIds");
            if (ids.Count == 0)
            {
                var sel = await context.Bridge.GetSelectionSummaryAsync();
                ids = sel.ElementIds?.ToList() ?? new List<string>();
            }
            var result = await context.Bridge.CopyFormworkParentParamsAsync(
                new CopyParamsRequestDto(ids, args.Strings("paramNames")));
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
                    if (s is { Length: > 0 }) list.Add(s);
                }
                else if (item.ValueKind == JsonValueKind.Number)
                    list.Add(item.ToString());
            }
            return list;
        }
    }
}