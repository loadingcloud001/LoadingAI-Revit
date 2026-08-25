using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class PlanParserTests
{
    [Fact]
    public void ParsesNumberedSteps()
    {
        var draft = PlanParser.TryParse("Room tagging\n1. Survey rooms with get_rooms\n2. Place tags via tag_rooms\n3. Export schedule");
        Assert.NotNull(draft);
        Assert.Equal("Room tagging", draft!.Title);
        Assert.Equal(3, draft.Steps.Count);
        Assert.Contains("Survey rooms", draft.Steps[0].Title);
    }

    [Fact]
    public void ReturnsNullWhenFewerThanTwoSteps()
    {
        Assert.Null(PlanParser.TryParse("Just a sentence."));
        Assert.Null(PlanParser.TryParse("1. Only one step"));
    }
}
