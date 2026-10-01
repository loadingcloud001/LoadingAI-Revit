# QS-Formwork Rework: Structured Results, Honest Outcomes, and Usable Controls

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `qs-formwork` report outcomes the user can trust and act on. Stop a failed or rolled-back formwork run being presented as a successful write, carry the per-item counts that `FormworkGenerator` already computes across the Bridge so the pane can show a result card instead of a prose sentence, and give each tool a host-owned form so the model stops guessing element ids and free-text enums.

**Architecture:** Keep the existing split — plugin is Bridge-only plumbing, all Revit work stays in `src/LoadingAI/Revit/Formwork/`. Change the *shape* of the handoff: `FormworkGenerator.Generate` already returns `GenerateResult` with 11 counters and a `FailReasons` dictionary, but `FormworkTools.cs` flattens it to one English string and `RunFormwork` wraps that string in `(Report, Error)`. Thread `GenerateResult` through to the DTOs instead, so status and write certainty are derived from counts rather than parsed from prose. Add `PresetField` forms and schema `enum`/bounds to the four tool definitions.

**Tech Stack:** C#/.NET (netstandard2.0 for Core, net48/net8/net10 per Revit version), System.Text.Json, xUnit, Revit ExternalEvent + Transaction, Vite/React web pane.

**Spec:** `docs/PROJECT_BUGS_AND_LIMITATIONS.md` B3 (remaining migration) and the `qs-formwork` findings below. Supersedes the qs-formwork portion of `docs/adr/0009-port-qs-formwork-onto-bridge.md` via `docs/adr/0013-qs-formwork-structured-results.md`.

## Findings this plan addresses

| # | Finding | Where |
|---|---|---|
| F1 | A formwork exception is caught and returned as `Report = "Failed: ..."`, so it reads as a **successful commit** | `FormworkTools.cs:31, 75, 124, ~180` |
| F2 | `cleanup_formwork` rolls the transaction back then returns a plain string, reported as a commit | `FormworkTools.cs:105, 118` |
| F3 | `GenerateResult` (11 counters + `FailReasons`) is flattened to prose at the boundary | `FormworkTools.cs:73` |
| F4 | The result DTOs are `(Report, Error)` only — no counts, so `Partial` cannot be reported | `RevitDtos.cs:340, 359, 371, 383` |
| F5 | No `enum` on `cleanup_formwork.action` or `faceTypes`; no bounds on `thicknessMm` | `QsFormworkPlugin.cs` schemas |
| F6 | No preset forms; the LLM guesses element ids and thickness | all 4 tools |
| F7 | ~1,400 Revit-bound lines in `FormworkCore`/`Generator`/`Geometry` with **zero** tests | `LoadingAI.RevitTests` |

## Global Constraints

- Revit 2024, 2025, 2026, and 2027 remain supported.
- `LoadingAI.Core` must not reference Revit assemblies.
- `Plugins/qs-formwork` must ProjectReference `LoadingAI.Core` and nothing else; it must not mention `Autodesk.Revit`.
- All four tools stay `ToolTier.L3` and `enabledByDefault: false`.
- The `RequiresWriteConfirm` values stay exactly as they are: `formwork_setup` false, `create_formwork_panels` true, `cleanup_formwork` true, `copy_formwork_parent_params` false. Frozen by ADR-0009 and asserted at `FakeBridgeToolTests.cs:2368-2371`.
- L3 tools keep web approval plus the native `Confirm L3:` TaskDialog; a dry-run or preset form does not remove the native confirm.
- **A rolled-back operation must never be reported as committed.** A crash must never be reported as success.
- `Partial` must only be claimed when counts prove it.
- The four `Formwork*Async` methods stay on `IRevitCreateCommand`. `L3CommandContractTests` pins their placement on every slice.
- No new production dependencies.

## Blocking constraint — read before Task 4

`docs/adr/0009-port-qs-formwork-onto-bridge.md:118` freezes, verbatim:

> the `Args` JSON helper struct, the four frozen `Definition` schemas, the two `RequiresWriteConfirm = true` flags, and all four `ToolTier.L3` tiers stay plugin-side and unchanged.

And `UsabilityContractTests.cs:58-72` (`ToolEntry_PresetsDefaultEmpty_ExistingPluginsUnchanged`) asserts `Assert.DoesNotContain("Presets =", src)` over `Plugins/qs-formwork/QsFormworkPlugin.cs` by name.

