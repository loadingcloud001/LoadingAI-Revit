# LoadingAI pane redesign

- Date: 2026-10-25 (revision 2)
- Status: Draft for review
- Branch: `feat/pane-redesign`
- Changes: [ADR-0004](../../adr/0004-host-owned-l3-confirm.md) gate order (the new ADR-0017 replaces it); [ADR-0016](../../adr/0016-auto-approve.md) auto-approve becomes the "Bypass" permission level
- Keeps: [ADR-0014](../../adr/0014-preview-before-preset-form.md) and [ADR-0015](../../adr/0015-confirm-l2-parameter-writes.md) previews; host-owned confirms; one undo step per turn

## Why

The pane works, but it reads like a debug console:

- **Status is shown up to four times:** the model line, the context line, the progress bar, and a "Working…" row.
- **One L3 action asks twice.** It shows a card in the chat, and then a Revit dialog for the same action. L2 tools without a form use a third style, a Revit dialog only. Run forms sit in a fourth place, above the composer.
- **Tool steps are raw text.** Each is `name: {json}`, and the pane works out its state by matching text with patterns.
- **The plan and the todo list are separate,** and the plan's "Edit" button only hides it.
- **A turn that changed the model never says what changed.**
- **Settings open inline,** cover up to half the pane, and mix providers, tools and "Clear session".
- **Modes are typed commands.** Ask, Plan and Build are `/ask` and `/plan`, and Auto-approve is a separate red header button that uses `window.confirm`.
- **The look is dated:** no dark theme, labels 10 px grey on grey, and `App.tsx` is about 1,000 lines in one component.

## What good agent apps do (research, 2026-10-25)

| Source | Pattern | Taken here as |
|---|---|---|
| VS Code Copilot | Permission picker in the chat box (Manual, Assisted, Allow all, Autopilot). Approvals scoped to once, session, workspace or always. A one-time warning before the loosest levels. | Five-level permission picker; Allow once, for the session, or always |
| Claude Code | Modes switched with Shift+Tab (default, accept edits, plan, bypass), always shown in the status bar | Auto-edit level; a persistent Bypass bar |
| Copilot CLI, JetBrains | Allow once, Always allow, or reject and tell the agent what to do instead | Deny with a reason |
| Cursor, Replit, Augment | Queue follow-ups (editable, auto-paused on error); steer at the next tool call; checkpoints | Queue and Send now |
| Excel Copilot | Every step listed in the pane; changes shown in the sheet; review or revert | Changed elements auto-selected; Zoom, Isolate and a per-category list |
| ChatGPT agent | Confirms high-impact actions; take over or stop; progress shown | One clear review card; Stop always visible |
| Confirmation-gate guidance (agentpatterns.ai) | Gate only consequential actions; make "No" easy; never approve on timeout; build the confirm text from the actual action, not the model's summary | Card content built by the host from the arguments; timeout cancels; Deny is never the risky click |
| Smashing Magazine, zylos.ai, p0stman | Show the plan before acting; an autonomy dial; tool cards; an audit log with undo; escalation; errors that say what to do next | Plan card, WorkGroup, change receipt, error copy |
| Anthropic, "Building effective agents" | Keep it simple, show the agent's planning, design the agent-tool interface carefully | Visible plan and steps; host-owned previews |
| Agent UX checklists (parallelhq, uxmag) | Retry one step instead of the whole turn; show partial results; easy reset; remember preferences only with consent | Retry step, partial-result receipts, custom instructions the user writes |

## Decisions

| Topic | Decision |
|---|---|
| Scope | Rebuild the whole web pane, plus the host changes listed below. |
| Confirm | **One confirm, in the chat card,** for every gated tool (plain L3, L2 without a form, and any Run form). LoadingAI shows no Revit dialog of its own. |
| Permission levels | One picker by Send: **Read only · Plan · Ask before changes** (default) **· Auto-edit · Bypass**. |
| Review card | Allow once · Allow this session · Always allow · Deny with a reason · Edit inputs before allowing. |
| After a change | Changed elements are auto-selected (setting, on by default); the receipt has Zoom to changes, Isolate and a per-category list. |
| While running | Stop (and Esc), queue follow-ups, Send now (steer). No Pause. |
| Extras | Retry a failed step; partial-result receipts; custom instructions; a first-run intro; a running-low quota state. |
| Look | "Pro console": dark first, live step list, amber for changes. The light theme follows Revit's theme. |
| Settings | Model picker in the composer. The gear opens a full-pane Settings page. |
| Approach | Rebuild in place, in stages, one PR per stage. |

