# Review Hardening (2026-10-03) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the confirmed control-plane, safety and UX defects from [`docs/FULL_REVIEW_2026-10-03.md`](../../FULL_REVIEW_2026-10-03.md): N1–N16. Each fix is small and needs no ADR.

**Architecture:** No new subsystems. The plan adds guards and seams inside existing types: `AgentLoop` (mode gate), `RevitBridge` (API-context gate, dialog timeout), `OpenAiCompatibleClient` (stream idle timeout and an internal handler seam for tests), `ChatHistoryTrimmer` (pinning), `ChatSession` / `HostPage` (run-state guards), `PluginLoader` (removal guard), the gateway handler (payload allow-list, upstream timeout) and the web pane (small fixes). Behaviour gets tests wherever a seam exists. Revit-only paths get `Source_*` text assertions, per CONTRIBUTING "Source-text assertions".

**Tech Stack:** C# (netstandard2.0 Core, net48/net8+ host), xUnit, Node 22 (`node --test`), React 18 + Vite + vitest.

**Out of scope:** these need their own plan, an ADR, or a protocol change.
- Structured tool events in the WebView protocol (`toolCallId` + status). Fixes the UI "Done on failure" problem and B7.
- Delete cascade *preview* before confirm.
- Gateway ticket *renewal* endpoint (N9). Needs a contract change and an ADR. Task 9 mitigates N9 by raising the default ticket lifetime to 2 hours.
- B4 (refuse unload while busy) and B5 (deterministic discovery).
- Dark theme, mode chip, turn summary card.

**Owner decisions (2026-10-03):**
1. Hiding or undocking the pane keeps today's behaviour: `OnUnloaded` cancels the run, which rolls back the turn's writes. It is the safe default, and nobody has yet observed in Revit whether `Unloaded` fires on hide. Task 8 step 5 records that. Changing it to "keep running when hidden" is a follow-up that depends on the result.
2. Ticket expiry: raise the default lifetime from 30 minutes to 2 hours now (Task 9). A renewal endpoint is a follow-up with an ADR.
3. Plan/Ask blocked write: return a tool error to the model that tells the user to switch to `/build`. No interactive prompt (Task 1 already does this).

**Branch:** run from a fresh branch off `main`:

```bash
git checkout -b fix/review-hardening-2026-10-03
```

**Test commands used throughout:**

```bash
dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true
```

```bash
cd gateway && npm test
```

```bash
cd web && npm test && npm run build
```

The xUnit suite needs Windows with the .NET 10 SDK, which is what CI uses. On Linux, run only the gateway and web suites, and let CI run xUnit on the PR.

---

## File map

| File | Change |
|---|---|
| `src/LoadingAI.Core/Llm/AgentLoop.cs` | Mode gate before tool execution; drop the dead heuristic block |
| `src/LoadingAI.Core/Llm/OpenAiCompatibleClient.cs` | Internal `HttpMessageHandler` ctor; stream idle timeout; cancel disposes the response |
| `src/LoadingAI.Core/Llm/ChatHistoryTrimmer.cs` | Pin the current turn's user message; larger cap for current-turn tool results |
| `src/LoadingAI.Core/Llm/CheckpointStore.cs` | Validate checkpoint ids |
| `src/LoadingAI/Revit/RevitBridge.cs` | Direct path only inside `Execute` or an attached test thread; `UserDialogTimeout` |
| `src/LoadingAI/Ui/ApprovalGate.cs` | Native confirm uses `UserDialogTimeout`; timeout counts as "No" |
| `src/LoadingAI/Tools/ToolGuards.cs` | Generic L2 confirm uses `UserDialogTimeout` |
| `src/LoadingAI/Ui/ChatSession.cs` | Run-state guards; `InitTools` idempotent; save outside the turn group |
| `src/LoadingAI/Ui/HostPage.xaml.cs` | Handler-leak fix; removal guard; run guards on resume/reload |
| `src/LoadingAI/Plugins/Core/PluginLoader.cs` | `CanRemove` |
| `src/LoadingAI/Revit/AdapterRevitBridge.Elements.cs` | Report the real deleted count |
| `gateway/src/index.js` | Field allow-list, `max_tokens` cap, upstream header timeout |
| `web/src/App.tsx`, `web/src/cli/*.ts`, `web/src/lib/scroll.ts` | Run-mode fix, dead slash commands, stick-to-bottom |
| `web/package.json`, `web/src/lib/utils.ts` | Remove dead dependencies and file |
| Tests | `AgentLoopModeTests.cs`, `StreamIdleTimeoutTests.cs`, `ChatHistoryTrimmerTests.cs`, `RevitBridgeContractTests.cs`, `ChatSessionContractTests.cs`, `PluginRemovalTests.cs`, `CheckpointStoreTests.cs`, `gateway/test/gateway.test.js`, `web/src/lib/scroll.test.ts`, `web/src/cli/slashCommands.test.ts` |

---

### Task 1: Enforce Plan/Ask read-only at execution time (N1)

**Files:**
- Modify: `src/LoadingAI.Core/Llm/OpenAiCompatibleClient.cs:174-179` (test seam)
- Modify: `src/LoadingAI.Core/Llm/AgentLoop.cs:278-296`
- Test: `src/LoadingAI.Tests/AgentLoopModeTests.cs` (create)

- [ ] **Step 1: Add the internal handler seam to the client**

In `OpenAiCompatibleClient.cs`, replace the constructor:

```csharp
    public OpenAiCompatibleClient()
    {
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }
```

with:

```csharp
    public OpenAiCompatibleClient()
        : this(new HttpClientHandler())
    {
    }

    /// <summary>Test seam: drive the real client against a scripted handler (InternalsVisibleTo LoadingAI.Tests).</summary>
    internal OpenAiCompatibleClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
```

- [ ] **Step 2: Write the failing test**

Create `src/LoadingAI.Tests/AgentLoopModeTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run it and confirm it fails**

Run: `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true --filter AgentLoopModeTests`
Expected: compile error, because `AgentLoop.ModeAllowsTier` does not exist yet.

- [ ] **Step 4: Implement the gate**

In `AgentLoop.cs`, add this method below `ApplySystemPrompt`:

```csharp
    /// <summary>Plan and Ask are read-only: only L1 tools may execute, whatever the model calls.</summary>
    public static bool ModeAllowsTier(string? mode, ToolTier tier)
        => tier == ToolTier.L1
           || !(string.Equals(mode, "plan", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "ask", StringComparison.OrdinalIgnoreCase));
```

Replace the `try` block inside the tool loop:

```csharp
                try
                {
                    toolResult = await toolExecutor(tc.Function.Name, tc.Function.Arguments);
                    // heuristic error detection
                    if (toolResult.StartsWith("Tool error", StringComparison.OrdinalIgnoreCase) || toolResult.Contains("Failed", StringComparison.OrdinalIgnoreCase) && toolResult.Length < 500)
                    {
                        // keep but not mark error unless explicit
                    }
                }
```

with:

```csharp
                try
                {
                    if (_registry.TryGet(tc.Function.Name, out var gated) && !ModeAllowsTier(mode, gated.Tier))
                        toolResult = $"Tool {tc.Function.Name} is not available in {mode} mode (read-only). Tell the user to switch to /build to run it.";
                    else
                        toolResult = await toolExecutor(tc.Function.Name, tc.Function.Arguments);
                }
