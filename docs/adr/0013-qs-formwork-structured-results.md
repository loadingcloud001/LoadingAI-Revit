# ADR-0013 — qs-formwork structured results and preset forms

- Status: Proposed
- Date: 2026-10-01
- Deciders: LoadingAI maintainers

## Context

ADR-0009 (Accepted) froze a deliberately thin result shape for `qs-formwork`:

> The result records are deliberately thin — `Report` plus `Error` — because the generator already folds its counters into its prose report and `ToolEntry.Handler` is `Func<string, Task<string>>`, so the report *is* the user-visible output. Converting it to structured counters would be a rewrite, not a relocation.
> — `docs/adr/0009-port-qs-formwork-onto-bridge.md:111`

and froze the surface around it:

> The `Args` JSON helper struct, the four frozen `Definition` schemas, the two `RequiresWriteConfirm = true` flags, and all four `ToolTier.L3` tiers stay plugin-side and unchanged.
> — `docs/adr/0009-port-qs-formwork-onto-bridge.md:118`

> Tiers do not change … No new preset, no plugin-owned confirm, no `dryRun` change.
> — `docs/adr/0009-port-qs-formwork-onto-bridge.md:120`

**The premise no longer holds.** ADR-0009's reasoning was that the report string *is* the only output channel, because `ToolEntry.Handler` returned `Task<string>`. B3 of the safety control plane (`docs/superpowers/plans/2026-09-27-safety-control-plane.md`) then added `ToolEntry.StructuredHandler`, returning `ToolExecutionResult` with `Status`, `WriteCertainty`, `ChangedCount`, `FailedCount`. There is now a second channel, and the port-vs-rewrite distinction that justified the thin DTOs is gone: carrying the counters that `FormworkGenerator` already computes is a **relocation**, exactly as ADR-0009 asked for.

Three failures follow from the thin shape:

- **F1** — `FormworkTools.Run` absorbs every exception into the `Report` as `"Failed: {message}"` (consistent with `docs/adr/0009:117`, which said the host absorbs its own exceptions and the Adapter adds none). With `Error` never set, a crash and a success are indistinguishable except by wording.
- **F3/F4** — `FormworkGenerator.Generate` returns `GenerateResult` with `SelectedElements`, `Candidates`, `Created`, `Updated`, `Duplicates`, `CoverageSkipped`, `PanelOverlapSkipped`, `Trimmed`, `DeletedObsolete`, `Failed` and a `FailReasons` dictionary. `FormworkTools` flattens all of it to one sentence. The plugin cannot report `Partial`, cannot set `ChangedCount`, and the pane gets prose instead of numbers.
- **F6** — no tool declares `PresetField`, so `create_formwork_panels` makes the model guess element ids while `structure-cuts` (ADR-0008) already ships host-owned `PickElements` forms.

ADR-0009 remains correct as a **port** decision. This ADR does not reopen it. It releases two clauses whose justification was the pre-B3 string-only handler.

## Options considered

1. **Keep the freeze and parse the prose in the plugin.** The plugin would regex `created 4, updated 0` out of a sentence to derive counts. This puts the invariant in two places, breaks on any wording change in `FormworkTools`, and still cannot distinguish a rolled-back run from a committed one without matching sentences. Rejected: it is the fragility that caused F1.

2. **Release only the DTO freeze and leave presets alone.** Fixes F3/F4 but leaves F6 — the model still guesses ids for a six-argument write tool. Half the work, no reduction in the "UI/UX experiment" you would still be seeing. Rejected as incomplete.

3. **Release the DTO freeze and the definition/preset freeze, keep everything else frozen.** One coherent change: counts flow, the plugin derives status from numbers rather than words, and the user gets a form instead of a guessed argument list. Chosen.

## Decision

The following are released, and `UsabilityContractTests` is narrowed to match:

