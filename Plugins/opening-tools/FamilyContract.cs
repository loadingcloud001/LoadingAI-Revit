using Autodesk.Revit.DB;

namespace LoadingAI.Plugins.OpeningTools;

internal static class FamilyContract
{
    public const string DefaultFamilyName = "LoadingAI_Opening";

    public static string? FailReason(FamilySymbol? symbol)
    {
        if (symbol == null)
            return "Opening family is missing. Load LoadingAI_Opening (Work Plane Based, Cut with Voids When Loaded, Width+Height or Radius).";

        var place = symbol.Family.FamilyPlacementType;
        var w = Has(symbol, "Width");
        var h = Has(symbol, "Height");
        var r = Has(symbol, "Radius");
        var sizeOk = (w && h) || r;

        if (place != FamilyPlacementType.WorkPlaneBased)
            return $"Opening family '{symbol.FamilyName}' placement is {place}, need WorkPlaneBased. Family Editor: Create → Family Category and Parameters → tick Work Plane-Based → Load into Project. Width={w} Height={h} Radius={r}.";

        if (!sizeOk)
            return $"Opening family '{symbol.FamilyName}' needs Width+Height or Radius. Width={w} Height={h} Radius={r}.";
        return null;
    }

    public static bool Has(Element el, string name) => el.LookupParameter(name) != null;

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
