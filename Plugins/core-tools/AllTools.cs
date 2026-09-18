using LoadingAI.Plugins;

namespace LoadingAI.Tools;

public static class AllTools
{
    public static IEnumerable<ToolEntry> CreateBridge(IPluginContext context)
    {
        foreach (var t in QueryTools.Create(context)) yield return t;
        foreach (var t in ViewTools.Create(context)) yield return t;
        foreach (var t in ParamTools.Create(context)) yield return t;
        foreach (var t in GeometryTools.Create(context)) yield return t;
        foreach (var t in DocumentTools.Create(context)) yield return t;
    }
}
