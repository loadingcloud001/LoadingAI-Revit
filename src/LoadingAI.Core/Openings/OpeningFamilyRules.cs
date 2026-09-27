using System.IO;

namespace LoadingAI.Core.Openings;

/// <summary>Revit-free family-name rules shared by the opening plugin and host.</summary>
public static class OpeningFamilyRules
{
    static readonly HashSet<string> Shipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "LoadingAI_Opening_Horizontal_Rectangle",
        "LoadingAI_Opening_Horizontal_Circle",
        "LoadingAI_Opening_Vertical_Rectangle",
        "LoadingAI_Opening_Vertical_Circle",
    };

    static readonly HashSet<string> KnownPlugin = new(Shipped, StringComparer.OrdinalIgnoreCase)
    {
        "LoadingAI_Opening",
        "LoadingAI_Opening_Horizontal",
        "LoadingAI_Opening_Vertical",
    };

    public static bool IsSupportedAutoLoadFamily(string? familyName)
        => !string.IsNullOrWhiteSpace(familyName) && Shipped.Contains(familyName!.Trim());

    public static bool IsKnownPluginFamily(string? familyName)
        => !string.IsNullOrWhiteSpace(familyName) && KnownPlugin.Contains(familyName!.Trim());

    public static bool IsSafeCustomFamilyName(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName)) return false;
        var name = familyName!.Trim();
        return name.Length > 0
            && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0
            && !name.Contains("..", StringComparison.Ordinal);
    }

    public static bool IsAllowedFamilyName(string? familyName, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(familyName)) return true;
        var name = familyName!.Trim();
        if (IsSupportedAutoLoadFamily(name) || IsSafeCustomFamilyName(name)) return true;
        error = "familyName must be a shipped LoadingAI_Opening_* family or a safe name for an already-loaded custom family.";
        return false;
    }
}
