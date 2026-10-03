# ADR-0015 — Confirm L2 parameter writes with a summary

- Status: Proposed
- Date: 2026-10-03
- Deciders: LoadingAI maintainers
- Extends: [ADR-0004](0004-host-owned-l3-confirm.md)
- Does not supersede: [ADR-0001](0001-bridge-first-hardening.md), [ADR-0004](0004-host-owned-l3-confirm.md), [ADR-0006](0006-port-export-tools-onto-bridge.md)

## Context

ADR-0004 records that `ToolGuards.ApplyConfirm` skips L1, L3, `RequiresWriteConfirm` and preset tools, and that "L2 without presets still gets a host TaskDialog". ADR-0006 and the register (B14) freeze the flag.

Five L2 parameter tools set the flag and declare no preset, so they write the model without a prompt: `set_parameter_by_id`, `set_comments_on_selection`, `copy_params`, `replace_text` and `numbering_auto`. The first two lost their own dialog in `e1d7ff9` (the Bridge port), which kept the flag. The other three arrived from the Compat port with the flag and never had a dialog (ADR-0001 pins that parity). All five are on by default. `docs/PLUGIN_REVIEW_2026-10-03.md` PL-01 has the evidence.

Tool output carries text other people wrote (names, comments, warnings). An unconfirmed bulk write is therefore reachable from model content.

The generic dialog prints the first 400 characters of the arguments. For a bulk write that tells the engineer nothing about what will change.

## Options considered

1. **Remove the flag from the five tools and let the generic dialog show a read-only summary (chosen).** One new optional field, `ToolEntry.Describe`.
2. **Give each tool a preset form.** The form is the confirm (ADR-0004). These tools take free text and a category, not elements to pick, so a form adds a click without adding information.
3. **Confirm every L2 tool.** Rejected: `isolate_in_view` and other temporary view operations would become noise.
4. **Leave it.** Rejected: the system prompt and two tool descriptions already tell the user a prompt exists.

## Decision

- `RequiresWriteConfirm` keeps its meaning. The five tools stop setting it. `isolate_in_view` keeps it: it is view-local and temporary.
- `ToolEntry.Describe` is optional: `Func<string, Task<string?>>?`. It receives the validated argument JSON, may read the model through the Bridge, and must not write. Null, an empty string or a throw means "no summary".
- The host calls `ToolGuards.DescribeAsync` before it asks. The generic dialog shows the summary instead of the raw arguments. A summary carries its own undo sentence; the fallback text keeps "This will create an undoable transaction."
- The L3 approval card and the native `Confirm L3` dialog show the same summary.
- The gate order of ADR-0004 does not change. `Describe` runs before the first gate and writes nothing.

## Consequences

- A fresh install no longer lets the model overwrite Comments or Marks without a prompt.
- Each prompt costs one or two Bridge reads. They run on the Revit thread like any query.
- Tool descriptions that promise a confirmation become true. `batch_set_parameter` gets the count preview its description always claimed.
- B14 is corrected: two tools lost a confirmation they once had.

## Do not

- Do not let `Describe` write, open a transaction or start a `TransactionGroup`.
- Do not show a summary on a path that skips the gate (`dryRun`).
- Do not use `Describe` to decide whether to confirm. The tier and the flag decide.
- Do not put a `new TaskDialog` in a plugin (ADR-0004).
