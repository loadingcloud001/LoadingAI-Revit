using System.Text;
using System.Text.RegularExpressions;

namespace LoadingAI.Revit;

public enum NameKind { Category, Level, Family, Type, Parameter, View, Sheet }

public sealed record NameCandidate(string Name, NameKind Kind, double Score, bool Exact);

/// <summary>Maps a user's word to the exact Revit names it most likely means (P2). Pure.</summary>
public static class NameResolver
{
    public const int MaxCandidates = 5;
    private const double MinScore = 0.45;

    /// <summary>Score given to a name the user typed exactly (ignoring case).</summary>
    private const double ExactScore = 1.0;

    /// <summary>Score for a name that only differs from the term in punctuation or spacing.</summary>
    private const double NormalizedScore = 0.98;

    /// <summary>Score for a name reached through a known category synonym ("beam" -> "Structural Framing").</summary>
    private const double SynonymScore = 0.95;

    /// <summary>Score for a level reached through an alias ("L3" -> "Level 3").</summary>
    private const double AliasScore = 0.93;

    /// <summary>Score for a fuzzy match whose numbers (level index, size) match the term.</summary>
    private const double NumberScore = 0.75;

    private static readonly Dictionary<string, string[]> CategorySynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["beam"] = new[] { "Structural Framing" },
        ["framing"] = new[] { "Structural Framing" },
        ["column"] = new[] { "Structural Columns", "Columns" },
        ["slab"] = new[] { "Floors" },
        ["floor"] = new[] { "Floors" },
        ["wall"] = new[] { "Walls" },
        ["door"] = new[] { "Doors" },
        ["window"] = new[] { "Windows" },
        ["duct"] = new[] { "Ducts" },
        ["pipe"] = new[] { "Pipes" },
        ["room"] = new[] { "Rooms" },
        ["sheet"] = new[] { "Sheets" },
        ["view"] = new[] { "Views" },
        ["roof"] = new[] { "Roofs" },
        ["stair"] = new[] { "Stairs" },
        ["foundation"] = new[] { "Structural Foundations" },
    };

    public static IReadOnlyList<NameCandidate> Match(string term, IEnumerable<string> names, NameKind kind)
    {
        var t = Normalize(term);
        if (t.Length == 0) return Array.Empty<NameCandidate>();
        var synonyms = kind == NameKind.Category ? SynonymsFor(t) : Array.Empty<string>();
        var levelAlias = kind == NameKind.Level ? LevelAlias(t) : null;

        var scored = new List<NameCandidate>();
        foreach (var name in names.Distinct())
        {
            var n = Normalize(name);
            if (n.Length == 0) continue;
            double score;
            var exact = false;
            if (string.Equals(term?.Trim(), name, StringComparison.OrdinalIgnoreCase)) { score = ExactScore; exact = true; }
            else if (n == t) score = NormalizedScore;
            else if (synonyms.Contains(name, StringComparer.OrdinalIgnoreCase)) score = SynonymScore;
            else if (levelAlias != null && n == levelAlias) score = AliasScore;
            else if (n.StartsWith(t) || t.StartsWith(n)) score = 0.85 * Math.Min(t.Length, n.Length) / Math.Max(t.Length, n.Length) + 0.1;
            else
            {
                var overlap = TokenOverlap(t, n);
                var dist = Levenshtein(t, n);
                var edit = Math.Max(t.Length, n.Length) <= 12 && dist <= 2 ? 0.9 - dist * 0.1 : 0;
                score = Math.Max(overlap * 0.8, edit);
                // "Levle 3" is closer to "Level 3" than to "Level 1": the number decides.
                if (score > 0 && SameNumbers(t, n)) score = Math.Max(score, NumberScore);
            }
            if (score >= MinScore) scored.Add(new NameCandidate(name, kind, Math.Round(score, 2), exact));
        }
        return scored.OrderByDescending(c => c.Score).ThenBy(c => c.Name.Length).Take(MaxCandidates).ToList();
    }

    /// <summary>
    /// Folds a name to a comparable form: lower case, punctuation and units dropped,
    /// so "600 x 300mm" and "600x300" are the same type.
    /// </summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (var raw in s!.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            var ch = raw is '×' or '*' ? 'x' : raw;
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
        }
        // "600 x 300mm" and "600x300" compare equal: drop spaces around x between digits, and a trailing unit.
        var text = Regex.Replace(sb.ToString().Trim(), @"(\d)\s*x\s*(\d)", "$1x$2");
        text = Regex.Replace(text, @"(\d)\s*mm\b", "$1");
        return text;
    }

    private static string[] SynonymsFor(string t)
    {
        var single = t.EndsWith("s") ? t.Substring(0, t.Length - 1) : t;
        return CategorySynonyms.TryGetValue(single, out var s) ? s
             : CategorySynonyms.TryGetValue(t, out s) ? s
             : Array.Empty<string>();
    }

    private static string? LevelAlias(string t)
    {
        var m = Regex.Match(t, @"^(?:l|lvl|level)\s*(\d+)$");
        return m.Success ? "level " + m.Groups[1].Value : null;
    }

    private static double TokenOverlap(string a, string b)
    {
        var ta = a.Split(' ');
        var tb = b.Split(' ');
        var hits = ta.Count(x => tb.Any(y => y.StartsWith(x) || x.StartsWith(y)));
        return (double)hits / Math.Max(ta.Length, tb.Length);
    }

    /// <summary>True when both sides carry the same digits, so a fuzzy match keeps the right index or size.</summary>
    private static bool SameNumbers(string a, string b)
    {
        var na = Numbers(a);
        var nb = Numbers(b);
        return na.Length > 0 && nb.Length > 0 && na.SequenceEqual(nb);
    }

    private static string[] Numbers(string s)
        => Regex.Matches(s, @"\d+").Cast<Match>().Select(m => m.Value).ToArray();

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
