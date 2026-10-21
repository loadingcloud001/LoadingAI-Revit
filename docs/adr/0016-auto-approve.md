# ADR-0016 — User-owned auto-approve

- Status: Accepted
- Date: 2026-10-21
- Deciders: the maintainer
- Extends: [ADR-0004](0004-host-owned-l3-confirm.md)
- Does not supersede: [ADR-0004](0004-host-owned-l3-confirm.md). The default gate is unchanged.

## Context

ADR-0004 freezes two confirms before an L3 write: a card in the pane, then a Revit dialog that defaults to No. L2 tools without a form have their own dialog. Revit can also open its own failure dialog inside a transaction and stall the turn.

The maintainer wants a mode where the agent finishes a fix without those stops. That mode must not be something the model can turn on from a tool result or a chat message. ADR-0004's "do not drop the native confirm" still holds whenever the mode is off.

## Options considered

1. **A pane switch, default off (chosen).** The host reads `AppSettings.AutoApprove`. While it is on, the host skips the preset form, the L3 card, the native L3 dialog, and the generic L2 dialog, and a failures handler dismisses Revit dialogs only during an open LoadingAI turn. The model is told the mode is on. Plan and Ask stay read-only.
2. **A tool argument `autoApprove: true`.** Rejected. The model could set it, which is the hole ADR-0004 closed.
3. **Drop the confirms for everyone.** Rejected. The default path in ADR-0004 stays.

## Decision

- The switch lives in the chat header. Turning it on asks once in the pane. It is stored in `settings.json`. `AutoApproveGate.Enabled` is the live copy call sites read.
- Default is off. A chat message cannot change it. There is no tool for it.
- While on, tools that are switched off stay off. The host does not enable L3.
- While on, and only while a LoadingAI turn group is open, Revit warnings are deleted. An error is resolved only with Detach, Unlock, Fix, Move, or Skip. Delete and Create are not applied. Anything else rolls that transaction back so Revit does not show a dialog. If a transaction preprocessor already chose commit or roll-back, that choice stands.
- While off, `ChatSession` still requires `entry.Tier == ToolTier.L3 && !ToolPresets.IsDryRun(argsJson)` before both confirms, and the native dialog still says `Confirm L3:` and defaults to No.
- One undo per turn is unchanged.

## Consequences

- With the switch on, a wrong answer can change the model without a second click. The turn still undoes as one step.
- Manual edits made while no LoadingAI turn is open still show Revit's own dialogs.
- Tests that lock ADR-0004 keep matching the default path. A separate test locks the switch.

## Do not

- Do not let a tool argument or tool result set `AutoApprove`.
- Do not skip the confirms when the switch is off.
- Do not resolve a Revit error by deleting or creating elements inside the failures handler.
- Do not dismiss Revit dialogs when no LoadingAI turn group is open.
- Do not let Plan or Ask write because the switch is on.
