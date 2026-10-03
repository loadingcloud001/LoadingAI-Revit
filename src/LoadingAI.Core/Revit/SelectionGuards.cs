namespace LoadingAI.Revit;

/// <summary>
/// The selection summary lists a bounded number of ids but always carries the real count. A tool
/// that acts on the selection must not run on the listed subset and report it as done.
/// </summary>
public static class SelectionGuards
{
    /// <summary>Null when every selected element is listed; otherwise a refusal naming both numbers.</summary>
    public static string? TruncationRefusal(SelectionSummaryDto selection)
    {
        if (selection == null) return null;
        var listed = selection.ElementIds?.Count ?? 0;
        if (selection.ElementCount <= listed) return null;
        return $"The selection has {selection.ElementCount} elements but one call can use {listed}. "
               + "Select fewer elements or pass elementIds.";
    }
}
