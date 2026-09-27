using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class ThinkTagStripperTests
{
    [Fact]
    public void Strip_CompleteTags_RoutesInnerToReasoning_LeavesAnswer()
    {
        var result = ThinkTagStripper.Strip("<think>plan the cut</think>Split the beam at the column.");
        Assert.Equal("Split the beam at the column.", result.Visible);
        Assert.Equal("plan the cut", result.Reasoning);
        Assert.DoesNotContain("<think>", result.Visible, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</think>", result.Visible, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Push_SplitAcrossChunks_HoldsIncompleteOpenAndClose()
    {
        var stripper = new ThinkTagStripper();

        var a = stripper.Push("Hello <th");
        Assert.Equal("Hello ", a.Visible);
        Assert.Equal("", a.Reasoning);

        var b = stripper.Push("ink>hidden reason");
        Assert.Equal("", b.Visible);
        Assert.Equal("hidden reason", b.Reasoning);

        var c = stripper.Push("</thi");
        Assert.Equal("", c.Visible);
        Assert.Equal("", c.Reasoning);

        var d = stripper.Push("nk> visible answer");
        Assert.Equal(" visible answer", d.Visible);
        Assert.Equal("", d.Reasoning);

        var flushed = stripper.Flush();
        Assert.Equal("", flushed.Visible);
        Assert.Equal("", flushed.Reasoning);
    }

    [Fact]
    public void Strip_NestedAndMalformedLeftovers_DropTagsKeepAnswer()
    {
        var nested = ThinkTagStripper.Strip("Answer <think>outer <think>inner</think> still</think> done.");
        Assert.Equal("Answer  done.", nested.Visible);
        Assert.DoesNotContain("<think>", nested.Visible, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</think>", nested.Visible, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outer", nested.Reasoning, StringComparison.Ordinal);
        Assert.Contains("still", nested.Reasoning, StringComparison.Ordinal);

        var leftover = ThinkTagStripper.Strip("Visible</think> more <think");
        Assert.Equal("Visible more ", leftover.Visible);
        Assert.DoesNotContain("<think>", leftover.Visible, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</think>", leftover.Visible, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Strip_PlainAnswer_Unchanged()
    {
        const string answer = "Would split 11 beams into 22 pieces. No changes made.";
        var result = ThinkTagStripper.Strip(answer);
        Assert.Equal(answer, result.Visible);
        Assert.Equal("", result.Reasoning);
    }

    [Fact]
    public void Flush_UnclosedThink_IsReasoningNotChat()
    {
        var stripper = new ThinkTagStripper();
        var streamed = stripper.Push("ok <think>still thinking");
        var flushed = stripper.Flush();
        Assert.Equal("ok ", streamed.Visible + flushed.Visible);
        Assert.Equal("still thinking", streamed.Reasoning + flushed.Reasoning);
    }
}
