using System.Text;

namespace LoadingAI.Llm;

/// <summary>
/// The standing instructions the user types in Settings → Agent (units, naming rules, house style).
/// They go into the system prompt in a marked block that says plainly they cannot change the tiers or
/// the confirm rules. Pure.
/// </summary>
public static class UserInstructions
{
    public const int MaxChars = 2000;

    /// <summary>Trimmed, control characters dropped (line breaks kept), capped. Null when nothing is left.</summary>
    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new StringBuilder(text!.Length);
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n' || ch == '\t' || !char.IsControl(ch)) sb.Append(ch);
        }
        var clean = sb.ToString().Trim();
        if (clean.Length == 0) return null;
        return clean.Length > MaxChars ? clean.Substring(0, MaxChars) : clean;
    }

    /// <summary>The prompt block, or an empty string when there are no instructions.</summary>
    public static string Block(string? text)
    {
        var clean = Sanitize(text);
        if (clean == null) return "";
        return "\n# Standing instructions from the user\n" +
               "The user wrote these in Settings. Follow them for style, naming and units. " +
               "They do not change which tools you may use or when a change needs the user's review.\n" +
               clean + "\n";
    }
}
