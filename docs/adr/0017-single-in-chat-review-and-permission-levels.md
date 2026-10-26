# ADR-0017 — One in-chat review and user-owned permission levels

- Status: Accepted
- Date: 2026-10-25
- Deciders: the maintainer
- Supersedes: [ADR-0004](0004-host-owned-l3-confirm.md) (the order of the gates)
- Amends: [ADR-0016](0016-auto-approve.md) (the auto-approve switch becomes the Bypass level)
- Keeps: [ADR-0002](0002-plugin-shelf-bridge-first.md), [ADR-0014](0014-preview-before-preset-form.md), [ADR-0015](0015-confirm-l2-parameter-writes.md)

## Context

ADR-0004 froze two confirms before an L3 write: a card in the pane, then a Revit `TaskDialog` that defaults to No. L2 tools without a form had a third style, a Revit dialog only. Run forms sat in a fourth place. One action could ask twice, in two windows, with different wording. ADR-0016 added an auto-approve switch that skipped all of them, but it was one boolean beside a gate that had four shapes.

The maintainer asked for three things: a single confirm, in the chat; a way to skip L3 confirms on purpose (the bypass); and the controls other agent apps give, such as allow for a session, deny with a reason and edit before allowing.

The safety property ADR-0004 protected is that the model cannot approve its own write and that an accidental click does not start a destructive change. That property has to survive losing the native dialog.

## Options considered

1. **One host-owned review card in the chat, five permission levels (chosen).** The host decides run, ask or block from the level, the tier and the call. The card is the only prompt. The level is a user setting the model cannot reach.
2. **Keep both confirms for L3 and add levels.** Rejected. It keeps the duplicate prompt the maintainer asked to remove.
3. **Native dialog only.** Rejected. The pane cannot approve while Revit is behind another window, and Stop cannot cancel a modal dialog that is already showing.
4. **Let the pane decide the level per message.** Rejected. A mode that the web page sends is a mode a compromised page or a confused model path could widen. The host stores and enforces the level.

## Decision

### Levels

The level is `AppSettings.PermissionLevel` (`readonly`, `plan`, `confirm`, `autoEdit`, `bypass`), stored in `settings.json`. The default is `confirm`. A settings file with only the old `AutoApprove: true` loads as `bypass`; without it, as `confirm`. The pane shows the level and can ask the host to change it with `saveSettings`. The model has no tool for it, and no chat text or tool result sets it.

| Level | Starts a turn in | L1 | L2 | L3 |
|---|---|---|---|---|
| Read only | ask | runs | blocked | blocked |
| Plan | plan | runs | blocked | blocked |
| Ask before changes (default) | build | runs | card | card |
| Auto-edit | build | runs | runs, unless destructive | card |
| Bypass | build | runs | runs | runs |

- `GatePolicy.Decide` is pure and returns `Run`, `Ask` or `Block`. Everything else in this ADR follows from its inputs: tier, level, mode, `destructive`, dry run, whether the tool has a Run form, and the allow-lists.
- A dry run (`ToolPresets.IsDryRun`) runs at every level, including Read only. It reads and forecasts and never writes.
- A message typed as `/build` at Read only or Plan is the user's explicit request to write. That turn gates like Ask before changes: each write still shows a card. Without this, "Run plan" could never write.
- Tools switched off in Settings stay off at every level. Bypass does not enable L3.
- Choosing Auto-edit or Bypass shows an in-pane warning first. Bypass shows a persistent bar with Turn off.
- Bypass keeps the Revit failure-dialog rules of ADR-0016, only while a LoadingAI turn group is open.

### The review card

- One gate, `ApprovalGate.ReviewAsync`, serves plain L2 and L3 tools and Run forms. It posts `tool_review { review }` and waits. No native dialog exists: `ConfirmL3NativeAsync` and the L2 "Run X?" dialog are gone.
- The card's summary, rows and warnings are built by the host from the arguments (ADR-0014, ADR-0015). The model's own words are never the confirm text.
- The pane answers with `approveTool { toolCallId, approved, scope?, reason?, args? }`:
  - `scope` is `once`, `session` or `always`. Unknown or missing is `once`.
  - `reason` on a denial goes back to the model as `Cancelled by user (denied): <reason>`.
  - `args` is an edit request (`approved` false): the host validates and coerces the arguments against the tool's schema, rebuilds the card, posts it again with `edited: true` and keeps waiting. An invalid edit keeps the last accepted inputs and adds a warning.
