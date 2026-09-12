using Autodesk.Revit.DB;

namespace LoadingAI.Plugins.QsFormwork;

internal static class FormworkGenerator
{
    public static GenerateResult Generate(Document doc, IReadOnlyList<string> elementIds, GenerateOptions options)
    {
        var result = new GenerateResult();
        var sources = FormworkCore.ResolveSources(doc, elementIds);
        result.SelectedElements = sources.Count;
        if (sources.Count == 0) return result;

        var schema = FormworkCore.GetOrCreateSchema();
        var existing = FormworkCore.CollectPanels(doc);
        if (options.DeleteExisting && existing.Count > 0)
            FormworkCore.DeletePanels(doc, existing);

        existing = FormworkCore.CollectPanels(doc);

        var obstacles = new List<(string Owner, Solid Solid)>();
        if (options.Collision)
        {
            foreach (var src in sources)
            {
                var owner = FormworkCore.OwnerKey(src);
                foreach (var s in FormworkCore.GetHostSpaceSolids(src))
                    obstacles.Add((owner, s));
            }

            if (options.IncludeUnselectedLinks)
            {
                var selectedUids = sources.Select(s => s.LinkUid).Where(u => !string.IsNullOrEmpty(u)).ToHashSet(StringComparer.Ordinal);
                foreach (var extra in FormworkCore.ExtraLinkSources(doc, selectedUids))
                {
                    var owner = FormworkCore.OwnerKey(extra);
                    foreach (var s in FormworkCore.GetHostSpaceSolids(extra))
                        obstacles.Add((owner, s));
                }
            }

            foreach (var panel in existing)
            {
                foreach (var s in DirectShapeSolids(panel))
                    obstacles.Add(("panel|" + panel.Id, s));
            }
        }

        var thicknessFt = FormworkConfig.SheetThicknessFt(options.ThicknessMm);
        var allowed = options.FaceTypes;
        var candidates = new List<PanelCandidate>();

        foreach (var src in sources)
        {
            var host = FormworkCore.ResolveHost(src.Element, src.SourceDoc);
            if (host.SkipReason != null)
            {
                Fail(result, host.SkipReason);
                continue;
            }
            if (FormworkCore.IsCurtainWall(host.SourceElem))
            {
                Fail(result, "curtain_wall");
                continue;
            }

            var solids = FormworkCore.GetHostSpaceSolids(src);
            if (solids.Count == 0)
            {
                Fail(result, "no_solids");
                continue;
            }

            foreach (var solid in solids)
            {
                foreach (var face in FormworkGeometry.CollectCandidateFaces(solid, host, allowed))
                {
                    var geoms = FormworkGeometry.BuildSheetGeometry(face.Face, face.Normal, thicknessFt)
                        .Where(FormworkGeometry.ValidateSolid)
                        .ToList();
                    if (geoms.Count == 0)
                    {
                        Fail(result, "build_geom_failed");
                        continue;
                    }

                    candidates.Add(new PanelCandidate
                    {
                        FaceInfo = face,
                        Source = src,
                        Key = FormworkCore.ComputeFwkKey(src, face),
                        OwnerSolid = solid,
                        Solids = geoms
                    });
                }
            }
        }

        result.Candidates = candidates.Count;
        candidates = Dedup(candidates, result);

        if (options.Collision && obstacles.Count > 0)
            ApplyCollision(candidates, obstacles, result);

        DedupPanelCoverage(candidates, result);

        var kept = candidates.Where(c => !c.Skip && c.Solids.Count > 0).ToList();
        var catId = new ElementId(BuiltInCategory.OST_GenericModel);
        var byKey = existing
            .GroupBy(p => p.ApplicationDataId ?? "")
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var createdKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cand in kept)
        {
            try
            {
                var areaNet = FormworkGeometry.NetFaceArea(cand.Solids, cand.FaceInfo.Normal);
                if (areaNet <= 0) areaNet = cand.FaceInfo.Area;
                var host = cand.FaceInfo.HostInfo ?? FormworkCore.ResolveHost(cand.Source.Element, cand.Source.SourceDoc);
                DirectShape ds;
                var isUpdate = byKey.TryGetValue(cand.Key, out var existingDs);
                if (isUpdate && existingDs != null)
                {
                    ds = existingDs;
                    try { ds.SetShape(cand.Solids.Cast<GeometryObject>().ToList()); }
                    catch
                    {
                        doc.Delete(ds.Id);
                        ds = DirectShape.CreateElement(doc, catId);
                        isUpdate = false;
                        AssignShape(ds, cand);
                    }
                }
                else
                {
                    ds = DirectShape.CreateElement(doc, catId);
                    AssignShape(ds, cand);
                }

                ds.ApplicationId = FormworkConfig.AppId;
                ds.ApplicationDataId = cand.Key;
                var shortName = FormworkCore.ElemName(cand.Source.Element);
                if (shortName.Length > 15) shortName = shortName.Substring(0, 15);
                try { ds.Name = $"FWK_{cand.FaceInfo.FaceType}_{shortName}"; }
                catch { /* name optional */ }

                FormworkParameters.WritePanelParameters(
                    ds, cand.FaceInfo, cand.Source, host,
                    cand.FaceInfo.Area, areaNet, thicknessFt, cand.ContactRatio);
                FormworkCore.StoreSchema(ds, schema, cand.Key);
                createdKeys.Add(cand.Key);
                if (isUpdate) result.Updated++;
                else result.Created++;
            }
            catch
            {
                Fail(result, "upsert_failed");
            }
        }

