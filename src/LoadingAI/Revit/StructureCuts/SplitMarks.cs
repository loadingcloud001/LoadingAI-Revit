using System.Globalization;

namespace LoadingAI.Revit.StructureCuts;

internal static class SplitMarks
{
    /// <summary>
    /// Mark for the <paramref name="piece"/>-th piece (0-based, creation order). Piece 0 keeps the
    /// original mark; later pieces are suffixed so the split stays traceable in a schedule.
    /// </summary>
    public static string? For(string? original, int piece)
        => piece <= 0 || string.IsNullOrEmpty(original)
            ? original
            : original + "-" + piece.ToString(CultureInfo.InvariantCulture);
}
