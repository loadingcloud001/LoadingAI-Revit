using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitAiPoc.Commands;

[Transaction(TransactionMode.Manual)]
public class ShowChatCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            // Ensure context is set
            RevitAiPoc.Revit.RevitContext.Set(commandData.Application);

            var dpid = new DockablePaneId(App.PaneId);
            var pane = commandData.Application.GetDockablePane(dpid);
            if (pane.IsShown()) pane.Hide();
            else pane.Show();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}