```

- [ ] **Step 5: Run the tests and confirm they pass**

Run the same filter. Expected: 8 passed.

- [ ] **Step 6: Run the full suite**

Run the full xUnit command. Expected: 0 failures.

- [ ] **Step 7: Commit**

```bash
git add src/LoadingAI.Core/Llm/AgentLoop.cs src/LoadingAI.Core/Llm/OpenAiCompatibleClient.cs src/LoadingAI.Tests/AgentLoopModeTests.cs
git commit -m "fix: plan and ask modes refuse non-L1 tools at execution"
```

---

### Task 2: Run Revit API calls only inside an API context (N2)

**Files:**
- Modify: `src/LoadingAI/Revit/RevitBridge.cs:7-20, 43-48, 60-79, 299-326`
- Test: `src/LoadingAI.Tests/RevitBridgeContractTests.cs` (append; the file already exists with 8 tests)

- [ ] **Step 1: Write the failing source assertion**

Create `src/LoadingAI.Tests/RevitBridgeContractTests.cs`:

```csharp
using System.IO;

namespace LoadingAI.Tests;

/// <summary>Textual: RevitBridge needs a live Revit to exercise. See CONTRIBUTING "Source-text assertions".</summary>
public class RevitBridgeContractTests
{
    private static string Src() => File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "RevitBridge.cs"));

    [Fact]
    public void Source_DirectPathRequiresApiContextNotJustUiThread()
    {
        var src = Src();
        var start = src.IndexOf("public async Task<T> RunAsync<T>(Func<UIApplication, T> func, TimeSpan timeout", StringComparison.Ordinal);
        var enqueue = src.IndexOf("_handler.Enqueue(", start, StringComparison.Ordinal);
        var gate = src.Substring(start, enqueue - start);
        Assert.Contains("t_inHandler || _attached", gate);
    }

    [Fact]
    public void Source_ExecuteMarksAndClearsTheApiContext()
    {
        var src = Src();
        var exec = src.Substring(src.IndexOf("public void Execute(UIApplication app)", StringComparison.Ordinal));
        Assert.Contains("t_inHandler = true;", exec);
        Assert.Contains("t_inHandler = false;", exec);
    }

    [Fact]
    public void Source_AttachCurrentThreadEnablesDirectPath()
    {
        var src = Src();
        var attach = src.Substring(src.IndexOf("public void AttachCurrentThread", StringComparison.Ordinal), 300);
        Assert.Contains("_attached = true;", attach);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln")))
            dir = dir.Parent!;
        return dir!.FullName;
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

Run with `--filter RevitBridgeContractTests`. Expected: 3 failed.

- [ ] **Step 3: Implement**

In `RevitBridge.cs`, add these fields under `private volatile int _uiThreadId;`:

```csharp
    /// <summary>True only while this thread is inside BridgeHandler.Execute (a valid Revit API context).</summary>
    [ThreadStatic] private static bool t_inHandler;
    /// <summary>Set by in-Revit test hosts that already own the API thread.</summary>
    private volatile bool _attached;
```

In `AttachCurrentThread`, add `_attached = true;` after `_uiThreadId = Environment.CurrentManagedThreadId;`.

Replace the direct-path condition in `RunAsync<T>(…, TimeSpan timeout, …)`:

```csharp
        if (_uiThreadId != 0
            && Environment.CurrentManagedThreadId == _uiThreadId
            && RevitContext.UiApplication is { } current)
```

with:

```csharp
        // Same thread is not enough: a WPF/WebView handler on the UI thread is outside API
        // context. Run inline only when nested inside Execute or on an attached test thread.
        if (_uiThreadId != 0
            && Environment.CurrentManagedThreadId == _uiThreadId
            && (t_inHandler || _attached)
            && RevitContext.UiApplication is { } current)
```

In `BridgeHandler.Execute`, set the flag. Replace:

```csharp
            _owner._executing = true;
            var local = new Queue<Action<UIApplication>>();
```

with:

```csharp
            _owner._executing = true;
            t_inHandler = true;
            var local = new Queue<Action<UIApplication>>();
```

and replace `finally { _owner._executing = false; }` with:

```csharp
            finally
            {
                t_inHandler = false;
                _owner._executing = false;
            }
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run with `--filter RevitBridgeContractTests`, then the full suite. Expected: all pass.

- [ ] **Step 5: Verify in Revit 2027 (manual, required before merge)**

1. Build with `dotnet build src/LoadingAI/LoadingAI.csproj -c Release -p:RevitVersion=2027` and start Revit with a model open.
2. Send "hello". The context line should show the selection and view (ContextCollector now goes through ExternalEvent).
3. Ask for `create_formwork_panels` (it has a PickElements preset), click **Pick**, pick two walls, Finish. The field should show "2 elements".
4. Record the result in the PR description.

- [ ] **Step 6: Commit**

```bash
git add src/LoadingAI/Revit/RevitBridge.cs src/LoadingAI.Tests/RevitBridgeContractTests.cs
git commit -m "fix: bridge runs inline only inside a Revit API context"
```

---

### Task 3: Idle timeout and cancellable streaming (N3)

**Files:**
- Modify: `src/LoadingAI.Core/Llm/OpenAiCompatibleClient.cs` (`ChatStreamAsync`, around `:594-650`)
- Test: `src/LoadingAI.Tests/StreamIdleTimeoutTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `src/LoadingAI.Tests/StreamIdleTimeoutTests.cs`:

```csharp
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class StreamIdleTimeoutTests
{
    /// <summary>A body that never yields a byte, like a provider that stalls mid-stream.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Stalls every streaming request; answers plain JSON for the non-stream fallback.</summary>
    private sealed class StallThenJsonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var streaming = request.Headers.Accept.Any(a => a.MediaType == "text/event-stream");
            HttpContent content = streaming
                ? new StreamContent(new StalledStream())
                : new StringContent("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""", Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public async Task ReadLine_ThrowsTimeoutWhenNothingArrives()
    {
        using var reader = new StreamReader(new StalledStream());
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            OpenAiCompatibleClient.ReadLineWithIdleTimeoutAsync(reader, TimeSpan.FromMilliseconds(200), CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReadLine_StopsPromptlyOnCancel()
    {
        using var reader = new StreamReader(new StalledStream());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OpenAiCompatibleClient.ReadLineWithIdleTimeoutAsync(reader, TimeSpan.FromMinutes(5), cts.Token));
    }

    [Fact]
    public async Task StalledStream_FallsBackInsteadOfHanging()
    {
        using var client = new OpenAiCompatibleClient(new StallThenJsonHandler()) { StreamIdleTimeout = TimeSpan.FromMilliseconds(300) };
        var provider = new ProviderConfig { Id = "fake", Name = "Fake", BaseUrl = "http://fake.test/v1", Model = "m", ApiKey = "k" };
        var call = client.ChatAsync(provider, new List<ChatMessage> { ChatMessage.FromText("user", "hi") }, null, _ => { });

        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(call, finished);
        Assert.Equal("ok", LlmJson.GetContentString((await call).Choices![0].Message!.Content));
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

Run with `--filter StreamIdleTimeoutTests`. Expected: compile error, because `ReadLineWithIdleTimeoutAsync` and `StreamIdleTimeout` do not exist yet.

- [ ] **Step 3: Implement**

In `OpenAiCompatibleClient.cs`, add these members after the constructors:

```csharp
    /// <summary>Longest silence tolerated between two stream lines before the call fails.</summary>
    internal TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// netstandard2.0 ReadLineAsync takes no token, so a stalled provider would hang Stop forever.
    /// Races the read against the idle timeout and the caller's token.
    /// </summary>
    internal static async Task<string?> ReadLineWithIdleTimeoutAsync(StreamReader reader, TimeSpan idle, CancellationToken ct)
    {
        var read = reader.ReadLineAsync();
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(idle, delayCts.Token);
        var done = await Task.WhenAny(read, delay).ConfigureAwait(false);
        if (done == read)
        {
            delayCts.Cancel();
            return await read.ConfigureAwait(false);
        }
        // The abandoned read faults once the response is disposed; observe it so it is not unobserved.
        _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        ct.ThrowIfCancellationRequested();
        throw new TimeoutException($"Provider stream sent nothing for {idle.TotalSeconds:0}s.");
    }
```

In `ChatStreamAsync`, after `using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);`, add:

```csharp
        // Stop must abort a body read that is already in flight.
        using var abortOnCancel = ct.Register(() => { try { resp.Dispose(); } catch { } });
