using LoadingAI.Revit;
using Xunit;

namespace LoadingAI.Tests;

public class LevelNameMatchTests
{
    [Fact]
    public void ExactMatch_Wins()
    {
        var names = new[] { "L1 - Block 43", "L1 - Block 35", "L5" };
        Assert.Equal("L1 - Block 43", LevelNames.Resolve("L1 - Block 43", names));
    }

    [Fact]
    public void Level1_SuggestsL1Blocks()
    {
        var names = new[] { "Parking", "L1 - Block 35", "L1 - Block 37", "L1 - Block 43", "L5" };
        var msg = LevelNames.NotFound("Level 1", names);
        Assert.Contains("L1 - Block 43", msg);
        Assert.Contains("L1 - Block 35", msg);
        Assert.DoesNotContain("L5", msg);
    }
}