Therefore **Task 4, 5 and 6 break an Accepted ADR freeze and a contract test.** `CONTRIBUTING.md` requires a **Proposed ADR before code** in that case. Task 3 writes it. Do not start Task 4 until Task 3 is reviewed. Tasks 1 and 2 are bug fixes that *keep* the freeze and can proceed immediately.

## File Structure

- `src/LoadingAI.Core/Revit/RevitDtos.cs` — extend the four formwork result DTOs with counts and failure reasons.
- `src/LoadingAI.Core/Revit/Formwork/FormworkDefaults.cs` — face type names for the `enum` schema.
- `src/LoadingAI.Core/Tools/ToolExecutionResult.cs` — no change expected; consume as-is.
- `src/LoadingAI/Revit/Formwork/FormworkTools.cs` — stop returning strings; return the structured result.
- `src/LoadingAI/Revit/Formwork/FormworkConfig.cs` — `GenerateResult` becomes the transport type.
- `src/LoadingAI/Revit/AdapterRevitBridge.Workflows.cs` — `RunFormwork` projects the structured result.
- `Plugins/qs-formwork/QsFormworkPlugin.cs` — derive status from counts; add presets and schema bounds.
- `docs/adr/0013-qs-formwork-structured-results.md` — the Proposed ADR.
- `src/LoadingAI.Tests/QsFormworkExecutionResultTests.cs` — outcome pins.
- `src/LoadingAI.Tests/FakeBridgeToolTests.cs` — update the frozen-name and handler expectations.
- `src/LoadingAI.Tests/UsabilityContractTests.cs` — narrow the preset freeze to `example.hello` only.
- `src/LoadingAI.RevitTests/FormworkInRevitTests.cs` — live-document geometry smoke.

---

### Task 1: Stop reporting a formwork crash as a committed write

**Urgent. A regression introduced in `cf3b2cb`.** The legacy `FromLegacy` classified `text.StartsWith("Failed:")` as `Failed`; the replacement `FromFormwork` does not, so a caught exception now maps to `Succeeded + Committed` and the turn `TransactionGroup` assimilates it.

**Files:**
- Modify: `Plugins/qs-formwork/QsFormworkPlugin.cs`
- Test: `src/LoadingAI.Tests/QsFormworkExecutionResultTests.cs`

**Interfaces:**
- Consumes: `FormworkXxxResultDto.Report`.
- Produces: `ToolExecutionResult.Failed(text, ToolWriteCertainty.NotCommitted)` when `Report` begins with `Failed:`.

- [ ] **Step 1: Write failing tests for the crash-shaped report and for the two rolled-back cleanup reports.**

```csharp
[Fact]
public async Task FormworkCrashIsNotReportedAsACommit()
{
    var (bridge, context) = Setup();
    bridge.NextFormworkPanelsResult =
        new FormworkPanelsResultDto("Failed: geometry could not be built.");

    var result = await CreateTool(context, "create_formwork_panels")
        .ExecuteAsync("""{"elementIds":["100"]}""");

    Assert.Equal(ToolExecutionStatus.Failed, result.Status);
    Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
    Assert.False(result.IsSafeToAssimilate);
}

[Theory]
[InlineData("Unknown action. Use stats, delete_all, delete_by_type, or cleanup_orphans.")]
[InlineData("faceType is required for delete_by_type.")]
public async Task CleanupThatRolledBackIsNotReportedAsACommit(string report)
{
    var (bridge, context) = Setup();
    bridge.NextCleanupFormworkResult = new CleanupResultDto(report);

    var result = await CreateTool(context, "cleanup_formwork")
        .ExecuteAsync("""{"action":"delete_all"}""");

    Assert.Equal(ToolExecutionStatus.Failed, result.Status);
    Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
    Assert.False(result.IsSafeToAssimilate);
}
```

- [ ] **Step 2: Run `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj --filter FullyQualifiedName~QsFormworkExecutionResultTests -m:1`.**
Expected: FAIL. The crash case currently reports `Succeeded`.

- [ ] **Step 3: In `FromFormwork`, classify a `Report` starting with `Failed:` as `Failed + NotCommitted` before the success branch. Keep the `Error`-first and null-report branches.**

- [ ] **Step 4: Add a test that a plain successful report is still `Succeeded + Committed`, so the prefix check cannot over-match.**

- [ ] **Step 5: Run the focused tests and the full `src/LoadingAI.Tests` suite.**
Expected: PASS with zero failures.

- [ ] **Step 6: Commit the honesty fix and its tests.**

---

### Task 2: Fix the rollback-reported-as-commit paths in the host

`FormworkCleanup.Run` calls `tx.RollBack()` and then returns a plain string (`FormworkTools.cs:105, 118`). Task 1 makes those strings classify correctly, but the deeper problem is that a rolled-back operation is only distinguishable from a success by the wording of a sentence.

