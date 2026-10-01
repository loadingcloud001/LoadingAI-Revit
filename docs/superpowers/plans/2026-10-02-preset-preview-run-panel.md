# Preset Preview & Run Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the preset form that currently sits as a permanent card in the chat transcript with a docked Run panel that shows a plain-language preview of what the tool will do, so confirming is one click rather than filling in a form embedded in the conversation.

**Architecture:** Build the preview generically in Core from the `PresetField` declarations a tool already makes plus the resolved arguments — no per-tool hook, no Revit access, fully unit-testable. Carry it to the pane as one optional field on the existing `PresetPayload`. The pane renders the pending form and preview in a pinned Run panel and leaves only a compact receipt line in the transcript. Gate order is untouched: preview is derived data, it runs before `AwaitPresetAsync` and opens no transaction.

**Tech Stack:** C#/.NET (netstandard2.0 Core; net48/net8/net10 host per Revit version), System.Text.Json, xUnit, TypeScript/React/Vite, vitest (new, dev-only).

**Spec:** the preset-forms design discussed in this session; `docs/adr/0004-host-owned-l3-confirm.md` (frozen gate constraints this plan must respect); `docs/adr/0014-preview-before-preset-form.md` (created by Task 0).

## Out of scope

Extending presets to the other packs (`structure-cuts`, `core-tools`, `export-tools`, `view-tools`, `opening-tools`, `modeling-tools`) is a **separate plan**. It needs a per-tool outcome hook to report predicted counts, which nothing in this plan requires yet. Adding it here would be speculative.

## Global Constraints

- Revit 2024, 2025, 2026, and 2027 remain supported.
- `LoadingAI.Core` must not reference Revit assemblies.
- `Plugins/qs-formwork` must ProjectReference `LoadingAI.Core` and nothing else; it must not mention `Autodesk.Revit`.
- **ADR-0004 gate order is frozen.** Preset L3 keeps `Confirm L3:` native after the form. Plain L3 keeps web card then native. Do not change the order, the `Cancel` strings, or the 180s timeout.
- **ADR-0004 freezes `ToolPresets.IsDryRun` as a generic merged-JSON check.** Do not replace it with a tool-name allowlist or a per-tool gate-skip flag. Adding `dryRun` to a tool's schema is a tool capability change and is allowed; changing how the gate decides is not.
- **ADR-0009 / ADR-0013 freeze `RequiresWriteConfirm` per tool:** `formwork_setup` false, `create_formwork_panels` true, `cleanup_formwork` true, `copy_formwork_parent_params` false. Asserted at `FakeBridgeToolTests.cs:2368-2371`.
- All four `qs-formwork` tools stay `ToolTier.L3` and `enabledByDefault: false`.
- Do not `new TaskDialog` outside `src/LoadingAI/Tools/ToolGuards.cs`.
- No new web **runtime** dependencies. `vitest` is a devDependency only.
- Preview data must never be presented as predicted outcome counts. Where the model is not actually known, the preview reports scope and settings only.
- The `Args` helper duplication across plugins is ADR-frozen; do not consolidate it.

## Review Focus

