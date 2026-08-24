using RevitAiPoc.Llm;

namespace RevitAiPoc.Tests;

public class ChatHistoryTrimmerTests
{
    [Fact]
    public void TruncatesLargeToolResults()
    {
        var history = new List<ChatMessage>
        {
            new() { Role = "system", Content = "sys" },
            new() { Role = "tool", Content = new string('x', 5000) }
        };
        ChatHistoryTrimmer.Trim(history);
        var text = LlmJson.GetContentString(history[1].Content);
        Assert.True(text.Length < 5000);
        Assert.EndsWith("[omitted]", text);
    }

    [Fact]
    public void KeepsSystemAndRecent()
    {
        var history = new List<ChatMessage> { new() { Role = "system", Content = "sys" } };
        for (int i = 0; i < 80; i++)
            history.Add(new ChatMessage { Role = "user", Content = $"u{i}" });
        ChatHistoryTrimmer.Trim(history);
        Assert.Equal("system", history[0].Role);
        Assert.True(history.Count <= ChatHistoryTrimmer.KeepMessages + 1);
    }

    [Fact]
    public void StripsOldImages_KeepsLatestUserImage()
    {
        var oldImg = new List<ChatContentPart>
        {
            new() { Type = "text", Text = "old" },
            new() { Type = "image_url", ImageUrl = new ChatImageUrl { Url = "data:image/png;base64,OLD" } }
        };
        var newImg = new List<ChatContentPart>
        {
            new() { Type = "text", Text = "new" },
            new() { Type = "image_url", ImageUrl = new ChatImageUrl { Url = "data:image/png;base64,NEW" } }
        };
        var history = new List<ChatMessage>
        {
            new() { Role = "system", Content = "sys" },
            new() { Role = "user", Content = oldImg },
            new() { Role = "assistant", Content = "ok" },
            new() { Role = "user", Content = newImg }
        };
        ChatHistoryTrimmer.Trim(history);
        Assert.False(LlmJson.IsMultimodal(history[1].Content));
        Assert.Contains("omitted", LlmJson.GetContentString(history[1].Content));
        Assert.True(LlmJson.IsMultimodal(history[3].Content));
        Assert.Contains("new", LlmJson.GetContentString(history[3].Content));
    }
}
