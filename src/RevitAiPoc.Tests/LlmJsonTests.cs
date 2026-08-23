using RevitAiPoc.Llm;

namespace RevitAiPoc.Tests;

public class LlmJsonTests
{
    [Theory]
    [InlineData(null, "{}")]
    [InlineData("", "{}")]
    [InlineData("   ", "{}")]
    [InlineData("{}", "{}")]
    [InlineData("{\"x\":1}", "{\"x\":1}")]
    [InlineData("{}{}{}{}", "{}")]
    [InlineData("{\"a\":1}{\"b\":2}", "{\"a\":1}")]
    [InlineData("not-json", "{}")]
    public void NormalizeToolArguments_MakesValidObject(string? raw, string expected)
    {
        Assert.Equal(expected, LlmJson.NormalizeToolArguments(raw));
    }
}