## Permission model (new ADR-0017)

### Levels

The picker value is a user setting (`permissionLevel`). The pane never derives it from the model, and the model cannot change it: there is no tool for it, and no chat text or tool result sets it.

| Level | Mode sent | Read (L1) | L2 write | L3 write | Revit failure dialogs during a turn |
|---|---|---|---|---|---|
| Read only | `ask` | runs | blocked | blocked | Revit's normal behaviour |
| Plan | `plan` | runs | blocked | blocked | Revit's normal behaviour |
| Ask before changes (default) | `build` | runs | card | card | Revit's normal behaviour |
| Auto-edit | `build` | runs | runs | card | Revit's normal behaviour |
| Bypass | `build` | runs | runs | runs | dismissed under the ADR-0016 rules |

- Switched-off tools stay off at every level. L3 is still off by default in the Tools section.
- Choosing Auto-edit or Bypass shows a one-time in-pane warning (no `window.confirm`). Bypass then shows a persistent amber bar, "Bypass on · changes run without asking · Turn off", above the composer.
- The existing `autoApprove` setting maps to Bypass. `ChatSession` reads `permissionLevel` and treats `autoApprove: true` with no level as Bypass, so old settings still work.
- Dry runs (`ToolPresets.IsDryRun`) skip the card at every level, as today.

### The single gate

- `ChatSession.ExecuteToolAsync` runs one gate, `ApprovalGate.ReviewAsync(entry, args, preview)`, for every tool the level requires.
- It posts `tool_review` to the pane and waits on the existing completion source, with a 180 s timeout. A timeout cancels; it never approves.
- The native dialogs are removed: `ConfirmL3NativeAsync`, and the L2 dialog in `ToolGuards.ApplyConfirm`.
- Run forms become the review card's fields. The separate preset step is folded into the same gate.
- The card's summary, detail rows and warnings come from the host's existing preview builders (ADR-0014/0015), computed from the arguments. The model's own words never appear in the card.
- **If the pane is hidden** when a review starts, the host shows the dockable pane (`DockablePane.Show()`) and flashes the review card. It does not fall back to a dialog.

### Card actions

The pane sends `approveTool { toolCallId, approved, scope, reason?, args? }`:

- `scope`:
  - `once`;
  - `session`: the host adds the tool name to a session allowlist, cleared on Clear session, model change, or Revit restart;
  - `always`: the host adds the tool name to `settings.alwaysAllowTools`, which Settings → Tools shows and lets you remove.
- `reason` (on Deny) is returned to the agent: `Cancelled by user (denied): <reason>`.
- `args` (Edit inputs) holds the edited arguments. The host re-validates them against the tool's schema, rebuilds the preview, and re-posts the card if the effect changed. Only changes accepted in the second card run.
- **"Always allow" is not offered for destructive tools.** These are tools tagged `destructive` (delete elements, overwrite or save files), for which the card offers once and session only. A new `Destructive` flag goes on `ToolEntry`, set in each plugin's `plugin.json`.

### Safety rules (kept or added)

- The host owns every gate; plugins never show dialogs.
- Deny is a plain button; the Allow button names the effect ("Split 8 beams", "Delete 12 elements").
- Enter in the composer never approves. Card buttons need a click, or a focused button plus Space/Enter.
- Stop denies every pending review (`DenyAllPending`).
- One undo step per turn (`TransactionGroup` "LoadingAI turn") is unchanged.

## Architecture

### Pane

