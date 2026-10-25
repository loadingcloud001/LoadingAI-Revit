# LoadingAI pane redesign

- Date: 2026-10-25
- Status: Draft for review
- Branch: `feat/pane-redesign`
- Keeps: [ADR-0004](../../adr/0004-host-owned-l3-confirm.md) gate order, [ADR-0014](../../adr/0014-preview-before-preset-form.md) and [ADR-0015](../../adr/0015-confirm-l2-parameter-writes.md) previews, [ADR-0016](../../adr/0016-auto-approve.md) user-owned auto-approve

## Why

The pane works, but it reads like a debug console:

- Status is shown up to four times: the model line, the context line, the progress bar, and a "Working…" row.
- Asking for permission happens in three places: the L3 card in the chat, the Run panel above the composer, and the Revit dialog.
- Tool steps are raw `name: {json}` text, and the pane works out their state by matching text with patterns.
- The plan and the todo list are separate. The plan's "Edit" button only hides it.
- A turn that changed the model never says what changed.
- Settings open inline, cover up to half the pane, and mix providers, tools and "Clear session".
- Ask, Plan and Build are typed commands. Auto-approve is a separate red header button that uses `window.confirm`.
- There is no dark theme, the labels are 10 px grey on grey, and `App.tsx` is about 1,000 lines in one component.

## What good agent UIs do (research, 2026-10-25)

1. **Show the plan before acting**, with options to run it, edit it, or do the task yourself.
2. **One autonomy control**, from "suggest only" to "act on its own", that can loosen over time.
3. **Tool steps as labelled cards** with input, output and time, folded into an activity view so the chat stays readable.
4. **Reads run freely; writes are confirmed showing their real effect.** Asking too often trains people to click approve without reading.
5. **A record of actions with undo.**
6. **Stop is always one click away.**
7. **Ask a clarifying question when unsure.** Errors say what failed, why, and what to try next.
8. **Calm quota display:** "running low" before it runs out.
9. **Autodesk Assistant in Revit 2027**, the closest competitor, has a prompt library, an Insights tab, a Stop button, and labels its changes in the Undo menu.

Sources: Smashing Magazine "Designing for agentic AI" (2026-02), zylos.ai "Agentic UX: frontend design patterns" (2026-05), p0stman.com agent UX principles, Autodesk "Autodesk Assistant in Revit tech preview" (2026-04), the Revit 2027 What's New help, and Cursor's agent review docs.

## Decisions

| Topic | Decision |
|---|---|
| Scope | Rebuild the whole web pane, plus a few structured messages from the host. |
| Autonomy | One mode picker in the composer: Ask · Plan · Build · Auto. |
| Transcript | Folded work per turn, one review card in the chat, and a change summary at the end of the turn. |
| Look | "Pro console": dark first, live step list, amber for changes. The light theme follows Revit's theme. |
| Settings | Model picker in the composer. The gear opens a full-pane Settings page. |
| Approach | Rebuild in place, in stages, with one PR per stage. |

## Architecture

### Pane

```
web/src/
  App.tsx                 shell: theme, page switch (chat | settings), about 100 lines
  bridge.ts               ToHost / FromHost types and the scripted mock host
  state/
    events.ts             FromHost message -> PaneEvent (normalises new and old messages)
    reducer.ts            PaneState = { turns[], pending, context, quota, settings, plugins, ... }
    legacy.ts             old text-message handling (tool_call/tool_result text, awaiting markers)
    selectors.ts          derived views: activeTurn, liveStatus, canSend, ...
  ui/                     TopBar, Icon, Button, Chip, theme.css (CSS variables)
  chat/                   Transcript, Turn, WorkGroup, StepRow, ReviewCard, ChangeReceipt, ErrorNote, EmptyState
  composer/               Composer, ModeDial, ModelPicker, SlashMenu, MentionMenu, Attachments
  settings/               SettingsPage, ModelsSection, ToolsSection (current PluginShelf), PluginsSection, AboutSection
  lib/                    presetPreview, clipboard, scroll (kept)
  cli/                    inputParser, slashCommands, mentions (kept)
```

- Every host message passes through `events.ts` into one `useReducer`. Components read state and post `ToHost` messages. They do no parsing of their own.
- `legacy.ts` holds the current text handling (`classifyResult`, `callInfo`, `toolStates`) and is used only when the new structured messages are missing. The tests in today's [transcript.tsx](../../../web/src/transcript.tsx) move with it.
- Data shapes:
  - A **turn** is `{ id, mode, userText, attachments, steps[], reviews[], answer, reasoning, changes?, outcome, startedAt, endedAt }`.
  - A **step** is `{ callId, name, tier, args, state: running|awaiting-review|awaiting-revit|done|failed|cancelled|timeout|auto, summary, text, ms }`.

### Theme

