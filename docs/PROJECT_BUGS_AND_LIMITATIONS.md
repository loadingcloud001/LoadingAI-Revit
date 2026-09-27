# Project Bug and Limitation Review

**Review date:** 2026-09-27  
**Review basis:** static review of cached `origin/main` at `ca0bbde` (2026-09-22) and the local checkout at `d2d275f`  
**Scope:** correctness, model safety, plugin safety, reliability, provider/gateway behavior, test coverage, and product limitations

## Executive assessment

The architecture is coherent and unusually careful about Revit safety: the host owns the Revit API, L3 writes require two confirmations, plugins use Bridge DTOs, and the latest main branch moves opening tools away from direct Revit API access. The most important remaining risks are not feature shortages; they are **control-plane correctness**:

1. invalid tool input can silently become a different operation;
2. timed-out or cancelled Revit work can still run later;
3. write success/failure is inferred from strings instead of a structured result;
4. plugin reload and discovery have lifecycle/ordering hazards;
5. the free-tier counter is not concurrency-safe.

I recommend fixing the first four before adding more automation features.

## Verification status

- The live GitHub remote could not be refreshed because the environment's network proxy refused GitHub access.
- `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj ...` did not complete; restore appeared blocked. A second `--no-restore` attempt also spawned stalled build processes and was stopped. This review therefore does **not** claim a passing test suite.
- The findings below are based on source and contract inspection. Runtime behavior in Revit 2024–2027 remains to be reproduced.

## Findings

### B1 — High: invalid tool arguments silently become defaults — addressed in this branch

**Evidence**

- `LlmJson.NormalizeToolArguments` returns `{}` when the model emits malformed JSON.
- `Plugins/opening-tools/OpeningToolsPlugin.cs` parses arguments with a permissive `Args.Parse`; parse failure returns an empty argument set.
- Empty `hostIds` and `mepIds` mean “use current selection” for the opening tools.
- The L3 approval dialog shows the raw arguments, but the handler can execute using different, defaulted arguments.

**Impact**

A malformed or wrongly typed argument can change the selected elements, skip an intended host limit, or run against the current selection even though the user approved a different request. This is especially risky for L3 writes.

**Implemented in `codex/safety-control-plane`**

- Validates tool arguments against the declared JSON schema before preset/L3 approval and again after preset merge.
- Rejects malformed JSON, wrong types, invalid enums, missing required values, boundary violations, and unknown object properties.
- Uses the validated normalized JSON for approval and execution.

**Remaining verification**

- Revit 2024–2027 smoke testing should confirm that every shipped tool schema matches its handler contract.
### B2 — High: timeout/cancellation does not cancel queued Revit work — addressed in this branch

**Evidence**

- `RevitBridge.RunAsync` enqueues an action and raises an ExternalEvent.
- On timeout it throws, but the queued action is not removed or marked cancelled.
- The cancellation token completes the waiting task but does not stop the queued action.
- Tool handlers do not receive the run cancellation token.

**Impact**

The caller can report “timed out” or “cancelled” while Revit later executes the write. A user may retry and duplicate the operation. For exports or large model edits, the model can also change after the UI has already reported failure.

**Implemented in `codex/safety-control-plane`**

- Gives every bridge action a retained lifecycle record: queued, running, succeeded, failed, cancelled, or abandoned.
- Caller timeout/cancellation throws `BridgeOperationUnknownException` and explicitly says not to retry automatically.
- ExternalEvent rejection cancels queued work before it can run; abandoned work remains inspectable through `TryGetAction`.

**Remaining verification**

- A real Revit smoke test should reproduce timeout during a long write and verify final transaction-group behavior.
### B3 — High: transaction outcome is inferred from result strings — structured control plane added

**Evidence**

- `ToolEntry.Handler` returns only `Task<string>`.
- `ChatSession.ExecuteToolAsync` sets `_turnWrote` from the tool tier and detects failure with string prefixes such as `Tool {name} failed:`.
- `AgentLoop` contains an empty “heuristic error detection” block and marks a tool as done unless the handler throws.
- DTOs already contain authoritative fields such as `CommandResultDto.Ok`, `OpeningPlaceResultDto.Failed`, and `SplitResultDto`, but the handler layer discards them in favor of report/error text.

**Impact**

A partial write, a structured `Ok: false`, or a report such as `Failed: ...` can be treated as success. The turn group may assimilate partial work or roll back successful work incorrectly. Tool timelines can also show failed operations as done.

