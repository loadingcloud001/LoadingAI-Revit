using Autodesk.Revit.DB;

namespace LoadingAI.Plugins.OpeningTools;

internal static class FamilyContract
{
    public const string DefaultFamilyName = "LoadingAI_Opening";

    public static string? FailReason(FamilySymbol? symbol)
    {
        if (symbol == null)
            return "Opening family is missing. Load LoadingAI_Opening (Cut with Voids When Loaded, Width+Height or Radius instance parameters). Level-based Generic Model copies between models.";
        // Width/Height/Radius on LoadingAI_Opening are instance params.
        // FamilySymbol.LookupParameter cannot see them; ApplySize sets them after place.
        return null;
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

    public static FamilySymbol? Find(Document doc, string? familyName, string shape)
    {
        var wanted = string.IsNullOrWhiteSpace(familyName) ? DefaultFamilyName : familyName.Trim();
        var symbols = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .ToList();
        var named = symbols.Where(s =>
            s.FamilyName.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
            s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 0) return null;
        var typeHint = shape.Equals("circle", StringComparison.OrdinalIgnoreCase) ? "circle" : "rectangle";
        return named.FirstOrDefault(s => s.Name.IndexOf(typeHint, StringComparison.OrdinalIgnoreCase) >= 0)
               ?? named[0];
    }
}
