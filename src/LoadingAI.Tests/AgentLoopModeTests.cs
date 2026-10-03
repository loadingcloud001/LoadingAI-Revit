using System.Net;
using System.Net.Http;
using System.Text;
using LoadingAI.Llm;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class AgentLoopModeTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        public ScriptedHandler(params string[] bodies) => _bodies = new Queue<string>(bodies);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "application/json")
            });
    }

    private const string CallWriteTool =
        """{"choices":[{"message":{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"set_comments","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""";
    private const string Done =
        """{"choices":[{"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}""";

    private static ProviderConfig Fake() => new()
    {
        Id = "fake", Name = "Fake", BaseUrl = "http://fake.test/v1", Model = "m", ApiKey = "k", ApiKind = "openai"
    };

    private static ToolRegistry RegistryWithL2Write()
    {
        var reg = new ToolRegistry();
        reg.Register(new ToolEntry
        {
            Definition = new ToolDefinition { Function = new ToolFunction { Name = "set_comments", Description = "Write comments.", Parameters = new { type = "object", properties = new { } } } },
            Tier = ToolTier.L2,
            Handler = _ => Task.FromResult("wrote")
        });
        return reg;
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("ask")]
    public async Task ReadOnlyMode_RefusesNonL1ToolEvenWhenModelCallsIt(string mode)
    {
        using var client = new OpenAiCompatibleClient(new ScriptedHandler(CallWriteTool, Done));
        var loop = new AgentLoop(client, RegistryWithL2Write());
        var executed = new List<string>();

        var result = await loop.RunAsync(Fake(), ChatMessage.FromText("user", "add comments"), mode,
            (name, args) => { executed.Add(name); return Task.FromResult("wrote"); },
            _ => { }, null);

        Assert.Empty(executed);
        Assert.Equal("done", result);
        var toolMsg = loop.History.Single(m => m.Role == "tool");
        Assert.Contains($"not available in {mode} mode", LlmJson.GetContentString(toolMsg.Content));
    }

    [Fact]
    public async Task BuildMode_StillExecutesWriteTools()
    {
        using var client = new OpenAiCompatibleClient(new ScriptedHandler(CallWriteTool, Done));
        var loop = new AgentLoop(client, RegistryWithL2Write());
        var executed = new List<string>();

        await loop.RunAsync(Fake(), ChatMessage.FromText("user", "add comments"), "build",
            (name, args) => { executed.Add(name); return Task.FromResult("wrote"); },
            _ => { }, null);

        Assert.Equal(new[] { "set_comments" }, executed);
    }

    [Theory]
    [InlineData("plan", ToolTier.L1, true)]
    [InlineData("plan", ToolTier.L2, false)]
    [InlineData("ASK", ToolTier.L3, false)]
    [InlineData("build", ToolTier.L3, true)]
    [InlineData(null, ToolTier.L2, true)]
    public void ModeAllowsTier_Matrix(string? mode, ToolTier tier, bool allowed)
        => Assert.Equal(allowed, AgentLoop.ModeAllowsTier(mode, tier));
}
