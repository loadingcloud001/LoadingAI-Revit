# Full Review — 2026-10-03

**Basis:** `main` at `136ae4f` (synced from GitHub 2026-10-03, 111 commits ahead of the previous local checkout).
**Scope:** logic, function, code quality, UI/UX, gateway, CI. Then brainstormed enhancements and debug targets.
**Plan:** [`docs/superpowers/plans/2026-10-03-review-hardening.md`](superpowers/plans/2026-10-03-review-hardening.md).

## What was run

| Suite | Where | Result |
|---|---|---|
| Web vitest | local, Node 20 | 12 / 12 pass |
| Web typecheck (`tsc --noEmit`) | local | clean |
| Gateway `node --test` | local, Node 22.23 | 49 / 49 pass |
| Web `npm audit --omit=dev` | local | 0 vulnerabilities |
| xUnit (`LoadingAI.Tests`) | **not run** — no .NET SDK on this Linux box | CI on `main` is green per the findings register |
| In-Revit NUnit | **not run** — needs Revit 2027 on Windows | still blocked (F7) |

Every finding below comes from reading the code and tracing the call path. **CONFIRMED** means I traced the path and it has to happen as described. **PLAUSIBLE** means the code allows it, but whether it fires depends on Revit runtime behaviour that someone has to check in Revit.

## Status of the existing register (`PROJECT_BUGS_AND_LIMITATIONS.md`)

| ID | Register status | Status now |
|---|---|---|
| B1–B3 | addressed | Still fixed. `ToolArgumentValidator` runs before approval and again after the preset merge. Bridge actions have a lifecycle. Tool results are structured. |
| B4 busy unload | open | **Still open** (`PluginLoader.cs:185`). Worse than recorded: `Discover` runs on the Revit UI thread and `WaitForIdle` sleeps there, so a plugin waiting on an ExternalEvent can never go idle. Every wait times out, and the plugin is unloaded anyway. |
| B5 nondeterministic discovery | open | **Still open** (`PluginLoader.cs:68,77,206`). |
| B6 quota race | open | **Downgraded to Low.** The Node gateway uses synchronous `node:sqlite`, and every `await` in `consume` resolves inside one microtask chain. Two requests therefore cannot interleave between the read and the write in a single process. The race only becomes real with more than one process or a move back to Workers KV. |
| B7 plan ↔ execution | open | **Still open** (`AgentLoop.cs:256`). |
| B8 `enabledByDefault` | open | **Latent.** `ChatSession.cs:80` still ignores the manifest. Today every manifest agrees with `tier != L3`, so no behaviour differs yet. |
| B9 Anthropic preset | open | Still open: the preset is still offered, and the UI still lists "Anthropic" as an API kind. |
| B10 coverage | partly addressed | Web tests now run in CI. The Revit smoke matrix and the package smoke test are still missing. |
| B11 logs | open | **Partly addressed.** `BoundedLog` rotates at 2 MB. Logs are still plaintext and on by default, and the app has no "delete local data" action. Checkpoints (20 × full history, including base64 images) are not bounded by size. |
| B12 ticket | open | `TICKET_SECRET` is now required (fixed). The 30-minute ticket TTL versus long runs is **still open**: see N9. |
| B14 | frozen by ADR | Unchanged. |

## New findings

### High

**N1 — Plan and Ask modes are read-only only in the prompt.** CONFIRMED. — **Fixed in `e8eb71e`.**
`AgentLoop.RunAsync` filters the *offered* catalog to L1 when the mode is `plan` or `ask`. `ChatSession.ExecuteToolAsync` resolves any name against the full registry, though, and never sees the mode. A model that remembers an L2 tool name from an earlier Build turn (history persists across modes) can run it in Plan mode. For example, `set_comments_on_selection` would run with no dialog at all, because it opts out through `RequiresWriteConfirm`. L3 tools still hit both confirms.
*Fix:* enforce the mode at execution time in `AgentLoop` (plan Task 1).

**N2 — The bridge runs Revit API calls outside API context on the UI thread.** PLAUSIBLE (needs Revit). — **Fixed in `8497c8e`.** **Revit 2027 check pending** (context line, element pick presets, modal-dialog stall).
`RevitBridge.RunAsync` (`RevitBridge.cs:64`) takes the "direct" path whenever the caller is on the recorded UI thread, not only when it is *inside* `BridgeHandler.Execute`. The WPF pane runs on Revit's UI thread, so callers that start from a WebView message call the API directly from a modeless event handler. That covers `ContextCollector.CollectAsync` (every send) and `ApprovalGate.RunPresetPickAsync` (`Selection.PickObjects` / `PickBox`). Revit only supports API calls inside an API context: reads usually work, while picks and transactions can throw or misbehave.
*Fix:* take the direct path only when inside `Execute` or a test-attached thread (plan Task 2).

