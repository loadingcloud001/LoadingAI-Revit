# Safety Control Plane Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent invalid tool calls from silently changing the wrong Revit elements, make timeout outcomes honest and retry-safe, and replace string-based write decisions with conservative structured tool outcomes.

**Architecture:** Add host-side JSON argument validation before approval, add an execution result type that preserves status and write certainty, and add a queued Revit action lifecycle that distinguishes success, failure, cancellation, timeout, and unknown completion. Existing string-returning tools remain compatible through a conservative adapter while key write paths can return structured results.

**Tech Stack:** C#/.NET, System.Text.Json, xUnit, Revit ExternalEvent, Vite/React web pane.

**Spec:** `docs/PROJECT_BUGS_AND_LIMITATIONS.md` findings B1, B2, and B3.

## Global Constraints

- Revit 2024, 2025, 2026, and 2027 remain supported.
- `LoadingAI.Core` must not reference Revit assemblies.
- L3 tools remain disabled by default and require web approval plus native TaskDialog confirmation.
- Invalid or normalized-away L3 arguments must not execute.
- A timed-out write must not be reported as definitely failed or automatically retried.
- Structured unknown/failed write outcomes force turn rollback. Legacy normal returns remain compatibility-committed until migrated to `StructuredHandler`.
- Tool result text remains model-readable; status is host-controlled.
- No new production dependencies.

---

## File Structure

- `src/LoadingAI.Core/Tools/ToolArgumentValidator.cs` — schema-aware validation of tool JSON.
- `src/LoadingAI.Core/Tools/ToolExecutionResult.cs` — structured result status, text, write certainty, and counts.
- `src/LoadingAI.Core/Tools/ToolRegistry.cs` — extend `ToolEntry` with optional structured execution while preserving legacy handlers.
- `src/LoadingAI/Revit/RevitBridge.cs` — queued action IDs and completion state.
- `src/LoadingAI/Ui/ChatSession.cs` — validate before approval, use structured result, and make rollback decisions from status.
- `src/LoadingAI.Tests/ToolArgumentValidatorTests.cs` — validator behavior.
- `src/LoadingAI.Tests/ToolExecutionResultTests.cs` — compatibility and classification behavior.
- `src/LoadingAI.Tests/RevitBridgeContractTests.cs` — queued action lifecycle contract.
- `src/LoadingAI.Tests/ChatSessionContractTests.cs` — control-plane safety contract.

---

### Task 1: Reject invalid tool arguments before approval

**Files:**
- Create: `src/LoadingAI.Core/Tools/ToolArgumentValidator.cs`
- Modify: `src/LoadingAI/Ui/ChatSession.cs`
- Test: `src/LoadingAI.Tests/ToolArgumentValidatorTests.cs`

**Interfaces:**
- Consumes: `ToolDefinition.Function.Parameters` as a JSON object schema and raw `argsJson`.
- Produces: `ToolArgumentValidator.Validate(object schema, string argsJson, out string normalizedJson, out string error) -> bool; accepts `JsonElement` and anonymous schema objects`.

- [x] **Step 1: Write failing tests for malformed JSON, wrong types, invalid enums, missing required values, and accepted valid input.**

```csharp
[Fact]
public void RejectsMalformedJson()
{
    var schema = JsonDocument.Parse("""{"type":"object","properties":{"hostIds":{"type":"array","items":{"type":"string"}}}}""");
    var ok = ToolArgumentValidator.Validate(schema.RootElement, "{bad", out _, out var error);
    Assert.False(ok);
    Assert.Contains("Invalid JSON", error);
}

[Fact]
public void RejectsWrongArrayType()
{
    var schema = JsonDocument.Parse("""{"type":"object","properties":{"hostIds":{"type":"array","items":{"type":"string"}}}}""");
    var ok = ToolArgumentValidator.Validate(schema.RootElement, """{"hostIds":"123"}""", out _, out var error);
    Assert.False(ok);
    Assert.Equal("hostIds: expected array.", error);
}

[Fact]
public void AcceptsValidArgumentsAndReturnsCanonicalJson()
{
    var schema = JsonDocument.Parse("""{"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":200},"shape":{"type":"string","enum":["rectangle","circle","auto"]},"dryRun":{"type":"boolean"}},"required":["limit"]}""");
    var ok = ToolArgumentValidator.Validate(schema.RootElement, """{"limit":50,"shape":"auto","dryRun":true}""", out var json, out var error);
    Assert.True(ok);
    Assert.True(string.IsNullOrEmpty(error));
    using var doc = JsonDocument.Parse(json);
    Assert.Equal(50, doc.RootElement.GetProperty("limit").GetInt32());
}
```

