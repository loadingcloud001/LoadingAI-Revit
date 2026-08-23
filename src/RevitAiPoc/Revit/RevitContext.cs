using Autodesk.Revit.UI;

namespace RevitAiPoc.Revit;

internal static class RevitContext
{
    public static UIApplication? UiApplication { get; private set; }

    public static void Set(UIApplication uiApp) => UiApplication = uiApp;

    public static void Clear() => UiApplication = null;
}
