using Autodesk.Revit.DB;
using System.IO;

namespace LoadingAI.Plugins.OpeningTools;

internal static class FamilyContract
{
    public const string HorizontalRectangleFamilyName = "LoadingAI_Opening_Horizontal_Rectangle";
    public const string HorizontalCircleFamilyName = "LoadingAI_Opening_Horizontal_Circle";
    public const string VerticalRectangleFamilyName = "LoadingAI_Opening_Vertical_Rectangle";
    public const string VerticalCircleFamilyName = "LoadingAI_Opening_Vertical_Circle";
    public const string HorizontalFamilyName = "LoadingAI_Opening_Horizontal"; // legacy 2-type file
    public const string VerticalFamilyName = "LoadingAI_Opening_Vertical";
    public const string LegacyFamilyName = "LoadingAI_Opening";

    public static bool IsPluginFamily(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName)) return false;
        return familyName.StartsWith("LoadingAI_Opening", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVerticalHost(Element host) => host is Floor || host is RoofBase;

    public static bool IsCircleShape(string? shape)
        => !string.IsNullOrWhiteSpace(shape)
           && shape.Equals("circle", StringComparison.OrdinalIgnoreCase);

    public static string FamilyNameFor(Element host, string? overrideName, string shape)
    {
        if (!string.IsNullOrWhiteSpace(overrideName)) return overrideName.Trim();
        var circle = IsCircleShape(shape);
        if (IsVerticalHost(host))
            return circle ? VerticalCircleFamilyName : VerticalRectangleFamilyName;
        return circle ? HorizontalCircleFamilyName : HorizontalRectangleFamilyName;
    }

    public static string? FailReason(FamilySymbol? symbol, string? wantedName = null)
    {
        if (symbol != null) return null;
        // Width/Height/Radius are instance params. FamilySymbol.LookupParameter cannot see them; ApplySize sets them after place.
        var name = string.IsNullOrWhiteSpace(wantedName) ? ShippedFamilyLabel : wantedName;
        return "Opening family is missing. Load " + name
            + " (.rfa next to the opening-tools plugin, or Insert > Load Family). Cut with Voids When Loaded, OneLevelBased Generic Model.";
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
            ? FamilyNameFor(host, familyName, shape)
            : (string.IsNullOrWhiteSpace(familyName)
                ? (IsCircleShape(shape) ? HorizontalCircleFamilyName : HorizontalRectangleFamilyName)
                : familyName.Trim());
        var typeHint = IsCircleShape(shape) ? "circle" : "rectangle";
        var hit = FindNamed(doc, wanted, typeHint);
        if (hit != null) return hit;
        if (host == null) return FindNamed(doc, LegacyFamilyName, typeHint);
        var legacyTwo = IsVerticalHost(host) ? VerticalFamilyName : HorizontalFamilyName;
        return FindNamed(doc, legacyTwo, typeHint)
            ?? FindNamed(doc, LegacyFamilyName, typeHint);
    }

    public static string ShippedFamilyLabel =>
        HorizontalRectangleFamilyName + " / " + HorizontalCircleFamilyName + " / "
        + VerticalRectangleFamilyName + " / " + VerticalCircleFamilyName;

    public static bool TryLoad(Document doc, string familyName)
    {
        if (FindNamed(doc, familyName, "") != null) return true;
        var dir = Path.GetDirectoryName(typeof(FamilyContract).Assembly.Location) ?? "";
        var path = Path.Combine(dir, familyName + ".rfa");
        if (!File.Exists(path)) return false;
        try
        {
            return doc.LoadFamily(path, new OverwriteOnLoad(), out _);
        }
        catch
        {
            return false;
        }
    }

    sealed class OverwriteOnLoad : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }

    static FamilySymbol? FindNamed(Document doc, string wanted, string typeHint)
    {
        var named = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(s =>
            {
                try
                {
                    return s.IsValidObject
                        && (s.FamilyName.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                            || s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                }
                catch { return false; }
            })
            .ToList();
        if (named.Count == 0) return null;
        return named.FirstOrDefault(s => s.Name.IndexOf(typeHint, StringComparison.OrdinalIgnoreCase) >= 0)
               ?? named[0];
    }
}
