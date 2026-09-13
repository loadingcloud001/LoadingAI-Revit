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
