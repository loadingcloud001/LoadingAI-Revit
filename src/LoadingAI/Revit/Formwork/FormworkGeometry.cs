using Autodesk.Revit.DB;

namespace LoadingAI.Revit.Formwork;

internal static class FormworkGeometry
{
    public static IEnumerable<FaceInfo> CollectCandidateFaces(Solid solid, FormworkHostInfo host, HashSet<string> allowedTypes)
    {
        FaceArray? faces;
        try { faces = solid.Faces; }
        catch { yield break; }
        if (faces == null) yield break;

        for (var i = 0; i < faces.Size; i++)
        {
            Face? face;
            try { face = faces.get_Item(i); }
            catch { continue; }
            if (face == null) continue;

            double area;
            try { area = face.Area; }
            catch { continue; }
            if (area < FormworkConfig.FaceAreaMinSqFt) continue;

            XYZ? normal = null;
            var curvedBottom = false;
            var curvedSide = false;
            if (face is PlanarFace pf)
                normal = pf.FaceNormal;
            else
            {
                try
                {
                    var bb = face.GetBoundingBox();
                    var uMid = (bb.Min.U + bb.Max.U) / 2.0;
                    var vMid = (bb.Min.V + bb.Max.V) / 2.0;
                    var samples = new[]
                    {
                        new UV(uMid, vMid),
                        new UV(bb.Min.U + 0.1 * (bb.Max.U - bb.Min.U), bb.Min.V + 0.1 * (bb.Max.V - bb.Min.V)),
                        new UV(bb.Max.U - 0.1 * (bb.Max.U - bb.Min.U), bb.Min.V + 0.1 * (bb.Max.V - bb.Min.V)),
                        new UV(bb.Min.U + 0.1 * (bb.Max.U - bb.Min.U), bb.Max.V - 0.1 * (bb.Max.V - bb.Min.V)),
                        new UV(bb.Max.U - 0.1 * (bb.Max.U - bb.Min.U), bb.Max.V - 0.1 * (bb.Max.V - bb.Min.V)),
                    };
                    foreach (var uv in samples)
                    {
                        try
                        {
                            var n = face.ComputeNormal(uv);
                            if (n == null) continue;
                            normal ??= n;
                            if (n.Z < -0.3) curvedBottom = true;
                            if (Math.Abs(n.Z) < 0.7) curvedSide = true;
                        }
                        catch { /* sample miss */ }
                    }
                    if (normal == null) curvedSide = true;
                }
                catch { curvedSide = true; }
            }

            var bic = host.Category;
            var catName = host.CategoryName;
            var isFloor = FormworkCore.IsCat(bic, BuiltInCategory.OST_Floors, catName, "floor", "slab");
            var topThreshold = isFloor ? 0.3 : 0.7;
            if (normal != null && normal.Z > topThreshold && !curvedBottom && !curvedSide)
                continue;

            XYZ centroid;
            try
            {
                var bb = face.GetBoundingBox();
                centroid = face.Evaluate(new UV((bb.Min.U + bb.Max.U) / 2.0, (bb.Min.V + bb.Max.V) / 2.0));
            }
            catch { centroid = XYZ.Zero; }

            var faceType = FormworkCore.ClassifyFaceType(normal, catName, bic, host.SourceElem);
            if (curvedBottom && faceType is FaceTypes.Unknown or FaceTypes.SlabSide)
            {
                if (FormworkCore.IsCat(bic, BuiltInCategory.OST_Floors, catName, "floor", "slab")) faceType = FaceTypes.SlabBottom;
                else if (FormworkCore.IsCat(bic, BuiltInCategory.OST_StructuralFraming, catName, "beam", "framing")) faceType = FaceTypes.BeamBottom;
                else if (FormworkCore.IsCat(bic, BuiltInCategory.OST_StructuralFoundation, catName, "foundation")) faceType = FaceTypes.FoundationBottom;
                else if (FormworkCore.IsCat(bic, BuiltInCategory.OST_Stairs, catName, "stair")) faceType = FaceTypes.StairsSoffit;
            }
            if (curvedSide && faceType == FaceTypes.Unknown)
            {
                if (FormworkCore.IsCat(bic, BuiltInCategory.OST_Floors, catName, "floor", "slab")) faceType = FaceTypes.SlabSide;
                else if (FormworkCore.IsCat(bic, BuiltInCategory.OST_StructuralFraming, catName, "beam", "framing")) faceType = FaceTypes.BeamSide;
                else if (FormworkCore.IsCat(bic, BuiltInCategory.OST_StructuralColumns, catName, "column") ||
                         FormworkCore.IsCat(bic, BuiltInCategory.OST_Columns, catName, "column")) faceType = FaceTypes.ColumnSide;
                else if (FormworkCore.IsCat(bic, BuiltInCategory.OST_Walls, catName, "wall")) faceType = FaceTypes.WallExterior;
                else if (FormworkCore.IsCat(bic, BuiltInCategory.OST_StructuralFoundation, catName, "foundation")) faceType = FaceTypes.FoundationSide;
                else if (FormworkCore.IsCat(bic, BuiltInCategory.OST_Stairs, catName, "stair")) faceType = FaceTypes.StairsSide;
            }
            if (faceType == FaceTypes.Unknown) continue;
            if (allowedTypes.Count > 0 && !allowedTypes.Contains(faceType)) continue;

            yield return new FaceInfo
            {
                Face = face,
                FaceIndex = i,
                Normal = normal,
                Area = area,
                Centroid = centroid,
                FaceType = faceType,
                ElementCategory = catName,
                ElementCategoryBic = bic,
                HostInfo = host
            };
        }
    }