```

Replace the loop head:

```csharp
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync();
```

with:

```csharp
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await ReadLineWithIdleTimeoutAsync(reader, StreamIdleTimeout, ct);
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run with `--filter StreamIdleTimeoutTests`, then the full suite (`GatewayFlowTests` exercises the real stream path). Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/LoadingAI.Core/Llm/OpenAiCompatibleClient.cs src/LoadingAI.Tests/StreamIdleTimeoutTests.cs
git commit -m "fix: stalled provider streams time out and Stop aborts them"
```

---

### Task 4: Keep the turn's request and the latest tool output in history (N4)

**Files:**
- Modify: `src/LoadingAI.Core/Llm/ChatHistoryTrimmer.cs`
- Test: `src/LoadingAI.Tests/ChatHistoryTrimmerTests.cs` (append)

- [ ] **Step 1: Write the failing tests**

Append inside `ChatHistoryTrimmerTests`:

```csharp
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
```

- [ ] **Step 2: Run them and confirm they fail**

Run with `--filter ChatHistoryTrimmerTests`. Expected: both new tests fail. The three existing tests pass.

- [ ] **Step 3: Implement**

Replace the body of `ChatHistoryTrimmer`:

```csharp
public static class ChatHistoryTrimmer
{
    public const int KeepMessages = 40;
    public const int MaxToolResultChars = 2000;
    /// <summary>Tool results the model has not answered yet (after the last user message) keep more.</summary>
    public const int MaxCurrentTurnToolResultChars = 8000;

    public static void Trim(List<ChatMessage> history)
    {
        if (history.Count == 0) return;

        var lastUser = history.FindLastIndex(m => m.Role == "user");
        for (int i = 0; i < history.Count; i++)
        {
            var msg = history[i];
            if (msg.Role != "tool") continue;
            var cap = lastUser >= 0 && i > lastUser ? MaxCurrentTurnToolResultChars : MaxToolResultChars;
            var text = LlmJson.GetContentString(msg.Content);
            if (text.Length > cap)
                msg.Content = text.Substring(0, cap) + "\n[omitted]";
        }

        var lastMulti = -1;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == "user" && LlmJson.IsMultimodal(history[i].Content))
            {
                lastMulti = i;
                break;
            }
        }
        for (int i = 0; i < history.Count; i++)
        {
            if (i == lastMulti) continue;
            if (LlmJson.IsMultimodal(history[i].Content))
                history[i].Content = LlmJson.StripImages(history[i].Content);
        }

        if (history.Count <= KeepMessages + 1) return;

        var hasSystem = history[0].Role == "system";
        int keepFrom = history.Count - KeepMessages;
        while (keepFrom < history.Count && history[keepFrom].Role == "tool")
            keepFrom++;

        // A long turn must not lose the instruction it is executing.
        var pinned = lastUser >= (hasSystem ? 1 : 0) && lastUser < keepFrom ? history[lastUser] : null;
        var system = hasSystem ? history[0] : null;
        var tail = history.Skip(keepFrom).ToList();
        history.Clear();
        if (system != null) history.Add(system);
        if (pinned != null) history.Add(pinned);
        history.AddRange(tail);
    }
}
```

The old no-system branch did not skip leading `tool` messages, so a cut could leave orphaned tool results. The shared skip above fixes that too.

- [ ] **Step 4: Run the tests and confirm they pass**

Run with `--filter ChatHistoryTrimmerTests` (5 pass), then the full suite.

- [ ] **Step 5: Commit**

```bash
git add src/LoadingAI.Core/Llm/ChatHistoryTrimmer.cs src/LoadingAI.Tests/ChatHistoryTrimmerTests.cs
git commit -m "fix: history trim keeps the current request and fresh tool output"
```

---

### Task 5: Confirm dialogs get a dialog-length timeout (N5)

**Files:**
- Modify: `src/LoadingAI/Revit/RevitBridge.cs` (constant)
- Modify: `src/LoadingAI/Ui/ApprovalGate.cs:155-170`
- Modify: `src/LoadingAI/Tools/ToolGuards.cs:29-38, 48-57`
- Test: `src/LoadingAI.Tests/RevitBridgeContractTests.cs` (append)

- [ ] **Step 1: Write the failing source assertions**

Append to `RevitBridgeContractTests`:

```csharp
    [Fact]
    public void Source_ConfirmDialogsUseUserDialogTimeout()
    {
        var gate = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ApprovalGate.cs"));
        var confirm = gate.Substring(gate.IndexOf("public async Task<bool> ConfirmL3NativeAsync", StringComparison.Ordinal), 1400);
        Assert.Contains("RevitBridge.UserDialogTimeout", confirm);
        Assert.Contains("catch (BridgeOperationUnknownException", confirm);

        var guards = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "ToolGuards.cs"));
        Assert.Equal(2, guards.Split(new[] { "RevitBridge.UserDialogTimeout" }, StringSplitOptions.None).Length - 1);
    }
```

- [ ] **Step 2: Run it and confirm it fails**

Run with `--filter Source_ConfirmDialogsUseUserDialogTimeout`. Expected: fail.

- [ ] **Step 3: Implement**

In `RevitBridge.cs`, under `DefaultTimeout`:

```csharp
    /// <summary>Waits on a human (TaskDialog). Matches the 15-minute preset form timeout.</summary>
    public static readonly TimeSpan UserDialogTimeout = TimeSpan.FromMinutes(15);
```

In `ApprovalGate.cs`, replace `ConfirmL3NativeAsync` with:

```csharp
    public async Task<bool> ConfirmL3NativeAsync(string name, string argsJson)
    {
        var raw = App.Bridge;
        if (raw == null) return false;
        try
        {
            return await raw.RunAsync(_ =>
            {
                var td = new TaskDialog("LoadingAI for Revit")
                {
                    MainInstruction = $"Confirm L3: {name}?",
                    MainContent = WebHostBridge.Truncate(argsJson, 400) + "\n\nUndoable transaction. Default No.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No
                };
                return td.Show() == TaskDialogResult.Yes;
            }, RevitBridge.UserDialogTimeout);
        }
        catch (BridgeOperationUnknownException ex)
        {
            // An unanswered confirm is a No: never run a write the engineer did not accept.
            HostLog.Write($"Confirm L3 {name} not answered: {ex.Message}");
            return false;
        }
    }
