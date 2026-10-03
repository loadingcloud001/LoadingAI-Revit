using System.Globalization;

namespace LoadingAI.Revit;

/// <summary>
/// Cells for the CSV files the export tools write. Parameter values come from the model, so a value
/// such as <c>=cmd|...</c> must not become a formula when the file is opened in a spreadsheet, and a
/// number must not pick up the machine's decimal comma.
/// </summary>
public static class CsvText
{
    /// <summary>
    /// One quoted text cell. A cell that starts like a formula (<c>= @ tab CR</c>, or <c>+ -</c> not
    /// followed by a plain number) gets a leading apostrophe.
    /// </summary>
    public static string Cell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && StartsLikeFormula(v)) v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    public static string Number(double value, string format = "0.###")
        => value.ToString(format, CultureInfo.InvariantCulture);

    public static string Row(IEnumerable<string?> cells) => string.Join(",", cells.Select(Cell));

    private static bool StartsLikeFormula(string v)
    {
        var c = v[0];
        if (c == '=' || c == '@' || c == '\t' || c == '\r') return true;
        if (c == '+' || c == '-')
            return !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        return false;
    }
}
