using System.Text.Json;
using RevitAiPoc.Revit;
using RevitAiPoc.Tools;

namespace RevitAiPoc.Llm;

public sealed record TodoItem(string Id, string Title, string? Tool, string Status, string? Error = null);

public sealed class AgentLoop
{
    private readonly OpenAiCompatibleClient _client;
    private readonly ToolRegistry _registry;
    private readonly List<ChatMessage> _history = new();
    private readonly object _historyLock = new();
    private readonly List<TodoItem> _todos = new();
    private readonly object _todoLock = new();

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
        lock (_todoLock) _todos.Clear();
    }

    public IReadOnlyList<TodoItem> GetTodos() { lock (_todoLock) return _todos.ToList(); }

    public void RestoreHistory(List<ChatMessage> messages)
    {
        lock (_historyLock)
        {
            _history.Clear();
            _history.AddRange(messages);
            if (_history.Count == 0 || _history[0].Role != "system")
                _history.Insert(0, new ChatMessage { Role = "system", Content = BaseSystemPrompt });
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
        => await RunAsync(provider, ChatMessage.FromText("user", userText), null, toolExecutor, onTrace, onDelta, ct);

    public async Task<string> RunAsync(
        ProviderConfig provider,
        ChatMessage userMsg,
        string? mode,
        Func<string, string, Task<string>> toolExecutor,
        Action<string> onTrace,
        Action<string>? onDelta,
        CancellationToken ct = default)
        => await RunAsync(provider, userMsg, mode, toolExecutor, onTrace, onDelta, null, ct);

    public async Task<string> RunAsync(
        ProviderConfig provider,
        ChatMessage userMsg,
        string? mode,
        Func<string, string, Task<string>> toolExecutor,
        Action<string> onTrace,
        Action<string>? onDelta,
        Action<IReadOnlyList<TodoItem>>? onTodo,
        CancellationToken ct = default)
    {
        lock (_historyLock)
        {
            _history.Add(userMsg);
            ChatHistoryTrimmer.Trim(_history);
        }
        var userTextForSelect = LlmJson.GetContentString(userMsg.Content);
        try { HistoryStore.Append("user", userTextForSelect); HistoryStore.Trim(); } catch { }

        var allTools = _registry.GetToolDefinitions();
        if (string.Equals(mode, "ask", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "plan", StringComparison.OrdinalIgnoreCase))
        {
            var l1Names = _registry.Entries
                .Where(e => e.Tier == ToolTier.L1)
                .Select(e => e.Definition.Function.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            allTools = allTools.Where(t => l1Names.Contains(t.Function.Name)).ToList();
        }
        var selected = ToolSelector.Select(allTools, userTextForSelect);
        var tools = selected.Count == 0 ? null : selected;
        var sys = BuildSystemPrompt(selected);
        if (string.Equals(mode, "plan", StringComparison.OrdinalIgnoreCase))
            sys = "PLAN MODE — read-only: use only L1 inspect tools. Do NOT modify the model. End with a numbered plan (1. 2. 3.) listing each step and the tool that would be used. Do not execute writes.\n" + sys;
        else if (string.Equals(mode, "ask", StringComparison.OrdinalIgnoreCase))
            sys = "ASK MODE — read-only: you may use L1 inspect tools to answer. Do not modify the model. Be concise.\n" + sys;
        else if (string.Equals(mode, "build", StringComparison.OrdinalIgnoreCase))
            sys = "BUILD MODE — you may use all tools (L1/L2/L3) to execute the task. Confirm destructive actions via tools.\n" + sys;
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
                // create todos for these tool calls
                lock (_todoLock)
                {
                    foreach (var tc in msgOut.ToolCalls)
                    {
                        var existing = _todos.FirstOrDefault(t => t.Id == tc.Id);
                        if (existing == null)
                            _todos.Add(new TodoItem(tc.Id, tc.Function.Name, tc.Function.Name, "running"));
                        else
                        {
                            var idx = _todos.IndexOf(existing);
                            _todos[idx] = existing with { Status = "running", Tool = tc.Function.Name };
                        }
                    }
                    try { onTodo?.Invoke(_todos.ToList()); } catch { }
                }
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
                // if plan mode, try to create todos from text
                if (string.Equals(mode, "plan", StringComparison.OrdinalIgnoreCase))
                {
                    var draft = PlanParser.TryParse(text);
                    if (draft != null)
                    {
                        lock (_todoLock)
                        {
                            _todos.Clear();
                            foreach (var s in draft.Steps)
                                _todos.Add(new TodoItem(Guid.NewGuid().ToString("N")[..6], s.Title, null, "pending"));
                            try { onTodo?.Invoke(_todos.ToList()); } catch { }
                        }
                    }
                }
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
                string toolResult;
                bool isError = false;
                try
                {
                    toolResult = await toolExecutor(tc.Function.Name, tc.Function.Arguments);
                    // heuristic error detection
                    if (toolResult.StartsWith("Tool error", StringComparison.OrdinalIgnoreCase) || toolResult.Contains("Failed", StringComparison.OrdinalIgnoreCase) && toolResult.Length < 500)
                    {
                        // keep but not mark error unless explicit
                    }
                }
                catch (Exception ex)
                {
                    toolResult = $"Tool error: {ex.Message}";
                    isError = true;
                }

                lock (_todoLock)
                {
                    var existing = _todos.FirstOrDefault(t => t.Id == tc.Id);
                    if (existing != null)
                    {
                        var idx = _todos.IndexOf(existing);
                        _todos[idx] = existing with { Status = isError ? "error" : "done", Error = isError ? toolResult : null };
                        try { onTodo?.Invoke(_todos.ToList()); } catch { }
                    }
                }

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
