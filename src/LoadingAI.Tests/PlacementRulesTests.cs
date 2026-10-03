using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class PlacementRulesTests
{
    [Fact]
    public void OneLevelBased_IsAccepted()
        => Assert.Null(PlacementRules.Refusal("600 x 600", "OneLevelBased"));

    [Theory]
    [InlineData("OneLevelBasedHosted")]
    [InlineData("WorkPlaneBased")]
    [InlineData("TwoLevelsBased")]
    [InlineData("CurveBased")]
    [InlineData("ViewBased")]
    [InlineData("Adaptive")]
    public void EveryOtherKind_IsRefusedByName(string placement)
    {
        var message = PlacementRules.Refusal("Single-Flush", placement);
        Assert.NotNull(message);
        Assert.Contains("Single-Flush", message);
        Assert.Contains(placement, message);
    }

    [Fact]
    public void AHostedFamily_IsToldItNeedsAHost()
        => Assert.Contains("host", PlacementRules.Refusal("Single-Flush", "OneLevelBasedHosted"), System.StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("OST_StructuralColumns", PlacementKind.Column)]
    [InlineData("OST_StructuralFoundation", PlacementKind.Footing)]
    [InlineData("OST_Furniture", PlacementKind.NonStructural)]
    [InlineData(null, PlacementKind.NonStructural)]
    public void TheStructuralKind_FollowsTheCategory(string? category, PlacementKind expected)
        => Assert.Equal(expected, PlacementRules.KindFor(category));
}

public class SymbolPickTests
{
    private static readonly (string Family, string Symbol)[] Catalog =
    {
        ("Concrete-Rectangular-Column", "600 x 600"),
        ("Concrete-Rectangular-Column", "450 x 450"),
        ("Steel Column", "600 x 600"),
        ("Desk", "1500 x 750"),
    };

    [Fact]
    public void AUniqueTypeName_IsPicked()
    {
        Assert.Equal(1, SymbolPick.Choose(Catalog, "450 x 450", null, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void ATypeNameInTwoFamilies_IsAmbiguousWithoutAFamily()
    {
        Assert.Equal(-1, SymbolPick.Choose(Catalog, "600 x 600", null, out var error));
        Assert.Contains("2 families", error);
        Assert.Contains("Steel Column", error);
        Assert.Contains("familyName", error);
    }

    [Fact]
    public void TheFamilyDecidesWhenThereAreTwo()
        => Assert.Equal(2, SymbolPick.Choose(Catalog, "600 x 600", "steel column", out _));

    [Fact]
    public void AFamilyThatLacksTheType_DoesNotFallBackToAnotherFamily()
    {
        Assert.Equal(-1, SymbolPick.Choose(Catalog, "450 x 450", "Steel Column", out var error));
        Assert.Contains("Steel Column", error);
        Assert.Contains("600 x 600", error);
    }

    [Fact]
    public void AnUnknownName_ListsWhatIsLoaded()
    {
        Assert.Equal(-1, SymbolPick.Choose(Catalog, "nope", null, out var error));
        Assert.Contains("Available", error);
        Assert.Contains("Desk: 1500 x 750", error);
    }

    [Fact]
    public void Matching_IgnoresCase()
        => Assert.Equal(3, SymbolPick.Choose(Catalog, "1500 X 750", "DESK", out _));
}

public class LevelNamesStrictTests
{
    private static readonly string[] Two = { "Level 1", "Level 2" };

    [Fact]
    public void ANamedLevel_IsFoundIgnoringCase()
    {
        Assert.Equal("Level 2", LevelNames.ResolveStrict("level 2", Two, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void AnUnknownName_IsAnErrorThatListsTheLevels_NotTheFirstLevel()
    {
        Assert.Null(LevelNames.ResolveStrict("Level 9", Two, out var error));
        Assert.Contains("Level 9", error);
        Assert.Contains("Level 1", error);
        Assert.Contains("Level 2", error);
    }

    [Fact]
    public void ABlankName_IsFineWhenThereIsOneLevel()
        => Assert.Equal("Ground", LevelNames.ResolveStrict(null, new[] { "Ground" }, out _));

    [Fact]
    public void ABlankName_IsAnErrorWhenThereAreSeveral()
    {
        Assert.Null(LevelNames.ResolveStrict("  ", Two, out var error));
        Assert.Contains("levelName", error);
        Assert.Contains("Level 1", error);
    }

    [Fact]
    public void NoLevels_IsAnError()
    {
        Assert.Null(LevelNames.ResolveStrict("Level 1", new string[0], out var error));
        Assert.Equal("No levels found.", error);
    }
}
