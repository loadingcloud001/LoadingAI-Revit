using Autodesk.Revit.DB;

namespace LoadingAI.Plugins.OpeningTools;

internal static class FamilyContract
{
    public const string DefaultFamilyName = "LoadingAI_Opening_Horizontal";
    public const string HorizontalFamilyName = "LoadingAI_Opening_Horizontal";
    public const string VerticalFamilyName = "LoadingAI_Opening_Vertical";
    public const string LegacyFamilyName = "LoadingAI_Opening";

    public static bool IsPluginFamily(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName)) return false;
        return familyName.StartsWith("LoadingAI_Opening", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVerticalHost(Element host) => host is Floor || host is RoofBase;

    public static string FamilyNameFor(Element host, string? overrideName)
    {
        if (!string.IsNullOrWhiteSpace(overrideName)) return overrideName.Trim();
        return IsVerticalHost(host) ? VerticalFamilyName : HorizontalFamilyName;
    }

    public static string? FailReason(FamilySymbol? symbol, string? wantedName = null)
    {
        if (symbol != null) return null;
        // Width/Height/Radius are instance params. FamilySymbol.LookupParameter cannot see them; ApplySize sets them after place.
        var name = string.IsNullOrWhiteSpace(wantedName) ? HorizontalFamilyName + " / " + VerticalFamilyName : wantedName;
        return "Opening family is missing. Load " + name
            + " from local/revitAI-opening-extract (Cut with Voids When Loaded, OneLevelBased Generic Model, types Rectangle and Circle).";
    }

    public static bool Has(Element el, string name) => el.LookupParameter(name) != null;

    /// <summary>
    /// OP_* live on the instance when the family has the parameter; otherwise PlaceOpenings
    /// writes them into Comments as "OP_HostId=587487". Cut must read both.
    /// </summary>
    public static string? OpValue(Element el, string name)
    {
        var p = el.LookupParameter(name)?.AsString();
        if (!string.IsNullOrWhiteSpace(p)) return p;
        var comments = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
        var key = name + "=";
        var i = comments.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = comments.Substring(i + key.Length);
        var end = rest.IndexOf(';');
        var val = (end < 0 ? rest : rest.Substring(0, end)).Trim();
        return string.IsNullOrWhiteSpace(val) ? null : val;
    }

    public static FamilySymbol? Find(Document doc, string? familyName, string shape, Element? host)
    {
        var wanted = host != null
            ? FamilyNameFor(host, familyName)
            : (string.IsNullOrWhiteSpace(familyName) ? HorizontalFamilyName : familyName.Trim());
        var typeHint = shape.Equals("circle", StringComparison.OrdinalIgnoreCase) ? "circle" : "rectangle";
        var hit = FindNamed(doc, wanted, typeHint);
        if (hit != null) return hit;
        if (host != null && !IsVerticalHost(host) && !wanted.Equals(LegacyFamilyName, StringComparison.OrdinalIgnoreCase))
            return FindNamed(doc, LegacyFamilyName, typeHint);
        return null;
    }

    static FamilySymbol? FindNamed(Document doc, string wanted, string typeHint)
    {
        var named = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(s =>
                s.FamilyName.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (named.Count == 0) return null;
        return named.FirstOrDefault(s => s.Name.IndexOf(typeHint, StringComparison.OrdinalIgnoreCase) >= 0)
               ?? named[0];
    }
}
