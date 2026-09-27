using LoadingAI.Revit;

namespace LoadingAI.Plugins.OpeningTools;

/// <summary>Summary-first copy for the existing tool-result card.</summary>
public static class OpeningResultText
{
    public static string ForDryRun(OpeningPlanResultDto plan)
    {
        var detail = Combine(plan.Error, plan.Report);
        return "Dry run: " + detail + "\nNo model changes.";
    }

    public static string ForPlace(OpeningPlaceResultDto result)
    {
        if (IsDocumentPrompt(result.Report)) return result.Report;
        var outcome =
            result.Placed > 0 && (result.Failed > 0 || result.Error != null) ? "Partial" :
            result.Placed > 0 ? "Success" :
            result.Failed > 0 || result.Error != null ? "Failed" :
            "No write";
        var summary = $"{outcome}: {result.Placed} created, {result.Punched} cut, {result.Failed} failed, {result.Skipped} skipped.";
        var text = summary + "\nDetails: " + Combine(result.Error, result.Report);
        if (result.Failed > 0)
            text += "\nNext: Review failed items, then rerun only for those hosts or openings.";
        return text;
    }

    public static string ForCut(OpeningCutResultDto result)
    {
        if (IsDocumentPrompt(result.Report)) return result.Report;
        var outcome =
            result.Cut > 0 && (result.Failed > 0 || result.Error != null) ? "Partial" :
            result.Cut > 0 ? "Success" :
            result.Failed > 0 || result.Error != null ? "Failed" :
            "No write";
        var summary = $"{outcome}: {result.Cut} cut, {result.SkippedLinked} linked host skipped, {result.SkippedCannot} unsupported hosts skipped, {result.Failed} failed.";
        var text = summary + "\nDetails: " + Combine(result.Error, result.Report);
        if (result.Failed > 0)
            text += "\nNext: Inspect failed hosts and rerun cut_hosts_by_openings for those ids.";
        return text;
    }

    static bool IsDocumentPrompt(string report)
        => string.Equals(report.Trim(), "Please open a model first.", StringComparison.OrdinalIgnoreCase);

    static string Combine(string? error, string report)
        => string.IsNullOrWhiteSpace(error)
            ? report
            : string.IsNullOrWhiteSpace(report)
                ? error
                : error + "\n" + report;
}
