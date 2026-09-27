using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using LoadingAI.Core.Openings;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Revit.Openings;


internal static class PlaceOpenings
{
    internal static readonly TimeSpan ClashTimeout = TimeSpan.FromMinutes(10);

    internal static List<ClashHit> FilterByLevel(IReadOnlyList<ClashHit> hits, ElementId? forcedLevelId)
        => OpeningLevelFilter.FilterByKey(hits, h => h.LevelId.ToString(), forcedLevelId?.ToString());

    /// <summary>Probe plus combine. No transaction. Used by dryRun and by the plan slice.</summary>
    public static OpeningPlanResultDto Plan(
        Document doc,
        UIDocument uidoc,
        IReadOnlyList<string> mepIds,
        IReadOnlyList<string> hostIds,
        string shape,
        double clearanceMm,
        string? levelName,
        int limit)
    {
        ElementId? forcedLevelId = null;
        if (!string.IsNullOrWhiteSpace(levelName))
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var names = levels.Select(l => l.Name).ToList();
            var resolved = LevelNames.Resolve(levelName, names);
            if (resolved == null)
                return new OpeningPlanResultDto(LevelNames.NotFound(levelName, names), 0, 0, 0, Array.Empty<OpeningPlanHitDto>());
            forcedLevelId = levels.FirstOrDefault(l => l.Name.Equals(resolved, StringComparison.OrdinalIgnoreCase))?.Id;
            if (forcedLevelId == null)
                return new OpeningPlanResultDto(LevelNames.NotFound(levelName, names), 0, 0, 0, Array.Empty<OpeningPlanHitDto>());
        }

        var scope = OpeningProbe.ScopeFail(doc, uidoc, mepIds, hostIds);
        if (scope != null)
            return new OpeningPlanResultDto(scope, 0, 0, 0, Array.Empty<OpeningPlanHitDto>());
        if (!OpeningProbe.AnyMep(doc, uidoc, mepIds, hostIds))
            return new OpeningPlanResultDto("Please select MEP elements first.", 0, 0, 0, Array.Empty<OpeningPlanHitDto>());