**N3 — Stop cannot interrupt a stalled stream, and streams have no timeout.** CONFIRMED. — **Fixed in `ae37edd`, `1ca6b10`, `8c3d0b6` (90 s idle, 180 s first-line allowance, Stop aborts the read).**
`HttpClient.Timeout` is infinite (`OpenAiCompatibleClient.cs:178`), and only the non-stream path adds a 120 s `CancelAfter`. In `ChatStreamAsync`, `reader.ReadLineAsync()` (`:647`) takes no token (netstandard2.0). `ct` is checked only *between* lines, so if a provider stops sending bytes, Stop does nothing and the turn hangs until the socket dies.
*Fix:* add a per-line idle timeout and make cancellation dispose the response (plan Task 3).

**N4 — A long turn forgets the user's request.** CONFIRMED. — **Fixed in `f98d74a`.**
`ChatHistoryTrimmer.Trim` keeps the last 40 messages and runs after every tool result. One Build turn makes 1 + N messages per round. After roughly 15–20 tool rounds (the limit is 64), the turn's own user message is cut, and the model carries on without the instruction it is executing. Separately, every tool result, including the one the model is about to read, is cut to 2000 chars, so `get_schedule_data` and similar tools lose data silently.
*Fix:* pin the current turn's user message, and allow a larger cap for current-turn tool results (plan Task 4).

**N5 — Confirm dialogs time out after 120 s while still on screen.** CONFIRMED. — **Fixed in `3e1f524` (15 min; an unanswered L3 confirm counts as No).** Follow-up: thread the Stop token through the confirm wait.
`ConfirmL3NativeAsync` (`ApprovalGate.cs:159`) and the generic L2 confirm (`ToolGuards.cs:29,48`) use `RevitBridge.DefaultTimeout` (120 s). If the engineer takes longer, `RunAsync` throws `BridgeOperationUnknownException`, the agent records "Tool error", and the dialog stays open. When the engineer then clicks *Yes*, the click is ignored. The exception also escapes `ExecuteToolAsync` outside `entry.ExecuteAsync`, so the turn is not marked failed.
*Fix:* give dialogs a dialog-length timeout (15 min, matching the preset form) (plan Task 5).

### Medium

