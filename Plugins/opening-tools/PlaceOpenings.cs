using System.Globalization;
using Autodesk.Revit.DB;
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
        var symbol = FamilyContract.Find(doc, familyName, shape);
        var fail = FamilyContract.FailReason(symbol);
        if (fail != null) return fail;

        Level? forced = null;
        if (!string.IsNullOrWhiteSpace(levelName))
        {
            var names = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Name).ToList();
            var resolved = LevelNames.Resolve(levelName, names);
            if (resolved == null) return LevelNames.NotFound(levelName, names);
            forced = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => l.Name.Equals(resolved, StringComparison.OrdinalIgnoreCase));
            if (forced == null) return LevelNames.NotFound(levelName, names);
        }

        if (!ClashProbe.AnyMep(doc, uidoc, mepIds))
            return "Please select MEP elements first.";

        var hits = ClashProbe.Find(doc, uidoc, mepIds, hostIds, Metric.MmToFt(clearanceMm), shape, limit);
        if (hits.Count == 0)
            return "No MEP-host clash found (bbox then solid intersect).";

        var placed = 0;
        var failed = 0;
        var tx = new Transaction(doc, "LoadingAI for Revit: create openings from clash");
        tx.Start();
        try
        {
            if (!symbol!.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }
            foreach (var hit in hits)
            {
                try
                {
                    if (forced != null && hit.LevelId != ElementId.InvalidElementId && hit.LevelId != forced.Id)
                    {
                        failed++;
                        continue;
                    }
                    var host = doc.GetElement(hit.HostId);
                    if (host == null || !TryHostFace(host, hit.Centroid, out var face, out var loc, out var normal))
                    {
                        failed++;
                        continue;
                    }
                    var dir = ReferenceDir(hit.HostDir, normal);
                    var inst = doc.Create.NewFamilyInstance(face, loc, dir, symbol);
                    ApplySize(inst, hit);
                    WriteOp(inst, hit);
                    placed++;
                }
                catch
                {
                    failed++;
                }
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            return $"Failed: {ex.Message}";
        }

        return $"Placed {placed} opening(s) in the active document ({symbol.FamilyName}). Failed {failed}. Linked MEP uses GetTotalTransform + CreateTransformed.";
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
            if (host is Floor)
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
        if (hit.Shape == "circle")
        {
            var r = Math.Max(widthMm, heightMm) / 2;
            SetLengthFt(inst, "Radius", Metric.MmToFt(r));
        }
        SetLengthFt(inst, "Width", hit.WidthFt);
        SetLengthFt(inst, "Height", hit.HeightFt);
        SetLengthFt(inst, "Depth", hit.DepthFt);
    }

    static void WriteOp(FamilyInstance inst, ClashHit hit)
    {
        SetOrComment(inst, "OP_SourceId", hit.MepId.ToString());
        SetOrComment(inst, "OP_HostId", hit.HostId.ToString());
        SetOrComment(inst, "OP_HostLinked", hit.HostLinked ? "true" : "false");
        SetOrComment(inst, "OP_Shape", hit.Shape);
        SetOrComment(inst, "OP_WidthMm", Metric.FtToMm(hit.WidthFt).ToString("F0", CultureInfo.InvariantCulture));
        SetOrComment(inst, "OP_HeightMm", Metric.FtToMm(hit.HeightFt).ToString("F0", CultureInfo.InvariantCulture));
        SetOrComment(inst, "OP_DepthMm", Metric.FtToMm(hit.DepthFt).ToString("F0", CultureInfo.InvariantCulture));
    }

    static void SetLengthFt(Element el, string name, double ft)
    {
        var p = el.LookupParameter(name);
        if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) return;
        try { p.Set(ft); } catch { }
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