1. **A tool declares `dryRun` in its schema but its handler ignores it.** `ChatSession` skips both gates on `dryRun:true`, so this silently removes every confirm on an L3 destructive write. Today the argument validator rejects the unknown key and the hole is closed by accident. Task 4 opens that key for `qs-formwork`, so its handler must short-circuit in the same commit. → Task 4, `DryRun_SkipsAllBridgeWrites_AndReportsNoWrite`; plus Task 1's registry-wide contract test.
2. **The preview throws.** A preview is enrichment; if it fails the preset form must still appear and the tool must still be confirmable. A thrown preview must never block a tool call. → Task 2, `Preview_Failure_FallsBackToFormWithoutBlocking`.
3. **A `PresetField.Warning` fires on a safe value.** Warnings must key off truthiness, so `deleteExisting: false` must not warn "existing panels will be deleted". `0` and `""` are also falsy. → Task 1, `BuildPreview_WarningOnlyWhenTruthy`.
4. **Stop / DenyAll while the Run panel holds a pending preset.** The panel must clear and no confirm may be sendable afterwards. → Task 3, `Stop_ClearsRunPanelAndSendsNothing`.
5. **Plain (non-preset) L3 approval still dumps raw JSON.** `App.tsx:865` renders `m.approval.args` as `font-mono text-[11px] break-all`. Fixing the preset surface but leaving the approval card as a wall of escaped text is half a fix. → Task 3, `FormatApprovalArgs_EmitsReadableLines`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/LoadingAI.Core/Tools/RunPreview.cs` | New. `RunPreview` + `PreviewRow`, the derived "what will happen" shape. |
| `src/LoadingAI.Core/Tools/ToolRegistry.cs` | Add `PresetField.Warning`. |
| `src/LoadingAI.Core/Tools/ToolPresets.cs` | Add `BuildPreview(fields, argsJson)`. |
| `src/LoadingAI.Core/Protocol/HostProtocol.cs` | Add `PreviewPayload`, `PreviewRowPayload`; `PresetPayload` gains `Preview`. |
| `src/LoadingAI/Ui/ApprovalGate.cs` | `AwaitPresetAsync` carries the preview into `FromHostMessage.ToolPreset`. |
| `web/src/lib/presetPreview.ts` | New. Pure formatting: preview rows, summary, readable approval args. No React. |
| `web/src/App.tsx` | Run panel renders pending preset + preview; transcript keeps only a receipt line. |
| `web/src/bridge.ts` | `PresetPayload` gains `preview`; `ApprovalPayload` unchanged. |
| `Plugins/qs-formwork/QsFormworkPlugin.cs` | `dryRun` in the four schemas, handler short-circuit, `Warning` on `deleteExisting`. |

---

### Task 0: Record ADR-014 — preview before the preset form

ADR-0004 states that a preview step in the gate sequence "is a product freeze, not a bugfix. Record it before anyone 'simplifies' the gate." This plan adds a preview step, so the record comes first.

**Files:**
- Create: `docs/adr/0014-preview-before-preset-form.md`
- Modify: `docs/adr/README.md`

**Interfaces:**
- Consumes: ADR-0004's frozen decisions (read, do not edit).
- Produces: an Accepted ADR that later tasks cite in their `Spec:` line.

- [ ] **Step 1: Write the ADR following `docs/adr/0004-host-owned-l3-confirm.md`'s shape**

Sections: Status (Accepted), Date, Deciders, Context, Options considered, Decision, Consequences, Do not.

The Decision must state exactly these three things and nothing more:

- A read-only preview is derived from `PresetField` declarations plus resolved arguments. It opens no transaction and is not a gate.
- The preview is displayed in a docked Run panel, not as a transcript message. The transcript keeps a receipt line.
- The ADR-0004 gate order is unchanged: preview → preset form → `Confirm L3:` native. The preview does not replace, merge with, or reorder any gate.

The Do not section must include: do not use the preview as a substitute for the native confirm; do not let a preview open a transaction; do not report predicted outcome counts the tool did not compute.

- [ ] **Step 2: Add the entry to `docs/adr/README.md`**

Append `0014` to the index in the same format as `0013`.

- [ ] **Step 3: Verify the ADR does not contradict ADR-0004**

Run: `Select-String -Path docs\adr\0014-preview-before-preset-form.md -Pattern "Confirm L3"`
Expected: at least one hit stating the native confirm is retained.

- [ ] **Step 4: Commit**

```bash
git add docs/adr/0014-preview-before-preset-form.md docs/adr/README.md
git commit -m "docs: ADR-014 preview before the preset form"
```

---

### Task 1: `RunPreview` in Core

Pure data and one pure builder. No Revit, no I/O, no thread affinity.

**Files:**
- Create: `src/LoadingAI.Core/Tools/RunPreview.cs`
- Modify: `src/LoadingAI.Core/Tools/ToolRegistry.cs` (`PresetField`, add `Warning`)
- Modify: `src/LoadingAI.Core/Tools/ToolPresets.cs` (add `BuildPreview`)
- Test: `src/LoadingAI.Tests/RunPreviewTests.cs`

**Interfaces:**
- Consumes: `PresetField` (`Id`, `Label`, `Kind`, `Required`, `Default`, `Options`, `Category`, `Prompt`) and `ToolPresets.Prefill(IReadOnlyList<PresetField>, string?) -> Dictionary<string, object?>`.
- Produces — later tasks rely on these exact names:
  - `LoadingAI.Core.Tools.RunPreview` with `init` properties `string Summary`, `IReadOnlyList<PreviewRow> Rows`, `IReadOnlyList<string> Warnings`.
  - `LoadingAI.Core.Tools.PreviewRow` with `init` properties `string Label`, `string Detail`.
  - `ToolPresets.BuildPreview(IReadOnlyList<PresetField> fields, string? argsJson) -> RunPreview`.
  - `PresetField.Warning` as `string?`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void BuildPreview_SummaryNamesToolShapeNotJson();
[Theory] // (value, shouldWarn) over true/false, 1/0, "yes"/""
[InlineData(...)] public void BuildPreview_WarningOnlyWhenTruthy(object? value, bool shouldWarn);
[Fact] public void BuildPreview_RowPerField_InDeclarationOrder();
[Fact] public void BuildPreview_UsesResolvedArgsOverFieldDefault();
[Fact] public void BuildPreview_UnknownValueRendersAsDashNotThrow();
[Fact] public void BuildPreview_SelectShowsChosenOption();
```

