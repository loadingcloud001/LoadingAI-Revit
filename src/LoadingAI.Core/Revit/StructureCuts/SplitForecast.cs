namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// The sentence a dry run adds so the person, and the L3 prompt, know what a write run would do to
/// members that cannot be split in place. Revit's own Split keeps the element, its host and everything
/// hanging off it. The fallback deletes the member and builds new ones, and whatever depended on the old
/// element (hosted doors, tags, dimensions) goes with it.
/// </summary>
public static class SplitForecast
{
    public static string Note(int inPlace, int reCreated, int dependents)
    {
        if (inPlace + reCreated == 0)
            return "";

        var text = $" In place: {inPlace}, re-created: {reCreated}.";
        if (reCreated > 0 && dependents > 0)
            text += $" Re-creating deletes {dependents} dependent element(s) (hosted elements, tags, dimensions).";
        return text;
    }
}
