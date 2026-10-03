using System.Globalization;
using System.Text.RegularExpressions;

namespace LoadingAI.Revit;

/// <summary>
/// What a numeric Revit parameter measures, as far as the LLM-facing unit convention is concerned.
/// Revit stores lengths in feet, areas in square feet, volumes in cubic feet and angles in radians.
/// The tool surface speaks millimetres, m², m³ and degrees (<see cref="Metric"/>, system prompt).
/// </summary>
public enum ParamUnitKind
{
    /// <summary>Counts, ratios and anything without a length-based unit. Never converted.</summary>
    Plain,
    Length,
    Area,
    Volume,
    Angle
}

/// <summary>
/// One place that converts a parameter value between the tool unit and the Revit internal unit,
/// formats it with its unit, and parses what a model sends. Revit-free so CI can test it.
/// </summary>
public static class ParamUnits
{
    private static readonly Regex Grouped =
        new(@"^[+-]?\d{1,3}(,\d{3})+(\.\d+)?$", RegexOptions.CultureInvariant);

    public static double ToInternal(ParamUnitKind kind, double toolValue) => kind switch
    {
        ParamUnitKind.Length => Metric.MmToFt(toolValue),
        ParamUnitKind.Area => toolValue / Metric.SqMPerSqFt,
        ParamUnitKind.Volume => toolValue / Metric.CuMPerCuFt,
        ParamUnitKind.Angle => toolValue * Math.PI / 180.0,
        _ => toolValue
    };

    public static double FromInternal(ParamUnitKind kind, double internalValue) => kind switch
    {
        ParamUnitKind.Length => Metric.FtToMm(internalValue),
        ParamUnitKind.Area => Metric.Ft2ToM2(internalValue),
        ParamUnitKind.Volume => Metric.Ft3ToM3(internalValue),
        ParamUnitKind.Angle => internalValue * 180.0 / Math.PI,
        _ => internalValue
    };

    public static string Suffix(ParamUnitKind kind) => kind switch
    {
        ParamUnitKind.Length => " mm",
        ParamUnitKind.Area => " m²",
        ParamUnitKind.Volume => " m³",
        ParamUnitKind.Angle => "°",
        _ => ""
    };

    /// <summary>The value in the tool unit as a plain invariant number: the form CSV exports use.</summary>
    public static string FormatNumber(ParamUnitKind kind, double internalValue)
    {
        var value = Math.Round(FromInternal(kind, internalValue), 3);
        if (value == 0) value = 0; // normalises negative zero, which prints as "-0" on newer runtimes
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>The value with its unit, e.g. <c>3000 mm</c>. Culture-invariant.</summary>
    public static string Format(ParamUnitKind kind, double internalValue)
        => FormatNumber(kind, internalValue) + Suffix(kind);

    /// <summary>
    /// Reads a value a model sent for a numeric parameter and returns it in Revit internal units.
    /// A bare number is read in the tool unit. A recognised unit suffix is converted. Anything else
    /// is refused with a message that names the accepted units, instead of being guessed.
    /// </summary>
    public static bool TryParse(ParamUnitKind kind, string? text, out double internalValue, out string error)
    {
        internalValue = 0;
        error = "";
        var s = (text ?? "").Trim();
        if (s.Length == 0)
        {
            error = "the value is empty.";
            return false;
        }

        var i = 0;
        if (s[i] == '+' || s[i] == '-') i++;
        while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.' || s[i] == ',')) i++;
        var numberText = s.Substring(0, i);
        var unitText = s.Substring(i).Trim();

        if (numberText.IndexOf(',') >= 0 && !Grouped.IsMatch(numberText))
        {
            error = $"'{s}' is not a number; use '.' as the decimal separator.";
            return false;
        }

        if (!double.TryParse(numberText.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || double.IsNaN(number) || double.IsInfinity(number))
        {
            error = $"'{s}' is not a number.";
            return false;
        }

        var factor = 1.0;
        if (unitText.Length > 0 && !TryFactor(kind, unitText, out factor))
        {
            error = $"the unit '{unitText}' is not accepted here. {Expected(kind)}";
            return false;
        }

        internalValue = ToInternal(kind, number * factor);
        return true;
    }

    private static string Expected(ParamUnitKind kind) => kind switch
    {
        ParamUnitKind.Length => "Use millimetres (mm), or add mm, cm, m, ft or in.",
        ParamUnitKind.Area => "Use m² (also m2, mm², ft²).",
        ParamUnitKind.Volume => "Use m³ (also m3, ft³).",
        ParamUnitKind.Angle => "Use degrees (° or deg) or rad.",
        _ => "This parameter has no unit; send a plain number."
    };

    private static bool TryFactor(ParamUnitKind kind, string unit, out double toToolUnit)
    {
        toToolUnit = 1.0;
        var u = unit.Trim().ToLowerInvariant();
        switch (kind)
        {
            case ParamUnitKind.Length:
                switch (u)
                {
                    case "mm": toToolUnit = 1.0; return true;
                    case "cm": toToolUnit = 10.0; return true;
                    case "m": toToolUnit = 1000.0; return true;
                    case "ft": case "'": toToolUnit = Metric.MmPerFoot; return true;
                    case "in": case "\"": toToolUnit = 25.4; return true;
                }
                return false;
            case ParamUnitKind.Area:
                switch (u)
                {
                    case "m²": case "m2": case "sqm": toToolUnit = 1.0; return true;
                    case "mm²": case "mm2": toToolUnit = 1e-6; return true;
                    case "ft²": case "ft2": case "sqft": toToolUnit = Metric.SqMPerSqFt; return true;
                }
                return false;
            case ParamUnitKind.Volume:
                switch (u)
                {
                    case "m³": case "m3": case "cum": toToolUnit = 1.0; return true;
                    case "ft³": case "ft3": case "cuft": toToolUnit = Metric.CuMPerCuFt; return true;
                }
                return false;
            case ParamUnitKind.Angle:
                switch (u)
                {
                    case "°": case "deg": case "degree": case "degrees": toToolUnit = 1.0; return true;
                    case "rad": case "radian": case "radians": toToolUnit = 180.0 / Math.PI; return true;
                }
                return false;
            default:
                return false;
        }
    }
}