```

In `ToolGuards.cs`, change both dialog calls so the closing of each `RunAsync` lambda passes the timeout. Replace `bool ok = await bridge.RunAsync(uiApp =>` … `});` with `bool ok = await bridge.RunAsync(uiApp =>` … `}, RevitBridge.UserDialogTimeout);` in **both** the `Handler` and the `StructuredHandler` wrappers. The lambda bodies stay as they are.

- [ ] **Step 4: Run the tests and confirm they pass**

Run the filter, then the full suite (`L3CommandContractTests` covers the gate order).

- [ ] **Step 5: Commit**

```bash
git add src/LoadingAI/Revit/RevitBridge.cs src/LoadingAI/Ui/ApprovalGate.cs src/LoadingAI/Tools/ToolGuards.cs src/LoadingAI.Tests/RevitBridgeContractTests.cs
git commit -m "fix: write confirms wait 15 minutes and treat no answer as No"
```

---

### Task 6: Plugin removal guard (N6)

**Files:**
- Modify: `src/LoadingAI/Plugins/Core/PluginLoader.cs` (add `CorePluginId`, `CanRemove`)
- Modify: `src/LoadingAI/Ui/HostPage.xaml.cs:411-435`
- Test: `src/LoadingAI.Tests/PluginRemovalTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `src/LoadingAI.Tests/PluginRemovalTests.cs`:

