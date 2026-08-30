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
                Description = "Place opening voids from MEP-host clash. Uses current selection. Params: mepIds, hostIds, shape (rectangle|circle|auto), clearanceMm, familyName, levelName, limit.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        mepIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
                        hostIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection or bbox-overlap hosts." },
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
        Handler = _ => Task.FromResult("Not implemented: select MEP and host, then retry after Step 1 ships.")
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
        Handler = _ => Task.FromResult("Not implemented: select openings and local hosts, then retry after Step 2 ships.")
    };
}
