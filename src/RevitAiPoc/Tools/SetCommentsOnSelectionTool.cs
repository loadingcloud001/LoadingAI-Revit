using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAiPoc.Llm;
using RevitAiPoc.Revit;

namespace RevitAiPoc.Tools;

public static class SetCommentsOnSelectionTool
{
    public static ToolEntry Create(RevitBridge bridge, Func<int, string, Task<bool>> confirmFunc)
    {
        var def = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "set_comments_on_selection",
                Description = "Set the Comments parameter (ALL_MODEL_INSTANCE_COMMENTS) on the currently selected elements. Use when user wants to tag, label, or set comments on selection. Requires user confirmation before writing.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        text = new { type = "string", description = "Text to write into the Comments parameter" }
                    },
                    required = new[] { "text" }
                }
            }
        };

        return new ToolEntry
        {
            Definition = def,
            RequiresWriteConfirm = true,
            Handler = async jsonArgs =>
            {
                string text;
                try
                {
                    using var doc = JsonDocument.Parse(jsonArgs);
                    if (!doc.RootElement.TryGetProperty("text", out var p))
                        return "Missing required parameter 'text'.";
                    text = p.GetString() ?? "";
                }
                catch (Exception ex)
                {
                    return $"Invalid arguments: {ex.Message}";
                }

                // First, get selection info (read)
                var pre = await bridge.RunAsync(uiApp =>
                {
                    var uidoc = uiApp.ActiveUIDocument;
                    if (uidoc == null) return (error: "Please open a model first.", count: 0, ids: Array.Empty<string>());
                    var ids = uidoc.Selection.GetElementIds();
                    if (ids.Count == 0) return (error: "Please select elements first.", count: 0, ids: Array.Empty<string>());
                    return (error: "", count: ids.Count, ids: ids.Select(i => i.ToString()).ToArray());
                });

                if (!string.IsNullOrEmpty(pre.error)) return pre.error;
                int count = pre.count;

                // Confirm on UI thread (TaskDialog must be on Revit UI thread). Use bridge to show dialog? But TaskDialog needs to be on UI thread anyway.
                // So we marshal confirm via bridge as well.
                bool confirmed = await bridge.RunAsync(uiApp =>
                {
                    // Need to run on UI thread; bridge ensures that.
                    var td = new TaskDialog("Revit AI POC")
                    {
                        MainInstruction = $"Set Comments on {count} selected element(s)?",
                        MainContent = $"New Comments value:\n\"{text}\"\n\nThis will create an undoable transaction.",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.No
                    };
                    var r = td.Show();
                    return r == TaskDialogResult.Yes;
                });

                // Also allow outer confirmFunc for WPF fallback (if caller wants to handle via WPF)
                // If bridge dialog was shown, we already have confirmed. But to respect plan's TaskDialog, we keep bridge dialog.
                // The confirmFunc is kept for future WebView2 path; ignore here since bridge already asked.
                // To also support WPF-driven confirm, we could skip bridge dialog and use confirmFunc. For now we did bridge.

                if (!confirmed)
                    return "Cancelled by user.";

                // Now do the write transaction
                var writeResult = await bridge.RunAsync(uiApp =>
                {
                    var uidoc = uiApp.ActiveUIDocument;
                    var doc = uidoc?.Document;
                    if (doc == null) return "Please open a model first.";
                    var selIds = uidoc!.Selection.GetElementIds();
                    if (selIds.Count == 0) return "Please select elements first.";

                    int updated = 0;
                    int skipped = 0;
                    List<string> skippedIds = new();

                    using (var tx = new Transaction(doc, "Revit AI POC: set comments"))
                    {
                        tx.Start();
                        try
                        {
                            foreach (var eid in selIds)
                            {
                                var el = doc.GetElement(eid);
                                if (el == null) { skipped++; continue; }
                                var param = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                                // BuiltIn param may be missing or read-only or wrong type
                                if (param == null || param.IsReadOnly || param.StorageType != StorageType.String)
                                {
                                    // Try lookup by name fallback
                                    param = el.LookupParameter("Comments");
                                }
                                if (param == null || param.IsReadOnly || param.StorageType != StorageType.String)
                                {
                                    skipped++;
                                    skippedIds.Add(eid.ToString());
                                    continue;
                                }
                                param.Set(text);
                                updated++;
                            }
                            tx.Commit();
                        }
                        catch
                        {
                            try { tx.RollBack(); } catch { }
                            throw;
                        }
                    }

                    var msg = $"Updated {updated} element(s).";
                    if (skipped > 0) msg += $" Skipped {skipped} (no Comments): {string.Join(", ", skippedIds.Take(10))}" + (skipped > 10 ? "..." : "");
                    return msg;
                });

                return writeResult;
            }
        };
    }
}