**N6 — "Remove plugin" guard checks the wrong id and can delete the whole Plugins folder.** CONFIRMED. — **Fixed in `fdbdc99`.** Manual check: a junction under `%AppData%\LoadingAI\Plugins` removed via Settings leaves its target intact.
`HostPage.xaml.cs:412` protects the id `"core"`, but the shipped id is `core-tools`, so the core plugin can be removed. `PluginLoader.Discover` also loads *loose* manifests placed directly in `Plugins\`, which gives that plugin `FolderPath == Plugins\`. Removing it would then `Directory.Delete(Plugins\, recursive)` and wipe every plugin.
*Fix:* add a `PluginLoader.CanRemove` check: no core, no root folders, folder must be under a plugin root (plan Task 6).

**N7 — Run-state hazards in `ChatSession`.** CONFIRMED (the first two). — **Fixed in `ab1147e` for the items listed.** Still open: the UI Clear button and `RebuildRegistry` callers mid-run, and Stop during the quota wait (the call takes no token).
- Stop pressed during "Checking LoadingAI allowance…" is ignored. `_cts` is created only *after* the quota await, so `CancelAndDeny` cancels nothing and the run starts anyway.
- `Task.Run(…, ct)` (`ChatSession.cs:426`): if `ct` is already cancelled when the task is scheduled, the delegate never runs. The `finally` that resets `_isRunning` never runs either, and the pane answers "Already working" until Revit restarts.
- `/clear`, `/resume` and `ResumeCheckpoint` rewrite history while a run is appending to it.
- `_lastUserText` is overwritten by a rejected "Already working" send, so the audit log credits the running turn to the wrong prompt.
- `ReloadPlugins` / Trust / settings saves rebuild the registry or unload plugin contexts mid-run.
*Fix:* plan Task 7.

**N8 — Pane reload leaks handlers and re-discovers plugins.** CONFIRMED for the leak; PLAUSIBLE for how often it happens. — **Fixed in `60ad7d7`.** **Revit 2027 check pending** (does `Unloaded` fire on hide; Reload still re-discovers).
`OnLoaded` subscribes `Web.NavigationCompleted` (the control event), but `OnUnloaded` unsubscribes `Web.CoreWebView2.NavigationCompleted` (a different event). Every Unloaded→Loaded cycle therefore adds a duplicate handler, which posts settings, status and plugins N times. Each cycle also re-runs `InitTools` → `PluginLoader.Discover` → `UnloadContexts`, and `OnUnloaded` cancels any running turn through `DisposeCts`. Whether docking or hiding the pane raises Unloaded inside Revit needs checking in Revit.
*Fix:* plan Task 8. The cancel-on-unload behaviour is left as a question (see below).

**N9 — The free-tier ticket expires mid-run and rolls back the work.** CONFIRMED. — **Mitigated, not fixed**: the default ticket lifetime is now 2 hours (`d2cc76f`, Worker pin `ebc161d`). A renewal endpoint still needs an ADR.
Tickets live 30 min (`quota.js:20`). One run can take longer: 64 rounds, plus preset forms that wait up to 15 min each. Once the ticket expires, the next LLM call fails with `401 no_ticket` → `LLM error:` → the turn's `TransactionGroup` rolls back, discarding writes the engineer approved.
*Fix (design needed):* a renew endpoint that exchanges a still-valid ticket for a fresh one without spending quota. It is listed under Deferred because it changes the gateway contract.

**N10 — The gateway forwards arbitrary request fields upstream.** CONFIRMED. — **Partly fixed** in `d2cc76f`, `4cb529d`: field allow-list, `max_tokens` ceiling, upstream header timeout. The shipped add-in never sends `max_tokens`, so output is still unbounded, and the 2 h ticket widens the replay window. Open: always set `max_tokens` (needs `finish_reason: length` handling), body-size and message-count limits, a per-ticket call cap.
`index.js:202` spreads the client payload, so a holder of one ticket can set `max_tokens`, `n` and any other upstream parameter, and can make unlimited calls for 30 min. Device ids are free to mint, so the 100/day limit does not bound upstream spend. The upstream `fetch` also has no timeout.
*Fix:* allow-list fields, cap `max_tokens`, add an upstream timeout (plan Task 9). A per-ticket call cap is deferred.

**N11 — `delete_elements` reports the requested count, not what was deleted.** CONFIRMED. — **Fixed in `7691a8b`.** The same under-count remains in the purge tool (`AdapterRevitBridge.Workflows.cs:54`).
`doc.Delete(ids)` (`AdapterRevitBridge.Elements.cs:35`) also deletes dependents (hosted doors and windows, tags, dimensions), and returns the full set. The result says "Deleted 3 element(s)" when 11 went, and the L3 confirm never mentions the cascade.
*Fix:* report both counts (plan Task 10). Showing the cascade before confirm (a dry-run count) is deferred.

**N12 — `save_document` runs inside the turn `TransactionGroup`.** PLAUSIBLE (needs Revit). — **Not done.** Needs the Revit 2027 reproduction first (plan Task 11 step 1). Reproduce with L3 enabled: "set comments on the selected wall to TEST, then save the document".
`save_document` is L3, so `ExecuteToolAsync` opens the turn group *before* calling `doc.Save()`. Revit may refuse to save with a group open. If it does save, a later rollback reverts the in-memory model after its changes were written to disk. `export_ifc` is L1 but can run inside a group opened earlier in the same turn; the existing `inTransaction` debug field suggests this was already seen.
*Fix:* verify in Revit 2027 first, then close the turn group before document-boundary tools (plan Task 11).

### Low

- **N13** — `CheckpointStore.Load` builds a path from a web-supplied id with no validation (`CheckpointStore.cs:63`), so `..\` traversal is possible. The web content is local, so the risk is low, but the fix is a one-line regex (plan Task 7). **Fixed in `ab1147e`.**
- **N14** — Plan card *Run* calls `setMode('build')` and then `handleSend` in the same tick (`App.tsx:789`). `handleSend` reads the stale `mode`. This is **latent** because nothing else changes `mode` today; it becomes a bug as soon as a mode toggle exists (plan Task 12). **Fixed in `2d41eb3`.**
- **N15** — `/history` and `/models` are advertised in the slash menu but handled nowhere, so each sends a literal "/history" to the LLM and spends a free request (plan Task 12). **Fixed in `2d41eb3`.**
- **N16** — Auto-scroll runs `smooth` on every delta and always pulls to the bottom, even while the engineer has scrolled up to read (`App.tsx:396`) (plan Task 12). **Fixed in `2d41eb3`.**
- **N17** — Dead web code and dependencies. Nothing imports `lucide-react` or `class-variance-authority`. (Correction: `cmdk` and `@radix-ui/react-popover` look unused but are peer dependencies of `open-model-selector`, which the pane uses, so they stay.) `clsx` and `tailwind-merge` are imported only by `src/lib/utils.ts`, and nothing imports that file (plan Task 12). **Fixed in `2d41eb3`, corrected in `0b3cc42`.**
- **N18** — `AgentLoop.cs:287` still has the empty "heuristic error detection" block. Todos come from the string executor, so a `Failed` tool shows ✓ done.
- **N19** — Group-enabling a plugin (`SetPluginGroupEnabled`) or enabling one L3 tool skips the "Enable all L3 tools?" dialog that tier-enable shows. `MergeFromWeb` also accepts `plugins` from the web when the host has no flags yet.
- **N20** — The plugin hash is checked, and then the DLL is loaded by path (`LoadFrom`), which leaves a TOCTOU window. Loading the hashed bytes (`LoadFromStream`) would close it. An attacker who can write there already has `%AppData%`, so this is low.
- **N21** — The API key cannot be cleared by blanking the field, because `MergeFromWeb` keeps the previous key.
- **N22** — Release builds are not Authenticode-signed, and no `SHA256SUMS` ships with the zips.

## UI/UX review

- **Tool status is inferred from prose.** The host has `ToolExecutionResult.Status`, but the pane classifies `"{name} result: …"` strings (`transcript.tsx:72`). A failed write therefore shows **Done**, and the same tool called twice in parallel pairs by name. Carrying `toolCallId` + `status` in the protocol fixes both (deferred: protocol change).
- **No visible mode.** Mode exists only as `/plan` `/ask`. The running label reads `mode`, which is always `build`, so `/plan` shows "Working…". A three-way mode chip would make read-only runs obvious.
- **Rollback is invisible.** Denying *one* L2/L3 step rolls back *every* write in the turn (ADR-0004). That is the right default, but the pane never says so. One line on the approval card ("Deny undoes this turn's earlier changes too") would prevent surprise.
- **No dark theme.** Revit 2024+ has a dark UI theme, and the pane is hard-coded light (`#f3f3f3`). Posting `UIThemeManager.CurrentTheme` to the web and switching CSS tokens would fix this.
- **Accessibility.** Icon-only buttons (`×`), state shown by colour only (chips), and no focus ring on the custom buttons.
- **Todo list grows across turns.** Todos are keyed by tool-call id and cleared only by `/clear` or Plan mode, so the "Todo · x/y" counter keeps climbing.

