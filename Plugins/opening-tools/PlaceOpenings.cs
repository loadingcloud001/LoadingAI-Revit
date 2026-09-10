using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.OpeningTools;

internal static class PlaceOpenings
{
    public static string Run(
        Document doc,
        UIDocument uidoc,
        IReadOnlyList<string> mepIds,
        IReadOnlyList<string> hostIds,
        string shape,
        double clearanceMm,
        string? familyName,
        string? levelName,
        int limit)
    {
        Level? forced = null;
        if (!string.IsNullOrWhiteSpace(levelName))
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var names = levels.Select(l => l.Name).ToList();
            var resolved = LevelNames.Resolve(levelName, names);
            if (resolved == null) return LevelNames.NotFound(levelName, names);
            forced = levels.FirstOrDefault(l => l.Name.Equals(resolved, StringComparison.OrdinalIgnoreCase));
            if (forced == null) return LevelNames.NotFound(levelName, names);
        }

        var scope = ClashProbe.ScopeFail(doc, uidoc, mepIds, hostIds);
        if (scope != null) return scope;

        if (!ClashProbe.AnyMep(doc, uidoc, mepIds, hostIds))
            return "Please select MEP elements first.";

        var hits = ClashProbe.Find(doc, uidoc, mepIds, hostIds, Metric.MmToFt(clearanceMm), shape, limit);
        if (hits.Count == 0)
            return "No MEP-host clash found (bbox then solid intersect).";

        var placed = 0;
        var failed = 0;
        var skipped = 0;
        var punched = 0;
        var placedAt = new List<string>();
        var pending = new List<Pending>();
        var placedKeys = new HashSet<string>(StringComparer.Ordinal);
        var placedSrcHost = new HashSet<string>(StringComparer.Ordinal);
        IndexExistingOpenings(doc, placedKeys, placedSrcHost);
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tx = new Transaction(doc, "LoadingAI for Revit: create openings from clash");
        tx.Start();
        try
        {
            foreach (var hit in hits)
            {
                var hostNeed = doc.GetElement(hit.HostId);
                if (hostNeed == null) continue;
                var want = FamilyContract.FamilyNameFor(hostNeed, familyName, hit.Shape);
                // Legacy LoadingAI_Opening_Horizontal / LoadingAI_Opening_Vertical still Find fallback.
                if (!loaded.Add(want)) continue;
                if (!FamilyContract.TryLoad(doc, want))
                {
                    tx.RollBack();
                    return FamilyContract.FailReason(null, want);
                }
            }
            foreach (var hit in hits)
            {
                try
                {
                    if (AlreadyPlaced(hit, placedKeys, placedSrcHost))
                    {
                        skipped++;
                        continue;
                    }
                    var host = doc.GetElement(hit.HostId);
                    if (host == null) { failed++; continue; }
                    var symbol = FamilyContract.Find(doc, familyName, hit.Shape, host);
                    if (symbol == null)
                    {
                        failed++;
                        continue;
                    }
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }
                    var inst = PlaceOne(doc, symbol, hit, forced);
                    if (inst == null)
                    {
                        failed++;
                        continue;
                    }
                    ApplySize(inst, hit);
                    DropOriginToOpeningBottom(doc, inst, hit);
                    WriteOp(inst, hit);
                    pending.Add(new Pending { Inst = inst, Host = host, Hit = hit });
                    RememberPlaced(hit, placedKeys, placedSrcHost);
                }
                catch
                {
                    failed++;
                }
            }
            // Tammik: place all, then regenerate, then cut all
            doc.Regenerate();
            foreach (var row in pending)
            {
                if (CutLocal.TryPunch(doc, row.Host, row.Inst))
                {
                    punched++;
                    placed++;
                    var loc = (row.Inst.Location as LocationPoint)?.Point ?? row.Hit.Centroid;
                    placedAt.Add(row.Inst.Id.ToString() + "(" +
                        loc.X.ToString("F1", CultureInfo.InvariantCulture) + ", " +
                        loc.Y.ToString("F1", CultureInfo.InvariantCulture) + ", " +
                        loc.Z.ToString("F1", CultureInfo.InvariantCulture) + " ft)");
                }
                else
                {
                    try { doc.Delete(row.Inst.Id); } catch { }
                    failed++; // no-punch
                }
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            return $"Failed: {ex.Message}";
        }