- `ui/theme.css` defines the colour variables (`--bg`, `--surface`, `--border`, `--text`, `--muted`, `--accent` blue, `--change` amber, `--ok` green, `--danger` red) under `[data-theme=dark]` and `[data-theme=light]`.
- Dark is the default. The host's `theme` message, or `prefers-color-scheme` when there is no host, picks the theme.
- Text is at least 11 px. Monospace is used only for step lines and step details.

### New host messages (added next to the current ones)

From host to pane:

| Message | Fields | Source |
|---|---|---|
| `turn_start` | `turnId, mode` | `ChatSession` when a send starts |
| `turn_end` | `turnId, outcome: done\|cancelled\|error` | `ChatSession` when the run ends |
| `tool_start` | `turnId, callId, name, tier, args, startedAt` | `ChatSession.ExecuteToolAsync` before the gates |
| `tool_end` | `callId, ok, outcome?, summary, text, ms` | after the handler or a gate outcome. `summary` is the first line of the result, at most 120 characters. |
| `turn_changes` | `turnId, added, modified, deleted, ids[]` (ids capped at 2,000) | new `TurnChangeTracker`: subscribes to `Application.DocumentChanged` while the turn group is open |
| `theme` | `dark: boolean` | `UIThemeManager.CurrentTheme` at load and on `ThemeChanged` (check both against the Revit 2027 API in P3) |

From pane to host:

| Message | Fields | Host action |
|---|---|---|
| `selectElements` | `ids[]` | `uidoc.Selection.SetElementIds` on ids that still exist |
| `isolateElements` | `ids[]` | temporary isolate in the active view (`IsolateElementsTemporary`), in its own small transaction outside any turn group |

- The host keeps sending `tool_call`, `tool_result`, `progress` and `status` exactly as today. The pane uses the structured messages whenever it has seen `turn_start` for the current turn.
- `HostProtocolTests` (the ToHost count of 21 rises to 23; `FromHostKind.All` adds six) and the `FromHostMessage` records are updated in the same PR.

### What stays the same

- The gates in ADR-0004: plain L3 is the web card then the native dialog (default No); a Run form is the web step, then the native dialog for L3; L2 without a form is the native dialog only; a dry run skips both gates.
- ADR-0016: the model cannot turn on auto-approve; Plan and Ask never write; switched-off tools stay off.
- Approve, Deny, the Run form, Pick, Stop and checkpoints keep their current messages.

## Screens and behaviour

### Top bar

The top bar shows `LoadingAI`, a context chip (active view, selection count, warnings), a history button (checkpoints and `/resume`), and the settings gear. There is one live status line under the top bar, and only while a turn runs. It replaces the context line, the progress bar and the "Working…" row.

### Chat

- **Your message** sits on a blue left edge, with any attached images.
- **Work group.**
  - While the turn runs, the group is open. Each step shows a state icon, the tool name, the result summary and its time.
  - When the turn ends, it folds to `Worked · N steps · 3.2s ▸`.
  - Reasoning shows inside it as `Thought for Ns`, and the plan and todo items show as its first rows.
  - Clicking a step opens its full input and output, with Copy.
- **Plan mode.** The plan shows as a card with **Run plan** (sends "Execute the plan." in Build), **Edit** (puts the plan text in the composer) and **Discard**.
- **Review card**, the only permission prompt in the pane:
  - It shows the summary, detail rows and warnings from the existing preview code (`approvalLines`, `previewRows`, `previewWarnings`). The amber edge marks L3, and a tier badge is shown.
  - **Plain L3:** Deny / Allow. A note reads "Revit will ask once more. Default is No." There is no Dry run button here, because `approveTool` cannot change the arguments.
  - **Run form (L2 or L3):** the fields go inside the card, including Pick, with Cancel / Confirm. The panel above the composer is removed. When the form has a `dryRun` field, a **Dry run** button sets it and confirms; a dry run skips the native dialog, per ADR-0004.
  - **L2 without a form:** a status row, "Confirm in the Revit dialog". There are no buttons, because the native dialog is the gate.
  - **Auto mode:** no card. Steps carry an `auto` badge.
  - **Outcomes:** the card collapses to one line: Allowed, Denied, Timed out (180 s) or Cancelled.
- **Change summary.** After `turn_changes` with a non-zero total, the turn shows `36 changed · 1 undo step (LoadingAI turn) · Ctrl+Z`, with **Select** and **Isolate**. Read-only turns show no summary. There is no Undo button: Revit's API has no supported way to trigger Undo, and the Autodesk forum advises against posting the Undo command.
- **Answer:** the agent's markdown answer, with streaming kept.
- **Errors:** one line in the form "what failed · why · what to try next", with an action where one fits (Open settings, Retry). Raw "Error:" prefixes are never shown. Quota errors show the reset time and an "Add your own key" link.
- **Past turns** fold to one line, the first 60 characters of your message plus the change count. The current and last turn stay open.

### Composer

