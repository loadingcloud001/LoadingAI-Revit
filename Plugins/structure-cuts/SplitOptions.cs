using System.Globalization;
using System.Linq;

namespace LoadingAI.Plugins.StructureCuts;

/// <summary>Shared knobs for every split tool, resolved once from the tool arguments.</summary>
internal sealed class SplitOptions
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const string DefaultScope = "selection";

    /// <summary>Max elements to process; the host form defaults it and this clamps the LLM's number.</summary>
    public int Limit { get; init; } = DefaultLimit;

    public bool DryRun { get; init; }

    /// <summary>
    /// When on, the first piece keeps the original Mark and the rest become <c>&lt;Mark&gt;-1</c>,
    /// <c>-2</c>, ... in creation order. A blank Mark stays blank.
    /// </summary>
    public bool UniqueMark { get; init; }

    /// <summary>Off leaves the new pieces with their type defaults instead of copying the original's.</summary>
    public bool CopyParams { get; init; } = true;

    /// <summary>
    /// Where hosts come from when ids are omitted: <c>selection</c> (default), <c>visible</c> in the
    /// active view, or <c>all</c> in the model. Chat never has to paste hundreds of ids.
    /// </summary>
    public string Scope { get; init; } = DefaultScope;

    /// <summary>How many already-collected hosts to skip, so a capped run can continue from nextOffset.</summary>
    public int ContinueFrom { get; init; }
}

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
