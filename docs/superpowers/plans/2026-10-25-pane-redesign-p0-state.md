# Pane redesign P0: state layer implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a typed, tested state layer that turns today's host messages into turns and steps, so the P1 pane can render from it without any text guessing in components.

**Architecture:**
- `web/src/state/` holds pure TypeScript: no React, `window` or bridge imports, so vitest can run it in Node.
- One reducer handles both host messages (`FromHost`) and local actions (send, stop, clear, decide, preset edits).
- Today's text messages (`tool_call`, `tool_result` with "awaiting" markers) are mapped in `legacyText.ts`.
- Scripted scenarios in `web/src/mock/scenarios.ts` are both the vitest fixtures and the browser mock host (`?demo=<case>`).
- The current `App.tsx` is untouched in P0.

**Tech stack:** TypeScript 5.5, vitest 2 (node environment, `src/**/*.test.ts`), React 18 (P1 only).

**Spec:** [docs/superpowers/specs/2026-10-25-pane-redesign-design.md](../specs/2026-10-25-pane-redesign-design.md)

---

## Files

| File | Responsibility |
|---|---|
| `web/src/state/types.ts` (create) | `Entry`, `Turn`, `Step`, `StepState`, `Review`, `Note`, `PaneState`, `PaneAction`, `PermissionLevel` |
| `web/src/state/legacyText.ts` (create) | `splitToolText`, `classifyResult`, `callInfo`, `outcomeLabel`, moved from `transcript.tsx` (which re-exports them so the old `App.tsx` still compiles) |
| `web/src/state/reducer.ts` (create) | `initialState()`, `reduce(state, action)` |
| `web/src/state/selectors.ts` (create) | `activeTurn`, `liveStatus`, `canSend`, `turnStats`, `pendingReview` |
| `web/src/state/reducer.test.ts` (create) | replay tests over the scenarios |
| `web/src/state/legacyText.test.ts` (create) | parser tests |
| `web/src/mock/scenarios.ts` (create) | named `FromHost` message scripts |

## Host message facts the mapping relies on

These come from `src/LoadingAI/Ui/ChatSession.cs` and `ApprovalGate.cs` at 54081d2.

- **Plain call:** `tool_call "name: {json}"`, then `tool_result "name result: …"`.
- **L3 web gate:**
  1. `tool_approval {toolCallId,…}`;
  2. `tool_call "name: {json} (awaiting approval <id>)"`;
  3. then one of: `tool_result "name denied by user"`, `tool_result "name timed out waiting for approval (180s)"`, `tool_result "name Cancelled by user."` (native No), or `tool_result "name result: …"`.
- **Run form:**
  1. `tool_preset {toolCallId,…}`;
  2. `tool_call "name: {json} (awaiting preset <id>)"`;
  3. after Confirm, an L2 tool posts `tool_call "name: {json}"` again (no marker), then the result. An L3 tool goes straight to the result, or to `"name Cancelled by user."`. A cancel posts `"name Cancelled by user."` or `"name Cancelled: preset timeout."`.
- **Lines that aren't tool results:** a validation error posts `tool_result "Tool argument error: …"`, and an unknown tool posts a hint that has no `name result:` shape. Both become notes.
- **Turn lifecycle:** `delta` streams the answer; `assistant` is the final answer and ends the turn; `error` ends the turn with an error.

## Tasks

### Task 1: Types
- [ ] Write `state/types.ts` with the types in the Files table. `StepState = 'running' | 'review' | 'done' | 'failed' | 'denied' | 'timeout' | 'cancelled'`.
- [ ] `npx tsc --noEmit` passes.

### Task 2: Move the legacy text parsers
- [ ] Create `state/legacyText.ts` with `splitToolText`, `classifyResult`, `callInfo` and `outcomeLabel`, copied verbatim from `transcript.tsx`.
- [ ] In `transcript.tsx`, replace those definitions with `export { … } from './state/legacyText'`.
- [ ] Write `legacyText.test.ts` with these cases:
  - `classifyResult('split_beams result: ok')` gives kind `ran`;
  - `'delete_elements denied by user'` gives outcome `cancelled`;
  - `'x timed out waiting for approval (180s)'` gives `timeout`;
  - `'Tool argument error: bad'` gives kind `other` with an empty name;
  - `callInfo('a: {} (awaiting approval ab12)')` gives awaiting `approval` and display `a: {}`.
- [ ] `npm test` passes.

### Task 3: Scenarios
- [ ] Write `mock/scenarios.ts` exporting `SCENARIOS: Record<string, { label: string; steps: { at: number; msg: FromHost }[] }>`, each built from the host message facts above. The cases are:
  - `l1`: question and answer;
  - `l2Preset`: Run form confirmed;
  - `l2PresetCancel`;
  - `l3Allow`: approval, allow, result;
  - `l3Deny`;
  - `l3Timeout`;
  - `dryRun`: a Run form result with no review;
  - `toolError`: argument error;
  - `providerError`;
  - `streaming`: deltas, then assistant;
  - `plan`: plan payload;
  - `quotaLow`.
- [ ] Export `userTextOf(name)`, the prompt each scenario starts from.

