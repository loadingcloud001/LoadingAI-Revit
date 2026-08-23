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
}
