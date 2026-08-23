using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAiPoc.Llm;
using RevitAiPoc.Revit;

namespace RevitAiPoc.Tools;

public static class GetModelSummaryTool
{
    public static ToolEntry Create(RevitBridge bridge)
    {
        var def = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "get_model_summary",
                Description = "Get summary of the active Revit model: title, path, view count, wall/door/window instance counts. Call this when user asks how many elements are in the model or for a model overview. No parameters.",
                Parameters = new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>()
                }
            }
        };

        return new ToolEntry
        {
            Definition = def,
            RequiresWriteConfirm = false,
            Handler = async _ =>
            {
                var result = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        var title = doc.Title;
                        var path = doc.PathName;
                        if (string.IsNullOrEmpty(path)) path = "(unsaved)";

                        // Views: count all View elements (excluding templates)
                        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => !v.IsTemplate);

                        int walls = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().ToElementIds().Count;
                        int doors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().ToElementIds().Count;
                        int windows = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType().ToElementIds().Count;

                        return $"Model: {title}\nPath: {path}\nViews: {views}\nWalls: {walls}\nDoors: {doors}\nWindows: {windows}";
                    }
                    catch (Exception ex)
                    {
                        return $"Failed to read model: {ex.Message}";
                    }
                });
                return result;
            }
        };
    }
}