Pin these assertions with the spec's exact strings:
- `BuildPreview_WarningOnlyWhenTruthy`: a field with `Warning = "Existing panels will be deleted first."` yields `preview.Warnings` containing that exact string when truthy, and `Warnings` empty when `false`, `0`, or `""`.
- `BuildPreview_RowPerField_InDeclarationOrder`: for a three-field form, `Rows.Select(r => r.Label)` equals the three `Label` values in declaration order.
- `BuildPreview_UsesResolvedArgsOverFieldDefault`: field `Id = "thicknessMm"`, `Default = (int)18`, args `{"thicknessMm": 22}` → the `thicknessMm` row's `Detail` contains `"22"` and not `"18"`.
- `BuildPreview_SelectShowsChosenOption`: `Kind = Select`, `Options = ["stats","clear"]`, value `"clear"` → `Detail` contains `"clear"`.
- `BuildPreview_UnknownValueRendersAsDashNotThrow`: `Kind = PickElements`, no value → `Detail` is `"-"`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true -m:1 -v quiet --filter "FullyQualifiedName~RunPreviewTests"`
Expected: FAIL — `RunPreview` does not exist.

- [ ] **Step 3: Implement `RunPreview`, `PreviewRow`, `PresetField.Warning`, `ToolPresets.BuildPreview`**

Model `RunPreview` and `PreviewRow` on `PresetField`'s style in the same folder: `public sealed class` with `{ get; init; }` properties and empty-string defaults, so a partially populated instance is still renderable.

`BuildPreview` delegates value resolution to the existing `Prefill` rather than re-reading the JSON. The summary line is `"N settings to confirm"` where `N` is `fields.Count`; do not invent a tool-specific sentence here. A row's `Detail` renders booleans as `"on"`/`"off"`, an absent or unknown value as `"-"`, and never throws.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true -m:1 -v quiet --filter "FullyQualifiedName~RunPreviewTests"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Add the registry-wide dry-run contract test**

This is the Review Focus item 1 guard and belongs with the code that introduces the concept.

In `src/LoadingAI.Tests/ToolPresetTests.cs`, add:

```csharp
[Fact] public void EverySchemaDeclaringDryRun_ImplementsDryRunShortCircuit();
```

It reads every `Plugins/**/*.cs` tool schema source text; any schema that mentions `"dryRun"` must sit in a file whose handler also contains the short-circuit marker `dryRun` handling. Where a plugin declares `dryRun` without handling it, the test fails with the plugin name. Expect this to fail until Task 4 completes — that is the point. Mark it with a comment naming Task 4 as the closer so a green run is not mistaken for coverage.

- [ ] **Step 6: Commit**

```bash
git add src/LoadingAI.Core/Tools/RunPreview.cs src/LoadingAI.Core/Tools/ToolRegistry.cs src/LoadingAI.Core/Tools/ToolPresets.cs src/LoadingAI.Tests/RunPreviewTests.cs src/LoadingAI.Tests/ToolPresetTests.cs
git commit -m "feat: derive preset preview from field declarations"
```

---

### Task 2: Carry the preview to the pane

**Files:**
- Modify: `src/LoadingAI.Core/Protocol/HostProtocol.cs`
- Modify: `src/LoadingAI/Ui/ApprovalGate.cs:67`
- Test: `src/LoadingAI.Tests/HostProtocolPreviewTests.cs`

**Interfaces:**
- Consumes: `ToolPresets.BuildPreview` and `RunPreview` from Task 1.
- Produces — `web/src/bridge.ts` mirrors these field-for-field in camelCase:
  - `PreviewRowPayload(string Label, string Detail)`
  - `PreviewPayload(string Summary, IReadOnlyList<PreviewRowPayload> Rows, IReadOnlyList<string> Warnings)`
  - `PresetPayload` gains a trailing `PreviewPayload? Preview = null`.
  - `ApprovalGate.AwaitPresetAsync(ToolEntry entry, string name, string argsJson)` keeps its signature and return `(bool approved, string? valuesJson, string message)`; it builds the preview itself from `entry.Presets` and `argsJson`, so no caller changes.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void PresetPayload_SerializesPreviewWhenPresent();
[Fact] public void PresetPayload_OmitsPreviewWhenAbsent();
[Fact] public void AwaitPresetAsync_EmbedsPreviewInToolPresetMessage();
[Fact] public void Preview_Failure_FallsBackToFormWithoutBlocking();
```

