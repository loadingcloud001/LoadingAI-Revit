using Autodesk.Revit.UI;
using RevitAiPoc.Revit;

namespace RevitAiPoc.Tools;

public static class ToolGuards
{
    public static ToolEntry ApplyConfirm(ToolEntry entry, RevitBridge bridge)
    {
        if (entry.Tier == ToolTier.L1 || entry.RequiresWriteConfirm)
            return entry;

        var inner = entry.Handler;
        var name = entry.Definition.Function.Name;
        return new ToolEntry
        {
            Definition = entry.Definition,
            Tier = entry.Tier,
            RequiresWriteConfirm = true,
            Handler = async json =>
            {
                var preview = json.Length > 400 ? json[..400] + "..." : json;
                bool ok = await bridge.RunAsync(uiApp =>
                {
                    var td = new TaskDialog("Revit AI POC")
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
            }
        };
    }

    public static IEnumerable<ToolEntry> ApplyAll(IEnumerable<ToolEntry> entries, RevitBridge bridge)
        => entries.Select(e => ApplyConfirm(e, bridge));
}
