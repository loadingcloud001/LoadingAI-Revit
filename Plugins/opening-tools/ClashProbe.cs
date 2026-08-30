using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.OpeningTools;

internal readonly struct ClashHit
{
    public readonly ElementId MepId;
    public readonly ElementId HostId;
    public readonly bool HostLinked;
    public readonly bool SourceLinked;
    public readonly XYZ Centroid;
    public readonly XYZ HostDir;
    public readonly double WidthFt;
    public readonly double HeightFt;
    public readonly double DepthFt;
    public readonly string Shape;
    public readonly ElementId LevelId;

    public ClashHit(
        ElementId mepId, ElementId hostId, bool hostLinked, bool sourceLinked,
        XYZ centroid, XYZ hostDir, double widthFt, double heightFt, double depthFt,
        string shape, ElementId levelId)
    {
        MepId = mepId;
        HostId = hostId;
        HostLinked = hostLinked;
        SourceLinked = sourceLinked;
        Centroid = centroid;
        HostDir = hostDir;
        WidthFt = widthFt;
        HeightFt = heightFt;
        DepthFt = depthFt;
        Shape = shape;
        LevelId = levelId;
    }
}

internal static class ClashProbe
{
    static readonly BuiltInCategory[] MepCats =
    {
        BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting,
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting,
        BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting
    };

    static readonly BuiltInCategory[] HostCats =
    {
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Floors
    };

    public static bool AnyMep(Document doc, UIDocument uidoc, IReadOnlyList<string> mepIds)
        => ResolveMep(doc, uidoc, mepIds).Count > 0;

    public static List<ClashHit> Find(
        Document doc,
        UIDocument uidoc,
        IReadOnlyList<string> mepIds,
        IReadOnlyList<string> hostIds,
        double clearanceFt,
        string shape,
        int limit)
    {
        var meps = ResolveMep(doc, uidoc, mepIds);
        var hosts = ResolveHosts(doc, uidoc, hostIds);
        var hits = new List<ClashHit>();
        if (meps.Count == 0) return hits;

        foreach (var mep in meps)
        {
            if (hits.Count >= limit) break;
            var mepSolids = SolidsOf(mep.Element, mep.ToHost).ToList();
            var mepBox = BoxOf(mep.Element, mep.ToHost);
            IList<Element> candidates = hosts;
            if (candidates.Count == 0)
                candidates = HostsOverlapping(doc, mepBox, mepSolids);

            foreach (var host in candidates)
            {
                if (hits.Count >= limit) break;
                if (Equals(host.Document, mep.Element.Document) && host.Id == mep.Element.Id) continue;
                if (!IsHost(host)) continue;
                var hostSolids = SolidsOf(host, Transform.Identity).ToList();
                var inter = FirstIntersect(mepSolids, hostSolids);
                if (inter == null || !WorldBox(inter, out var min, out var max)) continue;

                var centroid = new XYZ((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
                HostFrame(host, out var dir, out var up, out _);
                ProjectExtents(min, max, centroid, dir, up, out var width, out var height);
                width += 2 * clearanceFt;
                height += 2 * clearanceFt;
                var depth = HostThickness(host) + Metric.MmToFt(50);
                if (width < Metric.MmToFt(20) || height < Metric.MmToFt(20)) continue;
                var resolved = ResolveShape(shape, width, height);
                hits.Add(new ClashHit(
                    mep.Element.Id, host.Id, false, mep.Linked,
                    centroid, dir, width, height, depth, resolved, host.LevelId));
            }
        }

        return hits;
    }

    readonly struct MepRef
    {
        public readonly Element Element;
        public readonly Transform ToHost;
        public readonly bool Linked;
        public MepRef(Element element, Transform toHost, bool linked)
        {
            Element = element;
            ToHost = toHost;
            Linked = linked;
        }
    }

    static List<MepRef> ResolveMep(Document doc, UIDocument uidoc, IReadOnlyList<string> ids)
    {
        var seeds = new List<Element>();
        if (ids.Count > 0)
        {
            foreach (var s in ids)
            {
                try { var el = doc.GetElement(RevitParse.ParseEid(s)); if (el != null) seeds.Add(el); }
                catch { /* skip bad id */ }
            }
        }
        else
        {
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                var el = doc.GetElement(id);
                if (el != null) seeds.Add(el);
            }
        }

        var list = new List<MepRef>();
        foreach (var el in seeds)
        {
            if (el is RevitLinkInstance)
                continue;
            if (!IsMep(el)) continue;
            var xf = ToHostSpace(doc, el);
            list.Add(new MepRef(el, xf, !Equals(el.Document, doc)));
        }
        return list;
    }

    static List<Element> ResolveHosts(Document doc, UIDocument uidoc, IReadOnlyList<string> ids)
    {
        var seeds = new List<Element>();
        if (ids.Count > 0)
        {
            foreach (var s in ids)
            {
                try { var el = doc.GetElement(RevitParse.ParseEid(s)); if (el != null) seeds.Add(el); }
                catch { /* skip */ }
            }
        }
        else
        {
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                var el = doc.GetElement(id);
                if (el != null) seeds.Add(el);
            }
        }
        return seeds.Where(IsHost).ToList();
    }

