using System.IO;
using LoadingAI.Core.Openings;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class OpeningBridgeOutcomeTests
{
    [Fact]
    public void PlaceMapperPreservesStructuredCountsAndLocations()
    {
        var work = new OpeningPlaceWorkResult(
            "Placed 2. Punched 2. Failed 1. Skipped 3.",
            2, 2, 1, 3, 1,
            new[] { "101", "102" },
            new[] { "1,2,3", "4,5,6" },
            "One host failed.");

        var dto = OpeningOutcomeMapper.ToPlaceDto(work);

        Assert.Equal(2, dto.Placed);
        Assert.Equal(2, dto.Punched);
        Assert.Equal(1, dto.Failed);
        Assert.Equal(3, dto.Skipped);
        Assert.Equal(1, dto.JoinsLost);
        Assert.Equal(new[] { "101", "102" }, dto.InstanceIds);
        Assert.Equal(new[] { "1,2,3", "4,5,6" }, dto.Points);
        Assert.Equal("One host failed.", dto.Error);
        Assert.Contains("Placed 2.", dto.Report);
    }

    [Fact]
    public void CutMapperPreservesStructuredCounts()
    {
        var work = new OpeningCutWorkResult(
            "Cut 2. Skipped linked: 1. Skipped cannot-cut: 3. Failed 4.",
            2, 1, 3, 4,
            "One cut failed.");

        var dto = OpeningOutcomeMapper.ToCutDto(work);

        Assert.Equal(2, dto.Cut);
        Assert.Equal(1, dto.SkippedLinked);
        Assert.Equal(3, dto.SkippedCannot);
        Assert.Equal(4, dto.Failed);
        Assert.Equal("One cut failed.", dto.Error);
    }

    [Fact]
    public void AdapterDoesNotParseOpeningReports()
    {
        var adapter = AdapterSource.All();
        Assert.DoesNotContain("ProjectPlace", adapter);
        Assert.DoesNotContain("Regex.Matches", adapter);
        Assert.Contains("OpeningOutcomeMapper.ToPlaceDto", adapter);
        Assert.Contains("OpeningOutcomeMapper.ToCutDto", adapter);
        Assert.Contains("PlaceOpenings.Run", adapter);
        Assert.Contains("CutLocal.Run", adapter);
    }

    [Fact]
    public void OpeningAlgorithmsReturnWorkResults()
    {
        var place = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "Openings", "PlaceOpenings.cs"));
        var cut = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "Openings", "CutHosts.cs"));
        Assert.Contains("OpeningPlaceWorkResult Run(", place);
        Assert.Contains("OpeningCutWorkResult Run(", cut);
        Assert.DoesNotContain("public static string Run(", place);
        Assert.DoesNotContain("public static string Run(", cut);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find LoadingAI.sln");
    }
}
