using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class SafeRegexTests
{
    [Fact]
    public void TryCreate_AcceptsAnOrdinaryPattern()
    {
        Assert.True(SafeRegex.TryCreate(@"^W-(\d+)$", out var re, out var error), error);
        Assert.Equal("W-12", re!.Replace("W-12", "W-$1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryCreate_RequiresAPattern(string? pattern)
    {
        Assert.False(SafeRegex.TryCreate(pattern, out _, out var error));
        Assert.Contains("required", error);
    }

    [Fact]
    public void TryCreate_RefusesAnOverlongPattern()
    {
        Assert.False(SafeRegex.TryCreate(new string('a', SafeRegex.MaxPatternLength + 1), out _, out var error));
        Assert.Contains(SafeRegex.MaxPatternLength.ToString(), error);
    }

    [Fact]
    public void TryCreate_ExplainsAnInvalidPattern()
    {
        Assert.False(SafeRegex.TryCreate("([unclosed", out _, out var error));
        Assert.Contains("invalid regular expression", error);
    }

    [Fact]
    public void ACatastrophicPatternTimesOutInsteadOfHanging()
    {
        Assert.True(SafeRegex.TryCreate(@"^(a+)+$", out var re, out _));
        var input = new string('a', 45) + "b";
        var clock = Stopwatch.StartNew();
        Assert.Throws<RegexMatchTimeoutException>(() => re!.Replace(input, "x"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the timeout must stop the match");
    }

    [Fact]
    public void Source_ReplaceTextUsesSafeRegexAndRollsBackOnTimeout()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> ReplaceTextInParamAsync");
        var start = src.IndexOf("public Task<CommandResultDto> ReplaceTextInParamAsync", StringComparison.Ordinal);
        var method = src.Substring(start, src.IndexOf("public Task<CommandResultDto> NumberElementsAsync", start, StringComparison.Ordinal) - start);
        Assert.Contains("SafeRegex.TryCreate(pattern", method);
        Assert.Contains("RegexMatchTimeoutException", method);
        Assert.Contains("tx.RollBack()", method);
        Assert.DoesNotContain("new System.Text.RegularExpressions.Regex(pattern)", method);
    }
}
