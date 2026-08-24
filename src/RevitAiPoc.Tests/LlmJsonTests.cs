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

    [Fact]
    public void GetContentString_JoinsTextParts()
    {
        var parts = new List<ChatContentPart>
        {
            new() { Type = "text", Text = "hello" },
            new() { Type = "image_url", ImageUrl = new ChatImageUrl { Url = "data:image/png;base64,xx" } },
            new() { Type = "text", Text = "world" }
        };
        Assert.Equal("hello\nworld", LlmJson.GetContentString(parts));
    }

    [Fact]
    public void StripImages_ReplacesImageParts()
    {
        var parts = new List<ChatContentPart>
        {
            new() { Type = "text", Text = "see this" },
            new() { Type = "image_url", ImageUrl = new ChatImageUrl { Url = "data:image/png;base64,xx" } }
        };
        var stripped = LlmJson.StripImages(parts);
        Assert.Equal("see this\n[1 image(s) omitted]", LlmJson.GetContentString(stripped));
        Assert.False(LlmJson.IsMultimodal(stripped));
    }
}
