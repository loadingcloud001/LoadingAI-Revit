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
    public readonly string Key;

    public ClashHit(
        ElementId mepId, ElementId hostId, bool hostLinked, bool sourceLinked,
        XYZ centroid, XYZ hostDir, double widthFt, double heightFt, double depthFt,
        string shape, ElementId levelId, string key)
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
        Key = key;
    }
}

internal static class ClashProbe
{
    static readonly BuiltInCategory[] MepCats =
    {
        BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting,
        BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_FlexPipeCurves,
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting,
        BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting
    };

    internal static readonly BuiltInCategory[] HostCategories =
    {
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Roofs
    };

    public static bool AnyMep(Document doc, UIDocument uidoc, IReadOnlyList<string> mepIds, IReadOnlyList<string> hostIds)
        => ResolveMep(doc, uidoc, mepIds, hostIds).Count > 0;

    /// <summary>
    /// Whole-model HostsOverlapping is allowed only when the user named hosts, selected a host,
    /// selected MEP, or selected a Revit link. A section box / empty selection must not spray
    /// openings across the first N linked-MEP hits in the building.
    /// </summary>
    public static string? ScopeFail(
        Document doc, UIDocument uidoc, IReadOnlyList<string> mepIds, IReadOnlyList<string> hostIds)
    {
        if (ResolveHosts(doc, uidoc, hostIds).Count > 0) return null;
        if (mepIds.Count > 0) return null;
        foreach (var id in uidoc.Selection.GetElementIds())
        {
            var el = doc.GetElement(id);
            if (el == null) continue;
            if (el is RevitLinkInstance || IsMep(el)) return null;
        }
        try
        {
            var refs = uidoc.Selection.GetReferences();
            if (refs != null)
            {
                foreach (var rf in refs)
                {
                    if (rf.LinkedElementId != ElementId.InvalidElementId) return null;
                }
            }
        }
        catch { /* GetReferences not available */ }

        return "Please select a wall, floor, beam, column, or roof (or pass hostIds). Linked MEP is found automatically against that host; query tools cannot see it.";
    }

