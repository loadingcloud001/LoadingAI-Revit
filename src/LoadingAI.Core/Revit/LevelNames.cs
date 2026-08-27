using System;
using System.Collections.Generic;
using System.Linq;

namespace LoadingAI.Revit;

public static class LevelNames
{
    public static string? Resolve(string wanted, IReadOnlyList<string> names)
    {
        if (string.IsNullOrWhiteSpace(wanted) || names == null || names.Count == 0) return null;
        return names.FirstOrDefault(n => n.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    public static string NotFound(string wanted, IReadOnlyList<string> names)
    {
        var hints = Suggest(wanted, names).Take(8).ToList();
        if (hints.Count == 0) return $"Level '{wanted}' not found.";
        return $"Level '{wanted}' not found. Did you mean: {string.Join(", ", hints)}?";
    }

    public static IEnumerable<string> Suggest(string wanted, IReadOnlyList<string> names)
    {
        if (names == null) yield break;
        var n = Normalize(wanted);
        var levelOne = LooksLikeLevelOne(wanted);
        foreach (var name in names)
        {
            var nn = Normalize(name);
            if (nn.Length == 0) continue;
            if (nn.Contains(n) || n.Contains(nn) || (levelOne && nn.StartsWith("l1", StringComparison.Ordinal)))
                yield return name;
        }
    }

    static bool LooksLikeLevelOne(string s)
    {
        var n = Normalize(s);
        return n == "level1" || n == "l1" || n == "1";
    }

    static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var chars = s.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_').ToArray();
        return new string(chars).ToLowerInvariant();
    }
}