Assertions:
- `PresetPayload_SerializesPreviewWhenPresent`: round-trip a `PresetPayload` with a populated `Preview`; assert `Summary`, `Rows[0].Label`, `Rows[0].Detail`, and `Warnings[0]` survive.
- `PresetPayload_OmitsPreviewWhenAbsent`: `Preview` null serializes without a `preview` property (the pane treats absence as "no preview available").
- `Preview_Failure_FallsBackToFormWithoutBlocking`: when `BuildPreview` throws, `AwaitPresetAsync` still returns a normal preset whose `Preview` is null, and still awaits the user's decision.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true -m:1 -v quiet --filter "FullyQualifiedName~HostProtocolPreviewTests"`
Expected: FAIL — `PreviewPayload` does not exist.

- [ ] **Step 3: Implement `PreviewPayload`, `PreviewRowPayload`, and the `PresetPayload.Preview` parameter**

Append `PreviewPayload? Preview = null` as the **last** positional parameter of `PresetPayload`; existing call sites are positional and must not be renumbered. `ApprovalGate.AwaitPresetAsync` wraps its `BuildPreview` call in a `try/catch` that swallows to `null` — a preview is enrichment, never a precondition.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true -m:1 -v quiet --filter "FullyQualifiedName~HostProtocolPreviewTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Confirm ADR-0004's locked tests still pass**

Run: `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true -m:1 -v quiet --filter "FullyQualifiedName~UsabilityContractTests|FullyQualifiedName~ChatSessionContractTests"`
Expected: PASS. These are the ADR-0004 gate-order tests; any failure means this task moved a gate.

- [ ] **Step 6: Commit**

```bash
git add src/LoadingAI.Core/Protocol/HostProtocol.cs src/LoadingAI/Ui/ApprovalGate.cs src/LoadingAI.Tests/HostProtocolPreviewTests.cs
git commit -m "feat: carry preset preview to the web pane"
```

---

### Task 3: Run panel and receipt card

**Files:**
- Create: `web/src/lib/presetPreview.ts`
- Create: `web/src/lib/presetPreview.test.ts`
- Create: `web/vitest.config.ts`
- Modify: `web/package.json` (scripts `test`, devDependency `vitest`)
- Modify: `web/src/bridge.ts`
- Modify: `web/src/App.tsx`

