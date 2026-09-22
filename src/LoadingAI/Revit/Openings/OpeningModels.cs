using Autodesk.Revit.DB;

namespace LoadingAI.Revit.Openings;

internal readonly struct ClashHit
{
    public readonly ElementId MepId;
    public readonly ElementId HostId;
    public readonly bool HostLinked;
    public readonly bool SourceLinked;
    public readonly XYZ Centroid;
    public readonly XYZ HostDir;
    public readonly double WidthFt;
    public readonly double HeightFt;
    public readonly double DepthFt;
    public readonly string Shape;
    public readonly ElementId LevelId;
    public readonly string Key;
    public readonly bool Combined;
    public readonly int CombinedCount;
    public readonly string CombinedSources;

    public ClashHit(
        ElementId mepId, ElementId hostId, bool hostLinked, bool sourceLinked,
        XYZ centroid, XYZ hostDir, double widthFt, double heightFt, double depthFt,
        string shape, ElementId levelId, string key,
        bool combined = false, int combinedCount = 1, string combinedSources = "")
    {
        MepId = mepId;
        HostId = hostId;
        HostLinked = hostLinked;
        SourceLinked = sourceLinked;
        Centroid = centroid;
        HostDir = hostDir;
        WidthFt = widthFt;
        HeightFt = heightFt;
        DepthFt = depthFt;
        Shape = shape;
        LevelId = levelId;
        Key = key;
        Combined = combined;
        CombinedCount = combinedCount;
        CombinedSources = combinedSources ?? "";
    }
}

internal sealed class CombineReport
{
    public int Clusters;
    public int Large;
}