## Brainstorm — enhancements

Ranked by value ÷ effort, with safety first.

1. **Structured tool events end to end.** Executor returns `ToolExecutionResult`; `FromHostMessage.ToolResult` carries `{toolCallId, status, writeCertainty, changed, failed}`; the web drops string classification. This fixes N18, the UI "Done on failure" problem, parallel-call pairing and B7 in one move.
2. **Delete cascade preview.** Run `doc.Delete` inside a sub-transaction, roll it back, and show "3 requested → 11 deleted (8 dependents: 6 doors, 2 tags)" on the L3 card. This is the biggest honesty gain for the most destructive tool.
3. **Ticket renew** (`POST /v1/ticket/renew`, no quota spend, only for a ticket younger than 4 h). Closes N9 without lengthening replay windows.
4. **Token-aware history budget** instead of a 40-message count: estimate chars/4 per provider and keep pinned context.
5. **Turn summary card.** After a Build turn, list writes that landed, writes rolled back and the undo name ("LoadingAI turn"). The data already exists in `_turnWrote` and the results.
6. **Retry with backoff** for 429/502/503 on BYO-key providers (not the built-in one, where retries spend upstream credit).
7. **Dark theme + mode chip + stick-to-bottom scroll** (the UX items above).
8. **"Delete local data"** button: debug, audit, history, checkpoints. Make debug logging opt-in.
9. **Release signing + SHA256SUMS + a package smoke test** that unzips the release and checks the `.addin`, manifests, `.rfa` files and the tool catalog against `plugin.json`.
10. **Implement Anthropic `/messages`** or remove the preset (B9).

