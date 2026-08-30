using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.OpeningTools;

public sealed class OpeningToolsPlugin : IRevitToolPlugin, IRevitFullAccess
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
                Description = "Place opening voids where ducts/pipes/trays/conduits solid-intersect local walls/floors/beams. Includes MEP inside loaded RVT links (transformed into the active document). Call this tool immediately; do not ask to bind links or to find visible ducts first (query tools cannot see linked MEP). Pass hostIds for the wall/floor/beam the user selected or named. If omitted, current selection must include a host, or MEP, or a Revit link — a section box alone is not a host. Params: mepIds, hostIds, shape (rectangle|circle|auto), clearanceMm, familyName, levelName, limit.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        mepIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
                        hostIds = new { type = "array", items = new { type = "string" }, description = "Wall/floor/beam ids to clash against. Pass the user's selected host. If omitted, uses current selection; does not scan the whole model unless MEP or a Revit link is selected." },
                        shape = new { type = "string", description = "rectangle | circle | auto" },
                        clearanceMm = new { type = "number" },
                        familyName = new { type = "string" },
                        levelName = new { type = "string" },
                        limit = new { type = "integer", description = "Default 50, hard cap 200." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = OpeningArgs.Parse(json);
            var mepIds = args.Strings("mepIds");
            var hostIds = args.Strings("hostIds");
            var shape = args.Str("shape") ?? "auto";
            var clearanceMm = args.Dbl("clearanceMm", 0);
            var familyName = args.Str("familyName");
            var levelName = args.Str("levelName");
            var limit = args.Int("limit", 50);
            if (limit < 1) limit = 50;
            if (limit > 200) limit = 200;
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var uidoc = session.As<UIApplication>().ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    return PlaceOpenings.Run(doc, uidoc!, mepIds, hostIds, shape, clearanceMm, familyName, levelName, limit);
                }
                catch (Exception ex) { return "Failed: " + ex.Message; }
            });
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
                Description = "Cut local hosts with opening voids. Uses current selection. Linked hosts are skipped, not cut.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        openingIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
                        hostIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, solid-intersect local walls/beams/floors." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = OpeningArgs.Parse(json);
            var openingIds = args.Strings("openingIds");
            var hostIds = args.Strings("hostIds");
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var uidoc = session.As<UIApplication>().ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    return CutLocal.Run(doc, uidoc!, openingIds, hostIds);
                }
                catch (Exception ex) { return "Failed: " + ex.Message; }
            });
        }
    };
}