- [x] **Step 2: Run `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj --filter FullyQualifiedName~ToolArgumentValidatorTests -m:1`.**
Expected: FAIL because `ToolArgumentValidator` does not exist.

- [x] **Step 3: Implement recursive JSON Schema subset validation: `object`, `array`, `string`, `integer`, `number`, `boolean`, `enum`, `required`, `minimum`, `maximum`, and `items`. Reject unknown object properties for tools.**

- [x] **Step 4: Add tests for nested arrays, integer boundary violations, unknown properties, empty input, and schemas without `properties`.**

- [x] **Step 5: Run the focused tests and full `src/LoadingAI.Tests` suite.**
Expected: PASS with zero failures.

- [x] **Step 6: Integrate validation at the beginning of `ChatSession.ExecuteToolAsync` before presets or L3 approval. On failure, return a non-executing error and do not create a transaction group.**

- [x] **Step 7: Add contract assertions that validation occurs before `AwaitPresetAsync`, `AwaitL3WebAsync`, and `EnsureTurnGroupAsync`.**

- [x] **Step 8: Commit the validator and tests.**

---

### Task 2: Introduce structured tool execution outcomes

**Files:**
- Create: `src/LoadingAI.Core/Tools/ToolExecutionResult.cs`
- Modify: `src/LoadingAI.Core/Tools/ToolRegistry.cs`
- Modify: `src/LoadingAI/Ui/ChatSession.cs`
- Test: `src/LoadingAI.Tests/ToolExecutionResultTests.cs`

**Interfaces:**
- Consumes: legacy `Func<string, Task<string>> ToolEntry.Handler`.
- Produces:
  - `enum ToolExecutionStatus { Succeeded, Partial, Failed, Cancelled, Unknown }`
  - `enum ToolWriteCertainty { NoWrite, Committed, NotCommitted, Unknown }`
  - `record ToolExecutionResult(string Text, ToolExecutionStatus Status, ToolWriteCertainty WriteCertainty, int ChangedCount = 0, int FailedCount = 0)`
  - `ToolEntry.ExecuteAsync(string argsJson) -> Task<ToolExecutionResult>`

- [x] **Step 1: Write failing tests showing legacy string handlers preserve text and use conservative outcomes.**

```csharp
[Fact]
public async Task LegacyReadHandlerIsSucceededNoWrite()
{
    var entry = new ToolEntry
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition { Function = new ToolFunction { Name = "read" } },
        Handler = _ => Task.FromResult("ok")
    };
    var result = await entry.ExecuteAsync("{}");
    Assert.Equal("ok", result.Text);
    Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
    Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
}

[Fact]
public async Task LegacyWriteHandlerPreservesCompatibilityByAssumingCommit()
{
    var entry = new ToolEntry
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition { Function = new ToolFunction { Name = "write" } },
        Handler = _ => Task.FromResult("changed")
    };
    var result = await entry.ExecuteAsync("{}");
    Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
}
```

- [x] **Step 2: Run the focused tests and verify they fail because `ExecuteAsync` is missing.**

- [x] **Step 3: Implement `ToolExecutionResult` with factory methods `Succeeded`, `Partial`, `Failed`, `Cancelled`, and `Unknown`; add `IsSafeToAssimilate` requiring `Status == Succeeded` or explicit `Partial` with `WriteCertainty == Committed`. Preserve legacy L2/L3 normal-return commit behavior while migrating structured handlers.**