```
web/src/
  App.tsx                 shell: theme, page switch (chat | settings), about 100 lines
  bridge.ts               ToHost / FromHost types and the scripted mock host (?demo=<case>)
  state/
    events.ts             FromHost message -> PaneEvent (normalises new and old messages)
    reducer.ts            PaneState = { turns[], queue[], review, context, quota, settings, plugins, ui }
    legacy.ts             old text-message handling (moved from transcript.tsx)
    selectors.ts          activeTurn, liveStatus, canSend, permission
  ui/                     TopBar, BypassBar, Icon, Button, Chip, Popover, theme.css
  chat/                   Transcript, Turn, WorkGroup, StepRow, PlanCard, ReviewCard, ChangeReceipt, ErrorNote, EmptyState, Intro
  composer/               Composer, PermissionPicker, ModelPicker, Queue, SlashMenu, MentionMenu, Attachments
  settings/               SettingsPage, ModelsSection, AgentSection, ToolsSection, PluginsSection, AboutSection
  lib/ cli/               kept
```

- A **turn** is `{ id, level, userText, attachments, plan?, steps[], answer, reasoning, changes?, outcome, startedAt, endedAt }`.
- A **step** is `{ callId, name, tier, args, state, review?, summary, text, ms, auto }`. `state` is one of `running`, `review`, `done`, `failed`, `denied`, `timeout` or `cancelled`.
- `legacy.ts` builds the same turns from today's text messages whenever `turn_start` is missing, so the pane works against an old host.

### Theme

- `ui/theme.css` defines the colour variables (`--bg`, `--surface`, `--raised`, `--border`, `--text`, `--muted`, `--accent` blue, `--change` amber, `--ok` green, `--danger` red) under `[data-theme=dark]` and `[data-theme=light]`.
- Dark is the default. The host's `theme` message, or `prefers-color-scheme` when there is no host, picks the theme.
- Text is at least 11 px. Monospace is used only for step lines and step details.

### Host messages

New messages from host to pane, sent next to the current ones:

| Message | Fields |
|---|---|
| `turn_start` | `turnId, mode, level` |
| `turn_end` | `turnId, outcome: done\|cancelled\|error` |
| `tool_start` | `turnId, callId, name, tier, destructive, args, startedAt` |
| `tool_review` | `callId, name, tier, destructive, summary, rows[], warnings[], fields[]?, values?, canAlways` (replaces `tool_approval` and `tool_preset`; the host sends both old and new during P3) |
| `tool_end` | `callId, ok, outcome?, summary, text, ms, partial?{ done, failed, reasons[] }` |
| `turn_changes` | `turnId, added, modified, deleted, byCategory[{ category, added, modified, deleted, ids[] }]` (ids capped at 2,000) |
| `theme` | `dark` |
| `steer_ack` | `text, deliveredAt` |

New and changed messages from pane to host:

| Message | Fields | Host action |
|---|---|---|
| `approveTool` (extended) | `scope, reason?, args?` | see "Card actions" |
| `steer` | `text` | injected as a user note after the next tool result in the current turn |
| `retryStep` | `callId` | re-runs that tool call with the same arguments, in a new turn |
| `selectElements` | `ids[]` | `Selection.SetElementIds` on ids that still exist |
| `showElements` | `ids[]` | `UIDocument.ShowElements` |
| `isolateElements` | `ids[]` | `IsolateElementsTemporary` in the active view, in its own small transaction outside any turn group |
| `saveSettings` (extended) | `permissionLevel, alwaysAllowTools[], selectChangedAfterTurn, instructions` | persisted; `instructions` is added to the system prompt inside a clearly marked user-instructions block |

- `TurnChangeTracker` subscribes to `Application.DocumentChanged` while the turn group is open. It removes duplicates (an element added then modified counts as added) and groups by category name. At `turn_end` it posts `turn_changes`, and when `selectChangedAfterTurn` is on, it selects the added and modified elements.
- `HostProtocolTests`, `FromHostKind.All`, the `FromHostMessage` and `ToHostMessage` records, and the tests that lock ADR-0004 are updated in the same PR. Those tests are `HostPage_L3NativeConfirm_AppliesToEveryL3Tool`, `HostPage_SkipsL3ConfirmForDryRun_ViaGenericArgCheck`, `HostPage_PresetMessages_SkipL3WebCardKeepNativeConfirm`, and the `ApplyConfirm` tests.

## Screens and behaviour

### Top bar

The top bar shows `LoadingAI`, a context chip, a history button, and the settings gear.

