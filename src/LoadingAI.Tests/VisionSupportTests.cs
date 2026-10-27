using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class VisionSupportTests
{
    [Theory]
    [InlineData("gpt-4o-mini", true)]
    [InlineData("gpt-4.1", true)]
    [InlineData("gpt-5", true)]
    [InlineData("anthropic/claude-sonnet-4", true)]
    [InlineData("gemini-2.0-flash", true)]
    [InlineData("qwen2.5-vl-72b", true)]
    [InlineData("llava:13b", true)]
    [InlineData("deepseek-chat", false)]
    [InlineData("llama3.1", false)]
    [InlineData("MiniMax-M3.1-Flash", false)]
    [InlineData("", false)]
    public void Guess_FromModelName(string model, bool expected)
        => Assert.Equal(expected, VisionSupport.Supports(new ProviderConfig { Model = model }));

    [Fact]
    public void ExplicitSetting_Wins()
    {
        Assert.False(VisionSupport.Supports(new ProviderConfig { Model = "gpt-4o", SupportsImages = false }));
        Assert.True(VisionSupport.Supports(new ProviderConfig { Model = "llama3.1", SupportsImages = true }));
    }

    [Fact]
    public void NullProvider_IsFalse() => Assert.False(VisionSupport.Supports(null));
}