1. **The four formwork result DTOs carry counts.** `FormworkSetupResultDto`, `FormworkPanelsResultDto`, `CleanupResultDto` and `CopyParamsResultDto` gain optional trailing count properties (`Created`, `Updated`, `Deleted`, `Failed`, and a failure-reason breakdown for panels). `Report` and `Error` stay and keep their current precedence: `Error` is checked **first**, as ADR-0009:118, ADR-0007 and ADR-0008 all require.
2. **`FormworkTools.Run` returns the structured result instead of a sentence.** The human-readable sentence continues to be composed and carried as `Report`, so model-readable text does not regress.
3. **Status is derived from counts, never from wording.**
   - writes > 0 and `Failed` > 0 → `Partial` + `Committed`
   - writes > 0 → `Succeeded` + `Committed`
   - `Failed` > 0 and writes = 0 → `Failed` + `NotCommitted`
   - a report beginning `Failed:` → `Failed` + `NotCommitted` (the transitional rule, retired once every path carries counts)
   - `stats` and catalogue-only reads → `Succeeded` + `NoWrite`
4. **The four `Definition` schemas may declare `enum`, `minimum` and `maximum`.** Specifically `cleanup_formwork.action`, `create_formwork_panels.faceTypes` and `create_formwork_panels.thicknessMm`. `ToolArgumentValidator` enforces these before approval, so the model cannot reach the unknown-action branch at all. The handler keeps its own guard for direct `Handler` calls.
5. **The tools may declare `ToolEntry.Presets`.** Host-owned only: `PickElements`, `Select`, `Number`, `Boolean`. No `PickBox` (unused in-tree). The L3 native gate is unaffected — see Consequences.

## Consequences

- `UsabilityContractTests.ToolEntry_PresetsDefaultEmpty_ExistingPluginsUnchanged` is renamed and narrowed so the `Assert.DoesNotContain("Presets =", …)` assertion covers `example.hello` only. The assertion that `ToolRegistry` defaults `Presets` to empty is kept unchanged, because that is the real invariant the test was protecting.
- `FakeBridgeToolTests.Formwork_DeclareFrozenNamesAndTiers` keeps asserting all four tool names, all four `ToolTier.L3` tiers, and all four `RequiresWriteConfirm` values. Only its preset-related expectations change.
- `L3CommandContractTests` is unaffected: the four methods stay on `IRevitCreateCommand`.
- ADR-0004 is **not** relaxed. A preset form replaces the web `ToolApproval` card; the native `Confirm L3:` TaskDialog still runs for all four tools, and `HostPage_PresetMessages_SkipL3WebCardKeepNativeConfirm` still asserts it.
- `ToolGuards.ApplyConfirm` skips tools that have presets, so no second TaskDialog appears. `ToolGuards_SkipsL2WrapWhenPresetsExist` still asserts it.
- The transitional `Failed:` rule in Decision 3 becomes redundant once counts land on every path, and should be deleted in the task that proves it — it must not become a permanent second mechanism.
- Hosted CI can now pin `Partial` behaviourally for formwork, which it could not before.

## Do not

- Do not change any tier, and do not set `enabledByDefault: true` on any of the four.
- Do not change the `RequiresWriteConfirm` values: `formwork_setup` false, `create_formwork_panels` true, `cleanup_formwork` true, `copy_formwork_parent_params` false. They are frozen by ADR-0009 and ADR-0006, and the meaning hazard is recorded as B14 — renaming or inverting them needs its own ADR.
- Do not touch the `Args` JSON helper struct. It stays plugin-side and duplicated across the six plugins; deduplicating it is a separate decision (ADR-0009 freezes it and it is out of scope here).
- Do not add `dryRun`, a plugin-owned confirm, or a `new TaskDialog` inside the plugin.
- Do not give the plugin `Autodesk.Revit` types, a ProjectReference to the host or `LoadingAI.RevitUtil`, or any Nice3point package. `LoadingAI.Core` stays its only ProjectReference.
- Do not move the four `Formwork*Async` methods off `IRevitCreateCommand`.
- Do not claim `Partial` without counts proving it, and do not report a rolled-back run as committed.
- Do not rewrite the generator algorithms. This ADR changes only what crosses the boundary, not how panels are generated.