    public static List<Solid> BuildSheetGeometry(Face face, XYZ? normal, double thicknessFt)
    {
        var solids = new List<Solid>();
        if (face is PlanarFace planar)
        {
            var n = planar.FaceNormal ?? normal ?? XYZ.BasisZ;
            var loops = planar.GetEdgesAsCurveLoops();
            var extruded = TryExtrude(loops, n, thicknessFt);
            if (extruded == null && loops != null && loops.Count > 1)
            {
                var outer = loops.OrderByDescending(LoopLength).First();
                extruded = TryExtrude(new List<CurveLoop> { outer }, n, thicknessFt);
            }
            if (extruded != null) solids.Add(extruded);
            else solids.AddRange(Triangulate(face, n, thicknessFt));
            return solids;
        }

        var n2 = normal ?? SampleNormal(face) ?? XYZ.BasisZ;
        solids.AddRange(Triangulate(face, n2, thicknessFt));
        return solids;
    }

    public static bool ValidateSolid(Solid? s)
        => s != null && s.Volume > 1e-8 && s.SurfaceArea > 1e-6 && s.Faces != null && s.Faces.Size > 0;

    public static double NetFaceArea(IEnumerable<Solid> solids, XYZ? expectedNormal)
    {
        var best = 0.0;
        foreach (var s in solids)
        {
            FaceArray? faces;
            try { faces = s.Faces; }
            catch { continue; }
            if (faces == null) continue;
            var groups = new Dictionary<string, double>();
            for (var i = 0; i < faces.Size; i++)
            {
                Face? f;
                try { f = faces.get_Item(i); }
                catch { continue; }
                if (f is not PlanarFace pf) continue;
                var n = pf.FaceNormal;
                if (expectedNormal != null && n.DotProduct(expectedNormal) < 0.7) continue;
                var key = $"{n.X:0.00}|{n.Y:0.00}|{n.Z:0.00}";
                groups.TryGetValue(key, out var a);
                groups[key] = a + pf.Area;
            }
            if (groups.Count == 0) continue;
            var max = groups.Values.Max();
            if (max > best) best = max;
        }
        return best;
    }

    public static bool BboxOverlap(Solid a, Solid b)
    {
        try
        {
            var ba = a.GetBoundingBox();
            var bb = b.GetBoundingBox();
            if (ba == null || bb == null) return true;
            var t = FormworkConfig.BboxOverlapTolFt;
            if (ba.Max.X + t < bb.Min.X || bb.Max.X + t < ba.Min.X) return false;
            if (ba.Max.Y + t < bb.Min.Y || bb.Max.Y + t < ba.Min.Y) return false;
            if (ba.Max.Z + t < bb.Min.Z || bb.Max.Z + t < ba.Min.Z) return false;
            return true;
        }
        catch { return true; }
    }