**Files:**
- Modify: `src/LoadingAI/Revit/Formwork/FormworkTools.cs`
- Test: `src/LoadingAI.Tests/QsFormworkExecutionResultTests.cs`

- [ ] **Step 1: Write a failing test that an unknown action and a missing `faceType` both report `NotCommitted` even when the host returns a non-`Failed:` report.**

- [ ] **Step 2: Make `FormworkCleanup.Run` return a `Failed:`-prefixed message on the two rollback paths so the classifier is unambiguous, matching the existing `catch` convention.**

- [ ] **Step 3: Add `enum` to the `cleanup_formwork` schema in Task 5 so the LLM cannot reach the unknown-action branch at all. Note in the test why both layers are needed.**

- [ ] **Step 4: Run focused and full tests.**
Expected: PASS with zero failures.

- [ ] **Step 5: Commit.**

---

### Task 3: Proposed ADR-0013 — structured formwork results and preset forms

**Files:**
- Create: `docs/adr/0013-qs-formwork-structured-results.md`
- Modify: `docs/adr/README.md` (index row, Status: Proposed)

**Context to record:**
- ADR-0009 froze the four `Definition` schemas and the preset-free surface as part of proving the FullAccess-to-Bridge port was behaviour-preserving.
- The port is done and verified; the freeze is now blocking correct reporting (F3, F4) and usability (F6).
- `structure-cuts` already ships `PresetField` forms and count-bearing `SplitResultDto`, so the precedent exists in-tree.

**Decision to propose:**
- The four formwork result DTOs carry counts and failure reasons.
- The four tool definitions may declare `Presets` and schema `enum`/`minimum`/`maximum`.
- The `Args` helper, the `RequiresWriteConfirm` values, and the four L3 tiers remain frozen.

**Do not** include any change to tiers, to `RequiresWriteConfirm`, or to the plugin's ProjectReferences.

- [ ] **Step 1: Write the ADR with Status `Proposed`, Context, Options considered, Decision, Consequences, Do not.**
- [ ] **Step 2: Add the index row to `docs/adr/README.md` marked Proposed.**
- [ ] **Step 3: Commit the ADR on its own, before any code from Tasks 4-6.**
- [ ] **Step 4: Flip to `Accepted` only once Task 4 lands and the contract tests match the Decision (see `docs/adr/README.md` step 5).**

---

### Task 4: Carry counts across the Bridge boundary

**Files:**
- Modify: `src/LoadingAI.Core/Revit/RevitDtos.cs`
- Modify: `src/LoadingAI/Revit/Formwork/FormworkTools.cs`
- Modify: `src/LoadingAI/Revit/Formwork/FormworkConfig.cs`
- Modify: `src/LoadingAI/Revit/AdapterRevitBridge.Workflows.cs`
- Modify: `Plugins/qs-formwork/QsFormworkPlugin.cs`
- Test: `src/LoadingAI.Tests/QsFormworkExecutionResultTests.cs`

**Interfaces:**
- Consumes: `FormworkGenerator.Generate -> GenerateResult` (`FormworkConfig.cs:168-181`).
- Produces: result DTOs that carry `Created`, `Updated`, `Deleted`, `Failed`, and `FailReasons`, alongside the existing `Report` and `Error`.

- [ ] **Step 1: Write failing tests that `create_formwork_panels` reports `ChangedCount` and `FailedCount`, and that a run with both creations and failures is `Partial + Committed`.**

```csharp
[Fact]
public async Task CreatePanelsPartialKeepsCreatedAndNamesTheFailure()
{
    var (bridge, context) = Setup();
    bridge.NextFormworkPanelsResult = new FormworkPanelsResultDto(
        "Formwork panels: created 4, failed 2.",
        Error: null, Created: 4, Updated: 0, Deleted: 0, Failed: 2);

    var result = await CreateTool(context, "create_formwork_panels")
        .ExecuteAsync("""{"elementIds":["100","101"]}""");

    Assert.Equal(ToolExecutionStatus.Partial, result.Status);
    Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
    Assert.Equal(4, result.ChangedCount);
    Assert.Equal(2, result.FailedCount);
    Assert.True(result.IsSafeToAssimilate);
}

[Fact]
public async Task CreatePanelsAllFailedDoesNotClaimCommit()
{
    var (bridge, context) = Setup();
    bridge.NextFormworkPanelsResult = new FormworkPanelsResultDto(
        "Formwork panels: created 0, failed 3.", Error: null, Failed: 3);

    var result = await CreateTool(context, "create_formwork_panels")
        .ExecuteAsync("""{"elementIds":["100"]}""");

    Assert.Equal(ToolExecutionStatus.Failed, result.Status);
    Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
    Assert.False(result.IsSafeToAssimilate);
}
```

