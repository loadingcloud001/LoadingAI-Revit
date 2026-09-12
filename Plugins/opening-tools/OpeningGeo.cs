using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.OpeningTools;

/// <summary>Shared element/geometry helpers. ClashProbe solids stay visible-only; CutLocal includes hidden.</summary>
internal static class OpeningGeo
{
    public static List<Element> ByIds(Document doc, IEnumerable<string> ids)
    {
        var list = new List<Element>();
        foreach (var s in ids)
        {
            try
            {
                var el = doc.GetElement(RevitParse.ParseEid(s));
                if (el != null) list.Add(el);
            }
            catch { }
        }
        return list;
    }

    public static List<Element> Selection(Document doc, UIDocument uidoc)
    {
        var list = new List<Element>();
        foreach (var id in uidoc.Selection.GetElementIds())
        {
            var el = doc.GetElement(id);
            if (el != null) list.Add(el);
        }
        return list;
    }

    public static IEnumerable<Solid> Solids(Element el, Transform? toHost = null, bool includeNonVisible = false)
    {
        var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = includeNonVisible };
        GeometryElement? geo;
        try { geo = el.get_Geometry(opt); }
        catch { yield break; }
        if (geo == null) yield break;
        var xf = toHost ?? Transform.Identity;
        foreach (var s in Walk(geo))
        {
            if (s.Volume <= 1e-9) continue;
            if (xf.IsIdentity) { yield return s; continue; }
            Solid? transformed = null;
            try { transformed = SolidUtils.CreateTransformed(s, xf); }
            catch { }
            if (transformed != null) yield return transformed;
        }
    }

    public static bool AabbOverlap(XYZ aMin, XYZ aMax, XYZ bMin, XYZ bMax, double pad = 0.5)
    {
        if (aMax.X < bMin.X - pad || aMin.X > bMax.X + pad) return false;
        if (aMax.Y < bMin.Y - pad || aMin.Y > bMax.Y + pad) return false;
        if (aMax.Z < bMin.Z - pad || aMin.Z > bMax.Z + pad) return false;
        return true;
    }

    public static bool TryMove(Document doc, ElementId id, XYZ delta)
    {
        if (delta.GetLength() <= 1e-6) return false;
        try { ElementTransformUtils.MoveElement(doc, id, delta); return true; }
        catch { return false; }
    }

    static IEnumerable<Solid> Walk(GeometryElement geo)
    {
        foreach (var obj in geo)
        {
            if (obj is Solid s) yield return s;
            else if (obj is GeometryInstance gi)
            {
                GeometryElement? inst = null;
                try { inst = gi.GetInstanceGeometry(); }
                catch { }
                if (inst == null) continue;
                foreach (var inner in Walk(inst)) yield return inner;
            }
        }
    }
}