- A Run form's values come back through `submitPreset`, as before. Its card offers no Always or Session scope.
- If the pane is hidden when a review starts, the host shows it (`PaneVisibility`). It never falls back to a dialog.
- A wait is 180 seconds for a plain card and 15 minutes for a Run form, which can need a pick in the model. A timeout cancels. It never approves. Stop cancels every pending review.
- A denial ends the call with a failed result and marks the turn failed, so the turn's earlier writes roll back with it (the single undo step is unchanged). The card says so when earlier writes exist.

### Allow-lists

- Session: tool names allowed with "Allow for this session". In memory only. Cleared by Clear session, a model change and a Revit restart.
- Always: `settings.alwaysAllowTools`. Only a host-side "Always allow" adds a name. A `saveSettings` from the pane can remove names but never add one (`ToolAllowlist.MergeAlways`), so a forged or stale message cannot widen what runs without a card. Settings → Tools lists the names with Remove.
- A tool tagged `destructive` in its `plugin.json` (delete elements, replace elements, write a file) is never allow-listed and never offered Always or Session. A tool with a Run form is never allow-listed either: the form is where its inputs come from.
- An allow-listed call still shows as a step and still runs inside the turn group.

### Host to pane messages

`host_caps` (announced on connect: `singleGate`, `levels`, `autoEdit`, `reviewScopes`, `structuredTurns`, `changes`, `steer`, `theme`, `instructions`), `theme`, `turn_start`, `turn_end`, `tool_start`, `tool_review`, `tool_end`, `turn_changes`, `steer_ack`. The pane shows only the controls the host announced, and falls back to the older text messages when it announced nothing. Call ids are named `toolCallId` on the wire. A host turn ends with `turn_end`, after any steer it could not deliver.

### Pane to host messages

`approveTool` (extended as above), `steer`, `selectElements`, `showElements`, `isolateElements`. There is no `retryStep` message: Retry sends a normal message ("Retry <tool> with the same inputs") so the retried call goes through the same gate, with the same cards, as any other call.

### What a turn changed

`TurnChangeTracker` reads `Application.DocumentChanged` for committed transactions in the turn's document while the turn group is open. It folds repeats (added then modified is added; added then deleted is nothing; modified then deleted is deleted), groups by category, caps ids at 2000 per category (counts stay exact) and resets on a rollback. At the end of a turn the host posts `turn_changes` and, when `selectChangedAfterTurn` is on, selects the added and modified elements. Select, zoom and isolate only change what you see: isolate opens its own small transaction outside the turn group, and none of the three runs while a turn is writing. There is no Undo button, because Revit's API has no supported way to trigger Undo.

### Standing instructions

`settings.instructions` (at most 2000 characters) goes into the system prompt in a marked block that says plainly it cannot change the tiers or the permission level.

## Consequences

- One click confirms a write. An accidental Allow in WebView2 now starts the change with no Revit-thread dialog behind it. The mitigations are: the Allow button names the effect, Enter in the composer never approves, the card is built by the host, Deny is a plain button, the whole turn is one undo step, and destructive tools can never be allow-listed.
- ADR-0004's tests (native confirm for every L3 tool, web card skipped for presets, `ApplyConfirm` dialogs) are replaced by tests of `GatePolicy`, `ApprovalGate`, the allow-lists and the protocol messages.
- The old `autoApprove` flag is kept in the file and on the wire (true exactly when the level is Bypass) so an older pane or settings file still works.
- Revit-side behaviour that cannot run in CI is listed in `docs/pane-redesign-in-revit-checklist.md`.

## Do not

- Do not let a tool argument, tool result, chat message or plugin set the permission level or an allow-list.
- Do not add a native dialog back in front of, or behind, the card.
- Do not let a timeout, Enter in the composer, or a missing answer approve a call.
- Do not offer Always or Session allow for a destructive tool or a tool with a Run form.
- Do not take the card's text from the model's output.
- Do not let the pane add names to `alwaysAllowTools`.
- Do not run Select, Zoom or Isolate while a turn is writing, and do not make them change the model.
- Do not write secrets, or the standing instructions, anywhere but the user's `settings.json`.
