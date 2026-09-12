# Revit AI Assistant → Hermes / Kilo / Claude-class Autonomous Agent Plan

**Date**: 2026-08-24 (updated: confirmed no ready-made Revit-compatible solution; enhance the existing RevitAiPoc base)
**Goal**: Upgrade the existing Revit AI add-in (narrow WebView2 pane + AgentLoop + PluginLoader) into an agent with autonomous planning, execution, and resume. UI/UX targets Hermes / Kilo Code / Claude Code / Codex. Keep a single pane that also hosts CLI. Start with CLI, and make usability production-ready.

## 1. Confirmed decisions (from interviews)

- **Autonomy mode**: `Plan → Approve → Act`. One user sentence → Agent generates a Plan card with todos → user approves (or per-item L3 approval) → Agent autonomously chains tools/plugins. L3 needs a second confirmation via TaskDialog.
- **UI form**: Keep a 340px single Dockable Pane. Do not widen or pop a new window, but support CLI inside it. The single pane is the container; CLI is input enhancement plus a collapsible terminal.
- **CLI**: Both. 1) `/slash` commands and `@mention` context in the chat box (v1). 2) Collapsible `xterm.js` terminal at the bottom (v2). Both share the same command router.
- **Autonomy**: Auto-collect context + memory/resume + parallel sub-agents. The agent should read selection / active view / visible elements / warnings. Sessions persist, roll back, and retry. Later, sub-agents can split parallel subtasks.

## 2. Benchmark analysis and reuse strategy

> **Follow-up: among 50 popular agents, can any run in Revit as-is? Answer: no.** They are all code/web agents (VS Code plugins, CLIs, cloud, browser). Their host is a filesystem / terminal / browser. There is no Revit process, Revit API, ExternalEvent, Transaction, ElementId, families, views, or warnings. Even the same model via OpenRouter still lacks Revit-side tools and a host bridge. There is also no ready open-source Revit agent in the Autodesk ecosystem. The practical path is to reuse their UI/CLI patterns and open-source pieces on the existing RevitAiPoc host, not to install those products.

