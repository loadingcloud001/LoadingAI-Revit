using LoadingAI.Llm;

namespace LoadingAI.Tools;

/// <summary>
/// The native "Run X?" dialog this class used to add to L2 tools is gone (ADR-0017): the host asks once, in
/// chat, through <c>ApprovalGate</c>. What remains is the read-only summary a card shows.
/// </summary>
public static class ToolGuards
{
    private const int SummaryChars = 1200;

    /// <summary>
    /// The read-only summary a tool supplies for this call (ADR-0015), or null. Never throws: a
    /// summary is enrichment, so a failing or slow query leaves the raw-arguments fallback.
    /// </summary>
    public static async Task<string?> DescribeAsync(ToolEntry entry, string argsJson)
    {
        var describe = entry.Describe;
        if (describe == null) return null;
        try
        {
            var text = await describe(argsJson);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return text!.Length > SummaryChars ? text.Substring(0, SummaryChars) + "..." : text;
        }
        catch
        {
            return null;
        }
    }
}