## Brainstorm — debug targets that need Revit

These can only be closed on a Windows box with Revit 2027:

1. N2: call `PickObjects` from the preset form with the current code, and record whether Revit throws.
2. N8: does hiding, re-docking or floating the pane raise `Unloaded`? Count the `Plugins` posts after three cycles.
3. N12: `save_document` after an L2 write in the same turn: does `doc.Save()` throw with the group open?
4. B4: start `split_by_levels` on a large model, then Reload plugins: does Revit crash?
5. The F7 harness blocker (ricaun on 2027.2) is still the gate for automated in-Revit tests.

## Questions for the owner

1. Should hiding or undocking the pane **cancel** a running turn (current behaviour on Unloaded), or keep it running?
2. Ticket renewal (N9): is a gateway contract change acceptable now, or should the TTL just go to 2 h?
3. Should Plan/Ask mode refuse non-L1 tools silently (the model gets an error) or ask the user to switch to Build?

## Execution status (2026-10-03, branch `fix/review-hardening-2026-10-03`)

Done by subagent-driven execution: one implementer per task, then spec and code-quality review. Tasks 1-10 and 12 are committed; Task 11 is deferred.

| Task | Closes | Commits |
|---|---|---|
| 1 | N1 | `e8eb71e` |
| 2 | N2 | `8497c8e` |
| 3 | N3 | `ae37edd`, `1ca6b10`, `8c3d0b6` |
| 4 | N4 | `f98d74a` |
| 5 | N5 | `3e1f524` |
| 6 | N6 | `fdbdc99` |
| 7 | N7, N13 | `ab1147e` |
| 8 | N8 | `60ad7d7` |
| 9 | N10, N9 (mitigation) | `d2cc76f`, `ebc161d`, `4cb529d` |
| 10 | N11 | `7691a8b` |
| 11 | N12 | deferred: needs the Revit reproduction |
| 12 | N14-N17 | `2d41eb3`, `0b3cc42`, bundle `a11396e`, `72cb033` |

**Verified here:** gateway 63/63 (Node 22), web 17/17, web typecheck and build. **Not verified here:** no .NET SDK on the build machine, so none of the C# changes were compiled or run. CI is the first compile. Nothing was run inside Revit.

### Needs checking in Revit 2027 before release
1. Send "hello" with a selection: the context line fills, with no visible delay (N2).
2. Run `create_formwork_panels`, click Pick, pick two walls: the field shows "2 elements" (N2).
3. With a modal dialog open, send a message: it stalls at most 5 s and proceeds with an "Error" context (N2, N5).
4. Hide, re-dock and float the pane three times: Settings lists plugins once and `plugins.log` has no new discovery lines; note whether `Unloaded` fires on hide; Reload still re-discovers (N8).
5. Remove a plugin that is a junction under `%AppData%\LoadingAI\Plugins`: the target survives (N6).
6. N12 scenario (above), then Task 11 if it reproduces.

### Follow-ups found during review (not done)
- Refusals sent as `Error` while a run is in flight make the web pane look idle (latent today; the web gates input). Use a non-terminal message.
- Quota call is not cancellable; Stop during context collection leaves an orphan user message; early exits leave `_cts` undisposed; reset `_isRunning` before the awaited rollback in the `finally`.
- UI Clear button and `RebuildRegistry` callers (`SaveSettings`, `SetPlugin*`, `SetTier*`) are unguarded mid-run.
- Confirm wait cannot be cancelled by Stop; a timed-out L3 confirm tells the model "Cancelled by user."; `InvalidOperationException` from the confirm path skips `MarkTurnFailedIfGroupActive`.
- Gateway: 504 mid-run message and no non-stream fallback on 504 (double spend); link upstream abort to `request.signal`; body-size limits.
- `AttachCurrentThread` doc comment and `t_inHandler` save/restore; a few extra tests (no-system trim, CORE-TOOLS case, `..` rows).
- Early-return `InitTools` should `RebuildRegistry` for out-of-band settings edits.
- Purge tool and `FormworkCore` delete counts; dedupe ids in `delete_elements`.
