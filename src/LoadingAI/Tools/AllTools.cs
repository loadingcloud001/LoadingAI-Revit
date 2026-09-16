using Autodesk.Revit.DB;

namespace LoadingAI.Tools;

/// <summary>
/// Host-only parse wrappers used by AdapterRevitBridge.
/// Tool factories live in Plugins/core-tools.
/// </summary>
public static class AllTools
{
    public static BuiltInCategory? ParseCategory(string? name) => RevitParse.ParseCategory(name);

    public static ElementId ParseEid(string s) => RevitParse.ParseEid(s);

    public static bool TrySetParameter(Element el, string paramName, string value, out string error)
        => RevitParse.TrySetParameter(el, paramName, value, out error);

    public static bool TryParseDate(string s, out DateTime dt) => RevitParse.TryParseDate(s, out dt);
}
