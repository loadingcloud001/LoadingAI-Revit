using Autodesk.Revit.DB;
using LoadingAI.Revit;
using LoadingAI.Tools.Compat;
using LoadingAI.Plugins;

namespace LoadingAI.Tools;

public static class AllTools
{
    public static IEnumerable<ToolEntry> CreateAll(IPluginContext context)
    {
        foreach (var t in QueryTools.Create(context)) yield return t;
        foreach (var t in ViewTools.Create(context)) yield return t;
        foreach (var t in ParamTools.Create(context)) yield return t;
        foreach (var t in GeometryTools.Create(context)) yield return t;
        foreach (var t in QueryHealthTools.Create(context)) yield return t;
        foreach (var t in ParamExcelTools.Create(context)) yield return t;
        foreach (var t in SheetDocTools.Create(context)) yield return t;
        foreach (var t in GeometryCreateTools.Create(context)) yield return t;
        foreach (var t in WorksharingTools.Create(context)) yield return t;
        foreach (var t in IfcCostTools.Create(context)) yield return t;
    }

    public static BuiltInCategory? ParseCategory(string? name) => RevitParse.ParseCategory(name);

    public static ElementId ParseEid(string s) => RevitParse.ParseEid(s);

    public static bool TrySetParameter(Element el, string paramName, string value, out string error)
        => RevitParse.TrySetParameter(el, paramName, value, out error);

    public static bool TryParseDate(string s, out DateTime dt) => RevitParse.TryParseDate(s, out dt);
}