### Task 4: Reducer
- [ ] Write `reducer.test.ts` first. For each scenario, dispatch `{type:'send', text: userTextOf(name), mode:'build', at:0}`, then every message as `{type:'host', msg}`, then assert on the resulting entries:
  - **`l1`:** one turn, outcome `done`, one step `done` with its result, and the answer text.
  - **`l3Allow`:** one step (not two, despite the approval plus the awaiting `tool_call`), with `review.kind` `approval`. While pending, the state is `review` and `pendingReview` is set. After `{type:'decide', toolCallId, decision:'allowed'}` the step is `running`; after the result it is `done`.
  - **`l3Deny`:** the step is `denied`, and the turn outcome is `done` because the agent's answer still arrives.
  - **`l3Timeout`:** `timeout`.
  - **`l2Preset`:** one step. A `presetValue` action updates `pendingReview.values`, and a `presetPickResult` message sets ids. After confirm, the markerless `tool_call` re-uses the same step and the result makes it `done`.
  - **`l2PresetCancel`:** `cancelled`.
  - **`toolError`:** an error note in the turn, and no step with an empty name.
  - **`providerError`:** turn outcome `error`, with the note text kept.
  - **`streaming`:** while streaming, the answer grows and `answerStreaming` is true. After `assistant` it is false and the text is final.
  - **`plan`:** `turn.plan` is set, and todos are mapped from the plan steps.
  - **`stop` while in review:** the step is `cancelled`, `pendingReview` is null, and the outcome is `cancelled`.
  - **`clear`:** the entries hold only one "Cleared." note.
  - **`history`:** turns are rebuilt from `history` messages.
- [ ] Run `npm test` and expect the new tests to fail, because there is no reducer yet.
- [ ] Implement `reducer.ts`. The rules:
  - **`send`** pushes a new turn (running) and sets `running`.
  - **Messages outside a turn:** host messages that belong to a turn but arrive with none open (`trace`, `tool_*`, `error`) go into a standalone note entry.
  - **`tool_approval` / `tool_preset`** create the step in `review`, with `review` filled in, and set `pendingReview`.
  - **`tool_call` with an awaiting marker** whose id matches an existing review is ignored.
  - **`tool_call` without a marker:**
    - if the open turn's last step with the same name is in `running` or `review` with `review.decided === 'allowed'`, set it to `running` and fill in `argsText`;
    - otherwise push a new `running` step.
  - **`tool_result`:**
    - when `classifyResult` returns a name, find the last open step (`running` or `review`) with that name, or create one;
    - `ran` → `done` with `resultText`;
    - outcome `cancelled` → `denied` when the text says denied, else `cancelled`;
    - `timeout` → `timeout`;
    - `confirmed` → `review.decided = 'allowed'` and `running`;
    - no name → an error note when the text starts with "Tool argument error", else a system note;
    - always clear `pendingReview` when it belongs to the resolved step.
  - **`delta` / `assistant` / `error`:**
    - `delta` appends to the answer and sets `answerStreaming`;
    - `assistant` merges with the same rules as today's App and finishes the turn;
    - `error` adds an error note and finishes the turn with outcome `error`;
    - finishing a turn turns `running` steps into `done` and `review` steps into `cancelled`, and clears `running`, `progress` and `reasoning` streaming.
  - **`progress`:** `idle` gives null, otherwise it is stored.
  - **`reasoning`:** set on the turn.
  - **`plan` / `todo`:** set on the turn.
  - **Pane-wide messages** (`context`, `quota`, `status`, `settings`, `catalog`, `plugins` with `loadIssues`, `checkpoints`, `suggestions`, `testResult`, `models`): stored in pane state. `checkpoints` also adds a note.
  - **`stop`:** the current turn's `review` steps become `cancelled`, `pendingReview` is cleared, and the outcome is `cancelled`.
  - **`clear`:** resets entries to a single note.
- [ ] Run `npm test`; all tests pass.

### Task 5: Selectors
- [ ] Write `selectors.ts`:
  - `activeTurn(state)`;
  - `pendingReview(state)`;
  - `canSend(state)`, which is true when not running;
  - `liveStatus(state)`, which returns the progress label, or "Waiting for your review" when a review is pending, or "Thinking…";
  - `turnStats(turn)`, which returns `{ steps, ms, changed }`.
- [ ] Add selector tests to `reducer.test.ts`.
- [ ] Run `npm test` and `npx tsc --noEmit`; both pass.

### Task 6: Mock host plays scenarios
- [ ] In `bridge.ts`, when there is no WebView2 and the URL has `?demo=<name>`, a `send` replays that scenario's steps at their `at` times. `approveTool` and `submitPreset` continue the script by choosing the allow branch or the deny branch. Without `demo`, behaviour stays as today.
- [ ] Run `npm run build`; it passes. In the browser preview, `?demo=l3Allow` shows the current pane receiving the messages.

### Task 7: Commit
- [ ] `git add web/src/state web/src/mock web/src/transcript.tsx web/src/bridge.ts src/LoadingAI/Web/dist docs/superpowers/plans`
- [ ] Commit with the message `feat(web): typed pane state layer and scripted mock host (P0)`.