        var hits = OpeningProbe.Find(doc, uidoc, mepIds, hostIds, Metric.MmToFt(clearanceMm), shape, limit);
        hits = FilterByLevel(hits, forcedLevelId);
        if (hits.Count == 0)
            return new OpeningPlanResultDto("No MEP-host clash found at the requested scope (bbox then solid intersect).", 0, 0, 0, Array.Empty<OpeningPlanHitDto>());
        hits = OpeningCombine.Apply(doc, hits, out var combine);
        var planned = hits.Select(h => new OpeningPlanHitDto(
            h.HostId.ToString(),
            h.MepId.ToString(),
            h.Centroid.X, h.Centroid.Y, h.Centroid.Z,
            Metric.FtToMm(h.WidthFt), Metric.FtToMm(h.HeightFt), Metric.FtToMm(h.DepthFt),
            h.Shape, h.Combined, h.CombinedCount, h.CombinedSources)).ToList();
        var note = combine.Clusters > 0 ? $" Combined {combine.Clusters} cluster(s)." : "";
        if (combine.Large > 0) note += " large (>700mm).";
        return new OpeningPlanResultDto(
            $"Planned {planned.Count} opening(s).{note}",
            planned.Count, combine.Clusters, combine.Large, planned);
    }

    public static OpeningPlaceWorkResult Run(
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
            if (resolved == null) return NoWrite(LevelNames.NotFound(levelName, names), LevelNames.NotFound(levelName, names));
            forced = levels.FirstOrDefault(l => l.Name.Equals(resolved, StringComparison.OrdinalIgnoreCase));
            if (forced == null) return NoWrite(LevelNames.NotFound(levelName, names), LevelNames.NotFound(levelName, names));
        }

        var scope = OpeningProbe.ScopeFail(doc, uidoc, mepIds, hostIds);
        if (scope != null) return NoWrite(scope, scope);

        if (!OpeningProbe.AnyMep(doc, uidoc, mepIds, hostIds))
            return NoWrite("Please select MEP elements first.", "Please select MEP elements first.");

        var hits = OpeningProbe.Find(doc, uidoc, mepIds, hostIds, Metric.MmToFt(clearanceMm), shape, limit);
        hits = FilterByLevel(hits, forced?.Id);
        if (hits.Count == 0)
            return NoWrite("No MEP-host clash found at the requested scope (bbox then solid intersect).");
        hits = OpeningCombine.Apply(doc, hits, out var combine);

        var placed = 0;
        var failed = 0;
        var skipped = 0;
        var punched = 0;
        var placedAt = new List<string>();
        var instanceIds = new List<string>();
        var points = new List<string>();
        var pending = new List<Pending>();
        var placedKeys = new HashSet<string>(StringComparer.Ordinal);
        var placedSrcHost = new HashSet<string>(StringComparer.Ordinal);
        IndexExistingOpenings(doc, placedKeys, placedSrcHost);
        var tx = new Transaction(doc, "LoadingAI for Revit: create openings from clash");
        tx.Start();
        try
        {
            var loadFail = EnsureFamilies(doc, hits, familyName);
            if (loadFail != null)
            {
                tx.RollBack();
                return NoWrite(loadFail, loadFail);
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
                    var symbol = OpeningFamilyContract.Find(doc, familyName, hit.Shape, host);
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
            return NoWrite($"Failed: {ex.Message}", ex.Message);
        }

        var hostList = string.Join(", ", hits.Select(h => h.HostId.ToString()).Distinct());
        var extra = skipped > 0 ? $" Skipped {skipped} already placed." : "";
        if (combine.Clusters > 0) extra += $" Combined {combine.Clusters} cluster(s).";
        if (combine.Large > 0) extra += " large (>700mm).";
        extra += " Joins lost: not enumerated (host API does not expose a join delta on void cut).";
        var where = placedAt.Count == 0 ? "" : " " + string.Join("; ", placedAt) + ".";
        var famNames = pending
            .Select(p => p.Inst.Symbol?.FamilyName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var famLabel = famNames.Count > 0
            ? string.Join(" / ", famNames)
            : "none this run";
        var placeType = pending.Count > 0
            ? pending[0].Inst.Symbol?.Family.FamilyPlacementType.ToString() ?? "OneLevelBased"
            : "OneLevelBased";
        return new OpeningPlaceWorkResult(
            $"Placed {placed} opening(s) in the active document ({famLabel}, {placeType}) on host(s) {hostList}.{where} Punched {punched}. Failed {failed}.{extra}",
            placed, punched, failed, skipped, 0, instanceIds, points);
    }

    static OpeningPlaceWorkResult NoWrite(string report, string? error = null)
        => new(report, 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), error);
    struct Pending
    {
        public FamilyInstance Inst;
        public Element Host;
        public ClashHit Hit;
    }

    static string? EnsureFamilies(Document doc, List<ClashHit> hits, string? familyName)
    {
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits)
        {
            var hostNeed = doc.GetElement(hit.HostId);
            if (hostNeed == null) continue;
            var want = OpeningFamilyContract.FamilyNameFor(hostNeed, familyName, hit.Shape);
            // Legacy LoadingAI_Opening_Horizontal / LoadingAI_Opening_Vertical still Find fallback.
            if (!loaded.Add(want)) continue;
            if (!OpeningFamilyContract.TryLoad(doc, want))
                return OpeningFamilyContract.FailReason(null, want);
        }
        return null;
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
        PinVerticalToHostTop(doc, inst, host, hit.Centroid, level);
        RotateToHost(doc, inst, hit);
        var xyz = (inst.Location as LocationPoint)?.Point ?? hit.Centroid;
        if (!OpeningProbe.InsideHost(host, xyz))
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
        if (now != null) OpeningGeo.TryMove(doc, inst.Id, want - now);
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
    /// Vertical family origin is the family bottom (extrudes +Z). Revit floors hang
    /// down from the Level, so Elevation=0 sat the void on the slab top. Pin XY to
    /// the clash, Z to host bbox Min (slab bottom, model coords; do not OfPoint).
    /// Depth = thickness+50mm then goes up through the host.
    /// </summary>
    static void PinVerticalToHostTop(Document doc, FamilyInstance inst, Element host, XYZ want, Level? level)
    {
        if (!OpeningFamilyContract.IsVerticalHost(host)) return;
        var bb = host.get_BoundingBox(null);
        if (bb == null) return;
        var now = (inst.Location as LocationPoint)?.Point;
        if (now == null) return;
        var dest = new XYZ(want.X, want.Y, bb.Min.Z);
        OpeningGeo.TryMove(doc, inst.Id, dest - now);
        var off = level != null ? dest.Z - level.Elevation : dest.Z - now.Z;
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

    static void IndexExistingOpenings(Document doc, HashSet<string> keys, HashSet<string> srcHost)
    {
        foreach (var fi in new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>())
        {
            try { if (!fi.IsValidObject) continue; }
            catch { continue; }
            var familyName = fi.Symbol?.FamilyName;
            var tagged = OpeningFamilyContract.Has(fi, "OP_SourceId")
                || OpeningFamilyContract.Has(fi, "OP_HostId");
            if (!OpeningFamilyContract.IsPluginFamily(familyName) && !tagged) continue;
            var key = OpeningFamilyContract.OpValue(fi, "OP_Key");
            if (!string.IsNullOrEmpty(key)) keys.Add(key);
            var src = OpeningFamilyContract.OpValue(fi, "OP_SourceId");
            var host = OpeningFamilyContract.OpValue(fi, "OP_HostId");
            if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(host))
            {
                srcHost.Add(src + "|" + host);
                foreach (var part in src.Split('+'))
                    if (part.Length > 0) srcHost.Add(part + "|" + host);
            }
        }
    }

    static bool AlreadyPlaced(ClashHit hit, HashSet<string> keys, HashSet<string> srcHost)
    {
        if (!string.IsNullOrEmpty(hit.Key) && keys.Contains(hit.Key)) return true;
        if (hit.Combined && !string.IsNullOrEmpty(hit.CombinedSources))
        {
            foreach (var p in hit.CombinedSources.Split('+'))
            {
                if (p.Length == 0) continue;
                if (srcHost.Contains(p + "|" + hit.HostId)) return true;
            }
        }
        return srcHost.Contains(hit.MepId + "|" + hit.HostId);
    }

    static void RememberPlaced(ClashHit hit, HashSet<string> keys, HashSet<string> srcHost)
    {
        if (!string.IsNullOrEmpty(hit.Key)) keys.Add(hit.Key);
        if (hit.Combined && !string.IsNullOrEmpty(hit.CombinedSources))
        {
            foreach (var src in hit.CombinedSources.Split('+'))
                if (src.Length > 0) srcHost.Add(src + "|" + hit.HostId);
            return;
        }
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
        try { ElementTransformUtils.RotateElement(doc, inst.Id, axis, angle); } // MoveElement for XYZ lives in OpeningGeo.TryMove
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
            if (OpeningFamilyContract.IsVerticalHost(host))
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
        if (OpeningFamilyContract.IsCircleShape(hit.Shape))
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
    /// Vertical family origin is the family bottom at slab bottom — do not drop Height/2.
    /// </summary>
    static void DropOriginToOpeningBottom(Document doc, FamilyInstance inst, ClashHit hit)
    {
        var host = doc.GetElement(hit.HostId);
        if (host is Floor || host is RoofBase) return; // family bottom already at slab bottom (bbox Min)
        // opening bottom is the family origin
        OpeningGeo.TryMove(doc, inst.Id, new XYZ(0, 0, -(hit.HeightFt / 2.0)));
    }

    static void WriteOp(FamilyInstance inst, ClashHit hit)
    {
        SetOrComment(inst, "OP_SourceId", string.IsNullOrEmpty(hit.CombinedSources) ? hit.MepId.ToString() : hit.CombinedSources);
        SetOrComment(inst, "OP_HostId", hit.HostId.ToString());
        if (!string.IsNullOrEmpty(hit.Key)) SetOrComment(inst, "OP_Key", hit.Key);
        SetOrComment(inst, "OP_HostLinked", hit.HostLinked ? "true" : "false");
        SetOrComment(inst, "OP_Shape", hit.Shape);
        if (hit.Combined)
        {
            SetOrComment(inst, "OP_Combined", "true");
            SetOrComment(inst, "OP_CombinedCount", hit.CombinedCount.ToString(CultureInfo.InvariantCulture));
        }
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


