using System.IO;
using LoadingAI.Geometry;
using LoadingAI.Revit;
using LoadingAI.Revit.StructureCuts;
using Xunit;

namespace LoadingAI.Tests;

/// <summary>
/// Locks includeLinks without Revit.exe. The flag is default-off at the options, the DTO, and the
/// host gate that walks links. Station math is tested with fakes so a linked cutter is never
/// used at link-local coordinates, an unloaded link is named, a tilted datum is skipped, and a
/// coincident station cannot make a zero-height piece or a double split.
/// </summary>
public class StructureCutsLinkCutterTests
{
    [Fact]
    public void SplitOptions_IncludeLinks_DefaultsFalse()
    {
        var options = new SplitOptions();
        Assert.False(options.IncludeLinks);
    }

    [Fact]
    public void SplitRequestDto_OmittedIncludeLinks_IsFalse()
    {
        // The plugin still constructs the original nine arguments. A defaulted tenth positional
        // keeps that call compiling and must mean off, not "consider every loaded link".
        var request = new SplitRequestDto(
            HostIds: null,
            Cutters: null,
            Categories: null,
            Scope: "selection",
            ContinueFrom: 0,
            Limit: 50,
            DryRun: false,
            UniqueMark: false,
            CopyParams: true);

        Assert.False(request.IncludeLinks);
    }

    [Fact]
    public void LinkTraversal_DoesNotRunUnlessIncludeLinks()
    {
        var scope = File.ReadAllText(HostFile("SplitScope.cs"));
        var gate = scope.IndexOf("public static SplitElements.LinkCutters LinkCutters(", StringComparison.Ordinal);
        Assert.True(gate >= 0, "LinkCutters is the only link-walk gate.");
        var body = scope.Substring(gate, Math.Min(400, scope.Length - gate));
        var check = body.IndexOf("!options.IncludeLinks", StringComparison.Ordinal);
        var walk = body.IndexOf("SplitElements.Links(", StringComparison.Ordinal);
        Assert.True(check >= 0 && walk > check, "Links() must sit behind the IncludeLinks check.");

        // Flag-off returns before the walk, so a test that reads the gate is enough: the method
        // cannot reach RevitLinkInstance unless the flag is true.
        Assert.Contains("return new SplitElements.LinkCutters();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformPoint_DoesNotKeepLinkLocalCoordinates()
    {
        var linkLocal = new Vec3(4, 0, 10);
        var moved = LinkCutterMath.TransformPoint(
            origin: new Vec3(100, 20, 0),
            basisX: new Vec3(1, 0, 0),
            basisY: new Vec3(0, 1, 0),
            basisZ: new Vec3(0, 0, 1),
            point: linkLocal);

        Assert.NotEqual(linkLocal, moved);
        Assert.Equal(104, moved.X, 6);
        Assert.Equal(20, moved.Y, 6);
        Assert.Equal(10, moved.Z, 6);
    }

    [Fact]
    public void UnloadedLink_NoteIsHonest()
    {
        Assert.Equal("link not loaded", LinkCutterMath.UnloadedNote);

        var elements = File.ReadAllText(HostFile("SplitElements.cs"));
        Assert.Contains("LinkCutterMath.UnloadedNote", elements, StringComparison.Ordinal);
        Assert.Contains("GetLinkDocument()", elements, StringComparison.Ordinal);
        Assert.Contains("LinkCutterMath.TransformFailedNote", elements, StringComparison.Ordinal);
    }

    [Fact]
    public void TiltedLevelAndGrid_AreSkippedByName()
    {
        Assert.Equal("tilted link datum skipped", LinkCutterMath.TiltedSkip);
        Assert.True(LinkCutterMath.IsTiltedLevel(new Vec3(0.2, 0, 1)));
        Assert.False(LinkCutterMath.IsTiltedLevel(new Vec3(0, 0, 1)));
        Assert.True(LinkCutterMath.IsTiltedGrid(new Vec3(0, 0, 0), new Vec3(10, 0, 5)));
        Assert.False(LinkCutterMath.IsTiltedGrid(new Vec3(0, 0, 10), new Vec3(20, 0, 10)));

        var elements = File.ReadAllText(HostFile("SplitElements.cs"));
        Assert.Contains("LinkCutterMath.TiltedSkip", elements, StringComparison.Ordinal);
        Assert.Contains("IsTiltedLevel", elements, StringComparison.Ordinal);
        Assert.Contains("IsTiltedGrid", elements, StringComparison.Ordinal);
    }

    [Fact]
    public void CoincidentElevation_IsDedupedBeforePlanning()
    {
        var host = new[] { 0.0, 10.0 };
        var linked = new[] { 10.0 + (LinkCutterMath.ElevationTolFt / 2.0), 20.0 };

        var merged = LinkCutterMath.MergeElevations(host, linked);

        Assert.Equal(3, merged.Count);
        Assert.DoesNotContain(merged, z => Math.Abs(z - 10.0) > 0 && Math.Abs(z - 10.0) <= LinkCutterMath.ElevationTolFt);
        Assert.Contains(20.0, merged);
    }

    [Fact]
    public void CoincidentGridLine_IsDedupedBeforePlanning()
    {
        var hostLine = new LinkCutterMath.GridStation(new Vec3(0, 0, 0), new Vec3(30, 0, 0));
        var sameLineLonger = new LinkCutterMath.GridStation(new Vec3(-5, 0.0002, 0), new Vec3(40, 0, 0));
        var otherLine = new LinkCutterMath.GridStation(new Vec3(0, 8, 0), new Vec3(30, 8, 0));

        var merged = LinkCutterMath.MergeGrids(
            new[] { hostLine },
            new[] { sameLineLonger, otherLine });

        Assert.Equal(2, merged.Count);
        Assert.Equal(hostLine.Start, merged[0].Start);
        Assert.Equal(otherLine.Start, merged[1].Start);
    }

    [Fact]
    public void FlagOn_ReportNamesLoadedLinksAndLinkCutters()
    {
        foreach (var file in new[] { "BeamSplit.cs", "FloorSplit.cs", "BeamCut.cs", "LevelSplit.cs", "GridSplit.cs" })
        {
            var src = File.ReadAllText(HostFile(file));
            Assert.Contains("SplitScope.LinkCutters(", src, StringComparison.Ordinal);
            Assert.Contains(".Report()", src, StringComparison.Ordinal);
        }

        var elements = File.ReadAllText(HostFile("SplitElements.cs"));
        Assert.Contains("Links considered:", elements, StringComparison.Ordinal);
        Assert.Contains("link cutters", elements, StringComparison.Ordinal);
    }

    private static string HostFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln")))
                return Path.Combine(dir.FullName, "src", "LoadingAI", "Revit", "StructureCuts", name);
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find LoadingAI.sln from " + AppContext.BaseDirectory);
    }
}
