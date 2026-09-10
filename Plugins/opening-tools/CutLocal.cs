using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.OpeningTools;

internal static class CutLocal
{
    const string LinkedSkip = "host is linked — not cut";

    public static string Run(
        Document doc,
        UIDocument uidoc,
        IReadOnlyList<string> openingIds,
        IReadOnlyList<string> hostIds)
    {
        var openings = ResolveOpenings(doc, uidoc, openingIds);
        if (openings.Count == 0)
            openings = OpeningsForHosts(doc, Seeds(doc, uidoc, openingIds, hostIds));
        if (openings.Count == 0)
            openings = AllPluginOpenings(doc);
        if (openings.Count == 0) return "Please select opening instances first.";
        var hostsHint = ResolveHosts(doc, hostIds);

        var cut = 0;
        var linked = 0;
        var cannot = 0;
        var failed = 0;
        var tx = new Transaction(doc, "LoadingAI for Revit: cut hosts by openings");
        tx.Start();
        try
        {
            foreach (var opening in openings)
            {
                if (!Equals(opening.Document, doc))
                {
                    linked++;
                    continue;
                }

                var hosts = HostsForOpening(doc, opening, hostsHint);
                if (hosts.Count == 0)
                {
                    cannot++;
                    continue;
                }

                foreach (var host in hosts)
                {
                    if (!Equals(host.Document, doc) || host is RevitLinkInstance)
                    {
                        linked++;
                        continue;
                    }
                    if (!InstanceVoidCutUtils.CanBeCutWithVoid(host))
                    {
                        cannot++;
                        continue;
                    }
                    if (!CutLocal.TryPunch(doc, host, opening))
                    {
                        failed++; // no-punch
                        continue;
                    }
                    cut++;
                }
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            return "Failed: " + ex.Message;
        }

        var msg = $"Cut {cut}. Skipped linked: {linked}. Skipped cannot-cut: {cannot}. Failed {failed}.";
        if (linked > 0) msg += " " + LinkedSkip;
        return msg;
    }

    /// <summary>
    /// Tammik: CanBeCutWithVoid is category-only. Require the opening on the host (InsideHost)
    /// then AddInstanceVoidCut. GetCuttingVoidInstances confirms the relationship. no-punch otherwise.
    /// </summary>
    internal static bool TryPunch(Document doc, Element host, FamilyInstance opening)
    {
        if (!Equals(host.Document, doc) || host is RevitLinkInstance) return false;
        var xyz = (opening.Location as LocationPoint)?.Point;
        if (xyz != null && !ClashProbe.InsideHost(host, xyz)) return false;
        if (!InstanceVoidCutUtils.CanBeCutWithVoid(host)) return false;
        try
        {
            if (IsCutting(host, opening)) return true;
        }
        catch { /* not yet cutting */ }
        try { InstanceVoidCutUtils.AddInstanceVoidCut(doc, host, opening); }
        catch { return false; }
        try { doc.Regenerate(); }
        catch { }
        try
        {
            if (IsCutting(host, opening)) return true;
        }
        catch { }
        return false; // no-punch
    }

    static bool IsCutting(Element host, FamilyInstance opening)
    {
        var existing = InstanceVoidCutUtils.GetCuttingVoidInstances(host);
        if (existing == null) return false;
        foreach (ElementId id in existing)
        {
            if (id == opening.Id) return true;
        }
        return false;
    }

    static List<FamilyInstance> ResolveOpenings(Document doc, UIDocument uidoc, IReadOnlyList<string> ids)
    {
        var seeds = new List<Element>();
        if (ids.Count > 0)
        {
            foreach (var s in ids)
            {
                try { var el = doc.GetElement(RevitParse.ParseEid(s)); if (el != null) seeds.Add(el); }
                catch { }
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

        return seeds.OfType<FamilyInstance>().Where(IsOpening).ToList();
    }

    static List<Element> Seeds(Document doc, UIDocument uidoc, IReadOnlyList<string> openingIds, IReadOnlyList<string> hostIds)
    {
        var seeds = new List<Element>();
        foreach (var s in hostIds.Concat(openingIds))
        {
            try { var el = doc.GetElement(RevitParse.ParseEid(s)); if (el != null) seeds.Add(el); }
            catch { }
        }
        if (seeds.Count == 0)
        {
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                var el = doc.GetElement(id);
                if (el != null) seeds.Add(el);
            }
        }
        return seeds.Where(ClashProbe.IsHost).ToList();
    }

    static List<FamilyInstance> OpeningsForHosts(Document doc, List<Element> hosts)
    {
        if (hosts.Count == 0) return new List<FamilyInstance>();
        var want = new HashSet<string>(hosts.Select(h => h.Id.ToString()), StringComparer.Ordinal);
        var list = new List<FamilyInstance>();
        foreach (var fi in AllPluginOpenings(doc))
        {
            var hp = FamilyContract.OpValue(fi, "OP_HostId");
            if (hp != null && want.Contains(hp)) { list.Add(fi); continue; }
            foreach (var host in hosts)
            {
                if (SolidIntersects(fi, host)) { list.Add(fi); break; }
            }
        }
        return list;
    }

    static List<FamilyInstance> AllPluginOpenings(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(IsOpening)
            .Take(200)
            .ToList();
    }

    static List<Element> ResolveHosts(Document doc, IReadOnlyList<string> ids)
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

    static bool IsOpening(FamilyInstance fi)
    {
        var fam = fi.Symbol?.FamilyName ?? "";
        if (FamilyContract.IsPluginFamily(fam)) return true;
        return FamilyContract.Has(fi, "OP_HostId") || FamilyContract.Has(fi, "OP_SourceId");
    }

    static List<Element> HostsForOpening(Document doc, FamilyInstance opening, List<Element> hinted)
    {
        if (hinted.Count == 0) return HostsFor(doc, opening);
        var matched = new List<Element>();
        var hostParam = FamilyContract.OpValue(opening, "OP_HostId");
        ElementId? want = null;
        if (!string.IsNullOrWhiteSpace(hostParam))
        {
            try { want = RevitParse.ParseEid(hostParam); }
            catch { }
        }
        foreach (var host in hinted)
        {
            if (!Equals(host.Document, doc) || host is RevitLinkInstance) continue;
            if (want != null)
            {
                if (host.Id == want) matched.Add(host);
                continue;
            }
            if (SolidIntersects(opening, host)) matched.Add(host);
        }
        return matched;
    }

    static bool SolidIntersects(Element a, Element b)
    {
        foreach (var solid in Solids(a))
        {
            try
            {
                foreach (var _ in new FilteredElementCollector(a.Document)
                    .WherePasses(new ElementIdSetFilter(new[] { b.Id }))
                    .WherePasses(new ElementIntersectsSolidFilter(solid)))
                    return true;
            }
            catch { }
        }
        return false;
    }

    static List<Element> HostsFor(Document doc, FamilyInstance opening)
    {
        var list = new List<Element>();
        var hostParam = FamilyContract.OpValue(opening, "OP_HostId");
        if (!string.IsNullOrWhiteSpace(hostParam))
        {
            try
            {
                var el = doc.GetElement(RevitParse.ParseEid(hostParam));
                if (el != null) list.Add(el);
            }
            catch { }
        }
        if (list.Count > 0) return list;

        foreach (var solid in Solids(opening))
        {
            try
            {
                foreach (var el in new FilteredElementCollector(doc)
                    .WherePasses(new ElementMulticategoryFilter(ClashProbe.HostCategories)) // OST_StructuralColumns + roofs
                    .WherePasses(new ElementIntersectsSolidFilter(solid))
                    .WhereElementIsNotElementType())
                {
                    if (el.Id == opening.Id) continue;
                    list.Add(el);
                }
            }
            catch { }
        }
        return list;
    }

    static IEnumerable<Solid> Solids(Element el)
    {
        var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = true };
        GeometryElement? geo;
        try { geo = el.get_Geometry(opt); }
        catch { yield break; }
        if (geo == null) yield break;
        foreach (var obj in geo)
        {
            if (obj is Solid s && s.Volume > 1e-9) yield return s;
            else if (obj is GeometryInstance gi)
            {
                GeometryElement? inst = null;
                try { inst = gi.GetInstanceGeometry(); }
                catch { }
                if (inst == null) continue;
                foreach (var inner in inst)
                {
                    if (inner is Solid s2 && s2.Volume > 1e-9) yield return s2;
                }
            }
        }
    }
}