    public static List<ClashHit> Find(
        Document doc,
        UIDocument uidoc,
        IReadOnlyList<string> mepIds,
        IReadOnlyList<string> hostIds,
        double clearanceFt,
        string shape,
        int limit)
    {
        var meps = ResolveMep(doc, uidoc, mepIds, hostIds);
        var hosts = ResolveHosts(doc, uidoc, hostIds);
        var hits = new List<ClashHit>();
        if (meps.Count == 0) return hits;
        var hostSolidsCache = new Dictionary<ElementId, List<Solid>>();

        foreach (var mep in meps)
        {
            if (hits.Count >= limit) break;
            try { if (mep.Element == null || !mep.Element.IsValidObject) continue; }
            catch { continue; }
            var mepBox = BoxOf(mep.Element, mep.ToHost);
            IList<Element> candidates = hosts;
            if (candidates.Count > 0)
            {
                var any = false;
                foreach (var h in candidates)
                {
                    if (BoxesOverlap(mepBox, h)) { any = true; break; }
                }
                if (!any) continue;
            }
            var mepSolids = SolidsOf(mep.Element, mep.ToHost).ToList();
            if (candidates.Count == 0)
                candidates = HostsOverlapping(doc, mepBox, mepSolids);

            foreach (var host in candidates)
            {
                if (hits.Count >= limit) break;
                if (Equals(host.Document, mep.Element.Document) && host.Id == mep.Element.Id) continue;
                if (!IsHost(host)) continue;
                if (!BoxesOverlap(mepBox, host)) continue;
                if (!hostSolidsCache.TryGetValue(host.Id, out var hostSolids))
                {
                    hostSolids = SolidsOf(host, Transform.Identity).ToList();
                    hostSolidsCache[host.Id] = hostSolids;
                }
                var inter = FirstIntersect(mepSolids, hostSolids);
                if (inter == null) continue;
                // PointHost = MEP curve × xf ∩ host Face (host coords). Boolean volume only confirms the pair.
                if (!ClashPoint(mep.Element, mep.ToHost, mepBox, host, hostSolids, inter, out var centroid))
                    continue;
                if (!InsideHost(host, centroid)) continue;
                if (!SnapToHost(host, centroid, out centroid)) continue;
                HostFrame(host, out var dir, out var up, out _);
                ExtentsFromSolid(inter, centroid, dir, up, out var width, out var height);
                width += 2 * clearanceFt;
                height += 2 * clearanceFt;
                var depth = HostThickness(host) + Metric.MmToFt(50);
                if (width < Metric.MmToFt(20) || height < Metric.MmToFt(20)) continue;
                var resolved = ResolveShape(shape, width, height);
                var key = host.UniqueId + "|" + mep.Element.UniqueId;
                hits.Add(new ClashHit(
                    mep.Element.Id, host.Id, false, mep.Linked,
                    centroid, dir, width, height, depth, resolved, host.LevelId, key));
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

    static List<MepRef> ResolveMep(Document doc, UIDocument uidoc, IReadOnlyList<string> ids, IReadOnlyList<string> hostIds)
    {
        var list = new List<MepRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seeds = new List<Element>();
        if (ids.Count > 0)
        {
            foreach (var s in ids)
            {
                try
                {
                    var eid = RevitParse.ParseEid(s);
                    var el = doc.GetElement(eid);
                    if (el != null) seeds.Add(el);
                    else AddMepByIdFromLinks(list, seen, doc, eid);
                }
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
            AddMepFromLinkedSelection(list, seen, doc, uidoc);
        }

        foreach (var el in seeds)
        {
            if (el is RevitLinkInstance link)
            {
                AddMepFromLink(list, seen, link);
                continue;
            }
            if (!IsMep(el)) continue;
            TryAddMep(list, seen, el, ToHostSpace(doc, el), !Equals(el.Document, doc));
        }

        if (list.Count == 0)
        {
            var hostsInScope = ResolveHosts(doc, uidoc, hostIds).Count > 0;
            if (ids.Count == 0 || hostsInScope)
                AddMepFromAllLinks(list, seen, doc);
        }

        return list;
    }

    static void AddMepFromLinkedSelection(List<MepRef> list, HashSet<string> seen, Document doc, UIDocument uidoc)
    {
        IList<Reference>? refs;
        try { refs = uidoc.Selection.GetReferences(); }
        catch { return; }
        if (refs == null) return;
        foreach (var rf in refs)
        {
            if (rf.LinkedElementId == ElementId.InvalidElementId) continue;
            var link = doc.GetElement(rf.ElementId) as RevitLinkInstance;
            if (link == null) continue;
            var ldoc = link.GetLinkDocument();
            var el = ldoc?.GetElement(rf.LinkedElementId);
            if (el == null || !IsMep(el)) continue;
            TryAddMep(list, seen, el, link.GetTotalTransform(), true);
        }
    }

    static void AddMepFromAllLinks(List<MepRef> list, HashSet<string> seen, Document hostDoc)
    {
        foreach (var link in new FilteredElementCollector(hostDoc)
            .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            AddMepFromLink(list, seen, link);
    }

    static void AddMepFromLink(List<MepRef> list, HashSet<string> seen, RevitLinkInstance link)
    {
        var ldoc = link.GetLinkDocument();
        if (ldoc == null) return;
        var xf = link.GetTotalTransform();
        IList<Element> meps;
        try
        {
            meps = new FilteredElementCollector(ldoc)
                .WherePasses(new ElementMulticategoryFilter(MepCats))
                .WhereElementIsNotElementType()
                .ToElements();
        }
        catch { return; }
        foreach (var el in meps)
        {
            if (!IsMep(el)) continue;
            TryAddMep(list, seen, el, xf, true);
        }
    }

    static void AddMepByIdFromLinks(List<MepRef> list, HashSet<string> seen, Document hostDoc, ElementId eid)
    {
        foreach (var link in new FilteredElementCollector(hostDoc)
            .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            var ldoc = link.GetLinkDocument();
            var el = ldoc?.GetElement(eid);
            if (el == null || !IsMep(el)) continue;
            TryAddMep(list, seen, el, link.GetTotalTransform(), true);
        }
    }

    static void TryAddMep(List<MepRef> list, HashSet<string> seen, Element el, Transform xf, bool linked)
    {
        var key = el.Document.Title + ":" + el.Id.ToString();
        if (!seen.Add(key)) return;
        list.Add(new MepRef(el, xf, linked));
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
                    .WherePasses(new ElementMulticategoryFilter(HostCategories))
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
                    .WherePasses(new ElementMulticategoryFilter(HostCategories))
                    .WherePasses(new BoundingBoxIntersectsFilter(outline))
                    .WhereElementIsNotElementType())
                    found[el.Id] = el;
            }
            catch { /* outline inverted */ }
        }
        return found.Values.ToList();
    }

    static bool IsMep(Element el) => Matches(el, MepCats);
    internal static bool IsHost(Element el) => Matches(el, HostCategories);

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
        // XML GeometryInstance.GetInstanceGeometry: geometry is in the coordinate system of
        // the model that owns this instance (the link document). Instance.GetTotalTransform
        // maps link -> host (includes true north for import-like instances; for RevitLinkInstance
        // it is the placement transform). Apply that ONCE with SolidUtils.CreateTransformed.
        // Do not GeometryElement.GetTransformed then GetInstanceGeometry — Autodesk forum:
        // GetTransformed does not apply to nested GeometryInstance, and mixing both doubles.
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
            if (WorldBox(sa, out var amin, out var amax) && WorldBox(sb, out var bmin, out var bmax)
                && !CornersOverlap(amin, amax, bmin, bmax))
                continue;
            try
            {
                var inter = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb, BooleanOperationsType.Intersect);
                if (inter != null && inter.Volume > 1e-9) return inter;
            }
            catch { /* disjoint or non-manifold */ }
        }
        return null;
    }

    /// <summary>
    /// Clash POINT at the source. Autodesk MEP practice (AvoidObstruction): transform the
    /// MEP LocationCurve by GetTotalTransform once, Face.Intersect host solids, place there.
    /// If ModelCentroid of a CreateTransformed solid is ~2× BoxOf(mep, xf) mid — or far from
    /// that once-transformed bbox — trust curve/bbox-once, not the solid centroid.
    /// </summary>
    static bool ClashPoint(
        Element mep, Transform toHost, BoundingBoxXYZ? mepBox,
        Element host, List<Solid> hostSolids, Solid inter, out XYZ point)
    {
        if (TryCurveHit(mep, toHost, host, hostSolids, out point))
            return true;

        XYZ solidC;
        var haveSolid = ModelCentroid(inter, out solidC);
        XYZ? boxMidN = null;
        if (mepBox != null && Mid(mepBox.Min, mepBox.Max, out var boxMid))
            boxMidN = boxMid;

        if (haveSolid && !LooksDoubled(solidC, mepBox, boxMidN))
        {
            point = solidC;
            return true;
        }

        if (mepBox != null && BoxOnceHit(mepBox, host, out point))
            return true;

        if (haveSolid)
        {
            point = solidC;
            return true;
        }

        point = XYZ.Zero;
        return false;
    }

    static bool LooksDoubled(XYZ centroid, BoundingBoxXYZ? mepBox, XYZ? boxMid)
    {
        if (mepBox == null) return false;
        var pad = Metric.MmToFt(300);
        var far = centroid.X < mepBox.Min.X - pad || centroid.X > mepBox.Max.X + pad
               || centroid.Y < mepBox.Min.Y - pad || centroid.Y > mepBox.Max.Y + pad
               || centroid.Z < mepBox.Min.Z - pad || centroid.Z > mepBox.Max.Z + pad;
        if (boxMid == null) return far;
        var doubled = new XYZ(boxMid.X * 2.0, boxMid.Y * 2.0, boxMid.Z * 2.0);
        var dBox = centroid.DistanceTo(boxMid);
        var d2 = centroid.DistanceTo(doubled);
        var twice = dBox > Metric.MmToFt(500) && d2 <= dBox * 0.5 + Metric.MmToFt(50);
        return twice || (far && d2 < dBox);
    }

    static bool BoxOnceHit(BoundingBoxXYZ mepBox, Element host, out XYZ point)
    {
        point = XYZ.Zero;
        BoundingBoxXYZ? hbb;
        try { hbb = host.get_BoundingBox(null); }
        catch { return false; }
        if (hbb == null || !WorldMinMax(hbb, out var hmin, out var hmax)) return false;
        var minx = Math.Max(mepBox.Min.X, hmin.X);
        var miny = Math.Max(mepBox.Min.Y, hmin.Y);
        var minz = Math.Max(mepBox.Min.Z, hmin.Z);
        var maxx = Math.Min(mepBox.Max.X, hmax.X);
        var maxy = Math.Min(mepBox.Max.Y, hmax.Y);
        var maxz = Math.Min(mepBox.Max.Z, hmax.Z);
        if (minx > maxx || miny > maxy || minz > maxz)
        {
            if (!Mid(mepBox.Min, mepBox.Max, out var mid)) return false;
            return SnapToHost(host, mid, out point);
        }
        return Mid(new XYZ(minx, miny, minz), new XYZ(maxx, maxy, maxz), out point);
    }

    static bool TryCurveHit(Element mep, Transform toHost, Element host, List<Solid> hostSolids, out XYZ point)
    {
        point = XYZ.Zero;
        var curve = MepLocationCurve(mep);
        if (curve == null) return false;
        if (!toHost.IsIdentity)
        {
            try { curve = curve.CreateTransformed(toHost); }
            catch { return false; }
        }

        var pts = new List<XYZ>();
        foreach (var solid in hostSolids)
        {
            FaceArray? faces;
            try { faces = solid.Faces; }
            catch { continue; }
            if (faces == null) continue;
            foreach (Face f in faces)
            {
                IntersectionResultArray ira;
                SetComparisonResult scr;
                try { scr = f.Intersect(curve, out ira); }
                catch { continue; }
                if (scr != SetComparisonResult.Overlap || ira == null) continue;
                foreach (IntersectionResult ir in ira)
                {
                    XYZ p;
                    try { p = ir.XYZPoint; }
                    catch { continue; }
                    if (p == null) continue;
                    if (!InsideHost(host, p)) continue;
                    pts.Add(p);
                }
            }
        }
        if (pts.Count == 0) return SampleCurveInHost(curve, host, out point);
        return AveragePts(pts, out point);
    }

    static bool SampleCurveInHost(Curve curve, Element host, out XYZ point)
    {
        point = XYZ.Zero;
        var pts = new List<XYZ>();
        try
        {
            for (var i = 0; i <= 16; i++)
            {
                var p = curve.Evaluate(i / 16.0, true);
                if (InsideHost(host, p)) pts.Add(p);
            }
        }
        catch { return false; }
        return pts.Count > 0 && AveragePts(pts, out point);
    }

    static bool AveragePts(List<XYZ> pts, out XYZ point)
    {
        point = XYZ.Zero;
        if (pts.Count == 0) return false;
        var acc = XYZ.Zero;
        foreach (var p in pts) acc = acc + p;
        var n = pts.Count;
        point = new XYZ(acc.X / n, acc.Y / n, acc.Z / n);
        return true;
    }

    static Curve? MepLocationCurve(Element mep)
    {
        try
        {
            if (mep.Location is LocationCurve lc && lc.Curve != null)
                return lc.Curve;
        }
        catch { }

        try
        {
            if (mep is FamilyInstance fi && fi.MEPModel != null && fi.MEPModel.ConnectorManager != null)
            {
                Connector? a = null;
                Connector? b = null;
                foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
                {
                    if (c == null) continue;
                    if (a == null) a = c;
                    else { b = c; break; }
                }
                if (a != null && b != null && a.Origin.DistanceTo(b.Origin) > 1e-9)
                    return Line.CreateBound(a.Origin, b.Origin);
            }
        }
        catch { }
        return null;
    }

    static BoundingBoxXYZ? BoxOf(Element el, Transform toHost)
    {
        BoundingBoxXYZ? bb;
        try { bb = el.get_BoundingBox(null); }
        catch { return null; }
        if (bb == null) return null;
        if (toHost.IsIdentity) return bb;
        XYZ? mn = null, mx = null;
        foreach (var cx in new[] { bb.Min.X, bb.Max.X })
        foreach (var cy in new[] { bb.Min.Y, bb.Max.Y })
        foreach (var cz in new[] { bb.Min.Z, bb.Max.Z })
        {
            var p = toHost.OfPoint(new XYZ(cx, cy, cz));
            mn = mn == null ? p : new XYZ(Math.Min(mn.X, p.X), Math.Min(mn.Y, p.Y), Math.Min(mn.Z, p.Z));
            mx = mx == null ? p : new XYZ(Math.Max(mx.X, p.X), Math.Max(mx.Y, p.Y), Math.Max(mx.Z, p.Z));
        }
        if (mn == null || mx == null) return null;
        return new BoundingBoxXYZ { Min = mn, Max = mx };
    }

    /// <summary>
    /// Skip host solid boolean when MEP bbox and host bbox do not overlap.
    /// 85 walls+floors × all linked MEP solids exceeded the 120s ExternalEvent wait
    /// (live: Timeout then InvalidObject on retry).
    /// </summary>
    static bool BoxesOverlap(BoundingBoxXYZ? mepBox, Element host)
    {
        if (mepBox == null) return true;
        BoundingBoxXYZ? hb;
        try
        {
            if (!host.IsValidObject) return false;
            hb = host.get_BoundingBox(null);
        }
        catch { return false; }
        if (hb == null) return true;
        const double pad = 0.5;
        if (mepBox.Max.X < hb.Min.X - pad || mepBox.Min.X > hb.Max.X + pad) return false;
        if (mepBox.Max.Y < hb.Min.Y - pad || mepBox.Min.Y > hb.Max.Y + pad) return false;
        if (mepBox.Max.Z < hb.Min.Z - pad || mepBox.Min.Z > hb.Max.Z + pad) return false;
        return true;
    }

    /// <summary>
    /// SDK Solid.GetBoundingBox is local bounds + Transform, unlike Element.BoundingBox
    /// which is already model coordinates. Edge tessellation is 3d model points.
    /// </summary>
    static bool ModelCentroid(Solid s, out XYZ c)
    {
        c = XYZ.Zero;
        var acc = XYZ.Zero;
        var n = 0;
        try
        {
            foreach (Edge e in s.Edges)
            {
                IList<XYZ>? pts;
                try { pts = e.Tessellate(); }
                catch { continue; }
                if (pts == null) continue;
                foreach (var p in pts)
                {
                    acc = acc + p;
                    n++;
                }
            }
        }
        catch { return false; }
        if (n == 0) return WorldBox(s, out var min, out var max) && Mid(min, max, out c);
        c = new XYZ(acc.X / n, acc.Y / n, acc.Z / n);
        return true;
    }

    static bool Mid(XYZ min, XYZ max, out XYZ c)
    {
        c = new XYZ((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        return true;
    }

    /// <summary>
    /// XML: Element.BoundingBox stores bounds in modeling coordinates with an identity
    /// transform. Do not OfPoint Min through Transform — that 2× the origin and PlaceOne
    /// InsideHost then deletes snaps that already sit on the wall (live: Failed 6).
    /// </summary>
    static bool WorldMinMax(BoundingBoxXYZ bb, out XYZ min, out XYZ max)
    {
        min = bb.Min;
        max = bb.Max;
        return true;
    }

    /// <summary>
    /// OneLevelBased NewFamilyInstance keeps Z on the level; do not reject Z.
    /// Walls: XY must sit on the location curve (width + 300 mm).
    /// </summary>
    internal static bool InsideHost(Element host, XYZ p)
    {
        if (host is Wall wall && wall.Location is LocationCurve lc && lc.Curve != null)
        {
            try
            {
                var ir = lc.Curve.Project(p);
                if (ir == null) return false;
                var on = ir.XYZPoint;
                var dxy = new XYZ(p.X - on.X, p.Y - on.Y, 0).GetLength();
                return dxy <= wall.Width + Metric.MmToFt(300);
            }
            catch { return false; }
        }
        BoundingBoxXYZ? bb;
        try { bb = host.get_BoundingBox(null); }
        catch { return false; }
        if (bb == null) return false;
        var pad = HostThickness(host) + Metric.MmToFt(300);
        if (p.X < bb.Min.X - pad || p.X > bb.Max.X + pad) return false;
        if (p.Y < bb.Min.Y - pad || p.Y > bb.Max.Y + pad) return false;
        if (p.Z < bb.Min.Z - pad || p.Z > bb.Max.Z + pad) return false;
        return true;
    }

    /// <summary>
    /// Autodesk wall samples / ShaftHolePuncher put p1/p2 on the wall curve — never a free XYZ.
    /// PointHost must already InsideHost; never snap a far centroid onto a wall end.
    /// </summary>
    static bool SnapToHost(Element host, XYZ p, out XYZ snapped)
    {
        snapped = p;
        if (!InsideHost(host, p)) return false;
        if (host is Wall wall && wall.Location is LocationCurve lc && lc.Curve != null)
        {
            var curve = lc.Curve;
            IntersectionResult? ir = null;
            try { ir = curve.Project(p); }
            catch { ir = null; }
            try
            {
                var t0 = curve.GetEndParameter(0);
                var t1 = curve.GetEndParameter(1);
                var lo = Math.Min(t0, t1);
                var hi = Math.Max(t0, t1);
                var t = ir != null ? ir.Parameter : (lo + hi) / 2;
                if (t < lo) t = lo;
                if (t > hi) t = hi;
                var on = curve.Evaluate(t, false);
                var z = p.Z;
                BoundingBoxXYZ? bb;
                try { bb = host.get_BoundingBox(null); }
                catch { bb = null; }
                if (bb != null && WorldMinMax(bb, out var mn, out var mx))
                {
                    if (z < mn.Z) z = mn.Z;
                    if (z > mx.Z) z = mx.Z;
                }
                snapped = new XYZ(on.X, on.Y, z);
                return true;
            }
            catch { return false; }
        }
        snapped = p;
        return true;
    }

    static void ExtentsFromSolid(Solid s, XYZ origin, XYZ dir, XYZ up, out double width, out double height)
    {
        double minD = double.PositiveInfinity, maxD = double.NegativeInfinity;
        double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
        var any = false;
        try
        {
            foreach (Edge e in s.Edges)
            {
                IList<XYZ>? pts;
                try { pts = e.Tessellate(); }
                catch { continue; }
                if (pts == null) continue;
                foreach (var p in pts)
                {
                    any = true;
                    var v = p - origin;
                    var d = v.DotProduct(dir);
                    var u = v.DotProduct(up);
                    if (d < minD) minD = d;
                    if (d > maxD) maxD = d;
                    if (u < minU) minU = u;
                    if (u > maxU) maxU = u;
                }
            }
        }
        catch { }
        if (!any)
        {
            width = height = Metric.MmToFt(20);
            return;
        }
        width = Math.Max(maxD - minD, Metric.MmToFt(20));
        height = Math.Max(maxU - minU, Metric.MmToFt(20));
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
        if (FamilyContract.IsVerticalHost(host)) // is Floor or RoofBase
        {
            dir = XYZ.BasisX;
            up = XYZ.BasisY;
            norm = XYZ.BasisZ;
            return;
        }
        if (host.Category != null && host.Category.Id == new ElementId(BuiltInCategory.OST_StructuralColumns))
        {
            dir = XYZ.BasisX;
            up = XYZ.BasisZ;
            norm = XYZ.BasisY;
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

    static bool CornersOverlap(XYZ aMin, XYZ aMax, XYZ bMin, XYZ bMax)
    {
        const double pad = 0.5;
        if (aMax.X < bMin.X - pad || aMin.X > bMax.X + pad) return false;
        if (aMax.Y < bMin.Y - pad || aMin.Y > bMax.Y + pad) return false;
        if (aMax.Z < bMin.Z - pad || aMin.Z > bMax.Z + pad) return false;
        return true;
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
