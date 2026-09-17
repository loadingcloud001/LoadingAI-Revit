using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Revit;
using LoadingAI.Plugins;

namespace LoadingAI.Tools;

public static class DocumentTools
{
    public static IEnumerable<ToolEntry> Create(IPluginContext context)
    {
        yield return CreateSaveDocument(context);
        yield return CreateReloadLinks(context);
        yield return CreateWorksetAssign(context);
    }

    internal static ToolEntry CreateSaveDocument(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "save_document",
                    Description = "Save the active Revit document. L3. Host confirms before write. Fails if the document has never been saved to a path.",
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
                var result = await context.Bridge.SaveDocumentAsync();
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateReloadLinks(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "reload_links",
                    Description = "Reload all RVT links (pyRevit: Reload Links). No params.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                var result = await context.Bridge.ReloadLinksAsync();
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateWorksetAssign(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "workset_assign",
                    Description = "Assign elements to workset (BIMOne). Params: elementIds, worksetName.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" } },
                            worksetName = new { type = "string" }
                        },
                        required = new[] { "elementIds", "worksetName" }
                    }
                }
            },
            Handler = async json =>
            {
                List<string> ids;
                string ws;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    ids = d.RootElement.GetProperty("elementIds").EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => v.GetString()!)
                        .ToList();
                    ws = d.RootElement.GetProperty("worksetName").GetString() ?? "";
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }

                var result = await context.Bridge.AssignWorksetAsync(ids, ws);
                return result.Message;
            }
        };
    }
}
