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

    public static BuiltInCategory? ParseCategory(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        if (n.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
        {
            if (Enum.TryParse<BuiltInCategory>(n, true, out var bic1)) return bic1;
            return null;
        }
        var map = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
        {
            ["wall"] = BuiltInCategory.OST_Walls, ["walls"] = BuiltInCategory.OST_Walls,
            ["door"] = BuiltInCategory.OST_Doors, ["doors"] = BuiltInCategory.OST_Doors,
            ["window"] = BuiltInCategory.OST_Windows, ["windows"] = BuiltInCategory.OST_Windows,
            ["floor"] = BuiltInCategory.OST_Floors, ["floors"] = BuiltInCategory.OST_Floors,
            ["ceiling"] = BuiltInCategory.OST_Ceilings, ["ceilings"] = BuiltInCategory.OST_Ceilings,
            ["roof"] = BuiltInCategory.OST_Roofs, ["roofs"] = BuiltInCategory.OST_Roofs,
            ["column"] = BuiltInCategory.OST_Columns, ["columns"] = BuiltInCategory.OST_Columns,
            ["beam"] = BuiltInCategory.OST_StructuralFraming, ["beams"] = BuiltInCategory.OST_StructuralFraming,
            ["structural framing"] = BuiltInCategory.OST_StructuralFraming,
            ["furniture"] = BuiltInCategory.OST_Furniture, ["generic model"] = BuiltInCategory.OST_GenericModel,
            ["generic models"] = BuiltInCategory.OST_GenericModel,
            ["level"] = BuiltInCategory.OST_Levels, ["levels"] = BuiltInCategory.OST_Levels,
            ["grid"] = BuiltInCategory.OST_Grids, ["grids"] = BuiltInCategory.OST_Grids,
            ["view"] = BuiltInCategory.OST_Views, ["views"] = BuiltInCategory.OST_Views,
            ["sheet"] = BuiltInCategory.OST_Sheets, ["sheets"] = BuiltInCategory.OST_Sheets,
            ["room"] = BuiltInCategory.OST_Rooms, ["rooms"] = BuiltInCategory.OST_Rooms,
            ["area"] = BuiltInCategory.OST_Areas, ["areas"] = BuiltInCategory.OST_Areas,
            ["pipe"] = BuiltInCategory.OST_PipeCurves, ["pipes"] = BuiltInCategory.OST_PipeCurves,
            ["duct"] = BuiltInCategory.OST_DuctCurves, ["ducts"] = BuiltInCategory.OST_DuctCurves,
            ["rvt link"] = BuiltInCategory.OST_RvtLinks, ["rvt links"] = BuiltInCategory.OST_RvtLinks,
            ["revit link"] = BuiltInCategory.OST_RvtLinks, ["revit links"] = BuiltInCategory.OST_RvtLinks,
            ["link"] = BuiltInCategory.OST_RvtLinks, ["links"] = BuiltInCategory.OST_RvtLinks,
            ["rvtlinks"] = BuiltInCategory.OST_RvtLinks,
        };
        if (map.TryGetValue(n, out var v)) return v;
        var cand = "OST_" + n.Replace(" ", "");
        if (Enum.TryParse<BuiltInCategory>(cand, true, out var bic2)) return bic2;
        return null;
    }

    public static ElementId ParseEid(string s)
    {
        s = s.Trim();
        if (long.TryParse(s, out var l)) return new ElementId(l);
        throw new FormatException($"Invalid ElementId '{s}'");
    }

    public static bool TrySetParameter(Element el, string paramName, string value, out string error)
    {
        error = "";
        Parameter? p = el.LookupParameter(paramName);
        if (p == null && Enum.TryParse<BuiltInParameter>(paramName, true, out var bip))
            p = el.get_Parameter(bip);
        if (p == null && paramName.Equals("Comments", StringComparison.OrdinalIgnoreCase))
            p = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (p == null) { error = $"Parameter '{paramName}' not found on element {el.Id}"; return false; }
        if (p.IsReadOnly) { error = $"Parameter '{paramName}' is read-only on element {el.Id}"; return false; }
        try
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    p.Set(value);
                    break;
                case StorageType.Integer:
                    if (int.TryParse(value, out var iv)) p.Set(iv);
                    else if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) p.Set(1);
                    else if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) p.Set(0);
                    else { error = $"Cannot set integer param '{paramName}' to '{value}'"; return false; }
                    break;
                case StorageType.Double:
                    if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dv))
                        p.Set(dv);
                    else { error = $"Cannot set double param '{paramName}' to '{value}'"; return false; }
                    break;
                case StorageType.ElementId:
                    if (long.TryParse(value, out var eid)) p.Set(new ElementId(eid));
                    else { error = $"Cannot set ElementId param '{paramName}' to '{value}'"; return false; }
                    break;
                default:
                    error = $"Unsupported storage type {p.StorageType}";
                    return false;
            }
        }
        catch (Exception ex) { error = ex.Message; return false; }
        return true;
    }

    public static bool TryParseDate(string s, out DateTime dt)
    {
        s = s.Trim();
        if (DateTime.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt)) return true;
        if (DateTime.TryParseExact(s, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt)) return true;
        if (DateTime.TryParse(s, out dt)) return true;
        dt = default;
        return false;
    }
}
