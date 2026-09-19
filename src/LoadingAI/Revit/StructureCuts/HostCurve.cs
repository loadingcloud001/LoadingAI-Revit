using Autodesk.Revit.DB;

using LoadingAI.Geometry;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// Location curve for walls, beams and slanted columns, or a vertical line synthesised from a
/// <see cref="LocationPoint"/> column so storey and grid cuts still have something to cross.
/// </summary>
internal static class HostCurve
{
    public static Curve? Of(Element host)
    {
        var curve = SplitElements.LocationCurveOf(host);
        if (curve != null)
            return curve;

        return VerticalForPointHost(host);
    }

    public static List<Vec3> Samples(Element host)
    {
        var curve = Of(host);
        if (curve == null)
            return new List<Vec3>();

        return GeometryRevit.Tessellate(curve).Select(GeometryRevit.ToVec3).ToList();
    }

    /// <summary>
    /// Native Split and location-curve recreate both need a real <see cref="LocationCurve"/>.
    /// A synthesised vertical for a <see cref="LocationPoint"/> column is only for geometry
    /// sampling — it cannot be written, so plan must skip those hosts.
    /// </summary>
    public static bool CanWrite(Element host)
        => SplitElements.LocationCurveOf(host) != null;

    public static string? WallSkip(Element host)
    {
        if (host is not Wall wall)
            return null;

        if (wall.WallType.Kind == WallKind.Stacked)
            return "stacked";
        if (wall.CurtainGrid != null)
            return "curtain";

        return null;
    }

    private static Curve? VerticalForPointHost(Element host)
    {
        if (host.Location is not LocationPoint location)
            return null;

        var origin = location.Point;
        if (origin == null)
            return null;

        var minZ = origin.Z;
        var maxZ = origin.Z;
        try
        {
            var box = host.get_BoundingBox(null);
            if (box != null)
            {
                minZ = Math.Min(box.Min.Z, box.Max.Z);
                maxZ = Math.Max(box.Min.Z, box.Max.Z);
            }
        }
        catch
        {
            // Bounding box is a convenience; levels still work as a fallback.
        }

        var baseLevel = host.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId();
        var topLevel = host.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.AsElementId();
        var doc = host.Document;
        if (doc != null)
        {
            if (doc.GetElement(baseLevel) is Level baseStorey)
                minZ = Math.Min(minZ, baseStorey.Elevation + Offset(host, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM));
            if (doc.GetElement(topLevel) is Level topStorey)
                maxZ = Math.Max(maxZ, topStorey.Elevation + Offset(host, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM));
        }

        if (maxZ - minZ <= GeometryRevit.Tol)
            return null;

        try
        {
            return Line.CreateBound(new XYZ(origin.X, origin.Y, minZ), new XYZ(origin.X, origin.Y, maxZ));
        }
        catch
        {
            return null;
        }
    }

    private static double Offset(Element host, BuiltInParameter parameter)
        => host.get_Parameter(parameter)?.AsDouble() ?? 0;
}
