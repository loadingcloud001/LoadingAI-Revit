using Autodesk.Revit.DB;
using LoadingAI.Revit;

namespace LoadingAI.Plugins.OpeningTools;

internal sealed class CombineReport
{
    public int Clusters;
    public int Large;
}

/// <summary>
/// Cluster same-host clash hits into one void when leftover web cannot act as a pier.
/// Keep members separate when remaining edge is too thin or envelope exceeds 2/3 host length.
/// </summary>
internal static class OpeningCombine
{
    public static List<ClashHit> Apply(Document doc, List<ClashHit> hits, out CombineReport report)
    {
        report = new CombineReport();
        if (hits == null || hits.Count < 2) return hits ?? new List<ClashHit>();

        var minClear = Metric.MmToFt(150);
        var largeLimit = Metric.MmToFt(700);
        var byHost = new Dictionary<ElementId, List<int>>();
        for (var i = 0; i < hits.Count; i++)
        {
            if (hits[i].HostLinked) continue;
            if (!byHost.TryGetValue(hits[i].HostId, out var list))
                byHost[hits[i].HostId] = list = new List<int>();
            list.Add(i);
        }

        var used = new bool[hits.Count];
        var result = new List<ClashHit>(hits.Count);
        foreach (var pair in byHost)
        {
            var idxs = pair.Value;
            if (idxs.Count == 1)
            {
                result.Add(hits[idxs[0]]);
                used[idxs[0]] = true;
                continue;
            }
            var host = doc.GetElement(pair.Key);
            if (host == null || !TryHostFace(host, out var origin, out var dir, out var up, out var norm, out var face))
            {
                foreach (var i in idxs)
                {
                    result.Add(hits[i]);
                    used[i] = true;
                }
                continue;
            }

            var rects = new List<OpeningCombineMath.FaceRect>(idxs.Count);
            foreach (var i in idxs)
            {
                var h = hits[i];
                var rel = h.Centroid - origin;
                rects.Add(new OpeningCombineMath.FaceRect(
                    rel.DotProduct(dir), rel.DotProduct(up), h.WidthFt, h.HeightFt));
            }

            foreach (var g in OpeningCombineMath.Group(rects, face, minClear, largeLimit))
            {
                if (!g.Merged)
                {
                    foreach (var li in g.Indices)
                    {
                        result.Add(hits[idxs[li]]);
                        used[idxs[li]] = true;
                    }
                    continue;
                }
                report.Clusters++;
                if (g.Large) report.Large++;
                result.Add(MergeHits(idxs, g, hits, origin, dir, up, norm));
                foreach (var li in g.Indices) used[idxs[li]] = true;
            }
        }

        for (var i = 0; i < hits.Count; i++)
            if (!used[i]) result.Add(hits[i]);
        return result;
    }

    static ClashHit MergeHits(
        List<int> idxs, OpeningCombineMath.Cluster g, List<ClashHit> hits,
        XYZ origin, XYZ dir, XYZ up, XYZ norm)
    {
        var members = new List<ClashHit>(g.Indices.Length);
        foreach (var li in g.Indices) members.Add(hits[idxs[li]]);
        members.Sort((a, b) => string.CompareOrdinal(a.MepId.ToString(), b.MepId.ToString()));
        var first = members[0];
        var nAcc = 0.0;
        foreach (var m in members) nAcc += (m.Centroid - origin).DotProduct(norm);
        var c = origin
            + dir.Multiply(g.Envelope.U)
            + up.Multiply(g.Envelope.V)
            + norm.Multiply(nAcc / members.Count);
        var hostUid = KeyHost(first.Key);
        var mepParts = members
            .Select(m => KeyMep(m.Key))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal);
        var key = hostUid + "|" + string.Join("+", mepParts);
        var sources = string.Join("+", members.Select(m => m.MepId.ToString()).Distinct());
        var allCircle = true;
        var depth = 0.0;
        var linked = false;
        foreach (var m in members)
        {
            if (!m.Shape.Equals("circle", StringComparison.OrdinalIgnoreCase)) allCircle = false;
            if (m.DepthFt > depth) depth = m.DepthFt;
            if (m.SourceLinked) linked = true;
        }
        var shape = allCircle && OpeningCombineMath.IsNearlySquare(g.Envelope) ? "circle" : "rectangle";
        return new ClashHit(
            first.MepId, first.HostId, first.HostLinked, linked,
            c, first.HostDir, g.Envelope.W, g.Envelope.H, depth,
            shape, first.LevelId, key, true, members.Count, sources);
    }

    static string KeyHost(string key)
    {
        var i = key.IndexOf('|');
        return i < 0 ? key : key.Substring(0, i);
    }

    static string KeyMep(string key)
    {
        var i = key.IndexOf('|');
        return i < 0 ? "" : key.Substring(i + 1);
    }

    /// <summary>
    /// Face frame matches ClashProbe.HostFrame. Element.BoundingBox is model coords —
    /// Do not OfPoint Min through Transform.
    /// </summary>
    static bool TryHostFace(
        Element host, out XYZ origin, out XYZ dir, out XYZ up, out XYZ norm, out OpeningCombineMath.HostFace face)
    {
        origin = dir = up = norm = XYZ.Zero;
        face = default;
        BoundingBoxXYZ? bb;
        try { bb = host.get_BoundingBox(null); }
        catch { return false; }
        if (bb == null) return false;
        var min = bb.Min;
        var max = bb.Max;
        origin = new XYZ((min.X + max.X) * 0.5, (min.Y + max.Y) * 0.5, (min.Z + max.Z) * 0.5);
        ClashProbe.HostFrame(host, out dir, out up, out norm);
        if (dir.GetLength() < 1e-9) dir = XYZ.BasisX;
        else dir = dir.Normalize();
        if (up.GetLength() < 1e-9) up = XYZ.BasisZ;
        else up = up.Normalize();
        if (norm.GetLength() < 1e-9) norm = dir.CrossProduct(up);
        else norm = norm.Normalize();

        var uMin = double.PositiveInfinity;
        var uMax = double.NegativeInfinity;
        var vMin = double.PositiveInfinity;
        var vMax = double.NegativeInfinity;
        foreach (var x in new[] { min.X, max.X })
        foreach (var y in new[] { min.Y, max.Y })
        foreach (var z in new[] { min.Z, max.Z })
        {
            var p = new XYZ(x, y, z) - origin;
            var u = p.DotProduct(dir);
            var v = p.DotProduct(up);
            if (u < uMin) uMin = u;
            if (u > uMax) uMax = u;
            if (v < vMin) vMin = v;
            if (v > vMax) vMax = v;
        }
        if (uMax <= uMin || vMax <= vMin) return false;
        face = new OpeningCombineMath.HostFace(uMin, uMax, vMin, vMax, ClashProbe.HostThickness(host));
        return true;
    }
}
