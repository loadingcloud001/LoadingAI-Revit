# Plugin Review — 2026-10-03

**Basis:** `main` at `9fece17` (PR #89 merged; the run on `1fdec31` passed all seven checks, including the xUnit anchor on Revit 2027).
**Scope:** every plugin under `Plugins/` — 8 folders, 87 tools — plus the machinery that decides what a plugin can do: loader, allowlist and Trust flow, the Settings plugin shelf, the tool-argument pipeline (validator, presets, selector, system prompt) and the host adapters the tools call. Logic, function, code and UI/UX review, then enhancements and debug targets.
**Plan:** [`docs/superpowers/plans/2026-10-03-plugin-hardening.md`](superpowers/plans/2026-10-03-plugin-hardening.md) (Tasks 1–15, verifiable in CI) and [`docs/superpowers/plans/2026-10-03-plugin-revit-fixes.md`](superpowers/plans/2026-10-03-plugin-revit-fixes.md) (Tasks 16–19, each gated on a Revit 2027 run).
**Earlier reviews:** [`FULL_REVIEW_2026-10-03.md`](FULL_REVIEW_2026-10-03.md) (host, gateway, pane), [`PROJECT_BUGS_AND_LIMITATIONS.md`](PROJECT_BUGS_AND_LIMITATIONS.md) (B1–B14, F1–F7), [`OPENING_TOOLS_REVIEW.md`](OPENING_TOOLS_REVIEW.md).

## What was run

| Check | Result |
|---|---|
| `gh pr view 89` | merged; 7 / 7 checks green on `1fdec31` |
| Script: every `plugin.json` tool against its C# `ToolEntry` (name, tier, default) | 87 / 87 match; all 40 L3 tools default off |
| Script: confirm path per tool (tier, `RequiresWriteConfirm`, presets) | the matrix in the appendix; finding PL-01 |
| Script: manifest description against code description | 72 / 87 differ (PL-17) |
| `git log -S` / `git show` on the param tools | traced PL-01 to `e1d7ff9` |
| xUnit, in-Revit NUnit | **not run** — no .NET SDK on this Linux box, no Revit. CI is the compile and test gate. |

Everything below comes from reading code and tracing call paths. **CONFIRMED** means the path has to behave as described. **VERIFY** means the code allows it and the outcome depends on Revit runtime behaviour; the Revit checklist at the end lists what to try.

## Verdict

The plugin architecture holds up. Every tool reports a structured result, the dry-run protocol is real, and the two heaviest plugins are built defensively: `structure-cuts` refuses native `Split` on members that abort the process, and `opening-tools` has a written spec and a live Revit 2027.2 smoke test. The weak points sit at the seams between a plugin and the rest of the system:

1. **Confirmation policy.** Six default-on write tools run with no prompt, and two of them used to ask (PL-01).
2. **Units.** The prompt tells the model to send millimetres; the parameter writer stores them as feet (PL-02).
3. **Honest numbers.** Several tools cap or sample silently and then report a total that looks complete (PL-04, PL-05).
4. **Argument handling.** The validator is strict where models are sloppy, and one preset control cannot produce a value its own schema accepts (PL-06).
5. **Plugin management.** The shelf has no Remove or Rescan once a plugin loads, shows no source or version, and the loader still has the open B5 hazards (PL-12 to PL-15).

## Status of the existing register

| ID | Status now |
|---|---|
| B4 busy unload | Still open in code, but **no shipped plugin implements `IPluginLifecycle`**, so `WaitForIdle` has nothing to wait for. It only matters to third-party plugins. |
| B5 discovery | **Still open** (`PluginLoader.cs:118,127,256`): unsorted directory scan, "first DLL" fallback, duplicate plugin ids kept. See PL-14. |
| B8 `enabledByDefault` | Latent. Every shipped manifest agrees with `tier != L3`; `ChatSession.cs:82` still ignores the manifest. |
| B14 `RequiresWriteConfirm` | The conclusion "nothing in tree loses a confirmation it was meant to have" is **wrong for two tools**. See PL-01. |

## Inventory

| Plugin | Version | Tools (L1 / L2 / L3) | Default on | Logic lives in | Preset forms | Notes |
|---|---|---|---|---|---|---|
| `core-tools` | 1.0.0 | 62 (25 / 17 / 20) | 42 | `Plugins/core-tools/Core/*` + `AdapterRevitBridge*.cs` | 0 | Queries, params, views, geometry, document. 25 L1 tools are wrapped by `AsQuery`, which classifies by text prefix. |
| `export-tools` | 1.0.1 | 4 (3 / 0 / 1) | 3 | `AdapterRevitBridge.Documents.cs`, `.Workflows.cs` | 0 | `export_*` are L1 yet block Revit for up to 10 min. |
| `view-tools` | 1.0.1 | 6 (1 / 0 / 5) | 1 | `AdapterRevitBridge.Workflows.cs` | 0 | Private `Args` parser per plugin. |
| `modeling-tools` | 1.0.1 | 3 (0 / 0 / 3) | 0 | `.Documents.cs`, `.Elements.cs`, `.Create.cs` | 0 | `load_family` is sandboxed to `%AppData%\LoadingAI\families`. |
| `structure-cuts` | 2.0.1 | 5 (0 / 0 / 5) | 0 | `src/LoadingAI/Revit/StructureCuts/*` (~5.5k lines) | 5 | Best-hardened write plugin. No Revit-free tests of the geometry orchestration. |
| `qs-formwork` | 1.0.1 | 4 (0 / 0 / 4) | 0 | `src/LoadingAI/Revit/Formwork/*` (~1.6k lines) | 3 | Geometry untested (F7, blocked on a Revit 2027.2 harness). Cleanup filters on `ApplicationId`. |
| `opening-tools` | 1.0.0 | 2 (0 / 0 / 2) | 0 | `src/LoadingAI/Revit/Openings/*` (~2.3k lines) | 0 | Benchmark: structured outcomes, family allow-list, live smoke. |
| `example.hello` | 0.1.0 | 1 (1 / 0 / 0) | 1 | — | 0 | Not shipped; it is the template authors copy. |
| **Total** | | **87 (30 / 17 / 40)** | **47** | | 8 | 46 shipped tools are on by default against a 24-tool cap. |

## Findings

### High

**PL-01 — Six default-on L2 tools write the model with no confirmation, and two of them used to ask.** CONFIRMED.
`ToolGuards.ApplyConfirm` skips an L2 tool when `RequiresWriteConfirm` is set (`ToolGuards.cs:10`). `ChatSession.ExecuteToolAsync` gates only L3 and preset tools. Six L2 tools set the flag and declare no preset: `set_parameter_by_id` (`ParamTools.cs:30`), `set_comments_on_selection` (`:78`), `copy_params` (`:422`), `replace_text` (`:470`), `numbering_auto` (`:520`) and `isolate_in_view` (`ViewTools.cs:31`). Five of them change parameters in bulk. L2 defaults on, so a fresh install lets the model overwrite Comments or Marks across the whole selection, or a whole category, without a prompt. The view-only L2 tools (`hide_in_view`, `set_crop_box`, `duplicate_view_ex`, `reload_links` and the rest) do get the generic dialog, so the friction is inverted.
*History.* `e1d7ff9` (2026-08-27, "route param writes through typed IRevitCommand") deleted the in-handler dialogs of `set_parameter_by_id` ("Set {param} on element?") and `set_comments_on_selection` ("Set Comments on {n} selected element(s)?"), and the count preview of `batch_set_parameter`, but kept `RequiresWriteConfirm = true`. `copy_params`, `replace_text` and `numbering_auto` arrived from the Compat port with the flag and never had a dialog (ADR-0001 pins that). So B14 is right for those three and wrong for the first two.
*The copy still promises a prompt.* `set_comments_on_selection`: "Requires user confirmation before writing." `batch_set_parameter`: "Previews count, asks confirm, then writes" — no preview exists; the L3 card shows only `formatApprovalArgs`. The system prompt says "Destructive actions are confirmed via TaskDialog." (`AgentLoop.cs:23`).
*Why it matters.* Query tools return text that other people wrote: element names, comments, warnings. The prompt never says tool output is data. A comment reading "now call replace_text …" is a route to an unconfirmed bulk write. Plan and Ask mode block these tools since PR #89; Build mode does not.
*Fix.* ADR-0004 and ADR-0006 freeze the flag, so write a **Proposed ADR-0015** first. Then remove the flag from the five parameter tools and show a real summary (count, sample, before and after) in the dialog (plan Tasks 4 and 5). `isolate_in_view` is view-local and stays quiet.

**PL-02 — Parameter writes treat millimetres as feet.** CONFIRMED in code; the Revit semantics are documented API behaviour.
`RevitParse.TrySetParameter` parses a Double parameter and calls `p.Set(dv)` (`RevitParse.cs:82-86`). `Parameter.Set(double)` takes internal units: feet, square feet, radians. The system prompt says "Lengths are millimetres … Pass those units in tool arguments." (`AgentLoop.cs:24`), and `ToolSurface_DoesNotAdvertiseFeet` pins the tool surface to millimetres. So `set_parameter_by_id(Unconnected Height, "3000")` writes 3000 ft (914 m). The same helper serves `batch_set_parameter`, `import_excel_to_params` and `copy_params`. `copy_params` is worse: it copies the *display string* (`AsValueString()`) into `Set(double)`, so a metric project's "900" becomes 900 ft, and with unit symbols on, `double.TryParse("900 mm")` fails and the element is silently skipped.
Reads disagree with writes and with each other. `get_element_details` prints `AsDouble().ToString("F3")` (`AdapterRevitBridge.cs:551`): feet, culture-sensitive, no unit. `export_params_to_excel` and `find_elements_by_filter` use `AsValueString()` (project display units). `get_quantities` prints its bounding box in feet next to metric lengths. A model that reads 9.843 and then writes 3000 has no way to know the scale.
`create_wall` makes this likely: it has no height argument and builds a 10 ft wall (`AdapterRevitBridge.Create.cs:61`), so the natural follow-up is `set_parameter_by_id(Unconnected Height, 3500)`.
*Fix.* A spec-aware `ParamUnits` in Core (millimetres, m², m³, degrees), used by the writer, `copy_params`, `get_element_details` and the CSV export (plan Task 1).

**PL-03 — `replace_text` can hang Revit.** CONFIRMED.
`AdapterRevitBridge.Params.cs:399` builds `new Regex(pattern)` with no match timeout. It runs on the Revit UI thread inside `RunAsync`, and the pattern comes from the model or from an injected comment. A backtracking pattern on a long value does not return, Stop cannot reach a bridge action that is already running, and unsaved work is at risk. It also has no confirm (PL-01).
*Fix.* A `SafeRegex` helper: 1 s match timeout, 200-character cap, roll back on the first timeout (plan Task 2).

### Medium

**PL-04 — Silent caps and totals that look complete.** CONFIRMED.
- Selection ids are capped at 500 (`AdapterRevitBridge.cs:66`) while `ElementCount` is not. Tools that fall back to the selection touch only the first 500 and say "Updated 500": `set_comments_on_selection`, `isolate_in_view`, `hide_in_view`, `override_color_in_view`, `color_by_parameter`, `move_elements`, `delete_elements`, and `create_formwork_panels` through `ResolveIdsAsync`. For move and delete that is a partial destructive run.
- `copy_params`, `replace_text` and `export_params_to_excel` call `.ToElements().Take(limit)`, then report "Replaced 200/200" (`Params.cs:360,414`). The denominator is the truncated count, so 3 000 doors look finished.
- `get_wall_metrics` sums only `walls.Take(limit)` (`AdapterRevitBridge.cs:485`, limit ≤ 500) for a tool described as the answer to "total wall area"; the totals are wrong above 500 walls and only a note says so.
- `get_quantities` caps at 50 (`:633`) with no note. `get_rooms` prints a total from the truncated list. `get_family_types` and `get_materials` show 20, `get_sheets_views` 10, none with an offset.
*Fix.* Refuse a truncated selection instead of acting on a subset; report processed against total; compute wall totals over every wall (plan Tasks 3 and 6).

**PL-05 — `get_warnings` reports the number of warning types.** CONFIRMED.
The adapter groups by description (`AdapterRevitBridge.cs:133`); the plugin prints `Warnings: {warnings.Count}` (`QueryTools.cs:426`) and never prints `WarningDto.Count`. Three thousand warnings of twelve kinds read as "12". The chat context bar uses the true count, so the two numbers disagree.

**PL-06 — The argument pipeline is stricter than the models and than its own forms.** CONFIRMED.
- `ToolArgumentValidator` does no coercion. `elementIds: [123, 456]` fails with "expected string" (tool output prints ids unquoted, so models send numbers), `"limit": "50"` fails, and a lone string for an array fails. Each miss burns one of the 64 rounds. The plugins' own `Args.Strings` accepts numbers; that code never runs on the validated path.
- `structure-cuts` declares `categories` as an array of strings in the schema and as a single-value `Select` in the form (`StructureCutsPlugin.cs:131,169,337`). `ToolPresets.Merge` overlays the string, the post-merge validation rejects it ("categories: expected array."), and the user's pick can never run. A blank number field posts `""` and fails the same way.
- `scope` and `categories` carry no `enum`, so a typo in `scope` falls back to `selection`. `path_of_travel` `starts`/`ends` and `create_topography` `points` are `{ type = "array" }` with no `items` (`ViewToolsPlugin.cs:249-250`, `ModelingToolsPlugin.cs:112`); strict providers reject that.
*Fix.* A lossless `ToolArgumentCoercer` ahead of the validator, plus a schema and preset lint test (plan Tasks 8 and 9).

**PL-07 — `structure-cuts`: paging can skip hosts, failures read as skips, and write errors can open a modal.** CONFIRMED for paging (by trace); VERIFY for the modal.
- `continueFrom` is an offset into a list that is re-collected on every call (`SplitScope.cs:76`, `AllInModel`). A write run deletes and recreates walls, floors and unjoinable members, so recreated members leave the front of the list and unprocessed ones shift up. The report then says "nextOffset = 50", and the next call skips as many hosts as were recreated. Native splits keep their ids, so the common beam path is mostly safe.
- `SplitResultDto` has no failure count; a member that failed and rolled back is counted under "Skipped" and its exception message is dropped. The plugin cannot report `Partial`.
- `WriteTx` sets `SetForcedModalHandling(true)` and its preprocessor deletes only warnings (`SplitElements.cs:30,48`). An error-severity failure at commit shows Revit's dialog inside an ExternalEvent and stalls the call (the open "modal-dialog stall" check).
- The dry run says "Would split N beams into M pieces" but not how many would be re-created, nor how many tags, dimensions and hosted elements the re-created ones would delete. That is the information the L3 prompt needs.
- The recreate path flattens parts the snapshot cannot carry (ElementId parameters such as top constraint, wall joins, workset). `LevelSplit` already skips attached and shape-edited walls; `split_by_grids` and `split_beams_by_columns` should say what they drop.

**PL-08 — Placement and creation defects.** `AdapterRevitBridge.Create.cs`.
- `create_family_instance` and `create_family_instance_array` call `FamilySymbol.Activate()` before the transaction starts (`:115`, `:376`). Every other call site in the tree activates inside a transaction (`PlaceOpenings.cs:141`, `Views.cs:451`, the split code). VERIFY: an inactive symbol should throw.
- `z` is used as an absolute project elevation although the instance is tied to a level, so "on Level 3, z 0" lands three levels low. The array always uses z = 0.
- An unknown or omitted level silently falls back to the first level in collector order; `create_wall` and `create_floor_by_outline` return a clear not-found message with suggestions instead.
- The schema example is `Single-Flush` / `Door`, but `NewFamilyInstance(point, symbol, level, NonStructural)` cannot place a hosted family, so the example is a guaranteed failure; structural columns need `StructuralType.Column`.
- `create_wall` hard-codes a 10 ft height and takes `FirstOrDefault()` of every `WallType` (a curtain or stacked type is possible). The array silently clamps `count` to 50.

**PL-09 — Move and rotate loop per element, and the bridge mixes units.** CONFIRMED.
`MoveElementsAsync` calls `ElementTransformUtils.MoveElement` once per id (`Elements.cs:75`), `RotateElementsAsync` likewise (`Create.cs:461`). A wall and its hosted door both in the set are moved twice: once with the host, once on their own. The plural APIs let Revit handle hosts. Separately, `MoveElementsAsync` takes **feet** (the plugin converts, `GeometryTools.cs:76`, and the result prints "ft") while every `Create*`, rotate origin and mirror argument takes **millimetres**. The parameters are named `dx, dy, dz`, so a new plugin that follows the other methods is off by 304.8×.

**PL-10 — View tools: "temporary" colour is permanent, and some operations are broken.**
- `override_color_in_view`, `color_by_parameter` and `color_by_construction_schedule` call `SetElementOverrides` / `SetCategoryOverrides`. Those overrides are saved in the project and synced to the team. Only `isolate_in_view` and `hide_in_view` use temporary view modes. The descriptions still say "temporary", and `color_by_construction_schedule` ends with "To reset: say 'reset view' (reset_temporary_view)" (`Views.cs:329`), which does **not** clear overrides. Nothing clears them except per-element `reset=true`.
- No call sets a fill pattern id (grep: only `SetSurfaceForegroundPatternColor`), so a surface with no pattern shows no colour in Shaded or Consistent Colors. VERIFY.
- `tag_all` places the tag at `LocationPoint ?? XYZ.Zero` (`Views.cs:689`), so every wall tag lands on the project origin.
- `create_dimension` passes `new Reference(wall)` (`:639`), an element reference that dimensions normally reject. VERIFY.
- `create_sheet` looks for a title block family whose name contains "Title" (`:438`); "A1 Sheet" or "TB_A1" families are not found. `place_view_on_sheet` has no `CanAddViewToSheet` check and cannot place a schedule.

**PL-11 — `reload_links` runs `RevitLinkType.Reload()` inside a transaction and swallows every failure.** VERIFY for the transaction, CONFIRMED for the swallowing.
`Documents.cs:62-72` opens a `Transaction`, calls `Reload()` in a bare `try/catch`, and returns `Ok = true` with "Reloaded 0/5" if all five throw. Link load, unload and reload are normally done outside a transaction. The generic L2 dialog also tells the user "This will create an undoable transaction", which is false for a reload.

**PL-12 — The Settings plugin shelf is missing basics.** CONFIRMED.
- `removePlugin` exists in `bridge.ts` and the host, but no component sends it (the button went with the `93ea484` rewrite). The `CanRemove` guard from PR #89 protects an action nobody can reach.
- **Rescan** exists only in the empty state (`PluginShelf.tsx:19`). With any plugin loaded, a developer who drops a new DLL into `Plugins\dev` must restart Revit.
- `PluginInfo` already carries version, author and `verified`, but the shelf shows only name and id. CONTRIBUTING says a local plugin appears as **Unverified**; it does not. There is no source label (shipped, trusted, dev), no tool description, and no "needs restart" state.
- The per-tool and per-plugin checkboxes read "On" / "Off" with no tool name, so a screen reader announces nothing useful.
- The Trust button keys on the literal `issue.summary === 'DLL not on allowlist'` (`PluginShelf.tsx:54`), pinned by a test on both ends. `issue.detail` and the SHA-256 are not shown or copyable.

**PL-13 — Removal is not safe against a loaded DLL.** CONFIRMED by reading.
`HostPage.xaml.cs:442` calls `Directory.Delete(folder, true)` while the plugin's DLL is mapped by the load context. On Windows the delete fails part-way: `plugin.json` and other files go, the locked DLL stays, and the folder reloads next start as a manifest-less plugin. The handler posts a status but never refreshes the shelf, and the plugin's tools stay callable until Revit restarts.

**PL-14 — The loader still has the B5 hazards, plus a few more.**
- Directories and loose manifests are scanned unsorted (`PluginLoader.cs:118,127`); with no manifest, the loader takes `Directory.GetFiles(folder, "*.dll").FirstOrDefault()` (`:256`).
- Scan order is runtime folder first, then dev, and duplicate tool names are "first wins" (`:152`). A shipped plugin therefore **silently shadows** the dev copy being tested, with a log line as the only trace. Two folders with the same plugin id both load.
- Only the entry DLL is hashed. Sibling DLLs are resolved through `AssemblyDependencyResolver` / `LoadFrom` with no gate. This is hardening, not an exploit: the shipped-hash file and the allowlist both sit in user-writable `%AppData%`, so the allowlist stops accidents and third-party drop-ins, not local malware. The docs should say so.
- `LoadHashes` returns an **empty** set for a corrupt allowlist, so every trusted plugin is refused as "not on allowlist" with no hint that the file is unreadable. The writer is careful about this; the reader is not.
- `entryAssembly` is joined to the folder without a check (`..\` or an absolute path); the hash gate limits the damage.
- An ALC load failure records a "failed" issue even when the `LoadFrom` fallback then succeeds (`:283-285`). `PluginLoader.Log` clears the whole file at 512 KB, and `PluginLogs.Append` has no bound.

**PL-15 — The Trust dialog gives false comfort and too little information.**
The flow itself is sound: the host re-hashes, compares with the hash the scan recorded, shows a default-No dialog, writes once and reloads. But the text says "Tool tiers and Confirm L3 are unchanged" (`HostPage.xaml.cs:197`). A trusted DLL runs in-process with the engineer's permissions and can ignore tiers entirely; ADR-0011 calls the click "the whole authorisation". The dialog also omits what `plugin.json` claims (name, version, author, tools and tiers), even though that file is readable before load.

**PL-16 — Tool selection is English-only and the default catalog exceeds the cap.**
46 shipped tools are on by default against `DefaultMax = 24`, so `ToolSelector.Select` always runs. Its tags, keywords and `InferTags` are English (`ToolSelector.cs`), and no CJK handling exists anywhere under `src/` (grep). A Traditional Chinese or Cantonese prompt scores zero on every keyword and falls back to the `Preferred` list and alphabetical order, so the structure, opening and formwork tools may never reach the model. The system prompt also repeats the first sentence of every description while the API sends the full definition; six tools carry 700–1 000 character descriptions.

**PL-17 — Manifest and code describe different tools, and nothing compares them.**
72 of 87 descriptions differ between `plugin.json` and the C# `ToolDefinition` (the LLM sees the C# text; the shelf shows neither). Names, tiers and defaults agree today, but no test enforces it, and the shelf trusts the manifest: a third-party manifest that declares `"tier": "L1"` for a tool that is L3 in code displays as L1 and is counted in the L1 totals. Enforcement stays correct because it uses the code tier.

**PL-18 — Exports are L1, unconfirmed, and can freeze Revit.**
`export_pdf` with no `sheetIds` exports every sheet on the UI thread with a 10-minute timeout, no progress and no way to stop it. An existing file is overwritten silently. `export_params_to_excel` and `quantity_takeoff_by_category` write CSV with no formula-injection guard (cells starting `=`, `+`, `-`, `@`), no escaping for header names, and `:F2` formatting that emits a decimal comma under de, fr or es cultures (`QueryTools.cs:1055`). `ExportPaths.ResolveWrite(null, name)` returns `Path.Combine(Root, name)` without checking the name (`ExportPaths.cs:15`); callers build it from the model-supplied category (`ParamTools.cs:332`, `QueryTools.cs:1057`). The adapter rejects unknown categories first, so this is defence in depth.

**PL-19 — Query tools: wrong, overlapping or over-promising.**
- `get_element_details` shows the first 30 parameters in Revit's order with no way to ask for one, skips type parameters, and prints a line-based location as the .NET type name (`Curve {lc.Curve}` → "Autodesk.Revit.DB.Line", `AdapterRevitBridge.cs:541`).
- `find_elements_by_filter` `empty` / `notempty` use `AsString()`, so every numeric parameter counts as empty; `LookupParameter` misses type parameters.
- `get_clash_by_bb` tests the first 200 elements of each category by axis-aligned box. Hosted doors and windows always "clash" with their wall, rotated members inflate, and reaching `limit` is not mentioned.
- `get_model_health_score` says "warnings, views, worksets, rooms" but uses warnings and views only, and a higher view count raises the score.
- `sync_info` promises sync status and returns a blank user and time. `audit_warnings_fix` fixes nothing; it lists the same data as `get_warnings`. `get_document_info`, `sync_info` and `get_model_summary` overlap. Together that is three tools too many against the cap.
- `IRevitQuery.FindElementsAsync` (filters after `Take(200)`), `GetQuantitiesAsync` (totals always 0) and `SplitWallAsync` have no callers.

**PL-20 — Writes that report success without writing, or overwrite user data.**
`import_excel_to_params`, `rename_elements` and `apply_construction_dates` commit an empty transaction and return `Ok = true` when nothing changed (`SetParametersAsync` does it right: roll back, `Ok = false`). `apply_construction_dates` with only unknown categories prints "to 0 element(s) across 0 categories". When "Construction Date" is missing it writes the date into **Comments**, overwriting whatever was there. `import_excel_to_params` falls back to column 1 when the parameter name is not in the header, so it can write the wrong column. `rename_elements` `{i}` starts at 0. `numbering_auto` follows collector order (Revit does not preserve click order), counts up even when a set fails, and does not check existing Marks.

**PL-21 — A failed read rolls back earlier writes in the turn, and nobody is told.** CONFIRMED.
`ChatSession.ExecuteToolAsync` marks the turn failed for any non-write result that is `Failed`, `Cancelled` or `Unknown` (`ChatSession.cs:547-551`). `AsQuery` classifies "Please select elements first." and "Please open a model first." as `Failed`. So *write → verification read that finds nothing* undoes the write at turn end, while the assistant's closing message (already generated) claims success. FULL_REVIEW already records that rollback is invisible; this adds the read-failure trigger.

### Low

**PL-22 — Docs and ADR drift.** `Plugins/README.md:8` and `CONTRIBUTING.md:54` say the Trust prompt "is not implemented yet"; it is. `PluginLoader.AllowlistRefusalHint` says "stage 2 will add a trust dialog". ADR-0010 and ADR-0011 are still `Proposed`. `Plugins/README.md:37` says adding or removing a DLL needs a restart (Rescan, Trust and Remove exist). `src/LoadingAI/Plugins/Compat/manifest.json` has no reader, and its README arithmetic ("37 + 28 = ~65") is stale. `IRevitToolPlugin` still mentions "raw access for opted-in plugins". The root README calls `LoadingAI.RevitUtil` "Parse helpers for FullAccess plugins". `example.hello` tells authors to drop the folder into `%AppData%\LoadingAI\Plugins\hello` (now refused) and its csproj lacks `Private=false` / `CopyLocalLockFileAssemblies=false`, so a copy ships its own `LoadingAI.Core.dll`.

**PL-23 — Boilerplate and dead code.** Every tool sets both `Handler` and `StructuredHandler` and promises they agree. Each plugin re-implements an `Args` parser; core-tools repeats ~12 lines of `JsonDocument` parsing per tool. `qs-formwork` has four identical `FromFormwork` overloads. `IPluginLifecycle` and `WaitForIdle` serve no shipped plugin. `OverlayTags` and `ToolGuards` copy `ToolEntry` field by field, so the next field added is dropped silently.

**PL-24 — Small things.** `RuntimeDataDir` uses `Path.GetFileName(pluginDir)`, which is empty for a trailing separator. `mirror_elements` mirrors in place (`mirrorCopies: false`) but reports "→ N new elements". `duplicate_element` copies at offset zero and `View.Duplicate` throws for sheets. `LookupParameter` returns the first of several same-named parameters. Yes/No parameters reject "Yes". `ParseCategory` rebuilds a dictionary per call. `pack-addin` overwrites `%AppData%\LoadingAI\Plugins\*` with `-Force` and no backup.

## What to keep

- Structured results everywhere (`ToolExecutionResult`) and the `dryRun` protocol that both the host gate and the plugin read through `ToolPresets.IsDryRun`.
- `opening-tools`: spec, family allow-list, partial-run reporting and a live Revit 2027.2 smoke. Treat it as the template for other plugins.
- `structure-cuts`: per-member sub-transactions, refusing native `Split` on unsafe members, and reports that name each skip reason.
- The Trust flow design: re-hash on click, default No, single write, gate re-checked at load.
- ADR discipline. The plan below writes an ADR before it touches a frozen decision.

## Brainstorm — enhancements

| # | Idea | Why | Size |
|---|---|---|---|
| E1 | **Preview before confirm.** An optional `ToolEntry.Describe` that reads the model and returns a one-paragraph summary (counts, a sample, before → after). The generic dialog and the L3 card show it. | Confirm fatigue is real. "Rename 214 sheets, first three: A101 → Plan L1…" is a decision; `{"elementIds":[…]}` is not. Also gives delete and move a "what exactly" line. | M |
| E2 | **One unit policy.** `ParamUnits` in Core; every numeric read prints a unit, every numeric write parses one. | Removes the feet/millimetre/display-string split (PL-02). | S |
| E3 | **Cascade preview for delete.** Run `doc.Delete` inside a rolled-back sub-transaction and report the dependent count. | Deleting a Level takes everything hosted on it; today that is reported afterwards. | M, Revit |
| E4 | **Lossless argument coercion** (number ↔ string ids, `"50"` → 50, scalar → array, blank → omitted). | Fewer wasted rounds; fixes the categories form. | S |
| E5 | **Turn summary card and rollback notice.** List writes that landed, writes undone, and why. | Closes PL-21 and the FULL_REVIEW "invisible rollback" item. | M |
| E6 | **Plugin manager.** Source badge, version, Verified/Unverified, tool descriptions, Rescan, Remove (tombstone), copy diagnostics, "needs restart". | PL-12, PL-13. | M |
| E7 | **Manifest from code.** Generate `plugin.json` tools at build, or validate them in a contract test and at load time. | Ends PL-17 drift. | S |
| E8 | **Plugin SDK helpers.** `ToolArgs`, `ToolEntry.FromStructured(...)`, a `PluginTestKit` around `FakePluginContext`, a schema-lint test base, and a `dotnet new`-style template. | Removes ~1 500 lines of repeated parsing and the "must agree" footgun (PL-23). | M |
| E9 | **Plugin API version in the manifest** (`"coreApi": 1`), checked before load. | Today a mismatch surfaces as a type-load failure and a hint after the fact. | S |
| E10 | **zh-HK vocabulary for `ToolSelector`** plus aliases in descriptions. | The product is built for Hong Kong work. | S, needs a native reviewer |
| E11 | **Safer long operations.** Export of "all sheets" needs explicit ids or `confirmAll`; chunked exports with progress; overwrite guard. | PL-18. | M |
| E12 | **Reversible view colouring.** Snapshot prior overrides, `reset_view_overrides`, optionally work in a duplicated view. | PL-10. | M, Revit |
| E13 | **Stable split cursor and per-member failure reasons** for `structure-cuts`; a dry run that classifies native versus re-created and counts dependents. | PL-07. | M |
| E14 | **`/diag`.** Plugins and load issues, the last 20 tool calls with duration and status, bridge queue depth, log paths, one-click copy. | The fastest way to turn "it did nothing" into a bug report. | S |
| E15 | **Catalog hygiene.** Merge `get_document_info` / `sync_info` / `get_model_summary`, and `audit_warnings_fix` into `get_warnings`; one paging footer (`showing X of Y, offset n`). | Frees slots under the cap of 24 and makes lists complete. | M |

### Debug targets (reproduce these first)

1. Set a length parameter through chat and read the value back in Revit (PL-02).
2. Run `split_by_levels` on 120 walls in two pages and count the walls never processed (PL-07).
3. Place a family from an inactive symbol with `create_family_instance` (PL-08).
4. `reload_links` with two links (PL-11).
5. Colour a view with `color_by_construction_schedule` in Shaded and in Consistent Colors, then try to reset it (PL-10).
6. Trigger a modal failure inside a split batch (PL-07).
7. Move a wall and its door together (PL-09).

## Owner decisions (assumed in the plan)

Same convention as the last review: each has a recommended default that the plan uses unless you say otherwise.

1. **Restore confirmation on the five L2 parameter tools, with a summary in the dialog?** *Recommended: yes.* View-local temporary tools (`isolate_in_view`) stay quiet. This overrides the B14 freeze through a Proposed ADR.
2. **One LLM-facing unit policy: millimetres, m², m³, degrees, with a unit on every output?** *Recommended: yes*, as the system prompt already promises.
3. **Should a failed read undo the turn's earlier writes?** *Recommended: no.* Only a failed or cancelled write, or an unknown outcome, rolls back.
4. **When a dev plugin and an installed plugin share an id or tool name, which wins?** *Recommended: the dev copy, with a visible note.*
5. **Exports: keep L1, but require explicit sheet ids (or `confirmAll`) before exporting everything?** *Recommended: yes.*
6. **zh-HK vocabulary: can someone fluent in construction terminology review the list in Task 15?** *Recommended: ship it behind that review.* "模板" is formwork in HK practice but "template" elsewhere.
7. **Should an error-severity Revit failure inside a `structure-cuts` batch roll the whole batch back and say why, instead of showing Revit's dialog?** *Recommended: yes.* The report names the cause and the next step is a smaller `limit`. Warnings are still deleted quietly. (Phase E, Task 19.)
8. **`create_family_instance` `z`: a height above the level, instead of an absolute elevation?** *Recommended: yes.* The instance is tied to a level, so "Level 3, z 0" should mean on Level 3. (Phase E, Task 16.)
9. **`move_elements` on the bridge in millimetres, like every other bridge method?** *Recommended: yes.* No model-visible change; it removes a 304.8× trap for the next plugin author. (Phase E, Task 17.)

## Revit 2027 checklist (additions to the FULL_REVIEW list)

| # | Check | Expected today | After the plan |
|---|---|---|---|
| R1 | `set_parameter_by_id` on a wall: Unconnected Height = `3000` | 3000 ft | 3000 mm |
| R2 | `create_family_instance` with a type that is not yet active | exception | placed |
| R3 | `create_family_instance` on Level 3 with z omitted | three levels low | on Level 3 |
| R4 | `reload_links` with a loaded link | "Reloaded 0/1" if the API refuses a transaction | "Reloaded 1/1" |
| R5 | `create_dimension` between two parallel walls | likely fails | works or says why |
| R6 | `tag_all Walls` | tags at the origin | tags on the walls |
| R7 | `color_by_parameter` in Shaded | edges only | solid colour |
| R8 | `split_by_levels` 120 walls, `limit 50`, then `continueFrom` | some walls skipped | none skipped |
| R9 | Error-severity failure inside a split batch | modal dialog | rolled back, reported |
| R10 | Move a wall plus its hosted door | door moves twice | once |
| R11 | `export_ifc` after a write in the same turn | (FULL_REVIEW) | unchanged |

## Appendix — confirm path per tool

`G` = generic L2 dialog, `—` = none, `L3` = web approval then native confirm, `F` = preset form then (L3) native confirm. Default column: ✓ on, ✗ off.

| Plugin | Tool | Tier | Confirm | Def |
|---|---|---|---|---|
| core | `set_parameter_by_id`, `set_comments_on_selection`, `copy_params`, `replace_text`, `numbering_auto` | L2 | **— (PL-01)** | ✓ |
| core | `isolate_in_view` | L2 | — (view-local, intended) | ✓ |
| core | `hide_in_view`, `reset_temporary_view`, `override_color_in_view`, `color_by_construction_schedule`, `color_by_parameter`, `duplicate_view_ex`, `set_view_template`, `set_crop_box`, `join_geometry`, `reload_links`, `workset_assign` | L2 | G | ✓ |
| core | `save_document`, `move_elements`, `delete_elements`, `create_wall`, `create_family_instance`, `duplicate_element`, `create_floor_by_outline`, `create_roof`, `create_grid_level`, `create_family_instance_array`, `rotate_elements`, `mirror_elements`, `batch_set_parameter`, `rename_elements`, `apply_construction_dates`, `import_excel_to_params`, `create_sheet`, `place_view_on_sheet`, `create_dimension`, `tag_all` | L3 | L3 | ✗ |
| core | 25 query tools incl. `export_params_to_excel` | L1 | — | ✓ |
| export | `export_pdf`, `export_dwg`, `export_ifc` | L1 | — (PL-18) | ✓ |
| export | `purge_unused` | L3 | L3 (also on its `preview` default; only `dryRun` skips the gate) | ✗ |
| view | `get_schedule_data` | L1 | — | ✓ |
| view | `create_schedule`, `create_floor_plan`, `create_3d_view`, `create_section_view`, `path_of_travel` | L3 | L3 | ✗ |
| modeling | `load_family`, `change_element_type`, `create_topography` | L3 | L3 | ✗ |
| opening | `create_openings_from_clash`, `cut_hosts_by_openings` | L3 | L3, `dryRun` skips | ✗ |
| formwork | `formwork_setup` | L3 | L3, `dryRun` skips | ✗ |
| formwork | `create_formwork_panels`, `cleanup_formwork`, `copy_formwork_parent_params` | L3 | F | ✗ |
| structure | five split tools | L3 | F, `dryRun` skips | ✗ |
| hello | `hello` | L1 | — | ✓ |

Notes: `copy_formwork_parent_params` with no `paramNames` is a read that still pays for both L3 gates unless `dryRun` is passed. `purge_unused` uses `preview` rather than `dryRun`, so its harmless default call takes the full gate and opens a turn group.

## Status

Review and plan written. Nothing executed yet.