        if (options.DeleteExisting)
        {
            var leftover = FormworkCore.CollectPanels(doc).Where(p => !createdKeys.Contains(p.ApplicationDataId ?? "")).ToList();
            result.DeletedObsolete = FormworkCore.DeletePanels(doc, leftover);
        }

        return result;
    }

    static void AssignShape(DirectShape ds, PanelCandidate cand)
    {
        try { ds.SetShape(cand.Solids.Cast<GeometryObject>().ToList()); }
        catch
        {
            var tris = new List<GeometryObject>();
            foreach (var s in cand.Solids)
            {
                try
                {
                    foreach (Face f in s.Faces)
                    {
                        var mesh = f.Triangulate();
                        if (mesh != null) tris.Add(mesh);
                    }
                }
                catch { /* */ }
            }
            if (tris.Count == 0) throw;
            ds.SetShape(tris);
        }
    }

    static List<PanelCandidate> Dedup(List<PanelCandidate> candidates, GenerateResult result)
    {
        var kept = new List<PanelCandidate>();
        var tol = FormworkConfig.DedupCentroidTolFt;
        foreach (var c in candidates.OrderByDescending(x => x.FaceInfo.Area))
        {
            var dup = false;
            foreach (var k in kept)
            {
                if (!string.Equals(k.FaceInfo.FaceType, c.FaceInfo.FaceType, StringComparison.Ordinal)) continue;
                if (k.FaceInfo.Centroid.DistanceTo(c.FaceInfo.Centroid) > tol) continue;
                var kn = k.FaceInfo.Normal;
                var cn = c.FaceInfo.Normal;
                if (kn != null && cn != null && Math.Abs(kn.DotProduct(cn)) < 1.0 - FormworkConfig.DedupNormalTol)
                    continue;
                var ratio = Math.Abs(k.FaceInfo.Area - c.FaceInfo.Area) / Math.Max(k.FaceInfo.Area, 1e-9);
                if (ratio > FormworkConfig.DedupAreaRatioTol) continue;
                dup = true;
                break;
            }
            if (dup) result.Duplicates++;
            else kept.Add(c);
        }
        return kept;
    }

    static void ApplyCollision(List<PanelCandidate> candidates, List<(string Owner, Solid Solid)> obstacles, GenerateResult result)
    {
        var trims = 0;
        foreach (var cand in candidates)
        {
            if (cand.Skip) continue;
            var owner = FormworkCore.OwnerKey(cand.Source);
            var next = new List<Solid>();
            foreach (var s in cand.Solids)
            {
                var cover = FormworkGeometry.CoverRatio(s, obstacles, owner, FormworkConfig.MaxCollisionChecks);
                if (cover >= FormworkConfig.CoverRatioDelete)
                {
                    result.CoverageSkipped++;
                    continue;
                }

                var current = s;
                foreach (var (oKey, other) in obstacles)
                {
                    if (oKey == owner) continue;
                    if (trims >= FormworkConfig.MaxTrimOperations) break;
                    if (!FormworkGeometry.BboxOverlap(current, other)) continue;
                    var diff = FormworkGeometry.TryDifference(current, other);
                    if (diff == null) continue;
                    if (diff.Volume >= current.Volume * 0.999) continue;
                    current = diff;
                    trims++;
                    result.Trimmed++;
                }
                if (FormworkGeometry.ValidateSolid(current))
                {
                    next.Add(current);
                    if (cover > 0) cand.ContactRatio = Math.Max(cand.ContactRatio, cover);
                }
            }
            if (next.Count == 0) cand.Skip = true;
            else cand.Solids = next;
        }
    }

    static void DedupPanelCoverage(List<PanelCandidate> candidates, GenerateResult result)
    {
        var live = candidates.Where(c => !c.Skip).OrderByDescending(c => Priority(c)).ToList();
        for (var i = 0; i < live.Count; i++)
        {
            var a = live[i];
            if (a.Skip) continue;
            for (var j = i + 1; j < live.Count; j++)
            {
                var b = live[j];
                if (b.Skip) continue;
                var obstacles = a.Solids.Select(s => ("p", s)).ToList();
                var skipB = false;
                foreach (var s in b.Solids)
                {
                    if (FormworkGeometry.CoverRatio(s, obstacles, "", FormworkConfig.MaxCollisionChecks) >= FormworkConfig.CoverRatioDelete)
                    {
                        skipB = true;
                        break;
                    }
                }
                if (skipB)
                {
                    b.Skip = true;
                    result.PanelOverlapSkipped++;
                }
            }
        }
    }

    static int Priority(PanelCandidate c)
        => FormworkConfig.FacePriority.TryGetValue(c.FaceInfo.FaceType, out var p) ? p : 0;

    static IEnumerable<Solid> DirectShapeSolids(DirectShape ds)
    {
        GeometryElement? geo;
        try { geo = ds.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }); }
        catch { yield break; }
        if (geo == null) yield break;
        foreach (GeometryObject obj in geo)
        {
            if (obj is Solid s && FormworkGeometry.ValidateSolid(s))
                yield return s;
        }
    }

    static void Fail(GenerateResult result, string reason)
    {
        result.Failed++;
        result.FailReasons.TryGetValue(reason, out var n);
        result.FailReasons[reason] = n + 1;
    }
}
