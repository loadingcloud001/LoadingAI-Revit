using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LoadingAI.Revit;

public sealed record IndexLevel(string Name, double ElevationMm);
public sealed record IndexCategory(string Name, int Count);
public sealed record IndexFamily(string Category, string Family, IReadOnlyList<string> Types);

/// <summary>
/// What the model is likely to name: levels, grids, categories in use, families and types, phases,
/// worksets, parameter names per category, views and sheets (P2). Built by the host, matched in Core.
/// </summary>
public sealed record ProjectIndex(
    IReadOnlyList<IndexLevel> Levels,
    IReadOnlyList<string> Grids,
    IReadOnlyList<IndexCategory> Categories,
    IReadOnlyList<IndexFamily> Families,
    IReadOnlyList<string> Phases,
    IReadOnlyList<string> Worksets,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Parameters,
    IReadOnlyList<string> Views,
    IReadOnlyList<string> Sheets)
{
    public const int MaxSummaryChars = 1200;
    public const int SummaryCategories = 15;

    public static ProjectIndex Empty { get; } = new(
        Array.Empty<IndexLevel>(), Array.Empty<string>(), Array.Empty<IndexCategory>(), Array.Empty<IndexFamily>(),
        Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, IReadOnlyList<string>>(),
        Array.Empty<string>(), Array.Empty<string>());

    /// <summary>A short block for the system prompt: levels and the largest categories. Capped.</summary>
    public string ToPromptSummary()
    {
        var sb = new StringBuilder("\n# This model\n");
        sb.Append("Use resolve_names to get exact names before you pass a category, level, type or parameter.\n");
        sb.Append("Levels: ").Append(string.Join(", ", Levels.OrderBy(l => l.ElevationMm).Select(l => $"{l.Name} ({Math.Round(l.ElevationMm)} mm)"))).Append('\n');
        sb.Append("Categories: ").Append(string.Join(", ", Categories.OrderByDescending(c => c.Count).Take(SummaryCategories).Select(c => $"{c.Name} ({c.Count})"))).Append('\n');
        var s = sb.ToString();
        return s.Length <= MaxSummaryChars ? s : s.Substring(0, MaxSummaryChars - 2) + "…\n";
    }
}