- [x] **Step 4: Add optional `Func<string, Task<ToolExecutionResult>>? StructuredHandler` to `ToolEntry`; `ExecuteAsync` uses it when present and wraps legacy strings conservatively.**

- [x] **Step 5: Add tests for thrown handlers, empty results, cancelled text, failed text, explicit partial commit, and structured success.**

- [x] **Step 6: Update `ChatSession.ExecuteToolAsync` to call `entry.ExecuteAsync`, display `result.Text`, and set `_turnWrote`/`_turnFailed` from `result.WriteCertainty` and `result.Status`, never from tool-result prefixes.**

- [x] **Step 7: Update opening-tools handlers to return structured success/partial/failure from `OpeningPlaceResultDto`, `OpeningCutResultDto`, and `OpeningPlanResultDto`, preserving the full report when `Error` is present.**

- [x] **Step 8: Run focused and full tests.**
Expected: PASS with zero failures.

- [x] **Step 9: Commit the structured result control plane and opening-tools adapter.**

---

### Task 3: Track queued Revit actions and report timeout as unknown

**Files:**
- Modify: `src/LoadingAI/Revit/RevitBridge.cs`
- Modify: `src/LoadingAI/Ui/ChatSession.cs`
- Test: `src/LoadingAI.Tests/RevitBridgeContractTests.cs`
- Test: `src/LoadingAI.Tests/ChatSessionContractTests.cs`

**Interfaces:**
- Consumes: `RevitBridge.RunAsync`, `BridgeHandler.Enqueue`, cancellation tokens, and the turn `TransactionGroup`.
- Produces:
  - `enum BridgeActionState { Queued, Running, Succeeded, Failed, Cancelled, Abandoned }`
  - `record BridgeActionSnapshot(string Id, BridgeActionState State, string? Error)`
  - `RevitBridge.TryGetAction(string id, out BridgeActionSnapshot snapshot)`

- [x] **Step 1: Write failing tests that the bridge assigns an action ID, marks actions running and completed, and retains abandoned actions after timeout/cancellation.**

- [x] **Step 2: Run `RevitBridgeContractTests` and verify the lifecycle contract fails.**

- [x] **Step 3: Add a thread-safe action registry keyed by generated IDs. Wrap queued delegates so `Running`, `Succeeded`, and `Failed` are recorded exactly once.**

- [x] **Step 4: On timeout or caller cancellation, mark the action `Abandoned` but do not remove or cancel the queued delegate. Throw `BridgeOperationUnknownException` containing the action ID.**

- [x] **Step 5: Add a bounded retention policy so completed/abandoned records are pruned after 500 entries or 24 hours.**

- [x] **Step 6: Update `ChatSession` so `BridgeOperationUnknownException` creates a structured `Unknown` tool result and forces turn rollback. Do not automatically retry.**

- [x] **Step 7: Add tests for timeout before execution, cancellation before execution, failure during execution, nested same-thread execution, and result retention.**

- [x] **Step 8: Run focused and full tests.**
Expected: PASS with zero failures.

- [x] **Step 9: Commit the action lifecycle and conservative timeout handling.**

---

### Task 4: End-to-end safety verification

**Files:**
- Modify: `docs/PROJECT_BUGS_AND_LIMITATIONS.md`
- Test: existing `src/LoadingAI.Tests` suite

- [x] **Step 1: Run `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:RevitVersion=2027 -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true -m:1`.**
Expected: PASS with zero failures.

- [x] **Step 2: Run web build with `npm ci` and `npm run build` in `web/`.**
Expected: exit code 0.

- [x] **Step 3: Update B1-B3 in the review document to state the implemented behavior and remaining Revit smoke-test requirement.**

- [x] **Step 4: Run the code-review skill and address actionable findings.**

- [x] **Step 5: Commit documentation and final verification changes.**


