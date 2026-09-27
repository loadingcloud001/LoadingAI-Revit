# Opening Tools Review and Hardening Specification

**Status:** Approved for implementation on 2026-09-27
**Scope:** `Plugins/opening-tools`, `src/LoadingAI/Revit/Openings`, their Bridge DTO projection, focused tests, and Opening Tools-facing copy. The shared Plugin Shelf remains unchanged.

## Problem

PR #25 predates the Bridge-only architecture in ADR-0012. Current `main` already moved opening algorithms into the host, but the integration still has correctness and UX gaps:

1. Placement outcomes are reconstructed from report text with a regex.
2. Repair-cut outcomes always cross the Bridge with zero counts.
3. `levelName` can make dry-run plan openings that the write path later rejects.
4. `familyName` can escape the shipped family set and potentially resolve a path outside the plugin folder.
5. Family detection is too broad for `LoadingAI_Opening*`.
6. Tagged custom opening families are not included in duplicate detection.
7. Direct handler calls can silently turn malformed JSON into defaults.
8. Tool/result copy is too dense for quick review.

## Required behavior

### Architecture

- `Plugins/opening-tools` remains Bridge-only and references `LoadingAI.Core` only.
- Plugin handlers serialize `OpeningClashRequestDto` and `OpeningCutRequestDto`.
- `src/LoadingAI/Revit/Openings` returns authoritative structured outcomes.
- `AdapterRevitBridge` maps host outcomes to existing `OpeningPlaceResultDto`, `OpeningPlanResultDto`, and `OpeningCutResultDto` without parsing report strings.
- `Report` remains presentation text only.

### Correctness

- `dryRun` and write runs use the same level scope.
- Placement results report `placed`, `punched`, `failed`, `skipped`, `joinsLost`, instance ids, and points from actual execution.
- Repair cuts report `cut`, `skippedLinked`, `skippedCannot`, `failed`, and `error` from actual execution.
- `clearanceMm` is non-negative; `limit` is 1..200; `shape` is `rectangle`, `circle`, or `auto`.
- Automatic `.rfa` loading is limited to the four shipped `LoadingAI_Opening_*` families. An explicitly named custom family may be used only when already loaded and its name contains no path separator or traversal.
- Plugin-family detection recognizes the four shipped names and legacy names exactly.
- Duplicate detection recognizes both shipped families and custom family instances carrying the OP_* contract.
- Malformed handler JSON returns a failed/no-write result and never executes defaults.

### Opening Tools UX

- Tool descriptions lead with the action, scope, write behavior, and required selection.
- `dryRun` explicitly says no model changes.
- Results start with a concise outcome summary, then list failures/skips and actionable next steps.
- Partial writes say exactly what committed and what did not.
- No shared Plugin Shelf changes.

## Verification

- Focused xUnit tests for input contracts, family-name contracts, structured Bridge outcome projection, and result formatting.
- Full `src/LoadingAI.Tests` suite.
- `npm ci` and `npm run build` in `web/`.
- Revit 2027 live smoke testing is covered below; Revit 2024–2026 remain a runtime availability gate.

## Runtime verification — 2026-09-28

- Revit 2027.2 live smoke passed in `src/LoadingAI.RevitTests/OpeningToolsInRevitTests.cs`: shipped hash-gate discovery, dry-run planning, one real conduit/wall place-and-punch, and repair-cut after a removed void relationship.
- The live write probes use `TransactionGroup.RollBack`; no fixture model changes persist.
- Local host deployment now copies the four shipped `.rfa` files and records all seven shipped plugin hashes. `scripts/pack-addin.ps1 -SkipBuild` produced a zip containing the Opening Tools DLL, manifest, four families, and `plugin-shipped-hashes.json`.
- Remaining gate: only Revit 2027 is installed locally. Revit 2024–2026 remain build/CI compatibility checks until those runtimes are available for the same live smoke.
