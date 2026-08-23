using System.Text.Json;
using RevitAiPoc.Revit;
using RevitAiPoc.Tools;

namespace RevitAiPoc.Llm;

public sealed class AgentLoop
{
    private readonly OpenAiCompatibleClient _client;
    private readonly ToolRegistry _registry;
    private readonly List<ChatMessage> _history = new();
    private readonly object _historyLock = new();

    public const string BaseSystemPrompt =
        "You are a Revit BIM assistant. Use only the tools listed below. " +
        "If a tool returns an error, tell the user what to do (open a model, select elements). " +
        "Do not claim you changed the model unless a write tool succeeded. Be concise. " +
        "Destructive actions are confirmed via TaskDialog. You have up to 64 tool rounds; prefer composite tools. " +
        "User can press Stop to cancel.";

    public AgentLoop(OpenAiCompatibleClient client, ToolRegistry registry)
    {
        _client = client;
        _registry = registry;
        _history.Add(new ChatMessage { Role = "system", Content = BaseSystemPrompt });
    }

    public IReadOnlyList<ChatMessage> History
    {
        get { lock (_historyLock) return _history.ToList(); }
    }

    public void ClearHistory()
    {
        lock (_historyLock)
        {
            _history.Clear();
            _history.Add(new ChatMessage { Role = "system", Content = BaseSystemPrompt });
        }
    }

    public async Task<string> RunAsync(
        ProviderConfig provider,
        string userText,
        Func<string, string, Task<string>> toolExecutor,
        Action<string> onTrace,
        CancellationToken ct = default)
        => await RunAsync(provider, userText, toolExecutor, onTrace, null, ct);

    public async Task<string> RunAsync(
        ProviderConfig provider,
        string userText,
        Func<string, string, Task<string>> toolExecutor,
        Action<string> onTrace,
        Action<string>? onDelta,
        CancellationToken ct = default)
    {
        lock (_historyLock)
        {
            _history.Add(new ChatMessage { Role = "user", Content = userText });
            ChatHistoryTrimmer.Trim(_history);
        }
        try { HistoryStore.Append("user", userText); HistoryStore.Trim(); } catch { }

        var tools = ToolSelector.Select(_registry.GetToolDefinitions(), userText);
        var sys = BuildSystemPrompt(tools);
        lock (_historyLock)
        {
            if (_history.Count > 0 && _history[0].Role == "system")
                _history[0].Content = sys;
        }

        const int MaxRounds = 64;
        for (int round = 0; round < MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            List<ChatMessage> snapshot;
            lock (_historyLock) snapshot = new List<ChatMessage>(_history);

            ChatCompletionResponse resp;
            try
            {
                resp = await _client.ChatAsync(provider, snapshot, tools, onDelta, ct);
            }
            catch (OperationCanceledException)
            {
                var cancelMsg = "Cancelled.";
                lock (_historyLock) _history.Add(new ChatMessage { Role = "assistant", Content = cancelMsg });
                onTrace(cancelMsg);
                return cancelMsg;
            }
            catch (Exception ex)
            {
                var msg = $"LLM error: {ex.Message}";
                lock (_historyLock) _history.Add(new ChatMessage { Role = "assistant", Content = msg });
                try { HistoryStore.Append("assistant", msg); } catch { }
                return msg;
            }

            var choice = resp.Choices?.FirstOrDefault();
            var msgOut = choice?.Message;
            if (msgOut == null)
            {
                var fallback = "No response from model.";
                lock (_historyLock) _history.Add(new ChatMessage { Role = "assistant", Content = fallback });
                return fallback;
            }

            if (msgOut.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in msgOut.ToolCalls)
                {
                    if (string.IsNullOrWhiteSpace(tc.Id))
                        tc.Id = "call_" + Guid.NewGuid().ToString("N")[..8];
                    tc.Function.Arguments = LlmJson.NormalizeToolArguments(tc.Function.Arguments);
                }
                msgOut.Content ??= "";
            }

            lock (_historyLock)
            {
                _history.Add(new ChatMessage
                {
                    Role = msgOut.Role,
                    Content = msgOut.Content,
                    ToolCalls = msgOut.ToolCalls
                });
            }

            if (msgOut.ToolCalls == null || msgOut.ToolCalls.Count == 0)
            {
                var text = LlmJson.GetContentString(msgOut.Content);
                if (string.IsNullOrWhiteSpace(text) && choice?.FinishReason == "tool_calls")
                    text = "";
                onTrace(text);
                return text;
            }

            DebugLog.Write("agent.tool_calls", new
            {
                round,
                calls = msgOut.ToolCalls.Select(tc => new
                {
                    tc.Id,
                    tc.Function.Name,
                    args = tc.Function.Arguments,
                    argsValidJson = IsJsonObjectOrArray(tc.Function.Arguments)
                }).ToList()
            });

            foreach (var tc in msgOut.ToolCalls)
            {
                onTrace($"Tool {tc.Function.Name}: {tc.Function.Arguments}");
                string toolResult;
                try
                {
                    toolResult = await toolExecutor(tc.Function.Name, tc.Function.Arguments);
                }
                catch (Exception ex)
                {
                    toolResult = $"Tool error: {ex.Message}";
                }

                onTrace($"Tool {tc.Function.Name} result: {Truncate(toolResult, 400)}");

                lock (_historyLock)
                {
                    _history.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = tc.Id,
                        Name = tc.Function.Name,
                        Content = toolResult
                    });
                    ChatHistoryTrimmer.Trim(_history);
                }
            }
        }

        var abort = "Max tool rounds (64) reached. I stopped to avoid an infinite loop. Say 'continue' to keep going, or press Stop. Partial work may have been applied.";
        lock (_historyLock) _history.Add(new ChatMessage { Role = "assistant", Content = abort });
        return abort;
    }

    public static string BuildSystemPrompt(IReadOnlyList<ToolDefinition> tools)
    {
        var lines = tools.Select(t =>
        {
            var desc = t.Function.Description ?? "";
            var cut = desc.IndexOf('.');
            if (cut > 0 && cut < 160) desc = desc[..(cut + 1)];
            else if (desc.Length > 160) desc = desc[..157] + "...";
            return $"- {t.Function.Name}: {desc}";
        });
        return BaseSystemPrompt + "\n# Tools\n" + string.Join("\n", lines);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private static bool IsJsonObjectOrArray(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            using var d = JsonDocument.Parse(raw);
            return d.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch { return false; }
    }
}
