using System.Globalization;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using LoadingAI.Tools;

namespace LoadingAI.Plugins.StructureCuts;

/// <summary>
/// Shared Revit lookups for both split tools: resolving ids, reading the current selection, and the
/// category filters those tools agree on.
/// </summary>
internal static class SplitElements
{
    public static string Fmt(ElementId id)
        => id.Value.ToString(CultureInfo.InvariantCulture);

    public static void Select(UIDocument uidoc, IReadOnlyList<ElementId> created)
    {
        if (created.Count == 0)
            return;

        try
        {
            uidoc.Selection.SetElementIds(created.ToList());
            uidoc.ShowElements(created[0]);
        }
        catch
        {
            // Selecting is a convenience; it must never fail the run.
        }
    }

    public static List<Element> Resolve(Document doc, IReadOnlyList<string> ids, Func<Element, bool> wanted)
    {
        var list = new List<Element>();
        foreach (var s in ids)
        {
            Element? el;
            try
            {
                el = doc.GetElement(RevitParse.ParseEid(s));
            }
            catch
            {
                continue;
            }

            if (el != null && wanted(el))
                list.Add(el);
        }

        return list;
    }

    public static List<Element> GetSelection(UIDocument uidoc)
        => uidoc.Selection.GetElementIds()
            .Select(id => uidoc.Document.GetElement(id))
            .Where(el => el != null)
            .Cast<Element>()
            .ToList();

    public static bool IsFloorLike(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_Floors
           || el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFoundation;

    public static bool IsBeam(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFraming;

    public static bool IsColumn(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;

    public static bool IsWall(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_Walls;

    public static bool IsLevel(Element el) => el is Level;

    public static bool IsGrid(Element el) => el is Grid;

    public static Curve? LocationCurveOf(Element element)
        => element.Location is LocationCurve location ? location.Curve : null;

    public static bool IsHost(Element el)
        => IsWall(el) || IsColumn(el) || IsBeam(el) || IsFloorLike(el);

    /// <summary>
    /// Optional category filter for the Naviate-style tools. Empty means walls, columns, beams and floors.
    /// </summary>
    public static Func<Element, bool> HostsIn(IReadOnlyList<string> categories)
    {
        if (categories.Count == 0)
            return IsHost;

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in categories)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            wanted.Add(raw.Trim());
        }

        if (wanted.Count == 0)
            return IsHost;

        return el =>
            (Match(wanted, "wall", "walls") && IsWall(el))
            || (Match(wanted, "column", "columns") && IsColumn(el))
            || (Match(wanted, "beam", "beams", "framing") && IsBeam(el))
            || (Match(wanted, "floor", "floors", "slab", "slabs") && IsFloorLike(el));
    }

    public static string EmptyHostMessage(SplitOptions options)
        => string.Equals(options.Scope, "selection", StringComparison.OrdinalIgnoreCase)
            ? "Please select elements first."
            : "No hosts found.";

    private static bool Match(HashSet<string> wanted, params string[] names)
    {
        foreach (var name in names)
        {
            if (wanted.Contains(name))
                return true;
        }

        return false;
    }

    /// <summary>How many elements hang off this one: tags, dimensions, hosted families.</summary>
    public static int Dependent(Element element)
    {
        try
        {
            return element.GetDependentElements(null)?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    public static void AddSkip(List<string> parts, int count, string label)
    {
        if (count > 0)
            parts.Add($"{count} {label}");
    }
}