**Interfaces:**
- Consumes: `PreviewPayload` / `PreviewRowPayload` from Task 2, mirrored into `web/src/bridge.ts` as `PresetPayload['preview']`.
- Produces — `web/src/lib/presetPreview.ts` exports:
  - `formatApprovalArgs(argsJson: string): string[]` — readable lines from an args JSON blob.
  - `previewRows(preset: PresetPayload): { label: string; detail: string }[]` — preview rows, or field rows when no preview was sent.
  - `previewWarnings(preset: PresetPayload): string[]`.
  - `receiptLine(preset: PresetPayload, outcome: 'confirmed' | 'cancelled'): string`.

- [ ] **Step 1: Write the failing tests**

```ts
it('formatApprovalArgs_EmitsReadableLines', ...)          // Review Focus 5
it('formatApprovalArgs_HandlesNestedAndEmptyObjects', ...)
it('previewRows_FallsBackToFieldRowsWhenNoPreview', ...)
it('previewWarnings_EmptyWhenNoWarnings', ...)
it('receiptLine_ShortAndStable', ...)
```

Assertions:
- `formatApprovalArgs_EmitsReadableLines`: for `{"thicknessMm":18,"deleteExisting":false,"elementIds":[1,2]}` the result contains `"thicknessMm: 18"`, `"deleteExisting: false"`, `"elementIds: 2 items"` — and contains **no** `"{"`.
- `previewRows_FallsBackToFieldRowsWhenNoPreview`: a preset with `fields` and `preview: undefined` still yields one row per field.
- `receiptLine_ShortAndStable`: confirmed output is `"create_formwork_panels · confirmed"`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `npm test` (in `web/`)
Expected: FAIL — `presetPreview` module does not exist.

- [ ] **Step 3: Implement `web/src/lib/presetPreview.ts` and the vitest config**

Pure functions only — no React, no `window`, no host bridge. That is what makes them testable and what keeps the component thin.

- [ ] **Step 4: Run tests to verify they pass**

Run: `npm test` (in `web/`)
Expected: PASS, 5 tests.

- [ ] **Step 5: Move preset rendering into the Run panel**

Today `App.tsx` renders the interactive form inside the transcript message map at the branch `if (m.role === 'preset' && m.preset)`, driven by `isPending = pendingPreset?.toolCallId === m.preset.toolCallId`. That is why the form is a permanent card in the conversation.

Change it so:
- The **interactive** form and its preview render from `pendingPreset` in a pinned Run panel, always in the same place, above the composer. It does not scroll with the transcript.
- The **transcript** keeps the `role: 'preset'` message but renders it non-interactively as one line: `receiptLine(...)`, with `previewWarnings` shown as a red line beneath while pending.
- `handleSubmitPreset`, `handlePresetPick`, `handleStop`, and `handleClear` all clear the panel. `handleStop` and `handleClear` already set `setPendingPreset(null)`; verify no confirm can be sent after either. → Review Focus 4.
- The plain-L3 approval card at `App.tsx:865` stops dumping `m.approval.args` raw and renders `formatApprovalArgs(m.approval.args)` instead. → Review Focus 5.

- [ ] **Step 6: Typecheck and build**

Run: `npm run typecheck` then `npm run build` (in `web/`)
Expected: both exit 0.

- [ ] **Step 7: Commit**

```bash
git add web/src/lib web/vitest.config.ts web/package.json web/src/bridge.ts web/src/App.tsx
git commit -m "feat: preset form moves to a docked run panel"
```

---

### Task 4: `qs-formwork` — `dryRun` and danger warnings

Adding `dryRun` to a schema opens the key that `ChatSession` uses to skip both gates. The handler must therefore honour it in the same commit, or every confirm silently disappears on an L3 write.

**Files:**
- Modify: `Plugins/qs-formwork/QsFormworkPlugin.cs`
- Test: `src/LoadingAI.Tests/FakeBridgeToolTests.cs`

