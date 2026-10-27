using System.Linq;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class ProjectIndexTests
{
    private static ProjectIndex Sample(int categories = 20) => new(
        Levels: new[] { new IndexLevel("Level 1", 0), new IndexLevel("Level 2", 3500) },
        Grids: new[] { "A", "B", "1" },
        Categories: Enumerable.Range(0, categories).Select(i => new IndexCategory($"Cat{i}", 1000 - i)).ToList(),
        Families: new[] { new IndexFamily("Structural Framing", "UB-Universal Beam", new[] { "610x229x101UB" }) },
        Phases: new[] { "Existing", "New Construction" },
        Worksets: new[] { "Shared Levels and Grids" },
        Parameters: new Dictionary<string, IReadOnlyList<string>> { ["Walls"] = new[] { "Comments", "Mark" } },
        Views: new[] { "Level 1", "{3D}" },
        Sheets: new[] { "A-101 Ground Floor" });

    [Fact]
    public void Summary_ListsLevelsAndTop15Categories()
    {
        var s = Sample().ToPromptSummary();
        Assert.Contains("Level 1 (0 mm)", s);
        Assert.Contains("Level 2 (3500 mm)", s);
        Assert.Contains("Cat0 (1000)", s);
        Assert.Contains("Cat14", s);
        Assert.DoesNotContain("Cat15", s);
    }

    [Fact]
    public void Summary_IsCapped()
    {
        var many = Sample() with { Levels = Enumerable.Range(0, 500).Select(i => new IndexLevel($"Level {i} long name here", i * 3000)).ToList() };
        Assert.True(many.ToPromptSummary().Length <= ProjectIndex.MaxSummaryChars);
    }
}
