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
                    try
                    {
                        InstanceVoidCutUtils.AddInstanceVoidCut(doc, host, opening);
                        cut++;
                    }
                    catch
                    {
                        failed++;
                    }
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
        if (fam.Equals(FamilyContract.DefaultFamilyName, StringComparison.OrdinalIgnoreCase)) return true;
        if (fam.IndexOf("Opening", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return FamilyContract.Has(fi, "OP_HostId") || FamilyContract.Has(fi, "OP_SourceId");
    }

    static List<Element> HostsForOpening(Document doc, FamilyInstance opening, List<Element> hinted)
    {
        if (hinted.Count == 0) return HostsFor(doc, opening);
        var matched = new List<Element>();
        var hostParam = opening.LookupParameter("OP_HostId")?.AsString();
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
        var hostParam = opening.LookupParameter("OP_HostId")?.AsString();
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
                    .WherePasses(new ElementMulticategoryFilter(new[]
                    {
                        BuiltInCategory.OST_Walls,
                        BuiltInCategory.OST_StructuralFraming,
                        BuiltInCategory.OST_Floors
                    }))
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
        var opt = new Options { ComputeReferences = false };
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