    static List<Element> HostsOverlapping(Document doc, BoundingBoxXYZ? mepBox, List<Solid> mepSolids)
    {
        var found = new Dictionary<ElementId, Element>();
        foreach (var solid in mepSolids)
        {
            try
            {
                foreach (var el in new FilteredElementCollector(doc)
                    .WherePasses(new ElementMulticategoryFilter(HostCats))
                    .WherePasses(new ElementIntersectsSolidFilter(solid))
                    .WhereElementIsNotElementType())
                    found[el.Id] = el;
            }
            catch { /* solid not usable as filter */ }
        }
        if (found.Count == 0 && mepBox != null)
        {
            var outline = new Outline(mepBox.Min, mepBox.Max);
            try
            {
                foreach (var el in new FilteredElementCollector(doc)
                    .WherePasses(new ElementMulticategoryFilter(HostCats))
                    .WherePasses(new BoundingBoxIntersectsFilter(outline))
                    .WhereElementIsNotElementType())
                    found[el.Id] = el;
            }
            catch { /* outline inverted */ }
        }
        return found.Values.ToList();
    }

    static IEnumerable<Element> Collector(Document d, BuiltInCategory[] cats)
        => new FilteredElementCollector(d)
            .WherePasses(new ElementMulticategoryFilter(cats))
            .WhereElementIsNotElementType()
            .ToElements();

    static bool IsMep(Element el) => Matches(el, MepCats);
    static bool IsHost(Element el) => Matches(el, HostCats);

    static bool Matches(Element el, BuiltInCategory[] cats)
    {
        if (el.Category == null) return false;
        foreach (var c in cats)
        {
            if (el.Category.Id == new ElementId(c)) return true;
        }
        return false;
    }

    static IEnumerable<Solid> SolidsOf(Element el, Transform toHost)
    {
        var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
        GeometryElement? geo;
        try { geo = el.get_Geometry(opt); }
        catch { yield break; }
        if (geo == null) yield break;
        foreach (var s in Walk(geo))
        {
            if (s.Volume <= 1e-9) continue;
            if (toHost.IsIdentity) yield return s;
            else
            {
                Solid? xf = null;
                try { xf = SolidUtils.CreateTransformed(s, toHost); }
                catch { }
                if (xf != null) yield return xf;
            }
        }
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

    static Solid? FirstIntersect(List<Solid> a, List<Solid> b)
    {
        foreach (var sa in a)
        foreach (var sb in b)
        {
            try
            {
                var inter = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb, BooleanOperationsType.Intersect);
                if (inter != null && inter.Volume > 1e-9) return inter;
            }
            catch { /* disjoint or non-manifold */ }
        }
        return null;
    }

