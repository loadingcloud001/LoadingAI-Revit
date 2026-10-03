using System;
using System.Collections.Generic;
using System.Linq;

namespace LoadingAI.Revit;

public enum PlacementKind
{
    NonStructural,
    Column,
    Footing,
}

/// <summary>
/// What <c>create_family_instance</c> can place. It calls <c>NewFamilyInstance(point, symbol, level, type)</c>,
/// which only works for a family that stands on a level by itself. Everything else needs a host, a
/// second level, a work plane or a curve, so it is refused with the reason instead of reaching Revit
/// as a guaranteed exception.
/// </summary>
public static class PlacementRules
{
    /// <param name="placementType">The name of Revit's <c>FamilyPlacementType</c> value.</param>
    /// <returns>Null when the family can be placed, else the sentence to show the model.</returns>
    public static string? Refusal(string symbolName, string placementType)
    {
        if (string.Equals(placementType, "OneLevelBased", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(placementType, "OneLevelBasedHosted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(placementType, "WorkPlaneBased", StringComparison.OrdinalIgnoreCase))
            return $"'{symbolName}' is a hosted family ({placementType}); it needs a wall, floor or face to sit on. "
                + "This tool places level-based families only (columns, furniture, equipment).";

        return $"'{symbolName}' is a {placementType} family. This tool places single-point, level-based families only.";
    }

    /// <param name="builtInCategoryName">For example <c>OST_StructuralColumns</c>; null when the family has no category.</param>
    public static PlacementKind KindFor(string? builtInCategoryName)
    {
        if (string.Equals(builtInCategoryName, "OST_StructuralColumns", StringComparison.Ordinal))
            return PlacementKind.Column;
        if (string.Equals(builtInCategoryName, "OST_StructuralFoundation", StringComparison.Ordinal))
            return PlacementKind.Footing;
        return PlacementKind.NonStructural;
    }
}

/// <summary>Chooses a family type from the loaded ones without ever guessing between families.</summary>
public static class SymbolPick
{
    /// <returns>The index into <paramref name="candidates"/>, or -1 with <paramref name="error"/> set.</returns>
    public static int Choose(
        IReadOnlyList<(string Family, string Symbol)> candidates, string symbolName, string? familyName, out string? error)
    {
        error = null;
        var matches = new List<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i].Symbol, symbolName, StringComparison.OrdinalIgnoreCase))
                matches.Add(i);
        }

        if (!string.IsNullOrWhiteSpace(familyName))
        {
            foreach (var i in matches)
            {
                if (string.Equals(candidates[i].Family, familyName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            var inFamily = candidates
                .Where(c => string.Equals(c.Family, familyName, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Symbol)
                .Take(10)
                .ToList();
            error = inFamily.Count > 0
                ? $"Symbol '{symbolName}' not found in family '{familyName}'. Types in that family: {string.Join(", ", inFamily)}."
                : $"Family '{familyName}' not found. {Available(candidates)}";
            return -1;
        }

        if (matches.Count == 1)
            return matches[0];

        if (matches.Count == 0)
        {
            error = $"Symbol '{symbolName}' not found. {Available(candidates)}";
            return -1;
        }

        var families = matches.Select(i => candidates[i].Family).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        error = $"Symbol '{symbolName}' exists in {families.Count} families ({string.Join(", ", families)}). Pass familyName.";
        return -1;
    }

    private static string Available(IReadOnlyList<(string Family, string Symbol)> candidates)
    {
        var first = candidates.Take(10).Select(c => $"{c.Family}: {c.Symbol}").ToList();
        if (first.Count == 0)
            return "No family types are loaded.";
        return "Available: " + string.Join(", ", first) + (candidates.Count > 10 ? ", ..." : "") + ".";
    }
}
