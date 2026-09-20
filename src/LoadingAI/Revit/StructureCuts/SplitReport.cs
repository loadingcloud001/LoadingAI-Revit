using System.Globalization;

namespace LoadingAI.Revit.StructureCuts;

internal static class SplitReport
{
    /// <summary>Enough ids to confirm the run, not enough to flood the chat pane.</summary>
    private const int IdCap = 12;

    public static string Ids(IEnumerable<string> ids)
    {
        var list = ids.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (list.Count == 0)
            return "none";
        if (list.Count <= IdCap)
            return string.Join(", ", list);
        return string.Join(", ", list.Take(IdCap))
            + " (+" + (list.Count - IdCap).ToString(CultureInfo.InvariantCulture) + " more)";
    }

    /// <summary>
    /// How far a capped run got, and where the next call should start. Omitted when the whole set
    /// fitted in one go from offset 0, so a small selection does not grow a pagination footnote.
    /// </summary>
    public static string Progress(SplitBatch batch)
    {
        if (batch.Total == 0)
            return "";

        if (batch.NextOffset < batch.Total)
        {
            return " Processed " + batch.NextOffset.ToString(CultureInfo.InvariantCulture)
                + " of " + batch.Total.ToString(CultureInfo.InvariantCulture)
                + ". nextOffset = " + batch.NextOffset.ToString(CultureInfo.InvariantCulture) + ".";
        }

        if (batch.Offset > 0)
        {
            return " Processed " + batch.NextOffset.ToString(CultureInfo.InvariantCulture)
                + " of " + batch.Total.ToString(CultureInfo.InvariantCulture) + ".";
        }

        return "";
    }
}
