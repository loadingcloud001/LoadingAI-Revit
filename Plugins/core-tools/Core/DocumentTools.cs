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
            Handler = async _ => (await SaveDocumentAsync(context)).Text,
            StructuredHandler = _ => SaveDocumentAsync(context)
        };
    }

    internal static ToolEntry CreateReloadLinks(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Describe = _ => Task.FromResult<string?>(
                "Reload every Revit link from its saved path.\nThis is not a model edit, so Ctrl+Z will not reverse it."),
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "reload_links",
                    Description = "Reload every Revit link from its saved path. Reports how many loaded and which failed. Not an undoable edit.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ => (await ReloadLinksAsync(context)).Text,
            StructuredHandler = _ => ReloadLinksAsync(context)
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
            Handler = async json => (await WorksetAssignAsync(context, json)).Text,
            StructuredHandler = json => WorksetAssignAsync(context, json)
        };
    }

    private static async Task<ToolExecutionResult> SaveDocumentAsync(IPluginContext context)
        => FromCommand(await context.Bridge.SaveDocumentAsync());

    private static async Task<ToolExecutionResult> ReloadLinksAsync(IPluginContext context)
        => FromCommand(await context.Bridge.ReloadLinksAsync());

    private static async Task<ToolExecutionResult> WorksetAssignAsync(IPluginContext context, string json)
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
        catch (Exception ex)
        {
            // The legacy catch returned "Invalid args: {msg}" with no "Failed:" prefix, so a
            // malformed argument read as a committed write. ToolArgumentValidator normally
            // rejects this first; the guard stays for a direct Handler call.
            return ToolExecutionResult.Failed($"Invalid args: {ex.Message}", ToolWriteCertainty.NotCommitted);
        }

        return FromCommand(await context.Bridge.AssignWorksetAsync(ids, ws));
    }

    /// <summary>
    /// Status comes from CommandResultDto, whose Ok and AffectedCount the legacy handlers both
    /// discarded in favour of Message, so FromLegacy could only guess from the prose.
    ///
    /// Ok=false covers every refusal: no document, a save with no path, a non-workshared
    /// document, a workset that does not exist, and the adapter's "Failed: {message}" catch.
    /// All of them wrote nothing, so they are Failed + NotCommitted and force a turn rollback.
    ///
    /// Ok=true is a commit. AffectedCount carries how much: the links reloaded, the elements
    /// assigned. A partial like "Reloaded 2/5 RVT links" is Ok=true with AffectedCount 2, and
    /// CommandResultDto has no requested total, so the total lives only in the text. It is not
    /// reported as a structured Partial because that would mean parsing prose to invent a
    /// failure count. Same ceiling qs-formwork has.
    /// </summary>
    private static ToolExecutionResult FromCommand(CommandResultDto result)
        => result.Ok
            ? ToolExecutionResult.Succeeded(result.Message, ToolWriteCertainty.Committed, result.AffectedCount)
            : ToolExecutionResult.Failed(result.Message, ToolWriteCertainty.NotCommitted);
}
