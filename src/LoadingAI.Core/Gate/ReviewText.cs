using System.Text;
using System.Text.Json;
using LoadingAI.Tools;

namespace LoadingAI.Gate;

/// <summary>
/// Plain-language text for the in-chat review (ADR-0017): what a Deny, a timeout and a steer say to the
/// model, and the rows a card shows when a tool has no summary of its own. Pure.
/// </summary>
public static class ReviewText
{
    public const int ApprovalTimeoutSeconds = 180;
    public const int MaxReasonChars = 200;

    /// <summary>
    /// The reason the user typed on Deny, flattened to one line and capped. It reaches the model as
    /// the user's words, so control characters and line breaks are dropped.
    /// </summary>
    public static string CleanReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "";
        var sb = new StringBuilder(reason!.Length);
        foreach (var ch in reason)
        {
            if (ch == '\r' || ch == '\n' || ch == '\t') sb.Append(' ');
            else if (!char.IsControl(ch)) sb.Append(ch);
        }
        var text = string.Join(" ", sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > MaxReasonChars ? text.Substring(0, MaxReasonChars - 1) + "…" : text;
    }

    /// <summary>What the model reads when the user denies a change. The reason is optional.</summary>
    public static string DenyResult(string? reason)
    {
        var clean = CleanReason(reason);
        return clean.Length == 0 ? "Cancelled by user (denied)." : "Cancelled by user (denied): " + clean;
    }

    /// <summary>What the model reads when no one answered in time. A timeout never approves.</summary>
    public static string TimeoutResult()
        => $"Cancelled: approval timeout ({ApprovalTimeoutSeconds}s).";

    /// <summary>What the model reads when the user turned on a read-only level or mode mid-turn.</summary>
    public static string BlockedResult(string toolName)
        => $"Tool {toolName} was not run: this turn is read-only. Tell the user to switch the permission level to run it.";

    /// <summary>
    /// A user message sent while the agent works, shown to the model after the current tool result. The
    /// frame says whose words these are and that earlier instructions still apply to the safety rules.
    /// </summary>
    public static string SteerMessage(string text)
        => "[The user sent this while you were working. Read it before your next step.]\n" + text.Trim();

    /// <summary>
    /// Rows for a card whose tool supplied no summary: one per top-level argument, arrays as a count.
    /// Capped so a tool with many arguments cannot push the Allow button off the screen.
    /// </summary>
    public static IReadOnlyList<PreviewRow> RowsFromArgs(string? argsJson, int maxRows = 8)
    {
        var rows = new List<PreviewRow>();
        if (string.IsNullOrWhiteSpace(argsJson)) return rows;
        try
        {
            using var doc = JsonDocument.Parse(argsJson!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return rows;
            var total = 0;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                total++;
                if (rows.Count >= maxRows) continue;
                rows.Add(new PreviewRow { Label = p.Name, Detail = RenderValue(p.Value) });
            }
            if (total > rows.Count)
                rows.Add(new PreviewRow { Label = "…", Detail = $"{total - rows.Count} more argument{(total - rows.Count == 1 ? "" : "s")}" });
        }
        catch (JsonException)
        {
            // The caller already validated the arguments; an unreadable blob just has no rows.
        }
        return rows;
    }

    private static string RenderValue(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return "-";
            case JsonValueKind.True: return "on";
            case JsonValueKind.False: return "off";
            case JsonValueKind.Number: return el.GetRawText();
            case JsonValueKind.String:
            {
                var s = el.GetString() ?? "";
                if (s.Length == 0) return "-";
                return s.Length > 120 ? s.Substring(0, 119) + "…" : s;
            }
            case JsonValueKind.Array:
                return el.GetArrayLength() == 1 ? "1 item" : el.GetArrayLength() + " items";
            default:
                return "{…}";
        }
    }
}