```csharp
using System.IO;
using LoadingAI.Plugins.Core;

namespace LoadingAI.Tests;

public class PluginRemovalTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "lai-remove-root");
    private static readonly string[] Roots = { Root };

    [Fact]
    public void AllowsADirectChildOfAPluginRoot()
        => Assert.True(PluginLoader.CanRemove("hello", Path.Combine(Root, "hello"), Roots, out _));

    [Fact]
    public void RefusesTheCorePlugin()
    {
        Assert.False(PluginLoader.CanRemove("core-tools", Path.Combine(Root, "core-tools"), Roots, out var why));
        Assert.Contains("core", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesThePluginRootItself_LooseManifestCase()
    {
        Assert.False(PluginLoader.CanRemove("loose", Root, Roots, out var why));
        Assert.Contains("root", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesTrailingSeparatorOnTheRoot()
        => Assert.False(PluginLoader.CanRemove("loose", Root + Path.DirectorySeparatorChar, Roots, out _));

    [Theory]
    [InlineData("nested")]
    [InlineData("outside")]
    [InlineData("")]
    public void RefusesAnythingElse(string kind)
    {
        var folder = kind switch
        {
            "nested" => Path.Combine(Root, "a", "b"),
            "outside" => Path.Combine(Path.GetTempPath(), "somewhere-else"),
            _ => ""
        };
        Assert.False(PluginLoader.CanRemove("x", folder, Roots, out _));
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

Run with `--filter PluginRemovalTests`. Expected: compile error, because `CanRemove` does not exist yet.

- [ ] **Step 3: Implement `CanRemove`**

In `PluginLoader.cs`, add after `AllowlistRefusalHint`:

```csharp
    /// <summary>Shipped core plugin id (Plugins/core-tools/plugin.json). Never removable from Settings.</summary>
    public const string CorePluginId = "core-tools";

    /// <summary>
    /// Settings "Remove" may delete only a plugin's own folder: a direct child of a scanned root.
    /// A loose manifest's FolderPath is the root itself, and deleting that would wipe every plugin.
    /// </summary>
    public static bool CanRemove(string pluginId, string? folderPath, out string reason)
        => CanRemove(pluginId, folderPath, new[] { RuntimePluginsDir, DevPluginsDir }, out reason);

    internal static bool CanRemove(string pluginId, string? folderPath, IReadOnlyList<string> roots, out string reason)
    {
        reason = "";
        if (string.Equals(pluginId, CorePluginId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Cannot remove the core plugin.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            reason = $"Plugin {pluginId} has no folder.";
            return false;
        }

        string full;
        try { full = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex)
        {
            reason = $"Bad plugin folder {folderPath}: {ex.Message}";
            return false;
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, r, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"{full} is the plugin root, not a plugin folder. Remove that plugin's files by hand.";
                return false;
            }
            if (string.Equals(Path.GetDirectoryName(full), r, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        reason = $"{full} is not a plugin folder under {string.Join(" or ", roots)}.";
        return false;
    }
```

- [ ] **Step 4: Use it in HostPage**

Replace the whole `case ToHostMessage.RemovePlugin` block in `HostPage.xaml.cs` with:

```csharp
                case ToHostMessage.RemovePlugin { PluginId: var pluginId }:
                    {
                        var plugin = string.IsNullOrWhiteSpace(pluginId) ? null : _session.FindPlugin(pluginId);
                        if (plugin == null)
                        {
                            PostToWeb(new FromHostMessage.Error($"Plugin {pluginId} not found."));
                            break;
                        }
                        if (!PluginLoader.CanRemove(plugin.Meta.Id, plugin.Meta.FolderPath, out var refusal))
                        {
                            PostToWeb(new FromHostMessage.Error(refusal));
                            break;
                        }
                        if (!Directory.Exists(plugin.Meta.FolderPath))
                        {
                            PostToWeb(new FromHostMessage.Error($"Plugin folder not found: {plugin.Meta.FolderPath}"));
                            break;
                        }
                        var td = new TaskDialog("LoadingAI for Revit")
                        {
                            MainInstruction = $"Remove plugin {plugin.Meta.Id}?",
                            MainContent = plugin.Meta.FolderPath,
                            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                            DefaultButton = TaskDialogResult.No
                        };
                        if (td.Show() != TaskDialogResult.Yes)
                        {
                            PostToWeb(new FromHostMessage.Status("Remove cancelled."));
                            break;
                        }
                        try { Directory.Delete(plugin.Meta.FolderPath, true); PostToWeb(new FromHostMessage.Status($"Removed {plugin.Meta.Id}. Restart Revit to unload.")); }
                        catch (Exception ex) { PostToWeb(new FromHostMessage.Error($"Remove failed: {ex.Message}. Close Revit and delete manually: {plugin.Meta.FolderPath}")); }
                        break;
                    }
```

- [ ] **Step 5: Run the tests and confirm they pass**

Run with `--filter PluginRemovalTests` (7 pass), then the full suite.

- [ ] **Step 6: Commit**

```bash
git add src/LoadingAI/Plugins/Core/PluginLoader.cs src/LoadingAI/Ui/HostPage.xaml.cs src/LoadingAI.Tests/PluginRemovalTests.cs
git commit -m "fix: plugin remove refuses core-tools and plugin root folders"
```

---

### Task 7: Run-state guards and checkpoint id validation (N7, N13)

**Files:**
- Modify: `src/LoadingAI/Ui/ChatSession.cs` (`HandleSendAsync`, `TryReloadPlugins`)
- Modify: `src/LoadingAI/Ui/HostPage.xaml.cs` (`ResumeCheckpoint`, `ReloadPlugins` cases)
- Modify: `src/LoadingAI.Core/Llm/CheckpointStore.cs`
- Test: `src/LoadingAI.Tests/ChatSessionContractTests.cs` (append), `src/LoadingAI.Tests/CheckpointStoreTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Append to `ChatSessionContractTests`:

```csharp
    private static string Session() => File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));

    [Fact]
    public void Source_RunTaskIsNotTiedToTheCancelToken()
    {
        // Task.Run(..., ct) never runs its delegate if ct is already cancelled, so the
        // finally that clears _isRunning never runs and the pane is stuck "Already working".
        Assert.DoesNotContain("}, ct);", Session());
    }

    [Fact]
    public void Source_CancelTokenExistsBeforeQuotaWait()
    {
        var src = Session();
        var start = src.IndexOf("public async Task HandleSendAsync", StringComparison.Ordinal);
        var cts = src.IndexOf("_cts = new CancellationTokenSource();", start, StringComparison.Ordinal);
        var quota = src.IndexOf("ConsumeBuiltinQuotaAsync", start, StringComparison.Ordinal);
        Assert.True(cts >= 0 && cts < quota);
    }

    [Fact]
    public void Source_LastUserTextSetOnlyForAcceptedSend()
    {
        var src = Session();
        var start = src.IndexOf("public async Task HandleSendAsync", StringComparison.Ordinal);
        var guard = src.IndexOf("if (_isRunning)", start, StringComparison.Ordinal);
        var assign = src.IndexOf("_lastUserText = text;", start, StringComparison.Ordinal);
        Assert.True(guard >= 0 && assign > guard);
    }

    [Fact]
    public void Source_ReloadRefusedWhileRunning()
    {
        var src = Session();
        var reload = src.Substring(src.IndexOf("public bool TryReloadPlugins", StringComparison.Ordinal), 500);
        Assert.Contains("if (_isRunning)", reload);
    }
```

Create `src/LoadingAI.Tests/CheckpointStoreTests.cs`:

```csharp
using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class CheckpointStoreTests
{
    [Theory]
    [InlineData("20261003120000_ab12", true)]
    [InlineData("20261003120000", true)]
    [InlineData("..\\..\\settings", false)]
    [InlineData("../x", false)]
    [InlineData("a*", false)]
    [InlineData("", false)]
    public void IsValidId(string id, bool ok) => Assert.Equal(ok, CheckpointStore.IsValidId(id));

    [Fact]
    public void Load_RefusesTraversal() => Assert.Null(CheckpointStore.Load("..\\..\\settings"));
}
```

- [ ] **Step 2: Run them and confirm they fail**

Run with `--filter "ChatSessionContractTests|CheckpointStoreTests"`. Expected: the new tests fail or do not compile.

- [ ] **Step 3: Implement CheckpointStore validation**

In `CheckpointStore.cs`, add `using System.Text.RegularExpressions;` and inside the class:

```csharp
    private static readonly Regex IdPattern = new("^[0-9A-Za-z_]{1,64}$");

    /// <summary>Checkpoint ids are timestamp + suffix. Anything else could walk out of the folder.</summary>
    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && IdPattern.IsMatch(id);
```

At the top of `Load`, inside the `try`, add:

```csharp
            if (!IsValidId(id)) return null;
```

- [ ] **Step 4: Implement the ChatSession guards**

In `HandleSendAsync`:

1. In the `clear` branch, before `_cts?.Cancel();`, add:

```csharp
                if (_isRunning) { _web.Post(new FromHostMessage.Error("Stop the current run before /clear.")); return; }
```

2. At the start of the `resume` branch, add:

```csharp
                if (_isRunning) { _web.Post(new FromHostMessage.Error("Stop the current run before /resume.")); return; }
```

3. Move `_lastUserText = text;` from before `if (_isRunning)` to directly after `_isRunning = true;`.

4. Replace:

```csharp
        _isRunning = true;

        if (DefaultLlm.IsBuiltin(provider.Id))
```

with:

```csharp
        _isRunning = true;
        _lastUserText = text;
        // Created before the quota wait so Stop during "Checking allowance" is not lost.
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        if (DefaultLlm.IsBuiltin(provider.Id))
```

5. In the quota branch, after the `if (!quota.Ok) { … }` block, add:

```csharp
            if (ct.IsCancellationRequested)
            {
                BuiltinQuota.ClearTicket();
                _isRunning = false;
                _web.Post(new FromHostMessage.Error("Cancelled."));
                return;
            }
```

6. Delete the now-duplicate lines that follow the quota block:

```csharp
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
```

7. Change the end of the `Task.Run` call from `}, ct);` to `});`.

In `TryReloadPlugins`, add after the `bridge == null` check:

```csharp
        if (_isRunning)
        {
            message = "Stop the current run before reloading plugins.";
            return false;
        }
```

- [ ] **Step 5: Implement the HostPage guards**

At the top of the `ResumeCheckpoint` case in `HostPage.xaml.cs`, add:

```csharp
                    if (_session.IsRunning) { PostToWeb(new FromHostMessage.Error("Stop the current run before resuming a checkpoint.")); break; }
```

Replace the `ReloadPlugins` case with:

```csharp
                case ToHostMessage.ReloadPlugins:
                    if (_session.TryReloadPlugins(out var reloadMsg))
                        PostPlugins(reloadMsg);
                    else
                        PostToWeb(new FromHostMessage.Status(reloadMsg));
                    break;
```

- [ ] **Step 6: Run the tests and confirm they pass**

Run the filter, then the full suite.

- [ ] **Step 7: Commit**

```bash
git add src/LoadingAI/Ui/ChatSession.cs src/LoadingAI/Ui/HostPage.xaml.cs src/LoadingAI.Core/Llm/CheckpointStore.cs src/LoadingAI.Tests/ChatSessionContractTests.cs src/LoadingAI.Tests/CheckpointStoreTests.cs
git commit -m "fix: run state survives early Stop; no history rewrite or reload mid-run"
```

---

### Task 8: Pane reload must not leak handlers or re-discover plugins (N8)

**Files:**
- Modify: `src/LoadingAI/Ui/HostPage.xaml.cs:75, 98-111`
- Modify: `src/LoadingAI/Ui/ChatSession.cs` (`InitTools`)
- Test: `src/LoadingAI.Tests/ChatSessionContractTests.cs` (append)

- [ ] **Step 1: Write the failing source assertions**

Append to `ChatSessionContractTests`:

```csharp
    [Fact]
    public void Source_HostPageUnsubscribesTheControlNavigationEvent()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "HostPage.xaml.cs"));
        var unloaded = src.Substring(src.IndexOf("private void OnUnloaded", StringComparison.Ordinal), 700);
        Assert.Contains("Web.NavigationCompleted -= OnNavigationCompleted;", unloaded);
    }

    [Fact]
    public void Source_InitToolsIsIdempotent()
    {
        var src = Session();
        var init = src.Substring(src.IndexOf("public void InitTools()", StringComparison.Ordinal), 400);
        Assert.Contains("if (_agent != null && _discovered.Count > 0) return;", init);
    }
```

- [ ] **Step 2: Run them and confirm they fail**

Run with `--filter "Source_HostPageUnsubscribes|Source_InitToolsIsIdempotent"`. Expected: 2 failed.

- [ ] **Step 3: Implement**

In `ChatSession.InitTools`, add this as the first line:

```csharp
        // The pane can raise Loaded again after Unloaded; re-discovery would unload live plugin contexts.
        if (_agent != null && _discovered.Count > 0) return;
```

In `HostPage.OnLoaded`, replace `Web.NavigationCompleted += OnNavigationCompleted;` with:

```csharp
            Web.NavigationCompleted -= OnNavigationCompleted;
            Web.NavigationCompleted += OnNavigationCompleted;
```

In `HostPage.OnUnloaded`, inside the `try` and before `if (Web.CoreWebView2 != null)`, add:

```csharp
            Web.NavigationCompleted -= OnNavigationCompleted;
```

Delete the `Web.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;` line. It unsubscribed a different event that was never subscribed.

- [ ] **Step 4: Run the tests and confirm they pass**

Run the filter, then the full suite.

- [ ] **Step 5: Verify in Revit 2027 (manual)**

Open the pane, then hide, re-dock and float it three times. Open Settings. The plugin list should appear once, and `%AppData%\LoadingAI\plugins.log` should show no new discovery lines. Record in the PR whether `Unloaded` fired. That settles whether hiding the pane cancels a run today (owner decision 1: keep that behaviour for now).

- [ ] **Step 6: Commit**

```bash
git add src/LoadingAI/Ui/HostPage.xaml.cs src/LoadingAI/Ui/ChatSession.cs src/LoadingAI.Tests/ChatSessionContractTests.cs
git commit -m "fix: pane reload keeps one navigation handler and live plugins"
```

---

### Task 9: Gateway forwards only known fields and times out upstream (N10)

**Files:**
- Modify: `gateway/src/index.js:184-209`
- Test: `gateway/test/gateway.test.js` (append)

- [ ] **Step 1: Write the failing tests**

Append to `gateway/test/gateway.test.js`:

```js
test('chat forwards only allow-listed fields and caps max_tokens', async (t) => {
  const env = makeEnv();
  const ticket = await issueTicket(env);
  const calls = stubUpstream(t);

  const res = await worker.fetch(
    chatRequest(
      { 'x-loadingai-device': DEVICE, 'x-loadingai-ticket': ticket },
      { messages: [{ role: 'user', content: 'hi' }], stream: true, temperature: 0.2, n: 8, max_tokens: 999999, model: 'expensive' },
    ),
    env,
  );

  assert.equal(res.status, 200);
  const forwarded = JSON.parse(calls[0].init.body);
  assert.equal(forwarded.n, undefined);
  assert.equal(forwarded.max_tokens, 8192);
  assert.equal(forwarded.model, 'MiniMax-M3.1-Flash-Preview');
  assert.equal(forwarded.stream, true);
  assert.equal(forwarded.temperature, 0.2);
});

test('chat leaves max_tokens unset when the client does not ask', async (t) => {
  const env = makeEnv();
  const ticket = await issueTicket(env);
  const calls = stubUpstream(t);
  await worker.fetch(chatRequest({ 'x-loadingai-device': DEVICE, 'x-loadingai-ticket': ticket }), env);
  assert.equal(JSON.parse(calls[0].init.body).max_tokens, undefined);
});

test('chat answers 504 when upstream sends no headers in time', async (t) => {
  const env = makeEnv({ UPSTREAM_TIMEOUT_MS: '20' });
  const ticket = await issueTicket(env);
  const original = globalThis.fetch;
  t.after(() => {
    globalThis.fetch = original;
  });
  globalThis.fetch = (url, init) =>
    new Promise((_, reject) => {
      init.signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
    });

  const res = await worker.fetch(chatRequest({ 'x-loadingai-device': DEVICE, 'x-loadingai-ticket': ticket }), env);
  assert.equal(res.status, 504);
  assert.equal((await res.json()).error.code, 'upstream_timeout');
});
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `cd gateway && npm test`
Expected: 3 new failures. `n` is forwarded, `max_tokens` is 999999, and there is no 504.

- [ ] **Step 3: Implement**

In `gateway/src/index.js`, add below `DEFAULT_CLIENT_TOKEN`:

```js
/** The only request fields the add-in sends; anything else never reaches the paid upstream. */
const FORWARDED_FIELDS = ['messages', 'tools', 'tool_choice', 'temperature', 'stream'];
const DEFAULT_MAX_OUTPUT_TOKENS = 8192;
const DEFAULT_UPSTREAM_TIMEOUT_MS = 120_000;

function positiveInt(raw, fallback) {
  const parsed = Number.parseInt(raw ?? '', 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

export function upstreamBody(payload, env) {
  const body = {};
  for (const key of FORWARDED_FIELDS) {
    if (payload[key] !== undefined) body[key] = payload[key];
  }
  const asked = Number(payload.max_tokens);
  if (Number.isFinite(asked) && asked > 0) {
    body.max_tokens = Math.min(asked, positiveInt(env.MAX_OUTPUT_TOKENS, DEFAULT_MAX_OUTPUT_TOKENS));
  }
  body.model = env.MINIMAX_MODEL || 'MiniMax-M3.1-Flash-Preview';
  return body;
}
```

Replace the upstream call in `handleChat`:

```js
  const base = (env.MINIMAX_BASE_URL || 'https://api.minimaxi.com/v1').replace(/\/+$/, '');
  const upstream = await fetch(`${base}/chat/completions`, {
    method: 'POST',
    headers: {
      authorization: `Bearer ${apiKey}`,
      'content-type': 'application/json',
      accept: request.headers.get('accept') || 'application/json',
    },
    body: JSON.stringify({ ...payload, model: env.MINIMAX_MODEL || 'MiniMax-M3.1-Flash-Preview' }),
  });
```

with:

```js
  const base = (env.MINIMAX_BASE_URL || 'https://api.minimaxi.com/v1').replace(/\/+$/, '');
  // Bounds the wait for response headers only; a long stream that is flowing is never cut.
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), positiveInt(env.UPSTREAM_TIMEOUT_MS, DEFAULT_UPSTREAM_TIMEOUT_MS));
  let upstream;
  try {
    upstream = await fetch(`${base}/chat/completions`, {
      method: 'POST',
      headers: {
        authorization: `Bearer ${apiKey}`,
        'content-type': 'application/json',
        accept: request.headers.get('accept') || 'application/json',
      },
      body: JSON.stringify(upstreamBody(payload, env)),
      signal: controller.signal,
    });
  } catch (error) {
    if (controller.signal.aborted) {
      return fail(504, 'The model did not answer in time. Send your message again.', 'upstream_timeout');
    }
    throw error;
  } finally {
    clearTimeout(timer);
  }
```

Add `MAX_OUTPUT_TOKENS` and `UPSTREAM_TIMEOUT_MS` (both optional) with one-line comments to `gateway/deploy/env.example` and to the env table in `gateway/README.md`.

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `cd gateway && npm test`
Expected: 52 passed, 0 failed.

- [ ] **Step 4b: Raise the default ticket lifetime to 2 hours (N9 mitigation)**

In `gateway/src/quota.js`, change `export const DEFAULT_TICKET_TTL_SECONDS = 60 * 30;` to `export const DEFAULT_TICKET_TTL_SECONDS = 60 * 60 * 2;`. In `gateway/deploy/env.example` set `TICKET_TTL_SECONDS=7200`. In `gateway/README.md` change the `TICKET_TTL_SECONDS` default from `1800` to `7200`, and update any prose that says 30 minutes. Re-run `cd gateway && npm test`. Expected: 52 passed.

- [ ] **Step 5: Commit**

```bash
git add gateway/src/index.js gateway/src/quota.js gateway/test/gateway.test.js gateway/deploy/env.example gateway/README.md
git commit -m "fix(gateway): forward only known fields, cap max_tokens, time out upstream"
```

Deploy follows `gateway/README.md` and is the owner's call. This plan does not deploy.

---

### Task 10: `delete_elements` reports what was actually deleted (N11)

**Files:**
- Modify: `src/LoadingAI/Revit/AdapterRevitBridge.Elements.cs:33-38`
- Test: `src/LoadingAI.Tests/AdapterContractTests.cs` (append)

- [ ] **Step 1: Check for tests pinned to the old text**

Run: `grep -rn "element(s). Undo" src/LoadingAI.Tests Plugins`
Expected: no hit in an Adapter source assertion. If one exists, update it in Step 3 to the new wording.

- [ ] **Step 2: Write the failing source assertion**

Append inside the `AdapterContractTests` class. It already reads partials through `AdapterSource`; see CONTRIBUTING rule 2.

```csharp
    [Fact]
    public void Source_DeleteReportsDependentsFromDocDelete()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> DeleteElementsAsync");
        Assert.Contains("var deleted = doc.Delete(ids);", src);
        Assert.Contains("dependent element(s)", src);
    }
```

`AdapterSource.Containing(marker)` returns the text of the partial file that contains the marker.

- [ ] **Step 3: Implement**

Replace:

```csharp
                var ids = elementIds.Select(AllTools.ParseEid).ToList();
                doc.Delete(ids);
                tx.Commit();
                return new CommandResultDto(true, $"Deleted {ids.Count} element(s). Undo with Ctrl+Z.", ids.Count);
```

with:

```csharp
                var ids = elementIds.Select(AllTools.ParseEid).ToList();
                // Revit also deletes dependents (hosted doors, tags, dimensions); report the real total.
                var deleted = doc.Delete(ids);
                tx.Commit();
                var total = deleted?.Count ?? ids.Count;
                var dependents = total - ids.Count;
                var text = dependents > 0
                    ? $"Deleted {ids.Count} requested element(s) and {dependents} dependent element(s) ({total} total). Undo with Ctrl+Z."
                    : $"Deleted {ids.Count} element(s). Undo with Ctrl+Z.";
                return new CommandResultDto(true, text, total);
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run with `--filter AdapterContractTests`, then the full suite.

- [ ] **Step 5: Commit**

```bash
git add src/LoadingAI/Revit/AdapterRevitBridge.Elements.cs src/LoadingAI.Tests/AdapterContractTests.cs
git commit -m "fix: delete_elements reports dependents Revit removed"
```

---

### Task 11: `save_document` outside the turn group (N12): verify first

**Files:**
- Modify: `src/LoadingAI/Ui/ChatSession.cs` (`ExecuteToolAsync`, the `isWrite` block)
- Test: `src/LoadingAI.Tests/ChatSessionContractTests.cs` (append)

- [ ] **Step 1: Reproduce in Revit 2027 (manual, required)**

1. Enable L3 tools. In one message, ask: "Set comments on the selected wall to TEST, then save the document."
2. Approve both confirms.
3. Record which happens:
   - **(a)** `save_document` returns `Failed: …` mentioning a transaction (group) → the bug is confirmed. Continue with Step 2.
   - **(b)** The save succeeds → reopen the file without the in-memory undo, and check whether the comment is on disk. Then repeat with a third step that fails (e.g. ask to delete id `1`). The turn rolls back. If the disk file still has the comment while the model in memory does not, the divergence is confirmed. Continue with Step 2.
   - **(c)** Neither → stop here. Mark N12 "not reproduced" in `docs/FULL_REVIEW_2026-10-03.md` and skip this task.

- [ ] **Step 2: Write the failing source assertion**

Append to `ChatSessionContractTests`:

```csharp
    [Fact]
    public void Source_SaveClosesTheTurnGroupFirst()
    {
        var src = Session();
        var start = src.IndexOf("public async Task<string> ExecuteToolAsync", StringComparison.Ordinal);
        var boundary = src.IndexOf("TurnBoundaryTools.Contains(name)", start, StringComparison.Ordinal);
        var ensure = src.IndexOf("EnsureTurnGroupAsync", start, StringComparison.Ordinal);
        Assert.True(boundary >= 0 && boundary < ensure);
    }
```

- [ ] **Step 3: Run it and confirm it fails**

Run with `--filter Source_SaveClosesTheTurnGroupFirst`. Expected: fail.

- [ ] **Step 4: Implement**

Add a field to `ChatSession`:

```csharp
    /// <summary>Tools whose effect leaves the model (written to disk) and so cannot sit inside a rollback-able turn group.</summary>
    private static readonly HashSet<string> TurnBoundaryTools = new(StringComparer.OrdinalIgnoreCase) { "save_document" };
```

Replace:

```csharp
        if (isWrite)
        {
            var bridge = App.Bridge;
            if (bridge == null)
                return "Please open a model first.";
            await bridge.EnsureTurnGroupAsync(_cts?.Token ?? default);
        }
```

with:

```csharp
        if (isWrite)
        {
            var bridge = App.Bridge;
            if (bridge == null)
                return "Please open a model first.";
            if (TurnBoundaryTools.Contains(name))
            {
                // Saving inside the group would write changes a later rollback then removes from memory only.
                if (_turnFailed)
                    return "Not saved: an earlier step in this turn failed and its changes will be rolled back.";
                await FinishTurnGroupAsync(assimilate: _turnWrote);
                _turnWrote = false;
            }
            else
            {
                await bridge.EnsureTurnGroupAsync(_cts?.Token ?? default);
            }
        }
```

- [ ] **Step 5: Run the tests and confirm they pass**

Run the filter, then the full suite.

- [ ] **Step 6: Re-run the Step 1 scenario in Revit**

Expected: the save succeeds, and the comment is on disk and in memory. Ctrl+Z undoes the comment as the "LoadingAI turn" item.

- [ ] **Step 7: Commit**

```bash
git add src/LoadingAI/Ui/ChatSession.cs src/LoadingAI.Tests/ChatSessionContractTests.cs
git commit -m "fix: save_document closes the turn group before writing to disk"
```

---

### Task 12: Web pane fixes (N14–N17)

**Files:**
- Create: `web/src/lib/scroll.ts`, `web/src/lib/scroll.test.ts`, `web/src/cli/slashCommands.test.ts`
- Modify: `web/src/cli/slashCommands.ts`, `web/src/cli/inputParser.ts`, `web/src/App.tsx`
- Delete: `web/src/lib/utils.ts`
- Modify: `web/package.json`, `web/package-lock.json` (through npm)

- [ ] **Step 1: Write the failing tests**

Create `web/src/lib/scroll.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { isNearBottom } from './scroll';

describe('isNearBottom', () => {
  it('is true at the bottom', () => {
    expect(isNearBottom({ scrollTop: 600, scrollHeight: 1000, clientHeight: 400 })).toBe(true);
  });
  it('is true within the threshold', () => {
    expect(isNearBottom({ scrollTop: 570, scrollHeight: 1000, clientHeight: 400 })).toBe(true);
  });
  it('is false when the reader scrolled up', () => {
    expect(isNearBottom({ scrollTop: 100, scrollHeight: 1000, clientHeight: 400 })).toBe(false);
  });
});
```

Create `web/src/cli/slashCommands.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { LOCAL_COMMANDS, PASSTHROUGH_COMMANDS, SLASH_COMMANDS } from './slashCommands';

describe('slash commands', () => {
  it('every advertised command has a handler', () => {
    for (const c of SLASH_COMMANDS) {
      const handled = !!c.mode || LOCAL_COMMANDS.includes(c.name) || PASSTHROUGH_COMMANDS.includes(c.name);
      expect(handled, `/${c.name} is advertised but nothing handles it`).toBe(true);
    }
  });
});
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `cd web && npm test`
Expected: both new files fail on missing exports.

- [ ] **Step 3: Implement the scroll helper**

Create `web/src/lib/scroll.ts`:

```ts
export type ScrollBox = { scrollTop: number; scrollHeight: number; clientHeight: number };

/** True when the reader is at (or within `threshold` px of) the bottom, so new output may follow. */
export function isNearBottom(el: ScrollBox, threshold = 48): boolean {
  return el.scrollHeight - el.scrollTop - el.clientHeight <= threshold;
}
```

- [ ] **Step 4: Remove the dead slash commands and export the handler sets**

In `web/src/cli/slashCommands.ts`, delete the `history` and `models` entries, and add after `SLASH_COMMANDS`:

```ts
/** Handled in the pane (`clear`, `help`) or by a host slash branch with no LLM call (`checkpoints`, `resume`). */
export const LOCAL_COMMANDS = ['clear', 'help', 'checkpoints', 'resume'];
/** Sent to the model as natural language. */
export const PASSTHROUGH_COMMANDS = ['export', 'isolate', 'hide'];
```

In `web/src/cli/inputParser.ts`, change the import to `import { findSlash, LOCAL_COMMANDS, PASSTHROUGH_COMMANDS, SlashCommand } from './slashCommands';` and replace the two name lists:

```ts
    } else if (PASSTHROUGH_COMMANDS.includes(cmd.name)) {
```

```ts
    } else if (LOCAL_COMMANDS.includes(cmd.name)) {
```

- [ ] **Step 5: Fix App.tsx (run mode, label, stick-to-bottom)**

In `web/src/App.tsx`:

1. Imports: add `import { isNearBottom } from './lib/scroll';` and change the slashCommands import to `import { SLASH_COMMANDS, LOCAL_COMMANDS, filterCommands } from './cli/slashCommands';`.
2. Under `const [mode, setMode] = …`, add:

```tsx
  const [runMode, setRunMode] = useState<ChatMode>('build');
  const stickToBottomRef = useRef(true);
```

3. Replace the auto-scroll effect (line 396) with:

```tsx
  useEffect(() => {
    const el = scrollRef.current;
    if (el && stickToBottomRef.current) el.scrollTop = el.scrollHeight;
  }, [messages, isRunning, currentPlan, pendingApproval, pendingPreset, reasoning]);
```

4. Add `onScroll={e => { stickToBottomRef.current = isNearBottom(e.currentTarget); }}` to the `<div ref={scrollRef} …>` element.
5. Change the signature `const handleSend = (overrideText?: string) => {` to `const handleSend = (overrideText?: string, overrideMode?: ChatMode) => {`.
6. Replace `const isLocalSlash = parsed.slash && ['clear', 'help', 'checkpoints', 'resume'].includes(parsed.slash.name);` with `const isLocalSlash = !!parsed.slash && LOCAL_COMMANDS.includes(parsed.slash.name);`.
7. Replace:

```tsx
    let effMode = mode;
    if (parsed.effectiveMode) effMode = parsed.effectiveMode;
```

with:

```tsx
    const effMode: ChatMode = overrideMode ?? parsed.effectiveMode ?? mode;
    setRunMode(effMode);
    stickToBottomRef.current = true;
```

8. Change the plan card Run handler to `onClick={() => { setMode('build'); setCurrentPlan(null); handleSend('Execute the plan.', 'build'); }}`.
9. In the running row (line 846), replace `mode === 'plan'` and `mode === 'ask'` with `runMode === 'plan'` and `runMode === 'ask'`.

- [ ] **Step 6: Remove dead code and dependencies**

```bash
cd web && git rm src/lib/utils.ts && npm uninstall lucide-react cmdk class-variance-authority @radix-ui/react-popover clsx tailwind-merge
```

Then confirm nothing still imports them. Run `grep -rnE "lucide-react|cmdk|class-variance-authority|react-popover|clsx|tailwind-merge|lib/utils" src`. Expected: no output.

- [ ] **Step 7: Run the tests and build**

Run: `cd web && npm test && npm run build`
Expected: 16 tests pass (12 existing + 3 scroll + 1 slash), and the typecheck and build succeed.

- [ ] **Step 8: Check in the pane**

Run `npm run dev`, open the printed URL, and type `/`. `/history` and `/models` must be gone from the menu. Scroll up in a long transcript (paste several `/help`), and send `/help` again: the view should stay where you are until you scroll back to the bottom.

- [ ] **Step 9: Commit**

```bash
git add web
git commit -m "fix(web): plan Run uses build mode, no dead slash commands, scroll stays put"
```

---

### Task 13: Update the register and open the PR

**Files:**
- Modify: `docs/PROJECT_BUGS_AND_LIMITATIONS.md`
- Modify: `docs/FULL_REVIEW_2026-10-03.md`

- [ ] **Step 1: Mark the outcomes**

In `docs/FULL_REVIEW_2026-10-03.md`, append "— **Fixed** in `<sha>`" to each N-item this plan closed. Fill in the Revit verification results from Tasks 2, 8 and 11.

In `docs/PROJECT_BUGS_AND_LIMITATIONS.md`, add an "Update 2026-10-03" line under B6 (downgraded, with the reason from the review) and B11 (bounded, still plaintext). Then add a short "Full review 2026-10-03" section that links to `FULL_REVIEW_2026-10-03.md`.

- [ ] **Step 2: Run every suite**

```bash
dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true
```

```bash
cd gateway && npm test
```

```bash
cd web && npm test && npm run build
```

Expected: all green. Record the xUnit count in the PR body.

- [ ] **Step 3: Commit and push**

```bash
git add docs
git commit -m "docs: record the 2026-10-03 review outcomes"
git push -u origin fix/review-hardening-2026-10-03
```

- [ ] **Step 4: Open the PR**

```bash
gh pr create --title "Review hardening 2026-10-03" --body-file docs/FULL_REVIEW_2026-10-03.md
```

Before creating it, append the attribution line required for PR bodies to the body file. Do not commit the attribution line to the docs; use a temp copy.

---

## Self-review

- **Coverage:** N1→T1, N2→T2, N3→T3, N4→T4, N5→T5, N6→T6, N7+N13→T7, N8→T8, N10→T9, N11→T10, N12→T11, N14–N17→T12. N9, N18–N22 and the UI/UX protocol items are listed as out of scope, with reasons.
- **Signatures used across tasks:** `OpenAiCompatibleClient(HttpMessageHandler)` (T1, used in T3); `AgentLoop.ModeAllowsTier(string?, ToolTier)` (T1); `RevitBridge.UserDialogTimeout` (T5); `PluginLoader.CanRemove(string, string?, out string)` plus the internal roots overload (T6); `CheckpointStore.IsValidId(string?)` (T7); `ChatSessionContractTests.Session()` is defined in T7 and reused in T8 and T11, so run T7 before T8 and T11.
- **Order constraints:** T1 before T3 (handler seam). T7 before T8 and T11 (`Session()` helper). Everything else is independent.