        var hostList = string.Join(", ", hits.Select(h => h.HostId.ToString()).Distinct());
        var extra = skipped > 0 ? $" Skipped {skipped} already placed." : "";
        var where = placedAt.Count == 0 ? "" : " " + string.Join("; ", placedAt) + ".";
        var famNames = pending
            .Select(p => p.Inst.Symbol?.FamilyName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var famLabel = famNames.Count > 0
            ? string.Join(" / ", famNames)
            : FamilyContract.ShippedFamilyLabel;
        var placeType = pending.Count > 0
            ? pending[0].Inst.Symbol?.Family.FamilyPlacementType.ToString() ?? "OneLevelBased"
            : "OneLevelBased";
        return $"Placed {placed} opening(s) in the active document ({famLabel}, {placeType}) on host(s) {hostList}.{where} Punched {punched}. Failed {failed}.{extra}";
    }

    struct Pending
    {
        public FamilyInstance Inst;
        public Element Host;
        public ClashHit Hit;
    }

    static FamilyInstance? PlaceOne(Document doc, FamilySymbol symbol, ClashHit hit, Level? forced)
    {
        if (forced != null && hit.LevelId != ElementId.InvalidElementId && hit.LevelId != forced.Id)
            return null;
        var host = doc.GetElement(hit.HostId);
        if (host == null) return null;

        var pt = symbol.Family.FamilyPlacementType;
        if (pt == FamilyPlacementType.WorkPlaneBased)
        {
            if (!TryHostFace(host, hit.Centroid, out var face, out var loc, out var normal))
                return null;
            var dir = ReferenceDir(hit.HostDir, normal);
            return doc.Create.NewFamilyInstance(face, loc, dir, symbol);
        }
        if (pt == FamilyPlacementType.ViewBased
            || pt == FamilyPlacementType.CurveBased
            || pt == FamilyPlacementType.CurveBasedDetail
            || pt == FamilyPlacementType.Adaptive
            || pt == FamilyPlacementType.CurveDrivenStructural)
            return null;

        if (pt == FamilyPlacementType.OneLevelBasedHosted)
        {
            try
            {
                return doc.Create.NewFamilyInstance(hit.Centroid, symbol, host, StructuralType.NonStructural);
            }
            catch { /* Generic Model void is not wall-hosted; fall through to level */ }
        }

        var level = forced ?? doc.GetElement(hit.LevelId) as Level ?? NearestLevel(doc, hit.Centroid);
        if (level == null) return null;
        var inst = doc.Create.NewFamilyInstance(hit.Centroid, symbol, level, StructuralType.NonStructural);
        PinToPointHost(doc, inst, hit.Centroid, level);
        PinVerticalToHostTop(doc, inst, host, hit.Centroid);
        RotateToHost(doc, inst, hit);
        var xyz = (inst.Location as LocationPoint)?.Point ?? hit.Centroid;
        if (!ClashProbe.InsideHost(host, xyz))
        {
            try { doc.Delete(inst.Id); }
            catch { }
            return null; // OneLevelBased Z is on the level; XY must sit on the wall
        }
        return inst;
    }

