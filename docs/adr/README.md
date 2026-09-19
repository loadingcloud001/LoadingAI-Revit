# Architecture decision log

An **ADR** is one file that records a choice that is hard to reverse:
what we decided, why, and what we will not do.

This folder is the log. Code and `CONTRIBUTING.md` enforce it.
Do not treat this as a blog or a backlog.

## When to write one

Write an ADR **before** code if the change does any of:

- Breaks a freeze in an Accepted ADR (cap 24, Bridge-only core-tools, …)
- Adds a plugin capability the host does not have (`IRevitBridge` slice, pick API, thread rule)
- Changes how plugins load, confirm writes, or talk to Revit
- Moves a shipped plugin from FullAccess onto Bridge (or the reverse)

Do **not** write an ADR for:

- A bug that keeps the current freeze
- A wording / test-only change
- “We might someday…” — wait until a named fail or a named plugin needs it

## Format (Nygard + one MADR slice)

Keep Nygard’s four blocks. Add **Options considered** only when there were real alternatives.

```
# ADR-NNNN — short title
- Status: Proposed | Accepted | Superseded by ADR-NNNN
- Date: YYYY-MM-DD
- Deciders: …

## Context
## Options considered   ← omit if only one serious option
## Decision
## Consequences
## Do not
```

Status moves forward only: Proposed → Accepted → Superseded. Never delete; never rewrite an Accepted decision in place.

## How to add ADR-NNNN

1. Copy `0000-template.md` → `NNNN-short-kebab-title.md`.
2. Fill Context / Decision / Do not. One decision per file.
3. Set Status **Proposed**. Link it from this README table.
4. Review in the same PR as the first code that needs it — or in a docs-only PR if you are freezing something already in tree.
5. Flip to **Accepted** when the matching code (or existing code) matches the Decision. Add a contract test if the freeze can be broken by a later PR.

0001 is closed. Do not append new scope there.

## Index

| ADR | Title | Status |
|---|---|---|
| [0000](0000-template.md) | Template | — |
| [0001](0001-bridge-first-hardening.md) | Bridge-first hardening (core-tools, HostProtocol, Recall) | Accepted |
| [0002](0002-plugin-shelf-bridge-first.md) | Plugin shelf: Core-only, Bridge-first, never host | Accepted |
| [0003](0003-fullaccess-exceptions.md) | FullAccess is opt-in and listed | Accepted |
| [0004](0004-host-owned-l3-confirm.md) | Host-owned L3 confirm (web then native) | Accepted |
