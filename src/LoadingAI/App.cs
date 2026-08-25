using Autodesk.Revit.UI;
using LoadingAI.Revit;
using LoadingAI.Ui;

namespace LoadingAI;

public class App : IExternalApplication
{
    public static readonly Guid PaneId = new("A7B2C3D4-E5F6-4A1B-9C2D-3E4F5A6B7C8D");
    public static RevitBridge? Bridge { get; private set; }

    public Result OnStartup(UIControlledApplication application)
    {
        // UIApplication not available at UIControlledApplication stage; it will be set
        // on first ExternalEvent execution or command via RevitContext.Set
        Bridge = new RevitBridge();

        // Ribbon: Add-Ins tab
        try
        {
            var panel = application.CreateRibbonPanel("AI POC");
            var asmPath = typeof(App).Assembly.Location;
            var btnData = new PushButtonData("AIChat", "AI Chat", asmPath, typeof(Commands.ShowChatCommand).FullName!);
            btnData.ToolTip = "Open LoadingAI for Revit chat";
            btnData.LongDescription = "Chat with LLM and run tools on the active model.";
            panel.AddItem(btnData);
        }
        catch (Exception ex)
        {
            HostLog.Write($"ribbon failed: {ex}");
        }

        // Dockable pane
        try
        {
            var dpid = new DockablePaneId(PaneId);
            application.RegisterDockablePane(dpid, "AI Chat", new ChatPaneProvider());
        }
        catch (Exception ex)
        {
            TaskDialog.Show("LoadingAI for Revit", $"Failed to register pane: {ex.Message}");
        }

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        Bridge?.Dispose();
        Bridge = null;
        RevitContext.Clear();
        return Result.Succeeded;
    }
}

internal class ChatPaneProvider : IDockablePaneProvider
{
    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = new HostPage();
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Right,
            TabBehind = DockablePanes.BuiltInDockablePanes.ProjectBrowser
        };
        data.VisibleByDefault = false;
    }
}
