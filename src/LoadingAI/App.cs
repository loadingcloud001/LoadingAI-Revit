using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Revit;
using LoadingAI.Ui;
using System.IO;
using System.Windows.Media.Imaging;

namespace LoadingAI;

public class App : IExternalApplication
{
    public static readonly Guid PaneId = new("A7B2C3D4-E5F6-4A1B-9C2D-3E4F5A6B7C8D");
    public static RevitBridge? Bridge { get; private set; }

    private static BitmapImage? LoadIcon(string name)
    {
        var asm = typeof(App).Assembly;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = stream;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public Result OnStartup(UIControlledApplication application)
    {
        ProviderSettings.KeyProtector = WinKeyProtector.Instance;
        AutoApproveGate.Enabled = ProviderSettings.Load().AutoApprove;
        application.ControlledApplication.FailuresProcessing += AutoApproveFailures.OnFailures;

        // UIApplication not available at UIControlledApplication stage; it will be set
        // on first ExternalEvent execution or command via RevitContext.Set
        Bridge = new RevitBridge();

        // Ribbon: Add-Ins tab
        try
        {
            var panel = application.CreateRibbonPanel("LoadingAI");
            var asmPath = typeof(App).Assembly.Location;
            var btnData = new PushButtonData("AIChat", "Open Chat", asmPath, typeof(Commands.ShowChatCommand).FullName!);
            btnData.ToolTip = "Open LoadingAI for Revit chat";
            btnData.LongDescription = "Chat with the LLM and run tools on the active model.";
            btnData.LargeImage = LoadIcon("LoadingAI.Resources.Ribbon32.png");
            btnData.Image = LoadIcon("LoadingAI.Resources.Ribbon16.png");
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
            application.RegisterDockablePane(dpid, "LoadingAI", new ChatPaneProvider());
        }
        catch (Exception ex)
        {
            TaskDialog.Show("LoadingAI for Revit", $"Failed to register pane: {ex.Message}");
        }

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.ControlledApplication.FailuresProcessing -= AutoApproveFailures.OnFailures;
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
            TabBehind = DockablePanes.BuiltInDockablePanes.ProjectBrowser,
            MinimumWidth = 360
        };
        data.VisibleByDefault = false;
    }
}