- [ ] **Step 2: Run the focused tests and verify they fail because the DTOs carry no counts.**

- [ ] **Step 3: Add the count properties to `FormworkSetupResultDto`, `FormworkPanelsResultDto`, `CleanupResultDto`, and `CopyParamsResultDto` as optional trailing parameters so every existing `new XxxResultDto(report)` call still compiles.**

- [ ] **Step 4: Make `FormworkTools` return `GenerateResult` (or the enriched DTO) instead of a string. Keep the human-readable sentence as the `Report` so model-readable text does not regress.**

- [ ] **Step 5: Update `RunFormwork` (`AdapterRevitBridge.Workflows.cs:578`) to project the structured result instead of wrapping a string.**

- [ ] **Step 6: Rewrite `FromFormwork` in the plugin to derive status from counts:**
- `Created + Updated + Deleted > 0 && Failed > 0` → `Partial + Committed`
- `Failed > 0 && no writes` → `Failed + NotCommitted`
- `Created + Updated + Deleted > 0` → `Succeeded + Committed`
- `delete_all` / `delete_by_type` / `cleanup_orphans` with `Deleted > 0` → `Succeeded + Committed`, `ChangedCount = Deleted`
- `stats` → `Succeeded + NoWrite` (it never opens a transaction)
- anything else → `Succeeded + NoWrite`

- [ ] **Step 7: Update `FakeBridgeToolTests` formwork expectations (`FormworkSetup_Success_ForwardsEmptyRequest`, `CreateFormworkPanels_ForwardsIdsAndDefaults`, `CleanupFormwork_ForwardsNormalisedActionAndFaceType`, and friends) for the new DTO shape and text.**

- [ ] **Step 8: Confirm `L3CommandContractTests` still passes — the four methods must remain on `IRevitCreateCommand`.**

- [ ] **Step 9: Run focused and full tests.**
Expected: PASS with zero failures.

- [ ] **Step 10: Commit the DTO enrichment and plugin status derivation.**

---

### Task 5: Harden the tool schemas

**Files:**
- Modify: `Plugins/qs-formwork/QsFormworkPlugin.cs`
- Modify: `src/LoadingAI.Core/Revit/Formwork/FormworkDefaults.cs`
- Test: `src/LoadingAI.Tests/QsFormworkExecutionResultTests.cs`

**Interfaces:**
- Produces: `enum` on `action` and `faceTypes`, `minimum`/`maximum` on `thicknessMm`.

- [ ] **Step 1: Write failing tests that `ToolArgumentValidator` rejects an unknown `action`, a `faceType` outside `FormworkConfig.AllFaceTypes`, and a `thicknessMm` outside bounds.**

```csharp
[Fact]
public async Task CleanupRejectsUnknownAction()
{
    var (_, context) = Setup();
    var result = await CreateTool(context, "cleanup_formwork")
        .ExecuteAsync("""{"action":"destroy_everything"}""");

    Assert.Equal(ToolExecutionStatus.Failed, result.Status);
    Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
    Assert.Contains("action", result.Text);
}
```

- [ ] **Step 2: Expose the face type names from Core so both the schema and `FormworkConfig.AllFaceTypes` read from one list. Do not duplicate the array in the plugin.**
- [ ] **Step 3: Add `enum` to `cleanup_formwork.action` (`stats`, `delete_all`, `delete_by_type`, `cleanup_orphans`) and to `create_formwork_panels.faceTypes`; add `minimum` and `maximum` to `thicknessMm` (suggest 1-100 mm).**
- [ ] **Step 4: Keep the handler-level `default:` branch in `FormworkCleanup.Run` as defence-in-depth, since `Handler` can be called directly.**
- [ ] **Step 5: Run focused and full tests.**
Expected: PASS with zero failures.
- [ ] **Step 6: Commit.**

---

### Task 6: Add host-owned preset forms

**Files:**
- Modify: `Plugins/qs-formwork/QsFormworkPlugin.cs`
- Modify: `src/LoadingAI.Tests/UsabilityContractTests.cs`
- Test: `src/LoadingAI.Tests/ToolPresetTests.cs`

**Interfaces:**
- Produces: `ToolEntry.Presets` on the write tools, using `PresetFieldKind.PickElements`, `Select`, `Number`, `Boolean`.

