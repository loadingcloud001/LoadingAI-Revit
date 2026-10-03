using System.Text.RegularExpressions;

namespace LoadingAI.Revit;

/// <summary>
/// Builds a regular expression from text a model supplied. The pattern can come from an injected
/// comment, and the match runs on the Revit UI thread where nothing can interrupt it, so every
/// expression gets a match timeout and a length cap.
/// </summary>
public static class SafeRegex
{
    public const int MaxPatternLength = 200;

    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    public static bool TryCreate(string? pattern, out Regex? regex, out string error)
    {
        regex = null;
        error = "";
        if (string.IsNullOrEmpty(pattern))
        {
            error = "pattern is required.";
            return false;
        }

        if (pattern!.Length > MaxPatternLength)
        {
            error = $"pattern is longer than {MaxPatternLength} characters.";
            return false;
        }

        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = "invalid regular expression: " + ex.Message;
            return false;
        }
    }
}
