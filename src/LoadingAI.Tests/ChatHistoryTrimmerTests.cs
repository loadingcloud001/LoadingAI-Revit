using LoadingAI.Llm;

namespace LoadingAI.Tests;

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
    public void StripsOldImages_KeepsTheNewestTwoUserImages()
    {
        var oldImg = new List<ChatContentPart>
        {
            new() { Type = "text", Text = "old" },
            new() { Type = "image_url", ImageUrl = new ChatImageUrl { Url = "data:image/png;base64,OLD" } }
        };
        var midImg = new List<ChatContentPart>
        {
            new() { Type = "text", Text = "mid" },
            new() { Type = "image_url", ImageUrl = new ChatImageUrl { Url = "data:image/png;base64,MID" } }
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
            new() { Role = "user", Content = midImg },
            new() { Role = "user", Content = newImg }
        };
        ChatHistoryTrimmer.Trim(history);
        Assert.False(LlmJson.IsMultimodal(history[1].Content));
        Assert.Contains("omitted", LlmJson.GetContentString(history[1].Content));
        Assert.True(LlmJson.IsMultimodal(history[3].Content));
        Assert.True(LlmJson.IsMultimodal(history[4].Content));
        Assert.Contains("new", LlmJson.GetContentString(history[4].Content));
    }

    private static ChatMessage CallRound(int i) => new()
    {
        Role = "assistant",
        Content = "",
        ToolCalls = new List<ToolCall> { new() { Id = $"c{i}", Function = new ToolCallFunction { Name = "t", Arguments = "{}" } } }
    };

    [Fact]
    public void PinsCurrentTurnUserMessage_WhenALongTurnOverflows()
    {
        var history = new List<ChatMessage>
        {
            new() { Role = "system", Content = "sys" },
            new() { Role = "user", Content = "old question" },
            new() { Role = "assistant", Content = "old answer" },
            new() { Role = "user", Content = "split every beam by columns" }
        };
        for (int i = 0; i < 30; i++)
        {
            history.Add(CallRound(i));
            history.Add(new ChatMessage { Role = "tool", ToolCallId = $"c{i}", Name = "t", Content = $"r{i}" });
        }
        // Odd count makes the raw cut land on a tool message, so the leading-tool skip is exercised.
        history.Add(new ChatMessage { Role = "assistant", Content = "working" });

        ChatHistoryTrimmer.Trim(history);

        Assert.Equal("system", history[0].Role);
        Assert.Equal("split every beam by columns", LlmJson.GetContentString(history[1].Content));
        Assert.DoesNotContain(history, m => LlmJson.GetContentString(m.Content) == "old question");
        Assert.NotEqual("tool", history[2].Role);
    }

    [Fact]
    public void CurrentTurnToolResults_KeepMoreThanOlderOnes()
    {
        var big = new string('x', 5000);
        var history = new List<ChatMessage>
        {
            new() { Role = "system", Content = "sys" },
            new() { Role = "user", Content = "q1" },
            new() { Role = "tool", Content = big },
            new() { Role = "user", Content = "q2" },
            new() { Role = "tool", Content = big }
        };

        ChatHistoryTrimmer.Trim(history);

        Assert.EndsWith("[omitted]", LlmJson.GetContentString(history[2].Content));
        Assert.Equal(5000, LlmJson.GetContentString(history[4].Content).Length);
    }

    [Fact]
    public void KeepsTheNewestTwoImageMessages_AndSnapshotsDoNotCountAsTheUserTurn()
    {
        var img = new List<AttachmentDto> { new("a", "a.png", "image/png", "data:image/png;base64,AA") };
        var h = new List<ChatMessage>
        {
            ChatMessage.FromText("system", "s"),
            ChatMessage.FromMultimodal("user", "first", img),
            ChatMessage.FromText("user", "split the beams"),
            new() { Role = "tool", ToolCallId = "1", Name = "x", Content = new string('r', 5000) },
            Snap(img), Snap(img), Snap(img),
        };
        ChatHistoryTrimmer.Trim(h);
        Assert.False(LlmJson.IsMultimodal(h[1].Content));  // old user image stripped
        Assert.False(LlmJson.IsMultimodal(h[4].Content));  // oldest snapshot stripped
        Assert.True(LlmJson.IsMultimodal(h[5].Content));
        Assert.True(LlmJson.IsMultimodal(h[6].Content));
        // The tool result after the real user turn keeps the current-turn cap, not the 2000 cap.
        Assert.True(LlmJson.GetContentString(h[3].Content).Length > ChatHistoryTrimmer.MaxToolResultChars);

        static ChatMessage Snap(List<AttachmentDto> i)
        {
            var m = ChatMessage.FromMultimodal("user", "[Snapshot from capture_view]", i);
            m.IsSnapshot = true;
            return m;
        }
    }
}
