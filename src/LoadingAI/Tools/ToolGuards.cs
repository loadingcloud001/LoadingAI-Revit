using Autodesk.Revit.UI;
using LoadingAI.Revit;

namespace LoadingAI.Tools;

public static class ToolGuards
{
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
            Handler = async json =>
            {
                var preview = json.Length > 400 ? json.Substring(0, 400) + "..." : json;
                bool ok = await bridge.RunAsync(uiApp =>
                {
                    var td = new TaskDialog("LoadingAI for Revit")
                    {
                        MainInstruction = $"Run {name}?",
                        MainContent = preview + "\n\nThis will create an undoable transaction.",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.No
                    };
                    return td.Show() == TaskDialogResult.Yes;
                });
                if (!ok) return "Cancelled by user.";
                return await inner(json);
            },
            StructuredHandler = structured == null
                ? null
                : async argsJson =>
                {
                    var preview = argsJson.Length > 400 ? argsJson.Substring(0, 400) + "..." : argsJson;
                    bool ok = await bridge.RunAsync(uiApp =>
                    {
                        var td = new TaskDialog("LoadingAI for Revit")
                        {
                            MainInstruction = $"Run {name}?",
                            MainContent = preview + "\n\nThis will create an undoable transaction.",
                            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                            DefaultButton = TaskDialogResult.No
                        };
                        return td.Show() == TaskDialogResult.Yes;
                    });
                    if (!ok) return ToolExecutionResult.Cancelled("Cancelled by user.");
                    return await structured(argsJson);
                }
        };
    }
    public static IEnumerable<ToolEntry> ApplyAll(IEnumerable<ToolEntry> entries, RevitBridge bridge)
        => entries.Select(e => ApplyConfirm(e, bridge));
}