- **Context chip:** active view, selection count and warnings. Clicking it lists what will be attached to the next message, with remove toggles for @-mentions.
- **History:** saved sessions you can resume, with titles.
- **Status line:** one line, under the top bar, only while a turn runs.

### Chat

- **Your message** sits on a blue left edge. **Edit and resend** is offered on your last message.
- **Work group.**
  - While running, the group is open with live steps: an icon, the name, a one-line result and the time.
  - When the turn ends, it folds to `Worked · N steps · 3.2s ▸`.
  - Reasoning shows inside it as `Thought for Ns`. Clicking a step opens its input and output, with Copy.
  - A failed step shows **Retry step**.
  - Steps that ran without a card carry `auto`, `session` or `always` badges.
- **Plan card** (Plan level): numbered steps with **Run plan** (switches that message to Ask before changes and sends "Execute the plan."), **Edit** (puts the plan in the composer) and **Discard**.
- **Review card:**
  - An amber edge for L3 and a red tag for destructive tools.
  - The summary, rows and warnings, and the fields when present (including Pick).
  - **Edit inputs** expands the key arguments as fields.
  - Buttons: `Deny ▾` (with a reason box) and `Allow <effect> ▾` (once, this session, always). A **Dry run** button appears when the tool has a `dryRun` field.
  - A countdown appears in the last 30 s before the 180 s timeout.
  - After a decision, the card collapses to one line: Allowed (scope), Denied (reason), Timed out or Cancelled.
- **Change receipt** (when the turn changed anything):
  - `36 changed · selected · 1 undo step (LoadingAI turn) · Ctrl+Z`.
  - Buttons: **Zoom to**, **Isolate**, and a **▸ by category** list ("Structural Framing · 36 added · 8 deleted"). Each row selects its elements.
  - Partial results show as "6 of 8 split · 2 failed: <reason>".
  - There is no Undo button: Revit's API has no supported way to trigger Undo.
- **Answer:** the agent's markdown answer, with streaming kept, and Copy.
- **Errors:** one line in the form "what failed · why · what to try next", with an action (Retry, Open settings, Add your own key). Raw "Error:" prefixes are never shown.
- **Past turns** fold to one line, the first 60 characters of your message plus the change count. The last turn stays open.

### Composer

- **Text box:** an auto-growing textarea, 2 to 8 lines, always editable.
- **While a turn runs:**
  - Enter queues the message. Queued items show above the composer as chips labelled "queued", which you can edit or remove.
  - **Send now** delivers the text as a steer note, shown in the turn as "Note sent at step N".
  - The queue sends the next item on `turn_end`. It pauses after an error, a denial or a Stop, with Resume and Clear.
- **Permission picker:** five levels, with icons and one-line explanations. The current level is always visible, in amber for Auto-edit and red for Bypass. Ctrl+. cycles through the levels.
- **Model picker:** a popover listing the providers you've added, plus "Manage models".
- **Images:** an icon button, paste and drop, up to 5.
- **Send / Stop:** Stop is shown while running, and Esc stops.
- `/` commands and `@` mentions are unchanged. `/ask` and `/plan` apply to one message only.

### Empty state and first run

- **Empty state:** "What should we do in this model?" and "Reads run freely. Changes ask first. Each turn is one undo step." Then four starter prompts from a static map keyed by the enabled plugins, and one line with the tool count, the L3 state and the quota.
- **First run:** three short cards, dismissible: what it can do; how permissions work (the picker, the review card, one undo step per turn); choosing a model (the built-in free model or your own key).
- **Quota:** when 10 or fewer free requests are left, the status shows "Running low · 7 left today" in amber. At zero, the error shows the reset time and "Add your own key".

### Settings page

The settings page fills the pane, has a Back button, and has five sections:

- **Models:** provider cards with Test, Fetch models, Use, Save and Delete; "Add from presets" and "Custom".
- **Agent:**
  - default permission level;
  - "Select changed elements after each turn" (on by default);
  - custom instructions, a textarea with the hint "Applies to every chat. Example: Our levels are named L01, L02…; lengths in mm.";
  - a Clear session button.
