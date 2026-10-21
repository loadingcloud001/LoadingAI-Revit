using Autodesk.Revit.DB;

namespace LoadingAI.Tools;

/// <summary>
/// Revit-bound parse/set helpers used by the host.
/// Lives outside the add-in so plugins do not reference LoadingAI.dll.
/// </summary>
public static class RevitParse
{
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

    /// <summary>
    /// The tool unit a numeric parameter uses. Any spec that Revit can express in millimetres is a
    /// length, in square metres an area, in cubic metres a volume, in degrees an angle; everything
    /// else (counts, ratios, currency, slope) is plain and never converted.
    /// </summary>
    public static LoadingAI.Revit.ParamUnitKind UnitKindOf(Parameter p)
    {
        try
        {
            var spec = p.Definition.GetDataType();
            if (spec == null || !UnitUtils.IsMeasurableSpec(spec)) return LoadingAI.Revit.ParamUnitKind.Plain;
            var valid = UnitUtils.GetValidUnits(spec);
            if (valid.Contains(UnitTypeId.Millimeters)) return LoadingAI.Revit.ParamUnitKind.Length;
            if (valid.Contains(UnitTypeId.SquareMeters)) return LoadingAI.Revit.ParamUnitKind.Area;
            if (valid.Contains(UnitTypeId.CubicMeters)) return LoadingAI.Revit.ParamUnitKind.Volume;
            if (valid.Contains(UnitTypeId.Degrees)) return LoadingAI.Revit.ParamUnitKind.Angle;
        }
        catch
        {
            // A parameter whose spec cannot be read is treated as plain, the pre-existing behaviour.
        }
        return LoadingAI.Revit.ParamUnitKind.Plain;
    }

    /// <summary>A Double parameter as the tool surface shows it: millimetres, m², m³ or degrees, with the unit.</summary>
    public static string FormatDouble(Parameter p)
        => LoadingAI.Revit.ParamUnits.Format(UnitKindOf(p), p.AsDouble());

    public static Parameter? FindParameter(Element el, string paramName)
    {
        Parameter? p = el.LookupParameter(paramName);
        if (p == null && Enum.TryParse<BuiltInParameter>(paramName, true, out var bip))
            p = el.get_Parameter(bip);
        if (p == null && paramName.Equals("Comments", StringComparison.OrdinalIgnoreCase))
            p = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        return p;
    }

    /// <summary>The same text <c>ExportParams</c> writes for this parameter. Empty when there is no parameter.</summary>
    public static string DisplayParameter(Parameter? p)
    {
        if (p == null) return "";
        return p.StorageType switch
        {
            StorageType.String => p.AsString() ?? "",
            StorageType.Double => LoadingAI.Revit.ParamUnits.FormatNumber(UnitKindOf(p), p.AsDouble()),
            _ => p.AsValueString() ?? p.AsString() ?? ""
        };
    }

    /// <summary>
    /// Says whether <paramref name="value"/> could be stored, and what the parameter shows now.
    /// Does not call <c>Parameter.Set</c>.
    /// </summary>
    public static bool TryPreviewSet(Element el, string paramName, string value, out string current, out string error)
    {
        current = "";
        error = "";
        var p = FindParameter(el, paramName);
        if (p == null) { error = $"Parameter '{paramName}' not found on element {el.Id}"; return false; }
        if (p.IsReadOnly) { error = $"Parameter '{paramName}' is read-only on element {el.Id}"; return false; }
        current = DisplayParameter(p);
        switch (p.StorageType)
        {
            case StorageType.String:
                return true;
            case StorageType.Integer:
                if (int.TryParse(value, out _)
                    || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("false", StringComparison.OrdinalIgnoreCase))
                    return true;
                error = $"Cannot set integer param '{paramName}' to '{value}'";
                return false;
            case StorageType.Double:
                if (!LoadingAI.Revit.ParamUnits.TryParse(UnitKindOf(p), value, out _, out var unitError))
                {
                    error = $"Cannot set '{paramName}' to '{value}': {unitError}";
                    return false;
                }
                return true;
            case StorageType.ElementId:
                if (long.TryParse(value, out _)) return true;
                error = $"Cannot set ElementId param '{paramName}' to '{value}'";
                return false;
            default:
                error = $"Unsupported storage type {p.StorageType}";
                return false;
        }
    }

    /// <summary>
    /// The value <see cref="TryCopyParameter"/> would write, without writing it.
    /// <paramref name="current"/> is the target as exported; <paramref name="next"/> is the source.
    /// </summary>
    public static bool TryPreviewCopy(Element el, string sourceName, string targetName, out string current, out string next, out string error)
    {
        current = "";
        next = "";
        error = "";
        var src = el.LookupParameter(sourceName);
        if (src == null) { error = $"Parameter '{sourceName}' not found on element {el.Id}"; return false; }
        var dst = FindParameter(el, targetName);
        if (dst == null) { error = $"Parameter '{targetName}' not found on element {el.Id}"; return false; }
        if (dst.IsReadOnly) { error = $"Parameter '{targetName}' is read-only on element {el.Id}"; return false; }
        current = DisplayParameter(dst);

        if (src.StorageType == StorageType.Double
            && dst.StorageType == StorageType.Double
            && UnitKindOf(src) == UnitKindOf(dst))
        {
            next = DisplayParameter(src);
            return true;
        }

        next = src.StorageType switch
        {
            StorageType.String => src.AsString() ?? "",
            StorageType.Double => LoadingAI.Revit.ParamUnits.Format(UnitKindOf(src), src.AsDouble()),
            _ => src.AsValueString() ?? ""
        };
        if (!TryPreviewSet(el, targetName, next, out _, out error)) return false;
        return true;
    }

    public static bool TrySetParameter(Element el, string paramName, string value, out string error)
    {
        error = "";
        var p = FindParameter(el, paramName);
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
                {
                    // Revit's Set(double) takes internal units (feet, ft², radians). The tool surface is
                    // millimetres, m², m³ and degrees, so convert here instead of storing the raw number.
                    var kind = UnitKindOf(p);
                    if (!LoadingAI.Revit.ParamUnits.TryParse(kind, value, out var internalValue, out var unitError))
                    {
                        error = $"Cannot set '{paramName}' to '{value}': {unitError}";
                        return false;
                    }
                    p.Set(internalValue);
                    break;
                }
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

    /// <summary>
    /// Copies one parameter onto another on the same element. Two numeric parameters of the same
    /// unit kind are copied as numbers, so no unit text is parsed and nothing is lost to rounding;
    /// every other pairing goes through the text path of <see cref="TrySetParameter"/>.
    /// </summary>
    public static bool TryCopyParameter(Element el, string sourceName, string targetName, out string error)
    {
        error = "";
        var src = el.LookupParameter(sourceName);
        if (src == null) { error = $"Parameter '{sourceName}' not found on element {el.Id}"; return false; }

        if (src.StorageType == StorageType.Double)
        {
            var dst = el.LookupParameter(targetName);
            if (dst != null && !dst.IsReadOnly && dst.StorageType == StorageType.Double
                && UnitKindOf(src) == UnitKindOf(dst))
            {
                try { dst.Set(src.AsDouble()); return true; }
                catch (Exception ex) { error = ex.Message; return false; }
            }
        }

        var text = src.StorageType switch
        {
            StorageType.String => src.AsString() ?? "",
            StorageType.Double => LoadingAI.Revit.ParamUnits.Format(UnitKindOf(src), src.AsDouble()),
            _ => src.AsValueString() ?? ""
        };
        return TrySetParameter(el, targetName, text, out error);
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