- The text box is an auto-growing textarea, 2 to 8 lines, and stays editable while a turn runs. Send is locked until the turn ends; there is no queue.
- **Mode picker:**
  - **Ask:** read only.
  - **Plan:** propose steps, no writes.
  - **Build:** the default. It asks before making changes.
  - **Auto:** no prompts. Choosing it shows an in-pane confirmation (no `window.confirm`) with the ADR-0016 wording, then saves `autoApprove: true` and sends `build`. Leaving Auto saves `autoApprove: false`.
  - The chosen mode is stored in `localStorage`, except Auto, which lives in settings. `/ask` and `/plan` still work for one message.
- **Model picker:** a popover listing the providers you've added, with a "Manage models" item that opens Settings.
- **Images:** an icon button, paste and drop, up to 5.
- **Send / Stop:** Send turns into Stop while running. Esc sends Stop when the composer has focus.
- `/` commands and `@` mentions are unchanged.

### Empty state

- A heading line, then "Reads run freely. Changes ask first. Each turn is one undo step."
- Four starter prompts from a static map keyed by enabled plugin ids, filtered to plugins that are switched on. The host's `suggestions` message, which no code sends today, overrides the map if it ever arrives.
- One line with the tool count, the L3 state and the free quota.

### Settings page

The settings page fills the pane, has a Back button, and has four sections:

- **Models:**
  - Each provider is a card showing its name, model, connection state and, for the built-in model, the quota.
  - Opening a card shows the current edit form, restyled, with Test, Fetch models, Use, Save and Delete.
  - The card also has "Add from presets" and "Custom".
- **Tools:** the current `PluginShelf` tier and tool switches, restyled, with L3 in amber.
- **Plugins:** load failures, notices, "Trust this plugin…", Rescan and Copy diagnostics.
- **About:** the version, log folder path and diagnostics, plus a "Clear session" button moved here from the provider strip.

### Accessibility

The status line is a screen-reader live region. Every control has a label and can be reached from the keyboard, focus rings are visible, and colour contrast meets WCAG AA in both themes.

## Stages

Each stage is one PR from `feat/pane-redesign`, or a stage branch off it. The pane works after every stage.

| Stage | Contents | Checks |
|---|---|---|
| P0 Foundation | `state/` reducer and events, with legacy parsing moved over. App split into components, behaviour unchanged. The C# contract tests that read `App.tsx` move to the new file paths. | vitest; CI C# tests; `npm run build`; dist committed |
| P1 Look and frame | Theme variables and both themes, top bar, composer with mode and model pickers, Settings page | vitest; browser preview at 386 px in both themes; CI |
| P2 Chat | Turn, WorkGroup, StepRow, ReviewCard (Run form inside), ErrorNote, EmptyState. The scripted mock host covers every case. | vitest reducer cases; browser preview; CI |
| P3 Host messages | `FromHostMessage` records, `ChatSession` posts, `TurnChangeTracker`, theme, select and isolate. The pane uses structured messages with the old ones as fallback. | xunit on CI; in-Revit check on the maintainer's Windows PC |
| P4 Polish and docs | Keyboard and screen-reader pass, ADR-0017 (pane protocol and mode picker), README screenshots, version bump | review |

## Testing

- **Reducer:** vitest tests replay recorded host message sequences and check the resulting turns. The cases are an L1 question; an L2 Run form with Confirm and with Cancel; plain L3 with Allow then Revit No; an approval timeout; Stop while waiting for a review; a dry run; Auto mode; a provider error; quota used up; and a turn with no `turn_start` (legacy fallback).
- **View models:** pure functions (step summary, receipt text, error copy) get unit tests. There is no DOM test library.
- **Mock host:** in [bridge.ts](../../../web/src/bridge.ts) it becomes a scripted demo chosen by `?demo=<case>`, so each screen can be checked in a browser preview without Revit.
- **C# tests:**
  - Serialization of the new messages.
  - `TurnChangeTracker` counting: added, modified and deleted are de-duplicated, and an element added then modified counts as added.
  - Updated protocol counts.
- **In Revit** (maintainer, after P3), a checklist:
  - L1 question.
  - Colour by parameter: change summary, Select, Isolate.
  - Split beams: dry run, then a write; Ctrl+Z undoes the turn.
  - Plain L3 Deny.
  - Auto mode on and off.
  - Switching Revit's theme changes the pane.

## Risks

- **Revit's undo stack:** the tracker counts changes inside the turn group only; edits outside a LoadingAI turn are ignored.
- **Unmatched results:** some tool results arrive without a matching call (preset validation, hints). The reducer attaches them to the open step with that name, or as a turn note, the same as today.
- **Bundle size:** no new runtime dependencies are planned. `open-model-selector` stays, and only inside Settings.
- **C# builds only on Windows:** P3 relies on CI for build and unit tests, and on the maintainer for the in-Revit check.

## Out of scope

- An Undo button.
- Any change to the gate order or auto-approve rules.
- New tools.
- A third-party agent-UI library.
- A message queue while running.
- An Insights tab.
