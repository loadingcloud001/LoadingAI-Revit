using LoadingAI.Revit;
using Xunit;

namespace LoadingAI.Tests;

public class NameResolverTests
{
    private static readonly string[] Cats = { "Walls", "Structural Framing", "Structural Columns", "Columns", "Floors", "Doors", "Ducts", "Pipes" };

    [Theory]
    [InlineData("walls", "Walls", true)]
    [InlineData("WALL", "Walls", false)]
    [InlineData("beam", "Structural Framing", false)]
    [InlineData("beams", "Structural Framing", false)]
    [InlineData("slab", "Floors", false)]
    [InlineData("struct framing", "Structural Framing", false)]
    [InlineData("Floor", "Floors", false)]
    public void Category_BestMatch(string term, string expected, bool exact)
    {
        var r = NameResolver.Match(term, Cats, NameKind.Category);
        Assert.Equal(expected, r[0].Name);
        Assert.Equal(exact, r[0].Exact);
    }

    [Fact]
    public void Column_OffersBothColumnCategories()
    {
        var names = NameResolver.Match("column", Cats, NameKind.Category).Select(c => c.Name).ToList();
        Assert.Contains("Structural Columns", names);
        Assert.Contains("Columns", names);
    }

    [Theory]
    [InlineData("600x300", "600 x 300mm")]
    [InlineData("600*300", "600 x 300mm")]
    [InlineData("600×300", "600 x 300mm")]
    public void Type_SizeSeparatorsAreTheSame(string term, string expected)
        => Assert.Equal(expected, NameResolver.Match(term, new[] { "600 x 300mm", "300 x 300mm" }, NameKind.Type)[0].Name);

    [Fact]
    public void Level_TypoWithinDistance2()
        => Assert.Equal("Level 3", NameResolver.Match("Levle 3", new[] { "Level 1", "Level 2", "Level 3" }, NameKind.Level)[0].Name);

    [Fact]
    public void L3_MatchesLevel3()
        => Assert.Equal("Level 3", NameResolver.Match("L3", new[] { "Level 1", "Level 3", "Roof" }, NameKind.Level)[0].Name);

    [Fact]
    public void NoMatch_IsEmpty_AndAtMostFive()
    {
        Assert.Empty(NameResolver.Match("zzzz", Cats, NameKind.Category));
        Assert.True(NameResolver.Match("s", Cats, NameKind.Category).Count <= 5);
    }
}