    static BoundingBoxXYZ? BoxOf(Element el, Transform toHost)
    {
        BoundingBoxXYZ? bb;
        try { bb = el.get_BoundingBox(null); }
        catch { return null; }
        if (bb == null) return null;
        if (toHost.IsIdentity) return bb;
        var min = toHost.OfPoint(bb.Min);
        var max = toHost.OfPoint(bb.Max);
        return new BoundingBoxXYZ
        {
            Min = new XYZ(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z)),
            Max = new XYZ(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z))
        };
    }

    static bool WorldBox(Solid s, out XYZ min, out XYZ max)
    {
        min = max = XYZ.Zero;
        BoundingBoxXYZ? bb;
        try { bb = s.GetBoundingBox(); }
        catch { return false; }
        if (bb == null) return false;
        var t = bb.Transform ?? Transform.Identity;
        XYZ? mn = null, mx = null;
        foreach (var cx in new[] { bb.Min.X, bb.Max.X })
        foreach (var cy in new[] { bb.Min.Y, bb.Max.Y })
        foreach (var cz in new[] { bb.Min.Z, bb.Max.Z })
        {
            var p = t.OfPoint(new XYZ(cx, cy, cz));
            mn = mn == null ? p : new XYZ(Math.Min(mn.X, p.X), Math.Min(mn.Y, p.Y), Math.Min(mn.Z, p.Z));
            mx = mx == null ? p : new XYZ(Math.Max(mx.X, p.X), Math.Max(mx.Y, p.Y), Math.Max(mx.Z, p.Z));
        }
        if (mn == null || mx == null) return false;
        min = mn; max = mx;
        return true;
    }

    static Transform ToHostSpace(Document hostDoc, Element el)
    {
        if (Equals(el.Document, hostDoc)) return Transform.Identity;
        foreach (var link in new FilteredElementCollector(hostDoc)
            .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            if (link.GetLinkDocument() != null && Equals(link.GetLinkDocument(), el.Document))
                return link.GetTotalTransform();
        }
        return Transform.Identity;
    }

    static void HostFrame(Element host, out XYZ dir, out XYZ up, out XYZ norm)
    {
        up = XYZ.BasisZ;
        dir = XYZ.BasisX;
        norm = XYZ.BasisY;
        if (host is Wall wall)
        {
            norm = wall.Orientation;
            if (wall.Location is LocationCurve lc)
            {
                var tan = lc.Curve.ComputeDerivatives(0.5, true).BasisX;
                if (tan.GetLength() > 1e-9) dir = tan.Normalize();
            }
            var cross = norm.CrossProduct(dir);
            if (cross.GetLength() > 1e-9) up = cross.Normalize();
            return;
        }
        if (host is Floor)
        {
            dir = XYZ.BasisX;
            up = XYZ.BasisY;
            norm = XYZ.BasisZ;
            return;
        }
        if (host.Location is LocationCurve curveLoc)
        {
            var tan = curveLoc.Curve.ComputeDerivatives(0.5, true).BasisX;
            if (tan.GetLength() > 1e-9) dir = tan.Normalize();
            up = XYZ.BasisZ;
            if (Math.Abs(dir.DotProduct(up)) > 0.9) up = XYZ.BasisY;
            up = up - dir.Multiply(up.DotProduct(dir));
            if (up.GetLength() < 1e-9) up = XYZ.BasisY;
            else up = up.Normalize();
            var n = dir.CrossProduct(up);
            norm = n.GetLength() < 1e-9 ? XYZ.BasisY : n.Normalize();
        }
    }

    static void ProjectExtents(XYZ min, XYZ max, XYZ origin, XYZ dir, XYZ up, out double width, out double height)
    {
        double minD = double.PositiveInfinity, maxD = double.NegativeInfinity;
        double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
        foreach (var cx in new[] { min.X, max.X })
        foreach (var cy in new[] { min.Y, max.Y })
        foreach (var cz in new[] { min.Z, max.Z })
        {
            var v = new XYZ(cx, cy, cz) - origin;
            var d = v.DotProduct(dir);
            var u = v.DotProduct(up);
            if (d < minD) minD = d;
            if (d > maxD) maxD = d;
            if (u < minU) minU = u;
            if (u > maxU) maxU = u;
        }
        width = Math.Max(maxD - minD, Metric.MmToFt(20));
        height = Math.Max(maxU - minU, Metric.MmToFt(20));
    }

    static double HostThickness(Element host)
    {
        if (host is Wall w) return w.Width;
        var p = host.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM)
                ?? host.LookupParameter("Thickness")
                ?? host.LookupParameter("Width");
        if (p != null && p.StorageType == StorageType.Double && p.AsDouble() > 0) return p.AsDouble();
        var bb = host.get_BoundingBox(null);
        if (bb == null) return Metric.MmToFt(200);
        var dx = bb.Max.X - bb.Min.X;
        var dy = bb.Max.Y - bb.Min.Y;
        var dz = bb.Max.Z - bb.Min.Z;
        return Math.Min(dx, Math.Min(dy, dz));
    }

    static string ResolveShape(string wanted, double width, double height)
    {
        if (wanted.Equals("circle", StringComparison.OrdinalIgnoreCase)) return "circle";
        if (wanted.Equals("rectangle", StringComparison.OrdinalIgnoreCase)) return "rectangle";
        var m = Math.Max(width, height);
        if (m > 0 && Math.Abs(width - height) / m < 0.15) return "circle";
        return "rectangle";
    }
}
