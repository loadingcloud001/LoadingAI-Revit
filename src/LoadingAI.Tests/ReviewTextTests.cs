using LoadingAI.Gate;
using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class ReviewTextTests
{
    [Fact]
    public void DenyWithoutAReason_IsThePlainCancel()
        => Assert.Equal("Cancelled by user (denied).", ReviewText.DenyResult(null));

    [Fact]
    public void DenyWithAReason_CarriesTheUsersWords()
        => Assert.Equal("Cancelled by user (denied): use level 2 instead", ReviewText.DenyResult("use level 2 instead"));

    [Fact]
    public void Reason_IsOneLine_NoControlCharacters_AndCapped()
    {
        var messy = "first\r\nsecond\tthird\u0007  fourth";
        Assert.Equal("first second third fourth", ReviewText.CleanReason(messy));

        var long200 = new string('x', 500);
        var clean = ReviewText.CleanReason(long200);
        Assert.Equal(ReviewText.MaxReasonChars, clean.Length);
        Assert.EndsWith("…", clean);
    }

    [Fact]
    public void BlankReason_IsDropped()
        => Assert.Equal("Cancelled by user (denied).", ReviewText.DenyResult("   \r\n  "));

    [Fact]
    public void Timeout_SaysCancelled_NeverApproved()
    {
        var text = ReviewText.TimeoutResult();
        Assert.StartsWith("Cancelled", text);
        Assert.Contains("180", text);
        Assert.DoesNotContain("approved", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Blocked_TellsTheModelWhatToAsk()
        => Assert.Contains("permission level", ReviewText.BlockedResult("delete_elements"));

    [Fact]
    public void Steer_FramesTheUsersWords()
    {
        var text = ReviewText.SteerMessage("  skip the doors  ");
        Assert.Contains("The user sent this while you were working", text);
        Assert.EndsWith("skip the doors", text);
    }

    [Fact]
    public void RowsFromArgs_ListsTopLevelArguments()
    {
        var rows = ReviewText.RowsFromArgs("""{"category":"Walls","level":3,"all":true,"ids":[1,2,3],"note":"","gone":null}""");
        Assert.Equal(
            new[] { "category=Walls", "level=3", "all=on", "ids=3 items", "note=-", "gone=-" },
            rows.Select(r => r.Label + "=" + r.Detail).ToArray());
    }

    [Fact]
    public void RowsFromArgs_CapsTheRows_AndSaysHowManyAreHidden()
    {
        var args = "{" + string.Join(",", Enumerable.Range(1, 12).Select(i => $"\"a{i}\":{i}")) + "}";
        var rows = ReviewText.RowsFromArgs(args, maxRows: 5);
        Assert.Equal(6, rows.Count);
        Assert.Equal("7 more arguments", rows[5].Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void RowsFromArgs_NeverThrows(string? json)
        => Assert.Empty(ReviewText.RowsFromArgs(json));
}

public class UserInstructionsTests
{
    [Fact]
    public void Sanitize_TrimsDropsControlCharsAndKeepsLineBreaks()
        => Assert.Equal("Use mm.\nName levels L01.", UserInstructions.Sanitize("  Use mm.\r\nName levels\u0000 L01.  "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void Sanitize_BlankIsNull(string? text)
        => Assert.Null(UserInstructions.Sanitize(text));

    [Fact]
    public void Sanitize_CapsTheLength()
        => Assert.Equal(UserInstructions.MaxChars, UserInstructions.Sanitize(new string('a', 5000))!.Length);

    [Fact]
    public void Block_IsEmptyWithoutInstructions()
        => Assert.Equal("", UserInstructions.Block(null));

    [Fact]
    public void Block_SaysItCannotChangeTheTiersOrTheConfirms()
    {
        var block = UserInstructions.Block("Always use mm.");
        Assert.Contains("# Standing instructions from the user", block);
        Assert.Contains("Always use mm.", block);
        Assert.Contains("do not change which tools you may use", block);
    }

    [Fact]
    public void SystemPrompt_CarriesTheBlockBeforeTheToolList()
    {
        var prompt = AgentLoop.BuildSystemPrompt(new List<ToolDefinition>(), null, false, "Name sheets A-101.");
        var block = prompt.IndexOf("Name sheets A-101.", StringComparison.Ordinal);
        var tools = prompt.IndexOf("# Tools", StringComparison.Ordinal);
        Assert.True(block > 0 && block < tools);
    }

    [Fact]
    public void SystemPrompt_WithoutInstructionsIsUnchanged()
    {
        var with = AgentLoop.BuildSystemPrompt(new List<ToolDefinition>(), null, false, null);
        Assert.DoesNotContain("Standing instructions", with);
    }
}
