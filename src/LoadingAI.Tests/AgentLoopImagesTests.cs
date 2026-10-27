using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class AgentLoopImagesTests
{
    [Fact]
    public async Task ImagesFromARound_AreSentAsOneSnapshotUserMessage()
    {
        var (loop, provider) = TestAgent.WithScript("capture_view", visionModel: "gpt-4o");
        var drained = false;
        loop.DrainToolImages = () =>
        {
            if (drained) return Array.Empty<AttachmentDto>();
            drained = true;
            return new[] { new AttachmentDto("s1", "snap.png", "image/png", "data:image/png;base64,AAAA") };
        };
        await loop.RunAsync(provider, ChatMessage.FromText("user", "look"), "build",
            (_, _) => Task.FromResult("Snapshot of Level 2"), _ => { }, null, null, null, null);

        var h = loop.History;
        var toolIdx = h.ToList().FindIndex(m => m.Role == "tool");
        var snap = h[toolIdx + 1];
        Assert.Equal("user", snap.Role);
        Assert.True(snap.IsSnapshot);
        Assert.True(LlmJson.IsMultimodal(snap.Content));
    }

    [Fact]
    public async Task VisionTools_AreHidden_ForAProviderWithoutVision()
    {
        var (loop, provider) = TestAgent.WithScript(null, visionModel: "deepseek-chat");
        IReadOnlyList<ToolDefinition>? offered = null;
        TestAgent.OnRequest = tools => offered = tools;
        await loop.RunAsync(provider, ChatMessage.FromText("user", "look at the view"), "build",
            (_, _) => Task.FromResult(""), _ => { }, null, null, null, null);
        Assert.DoesNotContain(offered ?? Array.Empty<ToolDefinition>(), t => t.Function.Name == "capture_view");
    }
}

/// <summary>
/// A scripted provider and a registry with one vision tool (<c>capture_view</c>). Records the tools
/// offered in each request so a test can check what the model was allowed to call.
/// </summary>
internal static class TestAgent
{
    /// <summary>Called with the tools of every request the scripted client receives.</summary>
    public static Action<IReadOnlyList<ToolDefinition>?>? OnRequest { get; set; }

    public static (AgentLoop Loop, ProviderConfig Provider) WithScript(string? toolName, string visionModel)
    {
        OnRequest = null;
        var bodies = toolName == null
            ? new[] { Response(null) }
            : new[] { Response(toolName), Response(null) };
        var client = new OpenAiCompatibleClient(new ScriptedHandler(bodies));

        var registry = new ToolRegistry();
        registry.Register(new ToolEntry
        {
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "capture_view",
                    Description = "Capture the active view as an image.",
                    Parameters = new { type = "object", properties = new { } }
                }
            },
            Tier = ToolTier.L1,
            NeedsVision = true,
            Handler = _ => Task.FromResult("Snapshot of Level 2")
        });

        var provider = new ProviderConfig
        {
            Id = "fake",
            Name = "Fake",
            BaseUrl = "http://fake.test/v1",
            Model = visionModel,
            ApiKey = "k",
            ApiKind = "openai"
        };
        return (new AgentLoop(client, registry), provider);
    }

    /// <summary>One assistant reply: a tool call to <paramref name="toolName"/>, or a plain answer.</summary>
    private static string Response(string? toolName)
    {
        var msg = new ChatMessage
        {
            Role = "assistant",
            Content = toolName == null ? "done" : ""
        };
        if (toolName != null)
        {
            msg.ToolCalls = new List<ToolCall>
            {
                new()
                {
                    Id = "c1",
                    Function = new ToolCallFunction { Name = toolName, Arguments = "{}" }
                }
            };
        }
        var resp = new ChatCompletionResponse
        {
            Choices = new List<Choice>
            {
                new()
                {
                    Message = msg,
                    FinishReason = toolName == null ? "stop" : "tool_calls"
                }
            }
        };
        return JsonSerializer.Serialize(resp);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        public ScriptedHandler(IEnumerable<string> bodies) => _bodies = new Queue<string>(bodies);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
            try { OnRequest?.Invoke(ParseTools(body)); } catch { }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "application/json")
            };
        }

        private static IReadOnlyList<ToolDefinition>? ParseTools(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
                    return null;
                var list = new List<ToolDefinition>();
                foreach (var el in tools.EnumerateArray())
                {
                    if (!el.TryGetProperty("function", out var fn)) continue;
                    var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    list.Add(new ToolDefinition { Function = new ToolFunction { Name = name } });
                }
                return list;
            }
            catch { return null; }
        }
    }
}