**Implemented in `codex/safety-control-plane`**

- Adds `ToolExecutionResult` with status, write certainty, changed count, failed count, and model-readable text.
- Opening tools return structured succeeded/partial/failed/no-write outcomes from their DTOs and preserve both error and report text.
- `ChatSession` makes rollback decisions from structured status/write certainty instead of tool-result prefixes.
- Legacy string handlers remain compatible: normal L2/L3 returns are treated as committed, while explicit legacy failures and unknown bridge outcomes do not assimilate.

**Remaining migration**

- Other write plugins should move from legacy string handlers to `StructuredHandler` so partial/failed DTO results are authoritative everywhere.
### B4 — High: busy plugins can be unloaded after the reload timeout

**Evidence**

- `PluginLoader.WaitForIdle` waits for `IPluginLifecycle.IsBusy`.
- When the wait expires it logs “plugin still busy; unloading anyway” and then unloads the plugin context.
- `Discover` calls `UnloadContexts` before loading replacements.

**Impact**

A running tool can continue against an unloaded assembly/context. In Revit this can cause an assembly exception, process abort, or an apparently successful operation with broken state.

**Recommendation**

- Refuse reload while any plugin is busy.
- If a plugin cannot become idle, keep the old context and tell the user to retry later.
- Add a lifecycle test with a deliberately slow tool.

### B5 — Medium: plugin discovery is nondeterministic and can pick unintended DLLs

**Evidence**

- `Directory.GetDirectories` and `Directory.GetFiles` are used without sorting.
- Duplicate tool names use “first wins” based on filesystem order.
- If `plugin.json` is missing or fails to parse, `LoadOne` selects the first DLL in the folder.
- A plugin’s declared `Id` can be replaced from the loaded type after the DLL is selected.

**Impact**

Two installs can enable different implementations for the same tool name. A malformed manifest can make the loader choose an unintended but allowlisted DLL. This makes support and trust review harder.

**Recommendation**

- Sort directories and manifests deterministically.
- Reject duplicate plugin IDs and duplicate tool names instead of silently choosing one.
- Require a valid manifest and explicit `entryAssembly`; never fall back to “first DLL”.
- Include the plugin ID, assembly hash, and tool name in every load issue.

### B6 — Medium: free-tier quota consumption is not atomic

**Evidence**

- `gateway/src/quota.js` reads the used count, checks the limit, then writes `used + 1`.
- The comment assumes one device sends serially.
- The SQLite adapter provides storage but the quota operation is not an atomic increment/compare.

**Impact**

Concurrent messages from multiple Revit instances, a retried client, or a parallel caller can both read the same count and exceed the daily limit.

**Recommendation**

- Implement an atomic `INSERT ... ON CONFLICT ...` increment with a limit predicate or a SQLite transaction.
- Add concurrent consume tests.
- Define whether retries after network failure consume quota.

### B7 — Medium: plan/task state is not connected to tool execution

**Evidence**

- Plan parsing creates `TodoItem` IDs from a new random six-character GUID and leaves `Tool` null.
- Tool call IDs are model-generated or created as `call_...`.
- Execution looks up todos with `t.Id == tc.Id`, which cannot match the generated plan IDs.

**Impact**

The plan card can remain pending or show incorrect completion states while tools execute. The user cannot reliably map a plan step to the operation that changed the model.

**Recommendation**

- Produce a structured plan with stable step IDs, tool names, and arguments.
- Map each execution to a step ID and publish `pending → running → done/error`.
- Keep free-form numbered text as a fallback only.

### B8 — Medium: manifest `enabledByDefault` is ignored when rebuilding the registry

**Evidence**

- `ChatSession.RebuildRegistry` uses `entry.Tier != ToolTier.L3` as the default.
- `GetPluginsPayload` reports `meta.Tools.EnabledByDefault` from `plugin.json`.

**Impact**

A tool can be enabled in the actual registry while the plugin page shows it disabled, or vice versa. This is confusing and can weaken user intent for tools that should be opt-in.

**Recommendation**

- Use the per-tool manifest value consistently.
- Force L3 to `false` regardless of the manifest.
- Add a contract test comparing registry defaults with the displayed plugin state.

### B9 — Medium: the direct Anthropic provider is advertised but rejected

**Evidence**