- **Tools:** the tier and tool switches, plus the **Always allowed** list with Remove buttons.
- **Plugins:** load failures, notices, "Trust this plugin…", Rescan and Copy diagnostics.
- **About:** version, log folder, and diagnostics.

### Accessibility

The status line and the review card are screen-reader live regions. Every control has a label and can be reached from the keyboard, focus rings are visible, and colour contrast meets WCAG AA in both themes.

## Stages

Each stage is one PR on `feat/pane-redesign`, or a stage branch off it. The pane works after every stage.

| Stage | Contents | Checks |
|---|---|---|
| P0 State layer | `state/` types, reducer and the legacy mapping from today's host messages, covered by vitest replay tests, plus the scripted mock host. The current `App.tsx` is untouched, so the pane is unchanged. | vitest; `tsc` |
| P1 New pane | Theme, top bar, composer (permission and model pickers, queue chips), chat (Turn, WorkGroup, StepRow, PlanCard, ReviewCard, ChangeReceipt, ErrorNote, EmptyState, Intro) and the Settings page, all on the P0 state. Replaces the old `App.tsx`. Actions the host doesn't support yet are hidden until the host says it supports them. The C# contract tests that read `App.tsx` are repointed. | vitest; browser preview at 386 px in both themes; `dotnet build` here; C# tests on CI |
| P3 Host | ADR-0017; single gate; permission levels; scopes and allowlists; edit inputs; destructive flag; `TurnChangeTracker`; select, zoom and isolate; steer; retry; theme; instructions; the new messages | xunit on CI; in-Revit checklist on the maintainer's Windows PC |
| P4 Polish and docs | Keyboard and screen-reader pass, README and screenshots, ADR index, version bump | review |

## Testing

- **Reducer:** vitest tests replay recorded message sequences and check the resulting turns. The cases are:
  - an L1 question;
  - L2 at Ask before changes, Allow once;
  - L2 at Auto-edit, which runs with no card;
  - L3: Allow for the session, after which a second call has no card;
  - Deny with a reason;
  - edit inputs, then a re-posted card;
  - a timeout;
  - Stop while waiting for review;
  - a dry run;
  - Bypass;
  - a queue of two items;
  - a steer note;
  - a failed step, then Retry;
  - partial results;
  - an error, then the queue pausing;
  - quota running low;
  - legacy text messages only.
- **View models:** pure functions (receipt text, error copy, permission labels, effect labels) get unit tests.
- **Mock host:** in [bridge.ts](../../../web/src/bridge.ts) it becomes a scripted demo chosen by `?demo=<case>`, so each screen can be checked in a browser preview at 386 px in both themes.
- **C# tests:**
  - serialization of the new messages;
  - `TurnChangeTracker` de-duplication and grouping;
  - the permission-level matrix: which gate runs for each tier and level;
  - session and always allowlists;
  - destructive tools never get `canAlways`;
  - a timeout cancels;
  - edited arguments are re-validated.
- **In Revit** (maintainer, after P3), a checklist:
  - L1 question;
  - colour by parameter: auto-select, Zoom, Isolate, the category list;
  - split beams: dry run, then a write; Ctrl+Z undoes the turn;
  - plain L3 Deny with a reason, and the agent replies to the reason;
  - Allow for this session;
  - Always allow, then remove it in Settings;
  - Auto-edit and Bypass;
  - a review while the pane is hidden;
  - switching Revit's theme;
  - custom instructions are respected.

## Risks

- **Removing the native confirm** means an accidental Allow click in WebView2 starts a write. Mitigations: the effect-named button, no keyboard approve from the composer, host-built previews, one undo step per turn, and destructive tools excluded from Always allow. ADR-0017 records this trade-off.
- **Revit's undo stack:** the tracker counts changes inside the turn group only.
- **Unmatched results:** some tool results arrive without a matching call. They attach to the open step with that name, or become a turn note.
- **Bundle size:** no new runtime dependencies.
- **C# builds only on Windows:** P3 relies on CI and the maintainer's in-Revit check.

## Out of scope

- An Undo button.
- An Assisted (LLM-judge) permission level.
- Pause.
- New tools.
- A third-party agent-UI library.
- An Insights tab.
- Thumbs up/down feedback.
