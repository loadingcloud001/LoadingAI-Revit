using System.Text.RegularExpressions;

namespace RevitAiPoc.Llm;

public sealed record PlanStepDraft(string Title);
public sealed record PlanDraft(string Title, IReadOnlyList<PlanStepDraft> Steps);

public static class PlanParser
{
    private static readonly Regex StepLine = new(
        @"^\s*(?:\d+[\.\)、]\s+|[-*•]\s+)(.{3,240})\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static PlanDraft? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var steps = new List<PlanStepDraft>();
        foreach (Match m in StepLine.Matches(text))
        {
            var stepTitle = m.Groups[1].Value.Trim();
            if (stepTitle.Length < 3) continue;
            steps.Add(new PlanStepDraft(stepTitle));
        }
        if (steps.Count < 2) return null;
        var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "Plan";
        first = first.TrimStart('#', ' ', '\t');
        var heading = first.Length is > 0 and <= 80 && !StepLine.IsMatch(first) ? first : "Plan";
        return new PlanDraft(heading, steps);
    }
}
