# Opening Tools Review and Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring PR #25 onto the current Bridge-only architecture and fix Opening Tools correctness and UX gaps without changing the shared Plugin Shelf.

**Architecture:** Keep the plugin as a DTO-only adapter. Host-side opening algorithms return structured outcomes, the Bridge maps those outcomes without report parsing, and Opening Tools copy presents concise, actionable results.

**Tech Stack:** C#/.NET, Autodesk Revit API in host algorithms only, xUnit, Vite/React web pane.

**Spec:** `docs/OPENING_TOOLS_REVIEW.md`

## Global Constraints

- Revit 2024, 2025, 2026, and 2027 remain supported.
- `LoadingAI.Core` must not reference Revit assemblies.
- `Plugins/opening-tools` remains Bridge-only and must not mention `Autodesk`.
- L3 writes remain disabled by default and retain host-owned confirmation.
- `dryRun` never writes and skips the L3 TaskDialog.
- No shared Plugin Shelf redesign.
- Do not raise `ToolSelector.DefaultMax`.

---

### Task 1: Refresh PR #25 Against Current Main

**Files:**
- Modify: `feat/opening-plugin` branch history
- Test: existing `src/LoadingAI.Tests` suite

**Interfaces:**
- Consumes: current `main` containing ADR-0012, safety control-plane, and structured tool results.
- Produces: an updated `feat/opening-plugin` worktree based on current `main` with the existing PR identity preserved.

- [x] **Step 1:** Check out `feat/opening-plugin` and verify the working tree is clean.
- [x] **Step 2:** Merge current `main` into `feat/opening-plugin`; resolve conflicts in favor of Bridge-only plugin code while preserving Opening Tools fixes that remain relevant.
- [x] **Step 3:** Run `dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true`.
- [x] **Step 4:** Commit the branch refresh.

### Task 2: Enforce Opening Input and Scope Contracts

**Files:**
- Modify: `Plugins/opening-tools/OpeningToolsPlugin.cs`
- Modify: `src/LoadingAI/Revit/Openings/OpeningFamilyContract.cs`
- Modify: `src/LoadingAI/Revit/Openings/PlaceOpenings.cs`
- Test: `src/LoadingAI.Tests/OpeningToolsContractTests.cs`

**Interfaces:**
- Consumes: `OpeningClashRequestDto`, `ToolArgumentValidator`, `LevelNames.Resolve`.
- Produces: `OpeningFamilyContract.IsSupportedAutoLoadFamily(string)`, strict handler argument parsing, and plan/place level filtering.

- [x] **Step 1:** Add failing tests for malformed JSON, invalid shape/clearance/limit, unsafe family names, broad family matching, custom tagged openings, and dry-run/write level agreement.
- [x] **Step 2:** Run the focused test file and verify the expected failures.
- [x] **Step 3:** Implement strict handler parsing and narrow family contracts.
- [x] **Step 4:** Filter plan and place paths through the same resolved level scope.
- [x] **Step 5:** Run focused tests and the full suite.
- [x] **Step 6:** Commit input and scope hardening.

### Task 3: Preserve Structured Opening Outcomes Across the Bridge

**Files:**
- Modify: `src/LoadingAI/Revit/Openings/PlaceOpenings.cs`
- Modify: `src/LoadingAI/Revit/Openings/CutHosts.cs`
- Modify: `src/LoadingAI/Revit/AdapterRevitBridge.cs`
- Test: `src/LoadingAI.Tests/OpeningBridgeOutcomeTests.cs`

**Interfaces:**
- Consumes: existing `OpeningPlaceResultDto`, `OpeningPlanResultDto`, `OpeningCutResultDto`.
- Produces: host-run methods returning those DTOs directly or through a dedicated host outcome record; no report regex projection.

- [x] **Step 1:** Add failing tests that placement and cut counts/ids are carried from structured execution outcomes and that no report-string projection is used.
- [x] **Step 2:** Run focused tests and verify failure.
- [x] **Step 3:** Make `PlaceOpenings.Run` and `CutLocal.Run` return structured outcomes.
- [x] **Step 4:** Map those outcomes in `AdapterRevitBridge`; remove `ProjectPlace` regex parsing.
- [x] **Step 5:** Preserve human-readable `Report` and `Error` fields.
- [x] **Step 6:** Run focused tests and the full suite.
- [x] **Step 7:** Commit structured outcome propagation.

### Task 4: Improve Opening Tools UX Copy and Results

**Files:**
- Modify: `Plugins/opening-tools/OpeningToolsPlugin.cs`
- Modify: `Plugins/opening-tools/plugin.json`
- Test: `src/LoadingAI.Tests/OpeningToolsUsabilityTests.cs`

**Interfaces:**
- Consumes: `ToolExecutionResult`, structured opening DTOs.
- Produces: concise tool descriptions and result summaries suitable for the existing ToolCard.

- [x] **Step 1:** Add failing tests for concise dry-run, success, partial, failure, and skip result formatting.
- [x] **Step 2:** Run focused tests and verify failure.
- [x] **Step 3:** Rewrite Opening Tools descriptions and result text with summary-first formatting.
- [x] **Step 4:** Keep full details available below the summary and preserve error text.
- [x] **Step 5:** Run focused tests and the full suite.
- [x] **Step 6:** Commit UX copy and result formatting.

### Task 5: Verify and Prepare PR #25

**Files:**
- Modify: PR #25 description after approval to push
- Test: `src/LoadingAI.Tests`, `web/`

**Interfaces:**
- Consumes: Tasks 1-4.
- Produces: verified branch ready for PR update and merge request.

- [x] **Step 1:** Run the full xUnit suite.
- [x] **Step 2:** Run `npm ci` and `npm run build` in `web/`.
- [x] **Step 3:** Review the final diff for accidental shared Plugin Shelf or unrelated changes.
- [x] **Step 4:** Record remaining Revit 2024–2027 smoke-test limitations.
- [x] **Step 5:** Request code review, fix actionable findings, then ask the user before pushing and merging.