    /// <summary>
    /// OneLevelBased NewFamilyInstance keeps Z on the level; lp.Point often rejects Z.
    /// Pin XY+Z onto PointHost (host-face station) so the void actually meets the wall solid.
    /// Live 4f398fd: Punched 0 Failed 6 — clash hits were on the wall, cutter sat on the level.
    /// </summary>
    static void PinToPointHost(Document doc, FamilyInstance inst, XYZ want, Level? level)
    {
        if (inst.Location is LocationPoint lp)
        {
            try { lp.Point = want; }
            catch { /* level-based origin may reject Z */ }
            try
            {
                var cur = lp.Point;
                lp.Point = new XYZ(want.X, want.Y, cur.Z);
            }
            catch { }
        }
        var now = (inst.Location as LocationPoint)?.Point;
        if (now != null)
        {
            var d = want - now;
            if (d.GetLength() > 1e-6)
            {
                try { ElementTransformUtils.MoveElement(doc, inst.Id, d); }
                catch { }
            }
        }
        now = (inst.Location as LocationPoint)?.Point;
        if (now == null || Math.Abs(now.Z - want.Z) < 1e-4) return;
        var off = level != null ? want.Z - level.Elevation : want.Z - now.Z;
        foreach (BuiltInParameter bip in new[]
        {
            BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM,
            BuiltInParameter.INSTANCE_ELEVATION_PARAM,
            BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM
        })
        {
            Parameter? p;
            try { p = inst.get_Parameter(bip); }
            catch { continue; }
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) continue;
            try { p.Set(off); return; }
            catch { }
        }
    }

    /// <summary>
    /// Vertical family origin is slab top on the Level. PointHost is MEP center mid-slab —
    /// pin XY to the clash, Z to host bbox Max (model coords; do not OfPoint Transform).
    /// </summary>
    static void PinVerticalToHostTop(Document doc, FamilyInstance inst, Element host, XYZ want)
    {
        if (!FamilyContract.IsVerticalHost(host)) return;
        var bb = host.get_BoundingBox(null);
        if (bb == null) return;
        var now = (inst.Location as LocationPoint)?.Point;
        if (now == null) return;
        var dest = new XYZ(want.X, want.Y, bb.Max.Z);
        var d = dest - now;
        if (d.GetLength() < 1e-6) return;
        try { ElementTransformUtils.MoveElement(doc, inst.Id, d); } catch { }
    }

    static void IndexExistingOpenings(Document doc, HashSet<string> keys, HashSet<string> srcHost)
    {
        foreach (var fi in new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>())
        {
            try { if (!fi.IsValidObject) continue; }
            catch { continue; }
            if (!FamilyContract.IsPluginFamily(fi.Symbol?.FamilyName)) continue;
            var key = FamilyContract.OpValue(fi, "OP_Key");
            if (!string.IsNullOrEmpty(key)) keys.Add(key);
            var src = FamilyContract.OpValue(fi, "OP_SourceId");
            var host = FamilyContract.OpValue(fi, "OP_HostId");
            if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(host))
                srcHost.Add(src + "|" + host);
        }
    }

    static bool AlreadyPlaced(ClashHit hit, HashSet<string> keys, HashSet<string> srcHost)
    {
        if (!string.IsNullOrEmpty(hit.Key) && keys.Contains(hit.Key)) return true;
        return srcHost.Contains(hit.MepId + "|" + hit.HostId);
    }

    static void RememberPlaced(ClashHit hit, HashSet<string> keys, HashSet<string> srcHost)
    {
        if (!string.IsNullOrEmpty(hit.Key)) keys.Add(hit.Key);
        srcHost.Add(hit.MepId + "|" + hit.HostId);
    }

    static Level? NearestLevel(Document doc, XYZ p)
    {
        Level? best = null;
        var bestD = double.PositiveInfinity;
        foreach (var lv in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
        {
            var d = Math.Abs(lv.Elevation - p.Z);
            if (d >= bestD) continue;
            bestD = d;
            best = lv;
        }
        return best;
    }

    static void RotateToHost(Document doc, FamilyInstance inst, ClashHit hit)
    {
        if (hit.HostDir.GetLength() < 1e-9) return;
        var loc = (inst.Location as LocationPoint)?.Point ?? hit.Centroid;
        var axis = Line.CreateBound(loc, loc + XYZ.BasisZ);
        var angle = XYZ.BasisX.AngleOnPlaneTo(hit.HostDir, XYZ.BasisZ);
        if (Math.Abs(angle) < 1e-6) return;
        try { ElementTransformUtils.RotateElement(doc, inst.Id, axis, angle); }
        catch { }
    }

    static bool TryHostFace(Element host, XYZ centroid, out Face face, out XYZ loc, out XYZ normal)
    {
        face = null!;
        loc = centroid;
        normal = XYZ.BasisZ;
        var fromRefs = FacesFromHostUtils(host).ToList();
        if (Pick(fromRefs, centroid, out face, out loc, out normal)) return true;
        return Pick(GeometryFaces(host).ToList(), centroid, out face, out loc, out normal);
    }

    static IEnumerable<Face> FacesFromHostUtils(Element host)
    {
        if (host is not HostObject ho) yield break;
        var refs = new List<Reference>();
        try
        {
            if (FamilyContract.IsVerticalHost(host))
            {
                foreach (var r in HostObjectUtils.GetTopFaces(ho)) refs.Add(r);
            }
            else
            {
                try { foreach (var r in HostObjectUtils.GetSideFaces(ho, ShellLayerType.Exterior)) refs.Add(r); }
                catch { /* host has no exterior side */ }
                try { foreach (var r in HostObjectUtils.GetSideFaces(ho, ShellLayerType.Interior)) refs.Add(r); }
                catch { /* host has no interior side */ }
            }
        }
        catch { yield break; }

        foreach (var r in refs)
        {
            Face? f = null;
            try { f = host.GetGeometryObjectFromReference(r) as Face; }
            catch { }
            if (f != null) yield return f;
        }
    }

    static IEnumerable<Face> GeometryFaces(Element host)
    {
        var opt = new Options { ComputeReferences = true, IncludeNonVisibleObjects = false };
        GeometryElement? geo;
        try { geo = host.get_Geometry(opt); }
        catch { yield break; }
        if (geo == null) yield break;
        foreach (var f in FacesIn(geo)) yield return f;
    }

    static IEnumerable<Face> FacesIn(GeometryElement geo)
    {
        foreach (var obj in geo)
        {
            if (obj is Solid solid && solid.Faces != null)
            {
                foreach (Face f in solid.Faces)
                {
                    if (f != null) yield return f;
                }
            }
            else if (obj is GeometryInstance gi)
            {
                GeometryElement? inst = null;
                try { inst = gi.GetInstanceGeometry(); }
                catch { }
                if (inst == null) continue;
                foreach (var inner in FacesIn(inst)) yield return inner;
            }
        }
    }

    static bool Pick(List<Face> faces, XYZ centroid, out Face face, out XYZ loc, out XYZ normal)
    {
        face = null!;
        loc = centroid;
        normal = XYZ.BasisZ;
        Face? best = null;
        var bestDist = double.PositiveInfinity;
        var bestLoc = centroid;
        var bestN = XYZ.BasisZ;
        foreach (var f in faces)
        {
            if (f.Reference == null) continue;
            IntersectionResult? ir;
            try { ir = f.Project(centroid); }
            catch { continue; }
            if (ir == null) continue;
            var d = Math.Abs(ir.Distance);
            if (d >= bestDist) continue;
            XYZ n;
            try { n = f.ComputeNormal(ir.UVPoint); }
            catch { continue; }
            if (n.GetLength() < 1e-9) continue;
            best = f;
            bestDist = d;
            bestLoc = ir.XYZPoint;
            bestN = n.Normalize();
        }
        if (best == null) return false;
        face = best;
        loc = bestLoc;
        normal = bestN;
        return true;
    }

    static XYZ ReferenceDir(XYZ preferred, XYZ normal)
    {
        var n = normal.Normalize();
        var d = preferred.GetLength() < 1e-9 ? XYZ.BasisX : preferred.Normalize();
        if (Math.Abs(d.DotProduct(n)) > 0.99)
            d = Math.Abs(n.DotProduct(XYZ.BasisZ)) > 0.9 ? XYZ.BasisX : XYZ.BasisZ;
        var tan = d - n.Multiply(d.DotProduct(n));
        if (tan.GetLength() < 1e-9) tan = n.CrossProduct(XYZ.BasisX);
        if (tan.GetLength() < 1e-9) tan = n.CrossProduct(XYZ.BasisY);
        return tan.Normalize();
    }

    static void ApplySize(FamilyInstance inst, ClashHit hit)
    {
        var widthMm = Metric.FtToMm(hit.WidthFt);
        var heightMm = Metric.FtToMm(hit.HeightFt);
        if (FamilyContract.IsCircleShape(hit.Shape))
        {
            var r = Math.Max(widthMm, heightMm) / 2;
            SetLengthFt(inst, "Radius", Metric.MmToFt(r));
        }
        else
        {
            SetLengthFt(inst, "Width", hit.WidthFt);
            SetLengthFt(inst, "Height", hit.HeightFt);
            // Outer* is Horizontal Rectangle only (door/window splay). Vertical Rectangle
            // has none — SetLengthFt no-ops. Do not require Outer on floors/roofs.
            SetLengthFt(inst, "Outer Width", hit.WidthFt);
            SetLengthFt(inst, "Outer Height", hit.HeightFt);
        }
        SetLengthFt(inst, "Depth", hit.DepthFt);
    }

    /// <summary>
    /// Horizontal family origin = opening bottom (rectangle sill / circle bottom).
    /// PointHost is the MEP center; lower the instance by Height/2 so the hole is centered on the pipe.
    /// Vertical family origin is slab top — do not drop.
    /// </summary>
    static void DropOriginToOpeningBottom(Document doc, FamilyInstance inst, ClashHit hit)
    {
        var host = doc.GetElement(hit.HostId);
        if (host is Floor || host is RoofBase) return; // vertical family origin is slab top, not sill
        // opening bottom is the family origin
        var drop = new XYZ(0, 0, -(hit.HeightFt / 2.0));
        if (drop.GetLength() < 1e-6) return;
        try { ElementTransformUtils.MoveElement(doc, inst.Id, drop); }
        catch { }
    }

    static void WriteOp(FamilyInstance inst, ClashHit hit)
    {
        SetOrComment(inst, "OP_SourceId", hit.MepId.ToString());
        SetOrComment(inst, "OP_HostId", hit.HostId.ToString());
        if (!string.IsNullOrEmpty(hit.Key)) SetOrComment(inst, "OP_Key", hit.Key);
        SetOrComment(inst, "OP_HostLinked", hit.HostLinked ? "true" : "false");
        SetOrComment(inst, "OP_Shape", hit.Shape);
        SetOrComment(inst, "OP_WidthMm", Metric.FtToMm(hit.WidthFt).ToString("F0", CultureInfo.InvariantCulture));
        SetOrComment(inst, "OP_HeightMm", Metric.FtToMm(hit.HeightFt).ToString("F0", CultureInfo.InvariantCulture));
        SetOrComment(inst, "OP_DepthMm", Metric.FtToMm(hit.DepthFt).ToString("F0", CultureInfo.InvariantCulture));
    }

    static void SetLengthFt(Element el, string name, double ft)
    {
        // Live: Width set, Outer Width stayed 1000 mm. LookupParameter("Outer Width") misses;
        // walk Parameters and match names with spaces stripped (Outer Width == OuterWidth).
        // ShareWidth / Share Height are formulas (= Width/Height) — skip via IsReadOnly.
        var compact = CompactName(name);
        foreach (var p in AllLengthParams(el))
        {
            var n = p.Definition?.Name;
            if (n == null || CompactName(n) != compact) continue;
            if (p.IsReadOnly || p.StorageType != StorageType.Double) continue;
            try { p.Set(ft); return; } catch { }
        }
    }

    static string CompactName(string s)
        => s.Replace(" ", "").Replace("_", "").ToUpperInvariant();

    static IEnumerable<Parameter> AllLengthParams(Element el)
    {
        foreach (Parameter p in el.Parameters)
        {
            if (p != null) yield return p;
        }
        if (el is FamilyInstance fi && fi.Symbol != null)
        {
            foreach (Parameter p in fi.Symbol.Parameters)
            {
                if (p != null) yield return p;
            }
        }
    }

    static void SetOrComment(Element el, string name, string value)
    {
        if (RevitParse.TrySetParameter(el, name, value, out _)) return;
        var comments = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments == null || comments.IsReadOnly) return;
        var cur = comments.AsString() ?? "";
        var bit = name + "=" + value;
        comments.Set(string.IsNullOrWhiteSpace(cur) ? bit : cur + "; " + bit);
    }
}
