using Autodesk.Revit.UI;
using LoadingAI.Revit;

namespace LoadingAI.Tools;

public static class ToolGuards
{
    private const int ArgsPreviewChars = 400;
    private const int SummaryChars = 1200;

    /// <summary>
    /// The read-only summary a tool supplies for this call (ADR-0015), or null. Never throws: a
    /// summary is enrichment, so a failing or slow query leaves the raw-arguments fallback.
    /// </summary>
    public static async Task<string?> DescribeAsync(ToolEntry entry, string argsJson)
    {
        var describe = entry.Describe;
        if (describe == null) return null;
        try
        {
            var text = await describe(argsJson);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return text!.Length > SummaryChars ? text.Substring(0, SummaryChars) + "..." : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Dialog body for the generic L2 confirm: the tool's summary, else the arguments.</summary>
    public static string ConfirmBody(string? summary, string argsJson)
    {
        if (!string.IsNullOrWhiteSpace(summary)) return summary!;
        var preview = argsJson.Length > ArgsPreviewChars ? argsJson.Substring(0, ArgsPreviewChars) + "..." : argsJson;
        return preview + "\n\nThis will create an undoable transaction.";
    }

    public static ToolEntry ApplyConfirm(ToolEntry entry, RevitBridge bridge)
    {
        if (entry.Tier == ToolTier.L1 || entry.Tier == ToolTier.L3 || entry.RequiresWriteConfirm)
            return entry;
        // Chat preset form is the confirm; do not add a second TaskDialog.
        if (ToolPresets.HasPresets(entry))
            return entry;

        var inner = entry.Handler;
        var structured = entry.StructuredHandler;
        var name = entry.Definition.Function.Name;
        return new ToolEntry
        {
            Definition = entry.Definition,
            Tier = entry.Tier,
            RequiresWriteConfirm = true,
            Presets = entry.Presets,
            Tags = entry.Tags,
            Describe = entry.Describe,
            Handler = async json =>
            {
                var body = ConfirmBody(await DescribeAsync(entry, json), json);
                bool ok = await bridge.RunAsync(uiApp =>
                {
                    var td = new TaskDialog("LoadingAI for Revit")
                    {
                        MainInstruction = $"Run {name}?",
                        MainContent = body,
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.No
                    };
                    return td.Show() == TaskDialogResult.Yes;
                }, RevitBridge.UserDialogTimeout);
                if (!ok) return "Cancelled by user.";
                return await inner(json);
            },
            StructuredHandler = structured == null
                ? null
                : async argsJson =>
                {
                    var body = ConfirmBody(await DescribeAsync(entry, argsJson), argsJson);
                    bool ok = await bridge.RunAsync(uiApp =>
                    {
                        var td = new TaskDialog("LoadingAI for Revit")
                        {
                            MainInstruction = $"Run {name}?",
                            MainContent = body,
                            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                            DefaultButton = TaskDialogResult.No
                        };
                        return td.Show() == TaskDialogResult.Yes;
                    }, RevitBridge.UserDialogTimeout);
                    if (!ok) return ToolExecutionResult.Cancelled("Cancelled by user.");
                    return await structured(argsJson);
                }
        };
    }
    public static IEnumerable<ToolEntry> ApplyAll(IEnumerable<ToolEntry> entries, RevitBridge bridge)
        => entries.Select(e => ApplyConfirm(e, bridge));
}
