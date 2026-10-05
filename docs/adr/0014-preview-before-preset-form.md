# ADR-014 — preview before the preset form

- Status: Accepted
- Date: 2026-10-02
- Deciders: LoadingAI maintainers
- Extends: [ADR-0004](0004-host-owned-l3-confirm.md)
- Builds on: [ADR-0013](0013-qs-formwork-structured-results.md) (host-owned preset forms)

## Context

ADR-0004 froze the write-confirm gates and named this change explicitly:

> A later PR can drop the native dialog (web-only), drop the web card (native-only), or skip dry-run and double-prompt a preview. That is a product freeze, not a bugfix. Record it before anyone "simplifies" the gate.
> — `docs/adr/0004-host-owned-l3-confirm.md`

This ADR records the decision ADR-0004 asked to be written down before the gate is touched — but it
takes **none** of those three alternatives. The preview it adds is not a gate: the frozen order runs
unchanged behind it.

Today the preset form is rendered as a permanent card in the chat transcript
(`web/src/App.tsx`, the `if (m.role === 'preset' && m.preset)` branch in the
message map). Three things follow:

- The form is a **message**, so it scrolls with the conversation and remains
  after execution as a dead card showing `Confirmed <tool>`.
- `PresetPayload` carries `Fields` and `Values` only — a static schema. It asks
  the user for information the caller already passed, instead of showing what
  the call will do.
- The plain-L3 approval card dumps `m.approval.args` as raw JSON
  (`App.tsx:865`, `font-mono text-[11px] break-all`), so confirming is reading
  escaped text.

The forms exist because the model cannot know concrete values. The comment at
`Plugins/qs-formwork/QsFormworkPlugin.cs` puts it plainly: *"guessing ids for a
six-argument L3 write."* Fixing where the form renders does not fix the fact
that it asks for things the arguments already determine.

Eight tools declare presets today: `qs-formwork` (3) and `structure-cuts` (5,
via `SharedKnobs()`). Any change to `PresetPayload` reaches all of them.

## Options considered

1. **Derived preview, panel surface, gates untouched (chosen)** — build the
   preview generically from `PresetField` declarations plus the resolved
   arguments; move the interactive form out of the transcript into a docked
   panel. Pure data and pure formatting, both unit-testable, and no Revit
   access. Gate order is unchanged, so ADR-0004's freeze holds without
   amendment.
2. **Per-tool predicted counts** — a `ToolEntry.Preview` hook so a tool reports
   what it *would* create. More useful, but the counts come from Revit-bound
   geometry that has no tests (`FormworkGeometry`; see
   [Known limitations](../KNOWN_LIMITATIONS.md)). A guessed number rendered as a fact
   is worse than no number.
3. **Replace the form with the preview** — one-click confirm with no fields.
   Faster still, but it removes the only place the user can correct an argument
   the model got wrong, which is the form's actual purpose.

## Decision

- A read-only preview is **derived** from `PresetField` declarations plus the
  resolved arguments. It opens no transaction and is **not a gate**.
- The preview is displayed in a **docked Run panel**, not as a transcript
  message. The transcript keeps a receipt line.
- **The ADR-0004 gate order is unchanged**: preview → preset form →
  `Confirm L3:` native. The preview does not replace, merge with, or reorder
  any gate.

## Consequences

- All eight preset tools gain the panel and the preview with no plugin change.
- `dryRun` remains the only thing that skips both gates, and it keeps doing so
  through the generic merged-JSON check (`ToolPresets.IsDryRun`).
- Warnings may be attached to a `PresetField` so a dangerous setting is called
  out in the preview without the tool owning a confirm.
- Reported counts are limited to what the tool actually computed. Scope and
  settings are always safe to show; predicted outcome counts are not.

## Do not

- Do not use the preview as a substitute for the `Confirm L3:` native confirm.
- Do not let a preview open a transaction, or run after `EnsureTurnGroupAsync`.
- Do not report predicted outcome counts the tool did not compute.
- Do not replace `ToolPresets.IsDryRun` with a tool-name allowlist or a
  per-tool gate-skip flag (ADR-0004 freezes the generic check).
- Do not move the interactive form back into the transcript message list.
- Do not treat this ADR as licence to drop either L3 gate.