**Interfaces:**
- Consumes: `ToolPresets.IsDryRun(string?) -> bool` (existing, unchanged); `PresetField.Warning` from Task 1.
- Produces: each of the four tools accepts `dryRun` as `boolean`, default `false`, and returns `ToolExecutionResult` with `ToolWriteCertainty.NoWrite` and no bridge mutation when it is set.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void FormworkDryRun_ReportsNoWrite_AndIssuesNoBridgeWrites();
[Fact] public void FormworkDryRun_DeleteExistingStillNeedsNoConfirm();
[Fact] public void CreateFormworkPanels_DeleteExistingFieldCarriesWarning();
```

Assertions, using `FakeRevitBridge`'s call recording:
- `FormworkDryRun_ReportsNoWrite_AndIssuesNoBridgeWrites`: call each of `create_formwork_panels`, `cleanup_formwork`, `copy_formwork_parent_params` with `"dryRun": true`; assert `Status` is `Succeeded`, `WriteCertainty` is `NoWrite`, and the bridge recorded **zero** mutating calls.
- `FormworkDryRun_DeleteExistingStillNeedsNoConfirm`: `{"dryRun":true,"deleteExisting":true}` still reports `NoWrite` — the dangerous flag must not escalate a dry run into a write.
- `CreateFormworkPanels_DeleteExistingFieldCarriesWarning`: the `deleteExisting` `PresetField` carries a non-empty `Warning`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true -m:1 -v quiet --filter "FullyQualifiedName~FakeBridgeToolTests"`
Expected: FAIL — `dryRun` is currently rejected as an unknown property.

- [ ] **Step 3: Implement `dryRun` in the four schemas and short-circuit the handlers**

Add `"dryRun": { "type": "boolean" }` to each of the four `qs-formwork` tool schemas. In each handler, immediately after `Args.Parse(json)`, return when `ToolPresets.IsDryRun(json)` — before any bridge call — with a `ToolExecutionResult.Succeeded` whose report states the scope that would be processed and whose certainty is `NoWrite`.

Do **not** predict panel counts. The generator's outcome depends on Revit `Solid` geometry that has no tests (finding F7), so a count here would be a guess presented as fact. Report scope and settings only. Also add `Warning = "Existing panels will be deleted first."` to the `deleteExisting` field.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true -m:1 -v quiet --filter "FullyQualifiedName~FakeBridgeToolTests"`
Expected: PASS.

- [ ] **Step 5: Confirm the Task 1 registry contract test is now green**

Run: `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true -m:1 -v quiet --filter "FullyQualifiedName~EverySchemaDeclaringDryRun"`
Expected: PASS. If it still fails, another plugin declares `dryRun` without handling it; fix it in this task or record it in `docs/PROJECT_BUGS_AND_LIMITATIONS.md`.

- [ ] **Step 6: Full suite**

Run: `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true -m:1 -v quiet`
Expected: PASS. Compare the count against the pre-branch baseline and report the delta.

- [ ] **Step 7: Commit**

```bash
git add Plugins/qs-formwork/QsFormworkPlugin.cs src/LoadingAI.Tests/FakeBridgeToolTests.cs
git commit -m "feat: qs-formwork honours dryRun and warns on deleteExisting"
```

---

## Self-review notes

- **Spec coverage.** The two asks were (a) a better surface than the in-chat form and (b) presets on more plugins. (a) is Tasks 2–3; (b) is explicitly deferred to a follow-on plan and named under *Out of scope* rather than left implicit.
- **Type consistency.** `RunPreview`/`PreviewRow`/`BuildPreview` are named once in Task 1's Interfaces and reused verbatim in Tasks 2 and 4. `PreviewPayload`/`PreviewRowPayload` are named once in Task 2 and mirrored field-for-field in Task 3.
- **Proportion.** No task carries a function body. The only blocks are test signatures with pinned assertions, which is where the spec's values live.
- **Corrected from the brainstorm.** The earlier suggestion to replace `ToolPresets.IsDryRun` with a per-tool `SupportsDryRun` flag is **dropped**: ADR-0004 freezes the generic merged-JSON check and forbids a tool-name allowlist. If that hardening is ever wanted it needs its own ADR first.