- `ProviderCatalog` includes an Anthropic direct provider.
- `OpenAiCompatibleClient.ChatAsync` explicitly rejects `ApiKind == "anthropic"` and tells users to use OpenRouter.

**Impact**

Users can configure and test a provider that cannot complete a chat. This is a product correctness issue rather than a security issue.

**Recommendation**

Either implement the Anthropic `/messages` transport or remove/relabel the preset as unsupported.

### B10 — Medium: runtime and web coverage are too narrow

**Evidence**

- CI builds Revit 2024–2027 but runs xUnit only for the Revit 2027 anchor.
- In-Revit tests are explicitly skipped on hosted CI.
- The CI workflow does not run the web build or web tests; the release workflow builds the web bundle later.

**Impact**

Revit-version API differences, WebView2 behavior, install packaging, and web regressions can reach a release without a repeatable automated signal.

**Recommendation**

- Add a local/nightly Revit smoke matrix for 2024–2027.
- Run web build and tests in normal CI.
- Add an install/package smoke test that verifies the `.addin`, plugin manifests, family files, and expected tool catalog.

### B11 — Low/medium: diagnostic logs are plaintext and grow without a strict bound

**Evidence**

- `DebugLog` writes prompt/message previews and response previews to `%AppData%\LoadingAI\debug.jsonl`.
- `AuditLog` and `HostLog` append indefinitely.
- `HistoryStore` keeps recent history, but debug/audit logs do not have the same retention policy.

**Impact**

BIM model names, element IDs, prompts, tool results, and error text can accumulate in local plaintext files. Long-running installations can grow unbounded.

**Recommendation**

- Make detailed debug logging opt-in.
- Redact or hash model/user content by default.
- Add rotation, retention, and a documented “delete local logs” action.

### B12 — Low/medium: free-tier identity and ticket lifecycle are weak boundaries

**Evidence**

- Device IDs are stored in user-writable `device.json` and can be recreated.
- The add-in client token is compiled into the DLL and is only a casual anti-automation gate.
- Tickets are replayable for the same device until expiry.
- A ticket can expire during a long multi-round run.
- If `TICKET_SECRET` is absent, ticket signing falls back to `MINIMAX_API_KEY`.

**Impact**

The 100/day limit is per-install/device-file rather than a strong identity. Long runs can lose their ticket mid-flight. Using the upstream API key as the HMAC secret couples two security roles.

**Recommendation**

- Require a dedicated `TICKET_SECRET`.
- Renew or bind tickets to an active run.
- Document that the free tier is abuse-resistant, not hard authentication.
- If abuse becomes material, add a stronger device/install attestation or account layer.

### B13 — Architecture/version drift: the local opening branch is not merge-safe

**Evidence**

- Local `feat/opening-plugin` contains 31 commits not in cached `origin/main`; cached main contains 140 commits not in local HEAD.
- Local opening tools use `IRevitFullAccess` and direct Revit API references.
- Cached main follows ADR-0012 and keeps opening logic in host slices behind the Bridge.
- A merge scan found 26 interacting files, including the opening plugin, `ToolSelector`, packaging, and tests.

**Impact**

A direct merge can reintroduce a superseded architecture or create semantic conflicts. The local branch is useful as a behavior reference, not as a merge base.

**Recommendation**

Start from live `main`, port missing local behavior into the Bridge implementation, and prove parity with deterministic tests plus a Revit 2027 smoke test.

## Recommended order

1. **P0 model safety:** B1, B2, B3.
2. **P0 plugin/runtime safety:** B4, B5.
3. **P1 reliability/product correctness:** B6, B7, B8, B9.
4. **P1 quality:** B10.
5. **P2 operational hardening:** B11, B12.
6. **Version convergence:** B13 before further opening-tools feature work.

## Discussion points

My recommendation is to treat this as a safety and reliability consolidation before expanding autonomy or adding more tools. The most valuable next design decision is whether tool results should become structured objects end-to-end; that single change would improve transaction rollback, auditability, task state, and user reporting at the same time.

Questions for the next discussion:

1. Should a timed-out Revit write be reported as `unknown` and block retry, or should the host wait indefinitely for a final result?
2. Should partial model writes be committed when some items fail, or should the entire turn roll back?
3. Is the 100/day free tier intended to be a soft anti-abuse limit or a strict quota?
4. Do you want the next pass to focus on model safety, plugin safety, gateway behavior, or test infrastructure?