    public static double CoverRatio(Solid panel, IEnumerable<(string Owner, Solid Solid)> obstacles, string ownerKey, int maxChecks)
    {
        var vol = panel.Volume;
        if (vol <= FormworkConfig.EpsVol) return 0;
        var covered = 0.0;
        var checks = 0;
        foreach (var (owner, other) in obstacles)
        {
            if (owner == ownerKey) continue;
            if (checks++ >= maxChecks) break;
            if (!BboxOverlap(panel, other)) continue;
            try
            {
                var inter = BooleanOperationsUtils.ExecuteBooleanOperation(panel, other, BooleanOperationsType.Intersect);
                if (inter != null && inter.Volume > FormworkConfig.MinIntersectionVolCuFt)
                    covered += inter.Volume;
            }
            catch { /* boolean fail: skip, do not estimate */ }
        }
        return Math.Min(1.0, covered / vol);
    }

    public static Solid? TryDifference(Solid panel, Solid other)
    {
        try
        {
            var diff = BooleanOperationsUtils.ExecuteBooleanOperation(panel, other, BooleanOperationsType.Difference);
            if (!ValidateSolid(diff)) return null;
            if (diff.Volume < panel.Volume * FormworkConfig.DiffVanishKeepRatio &&
                diff.Volume < panel.Volume)
            {
                // keep if it did not vanish; already checked ratio
            }
            return diff;
        }
        catch { return null; }
    }

    static Solid? TryExtrude(IList<CurveLoop>? loops, XYZ normal, double thicknessFt)
    {
        if (loops == null || loops.Count == 0) return null;
        try
        {
            var s = GeometryCreationUtilities.CreateExtrusionGeometry(loops, normal, thicknessFt);
            return ValidateSolid(s) ? s : null;
        }
        catch { return null; }
    }

    static IEnumerable<Solid> Triangulate(Face face, XYZ normal, double thicknessFt)
    {
        Mesh? mesh = null;
        try { mesh = face.Triangulate(); }
        catch
        {
            try { mesh = face.Triangulate(0.5); }
            catch { yield break; }
        }
        if (mesh == null) yield break;

        var count = Math.Min(mesh.NumTriangles, 10000);
        for (var i = 0; i < count; i++)
        {
            MeshTriangle tri;
            try { tri = mesh.get_Triangle(i); }
            catch { continue; }
            XYZ a, b, c;
            try { a = tri.get_Vertex(0); b = tri.get_Vertex(1); c = tri.get_Vertex(2); }
            catch { continue; }

            var e1 = b - a;
            var e2 = c - a;
            var cross = e1.CrossProduct(e2);
            var clen = cross.GetLength();
            if (clen < 0.0005) continue;
            var n = cross.Normalize();
            var ea = a.DistanceTo(b);
            var eb = b.DistanceTo(c);
            var ec = c.DistanceTo(a);
            var minE = Math.Min(ea, Math.Min(eb, ec));
            var maxE = Math.Max(ea, Math.Max(eb, ec));
            if (minE < 0.003) continue;
            if (maxE / Math.Max(minE, 1e-9) > FormworkConfig.MaxAspectRatio) continue;

            CurveLoop? loop;
            try
            {
                loop = new CurveLoop();
                loop.Append(Line.CreateBound(a, b));
                loop.Append(Line.CreateBound(b, c));
                loop.Append(Line.CreateBound(c, a));
            }
            catch { continue; }
            var s = TryExtrude(new List<CurveLoop> { loop }, n, thicknessFt);
            if (s != null) yield return s;
        }
    }

    static XYZ? SampleNormal(Face face)
    {
        try
        {
            var bb = face.GetBoundingBox();
            return face.ComputeNormal(new UV((bb.Min.U + bb.Max.U) / 2.0, (bb.Min.V + bb.Max.V) / 2.0));
        }
        catch { return null; }
    }

    static double LoopLength(CurveLoop loop)
    {
        try { return loop.GetExactLength(); }
        catch
        {
            var len = 0.0;
            foreach (Curve c in loop) len += c.Length;
            return len;
        }
    }
}