- [ ] **Step 1: Write failing tests that `create_formwork_panels` declares a `PickElements` field and a bounded `Number` for thickness, and that `cleanup_formwork` declares a `Select` for action.**
- [ ] **Step 2: Narrow `ToolEntry_PresetsDefaultEmpty_ExistingPluginsUnchanged` (`UsabilityContractTests.cs:58-72`) so the preset-free assertion covers `example.hello` only, and rename it to say so. Keep the assertion that `ToolRegistry` still defaults `Presets` to empty.**
- [ ] **Step 3: Declare the forms:**
  - `create_formwork_panels` — `elementIds` PickElements (Category hint `Structural Framing` / `Walls`), `thicknessMm` Number default 18, `collision` Boolean default true, `deleteExisting` Boolean default false.
  - `cleanup_formwork` — `action` Select with the four options, `faceType` Select shown when `delete_by_type`.
  - `copy_formwork_parent_params` — `elementIds` PickElements, `paramNames` (leave free so the catalogue can be listed).
  - `formwork_setup` — no form; it takes no arguments.
- [ ] **Step 4: Confirm the L3 native `Confirm L3:` TaskDialog still appears after the preset form. `HostPage_PresetMessages_SkipL3WebCardKeepNativeConfirm` must still pass.**
- [ ] **Step 5: Confirm `ToolGuards.ApplyConfirm` still skips preset tools so no double dialog appears (`ToolGuards_SkipsL2WrapWhenPresetsExist`).**
- [ ] **Step 6: Run focused and full tests.**
Expected: PASS with zero failures.
- [ ] **Step 7: Commit.**

---

### Task 7: Cover the host geometry

`FormworkCore`, `FormworkGenerator`, and `FormworkGeometry` are roughly 1,400 Revit-bound lines with no automated coverage at all. The plugin layer is tested; the geometry is not.

**Files:**
- Create: `src/LoadingAI.RevitTests/FormworkInRevitTests.cs`
- Create: `src/LoadingAI.Tests/FormworkGeometryTests.cs` for anything that can be made Revit-independent

- [ ] **Step 1: Identify which of `FormworkGeometry` can be made deterministic without `Document` (area, cover ratio, bbox overlap) and move the pure parts to `LoadingAI.Core` or make them testable. Prefer behaviour over source-text assertions (`CONTRIBUTING.md` rule 1).**
- [ ] **Step 2: Write in-Revit smoke tests in `FormworkInRevitTests`: setup binds parameters, one panel is created from a wall, `stats` reports it, `cleanup_orphans` removes one, and `delete_all` leaves none.**
- [ ] **Step 3: Add one rollback test: a run that fails mid-batch leaves the document unchanged (single outer `Transaction` behaviour at `FormworkTools.cs:59, 92`).**
- [ ] **Step 4: Run `dotnet test src/LoadingAI.RevitTests/... -p:ForceRevitTests=true --settings src/LoadingAI.RevitTests/revit.runsettings` against local Revit 2027.**
Expected: PASS. This is local-only; CI cannot run it.
- [ ] **Step 5: Commit.**

---

### Task 8: End-to-end verification

- [ ] **Step 1: Run `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:RevitVersion=2027 -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true -m:1`.**
Expected: PASS with zero failures.
- [ ] **Step 2: Run `npm test` in `gateway/` and `npm run build` in `web/`.**
Expected: exit code 0. The web build runs `tsc --noEmit` first.
- [ ] **Step 3: Build all four targets: `dotnet build src/LoadingAI/LoadingAI.csproj -c Release -p:RevitVersion=2024|2025|2026|2027`.**
- [ ] **Step 4: Flip ADR-0013 to `Accepted` once Tasks 4-6 match its Decision.**
- [ ] **Step 5: Record the outcome of F1-F7 in `docs/PROJECT_BUGS_AND_LIMITATIONS.md`, including anything still open.**
- [ ] **Step 6: Run the code-review skill and address actionable findings.**
- [ ] **Step 7: Open a PR against `main` and wait for CI. Do not use auto-merge: `main` has no required status checks, so `gh pr merge --auto` merges without running CI.**

## Out of scope

- The `Args` helper duplication across six plugins. ADR-0009 freezes it plugin-side; deduplicating needs its own ADR and a Core helper.
- `RequiresWriteConfirm` naming. Documented as B14; behaviour is frozen by ADR-0004 and ADR-0006.
- Formwork output as a bill-of-quantities report. This plan makes the counts available; formatting them for QS use is a separate feature.
- Any change to `IRevitCreateCommand` method placement.