- **Kilo Code** (#3, 401B tokens, VS Code/JetBrains/CLI): top `Ask/Plan/Build/Auto` mode switcher, Plan todo list, tool timeline (collapsible cards), @file/symbol mentions, Diff preview. **Already reused here**: mode switcher and Plan card. **Still needed**: Todo state machine, @mentions, unify tool cards into a Kilo-style timeline. Kilo is open source so its Command/Palette + Timeline can be copied, but its host is the VS Code Extension Host and cannot run inside Revit WebView2.
- **Claude Code** (#2, 685B, Anthropic CLI): CLI-first, `/command` slash, continuous autonomous runs, interrupt/resume, sub-agents. Reuse idea: parse `/` and `@` in the input box; AgentLoop supports an interruptible 64-turn loop + checkpoint. Claude Code is a closed CLI and cannot embed in Revit, but slash + sub-agent interaction can be copied 1:1.
- **Hermes Agent** (#1, 2.31T, Nous Research, open-source self-evolving): persistent memory, 40+ tools, scheduled automation, sub-agents, skill generation. Reuse idea: cross-session memory + skill generation maps to our `CheckpointStore` + `ToolRegistry` plugin selection. Hermes is a general desktop agent with no Revit tools; Revit tools must be rewritten.
- **Codex / Cline / Roo etc.**: same story — file/terminal hosts, no Revit binding.

**Conclusion**: there is no zero-cost ready solution. Kilo/Hermes are best for copying design and components, but the host must stay the existing `RevitAiPoc` bridge (`RevitBridge.cs + HostPage.xaml.cs + WebView2`). Forking one of those repos and adding a Revit adapter would cost more than enhancing the codebase that already talks to Revit.

**Do not reinvent UI infrastructure**: Tailwind + shadcn + lucide + open-model-selector already exist. New deps are only `xterm.js` + `xterm-addon-fit` (MIT) and an `@mention` dropdown (reuse shadcn Command). Do not add a heavy editor.

## 3. Current state and gaps

- **Current**: `src/RevitAiPoc/Llm/AgentLoop.cs:42-212` 64 turns + ToolSelector Top-20 + HistoryTrimmer 40 messages + Mode filter; `HostPage.xaml.cs:227-529` WebView2 bridge; `web/src/App.tsx:80-778` single-pane Chat + Plan/Approval/Reasoning cards + attachment compression; `ProviderSettings.cs` multi-provider; `Plugins/Core` dynamic load.
- **Gaps**:
  1. No `/` command router or `@` context; input is free text only
  2. Plan is post-hoc text parse (`PlanParser.cs`), not structured agent output; no Todo state sync (`pending→running→done`)
  3. Tool timeline and Approval are separate; L3 is not truly blocking (already noted); no Diff/preview (e.g. parameter before/after)
  4. No terminal pane; logs/errors are scattered in the message stream
  5. No auto context injection (current selection/view must be described by the user)
  6. No session persist/rollback/retry, no sub-agents

## 4. Target architecture

```
[Web Chat Input] --"/cmd + @ctx + text + attachments"--> [CLI Router (web)] --"send {text, attachments, mode, slash, mentions}"--> [HostPage]
   |                                                                               |
   |<-- Suggestion/Reasoning/Plan/Todo/Tool Timeline <-- [AgentLoop (Planner→Executor)] <--> [ToolRegistry + PluginLoader]
   |                                                                               |
   +-- [xterm Terminal (collapsible)] <-- log stream (tool_call/result, audit) --+
   |                                                                               |
   +-- [Context Provider] -- auto inject selection/view/warnings --> prompt   [Checkpoint Store] --> resume/rollback
```

**Core change points**:
- `web/src/cli/` new module: `slashCommands.ts` (command table + arg completion), `mentions.ts` (@selection/view/family/warnings list), `inputParser.ts` (parse raw input into {slash, mentions, cleanText})
- `web/src/components/Terminal.tsx`: xterm.js collapsible pane, receives `tool_call/result/error` stream, can accept the next `/` command
- `src/RevitAiPoc/Llm/AgentLoop.cs`: split into `Planner` (Plan mode L1 only, structured Plan) and `Executor` (Build mode runs todos); add `TodoItem {id,title,tool,args,status,error}` state machine, push via `onTodoUpdate`; add `subAgent` placeholder (Executor can spawn child AgentLoops to query multiple views in parallel)
- `src/RevitAiPoc/Revit/ContextCollector.cs` new class: `Collect()` returns `{selection, activeView, visibleCount, warnings}` and injects it at the end of the system prompt before each `RunAsync`
- `src/RevitAiPoc/Llm/CheckpointStore.cs` new class: save `history + todos + mode` to `%AppData%\RevitAiPoc\checkpoints\{id}.json`, support `list/resume/rollback`
- `src/RevitAiPoc/Ui/HostPage.xaml.cs`: `send` parses `slash, mentions`; `ExecuteToolAsync` adds a true blocking `TaskCompletionSource` wait for `approveTool` (planned for phase 2); v1 keeps TaskDialog as fallback

## 5. UI/UX detail (single pane + CLI)

**Layout (inside 340px, top to bottom)**:
1. **Header**: Provider capsule (existing) + Mode switcher (Ask/Plan/Build/Auto, existing) + top-right Terminal toggle (`_Terminal` icon)
2. **Context Bar** (new, 22px, collapsible): show `Selected 3 | Active view: Level 1 | Visible 412 | Warnings 2`. Click to expand details; highlight when using @
3. **Message Timeline** (existing, polish): unify to Kilo timeline style
   - `user` bubble (attachment thumbs)
   - `reasoning` light yellow, collapsible
   - `plan` card (title + description + Todo list; status dots: pending gray / running blue pulse / done green / error red; bottom "Approve and run / Adjust")
   - `tool_call` blue card (tool name + arg preview, expandable JSON)
   - `tool_result` green/red card (truncate at 400 chars, copyable)
   - `approval` amber card (L3 pending, approve/reject)
   - `assistant` Markdown
   - `suggestions` horizontal chips
4. **Terminal** (new, collapsible, default collapsed): xterm height 120px, same stream as plain-text log; user can keep typing `/clear /resume`; sync with Chat input
5. **Composer** (existing, enhanced):
   - `/` opens Command Palette (shadcn Command dropdown, `/plan /build /ask /clear /help /export /isolate`, etc.); `@` opens Mention dropdown (`@selection @activeView @warnings @levels`, etc.)
   - Placeholder already changes by mode; add a `/` hint line `Type / for commands, @ to mention context`
   - Keep send button; add `↑` history recall

**Usability details**:
- `/` and `@` both support up/down, Tab complete, Esc to close
- After Plan approve, auto-switch to Build and replay "execute the previous plan" — already implemented, keep it
- While tools run, composer is locked but the terminal can type `stop`, consistent with existing `isRunning`
- Errors and empty states should offer next actions (e.g. "Need a key → open settings" already done; add "Context empty → select a few elements first")

## 6. CLI command table (v1)

| Command | Args | Description | Example tool |
|---|---|---|---|
| `/plan <goal>` | - | Force Plan mode (same as clicking Plan) | L1 only |
| `/build <goal>` | - | Force Build mode | All tools |
| `/ask <question>` | - | Force Ask mode | L1 |
| `/clear` | - | Clear session | `clear` |
| `/help` | - | List commands | local |
| `/export <fmt>` | `ifc/dwg/pdf` | Quick export | `export_*` |
| `/isolate [cat]` | optional category | Isolate current selection | `isolate_in_view` |
| `/hide` | - | Hide selection | `hide_in_view` |
| `/resume [id]` | optional | Resume last session | CheckpointStore |

`@` mentions: `@selection` `@activeView` `@visible` `@warnings` `@levels` `@sheets`. After parse, inject matching IDs/names at the end of `cleanText` as `[context: ...]`, and show them as blue chips in the UI.

## 7. Task list (in order, for the implementing agent)

**Phase 1 — CLI foundation (usable in the single pane)**
1. Implement `web/src/cli/slashCommands.ts` + `mentions.ts` + `inputParser.ts`; add `/` and `@` dropdowns, parse, and chips in `web/src/App.tsx` Composer; add `slash?:string, mentions?:string[]` to `bridge.ts` `ToHost`
2. Implement `Collect()` in `src/RevitAiPoc/Revit/ContextCollector.cs`; `HostPage.xaml.cs` `send` injects context into `userMsg` (append context text next to `FromMultimodal`)
3. Add `TodoItem/TodoUpdate` and `onTodoUpdate` in `src/RevitAiPoc/Llm/AgentLoop.cs`; Plan mode streams Todo updates (`pending→running→done`); frontend Plan card subscribes
4. Unify tool timeline style and truly block Approval (`TaskCompletionSource` waits for `approveTool`, 180s timeout, reject returns `Cancelled by user`)
5. Add tests: `CliParserTests.cs` (`/plan` / `@selection` parse), `ContextCollectorTests` mock; keep existing `ToolSelector/ChatHistoryTrimmer/LlmJson` passing

**Phase 2 — Terminal and resume**
6. `web/src/components/Terminal.tsx` with `xterm@5 + xterm-addon-fit`, collapsible pane, `tool_call/result` stream; Header toggle; bidirectional sync with Chat input
7. Implement save/list/read/delete in `src/RevitAiPoc/Llm/CheckpointStore.cs`; HostPage adds `listCheckpoints/resume/rollback` ToHost types; frontend adds `/resume` and history dropdown
8. Stream UX: `OpenAiCompatibleClient.cs` `onDelta` and `ToolSelector` already stream; ensure Todo and tool cards update incrementally during streaming

**Phase 3 — Sub-agents (optional; decide after the first two phases whether v1 includes this)**
9. `AgentLoop.SpawnSubAgent(task)` runs multiple read-only queries in parallel (e.g. "count walls per floor" becomes one sub-agent per floor), then merges results into the main agent; frontend shows child cards

**Per-phase verification**:
- `npm run build` (vite < 5s, gzip JS increment < 180KB)
- `dotnet test -c Release` stays ≥31 passing, new tests pass
- Manual: new empty Revit model, verify `/plan count walls → approve → streaming tool timeline`, `@selection isolate`, `/clear`, terminal toggle, resume

## 8. Data flow and failure modes

- **Data flow**: user input → CLI parse (slash/mentions/cleanText+attachments+mode) → HostPage builds `ChatMessage` (multimodal + context) → AgentLoop (Planner/Executor + ToolSelector Top-20) → OpenAiCompatibleClient (Responses/OpenAI) → tool loop (HostPage.ExecuteToolAsync → RevitBridge → ToolEntry.Handler, L2/L3 audit) → push timeline/terminal/plan/todo
- **Failures**:
  - `/` miss: do not intercept; send as normal text; UI hint "Unknown command, try /help"
  - Empty `@` (e.g. no selection): inject `[context: selection empty]`; model can ask the user to select first
  - Invalid tool args: `LlmJson.NormalizeToolArguments` falls back to `{}`; `ToolDiagnostics.DescribeMissing` suggests the closest name
  - L3 reject/timeout: return `Cancelled by user.`, Todo marked error, frontend can retry
  - Revit ExternalEvent denied: `RevitBridge` already logs `bridge.raise_denied` and shows a hint
  - History too long: `ChatHistoryTrimmer` cuts to 2000 chars and keeps only the latest image — already implemented

## 9. Risks and mitigations

- **Narrow WebView2 pane cannot fit a terminal**: mitigate by default-collapsed terminal, 120px height, resizable; below 360px hide icon labels and keep icons only
- **xterm increases bundle size**: mitigate with dynamic `import('xterm')` on first expand; keep first paint < 140KB gzip
- **Sub-agents increase LLM cost**: Phase 3 behind a switch, off by default; auto-enable only in Build mode when the task contains "each / batch / parallel"
- **Context injection inflates the prompt**: ContextCollector injects a summary only (counts + names + first 3 warnings); details via tools on demand

## 10. Out of scope

- Widen the Revit pane or pop a separate window (keep single pane)
- Filesystem-level agent (e.g. collaborating on a code repo besides the .rvt itself)
- Auto-install remote plugins (shelf display + toggle only, already exists)
- Directly install/embed the full host of those 50 agents (e.g. stuffing a VS Code plugin into Revit) — Revit is a separate process and API model, so that path does not work

## 11. Acceptance criteria

- User can type `/plan batch-tag all rooms` → see a structured Plan Todo → click approve → see the tool timeline go running→done item by item, with no further manual steps except L3
- Typing `@selection hide these` correctly references the current selection and runs
- Terminal can open/close, content matches the timeline, and `stop` interrupts
- After `/clear`, `/resume` restores the session
- `dotnet test` and `npm run build` pass; deployed to `%AppData%\Autodesk\Revit\Addins\2027\RevitAiPoc`

## 12. Open questions

- Should Phase 1 also add Diff preview (old vs new parameter values)? Recommend showing `tool_result` text in v1, structured Diff in v2.
