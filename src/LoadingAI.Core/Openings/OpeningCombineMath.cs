namespace LoadingAI.Core.Openings;

/// <summary>
/// Host-face clustering in caller units (host: feet; tests: mm). No Revit types.
/// Link when overlap or gap on both axes is less than max(smaller dim, thickness, minClear).
/// Abort merge when remaining edge is less than max(minClear, thickness) or envelope width exceeds 2/3 host length.
/// </summary>
public static class OpeningCombineMath
{
    public const double MaxHostFraction = 2.0 / 3.0;

    public readonly struct FaceRect
    {
        public readonly double U, V, W, H;
        public FaceRect(double u, double v, double w, double h)
        {
            U = u;
            V = v;
            W = w;
            H = h;
        }
        public double UMin => U - W * 0.5;
        public double UMax => U + W * 0.5;
        public double VMin => V - H * 0.5;
        public double VMax => V + H * 0.5;
        public double MinDim => Math.Min(W, H);
    }

    public readonly struct HostFace
    {
        public readonly double UMin, UMax, VMin, VMax, Thickness;
        public HostFace(double uMin, double uMax, double vMin, double vMax, double thickness)
        {
            UMin = uMin;
            UMax = uMax;
            VMin = vMin;
            VMax = vMax;
            Thickness = thickness;
        }
        public double LengthU => UMax - UMin;
    }

    public sealed class Cluster
    {
        public int[] Indices = Array.Empty<int>();
        public FaceRect Envelope;
        public bool Merged;
        public bool Large;
    }

    public static double AxisGap(double aMin, double aMax, double bMin, double bMax)
    {
        if (aMax < bMin) return bMin - aMax;
        if (bMax < aMin) return aMin - bMax;
        return 0;
    }

    public static bool ShouldLink(in FaceRect a, in FaceRect b, double thickness, double minClear)
    {
        var thresh = Math.Max(Math.Min(a.MinDim, b.MinDim), Math.Max(thickness, minClear));
        return AxisGap(a.UMin, a.UMax, b.UMin, b.UMax) < thresh
            && AxisGap(a.VMin, a.VMax, b.VMin, b.VMax) < thresh;
    }

    public static FaceRect EnvelopeOf(IReadOnlyList<FaceRect> rects)
    {
        var uMin = double.PositiveInfinity;
        var uMax = double.NegativeInfinity;
        var vMin = double.PositiveInfinity;
        var vMax = double.NegativeInfinity;
        foreach (var r in rects)
        {
            if (r.UMin < uMin) uMin = r.UMin;
            if (r.UMax > uMax) uMax = r.UMax;
            if (r.VMin < vMin) vMin = r.VMin;
            if (r.VMax > vMax) vMax = r.VMax;
        }
        return new FaceRect((uMin + uMax) * 0.5, (vMin + vMax) * 0.5, uMax - uMin, vMax - vMin);
    }

    public static bool CanMerge(in FaceRect env, in HostFace host, double minClear)
    {
        var remainU = Math.Min(env.UMin - host.UMin, host.UMax - env.UMax);
        var remainV = Math.Min(env.VMin - host.VMin, host.VMax - env.VMax);
        var remain = Math.Min(remainU, remainV);
        if (remain < Math.Max(minClear, host.Thickness)) return false;
        if (env.W > MaxHostFraction * host.LengthU) return false;
        return true;
    }

    public static bool IsNearlySquare(in FaceRect env, double tol = 0.15)
    {
        var m = Math.Max(env.W, env.H);
        return m > 0 && Math.Abs(env.W - env.H) / m < tol;
    }

    public static bool IsLarge(in FaceRect env, double largeLimit)
        => Math.Max(env.W, env.H) > largeLimit;

    public static List<Cluster> Group(
        IReadOnlyList<FaceRect> rects, HostFace host, double minClear, double largeLimit)
    {
        var n = rects.Count;
        var parent = new int[n];
        for (var i = 0; i < n; i++) parent[i] = i;

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        for (var i = 0; i < n; i++)
        for (var j = i + 1; j < n; j++)
        {
            if (!ShouldLink(rects[i], rects[j], host.Thickness, minClear)) continue;
            var a = Find(i);
            var b = Find(j);
            if (a != b) parent[a] = b;
        }

        var buckets = new Dictionary<int, List<int>>();
        for (var i = 0; i < n; i++)
        {
            var r = Find(i);
            if (!buckets.TryGetValue(r, out var list)) buckets[r] = list = new List<int>();
            list.Add(i);
        }

        var result = new List<Cluster>(buckets.Count);
        foreach (var list in buckets.Values)
        {
            list.Sort();
            var members = new List<FaceRect>(list.Count);
            foreach (var i in list) members.Add(rects[i]);
            var env = EnvelopeOf(members);
            var merge = list.Count > 1 && CanMerge(env, host, minClear);
            result.Add(new Cluster
            {
                Indices = list.ToArray(),
                Envelope = env,
                Merged = merge,
                Large = merge && IsLarge(env, largeLimit)
            });
        }
        result.Sort((a, b) => a.Indices[0].CompareTo(b.Indices[0]));
        return result;
    }
}
